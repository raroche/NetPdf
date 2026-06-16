// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Shaping;

namespace NetPdf.Layout.Inline;

/// <summary>
/// Per Phase 3 Task 9 cycle 2 — one <see cref="ItemizedRun"/> after
/// the shaping pass. Carries the source itemization metadata
/// (UTF-16 range, bidi level, source-style index) plus the
/// HarfBuzz-shaped glyph stream + the run's total inline-axis advance.
///
/// <para><b>Why store TotalAdvance?</b> Cycle 3's line-break +
/// wrapping pass needs per-run advance to decide when a candidate
/// line exceeds the available inline-size. Computing it once at
/// shape time is cheaper than re-summing on every wrap iteration.</para>
///
/// <para><b>Glyph order.</b> <see cref="Glyphs"/> is in HARFBUZZ
/// LOGICAL ORDER for LTR runs and HARFBUZZ VISUAL ORDER for RTL runs
/// (HarfBuzz already reverses RTL output internally). Painters can
/// walk LTR runs straight + RTL runs as-is. Cluster indices map
/// back to the SOURCE UTF-16 (the un-reversed text), so the painter
/// can build a ToUnicodeCMap from cluster→source spans regardless
/// of direction.</para>
///
/// <para><b>Cycle 2 simplification.</b> Cycle 2 produces ShapedRuns
/// for the entire input as a single shaping pass per ItemizedRun.
/// Cycle 3 will subdivide ShapedRuns at line-break opportunities for
/// the wrapping pass; the wrapping output is
/// <c>LineFragment[]</c> (a separate type, not yet shipped).</para>
/// </summary>
/// <param name="Source">The itemized run this shaped output belongs
/// to (UTF-16 range + bidi level + source-style index).</param>
/// <param name="Glyphs">HarfBuzz-shaped glyph stream. Empty array
/// when the source run is empty or contains only zero-advance
/// codepoints (e.g., joiners).</param>
/// <param name="TotalAdvance">Sum of <c>XAdvance</c> across
/// <see cref="Glyphs"/> in CSS px. Typically non-negative for normal
/// fonts — HarfBuzz's GPOS table can in principle emit negative
/// XAdvance (kerning corrections, marks), but well-formed fonts
/// produce a non-negative cumulative advance for a complete cluster
/// run. Cycle 3's wrap pass treats this as the run's inline-axis
/// length without enforcing a sign invariant on the wrapper itself.</param>
/// <param name="Atomic">Inline-atomic-boxes cycle — set when this run is an ATOMIC inline box (an
/// inline <c>&lt;img&gt;</c>): <see cref="Glyphs"/> is then a single synthetic glyph whose
/// <c>XAdvance</c> equals <see cref="InlineAtomic.WidthPx"/> (NOT HarfBuzz-shaped), and
/// <see cref="TotalAdvance"/> equals that width. The painter skips the synthetic glyph — the atomic's
/// box is painted from its own emitted fragment — but still advances the line cursor by it. Null for
/// ordinary text runs.</param>
internal readonly record struct ShapedRun(
    ItemizedRun Source,
    ShapedGlyph[] Glyphs,
    double TotalAdvance,
    InlineAtomic? Atomic = null);
