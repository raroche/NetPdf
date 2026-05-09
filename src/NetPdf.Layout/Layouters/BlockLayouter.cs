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
/// <para><b>Cycle 2b shipped — recursive nested-block layout per CSS
/// 2.1 §10.</b> Cycle 1 + 2 walked <c>_rootBox.Children</c> only; a
/// <c>div &gt; p</c> tree emitted only the <c>div</c> fragment. Cycle
/// 2b adds <see cref="EmitBlockSubtreeRecursive"/> — emits
/// <see cref="BoxFragment"/>s for nested block-level descendants at
/// correctly nested offsets, applying adjacent-margin collapse
/// (§8.3.1) between nested siblings. Recursion runs from BOTH the
/// Continue path + the forced-overflow path. Only block-flow
/// containers (<see cref="BoxKind.BlockContainer"/>,
/// <see cref="BoxKind.ListItem"/>, <see cref="BoxKind.AnonymousBlock"/>,
/// <see cref="BoxKind.Root"/>) are recursed into;
/// <see cref="BoxKind.Table"/> / <see cref="BoxKind.FlexContainer"/> /
/// <see cref="BoxKind.GridContainer"/> /
/// <see cref="BoxKind.BlockReplacedElement"/> are emitted as
/// placeholders (their inner geometry belongs to the dedicated
/// layouter). Recursion threads <see cref="CancellationToken"/> +
/// caps depth at <see cref="MaxRecursionDepth"/> for DoS protection;
/// child-cursor is a SIGNED visual cursor so negative-margin overlap
/// between nested siblings works correctly.</para>
///
/// <para><b>Cycle 2c shipped (this revision) — subtree-aware
/// pagination (atomic MVP).</b> Pre-cycle-2c the outer break decision
/// + cursor advance both used the box's OWN <c>borderBoxBlockSize</c>,
/// so containers with overflowing block-level descendants silently
/// clipped past the fragmentainer boundary + subsequent siblings
/// visually overlapped the overflow. Cycle 2c adds
/// <see cref="MeasureSubtreeVisualBlockExtent(Box, CancellationToken)"/> — a pre-measure
/// pass that returns the maximum block-axis extent reached by any
/// descendant. The break-fit chunk size + cursor advance both use
/// this measured extent (max of own border-box + descendant bottoms),
/// so:
/// <list type="bullet">
///   <item>Oversized subtrees trigger a break-before via the resolver,
///   pushing the entire subtree to the next page (atomic semantics
///   per CSS Fragmentation L3 §5).</item>
///   <item>Subtrees that can't fit any page (taller than the
///   fragmentainer) commit via the existing forced-overflow forward
///   progress + emit <c>PAGINATION-FORCED-OVERFLOW-001</c> — pre-fix
///   this case was silent because the outer measure missed the
///   overflow.</item>
///   <item>Siblings AFTER an overflowing subtree are positioned past
///   the overflow's bottom edge (no overlap).</item>
/// </list>
/// Non-flow block kinds (Table/Flex/Grid/Replaced) are atomic to the
/// measure pass — only their own border-box size contributes (their
/// inner geometry belongs to the dedicated layouter, same predicate
/// gating as <see cref="EmitBlockSubtreeRecursive"/>).</para>
///
/// <para><b>Cycle 2c policy: "PDF safety layout" semantics for
/// explicit-height + overflow.</b> Per cycle 2c post-PR-29 review #4 —
/// the cursor-advance change can produce a layout that diverges from
/// the strict CSS box model. Specifically: in CSS, when a parent has
/// an EXPLICIT height + a child overflows it, the parent's USED HEIGHT
/// is still the explicit value; the child paints PAST the parent's
/// bottom edge but doesn't enlarge the parent for layout-positioning
/// purposes. CSS <c>overflow: visible</c> renders the overflow;
/// <c>overflow: hidden/auto/scroll</c> clips/scrolls.</para>
///
/// <para>Cycle 2c uses the SUBTREE-aware extent for cursor advance
/// regardless of <c>overflow</c> property — so a sibling after an
/// overflowing parent is positioned BELOW the overflow's bottom (not
/// below the parent's explicit-height bottom). This is INTENTIONAL +
/// named the "PDF safety layout" policy: prefer no-visual-overlap +
/// page-break-aware over strict CSS used-height semantics. Rationale:
/// PDF is non-interactive paged media — there's no scroll bar, and
/// silent visual overlap is a worse UX failure than minor used-height
/// drift from CSS browsers. The <c>overflow</c> property is honored
/// at PAINT TIME (cycle 4 painter clips overflow:hidden boxes); cycle
/// 2c's layout-time treatment is uniform across all overflow values.
/// Cycle 3 will refine for <c>overflow: hidden</c> (clip the subtree
/// extent at the parent's border-box bottom) + add tests for the
/// other overflow values.</para>
///
/// <para><b>Cycle 2c deferrals (still — to subsequent commits):</b></para>
/// <list type="bullet">
///   <item><b>Cycle 2d.</b> True mid-subtree pagination splits —
///   cycle 2c MVP treats subtrees as ATOMIC (push wholly to next
///   page OR forced-overflow). Real CSS allows breaks INSIDE a
///   subtree (parent's first half on page 1 + parent's second half
///   on page 2). Requires recursive continuation tokens
///   (<c>BlockContinuation.NestedContinuation</c>) + break consultation
///   inside <see cref="EmitBlockSubtreeRecursive"/> + recursive
///   resume on retry.</item>
///   <item><b>Cycle 3.</b> Auto-height resolution per CSS 2.1 §10.6.3
///   (when a container has <c>height: auto</c>, its content area
///   should resolve to children's stack height — cycle 2b uses
///   <see cref="ComputedStyleLayoutExtensions.ReadLengthPxOrZero"/>
///   which returns 0 for auto, so a container with <c>height: auto</c>
///   + children renders with a 0-sized parent fragment but children
///   still emit at correct offsets); BFC root detection; intrinsic
///   sizing modes (<c>min-content</c> / <c>max-content</c> /
///   <c>fit-content</c>); width / height auto resolution per
///   §10.3.3 + §10.6.2; percentage resolution against containing-block;
///   avoid-break / break-inside / break-before / break-after metadata
///   extraction from <c>ComputedStyle</c> into
///   <see cref="BreakOpportunity"/> flags; logical-axis margin/padding/
///   border accessors that honor <see cref="LayoutContext.WritingMode"/>
///   (PR #22 review fix #5); parent/first-child + parent/last-child
///   margin collapse (CSS 2.1 §8.3.1 — needs BFC detection); BFC root
///   collapse suppression.</item>
///   <item><b>Phase 3 Task 8 cycles 1+2.</b> Float placement +
///   <c>clear</c> resolution + flow-around + cross-fragmentainer
///   deferral via <see cref="FloatManager"/>. Cycle 1 added the
///   manager + dispatch; cycle 2 (this revision) wires
///   <see cref="FloatManager.GetAvailableInlineRange"/> into the
///   in-flow block sizing path (blocks shrink + offset to flow
///   around floats per CSS 2.2 §9.5) + adds cross-fragmentainer
///   deferral (a float that won't fit defers to the next page when
///   the current page has prior content; falls back to emit +
///   diagnostic on an empty page per Fragmentation L3 §5). Remaining
///   deferrals: nested-Y dynamic flow-around (cycle 3); inline
///   content flowing around floats (Phase 3 Task 9-10
///   LineBuilder/InlineLayouter).</item>
///   <item><b>Phase 3 Task 9-12.</b> Dedicated layouters for the
///   non-flow block kinds the cycle-2b recursion deliberately skips:
///   <c>TableLayouter</c> (Tables L3), <c>FlexLayouter</c> (Flexbox L1),
///   <c>GridLayouter</c> (Grid L2), <c>InlineLayouter</c> (inline content
///   within blocks).</item>
/// </list>
/// </summary>
internal sealed class BlockLayouter : ILayouter, IDisposable
{
    private readonly Box _rootBox;
    private readonly IBlockFragmentSink _sink;
    private readonly LayoutContinuation? _incomingContinuation;
    private readonly IPaginateDiagnosticsSink? _diagnostics;

    /// <summary>Per Phase 3 Task 8 cycle 1 — the float manager for
    /// THIS BlockLayouter's BFC scope. One instance per BlockLayouter
    /// (one per BFC root). When children have <c>float: left|right|
    /// inline-start|inline-end</c>, they're registered here +
    /// positioned per CSS 2.2 §9.5.1 cycle-1-MVP rules. Subsequent
    /// children with <c>clear: ...</c> consult this manager to
    /// determine their post-clear block-axis position.
    ///
    /// <para>Cycle 1 MVP: blocks ignore floats for sizing (full
    /// containing-block inline-size); cycle 2 will reduce inline-size
    /// to flow around floats. See <c>FloatManager</c>'s class XML doc
    /// for the deferral list.</para></summary>
    private readonly FloatManager _floatManager = new();

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

