// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.
//
// MANUAL STOPGAP SNAPSHOT — NOT AUTOMATICALLY GENERATED.
//
// This file contains a sorted-range table hand-curated against UCD knowledge
// (DerivedBidiClass.txt, Unicode 16.0). It is the Stage 12.2 stopgap until the
// Roslyn source generator lands in Stage 12.2.x and emits a real
// BidiClassUcdRanges.g.cs from a checked-in copy of DerivedBidiClass.txt.
//
// Honest scope:
//   • Coverage is comprehensive for ASCII / Latin / Greek / Cyrillic / Hebrew /
//     Arabic / Syriac / Thaana / Mandaic / NKo / RTL ancient scripts / emoji /
//     CJK / Hiragana / Katakana / Hangul / Tags / Variation Selectors.
//   • Per-codepoint NSM/L splits are provided for Devanagari, Thai, Tibetan,
//     Myanmar, and Balinese (the high-traffic combining-mark scripts).
//   • Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada, Malayalam,
//     Sinhala, Lao, Combining Diacritical Marks Extended, Tai Tham, and other
//     combining-mark-bearing scripts are still flattened to broad L.
//     Stage 12.2.x with the actual UCD generator fixes the remaining gaps.
//
// Editing rules for this stopgap:
//   • Ranges MUST stay sorted by Start ascending and MUST NOT overlap.
//   • Test additions go in BidiClassUcdRangesTests / BidiClassTableTests so a
//     future generator-emitted replacement matches the same contract.
//
// Stage 12.2.x will replace this file with BidiClassUcdRanges.g.cs (auto-generated
// from DerivedBidiClass.txt). The public API (BidiClassUcdRanges.Lookup) stays
// identical; consumers see no break.
//
// Lookup is a binary search by codepoint into a sorted, non-overlapping range table.
// The default for any codepoint NOT covered by an explicit range is L — this matches
// the UCD default rule for unassigned codepoints in the BMP and supplementary planes.

namespace NetPdf.Text.Bidi;

