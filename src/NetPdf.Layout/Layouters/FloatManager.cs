// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;

namespace NetPdf.Layout.Layouters;

/// <summary>
/// Per Phase 3 Task 8 + plan §"FloatManager" — tracks left/right floats
/// per block formatting context (BFC) and supports the
/// <c>clear</c> property's resolution (CSS 2.2 §9.5.2). One instance
/// lives per BFC root; floats registered with this manager affect the
/// inline-axis available space + the block-axis placement of
/// subsequent non-floated content within the same BFC.
///
/// <para><b>Cycle 1 MVP scope.</b> This cycle ships:</para>
/// <list type="bullet">
///   <item>Float placement on left/right side of the containing block,
///   stacked along the block axis (no horizontal packing of multiple
///   floats at the same Y — cycle 2 will inline-pack floats per
///   CSS 2.2 §9.5.1 rule 5).</item>
///   <item><c>clear</c> resolution (none / left / right / both /
///   inline-start / inline-end) for non-float blocks following floats.
///   <see cref="GetClearedBlockY"/> returns the block-axis position
///   past which the next block must start.</item>
///   <item>Snapshot/restore via the <c>LayoutCheckpoint.FloatManagerStateSnapshot</c>
///   slot reserved in Phase 3 Task 4.</item>
/// </list>
///
/// <para><b>Cycle 1 deferrals (subsequent cycles):</b></para>
/// <list type="bullet">
///   <item><b>Cycle 2.</b> Inline-axis available-range computation —
///   blocks + line boxes that flow PAST a float should reduce their
///   inline-size to accommodate the float. Cycle 1 keeps blocks at
///   full containing-block inline-size (floats overlap visually; the
///   painter handles z-order). Real CSS sets blocks' inline-size
///   relative to the available range from <c>GetAvailableInlineRange</c>
///   (cycle 2 method).</item>
///   <item><b>Cycle 3.</b> Cross-fragmentainer floats per
///   CSS Fragmentation L3 §5: floats that don't fit on the current
///   page MOVE to the top of the next fragmentainer (not propagated
///   from the same offset).</item>
///   <item><b>Cycle 3.</b> Float interaction with negative margins,
///   <c>shape-outside</c>, multi-column containers, abs-pos
///   ancestors.</item>
///   <item><b>Phase 3 Task 9-10.</b> Inline content (LineBuilder /
///   InlineLayouter) flowing around floats — line boxes shrink to fit
///   the available inline range at each line's block-Y.</item>
/// </list>
///
/// <para><b>Threading.</b> Per-BFC instance; not thread-safe.
/// BlockLayouter holds one instance for its BFC scope. Cross-BFC
/// floats (e.g., a float inside <c>display: flow-root</c>) would
/// belong to a SEPARATE FloatManager owned by that nested BFC root.</para>
///
/// <para><b>Coordinate space.</b> All offsets are in CSS px, in the
/// BFC's own coordinate system (not fragmentainer-absolute). The
/// caller (BlockLayouter) translates to fragmentainer coordinates
/// when emitting <see cref="BoxFragment"/>s.</para>
/// </summary>
internal sealed class FloatManager
{
    private readonly List<FloatRecord> _floats;

    public FloatManager()
    {
        _floats = new List<FloatRecord>(capacity: 4);
    }

    /// <summary>The currently-active floats (in placement order). Test
    /// observation point — production callers use the placement +
    /// clear-resolution methods.</summary>
    internal IReadOnlyList<FloatRecord> ActiveFloats => _floats;

