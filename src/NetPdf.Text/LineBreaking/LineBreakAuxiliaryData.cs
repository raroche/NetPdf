// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Frozen;

namespace NetPdf.Text.LineBreaking;

/// <summary>
/// Auxiliary Unicode data tables that UAX #14 rules consult beyond a codepoint's
/// line-break class. All tables are sorted, immutable, and built once at class-init
/// time. Sourced from UCD 16.0:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>East-Asian-Wide ranges (W / F / H) — for LB19a/b East-Asian quotation
///         relaxation and LB30 East-Asian OP/CP filtering. From
///         <c>EastAsianWidth.txt</c>.</item>
///   <item>Pi / Pf QU sets — for LB15a/b initial / final quotation rules.
///         From <c>UnicodeData.txt</c> general categories Pi and Pf, intersected
///         with the QU set from <c>LineBreak.txt</c>.</item>
///   <item>Extended_Pictographic &amp; Cn ranges — for LB30b emoji-modifier rule
///         when the base is an unassigned codepoint reserved for a future
///         Extended_Pictographic. From <c>emoji-data.txt</c> intersected with
///         <c>UnicodeData.txt</c> Cn.</item>
/// </list>
/// Hand-curated for the small Pi/Pf sets; ranges generated from UCD via a Python
/// pipeline (one-off offline regeneration). Future hardening: drop a Roslyn source
/// generator that consumes the raw UCD files at build time.
/// </remarks>
internal static class LineBreakAuxiliaryData
{
    /// <summary>QU codepoints with general category Pi (Punctuation_Initial).</summary>
    private static readonly FrozenSet<int> _initialQuotation = new[]
    {
        0x00AB, 0x2018, 0x201B, 0x201C, 0x201F, 0x2039,
        0x2E02, 0x2E04, 0x2E09, 0x2E0C, 0x2E1C, 0x2E20,
    }.ToFrozenSet();

    /// <summary>QU codepoints with general category Pf (Punctuation_Final).</summary>
    private static readonly FrozenSet<int> _finalQuotation = new[]
    {
        0x00BB, 0x2019, 0x201D, 0x203A,
        0x2E03, 0x2E05, 0x2E0A, 0x2E0D, 0x2E1D, 0x2E21,
    }.ToFrozenSet();

