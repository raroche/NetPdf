// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using NetPdf.Layout.Boxes;

namespace NetPdf.Layout.Layouters;

/// <summary>Non-block-pagination arc (flex item content + grid content-sized
/// rows) — a fragment sink that BUFFERS the fragments a nested
/// <see cref="BlockLayouter"/> emits (in INNER, content-box-relative
/// coordinates) so the caller can (a) read the content's block / inline
/// extent for sizing an indefinite box, and (b) re-emit the content at the
/// box's FINAL position once that position is known (e.g. after a flex
/// item-split re-anchors the item, or after grid row heights settle).
///
/// <para>Mirrors <see cref="TableLayouter.MeasuringFragmentSink"/>'s
/// measure-then-flush role but keeps the buffered fragments in their raw
/// INNER coordinates and applies the FULL translation at
/// <see cref="FlushTo"/> time (rather than baking a partial translation at
/// <see cref="Emit"/> time). That suits callers whose final origin is not
/// known until after measurement: the flex item-content pass measures every
/// item up front (to content-size auto-height column items) but only learns
/// each item's re-anchored page offset during the emission loop.</para>
///
/// <para>Extracted as a shared sink (rule-of-three: the table cell sink, the
/// grid item translating sink, and this) rather than reusing
/// <see cref="TableLayouter.MeasuringFragmentSink"/> — that one is a private
/// nested type with a table-specific split-translation contract; the existing
/// table / grid measurement code is deliberately left untouched to keep the
/// regression surface small.</para></summary>
internal sealed class BufferingMeasureSink : IBlockFragmentSink
{
    private readonly List<BoxFragment> _buffered = new();

    /// <summary>The box whose content this sink buffers. A buffered fragment
    /// whose <see cref="BoxFragment.Box"/> IS this box (= the inline-only-root
    /// content fragment a flex / grid item emits for its own text) is marked
    /// <see cref="BoxFragment.SuppressBoxDecoration"/> so it paints text only —
    /// the item's flex / grid GEOMETRY fragment already paints the box
    /// decoration. Null = no marking (the fragment's box never matches).</summary>
    private readonly Box? _decorationOwner;

    public BufferingMeasureSink(Box? decorationOwner = null)
    {
        _decorationOwner = decorationOwner;
    }

    /// <summary>Maximum block extent (<c>BlockOffset + BlockSize</c>) observed
    /// across buffered fragments, in the inner content-box coordinate space
    /// (= relative to the content-box block-start, NOT the fragmentainer).
    /// Drives the auto-height main-size of an indefinite box.</summary>
    public double ContentBlockExtent { get; private set; }

    /// <summary>Maximum inline extent observed across buffered fragments, in
    /// the inner content-box coordinate space. For inline-only fragments
    /// (shaped text) this is the widest LINE advance — the ACTUAL text width,
    /// not the wrapper's available width — so a caller can derive a
    /// max-content-ish inline contribution. Mirrors
    /// <see cref="TableLayouter.MeasuringFragmentSink.MaxInlineExtentFromCellOrigin"/>.</summary>
    public double ContentInlineExtent { get; private set; }

    /// <summary>Count of buffered fragments — the rollback / checkpoint
    /// cursor for the inner <see cref="BlockLayouter"/>.</summary>
    public int Cursor => _buffered.Count;

    public void Emit(BoxFragment fragment)
    {
        // Out-of-flow (position: absolute / fixed) descendants of a flex / grid
        // item are emitted + anchored by the OUTER layout pass (flex is NOT a
        // delegation boundary for abspos — the outer walk reaches them), so the
        // nested content measure must NOT buffer them: re-emitting on FlushTo
        // would DOUBLE the box (anchored to the wrong containing block), and
        // counting it would wrongly inflate the in-flow content extent. Drop it.
        // NB: dropping means Cursor (= _buffered.Count) advances only for
        // BUFFERED (in-flow) fragments — safe because the inner BlockLayouter's
        // abspos / fixed passes run AFTER the in-flow child loop that captures
        // cursors + issues UpdateFragmentBlockSize, so no in-flow cursor target
        // is shifted by a skipped out-of-flow emission.
        if (fragment.Box.Style.IsOutOfFlow()) return;

        // The inline-only-root content fragment (box == the item) paints text
        // only — its decoration is the item's flex / grid geometry fragment's
        // job. Block-CHILD fragments (box != the item) keep their own decoration.
        if (_decorationOwner is not null && ReferenceEquals(fragment.Box, _decorationOwner))
        {
            fragment = fragment with { SuppressBoxDecoration = true };
        }

        var innerBottom = fragment.BlockOffset + fragment.BlockSize;
        if (innerBottom > ContentBlockExtent)
        {
            ContentBlockExtent = innerBottom;
        }

        // Inline extent: prefer the widest shaped LINE advance for inline-only
        // fragments (the natural text width), else the fragment's border-box
        // inline right edge. Mirrors the table cell sink's two-path tracker.
        if (fragment.InlineLayout is { } inlineLayout && inlineLayout.Lines.Length > 0)
        {
            var lines = inlineLayout.Lines;
            for (var i = 0; i < lines.Length; i++)
            {
                var lineRight = fragment.InlineOffset + lines[i].TotalAdvance;
                if (lineRight > ContentInlineExtent)
                {
                    ContentInlineExtent = lineRight;
                }
            }
        }
        else
        {
            var innerRight = fragment.InlineOffset + fragment.InlineSize;
            if (innerRight > ContentInlineExtent)
            {
                ContentInlineExtent = innerRight;
            }
        }

        _buffered.Add(fragment);
    }

