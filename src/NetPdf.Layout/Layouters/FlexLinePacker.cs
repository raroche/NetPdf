// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Threading;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;

namespace NetPdf.Layout.Layouters;

/// <summary>Per Phase 3 Task 16 cycle 4c (P3 #8 from PR-#79) —
/// shared line-packing algorithm for flex containers per
/// CSS Flexbox L1 §9.3.
///
/// <para><b>Pre-cycle-4c duplication.</b>
/// <see cref="FlexLayouter.PackLines"/> (the layouter's emission-time
/// packer) and
/// <see cref="BlockLayouter.PreMeasureFlexMultiLineCrossExtent"/>
/// (the pre-grow pre-measure used at both the outer dispatch site
/// + the recursive walk site) ran the SAME greedy line-packing
/// algorithm against the SAME (sorted) item sequence — but as two
/// independent implementations. The L8 post-PR-#68 F#1 hardening +
/// the L10 sort-by-effective-order refactor BOTH had to update
/// both copies in lockstep; drift between them would silently
/// produce line-boundary mismatches (= pre-measure reserves space
/// for line layout X while emit produces line layout Y, sibling
/// boxes land wrong / page splits trigger prematurely).</para>
///
/// <para><b>Cycle 4c consolidation.</b> The algorithm lives here as
/// one static method; both callers delegate. The pre-measure path
/// keeps a thin wrapper that sums <see cref="FlexLine.LineCrossSize"/>
/// (= the only datum it needs); the layouter path consumes the full
/// <see cref="FlexLine"/> list (it needs FirstItemIndex + ItemCount +
/// LineMainSize for the §9.7 distribution + per-line emission).</para>
///
/// <para><b>Why a standalone static class, not a partial of
/// FlexLayouter.</b> Both BlockLayouter and FlexLayouter consume the
/// packer; making it a FlexLayouter private would force BlockLayouter
/// to dispatch through an instance method (= unnecessary allocation
/// + tighter coupling). Static + internal preserves the existing
/// "BlockLayouter pre-measures + dispatches → FlexLayouter emits"
/// directionality without requiring either to own the
/// other.</para></summary>
internal static class FlexLinePacker
{
    /// <summary>Per Phase 3 Task 16 cycle 4c — pack a flex container's
    /// sorted-by-(order, DOM-index) items into lines per CSS Flexbox
    /// L1 §9.3's greedy algorithm.
    ///
    /// <para><b>Algorithm:</b>
    /// <list type="bullet">
    ///   <item><c>!isWrapping</c> (= <c>flex-wrap: nowrap</c>): every
    ///   item lands on a single line; the line's main extent is the
    ///   sum of items' hypothetical main-sizes; the line's cross
    ///   extent is <c>max(item cross-size)</c>. (= L1-L5 single-line
    ///   behavior preserved verbatim.)</item>
    ///   <item><c>isWrapping</c> (= <c>flex-wrap: wrap</c> or
    ///   <c>wrap-reverse</c>): walks items in effective-order;
    ///   accumulates main-size on the current line; when adding the
    ///   NEXT item would push the line past <paramref name="containerMainSize"/>,
    ///   closes the current line + starts a new one. Per §9.3 "if
    ///   the very first uncollected item wouldn't fit, collect just
    ///   it into the line" — the FIRST item on each line always
    ///   lands (= solo-overflow is OK; the "would exceed" check
    ///   applies only to subsequent items).</item>
    /// </list></para>
    ///
    /// <para><b>Hypothetical main-size</b> per CSS Flexbox L1 §9.2 —
    /// derived from <c>flex-basis</c> via
    /// <see cref="ComputedStyleLayoutExtensions.ResolveFlexItemHypotheticalMainSize"/>.
    /// Pre-L8 the packer used the raw declared width/height; the L8
    /// post-PR-#68 F#1 hardening switched to the hypothetical so an
    /// item with <c>width: 300; flex-basis: 0; flex-grow: 1</c> in a
    /// 300-px container packs as if 0 wide (= multiple such items
    /// share a line + grow to fill).</para>
    ///
    /// <para><b>Cross-size</b> is the raw declared length on the
    /// cross-axis property. Cross-axis flexibility (= a cross-size
    /// resolution that honors stretch / wrap-reverse re-derivation)
    /// is L9+ scope; the packer reports the declared length so the
    /// alignment + emission code can apply axis-aware overrides.</para>
    ///
    /// <para><b>Cancellation:</b> checked once per item so a
    /// long item list honors caller cancellation.</para>
    /// </summary>
    /// <param name="flexContainer">The flex container whose children
    /// are being packed. The packer dereferences
    /// <see cref="Box.Children"/> by DOM index after sort-position
    /// translation.</param>
    /// <param name="sortedChildIndices">Block-level children in
    /// effective flex order per CSS Flexbox L1 §5.4 — produced by
    /// <see cref="ComputedStyleLayoutExtensions.GetFlexChildrenInOrderSequence"/>.
    /// Non-block-level children are pre-filtered; the packer doesn't
    /// re-check IsBlockLevel.</param>
    /// <param name="direction">Determines which CSS property is the
    /// main axis (Width for row, Height for column) + which is cross.</param>
    /// <param name="containerMainSize">The line-packing budget along
    /// the main axis (= the container's content-main-size). The
    /// caller provides this from <c>ConfigureEmission</c>'s
    /// content-inline/block size (= post-borders + padding).</param>
    /// <param name="isWrapping">True for <c>flex-wrap: wrap</c> or
    /// <c>wrap-reverse</c>; false for <c>nowrap</c>.</param>
    /// <param name="cancellationToken">Honored once per item.</param>
    /// <returns>The packed lines. Empty when
    /// <paramref name="sortedChildIndices"/> is empty (= no items to
    /// pack; the caller short-circuits emission).</returns>
    public static List<FlexLine> Pack(
        Box flexContainer,
        List<int> sortedChildIndices,
        FlexDirectionValue direction,
        double containerMainSize,
        bool isWrapping,
        CancellationToken cancellationToken)
    {
        var lines = new List<FlexLine>();
        // Per PR-#84 review P3 #5 — axis mapping comes from the
        // shared extension on FlexDirectionValue so the packer +
        // FlexLayouter cannot drift on which CSS property is main vs.
        // cross.
        var (mainProp, crossProp) = direction.GetAxisProperties();

        if (sortedChildIndices.Count == 0)
        {
            return lines;
        }

        if (!isWrapping)
        {
            // Single-line algorithm — L1-L5 preserved verbatim.
            var totalMain = 0.0;
            var maxCross = 0.0;
            foreach (var idx in sortedChildIndices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = flexContainer.Children[idx];
                totalMain += item.ResolveFlexItemHypotheticalMainSize(
                    mainProp, containerMainSize);
                var c = item.Style.ReadLengthPxOrZero(crossProp);
                if (c > maxCross) maxCross = c;
            }
            lines.Add(new FlexLine(
                FirstItemIndex: 0,
                ItemCount: sortedChildIndices.Count,
                LineMainSize: totalMain,
                LineCrossSize: maxCross));
            return lines;
        }

        // Wrap — greedy line packing. See class XML doc for the
        // §9.3 contract; the first item on a line always lands +
        // subsequent items wrap when adding would exceed
        // containerMainSize.
        var currentFirstSortedPos = 0;
        var currentCount = 0;
        var currentMain = 0.0;
        var currentCross = 0.0;

        for (var sortedPos = 0; sortedPos < sortedChildIndices.Count; sortedPos++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var domIdx = sortedChildIndices[sortedPos];
            var item = flexContainer.Children[domIdx];
            var itemMain = item.ResolveFlexItemHypotheticalMainSize(
                mainProp, containerMainSize);
            var itemCross = item.Style.ReadLengthPxOrZero(crossProp);

            if (currentCount > 0 && currentMain + itemMain > containerMainSize)
            {
                lines.Add(new FlexLine(
                    FirstItemIndex: currentFirstSortedPos,
                    ItemCount: currentCount,
                    LineMainSize: currentMain,
                    LineCrossSize: currentCross));
                currentFirstSortedPos = sortedPos;
                currentCount = 0;
                currentMain = 0;
                currentCross = 0;
            }

            currentMain += itemMain;
            if (itemCross > currentCross) currentCross = itemCross;
            currentCount++;
        }

        if (currentCount > 0)
        {
            lines.Add(new FlexLine(
                FirstItemIndex: currentFirstSortedPos,
                ItemCount: currentCount,
                LineMainSize: currentMain,
                LineCrossSize: currentCross));
        }

        return lines;
    }