        // Per Phase 3 Task 8 cycle 1 post-PR-30 review (P0 #1 +
        // Copilot #1) — restore FloatManager state from the
        // fragmentainer's snapshot slot. This handles BOTH:
        //   (a) page boundaries — the same BlockLayouter instance can
        //       be reused across pages; without restore, floats from
        //       page 1 would leak into page 2's BFC tracking.
        //   (b) rewind retries — `LayoutCheckpoint.RestoreInto` writes
        //       the captured snapshot back into
        //       `fragmentainer.FloatManagerState`, but until cycle 1
        //       post-PR-30 the BlockLayouter never read it back into
        //       the live `_floatManager`. So a rewind would restore
        //       fragmentainer state but leave `_floatManager` with
        //       stale floats — causing duplicate emissions on retry.
        //
        // The contract: `fragmentainer.FloatManagerState` is the
        // "authoritative" float state at the start of each
        // AttemptLayout call. The layouter restores from it on entry
        // + writes back to it before each checkpoint capture (see
        // the capture site below).
        _floatManager.RestoreFrom(fragmentainer.FloatManagerState);

        // Per Phase 3 Task 8 cycle 1 post-PR-30 review (P1 #4) —
        // capture the BFC-wide content inline size for use by
        // EmitNestedFloat (the recursion doesn't have direct
        // FragmentainerContext access).
        _bfcContentInlineSize = fragmentainer.ContentInlineSize;

        // Track whether this attempt has emitted any fragment so far.
        // Per PR #22 review fix #1 — the oversized-block forward-
        // progress path needs to know if BreakHere fires before any
        // emission.
        var emittedThisAttempt = 0;

        // Per PR #24/#25 Copilot review — track the actual last-
        // emitted child index, separate from the loop counter
        // `childIdx`. The pre-fix `lastEmittedChildIndex: childIdx - 1`
        // was wrong when the child at `childIdx - 1` was a skipped
        // non-block-level node (e.g., a TextRun between two block
        // siblings). The contract for
        // <see cref="LayoutCheckpoint.LastEmittedChildIndex"/> is the
        // index of the last FULLY-EMITTED box, not the loop predecessor.
        // -1 = nothing emitted yet on this attempt's start (the
        // `startChildIdx - 1` baseline conveys "resume from
        // startChildIdx" if the rewind targets the very first
        // candidate); the resume-after-rewind path adds 1 in
        // `_resumeAtChildIdxAfterRewind = LastEmittedChildIndex + 1`.
        var lastEmittedIdx = startChildIdx - 1;

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

            // Per Phase 3 Task 8 cycle 1 — float dispatch. A
            // block-level child with `float: left|right|inline-start|
            // inline-end` is positioned out-of-flow + doesn't
            // contribute to the in-flow cursor or the margin-collapse
            // chain. Per CSS 2.2 §9.5: the float's outer top edge is
            // no higher than the block-axis position at which it was
            // authored (= current `fragmentainer.UsedBlockSize`); it's
            // shifted to the named side until it touches the
            // containing block edge or another same-side float.
            //
            // Cycle 1 MVP: floats stack along the block axis (cycle 2
            // will inline-pack same-side floats per §9.5.1 rule 5).
            // The float's fragment is emitted but blocks DON'T resize
            // around it (cycle 2 will reduce inline-size to flow
            // around floats; for now floats overlap visually + the
            // painter handles z-order).
            var floatSide = child.Style.ReadFloatSide();
            if (floatSide.HasValue)
            {
                // Per Phase 3 Task 8 cycle 2 — cross-fragmentainer
                // float deferral. CSS Fragmentation L3 §5: a float
                // that doesn't fit the current fragmentainer should
                // move to the top of the NEXT fragmentainer (not
                // overflow + clip silently as cycle 1 did with just
                // a diagnostic).
                //
                // Cycle 2 MVP: peek at the float's would-be margin-box
                // bottom; if it exceeds the page's block-size AND
                // this page has prior emitted content, return
                // PageComplete with the float as the resume point —
                // the float emits FIRST on the next page where it has
                // a fresh fragmentainer to fit in. If the page is
                // empty (this float would be first), fall through to
                // EmitFloat which still emits + diagnostic (the float
                // is taller than ANY page; cycle 3 will fragment it
                // or document the limitation).
                if (WouldFloatOverflow(child, fragmentainer)
                    && emittedThisAttempt > 0)
                {
                    return LayoutAttemptResult.PageComplete(
                        new BlockContinuation(
                            ResumeAtChild: childIdx,
                            ConsumedBlockSize: priorPagesConsumed
                                + (fragmentainer.UsedBlockSize - _pageStartUsedBlockSize)),
                        cost: 0);
                }

                EmitFloat(
                    child,
                    floatSide.Value,
                    fragmentainer,
                    ref layout,
                    cancellationToken);
                // Floats DON'T affect the in-flow margin-collapse
                // chain (they're out-of-flow per CSS 2.2 §9.5). Don't
                // mutate prevBlockMarginEnd / hasPriorAdjoiningBlock.
                //
                // Per Phase 3 Task 8 cycle 1 post-PR-30 review (P0 #1) —
                // a successfully-emitted float still counts as
                // "emitted on this attempt" for checkpoint accounting:
                //   - `lastEmittedIdx = childIdx` so the next checkpoint
                //     captures the correct resume index (a rewind to
                //     a checkpoint AFTER the float resumes at the
                //     subsequent sibling, not at the float itself).
                //   - `emittedThisAttempt++` so the forced-overflow
                //     "nothing emitted" guard correctly recognizes
                //     that a float committed content on this page
                //     (a page with a float + an oversized block must
                //     break-before the block, not force-overflow it).
                lastEmittedIdx = childIdx;
                emittedThisAttempt++;
                continue;
            }

