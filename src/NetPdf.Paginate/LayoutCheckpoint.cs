// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Concurrent;
using System.Collections.Generic;

namespace NetPdf.Paginate;

/// <summary>
/// Per Phase 3 plan + review fix #1 — atomic rewind snapshot. When
/// <see cref="IBreakResolver.ConsiderBreakAt"/> returns
/// <see cref="BreakAction.Rewind"/>, the layouter must restore EVERY
/// piece of state that the failed attempt mutated. A partial
/// restore (e.g., used-block-size rewinds but counters don't) is
/// strictly worse than no restore — silently produces inconsistent
/// pagination across re-attempts.
///
/// <para>Pooled via <see cref="LayoutCheckpointPool"/> so the bounded
/// retry loop doesn't allocate per attempt.</para>
///
/// <para><b>What's saved.</b> The fields below cover the state that
/// changes during one fragmentainer's worth of layout:</para>
/// <list type="bullet">
///   <item><see cref="PageIndex"/> + <see cref="UsedBlockSize"/> +
///   <see cref="LastEmittedChildIndex"/> — physical pagination
///   position.</item>
///   <item><see cref="IncomingContinuation"/> — what continuation the
///   previous-page layout ended with.</item>
///   <item><see cref="PageCounterValue"/> — <c>counter(page)</c>
///   reading.</item>
///   <item><see cref="NamedStringsSnapshot"/> + <see cref="CountersSnapshot"/>
///   — per Phase 3 review fix #1, the named-strings + author-counters
///   tables that downstream <c>content: string(name)</c> /
///   <c>counter()</c> reads consult. Captured as deep copies so the
///   live tables can mutate without aliasing the snapshot.</item>
///   <item><see cref="WritingMode"/> + <see cref="IsRtl"/> +
///   <see cref="AvailableInlineSize"/> + <see cref="AvailableBlockSize"/>
///   — layout context geometry that might have been mutated by the
///   failed attempt.</item>
///   <item><see cref="FloatManagerStateSnapshot"/> — placeholder for
///   the Phase 3 Task 8 float manager state.</item>
///   <item><see cref="FragmentOutputCursor"/> — index into the
///   in-progress fragment list for the current page; rewind discards
///   any fragments emitted past this cursor.</item>
/// </list>
///
/// <para>Things that DON'T change during layout (immutable box-tree
/// references, the source HTML, the computed cascade) are NOT saved
/// — re-reading them on rewind is free.</para>
/// </summary>
internal sealed class LayoutCheckpoint
{
    /// <summary>Page index this checkpoint was taken on. Rewinds within
    /// the same page only — cross-page rewinds aren't supported (the
    /// previous page's bytes have already been written).</summary>
    public int PageIndex;

    /// <summary>Block-axis position (CSS px from start of the content
    /// area) where the next layout call should resume. See
    /// <see cref="FragmentainerContext.UsedBlockSize"/>.</summary>
    public double UsedBlockSize;

    /// <summary>Last fully-emitted box index in the parent's children
    /// list. -1 when nothing has been emitted yet on this page.</summary>
    public int LastEmittedChildIndex;

    /// <summary>The continuation that the previous-page layout ended
    /// with, or <see langword="null"/> when this is the first page of
    /// the current layout subtree.</summary>
    public LayoutContinuation? IncomingContinuation;

    /// <summary>Page-counter snapshot — the value of <c>counter(page)</c>
    /// visible at the moment of the checkpoint. Captured separately
    /// from <see cref="PageIndex"/> because per CSS GCPM L3 §8.4
    /// counter values can be set by author CSS, not just by physical
    /// page number.</summary>
    public int PageCounterValue;

    /// <summary>Per Phase 3 review fix #1 — deep copy of
    /// <see cref="FragmentainerContext.NamedStrings"/> at checkpoint
    /// time. <see langword="null"/> when the named-strings table was
    /// untouched (no allocation overhead in the common case).</summary>
    public Dictionary<string, string>? NamedStringsSnapshot;

    /// <summary>Per Phase 3 review fix #1 — deep copy of the active
    /// <see cref="LayoutContext"/>'s author-counter table.
    /// <see langword="null"/> when no author counters have been touched.</summary>
    public Dictionary<string, int>? CountersSnapshot;

