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
/// <para><b>Cycle 1 PR #22 review fixes (this revision):</b></para>
/// <list type="bullet">
///   <item><b>#1 — Oversized-block forward progress.</b> When the
///   resolver returns <see cref="BreakAction.BreakHere"/> for the
///   FIRST candidate on a fresh page (no prior emission this attempt
///   AND the incoming continuation didn't carry consumed extent),
///   the layouter emits the fragment anyway with a forced-overflow
///   penalty (<see cref="LayoutAttemptOutcome.PageComplete"/> with
///   <see cref="BlockContinuation.ResumeAtChild"/> = next index).
///   This prevents an infinite loop where the layouter returns the
///   same child as the resume point on every retry.</item>
///   <item><b>#2 — Checkpoint capture/register.</b> Before each
///   block-boundary <see cref="IBreakResolver.ConsiderBreakAt"/>
///   call, the layouter rents a <see cref="LayoutCheckpoint"/> from
///   the pool, captures the live state (fragmentainer, layout, sink
///   cursor, child index), and registers the lease with the resolver.
///   When the rewind frontier advances (next break boundary), the
///   prior lease is automatically returned via the resolver's
///   internal management (per Phase 3 Task 4 review fix #7's
///   lease-token CAS). Layouter <see cref="IDisposable"/> releases
///   any final outstanding lease.</item>
///   <item><b>#3 + Copilot #2 — Border-box fragment geometry.</b>
///   <see cref="BoxFragment.InlineSize"/> + <see cref="BoxFragment.BlockSize"/>
///   are the BORDER box (excluding margin) on both axes; the
///   pagination cursor advances by the MARGIN box extent (border +
///   margins) so block-axis accounting + page-fit checks remain
///   correct. The painter reads <c>fragment.Box.Style</c> for
///   margins / padding / content rectangles by subtraction.</item>
///   <item><b>#6 — Negative margins clamp.</b> CSS allows negative
///   margins. The layouter passes <c>Math.Max(0,
///   marginBoxBlockSize)</c> as <see cref="BreakOpportunity.ChunkBlockSize"/>
///   so <see cref="BreakOpportunity.EnsureValid"/>'s non-negative
///   guard isn't tripped, while still using the unclamped value for
///   the cursor advance (negative margins move the cursor BACKWARD,
///   which is the intended visual effect).</item>
///   <item><b>#7 — Continuation validation.</b> Constructor rejects
///   non-<see cref="BlockContinuation"/> incoming continuations +
///   out-of-range <see cref="BlockContinuation.ResumeAtChild"/>
///   values. Pre-fix the layouter silently restarted from index 0
///   on type mismatch + silently returned <see cref="LayoutAttemptOutcome.AllDone"/>
///   on out-of-range index, hiding caller bugs.</item>
///   <item><b>Copilot #1 — <see cref="BlockContinuation.ConsumedBlockSize"/>
///   semantics fix.</b> The field's docs say "amount already emitted
///   on PRIOR pages" (cumulative cross-page). Cycle-1 cycle-1 set it
///   to current-attempt-only; this revision threads
///   <c>incomingContinuation.ConsumedBlockSize</c> +
///   current-attempt-extent so the cumulative invariant holds across
///   page boundaries.</item>
/// </list>
///
/// <para><b>Cycle 1 deferrals (still — to subsequent commits):</b></para>
/// <list type="bullet">
///   <item><b>Cycle 2.</b> Margin collapsing per CSS 2.1 §8.3.1.</item>
///   <item><b>Cycle 3.</b> BFC root detection; intrinsic sizing
///   modes (<c>min-content</c> / <c>max-content</c> / <c>fit-content</c>);
///   width / height auto resolution per §10.3.3 + §10.6.2;
///   percentage resolution against containing-block; avoid-break /
///   break-inside / break-before / break-after metadata extraction
///   from <c>ComputedStyle</c> into <see cref="BreakOpportunity"/>
///   flags; logical-axis margin/padding/border accessors that
///   honor <see cref="LayoutContext.WritingMode"/> (PR #22 review
///   fix #5).</item>
///   <item><b>Cycle 2-3.</b> Recursive nested-block layout (PR #22
///   review fix #4 — current cycle 1 walks <c>_rootBox.Children</c>
///   only, not nested block descendants). Failing-skip integration
///   tests pin the deferral.</item>
///   <item><b>Phase 3 Task 8.</b> Float interaction via the
///   <c>FloatManager</c>.</item>
///   <item><b>Phase 3 Task 10.</b> Inline content within blocks
///   (<c>InlineLayouter</c> recursion).</item>
/// </list>
/// </summary>
internal sealed class BlockLayouter : ILayouter, IDisposable
{
    private readonly Box _rootBox;
    private readonly IBlockFragmentSink _sink;
    private readonly LayoutContinuation? _incomingContinuation;

