// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Layout.Boxes;

namespace NetPdf.Layout.Layouters;

/// <summary>Per-conversion cache of an inline-only block's measured layout (`inline-only-block-line-
/// splitting`, PR #220 review [P2]). A single paragraph taller than a page splits its lines across N
/// pages; the multi-page driver re-runs the whole layout once per page, and each pass re-SHAPED the
/// ENTIRE paragraph (all text + every inline-block atomic's content) before discarding the lines
/// already emitted — ~O(pageCount × content) work + allocation, which an attacker could amplify with a
/// long atomic paragraph. The computed layout (shaped lines, per-line heights/baselines, atomic
/// placements + content) is PAGE-INVARIANT and deterministic for the block + its content inline size,
/// so it is held here and reused on every resume page (the per-page <c>EmitInlineOnlyBlockSlice</c> then
/// slices the cached lines). Mirrors <see cref="TableMeasurementCache"/> / <see cref="GridMeasurementCache"/>:
/// the value is boxed <c>object</c> (the computation is a private layouter type) and the key is the
/// block (reference identity — <see cref="Box"/> has no value equality) plus the inline size it was
/// shaped against, so a value is reused only under the identical inline budget. Threaded through
/// <c>LayoutContext</c> as <c>object?</c> and cast at the consumer.</summary>
internal sealed class InlineOnlyMeasurementCache
{
    private readonly Dictionary<(Box Block, double Inline), object> _layout = new();

    /// <summary>Diagnostic instrumentation — the number of FULL inline-only measures that actually ran
    /// (cache stores). For one paragraph it stays at 1 regardless of page count; the cross-page reuse is
    /// verified behaviorally by the per-page-allocation gate.</summary>
    public int FullMeasureCount { get; private set; }

    /// <summary>The page-invariant computed layout for this block + inline size, when a prior page
    /// already shaped it.</summary>
    public bool TryGet(Box block, double inline, out object? computation)
    {
        if (_layout.TryGetValue((block, inline), out var c))
        {
            computation = c;
            return true;
        }
        computation = null;
        return false;
    }

    /// <summary>Record a freshly-shaped computation. Stores once per key (the caller guards with
    /// <see cref="TryGet"/>), so a block + inline pair is shaped once per conversion.</summary>
    public void Store(Box block, double inline, object computation)
    {
        var key = (block, inline);
        if (_layout.ContainsKey(key))
        {
            return;
        }
        FullMeasureCount++;
        _layout[key] = computation;
    }
}