    public void RollbackTo(int cursor)
    {
        if (cursor < 0 || cursor > _buffered.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(cursor),
                $"BufferingMeasureSink.RollbackTo: cursor {cursor} out of range "
                + $"[0, {_buffered.Count}].");
        }
        if (cursor < _buffered.Count)
        {
            _buffered.RemoveRange(cursor, _buffered.Count - cursor);
        }
        // Per the table sink's documented approximation — leave the extent
        // trackers stale (over-estimated) after a partial rollback rather than
        // re-scanning; the inner LastResort strategy suppresses rewinds so this
        // path isn't reached for the measure pass.
    }

    public void UpdateFragmentBlockSize(int cursor, double newBlockSize)
    {
        if (cursor < 0 || cursor >= _buffered.Count) return;
        var existing = _buffered[cursor];
        _buffered[cursor] = existing with { BlockSize = newBlockSize };
    }

    /// <summary>Re-emit every buffered fragment into <paramref name="target"/>,
    /// translated by (<paramref name="inlineTranslation"/>,
    /// <paramref name="blockTranslation"/>) — the box's FINAL content-box
    /// origin in the outer fragmentainer's coordinate space. Clears the buffer
    /// (content commits once). The inner fragments carry offsets relative to
    /// the content box's origin (0,0), so adding the final origin yields
    /// absolute fragmentainer coordinates.</summary>
    public void FlushTo(IBlockFragmentSink target, double inlineTranslation, double blockTranslation)
    {
        ArgumentNullException.ThrowIfNull(target);
        for (var i = 0; i < _buffered.Count; i++)
        {
            var f = _buffered[i];
            target.Emit(f with
            {
                InlineOffset = f.InlineOffset + inlineTranslation,
                BlockOffset = f.BlockOffset + blockTranslation,
            });
        }
        _buffered.Clear();
    }

    /// <summary>Row-nowrap intra-item content pagination — flush only the
    /// buffered fragments whose CROSS-AXIS (block) top falls within the page
    /// window <c>[<paramref name="windowFrom"/>, <paramref name="windowTo"/>)</c>
    /// (in the flex container's content-cross coordinate space), re-anchored so
    /// the window top maps to the container content-box cross-start
    /// (<paramref name="contentCrossOriginAbs"/>) on this page. A
    /// <c>flex-direction: row</c> / <c>nowrap</c> line taller than the page
    /// splits at this SHARED cross cut: every item re-emits its fragments that
    /// fall in this page's window, so all items continue at the same cross
    /// position (the page top) on the next page.
    ///
    /// <para>Partition-by-top (child-boundary granularity): a fragment whose top
    /// is in the window emits HERE — even if it overruns <paramref name="windowTo"/>
    /// (force-overflow, like the block layouter's over-tall child); a fragment
    /// whose top is &gt;= <paramref name="windowTo"/> defers (sets the returned
    /// <c>AnyRemaining</c>). This guarantees each fragment emits on exactly one
    /// page — no loss, no double — with a single accumulated cut. Intra-fragment
    /// splitting (a single line / block taller than the page) is deferred.</para>
    ///
    /// <para>Does NOT clear the buffer: the row-nowrap path builds a FRESH
    /// measure sink per page (the layouter re-measures), so each page slices its
    /// own buffer. Returns the emitted cross extent RELATIVE to the window top
    /// (for the wrapper resize + <c>LastEmittedBlockExtent</c>) and whether any
    /// fragment remains beyond this page.</para>
    ///
    /// <para><c>itemCrossOffsetAbs</c> is the item content-box cross-start in
    /// fragmentainer coordinates (= the item's <c>blockOffset</c> for row);
    /// <c>contentCrossOriginAbs</c> is the flex container's content-box
    /// cross-start in fragmentainer coordinates.</para></summary>
    public (double EmittedCrossExtent, bool AnyRemaining) FlushRangeTo(
        IBlockFragmentSink target,
        double inlineTranslation,
        double itemCrossOffsetAbs,
        double contentCrossOriginAbs,
        double windowFrom,
        double windowTo)
    {
        ArgumentNullException.ThrowIfNull(target);
        // The item content-box cross-start expressed in the container's
        // content-cross coordinate space (where the shared cut accumulates).
        var crossBase = itemCrossOffsetAbs - contentCrossOriginAbs;
        var emittedExtent = 0.0;
        var anyRemaining = false;
        for (var i = 0; i < _buffered.Count; i++)
        {
            var f = _buffered[i];
            var lineCrossTop = crossBase + f.BlockOffset;
            if (lineCrossTop >= windowTo)
            {
                anyRemaining = true;
                continue;       // starts on a later page
            }
            if (lineCrossTop < windowFrom)
            {
                continue;       // already emitted on a prior page
            }
            var relTop = lineCrossTop - windowFrom;
            target.Emit(f with
            {
                InlineOffset = f.InlineOffset + inlineTranslation,
                BlockOffset = contentCrossOriginAbs + relTop,
            });
            var bottom = relTop + f.BlockSize;
            if (bottom > emittedExtent)
            {
                emittedExtent = bottom;
            }
        }
        return (emittedExtent, anyRemaining);
    }
}
