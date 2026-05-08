// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Threading;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Paginate;
using NetPdf.Paginate.Diagnostics;

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
/// <para><b>Cycle 2 (this revision) — adjacent-sibling margin collapse
/// per CSS 2.1 §8.3.1.</b> Adjacent block siblings' bottom-margin +
/// top-margin collapse to the spec formula
/// <c>max(positives) - max(absolute values of negatives)</c> via
/// <see cref="MarginCollapse.Collapse"/>. Cross-page collapse
/// suppressed per CSS Fragmentation L3 §6.1 (collapse chain resets
/// at every <see cref="AttemptLayout"/> entry). Non-block children
/// (inline / atomic — Task 10's domain) reset the collapse chain so
/// margins don't collapse across content that should create a line
/// box (PR #23 review fix #3).</para>
///
/// <para><b>Cycle 2 PR #23 review fixes (this revision):</b></para>
/// <list type="bullet">
///   <item><b>#1 — Rewind retry resumes from checkpoint.</b> After
///   a NeedsRewind, the layouter resumes from
///   <c>checkpoint.LastEmittedChildIndex + 1</c> rather than the
///   constructor's incoming continuation. Pre-fix the retry would
///   re-emit the already-preserved fragments + duplicate the
///   output.</item>
///   <item><b>#2 + Copilot #1 — Oversized resumed-block forward
///   progress.</b> The forced-overflow branch fires on "nothing
///   emitted on the current fragmentainer" rather than
///   "no prior pages consumed". An oversized block first on page 2+
///   now makes forward progress instead of looping.</item>
///   <item><b>#3 — Inline content interrupts margin adjacency.</b>
///   When a non-block child is skipped, the collapse chain resets
///   (the inline content would create a line box that breaks
///   adjacency per CSS 2.1 §8.3.1).</item>
///   <item><b>#4 — Resolver-facing UsedBlockSize stays non-negative.</b>
///   Negative-margin blocks can produce a negative margin-box advance.
///   The fragment's BlockOffset preserves the visual negative offset,
///   but <see cref="FragmentainerContext.UsedBlockSize"/> is clamped
///   to 0 so the next <see cref="BreakOpportunity.UsedBlockSize"/>
///   doesn't trip <see cref="CostModel.Score"/>'s non-negative
///   guard.</item>
///   <item><b>#5 — Border-box inline size clamped.</b> Oversized
///   left/right margins can produce a negative border-box inline
///   size; the layouter clamps to 0 to keep the fragment record
///   well-formed.</item>
/// </list>
///
/// <para><b>Cycle 2 deferrals (still — to subsequent commits):</b></para>
/// <list type="bullet">
///   <item><b>Cycle 2b.</b> Recursive nested-block layout (PR #22
///   review fix #4). Cycle 1 + 2 walk <c>_rootBox.Children</c> only;
///   nested block descendants (<c>div &gt; p</c>) aren't laid out.
///   Failing-skip integration test pins the deferral.</item>
///   <item><b>Cycle 3.</b> BFC root detection; intrinsic sizing
///   modes (<c>min-content</c> / <c>max-content</c> / <c>fit-content</c>);
///   width / height auto resolution per §10.3.3 + §10.6.2;
///   percentage resolution against containing-block; avoid-break /
///   break-inside / break-before / break-after metadata extraction
///   from <c>ComputedStyle</c> into <see cref="BreakOpportunity"/>
///   flags; logical-axis margin/padding/border accessors that
///   honor <see cref="LayoutContext.WritingMode"/> (PR #22 review
///   fix #5); parent/first-child + parent/last-child margin collapse
///   (CSS 2.1 §8.3.1 — needs recursive layout + BFC detection); BFC
///   root collapse suppression.</item>
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
    private readonly IPaginateDiagnosticsSink? _diagnostics;

    /// <summary>Per PR #22 review fix #2 — the lease for the most
    /// recently registered checkpoint. Held until the next checkpoint
    /// supersedes it (the resolver's <see cref="IBreakResolver.RegisterCheckpoint"/>
    /// returns the prior one to the pool internally) OR the layouter
    /// is disposed.</summary>
    private CheckpointLease _activeLease;

    /// <summary>Per PR #23 review fix #1 — the index to resume at on
    /// the NEXT call to <see cref="AttemptLayout"/>. Set when the
    /// layouter returns <see cref="LayoutAttemptOutcome.NeedsRewind"/>
    /// — the coordinator restores fragmentainer state from the
    /// rewind-target checkpoint, then re-calls <see cref="AttemptLayout"/>;
    /// without this field the retry would re-derive its starting
    /// point from the constructor's <see cref="_incomingContinuation"/>
    /// (typically index 0 for page 1) and re-emit the already-
    /// preserved fragments, doubling the output.
    ///
    /// <para>Initialized to <c>-1</c> meaning "use the incoming
    /// continuation"; set to
    /// <c>checkpoint.LastEmittedChildIndex + 1</c> just before
    /// returning <see cref="LayoutAttemptOutcome.NeedsRewind"/>.</para></summary>
    private int _resumeAtChildIdxAfterRewind = -1;

    /// <summary>Per PR #26 review fix #2 — page-start baseline for
    /// <see cref="BlockContinuation.ConsumedBlockSize"/> accounting.
    /// On the first <see cref="AttemptLayout"/> entry this captures
    /// <see cref="FragmentainerContext.UsedBlockSize"/> at page-start;
    /// it is NOT reset on rewind retries (which restore UsedBlockSize
    /// to the checkpoint's mid-page cursor). Without this, the retry
    /// would compute <c>ConsumedBlockSize = priorPagesConsumed +
    /// (UsedBlockSize - midpage)</c>, undercounting by midpage. The
    /// field stays at <see cref="double.NaN"/> until the first
    /// AttemptLayout entry; subsequent entries (rewind retries)
    /// preserve the original baseline.</summary>
    private double _pageStartUsedBlockSize = double.NaN;

    /// <summary>Per PR #26 review fix #3 — margin-collapse frontier
    /// preserved across rewind. After a rewind, the retry must
    /// resume with the same prevBlockMarginEnd + hasPriorAdjoiningBlock
    /// state that existed AT the checkpoint, otherwise the retried
    /// child applies its full top margin instead of collapsing with
    /// the preserved previous block's bottom margin. Set just before
    /// returning NeedsRewind; consumed on the next AttemptLayout
    /// entry.</summary>
    private double _prevBlockMarginEndAfterRewind;
    private bool _hasPriorAdjoiningBlockAfterRewind;
    private bool _hasRewindCollapseState;

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
        LayoutContinuation? incomingContinuation = null,
        IPaginateDiagnosticsSink? diagnostics = null)
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
        _diagnostics = diagnostics;
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

        // Per PR #23 review fix #1 — resume point. After a rewind,
        // the coordinator restores state + re-calls AttemptLayout; the
        // retry must resume from the rewind-target's
        // LastEmittedChildIndex + 1, NOT from the constructor's
        // incoming continuation. The _resumeAtChildIdxAfterRewind
        // field is set just before returning NeedsRewind; on entry,
        // -1 means "use the incoming continuation" (first call OR
        // re-entry post a clean PageComplete / AllDone, which the
        // coordinator's contract doesn't actually do).
        var incomingBlock = _incomingContinuation as BlockContinuation;
        var startChildIdx = _resumeAtChildIdxAfterRewind >= 0
            ? _resumeAtChildIdxAfterRewind
            : incomingBlock?.ResumeAtChild ?? 0;
        // Reset for the next iteration. If THIS attempt rewinds again,
        // the rewind branch sets it again before returning.
        _resumeAtChildIdxAfterRewind = -1;
        var priorPagesConsumed = incomingBlock?.ConsumedBlockSize ?? 0.0;

        // Snapshot UsedBlockSize at entry. Per-page extent placed by
        // THIS attempt = fragmentainer.UsedBlockSize - initialUsed.
        var initialUsed = fragmentainer.UsedBlockSize;

        // Per PR #26 review fix #2 — page-start baseline for
        // ConsumedBlockSize accounting. Set on first entry, NOT
        // reset on rewind retries.
        if (double.IsNaN(_pageStartUsedBlockSize))
        {
            _pageStartUsedBlockSize = initialUsed;
        }

        // Track whether this attempt has emitted any fragment so far.
        // Per PR #22 review fix #1 — the oversized-block forward-
        // progress path needs to know if BreakHere fires before any
        // emission.
        var emittedThisAttempt = 0;

        // Per Phase 3 Task 7 cycle 2 + CSS 2.1 §8.3.1 — adjacent-
        // sibling vertical margin collapsing. Track the prior
        // adjoining block's margin-end so the next block's margin-
        // start can collapse with it via MarginCollapse.Collapse.
        // Reset conditions:
        //   - Page boundary (every AttemptLayout entry initializes
        //     hasPriorAdjoiningBlock=false) — per CSS Fragmentation
        //     L3 §6.1, margins meeting at a fragmentainer boundary
        //     don't collapse.
        //   - Non-block child between two blocks (PR #23 review fix
        //     #3) — inline / atomic content creates a line box that
        //     breaks adjacency per §8.3.1. The next block's
        //     marginTop is honored without collapse.
        // Per PR #26 review fix #3 — preserve collapse frontier
        // across rewind. If the prior attempt set the rewind
        // collapse state, restore it here so the retried child
        // collapses correctly with the preserved previous block's
        // bottom margin.
        var prevBlockMarginEnd = _hasRewindCollapseState
            ? _prevBlockMarginEndAfterRewind
            : 0.0;
        var hasPriorAdjoiningBlock = _hasRewindCollapseState
            && _hasPriorAdjoiningBlockAfterRewind;
        _hasRewindCollapseState = false;

        for (var childIdx = startChildIdx; childIdx < _rootBox.Children.Count; childIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var child = _rootBox.Children[childIdx];

            // Cycle 1: only block-level children are laid out.
            if (!child.IsBlockLevel)
            {
                // Per PR #23 review fix #3 — non-block content (inline
                // / atomic) creates a line box that breaks margin
                // adjacency per CSS 2.1 §8.3.1. Reset the collapse
                // chain so the next block applies its full marginTop
                // without collapsing across the line box.
                hasPriorAdjoiningBlock = false;
                prevBlockMarginEnd = 0;
                continue;
            }

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
            // the BORDER box on both axes.
            var borderBoxBlockSize = borderStart + paddingStart + contentBlock
                + paddingEnd + borderEnd;

            // Per PR #23 review fix #5 — clamp the border-box inline
            // size to non-negative. Oversized left/right margins
            // (margin-left + margin-right > ContentInlineSize) would
            // otherwise produce a negative inline size + destabilize
            // downstream painting. The fragment records 0 inline-size
            // in that case; future cycle 3 will emit a layout
            // overflow diagnostic.
            var borderBoxInlineSize = Math.Max(0,
                fragmentainer.ContentInlineSize - marginInlineStart - marginInlineEnd);

            // Per Phase 3 Task 7 cycle 2 + CSS 2.1 §8.3.1 — adjacent-
            // sibling margin collapse.
            //
            // `effectiveTopGap` = the actual top gap between this
            // block's border-box top edge + the BFC's prior content:
            //   - First block on the page → marginStart (no prior
            //     block to collapse with; page boundary is a hard
            //     barrier per CSS Fragmentation L3 §6.1).
            //   - Subsequent blocks → MarginCollapse.Collapse(
            //     prevBlockMarginEnd, marginStart) per §8.3.1.
            //
            // `topShift` = the offset from the current
            // fragmentainer.UsedBlockSize (which ALREADY includes
            // prevBlockMarginEnd from the prior block's emission)
            // to this block's border-box top edge:
            //   - First block: topShift = marginStart.
            //   - Subsequent: topShift = effectiveTopGap -
            //     prevBlockMarginEnd (subtract because the prior
            //     bottom margin was already added; replace it with
            //     the collapsed gap). Can be negative when collapse
            //     reduces the total spacing.
            double effectiveTopGap;
            double topShift;
            if (!hasPriorAdjoiningBlock)
            {
                effectiveTopGap = marginStart;
                topShift = marginStart;
            }
            else
            {
                effectiveTopGap = MarginCollapse.Collapse(prevBlockMarginEnd, marginStart);
                topShift = effectiveTopGap - prevBlockMarginEnd;
            }

            // Margin-box block size of the current block (the cursor
            // advance from the prior block's border-box bottom edge
            // to this block's margin-end edge). For the first block
            // it includes the full marginStart; for subsequent blocks
            // it includes the collapsed gap MINUS the part already
            // consumed in prevBlockMarginEnd (= topShift). Drives the
            // resolver's fit-check.
            var marginBoxBlockSize = topShift + borderBoxBlockSize + marginEnd;

            // Per PR #22 review fix #6 — clamp ChunkBlockSize to
            // non-negative for BreakOpportunity.EnsureValid. Negative
            // values are valid for the cursor (negative margins move
            // backward) but the fit-check measure can't be negative.
            // Per PR #26 review fix #4 — negative margins can hide
            // visual overflow. Pre-fix `Math.Max(0, marginBoxBlockSize)`
            // hides cases like `margin-top:-1000; height:2000;
            // margin-bottom:-1000` where a 2000-px border box appears
            // to require 0 fragmentainer space. The break-fit measure
            // must use the VISUAL extent (positive contributions),
            // separate from the cursor-advance measure
            // (signed margin-box). Take the maximum of:
            //   - net margin-box advance (clamped non-negative — what
            //     the cursor will move by)
            //   - border-box block size + non-negative margin parts
            //     (what visually occupies the page)
            // The latter dominates when negative margins cancel a
            // large border box.
            var visualBlockExtent = borderBoxBlockSize
                + Math.Max(0, marginStart)
                + Math.Max(0, marginEnd);
            var chunkForBreakCheck = Math.Max(
                Math.Max(0, marginBoxBlockSize),
                visualBlockExtent);

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
                // Per PR #23 review fix #1 — store the resume point
                // for the next AttemptLayout entry. The coordinator
                // restores fragmentainer state from RewindTo, then
                // re-calls AttemptLayout; without this the retry
                // would re-derive its starting point from the
                // constructor's incoming continuation + re-emit the
                // already-preserved fragments.
                _resumeAtChildIdxAfterRewind = decision.RewindTo.LastEmittedChildIndex + 1;
                // Per PR #26 review fix #3 — capture the collapse
                // frontier so the retry resumes with the same
                // prevBlockMarginEnd + hasPriorAdjoiningBlock state
                // that existed AT this checkpoint. Without it, the
                // retry would reset to the page-start state +
                // double-apply the next block's marginTop.
                _prevBlockMarginEndAfterRewind = prevBlockMarginEnd;
                _hasPriorAdjoiningBlockAfterRewind = hasPriorAdjoiningBlock;
                _hasRewindCollapseState = true;
                return LayoutAttemptResult.NeedsRewind(decision.RewindTo, decision.Cost);
            }

            if (decision.Action == BreakAction.BreakHere)
            {
                // Per PR #22 review fix #1 + PR #23 review fix #2 +
                // Copilot #1 — oversized-block forward progress. If
                // BreakHere fires AND we haven't emitted anything ON
                // THIS FRAGMENTAINER (top of page), emit anyway as
                // forced overflow rather than returning a zero-progress
                // PageComplete that would loop forever.
                //
                // Pre-fix the condition required `priorPagesConsumed
                // == 0 && initialUsed == 0` — meaning oversized blocks
                // that were the first child on PAGE 2+ (or pages with
                // reserved header space) wouldn't trigger the forward-
                // progress path. The corrected predicate is "nothing
                // emitted on the current fragmentainer" — independent
                // of cumulative cross-page extent.
                var nothingEmittedThisAttempt = emittedThisAttempt == 0;
                var atTopOfPage = fragmentainer.UsedBlockSize == initialUsed;

                if (nothingEmittedThisAttempt && atTopOfPage)
                {
                    // Forced overflow: the block is taller than the
                    // fragmentainer — committing it anyway lets
                    // pagination make progress.
                    //
                    // Per PR #26 review fix #1 — emit
                    // PAGINATION-FORCED-OVERFLOW-001 here so the
                    // diagnostic isn't lost when the forced-overflow
                    // path fires on Strict (= the LayoutRetryCoordinator
                    // never reaches LastResort because the layouter
                    // returned PageComplete first). The coordinator
                    // ALSO emits the diagnostic when it triggers
                    // LastResort directly; the dual-emission is OK
                    // because consumers de-duplicate by code + page
                    // index, OR see the redundant emission as the
                    // signal-amplification it is.
                    OptimizingBreakResolver.SafeEmit(_diagnostics, new PaginateDiagnostic(
                        PaginateDiagnosticCodes.PaginationForcedOverflow001,
                        $"BlockLayouter: forced overflow on fragmentainer page index "
                        + $"{fragmentainer.PageIndex}, child index {childIdx} — "
                        + "block is taller than the fragmentainer. Committed anyway "
                        + "to make pagination progress.",
                        PaginateDiagnosticSeverity.Warning));
                    // First block on the (possibly resumed) page →
                    // topShift = effectiveTopGap = marginStart; no
                    // collapse-arithmetic needed.
                    _sink.Emit(new BoxFragment(
                        Box: child,
                        InlineOffset: marginInlineStart,
                        BlockOffset: fragmentainer.UsedBlockSize + topShift,
                        InlineSize: borderBoxInlineSize,
                        BlockSize: borderBoxBlockSize));
                    // Per PR #23 review fix #4 — clamp UsedBlockSize
                    // to non-negative so subsequent BreakOpportunity
                    // construction doesn't trip CostModel's guard.
                    fragmentainer.UsedBlockSize = Math.Max(0,
                        fragmentainer.UsedBlockSize + marginBoxBlockSize);
                    emittedThisAttempt++;

                    // Resume on the NEXT child (childIdx+1) so progress
                    // is monotonic. ConsumedBlockSize = cumulative
                    // across-pages per Copilot #1 + the field's docs.
                    return LayoutAttemptResult.PageComplete(
                        new BlockContinuation(
                            ResumeAtChild: childIdx + 1,
                            ConsumedBlockSize: priorPagesConsumed
                                + (fragmentainer.UsedBlockSize - _pageStartUsedBlockSize)),
                        cost: decision.Cost + CostModel.BreakInsideAvoidViolation);
                }

                // Normal page break — content placed earlier on this
                // page. Resume at THIS child on the next page.
                // Per Copilot #1 — ConsumedBlockSize accumulates across
                // pages, not just this attempt.
                // Per PR #26 review fix #2 — use _pageStartUsedBlockSize
                // (set on FIRST AttemptLayout entry, NOT reset on rewind
                // retries) so the page extent is the cumulative
                // contribution from ALL emissions on this page,
                // including those before a rewind. Pre-fix used
                // `initialUsed` (= UsedBlockSize at THIS entry), which
                // on rewind retries reflects the mid-page restore
                // cursor, undercounting the page extent.
                return LayoutAttemptResult.PageComplete(
                    new BlockContinuation(
                        ResumeAtChild: childIdx,
                        ConsumedBlockSize: priorPagesConsumed
                            + (fragmentainer.UsedBlockSize - _pageStartUsedBlockSize)),
                    cost: decision.Cost);
            }

            // BreakAction.Continue — emit + advance cursor.
            // Per PR #22 review fix #3 — fragment stores BORDER box
            // dimensions. Per Phase 3 Task 7 cycle 2 — topShift
            // already accounts for adjacent-sibling margin collapse.
            // The BlockOffset on the fragment can be NEGATIVE when
            // negative margins overlap with prior content — that
            // visual offset is preserved on the fragment for the
            // painter, but the resolver-facing
            // fragmentainer.UsedBlockSize is clamped to 0 (PR #23
            // review fix #4).
            var blockOffset = fragmentainer.UsedBlockSize + topShift;
            _sink.Emit(new BoxFragment(
                Box: child,
                InlineOffset: marginInlineStart,
                BlockOffset: blockOffset,
                InlineSize: borderBoxInlineSize,
                BlockSize: borderBoxBlockSize));

            // Per PR #23 review fix #4 — clamp UsedBlockSize to
            // non-negative. A valid block with very negative margins
            // could otherwise drive the cursor below zero, then the
            // next BreakOpportunity.UsedBlockSize would trip
            // CostModel.Score's non-negative guard.
            fragmentainer.UsedBlockSize = Math.Max(0,
                fragmentainer.UsedBlockSize + marginBoxBlockSize);
            prevBlockMarginEnd = marginEnd;
            hasPriorAdjoiningBlock = true;
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
