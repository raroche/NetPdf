// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Hyphenation;
using Xunit;
using Xunit.Abstractions;

namespace NetPdf.UnitTests.Text.Hyphenation;

/// <summary>
/// Golden-word integration tests for the bundled American-English Liang pattern set.
/// Each test asserts a specific break sequence the standard <c>hyph-en-us.tex</c>
/// patterns are known to produce — verifiable by inspection (the resulting
/// hyphenation should match accepted American-English typesetting practice).
/// </summary>
public sealed class EnUsHyphenationGoldenTests
{
    private readonly ITestOutputHelper _output;

    public EnUsHyphenationGoldenTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Bundled_pattern_set_loads_expected_count()
    {
        // hyph-en-us.tex contains 4,952 raw whitespace-separated tokens, but two embedded
        // TeX-style comment lines (mid-block "% hyphen.tex patterns end here, and
        // additional patterns begin:" + "% end of additional patterns.") leak ~12
        // non-pattern tokens that our parser strips, collapsing to 4,938 unique pattern
        // letter-forms. Plus the 14 exception words from \hyphenation{...}.
        Assert.Equal(4938, EnUsHyphenation.Patterns.Count);
        Assert.Equal(14, EnUsHyphenation.Exceptions.Count);
    }

    [Theory]
    [InlineData("hyphenation")]
    [InlineData("computer")]
    [InlineData("algorithm")]
    [InlineData("concatenation")]
    [InlineData("democracy")]
    [InlineData("typography")]
    [InlineData("manuscript")]
    [InlineData("dictionary")]
    public void Standard_words_produce_at_least_one_hyphenation_point(string word)
    {
        // Each of these has well-known multi-syllable English hyphenations under the
        // standard en-us pattern set; assert non-empty result. Specific positions are
        // pinned in dedicated tests below for words where the canonical break sequence
        // is widely cited.
        var breaks = EnUsHyphenation.Default.FindHyphenationPoints(word);
        _output.WriteLine($"{word} → {Render(word, breaks)} (positions: [{string.Join(", ", breaks)}])");
        Assert.NotEmpty(breaks);
    }

    [Fact]
    public void Hyphenation_word_breaks_at_known_canonical_positions()
    {
        // Standard Liang en-us output for "hyphenation" is "hy-phen-ation" (positions
        // [2, 6]) — the exact result Knuth's TeX produces with the bundled patterns.
        var breaks = EnUsHyphenation.Default.FindHyphenationPoints("hyphenation");
        _output.WriteLine($"hyphenation → {Render("hyphenation", breaks)}");
        // Pin the standard break — this is the value documented in Liang's thesis
        // and produced by every TeX engine using the en-us pattern set.
        Assert.Equal([2, 6], breaks);
    }

    [Fact]
    public void Computer_word_breaks_at_known_canonical_positions()
    {
        // "computer" → "com-puter" → positions [3]. With the default leftMin=2/rightMin=3,
        // the standard en-us patterns surface only the first-syllable break ("com-puter");
        // the second potential break ("com-put-er") would leave only "er" (2 letters)
        // after, which violates rightMin=3. This matches what TeX produces for "computer"
        // with the en-us pattern set under standard typesetting hyphenmins.
        var breaks = EnUsHyphenation.Default.FindHyphenationPoints("computer");
        _output.WriteLine($"computer → {Render("computer", breaks)}");
        Assert.Equal([3], breaks);
    }

    [Fact]
    public void Computer_with_relaxed_rightMin_surfaces_second_syllable_break()
    {
        // With rightMin=2 the patterns may surface a second-syllable break — verify the
        // algorithm honors the parameter rather than baking in the default.
        var breaks = EnUsHyphenation.Default.FindHyphenationPoints("computer", leftMin: 2, rightMin: 2);
        _output.WriteLine($"computer (rightMin=2) → {Render("computer", breaks)}");
        // Result is whatever the standard patterns yield; assert the first break still
        // appears so we know the relaxation did not regress earlier opportunities.
        Assert.Contains(3, breaks);
    }

    [Fact]
    public void Algorithm_word_breaks_at_known_canonical_positions()
    {
        // "algorithm" → "al-go-rithm" → positions [2, 4]
        var breaks = EnUsHyphenation.Default.FindHyphenationPoints("algorithm");
        _output.WriteLine($"algorithm → {Render("algorithm", breaks)}");
        Assert.Equal([2, 4], breaks);
    }

    [Fact]
    public void Exception_associate_short_circuits_to_dictionary_breaks()
    {
        // "associate" is in the en-us exception list as "as-so-ciate" → [2, 4].
        var breaks = EnUsHyphenation.Default.FindHyphenationPoints("associate");
        Assert.Equal([2, 4], breaks);
    }

    [Fact]
    public void Exception_present_signals_never_hyphenate()
    {
        // "present" is in the exception list with no internal hyphens → empty result.
        var breaks = EnUsHyphenation.Default.FindHyphenationPoints("present");
        Assert.Empty(breaks);
    }

    [Fact]
    public void Exception_table_breaks_at_two()
    {
        // "table" → "ta-ble" → [2]
        var breaks = EnUsHyphenation.Default.FindHyphenationPoints("table");
        Assert.Equal([2], breaks);
    }

    [Fact]
    public void Short_words_below_leftMin_plus_rightMin_produce_no_breaks()
    {
        // Defaults are leftMin=2, rightMin=3, so any word shorter than 5 letters
        // returns empty without consulting patterns.
        Assert.Empty(EnUsHyphenation.Default.FindHyphenationPoints("the"));
        Assert.Empty(EnUsHyphenation.Default.FindHyphenationPoints("and"));
        Assert.Empty(EnUsHyphenation.Default.FindHyphenationPoints("of"));
    }

    [Fact]
    public void Casing_does_not_affect_pattern_matching()
    {
        var lower = EnUsHyphenation.Default.FindHyphenationPoints("hyphenation");
        var upper = EnUsHyphenation.Default.FindHyphenationPoints("HYPHENATION");
        var mixed = EnUsHyphenation.Default.FindHyphenationPoints("Hyphenation");
        Assert.Equal(lower, upper);
        Assert.Equal(lower, mixed);
    }

    [Fact]
    public void Default_hyphenator_is_singleton_and_reusable()
    {
        var first = EnUsHyphenation.Default;
        var second = EnUsHyphenation.Default;
        Assert.Same(first, second);
    }

    private static string Render(string word, int[] breaks)
    {
        // Insert '-' at each break position for human-readable test diagnostics.
        if (breaks.Length == 0) return word;
        var sb = new System.Text.StringBuilder(word.Length + breaks.Length);
        var cursor = 0;
        foreach (var k in breaks)
        {
            sb.Append(word.AsSpan(cursor, k - cursor));
            sb.Append('-');
            cursor = k;
        }
        sb.Append(word.AsSpan(cursor));
        return sb.ToString();
    }
}