            // Per Phase 3 Task 8 cycle 1 — clear resolution. A
            // non-floating block with `clear: ...` clears past the
            // bottom of the named-side float(s).
            //
            // Per cycle 1 post-PR-30 review (P1 #2) — clearance is
            // space INSERTED ABOVE the block's margin-top, not below.
            // CSS 2.1 §9.5.2 / CSS 2 visual formatting model: the
            // clearance amount is chosen so:
            //   border-box-top = max(hypothetical-border-box-top,
            //                        floatBottom)
            // where hypothetical-border-box-top is where the block
            // WOULD be without clearance (= currentY + marginTop after
            // collapse). Pre-fix the layouter advanced
            // `fragmentainer.UsedBlockSize` to floatBottom + then
            // applied marginTop on top, putting the border-box top at
            // floatBottom + marginTop (one marginTop too low).
            //
            // The fix DEFERS the clearance computation: we compute
            // it from the BOX-MODEL READS BELOW (after we know
            // marginTop / topShift), then apply as the LARGER of:
            //   (a) the natural cursor advance for this block
            //       (= UsedBlockSize + topShift, post-collapse)
            //   (b) the cleared-float-bottom
            // The signed delta becomes the effective topShift when
            // clearance dominates. Clearance also resets the margin-
            // collapse chain — the inserted clearance breaks the
            // collapse boundary per §8.3.1.
            //
            // Implementation: read the clear kind here; the actual
            // application is folded into the topShift computation
            // below.
            var clearKind = child.Style.ReadClearKind();
            var clearedFloatBottom = clearKind != ClearKind.None
                ? _floatManager.GetClearedBlockY(
                    fragmentainer.UsedBlockSize, clearKind)
                : fragmentainer.UsedBlockSize;
            // Has clearance actually been triggered? Only if a
            // relevant float bottom is BELOW the current cursor —
            // otherwise the cleared-Y equals the current cursor and
            // no clearance is inserted.
            var clearanceTriggered = clearedFloatBottom > fragmentainer.UsedBlockSize;
            if (clearanceTriggered)
            {
                // Per §8.3.1 — clearance creates a new collapse
                // boundary. Reset the chain so the block's marginTop
                // is honored without collapsing across the cleared
                // gap.
                hasPriorAdjoiningBlock = false;
                prevBlockMarginEnd = 0;
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

            // Per Phase 3 Task 8 cycle 2 — flow around floats. The
            // available inline-axis range at the block's
            // hypothetical-top-Y is reduced by any float whose
            // vertical extent intersects that Y. Per CSS 2.2 §9.5,
            // an in-flow non-floating block shrinks its inline-size
            // to leave room for floats on both sides. Cycle 1 used
            // the full containing-block range (floats overlapped
            // visually); cycle 2 produces the correct flow-around
            // semantics that real documents (sidebar layouts, image-
            // with-text-wrap) need.
            //
            // The hypothetical block-Y for the available-range query
            // is the post-collapse cursor position the block WILL
            // occupy. We approximate it as `fragmentainer.UsedBlockSize`
            // here (before adding marginTop / topShift); the small
            // discrepancy when marginTop pushes the block past a float
            // is acceptable for cycle 2 MVP — the available range only
            // shrinks, never grows, so the worst case is a slightly
            // narrower block than strictly necessary. Cycle 3 will
            // refine to use the actual border-box top after collapse.
            var (availInlineStart, availInlineEnd) =
                _floatManager.GetAvailableInlineRange(
                    blockY: fragmentainer.UsedBlockSize,
                    containingStart: 0,
                    containingEnd: fragmentainer.ContentInlineSize);
            var availInlineSize = Math.Max(0, availInlineEnd - availInlineStart);

            // Per PR #23 review fix #5 — clamp the border-box inline
            // size to non-negative. Oversized left/right margins
            // (margin-left + margin-right > ContentInlineSize) would
            // otherwise produce a negative inline size + destabilize
            // downstream painting. The fragment records 0 inline-size
            // in that case; future cycle 3 will emit a layout
            // overflow diagnostic.
            // Per Phase 3 Task 8 cycle 2 — derive from the float-
            // adjusted available range, not the full containing block.
            var borderBoxInlineSize = Math.Max(0,
                availInlineSize - marginInlineStart - marginInlineEnd);

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

            // Per Phase 3 Task 8 cycle 1 post-PR-30 review (P1 #2) —
            // fold clearance into topShift. CSS clearance places
            // border-box-top at max(hypothetical-position, floatBottom):
            //   - Hypothetical position (post-collapse) =
            //     UsedBlockSize + topShift (border-box top with no
            //     clearance).
            //   - Cleared float bottom = `clearedFloatBottom` (computed
            //     above; equals UsedBlockSize when no clearance).
            // We need the SHIFT delta that achieves the max:
            //   topShift_with_clear = max(topShift,
            //     clearedFloatBottom - fragmentainer.UsedBlockSize).
            // When clearance does NOT trigger (clearedFloatBottom ==
            // UsedBlockSize), the delta is 0 + topShift is unchanged.
            // When clearance DOES trigger AND the float bottom is
            // ALREADY past the post-collapse hypothetical (i.e.,
            // marginTop alone wouldn't reach it), the shift snaps to
            // the float bottom — NOT marginTop on top of it. This is
            // the fix for the over-applied-marginTop bug.
            if (clearanceTriggered)
            {
                var floatBottomShift = clearedFloatBottom - fragmentainer.UsedBlockSize;
                if (floatBottomShift > topShift)
                {
                    topShift = floatBottomShift;
                }
            }

            // Per cycle 2c post-PR-29 review #7 — `marginBoxBlockSize`
            // (= `topShift + borderBoxBlockSize + marginEnd`) was
            // removed. Cycle 2c introduced the subtree-aware
            // `marginBoxBlockSizeForCursor` below which uses
            // `effectiveBlockSize` (max of own border-box + subtree
            // extent); the old non-subtree-aware variable became dead
            // code. The fit-check + cursor-advance now both use
            // `marginBoxBlockSizeForCursor`.

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
            //
            // Per post-Task-7 review (recommendation P2 #3) —
            // collapsed-margin overcounting. After adjacent-margin
            // collapse, the actual top contribution is `topShift`,
            // NOT raw `marginStart`. Pre-fix used `Math.Max(0, marginStart)`
            // which double-counts a margin that has already been
            // collapsed away with the previous block's marginBottom.
            // Example: prev block's marginBottom=80, current block's
            // marginTop=10. After collapse the top contribution is 0
            // (the 80 is already in fragmentainer.UsedBlockSize from
            // the prior emission; the collapsed gap of 80 replaces
            // both, so topShift = 80 - 80 = 0). Pre-fix counted +10
            // → false page break near a boundary. Post-fix uses
            // topShift, which is `marginStart` for the first block
            // on a page (no collapse) and `effectiveTopGap -
            // prevBlockMarginEnd` (= the actual additional advance)
            // for subsequent collapsed blocks.
            //
            // Per Phase 3 Task 7 cycle 2c — subtree-aware pagination.
            // Pre-cycle-2c the break-fit measure + cursor advance both
            // used the box's OWN `borderBoxBlockSize` (= border +
            // padding + Height-from-style). For containers with block-
            // level descendants whose VISUAL EXTENT exceeds the
            // parent's own height (auto-height + tall children, or
            // explicit-height + overflowing children), the outer
            // break decision didn't see the overflow + the painter
            // silently clipped on the next page. Post-fix the measure
            // is the SUBTREE visual extent — the maximum block-axis
            // position reached by ANY descendant, measured from the
            // box's border-box top. For leaf boxes this equals
            // `borderBoxBlockSize`; for containers with overflowing
            // children it dominates so:
            //   (a) the resolver sees the FULL visual footprint +
            //       can dispatch a break-before that pushes the
            //       entire subtree to the next page (cleanly, per
            //       CSS Fragmentation L3 §5).
            //   (b) the cursor advances past the overflow, so a
            //       sibling AFTER the overflowing parent doesn't
            //       visually overlap with the overflow.
            //
            // <b>Cycle 2c MVP scope.</b> The pre-measure produces
            // ATOMIC pagination — the entire subtree commits on one
            // page (with overflow + diagnostic if it doesn't fit any
            // single page) OR moves wholly to the next page via
            // break-before. Real CSS allows breaks INSIDE a subtree
            // (mid-split, where the parent's first half sits on page
            // 1 + the rest continues on page 2); that requires
            // recursive continuation tokens + break consultation
            // inside `EmitBlockSubtreeRecursive`. Deferred to cycle
            // 2d.
            var subtreeBlockExtent = MeasureSubtreeVisualBlockExtent(child, cancellationToken);
            // The "effective" border-box block size for pagination +
            // cursor purposes: max of own border-box + subtree extent.
            // Equals borderBoxBlockSize for leaves; dominates for
            // containers with overflowing children.
            var effectiveBlockSize = Math.Max(borderBoxBlockSize, subtreeBlockExtent);
            var visualBlockExtent = effectiveBlockSize
                + Math.Max(0, topShift)
                + Math.Max(0, marginEnd);
            // Cycle-2c cursor advance: use subtree-aware extent so
            // siblings of an overflowing parent don't overlap the
            // overflow. Falls back to borderBoxBlockSize for leaves
            // (where subtree extent == own size).
            var marginBoxBlockSizeForCursor = topShift + effectiveBlockSize + marginEnd;
            var chunkForBreakCheck = Math.Max(
                Math.Max(0, marginBoxBlockSizeForCursor),
                visualBlockExtent);

            // Per PR #22 review fix #2 — capture a checkpoint at the
            // candidate-break boundary BEFORE consulting the resolver.
            // The resolver may return Rewind, naming this checkpoint
            // for the rollback target.
            //
            // Per Phase 3 Task 8 cycle 1 post-PR-30 review (P0 #1) —
            // push the live FloatManager snapshot into the
            // fragmentainer's slot BEFORE the checkpoint captures it.
            // `LayoutCheckpoint.Capture` snapshots
            // `fragmentainer.FloatManagerState`; if the slot is null
            // (the pre-fix state), the captured snapshot is also null
            // + `RestoreInto` resets the live `_floatManager` to
            // empty on rewind, dropping legitimate float records that
            // shouldn't have been rewound. Writing the live state
            // here ensures the captured snapshot is authoritative.
            fragmentainer.FloatManagerState = _floatManager.Snapshot();

            var newLease = LayoutCheckpointPool.Rent();
            newLease.Checkpoint!.Capture(
                fragmentainer,
                in layout,
                fragmentOutputCursor: _sink.Cursor,
                // Per PR #24/#25 Copilot review — use the actual last-
                // emitted index, not `childIdx - 1` which mis-names a
                // skipped non-block-level predecessor as "emitted".
                // The contract is "last fully-emitted box index";
                // skipped children are not emitted.
                lastEmittedChildIndex: lastEmittedIdx,
                incomingContinuation: _incomingContinuation,
                pageCounterValue: layout.ReadCounter("page"),
                // Per post-Task-7 review (P2 #5) — capture the margin-
                // collapse frontier on the checkpoint itself so a rewind
                // to THIS specific checkpoint restores the right
                // previous-block bottom margin (instead of the layouter's
                // latest fields which may correspond to a different
                // candidate-break boundary).
                prevBlockMarginEnd: prevBlockMarginEnd,
                hasAdjoiningBlockOnEntry: hasPriorAdjoiningBlock);
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
                //
                // Per post-Task-7 review (P2 #5) — read the frontier
                // from the chosen rewind-target checkpoint, NOT from
                // the layouter's current state. The current state
                // corresponds to the just-registered checkpoint
                // (which the resolver typically picks); but if the
                // resolver returns an OLDER checkpoint as the rewind
                // target (a future optimizer-aware path retains
                // multiple checkpoints), the layouter's "current"
                // frontier is the wrong frontier. The checkpoint's
                // own captured fields are authoritative.
                _prevBlockMarginEndAfterRewind = decision.RewindTo.PrevBlockMarginEnd;
                _hasPriorAdjoiningBlockAfterRewind = decision.RewindTo.HasAdjoiningBlockOnEntry;
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
                    // Per post-Task-7 review (P1 #2) — prefer the
                    // ambient sink on the layout context (set by the
                    // coordinator's Run) over the constructor-injected
                    // sink. Falls back to the constructor sink for
                    // direct-construction callers (tests, integration
                    // helpers) that don't go through the coordinator.
                    var diagSink = layout.Diagnostics ?? _diagnostics;
                    // Per cycle 2c post-PR-29 review #9 — diagnostic
                    // wording updated for subtree overflow. Pre-fix
                    // said "block is taller than the fragmentainer"
                    // which was misleading when the block ITSELF fit
                    // but its descendants overflowed via cycle 2c's
                    // subtree-aware measure. Now reports both own
                    // border-box size + measured subtree extent so
                    // consumers can distinguish the two cases.
                    OptimizingBreakResolver.SafeEmit(diagSink, new PaginateDiagnostic(
                        PaginateDiagnosticCodes.PaginationForcedOverflow001,
                        $"BlockLayouter: forced overflow on fragmentainer page index "
                        + $"{fragmentainer.PageIndex}, child index {childIdx} — "
                        + $"block or subtree visual extent (own border-box="
                        + $"{borderBoxBlockSize:0.##}, subtree extent="
                        + $"{subtreeBlockExtent:0.##}) is taller than the "
                        + $"fragmentainer (block-size={fragmentainer.BlockSize:0.##}). "
                        + "Committed anyway to make pagination progress.",
                        PaginateDiagnosticSeverity.Warning));
                    // First block on the (possibly resumed) page →
                    // topShift = effectiveTopGap = marginStart; no
                    // collapse-arithmetic needed.
                    var forcedOverflowChildBlockOffset =
                        fragmentainer.UsedBlockSize + topShift;
                    // Per Phase 3 Task 8 cycle 2 — flow around floats:
                    // a left float pushes the block's start past it.
                    // The available-range start (= post-left-floats edge)
                    // anchors the in-flow block; the marginInlineStart
                    // is added on top.
                    var forcedOverflowInlineOffset = availInlineStart + marginInlineStart;
                    _sink.Emit(new BoxFragment(
                        Box: child,
                        InlineOffset: forcedOverflowInlineOffset,
                        BlockOffset: forcedOverflowChildBlockOffset,
                        InlineSize: borderBoxInlineSize,
                        BlockSize: borderBoxBlockSize));
                    // Per Phase 3 Task 7 cycle 2b — recursively emit
                    // fragments for the child's block-level descendants.
                    // The painter sees the full subtree on the
                    // committed page even though the child overflowed
                    // the fragmentainer (forced-overflow forward
                    // progress). Per cycle-2b post-PR-28 review #2 —
                    // CT threaded through to abort deep traversals.
                    EmitBlockSubtreeRecursive(
                        child,
                        parentBlockOffset: forcedOverflowChildBlockOffset,
                        parentInlineOffset: forcedOverflowInlineOffset,
                        parentInlineSize: borderBoxInlineSize,
                        cancellationToken: cancellationToken,
                        depth: 1);
                    // Per PR #23 review fix #4 — clamp UsedBlockSize
                    // to non-negative so subsequent BreakOpportunity
                    // construction doesn't trip CostModel's guard.
                    // Per cycle 2c — advance by `marginBoxBlockSizeForCursor`
                    // (subtree-aware) so siblings AFTER an overflowing
                    // subtree don't overlap with the overflow.
                    fragmentainer.UsedBlockSize = Math.Max(0,
                        fragmentainer.UsedBlockSize + marginBoxBlockSizeForCursor);
                    emittedThisAttempt++;
                    // Per PR #24/#25 Copilot review — track actual
                    // last-emitted index for accurate checkpoint
                    // metadata. The next checkpoint capture (or the
                    // rewind retry's resume point) reads this rather
                    // than `childIdx - 1` (which would mis-credit
                    // skipped non-block-level predecessors).
                    lastEmittedIdx = childIdx;

                    // Resume on the NEXT child (childIdx+1) so progress
                    // is monotonic. ConsumedBlockSize = cumulative
                    // across-pages per Copilot #1 + the field's docs.
                    //
                    // Per PR #24/#25 Copilot review — avoid double-
                    // counting BreakInsideAvoidViolation. CostModel.Score
                    // already adds the penalty when
                    // `ChunkBlockSize > contentBlockSize` (oversized
                    // chunk), so `decision.Cost` for a genuinely
                    // oversized block already includes it. The forced-
                    // overflow penalty MUST still apply for the
                    // alternative case where the chunk would fit on a
                    // FRESH page but not the remaining space — i.e.,
                    // we're forcing a break that the spec says should
                    // be avoided. Only add the penalty when Score
                    // didn't already.
                    var alreadyOverflowPenalized =
                        chunkForBreakCheck > fragmentainer.BlockSize;
                    var forcedOverflowCost = alreadyOverflowPenalized
                        ? decision.Cost
                        : decision.Cost + CostModel.BreakInsideAvoidViolation;
                    return LayoutAttemptResult.PageComplete(
                        new BlockContinuation(
                            ResumeAtChild: childIdx + 1,
                            ConsumedBlockSize: priorPagesConsumed
                                + (fragmentainer.UsedBlockSize - _pageStartUsedBlockSize)),
                        cost: forcedOverflowCost);
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
            // Per Phase 3 Task 8 cycle 2 — flow around floats: anchor
            // the in-flow block at the post-left-floats edge.
            var inFlowInlineOffset = availInlineStart + marginInlineStart;
            _sink.Emit(new BoxFragment(
                Box: child,
                InlineOffset: inFlowInlineOffset,
                BlockOffset: blockOffset,
                InlineSize: borderBoxInlineSize,
                BlockSize: borderBoxBlockSize));

            // Per Phase 3 Task 7 cycle 2b — recursively emit fragments
            // for the child's block-level descendants. Cycle 1 / 2
            // emitted only top-level children of `_rootBox`; with
            // recursion, a `div > p > span` tree (where span is block-
            // level) emits div, p, AND span fragments at correctly
            // nested offsets relative to each parent's content area.
            // See EmitBlockSubtreeRecursive's XML doc for the cycle 2b
            // scope + deferrals.
            // Per cycle-2b post-PR-28 review #2 — CT threaded through
            // to abort deep traversals; depth=1 since `child` is the
            // first nesting level under `_rootBox`.
            EmitBlockSubtreeRecursive(
                child,
                parentBlockOffset: blockOffset,
                parentInlineOffset: inFlowInlineOffset,
                parentInlineSize: borderBoxInlineSize,
                cancellationToken: cancellationToken,
                depth: 1);

            // Per PR #23 review fix #4 — clamp UsedBlockSize to
            // non-negative. A valid block with very negative margins
            // could otherwise drive the cursor below zero, then the
            // next BreakOpportunity.UsedBlockSize would trip
            // CostModel.Score's non-negative guard.
            // Per cycle 2c — advance by `marginBoxBlockSizeForCursor`
            // (subtree-aware) so siblings AFTER an overflowing
            // subtree don't overlap with the overflow.
            fragmentainer.UsedBlockSize = Math.Max(0,
                fragmentainer.UsedBlockSize + marginBoxBlockSizeForCursor);
            prevBlockMarginEnd = marginEnd;
            hasPriorAdjoiningBlock = true;
            emittedThisAttempt++;
            // Per PR #24/#25 Copilot review — track actual last-emitted
            // index. See the forced-overflow path for the contract
            // rationale.
            lastEmittedIdx = childIdx;
        }

