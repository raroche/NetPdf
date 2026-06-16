// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Layout.Boxes;

namespace NetPdf.Layout.Inline;

/// <summary>
/// Inline-atomic-boxes cycle (CSS Display 3 §2.4 "atomic inline-level box") — an opaque, indivisible
/// inline-level box that participates in line layout as a single unit of a fixed advance + height,
/// rather than as shaped glyphs. The first consumer is an inline <c>&lt;img&gt;</c>
/// (<see cref="BoxKind.InlineReplacedElement"/>): its used width/height (already written into the box's
/// style slots by the pre-layout replaced-size pass) become the atomic's advance + block extent, the
/// line builder reserves the space, and <c>BlockLayouter</c> emits a positioned
/// <c>BoxFragment</c> for <see cref="Box"/> so <c>ImagePainter</c> paints it for free.
///
/// <para>Carried as an optional payload on the <see cref="TextRun"/> (a one-char <c>U+FFFC</c> OBJECT
/// REPLACEMENT CHARACTER run, so concat/index bookkeeping stays aligned) and on the resulting
/// <see cref="ShapedRun"/> (whose single synthetic glyph carries <see cref="WidthPx"/> as its advance,
/// shaped WITHOUT HarfBuzz). The painter skips the synthetic glyph — the box is painted from its own
/// emitted fragment.</para>
///
/// <para><b>Baseline (first cut).</b> The atomic is treated as <c>vertical-align: baseline</c>: its
/// bottom margin edge sits on the text baseline (CSS 2.2 §10.8 — the replaced element has no own
/// baseline, so its bottom aligns to the line's baseline). Other <c>vertical-align</c> values, and
/// inline-block / inline-flex / inline-grid / inline-table atomics, remain deferred.</para>
/// </summary>
/// <param name="Box">The atomic's source box (e.g. the inline <c>&lt;img&gt;</c>). The painter looks it
/// up in the image cache; the emitted fragment carries it so painting is geometry-driven.</param>
/// <param name="WidthPx">The atomic's used border-box inline size (CSS px) — its line advance.</param>
/// <param name="HeightPx">The atomic's used border-box block size (CSS px) — its line-height
/// contribution (the line box grows to fit it).</param>
internal readonly record struct InlineAtomic(Box Box, double WidthPx, double HeightPx);
