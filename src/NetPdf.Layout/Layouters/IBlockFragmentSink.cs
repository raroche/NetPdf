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
}