        // All children laid out — no more pages needed.
        return LayoutAttemptResult.AllDone(cost: 0);
    }

    /// <summary>Per Phase 3 Task 7 cycle 2b — recursively emit fragments
    /// for <paramref name="parent"/>'s block-level descendants. Called
    /// AFTER the parent's own fragment is emitted; offsets are relative
    /// to <paramref name="parent"/>'s content area (fragmentainer
    /// coordinates).
    ///
    /// <para><b>Cycle 2b scope.</b> The recursion emits fragments so the
    /// painter (Phase 4) sees the full box tree. Geometric semantics:</para>
    /// <list type="bullet">
    ///   <item>Each descendant's <see cref="BoxFragment.BlockOffset"/>
    ///   is its border-box top edge in fragmentainer coordinates,
    ///   computed by stacking the descendant's siblings inside the
    ///   parent's content area with adjacent-margin collapse per
    ///   CSS 2.1 §8.3.1.</item>
    ///   <item><see cref="BoxFragment.InlineOffset"/> is the descendant's
    ///   border-box inline-start edge (= parent's content-area inline
    ///   offset + child's marginInlineStart).</item>
    ///   <item><see cref="BoxFragment.InlineSize"/> = parent's content-
    ///   area inline size - child's inline margins (clamped non-negative
    ///   per PR #23 review fix #5).</item>
    ///   <item><see cref="BoxFragment.BlockSize"/> = child's border-box
    ///   block size (border + padding + Height-from-style; auto Height
    ///   reads as 0, deferred to cycle 3).</item>
    /// </list>
    ///
    /// <para><b>Cycle 2c subtree-aware cursor advance (this revision).</b>
    /// Per cycle 2c post-PR-29 review #2 — the recursion now also calls
    /// <see cref="MeasureSubtreeVisualBlockExtent(Box, CancellationToken)"/>
    /// for each child + advances the inner cursor by
    /// <c>max(childBorderBox, childSubtreeExtent)</c>, mirroring the
    /// outer loop's behavior. Pre-cycle-2c-fix the recursion advanced
    /// by `childBorderBoxBlockSize` only — a sibling AFTER an
    /// overflowing nested grandchild would visually overlap with the
    /// overflow even though the outer pagination correctly reserved
    /// page space.</para>
    ///
    /// <para><b>Remaining deferrals.</b></para>
    /// <list type="bullet">
    ///   <item><b>Auto-height resolution</b> per CSS 2.1 §10.6.3 — when
    ///   a container has <c>height: auto</c>, its content area should
    ///   resolve to the children's stack height. Cycle 2b/2c use
    ///   <see cref="ComputedStyleLayoutExtensions.ReadLengthPxOrZero"/>
    ///   which returns 0 for auto, so a container with auto height +
    ///   children renders with a 0-sized parent-fragment but the
    ///   children DO emit at their correct offsets. The painter sees
    ///   a degenerate parent rectangle but the descendants are
    ///   correctly positioned relative to it. Cycle 3 wires real
    ///   auto-height + percentage resolution.</item>
    ///   <item><b>True mid-subtree pagination splits</b> — cycle 2c
    ///   MVP makes subtree-aware ATOMIC pagination work (oversized
    ///   subtree pushed wholly to next page OR forced-overflowed). Real
    ///   CSS allows breaks INSIDE a subtree (parent's first half on
    ///   page 1 + parent's second half on page 2). Requires recursive
    ///   continuation tokens (<c>BlockContinuation.NestedContinuation</c>) +
    ///   break consultation inside this method + recursive resume on
    ///   retry. Cycle 2d work.</item>
    ///   <item><b>Parent / first-child margin collapse</b> per CSS 2.1
    ///   §8.3.1 — when parent has no border / padding / non-empty
    ///   children before, the parent's marginStart collapses with the
    ///   first child's marginStart. Requires BFC-root detection
    ///   (cycle 3).</item>
    /// </list></summary>
    // TODO (cycle 2c / cycle 3) — per cycle-2b post-PR-28 review #6,
    // extract duplicated style reads + border-box computation +
    // topShift formula into a `BlockChildMetrics` readonly struct
    // shared between this method and the outer AttemptLayout loop.
    // Deferred for now: doing it in this PR would balloon the diff
    // and obscure the correctness fixes. Cycle 2c will add a third
    // call site (cross-subtree break propagation) where the
    // duplication starts hurting; the refactor lands cleanly there.
    private void EmitBlockSubtreeRecursive(
        Box parent,
        double parentBlockOffset,
        double parentInlineOffset,
        double parentInlineSize,
        CancellationToken cancellationToken,
        int depth)
    {
        // Per cycle-2b post-PR-28 review #2 + Copilot #1 — DoS
        // protection. Untrusted HTML can construct pathologically
        // deep box trees that would StackOverflow + halt the
        // process. Throw a typed exception at a fixed depth so the
        // surrounding pipeline can degrade gracefully (catches
        // bubble up to SafeResourceLoader-style typed-failure
        // handling at the assembly boundary).
        if (depth > MaxRecursionDepth)
        {
            throw new InvalidOperationException(
                $"BlockLayouter recursion depth exceeded {MaxRecursionDepth}; "
                + "pathologically deep box tree. This is a DoS guard against "
                + "untrusted HTML; legitimate documents rarely exceed depth "
                + "32. If you hit this with a real document, please file an "
                + "issue with the box tree.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        // Per cycle-2b post-PR-28 review #3 — only block-flow
        // containers' inner geometry is owned by BlockLayouter. Other
        // block-level kinds (Table, Flex, Grid, BlockReplacedElement)
        // were emitted by the caller as a placeholder fragment for the
        // outer-display contract, but their INNER content belongs to a
        // dedicated layouter (TableLayouter / FlexLayouter /
        // GridLayouter / atomic). Skip the recursion for those kinds —
        // the dedicated layouter will fill in the inner geometry when
        // it ships (Phase 3 Tasks 8-12).
        if (!IsBlockFlowContainerOwnedByBlockLayouter(parent))
        {
            return;
        }

        // Parent's content-area corner in fragmentainer coordinates.
        var pBorderStart = parent.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
        var pPaddingStart = parent.Style.ReadLengthPxOrZero(PropertyId.PaddingTop);
        var pBorderInlineStart = parent.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth);
        var pPaddingInlineStart = parent.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft);
        var pBorderInlineEnd = parent.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth);
        var pPaddingInlineEnd = parent.Style.ReadLengthPxOrZero(PropertyId.PaddingRight);

        var contentTop = parentBlockOffset + pBorderStart + pPaddingStart;
        var contentLeft = parentInlineOffset + pBorderInlineStart + pPaddingInlineStart;
        var contentInlineSize = Math.Max(0, parentInlineSize
            - pBorderInlineStart - pPaddingInlineStart
            - pBorderInlineEnd - pPaddingInlineEnd);

        // Stack children with adjacent-margin collapse (same formula
        // as the outer AttemptLayout loop; CSS 2.1 §8.3.1).
        // Per cycle-2b post-PR-28 review #1 + Copilot #2 — childCursor
        // is a SIGNED visual cursor (no Math.Max(0, ...)). Unlike the
        // outer loop's UsedBlockSize (which feeds BreakOpportunity
        // validation that requires non-negative measures), the inner
        // childCursor is purely positional — clamping it to 0 would
        // prevent legitimate negative-margin overlap between nested
        // siblings (CSS 2.1 §8.3.1 explicitly allows negative margins
        // to produce overlap; the painter's z-order then handles which
        // box paints on top).
        double childCursor = 0;  // block-axis position within parent's content area
        double prevMarginEnd = 0;
        var hasPrior = false;

        foreach (var child in parent.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!child.IsBlockLevel)
            {
                // Non-block content (inline / atomic) breaks margin
                // adjacency per CSS 2.1 §8.3.1 (PR #23 fix #3).
                // Cycle 2b doesn't yet lay out inline content (Task 10
                // domain); reset the collapse chain so the next block's
                // marginTop is honored without collapsing.
                hasPrior = false;
                prevMarginEnd = 0;
                continue;
            }

            // Per Phase 3 Task 8 cycle 1 post-PR-30 review (P1 #4) —
            // nested float dispatch. Pre-fix this recursion treated
            // floated descendants as in-flow blocks, mis-emitting
            // them at the wrong offset + missing them from
            // FloatManager. Cycle 1 assumes no nested BFCs (cycle 3
            // will detect display:flow-root / overflow:hidden / etc.),
            // so nested floats register with the SAME _floatManager
            // as top-level floats. Their authored Y in BFC coords =
            // contentTop + childCursor (where contentTop is in BFC
            // coords + childCursor is parent-relative).
            //
            // Layout context for the diagnostic sink isn't directly
            // available here (we'd need to thread it). Cycle 1
            // accepts that nested floats use the constructor-injected
            // diagnostics fallback only; cycle 2 will refactor to
            // thread layout through the recursion.
            var nestedFloatSide = child.Style.ReadFloatSide();
            if (nestedFloatSide.HasValue)
            {
                EmitNestedFloat(
                    child,
                    nestedFloatSide.Value,
                    currentBfcY: contentTop + childCursor,
                    cancellationToken);
                // Nested float doesn't advance childCursor or affect
                // collapse chain (out-of-flow per CSS 2.2 §9.5).
                continue;
            }

            // Per Phase 3 Task 8 cycle 1 post-PR-30 review (P1 #4) —
            // nested clear. A non-floating block inside the recursion
            // with `clear: ...` advances its top edge past the float
            // bottom IN BFC COORDINATES. The local cursor (parent-
            // relative) needs to be translated back: BFC-Y =
            // contentTop + childCursor; cleared-BFC-Y = max(BFC-Y,
            // float bottom on relevant side); local-clear-Y =
            // cleared-BFC-Y - contentTop. The childCursor advances to
            // local-clear-Y; collapse chain resets.
            var nestedClearKind = child.Style.ReadClearKind();
            if (nestedClearKind != ClearKind.None)
            {
                var localBfcY = contentTop + childCursor;
                var clearedBfcY = _floatManager.GetClearedBlockY(
                    localBfcY, nestedClearKind);
                if (clearedBfcY > localBfcY)
                {
                    childCursor = clearedBfcY - contentTop;
                    hasPrior = false;
                    prevMarginEnd = 0;
                }
            }

            var marginStart = child.Style.ReadLengthPxOrZero(PropertyId.MarginTop);
            var marginEnd = child.Style.ReadLengthPxOrZero(PropertyId.MarginBottom);
            var marginInlineStart = child.Style.ReadLengthPxOrZero(PropertyId.MarginLeft);
            var marginInlineEnd = child.Style.ReadLengthPxOrZero(PropertyId.MarginRight);
            var borderStart = child.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
            var borderEnd = child.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
            var paddingStart = child.Style.ReadLengthPxOrZero(PropertyId.PaddingTop);
            var paddingEnd = child.Style.ReadLengthPxOrZero(PropertyId.PaddingBottom);
            var contentBlock = child.Style.ReadLengthPxOrZero(PropertyId.Height);

            var childBorderBoxBlockSize = borderStart + paddingStart + contentBlock
                + paddingEnd + borderEnd;
            var childBorderBoxInlineSize = Math.Max(0,
                contentInlineSize - marginInlineStart - marginInlineEnd);

            // Per cycle 2c post-PR-29 review #2 — measure/emit cursor
            // mismatch fix. Pre-fix the recursion advanced by
            // childBorderBoxBlockSize while the OUTER loop (cycle 2c)
            // advanced by max(borderBox, subtreeExtent). Result: nested
            // siblings could overlap an overflowing nested grandchild
            // even though the OUTER pagination correctly reserved space.
            // Post-fix: also call MeasureSubtreeVisualBlockExtent for
            // each child + use max(borderBox, subtreeExtent) for the
            // cursor advance, mirroring the outer loop's behavior.
            //
            // Perf note: the measure runs at every recursion level for
            // each child — O(N²) work for a tree of depth N. Acceptable
            // for cycle 2c since legitimate documents are <30 deep.
            // Cycle 2d (mid-splits) will redesign to share extents
            // between measure + emit (avoiding the duplicate walk).
            var childSubtreeExtent = MeasureSubtreeVisualBlockExtentRecursive(
                child, cancellationToken, depth + 1);
            var childEffectiveBlockSize = Math.Max(
                childBorderBoxBlockSize, childSubtreeExtent);

            double topShift;
            if (!hasPrior)
            {
                topShift = marginStart;
            }
            else
            {
                var gap = MarginCollapse.Collapse(prevMarginEnd, marginStart);
                topShift = gap - prevMarginEnd;
            }

            var childBlockOffset = contentTop + childCursor + topShift;
            var childInlineOffset = contentLeft + marginInlineStart;

            // Fragment records the BORDER box (not subtree extent) —
            // the subtree extent is for cursor advance only. Per
            // cycle 2b, the painter sees the border box; descendants
            // that overflow are emitted as their own fragments at
            // their own offsets.
            _sink.Emit(new BoxFragment(
                Box: child,
                InlineOffset: childInlineOffset,
                BlockOffset: childBlockOffset,
                InlineSize: childBorderBoxInlineSize,
                BlockSize: childBorderBoxBlockSize));

            // Recurse — emit grandchildren relative to this child's
            // content area. The recursion's own predicate gate skips
            // walking INTO non-flow block kinds (Table/Flex/Grid/etc.).
            EmitBlockSubtreeRecursive(
                child,
                parentBlockOffset: childBlockOffset,
                parentInlineOffset: childInlineOffset,
                parentInlineSize: childBorderBoxInlineSize,
                cancellationToken: cancellationToken,
                depth: depth + 1);

            // Per cycle-2b post-PR-28 review #1 — SIGNED cursor advance.
            // Negative margins legitimately produce overlap; clamping
            // to 0 here would prevent a later sibling from being
            // positioned above the prior sibling.
            // Per cycle 2c post-PR-29 review #2 — use SUBTREE-AWARE
            // effective size (max of own border-box + descendant
            // overflow) so siblings don't overlap with a child's
            // grandchild overflow.
            childCursor = childCursor + topShift + childEffectiveBlockSize + marginEnd;
            prevMarginEnd = marginEnd;
            hasPrior = true;
        }
    }

    /// <summary>Per Phase 3 Task 8 cycle 1 — emit a float fragment.
    /// Reads the float child's box-model extents (margins, border,
    /// padding, height), places via <see cref="FloatManager.PlaceFloat"/>,
    /// emits the fragment + recursively emits its block-level
    /// descendants relative to the placed position.
    ///
    /// <para>The float DOES NOT participate in the in-flow margin-
    /// collapse chain or advance the in-flow cursor. Per CSS 2.2 §9.5
    /// floats are out-of-flow but still belong to their parent's
    /// formatting context for the purposes of clearing + flow-around
    /// (cycle 2 work).</para></summary>
    /// <summary>Per Phase 3 Task 8 cycle 1 post-PR-30 review (P1 #4) —
    /// nested-float emission helper called from
    /// <see cref="EmitBlockSubtreeRecursive"/>. Same logic as
    /// <see cref="EmitFloat"/> but doesn't need <c>ref LayoutContext</c>
    /// for diagnostics (the recursion doesn't receive layout). Cycle 2
    /// will unify into a single helper after threading layout through
    /// the recursion.</summary>
    private void EmitNestedFloat(
        Box child,
        FloatSide side,
        double currentBfcY,
        CancellationToken cancellationToken)
    {
        // Box-model reads — same set as the in-flow path.
        var marginStart = child.Style.ReadLengthPxOrZero(PropertyId.MarginTop);
        var marginEnd = child.Style.ReadLengthPxOrZero(PropertyId.MarginBottom);
        var marginInlineStart = child.Style.ReadLengthPxOrZero(PropertyId.MarginLeft);
        var marginInlineEnd = child.Style.ReadLengthPxOrZero(PropertyId.MarginRight);
        var borderStart = child.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
        var borderEnd = child.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
        var borderInlineStart = child.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth);
        var borderInlineEnd = child.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth);
        var paddingStart = child.Style.ReadLengthPxOrZero(PropertyId.PaddingTop);
        var paddingEnd = child.Style.ReadLengthPxOrZero(PropertyId.PaddingBottom);
        var paddingInlineStart = child.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft);
        var paddingInlineEnd = child.Style.ReadLengthPxOrZero(PropertyId.PaddingRight);
        var contentBlock = child.Style.ReadLengthPxOrZero(PropertyId.Height);
        var contentInline = child.Style.ReadLengthPxOrZero(PropertyId.Width);

        var borderBoxBlockSize = borderStart + paddingStart + contentBlock
            + paddingEnd + borderEnd;
        var borderBoxInlineSize = Math.Max(0,
            borderInlineStart + paddingInlineStart + contentInline
            + paddingInlineEnd + borderInlineEnd);
        var marginBoxBlockSize = Math.Max(0,
            marginStart + borderBoxBlockSize + marginEnd);
        var marginBoxInlineSize = Math.Max(0,
            marginInlineStart + borderBoxInlineSize + marginInlineEnd);

        // For BFC-wide containing block — see EmitFloat doc comment
        // for the cycle 1 simplification. Pass the nested-relative
        // BFC Y as currentBlockY.
        var (placedInline, placedBlock) = _floatManager.PlaceFloat(
            side,
            inlineSize: marginBoxInlineSize,
            blockSize: marginBoxBlockSize,
            containingInlineStart: 0,
            // Outer fragmentainer's ContentInlineSize from the
            // layouter's _rootBox would be ideal, but the recursion
            // doesn't have direct access. Use the root box's content
            // inline size — derived from the rootBox style — as a
            // proxy. Cycle 2 will refine when the inline-range API
            // lands.
            containingInlineEnd: GetBfcContentInlineSize(),
            currentBlockY: currentBfcY);

        var fragmentInlineOffset = placedInline + marginInlineStart;
        var fragmentBlockOffset = placedBlock + marginStart;

        _sink.Emit(new BoxFragment(
            Box: child,
            InlineOffset: fragmentInlineOffset,
            BlockOffset: fragmentBlockOffset,
            InlineSize: borderBoxInlineSize,
            BlockSize: borderBoxBlockSize));

        // Recurse for descendants of the nested float (e.g., a div
        // inside a float that has its own block-level content).
        EmitBlockSubtreeRecursive(
            child,
            parentBlockOffset: fragmentBlockOffset,
            parentInlineOffset: fragmentInlineOffset,
            parentInlineSize: borderBoxInlineSize,
            cancellationToken: cancellationToken,
            depth: 1);
    }

    /// <summary>Per Phase 3 Task 8 cycle 1 post-PR-30 review (P1 #4) —
    /// returns the BFC-wide content inline size (= the outer
    /// fragmentainer's ContentInlineSize, captured at the most recent
    /// AttemptLayout entry). Used by nested-float placement so the
    /// containing block matches the BFC root rather than the nested
    /// parent's content area. Cycle 2 will refine to use the actual
    /// containing block per CSS 2.2 §10.1.</summary>
    private double GetBfcContentInlineSize() => _bfcContentInlineSize;

    /// <summary>Per Phase 3 Task 8 cycle 2 — peek at whether the float
    /// <paramref name="child"/> would overflow the current fragmentainer
    /// if placed at the current cursor position. Used by the cross-
    /// fragmentainer float deferral path BEFORE the actual placement.
    ///
    /// <para>Computes the float's hypothetical margin-box bottom from
    /// box-model reads + the would-be Y from
    /// <see cref="FloatManager.PlaceFloat"/>'s same-side stacking rule
    /// (= max(currentBlockY, max-bottom-of-active-floats-on-side)).
    /// Returns true when that bottom exceeds <c>fragmentainer.BlockSize</c>.</para>
    ///
    /// <para>This duplicates a few box-model reads that EmitFloat will
    /// repeat after the deferral check passes; the deferral path is
    /// rare so the duplication is fine. Cycle 3 will consolidate via
    /// the BlockChildMetrics helper that's been deferred since cycle 2b.</para></summary>
    private bool WouldFloatOverflow(Box child, FragmentainerContext fragmentainer)
    {
        // Compute the float's margin-box block-size from the box-model
        // reads (same as EmitFloat).
        var marginStart = child.Style.ReadLengthPxOrZero(PropertyId.MarginTop);
        var marginEnd = child.Style.ReadLengthPxOrZero(PropertyId.MarginBottom);
        var borderStart = child.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
        var borderEnd = child.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
        var paddingStart = child.Style.ReadLengthPxOrZero(PropertyId.PaddingTop);
        var paddingEnd = child.Style.ReadLengthPxOrZero(PropertyId.PaddingBottom);
        var contentBlock = child.Style.ReadLengthPxOrZero(PropertyId.Height);
        var marginBoxBlockSize = Math.Max(0,
            marginStart + (borderStart + paddingStart + contentBlock + paddingEnd + borderEnd) + marginEnd);

        // Peek at the would-be Y. PlaceFloat's stacking rule = max of
        // currentBlockY + same-side max-bottom. The float's authored
        // currentBlockY in the outer-loop dispatch path is
        // fragmentainer.UsedBlockSize. We don't peek into FloatManager's
        // private cache here (test isolation); the existing public
        // ActiveFloats lets us derive it. Since stacked Y is monotonic
        // non-decreasing, using just the cursor without same-side
        // stacking is a LOWER bound — meaning we may MISS overflow
        // detection in the rare case where multiple floats already
        // stack BELOW the cursor. The existing diagnostic (P1 #3 from
        // cycle 1) catches that fallback case.
        var hypotheticalY = fragmentainer.UsedBlockSize;
        return hypotheticalY + marginBoxBlockSize > fragmentainer.BlockSize;
    }

    /// <summary>Captured at AttemptLayout entry; used by
    /// EmitNestedFloat as the BFC-wide containing inline size.</summary>
    private double _bfcContentInlineSize;

    private void EmitFloat(
        Box child,
        FloatSide side,
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        CancellationToken cancellationToken,
        double? currentBfcYOverride = null)
    {
        // Per Phase 3 Task 8 cycle 1 post-PR-30 review (P1 #4) —
        // optional override for the float's authored block-axis
        // position in BFC coords. The outer loop passes null
        // (=> uses fragmentainer.UsedBlockSize); the nested-float
        // dispatch in EmitBlockSubtreeRecursive passes the local
        // BFC-relative cursor so a float inside a nested div is
        // placed at the right vertical position.
        var currentBfcY = currentBfcYOverride ?? fragmentainer.UsedBlockSize;
        // Box-model reads — same set as the in-flow path.
        var marginStart = child.Style.ReadLengthPxOrZero(PropertyId.MarginTop);
        var marginEnd = child.Style.ReadLengthPxOrZero(PropertyId.MarginBottom);
        var marginInlineStart = child.Style.ReadLengthPxOrZero(PropertyId.MarginLeft);
        var marginInlineEnd = child.Style.ReadLengthPxOrZero(PropertyId.MarginRight);
        var borderStart = child.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
        var borderEnd = child.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
        var borderInlineStart = child.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth);
        var borderInlineEnd = child.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth);
        var paddingStart = child.Style.ReadLengthPxOrZero(PropertyId.PaddingTop);
        var paddingEnd = child.Style.ReadLengthPxOrZero(PropertyId.PaddingBottom);
        var paddingInlineStart = child.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft);
        var paddingInlineEnd = child.Style.ReadLengthPxOrZero(PropertyId.PaddingRight);
        var contentBlock = child.Style.ReadLengthPxOrZero(PropertyId.Height);
        var contentInline = child.Style.ReadLengthPxOrZero(PropertyId.Width);

        var borderBoxBlockSize = borderStart + paddingStart + contentBlock
            + paddingEnd + borderEnd;
        // Per cycle 1 post-PR-30 review (Copilot #2) — border-box
        // inline size includes inline-axis border + padding. Pre-fix
        // used Math.Max(0, contentInline) which was the CONTENT-box
        // size, mis-reporting the float's footprint to FloatManager
        // (any non-zero border or padding would be missing from the
        // float's margin-box width used for `clear` + future inline-
        // range calculations). Cycle 3 will resolve `auto` Width
        // against shrink-to-fit per CSS 2.2 §10.3.5; cycle 1 uses
        // explicit Width or 0.
        var borderBoxInlineSize = Math.Max(0,
            borderInlineStart + paddingInlineStart + contentInline
            + paddingInlineEnd + borderInlineEnd);

        // Outer (margin-box) extents drive FloatManager placement +
        // determine the float's footprint for `clear` computation.
        var marginBoxBlockSize = Math.Max(0,
            marginStart + borderBoxBlockSize + marginEnd);
        var marginBoxInlineSize = Math.Max(0,
            marginInlineStart + borderBoxInlineSize + marginInlineEnd);

        // Cycle 1 — containing block is the BFC content area
        // (inlineStart=0, inlineEnd=fragmentainer.ContentInlineSize).
        // Per cycle 1 post-PR-30 review (P1 #4) — even nested floats
        // use the BFC-wide containing block in cycle 1 (visually
        // simpler; cycle 2 will refine for true containing-block
        // alignment when GetAvailableInlineRange lands).
        var (placedInline, placedBlock) = _floatManager.PlaceFloat(
            side,
            inlineSize: marginBoxInlineSize,
            blockSize: marginBoxBlockSize,
            containingInlineStart: 0,
            containingInlineEnd: fragmentainer.ContentInlineSize,
            currentBlockY: currentBfcY);

        // Per Phase 3 Task 8 cycle 1 post-PR-30 review (P1 #3) —
        // overflow diagnostic. Cycle 1 doesn't yet move floats to
        // the next fragmentainer (Fragmentation L3 §5 — cycle 2);
        // when a float would extend past the fragmentainer's bottom,
        // we still EMIT it (don't break the document) but emit
        // PAGINATION-FORCED-OVERFLOW-001 so consumers can detect
        // the silent-truncation case.
        var floatBottom = placedBlock + marginBoxBlockSize;
        if (floatBottom > fragmentainer.BlockSize)
        {
            var diagSink = layout.Diagnostics ?? _diagnostics;
            OptimizingBreakResolver.SafeEmit(diagSink, new PaginateDiagnostic(
                PaginateDiagnosticCodes.PaginationForcedOverflow001,
                $"BlockLayouter: float overflow on fragmentainer page index "
                + $"{fragmentainer.PageIndex} — float margin-box bottom="
                + $"{floatBottom:0.##} exceeds fragmentainer block-size="
                + $"{fragmentainer.BlockSize:0.##}. Cycle 1 emits the float "
                + "anyway; cycle 2 will move overflowing floats to the next "
                + "fragmentainer per CSS Fragmentation L3 §5.",
                PaginateDiagnosticSeverity.Warning));
        }

        // Per cycle 1 post-PR-30 review (Copilot #4) — border-box
        // origin = margin-box origin + leading margin (same expression
        // for left + right; pre-fix had a same-result ternary that
        // suggested asymmetry that doesn't exist).
        var fragmentInlineOffset = placedInline + marginInlineStart;
        var fragmentBlockOffset = placedBlock + marginStart;

        _sink.Emit(new BoxFragment(
            Box: child,
            InlineOffset: fragmentInlineOffset,
            BlockOffset: fragmentBlockOffset,
            InlineSize: borderBoxInlineSize,
            BlockSize: borderBoxBlockSize));

        // Per cycle 2b — recursively emit descendants. Floats can
        // contain block-level children (like any other container);
        // they emit at offsets relative to the float's content area.
        EmitBlockSubtreeRecursive(
            child,
            parentBlockOffset: fragmentBlockOffset,
            parentInlineOffset: fragmentInlineOffset,
            parentInlineSize: borderBoxInlineSize,
            cancellationToken: cancellationToken,
            depth: 1);
    }

    /// <summary>Per cycle-2b post-PR-28 review #3 — predicate
    /// distinguishing block-level kinds whose INNER geometry is owned
    /// by <see cref="BlockLayouter"/> (block-flow containers per
    /// CSS Display L3 §2.1) from kinds that are block-level for
    /// outer display but whose inner content needs a dedicated
    /// layouter.
    ///
    /// <para>True for: <see cref="BoxKind.Root"/>,
    /// <see cref="BoxKind.BlockContainer"/>, <see cref="BoxKind.ListItem"/>,
    /// <see cref="BoxKind.AnonymousBlock"/> — these have inner-display
    /// "flow" + are laid out by the block-flow algorithm.</para>
    ///
    /// <para>False for: <see cref="BoxKind.Table"/> (TableLayouter
    /// owns rows/cells per Tables L3), <see cref="BoxKind.FlexContainer"/>
    /// (FlexLayouter owns flex items per Flexbox L1),
    /// <see cref="BoxKind.GridContainer"/> (GridLayouter owns grid
    /// items per Grid L2), <see cref="BoxKind.BlockReplacedElement"/>
    /// (atomic — no inner geometry; replaced content fills the
    /// border box).</para>
    ///
    /// <para>Pre-cycle-2b-fix the recursion used <see cref="Box.IsBlockLevel"/>
    /// which is true for ALL of the above. The recursion would walk INTO
    /// table rows / flex items / grid items as if they were block flow,
    /// emitting fragments at incorrect offsets. With this narrower
    /// predicate, non-flow block kinds are emitted as PLACEHOLDER
    /// fragments by the outer loop (their outer-display position is
    /// correct) but their inner content is left to the dedicated
    /// layouter (Phase 3 Tasks 8-12 ship those).</para></summary>
    private static bool IsBlockFlowContainerOwnedByBlockLayouter(Box box) => box.Kind switch
    {
        BoxKind.Root or BoxKind.BlockContainer
            or BoxKind.ListItem or BoxKind.AnonymousBlock => true,
        _ => false,
    };

    /// <summary>Per cycle-2b post-PR-28 review #2 + Copilot #1 — DoS
    /// guard for <see cref="EmitBlockSubtreeRecursive"/>. Pathologically
    /// deep HTML (e.g., 10,000 nested <c>div</c>s) could otherwise
    /// trigger <see cref="StackOverflowException"/> + halt the entire
    /// process. 256 is well above any realistic document depth (typical
    /// HTML documents nest 10-30 deep) but low enough that throwing
    /// catches the abuse cleanly without consuming the rest of the
    /// stack.
    ///
    /// <para>This is the simplest defense; a future pass could replace
    /// the recursion with an explicit stack to avoid the depth limit
    /// entirely. For cycle 2b's MVP scope the limit is sufficient.</para></summary>
    private const int MaxRecursionDepth = 256;

    /// <summary>Per Phase 3 Task 7 cycle 2c — pre-measure pass that
    /// computes the maximum block-axis extent reached by any
    /// descendant in <paramref name="parent"/>'s subtree, measured
    /// from <paramref name="parent"/>'s border-box top.
    ///
    /// <para>For leaf boxes (no block-level descendants) returns
    /// <paramref name="parent"/>'s own border-box block size (border +
    /// padding + Height-from-style). For containers with block-level
    /// descendants, walks the subtree applying adjacent-margin
    /// collapse (CSS 2.1 §8.3.1) + returns the max of (parent's own
    /// border-box, deepest descendant's bottom edge in parent's
    /// border-box coordinates). When children overflow the parent's
    /// content area (auto-height + tall children, or explicit-height
    /// + overflowing children), the descendant extent dominates.</para>
    ///
    /// <para><b>Used by</b> <see cref="AttemptLayout"/>'s outer break
    /// decision: the chunk-block-size for the resolver consult is
    /// computed against this subtree extent, so an overflowing
    /// subtree triggers a <see cref="BreakAction.BreakHere"/> that
    /// pushes the entire subtree to the next page (atomic semantics —
    /// real mid-subtree splits are deferred to cycle 2d). Also drives
    /// the cursor advance so siblings AFTER an overflowing subtree
    /// don't visually overlap with the overflow.</para>
    ///
    /// <para><b>Recursion + DoS guard.</b> Mirrors
    /// <see cref="EmitBlockSubtreeRecursive"/>'s
    /// <see cref="MaxRecursionDepth"/> + flow-container predicate
    /// gating: non-flow block kinds (Table / Flex / Grid / replaced)
    /// are treated as opaque (return their own border-box size; their
    /// inner geometry belongs to a dedicated layouter). Pathologically
    /// deep trees throw <see cref="InvalidOperationException"/> at
    /// the same depth limit. Note: the measure pass does NOT accept a
    /// CancellationToken — it's a pure traversal called once per
    /// outer-loop child + the depth cap bounds the work; the outer
    /// loop's CT check still runs between children.</para></summary>
    private double MeasureSubtreeVisualBlockExtent(Box parent, CancellationToken cancellationToken)
        => MeasureSubtreeVisualBlockExtentRecursive(parent, cancellationToken, depth: 0);

    /// <summary>Per cycle 2c post-PR-29 review #1 (P0) + #3 (P1) —
    /// recursive worker for <see cref="MeasureSubtreeVisualBlockExtent(Box, CancellationToken)"/>.
    /// Renamed from the same-name overload to fix CS0419 ambiguous cref
    /// errors in class-level XML docs. CT plumbing matches
    /// <see cref="EmitBlockSubtreeRecursive"/> so an oversized broad
    /// subtree (large fan-out, not just deep nesting) doesn't make
    /// AttemptLayout unresponsive to cancellation.</summary>
    private double MeasureSubtreeVisualBlockExtentRecursive(
        Box parent, CancellationToken cancellationToken, int depth)
    {
        if (depth > MaxRecursionDepth)
        {
            throw new InvalidOperationException(
                $"BlockLayouter measure-pass recursion depth exceeded {MaxRecursionDepth}; "
                + "pathologically deep box tree. This is a DoS guard against "
                + "untrusted HTML; legitimate documents rarely exceed depth 32.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        // Parent's own border-box block size from style.
        var pBorderStart = parent.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
        var pPaddingStart = parent.Style.ReadLengthPxOrZero(PropertyId.PaddingTop);
        var pBorderEnd = parent.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
        var pPaddingEnd = parent.Style.ReadLengthPxOrZero(PropertyId.PaddingBottom);
        var pHeight = parent.Style.ReadLengthPxOrZero(PropertyId.Height);
        var parentBorderBoxBlockSize = pBorderStart + pPaddingStart + pHeight
            + pPaddingEnd + pBorderEnd;

        // Non-flow block kinds (Table/Flex/Grid/Replaced) are atomic
        // to the BlockLayouter — their inner geometry belongs to a
        // dedicated layouter. Return own size.
        if (!IsBlockFlowContainerOwnedByBlockLayouter(parent))
        {
            return parentBorderBoxBlockSize;
        }

        // Walk block-level children, computing the deepest bottom-edge
        // reached. Children's content area top is at (border + padding)
        // from parent's border-box top.
        var contentAreaTop = pBorderStart + pPaddingStart;
        var maxExtent = parentBorderBoxBlockSize;  // baseline = own size

        // Stack children with adjacent-margin collapse (mirrors
        // EmitBlockSubtreeRecursive's stacking logic).
        double childCursor = 0;  // signed visual cursor (cycle 2b post-PR-28 fix)
        double prevMarginEnd = 0;
        var hasPrior = false;
        var lastChildMarginEnd = 0.0;
        var hadAnyBlockChild = false;

        foreach (var child in parent.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!child.IsBlockLevel)
            {
                // Non-block content (inline / atomic) breaks margin
                // adjacency — same as EmitBlockSubtreeRecursive. Inline
                // content's block-axis contribution is Task 10's
                // domain (LineBuilder); cycle 2c measures the
                // block-flow extent only.
                hasPrior = false;
                prevMarginEnd = 0;
                continue;
            }

            var marginStart = child.Style.ReadLengthPxOrZero(PropertyId.MarginTop);
            var marginEnd = child.Style.ReadLengthPxOrZero(PropertyId.MarginBottom);
            // Recurse — child's subtree extent (= max of child's own
            // border-box + its descendants).
            var childSubtreeExtent = MeasureSubtreeVisualBlockExtentRecursive(
                child, cancellationToken, depth + 1);

            double topShift;
            if (!hasPrior)
            {
                topShift = marginStart;
            }
            else
            {
                var gap = MarginCollapse.Collapse(prevMarginEnd, marginStart);
                topShift = gap - prevMarginEnd;
            }

            // Child's bottom edge in parent's border-box coordinates.
            var childTopInParent = contentAreaTop + childCursor + topShift;
            var childBottomInParent = childTopInParent + childSubtreeExtent;
            if (childBottomInParent > maxExtent)
            {
                maxExtent = childBottomInParent;
            }

            // Signed cursor advance (cycle 2b post-PR-28 fix — negative
            // margins legitimately produce overlap).
            childCursor = childCursor + topShift + childSubtreeExtent + marginEnd;
            prevMarginEnd = marginEnd;
            hasPrior = true;
            lastChildMarginEnd = marginEnd;
            hadAnyBlockChild = true;
        }

        // Per cycle 2c post-PR-29 review #5 — when children dominate
        // the parent's height, the measured extent must also include
        // the parent's bottom padding + bottom border + the LAST
        // CHILD'S margin-bottom (they sit BELOW the deepest descendant
        // bottom in the parent's box model). Pre-fix the measure
        // tracked only descendant border-box bottoms; the parent's
        // own padding-bottom + border-bottom + last-child margin-end
        // were silently undercounted.
        //
        // The fix only matters when descendants DOMINATE — for parents
        // whose own borderBoxBlockSize is the largest (children fit
        // within parent's content area), the baseline `maxExtent =
        // parentBorderBoxBlockSize` already covers the bottom padding
        // + border. For descendant-dominant cases, append the parent's
        // tail extents to the deepest-child bottom.
        //
        // Note: parent/last-child margin collapse is a separate cycle
        // 3 deferral (needs BFC root detection); cycle 2c sums them
        // without collapse. For typical documents (non-zero parent
        // padding/border) this is correct; for the BFC-root case
        // (zero padding/border) cycle 3 will refine.
        if (hadAnyBlockChild)
        {
            var deepestBottom = maxExtent;
            // The current maxExtent might be parentBorderBoxBlockSize
            // (no descendant overflow) — in that case the parent's tail
            // extents are already included. Only append when descendants
            // dominate.
            if (deepestBottom > parentBorderBoxBlockSize)
            {
                // Parent's tail (padding-bottom + border-bottom) lies
                // BELOW the deepest descendant in CSS box-model terms.
                // Add them to get the true total visual extent.
                var parentTail = pPaddingEnd + pBorderEnd;
                // Last-child margin-bottom contributes too — it lies
                // BELOW the last child's border-box bottom + ABOVE the
                // parent's padding-bottom. (For BFC roots it would
                // collapse with the parent's own marginBottom, but
                // that's cycle 3 BFC detection.)
                deepestBottom += Math.Max(0, lastChildMarginEnd) + parentTail;
                if (deepestBottom > maxExtent)
                {
                    maxExtent = deepestBottom;
                }
            }
        }

        return maxExtent;
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
