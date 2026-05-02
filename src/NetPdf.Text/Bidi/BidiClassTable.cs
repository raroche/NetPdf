// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Bidi;

/// <summary>
/// Maps Unicode codepoints to their <see cref="BidiClass"/> per UCD
/// <c>DerivedBidiClass.txt</c>. <b>This table is provisional and intentionally
/// incomplete.</b> It covers the high-traffic scripts and the high-risk RTL
/// presentation-form / mathematical-alphabetic blocks; everything else falls back to
/// <see cref="BidiClass.L"/>, which matches the UCD default for the largest
/// uncovered group (Han, Hiragana, Katakana, Devanagari, Thai, Latin Extended) but
/// is wrong for some assigned ranges that haven't been added yet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stage 12.2 plan</b> is to replace the body of <see cref="GetClass"/> with a
/// binary search over a sorted-range table emitted at build time by a Roslyn source
/// generator that consumes a checked-in copy of <c>DerivedBidiClass.txt</c>. The
/// public API stays identical; consumers see no break. Pulling the generator forward
/// (before the X/W/N/I/L rules in Stage 12.3) is deliberate — running the rule
/// machinery against slightly-wrong classes makes regressions much harder to find.
/// </para>
/// <para>
/// <b>Coverage today:</b>
/// </para>
/// <list type="bullet">
/// <item>ASCII (U+0000–U+007F) — letters L, digits EN, separators ES/CS/ET, controls B/S/WS/BN.</item>
/// <item>Latin-1 supplement (U+0080–U+00FF) — letters L; currency / soft-hyphen / NBSP per UCD.</item>
/// <item>Greek (U+0370–U+03FF), Cyrillic + Supplement (U+0400–U+052F) — L.</item>
/// <item>Hebrew (U+0590–U+05FF) — letters R, points NSM.</item>
/// <item>Arabic (U+0600–U+06FF) — number-prefix marks (U+0600–U+0605, U+06DD) AN, per-mille / per-myriad ET, ALM (U+061C) BN, comma CS, percent ET, decimal/thousands separators AN, letters AL, marks NSM, digits AN/EN.</item>
/// <item>Syriac + Arabic Supplement (U+0700–U+077F) — AL.</item>
/// <item>Arabic Extended-A (U+08A0–U+08FF) — AL.</item>
/// <item>Hebrew Presentation Forms (U+FB1D–U+FB4F) — letters R, U+FB1E NSM, U+FB29 ES.</item>
/// <item>Arabic Presentation Forms-A (U+FB50–U+FDFF) and -B (U+FE70–U+FEFF) — AL.</item>
/// <item>Arabic Mathematical Alphabetic Symbols (U+1EE00–U+1EEFF) — AL.</item>
/// <item>Emoji and pictograph supplementary blocks (U+1F300–U+1FAFF) — ON.</item>
/// <item>Explicit-formatting characters (U+202A–U+202E, U+2066–U+2069).</item>
/// </list>
/// <para>
/// Anything outside those ranges defaults to L. That's correct for the large
/// uncovered ranges (Latin Extended, IPA, modifier letters, CJK ideographs, Hangul,
/// Devanagari, Bengali, Tamil, Thai, etc., which UCD does default to L), but it is
/// wrong for some specific ranges (e.g. Tibetan, Mongolian punctuation, NKo).
/// Callers that care about correctness outside the covered set should wait for the
/// Stage 12.2 UCD-derived table.
/// </para>
/// </remarks>
internal static class BidiClassTable
{
    /// <summary>Look up the bidi class of a Unicode codepoint.</summary>
    public static BidiClass GetClass(int codepoint)
    {
        if ((uint)codepoint > 0x10FFFF)
        {
            throw new ArgumentOutOfRangeException(
                nameof(codepoint), codepoint,
                "Codepoint must be in the Unicode range [0, 0x10FFFF].");
        }

        // ASCII fast-path.
        if (codepoint < 0x80)
        {
            return AsciiClass(codepoint);
        }

        // Latin-1 supplement.
        if (codepoint < 0x100)
        {
            return Latin1Class(codepoint);
        }

        // Common script blocks.
        if (codepoint is >= 0x0370 and <= 0x03FF) // Greek and Coptic
        {
            return BidiClass.L;
        }
        if (codepoint is >= 0x0400 and <= 0x052F) // Cyrillic + Supplement
        {
            return BidiClass.L;
        }

        // Hebrew + extended.
        if (codepoint is >= 0x0590 and <= 0x05FF) // Hebrew
        {
            return HebrewClass(codepoint);
        }

        // Arabic.
        if (codepoint is >= 0x0600 and <= 0x06FF)
        {
            return ArabicClass(codepoint);
        }
        if (codepoint is >= 0x0700 and <= 0x074F) // Syriac (RTL)
        {
            return BidiClass.AL;
        }
        if (codepoint is >= 0x0750 and <= 0x077F) // Arabic Supplement
        {
            return BidiClass.AL;
        }
        if (codepoint is >= 0x08A0 and <= 0x08FF) // Arabic Extended-A
        {
            return BidiClass.AL;
        }

        // Hebrew Presentation Forms (U+FB1D–U+FB4F).
        if (codepoint is >= 0xFB1D and <= 0xFB4F)
        {
            return HebrewPresentationFormClass(codepoint);
        }

        // Arabic Presentation Forms-A (U+FB50–U+FDFF) and -B (U+FE70–U+FEFC).
        if (codepoint is (>= 0xFB50 and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFC))
        {
            return BidiClass.AL;
        }

        // Explicit-formatting characters.
        switch (codepoint)
        {
            case 0x202A: return BidiClass.LRE;
            case 0x202B: return BidiClass.RLE;
            case 0x202C: return BidiClass.PDF;
            case 0x202D: return BidiClass.LRO;
            case 0x202E: return BidiClass.RLO;
            case 0x2066: return BidiClass.LRI;
            case 0x2067: return BidiClass.RLI;
            case 0x2068: return BidiClass.FSI;
            case 0x2069: return BidiClass.PDI;
        }

        // Common neutrals + line separators.
        switch (codepoint)
        {
            case 0x2028: return BidiClass.WS; // LINE SEPARATOR
            case 0x2029: return BidiClass.B;  // PARAGRAPH SEPARATOR
        }

        // Supplementary-plane Arabic Mathematical Alphabetic Symbols.
        if (codepoint is >= 0x1EE00 and <= 0x1EEFF)
        {
            return BidiClass.AL;
        }

        // Emoji and supplementary symbols/pictographs — UCD assigns these ON.
        // Coverage: Misc Symbols and Pictographs (U+1F300–U+1F5FF), Emoticons
        // (U+1F600–U+1F64F), Ornamental Dingbats, Transport, Alchemical, Geometric
        // Shapes Extended, Supplemental Arrows-C, Supplemental Symbols and Pictographs,
        // Chess Symbols, Symbols and Pictographs Extended-A.
        if (codepoint is >= 0x1F300 and <= 0x1FAFF)
        {
            return BidiClass.ON;
        }

        // Default for everything else: L. This matches UCD's default for the bulk of
        // unallocated codepoints in the BMP and supplementary planes, plus the major
        // assigned LTR ranges (Latin Extended, IPA, CJK, Hangul, Devanagari, etc.).
        // It IS wrong for some specific blocks (NKo, Tibetan punctuation, etc.) — those
        // get correct classes when Stage 12.2's UCD-derived table lands.
        return BidiClass.L;
    }