    /// <summary>Per Phase 3 review fix #1 — writing mode at checkpoint.</summary>
    public WritingMode WritingMode;

    /// <summary>Per Phase 3 review fix #1 — block-direction RTL flag
    /// (the inline-axis bidi happens inside <c>NetPdf.Text</c>).</summary>
    public bool IsRtl;

    /// <summary>Per Phase 3 review fix #1 — inline-axis available
    /// extent (CSS px) at checkpoint.</summary>
    public double AvailableInlineSize;

    /// <summary>Per Phase 3 review fix #1 — block-axis available
    /// extent (CSS px) at checkpoint.</summary>
    public double AvailableBlockSize;

    /// <summary>Per Phase 3 review fix #1 — opaque snapshot of the
    /// float manager state (Phase 3 Task 8 fills this in).</summary>
    public object? FloatManagerStateSnapshot;

    /// <summary>Per Phase 3 review fix #1 — index into the
    /// in-progress fragment list for the current page. On rewind,
    /// fragments emitted past this cursor are discarded so the
    /// re-attempt produces a consistent fragment sequence.</summary>
    public int FragmentOutputCursor;

    /// <summary>Per Phase 3 review fix #1 — capture all rewindable
    /// state in one atomic operation. Layouters call this at
    /// candidate-break points BEFORE attempting the speculative
    /// layout that might be rolled back. Pass the live
    /// <see cref="FragmentainerContext"/> + <see cref="LayoutContext"/>
    /// + the fragment-list cursor; this populates every snapshot
    /// field.</summary>
    public void Capture(
        FragmentainerContext fragmentainer,
        in LayoutContext layout,
        int fragmentOutputCursor,
        int lastEmittedChildIndex,
        LayoutContinuation? incomingContinuation,
        int pageCounterValue)
    {
        PageIndex = fragmentainer.PageIndex;
        UsedBlockSize = fragmentainer.UsedBlockSize;
        LastEmittedChildIndex = lastEmittedChildIndex;
        IncomingContinuation = incomingContinuation;
        PageCounterValue = pageCounterValue;

        // Deep-copy mutable tables so the live layout can mutate them
        // without aliasing the snapshot.
        if (fragmentainer.NamedStrings.Count > 0)
        {
            NamedStringsSnapshot ??= new Dictionary<string, string>(fragmentainer.NamedStrings.Count);
            NamedStringsSnapshot.Clear();
            foreach (var kvp in fragmentainer.NamedStrings)
                NamedStringsSnapshot[kvp.Key] = kvp.Value;
        }
        else
        {
            NamedStringsSnapshot = null;
        }

        var counters = layout.PeekCounters();
        if (counters is not null && counters.Count > 0)
        {
            CountersSnapshot ??= new Dictionary<string, int>(counters.Count);
            CountersSnapshot.Clear();
            foreach (var kvp in counters)
                CountersSnapshot[kvp.Key] = kvp.Value;
        }
        else
        {
            CountersSnapshot = null;
        }

        WritingMode = layout.WritingMode;
        IsRtl = layout.IsRtl;
        AvailableInlineSize = layout.AvailableInlineSize;
        AvailableBlockSize = layout.AvailableBlockSize;
        FloatManagerStateSnapshot = fragmentainer.FloatManagerState;
        FragmentOutputCursor = fragmentOutputCursor;
    }

    /// <summary>Per Phase 3 review fix #1 — restore every snapshot
    /// field to the corresponding live state. Inverse of
    /// <see cref="Capture"/>; the layouter calls this on rewind +
    /// then re-runs the failed attempt with whatever new strategy
    /// the optimizer suggested.</summary>
    public void RestoreInto(FragmentainerContext fragmentainer, ref LayoutContext layout)
    {
        fragmentainer.PageIndex = PageIndex;
        fragmentainer.UsedBlockSize = UsedBlockSize;
        fragmentainer.NamedStrings.Clear();
        if (NamedStringsSnapshot is not null)
        {
            foreach (var kvp in NamedStringsSnapshot)
                fragmentainer.NamedStrings[kvp.Key] = kvp.Value;
        }
        fragmentainer.FloatManagerState = FloatManagerStateSnapshot;

        layout.WritingMode = WritingMode;
        layout.IsRtl = IsRtl;
        layout.AvailableInlineSize = AvailableInlineSize;
        layout.AvailableBlockSize = AvailableBlockSize;
        layout.RestoreCounters(CountersSnapshot);
    }

