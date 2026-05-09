// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Layout.Inline;

/// <summary>
/// Per Phase 3 Task 9 cycle 3a — a contiguous slice of glyphs from a
/// single <see cref="ShapedRun"/> that fits on a single line. The
/// wrapping pass splits a <see cref="ShapedRun"/> at line-break
/// boundaries; each line is composed of one or more
/// <see cref="ShapedRunSlice"/> records (one per shaped run that
/// contributes to the line).
///
/// <para><b>Why slices instead of new ShapedRuns?</b> A wrapped
/// line can split a shaped run mid-glyph-stream (the run is wider
/// than a line). Re-shaping the truncated text would lose
/// HarfBuzz's cross-cluster shaping decisions; slicing the existing
/// glyph array keeps the cycle-2 contextual shaping intact.</para>
///
/// <para><b>Indexing.</b> <see cref="GlyphStart"/> +
/// <see cref="GlyphLength"/> are offsets into the source
/// <see cref="ShapedRun.Glyphs"/> array, NOT into the concatenated
/// text. The painter pulls glyph data via
/// <c>shapedRuns[ShapedRunIndex].Glyphs[GlyphStart..GlyphStart+GlyphLength]</c>.</para>
/// </summary>
/// <param name="ShapedRunIndex">Index into the input
/// <see cref="ShapedRun"/> array — identifies which shaped run this
/// slice belongs to.</param>
/// <param name="GlyphStart">First glyph in this slice (inclusive).</param>
/// <param name="GlyphLength">Number of glyphs in this slice. Always
/// strictly positive — the wrapper never emits zero-length slices.
/// (Empty drawable lines, e.g. a lone LF on its own line after
/// mandatory-control trimming, surface as
/// <see cref="LineFragment.Slices"/> = <c>Array.Empty&lt;ShapedRunSlice&gt;()</c>
/// with <see cref="LineFragment.EndsWithMandatoryBreak"/> = true,
/// not as a zero-length <see cref="ShapedRunSlice"/>.)</param>
/// <param name="SliceAdvance">Sum of <c>XAdvance</c> across the
/// glyphs in this slice, in CSS px. Cycle 3b/c uses this for
/// <c>text-align</c> centering / justification distribution.</param>
internal readonly record struct ShapedRunSlice(
    int ShapedRunIndex,
    int GlyphStart,
    int GlyphLength,
    double SliceAdvance);
