// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Segmentation;

/// <summary>
/// Grapheme_Cluster_Break property values per Unicode UAX #29 §3.1
/// (<c>https://www.unicode.org/reports/tr29/</c>) and UCD
/// <c>auxiliary/GraphemeBreakProperty.txt</c> 16.0. Every Unicode codepoint maps to
/// exactly one of these — the rule engine consults the property of each pair of adjacent
/// codepoints to decide whether a break is permitted between them (boundary marker that
/// produces a user-perceived character).
/// </summary>
/// <remarks>
/// Names match the abbreviations the UCD file uses, which keeps validation against the
/// UCD <c>auxiliary/GraphemeBreakTest.txt</c> conformance suite straightforward.
/// </remarks>
internal enum GraphemeClusterBreakProperty : byte
{
    /// <summary>Default — codepoints not explicitly listed in <c>GraphemeBreakProperty.txt</c>.</summary>
    Other = 0,

    /// <summary>Carriage_Return — U+000D.</summary>
    CR,

    /// <summary>Line_Feed — U+000A.</summary>
    LF,

    /// <summary>Control — most C0/C1 controls and similar.</summary>
    Control,

    /// <summary>Extend — combining marks and certain extensions.</summary>
    Extend,

    /// <summary>Zero_Width_Joiner — U+200D; participates in GB11 emoji-ZWJ sequences.</summary>
    ZWJ,

    /// <summary>Regional_Indicator — flag emoji components; pair via GB12/GB13.</summary>
    Regional_Indicator,

    /// <summary>Prepend — codepoints that prepend to the following grapheme.</summary>
    Prepend,

    /// <summary>SpacingMark — combining marks that take a column.</summary>
    SpacingMark,

    /// <summary>Hangul_L_Jamo.</summary>
    L,

    /// <summary>Hangul_V_Jamo.</summary>
    V,

    /// <summary>Hangul_T_Jamo.</summary>
    T,

    /// <summary>Hangul_LV_Syllable.</summary>
    LV,

    /// <summary>Hangul_LVT_Syllable.</summary>
    LVT,
}
