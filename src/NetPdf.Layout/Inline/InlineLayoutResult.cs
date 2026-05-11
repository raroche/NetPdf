// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;

namespace NetPdf.Layout.Inline;

/// <summary>Per Phase 3 Task 11 sub-cycle 1 review Finding #1 —
/// the full result of an inline-pass invocation, bundling everything
/// the painter (Phase 4) needs to resolve glyph data.
///
/// <para><b>Why a bundle?</b> <see cref="LineFragment.Slices"/>
/// reference <see cref="ShapedRunSlice.ShapedRunIndex"/> into the
/// <see cref="ShapedRuns"/> array; each <see cref="ShapedRun"/>'s
/// <see cref="ShapedRun.Source"/> in turn references
/// <see cref="ItemizedRun.SourceTextRunIndex"/> into
/// <see cref="PreprocessedRuns"/>. The painter walks slice → glyph
/// → source-run-style to position the actual shaped glyphs.
/// Returning only <c>LineFragment[]</c> made the slices
/// unresolvable because the <c>ShapedRun[]</c> + preprocessed
/// runs went out of scope inside <see cref="InlineLayouter"/>.</para>
///
/// <para><b>Lifetime.</b> Arrays are owned by this struct + safe to
/// hand off across thread/lifetime boundaries — no <see cref="System.IDisposable"/>
/// state. <see cref="ShapedRuns"/> is in document order, one entry
/// per <see cref="ItemizedRun"/> produced by
/// <see cref="LineBuilder.Itemize"/>.</para>
/// </summary>
/// <param name="Lines">Per-wrapped-line output from
/// <see cref="LineBuilder.Wrap"/>. Empty array when the input has
/// no glyphs (all-empty source TextRuns).</param>
/// <param name="ShapedRuns">The shaped runs the lines' slices index
/// into. Owned by the result + safe to read directly.</param>
/// <param name="PreprocessedRuns">The source TextRuns AFTER
/// white-space preprocessing (collapse / preserve / segment-break
/// normalization). The painter reads each shaped run's source-run
/// index into this list to get the <see cref="NetPdf.Css.ComputedValues.ComputedStyle"/> for
/// font / color / etc.</param>
internal readonly record struct InlineLayoutResult(
    LineFragment[] Lines,
    IReadOnlyList<ShapedRun> ShapedRuns,
    IReadOnlyList<TextRun> PreprocessedRuns)
{
    /// <summary>An empty result — no lines, no shaped runs, empty
    /// source. Use for fast-path early returns when inline content
    /// collapses to nothing (e.g., all-empty TextRuns after
    /// preprocessing).</summary>
    public static InlineLayoutResult Empty { get; } = new(
        System.Array.Empty<LineFragment>(),
        System.Array.Empty<ShapedRun>(),
        System.Array.Empty<TextRun>());
}
