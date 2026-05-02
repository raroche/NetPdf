// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Bidi;
using Xunit;

namespace NetPdf.UnitTests.Text.Bidi;

/// <summary>
/// Broad-coverage tests for the Stage 12.2 UCD-derived range table. Pinning the
/// expected class for one or two representative codepoints in each major Unicode
/// block — the Roslyn source generator that lands in Stage 12.2.x must keep producing
/// these same answers when it replaces the hand-curated body.
/// </summary>
public sealed class BidiClassUcdRangesTests
{
    // ───── LTR scripts ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x0100)] // LATIN CAPITAL LETTER A WITH MACRON
    [InlineData(0x017F)] // LATIN SMALL LETTER LONG S
    [InlineData(0x024F)] // LATIN SMALL LETTER Y WITH STROKE
    public void Latin_Extended_A_and_B_are_L(int codepoint)
    {
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x0250)] // IPA Extensions
    [InlineData(0x02A0)] // IPA Extensions
    public void IPA_Extensions_are_L(int codepoint)
    {
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x0531)] // ARMENIAN CAPITAL LETTER AYB
    [InlineData(0x0561)] // ARMENIAN SMALL LETTER AYB
    public void Armenian_letters_are_L(int codepoint)
    {
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x0E01)] // THAI CHARACTER KO KAI
    [InlineData(0x0E45)] // THAI CHARACTER LAKKHANGYAO
    public void Thai_letters_are_L(int codepoint)
    {
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x4E00)] // CJK UNIFIED IDEOGRAPH-4E00 (一)
    [InlineData(0x9FFF)] // last in main CJK block
    public void CJK_Unified_Ideographs_are_L(int codepoint)
    {
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x3041)] // HIRAGANA LETTER SMALL A
    [InlineData(0x30A1)] // KATAKANA LETTER SMALL A
    public void Hiragana_and_Katakana_are_L(int codepoint)
    {
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0xAC00)] // HANGUL SYLLABLE GA
    [InlineData(0xD7A3)] // last Hangul syllable
    public void Hangul_syllables_are_L(int codepoint)
    {
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(codepoint));
    }

    // ───── RTL scripts ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x07C0)] // NKO DIGIT ZERO
    [InlineData(0x07CA)] // NKO LETTER A
    public void NKo_letters_are_R(int codepoint)
    {
        Assert.Equal(BidiClass.R, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x0710)] // SYRIAC LETTER ALAPH
    [InlineData(0x0712)] // SYRIAC LETTER BETH
    public void Syriac_letters_are_AL(int codepoint)
    {
        Assert.Equal(BidiClass.AL, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x0780)] // THAANA LETTER HAA
    [InlineData(0x07A5)] // THAANA LETTER NAA
    public void Thaana_letters_are_AL(int codepoint)
    {
        Assert.Equal(BidiClass.AL, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x0840)] // MANDAIC LETTER HALQA
    [InlineData(0x0858)] // MANDAIC LETTER AIN
    public void Mandaic_letters_are_AL(int codepoint)
    {
        Assert.Equal(BidiClass.AL, BidiClassTable.GetClass(codepoint));
    }

    // ───── Numbers, separators, terminators ──────────────────────────────────

    [Theory]
    [InlineData(0xFF10)] // FULLWIDTH DIGIT ZERO
    [InlineData(0xFF19)] // FULLWIDTH DIGIT NINE
    public void Fullwidth_digits_are_EN(int codepoint)
    {
        Assert.Equal(BidiClass.EN, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0xFF21)] // FULLWIDTH LATIN CAPITAL LETTER A
    [InlineData(0xFF41)] // FULLWIDTH LATIN SMALL LETTER A
    public void Fullwidth_Latin_letters_are_L(int codepoint)
    {
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x20A0)] // EURO-CURRENCY SIGN
    [InlineData(0x20AC)] // EURO SIGN
    public void Currency_symbols_are_ET(int codepoint)
    {
        Assert.Equal(BidiClass.ET, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x2000)] // EN QUAD
    [InlineData(0x2009)] // THIN SPACE
    [InlineData(0x205F)] // MEDIUM MATHEMATICAL SPACE
    public void Various_general_punctuation_spaces_are_WS(int codepoint)
    {
        Assert.Equal(BidiClass.WS, BidiClassTable.GetClass(codepoint));
    }

    // ───── Format / control characters ────────────────────────────────────────

    [Theory]
    [InlineData(0x200B)] // ZERO WIDTH SPACE
    [InlineData(0x200C)] // ZERO WIDTH NON-JOINER
    [InlineData(0x200D)] // ZERO WIDTH JOINER
    [InlineData(0xFEFF)] // ZERO WIDTH NO-BREAK SPACE / BOM
    public void Format_invisible_chars_are_BN(int codepoint)
    {
        Assert.Equal(BidiClass.BN, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x200E)] // LEFT-TO-RIGHT MARK
    public void LRM_is_L(int codepoint)
    {
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x200F)] // RIGHT-TO-LEFT MARK
    public void RLM_is_R(int codepoint)
    {
        Assert.Equal(BidiClass.R, BidiClassTable.GetClass(codepoint));
    }

    // ───── Combining marks ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x0300)] // COMBINING GRAVE ACCENT
    [InlineData(0x036F)] // COMBINING LATIN SMALL LETTER X
    public void Combining_diacritical_marks_are_NSM(int codepoint)
    {
        Assert.Equal(BidiClass.NSM, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0xFE00)] // VARIATION SELECTOR-1
    [InlineData(0xFE0F)] // VARIATION SELECTOR-16
    public void Variation_selectors_are_NSM(int codepoint)
    {
        Assert.Equal(BidiClass.NSM, BidiClassTable.GetClass(codepoint));
    }

    // ───── Supplementary plane ────────────────────────────────────────────────

    [Theory]
    [InlineData(0x1F300)] // CYCLONE
    [InlineData(0x1F600)] // GRINNING FACE
    [InlineData(0x1F680)] // ROCKET
    [InlineData(0x1F9FF)] // NAZAR AMULET
    [InlineData(0x1FA00)] // CHESS WHITE PAWN (Chess Symbols)
    public void Emoji_and_symbols_are_ON(int codepoint)
    {
        Assert.Equal(BidiClass.ON, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x10800)] // CYPRIOT SYLLABLE A — RTL ancient script
    [InlineData(0x10A00)] // KHAROSHTHI LETTER A — RTL
    public void RTL_ancient_scripts_are_R(int codepoint)
    {
        Assert.Equal(BidiClass.R, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x1EE00)] // ARABIC MATHEMATICAL ALEF
    [InlineData(0x1EEBB)] // ARABIC MATHEMATICAL DOUBLE-STRUCK YEH
    public void Arabic_mathematical_alphabetic_symbols_are_AL(int codepoint)
    {
        Assert.Equal(BidiClass.AL, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x20000)] // CJK Extension B
    [InlineData(0x2FFFF)] // CJK Extension B end region
    public void Han_supplementary_planes_are_L(int codepoint)
    {
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0xE0001)] // LANGUAGE TAG
    [InlineData(0xE0020)] // TAG SPACE
    [InlineData(0xE007F)] // CANCEL TAG
    public void Tag_characters_are_BN(int codepoint)
    {
        Assert.Equal(BidiClass.BN, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0xE0100)] // VARIATION SELECTOR-17
    [InlineData(0xE01EF)] // VARIATION SELECTOR-256
    public void Variation_selectors_supplement_are_NSM(int codepoint)
    {
        Assert.Equal(BidiClass.NSM, BidiClassTable.GetClass(codepoint));
    }

    // ───── Boundary / unassigned-default ──────────────────────────────────────

    [Fact]
    public void Codepoint_in_Private_Use_Area_returns_L_per_UCD_default()
    {
        // PUA is documented as "default L" in UCD when no override applies.
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(0xE000));
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(0xF8FF));
    }

    [Fact]
    public void Lookup_returns_default_L_for_codepoint_in_an_uncovered_unassigned_range()
    {
        // U+2FE0 sits in an unassigned reserved range between Kangxi Radicals (ends
        // U+2FDF) and CJK Symbols and Punctuation (starts U+3000). UCD's default rule
        // for unassigned codepoints in this region is L; the table doesn't enumerate
        // it, so this exercises the binary-search fallthrough specifically.
        Assert.Equal(BidiClass.L, BidiClassUcdRanges.Lookup(0x2FE0));
    }

    [Fact]
    public void Lookup_at_codepoint_zero_returns_BN_for_NULL()
    {
        // U+0000 NULL is BN per UCD (Boundary Neutral, control character).
        Assert.Equal(BidiClass.BN, BidiClassTable.GetClass(0x0000));
    }

    // ───── Post-Stage-12.2 hardening: combining-mark NSM coverage ─────────────

    [Theory]
    [InlineData(0x0900)] // DEVANAGARI SIGN INVERTED CANDRABINDU
    [InlineData(0x0901)] // DEVANAGARI SIGN CANDRABINDU
    [InlineData(0x0902)] // DEVANAGARI SIGN ANUSVARA
    [InlineData(0x093A)] // DEVANAGARI VOWEL SIGN OE
    [InlineData(0x093C)] // DEVANAGARI SIGN NUKTA
    [InlineData(0x0941)] // DEVANAGARI VOWEL SIGN U
    [InlineData(0x0948)] // DEVANAGARI VOWEL SIGN AI
    [InlineData(0x094D)] // DEVANAGARI SIGN VIRAMA
    [InlineData(0x0951)] // DEVANAGARI STRESS SIGN UDATTA
    [InlineData(0x0962)] // DEVANAGARI VOWEL SIGN VOCALIC L
    public void Devanagari_combining_marks_are_NSM(int codepoint)
    {
        Assert.Equal(BidiClass.NSM, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x0903)] // DEVANAGARI SIGN VISARGA — L
    [InlineData(0x0915)] // DEVANAGARI LETTER KA — L
    [InlineData(0x093D)] // DEVANAGARI SIGN AVAGRAHA — L
    [InlineData(0x0966)] // DEVANAGARI DIGIT ZERO — L (Devanagari digits are L not EN per UCD)
    public void Devanagari_letters_and_signs_are_L(int codepoint)
    {
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x0E31)] // THAI CHARACTER MAI HAN-AKAT
    [InlineData(0x0E34)] // THAI CHARACTER SARA I
    [InlineData(0x0E37)] // THAI CHARACTER SARA UEE
    [InlineData(0x0E3A)] // THAI CHARACTER PHINTHU
    [InlineData(0x0E47)] // THAI CHARACTER MAITAIKHU
    [InlineData(0x0E4E)] // THAI CHARACTER YAMAKKAN
    public void Thai_combining_marks_are_NSM(int codepoint)
    {
        Assert.Equal(BidiClass.NSM, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x0F71)] // TIBETAN VOWEL SIGN AA
    [InlineData(0x0F75)] // TIBETAN VOWEL SIGN U-I (combining)
    [InlineData(0x0F7E)] // TIBETAN SIGN RJES SU NGA RO
    [InlineData(0x0F80)] // TIBETAN VOWEL SIGN REVERSED I
    [InlineData(0x0F84)] // TIBETAN MARK HALANTA
    [InlineData(0x0F86)] // TIBETAN SIGN LCI RTAGS
    public void Tibetan_combining_marks_are_NSM(int codepoint)
    {
        Assert.Equal(BidiClass.NSM, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x102D)] // MYANMAR VOWEL SIGN I
    [InlineData(0x1030)] // MYANMAR VOWEL SIGN UU
    [InlineData(0x1032)] // MYANMAR VOWEL SIGN AI
    [InlineData(0x1037)] // MYANMAR SIGN DOT BELOW
    [InlineData(0x1039)] // MYANMAR SIGN VIRAMA
    [InlineData(0x103A)] // MYANMAR SIGN ASAT
    public void Myanmar_combining_marks_are_NSM(int codepoint)
    {
        Assert.Equal(BidiClass.NSM, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x1B00)] // BALINESE SIGN ULU RICEM
    [InlineData(0x1B01)] // BALINESE SIGN ULU CANDRA
    [InlineData(0x1B03)] // BALINESE SIGN SURANG
    [InlineData(0x1B34)] // BALINESE SIGN REREKAN
    [InlineData(0x1B36)] // BALINESE VOWEL SIGN ULU
    [InlineData(0x1B3A)] // BALINESE VOWEL SIGN RA REPA
    [InlineData(0x1B3C)] // BALINESE VOWEL SIGN LA LENGA
    [InlineData(0x1B42)] // BALINESE VOWEL SIGN PEPET
    [InlineData(0x1B6B)] // BALINESE MUSICAL SYMBOL COMBINING TEGEH
    [InlineData(0x1B73)] // BALINESE MUSICAL SYMBOL COMBINING GONG
    public void Balinese_combining_marks_are_NSM(int codepoint)
    {
        Assert.Equal(BidiClass.NSM, BidiClassTable.GetClass(codepoint));
    }

    [Theory]
    [InlineData(0x1B05)] // BALINESE LETTER AKARA — L
    [InlineData(0x1B33)] // BALINESE LETTER HA — L
    [InlineData(0x1B50)] // BALINESE DIGIT ZERO — L
    public void Balinese_letters_and_digits_are_L(int codepoint)
    {
        Assert.Equal(BidiClass.L, BidiClassTable.GetClass(codepoint));
    }
}
