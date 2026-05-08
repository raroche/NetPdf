// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Threading;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Paginate;

namespace NetPdf.Layout.Layouters;

/// <summary>
/// Per Phase 3 Task 7 + plan §"BlockLayouter" — CSS 2.1 block-level
/// layouter. Walks the block-level children of a root <see cref="Box"/>,
/// stacks them along the block axis, consults the
/// <see cref="IBreakResolver"/> at every block boundary, and emits
/// <see cref="BoxFragment"/>s into an <see cref="IBlockFragmentSink"/>
/// as each box is positioned.
///
/// <para><b>Cycle 1 scope</b> (Phase 3 Task 7 cycle 1):</para>
/// <list type="bullet">
///   <item>Walks <see cref="Box.Children"/> filtering for block-level
///   boxes only (per <see cref="Box.IsBlockLevel"/>); inline / atomic
///   children are skipped (<c>InlineLayouter</c> in Task 10 handles
///   them).</item>
///   <item>Reads margin / padding / border / height as CSS px from
///   <see cref="ComputedStyleLayoutExtensions.ReadLengthPxOrZero"/>.
///   Auto values + percentages return 0 per the cycle-1 simplification
///   — TODO cycle 3 wires CSS 2.1 §10.3.3 width / §10.6 height
///   resolution.</item>
///   <item>Stacks children sequentially along the block axis: each
///   child's block-axis position = preceding consumed extent +
///   child's <c>margin-block-start + border-block-start +
///   padding-block-start</c>; consumed extent then advances by the
///   full margin-box block extent.</item>
///   <item>NO margin collapsing — TODO cycle 2 wires CSS 2.1 §8.3.1.
///   Adjacent vertical margins currently sum, not collapse.</item>
///   <item>NO BFC root detection — TODO cycle 3.</item>
///   <item>NO float interaction — TODO Phase 3 Task 8 wires
///   <c>FloatManager</c>.</item>
///   <item>Inline-axis position: 0 (left edge of content area).
///   Inline-axis size: <c>fragmentainer.ContentInlineSize</c> for
///   block boxes (CSS 2.1 §10.3.3 default for non-replaced block-level).
///   Auto width resolution per the CSS spec is TODO cycle 3.</item>
///   <item>Consults <see cref="IBreakResolver.ConsiderBreakAt"/> at
///   every block boundary using
///   <see cref="BreakOpportunity.Block"/>. Honors
///   <see cref="BreakAction.BreakHere"/> by returning
///   <see cref="LayoutAttemptOutcome.PageComplete"/> with a
///   <see cref="BlockContinuation"/>.
///   <see cref="BreakAction.Rewind"/> propagates as
///   <see cref="LayoutAttemptOutcome.NeedsRewind"/>.</item>
/// </list>
///
/// <para><b>Continuation handling.</b> When called with a non-null
/// <see cref="LayoutContinuation"/> from a prior attempt's PageComplete,
/// the layouter resumes at the named child index. The
/// <see cref="BlockContinuation.ConsumedBlockSize"/> field carries
/// the block-axis position from prior pages (Task 7 cycle 1 doesn't
/// use it directly; Phase 4's painter consumes it for cross-page
/// margin / border continuation).</para>
///
/// <para><b>Strategy honoring.</b> Per Phase 3 Task 5 PR #21 review
/// fix #1, the layouter respects the
/// <see cref="LayoutAttemptStrategy"/> the coordinator passes:</para>
/// <list type="bullet">
///   <item><see cref="LayoutAttemptStrategy.Strict"/>: honor
///   <c>break-inside: avoid</c> + <c>break-before/after: avoid</c>
///   constraints (cycle 1 — these aren't yet wired since they
///   require ComputedStyle inspection of the Break* properties).</item>
///   <item><see cref="LayoutAttemptStrategy.DropAvoidInside"/>: drop
///   <c>break-inside: avoid</c>; still honor <c>break-before/after</c>
///   avoid.</item>
///   <item><see cref="LayoutAttemptStrategy.LastResort"/>: commit
///   regardless. Layouter MUST NOT return
///   <see cref="LayoutAttemptOutcome.NeedsRewind"/> on this strategy.</item>
/// </list>
/// <para>Cycle 1 returns <see cref="LayoutAttemptOutcome.NeedsRewind"/>
/// only when the resolver explicitly returns
/// <see cref="BreakAction.Rewind"/> — which the cycle-1 stub
/// resolvers don't do. So in practice cycle 1's strategy parameter
/// is documentation-only; cycle 3+ wires real avoid-break
/// detection.</para>
/// </summary>
internal sealed class BlockLayouter : ILayouter
{
    private readonly Box _rootBox;
    private readonly IBlockFragmentSink _sink;
    private readonly LayoutContinuation? _incomingContinuation;