    /// <summary>Place a new float on the given side. Cycle 1 MVP:
    /// floats stack along the block axis on each side independently
    /// (no inline-axis packing of multiple floats at the same Y).
    ///
    /// <para>The placement rules (cycle 1):</para>
    /// <list type="number">
    ///   <item>Block-axis position = max of <paramref name="currentBlockY"/>
    ///   + the bottom edge of any prior float on the SAME side. (Same-
    ///   side floats stack; opposite-side floats don't constrain each
    ///   other in cycle 1.)</item>
    ///   <item>Inline-axis position = the containing-block edge for
    ///   the float's side: <paramref name="containingInlineStart"/>
    ///   for left, <paramref name="containingInlineEnd"/> -
    ///   <paramref name="inlineSize"/> for right.</item>
    /// </list>
    ///
    /// <para>Cycle 2 will refine to per CSS 2.2 §9.5.1 rules 5-6
    /// (left floats stack horizontally if there's room; otherwise
    /// drop to a new line).</para>
    /// </summary>
    /// <param name="side">Float side after writing-mode resolution
    /// (<see cref="FloatSide.Left"/> or <see cref="FloatSide.Right"/>).</param>
    /// <param name="inlineSize">The float's outer inline-axis extent
    /// in CSS px (typically border-box inline-size + horizontal margins).</param>
    /// <param name="blockSize">The float's outer block-axis extent
    /// in CSS px.</param>
    /// <param name="containingInlineStart">Inline-axis start edge of
    /// the containing block (BFC-relative; typically 0).</param>
    /// <param name="containingInlineEnd">Inline-axis end edge of the
    /// containing block (BFC-relative; typically the content inline
    /// size).</param>
    /// <param name="currentBlockY">The block-axis position where the
    /// float would be placed if no prior floats constrained it. Per
    /// CSS 2.2 §9.5.1 rule 1, the float's outer top edge is no higher
    /// than the source-order block-Y at which it was authored.</param>
    /// <returns>The placed (inlineOffset, blockOffset) in BFC
    /// coordinates.</returns>
    public (double inlineOffset, double blockOffset) PlaceFloat(
        FloatSide side,
        double inlineSize,
        double blockSize,
        double containingInlineStart,
        double containingInlineEnd,
        double currentBlockY)
    {
        if (!double.IsFinite(inlineSize) || inlineSize < 0)
            throw new ArgumentOutOfRangeException(nameof(inlineSize),
                $"inlineSize must be finite + non-negative; got {inlineSize}");
        if (!double.IsFinite(blockSize) || blockSize < 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"blockSize must be finite + non-negative; got {blockSize}");
        if (!double.IsFinite(currentBlockY))
            throw new ArgumentOutOfRangeException(nameof(currentBlockY),
                $"currentBlockY must be finite; got {currentBlockY}");

        // Cycle 1 MVP — same-side floats stack vertically.
        // Find the bottom edge of the lowest active float on this side.
        var stackedY = currentBlockY;
        foreach (var f in _floats)
        {
            if (f.Side != side) continue;
            var bottom = f.BlockOffset + f.BlockSize;
            if (bottom > stackedY) stackedY = bottom;
        }

        // Inline-axis side alignment.
        var inlineOffset = side == FloatSide.Left
            ? containingInlineStart
            : containingInlineEnd - inlineSize;

        _floats.Add(new FloatRecord(side, inlineOffset, stackedY, inlineSize, blockSize));
        return (inlineOffset, stackedY);
    }