    /// <summary>Per Phase 3 Task 16 cycle 4c post-PR-#84 review P2 #1 —
    /// streaming variant of <see cref="Pack"/> that returns ONLY the
    /// sum of line cross-extents (= the pre-grow pre-measure's only
    /// consumed result) without materializing the
    /// <see cref="List{FlexLine}"/>.
    ///
    /// <para><b>Why.</b> <see cref="BlockLayouter.PreMeasureFlexMultiLineCrossExtent"/>
    /// runs ONCE PER FLEX CONTAINER ON THE PAGE during the outer
    /// pagination's pre-grow pass + ONCE MORE per nested flex
    /// container during the recursive walk's pre-grow. For documents
    /// with many wrapped flex containers (think product grids,
    /// dashboard tile layouts), the pre-cycle-4b code streamed the
    /// algorithm + kept only the cross-extent sum; cycle 4c's
    /// initial <see cref="Pack"/>-then-sum approach unnecessarily
    /// allocates one <see cref="List{FlexLine}"/> + N
    /// <see cref="FlexLine"/> entries per container. The post-PR-#84
    /// review P2 #1 hardening adds this streaming entry point so the
    /// pre-measure pays zero allocation for the line list it would
    /// throw away anyway.</para>
    ///
    /// <para><b>Algorithm parity.</b> The exact same packing rules as
    /// <see cref="Pack"/>: same hypothetical main-size derivation
    /// (flex-basis-aware via
    /// <see cref="ComputedStyleLayoutExtensions.ResolveFlexItemHypotheticalMainSize"/>),
    /// same first-item-always-lands semantics, same direction-aware
    /// property selection via the shared
    /// <see cref="FlexDirectionValueExtensions.GetAxisProperties"/>
    /// extension. The only difference is what gets returned (= just
    /// the sum, not the line list). Behavior parity is verified by
    /// dedicated parity tests in <c>FlexLinePackerTests</c>.</para>
    ///
    /// <para><b>FlexLayouter still calls
    /// <see cref="Pack"/></b> — emission needs the FlexLine list
    /// (FirstItemIndex / ItemCount / LineMainSize) for the §9.7
    /// flexibility algorithm + per-line emission. Only the
    /// pre-measure path needs the streaming variant.</para></summary>
    public static double SumCrossExtent(
        Box flexContainer,
        List<int> sortedChildIndices,
        FlexDirectionValue direction,
        double containerMainSize,
        bool isWrapping,
        CancellationToken cancellationToken)
    {
        var (mainProp, crossProp) = direction.GetAxisProperties();

        if (sortedChildIndices.Count == 0)
        {
            return 0.0;
        }

        if (!isWrapping)
        {
            // Single-line — sum is just max(item cross-size). No
            // line list to allocate; iterate items once.
            var maxCross = 0.0;
            foreach (var idx in sortedChildIndices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = flexContainer.Children[idx];
                var c = item.Style.ReadLengthPxOrZero(crossProp);
                if (c > maxCross) maxCross = c;
            }
            return maxCross;
        }

        // Wrap — same greedy algorithm as Pack, but accumulate
        // sumLineCross in place of building a FlexLine list. When
        // a new line starts (current line's items would overflow),
        // commit the current line's cross-extent to the sum + reset.
        var sumLineCross = 0.0;
        var currentCount = 0;
        var currentMain = 0.0;
        var currentCross = 0.0;

        foreach (var domIdx in sortedChildIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = flexContainer.Children[domIdx];
            var itemMain = item.ResolveFlexItemHypotheticalMainSize(
                mainProp, containerMainSize);
            var itemCross = item.Style.ReadLengthPxOrZero(crossProp);

            if (currentCount > 0 && currentMain + itemMain > containerMainSize)
            {
                sumLineCross += currentCross;
                currentCount = 0;
                currentMain = 0;
                currentCross = 0;
            }

            currentMain += itemMain;
            if (itemCross > currentCross) currentCross = itemCross;
            currentCount++;
        }

        if (currentCount > 0)
        {
            sumLineCross += currentCross;
        }

        return sumLineCross;
    }
}

/// <summary>Per Phase 3 Task 15 L6 → cycle 4c — packed flex line as
/// produced by <see cref="FlexLinePacker.Pack"/>.
///
/// <para><b>Indexing convention:</b> <see cref="FirstItemIndex"/> is
/// a POSITION in the SORTED sequence
/// (<c>GetFlexChildrenInOrderSequence</c>'s output), NOT a DOM-children
/// index. The emission loop walks
/// <c>sortedChildIndices[FirstItemIndex .. FirstItemIndex + ItemCount)</c>
/// + dereferences each position to a DOM-children index. The sorted
/// sequence is pre-filtered to block-level children only, so the
/// emission loop no longer carries the per-item IsBlockLevel skip the
/// L1-L9 code held inline.</para>
///
/// <para>Pre-cycle-4c this lived as a private nested type in
/// <see cref="FlexLayouter"/>. Promoted to internal at the namespace
/// level so <see cref="FlexLinePacker"/> can produce + return it +
/// both <see cref="FlexLayouter"/> and <see cref="BlockLayouter"/>
/// can consume it.</para></summary>
internal readonly record struct FlexLine(
    int FirstItemIndex,
    int ItemCount,
    double LineMainSize,
    double LineCrossSize);