    /// <summary>Construct a layouter for <paramref name="rootBox"/>'s
    /// block children, emitting fragments into <paramref name="sink"/>.
    /// <paramref name="incomingContinuation"/> resumes a multi-page
    /// layout at the named child index; pass <see langword="null"/>
    /// for the first page.</summary>
    public BlockLayouter(
        Box rootBox,
        IBlockFragmentSink sink,
        LayoutContinuation? incomingContinuation = null)
    {
        ArgumentNullException.ThrowIfNull(rootBox);
        ArgumentNullException.ThrowIfNull(sink);
        _rootBox = rootBox;
        _sink = sink;
        _incomingContinuation = incomingContinuation;
    }

    /// <inheritdoc />
    public LayoutAttemptResult AttemptLayout(
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        IBreakResolver resolver,
        LayoutAttemptStrategy strategy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fragmentainer);
        ArgumentNullException.ThrowIfNull(resolver);
        cancellationToken.ThrowIfCancellationRequested();

        // Resume from the incoming continuation if provided.
        var startChildIdx = (_incomingContinuation as BlockContinuation)?.ResumeAtChild ?? 0;

        // Snapshot UsedBlockSize at entry. Per-page extent placed by
        // THIS attempt = fragmentainer.UsedBlockSize - initialUsed
        // (computed when we emit a continuation; the fragmentainer
        // already tracks the cumulative position so we don't track
        // it separately here).
        var initialUsed = fragmentainer.UsedBlockSize;

        for (var childIdx = startChildIdx; childIdx < _rootBox.Children.Count; childIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var child = _rootBox.Children[childIdx];

            // Cycle 1: only block-level children are laid out.
            // Inline / atomic children are deferred to InlineLayouter
            // (Task 10). For now, skip them silently — a real layouter
            // would create anonymous-block wrappers (BoxBuilder already
            // does this in its anonymous-block-insertion pass).
            if (!child.IsBlockLevel) continue;

            // Read the margin-box extent along the block axis.
            // Cycle 1 — assumes resolved px values; auto / percentage
            // return 0 (TODO cycle 3 for §10.3.3 + §10.6).
            var marginStart = child.Style.ReadLengthPxOrZero(PropertyId.MarginTop);
            var borderStart = child.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
            var paddingStart = child.Style.ReadLengthPxOrZero(PropertyId.PaddingTop);
            var contentBlock = child.Style.ReadLengthPxOrZero(PropertyId.Height);
            var paddingEnd = child.Style.ReadLengthPxOrZero(PropertyId.PaddingBottom);
            var borderEnd = child.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
            var marginEnd = child.Style.ReadLengthPxOrZero(PropertyId.MarginBottom);

            var marginBoxBlockSize = marginStart + borderStart + paddingStart
                + contentBlock + paddingEnd + borderEnd + marginEnd;

            // Inline-axis: cycle 1 assumes block-level boxes fill the
            // full content inline-size (CSS 2.1 §10.3.3 default for
            // non-replaced block-level when width is auto). Auto +
            // percentage resolution is TODO cycle 3.
            var marginInlineStart = child.Style.ReadLengthPxOrZero(PropertyId.MarginLeft);
            var marginBoxInlineSize = fragmentainer.ContentInlineSize - marginInlineStart
                - child.Style.ReadLengthPxOrZero(PropertyId.MarginRight);

            // Consult the resolver at this block boundary.
            var opportunity = BreakOpportunity.Block(
                usedBlockSize: fragmentainer.UsedBlockSize,
                chunkBlockSize: marginBoxBlockSize);
            var decision = resolver.ConsiderBreakAt(opportunity, fragmentainer);

            if (decision.Action == BreakAction.Rewind)
            {
                // Per Phase 3 Task 5 PR #21 review fix #1 — RewindTo
                // MUST be non-null. Defensive: if the resolver
                // somehow returned Rewind without naming a checkpoint,
                // the coordinator's ValidateOrThrow will catch it.
                if (decision.RewindTo is null)
                {
                    throw new InvalidOperationException(
                        "Resolver returned BreakAction.Rewind without a RewindTo checkpoint. "
                        + "This violates the IBreakResolver contract — Rewind requires the "
                        + "resolver to name a checkpoint.");
                }
                return LayoutAttemptResult.NeedsRewind(decision.RewindTo, decision.Cost);
            }

            if (decision.Action == BreakAction.BreakHere)
            {
                // Commit the page break here. The current child becomes
                // the resume point for the next page. ConsumedBlockSize
                // = the block-axis extent placed BY THIS ATTEMPT (not
                // cumulative across pages — that's the layouter caller's
                // responsibility to thread).
                return LayoutAttemptResult.PageComplete(
                    new BlockContinuation(
                        ResumeAtChild: childIdx,
                        ConsumedBlockSize: fragmentainer.UsedBlockSize - initialUsed),
                    cost: decision.Cost);
            }

            // BreakAction.Continue — emit the fragment + advance the
            // fragmentainer's block-axis cursor.
            _sink.Emit(new BoxFragment(
                Box: child,
                InlineOffset: marginInlineStart,
                BlockOffset: fragmentainer.UsedBlockSize,
                InlineSize: marginBoxInlineSize,
                BlockSize: marginBoxBlockSize));

            fragmentainer.UsedBlockSize += marginBoxBlockSize;
        }

        // All children laid out — no more pages needed.
        return LayoutAttemptResult.AllDone(cost: 0);
    }
}
