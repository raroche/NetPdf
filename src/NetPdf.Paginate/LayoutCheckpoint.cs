// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

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
///   <item>Per Phase 3 Task 4 review fix #6 —
///   <see cref="CapturedFragmentainerRef"/> snapshots
///   <see cref="LayoutContext.Fragmentainer"/> at capture time so a
///   speculative attempt that swapped to a cloned fragmentainer
///   (e.g., laying out into <c>ctx2 = ctx1.Clone()</c> to test the
///   next-page hypothesis) is reseated back to the original on
///   rewind. Without this, <c>RestoreInto</c> would restore mutable
///   field values to whatever fragmentainer happens to be active at
///   restore time, but <c>layout.Fragmentainer</c> would still point
///   at the speculative clone — leaving subsequent layout reading
///   the wrong content-area dimensions.</item>
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

    /// <summary>Per Phase 3 Task 4 review fix #6 — reference snapshot
    /// of <see cref="LayoutContext.Fragmentainer"/> at capture time.
    /// <see cref="RestoreInto"/> reseats <c>layout.Fragmentainer</c>
    /// to this value so a speculative attempt that swapped the
    /// fragmentainer (laying out into a cloned context to test a
    /// next-page hypothesis) is undone on rewind. Distinct from the
    /// <c>fragmentainer</c> parameter passed to
    /// <see cref="Capture"/> / <see cref="RestoreInto"/> — that
    /// parameter is the <i>currently-active</i> fragmentainer; this
    /// field is the one <c>layout.Fragmentainer</c> referenced when
    /// the checkpoint was taken. In normal use they are the same;
    /// they diverge only during speculative swap.</summary>
    public FragmentainerContext? CapturedFragmentainerRef;

    /// <summary>Per Phase 3 review fix #1 — opaque snapshot of the
    /// float manager state (Phase 3 Task 8 fills this in).</summary>
    public object? FloatManagerStateSnapshot;

    /// <summary>Per Phase 3 review fix #1 — index into the
    /// in-progress fragment list for the current page. On rewind,
    /// fragments emitted past this cursor are discarded so the
    /// re-attempt produces a consistent fragment sequence.</summary>
    public int FragmentOutputCursor;

    /// <summary>Per post-Task-7 review (recommendation P2 #5) — the
    /// adjacent-margin-collapse frontier captured at this checkpoint
    /// so a rewind to THIS specific checkpoint restores the correct
    /// previous-block bottom margin (not the layouter's latest state,
    /// which may correspond to a different — older or newer —
    /// candidate-break boundary).
    ///
    /// <para>Cycle 2's PR #26 fix #3 stored the frontier in layouter-
    /// private fields, populated only on the rewind branch. That works
    /// for the current resolver (always rewinds to the most-recent
    /// checkpoint) but breaks once the resolver retains multiple
    /// checkpoints + chooses a non-most-recent one (a future
    /// optimizer-aware path). Storing the frontier on the checkpoint
    /// itself decouples the layouter's "current" state from the
    /// "rewind-target" state.</para>
    ///
    /// <para>Layouters that don't model adjacent-margin collapse
    /// (inline / table / flex / grid) leave both fields at their
    /// zero defaults; <see cref="HasAdjoiningBlockOnEntry"/> stays
    /// <see langword="false"/>, signaling "no collapse state to
    /// restore". The fields are public + named generically so other
    /// future block-flow layouters (nested BlockLayouter, etc.) can
    /// reuse the slot.</para></summary>
    public double PrevBlockMarginEnd;

    /// <summary>Per post-Task-7 review (P2 #5) — companion flag to
    /// <see cref="PrevBlockMarginEnd"/>. <see langword="true"/> when
    /// an adjacent block sibling was emitted before this checkpoint
    /// (so the next block's top margin should collapse with
    /// <see cref="PrevBlockMarginEnd"/>); <see langword="false"/>
    /// when this is the first block on the page or a non-block
    /// child broke adjacency.</summary>
    public bool HasAdjoiningBlockOnEntry;

    /// <summary>Per Phase 3 Task 4 review fix #7 — lease token stamped
    /// at <see cref="LayoutCheckpointPool.Rent"/> time + cleared by
    /// <see cref="LayoutCheckpointPool.Return"/> via atomic CAS. A
    /// non-zero value indicates "currently rented under this lease";
    /// 0 indicates "in pool / available". Stale-after-rerent returns
    /// (a caller holds a reference past their own Return + the
    /// instance has been re-rented under a new lease) are rejected
    /// because the caller's saved token won't match the new value.
    /// <see langword="long"/> + <see cref="Interlocked.CompareExchange(ref long, long, long)"/>
    /// chosen so the read-compare-write is atomic; without that, two
    /// concurrent Returns of the same checkpoint could both succeed
    /// + add the instance to the pool twice.</summary>
    internal long _leaseToken;

    /// <summary>Per Phase 3 review fix #1 — capture all rewindable
    /// state in one atomic operation. Layouters call this at
    /// candidate-break points BEFORE attempting the speculative
    /// layout that might be rolled back. Pass the live
    /// <see cref="FragmentainerContext"/> + <see cref="LayoutContext"/>
    /// + the fragment-list cursor; this populates every snapshot
    /// field.
    ///
    /// <para>Per Phase 3 Task 4 review fix #6 — also snapshots
    /// <c>layout.Fragmentainer</c> into <see cref="CapturedFragmentainerRef"/>.
    /// In typical use this equals the <paramref name="fragmentainer"/>
    /// parameter, but during speculative layout the two can diverge.
    /// <see cref="RestoreInto"/> reseats <c>layout.Fragmentainer</c>
    /// to the captured ref so the swap is undone on rewind.</para></summary>
    public void Capture(
        FragmentainerContext fragmentainer,
        in LayoutContext layout,
        int fragmentOutputCursor,
        int lastEmittedChildIndex,
        LayoutContinuation? incomingContinuation,
        int pageCounterValue,
        double prevBlockMarginEnd = 0,
        bool hasAdjoiningBlockOnEntry = false)
    {
        PageIndex = fragmentainer.PageIndex;
        UsedBlockSize = fragmentainer.UsedBlockSize;
        LastEmittedChildIndex = lastEmittedChildIndex;
        IncomingContinuation = incomingContinuation;
        PageCounterValue = pageCounterValue;
        // Per post-Task-7 review (P2 #5) — capture the margin-collapse
        // frontier so a rewind to THIS checkpoint restores the right
        // prior-block bottom margin.
        PrevBlockMarginEnd = prevBlockMarginEnd;
        HasAdjoiningBlockOnEntry = hasAdjoiningBlockOnEntry;

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
        // Per Phase 3 Task 4 review fix #6 — capture layout.Fragmentainer
        // so a speculative swap can be undone.
        CapturedFragmentainerRef = layout.Fragmentainer;
        FloatManagerStateSnapshot = fragmentainer.FloatManagerState;
        FragmentOutputCursor = fragmentOutputCursor;
    }

    /// <summary>Per Phase 3 review fix #1 — restore every snapshot
    /// field to the corresponding live state. Inverse of
    /// <see cref="Capture"/>; the layouter calls this on rewind +
    /// then re-runs the failed attempt with whatever new strategy
    /// the optimizer suggested.
    ///
    /// <para>Per Phase 3 Task 4 review fix #6 — when
    /// <see cref="CapturedFragmentainerRef"/> is non-<see langword="null"/>,
    /// the captured state is restored TO that fragmentainer (not the
    /// <paramref name="fragmentainer"/> parameter), and
    /// <c>layout.Fragmentainer</c> is reseated to point back at it.
    /// This undoes any speculative swap. The
    /// <paramref name="fragmentainer"/> parameter is treated as a
    /// fallback for checkpoints that pre-date the capture-fragmentainer
    /// fix (defensively — should not occur in production).</para></summary>
    public void RestoreInto(FragmentainerContext fragmentainer, ref LayoutContext layout)
    {
        // Per Phase 3 Task 4 review fix #6 — prefer the captured
        // fragmentainer ref so a speculative swap undoes correctly.
        var target = CapturedFragmentainerRef ?? fragmentainer;

        target.PageIndex = PageIndex;
        target.UsedBlockSize = UsedBlockSize;
        target.NamedStrings.Clear();
        if (NamedStringsSnapshot is not null)
        {
            foreach (var kvp in NamedStringsSnapshot)
                target.NamedStrings[kvp.Key] = kvp.Value;
        }
        target.FloatManagerState = FloatManagerStateSnapshot;

        layout.WritingMode = WritingMode;
        layout.IsRtl = IsRtl;
        layout.AvailableInlineSize = AvailableInlineSize;
        layout.AvailableBlockSize = AvailableBlockSize;
        // Reseat layout.Fragmentainer to the captured one so a
        // speculative swap is undone.
        layout.Fragmentainer = target;
        layout.RestoreCounters(CountersSnapshot);
    }

    /// <summary>Reset the checkpoint to neutral state for re-rental.
    /// Called by <see cref="LayoutCheckpointPool.Return"/>. Per
    /// Phase 3 review fix #6 — large reference fields
    /// (<see cref="IncomingContinuation"/>, snapshot dictionaries,
    /// <see cref="FloatManagerStateSnapshot"/>) are nulled / cleared
    /// so the GC can reclaim referenced graphs while the checkpoint
    /// sits in the pool.
    ///
    /// <para>Per Phase 3 Task 4 review fix #7 — does NOT touch
    /// <see cref="_leaseToken"/>. Lease-token lifecycle is owned by
    /// <see cref="LayoutCheckpointPool.Rent"/> +
    /// <see cref="LayoutCheckpointPool.Return"/> via atomic CAS;
    /// having Reset clear it would race against concurrent Return
    /// calls.</para></summary>
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
        CapturedFragmentainerRef = null;
        FloatManagerStateSnapshot = null;
        FragmentOutputCursor = 0;
        // Per post-Task-7 review (P2 #5) — clear margin-collapse
        // frontier on return-to-pool so a stale value doesn't leak
        // into a fresh rent.
        PrevBlockMarginEnd = 0;
        HasAdjoiningBlockOnEntry = false;
        // Per fix #7 — _leaseToken intentionally NOT touched here.
    }
}

