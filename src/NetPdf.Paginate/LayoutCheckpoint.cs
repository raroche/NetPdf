// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Concurrent;

namespace NetPdf.Paginate;

/// <summary>
/// Per Phase 3 plan — snapshot of <see cref="FragmentainerContext"/> +
/// the layout-position state needed to rewind when
/// <see cref="IBreakResolver.ConsiderBreakAt"/> returns
/// <see cref="BreakAction.Rewind"/>. Mutable (re-used across the
/// re-layout loop) but pooled via <see cref="LayoutCheckpointPool"/>
/// so the bounded retry loop doesn't allocate per attempt.
///
/// <para>Lifetime: rented by the bounded DP optimizer / re-layout loop
/// at the start of each layout attempt; returned to the pool when the
/// attempt commits or when its checkpoint is no longer reachable
/// (i.e., we've passed it on the optimizer's frontier).</para>
///
/// <para><b>What's saved.</b> The fields below cover the state that
/// changes during one fragmentainer's worth of layout. Things that
/// don't change (immutable box-tree references, the source HTML, the
/// computed cascade) are NOT saved — re-reading them on rewind is
/// free.</para>
/// </summary>
internal sealed class LayoutCheckpoint
{
    /// <summary>Page index this checkpoint was taken on. Rewinds within
    /// the same page only — cross-page rewinds aren't supported (the
    /// previous page's bytes have already been written).</summary>
    public int PageIndex;

    /// <summary>Y position (CSS px from top of the content rect) where
    /// the next layout call should resume.</summary>
    public double UsedHeight;

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

    /// <summary>Reset the checkpoint to neutral state for re-rental.
    /// Called by <see cref="LayoutCheckpointPool.Return"/>.</summary>
    internal void Reset()
    {
        PageIndex = 0;
        UsedHeight = 0;
        LastEmittedChildIndex = -1;
        IncomingContinuation = null;
        PageCounterValue = 0;
    }
}

/// <summary>
/// Process-wide pool of <see cref="LayoutCheckpoint"/> instances. Same
/// pattern as the Phase 2 <c>ComputedStyle</c> pool: <see cref="ConcurrentBag{T}"/>
/// with a soft cap so high-churn rendering doesn't consume unbounded
/// memory. Reset-on-rent (not on-return) so the rent path is the only
/// place that touches the inner state — defensive against a caller
/// that forgets to return.
/// </summary>
internal static class LayoutCheckpointPool
{
    private const int MaxPoolSize = 64;

    private static readonly ConcurrentBag<LayoutCheckpoint> _pool = new();

    /// <summary>Rent a checkpoint; reset to neutral on the way out.</summary>
    public static LayoutCheckpoint Rent()
    {
        if (_pool.TryTake(out var cp))
        {
            cp.Reset();
            return cp;
        }
        return new LayoutCheckpoint();
    }

    /// <summary>Return a checkpoint to the pool. Drops on the floor
    /// when the pool is at capacity (GC reclaims the instance).
    /// Idempotent — caller-side null + double-return are safe.</summary>
    public static void Return(LayoutCheckpoint? cp)
    {
        if (cp is null) return;
        if (_pool.Count >= MaxPoolSize) return;
        _pool.Add(cp);
    }
}
