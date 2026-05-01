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
/// <b>Two ways to build a mapping.</b>
/// <list type="bullet">
/// <item>
/// <see cref="FromSubset"/> derives subset glyph → codepoint mappings from the source
/// font's <c>cmap</c>. This is the <b>fallback</b> path — sufficient for static text
/// before shaping lands, but it can't recover the original Unicode for ligature glyphs
/// (one glyph → multiple codepoints) or for codepoint aliases that share a glyph.
/// </item>
/// <item>
/// A future <c>FromShapedRuns</c> factory will build mappings directly from shaped glyph
/// runs once Task 11 wires HarfBuzz in. The shaper knows the exact Unicode that produced
/// each glyph, so ligatures and GSUB substitutions round-trip correctly. Until then,
/// shaping consumers can build the mapping by hand and pass it directly — see the
/// <see cref="SubsetGlyphIdToText"/> property and <see cref="Emit"/>'s validation guard.
/// </item>
/// </list>
/// </para>
/// <para>
/// <b>Trust boundary.</b> <see cref="Emit"/> is the single point of byte production. It
/// validates every entry before serializing — subset glyph ids must fit in
/// <c>[0, 65535]</c> (Identity-H is 16-bit), target strings must be non-empty, and any
/// supplementary-plane characters must be expressed as well-formed UTF-16 surrogate
/// pairs. Direct construction via the <c>init</c> property is allowed, but Emit will
/// reject malformed inputs there too.
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
    /// <remarks>
    /// <b>Asymptotic shape.</b> The walker tracks unresolved subset glyphs and breaks out
    /// of the cmap walk as soon as every subset glyph has been mapped. For the typical
    /// case — a small subset of a large CJK or symbol font — work is bounded by the
    /// position of the last-needed cmap entry, not by the full Unicode coverage of the
    /// font.
    /// </remarks>
    public static ToUnicodeCMap FromSubset(OpenTypeFont font, GlyphSubsetPlan plan)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate(font);

        // Build the unresolved set (everything except .notdef — it has no cmap entry).
        var unresolved = new HashSet<int>(plan.NumGlyphs);
        foreach (var oldId in plan.OrderedOldGlyphIds)
        {
            if (oldId != 0)
            {
                unresolved.Add(oldId);
            }
        }

        var mapping = new Dictionary<int, string>(plan.NumGlyphs);
        if (unresolved.Count == 0)
        {
            return new ToUnicodeCMap { SubsetGlyphIdToText = mapping };
        }

        foreach (var group in font.Cmap.Groups)
        {
            if (unresolved.Count == 0)
            {
                break;
            }
            for (uint cp = group.StartCodePoint; cp <= group.EndCodePoint; cp++)
            {
                if (unresolved.Count == 0)
                {
                    break;
                }
                if (cp > 0x10FFFF)
                {
                    continue; // beyond Unicode max — malformed source, skip
                }

                var originalGid = (int)(group.StartGlyphId + (cp - group.StartCodePoint));
                if (!unresolved.Remove(originalGid))
                {
                    continue; // not in subset, or this glyph's lowest codepoint already recorded
                }

                var newGid = plan.OldToNew[originalGid];
                mapping[newGid] = char.ConvertFromUtf32((int)cp);
            }
        }

        return new ToUnicodeCMap { SubsetGlyphIdToText = mapping };
    }

    /// <summary>
    /// Serialize the CMap to ASCII bytes ready for embedding as a PDF stream. Output is
    /// byte-deterministic for a given <see cref="SubsetGlyphIdToText"/> mapping. Validates
    /// every entry before serializing — see the type-level remarks for the trust-boundary
    /// contract.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when any entry has a subset glyph id outside <c>[0, 65535]</c>, an empty
    /// target string, or malformed UTF-16 (unpaired surrogate).
    /// </exception>
    public byte[] Emit()
    {
        ValidateForEmit();

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

    /// <summary>
    /// Inspect every entry and throw if it would emit invalid CMap content. Runs once at
    /// the start of <see cref="Emit"/> so byte production never sees malformed state.
    /// </summary>
    private void ValidateForEmit()
    {
        foreach (var pair in SubsetGlyphIdToText)
        {
            if (pair.Key < 0 || pair.Key > 0xFFFF)
            {
                throw new InvalidOperationException(
                    $"ToUnicodeCMap: subset glyph id {pair.Key} is outside the 16-bit Identity-H range [0, 65535]. " +
                    "PDF Identity-H encoding cannot represent values that wide.");
            }
            if (string.IsNullOrEmpty(pair.Value))
            {
                throw new InvalidOperationException(
                    $"ToUnicodeCMap: subset glyph id {pair.Key} maps to an empty target string. " +
                    "PDF readers reject zero-byte CMap targets.");
            }
            ValidateUtf16(pair.Key, pair.Value);
        }
    }

    private static void ValidateUtf16(int subsetGid, string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    throw new InvalidOperationException(
                        $"ToUnicodeCMap: subset glyph id {subsetGid} target has an unpaired high surrogate at index {i}.");
                }
                i++; // skip the low surrogate we just validated
            }
            else if (char.IsLowSurrogate(c))
            {
                throw new InvalidOperationException(
                    $"ToUnicodeCMap: subset glyph id {subsetGid} target has an unpaired low surrogate at index {i}.");
            }
        }
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
