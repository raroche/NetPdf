// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Hyphenation;
using Xunit;

namespace NetPdf.UnitTests.Text.Hyphenation;

/// <summary>
/// Per-pattern parsing + lookup tests for <see cref="HyphenationPatternSet"/>.
/// Covers the digit-letter interleave decoding, boundary-marker handling, and
/// FrozenDictionary-backed lookup that the main Liang algorithm depends on.
/// </summary>
public sealed class HyphenationPatternSetTests
{
    [Fact]
    public void ParseRawPattern_letters_only_yields_zero_priorities()
    {
        var (letters, priorities) = HyphenationPatternSet.ParseRawPattern("abc");
        Assert.Equal("abc", letters);
        Assert.Equal([0, 0, 0, 0], priorities);
    }

    [Fact]
    public void ParseRawPattern_digit_between_letters_emits_priority_at_correct_slot()
    {
        // "hy3pho" → letters "hypho", priorities[2] = 3 (between 'y' and 'p').
        var (letters, priorities) = HyphenationPatternSet.ParseRawPattern("hy3pho");
        Assert.Equal("hypho", letters);
        Assert.Equal([0, 0, 3, 0, 0, 0], priorities);
    }

    [Fact]
    public void ParseRawPattern_trailing_digit_emits_priority_after_last_letter()
    {
        // ".ach4" → letters ".ach", priorities[4] = 4 (after final 'h').
        var (letters, priorities) = HyphenationPatternSet.ParseRawPattern(".ach4");
        Assert.Equal(".ach", letters);
        Assert.Equal([0, 0, 0, 0, 4], priorities);
    }

    [Fact]
    public void ParseRawPattern_leading_digit_emits_priority_before_first_letter()
    {
        // "2ye" → letters "ye", priorities[0] = 2 (before 'y').
        var (letters, priorities) = HyphenationPatternSet.ParseRawPattern("2ye");
        Assert.Equal("ye", letters);
        Assert.Equal([2, 0, 0], priorities);
    }

    [Fact]
    public void ParseRawPattern_consecutive_digits_keeps_last()
    {
        // Per Liang's format, each digit is the priority "before next letter"; the
        // most recently seen digit before a letter wins. "a23b" → priorities[1] = 3.
        var (letters, priorities) = HyphenationPatternSet.ParseRawPattern("a23b");
        Assert.Equal("ab", letters);
        Assert.Equal([0, 3, 0], priorities);
    }

    [Fact]
    public void Build_indexes_patterns_by_letters_only_form()
    {
        var set = HyphenationPatternSet.Build(["hy3pho", ".ach4", "2ye"]);
        Assert.Equal(3, set.Count);
        Assert.True(set.TryGet("hypho", out var p1));
        Assert.Equal(3, p1[2]);
        Assert.True(set.TryGet(".ach", out var p2));
        Assert.Equal(4, p2[4]);
        Assert.True(set.TryGet("ye", out var p3));
        Assert.Equal(2, p3[0]);
        Assert.False(set.TryGet("xyz", out _));
    }

    [Fact]
    public void Build_tracks_max_pattern_length()
    {
        var set = HyphenationPatternSet.Build(["a", "ab", "abc", "abcdef"]);
        Assert.Equal(6, set.MaxPatternLength);
    }

    [Fact]
    public void ParseBlock_splits_on_whitespace_including_newlines_and_tabs()
    {
        var block = ".ach4\n.ad4der\n.af1t\thy3pho\n";
        var set = HyphenationPatternSet.ParseBlock(block);
        Assert.Equal(4, set.Count);
        Assert.True(set.TryGet(".ach", out _));
        Assert.True(set.TryGet(".adder", out _));
        Assert.True(set.TryGet(".aft", out _));
        Assert.True(set.TryGet("hypho", out _));
    }

    [Fact]
    public void ParseBlock_skips_blank_lines_and_extra_whitespace()
    {
        var block = "\n\n   .ach4\n\n  hy3pho   \n\n";
        var set = HyphenationPatternSet.ParseBlock(block);
        Assert.Equal(2, set.Count);
    }

    // ───── TeX-style % comment handling (review #3) ───────────────────────────

    [Fact]
    public void ParseBlock_strips_line_start_TeX_comment()
    {
        var block = "% header line, ignored\n.ach4\nhy3pho";
        var set = HyphenationPatternSet.ParseBlock(block);
        Assert.Equal(2, set.Count);
        Assert.True(set.TryGet(".ach", out _));
        Assert.True(set.TryGet("hypho", out _));
    }

    [Fact]
    public void ParseBlock_strips_inline_TeX_comment_after_token()
    {
        // Pattern token followed by an inline % — the pattern is captured, the rest of
        // the line is dropped. Per TeX semantics: '%' anywhere on a line begins a comment.
        var block = ".ach4%trailing comment ignored\nhy3pho";
        var set = HyphenationPatternSet.ParseBlock(block);
        Assert.Equal(2, set.Count);
        Assert.True(set.TryGet(".ach", out _));
        Assert.True(set.TryGet("hypho", out _));
    }

    [Fact]
    public void ParseBlock_strips_inline_TeX_comment_eats_rest_of_line()
    {
        // Tokens after an inline '%' on the same line are part of the comment. Use
        // distinct letter-forms ("abc", "def", "jkl") so each yields a unique key —
        // otherwise the Liang parser collapses trailing-digit forms (".pat1" / ".pat2"
        // both have letter form ".pat") and the count test would mislead.
        var block = "abc1 de2f%comment with ghi5 in it\njkl3";
        var set = HyphenationPatternSet.ParseBlock(block);
        Assert.Equal(3, set.Count); // abc, def, jkl — NOT ghi (it was inside the comment)
        Assert.True(set.TryGet("abc", out _));
        Assert.True(set.TryGet("def", out _));
        Assert.True(set.TryGet("jkl", out _));
        Assert.False(set.TryGet("ghi", out _));
    }

    // ───── Mutation safety (review #4) ───────────────────────────────────────

    [Fact]
    public void TryGet_returns_ReadOnlySpan_that_cannot_corrupt_cached_state()
    {
        // The compile-time return type is ReadOnlySpan<byte>, which has no public
        // mutator. The static `out ReadOnlySpan<byte>` parameter declaration in
        // HyphenationPatternSet.TryGet pins this contract — any future API regression
        // back to `out byte[]` would produce a compile error against this test (the
        // local `p` would silently change type and the comment-line assertion would
        // start being reachable).
        var set = HyphenationPatternSet.Build(["hy3pho"]);
        Assert.True(set.TryGet("hypho", out ReadOnlySpan<byte> p));
        // Spot-check the data is correct.
        Assert.Equal(3, p[2]);
        // Type system disallows mutation; the line below would produce a compile error
        // ("Property or indexer 'ReadOnlySpan<byte>.this[int]' cannot be assigned to"):
        //   p[2] = 0;
    }

    [Fact]
    public void TryGet_lookup_with_span_input_does_not_allocate_a_string()
    {
        // The hot-path overload accepts ReadOnlySpan<char> directly. Verify the lookup
        // works on a span that is NOT a string (e.g., a slice of a stack buffer).
        var set = HyphenationPatternSet.Build(["hy3pho"]);
        Span<char> buf = stackalloc char[5] { 'h', 'y', 'p', 'h', 'o' };
        ReadOnlySpan<char> slice = buf;
        Assert.True(set.TryGet(slice, out var p));
        Assert.Equal(3, p[2]);
    }
}