/// <summary>
/// Per Phase 3 Task 4 review fix #7 — typed lease handle for a
/// <see cref="LayoutCheckpoint"/> rented from
/// <see cref="LayoutCheckpointPool"/>. Wraps the checkpoint reference
/// + the unique <see cref="LayoutCheckpoint._leaseToken"/> stamped at
/// rent time; <see cref="Return"/> presents the token back to the
/// pool, which uses an atomic CAS to reject:
/// <list type="bullet">
///   <item><b>Immediate double-return.</b> A caller calling
///   <see cref="Return"/> twice — the second call's token won't match
///   (the first call cleared the checkpoint's stored token).</item>
///   <item><b>Stale-after-rerent.</b> A caller holding a reference
///   past their own Return + the checkpoint has been re-rented to a
///   different caller. The stale lease's token won't match the new
///   token stamped by the second rent, so a stale Return is a no-op.</item>
/// </list>
///
/// <para>The pre-fix design used a <c>ConcurrentDictionary&lt;LayoutCheckpoint, byte&gt; _inPool</c>
/// to detect immediate double-return only — it could not distinguish
/// "this checkpoint is in the pool" from "this checkpoint was rented
/// + has now been re-rented under a different lease". The lease-token
/// design subsumes both cases, so the in-pool dictionary is dropped
/// in favor of a single atomic counter + CAS check.</para>
///
/// <para><b>Disposal.</b> Implements <see cref="IDisposable"/> so
/// <c>using var lease = LayoutCheckpointPool.Rent();</c> is a valid
/// scope. <c>Dispose</c> is equivalent to <see cref="Return"/>;
/// double-dispose is a no-op (CAS rejects the second Return).</para>
/// </summary>
internal readonly struct CheckpointLease : IDisposable
{
    /// <summary>The leased <see cref="LayoutCheckpoint"/> instance.
    /// <see langword="null"/> only on the default-struct value
    /// (which represents "no lease held"). Production code receives
    /// non-null leases from <see cref="LayoutCheckpointPool.Rent"/>.</summary>
    public LayoutCheckpoint? Checkpoint { get; }

    /// <summary>The unique lease token stamped at rent time.
    /// 0 on the default-struct value. Internal — callers don't read
    /// the token directly; <see cref="Return"/> handles the
    /// presentation.</summary>
    internal long Token { get; }

    internal CheckpointLease(LayoutCheckpoint checkpoint, long token)
    {
        Checkpoint = checkpoint;
        Token = token;
    }

    /// <summary>Return the leased checkpoint to the pool.
    /// Idempotent — double-Return is rejected via the lease-token CAS.
    /// No-op when this is a default-struct value (no lease held).</summary>
    public void Return() => LayoutCheckpointPool.Return(this);

    /// <summary>Equivalent to <see cref="Return"/>; supports
    /// <c>using</c>-block syntax.</summary>
    public void Dispose() => Return();
}

