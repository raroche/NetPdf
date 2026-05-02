// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Segmentation;

/// <summary>
/// Public entry point for the UAX #29 §3.1 Grapheme Cluster Boundaries algorithm. Given
/// UTF-16 text, returns the boundary positions where one user-perceived character ends
/// and the next begins — what cursor movement, double-click selection, and shaping
/// boundary detection use as their unit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Spec basis (clean-room).</b> UAX #29 16.0 §3.1.1. Property data from
/// <c>auxiliary/GraphemeBreakProperty.txt</c> 16.0 (basic class) +
/// <c>emoji-data.txt</c> (Extended_Pictographic for GB11) +
/// <c>DerivedCoreProperties.txt</c> (InCB for GB9c). No code transliterated from any
/// third-party implementation; per-rule branches reference the exact rule number.
/// </para>
/// <para>
/// <b>Output.</b> <see cref="FindBoundaries"/> returns the BOUNDARY POSITIONS as a
/// sorted <see cref="int"/>[]. The first and last positions are always 0 and
/// <c>utf16Text.Length</c> (per GB1 / GB2). Each pair of consecutive positions delimits
/// one grapheme cluster.
/// </para>
/// </remarks>
internal static class GraphemeClusterBreaker
{
    /// <summary>
    /// Find grapheme cluster boundaries in <paramref name="utf16Text"/>. Returns a sorted
    /// list of positions where boundaries occur — including 0 (start of text per GB1)
    /// and the text length (end of text per GB2).
    /// </summary>
    public static int[] FindBoundaries(ReadOnlySpan<char> utf16Text)
    {
        if (utf16Text.IsEmpty)
        {
            return [0];
        }

        // Decode to per-codepoint info upfront so the rule walk doesn't re-decode.
        var infos = DecodeToCodepoints(utf16Text);

        var boundaries = new List<int>(infos.Length + 1);
        boundaries.Add(0); // GB1: ÷ at start.
        for (var i = 0; i < infos.Length - 1; i++)
        {
            if (IsBreakBetween(infos, i))
            {
                // The boundary is between codepoint i and codepoint i+1, which in UTF-16
                // is at the start of codepoint i+1.
                boundaries.Add(infos[i + 1].Utf16Index);
            }
        }
        boundaries.Add(utf16Text.Length); // GB2: ÷ at end.
        return boundaries.ToArray();
    }

    private struct CodepointInfo
    {
        public int Utf16Index;
        public byte Utf16Length;
        public int Codepoint;
        public GraphemeClusterBreakProperty Property;
    }

