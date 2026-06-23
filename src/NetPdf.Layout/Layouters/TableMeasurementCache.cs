// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Layout.Boxes;

namespace NetPdf.Layout.Layouters;

/// <summary>Per-conversion table measurement cache (`multi-page-allocation-churn`). A table that
/// fragments across N pages was measured ONCE per page by the SUBTREE-EXTENT pass
/// (<c>BlockLayouter.MeasureNestedTableContentExtent</c>, a transient <see cref="TableLayouter"/>
/// with no incoming continuation) — re-shaping every cell on every page, the O(n²) allocation
/// churn the deferral tracked. The DISPATCH (emit) pass already reused the prior page's
/// <c>TableLayouter.ColumnLayoutCache</c> via the <see cref="NetPdf.Paginate.TableContinuation"/>;
/// this cache adds the SAME reuse to the subtree-extent pass by holding the page-invariant
/// column-layout token keyed by the table box + its content inline size, so the table is fully
/// SHAPED ONCE per conversion regardless of page count.
///
/// <para><b>Ownership invariant (PR #211 review [P1]).</b> This cache is read + written ONLY by
/// the MEASURE-ONLY subtree-extent pass, which measures at a PLACEHOLDER inline offset of 0 and
/// never flushes its buffered fragments to the output sink. The token bundles the column widths +
/// per-row measurements + cell placements + buffered cell/caption content; the cell/caption
/// buffers bake in the inline offset they were measured at (FlushTo rebases only the block axis),
/// so they are correct ONLY at that offset. The EMIT path (<c>PreMeasureTableIfNeeded</c> →
/// <c>EmitTableInner</c>) must NEVER restore this cache — doing so would flush cell content at x=0
/// for an indented / margined / nested table. The emit path reuses the prior page's REAL-offset
/// layout via the <see cref="NetPdf.Paginate.TableContinuation"/> instead.</para>
///
/// <para><b>Correctness.</b> The subtree pass only consumes the OFFSET-INDEPENDENT row heights
/// (for the dry-run committed extent), which are page-invariant + deterministic for the key, so a
/// restored measure yields a byte-identical extent. The key is the table box (reference identity —
/// <see cref="Box"/> has no value-equality override) plus the content inline size the column split
/// was resolved against, so a value is reused only under the identical inline budget. Threaded
/// through <c>LayoutContext</c> as <c>object?</c> and cast at the consumer, like
/// <see cref="GridMeasurementCache"/>.</para></summary>
internal sealed class TableMeasurementCache
{
    private readonly Dictionary<(Box Table, double Inline), object> _columnLayout = new();

    /// <summary>Diagnostic instrumentation — the number of FULL table measures that actually ran
    /// (cache stores). For a single table it stays at 1 regardless of page count; the cross-page
    /// reuse is verified BEHAVIORALLY by the allocation-slope gate
    /// (<c>PerformanceGateTests.Multi_page_allocation_per_page_stays_constant_with_page_count</c>),
    /// which would regress to ~O(n²) if the table were re-shaped per page. This counter is not
    /// asserted directly (the cache is internal to the pipeline); it exists for debugging.</summary>
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
