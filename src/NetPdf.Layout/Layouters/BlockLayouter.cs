// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Inline;
using NetPdf.Paginate;
using NetPdf.Paginate.Diagnostics;
using NetPdf.Text.Bidi;

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

    /// <summary>Per Phase 3 Task 11 cycle 1 sub-cycle 1 — optional
    /// inline shaper resolver. When non-null, <see cref="AttemptLayout"/>
    /// dispatches block containers whose children are entirely
    /// inline-level into <see cref="DispatchInlineOnlyBlock"/> which
    /// runs the <see cref="InlineLayouter.LayoutPerRun"/> pipeline +
    /// emits one <see cref="BoxFragment"/> carrying the
    /// <see cref="InlineLayoutResult"/> in its
    /// <see cref="BoxFragment.InlineLayout"/> field. When null,
    /// inline-only blocks are skipped as in cycles 1-2c (a single
    /// <c>LAYOUT-INLINE-SKIPPED-NO-SHAPER-RESOLVER-001</c>
    /// diagnostic is emitted per skipped block when a diagnostic sink
    /// is available). Default null preserves backwards compatibility
    /// with all existing call sites; production callers (the
    /// facade's renderer) wire a real resolver.</summary>
    private readonly IShaperResolver? _shaperResolver;

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
    /// at construction rather than silently misbehaving.</para>
    ///
    /// <para>Per Phase 3 Task 11 cycle 1 sub-cycle 1 —
    /// <paramref name="shaperResolver"/> is the inline shaping seam.
    /// When supplied, block containers whose children are entirely
    /// inline-level are dispatched through
    /// <see cref="InlineLayouter.LayoutPerRun"/>; the inline result
    /// is carried on the emitted <see cref="BoxFragment.InlineLayout"/>.
    /// When null (default — back-compat with cycles 1-2c), inline-only
    /// blocks are skipped + a single
    /// <c>LAYOUT-INLINE-SKIPPED-NO-SHAPER-RESOLVER-001</c> diagnostic
    /// is emitted per skipped block (subject to
    /// <paramref name="diagnostics"/> being non-null). Production
    /// callers (the facade's render pipeline) wire a real resolver;
    /// the null path keeps test harnesses + tooling working.</para></summary>
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
        IPaginateDiagnosticsSink? diagnostics = null,
        IShaperResolver? shaperResolver = null)
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
        _shaperResolver = shaperResolver;
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

        // Per Phase 3 Task 8 cycle 2 post-PR-31 review (P1 #5) —
        // capture fragmentainer + diagnostic sink for nested-float
        // overflow checks. Cycle 2 MVP: nested float overflows emit
        // a diagnostic but don't defer (recursion has no PageComplete
        // return path — cycle-2c-style continuations would be needed).
        _capturedFragmentainer = fragmentainer;
        _capturedDiagSink = layout.Diagnostics ?? _diagnostics;

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

            // Per Phase 3 Task 11 cycle 1 sub-cycle 1 — inline-only
            // block container dispatch. Detect block containers whose
            // children are entirely inline-level (the common case
            // produced by BoxBuilder for a <p> with text + inline
            // <span>s, or an AnonymousBlock synthesized around an
            // inline run). Dispatch through LayoutInlineContent which
            // runs the InlineLayouter pipeline + emits ONE BoxFragment
            // carrying the inline pass result in InlineLayout.
            //
            // <b>Sub-cycle 1 hardening review (this revision) — Findings
            // 3, 5, 6.</b>
            // <list type="bullet">
            //   <item>Finding 5 — the inline-only block now honors its
            //   own margins / borders / padding / width. Lines are
            //   placed at the BLOCK's content-box origin (inside its
            //   padding + border); the emitted BoxFragment's
            //   geometry is the BORDER box, matching the regular
            //   block path.</item>
            //   <item>Finding 3 — the resolver is consulted at the
            //   candidate-break boundary BEFORE the emit, with a
            //   captured checkpoint, mirroring the in-flow block
            //   dispatch below. Inline content that overflows the
            //   fragmentainer triggers the same forced-overflow
            //   diagnostic path; mid-page resolver verdicts (Rewind,
            //   PageComplete) propagate through the standard
            //   <see cref="LayoutAttemptResult"/> envelope.</item>
            //   <item>Finding 6 — <see cref="System.NotSupportedException"/>
            //   from the inline pass (e.g., per-source-TextRun
            //   <c>word-break: keep-all</c> mismatch — CJK semantics
            //   deferred) is now caught + degraded to a Warning
            //   diagnostic + the inline-only block skipped, same
            //   chain-reset semantics as the no-resolver skip.</item>
            // </list>
            //
            // <b>Deferrals still tracked for sub-cycle 2+:</b>
            //   * Mixed block + inline children inside ONE BFC
            //     (BoxBuilder's AnonymousBlock wrapper guarantees the
            //     dispatch sees all-block or all-inline children today;
            //     a future cycle widens this).
            //   * Margin collapse between the inline-only block + its
            //     adjacent block siblings (sub-cycle 1 still resets
            //     the chain; CSS 2.1 §8.3.1 says the line box breaks
            //     adjacency — correct but conservative).
            //   * Widows / orphans (CSS Fragmentation L3 §4.3) +
            //     per-line splitting at fragmentainer boundary
            //     (sub-cycle 1 atomic — the whole inline-only block
            //     commits on one page or forced-overflow).
            //   * `text-align` / `vertical-align`.
            //   * RTL fragment-level slice reversal.
            if (IsInlineOnlyBlockContainer(child))
            {
                if (_shaperResolver is null)
                {
                    // Per the constructor's contract — no resolver →
                    // skip inline content + emit a diagnostic so the
                    // caller has an observability hook. Mirrors the
                    // non-block-level skip's margin-chain reset (the
                    // skipped content would have formed a line box
                    // that breaks adjacency per §8.3.1).
                    OptimizingBreakResolver.SafeEmit(
                        layout.Diagnostics ?? _diagnostics,
                        new PaginateDiagnostic(
                            PaginateDiagnosticCodes.LayoutInlineSkippedNoShaperResolver001,
                            $"BlockLayouter: inline-only block container at child "
                            + $"index {childIdx} skipped — no IShaperResolver was "
                            + "supplied to the layouter. Production callers wire a "
                            + "resolver via the constructor's shaperResolver "
                            + "parameter; this diagnostic exists so test harnesses + "
                            + "tooling driving the layouter directly can detect the "
                            + "no-op path.",
                            PaginateDiagnosticSeverity.Warning));
                    hasPriorAdjoiningBlock = false;
                    prevBlockMarginEnd = 0;
                    continue;
                }

                // Run the box-model + pagination-aware dispatch. The
                // helper returns a non-null LayoutAttemptResult when
                // the outer loop must return immediately (PageComplete
                // / NeedsRewind). Null means "fragment emitted (or
                // skipped via diagnostic), continue with the next
                // child".
                var inlineDispatchResult = DispatchInlineOnlyBlock(
                    child,
                    childIdx,
                    fragmentainer,
                    ref layout,
                    resolver,
                    initialUsed,
                    priorPagesConsumed,
                    prevBlockMarginEnd,
                    hasPriorAdjoiningBlock,
                    lastEmittedIdx,
                    emittedThisAttempt,
                    out var inlineFragmentEmitted,
                    cancellationToken);
                if (inlineDispatchResult.HasValue)
                {
                    return inlineDispatchResult.Value;
                }
                // Successful emit OR graceful skip via diagnostic.
                // Margin-collapse reset matches the non-block-level
                // skip: the inline-only block (when emitted) creates
                // a line box that breaks adjacency per CSS 2.1 §8.3.1;
                // a skipped block-without-a-fragment also resets
                // because pagination treats the block as having been
                // processed.
                //
                // Per PR #48 review Copilot — `lastEmittedIdx` +
                // `emittedThisAttempt` only update when an actual
                // fragment was emitted (not on the null-resolver /
                // empty-content / NotSupportedException-caught skip
                // paths). Without this guard, the forced-overflow
                // detector at line ~2420 (which reads
                // `emittedThisAttempt == 0 && atTopOfPage`) would
                // false-negative when prior children only contributed
                // diagnostics + skips.
                hasPriorAdjoiningBlock = false;
                prevBlockMarginEnd = 0;
                if (inlineFragmentEmitted)
                {
                    lastEmittedIdx = childIdx;
                    emittedThisAttempt++;
                }
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
            // Cycle 1 MVP: floats stack along the block axis (cycle 3
            // will inline-pack same-side floats per §9.5.1 rule 5).
            // Cycle 2 (this revision) wires inline-size reduction so
            // in-flow blocks flow AROUND floats; the BlockLayouter +
            // FloatManager.GetAvailableInlineRange path handles that.
            // Cycle 3 will refine for inline content (line boxes
            // wrapping around floats — needs LineBuilder).
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
                //
                // Per post-PR-31 review (P1 #4) — "page has prior
                // content" is a disjunction:
                //   (a) `fragmentainer.UsedBlockSize > 0` — captures
                //       any prior content (header reservation, prior
                //       layouter output, prior page's content carrying
                //       forward) regardless of source.
                //   (b) `emittedThisAttempt > 0` — captures THIS
                //       layouter's emissions even when those emissions
                //       are out-of-flow + don't advance UsedBlockSize
                //       (floats!). A page with one float emitted then
                //       a second too-tall float must still defer the
                //       second; UsedBlockSize stays 0 because floats
                //       don't advance it.
                var pageHasPriorContent =
                    fragmentainer.UsedBlockSize > 0
                    || emittedThisAttempt > 0;
                if (WouldFloatOverflow(child, floatSide.Value, fragmentainer)
                    && pageHasPriorContent)
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

            // Per Phase 3 Task 8 cycle 2 + post-PR-31 review (P1 #1 +
            // #2 + Copilot #2) — flow around floats. Query the
            // available inline-axis range AT THE BLOCK'S ACTUAL
            // BORDER-BOX TOP, not at fragmentainer.UsedBlockSize.
            //
            // Pre-cycle-2c-fix queried at UsedBlockSize (= the cursor
            // before margin / clearance), which broke two cases:
            //   (a) clear:left after a left float → block correctly
            //       moved below the float vertically but still got
            //       reduced inline-size (range query saw the float
            //       active at the cursor Y); the block should use the
            //       FULL containing-block range now that it's past
            //       the float.
            //   (b) marginTop / collapsed margins that move the block
            //       past a float bottom → same false reduction.
            //
            // Post-fix: the hypothetical top-Y AFTER margin collapse +
            // clearance is `fragmentainer.UsedBlockSize + topShift`.
            // Per CSS 2.2 §9.5, the available range is queried at the
            // border-box top edge — this is what we now use.
            var hypotheticalBlockTop = fragmentainer.UsedBlockSize + topShift;
            var (availInlineStart, availInlineEnd) =
                _floatManager.GetAvailableInlineRange(
                    blockY: hypotheticalBlockTop,
                    containingStart: 0,
                    containingEnd: fragmentainer.ContentInlineSize);
            var availInlineSize = Math.Max(0, availInlineEnd - availInlineStart);

            // Per PR #23 review fix #5 — clamp the border-box inline
            // size to non-negative. Per cycle 2 — derive from the
            // float-adjusted available range, not the full containing
            // block.
            var borderBoxInlineSize = Math.Max(0,
                availInlineSize - marginInlineStart - marginInlineEnd);

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
                    // Per Phase 3 Task 12 sub-cycle 1 — dispatch into
                    // TableLayouter for the inner rows/cells when the
                    // emitted block is a Table or InlineTable wrapper.
                    // The wrapper's outer fragment is already emitted
                    // above; TableLayouter only adds rows + cells.
                    DispatchTableInnerIfNeeded(
                        child,
                        wrapperBlockOffset: forcedOverflowChildBlockOffset,
                        wrapperInlineOffset: forcedOverflowInlineOffset,
                        wrapperInlineSize: borderBoxInlineSize,
                        fragmentainer: fragmentainer,
                        layout: ref layout,
                        resolver: resolver,
                        cancellationToken: cancellationToken);
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
            // Per Phase 3 Task 12 sub-cycle 1 — dispatch into
            // TableLayouter for the inner rows/cells when the emitted
            // block is a Table or InlineTable wrapper. The wrapper's
            // outer fragment is already emitted above; TableLayouter
            // only adds rows + cells. EmitBlockSubtreeRecursive's own
            // predicate gate skips Table inner content (the inner
            // geometry belongs to TableLayouter, not BlockLayouter).
            DispatchTableInnerIfNeeded(
                child,
                wrapperBlockOffset: blockOffset,
                wrapperInlineOffset: inFlowInlineOffset,
                wrapperInlineSize: borderBoxInlineSize,
                fragmentainer: fragmentainer,
                layout: ref layout,
                resolver: resolver,
                cancellationToken: cancellationToken);

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

            // Per Phase 3 Task 11 cycle 1 sub-cycle 1 hardening
            // review Finding #2 — inline-only block descendants. A
            // nested <p> inside a <div> (the canonical case) is an
            // inline-only block whose CONTENT must run through the
            // inline pass + emit a fragment with InlineLayout set.
            // Pre-Finding-2 the recursion treated nested inline-only
            // blocks as if they were ordinary leaf blocks — emitting
            // a fragment with no InlineLayout — so painter glyph
            // resolution silently failed.
            //
            // The recursive path doesn't consult the resolver
            // (Finding #3's pagination integration is owned by the
            // outer loop's measure pass via
            // MeasureSubtreeVisualBlockExtent). Atomic-inline +
            // NotSupported diagnostics are routed through the
            // captured diagnostic sink.
            if (_shaperResolver is not null && IsInlineOnlyBlockContainer(child))
            {
                // First, compute the topShift the same way as the
                // regular block path below: collapse with prior +
                // honor clearance. For inline-only blocks the line
                // box breaks adjacency, so the chain is reset (same
                // rule as the main loop's reset). topShift is the
                // block's own marginTop.
                var inlineOnlyMarginStart = child.Style.ReadLengthPxOrZero(PropertyId.MarginTop);

                var emittedExtent = EmitInlineOnlyBlockInRecursion(
                    child,
                    parentContentInlineSize: contentInlineSize,
                    // Inline-axis offset: the parent's content-area
                    // left edge in fragmentainer coords; the helper
                    // adds the block's own marginInlineStart.
                    inlineOffsetFromContentOrigin: contentLeft,
                    // Block-axis offset: contentTop + childCursor
                    // gives the line-box start position INSIDE the
                    // parent's content area; add the inline-only
                    // block's marginTop to land at the border-box
                    // top edge.
                    blockOffsetFromContentOrigin: contentTop + childCursor
                        + inlineOnlyMarginStart,
                    cancellationToken);
                // Inline-only block resets the margin-collapse chain
                // per CSS 2.1 §8.3.1 (the line box breaks adjacency).
                hasPrior = false;
                prevMarginEnd = 0;
                // Advance the recursive cursor by the block's full
                // margin-box extent (marginTop + borderBox +
                // marginBottom). EmitInlineOnlyBlockInRecursion
                // returns that value.
                childCursor += emittedExtent;
                continue;
            }

            // Per Phase 3 Task 8 cycle 1 post-PR-30 review (P1 #4) —
            // nested clear resolution. A non-floating block inside the
            // recursion with `clear: ...` clears past relevant float
            // bottoms IN BFC COORDINATES.
            //
            // Per cycle 2 post-PR-31 review (P1 #6) — same fix as the
            // outer loop's clearance handling: clearance is space ABOVE
            // marginTop, not below. Pre-fix advanced `childCursor` to
            // the cleared-Y immediately + then applied marginTop on
            // top → border-box top at floatBottom + marginTop (one
            // marginTop too low). Post-fix: defer clearance into
            // topShift; border-box top = max(hypothetical-with-collapse,
            // clearedFloatBottom).
            var nestedClearKind = child.Style.ReadClearKind();
            var nestedLocalBfcY = contentTop + childCursor;
            var nestedClearedBfcY = nestedClearKind != ClearKind.None
                ? _floatManager.GetClearedBlockY(nestedLocalBfcY, nestedClearKind)
                : nestedLocalBfcY;
            var nestedClearanceTriggered = nestedClearedBfcY > nestedLocalBfcY;
            if (nestedClearanceTriggered)
            {
                // Per §8.3.1 — clearance creates a new collapse boundary.
                hasPrior = false;
                prevMarginEnd = 0;
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

            // Per cycle 2 post-PR-31 review (P1 #6) — fold nested
            // clearance into topShift, same as the outer loop. CSS
            // clearance is space ABOVE marginTop chosen so border-box
            // top = max(hypothetical, floatBottom).
            if (nestedClearanceTriggered)
            {
                var floatBottomShift = nestedClearedBfcY - nestedLocalBfcY;
                if (floatBottomShift > topShift)
                {
                    topShift = floatBottomShift;
                }
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

        // Per cycle 2 post-PR-31 review (P1 #5) — nested float
        // overflow diagnostic. Cycle 2 MVP: a nested float that
        // would extend past the fragmentainer emits the same
        // PAGINATION-FORCED-OVERFLOW-001 diagnostic as the top-level
        // path. Cycle 2 doesn't yet defer nested floats to the next
        // page (would require a PageComplete return path through
        // the recursion — a cycle-2c-style refactor); the diagnostic
        // is the observability channel until then.
        if (_capturedFragmentainer is not null)
        {
            var floatBottom = placedBlock + marginBoxBlockSize;
            if (floatBottom > _capturedFragmentainer.BlockSize)
            {
                OptimizingBreakResolver.SafeEmit(_capturedDiagSink, new PaginateDiagnostic(
                    PaginateDiagnosticCodes.PaginationForcedOverflow001,
                    $"BlockLayouter: nested float overflow on fragmentainer page index "
                    + $"{_capturedFragmentainer.PageIndex} — float margin-box bottom="
                    + $"{floatBottom:0.##} exceeds fragmentainer block-size="
                    + $"{_capturedFragmentainer.BlockSize:0.##}. Cycle 2 emits the float "
                    + "anyway (nested-float deferral requires recursive continuation "
                    + "tokens — deferred to a future cycle).",
                    PaginateDiagnosticSeverity.Warning));
            }
        }

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
    /// <para>Per cycle 2 post-PR-31 review (P1 #3 + Copilot #1) —
    /// uses <see cref="FloatManager.PeekPlaceFloatBlockOffset"/> to
    /// compute the SAME stacked Y that PlaceFloat would. Pre-fix used
    /// `currentBlockY` directly + missed overflow when prior same-side
    /// floats already stacked below the cursor (e.g., a 500-tall float
    /// followed by a 400-tall float on an 800-page should defer at
    /// y=900 but the check saw 0+400 and emitted instead).</para></summary>
    private bool WouldFloatOverflow(
        Box child, FloatSide side, FragmentainerContext fragmentainer)
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

        // Per post-PR-31 review (P1 #3 + Copilot #1) — use the
        // FloatManager peek API which mirrors PlaceFloat's stacking
        // rule (max of currentBlockY + same-side max-bottom). This
        // catches the case where prior same-side floats have stacked
        // below the cursor.
        var hypotheticalY = _floatManager.PeekPlaceFloatBlockOffset(
            side, fragmentainer.UsedBlockSize);
        return hypotheticalY + marginBoxBlockSize > fragmentainer.BlockSize;
    }

    /// <summary>Captured at AttemptLayout entry; used by
    /// EmitNestedFloat as the BFC-wide containing inline size.</summary>
    private double _bfcContentInlineSize;

    /// <summary>Per cycle 2 post-PR-31 review (P1 #5) — captured at
    /// AttemptLayout entry; used by EmitNestedFloat for overflow
    /// detection (cycle 2 MVP: emits diagnostic for nested float
    /// overflow but doesn't defer to next page; that requires
    /// cycle-2c-style continuation tokens which are out of cycle 2
    /// scope). The recursion can't easily defer (no PageComplete
    /// return path) — diagnostic only.</summary>
    private FragmentainerContext? _capturedFragmentainer;

    /// <summary>Per cycle 2 post-PR-31 review (P1 #5) — captured at
    /// AttemptLayout entry; the ambient diagnostic sink for nested-
    /// float overflow emission.</summary>
    private IPaginateDiagnosticsSink? _capturedDiagSink;

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
                + "anyway. Cross-fragmentainer deferral (cycle 2) handles "
                + "the with-prior-content case automatically; this fallback "
                + "fires only when the float is taller than ANY page (cycle "
                + "3 will fragment such floats per CSS Fragmentation L3 §5).",
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

    /// <summary>Per Phase 3 Task 11 cycle 1 sub-cycle 1 — predicate
    /// distinguishing block containers whose children are entirely
    /// inline-level (the inline-only block case) from mixed or
    /// all-block containers.
    ///
    /// <para>True when <paramref name="box"/> is a block-flow container
    /// owned by <see cref="BlockLayouter"/> (BlockContainer / ListItem
    /// / AnonymousBlock — Root is excluded because the outer driver
    /// constructs <see cref="BlockLayouter"/> with a Root + its mixed
    /// child kinds, NOT a Root containing only inline runs; the
    /// BoxBuilder synthesizes AnonymousBlock wrappers around inline
    /// runs at the Root level so this case shouldn't arise in
    /// practice) AND it has at least one child AND every child is
    /// inline-level per <see cref="Box.IsInlineLevel"/>.</para>
    ///
    /// <para><b>Why "at least one child" matters.</b> A childless
    /// block (e.g., an empty <c>&lt;p&gt;&lt;/p&gt;</c>) has nothing
    /// to wrap; routing it through the inline-content path produces
    /// an empty TextRun array which
    /// <see cref="DispatchInlineOnlyBlock"/> short-circuits to "emit
    /// nothing". Letting the existing block-level dispatch handle
    /// childless blocks preserves the cycle-1-2c behavior (placeholder
    /// fragment at the block's own border-box size from
    /// <c>height:</c>) which is what the painter expects.</para>
    ///
    /// <para><b>No LINQ — manual loop.</b> Per repo's
    /// no-LINQ-in-hot-paths rule; this predicate is called once per
    /// child per <see cref="AttemptLayout"/> iteration.</para></summary>
    private static bool IsInlineOnlyBlockContainer(Box box)
    {
        // Outer-display gate: only block-flow containers participate.
        // Root excluded — see XML doc rationale.
        if (box.Kind is not (BoxKind.BlockContainer
            or BoxKind.ListItem or BoxKind.AnonymousBlock))
        {
            return false;
        }
        if (box.Children.Count == 0)
        {
            return false;
        }
        for (var i = 0; i < box.Children.Count; i++)
        {
            if (box.Children[i].IsBlockLevel)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Per Phase 3 Task 11 cycle 1 sub-cycle 1 hardening
    /// review Finding #5 — read the inline-only block's box-model
    /// extents (margins, borders, padding, declared width) into a
    /// single struct so the dispatch site + the measure pass share
    /// one source of truth.</summary>
    /// <remarks>Sub-cycle 1's reads parallel the in-flow block
    /// dispatch but ARE NOT yet a refactor of the in-flow path's
    /// inline-style reads (that's a larger
    /// <c>BlockChildMetrics</c> extraction tracked in the existing
    /// TODO atop <see cref="EmitBlockSubtreeRecursive"/>). Cycle 3
    /// will fold both call sites into the shared struct.</remarks>
    private readonly record struct InlineOnlyBlockMetrics(
        double MarginInlineStart,
        double MarginInlineEnd,
        double MarginBlockStart,
        double MarginBlockEnd,
        double BorderInlineStart,
        double BorderInlineEnd,
        double BorderBlockStart,
        double BorderBlockEnd,
        double PaddingInlineStart,
        double PaddingInlineEnd,
        double PaddingBlockStart,
        double PaddingBlockEnd,
        double DeclaredWidthPx,    // 0 when `width: auto` (default)
        double LineHeightOverridePx); // 0 sentinel = use font-size × 1.2

    private static InlineOnlyBlockMetrics ReadInlineOnlyBlockMetrics(Box block) =>
        new(
            MarginInlineStart: block.Style.ReadLengthPxOrZero(PropertyId.MarginLeft),
            MarginInlineEnd: block.Style.ReadLengthPxOrZero(PropertyId.MarginRight),
            MarginBlockStart: block.Style.ReadLengthPxOrZero(PropertyId.MarginTop),
            MarginBlockEnd: block.Style.ReadLengthPxOrZero(PropertyId.MarginBottom),
            BorderInlineStart: block.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth),
            BorderInlineEnd: block.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth),
            BorderBlockStart: block.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth),
            BorderBlockEnd: block.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth),
            PaddingInlineStart: block.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft),
            PaddingInlineEnd: block.Style.ReadLengthPxOrZero(PropertyId.PaddingRight),
            PaddingBlockStart: block.Style.ReadLengthPxOrZero(PropertyId.PaddingTop),
            PaddingBlockEnd: block.Style.ReadLengthPxOrZero(PropertyId.PaddingBottom),
            DeclaredWidthPx: block.Style.ReadLengthPxOrZero(PropertyId.Width),
            // line-height: read from the block's own style (sub-cycle
            // 1 simple rule — uniform across the block). Sub-cycle 2
            // will read per-TextRun + apply the CSS Text L3 strut
            // rule (max of constituent runs' line-heights).
            LineHeightOverridePx: block.Style.ReadLengthPxOrZero(PropertyId.LineHeight));

    /// <summary>Per Phase 3 Task 11 cycle 1 sub-cycle 1 — compute the
    /// inline-only block's full pre-emit picture: the inline pass
    /// result + the geometry the fragment will be drawn with. Used
    /// by both <see cref="DispatchInlineOnlyBlock"/> (the main-loop
    /// path that needs the metrics for resolver consultation +
    /// emit) AND <see cref="EmitInlineOnlyBlockInRecursion"/> (the
    /// recursive-subtree path per Finding #2 that emits at a
    /// caller-provided position without resolver consultation).
    ///
    /// <para>Returns <see langword="null"/> when the inline pass
    /// produces no content (no TextRuns OR all collapse to empty
    /// after preprocessing) — caller emits NO fragment.</para>
    ///
    /// <para>The <c>NotSupported</c> branch returns a sentinel +
    /// the diagnostic is emitted by the caller (which has access to
    /// the layout's diagnostic sink). Caller treats sentinel the
    /// same as "no content" (no fragment, advance loop).</para></summary>
    private InlineOnlyBlockComputation? ComputeInlineOnlyBlockLayout(
        Box inlineOnlyBlock,
        InlineOnlyBlockMetrics metrics,
        double containingInlineSize,
        out string? notSupportedMessage,
        out int atomicInlineSkipCount,
        CancellationToken cancellationToken)
    {
        notSupportedMessage = null;
        atomicInlineSkipCount = 0;
        cancellationToken.ThrowIfCancellationRequested();

        // Resolve the border-box inline size. CSS 2.1 §10.3.3 maps
        // `width` (the content-box inline size) to the border box
        // via border + padding. Sub-cycle 1 handles the simple case:
        //   * `width` set explicitly → border-box = width + borders
        //     + padding (clamped non-negative).
        //   * `width: auto` (DeclaredWidthPx == 0 here because
        //     ReadLengthPxOrZero returns 0 for auto in cycle 1) →
        //     border-box = full available content area minus
        //     inline margins (the in-flow-fill behavior).
        // True `auto` resolution per §10.3.3 (with the margin
        // distribution algorithm) lands in cycle 3 of the BLOCK
        // layouter; sub-cycle 1's approximation matches the
        // regular block path's current cycle-1 behavior.
        double borderBoxInlineSize;
        if (metrics.DeclaredWidthPx > 0)
        {
            borderBoxInlineSize = Math.Max(0,
                metrics.DeclaredWidthPx
                + metrics.BorderInlineStart + metrics.BorderInlineEnd
                + metrics.PaddingInlineStart + metrics.PaddingInlineEnd);
        }
        else
        {
            borderBoxInlineSize = Math.Max(0,
                containingInlineSize
                - metrics.MarginInlineStart - metrics.MarginInlineEnd);
        }
        // Inline-pass available size = the content-box inline range.
        var contentInlineSize = Math.Max(0,
            borderBoxInlineSize
            - metrics.BorderInlineStart - metrics.BorderInlineEnd
            - metrics.PaddingInlineStart - metrics.PaddingInlineEnd);
        if (contentInlineSize <= 0)
        {
            // Negative or zero content area → no lines can fit. The
            // inline layouter would throw on availableInlineSize<=0
            // anyway; emit no fragment cleanly. (Real CSS would
            // still render glyphs that exceed the box; sub-cycle 1
            // takes the simple route.)
            return null;
        }

        // Collect inline TextRuns by walking the descendants in
        // document order. Per Finding #4 — atomic inlines are
        // counted so the caller can emit ONE LAYOUT-INLINE-ATOMIC-
        // NOT-SUPPORTED-001 diagnostic per skipped atomic; LineBreak
        // boxes inject a synthetic LF TextRun; Marker boxes recurse
        // (their child TextRun for bullet/number text).
        var textRuns = new List<NetPdf.Layout.Inline.TextRun>();
        atomicInlineSkipCount = CollectInlineTextRuns(
            inlineOnlyBlock, textRuns, inlineOnlyBlock.Style,
            cancellationToken);

        InlineLayoutResult inlineResult;
        if (textRuns.Count == 0)
        {
            // Per the spec — no contributing text means no fragment +
            // no cursor advance. (An inline-only block with only
            // whitespace-collapsed runs, e.g., a <p> &lt;span&gt;&lt;/span&gt;,
            // hits this path.)
            return null;
        }

        // Sub-cycle 1 — hard-coded script + language (UAX #24 +
        // BCP 47 plumbing deferred).
        // _shaperResolver is guaranteed non-null by the caller's
        // IsInlineOnlyBlockContainer gate + the diagnostic branch.
        // Per Finding #6 — wrap with try/catch so per-source-TextRun
        // mismatches that still throw (KeepAll) degrade to a Warning
        // diagnostic instead of bringing the block layouter down.
        try
        {
            inlineResult = InlineLayouter.LayoutPerRun(
                sourceTextRuns: textRuns,
                availableInlineSize: contentInlineSize,
                resolver: _shaperResolver!,
                scriptIso15924: "Latn",
                language: "en",
                paragraphDirection: ParagraphDirection.LeftToRight,
                hyphenator: null,
                cancellationToken: cancellationToken);
        }
        catch (NotSupportedException ex)
        {
            notSupportedMessage = ex.Message;
            return null;
        }

        if (inlineResult.Lines.Length == 0)
        {
            // Possible when every collected TextRun's text collapses
            // to nothing under white-space processing (e.g., a
            // single-space TextRun with white-space:normal becomes
            // empty after start-of-paragraph trimming). Same
            // emit-nothing path as the no-TextRuns case.
            return null;
        }

        // Per Finding #5 — line-height honoring. Use the block's
        // declared line-height when non-zero; fall back to
        // 1.2 × font-size for `line-height: normal`. CSS Text L3 §3
        // + CSS Inline L3 §3.5 say the used `line-height` is the
        // COMPUTED value (`normal` → font's intrinsic line metric);
        // reading the keyword + the font metric tables remains
        // sub-cycle 2 work — 1.2 is the de-facto Web default for
        // `line-height: normal` across major UAs.
        double lineHeight;
        if (metrics.LineHeightOverridePx > 0)
        {
            lineHeight = metrics.LineHeightOverridePx;
        }
        else
        {
            var fontSizePx = inlineOnlyBlock.Style.ReadLengthPxOrDefault(
                PropertyId.FontSize, defaultPx: 16);
            lineHeight = fontSizePx * 1.2;
        }
        var contentBlockSize = inlineResult.Lines.Length * lineHeight;
        var borderBoxBlockSize = contentBlockSize
            + metrics.BorderBlockStart + metrics.PaddingBlockStart
            + metrics.PaddingBlockEnd + metrics.BorderBlockEnd;

        return new InlineOnlyBlockComputation(
            InlineResult: inlineResult,
            BorderBoxInlineSize: borderBoxInlineSize,
            BorderBoxBlockSize: borderBoxBlockSize,
            ContentBlockSize: contentBlockSize);
    }

    /// <summary>Per Phase 3 Task 11 cycle 1 sub-cycle 1 — bundle the
    /// pre-emit metrics carried out of
    /// <see cref="ComputeInlineOnlyBlockLayout"/>. Geometry is
    /// border-box-only (matches <see cref="BoxFragment"/>'s
    /// contract).</summary>
    private readonly record struct InlineOnlyBlockComputation(
        InlineLayoutResult InlineResult,
        double BorderBoxInlineSize,
        double BorderBoxBlockSize,
        double ContentBlockSize);

    /// <summary>Per Phase 3 Task 11 cycle 1 sub-cycle 1 hardening
    /// review Findings 3 + 5 + 6 — main-loop dispatch for an
    /// inline-only block container. Honors the block's box model
    /// (margins / borders / padding / width) for geometry; consults
    /// the resolver via <see cref="BreakOpportunity.Block"/> with a
    /// captured checkpoint; falls back to a Warning diagnostic +
    /// no-emit on <see cref="System.NotSupportedException"/> from
    /// the inline pass.
    ///
    /// <para>Returns <see langword="null"/> on the Continue path
    /// (fragment emitted or graceful skip) — the outer loop
    /// advances + continues. Returns a non-null
    /// <see cref="LayoutAttemptResult"/> on the
    /// <see cref="BreakAction.Rewind"/> / forced-overflow
    /// <see cref="BreakAction.BreakHere"/> / clean
    /// <see cref="BreakAction.BreakHere"/> paths — the outer loop
    /// must return immediately.</para></summary>
    private LayoutAttemptResult? DispatchInlineOnlyBlock(
        Box inlineOnlyBlock,
        int childIdx,
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        IBreakResolver resolver,
        double initialUsed,
        double priorPagesConsumed,
        double prevBlockMarginEnd,
        bool hasPriorAdjoiningBlock,
        int lastEmittedIdx,
        int emittedThisAttempt,
        out bool emitted,
        CancellationToken cancellationToken)
    {
        // Per PR #48 review Copilot — the caller must NOT bump
        // `lastEmittedIdx` + `emittedThisAttempt` on skip paths
        // (null shaper resolver, empty content, NotSupportedException
        // caught). Each return point assigns `emitted` explicitly
        // so downstream pagination decisions see the truthful
        // "did this child put a fragment on the page" state.
        emitted = false;
        cancellationToken.ThrowIfCancellationRequested();

        var metrics = ReadInlineOnlyBlockMetrics(inlineOnlyBlock);
        // Sub-cycle 1 — inline-only blocks reset the margin chain
        // (the line box breaks adjacency per CSS 2.1 §8.3.1), so
        // the block's own marginBlockStart applies in full without
        // collapse arithmetic. This matches the cycle 1 behavior;
        // cycle 2 will integrate with the collapse chain.
        var topShift = metrics.MarginBlockStart;
        var computation = ComputeInlineOnlyBlockLayout(
            inlineOnlyBlock,
            metrics,
            containingInlineSize: fragmentainer.ContentInlineSize,
            out var notSupportedMessage,
            out var atomicInlineSkipCount,
            cancellationToken);

        // Emit atomic-inline skip diagnostics BEFORE the dispatch
        // outcome — they fire regardless of whether the line pass
        // produced anything. One per skipped atomic inline so
        // consumers can pinpoint each.
        if (atomicInlineSkipCount > 0)
        {
            var diagSink = layout.Diagnostics ?? _diagnostics;
            OptimizingBreakResolver.SafeEmit(diagSink, new PaginateDiagnostic(
                PaginateDiagnosticCodes.LayoutInlineAtomicNotSupported001,
                $"BlockLayouter: inline-only block container at child "
                + $"index {childIdx} contained {atomicInlineSkipCount} atomic "
                + "inline descendant(s) (inline-block / inline-flex / "
                + "inline-grid / inline-table / inline-replaced) — sub-cycle 1 "
                + "skips them in the resulting line. Sub-cycle 2 will inject "
                + "atomic-inline placeholders via per-layouter intrinsic-sizing "
                + "hooks.",
                PaginateDiagnosticSeverity.Warning));
        }

        if (notSupportedMessage is not null)
        {
            // Finding 6 — NotSupportedException from the inline
            // pass. Degrade to a Warning diagnostic + skip without
            // emitting a fragment. The block layouter advances past
            // the block (same chain-reset semantics as the no-
            // resolver skip).
            var diagSink = layout.Diagnostics ?? _diagnostics;
            OptimizingBreakResolver.SafeEmit(diagSink, new PaginateDiagnostic(
                PaginateDiagnosticCodes.LayoutInlineUnsupported001,
                $"BlockLayouter: inline pass for inline-only block at child "
                + $"index {childIdx} threw NotSupportedException + the inline "
                + "content was dropped. Detail: " + notSupportedMessage,
                PaginateDiagnosticSeverity.Warning));
            return null;
        }

        if (computation is null)
        {
            // No inline content (empty TextRuns OR all collapsed to
            // empty). No fragment emitted, no cursor advance.
            return null;
        }

        var comp = computation.Value;

        // Finding 3 — pagination integration. Mirror the in-flow
        // block dispatch: capture a checkpoint at the candidate-
        // break boundary, consult the resolver with the FULL margin-
        // box block extent (chunk size), handle Rewind /
        // forced-overflow / clean PageComplete.
        var marginBoxBlockSizeForCursor = topShift + comp.BorderBoxBlockSize
            + metrics.MarginBlockEnd;
        var visualBlockExtent = comp.BorderBoxBlockSize
            + Math.Max(0, topShift)
            + Math.Max(0, metrics.MarginBlockEnd);
        var chunkForBreakCheck = Math.Max(
            Math.Max(0, marginBoxBlockSizeForCursor),
            visualBlockExtent);

        // Push the FloatManager state into the fragmentainer slot
        // BEFORE the checkpoint captures it (same invariant as the
        // in-flow path — see Phase 3 Task 8 cycle 1 post-PR-30
        // review P0 #1).
        fragmentainer.FloatManagerState = _floatManager.Snapshot();

        var newLease = LayoutCheckpointPool.Rent();
        newLease.Checkpoint!.Capture(
            fragmentainer,
            in layout,
            fragmentOutputCursor: _sink.Cursor,
            lastEmittedChildIndex: lastEmittedIdx,
            incomingContinuation: _incomingContinuation,
            pageCounterValue: layout.ReadCounter("page"),
            prevBlockMarginEnd: prevBlockMarginEnd,
            hasAdjoiningBlockOnEntry: hasPriorAdjoiningBlock);
        resolver.RegisterCheckpoint(newLease);
        _activeLease = newLease;

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
            _resumeAtChildIdxAfterRewind = decision.RewindTo.LastEmittedChildIndex + 1;
            _prevBlockMarginEndAfterRewind = decision.RewindTo.PrevBlockMarginEnd;
            _hasPriorAdjoiningBlockAfterRewind = decision.RewindTo.HasAdjoiningBlockOnEntry;
            _hasRewindCollapseState = true;
            return LayoutAttemptResult.NeedsRewind(decision.RewindTo, decision.Cost);
        }

        if (decision.Action == BreakAction.BreakHere)
        {
            var nothingEmittedThisAttempt = emittedThisAttempt == 0;
            var atTopOfPage = fragmentainer.UsedBlockSize == initialUsed;
            if (nothingEmittedThisAttempt && atTopOfPage)
            {
                // Forced overflow — the inline content is taller
                // than the fragmentainer. Same loud-fail semantics
                // as the in-flow path: emit anyway + diagnostic.
                var diagSink = layout.Diagnostics ?? _diagnostics;
                OptimizingBreakResolver.SafeEmit(diagSink, new PaginateDiagnostic(
                    PaginateDiagnosticCodes.PaginationForcedOverflow001,
                    $"BlockLayouter: forced overflow on fragmentainer page index "
                    + $"{fragmentainer.PageIndex}, inline-only block at child "
                    + $"index {childIdx} — inline content border-box height="
                    + $"{comp.BorderBoxBlockSize:0.##} (margin-box="
                    + $"{marginBoxBlockSizeForCursor:0.##}) is taller than the "
                    + $"fragmentainer (block-size={fragmentainer.BlockSize:0.##}). "
                    + "Committed anyway to make pagination progress.",
                    PaginateDiagnosticSeverity.Warning));
                EmitInlineOnlyBlockFragment(
                    inlineOnlyBlock, metrics, comp,
                    inlineOffsetFromContentOrigin: 0,
                    blockOffsetFromContentOrigin: fragmentainer.UsedBlockSize + topShift);
                fragmentainer.UsedBlockSize = Math.Max(0,
                    fragmentainer.UsedBlockSize + marginBoxBlockSizeForCursor);
                emitted = true;
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
            // Normal page break — content earlier on this page.
            return LayoutAttemptResult.PageComplete(
                new BlockContinuation(
                    ResumeAtChild: childIdx,
                    ConsumedBlockSize: priorPagesConsumed
                        + (fragmentainer.UsedBlockSize - _pageStartUsedBlockSize)),
                cost: decision.Cost);
        }

        // BreakAction.Continue — emit + advance.
        EmitInlineOnlyBlockFragment(
            inlineOnlyBlock, metrics, comp,
            inlineOffsetFromContentOrigin: 0,
            blockOffsetFromContentOrigin: fragmentainer.UsedBlockSize + topShift);
        fragmentainer.UsedBlockSize = Math.Max(0,
            fragmentainer.UsedBlockSize + marginBoxBlockSizeForCursor);
        emitted = true;
        return null;
    }

    /// <summary>Per Phase 3 Task 11 cycle 1 sub-cycle 1 — emit one
    /// <see cref="BoxFragment"/> for the inline-only block at the
    /// supplied border-box offset. Used by both the main-loop
    /// dispatch (after resolver consultation) AND the recursive
    /// subtree dispatch (Finding #2). Geometry is the BORDER box on
    /// both axes.</summary>
    /// <param name="inlineOnlyBlock">The block (caller validated
    /// inline-only).</param>
    /// <param name="metrics">Pre-read box-model extents.</param>
    /// <param name="comp">Pre-computed inline-pass result + border-
    /// box sizes.</param>
    /// <param name="inlineOffsetFromContentOrigin">Inline-axis
    /// position of the BLOCK's BORDER-BOX inline-start edge from
    /// the fragmentainer's content-area origin. Caller adds
    /// <c>marginInlineStart</c> in the in-flow case OR the
    /// containing parent's content-area inline offset in the
    /// recursive case.</param>
    /// <param name="blockOffsetFromContentOrigin">Block-axis
    /// position of the BLOCK's BORDER-BOX block-start edge from
    /// the fragmentainer's content-area origin (= post-marginTop
    /// in the in-flow case).</param>
    private void EmitInlineOnlyBlockFragment(
        Box inlineOnlyBlock,
        InlineOnlyBlockMetrics metrics,
        InlineOnlyBlockComputation comp,
        double inlineOffsetFromContentOrigin,
        double blockOffsetFromContentOrigin)
    {
        // Border-box inline-start offset = caller-supplied offset +
        // marginInlineStart. The caller's offset is already in the
        // block's "stacking-context" coordinate space (border-box
        // top-left). For the in-flow case the inline anchor was
        // computed in <see cref="DispatchInlineOnlyBlock"/> as 0
        // (no flow-around yet for inline-only blocks per the cycle
        // 1 sub-cycle 1 deferral) + marginInlineStart applied here.
        var fragmentInlineOffset = inlineOffsetFromContentOrigin
            + metrics.MarginInlineStart;
        _sink.Emit(new BoxFragment(
            Box: inlineOnlyBlock,
            InlineOffset: fragmentInlineOffset,
            BlockOffset: blockOffsetFromContentOrigin,
            InlineSize: comp.BorderBoxInlineSize,
            BlockSize: comp.BorderBoxBlockSize,
            InlineLayout: comp.InlineResult));
    }

    /// <summary>Per Phase 3 Task 11 cycle 1 sub-cycle 1 hardening
    /// review Finding #2 — emit the inline-only block fragment from
    /// the recursive subtree walker
    /// (<see cref="EmitBlockSubtreeRecursive"/>). Computes the
    /// inline pass + emits at the caller-supplied content-area-
    /// relative position. The recursive caller has already chosen
    /// the in-parent-content-area offset via its own stacking +
    /// margin-collapse arithmetic; this helper just runs the
    /// inline pass + emits.
    ///
    /// <para><b>Pagination scope.</b> The recursive path does NOT
    /// consult the resolver (the outer-loop's measure pass already
    /// reserved page space via
    /// <see cref="MeasureSubtreeVisualBlockExtent"/>). If the
    /// inline content overflows the parent, the outer-loop's
    /// forced-overflow path fires per existing cycle 2c semantics.
    /// True mid-subtree splitting is sub-cycle 2 work.</para>
    ///
    /// <para><b>NotSupportedException + atomic-inline diagnostics.</b>
    /// Routed through the captured diagnostic sink (set at
    /// AttemptLayout entry); same diagnostic codes as the main-loop
    /// dispatch.</para></summary>
    /// <returns>The total block-axis extent the recursive caller
    /// should advance its child cursor by (= computed border-box
    /// block size when content was emitted; 0 when the inline pass
    /// produced nothing).</returns>
    private double EmitInlineOnlyBlockInRecursion(
        Box inlineOnlyBlock,
        double parentContentInlineSize,
        double inlineOffsetFromContentOrigin,
        double blockOffsetFromContentOrigin,
        CancellationToken cancellationToken)
    {
        var metrics = ReadInlineOnlyBlockMetrics(inlineOnlyBlock);
        var computation = ComputeInlineOnlyBlockLayout(
            inlineOnlyBlock,
            metrics,
            containingInlineSize: parentContentInlineSize,
            out var notSupportedMessage,
            out var atomicInlineSkipCount,
            cancellationToken);

        if (atomicInlineSkipCount > 0)
        {
            OptimizingBreakResolver.SafeEmit(_capturedDiagSink, new PaginateDiagnostic(
                PaginateDiagnosticCodes.LayoutInlineAtomicNotSupported001,
                $"BlockLayouter: inline-only block container (recursive subtree) "
                + $"contained {atomicInlineSkipCount} atomic inline descendant(s) "
                + "(inline-block / inline-flex / inline-grid / inline-table / "
                + "inline-replaced) — sub-cycle 1 skips them in the resulting line.",
                PaginateDiagnosticSeverity.Warning));
        }

        if (notSupportedMessage is not null)
        {
            OptimizingBreakResolver.SafeEmit(_capturedDiagSink, new PaginateDiagnostic(
                PaginateDiagnosticCodes.LayoutInlineUnsupported001,
                $"BlockLayouter: inline pass for inline-only block (recursive "
                + "subtree) threw NotSupportedException + the inline content "
                + "was dropped. Detail: " + notSupportedMessage,
                PaginateDiagnosticSeverity.Warning));
            return 0;
        }

        if (computation is null)
        {
            return 0;
        }

        var comp = computation.Value;
        EmitInlineOnlyBlockFragment(
            inlineOnlyBlock, metrics, comp,
            inlineOffsetFromContentOrigin,
            blockOffsetFromContentOrigin);
        // Total block-axis cursor advance for the recursive caller =
        // marginBlockStart + borderBoxBlockSize + marginBlockEnd.
        // The recursive caller's stacking does not apply collapse
        // for inline-only blocks (line box breaks adjacency — same
        // rule as the main loop).
        return metrics.MarginBlockStart + comp.BorderBoxBlockSize
            + metrics.MarginBlockEnd;
    }

    /// <summary>Per Phase 3 Task 11 cycle 1 sub-cycle 1 — measure
    /// the inline-only block's visual block-axis extent without
    /// emitting. Used by
    /// <see cref="MeasureSubtreeVisualBlockExtentRecursive"/>'s
    /// Finding #2 fix so containers with inline-only descendants
    /// reserve correct page space at pagination time.</summary>
    private double MeasureInlineOnlyBlockExtent(
        Box inlineOnlyBlock,
        double parentContentInlineSize,
        CancellationToken cancellationToken)
    {
        var metrics = ReadInlineOnlyBlockMetrics(inlineOnlyBlock);
        var computation = ComputeInlineOnlyBlockLayout(
            inlineOnlyBlock,
            metrics,
            containingInlineSize: parentContentInlineSize,
            out _,
            out _,
            cancellationToken);
        if (computation is null)
        {
            return 0;
        }
        var comp = computation.Value;
        // Return the margin-box block-axis extent so the parent's
        // measure pass reserves enough page space (matches the
        // in-flow block path's measure semantics).
        return metrics.MarginBlockStart + comp.BorderBoxBlockSize
            + metrics.MarginBlockEnd;
    }

    /// <summary>Per Phase 3 Task 11 cycle 1 sub-cycle 1 hardening
    /// review Finding #4 — depth-first document-order walk that
    /// collects every contributing
    /// <see cref="BoxKind.TextRun"/> descendant into
    /// <paramref name="textRuns"/>. Skips empty-text TextRun boxes
    /// (no glyphs would be produced) + recurses into
    /// <see cref="BoxKind.InlineBox"/> /
    /// <see cref="BoxKind.AnonymousInline"/> wrappers so their
    /// contained runs flow into the same paragraph.
    ///
    /// <para><b>Sub-cycle 1 hardening review Finding #4 (this
    /// revision):</b>
    /// <list type="bullet">
    ///   <item><see cref="BoxKind.LineBreak"/> (<c>&lt;br&gt;</c>) —
    ///   injects a synthetic TextRun containing <c>"\n"</c> with the
    ///   parent block's style. <see cref="LineBuilder.Wrap"/> already
    ///   honors LF as a mandatory wrap point via its
    ///   <c>IsMandatoryLineBreakControl</c> handling (CSS Text L3
    ///   §3.2 / UAX #14 segment breaks).</item>
    ///   <item><see cref="BoxKind.Marker"/> (list bullets / numbers
    ///   per Lists L3 §3.1) — recurses into the marker's children
    ///   (typically a single TextRun for the bullet text "•" or
    ///   list number "1."). The marker's OWN style is used as the
    ///   TextRun's style when injecting.</item>
    ///   <item>Atomic inlines
    ///   (<see cref="BoxKind.InlineBlockContainer"/> /
    ///   <see cref="BoxKind.InlineFlexContainer"/> /
    ///   <see cref="BoxKind.InlineGridContainer"/> /
    ///   <see cref="BoxKind.InlineTable"/> /
    ///   <see cref="BoxKind.InlineReplacedElement"/>) — counted +
    ///   the caller emits one LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001
    ///   diagnostic per skipped atomic. Sub-cycle 2 will inject an
    ///   atomic-inline placeholder line metric box via a per-
    ///   layouter intrinsic-sizing seam.</item>
    /// </list>
    /// </para></summary>
    /// <param name="parent">The current parent box being walked.</param>
    /// <param name="textRuns">The accumulator; runs are appended in
    /// document order.</param>
    /// <param name="parentBlockStyle">The inline-only block's own
    /// style. Used for the synthetic <c>"\n"</c> TextRun on
    /// <see cref="BoxKind.LineBreak"/> so the mandatory break
    /// inherits font / line metrics from the surrounding block.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Count of atomic-inline descendants skipped; caller
    /// emits one diagnostic per skip (or one aggregated diagnostic
    /// for the total count).</returns>
    private static int CollectInlineTextRuns(
        Box parent,
        List<NetPdf.Layout.Inline.TextRun> textRuns,
        ComputedStyle parentBlockStyle,
        CancellationToken cancellationToken)
    {
        var skipCount = 0;
        for (var i = 0; i < parent.Children.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var child = parent.Children[i];
            switch (child.Kind)
            {
                case BoxKind.TextRun:
                    if (child.Text.Length > 0)
                    {
                        textRuns.Add(new NetPdf.Layout.Inline.TextRun(
                            child.Text, child.Style));
                    }
                    break;
                case BoxKind.InlineBox:
                case BoxKind.AnonymousInline:
                    // Recurse — nested inline wrappers carry styled
                    // text leaves underneath. The TextRun's style is
                    // the LEAF's style (cascade-resolved), so the
                    // wrapper's own style doesn't need separate
                    // propagation at the LayoutPerRun seam.
                    skipCount += CollectInlineTextRuns(
                        child, textRuns, parentBlockStyle, cancellationToken);
                    break;
                case BoxKind.LineBreak:
                    // Per Finding #4 — inject a synthetic LINE
                    // SEPARATOR (U+2028) TextRun. LineBuilder's
                    // CSS Text L3 mandatory-break handling
                    // (IsMandatoryLineBreakControl) treats U+2028
                    // as a UAX #14 Mandatory break so the wrap
                    // pass emits a fresh line at this point.
                    // <para><b>Why U+2028 instead of LF (U+000A)?</b>
                    // Under <c>white-space: normal</c> (the default
                    // for typical paragraphs) the preprocessor
                    // <see cref="LineBuilder.PreprocessTextRunsPerRun"/>
                    // collapses LF (along with SP, TAB, CR, FF) into
                    // a single space + strips it across run
                    // boundaries — so an injected "\n" would NEVER
                    // reach the wrap pass as a mandatory break. U+2028
                    // is NOT in the CSS whitespace set
                    // (<c>IsCssWhiteSpace</c> in LineBuilder), so it
                    // survives the preprocess + is honored as a
                    // mandatory break at wrap time. The synthetic run
                    // carries the inline-only block's style so font /
                    // line metrics match the surrounding paragraph.</para>
                    textRuns.Add(new NetPdf.Layout.Inline.TextRun(
                        "\u2028", parentBlockStyle));
                    break;
                case BoxKind.Marker:
                    // Per Finding #4 — list-item markers (Lists L3
                    // §3.1) contain their bullet/number content as
                    // child TextRuns. Recurse into the marker so its
                    // text flows into the line. Marker's OWN style
                    // applies (font-family, color may differ from
                    // surrounding block style per the UA stylesheet
                    // for list-style-type) — use the marker's style,
                    // not the block's.
                    skipCount += CollectInlineTextRuns(
                        child, textRuns, child.Style, cancellationToken);
                    break;
                // Atomic inlines — sub-cycle 2 TODOs.
                case BoxKind.InlineReplacedElement:
                case BoxKind.InlineBlockContainer:
                case BoxKind.InlineFlexContainer:
                case BoxKind.InlineGridContainer:
                case BoxKind.InlineTable:
                    // Per Finding #4 — count + caller emits one
                    // LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001
                    // diagnostic per skip. Sub-cycle 1 still skips
                    // the atomic — sub-cycle 2 will inject a
                    // placeholder line metric box.
                    skipCount++;
                    break;
                default:
                    // Defensive: a block-level descendant inside an
                    // "inline-only" block would mean the caller's
                    // IsInlineOnlyBlockContainer predicate was
                    // violated. Skip silently — the predicate
                    // shouldn't have routed us here.
                    break;
            }
        }
        return skipCount;
    }

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

        // Per Phase 3 Task 11 cycle 1 sub-cycle 1 hardening review
        // Finding #2 — inline-only blocks contribute their wrapped-
        // line block extent on top of the box-model border/padding/
        // height. Pre-Finding-2 the measure pass returned only the
        // own border-box size, ignoring inline content entirely —
        // the outer-loop's pagination dispatch would reserve no
        // page space for inline paragraphs + the painter would
        // silently clip when a too-tall paragraph hit the page
        // boundary.
        //
        // The shaper resolver gate matches the dispatch site —
        // without a resolver the inline pass is skipped (no
        // contribution to the extent).
        //
        // Perf note: this runs the inline pass during the measure
        // walk; the dispatch site then runs it AGAIN at emit time.
        // Sub-cycle 2 will cache the result; sub-cycle 1's
        // correct-but-slow path mirrors the existing
        // EmitBlockSubtreeRecursive vs. MeasureSubtreeVisualBlockExtent
        // duplicate-walk pattern.
        if (_shaperResolver is not null && IsInlineOnlyBlockContainer(parent))
        {
            var inlineMargined = MeasureInlineOnlyBlockExtent(
                parent,
                parentContentInlineSize: _bfcContentInlineSize,
                cancellationToken);
            return Math.Max(parentBorderBoxBlockSize, inlineMargined);
        }

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

    /// <summary>Per Phase 3 Task 12 sub-cycle 1 — dispatch into
    /// <see cref="TableLayouter"/> when <paramref name="wrapperChild"/>
    /// is a <see cref="BoxKind.Table"/> or
    /// <see cref="BoxKind.InlineTable"/>. The wrapper's outer
    /// <see cref="BoxFragment"/> has already been emitted by the
    /// caller; this method appends the inner row + cell fragments at
    /// fragmentainer-relative offsets inside the wrapper's content
    /// area.
    ///
    /// <para>The wrapper's content-area top-left is computed from the
    /// wrapper's border-box top-left (= <paramref name="wrapperBlockOffset"/>
    /// + border-block-start + padding-block-start, similarly for
    /// inline). The wrapper's content-area inline extent is
    /// <paramref name="wrapperInlineSize"/> - border-inline - padding-
    /// inline (clamped non-negative). Sub-cycle 1 reads
    /// border/padding from the wrapper's style via the same physical-
    /// axis helpers as the block path (no logical-axis support yet
    /// — see cycle 3 TODO).</para>
    ///
    /// <para>No-op when <paramref name="wrapperChild"/> is not a
    /// Table or InlineTable wrapper — the predicate keeps the call
    /// site simple at the cost of an extra branch on every emitted
    /// block.</para></summary>
    private void DispatchTableInnerIfNeeded(
        Box wrapperChild,
        double wrapperBlockOffset,
        double wrapperInlineOffset,
        double wrapperInlineSize,
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        IBreakResolver resolver,
        CancellationToken cancellationToken)
    {
        if (wrapperChild.Kind is not (BoxKind.Table or BoxKind.InlineTable))
        {
            return;
        }

        // Compute the wrapper's content-box top-left + inline extent.
        // Sub-cycle 1 reads physical axes (matches the rest of the
        // BlockLayouter cycle 1 behavior; cycle 3 logical-axis pass
        // applies uniformly).
        var borderBlockStart = wrapperChild.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
        var paddingBlockStart = wrapperChild.Style.ReadLengthPxOrZero(PropertyId.PaddingTop);
        var borderInlineStart = wrapperChild.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth);
        var paddingInlineStart = wrapperChild.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft);
        var borderInlineEnd = wrapperChild.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth);
        var paddingInlineEnd = wrapperChild.Style.ReadLengthPxOrZero(PropertyId.PaddingRight);

        var contentInlineOffset = wrapperInlineOffset
            + borderInlineStart + paddingInlineStart;
        var contentBlockOffset = wrapperBlockOffset
            + borderBlockStart + paddingBlockStart;
        var contentInlineSize = Math.Max(0,
            wrapperInlineSize
            - borderInlineStart - paddingInlineStart
            - borderInlineEnd - paddingInlineEnd);

        using var tableLayouter = new TableLayouter(
            rootBox: wrapperChild,
            sink: _sink,
            incomingContinuation: null,
            diagnostics: _diagnostics,
            shaperResolver: _shaperResolver);
        tableLayouter.ConfigureEmission(
            contentInlineOffset: contentInlineOffset,
            contentBlockOffset: contentBlockOffset,
            contentInlineSize: contentInlineSize);
        _ = tableLayouter.AttemptLayout(
            fragmentainer,
            ref layout,
            resolver,
            LayoutAttemptStrategy.LastResort,
            cancellationToken);
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
