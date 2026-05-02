// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Hyphenation;
using Xunit;

namespace NetPdf.UnitTests.Text.Hyphenation;

/// <summary>
/// Tests for <see cref="HyphenationDictionary"/> — the explicit-exception list parser
/// and case-insensitive ASCII lookup used to short-circuit Knuth's en-us irregular
/// words (e.g. <c>as-so-ciate</c>, <c>ta-ble</c>, <c>present</c>).
/// </summary>
public sealed class HyphenationDictionaryTests
{
    [Fact]
    public void Parse_extracts_break_positions_from_hyphenated_word()
    {
        // "as-so-ciate" has 9 letters. Hyphens after "as" (pos 2) and "asso" (pos 4).
        var dict = HyphenationDictionary.Parse("as-so-ciate");
        Assert.True(dict.TryGet("associate", out var breaks));
        Assert.Equal([2, 4], breaks.ToArray());
    }

    [Fact]
    public void Parse_word_without_hyphens_means_never_hyphenate()
    {
        // "present" with no hyphens means: do not hyphenate this word.
        var dict = HyphenationDictionary.Parse("present");
        Assert.True(dict.TryGet("present", out var breaks));
        Assert.True(breaks.IsEmpty);
    }

    [Fact]
    public void TryGet_is_case_insensitive_for_ASCII()
    {
        var dict = HyphenationDictionary.Parse("ta-ble");
        Assert.True(dict.TryGet("Table", out var b1));
        Assert.True(dict.TryGet("TABLE", out var b2));
        Assert.Equal([2], b1.ToArray());
        Assert.Equal([2], b2.ToArray());
    }

    [Fact]
    public void TryGet_returns_false_for_words_not_in_list()
    {
        var dict = HyphenationDictionary.Parse("ta-ble\nas-so-ciate");
        Assert.False(dict.TryGet("computer", out _));
    }

    [Fact]
    public void Parse_handles_multiple_words_separated_by_whitespace()
    {
        var dict = HyphenationDictionary.Parse("ta-ble\nas-so-ciate\noblig-a-tory");
        Assert.Equal(3, dict.Count);
        Assert.True(dict.TryGet("table", out _));
        Assert.True(dict.TryGet("associate", out _));
        Assert.True(dict.TryGet("obligatory", out _));
    }

    [Fact]
    public void Parse_skips_TeX_style_comment_lines()
    {
        var dict = HyphenationDictionary.Parse("% header comment\nta-ble\n% another\nas-so-ciate");
        Assert.Equal(2, dict.Count);
        Assert.True(dict.TryGet("table", out _));
        Assert.True(dict.TryGet("associate", out _));
    }

    [Fact]
    public void Empty_dictionary_returns_false_for_any_lookup()
    {
        var dict = HyphenationDictionary.Empty;
        Assert.Equal(0, dict.Count);
        Assert.False(dict.TryGet("anything", out _));
    }

    [Fact]
    public void TryGet_handles_empty_input_safely()
    {
        var dict = HyphenationDictionary.Parse("ta-ble");
        Assert.False(dict.TryGet(ReadOnlySpan<char>.Empty, out _));
    }

    // ───── TeX-style % inline comment handling (review #3) ───────────────────

    [Fact]
    public void Parse_strips_inline_TeX_comment_after_token()
    {
        // "ta-ble%comment" — the trailing %... is a TeX comment, not part of the token.
        var dict = HyphenationDictionary.Parse("ta-ble%comment ignored\nas-so-ciate");
        Assert.Equal(2, dict.Count);
        Assert.True(dict.TryGet("table", out var b1));
        Assert.Equal([2], b1.ToArray());
        Assert.True(dict.TryGet("associate", out var b2));
        Assert.Equal([2, 4], b2.ToArray());
    }

    // ───── Malformed-token normalization (review #5) ─────────────────────────

    [Fact]
    public void Parse_drops_trailing_hyphen_in_malformed_token()
    {
        // "foo-" — the trailing hyphen is at position 3 (== letterCount), which means
        // "after the last letter". That's nonsensical; the parser drops it and emits
        // "foo" as a never-hyphenate entry.
        var dict = HyphenationDictionary.Parse("foo-");
        Assert.True(dict.TryGet("foo", out var breaks));
        Assert.True(breaks.IsEmpty);
    }

    [Fact]
    public void Parse_drops_leading_hyphen_in_malformed_token()
    {
        // "-foo" — the leading hyphen has no preceding letter. Dropped silently;
        // word becomes "foo" with no breaks.
        var dict = HyphenationDictionary.Parse("-foo");
        Assert.True(dict.TryGet("foo", out var breaks));
        Assert.True(breaks.IsEmpty);
    }

    [Fact]
    public void Parse_collapses_adjacent_hyphens_to_single_break()
    {
        // "a--b" — a doubled hyphen between 'a' and 'b'. Collapsed to a single break
        // at position 1.
        var dict = HyphenationDictionary.Parse("a--b");
        Assert.True(dict.TryGet("ab", out var breaks));
        Assert.Equal([1], breaks.ToArray());
    }

    [Fact]
    public void Parse_collapses_runs_of_three_or_more_hyphens()
    {
        // Same rule applies to longer runs.
        var dict = HyphenationDictionary.Parse("a---b");
        Assert.True(dict.TryGet("ab", out var breaks));
        Assert.Equal([1], breaks.ToArray());
    }

    [Fact]
    public void Parse_handles_combined_malformed_and_valid_breaks()
    {
        // "a-b---c-" combines a valid break (a-b), an adjacent-hyphen run (---), and a
        // trailing hyphen (-). Expected normalization: letters="abc", breaks=[1, 2].
        var dict = HyphenationDictionary.Parse("a-b---c-");
        Assert.True(dict.TryGet("abc", out var breaks));
        Assert.Equal([1, 2], breaks.ToArray());
    }

    // ───── Mutation safety (review #4) ───────────────────────────────────────

    [Fact]
    public void TryGet_returns_ReadOnlySpan_that_cannot_corrupt_cached_state()
    {
        // Same contract test as for HyphenationPatternSet — the static `out ReadOnlySpan<int>`
        // parameter declaration prevents callers from mutating the cached break arrays.
        var dict = HyphenationDictionary.Parse("ta-ble");
        Assert.True(dict.TryGet("table", out ReadOnlySpan<int> breaks));
        Assert.Equal(1, breaks.Length);
        Assert.Equal(2, breaks[0]);
        // breaks[0] = 0;  // would be a compile error
    }
}