    /// <summary>Per CSS 2.2 §9.5.2 — for a non-floated block at
    /// <paramref name="currentBlockY"/> with the given <paramref name="clear"/>
    /// value, return the block-axis position past which the block must
    /// start (= max of currentBlockY + the bottom edge of every float
    /// on the cleared side).
    ///
    /// <para><see cref="ClearKind.None"/> returns
    /// <paramref name="currentBlockY"/> unchanged (no clearance).</para>
    ///
    /// <para>Inline-start / inline-end resolve to left / right
    /// respectively for <c>writing-mode: horizontal-tb</c> + LTR.
    /// Vertical writing modes will resolve in cycle 3 (deferral note
    /// on <see cref="ClearKind.InlineStart"/> /
    /// <see cref="ClearKind.InlineEnd"/>).</para></summary>
    public double GetClearedBlockY(double currentBlockY, ClearKind clear)
    {
        if (!double.IsFinite(currentBlockY))
            throw new ArgumentOutOfRangeException(nameof(currentBlockY),
                $"currentBlockY must be finite; got {currentBlockY}");
        if (clear == ClearKind.None) return currentBlockY;

        var clearedY = currentBlockY;
        foreach (var f in _floats)
        {
            var affects = clear switch
            {
                ClearKind.Left => f.Side == FloatSide.Left,
                ClearKind.Right => f.Side == FloatSide.Right,
                ClearKind.Both => true,
                // Cycle 1 — assume horizontal-tb LTR. Cycle 3 will
                // resolve inline-start/end against writing-mode +
                // direction.
                ClearKind.InlineStart => f.Side == FloatSide.Left,
                ClearKind.InlineEnd => f.Side == FloatSide.Right,
                _ => false,
            };
            if (!affects) continue;
            var bottom = f.BlockOffset + f.BlockSize;
            if (bottom > clearedY) clearedY = bottom;
        }
        return clearedY;
    }

    /// <summary>Per Phase 3 Task 4 review fix #1 — snapshot the float
    /// list for <c>LayoutCheckpoint.Capture</c>. Returns an opaque
    /// object the caller stores in <c>FragmentainerContext.FloatManagerState</c>;
    /// deep-copies the float list so the snapshot doesn't alias the
    /// live state. <see cref="RestoreFrom"/> reverses the snapshot on
    /// rewind.</summary>
    public object Snapshot()
    {
        // Defensive deep copy — the live list can mutate after snapshot.
        return _floats.ToArray();
    }

    /// <summary>Restore the float state from a prior <see cref="Snapshot"/>.
    /// <see langword="null"/> resets to no floats (matches a fresh
    /// FloatManager); a non-null snapshot must be the array returned
    /// by <see cref="Snapshot"/>. Throws when the snapshot type is
    /// unrecognized — defensive guard against bad caller wiring.</summary>
    public void RestoreFrom(object? snapshot)
    {
        _floats.Clear();
        if (snapshot is null) return;
        if (snapshot is not FloatRecord[] arr)
        {
            throw new InvalidOperationException(
                $"FloatManager.RestoreFrom received a snapshot of type "
                + $"{snapshot.GetType().Name}; expected FloatRecord[]. "
                + "This indicates a wiring bug — only Snapshot()'s return "
                + "value should be passed here.");
        }
        _floats.AddRange(arr);
    }
}

/// <summary>Per Phase 3 Task 8 — a placed float. The caller (BlockLayouter)
/// emits a <see cref="BoxFragment"/> for the float using
/// <see cref="InlineOffset"/> + <see cref="BlockOffset"/> in BFC coords;
/// the manager retains this record so subsequent <c>clear</c> + inline-
/// range queries can compute against it.</summary>
internal readonly record struct FloatRecord(
    FloatSide Side,
    double InlineOffset,
    double BlockOffset,
    double InlineSize,
    double BlockSize);

/// <summary>Per Phase 3 Task 8 — float side after writing-mode
/// resolution. CSS authors specify <c>float: left|right|inline-start|inline-end</c>;
/// the integrating layouter resolves inline-start/end to left/right
/// based on the active <c>writing-mode</c> + <c>direction</c> before
/// calling <see cref="FloatManager.PlaceFloat"/>.</summary>
internal enum FloatSide : byte
{
    Left = 0,
    Right = 1,
}

/// <summary>Per Phase 3 Task 8 — clear value as authored. CSS keyword
/// indices (per <c>NetPdf.Css.Properties.PropertyId.Clear</c>):
/// 0=none, 1=left, 2=right, 3=both, 4=inline-start, 5=inline-end.
/// Matches the source-gen'd keyword table; the layouter's
/// <see cref="ComputedStyleLayoutExtensions.ReadClearKind"/> decodes.</summary>
internal enum ClearKind : byte
{
    None = 0,
    Left = 1,
    Right = 2,
    Both = 3,
    InlineStart = 4,
    InlineEnd = 5,
}