    /// <summary>128 East-Asian Wide / Fullwidth / Halfwidth ranges from UCD <c>EastAsianWidth.txt</c> 16.0.</summary>
    private static readonly (int Start, int End)[] _eastAsianRanges = new (int, int)[]
    {
        (0x1100, 0x115F),
        (0x20A9, 0x20A9),
        (0x231A, 0x231B),
        (0x2329, 0x232A),
        (0x23E9, 0x23EC),
        (0x23F0, 0x23F0),
        (0x23F3, 0x23F3),
        (0x25FD, 0x25FE),
        (0x2614, 0x2615),
        (0x2630, 0x2637),
        (0x2648, 0x2653),
        (0x267F, 0x267F),
        (0x268A, 0x268F),
        (0x2693, 0x2693),
        (0x26A1, 0x26A1),
        (0x26AA, 0x26AB),
        (0x26BD, 0x26BE),
        (0x26C4, 0x26C5),
        (0x26CE, 0x26CE),
        (0x26D4, 0x26D4),
        (0x26EA, 0x26EA),
        (0x26F2, 0x26F3),
        (0x26F5, 0x26F5),
        (0x26FA, 0x26FA),
        (0x26FD, 0x26FD),
        (0x2705, 0x2705),
        (0x270A, 0x270B),
        (0x2728, 0x2728),
        (0x274C, 0x274C),
        (0x274E, 0x274E),
        (0x2753, 0x2755),
        (0x2757, 0x2757),
        (0x2795, 0x2797),
        (0x27B0, 0x27B0),
        (0x27BF, 0x27BF),
        (0x2B1B, 0x2B1C),
        (0x2B50, 0x2B50),
        (0x2B55, 0x2B55),
        (0x2E80, 0x2E99),
        (0x2E9B, 0x2EF3),
        (0x2F00, 0x2FD5),
        (0x2FF0, 0x303E),
        (0x3041, 0x3096),
        (0x3099, 0x30FF),
        (0x3105, 0x312F),
        (0x3131, 0x318E),
        (0x3190, 0x31E5),
        (0x31EF, 0x321E),
        (0x3220, 0x3247),
        (0x3250, 0xA48C),
        (0xA490, 0xA4C6),
        (0xA960, 0xA97C),
        (0xAC00, 0xD7A3),
        (0xF900, 0xFAFF),
        (0xFE10, 0xFE19),
        (0xFE30, 0xFE52),
        (0xFE54, 0xFE66),
        (0xFE68, 0xFE6B),
        (0xFF01, 0xFFBE),
        (0xFFC2, 0xFFC7),
        (0xFFCA, 0xFFCF),
        (0xFFD2, 0xFFD7),
        (0xFFDA, 0xFFDC),
        (0xFFE0, 0xFFE6),
        (0xFFE8, 0xFFEE),
        (0x16FE0, 0x16FE4),
        (0x16FF0, 0x16FF1),
        (0x17000, 0x187F7),
        (0x18800, 0x18CD5),
        (0x18CFF, 0x18D08),
        (0x1AFF0, 0x1AFF3),
        (0x1AFF5, 0x1AFFB),
        (0x1AFFD, 0x1AFFE),
        (0x1B000, 0x1B122),
        (0x1B132, 0x1B132),
        (0x1B150, 0x1B152),
        (0x1B155, 0x1B155),
        (0x1B164, 0x1B167),
        (0x1B170, 0x1B2FB),
        (0x1D300, 0x1D356),
        (0x1D360, 0x1D376),
        (0x1F004, 0x1F004),
        (0x1F0CF, 0x1F0CF),
        (0x1F18E, 0x1F18E),
        (0x1F191, 0x1F19A),
        (0x1F200, 0x1F202),
        (0x1F210, 0x1F23B),
        (0x1F240, 0x1F248),
        (0x1F250, 0x1F251),
        (0x1F260, 0x1F265),
        (0x1F300, 0x1F320),
        (0x1F32D, 0x1F335),
        (0x1F337, 0x1F37C),
        (0x1F37E, 0x1F393),
        (0x1F3A0, 0x1F3CA),
        (0x1F3CF, 0x1F3D3),
        (0x1F3E0, 0x1F3F0),
        (0x1F3F4, 0x1F3F4),
        (0x1F3F8, 0x1F43E),
        (0x1F440, 0x1F440),
        (0x1F442, 0x1F4FC),
        (0x1F4FF, 0x1F53D),
        (0x1F54B, 0x1F54E),
        (0x1F550, 0x1F567),
        (0x1F57A, 0x1F57A),
        (0x1F595, 0x1F596),
        (0x1F5A4, 0x1F5A4),
        (0x1F5FB, 0x1F64F),
        (0x1F680, 0x1F6C5),
        (0x1F6CC, 0x1F6CC),
        (0x1F6D0, 0x1F6D2),
        (0x1F6D5, 0x1F6D7),
        (0x1F6DC, 0x1F6DF),
        (0x1F6EB, 0x1F6EC),
        (0x1F6F4, 0x1F6FC),
        (0x1F7E0, 0x1F7EB),
        (0x1F7F0, 0x1F7F0),
        (0x1F90C, 0x1F93A),
        (0x1F93C, 0x1F945),
        (0x1F947, 0x1F9FF),
        (0x1FA70, 0x1FA7C),
        (0x1FA80, 0x1FA89),
        (0x1FA8F, 0x1FAC6),
        (0x1FACE, 0x1FADC),
        (0x1FADF, 0x1FAE9),
        (0x1FAF0, 0x1FAF8),
        (0x20000, 0x2FFFD),
        (0x30000, 0x3FFFD),
    };

