// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Paginate;

namespace NetPdf.Layout.Layouters;

/// <summary>
/// Per Phase 3 Task 7 — the sink that <see cref="BlockLayouter"/>
/// emits its <see cref="BoxFragment"/>s into. Implementations are
/// the layout-pipeline glue between the layouter and Phase 4's
/// painter; in tests, recording sinks capture every emitted
/// fragment for inspection.
///
/// <para>Per Phase 3 Task 5 PR #21 review fix #3 — the sink also
/// implements <see cref="IFragmentSink"/> so the
/// <see cref="LayoutRetryCoordinator"/> can roll back fragments
/// emitted past a checkpoint's
/// <c>FragmentOutputCursor</c> on rewind. Layouters that emit only
/// on the final accepted attempt can use a sink that ignores
/// rollback (since there's nothing to roll back), but the more
/// common case — incremental emission as the layouter walks the
/// tree — uses the rollback hook to discard fragments from a
/// rejected attempt.</para>
/// </summary>
internal interface IBlockFragmentSink : IFragmentSink
{
    /// <summary>Emit one <paramref name="fragment"/>. The current
    /// position into the sink's internal list is the
    /// <c>FragmentOutputCursor</c> the layouter passes to
    /// <see cref="LayoutCheckpoint.Capture"/>; on rewind the
    /// coordinator calls <see cref="IFragmentSink.RollbackTo"/>
    /// with that cursor to truncate any emissions past it.</summary>
    void Emit(BoxFragment fragment);

    /// <summary>The current cursor into the emitted-fragment list.
    /// Captured by the layouter into
    /// <see cref="LayoutCheckpoint.FragmentOutputCursor"/> at
    /// candidate-break checkpoints; <see cref="IFragmentSink.RollbackTo"/>
    /// truncates back to this value on rewind.</summary>
    int Cursor { get; }

    /// <summary>Per Phase 3 Task 17 cycle 5c.2b F2 — retroactively
    /// resize a previously emitted wrapper fragment's
    /// <see cref="BoxFragment.BlockSize"/>. Used by BlockLayouter
    /// after a paginatable-grid / paginatable-flex dispatch returns
    /// to shrink the wrapper from the clamped page-budget extent
    /// down to the actual emitted-content extent — without this,
    /// a wrapper clamped to 250 but with content only 200 paints
    /// 50px of empty space + over-advances the cursor (= sibling
    /// displacement).
    ///
    /// <para><b>Z-order constraint preserved.</b> The wrapper
    /// fragment was already emitted BEFORE the inner layouter's
    /// item fragments (mirrors painter draw order). Mutating
    /// <c>BlockSize</c> in place doesn't change list position; the
    /// wrapper continues to paint first + its background / borders
    /// stay correct.</para>
    ///
    /// <para><b>Contract.</b> <paramref name="cursor"/> must be a
    /// valid index (= 0 ≤ cursor &lt; <see cref="Cursor"/>) when
    /// called. Implementations replace the fragment at that index
    /// with a copy whose <see cref="BoxFragment.BlockSize"/> equals
    /// <paramref name="newBlockSize"/>; other fields (Box,
    /// InlineOffset, BlockOffset, InlineSize, InlineLayout) are
    /// preserved. <paramref name="newBlockSize"/> must be finite +
    /// non-negative; the caller validates (= mirrors the
    /// <c>FlexContinuation.EmittedBlockExtent</c> /
    /// <c>GridContinuation.EmittedBlockExtent</c> constructor
    /// validation). Implementations that don't store fragments
    /// (= measure-only sinks) may treat this as a no-op since they
    /// don't paint anything for the wrapper resize to fix.</para>
    ///
    /// <para>Cycle 5c.2b ships the GRID consumer; cycle 4f
    /// previously deferred the FLEX consumer pending this same API
    /// (see <c>docs/deferrals.md</c>
    /// <c>flex-wrapper-resize-consumer-deferral</c>). Both
    /// consumers share this method.</para></summary>
    void UpdateFragmentBlockSize(int cursor, double newBlockSize);
}