    private static BidiClass AsciiClass(int codepoint)
    {
        // Letters
        if ((codepoint >= 'A' && codepoint <= 'Z') || (codepoint >= 'a' && codepoint <= 'z'))
        {
            return BidiClass.L;
        }
        // Digits
        if (codepoint >= '0' && codepoint <= '9')
        {
            return BidiClass.EN;
        }
        // Control characters and structural separators (per UCD).
        return codepoint switch
        {
            0x09 => BidiClass.S,                       // TAB
            0x0B => BidiClass.S,                       // VT
            0x0A or 0x0D or 0x0C => BidiClass.B,       // LF / CR / FF
            0x1C or 0x1D or 0x1E => BidiClass.B,       // FS / GS / RS
            0x1F => BidiClass.S,                       // US
            ' ' => BidiClass.WS,
            '+' or '-' => BidiClass.ES,
            '.' or ',' or '/' or ':' => BidiClass.CS,
            '#' or '$' or '%' => BidiClass.ET,
            _ => BidiClass.ON,
        };
    }

    private static BidiClass Latin1Class(int codepoint)
    {
        // Letters U+00C0..U+00D6 (excluding U+00D7 ×), U+00D8..U+00F6 (excluding U+00F7 ÷),
        // U+00F8..U+00FF, U+00AA, U+00B5, U+00BA.
        if ((codepoint >= 0x00C0 && codepoint <= 0x00D6)
            || (codepoint >= 0x00D8 && codepoint <= 0x00F6)
            || (codepoint >= 0x00F8 && codepoint <= 0x00FF)
            || codepoint is 0x00AA or 0x00B5 or 0x00BA)
        {
            return BidiClass.L;
        }
        // No-break space → CS (Common Separator) per UCD.
        if (codepoint == 0x00A0)
        {
            return BidiClass.CS;
        }
        // Currency symbols → ET.
        if (codepoint is 0x00A2 or 0x00A3 or 0x00A4 or 0x00A5)
        {
            return BidiClass.ET;
        }
        // Plus-minus sign → ET.
        if (codepoint == 0x00B1)
        {
            return BidiClass.ET;
        }
        // Superscript digits 1, 2, 3 (U+00B2, U+00B3, U+00B9) → EN.
        if (codepoint is 0x00B2 or 0x00B3 or 0x00B9)
        {
            return BidiClass.EN;
        }
        // Soft hyphen → BN (control-like).
        if (codepoint == 0x00AD)
        {
            return BidiClass.BN;
        }
        return BidiClass.ON;
    }