    /// <summary>35 Extended_Pictographic-AND-Cn ranges. Used by LB30b — codepoints in this set are treated as if they had Line_Break = EB (Emoji_Base) for the "EB x EM" rule.</summary>
    private static readonly (int Start, int End)[] _extendedPictographicCnRanges = new (int, int)[]
    {
        (0x1F02C, 0x1F02F),
        (0x1F094, 0x1F09F),
        (0x1F0AF, 0x1F0B0),
        (0x1F0C0, 0x1F0C0),
        (0x1F0D0, 0x1F0D0),
        (0x1F0F6, 0x1F0FF),
        (0x1F1AE, 0x1F1E5),
        (0x1F203, 0x1F20F),
        (0x1F23C, 0x1F23F),
        (0x1F249, 0x1F24F),
        (0x1F252, 0x1F25F),
        (0x1F266, 0x1F2FF),
        (0x1F6D8, 0x1F6DB),
        (0x1F6ED, 0x1F6EF),
        (0x1F6FD, 0x1F6FF),
        (0x1F777, 0x1F77A),
        (0x1F7DA, 0x1F7DF),
        (0x1F7EC, 0x1F7EF),
        (0x1F7F1, 0x1F7FF),
        (0x1F80C, 0x1F80F),
        (0x1F848, 0x1F84F),
        (0x1F85A, 0x1F85F),
        (0x1F888, 0x1F88F),
        (0x1F8AE, 0x1F8AF),
        (0x1F8BC, 0x1F8BF),
        (0x1F8C2, 0x1F8FF),
        (0x1FA54, 0x1FA5F),
        (0x1FA6E, 0x1FA6F),
        (0x1FA7D, 0x1FA7F),
        (0x1FA8A, 0x1FA8E),
        (0x1FAC7, 0x1FACD),
        (0x1FADD, 0x1FADE),
        (0x1FAEA, 0x1FAEF),
        (0x1FAF9, 0x1FAFF),
        (0x1FC00, 0x1FFFD),
    };

    /// <summary>True when <paramref name="codepoint"/> has general category Pi (Punctuation_Initial).</summary>
    public static bool IsInitialQuotation(int codepoint) => _initialQuotation.Contains(codepoint);

    /// <summary>True when <paramref name="codepoint"/> has general category Pf (Punctuation_Final).</summary>
    public static bool IsFinalQuotation(int codepoint) => _finalQuotation.Contains(codepoint);

    /// <summary>True when <paramref name="codepoint"/> has East_Asian_Width property of W, F, or H.</summary>
    public static bool IsEastAsianWide(int codepoint) => BinarySearchRange(_eastAsianRanges, codepoint);

    /// <summary>
    /// Compatibility wrapper kept for the existing LB30 OP/CP East-Asian filter call sites.
    /// Equivalent to <see cref="IsEastAsianWide"/> when the codepoint is OP/CP — non-OP/CP
    /// codepoints are not relevant to LB30 so the broader check is harmless.
    /// </summary>
    public static bool IsEastAsianWideOpenOrClose(int codepoint) => IsEastAsianWide(codepoint);

    /// <summary>True when <paramref name="codepoint"/> is in Extended_Pictographic and unassigned (Cn) — used by LB30b.</summary>
    public static bool IsExtendedPictographicCn(int codepoint) => BinarySearchRange(_extendedPictographicCnRanges, codepoint);

    /// <summary>Standard binary-search-over-(start, end)-ranges helper.</summary>
    private static bool BinarySearchRange((int Start, int End)[] ranges, int codepoint)
    {
        var lo = 0;
        var hi = ranges.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            ref readonly var r = ref ranges[mid];
            if (codepoint < r.Start) hi = mid - 1;
            else if (codepoint > r.End) lo = mid + 1;
            else return true;
        }
        return false;
    }
}
