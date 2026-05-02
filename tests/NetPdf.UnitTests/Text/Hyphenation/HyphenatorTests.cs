// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Hyphenation;
using Xunit;

namespace NetPdf.UnitTests.Text.Hyphenation;

/// <summary>
/// Behavioral tests for the Liang <see cref="Hyphenator"/>. Most tests use a small
/// hand-built pattern set so the expected output is deterministic and inspectable
/// (i.e., you can verify every break by reading the patterns + the algorithm).
/// Bundled-en-us golden words are exercised separately in
/// <c>EnUsHyphenationGoldenTests</c>.
/// </summary>
public sealed class HyphenatorTests
{
    [Fact]
    public void Word_shorter_than_leftMin_plus_rightMin_returns_empty()
    {
        var patterns = HyphenationPatternSet.Build([".a1b", "1c"]);
        var h = new Hyphenator(patterns);
        // word "ab" is length 2; leftMin=2, rightMin=3 → 2 < 5, so no hyphens.
        Assert.Empty(h.FindHyphenationPoints("ab"));
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        var h = new Hyphenator(HyphenationPatternSet.Build([]));
        Assert.Empty(h.FindHyphenationPoints(string.Empty));
    }

    [Fact]
    public void Single_pattern_with_odd_priority_marks_a_break()
    {
        // Pattern "a1b" assigns priority 1 between 'a' and 'b'. For input "aabb"
        // (length 4), the boundary text is ".aabb." — pattern "ab" matches at boundary
        // position 2 (text[2]='a', text[3]='b'). priorities[3] = 1 (odd). Word index
        // m maps via boundary index k = m+1, so m = k-1 = 2.
        var patterns = HyphenationPatternSet.Build(["a1b"]);
        var h = new Hyphenator(patterns);
        // leftMin=1, rightMin=1 to allow the break in this short test word.
        var breaks = h.FindHyphenationPoints("aabb", leftMin: 1, rightMin: 1);
        Assert.Equal([2], breaks);
    }

    [Fact]
    public void Even_priority_blocks_a_break_that_odd_pattern_would_otherwise_allow()
    {
        // "a1b" allows; "a2b" with higher priority blocks (even wins).
        // "a3b" allows again (still odd, even higher).
        var patterns = HyphenationPatternSet.Build(["a1b", "a2b"]);
        var h = new Hyphenator(patterns);
        // The "a2b" pattern wins (higher number); 2 is even → no hyphen.
        var breaks = h.FindHyphenationPoints("aabb", leftMin: 1, rightMin: 1);
        Assert.Empty(breaks);
    }

    [Fact]
    public void Higher_priority_odd_overrides_lower_priority_even()
    {
        var patterns = HyphenationPatternSet.Build(["a2b", "a3b"]);
        var h = new Hyphenator(patterns);
        // "a3b" wins over "a2b" (3 > 2); 3 is odd → hyphen permitted.
        var breaks = h.FindHyphenationPoints("aabb", leftMin: 1, rightMin: 1);
        Assert.Equal([2], breaks);
    }

    [Fact]
    public void Boundary_marker_pattern_matches_only_at_word_edges()
    {
        // ".a1b" only fires when 'a' is at the start of the word (next to leading '.').
        var patterns = HyphenationPatternSet.Build([".a1b"]);
        var h = new Hyphenator(patterns);
        // Word "abxy": 'a' is at start. leftMin=1, rightMin=1. priorities[2] = 1 → m=1.
        Assert.Equal([1], h.FindHyphenationPoints("abxy", leftMin: 1, rightMin: 1));
        // Word "xabxy": 'a' is interior; ".a1b" doesn't match → no hyphen there.
        Assert.Empty(h.FindHyphenationPoints("xabxy", leftMin: 1, rightMin: 1));
    }

    [Fact]
    public void leftMin_excludes_breaks_too_close_to_start()
    {
        var patterns = HyphenationPatternSet.Build(["a1b"]);
        var h = new Hyphenator(patterns);
        // Word "aabbab" length 6. With leftMin=4, breaks must be at index >= 4.
        var breaks = h.FindHyphenationPoints("aabbab", leftMin: 4, rightMin: 1);
        // pattern "ab" matches at boundary positions 2 (text[2..3]) and 5 (text[5..6])
        // and 6 ... priorities indices 3 and 6. Word indices m=2 and m=5. Filter m>=4:
        // only m=5 remains.
        Assert.Equal([5], breaks);
    }

