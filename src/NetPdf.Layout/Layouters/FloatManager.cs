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
/// <para><b>Cycle 2 shipped (this revision) — flow around floats +
/// cross-fragmentainer deferral.</b> New <see cref="GetAvailableInlineRange"/>
/// method returns the inline-axis range reduced by any active float
/// at the queried block-Y; in-flow blocks shrink to fit per CSS 2.2 §9.5.
/// Cross-fragmentainer float deferral (Fragmentation L3 §5) is wired
/// through <c>BlockLayouter</c>: floats that don't fit the current
/// page move to the top of the next fragmentainer when the page
/// already has emitted content; a too-tall float on an empty page
/// still emits with `PAGINATION-FORCED-OVERFLOW-001` (cycle 3 will
/// fragment such floats).</para>
///
/// <para><b>Remaining deferrals (cycle 3 / Task 9):</b></para>
/// <list type="bullet">
///   <item><b>Cycle 3.</b> Nested-block flow-around at the nested-Y
///   (cycle 2 only queries at the OUTER cursor Y; nested blocks use
///   the parent's content area as-is).</item>
///   <item><b>Cycle 3.</b> A single block dynamically widening past
///   a float's bottom (cycle 2 uses the block's hypothetical-top-Y
///   for the available-range query; the block doesn't re-flow if it
///   extends past the float bottom).</item>
///   <item><b>Cycle 3.</b> Float CONTAINING block alignment to
///   nearest block ancestor vs BFC-wide (cycle 1/2 simplification:
///   nested floats use BFC-wide containing block).</item>
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

    // Per cycle 1 post-PR-30 review (P2 #7) — per-side max-bottom
    // cache to keep `GetClearedBlockY` O(1) instead of O(n) over the
    // full record list. Documents with many floats and clears would
    // otherwise be O(n²); the cache lets clear-only queries hit
    // constant time. Updated incrementally as floats are placed; reset
    // on `RestoreFrom` (via re-derive from the restored array).
    private double _maxLeftBottom;
    private double _maxRightBottom;

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
        // Per cycle 1 post-PR-30 review (P2 #7) — argument validation
        // for containing-block extents. In production these are safe
        // (BlockLayouter passes 0 + fragmentainer.ContentInlineSize),
        // but the public contract should reject NaN/Infinity defensively.
        if (!double.IsFinite(containingInlineStart))
            throw new ArgumentOutOfRangeException(nameof(containingInlineStart),
                $"containingInlineStart must be finite; got {containingInlineStart}");
        if (!double.IsFinite(containingInlineEnd))
            throw new ArgumentOutOfRangeException(nameof(containingInlineEnd),
                $"containingInlineEnd must be finite; got {containingInlineEnd}");
        // The contract permits oversized floats (inlineSize >
        // containingInlineEnd - containingInlineStart) — they overflow
        // the containing block and the painter handles z-order.
        // Cycle 2 will refine `GetAvailableInlineRange` to express the
        // overflow visibly. End < start is also legal (degenerate
        // containing block) — treat as zero-width.

        // Cycle 1 MVP — same-side floats stack vertically. Use the
        // per-side max-bottom cache (post-P2-#7) instead of scanning
        // the full record list.
        var sameSideBottom = side == FloatSide.Left ? _maxLeftBottom : _maxRightBottom;
        var stackedY = Math.Max(currentBlockY, sameSideBottom);

        // Inline-axis side alignment.
        var inlineOffset = side == FloatSide.Left
            ? containingInlineStart
            : containingInlineEnd - inlineSize;

        _floats.Add(new FloatRecord(side, inlineOffset, stackedY, inlineSize, blockSize));

        // Update the per-side cache. The just-placed float's bottom
        // becomes the new max for its side (since same-side floats
        // stack monotonically).
        var newBottom = stackedY + blockSize;
        if (side == FloatSide.Left)
        {
            if (newBottom > _maxLeftBottom) _maxLeftBottom = newBottom;
        }
        else
        {
            if (newBottom > _maxRightBottom) _maxRightBottom = newBottom;
        }

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

        // Per cycle 1 post-PR-30 review (P2 #7) — O(1) lookup via the
        // per-side max-bottom cache. Pre-fix scanned all records on
        // every clear query; documents with many floats + many clears
        // were O(n²). The cache is correct because clearance is the
        // MAX of float bottoms on the relevant side(s), and the cache
        // is the running max maintained by `PlaceFloat`.
        // Cycle 1 inline-start/inline-end resolve as left/right under
        // horizontal-tb LTR (cycle 3 will resolve against
        // writing-mode + direction).
        var relevantBottom = clear switch
        {
            ClearKind.Left or ClearKind.InlineStart => _maxLeftBottom,
            ClearKind.Right or ClearKind.InlineEnd => _maxRightBottom,
            ClearKind.Both => Math.Max(_maxLeftBottom, _maxRightBottom),
            _ => 0.0,
        };
        return Math.Max(currentBlockY, relevantBottom);
    }

    /// <summary>Per Phase 3 Task 8 cycle 2 — return the inline-axis
    /// range available for an in-flow block at <paramref name="blockY"/>,
    /// reduced to flow around any float whose vertical extent overlaps
    /// <paramref name="blockY"/>. Per CSS 2.2 §9.5: a non-floating block
    /// shrinks its inline-size to leave room for floats on both sides
    /// at its current block-Y.
    ///
    /// <para>The returned range is in BFC coordinates:
    /// <list type="bullet">
    ///   <item><c>InlineStart</c> = max(<paramref name="containingStart"/>,
    ///   right edge of the deepest LEFT float at <paramref name="blockY"/>).</item>
    ///   <item><c>InlineEnd</c> = min(<paramref name="containingEnd"/>,
    ///   left edge of the deepest RIGHT float at <paramref name="blockY"/>).</item>
    /// </list>
    /// When no float intersects <paramref name="blockY"/>, returns the
    /// full containing range. When a left + right float together
    /// cover the full inline range (degenerate case — the block has
    /// 0 inline-axis space), returns <c>InlineEnd &lt; InlineStart</c>;
    /// callers handle this by clamping to 0 width or shifting block-Y
    /// past the float (cycle 2 MVP: callers don't re-flow; they use
    /// the clamped range as-is + paint may overlap).</para>
    ///
    /// <para>A float is considered "active" at <paramref name="blockY"/>
    /// when <c>blockY ∈ [float.BlockOffset, float.BlockOffset + float.BlockSize)</c>
    /// — i.e., the block-Y is within the float's vertical extent. Floats
    /// whose bottom is at-or-above <paramref name="blockY"/> don't
    /// constrain (they've ended); floats whose top is below
    /// <paramref name="blockY"/> haven't started yet.</para>
    ///
    /// <para><b>Cost.</b> O(n) over the active float list; cycle 2 MVP
    /// accepts this since typical pages have very few floats. Cycle 3
    /// could add an interval tree if profiling shows hot-path concern.</para></summary>
    public (double InlineStart, double InlineEnd) GetAvailableInlineRange(
        double blockY,
        double containingStart,
        double containingEnd)
    {
        if (!double.IsFinite(blockY))
            throw new ArgumentOutOfRangeException(nameof(blockY),
                $"blockY must be finite; got {blockY}");
        if (!double.IsFinite(containingStart))
            throw new ArgumentOutOfRangeException(nameof(containingStart),
                $"containingStart must be finite; got {containingStart}");
        if (!double.IsFinite(containingEnd))
            throw new ArgumentOutOfRangeException(nameof(containingEnd),
                $"containingEnd must be finite; got {containingEnd}");

        var leftEdge = containingStart;
        var rightEdge = containingEnd;

        foreach (var f in _floats)
        {
            // Active at blockY iff blockY is in [BlockOffset, BlockOffset+BlockSize).
            // Floats with BlockSize=0 are degenerate and can't constrain
            // any positive-extent block (we use exclusive upper bound to
            // avoid false-positive coverage at the float's bottom edge).
            if (blockY < f.BlockOffset) continue;
            if (blockY >= f.BlockOffset + f.BlockSize) continue;

            if (f.Side == FloatSide.Left)
            {
                // Left float occupies [InlineOffset, InlineOffset+InlineSize)
                // — push the available start past it.
                var rightOfFloat = f.InlineOffset + f.InlineSize;
                if (rightOfFloat > leftEdge) leftEdge = rightOfFloat;
            }
            else
            {
                // Right float occupies the same range from the right;
                // push the available end before it.
                var leftOfFloat = f.InlineOffset;
                if (leftOfFloat < rightEdge) rightEdge = leftOfFloat;
            }
        }

        return (leftEdge, rightEdge);
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
    /// unrecognized — defensive guard against bad caller wiring.
    ///
    /// <para>Per cycle 1 post-PR-30 review (Copilot #3) — validate
    /// the snapshot TYPE before mutating live state. Pre-fix the
    /// method cleared `_floats` before the type check, so a wiring
    /// bug that passed the wrong snapshot would wipe legitimate state
    /// AND throw, leaving the manager empty if the exception was
    /// caught by a wrapper. Post-fix: type check first; mutation only
    /// on a valid snapshot or null.</para></summary>
    public void RestoreFrom(object? snapshot)
    {
        if (snapshot is null)
        {
            // Null snapshot is the "fresh manager" reset — explicitly
            // valid per the public contract.
            _floats.Clear();
            _maxLeftBottom = 0;
            _maxRightBottom = 0;
            return;
        }
        if (snapshot is not FloatRecord[] arr)
        {
            // Type guard BEFORE any state mutation — pre-clear was a
            // foot-gun.
            throw new InvalidOperationException(
                $"FloatManager.RestoreFrom received a snapshot of type "
                + $"{snapshot.GetType().Name}; expected FloatRecord[]. "
                + "This indicates a wiring bug — only Snapshot()'s return "
                + "value should be passed here.");
        }
        _floats.Clear();
        _floats.AddRange(arr);
        // Per cycle 1 post-PR-30 review (P2 #7) — re-derive the
        // per-side max-bottom cache from the restored array.
        _maxLeftBottom = 0;
        _maxRightBottom = 0;
        foreach (var f in arr)
        {
            var bottom = f.BlockOffset + f.BlockSize;
            if (f.Side == FloatSide.Left)
            {
                if (bottom > _maxLeftBottom) _maxLeftBottom = bottom;
            }
            else
            {
                if (bottom > _maxRightBottom) _maxRightBottom = bottom;
            }
        }
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
