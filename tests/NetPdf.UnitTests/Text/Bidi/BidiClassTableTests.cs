// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Bidi;
using Xunit;

namespace NetPdf.UnitTests.Text.Bidi;

/// <summary>
/// Per-class tests for the Stage 12.1 hand-built bidi-class lookup. Stage 12.3 will
/// replace the body of <see cref="BidiClassTable.GetClass"/> with a UCD-derived table;
/// these tests pin the contract (codepoint → class for representative chars across the
/// covered scripts) so the swap doesn't regress.
/// </summary>
/// <remarks>
/// Test method parameters use <see cref="int"/> for the expected <see cref="BidiClass"/>
/// because xUnit's <c>[Theory]</c> requires public-accessible parameter types and
/// <see cref="BidiClass"/> is internal. The test body casts back to the enum.
/// </remarks>
public sealed class BidiClassTableTests
{
    [Theory]
    [InlineData('A')]
    [InlineData('Z')]
    [InlineData('a')]
    [InlineData('z')]
    public void Ascii_letters_are_L(int codepoint)
    {
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData('0')]
    [InlineData('5')]
    [InlineData('9')]
    public void Ascii_digits_are_EN(int codepoint)
    {
        Assert.Equal(BidiClass.EN, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(' ', (int)BidiClass.WS)]
    [InlineData('\t', (int)BidiClass.S)]
    [InlineData('\n', (int)BidiClass.B)]
    [InlineData('\r', (int)BidiClass.B)]
    public void Ascii_whitespace_classes_match_UCD(int codepoint, int expectedClass)
    {
        Assert.Equal((BidiClass)expectedClass, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData('+', (int)BidiClass.ES)]
    [InlineData('-', (int)BidiClass.ES)]
    [InlineData('.', (int)BidiClass.CS)]
    [InlineData(',', (int)BidiClass.CS)]
    [InlineData(':', (int)BidiClass.CS)]
    [InlineData('/', (int)BidiClass.CS)]
    [InlineData('$', (int)BidiClass.ET)]
    [InlineData('%', (int)BidiClass.ET)]
    [InlineData('#', (int)BidiClass.ET)]
    public void Ascii_separators_and_terminators_match_UCD(int codepoint, int expectedClass)
    {
        Assert.Equal((BidiClass)expectedClass, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x05D0)] // Hebrew letter alef
    [InlineData(0x05D9)] // Hebrew letter yod
    [InlineData(0x05E0)] // Hebrew letter nun
    public void Hebrew_letters_are_R(int codepoint)
    {
        Assert.Equal(BidiClass.R, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x0591)] // Hebrew accent etnahta
    [InlineData(0x05B0)] // Hebrew point sheva
    [InlineData(0x05BD)] // Hebrew point meteg
    public void Hebrew_combining_marks_are_NSM(int codepoint)
    {
        Assert.Equal(BidiClass.NSM, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x0627)] // Arabic letter alef
    [InlineData(0x0628)] // Arabic letter beh
    [InlineData(0x062A)] // Arabic letter teh
    public void Arabic_letters_are_AL(int codepoint)
    {
        Assert.Equal(BidiClass.AL, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x0660, (int)BidiClass.AN)] // Arabic-Indic digit zero → AN
    [InlineData(0x0669, (int)BidiClass.AN)] // Arabic-Indic digit nine
    [InlineData(0x06F0, (int)BidiClass.EN)] // Extended Arabic-Indic zero → EN per UCD
    [InlineData(0x06F9, (int)BidiClass.EN)] // Extended Arabic-Indic nine
    public void Arabic_digits_carry_AN_or_EN_per_UCD(int codepoint, int expectedClass)
    {
        Assert.Equal((BidiClass)expectedClass, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x202A, (int)BidiClass.LRE)]
    [InlineData(0x202B, (int)BidiClass.RLE)]
    [InlineData(0x202C, (int)BidiClass.PDF)]
    [InlineData(0x202D, (int)BidiClass.LRO)]
    [InlineData(0x202E, (int)BidiClass.RLO)]
    [InlineData(0x2066, (int)BidiClass.LRI)]
    [InlineData(0x2067, (int)BidiClass.RLI)]
    [InlineData(0x2068, (int)BidiClass.FSI)]
    [InlineData(0x2069, (int)BidiClass.PDI)]
    public void Explicit_formatting_characters_have_correct_class(int codepoint, int expectedClass)
    {
        Assert.Equal((BidiClass)expectedClass, BidiClassTable.GetClass(codepoint));
    }

    [Fact]
    public void Greek_letters_are_L()
    {
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(0x03B1)); // alpha
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(0x0391)); // capital ALPHA
    }

    [Fact]
    public void Cyrillic_letters_are_L()
    {
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(0x0410)); // А (capital A)
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(0x044F)); // я (lowercase ya)
    }

    [Fact]
    public void Latin1_letters_are_L_skipping_multiplication_division_signs()
    {
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(0x00C0));   // À
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(0x00FF));   // ÿ
        // U+00D7 (×) and U+00F7 (÷) are ON in UCD — we don't include them in the L ranges.
        Assert.NotEqual(BidiClass.L, BidiClassTable.GetClass(0x00D7));
        Assert.NotEqual(BidiClass.L, BidiClassTable.GetClass(0x00F7));
    }

    [Fact]
    public void Latin1_no_break_space_is_CS()
    {
        Assert.Equal(BidiClass.CS, BidiClassTable.GetClass(0x00A0));
    }

    [Fact]
    public void Latin1_currency_symbols_are_ET()
    {
        Assert.Equal(BidiClass.ET, BidiClassTable.GetClass(0x00A3)); // £
        Assert.Equal(BidiClass.ET, BidiClassTable.GetClass(0x00A5)); // ¥
    }

    [Fact]
    public void Paragraph_separator_is_B()
    {
        Assert.Equal(BidiClass.B, BidiClassTable.GetClass(0x2029));
    }

    [Fact]
    public void Codepoint_above_unicode_max_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BidiClassTable.GetClass(0x110000));
        Assert.Throws<ArgumentOutOfRangeException>(() => BidiClassTable.GetClass(-1));
    }

    [Fact]
    public void Default_for_supplementary_plane_codepoint_is_L()
    {
        // U+1F600 (😀 emoji) — not in our hand-built coverage; defaults to L.
        // Real UCD has these as ON; Stage 12.3 will refine. Test pins current behavior.
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(0x1F600));
    }
}