    private static BidiClass HebrewClass(int codepoint)
    {
        // Hebrew points (combining marks): U+0591..U+05BD, U+05BF, U+05C1..U+05C2, U+05C4..U+05C5, U+05C7
        if ((codepoint >= 0x0591 && codepoint <= 0x05BD)
            || codepoint is 0x05BF or 0x05C1 or 0x05C2 or 0x05C4 or 0x05C5 or 0x05C7)
        {
            return BidiClass.NSM;
        }
        // Hebrew letters and punctuation are R.
        return BidiClass.R;
    }

    private static BidiClass HebrewPresentationFormClass(int codepoint)
    {
        // U+FB1D..U+FB4F: Hebrew Alphabetic Presentation Forms.
        // - U+FB1E HEBREW POINT JUDEO-SPANISH VARIKA: NSM.
        // - U+FB29 HEBREW LETTER ALTERNATIVE PLUS SIGN: ES.
        // - Everything else in the block: R (letters and ligatures).
        return codepoint switch
        {
            0xFB1E => BidiClass.NSM,
            0xFB29 => BidiClass.ES,
            _ => BidiClass.R,
        };
    }

    private static BidiClass ArabicClass(int codepoint)
    {
        // U+0600..U+0605: Arabic number prefix marks → AN.
        if (codepoint >= 0x0600 && codepoint <= 0x0605)
        {
            return BidiClass.AN;
        }
        // U+0606..U+0608, U+060B..U+060F: mostly AL or ON; specific cases.
        switch (codepoint)
        {
            case 0x0606: // ARABIC-INDIC CUBE ROOT
            case 0x0607: // ARABIC-INDIC FOURTH ROOT
                return BidiClass.ON;
            case 0x0609: // ARABIC-INDIC PER MILLE SIGN
            case 0x060A: // ARABIC-INDIC PER TEN THOUSAND SIGN
                return BidiClass.ET;
            case 0x060C: // ARABIC COMMA
                return BidiClass.CS;
            case 0x061C: // ARABIC LETTER MARK (ALM)
                return BidiClass.BN;
            case 0x061D: // ARABIC END OF TEXT MARK
                return BidiClass.ON;
            case 0x066A: // ARABIC PERCENT SIGN
                return BidiClass.ET;
            case 0x066B: // ARABIC DECIMAL SEPARATOR
            case 0x066C: // ARABIC THOUSANDS SEPARATOR
                return BidiClass.AN;
            case 0x06DD: // ARABIC END OF AYAH
                return BidiClass.AN;
            case 0x06DE: // ARABIC START OF RUB EL HIZB
            case 0x06E9: // ARABIC PLACE OF SAJDAH
                return BidiClass.ON;
        }
        // Arabic-Indic digits 0..9: U+0660..U+0669 → AN.
        if (codepoint >= 0x0660 && codepoint <= 0x0669)
        {
            return BidiClass.AN;
        }
        // Extended Arabic-Indic digits: U+06F0..U+06F9 → EN per UCD.
        if (codepoint >= 0x06F0 && codepoint <= 0x06F9)
        {
            return BidiClass.EN;
        }
        // Arabic combining marks (NSM): a representative subset — full coverage in Stage 12.2.
        if ((codepoint >= 0x0610 && codepoint <= 0x061A)
            || (codepoint >= 0x064B && codepoint <= 0x065F)
            || codepoint == 0x0670
            || (codepoint >= 0x06D6 && codepoint <= 0x06DC)
            || (codepoint >= 0x06DF && codepoint <= 0x06E4)
            || (codepoint >= 0x06E7 && codepoint <= 0x06E8)
            || (codepoint >= 0x06EA && codepoint <= 0x06ED))
        {
            return BidiClass.NSM;
        }
        // Arabic letters (consonants + tatweel + most punctuation) → AL.
        return BidiClass.AL;
    }
}