    private static CodepointInfo[] DecodeToCodepoints(ReadOnlySpan<char> text)
    {
        var infos = new List<CodepointInfo>(text.Length);
        for (var i = 0; i < text.Length; /* advance inside */)
        {
            int codepoint;
            byte unitLen;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codepoint = char.ConvertToUtf32(text[i], text[i + 1]);
                unitLen = 2;
            }
            else
            {
                codepoint = text[i];
                unitLen = 1;
            }
            infos.Add(new CodepointInfo
            {
                Utf16Index = i,
                Utf16Length = unitLen,
                Codepoint = codepoint,
                Property = GraphemeClusterBreakUcdRanges.Lookup(codepoint),
            });
            i += unitLen;
        }
        return infos.ToArray();
    }

    /// <summary>
    /// True when there is a grapheme-cluster boundary between codepoint <paramref name="i"/>
    /// and codepoint <paramref name="i"/>+1. Implements GB3-GB13 + GB999 default per
    /// UAX #29 §3.1.1.
    /// </summary>
    private static bool IsBreakBetween(CodepointInfo[] infos, int i)
    {
        var left = infos[i];
        var right = infos[i + 1];

        // GB3: CR × LF
        if (left.Property == GraphemeClusterBreakProperty.CR && right.Property == GraphemeClusterBreakProperty.LF)
            return false;
        // GB4: (Control | CR | LF) ÷
        if (left.Property is GraphemeClusterBreakProperty.Control or GraphemeClusterBreakProperty.CR or GraphemeClusterBreakProperty.LF)
            return true;
        // GB5: ÷ (Control | CR | LF)
        if (right.Property is GraphemeClusterBreakProperty.Control or GraphemeClusterBreakProperty.CR or GraphemeClusterBreakProperty.LF)
            return true;
        // GB6: L × (L | V | LV | LVT)
        if (left.Property == GraphemeClusterBreakProperty.L
            && right.Property is GraphemeClusterBreakProperty.L or GraphemeClusterBreakProperty.V
                or GraphemeClusterBreakProperty.LV or GraphemeClusterBreakProperty.LVT)
            return false;
        // GB7: (LV | V) × (V | T)
        if (left.Property is GraphemeClusterBreakProperty.LV or GraphemeClusterBreakProperty.V
            && right.Property is GraphemeClusterBreakProperty.V or GraphemeClusterBreakProperty.T)
            return false;
        // GB8: (LVT | T) × T
        if (left.Property is GraphemeClusterBreakProperty.LVT or GraphemeClusterBreakProperty.T
            && right.Property == GraphemeClusterBreakProperty.T)
            return false;
        // GB9: × (Extend | ZWJ)
        if (right.Property is GraphemeClusterBreakProperty.Extend or GraphemeClusterBreakProperty.ZWJ)
            return false;
        // GB9a: × SpacingMark
        if (right.Property == GraphemeClusterBreakProperty.SpacingMark)
            return false;
        // GB9b: Prepend ×
        if (left.Property == GraphemeClusterBreakProperty.Prepend)
            return false;

        // GB9c: \p{InCB=Consonant} [\p{InCB=Extend} \p{InCB=Linker}]* \p{InCB=Linker}
        //       [\p{InCB=Extend} \p{InCB=Linker}]* × \p{InCB=Consonant}
        // Walk back from i for the pattern.
        if (SegmentationAuxiliaryData.IsIncbConsonant(right.Codepoint) && IsLB9cMatch(infos, i))
            return false;

        // GB11: \p{Extended_Pictographic} Extend* ZWJ × \p{Extended_Pictographic}
        // The pair we're testing is (left=ZWJ, right=Extended_Pictographic). Walk back
        // from i-1 through Extend to find an Extended_Pictographic.
        if (left.Property == GraphemeClusterBreakProperty.ZWJ
            && SegmentationAuxiliaryData.IsExtendedPictographic(right.Codepoint)
            && IsGB11Match(infos, i))
            return false;

        // GB12: ^ (RI RI)* RI × RI
        // GB13: [^RI] (RI RI)* RI × RI
        // Both reduce to: when left and right are RI, count consecutive preceding RIs;
        // break iff the count (including left) is even.
        if (left.Property == GraphemeClusterBreakProperty.Regional_Indicator
            && right.Property == GraphemeClusterBreakProperty.Regional_Indicator
            && IsOddRiRunEndingAt(infos, i))
            return false;

        // GB999: ÷ otherwise.
        return true;
    }

    /// <summary>
    /// GB9c lookback: confirm the sequence ending at <paramref name="i"/> matches
    /// <c>InCB=Consonant [InCB=Extend InCB=Linker]* InCB=Linker [InCB=Extend InCB=Linker]*</c>.
    /// </summary>
    private static bool IsLB9cMatch(CodepointInfo[] infos, int i)
    {
        // Walk backward from i. The character at i must be in the trailing
        // [InCB=Extend InCB=Linker]* run. Track whether we've seen a Linker.
        var seenLinker = false;
        var j = i;
        while (j >= 0)
        {
            var cp = infos[j].Codepoint;
            if (SegmentationAuxiliaryData.IsIncbLinker(cp))
            {
                seenLinker = true;
                j--;
                continue;
            }
            if (SegmentationAuxiliaryData.IsIncbExtend(cp))
            {
                j--;
                continue;
            }
            if (seenLinker && SegmentationAuxiliaryData.IsIncbConsonant(cp))
            {
                return true;
            }
            return false;
        }
        return false;
    }

    /// <summary>
    /// GB11 lookback: confirm the sequence preceding the ZWJ at position <paramref name="i"/>
    /// matches <c>Extended_Pictographic Extend*</c>.
    /// </summary>
    private static bool IsGB11Match(CodepointInfo[] infos, int i)
    {
        // i is the ZWJ position. Walk back through Extend to find an Extended_Pictographic.
        for (var j = i - 1; j >= 0; j--)
        {
            if (infos[j].Property == GraphemeClusterBreakProperty.Extend) continue;
            return SegmentationAuxiliaryData.IsExtendedPictographic(infos[j].Codepoint);
        }
        return false;
    }

    /// <summary>
    /// GB12/GB13 RI counting: count consecutive Regional_Indicator codepoints ending at
    /// <paramref name="i"/> (inclusive). Return true when the count is ODD — indicating
    /// the current RI is the FIRST of a flag pair and the right RI is the SECOND, so the
    /// pair must not break inside.
    /// </summary>
    private static bool IsOddRiRunEndingAt(CodepointInfo[] infos, int i)
    {
        var count = 0;
        for (var j = i; j >= 0 && infos[j].Property == GraphemeClusterBreakProperty.Regional_Indicator; j--)
        {
            count++;
        }
        return (count & 1) == 1;
    }
}
