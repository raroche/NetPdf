// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Layout.Boxes;

namespace NetPdf.Layout.Layouters;

/// <summary>Per-conversion table measurement cache (`multi-page-allocation-churn`). A table that
/// fragments across N pages was measured ONCE per page by the SUBTREE-EXTENT pass
/// (<c>BlockLayouter.MeasureNestedTableContentExtent</c>, a transient <see cref="TableLayouter"/>
/// with no incoming continuation) — re-shaping every cell on every page, the O(n²) allocation
/// churn the deferral tracked. The DISPATCH pass already reused the prior page's
/// <c>TableLayouter.ColumnLayoutCache</c> via the <see cref="NetPdf.Paginate.TableContinuation"/>;
/// this cache extends that reuse to the subtree-extent pass (and page 1's dispatch) by holding the
/// SAME page-invariant column-layout token, keyed by the table box + its content inline size, so
/// the table is fully measured ONCE per conversion regardless of page count.
///
/// <para><b>Correctness.</b> The cached token is the column widths + per-row measurements + cell
/// placements + caption extents — none depend on the page-relative origin (the only thing that
/// changes between pages), so a restored measure is byte-identical to a fresh one (the same
/// invariant the cross-page <see cref="NetPdf.Paginate.TableContinuation"/> reuse already relies
/// on). The key is the table box (reference identity — <see cref="Box"/> has no value-equality
/// override) plus the content inline size the column split was resolved against, so a value is
/// reused only when measured under the identical inline budget. Threaded through
/// <c>LayoutContext</c> as <c>object?</c> and cast at the consumer, like
/// <see cref="GridMeasurementCache"/>.</para></summary>
internal sealed class TableMeasurementCache
{
    private readonly Dictionary<(Box Table, double Inline), object> _columnLayout = new();

    /// <summary>Instrumentation — the number of FULL table measures that actually ran (cache
    /// misses / stores). A regression test asserts a multi-page table is fully measured ONCE
    /// across all its pages (== 1), proving the cross-page reuse elides the per-page re-shape.</summary>
    public int FullMeasureCount { get; private set; }

    /// <summary>The page-invariant column-layout token for this table + inline size, when a prior
    /// page (or pass) already measured it. The caller restores it via
    /// <c>TableLayouter.RestoreMeasuredStateFromReuse</c> and skips the re-shape.</summary>
    public bool TryGet(Box table, double inline, out object? cache)
    {
        if (_columnLayout.TryGetValue((table, inline), out var c))
        {
            cache = c;
            return true;
        }
        cache = null;
        return false;
    }

    /// <summary>Record a freshly-measured column-layout token. Increments
    /// <see cref="FullMeasureCount"/> only on the first store for a key (the caller guards with
    /// <see cref="TryGet"/>, so a table+inline pair is measured once).</summary>
    public void Store(Box table, double inline, object cache)
    {
        var key = (table, inline);
        if (_columnLayout.ContainsKey(key))
        {
            return;
        }
        FullMeasureCount++;
        _columnLayout[key] = cache;
    }
}