    /// <summary>Per PR #22 review fix #2 — the lease for the most
    /// recently registered checkpoint. Held until the next checkpoint
    /// supersedes it (the resolver's <see cref="IBreakResolver.RegisterCheckpoint"/>
    /// returns the prior one to the pool internally) OR the layouter
    /// is disposed.</summary>
    private CheckpointLease _activeLease;

    /// <summary>Construct a layouter for <paramref name="rootBox"/>'s
    /// block children, emitting fragments into <paramref name="sink"/>.
    /// <paramref name="incomingContinuation"/> resumes a multi-page
    /// layout at the named child index; pass <see langword="null"/>
    /// for the first page.
    ///
    /// <para>Per PR #22 review fix #7 — non-<see cref="BlockContinuation"/>
    /// incoming continuations OR out-of-range
    /// <see cref="BlockContinuation.ResumeAtChild"/> values throw
    /// at construction rather than silently misbehaving.</para></summary>
    /// <exception cref="ArgumentException">When
    /// <paramref name="incomingContinuation"/> is non-null but not a
    /// <see cref="BlockContinuation"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When the
    /// continuation's
    /// <see cref="BlockContinuation.ResumeAtChild"/> is &lt; 0 or
    /// &gt; <c>rootBox.Children.Count</c>.</exception>
    public BlockLayouter(
        Box rootBox,
        IBlockFragmentSink sink,
        LayoutContinuation? incomingContinuation = null)
    {
        ArgumentNullException.ThrowIfNull(rootBox);
        ArgumentNullException.ThrowIfNull(sink);

        // Per PR #22 review fix #7 — validate continuation.
        if (incomingContinuation is not null and not BlockContinuation)
        {
            throw new ArgumentException(
                $"BlockLayouter expects a BlockContinuation; got "
                + $"{incomingContinuation.GetType().Name}. The wrong continuation "
                + "type would silently restart from index 0 and likely duplicate / "
                + "drop content.",
                nameof(incomingContinuation));
        }
        if (incomingContinuation is BlockContinuation bc)
        {
            if (bc.ResumeAtChild < 0 || bc.ResumeAtChild > rootBox.Children.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(incomingContinuation),
                    $"BlockContinuation.ResumeAtChild={bc.ResumeAtChild} is "
                    + $"outside the root's child range [0, {rootBox.Children.Count}]. "
                    + "Out-of-range values silently return AllDone with no fragments, "
                    + "hiding caller bugs.");
            }
        }

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

        // Resume from the incoming continuation if provided. (Validated
        // in the constructor — at this point we know it's a
        // BlockContinuation if non-null.)
        var incomingBlock = _incomingContinuation as BlockContinuation;
        var startChildIdx = incomingBlock?.ResumeAtChild ?? 0;
        var priorPagesConsumed = incomingBlock?.ConsumedBlockSize ?? 0.0;