    [Fact]
    public void rightMin_excludes_breaks_too_close_to_end()
    {
        var patterns = HyphenationPatternSet.Build(["a1b"]);
        var h = new Hyphenator(patterns);
        // Same word "aabbab" length 6. rightMin=3 means m <= 6-3 = 3.
        var breaks = h.FindHyphenationPoints("aabbab", leftMin: 1, rightMin: 3);
        // m candidates are 2 and 5; only m=2 satisfies m <= 3.
        Assert.Equal([2], breaks);
    }

    [Fact]
    public void Exception_dictionary_short_circuits_pattern_application()
    {
        var patterns = HyphenationPatternSet.Build(["a1b"]); // would say [1] for "abc"
        var dict = HyphenationDictionary.Parse("a-bc"); // exception: break only at pos 1
        var h = new Hyphenator(patterns, dict);
        var breaks = h.FindHyphenationPoints("abc", leftMin: 1, rightMin: 1);
        Assert.Equal([1], breaks);
    }

    [Fact]
    public void Exception_with_no_breaks_means_never_hyphenate()
    {
        var patterns = HyphenationPatternSet.Build(["a1b", "b1c"]);
        var dict = HyphenationDictionary.Parse("present");
        var h = new Hyphenator(patterns, dict);
        // With leftMin=1, rightMin=1 the patterns would otherwise yield breaks; the
        // exception dictionary forces an empty result.
        Assert.Empty(h.FindHyphenationPoints("present", leftMin: 1, rightMin: 1));
    }

    [Fact]
    public void Algorithm_is_case_insensitive_for_ASCII_letters()
    {
        var patterns = HyphenationPatternSet.Build(["a1b"]);
        var h = new Hyphenator(patterns);
        var lower = h.FindHyphenationPoints("aabb", leftMin: 1, rightMin: 1);
        var upper = h.FindHyphenationPoints("AABB", leftMin: 1, rightMin: 1);
        var mixed = h.FindHyphenationPoints("aAbB", leftMin: 1, rightMin: 1);
        Assert.Equal(lower, upper);
        Assert.Equal(lower, mixed);
    }

    [Fact]
    public void FindHyphenationPoints_is_deterministic()
    {
        var h = new Hyphenator(HyphenationPatternSet.Build(["a1b", ".c2d", "e3f"]));
        var first = h.FindHyphenationPoints("aabbccddeeff", leftMin: 1, rightMin: 1);
        var second = h.FindHyphenationPoints("aabbccddeeff", leftMin: 1, rightMin: 1);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Constructor_throws_on_null_pattern_set()
    {
        Assert.Throws<ArgumentNullException>(() => new Hyphenator(null!));
    }

    // ───── Argument validation (review #2) ───────────────────────────────────

    [Fact]
    public void Negative_leftMin_throws_ArgumentOutOfRangeException()
    {
        var h = new Hyphenator(HyphenationPatternSet.Build(["a1b"]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            h.FindHyphenationPoints("hyphenation", leftMin: -1, rightMin: 3));
    }

    [Fact]
    public void Negative_rightMin_throws_ArgumentOutOfRangeException()
    {
        var h = new Hyphenator(HyphenationPatternSet.Build(["a1b"]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            h.FindHyphenationPoints("hyphenation", leftMin: 2, rightMin: -1));
    }

    [Fact]
    public void Zero_leftMin_and_rightMin_are_valid_advanced_overrides()
    {
        // Zero means "no edge constraint" — useful for advanced layout cases like
        // breaking a single-segment of an already-hyphenated compound. Neither value
        // alone produces breaks at edges (priorities[0] / priorities[Length+1] are
        // structurally always 0), but the call must not throw.
        var h = new Hyphenator(HyphenationPatternSet.Build(["a1b"]));
        var breaks = h.FindHyphenationPoints("aabb", leftMin: 0, rightMin: 0);
        // pattern "ab" matches at boundary positions 2 → priorities[3] = 1; word index m = 2.
        Assert.Equal([2], breaks);
    }
}
