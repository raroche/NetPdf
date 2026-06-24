// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Threading;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Inline;
using NetPdf.Paginate;
using NetPdf.Paginate.Diagnostics;

namespace NetPdf.Layout.Layouters;

/// <summary>Per-conversion grid measurement cache (measurement-cache cycle — the
/// cross-COMPONENT follow-up to <see cref="GridLayouter"/>'s per-instance caches).
/// A content-determined grid cell is shaped twice per conversion today — once by
/// <c>BlockLayouter.PreMeasureGridRowExtent</c> (the auto-row pre-grow) and once by
/// the <see cref="GridLayouter"/> emission Resolve — and a paginating grid re-shapes
/// per page. One cache, allocated ONCE at the root pipeline and threaded through the
/// <see cref="LayoutContext"/> (as <c>object?</c>, cast at the consumers), lets all
/// those sites reuse each other's measurements.
///
/// <para><b>Correctness.</b> The key is the FULL set of inputs
/// <see cref="NestedContentMeasurer.Measure"/> consumes — (item, available inline
/// width, block budget, writing mode, RTL) — so a value is only reused when measured
/// under identical inputs (the inline-extent key omits the width, which a max-content
/// measure is independent of). A measured CONTENT extent is deterministic for that
/// key (the atomic measure pass never paginates), so a cache hit is byte-identical to
/// a fresh measure. <see cref="Box"/> has no value-equality override, so the tuple key
/// compares the item by reference identity.</para></summary>
internal sealed class GridMeasurementCache
{
    private readonly Dictionary<
        (Box Item, double AvailInline, double BlockBudget, WritingMode Wm, bool Rtl), double> _blockExtent = new();
    private readonly Dictionary<
        (Box Item, double BlockBudget, WritingMode Wm, bool Rtl), double> _inlineExtent = new();

    /// <summary>Per Phase 3 Task 18 (grid-fragment-plan-shared-sizing-deferral, partial)
    /// — the natural row extent <c>BlockLayouter.PreMeasureGridRowExtent</c> resolves to
    /// grow an auto-height grid's wrapper, memoized per (grid box, content inline size,
    /// measure block budget). That pre-grow runs an entire §11 sizing + §8.5 placement
    /// pass and repeats IDENTICALLY on every page a multi-page grid spans (the inputs —
    /// indefinite block budget = 1, the wrapper's content inline size, the page block
    /// budget — are page-invariant). The cell SHAPING is already shared via
    /// <see cref="_blockExtent"/>/<see cref="_inlineExtent"/>; this memo additionally
    /// elides the redundant §11 ARITHMETIC on resume pages + rewind retries. The result
    /// is deterministic for the key (same inputs → same Resolve), so a hit is byte-
    /// identical.</summary>
    private readonly Dictionary<(Box Grid, double Inline, double Budget), double> _rowExtentSum = new();

    /// <summary>Instrumentation — the <see cref="NestedContentMeasurer.Measure"/>
    /// passes that actually RAN (cache misses). A regression test asserts the
    /// pre-measure + emission of one grid share the cache (the second site adds
    /// zero passes), proving the cross-component win is real.</summary>
    public int MeasurePassCount { get; private set; }

    /// <summary>Per Phase 3 Task 18 — instrumentation: the number of
    /// <c>PreMeasureGridRowExtent</c> §11 passes that actually RAN (row-extent cache
    /// misses). A regression test asserts a multi-page grid pre-measures its row extent
    /// ONCE across all its pages (== 1), proving the cross-page memo elides the
    /// per-page re-resolve.</summary>
    public int RowExtentComputeCount { get; private set; }

    /// <summary>Per Phase 3 Task 18 — true (with the memoized extent) when the natural
    /// row extent for this grid + inputs was already resolved on a prior page / attempt.
    /// The caller skips the §11 pass entirely on a hit.</summary>
    public bool TryGetRowExtentSum(Box grid, double inline, double budget, out double extent)
        => _rowExtentSum.TryGetValue((grid, inline, budget), out extent);

    /// <summary>Per Phase 3 Task 18 — record a freshly-resolved row extent. Increments
    /// <see cref="RowExtentComputeCount"/> only on the first store for a key (the caller
    /// guards with <see cref="TryGetRowExtentSum"/>, so a key is computed once).</summary>
    public void CacheRowExtentSum(Box grid, double inline, double budget, double extent)
    {
        var key = (grid, inline, budget);
        if (_rowExtentSum.ContainsKey(key)) return;
        RowExtentComputeCount++;
        _rowExtentSum[key] = extent;
    }

    /// <summary>The cell's CONTENT block extent at <paramref name="availInline"/>,
    /// memoized on the full input set. Mirrors the lambda in
    /// <see cref="GridLayouter"/>'s Resolve.</summary>
    public double BlockExtent(
        Box item, double availInline, double blockBudget, IShaperResolver? shaper,
        WritingMode writingMode, bool isRtl, CancellationToken cancellationToken,
        IPaginateDiagnosticsSink? diagnostics = null)
    {
        var key = (item, availInline, blockBudget, writingMode, isRtl);
        if (_blockExtent.TryGetValue(key, out var cached)) return cached;
        MeasurePassCount++;
        var extent = NestedContentMeasurer.Measure(
            item, availInline, blockBudget, shaper, writingMode, isRtl,
            cancellationToken, diagnostics,
            // A row block-extent at a definite column width — % padding resolves real (PR #218 [P1 #2]).
            purpose: MeasurePurpose.DefiniteWidthExtent).ContentBlockExtent;
        _blockExtent[key] = extent;
        return extent;
    }

    /// <summary>The cell's MAX-CONTENT inline extent. Width-independent, so the key
    /// omits <paramref name="availInline"/> (carried only for the measure call).</summary>
    public double InlineExtent(
        Box item, double availInline, double blockBudget, IShaperResolver? shaper,
        WritingMode writingMode, bool isRtl, CancellationToken cancellationToken,
        IPaginateDiagnosticsSink? diagnostics = null)
    {
        var key = (item, blockBudget, writingMode, isRtl);
        if (_inlineExtent.TryGetValue(key, out var cached)) return cached;
        MeasurePassCount++;
        var extent = NestedContentMeasurer.Measure(
            item, availInline, blockBudget, shaper, writingMode, isRtl,
            cancellationToken, diagnostics).ContentInlineExtent;
        _inlineExtent[key] = extent;
        return extent;
    }
}