/// <summary>
/// Process-wide pool of <see cref="LayoutCheckpoint"/> instances. Same
/// pattern as the Phase 2 <c>ComputedStyle</c> pool: <see cref="ConcurrentBag{T}"/>
/// with a soft cap so high-churn rendering doesn't consume unbounded
/// memory.
///
/// <para>Per Phase 3 Task 4 review fix #7 — <see cref="Rent"/> stamps
/// a unique lease token onto each checkpoint; <see cref="Return"/>
/// uses an atomic CAS on that token to reject double-return AND
/// stale-after-rerent. The pre-fix design (a separate
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> tracking "what's
/// in the pool") only caught immediate double-return; lease tokens
/// subsume both cases with a single atomic field.</para>
/// </summary>
internal static class LayoutCheckpointPool
{
    private const int MaxPoolSize = 64;

    private static readonly ConcurrentBag<LayoutCheckpoint> _pool = new();

    /// <summary>Per Phase 3 Task 4 review fix #7 — monotonically
    /// increasing counter. Each <see cref="Rent"/> increments + stamps
    /// the new value onto the rented checkpoint. <see cref="Interlocked.Increment(ref long)"/>
    /// guarantees uniqueness across threads. <see cref="long"/> chosen
    /// over <see cref="int"/> so wraparound is not a practical concern
    /// (would require ~9.2 × 10^18 rents).</summary>
    private static long _nextLeaseToken;

