// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.OpenType;

namespace NetPdf.Pdf.Fonts;

/// <summary>
/// The compaction plan for a TTF subset: ordered list of <b>original</b> glyph ids that
/// will appear in the subset (with .notdef always at new index 0), plus the old → new
/// mapping consumed by every downstream stage (glyf/loca/hmtx emitters and Task 9's
/// ToUnicode CMap generator).
/// </summary>
/// <remarks>
/// <para>
/// <b>Composite glyph chase.</b> A composite glyph (numberOfContours &lt; 0) references
/// other glyphs through component records. If we subset away a referenced component, the
/// composite renders as <c>.notdef</c>. <see cref="Build"/> walks the component graph
/// transitively so every dependency lands in the subset before the plan is finalized.
/// Composite-of-composite is supported via worklist iteration.
/// </para>
/// <para>
/// <b>Determinism.</b> The output ordering is "glyph 0, then the input set sorted by
/// original id, then any composite components reachable from those glyphs (also sorted)".
/// Identical seeds produce identical plans.
/// </para>
/// </remarks>
internal sealed class GlyphSubsetPlan
{
    /// <summary>Original glyph ids in subset order. Index 0 is always 0 (.notdef).</summary>
    public required IReadOnlyList<int> OrderedOldGlyphIds { get; init; }

    /// <summary>Original glyph id → new (subset) glyph id. Inverse of <see cref="OrderedOldGlyphIds"/>.</summary>
    public required IReadOnlyDictionary<int, int> OldToNew { get; init; }

    /// <summary>Number of glyphs in the subset (== <c>OrderedOldGlyphIds.Count</c>).</summary>
    public int NumGlyphs => OrderedOldGlyphIds.Count;

    public static GlyphSubsetPlan Build(OpenTypeFont font, IReadOnlyCollection<int> seedUsedGlyphIds)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(seedUsedGlyphIds);
        if (!font.HasTrueTypeOutlines)
        {
            throw new InvalidOperationException(
                "GlyphSubsetPlan.Build only supports TTF fonts in Phase 1 Task 8. " +
                "CFF subsetting is the deferred 'CFF later' half of the task.");
        }

        var maxGlyphs = font.Maxp.NumGlyphs;
        var visited = new HashSet<int> { 0 };
        var worklist = new Queue<int>();

        foreach (var gid in seedUsedGlyphIds)
        {
            if (gid < 0 || gid >= maxGlyphs)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(seedUsedGlyphIds), gid,
                    $"Glyph id {gid} is outside the font's glyph universe [0, {maxGlyphs}).");
            }
            if (visited.Add(gid))
            {
                worklist.Enqueue(gid);
            }
        }

        // Composite chase. Each iteration may add more glyphs; we keep going until the
        // worklist drains. The visit set bounds total work — we never enqueue the same
        // glyph twice — so the loop terminates in at most numGlyphs iterations even with
        // pathological composite graphs.
        while (worklist.Count > 0)
        {
            var gid = worklist.Dequeue();
            var glyphBytes = font.Glyf!.GetGlyphBytes(gid);
            if (glyphBytes.Length < 10)
            {
                continue; // empty glyph or simple-but-tiny — no composite component records
            }
            var numberOfContours = BinaryPrimitives.ReadInt16BigEndian(glyphBytes[..2]);
            if (numberOfContours >= 0)
            {
                continue; // simple glyph
            }
            var components = CollectComponentGlyphIds(glyphBytes);
            foreach (var component in components)
            {
                if (component < 0 || component >= maxGlyphs)
                {
                    throw new InvalidDataException(
                        $"Composite glyph {gid} references out-of-range component glyph id {component} " +
                        $"(numGlyphs = {maxGlyphs}).");
                }
                if (visited.Add(component))
                {
                    worklist.Enqueue(component);
                }
            }
        }

        // Build the deterministic ordering: glyph 0 first, then everything else by
        // ascending original id.
        var ordered = new List<int>(visited.Count) { 0 };
        var rest = visited.Where(g => g != 0).ToList();
        rest.Sort();
        ordered.AddRange(rest);

        var oldToNew = new Dictionary<int, int>(ordered.Count);
        for (var newId = 0; newId < ordered.Count; newId++)
        {
            oldToNew[ordered[newId]] = newId;
        }

        return new GlyphSubsetPlan
        {
            OrderedOldGlyphIds = ordered,
            OldToNew = oldToNew,
        };
    }

    /// <summary>Collect the component glyph ids referenced by a composite glyph.</summary>
    /// <remarks>
    /// Composite glyph component record per OpenType §"glyf":
    /// <list type="bullet">
    /// <item>uint16 flags</item>
    /// <item>uint16 glyphIndex</item>
    /// <item>arg1 / arg2 — int16 each if <c>ARG_1_AND_2_ARE_WORDS</c> (0x0001), else int8 each</item>
    /// <item>optional transform: 1× F2DOT14 (0x0008) or 2× F2DOT14 (0x0040) or 4× F2DOT14 (0x0080)</item>
    /// </list>
    /// More components follow when <c>MORE_COMPONENTS</c> (0x0020) is set.
    /// </remarks>
    private static List<int> CollectComponentGlyphIds(ReadOnlySpan<byte> span)
    {
        // Buffer into a List so the caller can iterate without holding the ref-struct span.
        // Real fonts cap composite components in the low single digits; the spec maximum is
        // 0xFFFF but never seen in practice.
        var components = new List<int>(4);
        var pos = 10; // skip header (numberOfContours + bbox = 2 + 4*2 = 10)

        while (true)
        {
            if (pos + 4 > span.Length)
            {
                throw new InvalidDataException(
                    $"Composite glyph: component record header truncated at offset {pos} (glyph bytes length {span.Length}).");
            }
            var flags = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(pos, 2));
            var componentGid = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(pos + 2, 2));
            components.Add(componentGid);
            pos += 4;

            // Argument-1/2 width
            pos += (flags & 0x0001) != 0 ? 4 : 2;

            // Optional transform
            if ((flags & 0x0008) != 0)
            {
                pos += 2;
            }
            else if ((flags & 0x0040) != 0)
            {
                pos += 4;
            }
            else if ((flags & 0x0080) != 0)
            {
                pos += 8;
            }

            if (pos > span.Length)
            {
                throw new InvalidDataException(
                    $"Composite glyph: component record body extends past glyph bytes (pos {pos}, length {span.Length}).");
            }

            if ((flags & 0x0020) == 0)
            {
                break; // last component
            }
        }

        return components;
    }
}
