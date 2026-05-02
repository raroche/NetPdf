// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Shaping;

/// <summary>
/// One glyph emitted by the shaper. Layout consumers (Phase 3) walk these to compute
/// line breaks, build paint runs, and feed the PDF emitter's <c>ToUnicodeCMap</c>
/// (Task 9 / <c>NetPdf.Pdf.Fonts.ToUnicodeCMap</c>) for text extraction.
/// </summary>
/// <remarks>
/// <para>
/// All position fields are in pixels at the requested font size — the shaper handles
/// the font-units → pixels conversion. Layout adds these to a pen position to produce
/// the next glyph's origin.
/// </para>
/// <para>
/// <see cref="Cluster"/> is HarfBuzz's source-codepoint index. After GSUB substitutions
/// (ligatures, contextual alternates) multiple glyphs can share a cluster, and one
/// glyph can span multiple source codepoints. Phase 1 doesn't yet exploit this for
/// ligature-aware ToUnicode — see the <c>FromShapedRuns</c> roadmap on
/// <c>NetPdf.Pdf.Fonts.ToUnicodeCMap</c>.
/// </para>
/// </remarks>
internal readonly record struct ShapedGlyph(
    ushort GlyphId,
    float XAdvance,
    float YAdvance,
    float XOffset,
    float YOffset,
    int Cluster);