    /// <summary>Rent a checkpoint; reset to neutral on the way out.
    /// Returns a <see cref="CheckpointLease"/> that wraps the
    /// checkpoint + the lease token; the caller must call
    /// <see cref="CheckpointLease.Return"/> (or use a <c>using</c>
    /// block) when finished. Concurrency-safe: the bag's
    /// <see cref="ConcurrentBag{T}.TryTake"/> is atomic, and
    /// the lease-token stamp uses
    /// <see cref="Interlocked.Increment(ref long)"/> so two concurrent
    /// rents always get distinct tokens even if they receive
    /// freshly-allocated instances.</summary>
    public static CheckpointLease Rent()
    {
        var token = Interlocked.Increment(ref _nextLeaseToken);
        if (_pool.TryTake(out var cp))
        {
            cp.Reset();
            cp._leaseToken = token;
            return new CheckpointLease(cp, token);
        }
        var fresh = new LayoutCheckpoint { _leaseToken = token };
        return new CheckpointLease(fresh, token);
    }

    /// <summary>Return a leased checkpoint to the pool. Drops on the
    /// floor when:
    /// <list type="bullet">
    ///   <item>The lease's <see cref="CheckpointLease.Checkpoint"/> is
    ///   <see langword="null"/> (default-struct value).</item>
    ///   <item>The lease's token doesn't match the checkpoint's
    ///   currently-stamped token (immediate double-return OR
    ///   stale-after-rerent — per Phase 3 Task 4 review fix #7).</item>
    ///   <item>The pool is at capacity (GC reclaims the instance;
    ///   the token IS still cleared so subsequent stale Returns
    ///   correctly no-op).</item>
    /// </list>
    ///
    /// <para>The atomic CAS on <see cref="LayoutCheckpoint._leaseToken"/>
    /// is the linearization point — exactly one Return wins ownership
    /// of zeroing the token, even when multiple threads concurrently
    /// hold the same (valid) lease. Without the CAS, two threads with
    /// the same lease could both clear the token + both add the
    /// checkpoint to the bag, causing a future Rent to hand out the
    /// same instance to two different callers.</para>
    /// </summary>
    public static void Return(CheckpointLease lease)
    {
        var cp = lease.Checkpoint;
        if (cp is null) return;
        var expected = lease.Token;

        // Atomic CAS: only the holder of the matching token can clear
        // it. A stale lease (token != current) returns the current
        // value (which !=expected), so the if-branch rejects without
        // mutation.
        if (Interlocked.CompareExchange(ref cp._leaseToken, 0, expected) != expected)
        {
            return;
        }

        // From here, this thread has exclusive ownership of pooling cp.
        if (_pool.Count >= MaxPoolSize)
        {
            // Pool full — discard. cp's _leaseToken is already 0
            // (the CAS succeeded), so any future stale Return is a
            // no-op + cp is GC-eligible.
            return;
        }

        cp.Reset();
        _pool.Add(cp);
    }
}
