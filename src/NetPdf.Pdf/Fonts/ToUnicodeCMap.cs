// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text;
using NetPdf.Text.Fonts.OpenType;

namespace NetPdf.Pdf.Fonts;

/// <summary>
/// Maps subset glyph ids back to their source Unicode codepoint(s). Embedded as a PDF
/// stream (the <c>/ToUnicode</c> entry of a CIDFontType2 / CIDFontType0 font dictionary),
/// it is what lets PDF readers recover "Hello, world." from a content stream that emitted
/// glyph ids through Identity-H encoding. Without it, the rendered text is uncopyable
/// and unsearchable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Output format.</b> A PostScript-style CMap per Adobe "PDF Reference" §5.9 +
/// ISO 32000-2:2020 §14.3.4. The header / codespacerange / footer sections are constant;
/// the body is one or more <c>beginbfchar</c> blocks (PDF caps each block at 100 entries).
/// Source codes are 4-hex-digit subset glyph ids (Identity-H is a 16-bit encoding); target
/// codepoints are UTF-16BE-encoded as hex — BMP characters use 4 hex chars, supplementary
/// codepoints emit a surrogate pair (8 hex chars).
/// </para>
/// <para>
/// <b>Ligature support.</b> The <see cref="SubsetGlyphIdToText"/> values are arbitrary
/// strings — a ligature glyph can map to multiple codepoints (the canonical example is
/// the "fi" ligature mapping to U+0066 U+0069). Phase 1 doesn't yet have GSUB-aware
/// shaping that would identify ligatures, so the <see cref="FromSubset"/> factory only
/// produces single-codepoint mappings derived from the <c>cmap</c>; the data shape is
/// ready for ligatures when shaping lands.
/// </para>
/// <para>
/// <b>Determinism.</b> Entries are emitted in ascending subset-glyph-id order. When two
/// Unicode codepoints map to the same original glyph (e.g. U+00B5 micro and U+03BC mu in
/// some fonts), <see cref="FromSubset"/> picks the lowest codepoint — the cmap groups are
/// already sorted, so the first-encountered entry is also the lowest.
/// </para>
/// </remarks>
internal sealed class ToUnicodeCMap
{
    /// <summary>PDF spec cap — each <c>beginbfchar</c> block is limited to 100 entries.</summary>
    public const int MaxEntriesPerBlock = 100;

    /// <summary>Subset glyph id → source Unicode text. Empty subsets (only .notdef) yield empty mapping.</summary>
    public required IReadOnlyDictionary<int, string> SubsetGlyphIdToText { get; init; }

    /// <summary>
    /// Build a ToUnicode mapping from a parsed source font and the subset plan that
    /// will embed it. Walks <see cref="OpenTypeFont.Cmap"/> and records the first (= lowest)
    /// codepoint that maps to each subset glyph.
    /// </summary>
    public static ToUnicodeCMap FromSubset(OpenTypeFont font, GlyphSubsetPlan plan)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate(font);

        var mapping = new Dictionary<int, string>(plan.NumGlyphs);
        foreach (var group in font.Cmap.Groups)
        {
            for (uint cp = group.StartCodePoint; cp <= group.EndCodePoint; cp++)
            {
                var originalGid = group.StartGlyphId + (cp - group.StartCodePoint);
                if (!plan.OldToNew.TryGetValue((int)originalGid, out var newGid))
                {
                    continue;
                }
                if (mapping.ContainsKey(newGid))
                {
                    continue; // first (= lowest) codepoint wins; cmap groups are sorted
                }
                if (cp > 0x10FFFF)
                {
                    continue; // beyond Unicode max — malformed, skip
                }
                mapping[newGid] = char.ConvertFromUtf32((int)cp);
            }
        }

        return new ToUnicodeCMap { SubsetGlyphIdToText = mapping };
    }

    /// <summary>
    /// Serialize the CMap to ASCII bytes ready for embedding as a PDF stream. Output is
    /// byte-deterministic for a given <see cref="SubsetGlyphIdToText"/> mapping.
    /// </summary>
    public byte[] Emit()
    {
        var sortedKeys = new int[SubsetGlyphIdToText.Count];
        var i = 0;
        foreach (var key in SubsetGlyphIdToText.Keys)
        {
            sortedKeys[i++] = key;
        }
        Array.Sort(sortedKeys);

        var sb = new StringBuilder(256 + (sortedKeys.Length * 24));
        AppendHeader(sb);
        AppendCodespaceRange(sb);
        AppendBfCharBlocks(sb, sortedKeys);
        AppendFooter(sb);

        // PDF CMaps are 7-bit ASCII; non-ASCII payload is hex-encoded inside <...> wrappers.
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static void AppendHeader(StringBuilder sb)
    {
        sb.Append("/CIDInit /ProcSet findresource begin\n");
        sb.Append("12 dict begin\n");
        sb.Append("begincmap\n");
        sb.Append("/CIDSystemInfo\n");
        sb.Append("<< /Registry (Adobe)\n");
        sb.Append("/Ordering (UCS)\n");
        sb.Append("/Supplement 0\n");
        sb.Append(">> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n");
        sb.Append("/CMapType 2 def\n");
    }

    private static void AppendCodespaceRange(StringBuilder sb)
    {
        sb.Append("1 begincodespacerange\n");
        sb.Append("<0000> <FFFF>\n");
        sb.Append("endcodespacerange\n");
    }

    private void AppendBfCharBlocks(StringBuilder sb, int[] sortedKeys)
    {
        if (sortedKeys.Length == 0)
        {
            return;
        }
        for (var blockStart = 0; blockStart < sortedKeys.Length; blockStart += MaxEntriesPerBlock)
        {
            var blockEnd = Math.Min(blockStart + MaxEntriesPerBlock, sortedKeys.Length);
            var blockSize = blockEnd - blockStart;
            sb.Append(blockSize.ToString(CultureInfo.InvariantCulture));
            sb.Append(" beginbfchar\n");
            for (var k = blockStart; k < blockEnd; k++)
            {
                AppendEntry(sb, sortedKeys[k], SubsetGlyphIdToText[sortedKeys[k]]);
            }
            sb.Append("endbfchar\n");
        }
    }

    private static void AppendEntry(StringBuilder sb, int subsetGid, string targetText)
    {
        sb.Append('<');
        sb.Append(((ushort)subsetGid).ToString("X4", CultureInfo.InvariantCulture));
        sb.Append("> <");

        // UTF-16BE hex of the target string. char.ConvertFromUtf32 already returns the
        // surrogate pair for supplementary codepoints, so we just walk the chars.
        for (var c = 0; c < targetText.Length; c++)
        {
            sb.Append(((ushort)targetText[c]).ToString("X4", CultureInfo.InvariantCulture));
        }
        sb.Append(">\n");
    }

    private static void AppendFooter(StringBuilder sb)
    {
        sb.Append("endcmap\n");
        sb.Append("CMapName currentdict /CMap defineresource pop\n");
        sb.Append("end\n");
        sb.Append("end\n");
    }
}
