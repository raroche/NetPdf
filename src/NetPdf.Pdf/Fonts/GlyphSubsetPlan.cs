// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

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
/// Composite-of-composite is supported via worklist iteration. The component-record
/// parsing is delegated to <see cref="CompositeGlyph"/> so the planner and the emitter
/// share one validated wire-format walker.
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
            CompositeGlyph.EnsureValidHeader(glyphBytes, gid);
            if (!CompositeGlyph.IsComposite(glyphBytes))
            {
                continue;
            }
            var components = CompositeGlyph.EnumerateComponents(glyphBytes);
            foreach (var component in components)
            {
                if (component.GlyphIndex >= maxGlyphs)
                {
                    throw new InvalidDataException(
                        $"Composite glyph {gid} references out-of-range component glyph id {component.GlyphIndex} " +
                        $"(numGlyphs = {maxGlyphs}).");
                }
                if (visited.Add(component.GlyphIndex))
                {
                    worklist.Enqueue(component.GlyphIndex);
                }
            }
        }

        // Build the deterministic ordering: glyph 0 first, then everything else by
        // ascending original id.
        var ordered = new List<int>(visited.Count) { 0 };
        var rest = new List<int>(visited.Count - 1);
        foreach (var g in visited)
        {
            if (g != 0)
            {
                rest.Add(g);
            }
        }
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

    /// <summary>
    /// Strict trust-boundary preflight for hand-constructed plans. Caller-built or
    /// out-of-band plans go through this before any byte emission so structural
    /// inconsistencies surface here, not as malformed PDF font data downstream.
    /// </summary>
    /// <remarks>
    /// Checks: <c>NumGlyphs</c> in [1, 65535] (PDF font tables are uint16-bound),
    /// <c>OrderedOldGlyphIds[0] == 0</c>, no duplicates, every old id in <c>[0, font.Maxp.NumGlyphs)</c>,
    /// and <c>OldToNew</c> is the exact inverse of <c>OrderedOldGlyphIds</c>.
    /// </remarks>
    public void Validate(OpenTypeFont font)
    {
        ArgumentNullException.ThrowIfNull(font);

        if (NumGlyphs == 0)
        {
            throw new InvalidOperationException("GlyphSubsetPlan: must contain at least .notdef (glyph 0).");
        }
        if (NumGlyphs > 0xFFFF)
        {
            throw new InvalidOperationException(
                $"GlyphSubsetPlan: subset glyph count {NumGlyphs} exceeds the uint16 cap of PDF font tables (65535).");
        }
        if (OrderedOldGlyphIds[0] != 0)
        {
            throw new InvalidOperationException(
                $"GlyphSubsetPlan: glyph 0 (.notdef) must be at new id 0; got original id {OrderedOldGlyphIds[0]}.");
        }
        if (OldToNew.Count != NumGlyphs)
        {
            throw new InvalidOperationException(
                $"GlyphSubsetPlan: OldToNew size ({OldToNew.Count}) does not match OrderedOldGlyphIds size ({NumGlyphs}).");
        }
        var fontGlyphCount = font.Maxp.NumGlyphs;
        var seen = new HashSet<int>(NumGlyphs);
        for (var i = 0; i < NumGlyphs; i++)
        {
            var oldId = OrderedOldGlyphIds[i];
            if (oldId < 0 || oldId >= fontGlyphCount)
            {
                throw new InvalidOperationException(
                    $"GlyphSubsetPlan: original glyph id {oldId} at new id {i} is outside the font's glyph universe [0, {fontGlyphCount}).");
            }
            if (!seen.Add(oldId))
            {
                throw new InvalidOperationException(
                    $"GlyphSubsetPlan: duplicate original glyph id {oldId} appears at new id {i}.");
            }
            if (!OldToNew.TryGetValue(oldId, out var mapped) || mapped != i)
            {
                throw new InvalidOperationException(
                    $"GlyphSubsetPlan: OldToNew[{oldId}] should be {i} but was " +
                    (OldToNew.TryGetValue(oldId, out var actual) ? actual.ToString() : "missing") + ".");
            }
        }
    }
}
