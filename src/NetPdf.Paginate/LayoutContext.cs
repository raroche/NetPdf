// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using NetPdf.Paginate.Diagnostics;

namespace NetPdf.Paginate;

/// <summary>PR #218 review [P2 #5] — the PURPOSE of a layout pass, carried transitively on
/// <see cref="LayoutContext.MeasurePurpose"/> so a nested specialized layouter (flex / grid / table /
/// multicol) inherits it instead of starting a fresh real-layout pass. It decouples two independent
/// policies — whether out-of-flow (abspos / fixed) descendants emit, and how percentage padding /
/// margins resolve — that a single boolean conflated.</summary>
internal enum MeasurePurpose
{
    /// <summary>Real layout (the default + a buffered emission that FLUSHES into the final tree, e.g.
    /// the flex item-content flush or the table cell content): out-of-flow descendants EMIT and
    /// percentages resolve against the real containing size.</summary>
    Layout = 0,

    /// <summary>An intrinsic min/max-content CONTRIBUTION probe (the box's indefinite-basis width
    /// measure): out-of-flow descendants are SKIPPED (they don't contribute to intrinsic inline size)
    /// AND cyclic percentage padding / margins contribute as 0 (CSS Sizing §5.2.1 — the basis is
    /// indefinite). The buffer is read for an extent and dropped.</summary>
    IntrinsicContribution = 1,

    /// <summary>A block-extent (height) measure at a DEFINITE width: out-of-flow descendants are
    /// SKIPPED (they don't contribute to the §10.6.7 auto block size) but percentages resolve against
    /// the definite width (real). The buffer is read for an extent and dropped.</summary>
    DefiniteWidthExtent = 2,
}

/// <summary>Policy helpers for <see cref="MeasurePurpose"/> (PR #218 review [P2 #5]).</summary>
internal static class MeasurePurposeExtensions
{
    /// <summary>Whether out-of-flow (abspos / fixed) descendants are SKIPPED — true for both
    /// extent-only measures (their buffer is dropped, and out-of-flow doesn't affect the extent).</summary>
    public static bool SuppressesOutOfFlowEmission(this MeasurePurpose p)
        => p is MeasurePurpose.IntrinsicContribution or MeasurePurpose.DefiniteWidthExtent;

    /// <summary>Whether cyclic percentage padding / margins resolve to 0 (CSS Sizing §5.2.1) — true
    /// only for the indefinite-basis intrinsic contribution probe.</summary>
    public static bool ZeroesCyclicPercentInsets(this MeasurePurpose p)
        => p == MeasurePurpose.IntrinsicContribution;

    /// <summary>The effective purpose of a NESTED measure requested with <paramref name="requested"/>
    /// from a pass whose purpose is the receiver. An <see cref="MeasurePurpose.IntrinsicContribution"/>
    /// parent ALWAYS wins (its subtree's basis is indefinite, so a nested "definite-width" measure is
    /// actually at the 1e6/1px probe width — still indefinite — and a flush still contributes 0 cyclic
    /// padding); otherwise a non-Layout request OVERRIDES (e.g. a definite-width height probe inside a
    /// real layout), while a flush / real (<see cref="MeasurePurpose.Layout"/>) request INHERITS the
    /// parent — so an item-content flush inside a dropped measure stays a dropped measure rather than
    /// leaking real-layout out-of-flow emission / percentage persistence.</summary>
    public static MeasurePurpose ForNested(this MeasurePurpose parent, MeasurePurpose requested)
        => parent == MeasurePurpose.IntrinsicContribution ? MeasurePurpose.IntrinsicContribution
            : requested != MeasurePurpose.Layout ? requested
            : parent;
}

