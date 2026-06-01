// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Layout.Layouters;

namespace NetPdf.Rendering;

/// <summary>
/// The production <see cref="IBlockFragmentSink"/> the facade drives a
/// <see cref="BlockLayouter"/> into — accumulates every emitted
/// <see cref="BoxFragment"/> in document order so the paint stage can walk
/// them. Supports the rewind + retroactive-resize hooks the layouter's
/// retry / pagination machinery requires.
/// </summary>
/// <remarks>
/// Mirrors the recording sink the layout integration tests use; promoted to a
/// shared production type now that the facade lays out for real. A single sink
/// backs one page's layout pass — the multi-page driver (a tracked
/// <c>deferrals.md#layout-to-pdf-pipeline</c> follow-up) will allocate a fresh
/// sink per fragmentainer.
/// </remarks>
internal sealed class ListFragmentSink : IBlockFragmentSink
{
    private readonly List<BoxFragment> _fragments = new();

    /// <summary>The fragments emitted so far, in emission (paint) order.</summary>
    public IReadOnlyList<BoxFragment> Fragments => _fragments;

    /// <inheritdoc />
    public int Cursor => _fragments.Count;

    /// <inheritdoc />
    public void Emit(BoxFragment fragment) => _fragments.Add(fragment);

    /// <inheritdoc />
    public void RollbackTo(int cursor)
    {
        if (cursor >= 0 && cursor < _fragments.Count)
            _fragments.RemoveRange(cursor, _fragments.Count - cursor);
    }

    /// <inheritdoc />
    public void UpdateFragmentBlockSize(int cursor, double newBlockSize)
    {
        if (cursor >= 0 && cursor < _fragments.Count)
            _fragments[cursor] = _fragments[cursor] with { BlockSize = newBlockSize };
    }
}
