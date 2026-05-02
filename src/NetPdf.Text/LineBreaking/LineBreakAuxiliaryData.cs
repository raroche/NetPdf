// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Frozen;

namespace NetPdf.Text.LineBreaking;

/// <summary>
/// Small auxiliary data sets used by UAX #14 rules that need to look beyond a codepoint's
/// line-break class — specifically:
/// </summary>
/// <list type="bullet">
///   <item>East-Asian-Wide OP/CP codepoints (LB30 East-Asian filter).</item>
///   <item>Punctuation_Initial QU codepoints (LB15a).</item>
///   <item>Punctuation_Final QU codepoints (LB15b).</item>
/// </list>
/// <remarks>
/// Hand-curated from UCD <c>EastAsianWidth.txt</c> 16.0 (intersected with OP/CP from
/// <c>LineBreak.txt</c>) and from UCD <c>UnicodeData.txt</c> general-category Pi/Pf.
/// Sets are small (101 OP/CP codepoints total of which 29 are EA-Wide; ~22 Pi/Pf-QU
/// codepoints). <see cref="FrozenSet{T}"/> built once for O(1) lookup.
/// </remarks>
internal static class LineBreakAuxiliaryData
{
    /// <summary>
    /// OP/CP codepoints with East_Asian_Width property of W, F, or H. UAX #14 LB30 only
    /// applies to OP/CP that are NOT in this set ("ea ≠ F, W, H").
    /// </summary>
    private static readonly FrozenSet<int> _eaWideOpenClose = new[]
    {
        0x2329, 0x3008, 0x300A, 0x300C, 0x300E, 0x3010, 0x3014, 0x3016, 0x3018, 0x301A, 0x301D,
        0xFE17, 0xFE35, 0xFE37, 0xFE39, 0xFE3B, 0xFE3D, 0xFE3F, 0xFE41, 0xFE43, 0xFE47,
        0xFE59, 0xFE5B, 0xFE5D,
        0xFF08, 0xFF3B, 0xFF5B, 0xFF5F, 0xFF62,
    }.ToFrozenSet();

    /// <summary>
    /// QU codepoints with general category Pi (Punctuation_Initial) — used by LB15a to
    /// forbid breaks after these quotation marks under the X SP* × pattern.
    /// </summary>
    private static readonly FrozenSet<int> _initialQuotation = new[]
    {
        0x00AB, 0x2018, 0x201B, 0x201C, 0x201F, 0x2039,
        0x2E02, 0x2E04, 0x2E09, 0x2E0C, 0x2E1C, 0x2E20,
    }.ToFrozenSet();

    /// <summary>
    /// QU codepoints with general category Pf (Punctuation_Final) — used by LB15b to
    /// forbid breaks before these quotation marks under the SP × Y patterns.
    /// </summary>
    private static readonly FrozenSet<int> _finalQuotation = new[]
    {
        0x00BB, 0x2019, 0x201D, 0x203A,
        0x2E03, 0x2E05, 0x2E0A, 0x2E0D, 0x2E1D, 0x2E21,
    }.ToFrozenSet();

    /// <summary>True when <paramref name="codepoint"/> is an OP/CP that is also EA Wide/Fullwidth/Halfwidth.</summary>
    public static bool IsEastAsianWideOpenOrClose(int codepoint) => _eaWideOpenClose.Contains(codepoint);

    /// <summary>True when <paramref name="codepoint"/> has general category Pi (Punctuation_Initial).</summary>
    public static bool IsInitialQuotation(int codepoint) => _initialQuotation.Contains(codepoint);

    /// <summary>True when <paramref name="codepoint"/> has general category Pf (Punctuation_Final).</summary>
    public static bool IsFinalQuotation(int codepoint) => _finalQuotation.Contains(codepoint);
}
