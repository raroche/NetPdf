// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Layout.Layouters;

/// <summary>
/// Per Phase 3 Task 15 L14 — encapsulates the cross-axis layout flow
/// of a flex container's lines, swapping the cross-start / cross-end
/// origin under <c>flex-wrap: wrap-reverse</c> (CSS Flexbox L1 §6.3).
///
/// <para>
/// <b>Purpose.</b> Pre-L14 the swap logic was inline at three sites in
/// <see cref="FlexLayouter"/>'s emission loop:
/// </para>
/// <list type="bullet">
///   <item>The line's physical cross-offset (= the line's TOP edge in
///   the wrapper's content-box coordinate system).</item>
///   <item>The per-item <c>align-items</c> / <c>align-self</c>
///   FlexStart/FlexEnd anchor swap (the <c>isCrossAxisReversed</c>
///   parameter on <c>ComputeAlignItemsPlacement</c>).</item>
///   <item>The align-content cursor origin (= where the line stack
///   starts within the swapped-axis space).</item>
/// </list>
/// <para>
/// The L11 post-PR-#71 hardening F#9 review flagged this as a
/// duplication seam: each site repeated the same swap predicate
/// (<c>isWrapReverse</c>) + the same formula
/// (<c>contentCrossOffset + containerCrossSize - swappedAxisCursor -
/// line.LineCrossSize</c>). L14 extracts the helper so the swap
/// state lives in ONE place; the emission loop calls into it via
/// method receivers that take the swapped cursor as input.
/// </para>
/// <para>
/// <b>Coordinate systems.</b> Two parallel coordinate systems are in
/// play during emission:
/// </para>
/// <list type="bullet">
///   <item><b>Swapped axis</b> — the abstract axis the L7 align-content
///   distribution walks: 0 = swapped cross-start, increasing toward the
///   swapped cross-end. For <c>wrap</c> this IS the physical axis. For
///   <c>wrap-reverse</c> the swapped cross-start is the physical
///   cross-END (= bottom for row + horizontal-tb LTR).</item>
///   <item><b>Physical axis</b> — the emission sink's coordinate system:
///   0 = wrapper's content-box top-left corner, increasing toward the
///   physical cross-end (= the block axis for row direction).</item>
/// </list>
/// <para>
/// <b>Future scope.</b> When writing-mode-aware logical-axis mapping
/// lands (L7+ deferred), the abstraction gets a writing-mode parameter
/// and the physical-mapping math moves here too. For cycle 1 only
/// row + horizontal-tb is wired so the abstraction is direction-
/// agnostic but writing-mode-naïve.
/// </para>
/// </summary>
/// <param name="IsReversed"><see langword="true"/> when
/// <c>flex-wrap: wrap-reverse</c> is in effect — toggles the swap.</param>
/// <param name="ContentCrossOffset">The wrapper's content-box origin on
/// the cross axis (= where the line stack's physical-top edge starts
/// when <c>!IsReversed</c>).</param>
/// <param name="ContainerCrossSize">The full content-box cross extent
/// of the wrapper (= the span the line stack distributes over).</param>
internal readonly record struct CrossAxisFlow(
    bool IsReversed,
    double ContentCrossOffset,
    double ContainerCrossSize)
{
    /// <summary>Convert a swapped-axis cursor position into the
    /// line's PHYSICAL cross-offset (= the line's TOP edge in the
    /// emission sink's coordinate system).
    /// <para>
    /// <b>Formula.</b> For wrap (<c>!IsReversed</c>):
    /// <c>contentCrossOffset + swappedCursor</c> (= the physical axis
    /// IS the swapped axis). For wrap-reverse: the line's PHYSICAL-TOP
    /// edge sits at <c>contentCrossOffset + containerCrossSize -
    /// swappedCursor - lineCrossSize</c> (= the line's "depth" from the
    /// swapped cross-start, subtracted from the physical-end edge).
    /// </para>
    /// </summary>
    /// <param name="swappedCursor">The cursor position in the swapped
    /// axis (0 at swapped cross-start, increasing toward swapped
    /// cross-end).</param>
    /// <param name="lineCrossSize">The line's own cross-extent
    /// (= max(item cross-size on this line) for wrap, or the
    /// container's full cross extent for nowrap).</param>
    public double PhysicalLineOffset(double swappedCursor, double lineCrossSize) =>
        IsReversed
            ? ContentCrossOffset + ContainerCrossSize - swappedCursor - lineCrossSize
            : ContentCrossOffset + swappedCursor;
}