/// <summary>
/// Per Phase 3 plan — the document-scoped, layouter-side mutable state
/// passed by-ref through the layout call tree. Distinct from
/// <see cref="FragmentainerContext"/> (per-page state) — this carries
/// data that survives page boundaries: counter values, named-string
/// last-write tracking, the containing-block stack, the active writing
/// mode + bidi resolution baseline.
///
/// <para><b>Block-axis naming.</b> Per Phase 3 review fix #2 + CSS
/// Logical Properties L1, dimensions use the
/// <c>AvailableInlineSize</c> / <c>AvailableBlockSize</c> nomenclature
/// rather than width / height. In <c>horizontal-tb</c> the inline
/// axis is horizontal + the block axis is vertical; in <c>vertical-rl/lr</c>
/// the axes flip. Layouters that consume this context don't need to
/// branch on writing mode for routine sizing.</para>
///
/// <para><b>ref struct.</b> The Phase 3 plan calls for
/// <c>LayoutContext ref struct</c> so the containing-block stack +
/// per-call mutation stay on the layouter's call stack with zero
/// heap pressure. Reference fields hosted in this struct are kept
/// minimal; mutable backing collections (the counter map + named
/// strings registry) are heap objects referenced through this
/// struct, which is fine — only the struct itself is non-allocatable.</para>
/// </summary>
internal ref struct LayoutContext
{
    /// <summary>Available inline-axis extent (CSS px) for the box
    /// currently being laid out. In <c>horizontal-tb</c> this is the
    /// containing-block width; in <c>vertical-rl/lr</c> it's the
    /// containing-block height.</summary>
    public double AvailableInlineSize;

    /// <summary>Available block-axis extent (CSS px) — the
    /// fragmentation-axis space available for the box. Typically
    /// <see cref="FragmentainerContext.RemainingBlockSize"/> when
    /// filling a page; <c>double.PositiveInfinity</c> when intrinsic
    /// (a flex / grid container in min/max-content sizing).</summary>
    public double AvailableBlockSize;

    /// <summary>Current writing mode. Mutates only at writing-mode
    /// boundaries (e.g., a child with <c>writing-mode: vertical-rl</c>
    /// inside an <c>horizontal-tb</c> parent); the layouter's caller
    /// snapshots + restores the value across the boundary.</summary>
    public WritingMode WritingMode;

    /// <summary>Per CSS Writing Modes L3 — Direction (LTR / RTL) at
    /// the containing-block level. Bidi resolution at the inline level
    /// happens in <c>NetPdf.Text</c>'s UAX #9 implementation; this
    /// flag tracks the BLOCK-level direction.</summary>
    public bool IsRtl;

    /// <summary>The active <see cref="FragmentainerContext"/>. Updated
    /// by the layouter when a page break commits + a new fragmentainer
    /// is allocated.</summary>
    public FragmentainerContext Fragmentainer;

    /// <summary>PR #218 review [P1 #1 / P2 #5] — the PURPOSE of the current layout pass. Carried
    /// transitively (this is a by-ref <c>ref struct</c>, so it flows down the call tree) so a nested
    /// specialized layouter inherits an intrinsic / definite-extent measure instead of starting a
    /// fresh real-layout pass that would emit out-of-flow content or persist probe-derived percentage
    /// insets onto the shared style. Set by the measure entry points (<c>NestedContentMeasurer</c>,
    /// <c>TableLayouter.MeasureCellContent</c>); <see cref="MeasurePurpose.Layout"/> for the driver's
    /// real page layout.</summary>
    public MeasurePurpose MeasurePurpose;

    /// <summary>Per post-Task-7 review (recommendation P1 #2) —
    /// ambient pagination diagnostics sink. Pre-fix, the
    /// <see cref="LayoutRetryCoordinator"/> + each layouter received
    /// their OWN <see cref="IPaginateDiagnosticsSink"/> at construction.
    /// A composition root that wired only the coordinator's sink would
    /// miss <c>PAGINATION-FORCED-OVERFLOW-001</c> emitted from a
    /// layouter's own forward-progress path on the Strict attempt
    /// (because LastResort never fires when Strict commits).
    ///
    /// <para>Post-fix, the sink lives on the layout context — the
    /// natural ambient holder. The coordinator's <c>Run</c> threads
    /// its sink through to <c>layout.Diagnostics</c> on entry; the
    /// layouter reads from <c>layout.Diagnostics</c> rather than
    /// from a constructor-injected field. Wiring once at the
    /// composition root reaches both sides.</para>
    ///
    /// <para><see langword="null"/> means "no diagnostic sink wired";
    /// emission sites use the <c>OptimizingBreakResolver.SafeEmit</c>
    /// helper which is null-safe.</para></summary>
    public IPaginateDiagnosticsSink? Diagnostics;

    /// <summary>Per-conversion grid measurement cache (measurement-cache cycle —
    /// the cross-COMPONENT follow-up to the per-instance caches). A grid's cells
    /// are shaped twice today — once by <c>BlockLayouter.PreMeasureGridRowExtent</c>
    /// (the auto-row pre-grow) and once by the <c>GridLayouter</c> emission Resolve.
    /// A shared cache, allocated ONCE at the root pipeline + threaded through the
    /// layout context, lets the two sites reuse each other's measurements (and
    /// successive page dispatches reuse prior pages'). Typed <c>object?</c> to keep
    /// the NetPdf.Paginate → NetPdf.Layout dependency edge clean (the concrete
    /// <c>GridMeasurementCache</c> lives in NetPdf.Layout, cast at the consumers —
    /// the same pattern as <see cref="LayoutContinuation"/>'s <c>LayouterState</c>).
    /// A measured CONTENT extent is deterministic for its keyed inputs (the atomic
    /// measure pass never paginates), so a hit is byte-identical to a fresh measure.
    /// <see langword="null"/> ⇒ no shared cache wired (e.g. a direct-layouter test);
    /// the consumer falls back to its own per-instance cache, so correctness holds
    /// even if a nested context misses propagation.</summary>
    public object? GridMeasureCache;

    /// <summary>Per `multi-page-allocation-churn` — the cross-page table measurement cache,
    /// allocated ONCE at the root pipeline + threaded through the layout context like
    /// <see cref="GridMeasureCache"/>. A table that fragments across N pages was re-shaped per
    /// page by the SUBTREE-EXTENT measure (a transient layouter with no continuation); this cache
    /// holds the page-invariant column-layout token so that pass (and page 1's dispatch) reuse the
    /// measure instead of re-shaping every cell every page (the O(n²) churn). Typed <c>object?</c>
    /// to keep the NetPdf.Paginate → NetPdf.Layout edge clean (concrete
    /// <c>TableMeasurementCache</c> lives in NetPdf.Layout, cast at the consumer); the token is
    /// page-invariant + deterministic, so a hit is byte-identical. <see langword="null"/> ⇒ no
    /// shared cache wired; the dispatch path still reuses the continuation's cache.</summary>
    public object? TableMeasureCache;

    /// <summary>Per `inline-only-block-line-splitting` (PR #220 review [P2]) — the cross-page cache of a
    /// split inline-only block's shaped layout, allocated ONCE at the root pipeline + threaded like
    /// <see cref="TableMeasureCache"/>. A paragraph that splits across N pages was re-shaped (all text +
    /// every inline-block atomic's content) on every resume page; this holds the page-invariant
    /// computation keyed by the block + its content inline size so it is shaped once per conversion.
    /// Typed <c>object?</c> (the concrete <c>InlineOnlyMeasurementCache</c> lives in NetPdf.Layout, cast
    /// at the consumer); the computation is page-invariant + deterministic, so a hit is byte-identical.
    /// <see langword="null"/> ⇒ no shared cache wired (each page re-shapes, the prior behavior).</summary>
    public object? InlineOnlyMeasureCache;

    /// <summary>Document-scoped counter values per CSS Lists L3 §4.
    /// Keys are counter names (<c>page</c>, <c>pages</c>, author-defined);
    /// values are integer counter readings at the current layout
    /// position. <see cref="Counter(string, int)"/> updates +
    /// <see cref="ReadCounter(string)"/> reads.</summary>
    private Dictionary<string, int>? _counters;

    /// <summary>Construct a fresh root layout context. The caller
    /// (typically the Phase 3 layout entry point) pre-allocates the
    /// fragmentainer + page setup; this constructor only wires
    /// references.</summary>
    public LayoutContext(FragmentainerContext fragmentainer)
    {
        ArgumentNullException.ThrowIfNull(fragmentainer);
        AvailableInlineSize = fragmentainer.ContentInlineSize;
        AvailableBlockSize = fragmentainer.BlockSize;
        WritingMode = WritingMode.HorizontalTb;
        IsRtl = false;
        Fragmentainer = fragmentainer;
        MeasurePurpose = MeasurePurpose.Layout;
        Diagnostics = null;
        GridMeasureCache = null;
        TableMeasureCache = null;
        InlineOnlyMeasureCache = null;
        _counters = null;
    }

    /// <summary>Read the current value of <paramref name="counterName"/>;
    /// 0 when the counter has not been set in this layout pass.</summary>
    public int ReadCounter(string counterName)
    {
        ArgumentNullException.ThrowIfNull(counterName);
        if (_counters is null) return 0;
        return _counters.TryGetValue(counterName, out var v) ? v : 0;
    }

    /// <summary>Set <paramref name="counterName"/> to
    /// <paramref name="value"/>. Lazily allocates the backing
    /// dictionary so a layout pass that never uses author counters
    /// pays zero allocation overhead.</summary>
    public void Counter(string counterName, int value)
    {
        ArgumentNullException.ThrowIfNull(counterName);
        _counters ??= new Dictionary<string, int>(StringComparer.Ordinal);
        _counters[counterName] = value;
    }

    /// <summary>Increment <paramref name="counterName"/> by
    /// <paramref name="delta"/> (default 1) per CSS Lists L3
    /// <c>counter-increment</c>. Returns the new value.</summary>
    public int IncrementCounter(string counterName, int delta = 1)
    {
        var current = ReadCounter(counterName);
        var next = current + delta;
        Counter(counterName, next);
        return next;
    }

    /// <summary>Per Phase 3 review fix #1 — read-only access to the
    /// underlying counter table for <see cref="LayoutCheckpoint.Capture"/>.
    /// Returns <see langword="null"/> when no counters have been
    /// touched (the lazy-alloc state). The caller MUST NOT mutate
    /// the returned dictionary; it's the live backing store.</summary>
    internal IReadOnlyDictionary<string, int>? PeekCounters() => _counters;

    /// <summary>Per Phase 3 review fix #1 — restore the counter table
    /// from a <see cref="LayoutCheckpoint"/> snapshot. Pass
    /// <see langword="null"/> (or an empty dict) to reset to the
    /// lazy-alloc-not-yet state — per PR #19 Copilot review #2,
    /// drop the backing dict reference (not just clear it) so
    /// <see cref="PeekCounters"/> returns <see langword="null"/>
    /// after restore + the next <see cref="Counter"/> call re-
    /// triggers the lazy-alloc path. Without this, an "untouched"
    /// counter state and a "touched-then-cleared" state were
    /// observably different through PeekCounters, breaking the
    /// stated allocation contract.</summary>
    internal void RestoreCounters(Dictionary<string, int>? snapshot)
    {
        if (snapshot is null || snapshot.Count == 0)
        {
            // Drop the dict ref so PeekCounters returns null again,
            // matching the freshly-constructed-context state.
            _counters = null;
            return;
        }
        _counters ??= new Dictionary<string, int>(snapshot.Count, StringComparer.Ordinal);
        _counters.Clear();
        foreach (var kvp in snapshot) _counters[kvp.Key] = kvp.Value;
    }
}