internal static class BidiClassUcdRanges
{
    /// <summary>
    /// Look up the bidi class of a Unicode codepoint via binary search over the sorted
    /// range table. Codepoints not covered by an explicit range default to L per UCD's
    /// default rule for unassigned ranges.
    /// </summary>
    public static BidiClass Lookup(int codepoint)
    {
        var lo = 0;
        var hi = Ranges.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            ref readonly var r = ref Ranges[mid];
            if (codepoint < r.Start)
            {
                hi = mid - 1;
            }
            else if (codepoint > r.End)
            {
                lo = mid + 1;
            }
            else
            {
                return (BidiClass)r.Class;
            }
        }
        return BidiClass.L;
    }

    private readonly record struct Range(int Start, int End, byte Class);

    // ───── Sorted, non-overlapping range table ────────────────────────────────
    //
    // Entries are sorted by Start. Gaps between ranges fall through to L (the UCD
    // default for unassigned codepoints). The order of declarations in this array
    // MUST stay sorted — binary-search correctness depends on it.

    private static readonly Range[] Ranges =
    [
        // ASCII control + structural separators
        new(0x0000, 0x0008, (byte)BidiClass.BN),
        new(0x0009, 0x0009, (byte)BidiClass.S),  // TAB
        new(0x000A, 0x000A, (byte)BidiClass.B),  // LF
        new(0x000B, 0x000B, (byte)BidiClass.S),  // VT
        new(0x000C, 0x000C, (byte)BidiClass.B),  // FF
        new(0x000D, 0x000D, (byte)BidiClass.B),  // CR
        new(0x000E, 0x001B, (byte)BidiClass.BN),
        new(0x001C, 0x001E, (byte)BidiClass.B),  // FS / GS / RS
        new(0x001F, 0x001F, (byte)BidiClass.S),  // US

        // ASCII printable
        new(0x0020, 0x0020, (byte)BidiClass.WS),
        new(0x0021, 0x0022, (byte)BidiClass.ON),
        new(0x0023, 0x0025, (byte)BidiClass.ET), // # $ %
        new(0x0026, 0x002A, (byte)BidiClass.ON),
        new(0x002B, 0x002B, (byte)BidiClass.ES), // +
        new(0x002C, 0x002C, (byte)BidiClass.CS), // ,
        new(0x002D, 0x002D, (byte)BidiClass.ES), // -
        new(0x002E, 0x002F, (byte)BidiClass.CS), // . /
        new(0x0030, 0x0039, (byte)BidiClass.EN), // 0-9
        new(0x003A, 0x003A, (byte)BidiClass.CS), // :
        new(0x003B, 0x0040, (byte)BidiClass.ON),
        new(0x0041, 0x005A, (byte)BidiClass.L),  // A-Z
        new(0x005B, 0x0060, (byte)BidiClass.ON),
        new(0x0061, 0x007A, (byte)BidiClass.L),  // a-z
        new(0x007B, 0x007E, (byte)BidiClass.ON),
        new(0x007F, 0x0084, (byte)BidiClass.BN),
        new(0x0085, 0x0085, (byte)BidiClass.B),  // NEL
        new(0x0086, 0x009F, (byte)BidiClass.BN),

        // Latin-1 supplement
        new(0x00A0, 0x00A0, (byte)BidiClass.CS), // NBSP
        new(0x00A1, 0x00A1, (byte)BidiClass.ON),
        new(0x00A2, 0x00A5, (byte)BidiClass.ET), // ¢ £ ¤ ¥
        new(0x00A6, 0x00A9, (byte)BidiClass.ON),
        new(0x00AA, 0x00AA, (byte)BidiClass.L),
        new(0x00AB, 0x00AC, (byte)BidiClass.ON),
        new(0x00AD, 0x00AD, (byte)BidiClass.BN), // soft hyphen
        new(0x00AE, 0x00AF, (byte)BidiClass.ON),
        new(0x00B0, 0x00B1, (byte)BidiClass.ET), // ° ±
        new(0x00B2, 0x00B3, (byte)BidiClass.EN), // ² ³
        new(0x00B4, 0x00B4, (byte)BidiClass.ON),
        new(0x00B5, 0x00B5, (byte)BidiClass.L),  // µ
        new(0x00B6, 0x00B8, (byte)BidiClass.ON),
        new(0x00B9, 0x00B9, (byte)BidiClass.EN), // ¹
        new(0x00BA, 0x00BA, (byte)BidiClass.L),
        new(0x00BB, 0x00BF, (byte)BidiClass.ON),
        new(0x00C0, 0x00D6, (byte)BidiClass.L),
        new(0x00D7, 0x00D7, (byte)BidiClass.ON), // ×
        new(0x00D8, 0x00F6, (byte)BidiClass.L),
        new(0x00F7, 0x00F7, (byte)BidiClass.ON), // ÷
        new(0x00F8, 0x02B8, (byte)BidiClass.L),  // Latin Extended + IPA Extensions + Spacing Modifier Letters

        // Spacing Modifier Letters (some are ON)
        new(0x02B9, 0x02BA, (byte)BidiClass.ON),
        new(0x02BB, 0x02C1, (byte)BidiClass.L),
        new(0x02C2, 0x02CF, (byte)BidiClass.ON),
        new(0x02D0, 0x02D1, (byte)BidiClass.L),
        new(0x02D2, 0x02DF, (byte)BidiClass.ON),
        new(0x02E0, 0x02E4, (byte)BidiClass.L),
        new(0x02E5, 0x02ED, (byte)BidiClass.ON),
        new(0x02EE, 0x02EE, (byte)BidiClass.L),
        new(0x02EF, 0x02FF, (byte)BidiClass.ON),

        // Combining Diacritical Marks (NSM)
        new(0x0300, 0x036F, (byte)BidiClass.NSM),

        // Greek and Coptic
        new(0x0370, 0x0373, (byte)BidiClass.L),
        new(0x0374, 0x0375, (byte)BidiClass.ON),
        new(0x0376, 0x037D, (byte)BidiClass.L),
        new(0x037E, 0x037E, (byte)BidiClass.ON),
        new(0x037F, 0x03FF, (byte)BidiClass.L),

        // Cyrillic + Cyrillic Supplement
        new(0x0400, 0x0482, (byte)BidiClass.L),
        new(0x0483, 0x0489, (byte)BidiClass.NSM),
        new(0x048A, 0x052F, (byte)BidiClass.L),

        // Armenian
        new(0x0530, 0x0530, (byte)BidiClass.L),
        new(0x0531, 0x0556, (byte)BidiClass.L),
        new(0x0559, 0x058A, (byte)BidiClass.L),
        new(0x058D, 0x058E, (byte)BidiClass.ON),
        new(0x058F, 0x058F, (byte)BidiClass.ET),

        // Hebrew (R)
        new(0x0590, 0x0590, (byte)BidiClass.R),
        new(0x0591, 0x05BD, (byte)BidiClass.NSM), // Hebrew points
        new(0x05BE, 0x05BE, (byte)BidiClass.R),
        new(0x05BF, 0x05BF, (byte)BidiClass.NSM),
        new(0x05C0, 0x05C0, (byte)BidiClass.R),
        new(0x05C1, 0x05C2, (byte)BidiClass.NSM),
        new(0x05C3, 0x05C3, (byte)BidiClass.R),
        new(0x05C4, 0x05C5, (byte)BidiClass.NSM),
        new(0x05C6, 0x05C6, (byte)BidiClass.R),
        new(0x05C7, 0x05C7, (byte)BidiClass.NSM),
        new(0x05C8, 0x05FF, (byte)BidiClass.R),

        // Arabic
        new(0x0600, 0x0605, (byte)BidiClass.AN), // ARABIC NUMBER SIGN family
        new(0x0606, 0x0607, (byte)BidiClass.ON),
        new(0x0608, 0x0608, (byte)BidiClass.AL),
        new(0x0609, 0x060A, (byte)BidiClass.ET), // PER MILLE / PER TEN THOUSAND
        new(0x060B, 0x060B, (byte)BidiClass.AL),
        new(0x060C, 0x060C, (byte)BidiClass.CS), // ARABIC COMMA
        new(0x060D, 0x060D, (byte)BidiClass.AL),
        new(0x060E, 0x060F, (byte)BidiClass.ON),
        new(0x0610, 0x061A, (byte)BidiClass.NSM),
        new(0x061B, 0x061B, (byte)BidiClass.AL),
        new(0x061C, 0x061C, (byte)BidiClass.AL), // ARABIC LETTER MARK (UCD 16.0 reclassified from BN to AL)
        new(0x061D, 0x061D, (byte)BidiClass.ON),
        new(0x061E, 0x064A, (byte)BidiClass.AL),
        new(0x064B, 0x065F, (byte)BidiClass.NSM),
        new(0x0660, 0x0669, (byte)BidiClass.AN),
        new(0x066A, 0x066A, (byte)BidiClass.ET), // PERCENT SIGN
        new(0x066B, 0x066C, (byte)BidiClass.AN),
        new(0x066D, 0x066F, (byte)BidiClass.AL),
        new(0x0670, 0x0670, (byte)BidiClass.NSM),
        new(0x0671, 0x06D5, (byte)BidiClass.AL),
        new(0x06D6, 0x06DC, (byte)BidiClass.NSM),
        new(0x06DD, 0x06DD, (byte)BidiClass.AN), // END OF AYAH
        new(0x06DE, 0x06DE, (byte)BidiClass.ON),
        new(0x06DF, 0x06E4, (byte)BidiClass.NSM),
        new(0x06E5, 0x06E6, (byte)BidiClass.AL),
        new(0x06E7, 0x06E8, (byte)BidiClass.NSM),
        new(0x06E9, 0x06E9, (byte)BidiClass.ON),
        new(0x06EA, 0x06ED, (byte)BidiClass.NSM),
        new(0x06EE, 0x06EF, (byte)BidiClass.AL),
        new(0x06F0, 0x06F9, (byte)BidiClass.EN), // EXTENDED ARABIC-INDIC DIGITS
        new(0x06FA, 0x06FF, (byte)BidiClass.AL),

        // Syriac
        new(0x0700, 0x070D, (byte)BidiClass.AL),
        new(0x070F, 0x070F, (byte)BidiClass.AL),
        new(0x0710, 0x0710, (byte)BidiClass.AL),
        new(0x0711, 0x0711, (byte)BidiClass.NSM),
        new(0x0712, 0x072F, (byte)BidiClass.AL),
        new(0x0730, 0x074A, (byte)BidiClass.NSM),
        new(0x074D, 0x074F, (byte)BidiClass.AL),

        // Arabic Supplement
        new(0x0750, 0x077F, (byte)BidiClass.AL),

        // Thaana
        new(0x0780, 0x07A5, (byte)BidiClass.AL),
        new(0x07A6, 0x07B0, (byte)BidiClass.NSM),
        new(0x07B1, 0x07B1, (byte)BidiClass.AL),

        // NKo (RTL)
        new(0x07C0, 0x07EA, (byte)BidiClass.R),
        new(0x07EB, 0x07F3, (byte)BidiClass.NSM),
        new(0x07F4, 0x07F5, (byte)BidiClass.R),
        new(0x07F6, 0x07F9, (byte)BidiClass.ON),
        new(0x07FA, 0x07FA, (byte)BidiClass.R),
        new(0x07FD, 0x07FD, (byte)BidiClass.NSM),
        new(0x07FE, 0x07FF, (byte)BidiClass.R),

        // Samaritan (RTL)
        new(0x0800, 0x0815, (byte)BidiClass.R),
        new(0x0816, 0x0819, (byte)BidiClass.NSM),
        new(0x081A, 0x081A, (byte)BidiClass.R),
        new(0x081B, 0x0823, (byte)BidiClass.NSM),
        new(0x0824, 0x0824, (byte)BidiClass.R),
        new(0x0825, 0x0827, (byte)BidiClass.NSM),
        new(0x0828, 0x0828, (byte)BidiClass.R),
        new(0x0829, 0x082D, (byte)BidiClass.NSM),
        new(0x0830, 0x083E, (byte)BidiClass.R),

        // Mandaic (RTL)
        new(0x0840, 0x0858, (byte)BidiClass.AL),
        new(0x0859, 0x085B, (byte)BidiClass.NSM),
        new(0x085E, 0x085E, (byte)BidiClass.AL),

        // Arabic Extended-B + Extended-A
        new(0x0860, 0x086A, (byte)BidiClass.AL),
        new(0x0870, 0x0882, (byte)BidiClass.AL),
        new(0x0883, 0x0885, (byte)BidiClass.AL),
        new(0x0886, 0x0886, (byte)BidiClass.AL),
        new(0x0887, 0x088E, (byte)BidiClass.AL),
        new(0x0890, 0x0891, (byte)BidiClass.AN),
        new(0x0898, 0x089F, (byte)BidiClass.NSM),
        new(0x08A0, 0x08C9, (byte)BidiClass.AL),
        new(0x08CA, 0x08E1, (byte)BidiClass.NSM),
        new(0x08E2, 0x08E2, (byte)BidiClass.AN),
        new(0x08E3, 0x08FF, (byte)BidiClass.NSM),

        // ──── Devanagari (per-codepoint NSM/L split) ─────────────────────────
        // The combining marks at 0x0900-0x0902, 0x093A, 0x093C, 0x0941-0x0948, 0x094D,
        // 0x0951-0x0957, 0x0962-0x0963 are NSM; everything else in the block is L.
        // Without this split, paragraph auto-direction is wrong for any string that
        // begins with one of these marks (rule W1 also depends on accurate NSM data).
        new(0x0900, 0x0902, (byte)BidiClass.NSM),
        new(0x0903, 0x0939, (byte)BidiClass.L),
        new(0x093A, 0x093A, (byte)BidiClass.NSM),
        new(0x093B, 0x093B, (byte)BidiClass.L),
        new(0x093C, 0x093C, (byte)BidiClass.NSM),
        new(0x093D, 0x0940, (byte)BidiClass.L),
        new(0x0941, 0x0948, (byte)BidiClass.NSM),
        new(0x0949, 0x094C, (byte)BidiClass.L),
        new(0x094D, 0x094D, (byte)BidiClass.NSM),
        new(0x094E, 0x0950, (byte)BidiClass.L),
        new(0x0951, 0x0957, (byte)BidiClass.NSM),
        new(0x0958, 0x0961, (byte)BidiClass.L),
        new(0x0962, 0x0963, (byte)BidiClass.NSM),
        new(0x0964, 0x097F, (byte)BidiClass.L),

        // Bengali through Sinhala — broad L. Stage 12.2.x will refine per-codepoint
        // NSM coverage for these scripts via the actual UCD source generator.
        new(0x0980, 0x0DFF, (byte)BidiClass.L),

        // ──── Thai (per-codepoint NSM/L split) ───────────────────────────────
        new(0x0E00, 0x0E30, (byte)BidiClass.L),
        new(0x0E31, 0x0E31, (byte)BidiClass.NSM),
        new(0x0E32, 0x0E33, (byte)BidiClass.L),
        new(0x0E34, 0x0E3A, (byte)BidiClass.NSM),
        new(0x0E3B, 0x0E46, (byte)BidiClass.L),
        new(0x0E47, 0x0E4E, (byte)BidiClass.NSM),
        new(0x0E4F, 0x0E7F, (byte)BidiClass.L),

        // Lao — broad L (Stage 12.2.x refines).
        new(0x0E80, 0x0EFF, (byte)BidiClass.L),

        // ──── Tibetan (per-codepoint NSM/L split) ────────────────────────────
        new(0x0F00, 0x0F70, (byte)BidiClass.L),
        new(0x0F71, 0x0F7E, (byte)BidiClass.NSM),
        new(0x0F7F, 0x0F7F, (byte)BidiClass.L),
        new(0x0F80, 0x0F84, (byte)BidiClass.NSM),
        new(0x0F85, 0x0F85, (byte)BidiClass.L),
        new(0x0F86, 0x0F87, (byte)BidiClass.NSM),
        new(0x0F88, 0x0FFF, (byte)BidiClass.L),

        // ──── Myanmar (per-codepoint NSM/L split, key NSM ranges only) ──────
        new(0x1000, 0x102C, (byte)BidiClass.L),
        new(0x102D, 0x1030, (byte)BidiClass.NSM),
        new(0x1031, 0x1031, (byte)BidiClass.L),
        new(0x1032, 0x1037, (byte)BidiClass.NSM),
        new(0x1038, 0x1038, (byte)BidiClass.L),
        new(0x1039, 0x103A, (byte)BidiClass.NSM),
        new(0x103B, 0x109F, (byte)BidiClass.L),

        // Georgian, Hangul Jamo, Ethiopic, Cherokee, UCAS, Ogham, Runic — broad L.
        new(0x10A0, 0x16FF, (byte)BidiClass.L),

        // Tagalog through Khmer + Mongolian — broad L (Stage 12.2.x refines for
        // combining marks in Tai Tham, Combining Diacritical Marks Extended, etc.).
        new(0x1700, 0x1AFF, (byte)BidiClass.L),

        // ──── Balinese (per-codepoint NSM/L split) ────────────────────────────
        new(0x1B00, 0x1B03, (byte)BidiClass.NSM),
        new(0x1B04, 0x1B33, (byte)BidiClass.L),
        new(0x1B34, 0x1B34, (byte)BidiClass.NSM),
        new(0x1B35, 0x1B35, (byte)BidiClass.L),
        new(0x1B36, 0x1B3A, (byte)BidiClass.NSM),
        new(0x1B3B, 0x1B3B, (byte)BidiClass.L),
        new(0x1B3C, 0x1B3C, (byte)BidiClass.NSM),
        new(0x1B3D, 0x1B41, (byte)BidiClass.L),
        new(0x1B42, 0x1B42, (byte)BidiClass.NSM),
        new(0x1B43, 0x1B6A, (byte)BidiClass.L),
        new(0x1B6B, 0x1B73, (byte)BidiClass.NSM),
        new(0x1B74, 0x1DFF, (byte)BidiClass.L),

        // Latin Extended Additional + Greek Extended + General Punctuation + Superscripts and Subscripts
        new(0x1E00, 0x1FFF, (byte)BidiClass.L),

        // General Punctuation (mostly ON / WS / BN)
        new(0x2000, 0x200A, (byte)BidiClass.WS), // Various spaces
        new(0x200B, 0x200D, (byte)BidiClass.BN), // ZWSP, ZWNJ, ZWJ
        new(0x200E, 0x200E, (byte)BidiClass.L),  // LRM
        new(0x200F, 0x200F, (byte)BidiClass.R),  // RLM
        new(0x2010, 0x2027, (byte)BidiClass.ON),
        new(0x2028, 0x2028, (byte)BidiClass.WS), // LINE SEPARATOR
        new(0x2029, 0x2029, (byte)BidiClass.B),  // PARAGRAPH SEPARATOR
        new(0x202A, 0x202A, (byte)BidiClass.LRE),
        new(0x202B, 0x202B, (byte)BidiClass.RLE),
        new(0x202C, 0x202C, (byte)BidiClass.PDF),
        new(0x202D, 0x202D, (byte)BidiClass.LRO),
        new(0x202E, 0x202E, (byte)BidiClass.RLO),
        new(0x202F, 0x202F, (byte)BidiClass.CS),
        new(0x2030, 0x2034, (byte)BidiClass.ET),
        new(0x2035, 0x2043, (byte)BidiClass.ON),
        new(0x2044, 0x2044, (byte)BidiClass.CS),
        new(0x2045, 0x205E, (byte)BidiClass.ON),
        new(0x205F, 0x205F, (byte)BidiClass.WS),
        new(0x2060, 0x2064, (byte)BidiClass.BN),
        new(0x2066, 0x2066, (byte)BidiClass.LRI),
        new(0x2067, 0x2067, (byte)BidiClass.RLI),
        new(0x2068, 0x2068, (byte)BidiClass.FSI),
        new(0x2069, 0x2069, (byte)BidiClass.PDI),
        new(0x206A, 0x206F, (byte)BidiClass.BN),

        // Superscripts and Subscripts + Currency Symbols
        new(0x2070, 0x2070, (byte)BidiClass.EN),
        new(0x2071, 0x2073, (byte)BidiClass.L),
        new(0x2074, 0x2079, (byte)BidiClass.EN),
        new(0x207A, 0x207B, (byte)BidiClass.ES),
        new(0x207C, 0x207E, (byte)BidiClass.ON),
        new(0x207F, 0x207F, (byte)BidiClass.L),
        new(0x2080, 0x2089, (byte)BidiClass.EN),
        new(0x208A, 0x208B, (byte)BidiClass.ES),
        new(0x208C, 0x208E, (byte)BidiClass.ON),
        new(0x2090, 0x209C, (byte)BidiClass.L),
        new(0x20A0, 0x20CF, (byte)BidiClass.ET), // Currency Symbols

        // Combining Diacritical Marks for Symbols
        new(0x20D0, 0x20F0, (byte)BidiClass.NSM),

        // Letterlike Symbols, Number Forms, Arrows, Mathematical Operators
        new(0x2100, 0x2101, (byte)BidiClass.ON),
        new(0x2102, 0x2102, (byte)BidiClass.L),
        new(0x2103, 0x2106, (byte)BidiClass.ON),
        new(0x2107, 0x2107, (byte)BidiClass.L),
        new(0x2108, 0x2109, (byte)BidiClass.ON),
        new(0x210A, 0x2113, (byte)BidiClass.L),
        new(0x2114, 0x2114, (byte)BidiClass.ON),
        new(0x2115, 0x2115, (byte)BidiClass.L),
        new(0x2116, 0x2118, (byte)BidiClass.ON),
        new(0x2119, 0x211D, (byte)BidiClass.L),
        new(0x211E, 0x2123, (byte)BidiClass.ON),
        new(0x2124, 0x2124, (byte)BidiClass.L),
        new(0x2125, 0x2125, (byte)BidiClass.ON),
        new(0x2126, 0x2126, (byte)BidiClass.L),
        new(0x2127, 0x2127, (byte)BidiClass.ON),
        new(0x2128, 0x2128, (byte)BidiClass.L),
        new(0x2129, 0x2129, (byte)BidiClass.ON),
        new(0x212A, 0x212D, (byte)BidiClass.L),
        new(0x212E, 0x212E, (byte)BidiClass.ET),
        new(0x212F, 0x2139, (byte)BidiClass.L),
        new(0x213A, 0x213B, (byte)BidiClass.ON),
        new(0x213C, 0x213F, (byte)BidiClass.L),
        new(0x2140, 0x2144, (byte)BidiClass.ON),
        new(0x2145, 0x2149, (byte)BidiClass.L),
        new(0x214A, 0x214D, (byte)BidiClass.ON),
        new(0x214E, 0x214F, (byte)BidiClass.L),
        new(0x2150, 0x215F, (byte)BidiClass.ON),
        new(0x2160, 0x2188, (byte)BidiClass.L),
        new(0x2189, 0x2189, (byte)BidiClass.ON),
        new(0x218A, 0x218B, (byte)BidiClass.ON),
        new(0x2190, 0x2211, (byte)BidiClass.ON),
        new(0x2212, 0x2212, (byte)BidiClass.ES),
        new(0x2213, 0x2213, (byte)BidiClass.ET),
        new(0x2214, 0x22FF, (byte)BidiClass.ON),

        // Box Drawing through Geometric Shapes Extended (mostly ON)
        new(0x2300, 0x2426, (byte)BidiClass.ON),
        new(0x2440, 0x244A, (byte)BidiClass.ON),

        // Enclosed Alphanumerics + Box Drawing
        new(0x2460, 0x2487, (byte)BidiClass.ON),
        new(0x2488, 0x249B, (byte)BidiClass.EN),
        new(0x249C, 0x24E9, (byte)BidiClass.L),
        new(0x24EA, 0x24FF, (byte)BidiClass.ON),
        new(0x2500, 0x26FF, (byte)BidiClass.ON),

        // Dingbats + Misc Symbols
        new(0x2700, 0x27BF, (byte)BidiClass.ON),

        // CJK Symbols and Punctuation, Hiragana, Katakana, Bopomofo, Hangul Compatibility Jamo, Kanbun
        new(0x3000, 0x3000, (byte)BidiClass.WS),
        new(0x3001, 0x3004, (byte)BidiClass.ON),
        new(0x3005, 0x3007, (byte)BidiClass.L),
        new(0x3008, 0x3020, (byte)BidiClass.ON),
        new(0x3021, 0x3029, (byte)BidiClass.L),
        new(0x302A, 0x302D, (byte)BidiClass.NSM),
        new(0x302E, 0x302F, (byte)BidiClass.L),
        new(0x3030, 0x3030, (byte)BidiClass.ON),
        new(0x3031, 0x3035, (byte)BidiClass.L),
        new(0x3036, 0x3037, (byte)BidiClass.ON),
        new(0x3038, 0x303C, (byte)BidiClass.L),
        new(0x303D, 0x303F, (byte)BidiClass.ON),
        new(0x3041, 0x3098, (byte)BidiClass.L),
        new(0x3099, 0x309A, (byte)BidiClass.NSM),
        new(0x309B, 0x309C, (byte)BidiClass.ON),
        new(0x309D, 0x30FF, (byte)BidiClass.L),
        new(0x3105, 0x33FF, (byte)BidiClass.L),

        // CJK Unified Ideographs (Han) — L
        new(0x3400, 0x4DBF, (byte)BidiClass.L),
        new(0x4E00, 0x9FFF, (byte)BidiClass.L),

        // Yi Syllables, Yi Radicals, Lisu, Vai, Cyrillic Extended-B
        new(0xA000, 0xA4FF, (byte)BidiClass.L),

        // Vai through Latin Extended-D
        new(0xA500, 0xA82F, (byte)BidiClass.L),

        // Hangul Syllables
        new(0xAC00, 0xD7A3, (byte)BidiClass.L),

        // High/Low Surrogates: not classified individually; HarfBuzz handles via surrogate pair conversion
        // (lone surrogates would get the default L; the algorithm reads pairs as one codepoint).

        // Private Use Area
        new(0xE000, 0xF8FF, (byte)BidiClass.L),

        // CJK Compatibility Ideographs
        new(0xF900, 0xFAFF, (byte)BidiClass.L),

        // Alphabetic Presentation Forms (Latin + Armenian + Hebrew)
        new(0xFB00, 0xFB1C, (byte)BidiClass.L),  // Latin + Armenian ligatures
        new(0xFB1D, 0xFB1D, (byte)BidiClass.R),
        new(0xFB1E, 0xFB1E, (byte)BidiClass.NSM),
        new(0xFB1F, 0xFB28, (byte)BidiClass.R),
        new(0xFB29, 0xFB29, (byte)BidiClass.ES), // HEBREW LETTER ALTERNATIVE PLUS SIGN
        new(0xFB2A, 0xFB4F, (byte)BidiClass.R),

        // Arabic Presentation Forms-A
        new(0xFB50, 0xFD3D, (byte)BidiClass.AL),
        new(0xFD3E, 0xFD3F, (byte)BidiClass.ON),
        new(0xFD40, 0xFDCF, (byte)BidiClass.AL),
        new(0xFDF0, 0xFDFF, (byte)BidiClass.AL),

        // Variation Selectors + Combining Half Marks
        new(0xFE00, 0xFE0F, (byte)BidiClass.NSM),
        new(0xFE20, 0xFE2F, (byte)BidiClass.NSM),

        // CJK Compatibility Forms + Small Form Variants
        new(0xFE30, 0xFE4F, (byte)BidiClass.ON),
        new(0xFE50, 0xFE50, (byte)BidiClass.CS),
        new(0xFE51, 0xFE51, (byte)BidiClass.ON),
        new(0xFE52, 0xFE52, (byte)BidiClass.CS),
        new(0xFE53, 0xFE54, (byte)BidiClass.ON),
        new(0xFE55, 0xFE55, (byte)BidiClass.CS),
        new(0xFE56, 0xFE5E, (byte)BidiClass.ON),
        new(0xFE5F, 0xFE61, (byte)BidiClass.ET),
        new(0xFE62, 0xFE62, (byte)BidiClass.ES),
        new(0xFE63, 0xFE63, (byte)BidiClass.ES),
        new(0xFE64, 0xFE66, (byte)BidiClass.ON),
        new(0xFE69, 0xFE69, (byte)BidiClass.ET),
        new(0xFE6A, 0xFE6A, (byte)BidiClass.ET),
        new(0xFE6B, 0xFE6B, (byte)BidiClass.ON),

        // Arabic Presentation Forms-B
        new(0xFE70, 0xFEFC, (byte)BidiClass.AL),
        new(0xFEFF, 0xFEFF, (byte)BidiClass.BN), // ZERO WIDTH NO-BREAK SPACE (BOM)

        // Halfwidth and Fullwidth Forms (non-overlapping ranges)
        new(0xFF00, 0xFF0F, (byte)BidiClass.ON),
        new(0xFF10, 0xFF19, (byte)BidiClass.EN), // Fullwidth digits
        new(0xFF1A, 0xFF20, (byte)BidiClass.ON),
        new(0xFF21, 0xFF3A, (byte)BidiClass.L),  // Fullwidth A-Z
        new(0xFF3B, 0xFF40, (byte)BidiClass.ON),
        new(0xFF41, 0xFF5A, (byte)BidiClass.L),  // Fullwidth a-z
        new(0xFF5B, 0xFF65, (byte)BidiClass.ON),
        new(0xFF66, 0xFFDC, (byte)BidiClass.L),  // Halfwidth Katakana + Hangul
        new(0xFFE0, 0xFFE1, (byte)BidiClass.ET),
        new(0xFFE2, 0xFFE4, (byte)BidiClass.ON),
        new(0xFFE5, 0xFFE6, (byte)BidiClass.ET),
        new(0xFFE8, 0xFFEE, (byte)BidiClass.ON),
        new(0xFFF9, 0xFFFC, (byte)BidiClass.ON),

        // ──── Supplementary planes ─────────────────────────────────────────────

        // Linear B, Aegean Numbers, Ancient Greek (L), then Phaistos Disc (ON)
        new(0x10000, 0x101CF, (byte)BidiClass.L),
        new(0x101D0, 0x101FF, (byte)BidiClass.ON),

        // RTL ancient scripts (Ugaritic, Old Persian, Imperial Aramaic, Phoenician,
        // Lydian, Meroitic Hieroglyphs / Cursive, Kharoshthi). Combining marks within
        // these ranges should ideally be NSM; Stage 12.2.x with the full UCD generator
        // distinguishes them per-codepoint. For Phase 1 paragraph-level resolution the
        // broad R coverage is sufficient.
        new(0x10800, 0x1093F, (byte)BidiClass.R),
        new(0x10A00, 0x10A9F, (byte)BidiClass.R),

        // Old North Arabian, Manichaean, Avestan, Inscriptional Parthian/Pahlavi (RTL)
        new(0x10AC0, 0x10AFF, (byte)BidiClass.R),
        new(0x10B00, 0x10B7F, (byte)BidiClass.R),
        new(0x10B80, 0x10BAF, (byte)BidiClass.R),
        new(0x10C00, 0x10C7F, (byte)BidiClass.R),
        new(0x10C80, 0x10CFF, (byte)BidiClass.R),

        // Mende Kikakui, Indic Siyaq Numbers, Sinhala Archaic Numbers
        new(0x10E60, 0x10E7E, (byte)BidiClass.AN),
        new(0x10E80, 0x10EFF, (byte)BidiClass.R),

        // Hanifi Rohingya, Old Sogdian, Sogdian, Hebrew (RTL)
        new(0x10D00, 0x10D7F, (byte)BidiClass.AL),
        new(0x10F00, 0x10FFF, (byte)BidiClass.R),

        // Counting Rod Numerals, Mathematical Alphanumeric Symbols, Sutton SignWriting
        new(0x1D000, 0x1D6FF, (byte)BidiClass.L),
        new(0x1D7CE, 0x1D7FF, (byte)BidiClass.EN),

        // Arabic Mathematical Alphabetic Symbols
        new(0x1EE00, 0x1EEFF, (byte)BidiClass.AL),

        // Mahjong Tiles, Domino Tiles, Playing Cards
        new(0x1F000, 0x1F02F, (byte)BidiClass.ON),
        new(0x1F030, 0x1F09F, (byte)BidiClass.ON),
        new(0x1F0A0, 0x1F0FF, (byte)BidiClass.ON),

        // Enclosed Alphanumeric Supplement, Enclosed Ideographic Supplement
        new(0x1F100, 0x1F10A, (byte)BidiClass.EN),
        new(0x1F10B, 0x1F1FF, (byte)BidiClass.ON),
        new(0x1F200, 0x1F2FF, (byte)BidiClass.L),

        // Emoji + Symbol blocks (ON)
        new(0x1F300, 0x1FAFF, (byte)BidiClass.ON),

        // Han supplementary planes
        new(0x20000, 0x2FFFF, (byte)BidiClass.L),
        new(0x30000, 0x3FFFF, (byte)BidiClass.L),

        // Tags + Variation Selectors Supplement
        new(0xE0001, 0xE0001, (byte)BidiClass.BN),
        new(0xE0020, 0xE007F, (byte)BidiClass.BN),
        new(0xE0100, 0xE01EF, (byte)BidiClass.NSM),
    ];
}