        // Snapshot UsedBlockSize at entry. Per-page extent placed by
        // THIS attempt = fragmentainer.UsedBlockSize - initialUsed.
        var initialUsed = fragmentainer.UsedBlockSize;

        // Track whether this attempt has emitted any fragment so far.
        // Per PR #22 review fix #1 — the oversized-block forward-
        // progress path needs to know if BreakHere fires before any
        // emission.
        var emittedThisAttempt = 0;

        for (var childIdx = startChildIdx; childIdx < _rootBox.Children.Count; childIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var child = _rootBox.Children[childIdx];

            // Cycle 1: only block-level children are laid out.
            if (!child.IsBlockLevel) continue;

            // Read the box-axis extents.
            // Cycle 1 — assumes resolved px values; auto / percentage
            // return 0 (TODO cycle 3). PR #22 review fix #5: physical-
            // axis reads (TODO cycle 3 wires logical-axis helpers).
            var marginStart = child.Style.ReadLengthPxOrZero(PropertyId.MarginTop);
            var borderStart = child.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
            var paddingStart = child.Style.ReadLengthPxOrZero(PropertyId.PaddingTop);
            var contentBlock = child.Style.ReadLengthPxOrZero(PropertyId.Height);
            var paddingEnd = child.Style.ReadLengthPxOrZero(PropertyId.PaddingBottom);
            var borderEnd = child.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
            var marginEnd = child.Style.ReadLengthPxOrZero(PropertyId.MarginBottom);
            var marginInlineStart = child.Style.ReadLengthPxOrZero(PropertyId.MarginLeft);
            var marginInlineEnd = child.Style.ReadLengthPxOrZero(PropertyId.MarginRight);

            // Per PR #22 review fix #3 + Copilot #2 — BoxFragment is
            // the BORDER box on both axes. Compute both:
            // - marginBoxBlockSize: drives pagination accounting
            //   (cursor advance + fit-check).
            // - borderBoxBlockSize: stored on the fragment for the
            //   painter.
            var borderBoxBlockSize = borderStart + paddingStart + contentBlock
                + paddingEnd + borderEnd;
            var marginBoxBlockSize = marginStart + borderBoxBlockSize + marginEnd;

            var borderBoxInlineSize = fragmentainer.ContentInlineSize
                - marginInlineStart - marginInlineEnd;

            // Per PR #22 review fix #6 — clamp ChunkBlockSize to
            // non-negative for BreakOpportunity.EnsureValid. Negative
            // margin-box sizes are valid (CSS allows negative margins
            // that visually overlap), but the BreakOpportunity's fit-
            // measure can't be negative.
            var chunkForBreakCheck = Math.Max(0, marginBoxBlockSize);

            // Per PR #22 review fix #2 — capture a checkpoint at the
            // candidate-break boundary BEFORE consulting the resolver.
            // The resolver may return Rewind, naming this checkpoint
            // for the rollback target.
            var newLease = LayoutCheckpointPool.Rent();
            newLease.Checkpoint!.Capture(
                fragmentainer,
                in layout,
                fragmentOutputCursor: _sink.Cursor,
                lastEmittedChildIndex: childIdx - 1,
                incomingContinuation: _incomingContinuation,
                pageCounterValue: layout.ReadCounter("page"));
            resolver.RegisterCheckpoint(newLease);
            // The resolver's internal RegisterCheckpoint returns the
            // PRIOR lease to the pool via the lease-token CAS; we just
            // track our local reference so Dispose can release the
            // last one.
            _activeLease = newLease;

            // Consult the resolver at this block boundary.
            var opportunity = BreakOpportunity.Block(
                usedBlockSize: fragmentainer.UsedBlockSize,
                chunkBlockSize: chunkForBreakCheck);
            var decision = resolver.ConsiderBreakAt(opportunity, fragmentainer);

            if (decision.Action == BreakAction.Rewind)
            {
                if (decision.RewindTo is null)
                {
                    throw new InvalidOperationException(
                        "Resolver returned BreakAction.Rewind without a RewindTo "
                        + "checkpoint. This violates the IBreakResolver contract — "
                        + "Rewind requires the resolver to name a checkpoint.");
                }
                return LayoutAttemptResult.NeedsRewind(decision.RewindTo, decision.Cost);
            }

            if (decision.Action == BreakAction.BreakHere)
            {
                // Per PR #22 review fix #1 — oversized-block forward
                // progress. If BreakHere fires AND we haven't emitted
                // anything on this attempt AND no prior pages
                // contributed (== fresh page genuinely starting from
                // scratch with this child), emit anyway as forced
                // overflow rather than returning a zero-progress
                // PageComplete that would loop forever.
                var nothingEmittedThisAttempt = emittedThisAttempt == 0;
                var freshPageStart = priorPagesConsumed == 0
                    && fragmentainer.UsedBlockSize == initialUsed
                    && initialUsed == 0;

                if (nothingEmittedThisAttempt && freshPageStart)
                {
                    // Forced overflow: the block is taller than the
                    // fragmentainer — committing it anyway lets
                    // pagination make progress. The
                    // LayoutRetryCoordinator emits
                    // PAGINATION-FORCED-OVERFLOW-001 when the
                    // LastResort attempt fires; the cost we attribute
                    // here reflects the overflow.
                    _sink.Emit(new BoxFragment(
                        Box: child,
                        InlineOffset: marginInlineStart,
                        BlockOffset: fragmentainer.UsedBlockSize + marginStart,
                        InlineSize: borderBoxInlineSize,
                        BlockSize: borderBoxBlockSize));
                    fragmentainer.UsedBlockSize += marginBoxBlockSize;
                    emittedThisAttempt++;

                    // Resume on the NEXT child (childIdx+1) so progress
                    // is monotonic. ConsumedBlockSize = cumulative
                    // across-pages per Copilot #1 + the field's docs.
                    return LayoutAttemptResult.PageComplete(
                        new BlockContinuation(
                            ResumeAtChild: childIdx + 1,
                            ConsumedBlockSize: priorPagesConsumed
                                + (fragmentainer.UsedBlockSize - initialUsed)),
                        cost: decision.Cost + CostModel.BreakInsideAvoidViolation);
                }

                // Normal page break — content placed earlier on this
                // page. Resume at THIS child on the next page.
                // Per Copilot #1 — ConsumedBlockSize accumulates across
                // pages, not just this attempt.
                return LayoutAttemptResult.PageComplete(
                    new BlockContinuation(
                        ResumeAtChild: childIdx,
                        ConsumedBlockSize: priorPagesConsumed
                            + (fragmentainer.UsedBlockSize - initialUsed)),
                    cost: decision.Cost);
            }

            // BreakAction.Continue — emit the fragment + advance.
            // Per PR #22 review fix #3 — store BORDER box dimensions;
            // advance cursor by margin-box extent.
            _sink.Emit(new BoxFragment(
                Box: child,
                InlineOffset: marginInlineStart,
                BlockOffset: fragmentainer.UsedBlockSize + marginStart,
                InlineSize: borderBoxInlineSize,
                BlockSize: borderBoxBlockSize));

            fragmentainer.UsedBlockSize += marginBoxBlockSize;
            emittedThisAttempt++;
        }

        // All children laid out — no more pages needed.
        return LayoutAttemptResult.AllDone(cost: 0);
    }

    /// <summary>Per PR #22 review fix #2 — release the final
    /// outstanding checkpoint lease when the layouter is discarded.
    /// Idempotent. The integrating layout pipeline drives via
    /// <c>using var layouter = new BlockLayouter(...)</c>.</summary>
    public void Dispose()
    {
        if (_activeLease.Checkpoint is not null)
        {
            LayoutCheckpointPool.Return(_activeLease);
            _activeLease = default;
        }
    }
}
