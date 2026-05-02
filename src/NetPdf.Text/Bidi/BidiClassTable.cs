// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Bidi;

/// <summary>
/// Maps Unicode codepoints to their <see cref="BidiClass"/> per UCD
/// <c>DerivedBidiClass.txt</c>. Stage 12.1 uses a hand-built range table covering the
/// most common scripts and explicit-formatting characters; Stage 12.3 will replace this
/// with a source-generator-emitted compressed table over the full UCD so
/// <c>BidiTest.txt</c> + <c>BidiCharacterTest.txt</c> achieve 100% passage.
/// </summary>
/// <remarks>
/// <para>
/// Coverage today: ASCII, Latin-1 supplement, Greek, Cyrillic, Hebrew, Arabic, plus the
/// explicit-formatting characters (LRE/RLE/LRO/RLO/PDF/LRI/RLI/FSI/PDI). Every other
/// codepoint defaults to <see cref="BidiClass.L"/> — accurate for the majority of
/// modern scripts (Han, Hiragana, Katakana, Devanagari, Thai are all L by default in
/// the UCD), but obviously imperfect for Hebrew/Arabic supplementary blocks not yet
/// in our hand-built table.
/// </para>
/// <para>
/// <b>Stage 12.3 plan</b> swaps the body of <see cref="GetClass"/> with a binary search
/// over a sorted range table generated at build time from a checked-in copy of
/// <c>DerivedBidiClass.txt</c>. The public API stays identical; consumers see no break.
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
        if (codepoint is >= 0x0400 and <= 0x04FF) // Cyrillic
        {
            return BidiClass.L;
        }
        if (codepoint is >= 0x0500 and <= 0x052F) // Cyrillic Supplement
        {
            return BidiClass.L;
        }

        // Hebrew + extended.
        if (codepoint is >= 0x0590 and <= 0x05FF) // Hebrew
        {
            return HebrewClass(codepoint);
        }

        // Arabic.
        if (codepoint is >= 0x0600 and <= 0x06FF) // Arabic
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

        // Default for unassigned / out-of-coverage: L for "most scripts" — matches the
        // UCD default for unallocated codepoints in the BMP and supplementary planes.
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

    private static BidiClass ArabicClass(int codepoint)
    {
        // Arabic-Indic digits 0..9: U+0660..U+0669 → AN.
        if (codepoint >= 0x0660 && codepoint <= 0x0669)
        {
            return BidiClass.AN;
        }
        // Extended Arabic-Indic digits: U+06F0..U+06F9 → EN (per UCD — these get European treatment).
        if (codepoint >= 0x06F0 && codepoint <= 0x06F9)
        {
            return BidiClass.EN;
        }
        // Arabic combining marks (NSM): a representative subset — full coverage in Stage 12.3.
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
        // Arabic comma U+060C → CS.
        if (codepoint == 0x060C)
        {
            return BidiClass.CS;
        }
        // Arabic letters (consonants + tatweel + most punctuation) → AL.
        return BidiClass.AL;
    }
}