    /// <summary>Reset the checkpoint to neutral state for re-rental.
    /// Called by <see cref="LayoutCheckpointPool.Return"/>. Per
    /// Phase 3 review fix #6 — large reference fields
    /// (<see cref="IncomingContinuation"/>, snapshot dictionaries,
    /// <see cref="FloatManagerStateSnapshot"/>) are nulled / cleared
    /// so the GC can reclaim referenced graphs while the checkpoint
    /// sits in the pool.</summary>
    internal void Reset()
    {
        PageIndex = 0;
        UsedBlockSize = 0;
        LastEmittedChildIndex = -1;
        IncomingContinuation = null;
        PageCounterValue = 0;
        NamedStringsSnapshot?.Clear();
        // Drop the dict reference too — the next Capture re-allocates
        // when it actually has data to store, so a long-idle checkpoint
        // doesn't pin an empty dict.
        NamedStringsSnapshot = null;
        CountersSnapshot?.Clear();
        CountersSnapshot = null;
        WritingMode = WritingMode.HorizontalTb;
        IsRtl = false;
        AvailableInlineSize = 0;
        AvailableBlockSize = 0;
        FloatManagerStateSnapshot = null;
        FragmentOutputCursor = 0;
    }
}

/// <summary>
/// Process-wide pool of <see cref="LayoutCheckpoint"/> instances. Same
/// pattern as the Phase 2 <c>ComputedStyle</c> pool: <see cref="ConcurrentBag{T}"/>
/// with a soft cap so high-churn rendering doesn't consume unbounded
/// memory.
///
/// <para>Per Phase 3 review fix #6 — <see cref="Return"/> guards
/// against double-return aliasing: a stamped sentinel + a reference
/// hash check prevents the same instance from being added twice +
/// rented concurrently by two callers (which would let them step on
/// each other's state).</para>
/// </summary>
internal static class LayoutCheckpointPool
{
    private const int MaxPoolSize = 64;

    private static readonly ConcurrentBag<LayoutCheckpoint> _pool = new();

    /// <summary>Per Phase 3 review fix #6 — set of instances currently
    /// in the pool, keyed by reference identity. Used to reject
    /// double-Return calls before the second add corrupts the pool.</summary>
    private static readonly ConcurrentDictionary<LayoutCheckpoint, byte> _inPool = new();

    /// <summary>Rent a checkpoint; reset to neutral on the way out.</summary>
    public static LayoutCheckpoint Rent()
    {
        if (_pool.TryTake(out var cp))
        {
            _inPool.TryRemove(cp, out _);
            cp.Reset();
            return cp;
        }
        return new LayoutCheckpoint();
    }

    /// <summary>Return a checkpoint to the pool. Drops on the floor
    /// when:
    /// <list type="bullet">
    ///   <item><paramref name="cp"/> is null (idempotent).</item>
    ///   <item>The pool is at capacity (GC reclaims the instance).</item>
    ///   <item>The instance is already in the pool (double-return —
    ///   per Phase 3 review fix #6, accepting the duplicate would
    ///   alias two rentals of the same backing instance + corrupt
    ///   their state).</item>
    /// </list>
    /// </summary>
    public static void Return(LayoutCheckpoint? cp)
    {
        if (cp is null) return;
        if (_pool.Count >= MaxPoolSize) return;
        // Reject double-return — accepting would let two concurrent
        // Rent() calls hand out the same instance to different callers.
        if (!_inPool.TryAdd(cp, 0)) return;
        // Clear large refs BEFORE the instance becomes pool-eligible.
        // A subsequent Rent will Reset() it again, but the early clear
        // ensures GC can reclaim large referenced graphs while the
        // checkpoint sits idle.
        cp.Reset();
        _pool.Add(cp);
    }
}
