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
    public void Emoji_supplementary_plane_codepoints_are_ON()
    {
        // UCD assigns emoji and pictograph codepoints to ON. Covered range is
        // U+1F300–U+1FAFF (Misc Symbols + Emoticons + Transport + Alchemical + …).
        Assert.Equal(BidiClass.ON, BidiClassTable.GetClass(0x1F600)); // 😀
        Assert.Equal(BidiClass.ON, BidiClassTable.GetClass(0x1F4A9)); // 💩
        Assert.Equal(BidiClass.ON, BidiClassTable.GetClass(0x1F680)); // 🚀
    }

    // ───── Post-Stage-12.1 hardening: misclassification fixes per reviewer ────

    [Theory]
    [InlineData(0x0600)] // ARABIC NUMBER SIGN
    [InlineData(0x0601)] // ARABIC SIGN SANAH
    [InlineData(0x0602)] // ARABIC FOOTNOTE MARKER
    [InlineData(0x0603)] // ARABIC SIGN SAFHA
    [InlineData(0x0604)] // ARABIC SIGN SAMVAT
    [InlineData(0x0605)] // ARABIC NUMBER MARK ABOVE
    public void Arabic_number_prefix_marks_U0600_to_U0605_are_AN(int codepoint)
    {
        Assert.Equal(BidiClass.AN, BidiClassTable.GetClass(codepoint));
    }

    [Fact]
    public void Arabic_letter_mark_U061C_is_BN()
    {
        Assert.Equal(BidiClass.BN, BidiClassTable.GetClass(0x061C));
    }

    [Fact]
    public void Arabic_per_mille_signs_are_ET()
    {
        Assert.Equal(BidiClass.ET, BidiClassTable.GetClass(0x0609)); // ARABIC-INDIC PER MILLE SIGN
        Assert.Equal(BidiClass.ET, BidiClassTable.GetClass(0x060A)); // ARABIC-INDIC PER TEN THOUSAND SIGN
        Assert.Equal(BidiClass.ET, BidiClassTable.GetClass(0x066A)); // ARABIC PERCENT SIGN
    }

    [Theory]
    [InlineData(0x066B)] // ARABIC DECIMAL SEPARATOR
    [InlineData(0x066C)] // ARABIC THOUSANDS SEPARATOR
    [InlineData(0x06DD)] // ARABIC END OF AYAH
    public void Arabic_number_helpers_are_AN(int codepoint)
    {
        Assert.Equal(BidiClass.AN, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x08A0)] // Arabic Extended-A start — AL
    [InlineData(0x08B6)] // ARABIC LETTER BEH WITH SMALL MEEM ABOVE — AL
    [InlineData(0x08C7)] // ARABIC LETTER LAM WITH SMALL ARABIC LETTER TAH ABOVE — AL
    public void Arabic_Extended_A_letters_are_AL(int codepoint)
    {
        Assert.Equal(BidiClass.AL, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x08CA)] // ARABIC SMALL HIGH FARSI YEH — NSM
    [InlineData(0x08FF)] // ARABIC MARK SIDEWAYS NOON GHUNNA — NSM (later part of Extended-A is combining marks)
    public void Arabic_Extended_A_combining_marks_are_NSM(int codepoint)
    {
        Assert.Equal(BidiClass.NSM, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0xFB1D)] // HEBREW LETTER YOD WITH HIRIQ
    [InlineData(0xFB2A)] // HEBREW LETTER SHIN WITH SHIN DOT
    [InlineData(0xFB4F)] // HEBREW LIGATURE ALEF LAMED
    public void Hebrew_Presentation_Forms_are_R(int codepoint)
    {
        Assert.Equal(BidiClass.R, BidiClassTable.GetClass(codepoint));
    }

    [Fact]
    public void Hebrew_Presentation_Form_special_cases_match_UCD()
    {
        Assert.Equal(BidiClass.NSM, BidiClassTable.GetClass(0xFB1E)); // POINT JUDEO-SPANISH VARIKA
        Assert.Equal(BidiClass.ES, BidiClassTable.GetClass(0xFB29));  // ALTERNATIVE PLUS SIGN
    }

    [Theory]
    [InlineData(0xFB50)] // Arabic Presentation Forms-A start
    [InlineData(0xFE70)] // Arabic Presentation Forms-B start
    [InlineData(0xFEFC)] // ARABIC LIGATURE LAM WITH ALEF FINAL FORM (last AL in -B)
    public void Arabic_Presentation_Forms_are_AL(int codepoint)
    {
        Assert.Equal(BidiClass.AL, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x1EE00)] // Arabic Mathematical Alphabetic Symbols start
    [InlineData(0x1EE6E)]
    [InlineData(0x1EEBB)] // ARABIC MATHEMATICAL DOUBLE-STRUCK (last assigned)
    public void Arabic_Mathematical_Alphabetic_Symbols_are_AL(int codepoint)
    {
        Assert.Equal(BidiClass.AL, BidiClassTable.GetClass(codepoint));
    }
}
