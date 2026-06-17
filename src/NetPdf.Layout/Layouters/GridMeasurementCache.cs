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

    /// <summary>Instrumentation — the <see cref="NestedContentMeasurer.Measure"/>
    /// passes that actually RAN (cache misses). A regression test asserts the
    /// pre-measure + emission of one grid share the cache (the second site adds
    /// zero passes), proving the cross-component win is real.</summary>
    public int MeasurePassCount { get; private set; }

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
            cancellationToken, diagnostics).ContentBlockExtent;
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
