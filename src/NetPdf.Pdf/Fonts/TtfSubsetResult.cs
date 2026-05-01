// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf.Fonts;

/// <summary>
/// Output of <see cref="TtfSubsetter.Subset"/>. Each property is a freshly-emitted byte
/// stream for the corresponding subset table, ready to drop into an SFNT envelope by the
/// embedder (Phase 1 Task 10).
/// </summary>
/// <remarks>
/// Tables not listed here (<c>OS/2</c>, <c>post</c>, <c>cmap</c>, <c>name</c>) are
/// typically passed through from the source font unchanged for Phase 1 — the embedder
/// chooses which to carry forward into the PDF FontFile2 stream.
/// </remarks>
internal sealed class TtfSubsetResult
{
    /// <summary>The plan that produced this subset — exposed so consumers (Task 9 ToUnicode, Task 10 wrapper) can re-walk the old→new mapping without recomputing.</summary>
    public required GlyphSubsetPlan Plan { get; init; }

    /// <summary>The PDF BaseFont string (<c>"PREFIX+OriginalName"</c>).</summary>
    public required string SubsetBaseFontName { get; init; }

    /// <summary>Updated <c>head</c> bytes — <c>indexToLocFormat</c> chosen so the new <c>loca</c> fits.</summary>
    public required byte[] HeadBytes { get; init; }

    /// <summary>Updated <c>hhea</c> bytes — <c>numberOfHMetrics</c> equals the subset glyph count.</summary>
    public required byte[] HheaBytes { get; init; }

    /// <summary>Updated <c>maxp</c> bytes — <c>numGlyphs</c> equals the subset glyph count.</summary>
    public required byte[] MaxpBytes { get; init; }

    /// <summary>Subset <c>hmtx</c> bytes — one longHorMetric per subset glyph (no lsb-only trail).</summary>
    public required byte[] HmtxBytes { get; init; }

    /// <summary>New <c>loca</c> bytes — short or long format per the new <c>head.indexToLocFormat</c>.</summary>
    public required byte[] LocaBytes { get; init; }

    /// <summary>Subset <c>glyf</c> bytes — only the glyphs in <see cref="Plan"/>, with composite component glyph ids rewritten.</summary>
    public required byte[] GlyfBytes { get; init; }
}
