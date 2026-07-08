// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
using AngleSharp.Dom;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Hyphenation;
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
/// <see cref="MeasureSubtreeVisualBlockExtent(Box, CancellationToken, double, double)"/> — a pre-measure
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

    // Phase 3 cycle-2d "mid-split" (enter-and-split) gate. When ON, an eligible multi-child
    // block-flow container that doesn't fit the remaining page — but whose first in-flow child does
    // — STARTS on the current page and breaks between its children (filling the trailing space)
    // instead of moving wholly to the next page. See docs/design/pagination-mid-split.md. Default ON
    // (the CSS-correct behavior); the env var NETPDF_MIDSPLIT=0 is an escape hatch to A/B the pre-2d
    // atomic-subtree behavior (all repo goldens are byte-identical either way — the shape only occurs
    // in deeply-nested multi-child overflow, e.g. a `.timeline` of flex `.day` rows).
    private const bool MidSplitDefault = true;
    internal static readonly bool MidSplitEnabled =
        Environment.GetEnvironmentVariable("NETPDF_MIDSPLIT") switch
        {
            "0" => false,
            "1" => true,
            _ => MidSplitDefault,
        };

    // Phase 3 cycle-2d "mid-split" — out-of-band channel for the actual block-axis extent a RESUMED
    // block-flow container emitted on the current page. A resumed container's measured full-subtree
    // extent (childEffectiveBlockSize) counts children already committed on PRIOR pages, so using it
    // for the parent's cursor advance would push a trailing sibling (e.g. a `.note` after a paginated
    // `.timeline`) off the bottom of the page. The recursion writes this on its terminal completion
    // return when it was a resumed entry; the parent consumes it for the cursor advance. -1 = unset.
    private double _resumedContainerEmittedExtent = -1;

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

    /// <summary>Per Phase 3 Task 17 cycle 5c.2b post-PR-#100 review
    /// P1#1 — when <see langword="true"/>, this BlockLayouter
    /// instance suppresses the outer-site paginatable-grid path
    /// (= F1 pre-dispatch row-fit, the cycle-5b extent clamp +
    /// <c>paginateGridForOuterChild</c> gate, F2 wrapper-resize).
    /// Set by nested-context callers
    /// (<c>GridLayouter.DispatchGridItemContents</c> +
    /// <c>TableLayouter</c>'s cell-content + caption-content
    /// dispatch) that intentionally discard <c>AttemptLayout</c>'s
    /// result + can't propagate a <c>BlockContinuation</c> through
    /// their parent's layouter. Without this guard, a paginatable
    /// direct-child grid inside a table cell or grid item would
    /// return <c>PageComplete(GridContinuation)</c> + only emit
    /// rows preceding the split; the continuation is dropped + the
    /// remaining rows silently vanish (= the parallel-flows
    /// fragmentation contract of CSS Fragmentation L3 + the
    /// independent-fragmentation rule for table cells / grid
    /// items). Cycle 5c.2d will wire nested continuation
    /// propagation where the parent layouter supports it; until
    /// then nested grids dispatch atomically via the cycle-1
    /// contract.</summary>
    private readonly bool _disableGridPagination;

    /// <summary>PR-#182 review P1 — when <see langword="true"/>, FLEX pagination
    /// is suppressed (mirrors <see cref="_disableGridPagination"/> for grid):
    /// the paginatable-flex clamps don't fire, so <c>FlexLayouter</c> dispatches
    /// with <c>allowPagination: false</c> (emits all items atomically, content
    /// overflows) instead of returning a <c>PageComplete(FlexContinuation)</c>.
    /// Set by NESTED-content callers that DISCARD the layout result (flex / grid
    /// item content, table cell) — they cannot resume a continuation, so an
    /// unsuppressed nested flex split would silently DROP the deferred items.
    /// The flex subtree-extent projection is gated by it too (so an atomic
    /// nested flex is measured at its FULL extent, not projected to one page).</summary>
    private readonly bool _disableFlexPagination;

    /// <summary>RC2 residual (1) — suppress this layouter's out-of-flow (abspos) EMISSION pass while still
    /// running a real <see cref="MeasurePurpose.Layout"/> pass (in-flow content persists for paint). Set
    /// by <see cref="FlexLayouter"/> when it lays a flex item's content into a buffer that will be flushed:
    /// FlexLayouter is NOT an abspos delegation boundary, so the flex item's abspos DESCENDANTS are owned +
    /// emitted by the TOP-LEVEL pass (which resolves them against the geometry the buffer flush records).
    /// Without this the nested item-content pass ALSO runs the abspos pass — always dropping the descendant
    /// (its containing block isn't recorded in this transient nested map) and leaking a spurious
    /// LAYOUT-ABSOLUTE-FEATURE-UNSUPPORTED-001 even though the top-level pass renders the box correctly.
    /// Grid items / table cells DON'T set this — they ARE delegation boundaries and own their abspos.</summary>
    private readonly bool _suppressOutOfFlowEmission;

    /// <summary>PR #218 review [P1 #1 / P2 #5] — the PURPOSE of this layout pass, captured at
    /// <see cref="AttemptLayout"/> entry from <c>layout.MeasurePurpose</c> so it is TRANSITIVE: a
    /// nested specialized layouter (flex / grid / table) inherits the measure via the by-ref
    /// <see cref="LayoutContext"/> instead of starting a fresh real-layout pass. It drives two
    /// independent policies, both keeping a measure's extents the same as the real layout:
    /// <list type="number">
    ///   <item><b>Out-of-flow emission</b> (<see cref="MeasurePurposeExtensions.SuppressesOutOfFlowEmission"/>)
    ///   — the abspos / fixed passes (<see cref="EmitAbsolutelyPositionedChildren"/> /
    ///   <see cref="EmitFixedPositionedChildren"/>) are SKIPPED for any extent-only measure
    ///   (they don't contribute to intrinsic inline width or §10.6.7 auto block size).</item>
    ///   <item><b>Cyclic percentage insets</b>
    ///   (<see cref="MeasurePurposeExtensions.ZeroesCyclicPercentInsets"/>) — for an
    ///   <see cref="MeasurePurpose.IntrinsicContribution"/> probe the percentage padding / margin
    ///   resolution base is 0 (CSS Sizing §5.2.1 — the basis is indefinite); a
    ///   <see cref="MeasurePurpose.DefiniteWidthExtent"/> measure resolves them against its definite
    ///   width. Only a <see cref="MeasurePurpose.Layout"/> pass PERSISTS the resolved value onto the
    ///   shared style for paint.</item>
    /// </list></summary>
    private MeasurePurpose _measurePurpose;

    /// <summary>Per `inline-only-block-line-splitting` (PR #220 review [P2]) — the cross-page cache of a
    /// split inline-only block's shaped layout, captured from <c>layout.InlineOnlyMeasureCache</c> at
    /// <see cref="AttemptLayout"/> entry so the dispatch AND the recursive split-emit (which has no
    /// <c>LayoutContext</c> in scope) both reuse it instead of re-shaping the paragraph every page.
    /// <see langword="null"/> ⇒ no shared cache (each page re-shapes — the prior behavior).</summary>
    private InlineOnlyMeasurementCache? _inlineOnlyMeasureCache;

    /// <summary>Non-block-pagination arc (flex item CONTENT layout) — when
    /// <see langword="true"/>, a NESTED BlockLayouter whose <c>_rootBox</c> is
    /// itself an inline-only block container (every direct child inline-level)
    /// emits the root's OWN inline content as one inline-only-block fragment,
    /// instead of the block-only child loop silently skipping every (inline)
    /// child. OFF by default so every existing nested-root caller (multicol
    /// inner, table caption / cell, the recursion inner layouter, the pipeline's
    /// <see cref="BoxKind.Root"/> layouter) is byte-identical; only
    /// <see cref="FlexLayouter"/>'s item-content pass opts in (a flex item like
    /// <c>&lt;div&gt;text&lt;/div&gt;</c> has DIRECT inline children, unlike a
    /// table cell whose content the box builder wraps in an anonymous block).
    /// The opt-in keeps the blast radius to exactly the flex item case.</summary>
    private readonly bool _layoutRootInlineContent;

    /// <summary>Per Phase 3 Task 12 sub-cycle 5 hardening Finding 5 —
    /// when <see langword="true"/>, the inline-pass through
    /// <c>InlineLayouter.LayoutPerRun</c> downgrades
    /// <c>OverflowWrap.BreakWord</c> opportunities to
    /// <c>OverflowWrap.Normal</c> per CSS Text L3 §5.1 (break-word's
    /// soft opportunities don't count for min-content sizing).
    /// <c>OverflowWrap.Anywhere</c> opportunities continue to fire
    /// (the spec carves out anywhere as the only soft-opportunity
    /// source that contributes to intrinsic sizing).
    ///
    /// <para>Set by <c>TableLayouter.MeasureCellIntrinsicWidths</c>
    /// (via <see cref="SetIntrinsicSizingMode"/>) during the
    /// speculative min-content cell-content layout so break-word
    /// cells don't get narrowed to a single glyph. Cleared after
    /// the speculative pass returns. The default
    /// <see langword="false"/> means production line-wrap honors
    /// break-word's glyph-boundary fallback as expected.</para></summary>
    private bool _intrinsicSizingMode;

    /// <summary>Per Phase 3 Task 12 sub-cycle 5 hardening Finding 5 —
    /// set the intrinsic-sizing-mode flag for this layouter. Called
    /// by <see cref="TableLayouter.MeasureCellIntrinsicWidths"/>
    /// before dispatching the speculative cell-content layout, and
    /// reset to <see langword="false"/> after. Internal so only the
    /// layout package can flip it; not public API.</summary>
    internal void SetIntrinsicSizingMode(bool value) => _intrinsicSizingMode = value;

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

    /// <summary>Per Phase 3 Task 13 cycle 1 — one-shot consumption flag
    /// for the incoming <see cref="TableContinuation"/> piggy-backed on
    /// <see cref="BlockContinuation.LayouterState"/>. The continuation
    /// applies to the FIRST table child encountered after resume (=
    /// the child the prior page deferred at); subsequent table children
    /// in the same attempt start fresh. Reset on every AttemptLayout
    /// entry (line `_consumedIncomingTableContinuation = false`).</summary>
    private bool _consumedIncomingTableContinuation;

    /// <summary>Per Phase 3 Task 14 cycle 2 — one-shot consumption flag
    /// for the incoming <see cref="MulticolContinuation"/> piggy-backed
    /// on <see cref="BlockContinuation.LayouterState"/>. Same semantics
    /// as <see cref="_consumedIncomingTableContinuation"/> but for the
    /// multicol-resume contract: the carried continuation applies to
    /// the FIRST multicol child encountered after resume; subsequent
    /// multicol children in the same attempt start fresh. Reset on
    /// every AttemptLayout entry.</summary>
    private bool _consumedIncomingMulticolContinuation;

    /// <summary>Per `inline-only-block-line-splitting` — mirrors
    /// <see cref="_consumedIncomingMulticolContinuation"/> for an incoming
    /// <see cref="InlineOnlyLineSplitContinuation"/>: it applies to the FIRST
    /// inline-only block encountered at its <c>ResumeAtChild</c> index after
    /// resume (so a later split in the same attempt starts fresh). Reset on
    /// every AttemptLayout entry.</summary>
    private bool _consumedIncomingInlineLineSplit;

    /// <summary>Per Phase 3 Task 16 cycle 2 — mirrors
    /// <see cref="_consumedIncomingMulticolContinuation"/> for the
    /// flex multi-page resume contract. The incoming
    /// <see cref="FlexContinuation"/> piggy-backed on the resume
    /// <see cref="BlockContinuation.LayouterState"/> is one-shot:
    /// once forwarded to the FlexLayouter at the deferred-at child
    /// index, subsequent flex children in the same attempt start
    /// fresh (= no continuation forwarded).</summary>
    private bool _consumedIncomingFlexContinuation;

    /// <summary>Per Phase 3 Task 14 cycle 2 hardening (Finding #1) —
    /// one-shot consumption flag for the incoming chained
    /// <see cref="BlockContinuation"/> in
    /// <see cref="BlockContinuation.LayouterState"/>. The cycle 2
    /// hardening lifted the depth==1-only continuation propagation
    /// limit: a deep nested multicol/table can now return its
    /// continuation through a chain of <see cref="BlockContinuation"/>s
    /// nested in <see cref="BlockContinuation.LayouterState"/>. This
    /// flag ensures the carried chain feeds into the FIRST matching
    /// recursion entry per attempt; siblings start fresh. Reset on
    /// every AttemptLayout entry.</summary>
    private bool _consumedIncomingBlockContinuationRecursion;

    /// <summary>Per Phase 3 Task 14 cycle 2 hardening (Finding #1) —
    /// one-shot per-page emission flag for
    /// <c>LAYOUT-FLOAT-BREAK-INSIDE-NESTED-001</c>. A float subtree
    /// (out-of-flow per CSS 2.2 §9.5) whose nested recursion returns a
    /// non-null continuation has its continuation discarded (float-
    /// tracking continuation machinery is a Phase 3 Task 8 deferral);
    /// the diagnostic surfaces the truncation. To avoid spamming pages
    /// that have many such floats, we emit at most one diagnostic per
    /// page. Reset on every AttemptLayout entry.</summary>
    private bool _emittedFloatBreakInsideNestedDiagnosticThisPage;

    /// <summary>Per Phase 3 Task 19 (float-continuation-propagation, partial) — set
    /// while emitting a FLOAT's subtree. Floats are out-of-flow (CSS 2.2 §9.5) and the
    /// engine does NOT yet fragment them across pages; a nested grid / flex that
    /// paginated inside a float returned a continuation the float path discarded,
    /// silently TRUNCATING the overflow. With this flag set, nested grid + flex
    /// containers in a float emit ATOMICALLY (the cycle-1 contract: all rows / items on
    /// one page, overflowing the page edge if tall) — lossless, the correct "floats
    /// don't fragment yet" model, mirroring how a float with tall explicit content
    /// already force-overflows. Block-flow content in floats already emits atomically
    /// (the float recursion passes no propagating fragmentainer). Nested TABLE +
    /// multicol stay on the truncate-and-diagnose path (their wrapper-sizing couples to
    /// the page budget — a separate cycle). Save/restore around the recursion entry so
    /// float-in-float nesting composes; in-flow content (flag false) is byte-identical.</summary>
    private bool _inAtomicFloatSubtree;

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
        IShaperResolver? shaperResolver = null,
        bool disableGridPagination = false,
        bool layoutRootInlineContent = false,
        bool disableFlexPagination = false,
        bool suppressOutOfFlowEmission = false,
        System.Collections.Generic.HashSet<Box>? crossPageEmittedAbsolute = null,
        System.Collections.Generic.HashSet<Box>? crossPageConsideredAbsolute = null)
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
        _disableGridPagination = disableGridPagination;
        _layoutRootInlineContent = layoutRootInlineContent;
        _disableFlexPagination = disableFlexPagination;
        _suppressOutOfFlowEmission = suppressOutOfFlowEmission;
        _crossPageEmittedAbsolute = crossPageEmittedAbsolute;
        _crossPageConsideredAbsolute = crossPageConsideredAbsolute;
    }

    /// <summary>Phase 3 cycle-2d — shared across every page's fresh BlockLayouter (the pipeline owns
    /// it). The abspos pass now runs on EVERY page (an abspos box emits on the page where its
    /// containing block is laid out, not only page 1), so this set guards against a box being emitted
    /// twice when its positioned containing block is itself split across pages. Null for non-paginated
    /// / nested callers (single-pass, no cross-page duplication risk).</summary>
    private readonly System.Collections.Generic.HashSet<Box>? _crossPageEmittedAbsolute;

    /// <summary>Phase 3 cycle-2d — companion to <see cref="_crossPageEmittedAbsolute"/>: every abspos
    /// box the page-root pass CONSIDERED (walked to + attempted to resolve) on ANY page. A box that
    /// was considered but is never in the emitted set after the last page is genuinely unresolvable —
    /// its containing block was never recorded on ANY page (an unsupported / never-recordable
    /// geometry path), NOT merely "on another page" — so the pipeline diagnoses it once (preserving
    /// the no-silent-drop contract) instead of the per-page pass suppressing it as a cross-page
    /// miss. Owned by the pipeline (so it spans pages); null for nested / single-pass callers.</summary>
    private readonly System.Collections.Generic.HashSet<Box>? _crossPageConsideredAbsolute;

    /// <inheritdoc />
    /// <remarks>Per Phase 3 Task 19 cycle 1 + post-PR-#112 review C2 —
    /// thin wrapper around <see cref="AttemptLayoutInFlow"/> that runs
    /// the <c>position: absolute</c> emission pass AFTER the in-flow
    /// result is computed. Running it here (rather than inline before
    /// one return) guarantees abspos boxes emit on the establishing
    /// block's first page regardless of whether that page completes
    /// with <see cref="LayoutAttemptOutcome.AllDone"/> OR
    /// <see cref="LayoutAttemptOutcome.PageComplete"/> — multi-page
    /// in-flow content used to skip the inline pre-AllDone call
    /// entirely, dropping every abspos fragment.
    ///
    /// <para>Gates: the pass runs only on the FIRST page
    /// (<c>_incomingContinuation is null</c> — resume pages skip it,
    /// cycle-1 abspos-pagination deferral) + only for COMMITTED
    /// outcomes (AllDone / PageComplete, NOT NeedsRewind — a rewind
    /// rolls the sink back + retries, so abspos must wait for the
    /// committed attempt) + at most once
    /// (<see cref="_absoluteChildrenEmitted"/> guards against a
    /// committed re-entry). Emitting after the core also preserves
    /// paint order: abspos fragments append after all in-flow
    /// fragments (= painted over in-flow content, the CSS default for
    /// out-of-flow positioned boxes).</para></remarks>
    public LayoutAttemptResult AttemptLayout(
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        IBreakResolver resolver,
        LayoutAttemptStrategy strategy,
        CancellationToken cancellationToken = default)
    {
        // Reset the forced-break parity per attempt; the child loop sets it if a forced
        // left/right/recto/verso break ends this page.
        _forcedBreakParityForNextPage = PageParity.Any;
        // Capture the pass purpose from the (transitive) context so the gate sites + nested
        // specialized layouters inherit an intrinsic / definite-extent measure (PR #218 [P1 #1]).
        _measurePurpose = layout.MeasurePurpose;
        _inlineOnlyMeasureCache = layout.InlineOnlyMeasureCache as InlineOnlyMeasurementCache;
        var result = AttemptLayoutInFlow(
            fragmentainer, ref layout, resolver, strategy, cancellationToken);

        // Phase 3 cycle-2d — the abspos pass runs on EVERY committed page, not only page 1. Pre-fix
        // (gated on `_incomingContinuation is null`) an abspos box whose positioned containing block
        // was laid out on a RESUME page was dropped (LAYOUT-ABSOLUTE-FEATURE-UNSUPPORTED-001) — e.g.
        // the 03-itinerary `.day .badge` circles, when the `.timeline` paginates. Now each page's
        // pass resolves boxes whose CB was recorded on THAT page (the geometry map is per-page) and
        // silently skips the rest; the shared `_crossPageEmittedAbsolute` set guards double-emit for
        // a positioned CB that itself spans pages. The `_incomingContinuation is null` gate is gone;
        // the per-instance `_absoluteChildrenEmitted` still bounds it to once per page.
        if (!_absoluteChildrenEmitted
            && !_measurePurpose.SuppressesOutOfFlowEmission()
            && !_suppressOutOfFlowEmission
            && result.Outcome is LayoutAttemptOutcome.AllDone
                or LayoutAttemptOutcome.PageComplete)
        {
            _absoluteChildrenEmitted = true;
            EmitAbsolutelyPositionedChildren(fragmentainer, ref layout, cancellationToken);
        }

        // Per Phase 3 Task 20 cycle 1 — `position: fixed` boxes anchor to
        // the page (initial containing block) and repeat on EVERY page,
        // so (UNLIKE the abspos pass above) this is NOT gated on
        // `_incomingContinuation is null` — it fires on every page,
        // including resume pages. It runs only on the PAGE-ROOT layouter
        // (`_rootBox.Kind == BoxKind.Root`): nested item / cell / column /
        // abspos-content sub-layouters have a non-Root root box, so they
        // must NOT re-emit fixed boxes against their own (sub-page)
        // fragmentainer. The per-instance `_fixedChildrenEmitted` guard
        // only defends against a committed re-entry within ONE page; a
        // fresh BlockLayouter is constructed per page, so the fixed box
        // still emits once per page.
        if (!_fixedChildrenEmitted
            && !_measurePurpose.SuppressesOutOfFlowEmission()
            && _rootBox.Kind == BoxKind.Root
            && result.Outcome is LayoutAttemptOutcome.AllDone
                or LayoutAttemptOutcome.PageComplete)
        {
            _fixedChildrenEmitted = true;
            EmitFixedPositionedChildren(fragmentainer, ref layout, cancellationToken);
        }
        return result;
    }

    /// <summary>Per post-PR-#112 review C2 — one-shot guard so the
    /// abspos emission pass runs at most once per BlockLayouter
    /// instance (defends against a committed re-entry after the
    /// coordinator's rewind/retry cycle).</summary>
    private bool _absoluteChildrenEmitted;

    /// <summary>Per Phase 3 Task 20 cycle 1 — one-shot guard so the
    /// fixed-positioning emission pass runs at most once per
    /// BlockLayouter instance (= once per page, since a fresh layouter is
    /// constructed per page). Defends against a committed re-entry after
    /// the coordinator's rewind/retry cycle, mirroring
    /// <see cref="_absoluteChildrenEmitted"/>.</summary>
    private bool _fixedChildrenEmitted;

    /// <summary>CSS Page L3 §3.4.1 — the page-parity constraint of the forced
    /// <c>break-before</c> / <c>break-after</c> (<c>left</c> / <c>right</c> / <c>recto</c> /
    /// <c>verso</c>) that ENDED this page, if any. A forced break always ends the page (it is
    /// suppressed only at the fragmentainer start), so it is captured at the
    /// <see cref="ResolveChildBreakMetadata"/> sites the moment the forced-parity break is read.
    /// Reset to <see cref="PageParity.Any"/> per <see cref="AttemptLayout"/> entry. The driver
    /// (<c>PdfRenderPipeline</c>) reads <see cref="ForcedBreakParityForNextPage"/> after the page
    /// commits and inserts a blank <c>@page :blank</c> when the resumed content would otherwise
    /// land on the wrong-parity page.</summary>
    private PageParity _forcedBreakParityForNextPage = PageParity.Any;

    /// <summary>The page-parity (<c>left</c>/<c>right</c>/<c>recto</c>/<c>verso</c>) the content
    /// resuming on the NEXT page must land on, set when a forced-parity break ended this page;
    /// <see cref="PageParity.Any"/> otherwise. Read by the driver for blank-page insertion.</summary>
    public PageParity ForcedBreakParityForNextPage => _forcedBreakParityForNextPage;


    /// <summary>Per Phase 3 Task 19 cycle 2a — border-box geometry (in
    /// fragmentainer/sink coordinates) of in-flow descendants that
    /// establish an absolute containing block (<c>position</c> !=
    /// <c>static</c>), recorded as they're emitted. The post-flow
    /// abspos pass walks <c>Box.Parent</c> to the nearest such ancestor
    /// + reads its geometry here to derive the containing block (=
    /// padding box). Keyed by <see cref="Box"/>; last-write-wins so the
    /// committed layout pass overwrites any rolled-back rewind attempt.
    /// Lazily allocated (most documents have no positioned ancestors).</summary>
    private System.Collections.Generic.Dictionary<Box, BoxFragment>? _positionedBoxGeometry;

    /// <summary>Per Phase 3 Task 19 cycle 2a — record an in-flow box's
    /// emitted border-box geometry IFF it establishes an absolute
    /// containing block, so the abspos pass can anchor descendants to
    /// it. No-op for the common (non-positioned) box.</summary>
    private void RecordPositionedBoxGeometry(
        Box box, double inlineOffset, double blockOffset,
        double inlineSize, double blockSize)
    {
        if (!box.Style.EstablishesAbsoluteContainingBlock()) return;
        _positionedBoxGeometry ??= new System.Collections.Generic.Dictionary<Box, BoxFragment>();
        _positionedBoxGeometry[box] = new BoxFragment(
            Box: box,
            InlineOffset: inlineOffset,
            BlockOffset: blockOffset,
            InlineSize: inlineSize,
            BlockSize: blockSize);
    }

    private LayoutAttemptResult AttemptLayoutInFlow(
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        IBreakResolver resolver,
        LayoutAttemptStrategy strategy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fragmentainer);
        ArgumentNullException.ThrowIfNull(resolver);
        cancellationToken.ThrowIfCancellationRequested();

        // Per Phase 3 Task 19 cycle 2a post-PR-#113 review P2#2 — clear
        // the positioned-box geometry map at the start of EACH in-flow
        // pass. A rewind returns NeedsRewind to the coordinator, which
        // rolls the sink back + RE-CALLS AttemptLayout — so each
        // AttemptLayoutInFlow invocation is a single forward pass with
        // no internal rollback loop. Rebuilding the map from scratch
        // each call means the COMMITTED pass's map reflects exactly the
        // boxes that pass emitted (no stale geometry from a rolled-back
        // speculative attempt). The abspos pass (in the AttemptLayout
        // wrapper) reads the map immediately after the committed core
        // returns. This is simpler + more robust than threading the map
        // through LayoutCheckpoint (which only snapshots cursor state,
        // not side-table maps).
        _positionedBoxGeometry?.Clear();

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

        // Per Phase 3 Task 13 cycle 1 hardening (Finding 9) — when
        // the incoming BlockContinuation carries a TableContinuation
        // in LayouterState, the child at ResumeAtChild MUST be a
        // Table or InlineTable wrapper (direct dispatch path) OR a
        // block-flow container (the cycle 2 hardening Finding 1
        // single-level nested-recursion propagation path — e.g.
        // <body><table>, where ResumeAtChild points at the body wrapper
        // + the recursion finds the nested table). Pre-fix, a
        // malformed continuation (LayouterState is TableContinuation
        // but ResumeAtChild was neither) was silently ignored —
        // the resume page emitted as if no table-resume were pending,
        // dropping the deferred row content. Throw here so caller bugs
        // surface loudly rather than producing mis-laid documents.
        if (incomingBlock?.LayouterState is TableContinuation
            && startChildIdx >= 0
            && startChildIdx < _rootBox.Children.Count
            && _rootBox.Children[startChildIdx].Kind is not (BoxKind.Table or BoxKind.InlineTable)
            && !IsBlockFlowContainerOwnedByBlockLayouter(_rootBox.Children[startChildIdx]))
        {
            throw new InvalidOperationException(
                "BlockLayouter.AttemptLayout: incoming BlockContinuation carries "
                + "a TableContinuation in LayouterState but the child at "
                + $"ResumeAtChild={startChildIdx} has BoxKind."
                + $"{_rootBox.Children[startChildIdx].Kind}, not Table / "
                + "InlineTable / block-flow container. This is a layouter "
                + "contract violation — the table-resume state can only "
                + "attach to a table child OR a block-flow container "
                + "containing the table at depth 1 (cycle 2 hardening "
                + "Finding 1). The dispatching layouter must produce "
                + "continuations where the ResumeAtChild + LayouterState "
                + "pair are mutually consistent.");
        }

        // Per Phase 3 Task 14 cycle 2 — symmetric validation for an
        // incoming MulticolContinuation in LayouterState. The child at
        // ResumeAtChild MUST be a multicol container OR a block-flow
        // container that recursively contains the multicol (single-
        // level nested propagation). Out-of-range / null-children
        // bounds are tolerated to allow the resume page to detect
        // "all done" cases — the validation kicks in only when the
        // index points at a concrete child.
        if (incomingBlock?.LayouterState is MulticolContinuation
            && startChildIdx >= 0
            && startChildIdx < _rootBox.Children.Count
            && !IsMulticolContainer(_rootBox.Children[startChildIdx])
            && !IsBlockFlowContainerOwnedByBlockLayouter(_rootBox.Children[startChildIdx]))
        {
            throw new InvalidOperationException(
                "BlockLayouter.AttemptLayout: incoming BlockContinuation carries "
                + "a MulticolContinuation in LayouterState but the child at "
                + $"ResumeAtChild={startChildIdx} has BoxKind."
                + $"{_rootBox.Children[startChildIdx].Kind} (column-count="
                + $"{_rootBox.Children[startChildIdx].Style.ReadColumnCount()}), "
                + "which is neither a multicol container nor a block-flow "
                + "container that could contain one. The dispatching "
                + "layouter must produce continuations where the "
                + "ResumeAtChild + LayouterState pair are mutually "
                + "consistent. Per Phase 3 Task 14 cycle 2 resume contract.");
        }

        // Per Phase 3 Task 16 cycle 2 post-PR-#79 review P2 #6 —
        // symmetric validation for an incoming FlexContinuation in
        // LayouterState. Mirrors the TableContinuation +
        // MulticolContinuation checks above. The child at
        // ResumeAtChild MUST be a flex container OR a block-flow
        // container that recursively contains the flex (= same
        // single-level-nested propagation contract as multicol). Pre-
        // fix a misrouted FlexContinuation was silently ignored
        // unless it happened to land on the direct flex child path.
        if (incomingBlock?.LayouterState is FlexContinuation
            && startChildIdx >= 0
            && startChildIdx < _rootBox.Children.Count
            && _rootBox.Children[startChildIdx].Kind is not
                (BoxKind.FlexContainer or BoxKind.InlineFlexContainer)
            && !IsBlockFlowContainerOwnedByBlockLayouter(_rootBox.Children[startChildIdx]))
        {
            throw new InvalidOperationException(
                "BlockLayouter.AttemptLayout: incoming BlockContinuation carries "
                + "a FlexContinuation in LayouterState but the child at "
                + $"ResumeAtChild={startChildIdx} has BoxKind."
                + $"{_rootBox.Children[startChildIdx].Kind}, which is neither "
                + "a FlexContainer / InlineFlexContainer nor a block-flow "
                + "container that could contain one. The dispatching "
                + "layouter must produce continuations where the "
                + "ResumeAtChild + LayouterState pair are mutually "
                + "consistent. Per Phase 3 Task 16 cycle 2 resume contract.");
        }

        // Per PR-#97 review F5 — symmetric grid continuation validation
        // mirroring the flex / multicol / table guards. The child at
        // ResumeAtChild MUST be a grid container OR a block-flow
        // container that recursively contains the grid (= same single-
        // level-nested propagation contract). Pre-fix a misrouted
        // GridContinuation would silently lose resume intent.
        if (incomingBlock?.LayouterState is GridContinuation
            && startChildIdx >= 0
            && startChildIdx < _rootBox.Children.Count
            && _rootBox.Children[startChildIdx].Kind is not
                (BoxKind.GridContainer or BoxKind.InlineGridContainer)
            && !IsBlockFlowContainerOwnedByBlockLayouter(_rootBox.Children[startChildIdx]))
        {
            throw new InvalidOperationException(
                "BlockLayouter.AttemptLayout: incoming BlockContinuation carries "
                + "a GridContinuation in LayouterState but the child at "
                + $"ResumeAtChild={startChildIdx} has BoxKind."
                + $"{_rootBox.Children[startChildIdx].Kind}, which is neither "
                + "a GridContainer / InlineGridContainer nor a block-flow "
                + "container that could contain one. The dispatching "
                + "layouter must produce continuations where the "
                + "ResumeAtChild + LayouterState pair are mutually "
                + "consistent. Per Phase 3 Task 17 cycle 5b resume contract.");
        }

        // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) — when
        // the incoming BlockContinuation carries a nested
        // BlockContinuation (the recursion-chain protocol introduced
        // by lifting the depth==1-only propagation limit), the child
        // at ResumeAtChild MUST be a block-flow container — NOT a
        // Table/InlineTable/multicol container (which would be the
        // direct-dispatch path, unwrapped one level deeper). The chain
        // is walked at the recursion-dispatch site; the entry-time
        // check is a fail-fast guard for malformed dispatch.
        if (incomingBlock?.LayouterState is BlockContinuation chainHead
            && startChildIdx >= 0
            && startChildIdx < _rootBox.Children.Count)
        {
            var chainResumeChild = _rootBox.Children[startChildIdx];
            if (chainResumeChild.Kind is BoxKind.Table or BoxKind.InlineTable
                || IsMulticolContainer(chainResumeChild)
                || !IsBlockFlowContainerOwnedByBlockLayouter(chainResumeChild))
            {
                throw new ArgumentException(
                    "BlockLayouter.AttemptLayout: incoming BlockContinuation "
                    + "carries a chained BlockContinuation in LayouterState "
                    + "(the recursion-chain protocol from Phase 3 Task 14 "
                    + "cycle 2 hardening Finding #1) but the child at "
                    + $"ResumeAtChild={startChildIdx} has BoxKind."
                    + $"{chainResumeChild.Kind} (column-count="
                    + $"{chainResumeChild.Style.ReadColumnCount()}), which is "
                    + "neither a block-flow container nor allowed to host a "
                    + "chained continuation. The chain protocol unwraps one "
                    + "BlockContinuation per recursion level; the LEAF must "
                    + "be a Table/Multicol continuation attaching to the "
                    + "matching child kind. The dispatching layouter must "
                    + "produce continuations where each chain level's "
                    + "ResumeAtChild + LayouterState pair are mutually "
                    + "consistent.",
                    "incomingContinuation");
            }

            // Walk the chain (bc → bc.LayouterState as BlockContinuation
            // → ...) and cap depth at MaxRecursionDepth (= 256). A
            // malformed chain (e.g., 1M nested BlockContinuations) is a
            // DoS vector — throw at entry rather than blow the stack
            // mid-recursion.
            var chainDepth = 1;
            var walker = chainHead;
            while (walker.LayouterState is BlockContinuation deeperBlock)
            {
                chainDepth++;
                if (chainDepth > MaxRecursionDepth)
                {
                    throw new ArgumentOutOfRangeException(
                        "incomingContinuation",
                        $"BlockLayouter.AttemptLayout: incoming BlockContinuation "
                        + $"chain depth exceeds MaxRecursionDepth ({MaxRecursionDepth}). "
                        + "Pathologically deep chains are a DoS vector against "
                        + "untrusted continuation state. Per Phase 3 Task 14 "
                        + "cycle 2 hardening Finding #1.");
                }
                walker = deeperBlock;
            }
        }

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
        // Cross-COMPONENT per-conversion grid measure cache (measurement-cache
        // cycle) — capture the shared cache the root pipeline wired through the
        // layout context into an instance field, so PreMeasureGridRowExtent + the
        // nested-context creations (which have no direct LayoutContext) can reach
        // it. Re-captured each AttemptLayout (idempotent — the same per-conversion
        // object). Null when no shared cache is wired (the per-instance fallback).
        _gridMeasureCache = layout.GridMeasureCache as GridMeasurementCache;
        // Per `multi-page-allocation-churn` — same cross-page capture for the table measurement
        // cache (re-captured each AttemptLayout; null ⇒ the dispatch path still reuses the
        // continuation's cache, only the per-page subtree re-shape stays).
        _tableMeasureCache = layout.TableMeasureCache as TableMeasurementCache;

        // Per Phase 3 Task 12 sub-cycle 2 hardening (Finding 2) —
        // clear the per-AttemptLayout nested-table content-height
        // cache. Old entries from a prior AttemptLayout could
        // reference a stale extent if the previous attempt's
        // wrapperInlineSize differed. Lazily allocated when the first
        // nested table is encountered.
        _measuredTableContentHeightCache?.Clear();

        // Per Phase 3 Task 13 cycle 1 — reset the one-shot
        // table-continuation-consumption flag for the current attempt.
        // The incoming BlockContinuation.LayouterState (if it's a
        // TableContinuation) gets consumed by the first table child
        // we encounter at the resumed index; subsequent table children
        // start fresh. On rewind retries the flag goes back to false
        // so the retry resumes the same way.
        _consumedIncomingTableContinuation = false;

        // Per Phase 3 Task 14 cycle 2 — same reset for the multicol-
        // resume one-shot flag. The carried MulticolContinuation (if
        // any) feeds into the FIRST multicol child encountered at the
        // resumed index; subsequent multicol children in the same
        // attempt start fresh.
        _consumedIncomingMulticolContinuation = false;
        // Per `inline-only-block-line-splitting` — same one-shot reset for the
        // inline-only line-split resume contract.
        _consumedIncomingInlineLineSplit = false;
        // Per Phase 3 Task 16 cycle 2 — same one-shot reset for the
        // flex multi-page resume contract.
        _consumedIncomingFlexContinuation = false;

        // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) — reset
        // the one-shot consumption flag for an incoming chained
        // BlockContinuation in LayouterState (the deep-nested
        // recursion-continuation path lifted from depth==1 only).
        _consumedIncomingBlockContinuationRecursion = false;

        // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) — reset
        // the per-page one-shot flag for
        // LAYOUT-FLOAT-BREAK-INSIDE-NESTED-001 emission.
        _emittedFloatBreakInsideNestedDiagnosticThisPage = false;

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

        // Non-block-pagination arc (flex item CONTENT layout) — a NESTED
        // layouter whose ROOT is itself inline-only would have ALL its
        // (inline) children skipped by the block-only loop below (the
        // `if (!child.IsBlockLevel) { … continue; }` guard), silently dropping
        // the content. When opted in (FlexLayouter's item-content pass),
        // dispatch the root's OWN inline content as one inline-only-block
        // fragment (box == _rootBox). Gated by _layoutRootInlineContent +
        // IsInlineOnlyBlockContainer(_rootBox) — the latter excludes
        // BoxKind.Root — so it NEVER fires for the outer pipeline layouter or
        // any non-opted-in nested caller (multicol / caption / cell /
        // recursion). startChildIdx == 0 skips a resume (the atomic LastResort
        // nested layout never resumes). An inline-only root has no block
        // children, so the dispatch outcome IS the layout outcome.
        if (_layoutRootInlineContent
            && _shaperResolver is not null
            && startChildIdx == 0
            && IsInlineOnlyRootContainer(_rootBox))
        {
            var rootInlineResult = DispatchInlineOnlyBlock(
                _rootBox, childIdx: 0, fragmentainer, ref layout, resolver,
                initialUsed, priorPagesConsumed, prevBlockMarginEnd,
                hasPriorAdjoiningBlock, lastEmittedIdx, emittedThisAttempt,
                out _, cancellationToken,
                // The outer layouter already positioned this item including its
                // margins; don't let the root's own margins offset its text or
                // inflate the measured extent (PR-#182 Copilot review).
                suppressOwnMargins: true);
            // Null = fragment emitted (the outer loop would "continue") = done.
            return rootInlineResult ?? LayoutAttemptResult.AllDone(cost: 0);
        }

        for (var childIdx = startChildIdx; childIdx < _rootBox.Children.Count; childIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var child = _rootBox.Children[childIdx];

            // Per Phase 3 Task 19 cycle 1 (+ Task 20 cycle 1) —
            // out-of-flow positioned boxes (`position: absolute` AND
            // `position: fixed`) are removed from normal flow per CSS
            // Positioned Layout L3 §6: they don't advance the in-flow
            // cursor + do NOT break margin adjacency (unlike inline
            // content, which forms a line box). So the collapse chain is
            // PRESERVED across them — skip without touching
            // hasPriorAdjoiningBlock / prevBlockMarginEnd. The actual
            // placement happens in the post-flow absolute / fixed passes
            // (EmitAbsolutelyPositionedChildren / EmitFixedPositionedChildren).
            if (child.Style.IsOutOfFlow())
            {
                continue;
            }

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
                // `inline-only-block-line-splitting` — resume a prior line split. The carried
                // InlineOnlyLineSplitContinuation (one-shot, like the multicol/flex contracts)
                // names the line to resume this block's tail at on this fresh page.
                var inlineResumeLine = 0;
                // box-decoration-break: slice (PR #222 review [P1]) — the resumed slice's cumulative physical
                // decoration offset + the composite box's total physical extent, carried in the continuation.
                var inlineResumeDecorationOffset = 0.0;
                var inlineResumeDecorationTotal = 0.0;
                if (incomingBlock?.LayouterState is InlineOnlyLineSplitContinuation inlineLineCont
                    && childIdx == incomingBlock.ResumeAtChild
                    && !_consumedIncomingInlineLineSplit)
                {
                    inlineResumeLine = inlineLineCont.ResumeLineIndex;
                    inlineResumeDecorationOffset = inlineLineCont.DecorationBlockOffsetPx;
                    inlineResumeDecorationTotal = inlineLineCont.DecorationTotalExtentPx;
                    _consumedIncomingInlineLineSplit = true;
                }
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
                    cancellationToken,
                    resumeAtLine: inlineResumeLine,
                    resumeDecorationOffsetPx: inlineResumeDecorationOffset,
                    resumeDecorationTotalPx: inlineResumeDecorationTotal);
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
                // Per Phase 3 Task 20 cycle 2 — when pagination is
                // suppressed (fixed-box content), never defer a float to a
                // next page (there is none): emit it so it overflows with
                // the rest of the content instead of being dropped.
                if (WouldFloatOverflow(child, floatSide.Value, fragmentainer)
                    && pageHasPriorContent
                    && !fragmentainer.SuppressBlockPagination)
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
            // Body % lengths (body-percent cycle): margin/padding percentages on EVERY side
            // resolve against the containing block's INLINE size (CSS 2.2 §8.3/§8.4 — including
            // the top/bottom ones); a % HEIGHT resolves against the fragmentainer's definite
            // content height below (percent-height cycle).
            // PR #218 review [P1 #2 / P2 #5] — cyclic % padding/margins resolve against 0 in an
            // INTRINSIC contribution probe (CSS Sizing §5.2.1 — the basis is indefinite), and against
            // the definite width otherwise. Only a real layout PERSISTS the resolved % padding for
            // paint; a dropped measure must leave the shared slot Percentage.
            var realInlineBase = fragmentainer.ContentInlineSize;
            var pctBase = _measurePurpose.ZeroesCyclicPercentInsets() ? 0.0 : realInlineBase;
            if (_measurePurpose == MeasurePurpose.Layout)
                child.Style.ResolveUsedPercentPaddingInPlace(realInlineBase);   // paint reads the slots later.
            var marginStart = child.Style.ReadLengthOrPercentPx(PropertyId.MarginTop, pctBase);
            var borderStart = child.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
            var paddingStart = child.Style.ReadLengthOrPercentPx(PropertyId.PaddingTop, pctBase);
            // % height resolves against the fragmentainer's DEFINITE content height (the page
            // content area — percent-height cycle, CSS 2.2 §10.5).
            var contentBlock = child.Style.ReadLengthOrPercentPx(PropertyId.Height, fragmentainer.BlockSize);
            var paddingEnd = child.Style.ReadLengthOrPercentPx(PropertyId.PaddingBottom, pctBase);
            var borderEnd = child.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
            var marginEnd = child.Style.ReadLengthOrPercentPx(PropertyId.MarginBottom, pctBase);
            var marginInlineStart = child.Style.ReadLengthOrPercentPx(PropertyId.MarginLeft, pctBase);
            var marginInlineEnd = child.Style.ReadLengthOrPercentPx(PropertyId.MarginRight, pctBase);

            // Per PR #22 review fix #3 + Copilot #2 — BoxFragment is
            // the BORDER box on both axes. Box-sizing audit: an explicit `height`
            // under `box-sizing: border-box` IS the border box (chrome inside),
            // floored at the chrome; `content-box` (initial) adds it — byte-identical.
            var borderBoxBlockSize = BoxSizingHelper.DeclaredToBorderBox(
                child.Style, contentBlock, borderStart + paddingStart + paddingEnd + borderEnd);
            // §10.7 — min-height / max-height clamp the explicit height. No-op when unset.
            // A `%` min/max-height resolves against the SAME base as `% height` above
            // (the fragmentainer's definite content height — min-max-percentage-sizing).
            borderBoxBlockSize = child.ClampBorderBoxToMinMax(
                borderBoxBlockSize, PropertyId.MinHeight, PropertyId.MaxHeight,
                fragmentainer.BlockSize);

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
            // block. Per the body-explicit-width gap fix — an explicit
            // `width` on a plain block container overrides the fill.
            var borderBoxInlineSize = ResolveInFlowBorderBoxInlineSize(
                child, availInlineSize, fragmentainer.ContentInlineSize, marginInlineStart, marginInlineEnd);
            // §10.3.3 auto margins (auto-margins cycle): `margin: 0 auto` centres an explicit-width
            // block — must run before any inline-offset consumption below. The distribution range
            // is the FLOAT-ADJUSTED available range (the same range placement uses — review P1).
            ResolveAutoInlineMargins(
                child, borderBoxInlineSize, availInlineSize,
                ref marginInlineStart, ref marginInlineEnd);

            // Per Phase 3 Task 12 sub-cycle 1 hardening (Finding 1) —
            // for Table / InlineTable wrappers, pre-measure the
            // table's row-stack content height so the wrapper's
            // border-box block extent reflects the auto-height
            // content (CSS `height: auto` returns 0 from
            // ReadLengthPxOrZero, so without this fold-in siblings
            // would overlap the table). The TableLayouter caches the
            // per-row measurements + buffered cell content; we hand
            // the same instance to EmitTableInner AFTER the wrapper
            // fragment is emitted so it doesn't redo the work.
            //
            // The pre-measure needs the wrapper's content-block
            // top-left + content-inline-size to anchor cell
            // measurements; we feed it the EXPECTED border-box
            // top-left for the Continue path (= UsedBlockSize +
            // topShift). The forced-overflow path uses the same
            // expected anchor — its actual wrapperBlockOffset equals
            // UsedBlockSize + topShift too (see forcedOverflow* below).
            // The cell-content offsets are translated from the cell-
            // local origin captured during the buffer FlushTo pass,
            // so the absolute anchor matters only for the row cursor
            // arithmetic which is consistent across measure + emit.
            TableLayouter? pendingTableLayouter = null;
            var tableMeasuredContentHeight = 0.0;
            var tableMeasuredUsedInlineSize = 0.0;
            if (child.Kind is BoxKind.Table or BoxKind.InlineTable)
            {
                var expectedBorderBoxBlockTop = fragmentainer.UsedBlockSize + topShift;
                var inFlowInlineOffsetForTable = availInlineStart + marginInlineStart;
                // Per Phase 3 Task 13 cycle 1 — if the incoming
                // continuation carries a TableContinuation in its
                // LayouterState AND we're at the table child it
                // resumed (childIdx == incomingBlock?.ResumeAtChild),
                // pass it through to the new TableLayouter so it
                // resumes at the correct row. This is the resume side
                // of the row-pagination contract — the PageComplete
                // side stashes the TableContinuation in
                // BlockContinuation.LayouterState; the resume side
                // unpacks + dispatches.
                //
                // Consume the TableContinuation (set the local to
                // null) so subsequent table children in the SAME loop
                // attempt don't accidentally inherit it. Only the
                // resumed child gets it. The outer call to
                // AttemptLayout is one-shot — but the caller may
                // re-enter on rewind, in which case the constructor's
                // _incomingContinuation still has the original
                // BlockContinuation. Per cycle 1 we tolerate that:
                // the rewind frontier is captured BEFORE the table
                // emit + restoring it discards the partial emission
                // (so the retry pays for a fresh table emit anyway).
                LayoutContinuation? tableContinuationForChild = null;
                if (incomingBlock?.LayouterState is TableContinuation incomingTableCont
                    && childIdx == incomingBlock.ResumeAtChild
                    && !_consumedIncomingTableContinuation)
                {
                    tableContinuationForChild = incomingTableCont;
                    _consumedIncomingTableContinuation = true;
                }
                pendingTableLayouter = PreMeasureTableIfNeeded(
                    wrapperChild: child,
                    wrapperInlineOffset: inFlowInlineOffsetForTable,
                    wrapperBlockOffsetExpected: expectedBorderBoxBlockTop,
                    wrapperInlineSize: borderBoxInlineSize,
                    fragmentainer: fragmentainer,
                    layout: ref layout,
                    incomingTableContinuation: tableContinuationForChild,
                    tableContentHeight: out tableMeasuredContentHeight,
                    tableMeasuredUsedInlineSize: out tableMeasuredUsedInlineSize,
                    cancellationToken: cancellationToken,
                    // Per Phase 3 Task 13 cycle 1 hardening (Finding 2)
                    // — the outer dispatch path drives the OUTER block
                    // pagination loop; it expects the wrapper to size
                    // to ONE page's committed extent so the outer
                    // resolver doesn't see the wrapper as oversized
                    // when the table will split cleanly. The nested-
                    // recursion atomic path does NOT need this (cycle
                    // 1 keeps nested tables atomic via the
                    // NoBreakBreakResolver).
                    useDryRunCommittedHeight: true);
                if (pendingTableLayouter is not null)
                {
                    // Fold the measured content height into the
                    // wrapper's border-box block size. Per CSS Tables
                    // L3 table-wrapper sizing is content-driven for
                    // auto-height; explicit CSS height acts as a
                    // floor. The wrapper's content-area extent is the
                    // table content (rows), so add the wrapper's own
                    // border + padding around it.
                    var wrapperBorderPaddingBlock =
                        borderStart + paddingStart + paddingEnd + borderEnd;
                    var tableDrivenBorderBox =
                        tableMeasuredContentHeight + wrapperBorderPaddingBlock;
                    if (tableDrivenBorderBox > borderBoxBlockSize)
                    {
                        borderBoxBlockSize = tableDrivenBorderBox;
                    }

                    // Per Phase 3 Task 12 sub-cycle 5 hardening
                    // Finding 6 — also widen the wrapper's border-box
                    // INLINE extent when the grid's used inline-size
                    // exceeds the wrapper's content-inline-size. Under
                    // auto-table-layout this fires when min-content sum
                    // overflows the wrapper (LAYOUT-TABLE-INLINE-
                    // OVERFLOW-001 also surfaces); under fixed-layout
                    // it fires when declared widths sum past the
                    // wrapper. The wrapper widens to match the grid so
                    // backgrounds/borders/captions consistently span
                    // the overflowing extent. Pre-fix the wrapper
                    // stayed at borderBoxInlineSize + the grid grew
                    // PAST it — leaving the wrapper visually narrower
                    // than its own content. (Read the inline edges
                    // here — they're not in scope from the outer block
                    // dispatch which only carries block-axis padding/
                    // border for the cursor advance.)
                    var wrapperBorderInlineStart =
                        child.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth);
                    var wrapperBorderInlineEnd =
                        child.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth);
                    var wrapperPaddingInlineStart =
                        child.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft);
                    var wrapperPaddingInlineEnd =
                        child.Style.ReadLengthPxOrZero(PropertyId.PaddingRight);
                    var wrapperBorderPaddingInline =
                        wrapperBorderInlineStart + wrapperBorderInlineEnd
                        + wrapperPaddingInlineStart + wrapperPaddingInlineEnd;
                    var tableDrivenBorderBoxInline =
                        tableMeasuredUsedInlineSize + wrapperBorderPaddingInline;
                    if (tableDrivenBorderBoxInline > borderBoxInlineSize)
                    {
                        borderBoxInlineSize = tableDrivenBorderBoxInline;
                    }
                }
            }

            // Per Phase 3 Task 14 cycle 1 hardening (Findings 1 + 2) —
            // multicol container pre-measure. For multicol containers,
            // grow `borderBoxBlockSize` to fit the columnized content
            // extent. This serves two purposes:
            //   (1) Finding 2 — when the container has `height: auto`,
            //       `borderBoxBlockSize` reflects ONLY the wrapper's
            //       border + padding (CSS height = 0 for auto). The
            //       wrapper's painted size would be ~0 px; the cursor
            //       advance (which uses the subtree-aware extent fix
            //       in MeasureSubtreeVisualBlockExtentRecursive) is
            //       correct, but the wrapper fragment is painted
            //       smaller than its content. Growing borderBoxBlockSize
            //       here makes the wrapper visually contain the columns.
            //   (2) Finding 1 — the pre-measured columnized extent is
            //       also surfaced to the outer pagination decision via
            //       the subtree-aware measure. The cursor advance below
            //       uses the columnized extent (not the serial sum) so
            //       siblings after the multicol don't overlap.
            //
            // For auto-height multicol the per-column block-size is
            // derived from the fragmentainer's REMAINING block-space
            // (= fragmentainer.BlockSize - UsedBlockSize - wrapper's
            // vertical border + padding). This is the "fill the
            // available page space columnwise" semantics CSS
            // Multi-column L1 §3.5 describes for an auto-height
            // multicol container.
            //
            // <b>Caveat: 2× cost.</b> The pre-measure runs a full
            // dry-run multicol layout against a discarding sink; the
            // subsequent emit pass re-runs it against the real outer
            // sink. Acceptable for cycle 2 hardening; sub-cycle 3+
            // may cache the result per Box.
            if (IsMulticolContainer(child))
            {
                // Per Phase 3 Task 14 cycle 2 hardening (Finding #5) —
                // multicol content-box geometry is shared with the
                // outer / recursion emit + recursion premeasure sites
                // via MulticolGeometryHelper. For Finding 2's auto-
                // height path the per-column block-size derives from
                // the fragmentainer's remaining block-space at the
                // hypothetical wrapper border-box top
                // (= fragmentainer.UsedBlockSize + topShift).
                var multicolHypotheticalBlockTop =
                    fragmentainer.UsedBlockSize + topShift;
                var multicolHypotheticalInlineLeft =
                    availInlineStart + marginInlineStart;
                var multicolPremeasureGeom = MulticolGeometryHelper.ComputeContentGeometry(
                    multicolBox: child,
                    borderBoxInlineSize: borderBoxInlineSize,
                    borderBoxBlockSize: borderBoxBlockSize,
                    borderBoxInlineOffset: multicolHypotheticalInlineLeft,
                    borderBoxBlockOffset: multicolHypotheticalBlockTop,
                    fragmentainer: fragmentainer,
                    isHeightAuto: IsHeightAuto(child));

                var multicolMeasuredColumnExtent = PreMeasureMulticolColumnExtent(
                    child,
                    contentInlineSize: multicolPremeasureGeom.ContentInlineSize,
                    contentBlockSize: multicolPremeasureGeom.ContentBlockSize,
                    fragmentainer: fragmentainer,
                    layout: ref layout,
                    cancellationToken: cancellationToken);

                // Grow borderBoxBlockSize so the painted wrapper
                // visually contains the columns. For auto-height the
                // grown value is just the columnized extent + border/
                // padding; for explicit-height it's the max of the CSS
                // height + the columnized extent + border/padding (the
                // wrapper grows when columns overflow the CSS height).
                var multicolDrivenBorderBox =
                    multicolMeasuredColumnExtent
                    + multicolPremeasureGeom.BorderPaddingBlockSum;
                if (multicolDrivenBorderBox > borderBoxBlockSize)
                {
                    borderBoxBlockSize = multicolDrivenBorderBox;
                }
            }

            // Per Phase 3 Task 16 cycle 4b — set by the
            // paginatable-flex clamp inside the IsFlexContainer
            // pre-grow block below + read by the outer flex dispatch
            // block at line ~2213 to flip <c>allowPagination</c> ON
            // when the wrapper would otherwise overflow the remaining
            // fragmentainer space. Declared OUTSIDE the pre-grow block
            // because the dispatch block is a separate
            // <c>if (IsFlexContainer(child))</c> sibling at the same
            // loop-iteration scope.
            bool paginateFlexForOuterChild = false;
            // Flex-column pagination dual-input at the OUTER site (mirrors the
            // recursive site's nestedFlex* locals): a paginating COLUMN flex must
            // size items against the NATURAL main extent (else flex-shrink fights
            // the page) while the wrapper paints + cuts at the clamped page budget.
            // Capture the pre-clamp authored border-box block size + a flag.
            double outerFlexAuthoredBorderBoxBlockSize = 0;
            // Dual-input (natural sizing + clamped page budget) paginating flex —
            // set for COLUMN nowrap (main-axis item split) AND ROW nowrap
            // (intra-item content split). Both size content against the natural
            // extent + cut at the page budget; row-WRAP keeps single-input.
            bool outerFlexDualInputPaginating = false;
            // Per Phase 3 Task 17 cycle 5b — outer-site paginatable-grid
            // gate. Flipped ON by the IsPaginatableGrid clamp below
            // (mirrors paginateFlexForOuterChild). When true, the
            // DispatchGridInner call below passes allowPagination=true
            // so GridLayouter splits rows + returns PageComplete.
            bool paginateGridForOuterChild = false;

            // Per Phase 3 Task 15 L3 post-PR-#63 hardening F#1 — flex
            // container wrapper pre-measure. Mirrors the multicol
            // pre-measure pattern above: for a flex container whose
            // BlockLayouter-derived `borderBoxBlockSize` is smaller than
            // the actual flex cross-extent (= max(item natural cross-
            // size) for L3 single-line row direction), grow
            // `borderBoxBlockSize` so the wrapper fragment painted at
            // line 1797 visually contains the items + the cursor advance
            // doesn't over-reserve page space.
            //
            // Pre-fix: when the flex container has `height: auto`,
            // BlockLayouter's regular block-sizing path falls through to
            // `MeasureSubtreeVisualBlockExtent` which stacks the items
            // vertically (= as if they were block-flow children) — for
            // items of cross-size 50/100/75 it returns ~225 px, while the
            // actual single-line flex cross-extent is max = 100 px. The
            // wrapper was over-large; siblings after the flex container
            // landed ~125 px too low + page splits triggered prematurely.
            //
            // Post-fix: the flex pre-measure derives max(item cross-size)
            // — matching FlexLayouter's own derivation for the same
            // height-auto case (CSS Flexbox L1 §9.4's max-content cross-
            // size simplification). The pre-fix subtree-stacking value
            // can still dominate via the subtreeBlockExtent path below
            // for legacy cases where the flex container has explicit
            // height + the subtree extent reads larger; the
            // `Math.Max(borderBoxBlockSize, subtreeBlockExtent)`
            // composition keeps the spec-correct "wrapper paints at
            // least its CSS height" floor.
            //
            // Sub-cycle L4+ scope: outer cross-size (item margins +
            // borders + padding) + baseline alignment + multi-line wrap
            // — move in lockstep with FlexLayouter's placement math.
            if (IsFlexContainer(child))
            {
                // Per Phase 3 Task 15 L4 — the row-direction pre-measure
                // grows the wrapper's block-axis extent to the flex
                // cross-extent (= max(item natural block-size)). For
                // column direction the main axis IS the block axis —
                // per Phase 3 Task 15 L4 post-PR-#64 review hardening
                // F#1 the wrapper's auto-block-size equals the SUM of
                // items' block-sizes (= the spec-correct main-axis
                // content extent under CSS Flexbox L1 §9.4's max-content
                // main-size simplification). Pre-F#1 the column path
                // SKIPPED the pre-measure entirely, leaving
                // borderBoxBlockSize at the auto-resolved default
                // (often 0 or the fragmentainer-remainder fallback);
                // FlexLayouter's containerMainSize then read that tiny
                // value + justify-content: center / flex-end / etc.
                // computed negative freeSpace + items overflowed. The
                // F#1 fix wires PreMeasureFlexMainExtent into the column
                // branch — both directions now grow the wrapper to the
                // spec-correct extent at premeasure time.
                //
                // Per F#4 — the helpers now take cancellationToken so
                // a long item list honors caller cancellation.
                //
                // Per Phase 3 Task 15 L6 — flex-wrap: wrap changes the
                // row-direction cross-extent derivation from max(item
                // cross-size) (single line) to sum(line cross-extents)
                // per CSS Flexbox L1 §9.4. The line-packing budget for
                // the row case is the wrapper's available inline-size
                // (= `borderBoxInlineSize` minus inline-axis border +
                // padding); we pass the content-inline-size of the
                // wrapper for the line-packing budget. For column
                // direction + wrap the main axis is the block axis
                // (= what we're computing here), so wrap can't change
                // the main-extent derivation in a single-pass auto
                // resolution — column + wrap + auto block-size falls
                // back to the sum-of-items (= single-column) extent;
                // wrap activates only when an explicit block-size is
                // declared.
                var childFlexDirection = child.Style.ReadFlexDirection();
                var childFlexWrap = child.Style.ReadFlexWrap();
                var childIsWrapping = childFlexWrap.IsFlexWrapping();
                var flexBorderPaddingBlock =
                    borderStart + paddingStart + paddingEnd + borderEnd;
                // Per Phase 3 Task 15 L6 — skip the main-extent grow
                // for column + wrap with an EXPLICIT block-size.
                // PreMeasureFlexMainExtent sums items' main-size (= the
                // single-column total) which for the wrap case exceeds
                // the declared block-size + would re-budget the
                // FlexLayouter's `containerMainSize` past the wrap
                // threshold, defeating the wrap. When the wrapper's
                // block-size is auto (= the column-direction L4 F#1
                // case), the grow remains correct: the column extends
                // to contain all items.
                var skipMainExtentGrow =
                    childFlexDirection.IsFlexColumnDirection()
                    && childIsWrapping
                    && !IsHeightAuto(child);
                double flexAxisExtent;
                // Per Phase 3 Task 15 L6 post-PR-#66 review F#5 —
                // short-circuit the pre-measure when its result will be
                // skipped. Pre-fix the column+wrap+explicit-height
                // path computed `PreMeasureFlexMainExtent` (which sums
                // every item's main-axis size) and then discarded the
                // result via `if (!skipMainExtentGrow)`. Skipping
                // BEFORE the call avoids the wasted item walk —
                // especially valuable for long item lists where the
                // sum-of-items walk is O(N).
                if (skipMainExtentGrow)
                {
                    flexAxisExtent = 0;
                }
                else if (childFlexDirection.IsFlexColumnDirection())
                {
                    flexAxisExtent = PreMeasureFlexMainExtent(
                        child, borderBoxInlineSize, cancellationToken);
                }
                else if (childIsWrapping)
                {
                    // Row + wrap — sum of line cross-extents per the
                    // multi-line algorithm. Use the wrapper's content-
                    // inline-size as the line-packing budget. The
                    // wrapper's inline-axis border + padding contribute
                    // to the budget separately from the block-axis
                    // contribution we add to flexBorderPaddingBlock.
                    //
                    // Per Phase 3 Task 16 cycle 4c (P3 #8 from PR-#79)
                    // — the line-packing algorithm has been extracted
                    // to <see cref="FlexLinePacker"/>.
                    // <see cref="PreMeasureFlexMultiLineCrossExtent"/>
                    // below delegates to
                    // <see cref="FlexLinePacker.SumCrossExtent"/> (=
                    // streaming variant per PR-#84 P2 #1) +
                    // <see cref="FlexLayouter.PackLines"/> calls
                    // <see cref="FlexLinePacker.Pack"/>. The pre-L7
                    // duplication is gone.
                    var inlineBorderStart =
                        child.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth);
                    var inlineBorderEnd =
                        child.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth);
                    var inlinePaddingStart =
                        child.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft);
                    var inlinePaddingEnd =
                        child.Style.ReadLengthPxOrZero(PropertyId.PaddingRight);
                    var contentInlineSize = Math.Max(0,
                        borderBoxInlineSize
                        - inlineBorderStart - inlinePaddingStart
                        - inlinePaddingEnd - inlineBorderEnd);
                    flexAxisExtent = PreMeasureFlexMultiLineCrossExtent(
                        child, childFlexDirection,
                        containerMainSize: contentInlineSize,
                        cancellationToken);
                }
                else
                {
                    flexAxisExtent = PreMeasureFlexCrossExtent(
                        child, borderBoxInlineSize, cancellationToken);
                }
                if (!skipMainExtentGrow)
                {
                    var flexDrivenBorderBox = flexAxisExtent + flexBorderPaddingBlock;
                    if (flexDrivenBorderBox > borderBoxBlockSize)
                    {
                        borderBoxBlockSize = flexDrivenBorderBox;
                    }
                }

                // Per Phase 3 Task 16 cycle 4b — paginatable-flex extent
                // clamp at the outer-dispatch site. Mirrors the recursive
                // site's clamp at the EmitBlockSubtreeRecursive
                // sibling block: when the wrapper's grown natural extent
                // would overflow the remaining fragmentainer space AND
                // the container is eligible per
                // <see cref="IsPaginatableFlex"/>, clamp
                // <c>borderBoxBlockSize</c> to what fits + flip
                // <c>paginateFlexForOuterChild</c> ON so the dispatch
                // block at line ~2213 passes <c>allowPagination: true</c>
                // through to FlexLayouter. With the clamp in place the
                // break-check below sees a fitting chunk (= no
                // forced-overflow path) + the normal Continue path
                // dispatches with pagination ENABLED — FlexLayouter
                // packs lines up to the clamped budget + emits a
                // FlexContinuation for the rest (CSS Flexbox L1 §10).
                //
                // <para><b>Why clamp before the break check.</b>
                // The break check uses chunkForBreakCheck which derives
                // from borderBoxBlockSize; clamping here makes the
                // chunk fit the remaining page space + skips the
                // forced-overflow path that would otherwise paint the
                // full natural extent + silently drop overflow lines.
                // The clamp is mathematically safe: FlexLayouter (with
                // allowPagination: true) always emits at least the
                // first remaining line on each fragment per CSS
                // Fragmentation L3 §4.4, so the clamped wrapper
                // visually contains at least one line of content (=
                // no zero-progress page).</para>
                // PR-#182 review P1 — suppressed for nested content (a discarded
                // PageComplete would drop the deferred flex items).
                if (IsPaginatableFlex(child) && !_disableFlexPagination)
                {
                    var pageRemainingForOuterFlex =
                        fragmentainer.BlockSize
                        - fragmentainer.UsedBlockSize - topShift;
                    // Eligibility identical to the recursive site —
                    // wrapper would overflow + remaining can host
                    // chrome plus content.
                    if (pageRemainingForOuterFlex > 0
                        && pageRemainingForOuterFlex < borderBoxBlockSize
                        && pageRemainingForOuterFlex > flexBorderPaddingBlock)
                    {
                        // Column nowrap (main-axis item split) AND row nowrap
                        // (intra-item content split): remember the natural
                        // (pre-clamp) border-box block size so the dispatch sizes
                        // content against it + cuts against the clamped page
                        // budget. Row-WRAP keeps the single-input clamp (the
                        // line-split fits lines to the budget directly).
                        var childIsColumnDir = childFlexDirection.IsFlexColumnDirection();
                        if (childIsColumnDir || !childIsWrapping)
                        {
                            outerFlexAuthoredBorderBoxBlockSize = borderBoxBlockSize;
                            outerFlexDualInputPaginating = true;
                        }
                        borderBoxBlockSize = pageRemainingForOuterFlex;
                        paginateFlexForOuterChild = true;
                    }
                }

                // NB: paginatable-grid clamp moved to AFTER the grid
                // pre-measure (= line ~1695) so it operates on the
                // GROWN borderBoxBlockSize, not the chrome-only init.
                // The flex clamp above works on grown extent because
                // the flex pre-measure runs INSIDE the flex branch
                // above; grid's pre-measure is a separate gate at
                // line ~1681 that we must wait for.
            }

            // Per Phase 3 Task 17 cycle 1 post-PR-#92 review F2 — grid
            // container pre-measure for auto-height wrappers. Without
            // this the wrapper's borderBoxBlockSize stays at the
            // chrome-only initialization (= 0 + borders + padding) when
            // the author didn't declare height; following block-flow
            // siblings then visually overlap the grid rows because the
            // outer cursor advances by 0 + chrome instead of by the
            // grid's natural row-track sum.
            //
            // Cycle 1 contract: sum the explicit row tracks (= Length
            // entries only; cycle 2-7 will widen to fr / intrinsic /
            // repeat). The sum is the natural block extent of the
            // grid's content area; add the wrapper's own block-axis
            // chrome to get the natural border-box block extent. If
            // larger than the current borderBoxBlockSize, grow.
            //
            // Mirrors the flex / multicol / table pre-measure pattern
            // earlier in this outer dispatch path. Cycle 5 will add a
            // paginatable-grid clamp here too (= matching the
            // paginatable-flex clamp above when grid pagination ships).
            if (IsGridContainer(child) && IsHeightAuto(child))
            {
                // PR-#181 review P1 — pass the grid's REAL content inline size (so
                // auto-repeat column templates resolve the same column count the
                // dispatch will) + the cancellation token (P2). The content geometry
                // is derived the same way as the dispatch (GridGeometryHelper) so
                // pre-measure + emit agree.
                // Offsets are 0 here — the in-flow offsets aren't computed until
                // the emit phase below, and only ContentInlineSize is read (it
                // depends on borderBoxInlineSize + the grid's inline chrome, not
                // the offsets).
                var preGridGeom = GridGeometryHelper.ComputeContentGeometry(
                    gridBox: child,
                    borderBoxInlineSize: borderBoxInlineSize,
                    borderBoxBlockSize: borderBoxBlockSize,
                    borderBoxInlineOffset: 0,
                    borderBoxBlockOffset: 0);
                var gridRowExtent = PreMeasureGridRowExtent(
                    child, preGridGeom.ContentInlineSize, cancellationToken);
                if (gridRowExtent > 0)
                {
                    var gridBorderPaddingBlock =
                        borderStart + paddingStart + paddingEnd + borderEnd;
                    var gridDrivenBorderBox =
                        gridRowExtent + gridBorderPaddingBlock;
                    if (gridDrivenBorderBox > borderBoxBlockSize)
                    {
                        borderBoxBlockSize = gridDrivenBorderBox;
                    }
                }
            }

            // Per Phase 3 Task 17 cycle 5c.2b — paginatable-grid
            // outer-site extent clamp + gate-flip (REACTIVATED after
            // the cycle-5b PR-#97 revert). The cycle-5a F1 pre-
            // dispatch row-fit decision (above, BEFORE the wrapper
            // emit) handles the Strict-defer pre-empt; cycle-5b's
            // initial activation was reverted because the same site
            // emitted the wrapper at the clamped budget extent even
            // when GridLayouter only emitted K of N rows (= F2's
            // empty trailing space + cursor inflation). Cycle 5c.2b
            // ships F2's wrapper-resize consumer (below at the
            // IsGridContainer dispatch branch) which mutates the
            // wrapper's BlockSize to <c>chrome + emittedExtent</c>
            // post-dispatch + recomputes the cursor advance to
            // match. With F1 + F2 both in place, this clamp is safe
            // to re-enable.
            //
            // <para>Mirrors the paginatable-flex clamp at
            // line ~1630 — when the wrapper's grown natural extent
            // would overflow the remaining fragmentainer space AND
            // the container is eligible per
            // <see cref="IsPaginatableGrid"/>, clamp
            // <c>borderBoxBlockSize</c> to what fits + flip
            // <c>paginateGridForOuterChild</c> ON so the dispatch
            // block below passes
            // <c>allowPagination: true</c> through to
            // <c>GridLayouter</c>. With the clamp in place the
            // break-check below sees a fitting chunk (= no forced-
            // overflow path) + the normal Continue path dispatches
            // with pagination enabled — <c>GridLayouter</c> emits
            // rows up to the clamped budget + returns
            // <c>PageComplete(GridContinuation)</c> for the rest;
            // the F2 consumer then resizes the wrapper to the
            // actual emitted extent (= eliminates the empty
            // trailing space + corrects sibling placement).</para>
            //
            // <para>F3 (= explicit-height grids) — SHIPPED in
            // cycle 5c.3 + post-PR-#110 review P1#2 via the
            // dual-input <c>pageBlockBudget</c> separation in
            // <see cref="GridLayouter.ConfigureEmission"/> + the
            // paginatable-grid projection in
            // <see cref="MeasureSubtreeVisualBlockExtentRecursive"/>.
            // See the resolved
            // <c>grid-explicit-height-paginate-deferral</c> entry
            // in <c>docs/deferrals.md</c> for the full ship
            // summary + the residual cursor-advance approximation
            // tracked under
            // <c>recursive-block-continuation-consumed-extent-accounting-deferral</c>.
            // </para>
            // Per Phase 3 Task 17 cycle 5c.2b post-PR-#100 review
            // P1#1 — <c>_disableGridPagination</c> gate. Nested
            // BlockLayouters (grid-item / table-cell / table-
            // caption contexts) cannot propagate a
            // BlockContinuation to their parent layouter; the
            // parent intentionally discards the inner result.
            // Activating the clamp here would route
            // PageComplete(GridContinuation) up + the parent's
            // discard would silently drop remaining grid rows.
            //
            // <para>Per Phase 3 Task 17 cycle 5c.2c post-PR-#101
            // review P1#1 — <c>IsHeightAuto(child)</c> gate was
            // RESTORED then REMOVED again by cycle 5c.3 once the
            // dual-input separation made the clamp safe for
            // explicit-height grids. The cycle-5c.2c F3 subtree-
            // extent clamp approach corrupted row geometry because
            // the clamp fed the shrunk <c>borderBoxBlockSize</c>
            // into <see cref="GridLayouter.ConfigureEmission"/> /
            // <see cref="GridSizing.Resolve"/> as the
            // <c>contentBlockSize</c>, causing fr / definite-height
            // row sizing to redistribute against the SMALLER
            // budget. Cycle 5c.3's <c>pageBlockBudget</c> parameter
            // separates "row sizing input" (= authored geometry)
            // from "page budget" (= clamped geometry); the
            // dispatch site below computes both + threads them as
            // separate <see cref="GridLayouter.ConfigureEmission"/>
            // arguments. The full <c>GridFragmentPlan</c> perf
            // consolidation across pre-measure + F1 + dispatch is
            // still tracked under
            // <c>grid-fragment-plan-shared-sizing-deferral</c>;
            // <c>grid-explicit-height-paginate-deferral</c> is
            // RESOLVED end-to-end (see deferrals.md).</para>
            // Per Phase 3 Task 17 cycle 5c.3 — capture the AUTHORED
            // (= pre-clamp) border-box block size so the
            // <see cref="DispatchGridInner"/> call below can pass the
            // authored geometry to <see cref="GridSizing.Resolve"/>
            // while the clamped value drives break-check + page
            // budget. For auto-height grids the two are equal (=
            // the clamp shrinks the natural extent which was the
            // authored "auto" computed value), so the dual-input
            // mechanism degenerates to legacy behavior. For
            // explicit-height grids the authored value preserves
            // the row geometry (= `1fr` distributes against
            // authored height, not page-remaining), fixing the
            // `grid-explicit-height-paginate-deferral` correctness
            // gap.
            var authoredBorderBoxBlockSize = borderBoxBlockSize;
            // Per Phase 3 Task 17 cycle 5c.3 — `IsHeightAuto(child)`
            // gate REMOVED. The cycle-5c.2c gate was reverted
            // because clamping `borderBoxBlockSize` for explicit-
            // height grids fed the shrunk size into
            // <see cref="GridSizing.Resolve"/>, silently corrupting
            // fr / definite-height row sizing. Cycle 5c.3 ships
            // the minimum-viable separation: authored size flows
            // into Resolve (= row geometry preserved); clamped size
            // flows into break-check + page budget (= correct
            // pagination cut-off). The full
            // `GridFragmentPlan` refactor
            // (`grid-fragment-plan-shared-sizing-deferral`) is a
            // future optimization that consolidates the §11
            // sizing work across pre-measure + F1 + dispatch;
            // this cycle only fixes the correctness gap.
            if (IsPaginatableGrid(child)
                && !_disableGridPagination)
            {
                var pageRemainingForOuterGrid =
                    fragmentainer.BlockSize
                    - fragmentainer.UsedBlockSize - topShift;
                var outerGridBorderPaddingBlock =
                    borderStart + paddingStart + paddingEnd + borderEnd;
                if (pageRemainingForOuterGrid > 0
                    && pageRemainingForOuterGrid < borderBoxBlockSize
                    && pageRemainingForOuterGrid > outerGridBorderPaddingBlock)
                {
                    borderBoxBlockSize = pageRemainingForOuterGrid;
                    paginateGridForOuterChild = true;
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
            var subtreeBlockExtent = MeasureSubtreeVisualBlockExtent(
                child, cancellationToken, parentContentBlockSize: fragmentainer.BlockSize,
                // This child's block-start offset on the current page — threaded so a table nested in its
                // subtree (below preceding content / wrapper padding) reserves the correct remaining page
                // space in its dry-run, instead of over-committing and tripping a false forced-overflow
                // that clips the table (offset-table clip fix).
                blockOffsetOnPage: fragmentainer.UsedBlockSize + topShift);
            // Per Phase 3 Task 13 cycle 1 hardening (Finding 2) — for
            // Table / InlineTable wrappers in the OUTER dispatch path,
            // the subtree extent must reflect the SINGLE-PAGE-COMMITTED
            // dry-run extent (already folded into `borderBoxBlockSize`
            // by the PreMeasureTableIfNeeded call above when
            // `useDryRunCommittedHeight: true`). The recursive measure
            // walks the table's natural content extent — which would
            // include rows that defer to a future page — so we clamp
            // it back to the wrapper's already-shrunken
            // `borderBoxBlockSize`. Without this clamp the outer
            // pagination sees the wrapper as oversized + emits a
            // false PAGINATION-FORCED-OVERFLOW-001 even when the table
            // splits cleanly internally.
            if (pendingTableLayouter is not null
                && subtreeBlockExtent > borderBoxBlockSize)
            {
                subtreeBlockExtent = borderBoxBlockSize;
            }
            // Per Phase 3 Task 15 L3 post-PR-#63 hardening F#1 — for
            // flex containers in the OUTER dispatch path the
            // `MeasureSubtreeVisualBlockExtent` walk sums the items'
            // block-axis extents as if they were block-flow children
            // (CSS Flexbox L1 §4 — flex items DON'T stack vertically
            // in the cross axis; the spec uses the line's max
            // hypothetical cross-size for single-line containers). The
            // flex pre-measure above already grew `borderBoxBlockSize`
            // to the spec-correct max(item cross-size); clamp the
            // subtree extent back to that value so siblings after the
            // flex container land at the right page offset + the outer
            // pagination doesn't over-defer. Mirrors the
            // pendingTableLayouter clamp above for the same reason
            // (the recursive walk over-measures content that has its
            // own internal layout discipline).
            // Per Phase 3 Task 15 L4 — the row-direction clamp only
            // applies when the flex container's main axis is NOT the
            // block axis. For column direction the main axis IS the
            // block axis — the block-flow stacking sum that
            // MeasureSubtreeVisualBlockExtent produces IS the correct
            // wrapper extent (the items genuinely stack vertically
            // along the main axis). The L3 hardening's clamp would
            // shrink the column wrapper to max(item block-size) which
            // truncates the lower items; the row-direction clamp
            // correctly counteracted the over-measurement caused by
            // the recursive walk treating items as block-flow children
            // when the spec says they are single-line horizontally.
            //
            // Per Phase 3 Task 15 L6 post-PR-#66 review F#1 — the
            // column-direction case gets the SAME clamp when the
            // container is wrapping AND has an explicit (LengthPx)
            // block-size declared. Under those conditions the wrapper's
            // block extent is fixed by the declaration (= the wrap
            // threshold the FlexLayouter packed against); leaving
            // `subtreeBlockExtent` at the un-wrapped block-flow sum
            // (= sum of items along the main axis as if no wrap
            // existed) would over-reserve space → siblings land too
            // low + false page breaks. The clamp mirrors the
            // row-direction path: bring the over-measured subtree
            // extent back to the wrapper's declared border-box block
            // size. (Column + wrap + auto-height falls through to the
            // pre-clamp behavior: there's no explicit threshold so the
            // sum-of-items IS the spec-correct extent.) The
            // skipMainExtentGrow predicate above and this clamp move
            // in lockstep — both fire on the same condition.
            if (IsFlexContainer(child)
                && !child.Style.ReadFlexDirection().IsFlexColumnDirection()
                && subtreeBlockExtent > borderBoxBlockSize)
            {
                subtreeBlockExtent = borderBoxBlockSize;
            }
            else if (IsFlexContainer(child)
                && child.Style.ReadFlexDirection().IsFlexColumnDirection()
                && child.Style.ReadFlexWrap().IsFlexWrapping()
                && child.Style.Get(PropertyId.Height).Tag == ComputedSlotTag.LengthPx
                && subtreeBlockExtent > borderBoxBlockSize)
            {
                subtreeBlockExtent = borderBoxBlockSize;
            }
            // Per Phase 3 Task 17 cycle 5c.2c post-PR-#101 review
            // P1#1 + cycle 5c.3 — the F3 subtree-extent clamp is
            // RE-ENABLED with the dual-input fix from cycle 5c.3.
            // The cycle-5c.2c version corrupted row geometry
            // because the clamp fed the shrunk
            // <c>borderBoxBlockSize</c> into
            // <c>GridSizing.Resolve</c> as both the geometry input
            // AND the page budget. Cycle 5c.3 ships
            // <see cref="GridLayouter.ConfigureEmission"/>'s
            // <c>pageBlockBudget</c> parameter that separates the
            // two: dispatch passes AUTHORED contentBlockSize to
            // Resolve (= row geometry preserved) + clamped
            // contentBlockSize as pageBlockBudget (= pagination
            // cut-off). The clamp here can now also fire for
            // explicit-height grids without silently losing
            // authored row sizing — the dispatch site (line ~3041)
            // computes both authored + clamped geometries when the
            // gate fires.
            //
            // <para>Mirrors the flex clamp at line ~1984 — when the
            // grid is paginatable AND the outer-site gate fired
            // (= cleared the page-budget arithmetic above), clamp
            // <c>subtreeBlockExtent</c> back to the clamped
            // <c>borderBoxBlockSize</c> so the break-check below
            // sees a chunk that fits within page-remaining and
            // takes the Continue path (= GridLayouter dispatch
            // paginates rows) instead of the forced-overflow path
            // (= would emit the wrapper at clamped extent + dispatch
            // atomically with mismatched content sizing).</para>
            if (IsGridContainer(child)
                && paginateGridForOuterChild
                && subtreeBlockExtent > borderBoxBlockSize)
            {
                subtreeBlockExtent = borderBoxBlockSize;
            }
            //
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
            // CSS Fragmentation L3 §3.1 — an author FORCED break is offered here (the
            // boundary between the previously-emitted in-flow sibling and this child):
            // `break-before: page` (etc.) on THIS child OR `break-after: page` on the
            // prior sibling forces a page break. Per §3.1 a forced break at the very
            // START of a fragmentainer is ignored (it would make an empty fragment), so
            // gate on `atFragmentainerStart` — without it the forced-overflow guard below
            // would mislabel an already-satisfied break as an overflow.
            // §3.2 — `break-inside: avoid` on THIS box (every between-children boundary is
            // an internal break of `_rootBox`), plus `break-before: avoid` on this child /
            // `break-after: avoid` on the prior sibling, mark the boundary AvoidBreak so the
            // cost model penalizes breaking here (honored by the optimizing resolver; the
            // production greedy resolver is cost-insensitive — see the deferral note).
            var atFragmentainerStart = emittedThisAttempt == 0
                && fragmentainer.UsedBlockSize == initialUsed;
            var prevEmittedChild = lastEmittedIdx >= 0 && lastEmittedIdx < _rootBox.Children.Count
                ? _rootBox.Children[lastEmittedIdx]
                : null;
            var (forceBreakBefore, avoidBreakHere, forceParityBefore) = ResolveChildBreakMetadata(
                _rootBox, child, prevEmittedChild, suppressForce: atFragmentainerStart);
            // CSS Page L3 §3.4.1 — a forced left/right/recto/verso break always ends the page
            // (forced breaks are unconditional + suppressForce already guards the page start), so
            // record its parity now for the driver's blank-page (`@page :blank`) insertion.
            if (forceBreakBefore && forceParityBefore != PageParity.Any)
                _forcedBreakParityForNextPage = forceParityBefore;
            var opportunity = BreakOpportunity.Block(
                usedBlockSize: fragmentainer.UsedBlockSize,
                chunkBlockSize: chunkForBreakCheck,
                forceBreak: forceBreakBefore,
                avoidBreak: avoidBreakHere,
                forceParity: forceParityBefore);
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
                // Per Finding 1 — release the pre-measured TableLayouter
                // on the Rewind early-return so we don't leak the
                // measure-phase state. The retry constructs a fresh
                // one (and re-measures).
                pendingTableLayouter?.Dispose();
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
                    // Per Phase 3 Task 19 cycle 2a post-PR-#113 review
                    // P1#1 — record positioned-CB establishers emitted
                    // via the forced-overflow path too, so abspos
                    // descendants anchored to a force-emitted positioned
                    // box resolve correctly instead of deferring.
                    RecordPositionedBoxGeometry(
                        child, forcedOverflowInlineOffset, forcedOverflowChildBlockOffset,
                        borderBoxInlineSize, borderBoxBlockSize);

                    // Per Phase 3 Task 16 cycle 4b post-PR-#83 review
                    // P1 #2 — flex container forced-overflow re-route.
                    // If <c>child</c> is itself a FlexContainer, the
                    // <see cref="EmitBlockSubtreeRecursive"/> walk below
                    // would walk INTO the flex items as if they were
                    // block-flow children — stacking them vertically
                    // instead of routing through FlexLayouter's
                    // per-axis placement. Result: dropped/misplaced
                    // items for column / wrap-reverse / nowrap +
                    // any case where the cycle-4b paginatable-flex
                    // extent clamp didn't fire (= ineligible OR
                    // remaining space couldn't fit chrome).
                    //
                    // The fix: dispatch through
                    // <see cref="DispatchFlexInner"/> with
                    // <c>allowPagination: false</c> (= atomic emit; the
                    // wrapper is already painted at its natural extent
                    // above per the forced-overflow contract; flex
                    // items emit correctly via FlexLayouter even when
                    // they visually overflow the wrapper / page). The
                    // outbound continuation propagation matches the
                    // Continue path's pattern below + the table /
                    // multicol forced-overflow pattern (= wrap any
                    // returned FlexContinuation in a
                    // BlockContinuation up to AttemptLayout).
                    if (IsFlexContainer(child))
                    {
                        // Per Phase 3 Task 16 cycle 4d (PR-#82 review #2)
                        // — content-box geometry from the shared helper.
                        var forcedFlexGeom = FlexGeometryHelper.ComputeContentGeometry(
                            flexBox: child,
                            borderBoxInlineSize: borderBoxInlineSize,
                            borderBoxBlockSize: borderBoxBlockSize,
                            borderBoxInlineOffset: forcedOverflowInlineOffset,
                            borderBoxBlockOffset: forcedOverflowChildBlockOffset);
                        var forcedFlexContentInlineSize = forcedFlexGeom.ContentInlineSize;
                        var forcedFlexContentBlockSize = forcedFlexGeom.ContentBlockSize;
                        var forcedFlexContentInlineOffset = forcedFlexGeom.ContentInlineOffset;
                        var forcedFlexContentBlockOffset = forcedFlexGeom.ContentBlockOffset;

                        // Forward incoming FlexContinuation (mirrors
                        // the Continue path's one-shot consume).
                        FlexContinuation? forcedFlexIncoming = null;
                        if (incomingBlock?.LayouterState is FlexContinuation incomingForcedFlex
                            && childIdx == incomingBlock.ResumeAtChild
                            && !_consumedIncomingFlexContinuation)
                        {
                            forcedFlexIncoming = incomingForcedFlex;
                            _consumedIncomingFlexContinuation = true;
                        }

                        // allowPagination: false — the forced-overflow
                        // path is the "we have to commit anyway"
                        // branch; pagination would have intercepted
                        // earlier via the cycle-4b clamp if eligible.
                        // Ineligible containers (column / wrap-reverse
                        // / nowrap) commit atomically at the cost of
                        // visual overflow + the diagnostic above.
                        var forcedFlexResult = DispatchFlexInner(
                            flexBox: child,
                            contentInlineOffset: forcedFlexContentInlineOffset,
                            contentBlockOffset: forcedFlexContentBlockOffset,
                            contentInlineSize: forcedFlexContentInlineSize,
                            contentBlockSize: forcedFlexContentBlockSize,
                            incomingContinuation: forcedFlexIncoming,
                            allowPagination: false,
                            fragmentainer: fragmentainer,
                            layout: ref layout,
                            cancellationToken: cancellationToken,
                            lastEmittedBlockExtent: out _); // atomic — no wrapper resize

                        // Advance + propagate. Cursor uses
                        // marginBoxBlockSizeForCursor (= subtree-aware
                        // span; here = own border box since the inner
                        // is atomic + already inside the wrapper).
                        fragmentainer.UsedBlockSize = Math.Max(0,
                            fragmentainer.UsedBlockSize + marginBoxBlockSizeForCursor);
                        emittedThisAttempt++;
                        lastEmittedIdx = childIdx;

                        pendingTableLayouter?.Dispose();

                        if (forcedFlexResult.Outcome == LayoutAttemptOutcome.PageComplete
                            && forcedFlexResult.Continuation is FlexContinuation forcedFlexCont)
                        {
                            return LayoutAttemptResult.PageComplete(
                                new BlockContinuation(
                                    ResumeAtChild: childIdx,
                                    ConsumedBlockSize: priorPagesConsumed
                                        + (fragmentainer.UsedBlockSize - _pageStartUsedBlockSize),
                                    LayouterState: forcedFlexCont),
                                cost: forcedFlexResult.Cost);
                        }
                        // AllDone: forced-overflow forward-progress.
                        // The wrapper committed; no resume token; end
                        // the page (= what the prior path did via the
                        // fall-through to the PageComplete return at
                        // the bottom of this branch).
                        return LayoutAttemptResult.PageComplete(
                            new BlockContinuation(
                                ResumeAtChild: childIdx + 1,
                                ConsumedBlockSize: priorPagesConsumed
                                    + (fragmentainer.UsedBlockSize - _pageStartUsedBlockSize)),
                            cost: CostModel.BreakInsideAvoidViolation);
                    }

                    // Per Phase 3 Task 17 cycle 1 (Hello World) —
                    // forced-overflow re-route for grid containers.
                    // Mirrors the flex path above: GridLayouter dispatches
                    // atomically (cycle 1 doesn't paginate) so grid items
                    // emit correctly inside the wrapper even when the
                    // wrapper visually overflows the page.
                    if (IsGridContainer(child))
                    {
                        var forcedGridGeom = GridGeometryHelper.ComputeContentGeometry(
                            gridBox: child,
                            borderBoxInlineSize: borderBoxInlineSize,
                            borderBoxBlockSize: borderBoxBlockSize,
                            borderBoxInlineOffset: forcedOverflowInlineOffset,
                            borderBoxBlockOffset: forcedOverflowChildBlockOffset);
                        _ = DispatchGridInner(
                            gridBox: child,
                            contentInlineOffset: forcedGridGeom.ContentInlineOffset,
                            contentBlockOffset: forcedGridGeom.ContentBlockOffset,
                            contentInlineSize: forcedGridGeom.ContentInlineSize,
                            contentBlockSize: forcedGridGeom.ContentBlockSize,
                            fragmentainer: fragmentainer,
                            layout: ref layout,
                            cancellationToken: cancellationToken,
                            lastEmittedBlockExtent: out _);

                        fragmentainer.UsedBlockSize = Math.Max(0,
                            fragmentainer.UsedBlockSize + marginBoxBlockSizeForCursor);
                        emittedThisAttempt++;
                        lastEmittedIdx = childIdx;

                        pendingTableLayouter?.Dispose();

                        return LayoutAttemptResult.PageComplete(
                            new BlockContinuation(
                                ResumeAtChild: childIdx + 1,
                                ConsumedBlockSize: priorPagesConsumed
                                    + (fragmentainer.UsedBlockSize - _pageStartUsedBlockSize)),
                            cost: CostModel.BreakInsideAvoidViolation);
                    }

                    // Per Phase 3 Task 7 cycle 2b — recursively emit
                    // fragments for the child's block-level descendants.
                    // The painter sees the full subtree on the
                    // committed page even though the child overflowed
                    // the fragmentainer (forced-overflow forward
                    // progress). Per cycle-2b post-PR-28 review #2 —
                    // CT threaded through to abort deep traversals.
                    //
                    // Per Phase 3 Task 14 cycle 2 hardening (Finding #1)
                    // — capture the recursion's return; if non-null,
                    // propagate as a chained BlockContinuation up. The
                    // forced-overflow forward-progress contract pins
                    // the wrapper child's first slice to THIS page;
                    // any deeper break the recursion encounters still
                    // needs a continuation, otherwise content vanishes.
                    //
                    // Per post-PR-#57 review #2 Finding #1 — preserve
                    // the full chain (no flatten). See the matching
                    // comment at the regular recursive-walk site below
                    // for the rationale.
                    //
                    // Per Phase 3 Task 16 cycle 4b post-PR-#83 review
                    // P1 #1 + P2 #3 — INBOUND chain forwarding. When
                    // the outer AttemptLayout was given an incoming
                    // BlockContinuation whose <c>ResumeAtChild</c>
                    // matches THIS forced-overflow child AND its
                    // <c>LayouterState</c> is a deeper
                    // BlockContinuation, forward that deeper chain to
                    // the recursion as <c>incomingContinuation</c>.
                    // Without this, paginated flex containers driven
                    // through forced-overflow (= when margin-bottom or
                    // wrapper-extent-after-pagination tips the chunk
                    // past the resolver fit-check) would restart from
                    // line 0 on every page = infinite loop /
                    // duplicate content. Mirrors the regular Continue
                    // path's <c>recIncoming</c> peel below.
                    LayoutContinuation? forcedRecIncoming = null;
                    if (incomingBlock?.LayouterState is BlockContinuation forcedDeeperBlock
                        && childIdx == incomingBlock.ResumeAtChild
                        && !_consumedIncomingBlockContinuationRecursion)
                    {
                        forcedRecIncoming = forcedDeeperBlock;
                        _consumedIncomingBlockContinuationRecursion = true;
                    }
                    var forcedNestedRet = EmitBlockSubtreeRecursive(
                        child,
                        parentBlockOffset: forcedOverflowChildBlockOffset,
                        parentInlineOffset: forcedOverflowInlineOffset,
                        parentInlineSize: borderBoxInlineSize,
                        cancellationToken: cancellationToken,
                        depth: 1,
                        propagatingResolver: resolver,
                        propagatingFragmentainer: fragmentainer,
                        incomingContinuation: forcedRecIncoming,
                        parentContentBlockSize: contentBlock);   // % height base (percent-height cycle).
                    if (forcedNestedRet is BlockContinuation forcedDeep)
                    {
                        return LayoutAttemptResult.PageComplete(
                            new BlockContinuation(
                                ResumeAtChild: childIdx,
                                ConsumedBlockSize: priorPagesConsumed
                                    + (fragmentainer.UsedBlockSize - _pageStartUsedBlockSize),
                                LayouterState: forcedDeep),
                            cost: 0);
                    }
                    // Per Phase 3 Task 12 sub-cycle 1 hardening
                    // (Finding 1) — drain the pre-measured table
                    // content into the outer sink. The wrapper's
                    // outer fragment is already emitted above (with
                    // a border-box size that includes the measured
                    // table content height); EmitTableInner only
                    // appends the row + cell fragments + flushes the
                    // buffered cell content in paint-safe order.
                    var tableInnerResult = EmitTableInner(
                        pendingTableLayouter,
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
                    // Per Phase 3 Task 13 cycle 1 — if the table
                    // itself returned PageComplete (partial row
                    // emission), stash the TableContinuation in the
                    // outer BlockContinuation's LayouterState so the
                    // next page's BlockLayouter knows to resume the
                    // table at the right row. The outer resume point
                    // is THIS child (the table wrapper), not the next
                    // one — because the wrapper still has unemitted
                    // rows; the resume page constructs a fresh
                    // TableLayouter with the carried TableContinuation
                    // + re-uses the wrapper Box.
                    var tableCont = tableInnerResult.Continuation as TableContinuation;
                    var (forcedResumeAtChild, forcedLayouterState) =
                        tableCont is not null
                            ? (childIdx, (object?)tableCont)
                            : (childIdx + 1, (object?)null);
                    return LayoutAttemptResult.PageComplete(
                        new BlockContinuation(
                            ResumeAtChild: forcedResumeAtChild,
                            ConsumedBlockSize: priorPagesConsumed
                                + (fragmentainer.UsedBlockSize - _pageStartUsedBlockSize),
                            LayouterState: forcedLayouterState),
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
                // Per Finding 1 — release the pre-measured TableLayouter
                // on the normal-break early-return; the retry on the
                // next page will reconstruct + remeasure.
                pendingTableLayouter?.Dispose();
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

            // Per Phase 3 Task 17 cycle 5c.2a F1 — pre-dispatch row-fit
            // decision for paginatable grids. When a paginatable grid
            // (= an outer-site direct child) whose first remaining row
            // exceeds the page-remaining content area would, on
            // dispatch + Strict, return
            // <c>PageComplete(GridContinuation(startRow, _, 0))</c>
            // from <c>GridLayouter</c>, the wrapper fragment would
            // already be committed here — leaving an empty wrapper box
            // visible on the prior page. F1 resolves that by routing
            // PageComplete from BlockLayouter directly when the first
            // remaining row won't fit, BEFORE the wrapper is emitted.
            //
            // <para><b>OBSERVABLE FOR DIRECT-OUTER-SITE CALLERS.</b>
            // Per PR-#99 review P2#1, this is an active outer-site
            // behavioral change at the <c>BlockLayouter.AttemptLayout</c>
            // contract level: any caller constructing a BlockLayouter
            // directly with a paginatable-grid outer-site child + a
            // tight remaining page extent will observe the
            // skip-emit / defer routing. Production HTML fixtures
            // currently route grids through the recursive
            // <see cref="EmitBlockSubtreeRecursive"/> path which is
            // unaffected (= recursion site keeps cycle-1 atomic
            // dispatch until cycle 5c.2d wires it); but
            // direct-construction tests + future outer-site grid
            // pipelines see the F1 routing immediately.</para>
            //
            // <para>Gating:
            //   * <c>IsPaginatableGrid</c> identifies grid containers.
            //     Every grid is paginatable per cycle-5b's predicate
            //     (= a grid with one row degenerates to atomic
            //     behavior naturally; multi-row grids paginate at row
            //     boundaries).
            //   * <c>strategy != LastResort</c> preserves the CSS
            //     Fragmentation L3 §4.4 progress rule under LastResort
            //     — the retry coordinator escalates to LastResort when
            //     Strict can't make progress, and at that point the
            //     wrapper-emit + force-overflow path MUST run so
            //     pagination commits at least the oversized first row.
            //     Under LastResort F1 skips its defer + falls through.
            //   * Progress guard (post-PR-#99 review P1#1):
            //     <c>emittedThisAttempt &gt; 0</c> — defer only when
            //     prior content has been emitted on this attempt.
            //     When this grid is the FIRST emittable child on the
            //     page, deferring is unproductive (= the next page
            //     starts with the same grid as the first emittable
            //     child + the same constraints) and could create an
            //     unbounded zero-progress page loop because
            //     <see cref="LayoutRetryCoordinator"/> returns
            //     <c>PageComplete</c> immediately without escalating
            //     to <c>LastResort</c>. The
            //     <c>pageRemaining &lt; fullPage</c> proxy used pre-
            //     P1#1 was unsound: a first-on-page grid with
            //     <c>margin-top &gt; 0</c> has
            //     <c>topShift = marginStart</c>, making
            //     pageRemaining (which subtracts topShift) less than
            //     fullPage (which didn't), even on a fresh page.
            //   * Row-fit predicate:
            //     <c>firstRowExtent &gt; pageRemainingForGridContent</c>
            //     (= the first remaining row exceeds what's left on
            //     this page) AND
            //     <c>firstRowExtent &lt;= nextPageRemainingForGridContent</c>
            //     (= the row would fit on the next page where this
            //     grid will be the first emittable child + its
            //     <c>margin-top</c> would still apply as
            //     <c>topShift</c>). Together these mean "defer to
            //     next page will let it render"; when the row exceeds
            //     even the next page, deferral can't help → fall
            //     through to atomic / forced-overflow and let the
            //     painter clip per the existing contract. The
            //     next-page calculation accounts for
            //     <c>marginStart</c> per
            //     <see href="https://www.w3.org/TR/css-break-3/#unforced-breaks">CSS
            //     Fragmentation L3 §6.1</see> (page boundary is a
            //     hard barrier; <c>margin-top</c> reapplies on the
            //     next page when the grid is first emittable there).
            // </para>
            //
            // <para>Per PR-#99 review P1#2, the probe takes the
            // grid's content-box geometry (= what
            // <see cref="DispatchGridInner"/> will compute below) +
            // the incoming <c>GridResumeCache</c> when present, so
            // the probe sees the same row sizes
            // <see cref="GridLayouter"/> will emit. On cached
            // resume, the probe reads
            // <c>cache.RowBaseSizes[rowIndex]</c> directly — matching
            // GridLayouter's cache-reuse identity + inline-size
            // checks. On non-cached fresh resolves, the probe uses
            // the dispatch's actual content-inline-size /
            // content-block-size so fr / minmax / intrinsic tracks
            // resolve to the same sizes as the dispatch will. Cycle
            // 5c.2b will wire a shared <c>GridSizing.Result</c> /
            // <c>GridFragmentPlan</c> across pre-measure + F1 +
            // dispatch (= PR-#99 review P2#2) so the §11 work runs
            // once per attempt, not three times.</para>
            //
            // <para>The deferral text
            // (<c>grid-wrapper-rollback-for-pre-dispatch-deferral</c>)
            // allows EITHER pre-dispatch row-fit query (this design)
            // OR a wrapper rollback/backfill API. Pre-dispatch query
            // is chosen for cleaner semantics — no speculative
            // emission + no sink-rollback contract complexity + the
            // probe reuses the existing <c>GridSizing.Resolve</c>
            // pattern from <see cref="PreMeasureGridRowExtent"/>.</para>
            // Per Phase 3 Task 17 cycle 5c.2b post-PR-#100 review P1#1
            // — F1 also gated by <c>!_disableGridPagination</c>. In
            // a nested-context BlockLayouter (table cell / grid
            // item / table caption) the F1 defer would route
            // PageComplete(BlockContinuation) up to a parent that
            // discards the result, silently dropping the grid.
            //
            // <para>Per Phase 3 Task 17 cycle 5c.2c post-PR-#101
            // review P1#3 + cycle 5c.3 — F1 ALSO gated by
            // <c>paginateGridForOuterChild</c>. The outer-site
            // clamp (above, in the pre-grow section) decides
            // whether the dispatch will actually paginate the
            // grid: when the wrapper would otherwise overflow
            // page-remaining AND the chrome fits. Cycle 5c.3
            // removed the auto-height-only gate; explicit-height
            // grids now also participate (their authored row
            // geometry is preserved via the dual-input dispatch).
            // F1 must mirror the clamp's decision — if the clamp
            // didn't fire, dispatch passes
            // <c>allowPagination: false</c>, and a fabricated
            // GridContinuation here would route a continuation
            // that the resume page's F2 (= keyed off
            // <c>incomingGridContinuation != null</c>) would
            // mistakenly resize the wrapper against. The gate
            // ensures F1's pre-empt is structurally equivalent to
            // the dispatch's pagination decision.</para>
            if (IsPaginatableGrid(child)
                && !_disableGridPagination
                && paginateGridForOuterChild
                && strategy != LayoutAttemptStrategy.LastResort
                && emittedThisAttempt > 0)
            {
                int probeStartRow = 0;
                GridContinuation? incomingGridForProbe = null;
                if (incomingBlock is
                    {
                        ResumeAtChild: var probeResumeAt,
                        LayouterState: GridContinuation probeGridCont,
                    }
                    && probeResumeAt == childIdx)
                {
                    probeStartRow = probeGridCont.RowIndex;
                    incomingGridForProbe = probeGridCont;
                }

                // Per PR-#99 review P1#2 + cycle 5c.3 — derive the
                // content-box geometry the actual dispatch below will
                // use as its ROW-SIZING input, so the probe sees the
                // same sizing inputs. For explicit-height grids that
                // triggered the cycle-5c.3 clamp, this is the AUTHORED
                // (= pre-clamp) borderBoxBlockSize; for auto-height
                // grids the authored == clamped. Matches the
                // <see cref="GridGeometryHelper.ComputeContentGeometry"/>
                // call on the IsGridContainer dispatch branch.
                var probeGridGeom = GridGeometryHelper.ComputeContentGeometry(
                    gridBox: child,
                    borderBoxInlineSize: borderBoxInlineSize,
                    borderBoxBlockSize: authoredBorderBoxBlockSize,
                    borderBoxInlineOffset: inFlowInlineOffset,
                    borderBoxBlockOffset: blockOffset);

                var gridBorderPaddingBlock =
                    borderStart + paddingStart + paddingEnd + borderEnd;
                var pageRemainingForGridContent =
                    fragmentainer.BlockSize
                    - fragmentainer.UsedBlockSize
                    - topShift
                    - gridBorderPaddingBlock;
                // Per PR-#99 review P1#1 — the NEXT page's content
                // remaining (= the page F1's defer would route the
                // grid onto). On that page the grid will be the
                // first emittable child + its <c>margin-top</c>
                // applies as <c>topShift</c>; subtract
                // <c>marginStart</c> so the productivity check
                // mirrors the actual next-page geometry. Without
                // this subtraction, F1 could defer a row that fits
                // <c>BlockSize - chrome</c> but NOT
                // <c>BlockSize - chrome - marginStart</c>, leading
                // to a defer that the next page rejects again.
                var nextPageRemainingForGridContent =
                    fragmentainer.BlockSize
                    - marginStart
                    - gridBorderPaddingBlock;

                if (pageRemainingForGridContent > 0)
                {
                    var firstRowExtent = PreMeasureGridRowExtentAt(
                        gridBox: child,
                        rowIndex: probeStartRow,
                        contentInlineSize: probeGridGeom.ContentInlineSize,
                        contentBlockSize: probeGridGeom.ContentBlockSize,
                        incomingCache: incomingGridForProbe?.Cache,
                        cancellationToken: cancellationToken);
                    if (firstRowExtent
                            > pageRemainingForGridContent + GridSizing.SizeEpsilonPublic
                        && firstRowExtent
                            <= nextPageRemainingForGridContent + GridSizing.SizeEpsilonPublic)
                    {
                        // The first remaining row would not fit this
                        // page's remaining content area but would fit
                        // the next page → defer without emitting the
                        // wrapper. The outgoing BlockContinuation
                        // carries a GridContinuation pointing at the
                        // probed startRow + preserves any incoming
                        // cache (= identity stays bound to this grid
                        // across the deferral so the resume page's
                        // GridLayouter sees the same sizing + sparse-
                        // placement decisions).
                        return LayoutAttemptResult.PageComplete(
                            new BlockContinuation(
                                ResumeAtChild: childIdx,
                                ConsumedBlockSize: priorPagesConsumed
                                    + (fragmentainer.UsedBlockSize - _pageStartUsedBlockSize),
                                LayouterState: new GridContinuation(
                                    RowIndex: probeStartRow,
                                    Cache: incomingGridForProbe?.Cache,
                                    EmittedBlockExtent: 0)),
                            cost: 0);
                    }
                }
            }

            // Per Phase 3 Task 17 cycle 5c.2b F2 — capture the sink
            // cursor BEFORE emitting the wrapper so the post-dispatch
            // wrapper-resize consumer (below, at the IsGridContainer
            // dispatch branch) can mutate the wrapper's BlockSize in
            // place via <see cref="IBlockFragmentSink.UpdateFragmentBlockSize"/>
            // after the inner GridLayouter reports its
            // <c>LastEmittedBlockExtent</c>. Z-order is preserved (=
            // the wrapper stays at this cursor index, ahead of its
            // children).
            var wrapperCursor = _sink.Cursor;
            // CSS 2.1 §10.6.3 — an auto-height block-flow container's painted
            // border box spans its in-flow children; the chrome-only
            // borderBoxBlockSize stays the pagination/cursor input (untouched
            // above + below), this only grows the painter's rectangle, capped
            // to the page (`auto-height-emit-vs-pagination`).
            var emittedBlockSize = ResolveAutoHeightEmittedBlockSize(
                child, borderBoxBlockSize, effectiveBlockSize, blockOffset, fragmentainer);
            _sink.Emit(new BoxFragment(
                Box: child,
                InlineOffset: inFlowInlineOffset,
                BlockOffset: blockOffset,
                InlineSize: borderBoxInlineSize,
                BlockSize: emittedBlockSize));
            // Per Phase 3 Task 19 cycle 2a — record positioned-CB
            // establishers for abspos descendant anchoring. Anchors to the
            // chrome border box (abspos containing-block geometry is a separate
            // concern from the painted auto-height span).
            RecordPositionedBoxGeometry(
                child, inFlowInlineOffset, blockOffset,
                borderBoxInlineSize, borderBoxBlockSize);

            // Per Phase 3 Task 14 cycle 1 — multicol container
            // dispatch. A block container with `column-count: N`
            // (N >= 2) splits its in-flow content equally across N
            // parallel columns via MulticolLayouter. The wrapper
            // fragment is already emitted above (with the regular
            // block sizing); MulticolLayouter emits the per-column
            // content INSIDE the wrapper's content area. Skips the
            // EmitBlockSubtreeRecursive call below (the multicol
            // layouter owns the inner emission). column-count: 1 is
            // NOT a multicol container — the regular block flow path
            // applies.
            if (IsMulticolContainer(child))
            {
                // Per Phase 3 Task 14 cycle 2 hardening (Finding #5) —
                // multicol content-box geometry is shared with the
                // outer / recursion premeasure + recursion emit sites
                // via MulticolGeometryHelper. Per CSS Multi-column L1
                // §3 the column content area lives inside the
                // container's content box (= border box minus border +
                // padding edges). For Finding 2's auto-height path the
                // per-column block-size derives from the
                // fragmentainer's REMAINING block-space at the
                // wrapper's actual border-box top (= blockOffset);
                // mirrors the pre-measure path above so emit + measure
                // agree.
                var multicolEmitGeom = MulticolGeometryHelper.ComputeContentGeometry(
                    multicolBox: child,
                    borderBoxInlineSize: borderBoxInlineSize,
                    borderBoxBlockSize: borderBoxBlockSize,
                    borderBoxInlineOffset: inFlowInlineOffset,
                    borderBoxBlockOffset: blockOffset,
                    fragmentainer: fragmentainer,
                    isHeightAuto: IsHeightAuto(child));
                var multicolContentInlineSize = multicolEmitGeom.ContentInlineSize;
                var multicolContentBlockSize = multicolEmitGeom.ContentBlockSize;
                var multicolContentInlineOffset = multicolEmitGeom.ContentInlineOffset;
                var multicolContentBlockOffset = multicolEmitGeom.ContentBlockOffset;

                // Per Phase 3 Task 14 cycle 2 — multi-page multicol
                // resume. When the incoming BlockContinuation carries
                // a MulticolContinuation in LayouterState AND we're
                // at the multicol child it deferred at, pass the
                // MulticolContinuation through to the new
                // MulticolLayouter so it resumes at the correct
                // child + nested layouter state. The carried
                // continuation is one-shot: subsequent multicol
                // children in the same attempt start fresh.
                MulticolContinuation? multicolContinuationForChild = null;
                if (incomingBlock?.LayouterState is MulticolContinuation incomingMulticolCont
                    && childIdx == incomingBlock.ResumeAtChild
                    && !_consumedIncomingMulticolContinuation)
                {
                    multicolContinuationForChild = incomingMulticolCont;
                    _consumedIncomingMulticolContinuation = true;
                }

                using var multicolLayouter = new MulticolLayouter(
                    rootBox: child,
                    sink: _sink,
                    incomingContinuation: multicolContinuationForChild,
                    diagnostics: _diagnostics,
                    shaperResolver: _shaperResolver);
                multicolLayouter.ConfigureEmission(
                    contentInlineOffset: multicolContentInlineOffset,
                    contentBlockOffset: multicolContentBlockOffset,
                    contentInlineSize: multicolContentInlineSize,
                    contentBlockSize: multicolContentBlockSize);
                // Use a fresh column-scoped resolver — the outer
                // resolver's checkpoint state is isolated from the
                // multicol's per-column pagination. Mirrors
                // TableLayouter's per-cell resolver isolation.
                using var multicolResolver = new BreakResolver();
                var multicolResult = multicolLayouter.AttemptLayout(
                    fragmentainer,
                    ref layout,
                    multicolResolver,
                    LayoutAttemptStrategy.LastResort,
                    cancellationToken);

                // Cursor advance + margin-collapse bookkeeping mirror
                // the regular block path below. Skip the
                // EmitBlockSubtreeRecursive + EmitTableInner calls.
                fragmentainer.UsedBlockSize = Math.Max(0,
                    fragmentainer.UsedBlockSize + marginBoxBlockSizeForCursor);
                prevBlockMarginEnd = marginEnd;
                hasPriorAdjoiningBlock = true;
                emittedThisAttempt++;
                lastEmittedIdx = childIdx;

                // Per Phase 3 Task 14 cycle 2 — propagate
                // PageComplete(MulticolContinuation) up. The wrapper's
                // outer fragment has already been emitted; the resume
                // page's BlockLayouter sees a BlockContinuation whose
                // LayouterState is the MulticolContinuation +
                // dispatches it back to MulticolLayouter via the
                // resume contract above. Mirrors Task 13 cycle 1's
                // PageComplete(BlockContinuation(LayouterState=TableContinuation))
                // pattern.
                if (multicolResult.Outcome == LayoutAttemptOutcome.PageComplete
                    && multicolResult.Continuation is MulticolContinuation mcCont)
                {
                    return LayoutAttemptResult.PageComplete(
                        new BlockContinuation(
                            ResumeAtChild: childIdx,
                            ConsumedBlockSize: priorPagesConsumed
                                + (fragmentainer.UsedBlockSize - _pageStartUsedBlockSize),
                            LayouterState: mcCont),
                        cost: multicolResult.Cost);
                }

                // Skip the rest of the regular Continue path for
                // this child; the multicol's inner content was
                // committed in full on this page (AllDone) — or
                // truncated via the forced-overflow fallback.
                continue;
            }

            // Per Phase 3 Task 15 cycle 1 (Hello World) — flex
            // container dispatch. A box with BoxKind.FlexContainer /
            // InlineFlexContainer (= display: flex / inline-flex)
            // lays out its direct children as flex items via
            // FlexLayouter. The wrapper fragment is already emitted
            // above (with the regular block sizing); FlexLayouter
            // emits the per-item content INSIDE the wrapper's
            // content area. Skips the EmitBlockSubtreeRecursive call
            // below (FlexLayouter owns the inner emission).
            //
            // Geometry: the flex container's content-box is derived
            // from the wrapper's border-box minus the wrapper's own
            // borders + paddings. Cycle 4b's paginatable-flex extent
            // clamp inside the pre-grow above may have already
            // CLAMPED <c>borderBoxBlockSize</c> down to the
            // page-remaining-block; in that case the dispatched
            // content-block-size is the page-remaining-block minus
            // chrome (= the fragment budget) + <c>allowPagination</c>
            // flips ON via <c>paginateFlexForOuterChild</c>.
            //
            // Task 16 cycle 4b activation status: this dispatch site
            // ACTIVELY propagates
            // PageComplete(BlockContinuation(LayouterState=FlexContinuation))
            // through to AttemptLayout when FlexLayouter returns a
            // FlexContinuation. The "scaffolding" wording from
            // cycle 2/3 docs no longer applies — production
            // multi-page flex pagination round-trips end-to-end
            // through this path (see
            // <c>Task16_cycle2_production_html_flex_container_splits_across_two_pages</c>
            // + the cycle-4b resume tests).
            if (IsFlexContainer(child))
            {
                // Derive the flex container's content-box geometry
                // from the wrapper's border-box (already painted via
                // the BoxFragment emission above). The math mirrors
                // MulticolGeometryHelper.ComputeContentGeometry's
                // explicit-height branch (no fragmentainer-remaining
                // derivation for cycle 1).
                // Per Phase 3 Task 16 cycle 4d (PR-#82 review #2) —
                // delegate content-box geometry derivation to the
                // shared <see cref="FlexGeometryHelper"/>. The math
                // is identical at all 3 dispatch sites (outer here,
                // recursive at line ~3460, forced-overflow re-route
                // at line ~1991); consolidation removes the drift
                // risk + ~25 LOC per site.
                var flexGeom = FlexGeometryHelper.ComputeContentGeometry(
                    flexBox: child,
                    borderBoxInlineSize: borderBoxInlineSize,
                    borderBoxBlockSize: borderBoxBlockSize,
                    borderBoxInlineOffset: inFlowInlineOffset,
                    borderBoxBlockOffset: blockOffset);
                var flexContentInlineSize = flexGeom.ContentInlineSize;
                var flexContentBlockSize = flexGeom.ContentBlockSize;
                var flexContentInlineOffset = flexGeom.ContentInlineOffset;
                var flexContentBlockOffset = flexGeom.ContentBlockOffset;
                // Dual-input (P1 fix — mirrors the recursive site): the wrapper
                // geometry above used the CLAMPED borderBoxBlockSize (so the
                // wrapper paints clamped). For a paginating COLUMN flex (item
                // split) OR ROW-nowrap flex (intra-item content split), recompute
                // the AUTHORED content-block-size as the flex/line sizing input +
                // use the clamped content-block-size as the page budget, so
                // content is sized naturally + the cut honors the page. Without
                // this a root/body-level dual-input flex would shrink to the
                // budget + return AllDone instead of splitting. Row-wrap → null.
                double? outerFlexPageBudget = null;
                if (outerFlexDualInputPaginating)
                {
                    var authoredFlexGeom = FlexGeometryHelper.ComputeContentGeometry(
                        flexBox: child,
                        borderBoxInlineSize: borderBoxInlineSize,
                        borderBoxBlockSize: outerFlexAuthoredBorderBoxBlockSize,
                        borderBoxInlineOffset: inFlowInlineOffset,
                        borderBoxBlockOffset: blockOffset);
                    outerFlexPageBudget = flexContentBlockSize;          // clamped = cut-off
                    flexContentBlockSize = authoredFlexGeom.ContentBlockSize; // natural = sizing
                }

                // Per Phase 3 Task 16 cycle 2 — multi-page flex
                // resume. Mirrors the multicol dispatch above: when
                // the incoming BlockContinuation carries a
                // FlexContinuation in LayouterState AND we're at the
                // flex child it deferred at, pass the
                // FlexContinuation through to the new FlexLayouter
                // so it resumes at the correct line. The carried
                // continuation is one-shot: subsequent flex children
                // in the same attempt start fresh.
                FlexContinuation? flexContinuationForChild = null;
                if (incomingBlock?.LayouterState is FlexContinuation incomingFlexCont
                    && childIdx == incomingBlock.ResumeAtChild
                    && !_consumedIncomingFlexContinuation)
                {
                    flexContinuationForChild = incomingFlexCont;
                    _consumedIncomingFlexContinuation = true;
                }

                // Per Phase 3 Task 16 cycle 4a (PR #82, following
                // the PR #81 execution order) — both dispatch sites
                // now route through the shared `DispatchFlexInner`
                // helper to eliminate the 135 + 107 LOC of
                // duplicated FlexLayouter construction code. The
                // `allowPagination: false` gate stays in place at
                // the call sites until cycle 4b adds the
                // pre-break-check routing — see
                // `docs/deferrals.md` flex-layouter-features for
                // the documented execution order.
                var flexResult = DispatchFlexInner(
                    flexBox: child,
                    contentInlineOffset: flexContentInlineOffset,
                    contentBlockOffset: flexContentBlockOffset,
                    contentInlineSize: flexContentInlineSize,
                    contentBlockSize: flexContentBlockSize,
                    incomingContinuation: flexContinuationForChild,
                    // Per Phase 3 Task 16 cycle 4b — gate flipped ON by
                    // the paginatable-flex extent clamp earlier in
                    // this iteration (see <c>paginateFlexForOuterChild</c>
                    // initialization just before the pre-grow block).
                    // When true, FlexLayouter packs lines up to the
                    // clamped contentBlockSize budget + emits a
                    // FlexContinuation for the rest; when false (=
                    // wrapper fits in remaining space OR container
                    // isn't paginatable), FlexLayouter emits
                    // atomically as in L1-L17. The dormant
                    // PageComplete propagation below activates when
                    // this flag is true.
                    allowPagination: paginateFlexForOuterChild,
                    fragmentainer: fragmentainer,
                    layout: ref layout,
                    cancellationToken: cancellationToken,
                    lastEmittedBlockExtent: out var outerFlexLastEmittedExtent,
                    pageBlockBudget: outerFlexPageBudget);

                // PR-#180 review P2 — resize a paginating dual-input flex wrapper
                // to the ACTUAL emitted extent instead of the clamped page budget
                // (mirrors the grid F2 resize). Without this the wrapper paints
                // blank trailing space + the cursor over-advances when the
                // committed content occupies less than the budget (e.g. the final
                // resume page). Gated to dual-input pagination (column item split
                // OR row-nowrap content split); row-WRAP wrapper resize stays the
                // cycle-4f deferral. Uses `wrapperCursor` (the sink index of this
                // child's wrapper fragment, captured before the wrapper emit) +
                // UpdateFragmentBlockSize, preserving z-order.
                var cursorAdvanceForFlex = marginBoxBlockSizeForCursor;
                if (outerFlexDualInputPaginating)
                {
                    var flexChromeBlock = borderStart + paddingStart + paddingEnd + borderEnd;
                    var resizedFlexWrapperBlockSize = flexChromeBlock + outerFlexLastEmittedExtent;
                    _sink.UpdateFragmentBlockSize(wrapperCursor, resizedFlexWrapperBlockSize);
                    cursorAdvanceForFlex = topShift + resizedFlexWrapperBlockSize + marginEnd;
                }
                else if (IsFlexContainer(child) && IsHeightAuto(child)
                    && !child.Style.ReadFlexDirection().IsFlexColumnDirection()
                    && !child.Style.ReadFlexWrap().IsFlexWrapping())
                {
                    // Corpus-fidelity (03 itinerary footer overlap), mirror of the recursive site — an
                    // AUTO-height ROW flex's used cross size is its content cross extent (FlexLinePacker
                    // sizes lines from the items' DECLARED cross only, 0 for auto). `outerFlexLastEmittedExtent`
                    // carries the real content cross; resize the wrapper + advance the cursor by it so a
                    // trailing sibling doesn't overlap the flex content. Byte-identical when content fits.
                    var flexChromeBlock = borderStart + paddingStart + paddingEnd + borderEnd;
                    var resizedFlexWrapperBlockSize = flexChromeBlock + outerFlexLastEmittedExtent;
                    if (resizedFlexWrapperBlockSize > marginBoxBlockSizeForCursor - topShift - marginEnd)
                    {
                        _sink.UpdateFragmentBlockSize(wrapperCursor, resizedFlexWrapperBlockSize);
                        cursorAdvanceForFlex = topShift + resizedFlexWrapperBlockSize + marginEnd;
                    }
                }

                // Cursor advance + bookkeeping mirror the multicol
                // dispatch above.
                fragmentainer.UsedBlockSize = Math.Max(0,
                    fragmentainer.UsedBlockSize + cursorAdvanceForFlex);
                prevBlockMarginEnd = marginEnd;
                hasPriorAdjoiningBlock = true;
                emittedThisAttempt++;
                lastEmittedIdx = childIdx;

                // Per Phase 3 Task 16 cycle 2 — propagate
                // PageComplete(FlexContinuation) up. Mirrors the
                // multicol pattern at line ~2122: the wrapper's
                // outer fragment is already emitted; the resume
                // page's BlockLayouter sees a BlockContinuation
                // whose LayouterState is the FlexContinuation +
                // dispatches it back to FlexLayouter via the resume
                // contract above.
                if (flexResult.Outcome == LayoutAttemptOutcome.PageComplete
                    && flexResult.Continuation is FlexContinuation flexCont)
                {
                    return LayoutAttemptResult.PageComplete(
                        new BlockContinuation(
                            ResumeAtChild: childIdx,
                            ConsumedBlockSize: priorPagesConsumed
                                + (fragmentainer.UsedBlockSize - _pageStartUsedBlockSize),
                            LayouterState: flexCont),
                        cost: flexResult.Cost);
                }

                // AllDone case: skip the rest of the Continue path
                // for this child; the flex container's inner
                // content was committed in full on this page.
                continue;
            }

            // Per Phase 3 Task 17 cycle 1 (Hello World) + cycle 5b
            // (paginatable-grid wiring) — grid container dispatch. A
            // box with BoxKind.GridContainer / InlineGridContainer
            // (= display: grid / inline-grid) lays out its direct
            // children as grid items via GridLayouter. The wrapper
            // fragment is already emitted above; GridLayouter emits
            // per-item content INSIDE the wrapper's content area.
            //
            // Cycle 5b: when paginateGridForOuterChild flipped ON,
            // dispatch with allowPagination=true + propagate
            // PageComplete(GridContinuation) up as
            // BlockContinuation(LayouterState=GridContinuation).
            //
            // Resume contract: when this BlockLayouter is invoked with
            // an incoming BlockContinuation matching `childIdx` whose
            // LayouterState is a GridContinuation, route it back to
            // GridLayouter via DispatchGridInner's incomingContinuation
            // parameter. (Cycle 5b initial ship — the recursive site
            // gets the same wiring in cycle 5c.)
            if (IsGridContainer(child))
            {
                // Per Phase 3 Task 17 cycle 5c.3 — when the paginatable-
                // grid clamp fired, compute BOTH the clamped geometry
                // (= page budget) AND the authored geometry (= row
                // sizing input). The clamped geometry's
                // ContentBlockSize feeds <c>DispatchGridInner</c>'s
                // <c>pageBlockBudget</c>; the authored geometry's
                // ContentBlockSize feeds the standard
                // <c>contentBlockSize</c> parameter that
                // <see cref="GridSizing.Resolve"/> uses for fr /
                // definite-height row distribution. When the clamp
                // didn't fire (= auto-height grid that fits, OR
                // pagination disabled at this site), authored == clamped
                // and the dual-input mechanism degenerates to legacy
                // single-input behavior with <c>pageBlockBudget</c>
                // passed as null.
                var gridGeom = GridGeometryHelper.ComputeContentGeometry(
                    gridBox: child,
                    borderBoxInlineSize: borderBoxInlineSize,
                    borderBoxBlockSize: borderBoxBlockSize,
                    borderBoxInlineOffset: inFlowInlineOffset,
                    borderBoxBlockOffset: blockOffset);
                double? gridPageBlockBudget = null;
                double gridSizingContentBlockSize = gridGeom.ContentBlockSize;
                if (paginateGridForOuterChild
                    && authoredBorderBoxBlockSize > borderBoxBlockSize)
                {
                    var authoredGridGeom = GridGeometryHelper.ComputeContentGeometry(
                        gridBox: child,
                        borderBoxInlineSize: borderBoxInlineSize,
                        borderBoxBlockSize: authoredBorderBoxBlockSize,
                        borderBoxInlineOffset: inFlowInlineOffset,
                        borderBoxBlockOffset: blockOffset);
                    gridSizingContentBlockSize = authoredGridGeom.ContentBlockSize;
                    gridPageBlockBudget = gridGeom.ContentBlockSize;
                }

                // Resume contract: extract the incoming GridContinuation
                // if the BlockLayouter was invoked with a chained
                // BlockContinuation pointing at this child.
                GridContinuation? incomingGridContinuation = null;
                if (_incomingContinuation is BlockContinuation
                    {
                        ResumeAtChild: var resumeAt,
                        LayouterState: GridContinuation gridIncoming
                    }
                    && resumeAt == childIdx)
                {
                    incomingGridContinuation = gridIncoming;
                }

                var gridResult = DispatchGridInner(
                    gridBox: child,
                    contentInlineOffset: gridGeom.ContentInlineOffset,
                    contentBlockOffset: gridGeom.ContentBlockOffset,
                    contentInlineSize: gridGeom.ContentInlineSize,
                    contentBlockSize: gridSizingContentBlockSize,
                    fragmentainer: fragmentainer,
                    layout: ref layout,
                    cancellationToken: cancellationToken,
                    lastEmittedBlockExtent: out var gridLastEmittedBlockExtent,
                    allowPagination: paginateGridForOuterChild,
                    incomingContinuation: incomingGridContinuation,
                    pageBlockBudget: gridPageBlockBudget);

                // Per Phase 3 Task 17 cycle 5c.2b F2 — wrapper-resize
                // + cursor-advance consumer for grids in a multi-page
                // state. <c>gridLastEmittedBlockExtent</c> (= the
                // <c>GridLayouter.LastEmittedBlockExtent</c> property
                // from cycle 5c.1, populated on EVERY outcome) is the
                // TRUE occupied content-box block extent of the
                // emitted rows.
                //
                // <para>F2 fires when EITHER:
                //   * <c>paginateGridForOuterChild</c> is on (= the
                //     outer-site clamp fired this page; the wrapper
                //     was emitted at the clamped budget but
                //     GridLayouter may have only emitted K of N
                //     rows). Pre-F2 the wrapper paints empty
                //     trailing space + the cursor over-advances,
                //     displacing following siblings.
                //   * <c>incomingGridContinuation</c> is non-null
                //     (= resuming a previously-deferred grid;
                //     GridLayouter emits only the remaining rows
                //     even when the gate doesn't fire on this
                //     page). Without this branch, the AllDone-on-
                //     resume case from cycle 5c.1 PR-#98 review F1
                //     would leave the wrapper at the FULL grid's
                //     natural extent on the final page → empty
                //     trailing space below the last emitted row.
                // </para>
                //
                // <para>Single-page grids (no clamp, no resume) hit
                // neither condition + take the natural-extent
                // <c>marginBoxBlockSizeForCursor</c> path (= byte-
                // identical to pre-cycle-5c.2b behavior for fixtures
                // that don't paginate grids).</para>
                //
                // <para>Chrome derivation: the wrapper's vertical
                // chrome is the sum of borders + paddings on the
                // block axis (= <c>borderStart + paddingStart +
                // paddingEnd + borderEnd</c>). The wrapper's
                // BlockSize equals <c>chrome + emittedExtent</c>;
                // the cursor advance equals <c>marginStart +
                // chrome + emittedExtent + marginEnd</c> (= margin-
                // box). Both derivations use the same
                // <c>gridLastEmittedBlockExtent</c> from
                // GridLayouter so wrapper geometry +
                // <c>UsedBlockSize</c> +
                // <c>BlockContinuation.ConsumedBlockSize</c> all
                // agree.</para>
                double cursorAdvanceForGrid = marginBoxBlockSizeForCursor;
                var f2WrapperResizeFires =
                    paginateGridForOuterChild
                    || incomingGridContinuation is not null;
                if (f2WrapperResizeFires)
                {
                    var gridChromeBlock =
                        borderStart + paddingStart + paddingEnd + borderEnd;
                    var resizedWrapperBlockSize =
                        gridChromeBlock + gridLastEmittedBlockExtent;
                    _sink.UpdateFragmentBlockSize(
                        wrapperCursor, resizedWrapperBlockSize);
                    // Recompute the cursor advance to use the
                    // emitted-content extent instead of the natural
                    // / clamped budget.
                    //
                    // <para>Per Phase 3 Task 17 cycle 5c.2b
                    // post-PR-#100 review P1#2 — use <c>topShift</c>
                    // rather than <c>marginStart</c>. The block-flow
                    // cursor accounting (per CSS 2.1 §8.3.1 margin
                    // collapsing) advances by
                    // <c>topShift + borderBox + marginEnd</c>:
                    // <c>topShift</c> already encodes the ADDITIONAL
                    // distance after sibling-margin collapse (=
                    // <c>marginStart</c> when first on page; the
                    // collapsed delta otherwise). Substituting
                    // <c>marginStart</c> double-counts the top
                    // margin whenever a preceding sibling's bottom
                    // margin absorbed part of the collapse — e.g.,
                    // sibling marginBottom=80, grid marginTop=10:
                    // collapsed gap = 80, so topShift contribution
                    // = 0 (the 80 is already in UsedBlockSize from
                    // the prior block). Pre-fix charged 10 again;
                    // post-fix correctly charges 0.</para>
                    cursorAdvanceForGrid =
                        topShift + resizedWrapperBlockSize + marginEnd;
                }

                // Cursor advance + bookkeeping mirror the flex/multicol
                // path.
                fragmentainer.UsedBlockSize = Math.Max(0,
                    fragmentainer.UsedBlockSize + cursorAdvanceForGrid);
                prevBlockMarginEnd = marginEnd;
                hasPriorAdjoiningBlock = true;
                emittedThisAttempt++;
                lastEmittedIdx = childIdx;

                // Per cycle 5b — propagate PageComplete(GridContinuation)
                // up as BlockContinuation(LayouterState=GridContinuation).
                // Mirrors the flex cycle-2 propagation pattern at
                // line ~2572.
                if (gridResult.Outcome == LayoutAttemptOutcome.PageComplete
                    && gridResult.Continuation is GridContinuation gridCont)
                {
                    return LayoutAttemptResult.PageComplete(
                        new BlockContinuation(
                            ResumeAtChild: childIdx,
                            ConsumedBlockSize: priorPagesConsumed
                                + (fragmentainer.UsedBlockSize - _pageStartUsedBlockSize),
                            LayouterState: gridCont),
                        cost: gridResult.Cost);
                }

                continue;
            }

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
            //
            // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) —
            // multi-level continuation propagation. The recursion now
            // returns a `LayoutContinuation?`: null on clean emission,
            // or a chained `BlockContinuation` whose `LayouterState`
            // traces down to a `TableContinuation` or
            // `MulticolContinuation` leaf representing the deep
            // multicol/table break. The outer loop wraps any non-null
            // return into the top-level `BlockContinuation` for
            // PageComplete.
            //
            // Routing protocol (down): when the incoming
            // BlockContinuation matches this child AND its
            // `LayouterState` is itself a BlockContinuation, pass
            // that inner chain as the recursion's `incomingContinuation`.
            // When the inner is a direct Table/Multicol continuation,
            // wrap it in a BlockContinuation(rc=0, ls=inner) so the
            // recursion's chain-unwrap branch finds the leaf at
            // depth==1's first matching child (the cycle-1 dispatch
            // shape).
            // Per post-PR-#57 review #2 Finding #1 — chain peel only.
            // With the no-flatten chain contract (see the stitching
            // comment below), the chain always has DOM-depth layers
            // and the leaf (Table/MulticolContinuation) is wrapped in a
            // BlockContinuation whose `ResumeAtChild` names its
            // container's idx in the matching parent. So a chain head
            // arriving at the recursive-walk site whose `LayouterState`
            // is itself a Table/MulticolContinuation leaf means the
            // leaf's container IS the current child — the cycle-1
            // direct-dispatch path fires earlier and consumes that
            // chain. By the time we reach this switch, the only valid
            // case is `BlockContinuation innerBlock` (a deeper level).
            //
            // The prior hardcoded-`rc=0` re-wrap branches for Table /
            // Multicol leaves at this site were workarounds for the
            // flatten that dropped intermediate indices; removed for
            // correctness (they discarded the leaf's actual parent
            // idx when the chain had been flattened).
            LayoutContinuation? recIncoming = null;
            if (incomingBlock?.LayouterState is LayoutContinuation lc
                && childIdx == incomingBlock.ResumeAtChild
                && !_consumedIncomingBlockContinuationRecursion)
            {
                recIncoming = lc switch
                {
                    BlockContinuation innerBlock => innerBlock,
                    _ => null,
                };
                if (recIncoming is not null)
                {
                    _consumedIncomingBlockContinuationRecursion = true;
                }
            }
            var recursiveReturn = EmitBlockSubtreeRecursive(
                child,
                parentBlockOffset: blockOffset,
                parentInlineOffset: inFlowInlineOffset,
                parentInlineSize: borderBoxInlineSize,
                cancellationToken: cancellationToken,
                depth: 1,
                propagatingResolver: resolver,
                propagatingFragmentainer: fragmentainer,
                incomingContinuation: recIncoming,
                parentContentBlockSize: contentBlock);   // % height base (percent-height cycle).

            // Stitching (up): on non-null return propagate as a
            // `PageComplete(BlockContinuation(rc=childIdx, ls=<chain>))`
            // to the outer LayoutRetryCoordinator.
            //
            // Per post-PR-#57 review #2 Finding #1 — preserve the full
            // chain (do NOT flatten `deepRet.LayouterState ?? deepRet`).
            // The flatten dropped the recursion-depth's wrap layer,
            // discarding its `ResumeAtChild` index. For DOM shapes where
            // an intermediate container's matching child is at a
            // non-zero index (e.g., `<body><spacer/><multicol/>` where
            // multicol is body's idx 1), the dropped index caused the
            // chain to misroute on resume — the inner BC's rc was
            // interpreted against the wrong level's child list, missing
            // its target. The chain now has DOM-depth layers; every
            // intermediate container's child index is preserved.
            if (recursiveReturn is BlockContinuation deepRet)
            {
                return LayoutAttemptResult.PageComplete(
                    new BlockContinuation(
                        ResumeAtChild: childIdx,
                        ConsumedBlockSize: priorPagesConsumed
                            + (fragmentainer.UsedBlockSize - _pageStartUsedBlockSize),
                        LayouterState: deepRet),
                    cost: 0);
            }

            // Per Phase 3 Task 12 sub-cycle 1 hardening (Finding 1) —
            // drain the pre-measured table content into the outer
            // sink. The wrapper's outer fragment is already emitted
            // above (with a border-box size that includes the
            // measured table content height); EmitTableInner only
            // appends the row + cell fragments + flushes the buffered
            // cell content in paint-safe order.
            // EmitBlockSubtreeRecursive's own predicate gate skips
            // Table inner content (the inner geometry belongs to
            // TableLayouter, not BlockLayouter).
            var tableInnerResultContinue = EmitTableInner(
                pendingTableLayouter,
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

            // Per Phase 3 Task 13 cycle 1 — if the table returned
            // PageComplete (partial row emission), short-circuit the
            // outer loop + propagate. The resume page constructs a
            // fresh TableLayouter with the carried TableContinuation
            // (stashed in BlockContinuation.LayouterState) and emits
            // the remaining rows. The wrapper's outer fragment has
            // already been emitted; the resume page does NOT re-emit
            // it (BlockLayouter's child-index resume points AT the
            // wrapper child, but the table-resume path inside the
            // resumed BlockLayouter skips the wrapper re-emit when
            // the LayouterState is a TableContinuation).
            //
            // Cycle 1's resume contract: ResumeAtChild = childIdx (the
            // wrapper Box) + LayouterState = TableContinuation. The
            // resume BlockLayouter sees both + dispatches the
            // continuation through PreMeasureTableIfNeeded's resume
            // path before iterating children further. Cycle 2+ may
            // generalize by promoting LayouterState to a typed
            // "nested-layouter-state" subtype for the same pattern
            // across flex / grid / multi-col layouters.
            var continueTableCont = tableInnerResultContinue.Continuation as TableContinuation;
            if (continueTableCont is not null)
            {
                return LayoutAttemptResult.PageComplete(
                    new BlockContinuation(
                        ResumeAtChild: childIdx,
                        ConsumedBlockSize: priorPagesConsumed
                            + (fragmentainer.UsedBlockSize - _pageStartUsedBlockSize),
                        LayouterState: continueTableCont),
                    cost: tableInnerResultContinue.Cost);
            }
        }

        // Per Phase 3 Task 19 cycle 1 + post-PR-#112 review C2 — the
        // abspos emission pass is NOT run here. It runs in the
        // AttemptLayout WRAPPER after this core returns, so it fires on
        // BOTH the AllDone path (reached here) AND the PageComplete
        // paths (the early returns scattered through the in-flow loop
        // for multi-page documents). Running it inline here would only
        // cover AllDone, dropping every abspos fragment for any
        // document whose in-flow content paginates.

        // All children laid out — no more pages needed.
        return LayoutAttemptResult.AllDone(cost: 0);
    }

    /// <summary>Per Phase 3 Task 19 cycle 1 — lay out + emit the
    /// <c>position: absolute</c> direct children of <see cref="_rootBox"/>
    /// per CSS Positioned Layout L3 §6. Runs AFTER the in-flow pass so
    /// abspos boxes paint over in-flow content (cycle 1 = source-order
    /// paint; z-index ordering is a later cycle).
    ///
    /// <para><b>Containing block (cycle 1):</b> the establishing
    /// <c>BlockLayouter</c>'s content area = the fragmentainer content
    /// box <c>(0, 0, contentInlineSize, blockSize)</c>. For the top-
    /// level layouter this coincides with the initial containing block
    /// AND with a positioned root's content box. The spec-correct
    /// nearest-positioned-ancestor PADDING box + the ancestor walk
    /// (so an abspos box anchors to its <c>position: relative</c>
    /// ancestor rather than the ICB) is cycle 2 — see
    /// <c>docs/deferrals.md#abspos-cycle-1-explicit-only</c>.</para>
    ///
    /// <para><b>Whole-subtree collection.</b> Abspos boxes appear at
    /// ANY depth (real HTML nests them inside <c>&lt;body&gt;</c> etc.),
    /// so this walks <see cref="_rootBox"/>'s descendant tree. The
    /// in-flow passes (top-level loop + EmitBlockSubtreeRecursive) skip
    /// abspos boxes, so for THIS formatting context emitting them here is
    /// the single emission site. The walk does NOT descend INTO an abspos
    /// box's own subtree — that box's descendants are laid out by the
    /// inner sub-BlockLayouter (their own nested abspos boxes anchor to
    /// the inner box once it's a positioned CB; cycle-1 they'd anchor
    /// to the inner box's content area via that recursion). Nor does it
    /// cross a DELEGATION boundary into a grid item or table cell/caption
    /// (post-PR-#114 review P2#4) — those subtrees are owned by a nested
    /// BlockLayouter that runs its own abspos pass, so descending here
    /// would double-emit. (Flex is NOT a boundary — FlexLayouter spawns
    /// no per-item layouter; see
    /// <see cref="EmitAbsolutelyPositionedDescendants"/>.)</para>
    ///
    /// <para>Each box's offsets/size are resolved by the pure
    /// <see cref="AbsoluteLayouter.ResolvePlacement"/>; unresolved
    /// (= cycle-1-deferred) boxes are DROPPED with a
    /// <c>LAYOUT-ABSOLUTE-FEATURE-UNSUPPORTED-001</c> diagnostic rather
    /// than mis-placed. Resolved boxes emit their border-box fragment +
    /// dispatch inner content through a translating sub-BlockLayouter
    /// (mirrors <c>GridLayouter.DispatchGridItemContents</c>).</para></summary>
    private void EmitAbsolutelyPositionedChildren(
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        CancellationToken cancellationToken)
    {
        // The initial containing block (cycle 1 fallback) = the
        // fragmentainer content area. Cycle 2a anchors each abspos box
        // to its NEAREST positioned ancestor's padding box when one
        // exists (see ResolveContainingBlock); the ICB is the fallback
        // when there's no positioned ancestor.
        var icb = new AbsoluteContainingBlock(
            InlineOrigin: 0,
            BlockOrigin: 0,
            InlineSize: fragmentainer.ContentInlineSize,
            BlockSize: fragmentainer.BlockSize);

        // Collect abspos descendants at any depth (in source order).
        // The diagnostics sink is captured once for the whole pass.
        var diagnostics = layout.Diagnostics ?? _diagnostics;
        EmitAbsolutelyPositionedDescendants(
            _rootBox, icb, diagnostics, ref layout, cancellationToken);
    }

    /// <summary>Per Phase 3 Task 19 cycle 2a (+ post-PR-#113 review
    /// P1#1 / P2#1) — resolve the containing block for
    /// <paramref name="absBox"/> per CSS Positioned Layout L3 §6: the
    /// nearest ancestor with <c>position</c> != <c>static</c> (walked
    /// via <see cref="Box.Parent"/>), using that ancestor's PADDING box
    /// (= its recorded border box inset by its border widths).
    ///
    /// <para><b>Return contract</b>: a non-null
    /// <see cref="AbsoluteContainingBlock"/> on success;
    /// <see langword="null"/> = DEFERRED (the caller drops the box with
    /// <c>LAYOUT-ABSOLUTE-FEATURE-UNSUPPORTED-001</c> rather than
    /// misplacing it). Cases:</para>
    /// <list type="bullet">
    ///   <item>No positioned ancestor → the <paramref name="icb"/>
    ///   (initial containing block) IS the correct CB per §6.</item>
    ///   <item>Positioned ancestor found + geometry recorded + (if
    ///   <c>relative</c>) no explicit inset offsets → its padding
    ///   box.</item>
    ///   <item>Per P1#1 — positioned ancestor found but geometry NOT
    ///   recorded (laid out on a later page that page-1 didn't reach,
    ///   OR via the table-cell / grid-item / forced-overflow paths that
    ///   don't record) → DEFER. Falling back to the ICB here would
    ///   silently MISPLACE the box at the wrong origin.</item>
    ///   <item>Per cycle 2b — a <c>position: relative</c> ancestor with
    ///   explicit inset offsets has its relative shift APPLIED to the CB
    ///   origin (CSS 2.1 §9.4.3): the abspos descendant anchors to the
    ///   ancestor's VISUALLY-SHIFTED padding box. (Cycle 2a deferred
    ///   this; cycle 2b applies it.)</item>
    /// </list></summary>
    private AbsoluteContainingBlock? ResolveContainingBlock(
        Box absBox, AbsoluteContainingBlock icb)
    {
        for (var ancestor = absBox.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (!ancestor.Style.EstablishesAbsoluteContainingBlock()) continue;

            // Found the nearest positioned ancestor. Use its padding
            // box IFF its geometry was recorded during in-flow emit.
            if (_positionedBoxGeometry is not null
                && _positionedBoxGeometry.TryGetValue(ancestor, out var borderBox))
            {
                var bl = ancestor.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth);
                var bt = ancestor.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
                var br = ancestor.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth);
                var bb = ancestor.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);

                // Cycle 2b — apply the ancestor's relative-position shift
                // (CSS 2.1 §9.4.3) to the CB origin. The recorded
                // geometry is the ancestor's UNSHIFTED flow position
                // (in-flow emit doesn't apply relative offsets yet); for
                // a `position: relative` ancestor we add the shift here
                // so abspos descendants anchor to the visually-shifted
                // box. inline shift = left (or -right); block shift =
                // top (or -bottom). Non-relative ancestors (absolute /
                // fixed / sticky) already have their final position
                // baked into the recorded geometry → zero shift.
                var (relInline, relBlock) =
                    ancestor.Style.ReadPosition() == PositionValue.Relative
                        ? ComputeRelativeShift(
                            ancestor.Style, borderBox.InlineSize, borderBox.BlockSize)
                        : (0.0, 0.0);

                return new AbsoluteContainingBlock(
                    InlineOrigin: borderBox.InlineOffset + bl + relInline,
                    BlockOrigin: borderBox.BlockOffset + bt + relBlock,
                    InlineSize: System.Math.Max(0, borderBox.InlineSize - bl - br),
                    BlockSize: System.Math.Max(0, borderBox.BlockSize - bt - bb));
            }
            // Per post-PR-#114 review P2#4 — the positioned ancestor is
            // THIS formatting context's own root box. The ICB this
            // layouter was handed already IS that root's content box: a
            // grid item / table cell / abspos box is laid out by a nested
            // BlockLayouter whose fragmentainer == the (positioned) root's
            // content area. So anchor to the ICB rather than dropping —
            // without this, an abspos descendant of a positioned grid
            // item / cell would be dropped (its CB ancestor is the nested
            // root, never recorded in this layouter's in-flow geometry
            // map). NB this resolves to the content box; the padding-box
            // inset is absorbed by the nested fragmentainer already.
            if (ReferenceEquals(ancestor, _rootBox)) return icb;

            // Per PR-#113 review P1#1 — positioned ancestor found but
            // geometry NOT recorded AND it isn't this layouter's root
            // (laid out via a path that doesn't record, e.g. a positioned
            // ancestor ABOVE this layouter's subtree). Defer (drop +
            // diagnose) instead of misplacing at the ICB.
            return null;
        }
        // No positioned ancestor → the ICB IS the containing block.
        return icb;
    }

    /// <summary>Per Phase 3 Task 19 cycle 2b (+ post-PR-#114 review
    /// P1#2) — compute the relative-positioning shift (CSS 2.1 §9.4.3)
    /// for a <c>position: relative</c> box. LTR / horizontal: inline
    /// shift = <c>left</c> if specified else <c>-right</c> else 0; block
    /// shift = <c>top</c> if specified else <c>-bottom</c> else 0.
    /// PERCENTAGES resolve per-axis against the containing-block
    /// dimensions: <c>left</c>/<c>right</c> against
    /// <paramref name="cbInlineSize"/> (width), <c>top</c>/<c>bottom</c>
    /// against <paramref name="cbBlockSize"/> (HEIGHT) — using the
    /// inline base for the block axis would shift along the wrong extent
    /// whenever the two differ (the P1#2 fix). The ancestor's own
    /// border-box extents approximate its containing block here.</summary>
    private static (double inline, double block) ComputeRelativeShift(
        ComputedStyle style, double cbInlineSize, double cbBlockSize)
    {
        var left = ReadInsetPxOrNull(style, PropertyId.Left, cbInlineSize);
        var right = ReadInsetPxOrNull(style, PropertyId.Right, cbInlineSize);
        var top = ReadInsetPxOrNull(style, PropertyId.Top, cbBlockSize);
        var bottom = ReadInsetPxOrNull(style, PropertyId.Bottom, cbBlockSize);
        var inline = left ?? (right is { } r ? -r : 0.0);
        var block = top ?? (bottom is { } b ? -b : 0.0);
        return (inline, block);

        static double? ReadInsetPxOrNull(ComputedStyle s, PropertyId id, double pctBase)
        {
            var slot = s.Get(id);
            return slot.Tag switch
            {
                ComputedSlotTag.LengthPx => slot.AsLengthPx(),
                ComputedSlotTag.Percentage => slot.AsPercentage() / 100.0 * pctBase,
                _ => (double?)null,
            };
        }
    }

    /// <summary>Per Phase 3 Task 19 cycle 1 (+ post-PR-#114 review
    /// P2#4) — recursively walk <paramref name="box"/>'s children,
    /// emitting each <c>position: absolute</c> descendant against
    /// <paramref name="containingBlock"/>. Does NOT recurse into an
    /// abspos box's own subtree (the inner sub-BlockLayouter owns
    /// that); DOES recurse through in-flow boxes to find abspos boxes
    /// nested at any depth — EXCEPT across a delegation boundary.
    ///
    /// <para><b>Delegation boundary (P2#4 — emit exactly once):</b>
    /// <see cref="GridLayouter"/> lays out each grid ITEM, and
    /// <see cref="TableLayouter"/> each table CELL + CAPTION, via a
    /// NESTED <c>BlockLayouter</c> (constructed with
    /// <c>incomingContinuation: null</c>), so each of those runs its OWN
    /// post-flow abspos pass over its subtree. Recursing into those
    /// subtrees here too would emit the same abspos descendant a SECOND
    /// time (at a different anchor). So this walk does NOT descend into a
    /// grid container's in-flow children (= items), nor into a
    /// <see cref="BoxKind.TableCell"/> / <see cref="BoxKind.TableCaption"/>.
    /// It STILL emits abspos DIRECT children of a grid container
    /// (out-of-flow → never items → no nested layouter sees them) and
    /// STILL descends through table-internal boxes that aren't
    /// cells/captions (rows / row-groups) so an abspos child of a row
    /// isn't silently dropped.</para>
    ///
    /// <para><b>Why FLEX is NOT a boundary:</b> unlike grid/table,
    /// <see cref="FlexLayouter"/> constructs NO per-item nested
    /// BlockLayouter — it emits each flex item's border box only. A flex
    /// item's subtree (including any abspos descendant) is walked +
    /// emitted by THIS (outer) pass, so flex items must be recursed into
    /// — skipping them would silently DROP their abspos descendants. Add
    /// flex here only once FlexLayouter dispatches item content through a
    /// nested BlockLayouter.</para></summary>
    private void EmitAbsolutelyPositionedDescendants(
        Box box,
        AbsoluteContainingBlock containingBlock,
        IPaginateDiagnosticsSink? diagnostics,
        ref LayoutContext layout,
        CancellationToken cancellationToken)
    {
        // A grid container delegates EVERY in-flow child to a nested item
        // BlockLayouter (which owns that item's abspos emission). Table
        // cells/captions (also nested-BlockLayouter-owned) are detected
        // per-child below. Flex is deliberately excluded (see summary).
        var delegatesItemsToNestedLayouter = IsGridContainer(box);
        foreach (var child in box.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child.Style.IsAbsolutelyPositioned())
            {
                // Phase 3 cycle-2d — only a genuine positioned box (generated by an element whose OWN
                // style is position:absolute) is emitted here. `position` does not inherit, so a child
                // ELEMENT of an abspos box is static — but a TextRun / anonymous box REUSES its
                // parent's whole ComputedStyle (CSS 2.1 §9.2.1.1 anonymous inheritance), so the text
                // node inside an abspos box reports IsAbsolutelyPositioned. Emitting it would re-paint
                // the box's background at the box origin. Skip those synthetic boxes (a real abspos box
                // is never a TextRun or an anonymous block).
                if (child.Kind is not (BoxKind.TextRun or BoxKind.AnonymousBlock))
                {
                    EmitOneAbsoluteBox(
                        child, containingBlock, diagnostics, ref layout, cancellationToken);
                }
                // Do NOT descend into the abspos box here — its own
                // descendants are laid out by the inner sub-BlockLayouter
                // dispatched inside EmitOneAbsoluteBox.
                continue;
            }
            // Per Phase 3 Task 20 cycle 1 — a `position: fixed` box is
            // out-of-flow but owned by the SEPARATE fixed pass (anchored
            // to the page / ICB, repeated per page). Skip it here: don't
            // emit it as abspos, and don't recurse into its subtree (its
            // content is dispatched by the fixed pass, whose nested
            // layouter owns any abspos-inside-fixed).
            if (child.Style.IsFixedPositioned())
            {
                continue;
            }
            // In-flow box. Stop at a delegation boundary: a grid item, or
            // a table cell/caption — the nested BlockLayouter that lays
            // out that subtree already emits its abspos descendants
            // (recursing here would double-emit).
            if (delegatesItemsToNestedLayouter
                || child.Kind is BoxKind.TableCell or BoxKind.TableCaption)
            {
                continue;
            }
            // In-flow box: recurse to find deeper abspos descendants.
            EmitAbsolutelyPositionedDescendants(
                child, containingBlock, diagnostics, ref layout, cancellationToken);
        }
    }

    private void EmitOneAbsoluteBox(
        Box child,
        AbsoluteContainingBlock icb,
        IPaginateDiagnosticsSink? diagnostics,
        ref LayoutContext layout,
        CancellationToken cancellationToken)
    {
        // Phase 3 cycle-2d — this box was CONSIDERED by the page-root pass (record it, on every page,
        // before any early-out), so the pipeline can tell a genuinely-unresolvable box (considered but
        // never emitted on ANY page) from a normal cross-page miss + still diagnose the former.
        _crossPageConsideredAbsolute?.Add(child);
        // The abspos pass runs on every page, so an already-emitted box (its positioned CB spans pages
        // + was recorded on an earlier page too) must not paint again.
        if (_crossPageEmittedAbsolute is not null && _crossPageEmittedAbsolute.Contains(child))
        {
            return;
        }
        // Per Phase 3 Task 19 cycle 2a — anchor to the nearest positioned ancestor's padding box.
        // Phase 3 cycle-2d — a null CB means the ancestor's geometry isn't recorded on THIS page. That
        // is AMBIGUOUS within one page: the CB may be laid out on a LATER page (a normal cross-page
        // miss → the box emits there) OR it may be genuinely never-recordable (an unsupported geometry
        // path). Skip SILENTLY here — the pipeline's post-pagination pass converts "considered but
        // never emitted on any page" into the LAYOUT-ABSOLUTE-FEATURE-UNSUPPORTED-001 diagnostic, so
        // the real drop is still reported while the cross-page miss stays quiet.
        var containingBlock = ResolveContainingBlock(child, icb);
        if (containingBlock is null)
        {
            return;
        }

        var placement = AbsoluteLayouter.ResolvePlacement(child, containingBlock.Value);
        if (!placement.IsResolved)
        {
            OptimizingBreakResolver.SafeEmit(
                diagnostics,
                new PaginateDiagnostic(
                    PaginateDiagnosticCodes.LayoutAbsoluteFeatureUnsupported001,
                    $"position:absolute box dropped (cycle-1 explicit-only): "
                    + $"{placement.DeferReason}",
                    PaginateDiagnosticSeverity.Warning));
            return;
        }

        // Emit the border-box fragment at the resolved position.
        _sink.Emit(new BoxFragment(
            Box: child,
            InlineOffset: placement.InlineOffset,
            BlockOffset: placement.BlockOffset,
            InlineSize: placement.InlineSize,
            BlockSize: placement.BlockSize));
        // Phase 3 cycle-2d — record the emit so a later page (whose pass reconsiders the same box
        // because its CB spans pages) skips it instead of double-painting.
        _crossPageEmittedAbsolute?.Add(child);

        // Dispatch inner content (text / nested blocks) into the box's
        // content area via a translating sub-BlockLayouter. The content
        // box = border box minus border + padding; cycle 1 approximates
        // the content origin/size as the border box (border/padding
        // inset for abspos content is a cycle-2 refinement alongside
        // the padding-box CB).
        if (child.Children.Count > 0
            && placement.InlineSize > 0 && placement.BlockSize > 0)
        {
            // Abspos content pagination (overflow past the box block-size)
            // is a pre-existing behavior outside this cycle's scope — the
            // box-sized fragmentainer paginates + the result is discarded.
            DispatchAbsoluteChildContents(
                child,
                placement.InlineOffset,
                placement.BlockOffset,
                placement.InlineSize,
                placement.BlockSize,
                noPaginate: false,
                ref layout,
                cancellationToken);
        }
    }

    /// <summary>Per Phase 3 Task 20 cycle 1 — lay out + emit the
    /// <c>position: fixed</c> boxes anywhere in <see cref="_rootBox"/>'s
    /// subtree. Runs AFTER the in-flow pass, on EVERY page (the
    /// <see cref="AttemptLayout"/> wrapper does NOT gate this on the
    /// incoming continuation, unlike the abspos pass) and ONLY on the
    /// page-root layouter — so a fixed box repeats on each page anchored
    /// to that page's content area.
    ///
    /// <para><b>Containing block.</b> A fixed box's CB is ALWAYS the page
    /// / initial containing block (the viewport) — never a positioned
    /// ancestor. (The <c>transform</c> / <c>filter</c> / <c>will-change</c>
    /// ancestor that would capture fixed positioning is deferred — those
    /// properties aren't wired yet.) So this anchors every fixed box to
    /// the fragmentainer content area (the same ICB the abspos pass uses
    /// as its fallback) via the shared
    /// <see cref="AbsoluteLayouter.ResolvePlacement"/> §6 solver.</para>
    ///
    /// <para><b>Whole-subtree walk, no delegation boundary.</b> Fixed
    /// boxes appear at any depth — inside grid / flex / table item
    /// subtrees, inside <c>position: absolute</c> subtrees, even nested
    /// inside another fixed box. The page-root layouter is the SOLE owner
    /// of fixed emission: every nested item / cell / column /
    /// abspos-content / fixed-content sub-layouter has a non-Root root
    /// box, so none of them run a fixed pass. Therefore this walk
    /// descends THROUGH all of those subtrees (post-PR-#115 review P2#1:
    /// skipping any of them would silently DROP a fixed box nested
    /// inside). It EMITS only fixed boxes, so re-walking a positioned
    /// subtree never double-emits its normal content (owned by the box's
    /// dispatch / the abspos pass). There is no double-emit to guard
    /// against, unlike abspos P2#4.</para></summary>
    private void EmitFixedPositionedChildren(
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        CancellationToken cancellationToken)
    {
        var pageCb = new AbsoluteContainingBlock(
            InlineOrigin: 0,
            BlockOrigin: 0,
            InlineSize: fragmentainer.ContentInlineSize,
            BlockSize: fragmentainer.BlockSize);
        EmitFixedPositionedDescendants(_rootBox, pageCb, ref layout, cancellationToken);
    }

    /// <summary>Per Phase 3 Task 20 cycle 1 (+ post-PR-#115 review P2#1)
    /// — recursively walk <paramref name="box"/>'s WHOLE subtree,
    /// emitting each <c>position: fixed</c> descendant against the page
    /// <paramref name="pageCb"/>. Descends through EVERY child — in-flow
    /// boxes (incl. grid / flex / table containers + their items),
    /// <c>position: absolute</c> subtrees, AND a fixed box's own subtree
    /// — because the page-root layouter is the SOLE owner of fixed
    /// emission: the nested item / cell / column / abspos-content /
    /// fixed-content sub-layouters all have a non-Root root box, so none
    /// of them run a fixed pass. Skipping any subtree would silently DROP
    /// a fixed box nested inside it (review P2#1: fixed-inside-absolute,
    /// nested fixed-inside-fixed). The walk EMITS only fixed boxes (each
    /// page-anchored); abspos boxes + all normal in-flow content are
    /// emitted by their own passes / dispatches, never here — so
    /// descending into those subtrees finds deeper fixed boxes without
    /// double-emitting anything else.</summary>
    private void EmitFixedPositionedDescendants(
        Box box,
        AbsoluteContainingBlock pageCb,
        ref LayoutContext layout,
        CancellationToken cancellationToken)
    {
        foreach (var child in box.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child.Style.IsFixedPositioned())
            {
                // Emit + dispatch this fixed box's own content...
                EmitOneFixedBox(child, pageCb, ref layout, cancellationToken);
            }
            // ...then descend into EVERY child (including this fixed box,
            // and any abspos box — which we do NOT emit here) so a fixed
            // box nested at any depth is still discovered + page-anchored
            // by this single root-owned pass. We only ever EMIT fixed
            // boxes, so re-walking a positioned subtree can't double-emit
            // its normal content (that's owned by the box's dispatch /
            // the abspos pass).
            EmitFixedPositionedDescendants(child, pageCb, ref layout, cancellationToken);
        }
    }

    /// <summary>Per Phase 3 Task 20 cycle 1 / cycle 2 — resolve a single
    /// <c>position: fixed</c> box against the page CB via the §6 solver
    /// and emit its border-box fragment + dispatch its inner content
    /// (reusing the abspos translating-sub-layouter dispatch — the
    /// dispatch logic is position-mode-agnostic). The §6 solver always
    /// resolves (a negative size clamps to 0), so there is no drop path
    /// for the box itself.
    ///
    /// <para><b>Content overflow (cycle 2).</b> CSS Position L3 §6.3 says
    /// fixed-positioned boxes are NOT paginated. The inner content is
    /// dispatched with <c>noPaginate: true</c> — pagination is SUPPRESSED
    /// (the inner fragmentainer's <c>SuppressBlockPagination</c>), so
    /// content taller than the box OVERFLOWS at its natural position (CSS
    /// <c>overflow: visible</c>) instead of being clipped. (Cycle 1
    /// clipped + diagnosed; cycle 2 emits the overflow.) Crucially the
    /// inner fragmentainer's <c>BlockSize</c> stays the box content-area
    /// height (NOT inflated), so descendant percentage / <c>bottom</c>
    /// resolution — which the §6 solver DOES compute against the CB block
    /// extent — anchors to the box, not an artificial budget (post-PR-#116
    /// review P1).</para></summary>
    private void EmitOneFixedBox(
        Box child,
        AbsoluteContainingBlock pageCb,
        ref LayoutContext layout,
        CancellationToken cancellationToken)
    {
        var placement = AbsoluteLayouter.ResolvePlacement(child, pageCb);
        _sink.Emit(new BoxFragment(
            Box: child,
            InlineOffset: placement.InlineOffset,
            BlockOffset: placement.BlockOffset,
            InlineSize: placement.InlineSize,
            BlockSize: placement.BlockSize));

        // Per Phase 3 Task 20 cycle 2 (+ post-PR-#116 review) — dispatch
        // the content even when the box's resolved block-size is 0 (e.g.
        // `height: 0` with visible overflow): pagination is suppressed, so
        // the content overflows the box regardless, and dropping it would
        // be exactly the clip cycle 2 removes. Only the INLINE extent
        // gates (content needs a width to lay into). The inner
        // fragmentainer requires a positive block extent (its ctor
        // enforces it), so a 0/sub-px box is clamped to 1px — content
        // still overflows from the top; the 1px CB only marginally affects
        // a degenerate box's descendant percentage/`bottom` resolution.
        if (child.Children.Count > 0 && placement.InlineSize > 0)
        {
            // pagination suppressed (SuppressBlockPagination) so the fixed
            // box's content lays out in one pass + OVERFLOWS at its natural
            // position (CSS `overflow: visible`; CSS Position L3 §6.3). The
            // inner fragmentainer keeps BlockSize = the box content height
            // (clamped >0), so descendant percentage / `bottom` resolution
            // is against the box, not an inflated budget.
            DispatchAbsoluteChildContents(
                child,
                placement.InlineOffset,
                placement.BlockOffset,
                placement.InlineSize,
                System.Math.Max(placement.BlockSize, 1.0),
                noPaginate: true,
                ref layout,
                cancellationToken);
        }
    }

    /// <summary>Per Phase 3 Task 19 cycle 1 (+ Task 20 cycle 2) —
    /// dispatch a positioned box's inner content into its content area
    /// via a translating sub-BlockLayouter. Mirrors
    /// <c>GridLayouter.DispatchGridItemContents</c>: a translating sink
    /// maps inner box-relative offsets to outer sink coordinates; the
    /// inner fragmentainer is sized to the box.
    ///
    /// <para><b><c>noPaginate</c> (fixed content, cycle 2).</b> The inner
    /// fragmentainer's <c>BlockSize</c> stays the box content-area height
    /// either way — it is the containing-block extent the nested abspos
    /// pass hands the §6 solver for descendant percentage / <c>bottom</c>
    /// resolution, so it must NOT be inflated. Instead
    /// <c>SuppressBlockPagination</c> (+ <c>disableGridPagination</c>)
    /// disables breaking, so a `position: fixed` box's content lays out
    /// in one pass + OVERFLOWS the box at its natural position (CSS
    /// `overflow: visible`; CSS Position L3 §6.3 — fixed boxes are not
    /// paginated) rather than being clipped. The abspos caller passes
    /// <c>noPaginate: false</c> (it paginates + discards the result —
    /// a separate pre-existing item).</para></summary>
    private void DispatchAbsoluteChildContents(
        Box box,
        double inlineOrigin,
        double blockOrigin,
        double inlineSize,
        double blockSize,
        bool noPaginate,
        ref LayoutContext outerLayout,
        CancellationToken cancellationToken)
    {
        var translatingSink = new AbsoluteTranslatingSink(
            outerSink: _sink,
            inlineTranslation: inlineOrigin,
            blockTranslation: blockOrigin);
        var innerFragmentainer = new FragmentainerContext(
            contentInlineSize: inlineSize,
            blockSize: blockSize)
        {
            SuppressBlockPagination = noPaginate,
        };
        var innerLayout = new LayoutContext(innerFragmentainer)
        {
            Diagnostics = outerLayout.Diagnostics,
            WritingMode = outerLayout.WritingMode,
            IsRtl = outerLayout.IsRtl,
            GridMeasureCache = outerLayout.GridMeasureCache,   // measurement-cache cycle
            TableMeasureCache = outerLayout.TableMeasureCache, // multi-page-allocation-churn
        };
        using var innerLayouter = new BlockLayouter(
            rootBox: box,
            sink: translatingSink,
            incomingContinuation: null,
            diagnostics: _diagnostics,
            shaperResolver: _shaperResolver,
            // Fixed content isn't paginated, so a nested grid mustn't
            // paginate inside it either (it would drop rows past the box).
            disableGridPagination: noPaginate);
        using var innerResolver = new BreakResolver();
        // The result is intentionally not consumed: with pagination
        // suppressed (fixed) the content fully overflows in one pass; the
        // abspos path (noPaginate:false) discards it as before.
        _ = innerLayouter.AttemptLayout(
            innerFragmentainer,
            ref innerLayout,
            innerResolver,
            LayoutAttemptStrategy.LastResort,
            cancellationToken);
    }

    /// <summary>Per Phase 3 Task 19 cycle 1 — translates inner
    /// (box-relative) fragment offsets to outer sink coordinates.
    /// Identical contract to <c>GridLayouter.TranslatingFragmentSink</c>;
    /// duplicated here as a nested type to avoid cross-layouter
    /// coupling (the rule-of-three extraction can wait until a third
    /// consumer appears).</summary>
    private sealed class AbsoluteTranslatingSink : IBlockFragmentSink
    {
        private readonly IBlockFragmentSink _outer;
        private readonly double _inlineTranslation;
        private readonly double _blockTranslation;
        private readonly int _baseline;

        public AbsoluteTranslatingSink(
            IBlockFragmentSink outerSink,
            double inlineTranslation,
            double blockTranslation)
        {
            _outer = outerSink;
            _inlineTranslation = inlineTranslation;
            _blockTranslation = blockTranslation;
            _baseline = outerSink.Cursor;
        }

        public int Cursor => _outer.Cursor - _baseline;

        public void Emit(BoxFragment fragment)
        {
            _outer.Emit(fragment with
            {
                InlineOffset = fragment.InlineOffset + _inlineTranslation,
                BlockOffset = fragment.BlockOffset + _blockTranslation,
            });
        }

        public void RollbackTo(int cursor) => _outer.RollbackTo(_baseline + cursor);

        public void UpdateFragmentBlockSize(int cursor, double newBlockSize)
            => _outer.UpdateFragmentBlockSize(_baseline + cursor, newBlockSize);
    }

    /// <summary>Minimum margin-box content extent (px) below an inline-only block's border-box top for the
    /// prose-pagination break to consider it. A block at or under this is treated as having NO paginatable
    /// content (an empty / whitespace anonymous block) — breaking before it would spawn a spurious page.
    /// Sub-px to admit any real single line while excluding the zero-extent case.</summary>
    private const double InlineOnlyBreakMinExtentPx = 0.5;

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
    /// <see cref="MeasureSubtreeVisualBlockExtent(Box, CancellationToken, double, double)"/>
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
    private LayoutContinuation? EmitBlockSubtreeRecursive(
        Box parent,
        double parentBlockOffset,
        double parentInlineOffset,
        double parentInlineSize,
        CancellationToken cancellationToken,
        int depth,
        IBreakResolver? propagatingResolver = null,
        FragmentainerContext? propagatingFragmentainer = null,
        LayoutContinuation? incomingContinuation = null,
        // Body % height (percent-height cycle): the parent's DEFINITE content height in px —
        // a child `height: N%` resolves against it (CSS 2.2 §10.5); 0 = indefinite (the parent's
        // height is auto), the percentage then computes to auto (reads 0).
        double parentContentBlockSize = 0)
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
            throw new LayoutDepthExceededException(
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
            return null;
        }

        // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) — unwrap
        // the incoming continuation chain. The recursion-chain
        // protocol nests BlockContinuations inside
        // LayouterState; each recursion level peels one layer.
        // The LEAF must be a Table/MulticolContinuation that the
        // direct-dispatch branch below consumes.
        var incomingBlockChain = incomingContinuation as BlockContinuation;

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

        // Phase 3 multi-page driver, cycle 1 (PR #175 review P1) — forward-
        // progress baseline for the nested fragmentation break check below.
        // The sink's fragment count when THIS recursion level starts: the
        // break only fires once `_sink.Cursor` has advanced past it — i.e. at
        // least one child's fragment was actually emitted at this level on
        // this page. This is robust where the signed `childCursor` is NOT a
        // reliable "emitted something" signal: a prior nested FLOAT emits a
        // fragment without advancing childCursor, and a prior block with
        // NEGATIVE margins can leave childCursor <= 0 after real emission.
        var sinkCursorAtRecursionEntry = _sink.Cursor;

        // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) — indexed
        // iteration so we can match the recursion-chain's
        // ResumeAtChild index against the current child position.
        //
        // Per post-PR-#57 review #2 Finding #1 — skip-to-resume.
        // When the recursion receives a chain head with `ResumeAtChild
        // = N`, the prior page already committed this subtree's
        // children at indices [0, N). Re-emitting them on the resume
        // page would duplicate content. Start iteration at `N` so the
        // pre-resume children are correctly skipped. (When the chain
        // head is null — i.e., the recursion is layering down a fresh
        // subtree, not resuming — startIdx defaults to 0 and the full
        // child list is walked.) The top-level AttemptLayout loop has
        // a symmetric skip via `startChildIdx`; this brings the
        // recursion in line.
        var startIdx = incomingBlockChain?.ResumeAtChild ?? 0;
        // Named-page forced break (PR #179 review P1): the used `page` value (CSS Page 3 §3.4) of the
        // previous block-flow child emitted at this level on this page. A change forces a page break before
        // the next child even if it would fit. Initialized to the container's own page name (so the first
        // child only breaks once there's prior content — guarded by the forward-progress check below).
        var prevPageName = parent.PageName;
        // CSS Fragmentation L3 §3.1/§3.2 — the previously-emitted in-flow block-level
        // sibling (block-flow OR inline-only); its `break-after` forces a page break before
        // the next child + its `break-after: avoid` marks the boundary AvoidBreak (mirrors
        // prevPageName). Reset each recursion entry, so a forced break at a fragmentainer
        // start is a no-op (the forward-progress guard below also enforces this).
        Box? prevInFlowChild = null;
        for (var childIdx = startIdx; childIdx < parent.Children.Count; childIdx++)
        {
            var child = parent.Children[childIdx];
            cancellationToken.ThrowIfCancellationRequested();

            // Per Phase 3 Task 19 cycle 1 (+ Task 20 cycle 1) —
            // out-of-flow positioned boxes (`position: absolute` AND
            // `position: fixed`) are out-of-flow at EVERY depth (not just
            // top-level children): skip them from the in-flow recursion
            // without touching the margin-collapse chain (out-of-flow
            // boxes don't break adjacency). The post-flow passes
            // (EmitAbsolutelyPositionedChildren / EmitFixedPositionedChildren)
            // walk the whole subtree + emit them, so skipping here just
            // keeps them out of normal flow.
            if (child.Style.IsOutOfFlow())
            {
                continue;
            }

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

            // PR #207 Copilot review [P1]/[P2] — CSS Fragmentation forced + avoid breaks apply
            // to EVERY in-flow block-level child (block-flow, inline-only, AND nested table /
            // flex / grid containers), so compute the metadata ONCE here instead of per dispatch
            // branch. A FORCED break propagates out of a fitting ancestor regardless of the
            // child's layout type; the per-branch break opportunities below carry the AVOID flag
            // via `childAvoidBreak`. The forced break is gated on the same forward-progress +
            // resolver state as the per-branch checks (`suppressForce: false` — the guard ensures
            // prior content, so a forced break at a fragmentainer start is a no-op per §3.1).
            var (childForcedBreak, childAvoidBreak, childForceParity) = ResolveChildBreakMetadata(
                parent, child, prevInFlowChild, suppressForce: false);
            if (childForcedBreak
                && propagatingResolver is not null
                && propagatingFragmentainer is { SuppressBlockPagination: false }
                && _sink.Cursor > sinkCursorAtRecursionEntry)
            {
                // CSS Page L3 §3.4.1 — carry a nested forced left/right/recto/verso break's parity
                // up for the driver's blank-page insertion (same instance owns the field).
                if (childForceParity != PageParity.Any)
                    _forcedBreakParityForNextPage = childForceParity;
                return new BlockContinuation(ResumeAtChild: childIdx, ConsumedBlockSize: 0);
            }
            // Remember THIS child as the previous in-flow sibling for the next iteration's
            // `break-after` (covers all child types — block-flow, inline-only, table/flex/grid).
            prevInFlowChild = child;

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
                // Percent-aware (post-PR-#163 review P1): EmitInlineOnlyBlockInRecursion
                // advances the cursor by the PERCENT-RESOLVED MarginBlockStart (its metrics read
                // against the parent content box), so the POSITION must use the same base — a
                // ReadLengthPxOrZero here read `margin-top: 10%` as 0 and the margin got reserved
                // AFTER the child instead of pushing it down.
                var inlineOnlyMarginStart =
                    child.Style.ReadLengthOrPercentPx(PropertyId.MarginTop, contentInlineSize);

                // `inline-only-block-line-splitting` — resume a prior line split of THIS block
                // (one-shot consume of the chained continuation, mirroring grid/multicol). On
                // resume the block re-enters at the top of a fresh page with no margin-top.
                var recInlineResumeLine = 0;
                // box-decoration-break: slice (PR #222 review [P1]) — the resumed slice's cumulative physical
                // decoration offset + the composite box's total physical extent, carried in the continuation.
                var recInlineDecorationOffset = 0.0;
                var recInlineDecorationTotal = 0.0;
                if (incomingBlockChain is not null
                    && childIdx == incomingBlockChain.ResumeAtChild
                    && incomingBlockChain.LayouterState is InlineOnlyLineSplitContinuation recLineCont)
                {
                    recInlineResumeLine = recLineCont.ResumeLineIndex;
                    recInlineDecorationOffset = recLineCont.DecorationBlockOffsetPx;
                    recInlineDecorationTotal = recLineCont.DecorationTotalExtentPx;
                    incomingBlockChain = null;
                }

                // Prose pagination (block-granularity, 2026-06-20) — the recursion's inline-only path now
                // CONSULTS the break resolver before emitting, mirroring the in-flow block break check below.
                // Pre-fix this branch emitted unconditionally + `continue`d, so N stacked text blocks taller
                // than the page all force-overflowed page 1 (`<p>×200` → 1 page). Now a text block whose
                // VISUAL extent would overflow the fragmentainer breaks WHOLE to the next page (its resume
                // re-enters at `childIdx`, exactly like a block-flow child). CHILD-BOUNDARY granularity only —
                // splitting a single paragraph's LINES across pages (orphans/widows) stays the deferred
                // mid-subtree work (`inline-only-block-line-splitting`).
                //
                // The chunk is the VISUAL extent BELOW the border-box top (border-box + a NON-NEGATIVE
                // margin-bottom), mirroring the top-level inline path's `max(marginBox, visualBlockExtent)`
                // (review P2): a negative margin-bottom must NOT shrink it below the real text border-box,
                // which would let visible prose overflow instead of breaking.
                //
                // GUARD `> InlineOnlyBreakMinExtentPx`: only break a block with REAL content extent. A
                // ZERO-extent inline-only block — an empty/whitespace AnonymousBlock the recursion may walk
                // for flex/grid content, placed PAST the page edge by that layout — has nothing to push to
                // the next page; breaking it spawned a spurious page (regressed flex wrap-reverse + grid-fr —
                // both chunk == 0 at start > pageBlockSize). Forward-progress guard (`_sink.Cursor >
                // sinkCursorAtRecursionEntry`): an over-tall FIRST block still force-overflows so pagination
                // always progresses. Named-page breaks on an inline-only block stay deferred (rare; the
                // block-flow path owns them).
                if (propagatingResolver is not null
                    && propagatingFragmentainer is { SuppressBlockPagination: false } pfInline
                    && _sink.Cursor > sinkCursorAtRecursionEntry)
                {
                    // PR #207 review [P1] — a text-bearing / inline-only block honors author
                    // forced + avoid breaks too. The FORCED break is handled at the loop top
                    // (above, before this branch — even for a near-empty block); here the
                    // overflow-driven opportunity carries the AVOID flag (`childAvoidBreak`).
                    _ = MeasureInlineOnlyBlockExtent(
                        child, contentInlineSize, out var inlineVisualChunk, cancellationToken);
                    if (inlineVisualChunk > InlineOnlyBreakMinExtentPx)
                    {
                        var inlineChildStart = Math.Max(0, contentTop + childCursor + inlineOnlyMarginStart);
                        var savedUsedBlockSizeInline = pfInline.UsedBlockSize;
                        pfInline.UsedBlockSize = inlineChildStart;
                        var inlineDecision = propagatingResolver.ConsiderBreakAt(
                            BreakOpportunity.Block(
                                usedBlockSize: inlineChildStart, chunkBlockSize: inlineVisualChunk,
                                avoidBreak: childAvoidBreak),
                            pfInline);
                        pfInline.UsedBlockSize = savedUsedBlockSizeInline;
                        if (inlineDecision.Action == BreakAction.BreakHere)
                        {
                            return new BlockContinuation(ResumeAtChild: childIdx, ConsumedBlockSize: 0);
                        }
                    }
                }

                // Block-axis offset of the block's BORDER-BOX top inside the parent's content
                // area. A FRESH block (resumeLine 0) lands after its own margin-top; a RESUMED
                // tail (`inline-only-block-line-splitting`) continues at the page top with no
                // margin (a fragmentation break suppresses the continuation's margin).
                var recInlineBorderBoxTop = recInlineResumeLine > 0
                    ? contentTop + childCursor
                    : contentTop + childCursor + inlineOnlyMarginStart;
                var inlineSplitReturn = EmitInlineOnlyBlockInRecursionSplitting(
                    child,
                    parentContentInlineSize: contentInlineSize,
                    // Inline-axis offset: the parent's content-area left edge in fragmentainer
                    // coords; the helper adds the block's own marginInlineStart.
                    inlineOffsetFromContentOrigin: contentLeft,
                    blockBorderBoxTop: recInlineBorderBoxTop,
                    childIdx: childIdx,
                    startLine: recInlineResumeLine,
                    fragmentainer: propagatingFragmentainer,
                    out var emittedExtent,
                    cancellationToken,
                    resumeDecorationOffsetPx: recInlineDecorationOffset,
                    resumeDecorationTotalPx: recInlineDecorationTotal);
                // Inline-only block resets the margin-collapse chain
                // per CSS 2.1 §8.3.1 (the line box breaks adjacency).
                hasPrior = false;
                prevMarginEnd = 0;
                // (The previous-in-flow-sibling tracking for `break-after` is done once at the
                // loop top now — covers all child types.)
                // A non-null return means the block split its lines across pages: propagate the
                // continuation up (page break, resume the tail next page).
                if (inlineSplitReturn is not null)
                {
                    return inlineSplitReturn;
                }
                // Finished on this page — advance the recursive cursor by the emitted slice's
                // full margin-box extent (margin-top-if-first + borderBox + margin-bottom).
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

            // Body % lengths (body-percent cycle) — same §8.3/§8.4 inline-axis base as the outer
            // dispatch path, here the parent's content box.
            // PR #218 review [P1 #2 / P2 #5] — cyclic % padding/margins → 0 for an intrinsic probe
            // (CSS Sizing §5.2.1), real against the definite width otherwise; only a real layout
            // persists the resolved % padding for paint.
            var pctInsetBase = _measurePurpose.ZeroesCyclicPercentInsets() ? 0.0 : contentInlineSize;
            if (_measurePurpose == MeasurePurpose.Layout)
                child.Style.ResolveUsedPercentPaddingInPlace(contentInlineSize);   // paint reads the slots later.
            var marginStart = child.Style.ReadLengthOrPercentPx(PropertyId.MarginTop, pctInsetBase);
            var marginEnd = child.Style.ReadLengthOrPercentPx(PropertyId.MarginBottom, pctInsetBase);
            var marginInlineStart = child.Style.ReadLengthOrPercentPx(PropertyId.MarginLeft, pctInsetBase);
            var marginInlineEnd = child.Style.ReadLengthOrPercentPx(PropertyId.MarginRight, pctInsetBase);
            var borderStart = child.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
            var borderEnd = child.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
            var paddingStart = child.Style.ReadLengthOrPercentPx(PropertyId.PaddingTop, pctInsetBase);
            var paddingEnd = child.Style.ReadLengthOrPercentPx(PropertyId.PaddingBottom, pctInsetBase);
            // % height resolves against the parent's DEFINITE content height (0 = indefinite →
            // auto, CSS 2.2 §10.5 — percent-height cycle).
            var contentBlock = child.Style.ReadLengthOrPercentPx(PropertyId.Height, parentContentBlockSize);

            // Box-sizing audit (CSS Basic UI 4 §10) — an explicit `height` under
            // `box-sizing: border-box` IS the border box (chrome inside), floored at
            // the chrome; `content-box` (the initial) adds it, byte-identical to the
            // pre-fix `declared + chrome`. Mirrors the outer dispatch (line ~1372) +
            // the float path: pre-fix this recursive emitter added chrome
            // unconditionally, so a border-box block over-sized on the BLOCK axis
            // while the inline axis already routed through
            // ResolveInFlowBorderBoxInlineSize → BoxSizingHelper.
            var childBorderBoxBlockSize = BoxSizingHelper.DeclaredToBorderBox(
                child.Style, contentBlock, borderStart + paddingStart + paddingEnd + borderEnd);
            // §10.7 — min-height / max-height clamp the explicit height. No-op when unset.
            // A `%` min/max-height resolves against the same base as `% height` above.
            childBorderBoxBlockSize = child.ClampBorderBoxToMinMax(
                childBorderBoxBlockSize, PropertyId.MinHeight, PropertyId.MaxHeight,
                parentContentBlockSize);
            // Per the body-explicit-width gap fix — same §10.3.3 subset
            // as the outer dispatch path (explicit `width` on a plain
            // block container overrides the fill), so a nested div
            // sizes identically to a top-level one.
            var childBorderBoxInlineSize = ResolveInFlowBorderBoxInlineSize(
                child, contentInlineSize, contentInlineSize, marginInlineStart, marginInlineEnd);
            ResolveAutoInlineMargins(
                child, childBorderBoxInlineSize, contentInlineSize,
                ref marginInlineStart, ref marginInlineEnd);   // §10.3.3 — auto-margins cycle.

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
                child, cancellationToken, depth + 1,
                parentContentBlockSize: contentBlock);   // % height base (percent-height cycle).
            // Per Phase 3 Task 15 L6 post-PR-#66 review F#1 — mirror
            // the outer-dispatch column-wrap clamp at the recursion
            // site. The unconditional clamp later in the flex grow
            // block (= `childEffectiveBlockSize > childBorderBoxBlockSize`
            // after the post-grow recalc) ALSO catches this case, but
            // a defensive symmetric clamp on `childSubtreeExtent`
            // before the `Math.Max` keeps the two dispatch paths in
            // lockstep — the same predicate fires the same shrinkage
            // at both sites. Column + wrap + explicit (LengthPx)
            // height: the wrapper's block extent is fixed by the
            // declaration; over-measuring `childSubtreeExtent` past it
            // would propagate into `childEffectiveBlockSize` via the
            // Math.Max below.
            if (IsFlexContainer(child)
                && child.Style.ReadFlexDirection().IsFlexColumnDirection()
                && child.Style.ReadFlexWrap().IsFlexWrapping()
                && child.Style.Get(PropertyId.Height).Tag == ComputedSlotTag.LengthPx
                && childSubtreeExtent > childBorderBoxBlockSize)
            {
                childSubtreeExtent = childBorderBoxBlockSize;
            }
            var childEffectiveBlockSize = Math.Max(
                childBorderBoxBlockSize, childSubtreeExtent);

            double topShift;
            if (!hasPrior)
            {
                // Phase 3 cycle-2d "mid-split" — when this container was RESUMED at a mid-child
                // boundary (startIdx > 0), the first child processed here starts at the fragmentainer
                // top. Per CSS Fragmentation L3 §5.1 the margin adjacent to the (unforced) break is
                // truncated to zero, so the resumed child hugs the page top instead of re-applying its
                // full top margin — which would open a phantom gap on the resume page AND inflate the
                // resumed-extent this level publishes to its parent's cursor advance. Only the first
                // resumed child truncates; its siblings collapse normally.
                topShift = (MidSplitEnabled && startIdx > 0 && childIdx == startIdx)
                    ? 0
                    : marginStart;
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

            // Phase 3 multi-page driver, cycle 1 — nested-container
            // fragmentation (docs/design/multi-page-driver.md §4.1). Before
            // committing a nested child, break the page when its border-box
            // (visual extent, incl. its own descendants) would overflow the
            // fragmentainer's block axis, AS LONG AS at least one child has
            // already committed at this level (childCursor advanced past 0 =
            // forward progress; an oversized FIRST child force-emits instead,
            // mirroring the top-level loop's forced-overflow path — so
            // pagination always makes progress + never spins). The break is
            // returned as a BlockContinuation pointing at THIS child; the
            // recursion's callers (the normal walk's `recursiveReturn`
            // handler + the forced-overflow path's `forcedNestedRet` handler)
            // wrap it into the chain + propagate PageComplete, and the resume
            // page skips the committed children via `startIdx`. Gated on a
            // propagating resolver + fragmentainer: the float recursion sites
            // omit both, so float subtrees stay atomic (float continuation is
            // a separate deferral). Suppressed when the fragmentainer
            // disables pagination (position: fixed content, Task 20). Child-
            // boundary granularity only — a single child taller than the page
            // still force-overflows (no intra-child line split this cycle).
            //
            // ONLY break before a plain block-flow container (the kinds this
            // layouter owns). A flex / grid / table / multicol child
            // paginates INTERNALLY via its own dispatch below (returning a
            // chained continuation when it splits); breaking before it would
            // wrongly push the whole box to the next page + waste the
            // current page's remaining space (a flex container that can fit
            // its first lines must not be deferred wholesale).
            //
            // PR #175 review P2 — consult the IBreakResolver (do NOT hard-code
            // the overflow test) so the nested boundary honors the resolver's
            // policy + is forward-compatible with the optimizing resolver. The
            // greedy resolver's fit check reads ctx.RemainingBlockSize (= page
            // minus UsedBlockSize), but the recursion tracks position in its
            // own childCursor and does NOT advance the fragmentainer's
            // UsedBlockSize per child — so set it transiently to this child's
            // block-start before asking, then restore it (the top-level loop
            // owns that accounting; we're single-threaded, so the mutation is
            // fully contained). CSS Fragmentation `break-before`/`-after`/`-inside`
            // metadata IS now carried on the opportunity (PR #207 review [P2] — see the
            // ResolveChildBreakMetadata call below). STILL deferred at nested boundaries:
            // checkpoint registration + BreakAction.Rewind (the greedy resolver never
            // rewinds; the recursion holds no checkpoint to roll back to).
            if (propagatingResolver is not null
                && propagatingFragmentainer is { SuppressBlockPagination: false } pf
                && IsBlockFlowContainerOwnedByBlockLayouter(child)
                && _sink.Cursor > sinkCursorAtRecursionEntry)
            {
                // Named-page FORCED break (PR #179 review P1): CSS Page 3 §3.4 — a change in the used
                // `page` value from the preceding box forces a page break, even if the child would fit.
                // Check this BEFORE the greedy fit check; the forward-progress guard above ensures the
                // page already has content (a named child that STARTS the page just names the page).
                if (!string.Equals(child.PageName, prevPageName, StringComparison.Ordinal))
                {
                    return new BlockContinuation(
                        ResumeAtChild: childIdx,
                        ConsumedBlockSize: 0);
                }
                // CSS Fragmentation L3 §3.2 (PR #207 review [P2]) — the recursive nested-block
                // boundary carries the AVOID flag (`childAvoidBreak`, computed once at the loop
                // top from `parent`'s `break-inside:avoid` / this child's `break-before:avoid` /
                // the prior sibling's `break-after:avoid`; optimizer-honored). The FORCED break is
                // handled at the loop top (covering all child types), so the opportunity does not
                // re-force here.
                var childStart = Math.Max(0, childBlockOffset);
                var pageRemaining = pf.BlockSize - childStart;
                // Phase 3 cycle-2d "mid-split" (enter-and-split): the whole subtree
                // (childEffectiveBlockSize) doesn't fit the remaining page, so the pre-cycle-2d
                // behavior moves it WHOLLY to the next page — wasting the trailing space. Instead,
                // for an eligible multi-child container whose FIRST in-flow child DOES fit, feed the
                // resolver that first child's extent (the minimum unbreakable unit) so it returns
                // Continue: the container is entered here + the recursion below breaks BETWEEN its
                // children on overflow (returning a BlockContinuation(ResumeAtChild) that the caller
                // propagates). Forced breaks + avoid metadata are unaffected (still on the
                // opportunity). See docs/design/pagination-mid-split.md.
                var breakChunk = childEffectiveBlockSize;
                if (MidSplitEnabled
                    && childEffectiveBlockSize > pageRemaining
                    && IsEnterAndSplitEligible(child))
                {
                    var firstChildExtent = EstimateFirstInFlowChildExtent(
                        child, cancellationToken, contentBlock);
                    if (firstChildExtent > 0 && firstChildExtent <= pageRemaining)
                    {
                        breakChunk = firstChildExtent;
                    }
                }
                var savedUsedBlockSize = pf.UsedBlockSize;
                pf.UsedBlockSize = childStart;
                var nestedDecision = propagatingResolver.ConsiderBreakAt(
                    BreakOpportunity.Block(
                        usedBlockSize: childStart,
                        chunkBlockSize: breakChunk,
                        avoidBreak: childAvoidBreak),
                    pf);
                pf.UsedBlockSize = savedUsedBlockSize;
                if (nestedDecision.Action == BreakAction.BreakHere)
                {
                    return new BlockContinuation(
                        ResumeAtChild: childIdx,
                        ConsumedBlockSize: 0);
                }
            }
            // Track this block-flow child's used page name as the "previous" for the next child's
            // named-page break check (PR #179 review P1). Updated whether or not the child later
            // breaks for another reason — it is on (or starts) this page either way. (The
            // break-after sibling tracking is done once at the loop top — all child types.)
            if (IsBlockFlowContainerOwnedByBlockLayouter(child))
            {
                prevPageName = child.PageName;
            }

            // Phase 3 cycle-2d "mid-split" — child-boundary break for a FLEX child that fits a FRESH
            // page but not the space remaining on THIS page. Per CSS Fragmentation L3 §3, a box that
            // fits within a single fragmentainer should move wholly to the next one rather than be
            // fragmented; the flex dispatch below only splits a flex INTERNALLY when it's taller than
            // the page (or force-overflows it when it starts too near the page end for its chrome to
            // fit). Without this, an eligible container entered mid-page (enter-and-split above) would
            // pile its later flex children off the bottom of the page instead of continuing them on
            // the next page. Gated on forward progress (something already emitted at this level on
            // this page) so a first, oversized flex child still force-overflows and pagination
            // progresses. Scoped to flex — grid + table have their own row/track deferral machinery.
            if (MidSplitEnabled
                && propagatingResolver is not null
                && propagatingFragmentainer is { SuppressBlockPagination: false } flexBreakPf
                && IsFlexContainer(child)
                && _sink.Cursor > sinkCursorAtRecursionEntry
                // Only a flex child that fits a FRESH page is a move-wholly candidate; one taller than
                // a page paginates internally via the dispatch below (don't pre-empt it).
                && childEffectiveBlockSize <= flexBreakPf.BlockSize)
            {
                // Route through the resolver — usedBlockSize / chunkBlockSize / avoidBreak — exactly
                // like the block-flow boundary above, so the break honors the resolver's policy + the
                // CSS Fragmentation avoid metadata (`childAvoidBreak`) instead of a hard-coded fit
                // test. The greedy resolver breaks here iff the child overflows the remaining space;
                // the optimizing resolver can weigh the avoid penalty.
                var flexChildStart = Math.Max(0, childBlockOffset);
                var savedFlexUsed = flexBreakPf.UsedBlockSize;
                flexBreakPf.UsedBlockSize = flexChildStart;
                var flexDecision = propagatingResolver.ConsiderBreakAt(
                    BreakOpportunity.Block(
                        usedBlockSize: flexChildStart,
                        chunkBlockSize: childEffectiveBlockSize,
                        avoidBreak: childAvoidBreak),
                    flexBreakPf);
                flexBreakPf.UsedBlockSize = savedFlexUsed;
                if (flexDecision.Action == BreakAction.BreakHere)
                {
                    return new BlockContinuation(
                        ResumeAtChild: childIdx,
                        ConsumedBlockSize: 0);
                }
            }

            // Per Phase 3 Task 12 sub-cycle 1 hardening (Finding 6 /
            // 1) — for nested Table / InlineTable wrappers, pre-
            // measure the table content + fold it into the wrapper's
            // border-box block size (same protocol the outer
            // AttemptLayout loop uses). Without this, tables that
            // appear deeper than the layouter's root (e.g., the
            // canonical body > table case from real HTML) wouldn't
            // get their wrapper sized by the row stack — siblings
            // would overlap them.
            //
            // Per Phase 3 Task 12 sub-cycle 2 hardening (Finding 1) —
            // use the OUTER fragmentainer captured at AttemptLayout
            // entry, not a synthesized `blockSize: 1`. The 1-px
            // fragmentainer was a sub-cycle-1 stopgap that produced
            // false PAGINATION-FORCED-OVERFLOW-001 warnings for ANY
            // positive-height nested table + caused cell content with
            // tall block children to hit BlockLayouter's forced-
            // overflow path (whose continuation TableLayouter ignored,
            // losing content).
            TableLayouter? nestedPendingTable = null;
            var nestedMeasuredHeight = 0.0;
            if (child.Kind is BoxKind.Table or BoxKind.InlineTable
                && _capturedFragmentainer is not null)
            {
                // The recursion doesn't have ref layout. Synthesize a
                // transient LayoutContext carrying the constructor-
                // injected diagnostics sink + the captured
                // FragmentainerContext — the table layouter consults
                // these for cell-content layout, deferral diagnostics,
                // forced-overflow signals, etc.
                var fragmentainerForMeasure = _capturedFragmentainer;
                var transientLayout = new LayoutContext(fragmentainerForMeasure)
                {
                    Diagnostics = _diagnostics,
                };
                var nestedMeasuredUsedInline = 0.0;
                // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) —
                // the recursion-chain protocol replaces the depth==1
                // gate. When the incoming continuation's chain reaches
                // a TableContinuation leaf AND the current child
                // matches its ResumeAtChild, use that continuation.
                LayoutContinuation? incomingForThisTable = null;
                if (incomingBlockChain is not null
                    && childIdx == incomingBlockChain.ResumeAtChild
                    && incomingBlockChain.LayouterState is TableContinuation incomingTcLeaf)
                {
                    incomingForThisTable = incomingTcLeaf;
                }
                nestedPendingTable = PreMeasureTableIfNeeded(
                    wrapperChild: child,
                    wrapperInlineOffset: childInlineOffset,
                    wrapperBlockOffsetExpected: childBlockOffset,
                    wrapperInlineSize: childBorderBoxInlineSize,
                    fragmentainer: fragmentainerForMeasure,
                    layout: ref transientLayout,
                    incomingTableContinuation: incomingForThisTable,
                    tableContentHeight: out nestedMeasuredHeight,
                    tableMeasuredUsedInlineSize: out nestedMeasuredUsedInline,
                    cancellationToken: cancellationToken,
                    // Per Phase 3 Task 14 cycle 2 hardening (Finding #1)
                    // — match the cycle-1 outer-loop convention
                    // (BlockLayouter.cs:1128). The deep table can now
                    // split via the lifted recursion-chain propagation;
                    // size the wrapper to the committed-page extent so
                    // the wrapper doesn't claim the full natural extent
                    // (which would over-reserve space + trip false
                    // forced-overflow on the page that actually fits
                    // only some rows).
                    useDryRunCommittedHeight: true);
                if (incomingForThisTable is not null && nestedPendingTable is not null)
                {
                    // Consumed — clear so siblings (or deeper
                    // recursions) don't accidentally inherit.
                    incomingBlockChain = null;
                }
                if (nestedPendingTable is not null)
                {
                    var wrapperBorderPaddingBlock =
                        borderStart + paddingStart + paddingEnd + borderEnd;
                    var tableDriven = nestedMeasuredHeight + wrapperBorderPaddingBlock;
                    if (tableDriven > childBorderBoxBlockSize)
                    {
                        childBorderBoxBlockSize = tableDriven;
                    }
                    if (tableDriven > childEffectiveBlockSize)
                    {
                        childEffectiveBlockSize = tableDriven;
                    }

                    // Per Phase 3 Task 12 sub-cycle 5 hardening
                    // Finding 6 — widen the wrapper's INLINE border-
                    // box when the grid's used inline-size overflows.
                    // Same protocol as the outer loop above.
                    var nestedBorderInlineStart =
                        child.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth);
                    var nestedBorderInlineEnd =
                        child.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth);
                    var nestedPaddingInlineStart =
                        child.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft);
                    var nestedPaddingInlineEnd =
                        child.Style.ReadLengthPxOrZero(PropertyId.PaddingRight);
                    var nestedBorderPaddingInline =
                        nestedBorderInlineStart + nestedBorderInlineEnd
                        + nestedPaddingInlineStart + nestedPaddingInlineEnd;
                    var nestedTableDrivenInline =
                        nestedMeasuredUsedInline + nestedBorderPaddingInline;
                    if (nestedTableDrivenInline > childBorderBoxInlineSize)
                    {
                        childBorderBoxInlineSize = nestedTableDrivenInline;
                    }
                }
            }

            // Per Phase 3 Task 14 cycle 1 hardening (Findings 1 + 2) —
            // nested multicol wrapper pre-measure. Mirrors the outer
            // dispatch path's pre-measure: grow childBorderBoxBlockSize
            // (the wrapper's painted block extent) to fit the columnized
            // content. Without this:
            //   (a) for height:auto multicol nested inside the recursion
            //       the wrapper is painted at ~0 px (CSS height = 0);
            //   (b) for explicit-height multicol whose columns overflow,
            //       the wrapper's painted size doesn't reflect the
            //       actual content extent.
            if (IsMulticolContainer(child) && _capturedFragmentainer is not null)
            {
                // Per Phase 3 Task 14 cycle 2 hardening (Finding #5) —
                // shared with the outer / recursion emit + outer
                // premeasure sites via MulticolGeometryHelper. Mirrors
                // the outer dispatch path's pre-measure: grow
                // childBorderBoxBlockSize (the wrapper's painted block
                // extent) to fit the columnized content.
                var nMcGeom = MulticolGeometryHelper.ComputeContentGeometry(
                    multicolBox: child,
                    borderBoxInlineSize: childBorderBoxInlineSize,
                    borderBoxBlockSize: childBorderBoxBlockSize,
                    borderBoxInlineOffset: childInlineOffset,
                    borderBoxBlockOffset: childBlockOffset,
                    fragmentainer: _capturedFragmentainer,
                    isHeightAuto: IsHeightAuto(child));
                var nMcMeasureLayout = new LayoutContext(_capturedFragmentainer)
                {
                    Diagnostics = _diagnostics,
                };
                var nMcColumnExtent = PreMeasureMulticolColumnExtent(
                    child,
                    contentInlineSize: nMcGeom.ContentInlineSize,
                    contentBlockSize: nMcGeom.ContentBlockSize,
                    fragmentainer: _capturedFragmentainer,
                    layout: ref nMcMeasureLayout,
                    cancellationToken: cancellationToken);
                var nMcDriven = nMcColumnExtent + nMcGeom.BorderPaddingBlockSum;
                if (nMcDriven > childBorderBoxBlockSize)
                {
                    childBorderBoxBlockSize = nMcDriven;
                }
                if (nMcDriven > childEffectiveBlockSize)
                {
                    childEffectiveBlockSize = nMcDriven;
                }
            }

            // Per Phase 3 Task 16 cycle 4b — set by the
            // paginatable-flex clamp inside the IsFlexContainer
            // pre-grow block below + read by the recursive dispatch
            // block further down to flip <c>allowPagination</c>
            // ON when the wrapper would otherwise overflow the
            // remaining fragmentainer space. Declared OUTSIDE the
            // pre-grow block because the dispatch block is a
            // separate <c>if (IsFlexContainer(child))</c> sibling at
            // the same loop-iteration scope.
            bool paginateFlexForChild = false;
            // Dual-input pagination (mirrors the grid page-budget): for a
            // paginating COLUMN flex (item split) OR ROW-nowrap flex (intra-item
            // content split) the wrapper paints at the clamped page-remaining
            // size, but the FlexLayouter must SIZE content against the NATURAL
            // extent (else flex-shrink / line cross-size fights the page). Capture
            // the pre-clamp authored border-box block size + a flag so the
            // dispatch passes natural-as-contentBlockSize + clamped-as-budget.
            double nestedFlexAuthoredBorderBoxBlockSize = 0;
            bool nestedFlexDualInputPaginating = false;

            // Per Phase 3 Task 15 L3 post-PR-#63 hardening F#1 — nested
            // flex container wrapper pre-measure. Mirrors the outer
            // dispatch path's pre-measure: grow childBorderBoxBlockSize
            // (the wrapper's painted block extent) to fit the flex
            // cross-extent (= max(item natural cross-size) for L3 single-
            // line row direction). Without this:
            //   (a) height-auto nested flex containers inherit the
            //       block-flow stacking sum from MeasureSubtreeVisualBlockExtent
            //       (= ~225 for items 50/100/75) into childEffectiveBlockSize
            //       — over-reserving space + pushing siblings down;
            //   (b) the wrapper fragment painted at line 2767 has
            //       BlockSize ~0 (the no-explicit-height-no-padding case),
            //       visually clipping the items.
            // Sub-cycle L4+ scope: outer cross-size (item margins +
            // borders + padding) + baseline alignment + multi-line wrap
            // — move in lockstep with FlexLayouter's placement math.
            if (IsFlexContainer(child))
            {
                // Per Phase 3 Task 15 L4 — the row premeasure applies
                // max(item cross-size) + a subtree clamp; column
                // direction needs the SUM of items' main-axis sizes.
                // Per Phase 3 Task 15 L4 post-PR-#64 review hardening
                // F#1 the column branch now wires the dedicated
                // PreMeasureFlexMainExtent helper so the nested flex
                // wrapper paints at the spec-correct main extent (the
                // pre-F#1 fall-through to the natural block-flow
                // stacking sum produced by the recursive walk happened
                // to be right for column direction BUT the subtree
                // clamp's row-only gate left childEffectiveBlockSize
                // unchanged + the FlexLayouter still read the original
                // childBorderBoxBlockSize via ConfigureEmission). The
                // F#1 fix surfaces the main extent at premeasure time
                // so both axes' contracts are explicit.
                //
                // The cross-axis subtree clamp + post-grow re-clamp
                // pattern still applies in BOTH directions: the
                // recursive walk over-measures content that has its
                // own layout discipline.
                //
                // Per F#4 — helpers now take cancellationToken.
                //
                // Per Phase 3 Task 15 L6 — mirror the outer-dispatch
                // wrap branch: row + wrap uses sum(line cross-extents)
                // via PreMeasureFlexMultiLineCrossExtent; column + wrap
                // falls back to the single-column sum (wrap requires
                // an explicit main constraint; auto block-size in
                // column direction can't wrap in a single pass).
                var childFlexDirection = child.Style.ReadFlexDirection();
                var childFlexWrap = child.Style.ReadFlexWrap();
                var childIsWrapping = childFlexWrap.IsFlexWrapping();
                var nFlexBorderPaddingBlock =
                    borderStart + paddingStart + paddingEnd + borderEnd;
                // Per Phase 3 Task 15 L6 — same skip rule as the outer
                // dispatch site: column + wrap + explicit block-size
                // mustn't grow the wrapper past the declared height
                // (that would re-budget the FlexLayouter's
                // containerMainSize past the wrap threshold).
                var nSkipMainExtentGrow =
                    childFlexDirection.IsFlexColumnDirection()
                    && childIsWrapping
                    && !IsHeightAuto(child);
                double nFlexAxisExtent;
                // Per Phase 3 Task 15 L6 post-PR-#66 review F#5 —
                // short-circuit the pre-measure when its result will
                // be skipped (= the column+wrap+explicit-height path
                // would zero out nFlexAxisExtent below anyway). Pre-
                // fix the column branch called
                // PreMeasureFlexMainExtent (= O(N) item walk) only to
                // throw the result away. Same change as the outer
                // dispatch site.
                if (nSkipMainExtentGrow)
                {
                    nFlexAxisExtent = 0;
                }
                else if (childFlexDirection.IsFlexColumnDirection())
                {
                    nFlexAxisExtent = PreMeasureFlexMainExtent(
                        child, childBorderBoxInlineSize, cancellationToken);
                }
                else if (childIsWrapping)
                {
                    // Row + wrap — same line-budget derivation as the
                    // outer dispatch site.
                    //
                    // Per Phase 3 Task 16 cycle 4c (P3 #8 from
                    // PR-#79) — the line-packing algorithm has been
                    // extracted to <see cref="FlexLinePacker"/>.
                    // <see cref="PreMeasureFlexMultiLineCrossExtent"/>
                    // (called immediately below) delegates to
                    // <see cref="FlexLinePacker.SumCrossExtent"/> (=
                    // streaming variant per PR-#84 P2 #1) + shares
                    // line boundaries with
                    // <see cref="FlexLayouter.PackLines"/> by
                    // construction.
                    var inlineBorderStart =
                        child.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth);
                    var inlineBorderEnd =
                        child.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth);
                    var inlinePaddingStart =
                        child.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft);
                    var inlinePaddingEnd =
                        child.Style.ReadLengthPxOrZero(PropertyId.PaddingRight);
                    var nContentInlineSize = Math.Max(0,
                        childBorderBoxInlineSize
                        - inlineBorderStart - inlinePaddingStart
                        - inlinePaddingEnd - inlineBorderEnd);
                    nFlexAxisExtent = PreMeasureFlexMultiLineCrossExtent(
                        child, childFlexDirection,
                        containerMainSize: nContentInlineSize,
                        cancellationToken);
                }
                else
                {
                    nFlexAxisExtent = PreMeasureFlexCrossExtent(
                        child, childBorderBoxInlineSize, cancellationToken);
                }
                var nFlexDriven = nFlexAxisExtent + nFlexBorderPaddingBlock;
                if (nFlexDriven > childBorderBoxBlockSize)
                {
                    childBorderBoxBlockSize = nFlexDriven;
                }
                // Clamp childEffectiveBlockSize back to the post-grow
                // childBorderBoxBlockSize when the subtree extent
                // (which stacked the items as block-flow) exceeded it.
                // Mirrors the outer-loop's `subtreeBlockExtent` clamp
                // for flex containers — the recursive walk over-measures
                // children with their own layout discipline.
                if (childEffectiveBlockSize > childBorderBoxBlockSize)
                {
                    childEffectiveBlockSize = childBorderBoxBlockSize;
                }
                // After the clamp, ensure the grown wrapper size still
                // dominates if the spec'd extent now exceeds the
                // (clamped) childEffectiveBlockSize.
                if (nFlexDriven > childEffectiveBlockSize)
                {
                    childEffectiveBlockSize = nFlexDriven;
                }

                // Per Phase 3 Task 16 cycle 4b — paginatable-flex extent
                // clamp at the recursive site. When the wrapper's grown
                // natural extent overflows the remaining fragmentainer
                // space AND the container is eligible per
                // <see cref="IsPaginatableFlex"/>, clamp the wrapper to the
                // page-remaining block + flip the dispatch's
                // <c>allowPagination</c> flag ON below. For ROW + wrap the
                // FlexLayouter packs lines up to the clamped budget +
                // returns a FlexContinuation for the overflow LINES; for
                // COLUMN + nowrap (the non-block-pagination arc) it splits
                // between ITEMS via <c>FlexContinuation.ItemIndex</c> (CSS
                // Flexbox L1 §10 fragmentation + CSS Fragmentation L3 §4.4
                // progress rule). The wrapper fragment emitted below paints
                // at the clamped size; the resume page's BlockLayouter
                // dispatches the FlexContinuation back via the cycle-3
                // propagation chain.
                //
                // <para><b>Dual-input.</b> A paginating COLUMN flex (item split)
                // OR ROW-nowrap flex (intra-item content split) sets
                // <c>nestedFlexDualInputPaginating</c> here (capturing the
                // pre-clamp authored block size) so the dispatch below sizes
                // content against the NATURAL extent + cuts against the
                // clamped page budget — without that split, flex-shrink would
                // collapse items to the clamped main-size instead of
                // paginating. STILL EXCLUDED (atomic, documented in
                // <c>docs/deferrals.md</c>): column + wrap (lines stack on
                // the inline axis — not a fragment boundary) + column-reverse
                // / row wrap-reverse (cross-axis SWAP origin derives from the
                // unfragmented size + needs per-fragment re-derivation).</para>
                //
                // <para><b>Why this site, not pre-break-check.</b> The
                // recursive walk doesn't consult the resolver — there's
                // no break-check to pre-empt at this depth. The clamp +
                // pagination here is the EQUIVALENT of the outer
                // forced-overflow re-route (added below in the same
                // cycle): both intercept the moment where the wrapper
                // would otherwise paint at its natural extent + overflow
                // the page.</para>
                // PR-#182 review P1 — suppressed for nested content (a discarded
                // PageComplete would drop the deferred flex items).
                // Per Phase 3 Task 19 — also suppressed inside a FLOAT subtree: the
                // flex emits atomically (full natural extent, no page clamp) so its
                // items are lossless rather than truncated past a discarded split.
                // Corpus-fidelity (03 footer overlap) — is this child RESUMING a prior DUAL-INPUT flex
                // split (column item split OR row-nowrap content split)? Such a child MUST re-enter the
                // pagination path even when the (fresh) resume page has room for it, so FlexLayouter
                // honors the incoming continuation's resume-cut instead of re-emitting the WHOLE item.
                // Pre-fix, the would-overflow gate below stayed OFF on a fresh page → allowPagination
                // false → the resume-cut was ignored → the item re-rendered wholly (duplicated content)
                // AND the wrapper wasn't resized, so the sibling cursor under-advanced and the trailing
                // block (e.g. `.note`) overlapped the flex content. The row-WRAP line-split resume is a
                // separate residual (no dual-input resize consumer) and is left unchanged here.
                var isDualInputFlexResume = incomingBlockChain is not null
                    && childIdx == incomingBlockChain.ResumeAtChild
                    && incomingBlockChain.LayouterState is FlexContinuation
                    && (childFlexDirection.IsFlexColumnDirection() || !childIsWrapping);
                if (IsPaginatableFlex(child)
                    && !_disableFlexPagination
                    && !_inAtomicFloatSubtree
                    && _capturedFragmentainer is not null)
                {
                    var pageRemainingForFlex =
                        _capturedFragmentainer.BlockSize - childBlockOffset;
                    // Eligibility: the wrapper would otherwise overflow
                    // (childBorderBoxBlockSize > remaining) AND the
                    // remaining space exceeds the wrapper's own chrome
                    // (borders + padding) — i.e., there's positive
                    // content room. Pathological case (remaining <=
                    // chrome) falls through to the natural-extent emit;
                    // FlexLayouter would receive a non-positive content
                    // budget which it rejects via
                    // ConfigureEmission's argument validation.
                    // OR — the child is resuming a dual-input split (above): activate pagination so the
                    // resume-cut is honored, even though the fresh page doesn't overflow.
                    var flexWouldOverflow = pageRemainingForFlex > 0
                        && pageRemainingForFlex < childBorderBoxBlockSize;
                    if (pageRemainingForFlex > nFlexBorderPaddingBlock
                        && (flexWouldOverflow || isDualInputFlexResume))
                    {
                        // Column nowrap (item split) AND row nowrap (intra-item
                        // content split): remember the natural (pre-clamp)
                        // border-box block size so the dispatch sizes content
                        // against it + cuts against the clamped page budget.
                        // Row-WRAP keeps the single-input clamp (the line-split
                        // fits lines to the budget directly).
                        var childIsColumnDir = childFlexDirection.IsFlexColumnDirection();
                        if (childIsColumnDir || !childIsWrapping)
                        {
                            nestedFlexAuthoredBorderBoxBlockSize = childBorderBoxBlockSize;
                            nestedFlexDualInputPaginating = true;
                        }
                        childBorderBoxBlockSize = pageRemainingForFlex;
                        childEffectiveBlockSize = pageRemainingForFlex;
                        paginateFlexForChild = true;
                    }
                }
            }

            // Per Phase 3 Task 17 cycle 5c.2d — recursive-site
            // paginatable-grid flag. Mirrors
            // <c>paginateGridForOuterChild</c> at the outer-site
            // AttemptLayout. Flipped ON by the paginatable-grid
            // clamp below; consumed by the grid-dispatch branch
            // that passes
            // <c>allowPagination: paginateGridForChild</c> +
            // <c>incomingContinuation</c>.
            bool paginateGridForChild = false;

            // Per Phase 3 Task 17 cycle 1 post-PR-#92 review F2 —
            // nested grid container wrapper pre-measure. Mirrors the
            // outer-dispatch grid pre-measure + the
            // <see cref="IsMulticolContainer"/> / <see cref="IsFlexContainer"/>
            // recursive pre-measures above. Without this, an
            // auto-height grid reached via the recursive walk paints
            // at its chrome-only natural extent; following block-flow
            // siblings then visually overlap the grid rows.
            if (IsGridContainer(child) && IsHeightAuto(child))
            {
                // PR-#181 review P1/P2 — real content inline size (auto-repeat
                // column count) + cancellation token, same as the outer site.
                var nPreGridGeom = GridGeometryHelper.ComputeContentGeometry(
                    gridBox: child,
                    borderBoxInlineSize: childBorderBoxInlineSize,
                    borderBoxBlockSize: childBorderBoxBlockSize,
                    borderBoxInlineOffset: childInlineOffset,
                    borderBoxBlockOffset: childBlockOffset);
                var nGridRowExtent = PreMeasureGridRowExtent(
                    child, nPreGridGeom.ContentInlineSize, cancellationToken);
                if (nGridRowExtent > 0)
                {
                    var nGridBorderPaddingBlock =
                        borderStart + paddingStart + paddingEnd + borderEnd;
                    var nGridDriven =
                        nGridRowExtent + nGridBorderPaddingBlock;
                    if (nGridDriven > childBorderBoxBlockSize)
                    {
                        childBorderBoxBlockSize = nGridDriven;
                    }
                    if (nGridDriven > childEffectiveBlockSize)
                    {
                        childEffectiveBlockSize = nGridDriven;
                    }
                }
            }

            // Per Phase 3 Task 17 cycle 5c.2d + cycle 5c.3 — recursive-
            // site paginatable-grid extent clamp + gate-flip. Mirrors
            // the outer-site clamp at line ~1796. Per cycle 5c.3 the
            // <c>IsHeightAuto(child)</c> gate is REMOVED — the dispatch
            // below passes the AUTHORED <c>childBorderBoxBlockSize</c>
            // captured here as the grid sizing input + the CLAMPED
            // value as the page budget, so explicit-height grids
            // resolve row geometry against authored height (= fr /
            // definite rows distribute correctly) while paginating
            // against the page-remaining budget. Auto-height grids
            // see authored == clamped + the dual-input mechanism
            // degenerates to legacy behavior.
            var recursiveAuthoredBorderBoxBlockSize = childBorderBoxBlockSize;
            // Per Phase 3 Task 19 — a grid inside a FLOAT subtree emits atomically
            // (full natural extent, no page clamp) so its rows are lossless rather than
            // truncated past a discarded split (floats don't fragment yet).
            if (IsPaginatableGrid(child)
                && !_disableGridPagination
                && !_inAtomicFloatSubtree
                && _capturedFragmentainer is not null)
            {
                var pageRemainingForGrid =
                    _capturedFragmentainer.BlockSize - childBlockOffset;
                var nGridBorderPaddingBlock =
                    borderStart + paddingStart + paddingEnd + borderEnd;
                if (pageRemainingForGrid > 0
                    && pageRemainingForGrid < childBorderBoxBlockSize
                    && pageRemainingForGrid > nGridBorderPaddingBlock)
                {
                    childBorderBoxBlockSize = pageRemainingForGrid;
                    childEffectiveBlockSize = pageRemainingForGrid;
                    paginateGridForChild = true;
                }
            }

            // Per Phase 3 Task 17 cycle 5c.2d post-PR-#102 review
            // P1#1 — recursive-site F1 pre-dispatch row-fit defer.
            // Without this, a recursive grid whose first remaining
            // row exceeds the page-remaining content area (= e.g.
            // body's first child is a 200px block + body's second
            // child is a grid with 100px rows + page-remaining
            // after the 200px sibling is 50px) would force-emit
            // the first row past the page edge instead of cleanly
            // deferring to the next page.
            //
            // <para>The recursive site does NOT have access to the
            // attempt's strategy (= the recursion doesn't propagate
            // it). Mirrors the outer-site F1 but uses
            // <c>childBlockOffset &gt; 0</c> as the productivity
            // guard (= deferral helps when the grid isn't at the
            // top of the page; on a fresh page the parent re-enters
            // the recursion with childBlockOffset back at 0 + F1
            // doesn't fire → atomic dispatch + forced-overflow if
            // needed). Doesn't use <c>emittedThisAttempt</c>
            // because that local belongs to the outer
            // <see cref="AttemptLayout"/> + isn't accessible from
            // recursion.</para>
            //
            // <para>When F1 fires, the recursion returns a
            // <c>BlockContinuation(ResumeAtChild=childIdx,
            // LayouterState=GridContinuation(RowIndex=startRow,
            // Cache=incomingCache, EmittedBlockExtent=0))</c>
            // without emitting the wrapper. The outer
            // <see cref="AttemptLayout"/> wraps this into a
            // PageComplete with the outer's
            // <c>ConsumedBlockSize</c> accounting (= mirrors the
            // existing chain-propagation pattern at line ~3254
            // for any nested deferral).</para>
            if (IsPaginatableGrid(child)
                && !_disableGridPagination
                && paginateGridForChild
                && _capturedFragmentainer is not null
                && childBlockOffset > 0)
            {
                int recursiveProbeStartRow = 0;
                GridContinuation? recursiveIncomingForProbe = null;
                if (incomingBlockChain is not null
                    && childIdx == incomingBlockChain.ResumeAtChild
                    && incomingBlockChain.LayouterState is GridContinuation recProbeGrid)
                {
                    recursiveProbeStartRow = recProbeGrid.RowIndex;
                    recursiveIncomingForProbe = recProbeGrid;
                }

                var recursiveGridChrome =
                    borderStart + paddingStart + paddingEnd + borderEnd;
                var recursivePageRemainingForGridContent =
                    _capturedFragmentainer.BlockSize
                    - childBlockOffset
                    - recursiveGridChrome;
                // Next-page remaining: a fresh page where this
                // recursion's parent starts at the top. Approximated
                // as <c>fragmentainer.BlockSize - chrome</c>; the
                // parent's own offset on the next page is unknown
                // from this depth, so use the upper bound. F1's
                // predicate is `row would fit if the next page
                // gave us a fresh allocation` — the bound is safe
                // because if the row doesn't fit even this
                // upper-bound budget, deferral can't help → F1
                // doesn't fire + atomic dispatch handles overflow.
                var recursiveNextPageRemainingForGridContent =
                    _capturedFragmentainer.BlockSize - recursiveGridChrome;

                if (recursivePageRemainingForGridContent > 0)
                {
                    // Per cycle 5c.2a P1#2 + cycle 5c.3 — the probe
                    // uses the actual grid content geometry the
                    // dispatch will use as its ROW-SIZING input;
                    // mirrors the outer site. For explicit-height
                    // grids the AUTHORED (= pre-clamp) borderBox
                    // feeds the probe so row geometry matches what
                    // the dispatch sees. Auto-height grids see
                    // authored == clamped.
                    var recursiveProbeGridGeom =
                        GridGeometryHelper.ComputeContentGeometry(
                            gridBox: child,
                            borderBoxInlineSize: childBorderBoxInlineSize,
                            borderBoxBlockSize: recursiveAuthoredBorderBoxBlockSize,
                            borderBoxInlineOffset: childInlineOffset,
                            borderBoxBlockOffset: childBlockOffset);
                    var recursiveFirstRowExtent = PreMeasureGridRowExtentAt(
                        gridBox: child,
                        rowIndex: recursiveProbeStartRow,
                        contentInlineSize: recursiveProbeGridGeom.ContentInlineSize,
                        contentBlockSize: recursiveProbeGridGeom.ContentBlockSize,
                        incomingCache: recursiveIncomingForProbe?.Cache,
                        cancellationToken: cancellationToken);
                    if (recursiveFirstRowExtent
                            > recursivePageRemainingForGridContent + GridSizing.SizeEpsilonPublic
                        && recursiveFirstRowExtent
                            <= recursiveNextPageRemainingForGridContent + GridSizing.SizeEpsilonPublic)
                    {
                        // Defer without emitting the wrapper. The
                        // recursion's return propagates up via
                        // chained BlockContinuation; the outer
                        // AttemptLayout wraps into PageComplete
                        // with proper ConsumedBlockSize accounting.
                        // If the incoming chain already consumed
                        // a GridContinuation at this child, null
                        // it out (= the chain-consumption pattern
                        // shared with multicol / flex).
                        if (recursiveIncomingForProbe is not null)
                        {
                            incomingBlockChain = null;
                        }
                        return new BlockContinuation(
                            ResumeAtChild: childIdx,
                            ConsumedBlockSize: 0,
                            LayouterState: new GridContinuation(
                                RowIndex: recursiveProbeStartRow,
                                Cache: recursiveIncomingForProbe?.Cache,
                                EmittedBlockExtent: 0));
                    }
                }
            }

            // Per Phase 3 Task 17 cycle 5c.2d — capture sink cursor
            // BEFORE wrapper emit. Mirrors the outer-site pattern
            // at line ~2596: the F2 wrapper-resize consumer (below
            // at the grid dispatch branch) uses this index to
            // mutate the wrapper's BlockSize in place via
            // <see cref="IBlockFragmentSink.UpdateFragmentBlockSize"/>
            // after the inner GridLayouter reports its
            // <c>LastEmittedBlockExtent</c>. Z-order preserved.
            var recursiveWrapperCursor = _sink.Cursor;

            // Fragment records the BORDER box (not subtree extent) —
            // the subtree extent is for cursor advance only. Per
            // cycle 2b, the painter sees the border box; descendants
            // that overflow are emitted as their own fragments at
            // their own offsets. EXCEPTION (CSS 2.1 §10.6.3,
            // `auto-height-emit-vs-pagination`): an auto-height block-flow
            // container's painted border box spans its children — the
            // chrome-only childBorderBoxBlockSize stays the cursor/break input,
            // this only grows the painter's rectangle, capped to the page.
            var emittedChildBlockSize = ResolveAutoHeightEmittedBlockSize(
                child, childBorderBoxBlockSize, childEffectiveBlockSize,
                childBlockOffset, _capturedFragmentainer);
            _sink.Emit(new BoxFragment(
                Box: child,
                InlineOffset: childInlineOffset,
                BlockOffset: childBlockOffset,
                InlineSize: childBorderBoxInlineSize,
                BlockSize: emittedChildBlockSize));
            // Per Phase 3 Task 19 cycle 2a — record positioned-CB
            // establishers reached via the recursive walk so abspos
            // descendants anchor to their nearest positioned ancestor.
            RecordPositionedBoxGeometry(
                child, childInlineOffset, childBlockOffset,
                childBorderBoxInlineSize, childBorderBoxBlockSize);

            // Per Phase 3 Task 14 cycle 1 — nested multicol dispatch.
            // A nested block-flow descendant with `column-count: N`
            // (N >= 2) dispatches to MulticolLayouter for per-column
            // emission INSIDE its border box. Skip the
            // EmitBlockSubtreeRecursive call below for this child
            // (the multicol layouter owns the inner content
            // emission). Mirrors the outer-loop dispatch above for
            // the depth-0 case.
            if (IsMulticolContainer(child))
            {
                // Per Phase 3 Task 14 cycle 2 hardening (Finding #5) —
                // shared with the outer / recursion premeasure + outer
                // emit sites via MulticolGeometryHelper. Mirrors the
                // outer-loop dispatch above for the depth-0 case.
                // The auto-height path's remaining-space derivation
                // requires the captured fragmentainer; when null
                // (recursion entered outside the main outer loop), the
                // helper falls through to the explicit-height path —
                // matching the legacy `&& _capturedFragmentainer is
                // not null` guard.
                var nestedMulticolGeom = MulticolGeometryHelper.ComputeContentGeometry(
                    multicolBox: child,
                    borderBoxInlineSize: childBorderBoxInlineSize,
                    borderBoxBlockSize: childBorderBoxBlockSize,
                    borderBoxInlineOffset: childInlineOffset,
                    borderBoxBlockOffset: childBlockOffset,
                    fragmentainer: _capturedFragmentainer,
                    isHeightAuto: IsHeightAuto(child));
                var nestedMulticolContentInlineSize = nestedMulticolGeom.ContentInlineSize;
                var nestedMulticolContentBlockSize = nestedMulticolGeom.ContentBlockSize;
                var nestedMulticolContentInlineOffset = nestedMulticolGeom.ContentInlineOffset;
                var nestedMulticolContentBlockOffset = nestedMulticolGeom.ContentBlockOffset;

                // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) —
                // the recursion-chain protocol replaces the depth==1
                // gate. When the incoming continuation's chain reaches
                // a MulticolContinuation leaf AND the current child
                // matches its ResumeAtChild, use that continuation.
                MulticolContinuation? incomingForThisMulticol = null;
                if (incomingBlockChain is not null
                    && childIdx == incomingBlockChain.ResumeAtChild
                    && incomingBlockChain.LayouterState is MulticolContinuation incomingMcLeaf)
                {
                    incomingForThisMulticol = incomingMcLeaf;
                    incomingBlockChain = null;
                }

                using var nestedMulticolLayouter = new MulticolLayouter(
                    rootBox: child,
                    sink: _sink,
                    incomingContinuation: incomingForThisMulticol,
                    diagnostics: _diagnostics,
                    shaperResolver: _shaperResolver);
                nestedMulticolLayouter.ConfigureEmission(
                    contentInlineOffset: nestedMulticolContentInlineOffset,
                    contentBlockOffset: nestedMulticolContentBlockOffset,
                    contentInlineSize: nestedMulticolContentInlineSize,
                    contentBlockSize: nestedMulticolContentBlockSize);
                using var nestedMulticolResolver = new BreakResolver();
                // The recursion doesn't have a ref LayoutContext;
                // synthesize a transient one carrying the
                // constructor's diagnostic sink. Mirrors the nested-
                // table dispatch above (Phase 3 Task 12 sub-cycle 2
                // Finding 1).
                LayoutAttemptResult? nestedMulticolResult = null;
                if (_capturedFragmentainer is not null)
                {
                    var nestedMulticolLayoutCtx = new LayoutContext(_capturedFragmentainer)
                    {
                        Diagnostics = _diagnostics,
                        GridMeasureCache = _gridMeasureCache,   // measurement-cache cycle
                        TableMeasureCache = _tableMeasureCache, // multi-page-allocation-churn
                    };
                    nestedMulticolResult = nestedMulticolLayouter.AttemptLayout(
                        _capturedFragmentainer,
                        ref nestedMulticolLayoutCtx,
                        nestedMulticolResolver,
                        LayoutAttemptStrategy.LastResort,
                        cancellationToken);
                }

                // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) —
                // multi-level recursion-continuation propagation. The
                // cycle-1 depth==1-only gate is lifted: ANY nested
                // multicol that returns PageComplete propagates UP via
                // the return value as a chained BlockContinuation
                // (LayouterState = MulticolContinuation at the leaf).
                // The outer AttemptLayout dispatch wraps the chain
                // into the final outer BlockContinuation.
                if (nestedMulticolResult is { Outcome: LayoutAttemptOutcome.PageComplete }
                    && nestedMulticolResult.Value.Continuation is MulticolContinuation mcCont)
                {
                    return new BlockContinuation(
                        ResumeAtChild: childIdx,
                        ConsumedBlockSize: 0,
                        LayouterState: mcCont);
                }

                // Advance the cursor + bookkeeping; skip the
                // EmitBlockSubtreeRecursive below. Mirrors the
                // standard cursor advance at the bottom of the loop
                // (childCursor + topShift + childEffectiveBlockSize
                // + marginEnd).
                childCursor = childCursor + topShift + childEffectiveBlockSize + marginEnd;
                prevMarginEnd = marginEnd;
                hasPrior = true;
                continue;
            }

            // Per Phase 3 Task 15 cycle 1 (Hello World) → Task 16
            // cycle 3 → cycle 4b — nested flex container dispatch. A
            // nested block-flow descendant with
            // BoxKind.FlexContainer / InlineFlexContainer
            // (= display: flex / inline-flex) dispatches to
            // FlexLayouter for per-item emission INSIDE its border
            // box. Skip the EmitBlockSubtreeRecursive call below
            // for this child (the flex layouter owns the inner
            // content emission).
            //
            // Cycle 4b activation status: the outbound propagation
            // path (= capture LayoutAttemptResult + return
            // BlockContinuation(LayouterState=FlexContinuation)) is
            // ACTIVE because the cycle-4b paginatable-flex extent
            // clamp inside the pre-grow above can flip
            // <c>allowPagination</c> ON at this site. The inbound
            // recursive chain-walk is wired below (post-PR-#83
            // review P1 #1) so resume-page recursion forwards the
            // incoming FlexContinuation leaf to FlexLayouter +
            // multi-page flex containers actually round-trip
            // (page 1 → continuation → page 2 emits remaining
            // lines → AllDone).
            if (IsFlexContainer(child))
            {
                // Per Phase 3 Task 16 cycle 4d (PR-#82 review #2) —
                // content-box geometry from the shared helper.
                var nestedFlexGeom = FlexGeometryHelper.ComputeContentGeometry(
                    flexBox: child,
                    borderBoxInlineSize: childBorderBoxInlineSize,
                    borderBoxBlockSize: childBorderBoxBlockSize,
                    borderBoxInlineOffset: childInlineOffset,
                    borderBoxBlockOffset: childBlockOffset);
                var nestedFlexContentInlineSize = nestedFlexGeom.ContentInlineSize;
                var nestedFlexContentBlockSize = nestedFlexGeom.ContentBlockSize;
                var nestedFlexContentInlineOffset = nestedFlexGeom.ContentInlineOffset;
                var nestedFlexContentBlockOffset = nestedFlexGeom.ContentBlockOffset;
                // Column dual-input: size against the natural (authored) content
                // block extent, split against the clamped page budget. The
                // wrapper geometry above used the CLAMPED childBorderBoxBlockSize
                // (so the wrapper paints clamped) — recompute the AUTHORED content
                // block size for the flex sizing input + use the clamped content
                // block size as the page budget. Row-wrap leaves budget null.
                double? nestedFlexPageBudget = null;
                if (nestedFlexDualInputPaginating)
                {
                    var authoredFlexGeom = FlexGeometryHelper.ComputeContentGeometry(
                        flexBox: child,
                        borderBoxInlineSize: childBorderBoxInlineSize,
                        borderBoxBlockSize: nestedFlexAuthoredBorderBoxBlockSize,
                        borderBoxInlineOffset: childInlineOffset,
                        borderBoxBlockOffset: childBlockOffset);
                    nestedFlexPageBudget = nestedFlexContentBlockSize;       // clamped = cut-off
                    nestedFlexContentBlockSize = authoredFlexGeom.ContentBlockSize; // natural = sizing
                }

                // Per Phase 3 Task 16 cycle 4b post-PR-#83 review
                // P1 #1 — inbound recursive FlexContinuation
                // chain-walk. Mirrors the multicol pattern at line
                // ~3357 + the table pattern at line ~2983: when the
                // incoming chain has reached its FlexContinuation
                // leaf AND the current child is the deferred-at
                // target, extract the FlexContinuation + null out
                // <c>incomingBlockChain</c> (one-shot consume).
                // Without this, the resume page restarts at line
                // index 0 = duplicate emission of page-1 lines.
                //
                // Chain shape recap: the outer AttemptLayout entry
                // unwraps the top BlockContinuation; the recursive
                // walk passes the inner chain down via
                // <c>incomingContinuation</c>. At each level the
                // BlockContinuation peel at line ~3579 forwards
                // deeper layers; the leaf level (this branch) sees
                // the FlexContinuation directly.
                FlexContinuation? nestedFlexContinuationForChild = null;
                if (incomingBlockChain is not null
                    && childIdx == incomingBlockChain.ResumeAtChild
                    && incomingBlockChain.LayouterState is FlexContinuation incomingFlexLeaf)
                {
                    nestedFlexContinuationForChild = incomingFlexLeaf;
                    incomingBlockChain = null;
                }

                // Per Phase 3 Task 16 cycle 4a (PR #82, following
                // the PR #81 execution order) — route through the
                // shared `DispatchFlexInner` helper. The recursive
                // path needs its own LayoutContext (= the outer
                // AttemptLayout's `ref layout` isn't in scope
                // here); built from `_capturedFragmentainer` per
                // the pre-extraction logic.
                LayoutAttemptResult? nestedFlexResult = null;
                var nestedFlexLastEmittedExtent = 0.0;
                if (_capturedFragmentainer is not null)
                {
                    var nestedFlexLayoutCtx = new LayoutContext(_capturedFragmentainer)
                    {
                        Diagnostics = _diagnostics,
                        GridMeasureCache = _gridMeasureCache,   // measurement-cache cycle
                        TableMeasureCache = _tableMeasureCache, // multi-page-allocation-churn
                    };
                    nestedFlexResult = DispatchFlexInner(
                        flexBox: child,
                        contentInlineOffset: nestedFlexContentInlineOffset,
                        contentBlockOffset: nestedFlexContentBlockOffset,
                        contentInlineSize: nestedFlexContentInlineSize,
                        contentBlockSize: nestedFlexContentBlockSize,
                        incomingContinuation: nestedFlexContinuationForChild,
                        // Per Phase 3 Task 16 cycle 4b — gate flipped ON
                        // by the paginatable-flex extent clamp above
                        // when (a) the container is row + wrap +
                        // non-wrap-reverse, (b) its grown natural extent
                        // overflows the remaining fragmentainer space,
                        // AND (c) the remaining space exceeds the
                        // wrapper's chrome (so positive content-block
                        // budget is available). Otherwise stays OFF —
                        // FlexLayouter emits everything atomically as in
                        // L1-L17 (the pre-cycle-4b behavior).
                        allowPagination: paginateFlexForChild,
                        fragmentainer: _capturedFragmentainer,
                        layout: ref nestedFlexLayoutCtx,
                        cancellationToken: cancellationToken,
                        lastEmittedBlockExtent: out nestedFlexLastEmittedExtent,
                        pageBlockBudget: nestedFlexPageBudget);
                }

                // PR-#180 review P2 — resize a paginating dual-input flex wrapper
                // to its ACTUAL emitted extent (column item split OR row-nowrap
                // content split; mirrors the outer site + the grid recursion F2
                // resize). Recompute childEffectiveBlockSize so the cursor advance
                // below (which DOES run on a final AllDone resume page where
                // siblings follow) uses the emitted content, not the clamped
                // budget — otherwise siblings sit too low + the wrapper paints
                // blank trailing space.
                if (nestedFlexDualInputPaginating)
                {
                    var flexChromeBlock = borderStart + paddingStart + paddingEnd + borderEnd;
                    var resizedFlexWrapperBlockSize = flexChromeBlock + nestedFlexLastEmittedExtent;
                    _sink.UpdateFragmentBlockSize(recursiveWrapperCursor, resizedFlexWrapperBlockSize);
                    childEffectiveBlockSize = resizedFlexWrapperBlockSize;
                }
                else if (IsFlexContainer(child) && IsHeightAuto(child)
                    && !child.Style.ReadFlexDirection().IsFlexColumnDirection()
                    && !child.Style.ReadFlexWrap().IsFlexWrapping())
                {
                    // Corpus-fidelity (03 itinerary footer overlap) — an AUTO-height ROW flex's used cross
                    // (block) size is its content cross extent, but FlexLinePacker sizes lines from the
                    // items' DECLARED cross only (0 for auto-height items), so the wrapper's resolved
                    // border box is chrome-only. `nestedFlexLastEmittedExtent` now carries the real content
                    // cross (FlexLayouter's `autoRowContentCross`); resize the wrapper + advance the cursor
                    // by it so a trailing sibling (`.note` after a `.timeline` of flex `.day` rows) lands
                    // below the content instead of overlapping it. Byte-identical when content fits the
                    // resolved box (the guard keeps the larger value only).
                    var flexChromeBlock = borderStart + paddingStart + paddingEnd + borderEnd;
                    var resizedFlexWrapperBlockSize = flexChromeBlock + nestedFlexLastEmittedExtent;
                    if (resizedFlexWrapperBlockSize > childEffectiveBlockSize)
                    {
                        _sink.UpdateFragmentBlockSize(recursiveWrapperCursor, resizedFlexWrapperBlockSize);
                        childEffectiveBlockSize = resizedFlexWrapperBlockSize;
                    }
                }

                // Per Task 16 cycle 3 P1 #2 — propagate
                // PageComplete(FlexContinuation) up the recursion
                // chain as a nested BlockContinuation. Mirrors the
                // multicol nested-propagation pattern at line ~3284.
                // The outer BlockLayouter sees the returned
                // BlockContinuation + dispatches it back to the
                // appropriate level. Cycle 4 will flip allowPagination
                // on which makes this branch reachable in production
                // shapes.
                if (nestedFlexResult is { Outcome: LayoutAttemptOutcome.PageComplete }
                    && nestedFlexResult.Value.Continuation is FlexContinuation nestedFlexCont)
                {
                    return new BlockContinuation(
                        ResumeAtChild: childIdx,
                        ConsumedBlockSize: 0,
                        LayouterState: nestedFlexCont);
                }

                // Advance the cursor + bookkeeping; skip the
                // EmitBlockSubtreeRecursive below.
                childCursor = childCursor + topShift + childEffectiveBlockSize + marginEnd;
                prevMarginEnd = marginEnd;
                hasPrior = true;
                continue;
            }

            // Per Phase 3 Task 17 cycle 1 (Hello World) — nested grid
            // container dispatch. Same pattern as the outer-loop site:
            // GridLayouter emits per-item content inside the wrapper.
            //
            // <para>Per Phase 3 Task 17 cycle 5c.2d — paginatable-
            // grid wiring at the recursive site. The recursive
            // walk is where production HTML fixtures
            // (<c>&lt;body&gt;&lt;div class="grid"&gt;...&lt;/div&gt;&lt;/body&gt;</c>)
            // route into grid containers; cycles 5c.2a–5c.2c
            // wired the outer site only. Mirrors the outer-site
            // contract:
            //   * <c>paginateGridForChild</c> (= flipped by the
            //     paginatable-grid clamp above) gates
            //     <c>allowPagination</c>.
            //   * Resume contract: extract any incoming
            //     <c>GridContinuation</c> from
            //     <c>incomingBlockChain</c> via the same chain-
            //     consumption pattern as the multicol / flex
            //     branches above.
            //   * F2 wrapper-resize: when paginatable OR
            //     incoming-continuation, mutate the wrapper's
            //     <c>BlockSize</c> via
            //     <see cref="IBlockFragmentSink.UpdateFragmentBlockSize"/>
            //     + recompute the cursor advance using
            //     <c>topShift + resizedWrapper + marginEnd</c>.
            //   * Propagation: on
            //     <c>PageComplete(GridContinuation)</c>, wrap into
            //     <c>BlockContinuation(ResumeAtChild=childIdx,
            //     LayouterState=GridContinuation)</c> + return up
            //     the recursion chain (= mirrors the flex
            //     propagation at line ~4336).
            // F1 (pre-dispatch row-fit defer) is NOT wired here —
            // the recursive site doesn't receive the attempt's
            // strategy parameter, so the F1 strategy-aware gate
            // can't apply cleanly. Cycle 5c.3+ may thread
            // strategy through the recursion if F1's pre-empt
            // optimization is needed inside nested grids.</para>
            if (IsGridContainer(child) && _capturedFragmentainer is not null)
            {
                // Per Phase 3 Task 17 cycle 5c.3 — same dual-input
                // pattern as the outer site (line ~3039): when the
                // recursive clamp fired AND the authored size is
                // larger than the clamped size, dispatch with the
                // AUTHORED geometry as the row-sizing input + the
                // CLAMPED geometry as the page budget. Auto-height
                // grids (= authored == clamped) get
                // <c>pageBlockBudget</c> = null + dispatch behaves
                // identically to the pre-5c.3 path.
                var nestedGridGeom = GridGeometryHelper.ComputeContentGeometry(
                    gridBox: child,
                    borderBoxInlineSize: childBorderBoxInlineSize,
                    borderBoxBlockSize: childBorderBoxBlockSize,
                    borderBoxInlineOffset: childInlineOffset,
                    borderBoxBlockOffset: childBlockOffset);
                double? nestedGridPageBlockBudget = null;
                double nestedGridSizingContentBlockSize = nestedGridGeom.ContentBlockSize;
                if (paginateGridForChild
                    && recursiveAuthoredBorderBoxBlockSize > childBorderBoxBlockSize)
                {
                    var nestedAuthoredGridGeom = GridGeometryHelper.ComputeContentGeometry(
                        gridBox: child,
                        borderBoxInlineSize: childBorderBoxInlineSize,
                        borderBoxBlockSize: recursiveAuthoredBorderBoxBlockSize,
                        borderBoxInlineOffset: childInlineOffset,
                        borderBoxBlockOffset: childBlockOffset);
                    nestedGridSizingContentBlockSize = nestedAuthoredGridGeom.ContentBlockSize;
                    nestedGridPageBlockBudget = nestedGridGeom.ContentBlockSize;
                }

                var nestedGridLayoutCtx = new LayoutContext(_capturedFragmentainer)
                {
                    Diagnostics = _diagnostics,
                    // Measurement-cache cycle — propagate the per-conversion cache so a
                    // nested (html→body-nested) grid's emission shares with its pre-measure.
                    GridMeasureCache = _gridMeasureCache,
                    TableMeasureCache = _tableMeasureCache,
                };

                // Per cycle 5c.2d — resume contract: extract any
                // incoming GridContinuation from the chain. The
                // outer AttemptLayout unpacks
                // <c>BlockContinuation(LayouterState=
                // BlockContinuation(LayouterState=...))</c>
                // chains; when the chain's leaf is a
                // GridContinuation at this child, route it
                // through.
                GridContinuation? incomingGridForChild = null;
                if (incomingBlockChain is not null
                    && childIdx == incomingBlockChain.ResumeAtChild
                    && incomingBlockChain.LayouterState is GridContinuation incomingGridLeaf)
                {
                    incomingGridForChild = incomingGridLeaf;
                    incomingBlockChain = null;
                }

                var nestedGridResult = DispatchGridInner(
                    gridBox: child,
                    contentInlineOffset: nestedGridGeom.ContentInlineOffset,
                    contentBlockOffset: nestedGridGeom.ContentBlockOffset,
                    contentInlineSize: nestedGridGeom.ContentInlineSize,
                    contentBlockSize: nestedGridSizingContentBlockSize,
                    fragmentainer: _capturedFragmentainer,
                    layout: ref nestedGridLayoutCtx,
                    cancellationToken: cancellationToken,
                    lastEmittedBlockExtent: out var nestedGridLastEmittedBlockExtent,
                    allowPagination: paginateGridForChild,
                    incomingContinuation: incomingGridForChild,
                    pageBlockBudget: nestedGridPageBlockBudget);

                // F2 wrapper-resize + cursor-advance recomputation.
                // Same trigger semantics as the outer site: fire
                // when EITHER the clamp activated (=
                // paginateGridForChild) OR resume continuation
                // present (= incomingGridForChild).
                double cursorAdvanceForNestedGrid =
                    topShift + childEffectiveBlockSize + marginEnd;
                var f2FiresAtRecursiveSite =
                    paginateGridForChild
                    || incomingGridForChild is not null;
                if (f2FiresAtRecursiveSite)
                {
                    var nGridChromeBlock =
                        borderStart + paddingStart + paddingEnd + borderEnd;
                    var resizedRecursiveWrapper =
                        nGridChromeBlock + nestedGridLastEmittedBlockExtent;
                    _sink.UpdateFragmentBlockSize(
                        recursiveWrapperCursor, resizedRecursiveWrapper);
                    cursorAdvanceForNestedGrid =
                        topShift + resizedRecursiveWrapper + marginEnd;
                }

                // Propagation: on PageComplete(GridContinuation),
                // wrap into BlockContinuation + return up. The
                // outer AttemptLayout then sees the chained
                // BlockContinuation + dispatches it back through
                // the resume contract on the next page.
                if (nestedGridResult.Outcome == LayoutAttemptOutcome.PageComplete
                    && nestedGridResult.Continuation is GridContinuation nestedGridCont)
                {
                    return new BlockContinuation(
                        ResumeAtChild: childIdx,
                        ConsumedBlockSize: 0,
                        LayouterState: nestedGridCont);
                }

                // Advance the cursor + bookkeeping; skip the
                // EmitBlockSubtreeRecursive below.
                childCursor = childCursor + cursorAdvanceForNestedGrid;
                prevMarginEnd = marginEnd;
                hasPrior = true;
                continue;
            }

            // Recurse — emit grandchildren relative to this child's
            // content area. The recursion's own predicate gate skips
            // walking INTO non-flow block kinds (Table/Flex/Grid/etc.).
            //
            // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) —
            // multi-level continuation propagation. The depth==1 gate
            // is lifted: ANY nested multicol / table / further-nested
            // block at ANY depth can now propagate its PageComplete
            // up via the chain-of-BlockContinuation return protocol.
            //
            // Per post-PR-#57 review #2 Finding #1 — chain peel only.
            // The chain has DOM-depth layers (top-level no-flatten +
            // recursion-level skip-to-resume preserve every
            // intermediate container's child index). A leaf
            // (Table/MulticolContinuation) in the chain's LayouterState
            // means the leaf's container IS the matching child — the
            // Table/Multicol direct-dispatch branches above consume it
            // before this recursive-walk site is reached. So the only
            // valid chain-head case here is `BlockContinuation
            // deeperBlock` (the next level down).
            //
            // The prior hardcoded-`rc=0` re-wrap branches for Table /
            // Multicol leaves were workarounds for the flatten that
            // dropped intermediate indices; removed for correctness.
            LayoutContinuation? incomingForChild = null;
            if (incomingBlockChain is not null
                && childIdx == incomingBlockChain.ResumeAtChild
                && incomingBlockChain.LayouterState is BlockContinuation deeperBlock)
            {
                incomingForChild = deeperBlock;
                incomingBlockChain = null;
            }
            // Phase 3 cycle-2d "mid-split" — reset the resumed-extent channel before recursing so a
            // stale value from an earlier sibling isn't read; the child sets it iff it was a resumed
            // entry that completed cleanly this page.
            _resumedContainerEmittedExtent = -1;
            var nestedRet = EmitBlockSubtreeRecursive(
                child,
                parentBlockOffset: childBlockOffset,
                parentInlineOffset: childInlineOffset,
                parentInlineSize: childBorderBoxInlineSize,
                cancellationToken: cancellationToken,
                depth: depth + 1,
                propagatingResolver: propagatingResolver,
                propagatingFragmentainer: propagatingFragmentainer,
                incomingContinuation: incomingForChild,
                parentContentBlockSize: contentBlock);   // % height base (percent-height cycle).
            if (nestedRet is not null)
            {
                return new BlockContinuation(
                    ResumeAtChild: childIdx,
                    ConsumedBlockSize: 0,
                    LayouterState: nestedRet);
            }
            // Phase 3 cycle-2d "mid-split" — a resumed container that completed on THIS page reports
            // the extent it actually emitted (its measured childEffectiveBlockSize counts prior-page
            // children). Advance the cursor by that so a trailing in-flow sibling lands directly below
            // the emitted content instead of a full-subtree gap. Only when the child was resumed at a
            // child index > 0 (matching the set condition below, so a deeper grandchild's value can't
            // leak) + it published a value; otherwise the measured extent stands.
            if (MidSplitEnabled
                && incomingForChild is BlockContinuation { ResumeAtChild: > 0 }
                && _resumedContainerEmittedExtent >= 0)
            {
                childEffectiveBlockSize = _resumedContainerEmittedExtent;
            }

            // Per Phase 3 Task 12 sub-cycle 1 hardening (Finding 6 /
            // 1) + sub-cycle 2 hardening (Finding 1) — drain the pre-
            // measured nested table content. The recursion doesn't
            // have a ref LayoutContext so we synthesize one carrying
            // the constructor's diagnostic sink + the OUTER
            // fragmentainer captured at AttemptLayout entry; the
            // table layouter uses these for deferral diagnostics +
            // forced-overflow signals + cell-content layout.
            //
            // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) —
            // the depth==1-only NoBreakBreakResolver fallback at
            // deeper nesting is lifted. ALL depths now use the
            // propagating outer resolver + fragmentainer when
            // available (or the captured outer fragmentainer + a
            // fresh BreakResolver when the propagating pair is not
            // provided — e.g. the forced-overflow path). The deep
            // table can now return a TableContinuation; the recursion
            // wraps that into a chained BlockContinuation up to the
            // outer dispatch.
            if (nestedPendingTable is not null && _capturedFragmentainer is not null)
            {
                var fragmentainerForEmit = propagatingFragmentainer ?? _capturedFragmentainer;
                var emitLayout = new LayoutContext(fragmentainerForEmit)
                {
                    Diagnostics = _diagnostics,
                };
                // Use the propagating resolver when supplied; otherwise
                // a fresh BreakResolver. NoBreakBreakResolver is no
                // longer used here — TableLayouter's single-oversized-
                // row forward-progress fallback is the safety net that
                // makes lifting the atomic-deep-nesting fallback safe.
                IBreakResolver resolverForEmit;
                BreakResolver? localResolver = null;
                if (propagatingResolver is not null)
                {
                    resolverForEmit = propagatingResolver;
                }
                else
                {
                    localResolver = new BreakResolver();
                    resolverForEmit = localResolver;
                }
                try
                {
                    var tableInnerResult = EmitTableInner(
                        nestedPendingTable,
                        fragmentainer: fragmentainerForEmit,
                        layout: ref emitLayout,
                        resolver: resolverForEmit,
                        cancellationToken: cancellationToken);
                    if (tableInnerResult.Continuation is TableContinuation tc)
                    {
                        return new BlockContinuation(
                            ResumeAtChild: childIdx,
                            ConsumedBlockSize: 0,
                            LayouterState: tc);
                    }
                }
                finally
                {
                    localResolver?.Dispose();
                }
            }

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

        // Phase 3 cycle-2d "mid-split" — this container completed cleanly. If it was a RESUMED entry
        // (its earlier children were committed on prior pages via mid-split), publish the block-box
        // extent ACTUALLY emitted on this page so the parent advances its cursor by that (not the
        // measured full-subtree extent, which would over-reserve + push a trailing sibling off-page).
        if (MidSplitEnabled && startIdx > 0)
        {
            var pPaddingBottom = parent.Style.ReadLengthPxOrZero(PropertyId.PaddingBottom);
            var pBorderBottom = parent.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
            _resumedContainerEmittedExtent =
                pBorderStart + pPaddingStart + Math.Max(0, childCursor) + pPaddingBottom + pBorderBottom;
        }

        // Clean emission — no nested break propagated up.
        return null;
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
        // Box-model reads — the SHARED float model (percent-aware on the inline axis against
        // the BFC content box + the used-%-padding in-place rewrite for paint; % height stays
        // deferred). See <see cref="ReadFloatBoxModel"/>.
        var box = ReadFloatBoxModel(child, _bfcContentInlineSize, _measurePurpose);
        var marginStart = box.MarginStart;
        var marginInlineStart = box.MarginInlineStart;
        var borderBoxBlockSize = box.BorderBoxBlockSize;
        var borderBoxInlineSize = box.BorderBoxInlineSize;
        var marginBoxBlockSize = box.MarginBoxBlockSize;
        var marginBoxInlineSize = box.MarginBoxInlineSize;

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
        //
        // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) — floats
        // are out-of-flow per CSS 2.2 §9.5; propagating a nested
        // multicol/table continuation through the in-flow pagination
        // machinery requires float-tracking continuation machinery
        // that's an existing Phase 3 Task 8 deferral. Discard the
        // returned continuation (atomic-fallback behavior preserved)
        // + emit a one-shot per-page diagnostic so the truncation is
        // observable. The first-page slice of the float is committed;
        // the deep break's remainder is lost.
        // Per Phase 3 Task 19 — mark the float subtree so nested grid / flex emit
        // atomically (lossless) instead of paginating into a discarded continuation.
        var nestedPrevAtomicFloat = _inAtomicFloatSubtree;
        _inAtomicFloatSubtree = true;
        LayoutContinuation? nestedFloatRet;
        try
        {
            nestedFloatRet = EmitBlockSubtreeRecursive(
                child,
                parentBlockOffset: fragmentBlockOffset,
                parentInlineOffset: fragmentInlineOffset,
                parentInlineSize: borderBoxInlineSize,
                cancellationToken: cancellationToken,
                depth: 1);
        }
        finally
        {
            _inAtomicFloatSubtree = nestedPrevAtomicFloat;
        }
        if (nestedFloatRet is not null
            && !_emittedFloatBreakInsideNestedDiagnosticThisPage)
        {
            OptimizingBreakResolver.SafeEmit(
                _capturedDiagSink ?? _diagnostics,
                new PaginateDiagnostic(
                    PaginateDiagnosticCodes.LayoutFloatBreakInsideNested001,
                    $"BlockLayouter: nested float (BoxKind."
                    + $"{child.Kind}) contains a multicol/table that broke "
                    + "mid-emission. Floats are out-of-flow per CSS 2.2 "
                    + "§9.5; propagating their nested continuation through "
                    + "the in-flow pagination machinery requires float-"
                    + "tracking continuation machinery that's an existing "
                    + "Phase 3 Task 8 deferral (cycle 3+ scope). The deep "
                    + "break's remainder is truncated. See "
                    + "docs/deferrals.md#float-continuation-propagation.",
                    PaginateDiagnosticSeverity.Warning));
            _emittedFloatBreakInsideNestedDiagnosticThisPage = true;
        }
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
        // The float's margin-box block-size from the SHARED box-model
        // reads (PR #165 review P1 — the pre-check read absolute-only
        // lengths, so a `% margin-top`/`% padding-top` float could pass
        // break planning and only overflow after EmitFloat resolved the
        // percentages; the shared model keeps both percent-aware).
        var box = ReadFloatBoxModel(child, fragmentainer.ContentInlineSize, _measurePurpose);

        // Per post-PR-31 review (P1 #3 + Copilot #1) — use the
        // FloatManager peek API which mirrors PlaceFloat's stacking
        // rule (max of currentBlockY + same-side max-bottom). This
        // catches the case where prior same-side floats have stacked
        // below the cursor.
        var hypotheticalY = _floatManager.PeekPlaceFloatBlockOffset(
            side, fragmentainer.UsedBlockSize);
        return hypotheticalY + box.MarginBoxBlockSize > fragmentainer.BlockSize;
    }

    /// <summary>A float's used box-model extents — the single source of truth shared by
    /// <see cref="EmitFloat"/>, <see cref="EmitNestedFloat"/> AND the
    /// <see cref="WouldFloatOverflow"/> break-planning pre-check, so planning sees the SAME
    /// metrics emission uses (PR #165 review P1).</summary>
    private readonly record struct FloatBoxModel(
        double MarginStart, double MarginInlineStart,
        double BorderBoxBlockSize, double BorderBoxInlineSize,
        double MarginBoxBlockSize, double MarginBoxInlineSize);

    /// <summary>Read <paramref name="child"/>'s float box model, PERCENT-aware on the inline
    /// axis (float-percent cycle, CSS 2.2 §8.3/§8.4/§10.3.5 — a float's <c>%</c>
    /// margins/padding/width resolve against its containing block's inline size, here the BFC
    /// content box <paramref name="bfcInlineSizePx"/>; <c>% height</c> stays deferred — the
    /// containing height is indefinite, same story as the body pre-percent-height). The used
    /// <c>%</c> padding is rewritten IN PLACE first so paint reads the same slots — TextPainter's
    /// content-origin inset is absolute-only, so an unrewritten <c>padding-left: 10%</c> sized
    /// the fragment but painted the text at the border edge (PR #165 review P1). The rewrite is
    /// idempotent (the BFC inline size is constant for the document), so the
    /// <see cref="WouldFloatOverflow"/> pre-check calling this before a deferral re-check is
    /// harmless.</summary>
    private static FloatBoxModel ReadFloatBoxModel(
        Box child, double bfcInlineSizePx, MeasurePurpose measurePurpose)
    {
        // PR #218 review [P1 #2 / P2 #5] — cyclic % padding/margins → 0 for an intrinsic probe (CSS
        // Sizing §5.2.1), real against the BFC width otherwise; only a real layout persists for paint.
        var pctInsetBase = measurePurpose.ZeroesCyclicPercentInsets() ? 0.0 : bfcInlineSizePx;
        if (measurePurpose == MeasurePurpose.Layout)
            child.Style.ResolveUsedPercentPaddingInPlace(bfcInlineSizePx);   // paint reads the slots later.
        var marginStart = child.Style.ReadLengthOrPercentPx(PropertyId.MarginTop, pctInsetBase);
        var marginEnd = child.Style.ReadLengthOrPercentPx(PropertyId.MarginBottom, pctInsetBase);
        var marginInlineStart = child.Style.ReadLengthOrPercentPx(PropertyId.MarginLeft, pctInsetBase);
        var marginInlineEnd = child.Style.ReadLengthOrPercentPx(PropertyId.MarginRight, pctInsetBase);
        var borderStart = child.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
        var borderEnd = child.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
        var borderInlineStart = child.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth);
        var borderInlineEnd = child.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth);
        var paddingStart = child.Style.ReadLengthOrPercentPx(PropertyId.PaddingTop, pctInsetBase);
        var paddingEnd = child.Style.ReadLengthOrPercentPx(PropertyId.PaddingBottom, pctInsetBase);
        var paddingInlineStart = child.Style.ReadLengthOrPercentPx(PropertyId.PaddingLeft, pctInsetBase);
        var paddingInlineEnd = child.Style.ReadLengthOrPercentPx(PropertyId.PaddingRight, pctInsetBase);
        var contentBlock = child.Style.ReadLengthPxOrZero(PropertyId.Height);
        var contentInline = child.Style.ReadLengthOrPercentPx(PropertyId.Width, bfcInlineSizePx);

        // Box-sizing audit — a float's explicit `height` honors box-sizing (border-box
        // → declared is the border box; content-box → declared + chrome, byte-identical).
        var borderBoxBlockSize = BoxSizingHelper.DeclaredToBorderBox(
            child.Style, contentBlock, borderStart + paddingStart + paddingEnd + borderEnd);
        // Per cycle 1 post-PR-30 review (Copilot #2) — border-box
        // inline size includes inline-axis border + padding (the
        // content-box size alone mis-reports the float's footprint to
        // FloatManager for `clear` + inline-range calculations).
        // Box-sizing audit (PR #203 review [P1]) — a `box-sizing: border-box`
        // float's declared `width` IS the border box (chrome inside), floored at
        // the chrome; pre-fix the insets were always ADDED, so a border-box float
        // over-reported its footprint to FloatManager (placement / clear /
        // flow-around). Routes through the same mapping as in-flow blocks +
        // honors min/max-width. Byte-identical for content-box without min/max.
        var floatInlineInsets =
            borderInlineStart + paddingInlineStart + paddingInlineEnd + borderInlineEnd;
        var borderBoxInlineSize = DeclaredWidthToBorderBox(
            child.Style, contentInline, floatInlineInsets);
        borderBoxInlineSize = child.ClampBorderBoxToMinMax(
            borderBoxInlineSize, PropertyId.MinWidth, PropertyId.MaxWidth, bfcInlineSizePx);
        return new(
            MarginStart: marginStart,
            MarginInlineStart: marginInlineStart,
            BorderBoxBlockSize: borderBoxBlockSize,
            BorderBoxInlineSize: borderBoxInlineSize,
            // Outer (margin-box) extents drive FloatManager placement +
            // the float's footprint for `clear` computation.
            MarginBoxBlockSize: Math.Max(0, marginStart + borderBoxBlockSize + marginEnd),
            MarginBoxInlineSize: Math.Max(0, marginInlineStart + borderBoxInlineSize + marginInlineEnd));
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

    /// <summary>Measurement-cache cycle — the per-conversion grid measure cache
    /// captured from the layout context at AttemptLayout entry. Used by
    /// <see cref="PreMeasureGridRowExtent"/> (which has no LayoutContext) +
    /// propagated onto the nested-dispatch contexts so a nested grid's emission
    /// shares it. Null ⇒ no shared cache wired (per-instance fallback).</summary>
    private GridMeasurementCache? _gridMeasureCache;

    /// <summary>Per `multi-page-allocation-churn` — the cross-page table measurement cache,
    /// captured from <c>LayoutContext.TableMeasureCache</c> + propagated onto nested-dispatch
    /// contexts. Lets <see cref="MeasureNestedTableContentExtent"/> (and page 1's
    /// <see cref="PreMeasureTableIfNeeded"/>) reuse the page-invariant column layout instead of
    /// re-shaping every cell on every page. Null ⇒ no shared cache wired.</summary>
    private TableMeasurementCache? _tableMeasureCache;

    /// <summary>Per Phase 3 Task 12 sub-cycle 2 hardening (Finding 2)
    /// — per-AttemptLayout cache of nested-table CONTENT HEIGHT
    /// measurements. Keyed by the Table / InlineTable wrapper
    /// <see cref="Box"/>. The recursive measure pass
    /// (<see cref="MeasureSubtreeVisualBlockExtentRecursive"/>)
    /// populates the entry the first time it encounters a table
    /// wrapper so subsequent visits within the same outer-walk reuse
    /// the cached height instead of re-running the cell content
    /// layout. (The emit recursion still constructs its own
    /// <see cref="TableLayouter"/> with the wrapper's real offsets;
    /// re-measuring there is symmetric with the existing duplicate-
    /// walk pattern.) Cleared at AttemptLayout entry.</summary>
    private Dictionary<Box, double>? _measuredTableContentHeightCache;

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
        // Box-model reads — the SHARED float model (percent-aware on the inline axis against
        // the BFC content box + the used-%-padding in-place rewrite for paint; % height stays
        // deferred; `auto` Width reads 0 — cycle 3 will shrink-to-fit per CSS 2.2 §10.3.5).
        // See <see cref="ReadFloatBoxModel"/>.
        var box = ReadFloatBoxModel(child, fragmentainer.ContentInlineSize, _measurePurpose);
        var marginStart = box.MarginStart;
        var marginInlineStart = box.MarginInlineStart;
        var borderBoxBlockSize = box.BorderBoxBlockSize;
        var borderBoxInlineSize = box.BorderBoxInlineSize;
        var marginBoxBlockSize = box.MarginBoxBlockSize;
        var marginBoxInlineSize = box.MarginBoxInlineSize;

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
        //
        // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) — discard
        // the recursion's return for float subtrees. Floats are
        // out-of-flow per CSS 2.2 §9.5; their nested continuation
        // can't propagate through the in-flow pagination machinery
        // without float-tracking continuation machinery (existing
        // Phase 3 Task 8 deferral). Emit a one-shot per-page
        // diagnostic so authors see the truncation.
        // Per Phase 3 Task 19 — mark the float subtree so nested grid / flex emit
        // atomically (lossless) instead of paginating into a discarded continuation.
        var floatPrevAtomicFloat = _inAtomicFloatSubtree;
        _inAtomicFloatSubtree = true;
        LayoutContinuation? floatRet;
        try
        {
            floatRet = EmitBlockSubtreeRecursive(
                child,
                parentBlockOffset: fragmentBlockOffset,
                parentInlineOffset: fragmentInlineOffset,
                parentInlineSize: borderBoxInlineSize,
                cancellationToken: cancellationToken,
                depth: 1);
        }
        finally
        {
            _inAtomicFloatSubtree = floatPrevAtomicFloat;
        }
        if (floatRet is not null
            && !_emittedFloatBreakInsideNestedDiagnosticThisPage)
        {
            var diagSink = layout.Diagnostics ?? _diagnostics;
            OptimizingBreakResolver.SafeEmit(diagSink, new PaginateDiagnostic(
                PaginateDiagnosticCodes.LayoutFloatBreakInsideNested001,
                $"BlockLayouter: float (BoxKind.{child.Kind}) contains a "
                + "multicol/table that broke mid-emission. Floats are "
                + "out-of-flow per CSS 2.2 §9.5; propagating their nested "
                + "continuation through the in-flow pagination machinery "
                + "requires float-tracking continuation machinery that's "
                + "an existing Phase 3 Task 8 deferral (cycle 3+ scope). "
                + "The deep break's remainder is truncated. See "
                + "docs/deferrals.md#float-continuation-propagation.",
                PaginateDiagnosticSeverity.Warning));
            _emittedFloatBreakInsideNestedDiagnosticThisPage = true;
        }
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

    /// <summary>Per Phase 3 Task 14 cycle 1 — predicate distinguishing
    /// multicol containers from regular block-flow containers. A box
    /// is a multicol container when:
    /// <list type="bullet">
    ///   <item>Its kind is a block-flow container owned by
    ///   <see cref="BlockLayouter"/> (<see cref="BoxKind.BlockContainer"/>,
    ///   <see cref="BoxKind.ListItem"/>, <see cref="BoxKind.AnonymousBlock"/>;
    ///   <see cref="BoxKind.Root"/> excluded since the cascade origin
    ///   wouldn't carry <c>column-count</c> in practice).</item>
    ///   <item>Its computed style declares <c>column-count</c> with a
    ///   positive integer value &gt;= 1 (= <see cref="ComputedStyleLayoutExtensions.ReadColumnCount"/>
    ///   returns a value &gt;= 1). Per post-PR-#60 review hardening
    ///   (F#3), <c>column-count: 1</c> ALSO establishes a multicol
    ///   container — CSS Multi-column L1 §1 says "a multi-column
    ///   container is created when [column-count or column-width] is
    ///   set on a block-level element [non-auto]"; column boxes
    ///   establish their own BFC. Pre-fix this branch required
    ///   <c>n &gt;= 2</c>, so <c>column-count: 1</c> fell through to
    ///   ordinary block flow + lost the BFC contract (e.g., outer
    ///   margins could collapse with the multicol's first child's top
    ///   margin). The MulticolLayouter now degrades to
    ///   <c>EmitSingleColumnFallthrough</c> when used N &lt; 2 instead.</item>
    ///   <item><b>Per Phase 3 Task 14 cycle 4</b> — OR its computed style
    ///   declares <c>column-width</c> with a length value (=
    ///   <see cref="ComputedStyleLayoutExtensions.ReadColumnWidth"/>
    ///   returns a non-null value). The effective column count is
    ///   derived from the container's content inline-size + column-gap
    ///   per CSS Multi-column L1 §3.3 at MulticolLayouter dispatch time
    ///   (container geometry isn't known at this static predicate).
    ///   When the derived count is &lt; 2 the MulticolLayouter degrades
    ///   to a single-column emit (= functionally non-multicol).</item>
    /// </list>
    ///
    /// <para><b>Why not a dedicated <c>BoxKind.MulticolContainer</c>.</b>
    /// CSS Multi-column L1 §2 defines the multicol container as a
    /// regular block-level box that gains multi-column behavior from
    /// the <c>column-count</c> / <c>column-width</c> property values.
    /// Encoding this as a layout-time predicate (rather than a
    /// BoxBuilder-time box kind) mirrors how CSS encodes it +
    /// preserves the box hierarchy invariants (e.g., a
    /// <c>display: block</c> element is still a BlockContainer
    /// regardless of <c>column-count</c>; the cascade's typed-slot
    /// reads decide layout at dispatch time).</para></summary>
    private static bool IsMulticolContainer(Box box)
    {
        if (!IsBlockFlowContainerOwnedByBlockLayouter(box))
        {
            return false;
        }
        // Root is the implicit document containing block; it
        // shouldn't carry column-count + the multicol model doesn't
        // apply to it. Exclude defensively.
        if (box.Kind == BoxKind.Root)
        {
            return false;
        }
        var n = box.Style.ReadColumnCount();
        // Per post-PR-#60 review hardening (F#3) — `column-count: 1`
        // establishes a multicol container per CSS Multi-column L1 §1
        // (column boxes have their own BFC). Pre-fix this branch
        // required n >= 2, so column-count: 1 fell through to ordinary
        // block flow + lost the BFC contract. MulticolLayouter
        // degrades to EmitSingleColumnFallthrough when used N < 2.
        if (n is >= 1) return true;
        // Per Phase 3 Task 14 cycle 4 — `column-width: <length>` with
        // `column-count: auto` ALSO constitutes a multicol container;
        // the effective column count is derived from the container's
        // inline size at dispatch time (= inside MulticolLayouter via
        // ComputeUsedColumnCount). The derived-N-must-be->=2 check
        // happens there once container geometry is known; if it's < 2
        // MulticolLayouter degrades to a single-column emit.
        if (box.Style.ReadColumnWidth() is not null) return true;
        return false;
    }

    /// <summary>Per Phase 3 Task 15 cycle 1 (Hello World) — predicate
    /// distinguishing flex containers from regular block-flow containers.
    /// A box is a flex container when its <see cref="Box.Kind"/> is
    /// <see cref="BoxKind.FlexContainer"/> (block-outer + flex-inner =
    /// <c>display: flex</c>) or <see cref="BoxKind.InlineFlexContainer"/>
    /// (inline-outer + flex-inner = <c>display: inline-flex</c>).
    ///
    /// <para><b>Why BoxKind-based, not property-based.</b> Contrast
    /// with <see cref="IsMulticolContainer"/>'s property-based gate
    /// (<c>column-count</c> / <c>column-width</c> on a regular
    /// <see cref="BoxKind.BlockContainer"/>). Per CSS Display L3 §2,
    /// <c>display: flex</c> defines an outer + inner display pair that
    /// <see cref="DisplayMapper"/> already resolves into the dedicated
    /// kind at box-generation time; the dispatch predicate just reads
    /// the kind.</para></summary>
    private static bool IsFlexContainer(Box box)
        => box.Kind is BoxKind.FlexContainer or BoxKind.InlineFlexContainer;

    /// <summary>Per Phase 3 Task 17 cycle 1 (Hello World) — predicate
    /// distinguishing grid containers from regular block-flow containers.
    /// A box is a grid container when its <see cref="Box.Kind"/> is
    /// <see cref="BoxKind.GridContainer"/> (block-outer + grid-inner =
    /// <c>display: grid</c>) or <see cref="BoxKind.InlineGridContainer"/>
    /// (inline-outer + grid-inner = <c>display: inline-grid</c>).
    /// Mirrors <see cref="IsFlexContainer"/>; the BoxKind is set by
    /// <see cref="DisplayMapper"/> at box-generation time so the
    /// dispatch predicate just reads the kind.</summary>
    private static bool IsGridContainer(Box box)
        => box.Kind is BoxKind.GridContainer or BoxKind.InlineGridContainer;

    /// <summary>Per Phase 3 Task 17 cycle 5 — predicate identifying
    /// grid containers eligible for multi-page row-by-row splitting.
    ///
    /// <para>Cycle 5 ships row-atomic pagination for ALL grid
    /// containers (= unlike flex which gates on row + wrap + non-
    /// wrap-reverse, every grid can paginate row-by-row regardless
    /// of CSS styling — the row is the only natural break point + a
    /// grid with one row degenerates to atomic behavior naturally).</para>
    ///
    /// <para>The predicate exists as a separate method so future
    /// cycles can add gating conditions (e.g., spans straddling
    /// pages once cycle 6 ships span placement, or auto-track-
    /// induced indefinite-row scenarios) without ripping out the
    /// dispatch sites.</para></summary>
    private static bool IsPaginatableGrid(Box box)
        => IsGridContainer(box);

    /// <summary>Per Phase 3 Task 16 cycle 4b — predicate identifying
    /// flex containers eligible for multi-page line splitting via the
    /// pre-break-check / forced-overflow re-route paths.
    ///
    /// <para>Post-PR-#83 review P3 #6 (DRY/SOLID): now delegates to
    /// <see cref="FlexLayouter.IsPaginatablePerStyle"/> so the
    /// dispatch-side gate + the layouter-side emission gate share ONE
    /// source of truth. Without delegation a drift in either predicate
    /// silently dropped flex content (= dispatch flips
    /// <c>allowPagination: true</c> but the layouter falls into the
    /// atomic branch + no FlexContinuation is emitted; or vice
    /// versa).</para></summary>
    private static bool IsPaginatableFlex(Box box)
        => FlexLayouter.IsPaginatablePerStyle(box);

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
    /// child per <see cref="AttemptLayout"/> iteration.</para>
    ///
    /// <para><b>Phase 3 Task 12 sub-cycle 5 hardening (Finding 1)</b> —
    /// <see cref="BoxKind.TableCell"/> is now included in the allowed
    /// outer-display kinds. Previously, when <see cref="TableLayouter"/>
    /// invoked a nested <see cref="BlockLayouter"/> with the TableCell as
    /// root (during <c>MeasureCellContent</c>), the cell's direct
    /// inline-only children (a bare <c>&lt;td&gt;Description&lt;/td&gt;</c>'s
    /// <see cref="BoxKind.TextRun"/>) were skipped — the predicate
    /// rejected the cell kind + the block-loop only dispatched
    /// block-level children. Production HTML like
    /// <c>&lt;td&gt;A&lt;/td&gt;&lt;td&gt;BBBBBBBBBB&lt;/td&gt;</c>
    /// silently contributed 0 intrinsic width, defeating auto-table-
    /// layout's shrink-to-fit. Adding TableCell makes the
    /// inline-only-block dispatch path fire so the cell's children lay
    /// out as inline lines. TableCell is only ever a BlockLayouter root
    /// when measured via <see cref="TableLayouter.MeasureCellContent"/>
    /// — never the outer <see cref="BlockLayouter"/>'s
    /// <c>_rootBox</c> — so the predicate's existing protection (Root
    /// is excluded) is unaffected.</para></summary>
    private static bool IsInlineOnlyBlockContainer(Box box)
    {
        // Outer-display gate: only block-flow containers participate.
        // Root excluded — see XML doc rationale.
        // TableCell added per Phase 3 Task 12 sub-cycle 5 hardening
        // Finding 1 — TableLayouter dispatches the cell box itself as
        // the nested BlockLayouter's root, so inline-only TableCell
        // children must hit the inline-dispatch path.
        if (box.Kind is not (BoxKind.BlockContainer
            or BoxKind.ListItem or BoxKind.AnonymousBlock
            or BoxKind.TableCell))
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

    /// <summary>Inline-atomic-boxes cycle (inline-block first cut) — the nested-content-root
    /// variant of <see cref="IsInlineOnlyBlockContainer"/>: it ALSO accepts an
    /// <see cref="BoxKind.InlineBlockContainer"/> root with only inline children, so a nested
    /// content measure of an inline-block whose direct content is text dispatches that content
    /// as one inline-only-root fragment (box == the inline-block). Used ONLY at the
    /// <c>_layoutRootInlineContent</c> root gate — the child-loop / outer-pipeline paths keep
    /// the strict predicate, so an inline-block CHILD stays an ATOMIC in its parent's line
    /// (it is never block-level-dispatched).</summary>
    private static bool IsInlineOnlyRootContainer(Box box)
    {
        if (box.Kind == BoxKind.InlineBlockContainer)
        {
            if (box.Children.Count == 0) return false;
            for (var i = 0; i < box.Children.Count; i++)
                if (box.Children[i].IsBlockLevel) return false;
            return true;
        }
        return IsInlineOnlyBlockContainer(box);
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
        double? LineHeightOverridePx); // null = `normal` → font-size × 1.2; an explicit value (incl. 0) is used

    private static InlineOnlyBlockMetrics ReadInlineOnlyBlockMetrics(
        Box block, double containingInlinePx, MeasurePurpose measurePurpose)
    {
        // An AnonymousBlock wrapper (whitespace / block-in-inline fixup, or the inline-only
        // table-cell wrapper — BoxBuilder.FixupAnonymousBlocks) REUSES its real parent's
        // ComputedStyle by reference, so reading margin / border / padding / width off block.Style
        // here would charge the PARENT's own box model to a box that PAINTS none — double-counting
        // the parent's chrome. This is the RC-9 sub-bug: a `<td>` with inline-only content wraps
        // its text in an AnonymousBlock carrying the cell's style, so the cell's padding was emitted
        // TWICE (once on the cell, once on the wrapper) → inflated row heights. The painter skips
        // anonymous-box decorations and the measure pass already zeroes this chrome (see the
        // AnonymousBlock guard in the measure recursion ~L9894); mirror it here so the EMIT read and
        // the measure agree. The wrapper's inherited line-height still governs its wrapped text.
        if (block.Kind == BoxKind.AnonymousBlock)
        {
            return new(
                MarginInlineStart: 0, MarginInlineEnd: 0, MarginBlockStart: 0, MarginBlockEnd: 0,
                BorderInlineStart: 0, BorderInlineEnd: 0, BorderBlockStart: 0, BorderBlockEnd: 0,
                PaddingInlineStart: 0, PaddingInlineEnd: 0, PaddingBlockStart: 0, PaddingBlockEnd: 0,
                DeclaredWidthPx: 0,
                LineHeightOverridePx: block.Style.ReadLineHeightPx(
                    block.Style.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16)));
        }

        // Rewrite % padding to used px FIRST (body-percent cycle) — TextPainter's content-origin
        // inset reads the same slots at paint time. Only a real layout PERSISTS; a dropped measure
        // leaves the slot Percentage so the speculative containing size never reaches the shared
        // style. PR #218 review [P1 #2 / P2 #5] — cyclic % padding/margins → 0 for an intrinsic probe
        // (CSS Sizing §5.2.1), real against the definite width otherwise (width + auto-margin
        // distribution still use the real `containingInlinePx`).
        var pctInsetBase = measurePurpose.ZeroesCyclicPercentInsets() ? 0.0 : containingInlinePx;
        if (measurePurpose == MeasurePurpose.Layout)
            block.Style.ResolveUsedPercentPaddingInPlace(containingInlinePx);
        // §10.3.3 auto margins (auto-margins cycle) — the same distribution as the block paths,
        // so a text-bearing `width: …; margin: 0 auto` block centres like an empty one. The
        // declared width must be explicit (the fill path's auto margins stay 0).
        var marginInlineStart = block.Style.ReadLengthOrPercentPx(PropertyId.MarginLeft, pctInsetBase);
        var marginInlineEnd = block.Style.ReadLengthOrPercentPx(PropertyId.MarginRight, pctInsetBase);
        var declaredWidth = block.Style.ReadLengthOrPercentPx(PropertyId.Width, containingInlinePx);
        if (HasExplicitWidth(block))
        {
            // The distribution must use the SAME border-box width the layout computes
            // (PR #165 review P2 — always adding the insets treated a `box-sizing: border-box`
            // width as content-box, so a centred block computed leftover from a too-wide box).
            var inlineInsets = block.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth)
                + block.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth)
                + block.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft)
                + block.Style.ReadLengthPxOrZero(PropertyId.PaddingRight);
            var borderBox = DeclaredWidthToBorderBox(block.Style, declaredWidth, inlineInsets);
            // §10.4 — clamp by min/max-width BEFORE distributing auto margins, so a
            // centred block whose width is raised by min-width (or lowered by
            // max-width) centres against its USED width — matching the same clamp
            // ComputeInlineOnlyBlockLayout applies to the emitted width (PR #203
            // Copilot review: the two must use the same border-box width).
            borderBox = block.ClampBorderBoxToMinMax(
                borderBox, PropertyId.MinWidth, PropertyId.MaxWidth, containingInlinePx);
            ResolveAutoInlineMargins(
                block, borderBox, containingInlinePx, ref marginInlineStart, ref marginInlineEnd);
        }
        else if (HasExplicitMaxWidth(block))
        {
            // Corpus-fidelity (11 certificate `.citation`) — an AUTO-width block that `max-width`
            // clamps below the available range still has a definite used width, so `margin: 0 auto`
            // centers it (CSS 2.2 §10.3.3 + §10.4). ComputeInlineOnlyBlockLayout applies the same
            // auto-fill + max-width clamp to the emitted width, so distribute against the same used
            // border box. No-op when max-width doesn't clamp (used width == range → leftover 0).
            var autoFill = Math.Max(0, containingInlinePx - marginInlineStart - marginInlineEnd);
            var usedBorderBox = block.ClampBorderBoxToMinMax(
                autoFill, PropertyId.MinWidth, PropertyId.MaxWidth, containingInlinePx);
            ResolveAutoInlineMargins(
                block, usedBorderBox, containingInlinePx, ref marginInlineStart, ref marginInlineEnd);
        }
        return new(
            // Body % lengths (body-percent cycle): margins/paddings/width take percentages against
            // the containing block's INLINE size (CSS 2.2 §8.3/§8.4/§10.2), like the block paths.
            MarginInlineStart: marginInlineStart,
            MarginInlineEnd: marginInlineEnd,
            MarginBlockStart: block.Style.ReadLengthOrPercentPx(PropertyId.MarginTop, pctInsetBase),
            MarginBlockEnd: block.Style.ReadLengthOrPercentPx(PropertyId.MarginBottom, pctInsetBase),
            BorderInlineStart: block.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth),
            BorderInlineEnd: block.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth),
            BorderBlockStart: block.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth),
            BorderBlockEnd: block.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth),
            PaddingInlineStart: block.Style.ReadLengthOrPercentPx(PropertyId.PaddingLeft, pctInsetBase),
            PaddingInlineEnd: block.Style.ReadLengthOrPercentPx(PropertyId.PaddingRight, pctInsetBase),
            PaddingBlockStart: block.Style.ReadLengthOrPercentPx(PropertyId.PaddingTop, pctInsetBase),
            PaddingBlockEnd: block.Style.ReadLengthOrPercentPx(PropertyId.PaddingBottom, pctInsetBase),
            DeclaredWidthPx: block.Style.ReadLengthOrPercentPx(PropertyId.Width, containingInlinePx),
            // line-height: read from the block's own style (sub-cycle
            // 1 simple rule — uniform across the block). The line-height
            // cycle reads the full grammar (normal/number/length/%) via
            // ReadLineHeightPx — a unitless number multiplies the block's
            // own font-size; 0 = `normal` → the font-size × 1.2 fallback.
            LineHeightOverridePx: block.Style.ReadLineHeightPx(
                block.Style.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16)));
    }

    /// <summary>Whether the box DECLARES an explicit <c>width</c> — a LengthPx or Percentage
    /// slot, INCLUDING the legal zero (`width: 0` / `0%`); `auto`/keywords/unset are not
    /// explicit. The §10.3.3 gates key on this tag test (post-PR-#164 review P3).</summary>
    private static bool HasExplicitWidth(Box child) =>
        child.Style.Get(PropertyId.Width).Tag is ComputedSlotTag.LengthPx or ComputedSlotTag.Percentage;

    /// <summary>An explicit <c>max-width</c> (LengthPx / Percentage). A <c>width: auto</c> block that
    /// <c>max-width</c> clamps below the available range still has a definite used width, so
    /// <c>margin: 0 auto</c> centers it (CSS 2.2 §10.3.3 + §10.4) — see
    /// <see cref="ResolveAutoInlineMargins"/>.</summary>
    private static bool HasExplicitMaxWidth(Box child) =>
        child.Style.Get(PropertyId.MaxWidth).Tag is ComputedSlotTag.LengthPx or ComputedSlotTag.Percentage;

    /// <summary>Body `box-sizing: border-box` (box-sizing cycle) — the KeywordResolver box-sizing
    /// table: 0 = content-box (the initial), 1 = border-box. Mirrors the margin-box reader
    /// (PR #155).</summary>
    private static bool IsBorderBoxSizing(ComputedStyle style) =>
        BoxSizingHelper.IsBorderBox(style);

    /// <summary>The used border-box inline size from a declared <c>width</c> +
    /// <paramref name="inlineInsets"/> (inline-axis borders + padding) under the box's
    /// <c>box-sizing</c> (CSS Basic UI 4 §10): <c>border-box</c> → the declared width IS the
    /// border box, floored at the insets (the content box bottoms out at 0 — the PR #155
    /// margin-box rule); <c>content-box</c> (the initial) adds them. The ONE mapping shared by
    /// the block fill helper, the inline-only computation AND the auto-margin distribution, so
    /// a centred `border-box` block distributes leftover from the same width it is laid out at
    /// (PR #165 review P2).</summary>
    private static double DeclaredWidthToBorderBox(
        ComputedStyle style, double declaredWidth, double inlineInsets) =>
        BoxSizingHelper.DeclaredToBorderBox(style, declaredWidth, inlineInsets);

    /// <summary>Used border-box inline size for an in-flow block-level child (the CSS 2.2
    /// §10.3.3 cycle-1 subset; body-explicit-width gap fix). A plain block container
    /// (<see cref="BoxKind.BlockContainer"/> / <see cref="BoxKind.ListItem"/>) with an explicit
    /// <c>width</c> (the CONTENT-box inline size; <c>auto</c> / percentage read as 0 via
    /// <see cref="ComputedStyleLayoutExtensions.ReadLengthPxOrZero"/>, the cycle-1 contract)
    /// maps it to the border box via the inline-axis borders + padding — mirroring
    /// <see cref="ComputeInlineOnlyBlockLayout"/>, so a block WITHOUT inline content (whose
    /// background band previously spanned the full available range) sizes identically to one
    /// WITH it. Everything else keeps the fill behavior (available range minus the inline
    /// margins, the pre-fix path, byte-identical):
    /// <list type="bullet">
    ///   <item><see cref="BoxKind.AnonymousBlock"/> SHARES its parent's style object, so the
    ///   parent's explicit width would double-apply (plus re-add the parent's borders/padding);
    ///   per Display L3 §3.1 anonymous boxes take the initial <c>width: auto</c>.</item>
    ///   <item><see cref="BoxKind.Table"/> / <see cref="BoxKind.InlineTable"/> wrappers track
    ///   the MEASURED grid via the table-driven growth logic (Task 12 Finding 6) — the grid
    ///   already consumes the css width itself.</item>
    ///   <item><see cref="BoxKind.BlockReplacedElement"/> sizes like a plain block
    ///   (img-pipeline cycle): the sizing pre-pass writes the §10.3.2 used width/height into
    ///   the slots, so an explicit width here is the image's used size — filling would
    ///   stretch it.</item>
    ///   <item><see cref="BoxKind.FlexContainer"/> / <see cref="BoxKind.GridContainer"/> now
    ///   honor an explicit <c>width</c> too (the flex/grid container-width cycle): the declared
    ///   border-box width feeds <c>FlexGeometryHelper</c> / <c>GridGeometryHelper</c>'s content
    ///   inline size, so alignment / wrap / track sizing run against the declared width, not the
    ///   page width. <c>width: auto</c> keeps the fill behavior.</item>
    /// </list>
    /// §10.3.3 margin DISTRIBUTION ships in the auto-margins cycle — see
    /// <see cref="ResolveAutoInlineMargins"/> (the caller applies it right after this).</summary>
    private static double ResolveInFlowBorderBoxInlineSize(
        Box child, double availableInlineSize, double containingInlinePx,
        double marginInlineStart, double marginInlineEnd)
    {
        if (child.Kind is BoxKind.BlockContainer or BoxKind.ListItem or BoxKind.BlockReplacedElement
            or BoxKind.FlexContainer or BoxKind.GridContainer)
        {
            // Body % lengths (body-percent cycle): an explicit PERCENTAGE width resolves against
            // the CONTAINING block's inline size (CSS 2.2 §10.2 — not the float-adjusted available
            // range), as do the padding percentages (§8.4). Explicitness is a TAG test, so the
            // legal `width: 0` / `0%` sizes a zero-content border box instead of filling
            // (post-PR-#164 review P3).
            if (HasExplicitWidth(child))
            {
                var declaredWidth = child.Style.ReadLengthOrPercentPx(PropertyId.Width, containingInlinePx);
                var inlineInsets = child.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth)
                    + child.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth)
                    + child.Style.ReadLengthOrPercentPx(PropertyId.PaddingLeft, containingInlinePx)
                    + child.Style.ReadLengthOrPercentPx(PropertyId.PaddingRight, containingInlinePx);
                // Body `box-sizing` (box-sizing cycle, CSS Basic UI 4 §10) — the shared mapping.
                var borderBoxWidth = DeclaredWidthToBorderBox(child.Style, declaredWidth, inlineInsets);
                // §10.4 — min-width / max-width clamp the explicit width (max first,
                // then min so min wins when min > max). A no-op when neither is set.
                return child.ClampBorderBoxToMinMax(
                    borderBoxWidth, PropertyId.MinWidth, PropertyId.MaxWidth, containingInlinePx);
            }
            // §10.3.3 — `width: auto` on a plain block fills the available range
            // (minus margins); §10.4 — min-width/max-width then clamp that USED
            // width (e.g. `max-width: 600px` on an auto-width content column).
            // No-op when neither is set, so the fill path stays byte-identical
            // (PR #203 review [P2]). Gated to plain block kinds — anonymous / flex
            // / grid / table fill is unchanged (they own their width logic).
            var autoFillWidth = Math.Max(0,
                availableInlineSize - marginInlineStart - marginInlineEnd);
            return child.ClampBorderBoxToMinMax(
                autoFillWidth, PropertyId.MinWidth, PropertyId.MaxWidth, containingInlinePx);
        }
        return Math.Max(0, availableInlineSize - marginInlineStart - marginInlineEnd);
    }

    /// <summary>§10.3.3 auto-margin DISTRIBUTION (auto-margins cycle) — for a plain in-flow block
    /// with an EXPLICIT width (a LengthPx/Percentage slot, INCLUDING the legal zero),
    /// <c>margin-left/right: auto</c> (a Keyword slot — the initial unset margin is 0, so
    /// Keyword = the authored <c>auto</c>) absorb the leftover of
    /// <paramref name="distributionRangePx"/>: both auto → CENTRED (equal split); one auto →
    /// that side takes the rest. A non-positive leftover clamps the auto side(s) at 0
    /// (§10.3.3's over-constrained rule). The range is the SAME inline range that drives
    /// placement (post-PR-#164 review P1: the outer dispatch passes the FLOAT-ADJUSTED
    /// available range — the engine's cycle-1 model places in-flow blocks within it, so the
    /// distribution must match or a centred box drifts; the recursion passes the parent's
    /// content box). A box with AUTO width keeps the fill behavior (auto margins read 0).
    /// <see cref="BoxKind.FlexContainer"/> / <see cref="BoxKind.GridContainer"/> participate
    /// too (PR #204 review [P2]) now that they honor their own explicit <c>width</c> — a
    /// flex/grid container is a block-level box in normal flow, so §10.3.3 auto-margins apply;
    /// table + anonymous kinds stay untouched (they own their inline-size logic).</summary>
    private static void ResolveAutoInlineMargins(
        Box child, double borderBoxInlineSize, double distributionRangePx,
        ref double marginInlineStart, ref double marginInlineEnd)
    {
        // BlockReplacedElement included (img-pipeline cycle) — `display: block; margin: 0 auto`
        // is the canonical image-centering idiom; the pre-pass gave it an explicit used width.
        // FlexContainer / GridContainer included (PR #204 review [P2]) — `display: flex;
        // width: 200px; margin: 0 auto` centers the container, matching the explicit-width
        // gate in ResolveInFlowBorderBoxInlineSize (both sets must stay in lockstep).
        if (child.Kind is not (BoxKind.BlockContainer or BoxKind.ListItem or BoxKind.BlockReplacedElement
            or BoxKind.FlexContainer or BoxKind.GridContainer)) return;
        // EXPLICIT width (a tag test, so the legal `width: 0` / `0%` distributes too — post-PR-#164
        // review P3) OR an explicit `max-width` (a `width: auto` block that max-width clamps below the
        // range still has a definite used width, so `margin: 0 auto` centers it — CSS 2.2 §10.3.3 +
        // §10.4; the `borderBoxInlineSize` passed in is already the max-width-clamped used width, so the
        // leftover below is 0 when max-width doesn't actually clamp → byte-identical for the fill case).
        // A plain auto-width box with no max-width keeps the fill behavior (auto margins read 0).
        if (!HasExplicitWidth(child) && !HasExplicitMaxWidth(child)) return;
        var leftAuto = child.Style.Get(PropertyId.MarginLeft).Tag == ComputedSlotTag.Keyword;
        var rightAuto = child.Style.Get(PropertyId.MarginRight).Tag == ComputedSlotTag.Keyword;
        if (!leftAuto && !rightAuto) return;
        var leftover = distributionRangePx - borderBoxInlineSize
            - (leftAuto ? 0 : marginInlineStart) - (rightAuto ? 0 : marginInlineEnd);
        if (leftover <= 0)
        {
            if (leftAuto) marginInlineStart = 0;
            if (rightAuto) marginInlineEnd = 0;
            return;
        }
        if (leftAuto && rightAuto)
        {
            marginInlineStart = leftover / 2;
            marginInlineEnd = leftover / 2;
        }
        else if (leftAuto)
        {
            marginInlineStart = leftover;
        }
        else
        {
            marginInlineEnd = leftover;
        }
    }

    /// <summary>Per Phase 3 Task 11 cycle 1 sub-cycle 1 — compute the
    /// inline-only block's full pre-emit picture: the inline pass
    /// result + the geometry the fragment will be drawn with. Used
    /// by both <see cref="DispatchInlineOnlyBlock"/> (the main-loop
    /// path that needs the metrics for resolver consultation +
    /// emit) AND <see cref="EmitInlineOnlyBlockInRecursionSplitting"/>
    /// (the recursive-subtree path per Finding #2 that emits — slicing
    /// the lines across pages when needed — at a caller-provided
    /// position without resolver consultation).
    ///
    /// <para>Returns <see langword="null"/> when the inline pass
    /// produces no content (no TextRuns OR all collapse to empty
    /// after preprocessing) — caller emits NO fragment.</para>
    ///
    /// <para>The <c>NotSupported</c> branch returns a sentinel +
    /// the diagnostic is emitted by the caller (which has access to
    /// the layout's diagnostic sink). Caller treats sentinel the
    /// same as "no content" (no fragment, advance loop).</para></summary>
    /// <summary>Per `inline-only-block-line-splitting` (PR #220 review [P2]) — a cache-aware wrapper over
    /// <see cref="ComputeInlineOnlyBlockLayout"/>. The multi-page driver re-runs the whole layout once
    /// per page, so a paragraph that splits across N pages was re-SHAPED (all text + every inline-block
    /// atomic's content) every page. The shaped computation is page-invariant + deterministic for the
    /// block + its content inline size, so it is cached (only in a real <see cref="MeasurePurpose.Layout"/>
    /// pass — a speculative measure probes at other widths) and reused on resume pages; a cache hit skips
    /// the re-shape AND the one-time diagnostics (already emitted on the page that first shaped it).
    /// Byte-identical: a reused computation equals a re-shape.</summary>
    private InlineOnlyBlockComputation? ComputeInlineOnlyBlockLayoutCached(
        Box inlineOnlyBlock,
        InlineOnlyBlockMetrics metrics,
        double containingInlineSize,
        out string? notSupportedMessage,
        out int atomicInlineSkipCount,
        CancellationToken cancellationToken)
    {
        if (_measurePurpose == MeasurePurpose.Layout && _inlineOnlyMeasureCache is { } cache
            && cache.TryGet(inlineOnlyBlock, containingInlineSize, out var cached)
            && cached is InlineOnlyBlockComputation hit)
        {
            notSupportedMessage = null;   // the diagnostics fired on the page that first shaped it
            atomicInlineSkipCount = 0;
            return hit;
        }
        var computation = ComputeInlineOnlyBlockLayout(
            inlineOnlyBlock, metrics, containingInlineSize,
            out notSupportedMessage, out atomicInlineSkipCount, cancellationToken);
        if (computation is { } c && _measurePurpose == MeasurePurpose.Layout
            && _inlineOnlyMeasureCache is { } store)
            store.Store(inlineOnlyBlock, containingInlineSize, c);
        return computation;
    }

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
        //   * `width` set explicitly (a LengthPx/Percentage slot,
        //     INCLUDING the legal zero; a PERCENTAGE resolves against
        //     the containing block's inline size, body-percent cycle)
        //     → border-box per the box's `box-sizing`.
        //   * `width: auto` → border-box = full available content
        //     area minus inline margins (the in-flow-fill behavior).
        // True `auto` resolution per §10.3.3 (with the margin
        // distribution algorithm) lands in cycle 3 of the BLOCK
        // layouter; sub-cycle 1's approximation matches the
        // regular block path's current cycle-1 behavior.
        // EXPLICIT-width semantics (PR #165 review P3): the legal `width: 0` / `0%` sizes a
        // zero-content border box (insets only — its text can't fit, the documented
        // zero-content-area minimal route below) instead of falling back to fill-width. The
        // tag test is gated to plain block kinds like ResolveInFlowBorderBoxInlineSize: an
        // AnonymousBlock SHARES its parent's style object (Display L3 §3.1 — anonymous boxes
        // take the initial `width: auto`), and a TableCell's width is a table-grid input
        // floored at min-content, so both keep the `> 0` legacy read.
        var explicitWidth = inlineOnlyBlock.Kind is BoxKind.BlockContainer or BoxKind.ListItem
            && HasExplicitWidth(inlineOnlyBlock);
        double borderBoxInlineSize;
        if (metrics.DeclaredWidthPx > 0 || explicitWidth)
        {
            // Body `box-sizing` (box-sizing cycle): border-box → the declared width IS the
            // border box (floored at the insets); content-box (initial) adds them — the
            // shared mapping.
            var inlineInsets = metrics.BorderInlineStart + metrics.BorderInlineEnd
                + metrics.PaddingInlineStart + metrics.PaddingInlineEnd;
            borderBoxInlineSize = DeclaredWidthToBorderBox(
                inlineOnlyBlock.Style, metrics.DeclaredWidthPx, inlineInsets);
            // §10.4 — min-width / max-width clamp the explicit width.
            borderBoxInlineSize = inlineOnlyBlock.ClampBorderBoxToMinMax(
                borderBoxInlineSize, PropertyId.MinWidth, PropertyId.MaxWidth, containingInlineSize);
        }
        else
        {
            // §10.3.3 fill + §10.4 min/max clamp (PR #203 review [P2]) — e.g. a
            // `<p style="max-width:600px">` text block caps its auto width.
            borderBoxInlineSize = Math.Max(0,
                containingInlineSize
                - metrics.MarginInlineStart - metrics.MarginInlineEnd);
            borderBoxInlineSize = inlineOnlyBlock.ClampBorderBoxToMinMax(
                borderBoxInlineSize, PropertyId.MinWidth, PropertyId.MaxWidth, containingInlineSize);
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
        // Inline-atomic-boxes cycle — laid-out inline-block content keyed by its box, for
        // EmitInlineOnlyBlockFragment to flush at the atomic's content-box origin. The
        // collector measures each inline-block at `contentInlineSize` (the available line
        // width) + records its buffer here.
        var atomicContents = new Dictionary<Box, InlineAtomicContent>(ReferenceEqualityComparer.Instance);
        atomicInlineSkipCount = CollectInlineTextRuns(
            inlineOnlyBlock, textRuns, inlineOnlyBlock.Style,
            contentInlineSize, atomicContents, cancellationToken);

        InlineLayoutResult inlineResult;
        if (textRuns.Count == 0)
        {
            // Per the spec — no contributing text means no fragment +
            // no cursor advance. (An inline-only block with only
            // whitespace-collapsed runs, e.g., a <p> &lt;span&gt;&lt;/span&gt;,
            // hits this path.)
            return null;
        }

        // Sub-cycle 1 — hard-coded shaping script + shaping language (UAX #24 + BCP 47
        // SHAPING plumbing still deferred). HYPHENATION, however, now routes by the block's
        // effective `lang` — see the hyphenator argument below.
        // _shaperResolver is guaranteed non-null by the caller's
        // IsInlineOnlyBlockContainer gate + the diagnostic branch.
        // Per Finding #6 — wrap with try/catch so per-source-TextRun
        // mismatches that still throw (KeepAll) degrade to a Warning
        // diagnostic instead of bringing the block layouter down.

        // Hyphenation language routing — resolve the hyphenator for `hyphens: auto` from the block's
        // effective HTML `lang` (the nearest `lang` attribute up the ancestor chain, e.g. <html lang="de">).
        // ResolveOrDefault returns the bundled English hyphenator for an unregistered language, so output
        // is byte-identical to the pre-routing behavior UNLESS a NetPdf.Languages.* pack has registered that
        // language. The shaping `language` argument below stays "en": changing it would alter shaping
        // output; script/shaping-language routing is a separate follow-up.
        var hyphenationLanguage = ResolveEffectiveLanguage(inlineOnlyBlock.SourceElement);
        try
        {
            inlineResult = InlineLayouter.LayoutPerRun(
                sourceTextRuns: textRuns,
                availableInlineSize: contentInlineSize,
                resolver: _shaperResolver!,
                scriptIso15924: "Latn",
                language: "en",
                // Direction pipeline — the paragraph base direction is the block's
                // computed `direction` (CSS Writing Modes 4 §2.1), not a hardcoded
                // LTR. An RTL block (`direction: rtl`) lays its inline formatting
                // context out right-to-left (bidi base level 1); a default/LTR block
                // is byte-identical to the pre-pipeline output.
                paragraphDirection: inlineOnlyBlock.Style.ReadParagraphDirection(),
                hyphenator: HyphenationRegistry.ResolveOrDefault(hyphenationLanguage),
                cancellationToken: cancellationToken,
                // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 5
                // — pass through the layouter's intrinsic-sizing-mode
                // flag so the inline pass downgrades break-word
                // opportunities for the auto-table-layout speculative
                // min-content pass.
                intrinsicSizingMode: _intrinsicSizingMode);
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
        // declared line-height when present (incl. an explicit 0 — a collapsed line box); fall back to
        // 1.2 × font-size for `line-height: normal` (null). CSS Text L3 §3 + CSS Inline L3 §3.5 say the
        // used `line-height` is the COMPUTED value (`normal` → font's intrinsic line metric); reading the
        // keyword + the font metric tables remains sub-cycle 2 work — 1.2 is the de-facto Web default for
        // `line-height: normal` across major UAs.
        double lineHeight;
        if (metrics.LineHeightOverridePx is { } overridePx)
        {
            lineHeight = overridePx;
        }
        else
        {
            var fontSizePx = inlineOnlyBlock.Style.ReadLengthPxOrDefault(
                PropertyId.FontSize, defaultPx: 16);
            lineHeight = fontSizePx * 1.2;
        }
        // Inline-atomic-boxes cycle — inline `<img>` atomics on a line grow that line to fit them and
        // are emitted as positioned child fragments. The walk returns per-line heights (null when the
        // block has no atomic, keeping the uniform text path byte-identical), the atomics' placements
        // relative to the content box, and the resulting content block size.
        var (perLineHeightsPx, perLineBaselineTopPx, atomicPlacements, contentBlockSize) =
            ComputeInlineAtomicLayout(inlineResult, lineHeight, inlineOnlyBlock.Style, contentInlineSize);
        var borderBoxBlockSize = contentBlockSize
            + metrics.BorderBlockStart + metrics.PaddingBlockStart
            + metrics.PaddingBlockEnd + metrics.BorderBlockEnd;

        return new InlineOnlyBlockComputation(
            InlineResult: inlineResult,
            BorderBoxInlineSize: borderBoxInlineSize,
            BorderBoxBlockSize: borderBoxBlockSize,
            ContentBlockSize: contentBlockSize,
            PerLineHeightsPx: perLineHeightsPx,
            PerLineBaselineTopPx: perLineBaselineTopPx,
            AtomicPlacements: atomicPlacements,
            InlineBlockContents: atomicContents);
    }

    /// <summary>Inline-atomic-boxes cycle — second pass over an <see cref="InlineLayoutResult"/> that
    /// places inline-atomic boxes (inline <c>&lt;img&gt;</c>) on their lines. For each line the height
    /// is <c>max(text line-height, tallest atomic on the line)</c> so a tall atomic grows its line box;
    /// each atomic is positioned with its bottom on the line's text baseline
    /// (<c>vertical-align: baseline</c>, CSS 2.2 §10.8) at the inline offset reached by the preceding
    /// slices' advances. Returns <c>(null, empty, lines × lineHeight)</c> when the block has no atomic,
    /// so the text-only path is byte-identical.
    ///
    /// <para><b>Approximations (first cut).</b> The baseline uses an approximate font ascent/descent
    /// (0.8 / −0.2 em — the layout layer has no font-metric access; the painter uses the REAL font
    /// metrics for glyphs, so an atomic's bottom aligns to the text baseline within typical-font
    /// tolerance); the line's <c>text-align</c> offset shifts the atomic WITH its text (body
    /// text-align cycle — center / right / end, plus the direction-relative <c>start</c>/<c>end</c>
    /// so an RTL block's atomic shifts to the RIGHT edge; <c>justify</c> still leaves it
    /// start-relative); and only <c>vertical-align: baseline</c> is honoured.</para></summary>
    private (IReadOnlyList<double>? PerLineHeightsPx,
             IReadOnlyList<double>? PerLineBaselineTopPx,
             IReadOnlyList<InlineAtomicPlacement> Placements,
             double ContentBlockSize)
        ComputeInlineAtomicLayout(
            InlineLayoutResult inlineResult, double textLineHeightPx, ComputedStyle blockStyle,
            double contentInlineSize)
    {
        var lines = inlineResult.Lines;
        var shapedRuns = inlineResult.ShapedRuns;
        // Body text-align cycle — each line shifts by (content width − line advance) × factor, the
        // SAME shift TextPainter applies to the glyph lines (so an inline atomic moves WITH its
        // text under text-align: center / right, and to the RIGHT edge under an RTL `start` —
        // ReadInlineAlignFactor resolves start/end against `direction`). A factor of 0 leaves the
        // start-relative placement byte-identical to the pre-cycle output.
        var alignFactor = blockStyle.ReadInlineAlignFactor();

        // Approximate Latin font ascent/descent as a fraction of font-size (consistent with the
        // codebase's 1.2 line-height factor: ascent 0.8em + |descent| 0.2em + 0.2em leading).
        var fontSizePx = blockStyle.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16);
        var ascentPx = fontSizePx * 0.8;
        var descentPx = fontSizePx * -0.2;
        // The text strut's ascent ABOVE / descent BELOW its baseline, arranged so a strut-only line
        // reproduces TextPainter's centred baseline EXACTLY (ascentAbove = lineHeight/2 + 0.3em, the
        // two summing to the line height) — the §10.8.1 max-ascent line-box model (Plan C: used ONLY
        // on a line carrying a baseline-aligned inline-block; img/text lines keep the current model).
        var textAscentAbovePx = textLineHeightPx / 2.0 + 0.3 * fontSizePx;
        var textDescentBelowPx = textLineHeightPx - textAscentAbovePx;

        var preRuns = inlineResult.PreprocessedRuns;
        var perLineHeights = new double[lines.Length];
        var perLineBaselines = new double[lines.Length];   // offset from line top; NaN = painter default.
        var anyAtomic = false;
        var anyMaxAscent = false;                           // any line uses the max-ascent model (→ baselines).
        var anyTextShift = false;                           // any inline-level text run has a vertical-align shift.
        for (var li = 0; li < lines.Length; li++)
        {
            double maxAtomicMarginBoxHeight = 0;
            double maxLineEdgeTextHeight = 0;   // text vertical-align line-growth — top/bottom run content-box floor.
            var ascentAbove = textAscentAbovePx;
            var descentBelow = textDescentBelowPx;
            var lineNeedsMaxAscent = false;
            foreach (var slice in lines[li].Slices)
            {
                if (shapedRuns[slice.ShapedRunIndex].Atomic is { } a)
                {
                    anyAtomic = true;
                    if (a.MarginBoxHeightPx > maxAtomicMarginBoxHeight)
                        maxAtomicMarginBoxHeight = a.MarginBoxHeightPx;
                    // vertical-align as (keyword, numeric RAISE px) — a <length>/<percentage> reads as
                    // keyword 0 (baseline) + a raise (+ up, % of the box's OWN line-height); a keyword
                    // reads its index + 0 raise.
                    var (valign, numericRaisePx) = ReadAtomicVerticalAlign(a.Box.Style);
                    // The line uses the §10.8.1 max-ascent MODEL when an atomic is either a BASELINE-owning
                    // inline-block (baseline/sub/super) OR ANY baseline-relative SHIFTED atomic — including
                    // an IMG-ish atomic with super/sub/numeric/middle/text-* (post-PR-#193 review P1: those
                    // were placed but NOT sized into the line, so a raised box spilled above the line top).
                    // A plain baseline/img + top/bottom keep the centred fallback model (byte-identical).
                    if ((a.BaselineFromBorderTopPx is not null && IsBaselineValign(valign))
                        || IsBaselineRelativeShifted(valign, numericRaisePx))
                    {
                        lineNeedsMaxAscent = true;
                        anyMaxAscent = true;
                    }
                    // Grow the baseline-relative extents from EVERY atomic per its vertical-align — so a
                    // tall middle / text-top / text-bottom (or baseline / img) sibling is CONTAINED by the
                    // max-ascent line without overflowing (post-PR-#192 review P1; was only the raw margin
                    // box height, which left a baseline-relative sibling outside the box). top / bottom are
                    // line-edge-relative → they contribute only the margin-box-height floor below. A numeric
                    // raise shifts the extents UP (raise > 0 grows the ascent, shrinks the descent).
                    var (extentAbove, extentBelow) =
                        AtomicBaselineExtents(a, valign, ascentPx, descentPx, fontSizePx);
                    extentAbove += numericRaisePx;
                    extentBelow -= numericRaisePx;
                    if (extentAbove > ascentAbove) ascentAbove = extentAbove;
                    if (extentBelow > descentBelow) descentBelow = extentBelow;
                }
                else
                {
                    // text vertical-align line-growth cycle — an inline-level TEXT run with a sub/super/
                    // numeric vertical-align shifts its glyph baseline (TextPainter applies the SAME shift
                    // via the shared InlineVerticalAlign helper). Grow the line so the shifted text is
                    // CONTAINED (a super run's top no longer spills into the line above): the strut shifts
                    // UP by the raise (ascent += raise, descent −= raise). raise 0 (baseline / plain /
                    // block-direct text — the gate) leaves the line byte-identical.
                    var run = shapedRuns[slice.ShapedRunIndex];
                    var runStyle = preRuns[run.Source.SourceTextRunIndex].Style;
                    var runFontSizePx = runStyle.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16);
                    var raisePx = NetPdf.Layout.Inline.InlineVerticalAlign.TextRaisePx(
                        runStyle, blockStyle, runFontSizePx);
                    if (raisePx != 0.0)
                    {
                        lineNeedsMaxAscent = true;
                        anyMaxAscent = true;
                        anyTextShift = true;
                        // Contain the shift with the SHIFTED RUN's OWN strut (its own font-size + line-
                        // height), NOT the block's — a font-size:32px super span must grow the line by its
                        // own ~32px extent; reusing the block's 16px strut under-grows it and the raised
                        // glyph spills above the line top (post-PR-#194 review P2). Mirrors the block
                        // textAscentAbovePx / textDescentBelowPx formula, scaled to the run (so a run of the
                        // block's own size + line-height stays byte-identical to the prior block-strut math).
                        var runLineHeightPx =
                            NetPdf.Layout.Inline.InlineVerticalAlign.OwnLineHeightPx(runStyle, runFontSizePx);
                        var runAscentAbovePx = runLineHeightPx / 2.0 + 0.3 * runFontSizePx;
                        var runDescentBelowPx = runLineHeightPx - runAscentAbovePx;
                        if (runAscentAbovePx + raisePx > ascentAbove) ascentAbove = runAscentAbovePx + raisePx;
                        if (runDescentBelowPx - raisePx > descentBelow) descentBelow = runDescentBelowPx - raisePx;
                    }
                    else
                    {
                        // text vertical-align line-growth cycle (PR 3 task 7) — a LINE-EDGE-aligned inline
                        // text run (top/bottom/middle/text-top/text-bottom) GROWS the line so a run TALLER
                        // than the baseline-sized line is CONTAINED (it previously overflowed — the PR #195
                        // deferral). text-top/text-bottom/middle grow the baseline-relative ascent/descent
                        // (the max-ascent model, mirroring the atomic AtomicBaselineExtents path); top/bottom
                        // are line-edge-relative so they contribute an inline-box FLOOR instead. The box
                        // height is the run's USED line-height (post-PR-#197 review P2 — a tall line-height,
                        // not only a large font-size, grows the line). The painter positions the run via the
                        // SAME InlineVerticalAlign.TextLineEdgeBaselineTopPx using the grown line metrics, so
                        // layout + paint can't disagree. baseline/plain/block-direct text → (0,0,0), so those
                        // lines stay byte-identical.
                        var runLineHeightPx =
                            NetPdf.Layout.Inline.InlineVerticalAlign.OwnLineHeightPx(runStyle, runFontSizePx);
                        var (edgeAbove, edgeBelow, edgeFloor) =
                            NetPdf.Layout.Inline.InlineVerticalAlign.TextLineEdgeGrowth(
                                runStyle, blockStyle, runLineHeightPx, ascentPx, descentPx, fontSizePx);
                        if (edgeAbove != 0.0 || edgeBelow != 0.0)
                        {
                            lineNeedsMaxAscent = true;
                            anyMaxAscent = true;
                            anyTextShift = true;
                            if (edgeAbove > ascentAbove) ascentAbove = edgeAbove;
                            if (edgeBelow > descentBelow) descentBelow = edgeBelow;
                        }
                        if (edgeFloor > 0.0)
                        {
                            // top / bottom — line-edge-relative: the run must FIT the line box (current
                            // model, centred baseline + the floor grows the line, like a top/bottom atomic).
                            anyTextShift = true;
                            if (edgeFloor > maxLineEdgeTextHeight) maxLineEdgeTextHeight = edgeFloor;
                        }
                    }
                }
            }
            if (lineNeedsMaxAscent)
            {
                // §10.8.1 max-ascent model — the baseline at max-ascent-above so a baseline-aligned
                // inline-block sits ON the text baseline without overflowing the line top; the line is
                // at least the sum AND tall enough for any non-baseline (top/bottom) sibling's margin box
                // or line-edge text run's content box (text vertical-align line-growth).
                perLineHeights[li] = Math.Max(ascentAbove + descentBelow,
                    Math.Max(maxAtomicMarginBoxHeight, maxLineEdgeTextHeight));
                perLineBaselines[li] = ascentAbove;
            }
            else
            {
                // Current model (byte-identical when every atomic is baseline/img) — the line grows to the
                // tallest atomic MARGIN box or line-edge (top/bottom) text run; the baseline stays centred
                // (NaN → real font metrics).
                perLineHeights[li] = Math.Max(textLineHeightPx,
                    Math.Max(maxAtomicMarginBoxHeight, maxLineEdgeTextHeight));
                perLineBaselines[li] = double.NaN;
            }
        }

        if (!anyAtomic && !anyTextShift)
        {
            return (null, null, System.Array.Empty<InlineAtomicPlacement>(),
                lines.Length * textLineHeightPx);
        }

        // No inline atomics to place → justification (which only SHIFTS atomics; the painter justifies the
        // glyphs itself) is irrelevant. For a text-shift-only block (a sub/super/line-edge run grew a line)
        // just sum the per-line heights for the content block size + return the per-line baselines; skip the
        // justify concat + the per-line gap scan entirely (post-PR-#195 review P3).
        if (!anyAtomic)
        {
            var textShiftBlockSize = 0.0;
            foreach (var h in perLineHeights) textShiftBlockSize += h;
            return (perLineHeights, anyMaxAscent ? perLineBaselines : null,
                System.Array.Empty<InlineAtomicPlacement>(), textShiftBlockSize);
        }

        // text-align: justify cycle — an inline atomic on a JUSTIFIED line shifts RIGHT by the inter-word
        // gaps accumulated BEFORE it, mirroring TextPainter's EmitJustifiedLine pen EXACTLY (the shared
        // InlineJustify helper counts the same opportunities) so the atomic stays glued to its justified
        // text. justifyConcatText is the same source-run concatenation the painter rebuilds (the
        // preprocessed runs' text in document order — a glyph's Cluster indexes straight into it).
        var justifies = blockStyle.ReadInlineJustify();
        var justifyLastLine = justifies && blockStyle.ReadInlineJustifyAll();
        string? justifyConcatText = null;
        if (justifies)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var pr in preRuns) sb.Append(pr.Text);
            justifyConcatText = sb.ToString();
        }

        var placements = new List<InlineAtomicPlacement>();
        var cumulativeTopPx = 0.0;
        for (var li = 0; li < lines.Length; li++)
        {
            var lineTopPx = cumulativeTopPx;
            var thisLineHeightPx = perLineHeights[li];
            // The line baseline: the §10.8.1 max-ascent offset for a baseline-aligned line, else the
            // centred em-box baseline (mirrors TextPainter's centred formula for img/text lines).
            var halfLeadingPx = (thisLineHeightPx - (ascentPx - descentPx)) / 2.0;
            var baselineTopPx = double.IsNaN(perLineBaselines[li])
                ? lineTopPx + halfLeadingPx + ascentPx
                : lineTopPx + perLineBaselines[li];
            // The line's text-align shift (matching TextPainter's glyph shift): the free space
            // times the factor, clamped ≥ 0 (an overflowing line stays start-aligned).
            var lineAlignOffsetPx = alignFactor != 0.0
                ? Math.Max(0.0, (contentInlineSize - lines[li].TotalAdvance) * alignFactor)
                : 0.0;
            // Per-line justify plan (the SAME gate + math as TextPainter): the LAST line justifies only
            // under justify-all; a non-last line justifies unless it's forced-break-terminated (an internal
            // <br>) — EXCEPT under justify-all, which justifies EVERY line including forced-break ones
            // (PR-3 task 9). The last line of a block carries EndsWithMandatoryBreak (content end), so it's
            // gated on justifyLastLine, NOT the break flag. justify and center/right are mutually exclusive
            // (alignFactor is 0 for justify), so lineAlignOffsetPx is 0 and justifyAccumPx shifts the atomic.
            var isLastLine = li == lines.Length - 1;
            var justifyExtraPerGapPx = 0.0;
            var justifyGapCount = 0;
            if (justifies && justifyConcatText is not null
                && (isLastLine ? justifyLastLine : (!lines[li].EndsWithMandatoryBreak || justifyLastLine)))
            {
                justifyGapCount = NetPdf.Layout.Inline.InlineJustify.InteriorGapCount(
                    lines[li], shapedRuns, justifyConcatText);
                if (justifyGapCount > 0)
                {
                    var freePx = contentInlineSize - lines[li].TotalAdvance;
                    if (freePx > 0) justifyExtraPerGapPx = freePx / justifyGapCount;
                }
            }
            var xCursorPx = 0.0;
            var justifyAccumPx = 0.0;        // gaps added so far on this line (mirrors the painter's pen).
            var justifyOpportunitiesUsed = 0;
            foreach (var slice in lines[li].Slices)
            {
                var sliceStartXPx = xCursorPx;
                xCursorPx += slice.SliceAdvance;
                var sliceRun = shapedRuns[slice.ShapedRunIndex];
                if (sliceRun.Atomic is { } a)
                {
                    // vertical-align (CSS 2.2 §10.8.1) places the atomic's border box within the line box:
                    // baseline (the default) aligns the inline-block's own baseline / an img's margin-box
                    // bottom with the line baseline; top/bottom align the margin box to the line edges;
                    // middle/text-top/text-bottom to the line centre / text ascent / text descent; a
                    // <length>/<percentage> RAISES the baseline by that distance (% of the box's OWN
                    // line-height). The emitted BORDER box is what ImagePainter / the content flush paint.
                    var (valign, numericRaisePx) = ReadAtomicVerticalAlign(a.Box.Style);
                    var borderBoxTopPx = ComputeAtomicBorderBoxTop(
                        a, valign, lineTopPx, thisLineHeightPx, baselineTopPx - numericRaisePx,
                        ascentPx, descentPx, fontSizePx);
                    placements.Add(new InlineAtomicPlacement(
                        a.Box, li,
                        lineAlignOffsetPx + justifyAccumPx + sliceStartXPx + a.MarginInlineStartPx,
                        borderBoxTopPx, a.BorderBoxWidthPx, a.BorderBoxHeightPx));
                }
                // Accumulate THIS slice's interior word-gaps AFTER placing any atomic in it (an atomic is
                // its own slice, so its shift comes from PRIOR slices' gaps) — exactly the painter's pen.
                else if (justifyExtraPerGapPx > 0.0)
                {
                    for (var g = 0; g < slice.GlyphLength && justifyOpportunitiesUsed < justifyGapCount; g++)
                    {
                        if (NetPdf.Layout.Inline.InlineJustify.IsJustifySpace(
                                justifyConcatText!, sliceRun.Glyphs[slice.GlyphStart + g].Cluster))
                        {
                            justifyAccumPx += justifyExtraPerGapPx;
                            justifyOpportunitiesUsed++;
                        }
                    }
                }
            }
            cumulativeTopPx += thisLineHeightPx;
        }
        // PerLineBaselines is meaningful only when a baseline-aligned inline-block OR a shifted text run
        // forced the max-ascent model on some line; otherwise return null so the painter keeps its
        // real-metric baseline for every line (plain img/text fragments stay byte-identical).
        return (perLineHeights, anyMaxAscent ? perLineBaselines : null, placements, cumulativeTopPx);
    }

    /// <summary>vertical-align cycle — whether a keyword aligns by the BASELINE (so a baseline-owning
    /// inline-block drives the §10.8.1 max-ascent baseline): <c>baseline</c>(0), <c>sub</c>(1),
    /// <c>super</c>(2) — sub / super are now real ±em baseline shifts (vertical-align completion). The
    /// box-affecting keywords <c>text-top</c>(3) / <c>text-bottom</c>(4) / <c>middle</c>(5) /
    /// <c>top</c>(6) / <c>bottom</c>(7) are placed by line/text edges instead. Indices MUST match
    /// <c>VerticalAlignResolver</c> (NetPdf.Css).</summary>
    private static bool IsBaselineValign(int valign) => valign is 0 or 1 or 2;

    /// <summary>inline-block baseline overflow exception (CSS 2.2 §10.8.1) — whether a box's computed
    /// <c>overflow</c> is <c>visible</c> on BOTH axes (keyword 0; <c>hidden</c>/<c>clip</c>/<c>scroll</c>/
    /// <c>auto</c> are 1–4). Only a visible-overflow inline-block takes its baseline from its last line
    /// box; otherwise the baseline is the bottom margin edge.</summary>
    private static bool IsOverflowVisible(ComputedStyle style) =>
        style.ReadKeywordOrDefault(PropertyId.OverflowX, defaultIndex: 0) == 0
        && style.ReadKeywordOrDefault(PropertyId.OverflowY, defaultIndex: 0) == 0;

    /// <summary>vertical-align completion — whether a vertical-align is placed BASELINE-RELATIVE AND
    /// SHIFTED off the baseline, so the line box MUST use the §10.8.1 max-ascent model to CONTAIN it
    /// (even for an img-ish atomic with no own line box — post-PR-#193 review P1; the centred fallback
    /// model doesn't reserve room for the shift). True for <c>sub</c>(1) / <c>super</c>(2) /
    /// <c>text-top</c>(3) / <c>text-bottom</c>(4) / <c>middle</c>(5) and for ANY non-zero numeric raise.
    /// False for plain <c>baseline</c>(0, no raise) — the centred model contains it, byte-identical —
    /// and <c>top</c>(6) / <c>bottom</c>(7) — line-edge-relative, contained by the margin-box-height
    /// floor.</summary>
    private static bool IsBaselineRelativeShifted(int valign, double numericRaisePx) =>
        valign is 1 or 2 or 3 or 4 or 5 || numericRaisePx != 0.0;

    /// <summary>vertical-align length cycle (CSS 2.2 §10.8.1) — read an atomic's <c>vertical-align</c> as
    /// (keyword index, numeric RAISE in px). A <c>&lt;length&gt;</c> raises the box by the length
    /// (positive up, negative down); a <c>&lt;percentage&gt;</c> by that fraction of the ELEMENT's OWN
    /// line-height (§10.8.1 — not the parent's or the grown line box; post-PR-#193 review P2). A keyword
    /// returns (index, 0). Keyword 0 (baseline) + a non-zero raise = a numeric shift, placed like a
    /// shifted baseline (so the inline-block's own-baseline / img margin-box-bottom alignment rides
    /// it).</summary>
    private static (int Keyword, double NumericRaisePx) ReadAtomicVerticalAlign(ComputedStyle style)
    {
        var slot = style.Get(PropertyId.VerticalAlign);
        return slot.Tag switch
        {
            ComputedSlotTag.Keyword => (slot.AsKeyword(), 0.0),
            ComputedSlotTag.LengthPx => (0, slot.AsLengthPx()),
            ComputedSlotTag.Percentage => (0, slot.AsPercentage() / 100.0 * OwnLineHeightPx(style)),
            _ => (0, 0.0),
        };
    }

    /// <summary>The element's OWN computed line-height (px) — a declared length, else font-size × 1.2
    /// (the normal-line-height factor). The base for a vertical-align <c>%</c> (CSS 2.2 §10.8.1 — the
    /// element's own line-height, not the parent's or the grown line box).</summary>
    private static double OwnLineHeightPx(ComputedStyle style)
    {
        var fontSizePx = style.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16);
        // line-height cycle — number/length/% honored; null = `normal` → font-size × 1.2. An explicit
        // line-height: 0 returns a 0 % base (a 0% vertical-align of a 0 line-height is 0).
        return style.ReadLineHeightPx(fontSizePx) ?? fontSizePx * 1.2;
    }

    /// <summary>vertical-align cycle (CSS 2.2 §10.8.1) — an atomic's extent ABOVE / BELOW the line
    /// baseline for its <paramref name="valign"/>, so the max-ascent line box grows to CONTAIN it.
    /// <c>middle</c> / <c>text-top</c> / <c>text-bottom</c> are positioned relative to the baseline /
    /// parent text metrics (so they have definite baseline-relative extents); <c>top</c> / <c>bottom</c>
    /// are line-EDGE-relative (circular — they're contained by the margin-box-height floor instead, so
    /// they contribute nothing here); <c>baseline</c> / <c>sub</c> / <c>super</c> / a numeric value use
    /// the atomic's own baseline split (<see cref="NetPdf.Layout.Inline.InlineAtomic.AscentAbovePx"/>).
    /// A negative extent (a short box that doesn't reach past the baseline) is harmless — it loses the
    /// <c>Math.Max</c> against the text strut. Indices match <see cref="ComputeAtomicBorderBoxTop"/>.</summary>
    private static (double Above, double Below) AtomicBaselineExtents(
        in NetPdf.Layout.Inline.InlineAtomic a, int valign,
        double ascentPx, double descentPx, double fontSizePx)
    {
        var mbH = a.MarginBoxHeightPx;
        return valign switch
        {
            5 => (0.25 * fontSizePx + mbH / 2.0, mbH / 2.0 - 0.25 * fontSizePx),  // middle
            3 => (ascentPx, mbH - ascentPx),                                       // text-top
            4 => (mbH + descentPx, -descentPx),                                    // text-bottom (descent < 0)
            6 or 7 => (0.0, 0.0),                                                   // top / bottom — line-edge
            // super / sub — the baseline split shifted UP / DOWN by the raise / drop.
            2 => (a.AscentAbovePx + SuperRiseEm * fontSizePx, a.DescentBelowPx - SuperRiseEm * fontSizePx),
            1 => (a.AscentAbovePx - SubDropEm * fontSizePx, a.DescentBelowPx + SubDropEm * fontSizePx),
            _ => (a.AscentAbovePx, a.DescentBelowPx),                              // baseline
        };
    }

    /// <summary>vertical-align <c>sub</c> / <c>super</c> raise / drop, as a fraction of the parent
    /// font-size (CSS 2.2 §10.8.1 leaves the exact amount to the UA — these approximate typical
    /// superscript / subscript offsets; the layout layer has no font OS/2 super/subscript metrics).</summary>
    private const double SuperRiseEm = 0.3;
    private const double SubDropEm = 0.2;

    /// <summary>vertical-align cycle (CSS 2.2 §10.8.1) — the atomic's BORDER-box top within the line box
    /// for its <paramref name="valign"/> keyword index (see <see cref="IsBaselineValign"/> for the
    /// mapping). <paramref name="ascentPx"/> / <paramref name="descentPx"/> are the parent text's
    /// approximate ascent / descent (descent is negative); <paramref name="fontSizePx"/> feeds the
    /// <c>middle</c> half-x-height (≈ 0.25em) offset + the <c>sub</c> / <c>super</c> shift. A numeric
    /// <c>&lt;length&gt;</c> / <c>&lt;%&gt;</c> reads back as index 0 (baseline) here — the numeric
    /// shift is applied by the caller (vertical-align length cycle) before this runs.</summary>
    private static double ComputeAtomicBorderBoxTop(
        in NetPdf.Layout.Inline.InlineAtomic a, int valign,
        double lineTopPx, double lineHeightPx, double baselineTopPx,
        double ascentPx, double descentPx, double fontSizePx)
    {
        // Place the border box so the atomic's OWN baseline (an inline-block's last-line baseline) — or
        // its margin-box bottom (an img-ish atomic) — sits on `atBaseline`. Fields hoisted to locals so
        // the local function doesn't capture the `in` parameter (CS1628).
        var baselineFromTop = a.BaselineFromBorderTopPx;
        var marginEndPx = a.MarginBlockEndPx;
        var borderBoxHeightPx = a.BorderBoxHeightPx;
        double OnBaseline(double atBaseline) => baselineFromTop is { } b
            ? atBaseline - b
            : atBaseline - marginEndPx - borderBoxHeightPx;

        return valign switch
        {
            // top — the margin box's TOP edge meets the line-box top.
            6 => lineTopPx + a.MarginBlockStartPx,
            // bottom — the margin box's BOTTOM edge meets the line-box bottom.
            7 => lineTopPx + lineHeightPx - a.MarginBlockEndPx - a.BorderBoxHeightPx,
            // middle — the margin box's vertical centre meets the baseline minus half the x-height.
            5 => baselineTopPx - 0.25 * fontSizePx - a.MarginBoxHeightPx / 2.0 + a.MarginBlockStartPx,
            // text-top — the margin box's TOP edge meets the parent text's content-area top.
            3 => baselineTopPx - ascentPx + a.MarginBlockStartPx,
            // text-bottom — the margin box's BOTTOM edge meets the text content-area bottom.
            4 => baselineTopPx - descentPx - a.MarginBlockEndPx - a.BorderBoxHeightPx,
            // super / sub — the atomic's baseline RAISED / LOWERED off the line baseline (the shifted
            // baseline carries the inline-block's own-baseline or img margin-box-bottom alignment).
            2 => OnBaseline(baselineTopPx - SuperRiseEm * fontSizePx),
            1 => OnBaseline(baselineTopPx + SubDropEm * fontSizePx),
            // baseline (0) / numeric (→0) — the atomic's own baseline / margin-box bottom on the baseline.
            _ => OnBaseline(baselineTopPx),
        };
    }

    /// <summary>Inline-atomic-boxes cycle — an inline-atomic box (inline <c>&lt;img&gt;</c>) positioned
    /// relative to its inline-only block's CONTENT box. <see cref="EmitInlineOnlyBlockFragment"/> adds
    /// the block fragment's border-box origin + the block's leading border + padding to emit the
    /// atomic's own <see cref="BoxFragment"/> (so <c>ImagePainter</c> paints it from the image
    /// cache).</summary>
    private readonly record struct InlineAtomicPlacement(
        Box Box,
        // The 0-based index of the wrapped line this atomic belongs to — recorded at layout so line
        // splitting can filter atomics to a page slice by line (robust to vertical-align / zero-height /
        // negative margins moving the rendered top coordinate — PR #220 review [P2]).
        int LineIndex,
        double ContentInlineOffsetPx,
        double ContentBlockOffsetPx,
        double WidthPx,
        double HeightPx);

    /// <summary>Inline-atomic-boxes cycle (inline-block first cut) — the laid-out content of an
    /// <c>display: inline-block</c> atomic, flushed by <see cref="EmitInlineOnlyBlockFragment"/> at
    /// the atomic's content-box origin (= its placed border-box origin + the inline-block's own
    /// inline-/block-start border + padding). <see cref="IsInlineOnlyRoot"/> mirrors the flex
    /// content-inset rule: an inline-only-root buffer (the inline-block's own text) sits at the
    /// border-box origin (TextPainter insets its glyphs) — NOT inset here; block-child content is
    /// inset by the recorded chrome.</summary>
    private readonly record struct InlineAtomicContent(
        BufferingMeasureSink Buffer,
        bool IsInlineOnlyRoot,
        double ContentInsetInlinePx,
        double ContentInsetBlockPx);

    /// <summary>Per Phase 3 Task 11 cycle 1 sub-cycle 1 — bundle the
    /// pre-emit metrics carried out of
    /// <see cref="ComputeInlineOnlyBlockLayout"/>. Geometry is
    /// border-box-only (matches <see cref="BoxFragment"/>'s
    /// contract).</summary>
    private readonly record struct InlineOnlyBlockComputation(
        InlineLayoutResult InlineResult,
        double BorderBoxInlineSize,
        double BorderBoxBlockSize,
        double ContentBlockSize,
        // Inline-atomic-boxes cycle — per-line heights when the block carries an inline `<img>` atomic
        // (a tall atomic grows its line); null for a text-only block, so the painter's uniform-pitch
        // path is byte-identical. The inline-atomic boxes' own positioned fragments.
        IReadOnlyList<double>? PerLineHeightsPx = null,
        // Inline-block last-line-baseline cycle — per-line baseline offset (from the line top) when a
        // line carries a baseline-aligned inline-block (§10.8.1 max-ascent model); NaN per line = the
        // painter's real-metric baseline. Null for img/text-only blocks → byte-identical.
        IReadOnlyList<double>? PerLineBaselineTopPx = null,
        IReadOnlyList<InlineAtomicPlacement>? AtomicPlacements = null,
        // Inline-block first cut — the laid-out content per inline-block atomic box, flushed at the
        // atomic's content-box origin. Null/empty for a block with no inline-block atomic.
        IReadOnlyDictionary<Box, InlineAtomicContent>? InlineBlockContents = null);

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

    /// <summary>Per CSS Fragmentation L3 §3.1 / §3.2 (PR #207 review [P1]/[P2]) — the
    /// SHARED forced / avoid break-metadata calculation for a child boundary, used by
    /// ALL four block break-decision sites: the top-level loop's block + inline-only
    /// dispatch and <see cref="EmitBlockSubtreeRecursive"/>'s block + inline-only paths.
    /// A FORCED page break is offered when this child's <c>break-before</c> OR the
    /// previous in-flow sibling's <c>break-after</c> forces one (suppressed at a
    /// fragmentainer start, where a forced break would make an empty fragment). The
    /// boundary is marked AVOID when <paramref name="container"/> has <c>break-inside:
    /// avoid</c> (every between-children boundary is an internal break of it), or this
    /// child has <c>break-before: avoid</c>, or the previous sibling has
    /// <c>break-after: avoid</c>.</summary>
    private static (bool Force, bool Avoid, PageParity Parity) ResolveChildBreakMetadata(
        Box container, Box child, Box? prevSibling, bool suppressForce)
    {
        var childForces = !suppressForce && child.Style.ForcesPageBreakBefore();
        var prevForces = !suppressForce
            && prevSibling is not null && prevSibling.Style.ForcesPageBreakAfter();
        var force = childForces || prevForces;
        // CSS Fragmentation L3 §4.3 — combine the forced break's page-parity constraint (left / right /
        // recto / verso) from both sides, "the value on the LATEST element in the flow wins": the child's
        // break-before is later in flow than the prior sibling's break-after, so the child wins when it
        // carries a parity; a child's parity-less `break-before: page` must NOT drop the prior sibling's
        // `break-after: left` (shared helper, mirrors the document-start chain in PdfRenderPipeline).
        var childParity = childForces ? child.Style.ForcedPageBreakParityBefore() : PageParity.Any;
        var prevParity = prevForces ? prevSibling!.Style.ForcedPageBreakParityAfter() : PageParity.Any;
        var parity = ComputedStyleLayoutExtensions.CombineForcedParityLatestWins(prevParity, childParity);
        var avoid = container.Style.AvoidsBreakInside()
            || child.Style.AvoidsPageBreakBefore()
            || (prevSibling is not null && prevSibling.Style.AvoidsPageBreakAfter());
        return (force, avoid, parity);
    }

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
        CancellationToken cancellationToken,
        bool suppressOwnMargins = false,
        // `inline-only-block-line-splitting` — when > 0 the block is RESUMING a prior line
        // split: emit its lines from this 0-based index (skipping the lines already emitted on
        // earlier pages), splitting again if the remainder still overflows.
        int resumeAtLine = 0,
        // box-decoration-break: slice (PR #222 review [P1]) — the resumed slice's cumulative physical
        // decoration offset + the composite box's total physical extent, carried in the continuation.
        double resumeDecorationOffsetPx = 0.0, double resumeDecorationTotalPx = 0.0)
    {
        // Per PR #48 review Copilot — the caller must NOT bump
        // `lastEmittedIdx` + `emittedThisAttempt` on skip paths
        // (null shaper resolver, empty content, NotSupportedException
        // caught). Each return point assigns `emitted` explicitly
        // so downstream pagination decisions see the truthful
        // "did this child put a fragment on the page" state.
        emitted = false;
        cancellationToken.ThrowIfCancellationRequested();

        var metrics = ReadInlineOnlyBlockMetrics(
            inlineOnlyBlock, fragmentainer.ContentInlineSize, _measurePurpose);
        // PR-#182 Copilot review — when this block is the NESTED-content ROOT
        // (flex / grid item content via `_layoutRootInlineContent`), the OUTER
        // layouter already positioned the item INCLUDING its margins, so the
        // root's OWN margins must NOT shift the emitted inline fragment or
        // inflate the measured ContentBlockExtent / ContentInlineExtent (else a
        // `<div style="margin:…">text</div>` item double-counts the margin).
        // Zero them — `topShift`, EmitInlineOnlyBlockFragment's inline anchor,
        // and the margin-box cursor advance all read these fields.
        if (suppressOwnMargins)
        {
            metrics = metrics with
            {
                MarginInlineStart = 0,
                MarginInlineEnd = 0,
                MarginBlockStart = 0,
                MarginBlockEnd = 0,
            };
        }
        // Sub-cycle 1 — inline-only blocks reset the margin chain
        // (the line box breaks adjacency per CSS 2.1 §8.3.1), so
        // the block's own marginBlockStart applies in full without
        // collapse arithmetic. This matches the cycle 1 behavior;
        // cycle 2 will integrate with the collapse chain.
        var topShift = metrics.MarginBlockStart;
        var computation = ComputeInlineOnlyBlockLayoutCached(
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
        var canSplitLines = CanSplitInlineOnlyLines(comp);

        // `inline-only-block-line-splitting` — RESUME a previously line-split block. It re-enters
        // at the top of a fresh page (a fragmentation break suppresses the block's own margin +
        // — gated chrome-free — its border/padding), emitting its remaining lines from
        // `resumeAtLine` and splitting again if they still overflow. No resolver consult: the
        // tail unconditionally continues here (it was already deferred to its own fresh page).
        if (resumeAtLine > 0)
        {
            return EmitInlineOnlyBlockSplitting(
                block: inlineOnlyBlock,
                childIdx: childIdx,
                metrics: metrics,
                comp: comp,
                fragmentainer: fragmentainer,
                startLine: resumeAtLine,   // validated against the line count inside (fail-fast, no clamp)
                blockBorderBoxTop: fragmentainer.UsedBlockSize,
                priorPagesConsumed: priorPagesConsumed,
                out emitted,
                resumeDecorationOffsetPx: resumeDecorationOffsetPx,
                resumeDecorationTotalPx: resumeDecorationTotalPx);
        }

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

        // PR #207 review [P1] — a text-bearing / inline-only block honors author forced +
        // avoid breaks too (`<p style='break-before:page'>` / a text `<div break-after>`).
        // Same calculation as the block-flow dispatch (shared helper), keyed off this
        // inline-only child + its previously-emitted sibling.
        var inlineAtFragmentainerStart = emittedThisAttempt == 0
            && fragmentainer.UsedBlockSize == initialUsed;
        var inlinePrevEmittedChild = lastEmittedIdx >= 0 && lastEmittedIdx < _rootBox.Children.Count
            ? _rootBox.Children[lastEmittedIdx]
            : null;
        var (inlineForceBreak, inlineAvoidBreak, inlineForceParity) = ResolveChildBreakMetadata(
            _rootBox, inlineOnlyBlock, inlinePrevEmittedChild,
            suppressForce: inlineAtFragmentainerStart);
        // CSS Page L3 §3.4.1 — a forced left/right/recto/verso break before a text/prose block ends
        // the page; record its parity for the driver's blank-page insertion.
        if (inlineForceBreak && inlineForceParity != PageParity.Any)
            _forcedBreakParityForNextPage = inlineForceParity;
        var opportunity = BreakOpportunity.Block(
            usedBlockSize: fragmentainer.UsedBlockSize,
            chunkBlockSize: chunkForBreakCheck,
            forceBreak: inlineForceBreak,
            avoidBreak: inlineAvoidBreak,
            forceParity: inlineForceParity);
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
                // `inline-only-block-line-splitting` — the block is taller than a WHOLE page
                // (it's alone at the top + still doesn't fit). When its lines are sliceable
                // (multi-line, no unsliceable decoration — text, inline atomics, and block-axis
                // border + padding all slice), split them across pages instead of force-overflowing:
                // emit the lines that fit here + resume the tail next page.
                if (canSplitLines)
                {
                    return EmitInlineOnlyBlockSplitting(
                        block: inlineOnlyBlock,
                        childIdx: childIdx,
                        metrics: metrics,
                        comp: comp,
                        fragmentainer: fragmentainer,
                        startLine: 0,
                        blockBorderBoxTop: fragmentainer.UsedBlockSize + topShift,
                        priorPagesConsumed: priorPagesConsumed,
                        out emitted);
                }

                // Forced overflow — the inline content is taller
                // than the fragmentainer AND not sliceable (a single line, or an unsliceable
                // decoration — gradient / background-image / box-shadow / border-radius / outline).
                // Same loud-fail semantics as the in-flow path: emit anyway + diagnostic.
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
    /// <param name="lastLineContinues">inline-only-block-line-splitting — true when this fragment is a
    /// NON-final slice of a paragraph that resumes on a later page, so the painter justifies its last
    /// line (it is an interior line, not the paragraph end). Default false.</param>
    /// <param name="suppressBlockStartChrome">inline-only-block-line-splitting (box-decoration-break:
    /// slice) — true when this is a NON-first slice, so its block-start padding/border is CUT: the painter
    /// starts the content at the border-box top + skips the top border edge. Default false.</param>
    /// <param name="suppressBlockEndChrome">box-decoration-break: slice — true when this is a NON-last
    /// slice, so its block-end border is CUT (the painter skips the bottom border edge). Default false.</param>
    /// <param name="decorationBlockExtentPx">box-decoration-break: slice — when &gt; 0, the unsliced box's
    /// block-axis border-box size, so a continuous decoration (gradient / background-image / outline) on this
    /// slice is painted over the whole box + clipped to the slice. Default 0 → the decoration uses this box.</param>
    /// <param name="decorationBlockOffsetPx">box-decoration-break: slice — this slice's block-axis offset
    /// within the unsliced box (paired with <paramref name="decorationBlockExtentPx"/>). Default 0.</param>
    private void EmitInlineOnlyBlockFragment(
        Box inlineOnlyBlock,
        InlineOnlyBlockMetrics metrics,
        InlineOnlyBlockComputation comp,
        double inlineOffsetFromContentOrigin,
        double blockOffsetFromContentOrigin,
        // inline-only-block-line-splitting — true when this fragment is a NON-final slice of a paragraph
        // that resumes on a later page, so the painter justifies its last line (it isn't the paragraph end).
        bool lastLineContinues = false,
        // box-decoration-break: slice — a NON-first slice's block-start chrome is cut (the painter starts
        // the content at the border-box top + skips the top border); a NON-last slice's block-end border
        // is cut (the painter skips the bottom border).
        bool suppressBlockStartChrome = false,
        bool suppressBlockEndChrome = false,
        // box-decoration-break: slice — when > 0, a CONTINUOUS decoration (gradient / background-image /
        // outline) on this slice is painted over the WHOLE box (height = decorationBlockExtentPx, virtual
        // top = this slice's top − decorationBlockOffsetPx) + clipped to the slice. Default 0 → uses the slice box.
        double decorationBlockExtentPx = 0.0,
        double decorationBlockOffsetPx = 0.0)
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
            InlineLayout: comp.InlineResult,
            // Inline-atomic-boxes cycle — a line carrying a tall inline `<img>` grew; the painter walks
            // these per-line heights so glyph baselines match the grown lines. Null for text-only
            // blocks → the uniform-pitch path, byte-identical.
            PerLineHeightsPx: comp.PerLineHeightsPx,
            // Inline-block last-line-baseline cycle — a line carrying a baseline-aligned inline-block
            // sizes by §10.8.1 max-ascent + supplies its baseline so the painter places the line's text
            // on the SAME baseline as the box. Null (img/text) → the painter's real-metric baseline.
            PerLineBaselineTopPx: comp.PerLineBaselineTopPx,
            // Body text-align cycle — center / right shifts each glyph line by
            // (content width − line advance) × factor (TextPainter consumes it). 0 (start, the
            // initial) is byte-identical to the pre-cycle no-shift output.
            LineAlignFactor: inlineOnlyBlock.Style.ReadInlineAlignFactor(),
            // text-align: justify cycle — distribute each non-last line's free space across its
            // inter-word gaps (TextPainter splits at spaces). False (the default) byte-identical.
            JustifyLines: inlineOnlyBlock.Style.ReadInlineJustify(),
            // text-align: justify-all — the LAST line justifies too (lifts the painter's last-line gate).
            JustifyLastLine: inlineOnlyBlock.Style.ReadInlineJustifyAll(),
            // inline-only-block-line-splitting — a non-final slice's last line is an interior line.
            LastLineContinues: lastLineContinues,
            // box-decoration-break: slice — a non-first slice's block-start chrome / a non-last slice's
            // block-end border is cut.
            SuppressBlockStartChrome: suppressBlockStartChrome,
            SuppressBlockEndChrome: suppressBlockEndChrome,
            // box-decoration-break: slice — slice-aware continuous-decoration geometry (0 → uses this box).
            DecorationBlockExtentPx: decorationBlockExtentPx,
            DecorationBlockOffsetPx: decorationBlockOffsetPx));

        // Inline-atomic-boxes cycle — emit each inline atomic's own positioned fragment. The
        // placement is content-box-relative; add the block fragment's border-box origin + the
        // block's leading border + padding to reach the content box. An inline `<img>` is painted
        // by ImagePainter from the cache; an inline-BLOCK fragment is the box DECORATION (painted by
        // FragmentPainter) + its laid-out content is flushed at the atomic's content-box origin.
        if (comp.AtomicPlacements is { Count: > 0 } placements)
        {
            var contentInlineOrigin = fragmentInlineOffset
                + metrics.BorderInlineStart + metrics.PaddingInlineStart;
            // box-decoration-break: slice — a non-first slice's block-START chrome is CUT, so its atomics
            // (and the nested content flushed below them) start at the border-box top, matching the text's
            // contentTopPx (PR #220 review [P1] — was always adding the block-start chrome, so images /
            // inline-blocks on page 2+ shifted down by the top padding). Inline-axis chrome is unchanged
            // (present on every slice).
            var contentBlockOrigin = blockOffsetFromContentOrigin
                + (suppressBlockStartChrome
                    ? 0.0
                    : metrics.BorderBlockStart + metrics.PaddingBlockStart);
            foreach (var placement in placements)
            {
                var atomicInlineOrigin = contentInlineOrigin + placement.ContentInlineOffsetPx;
                var atomicBlockOrigin = contentBlockOrigin + placement.ContentBlockOffsetPx;
                _sink.Emit(new BoxFragment(
                    Box: placement.Box,
                    InlineOffset: atomicInlineOrigin,
                    BlockOffset: atomicBlockOrigin,
                    InlineSize: placement.WidthPx,
                    BlockSize: placement.HeightPx));

                // Inline-block first cut — flush the atomic's laid-out content at its content-box
                // origin (= the placed border-box origin + the inline-block's own start border +
                // padding for BLOCK-child content; an inline-only-root buffer stays at the border-box
                // origin — TextPainter insets its glyphs by the same chrome). No entry → an inline
                // `<img>` (ImagePainter paints it) or an empty box.
                if (comp.InlineBlockContents is { Count: > 0 } contents
                    && contents.TryGetValue(placement.Box, out var content))
                {
                    var insetI = content.IsInlineOnlyRoot ? 0 : content.ContentInsetInlinePx;
                    var insetB = content.IsInlineOnlyRoot ? 0 : content.ContentInsetBlockPx;
                    content.Buffer.FlushTo(_sink, atomicInlineOrigin + insetI, atomicBlockOrigin + insetB);
                }
            }
        }
    }

    // ---- inline-only-block-line-splitting -------------------------------------
    // A single inline-only (text-bearing) block whose wrapped lines are taller than a
    // whole fragmentainer splits its OWN lines across pages, honoring orphans / widows.
    // The painter walks a fragment's InlineLayout.Lines by ARRAY index from 0 (positions =
    // BlockOffset + cumulative per-line heights), so a page-fragment is just the original
    // Lines[] sliced to the lines that fit + a fresh BlockOffset; an InlineOnlyLineSplit-
    // Continuation (carried in BlockContinuation.LayouterState) resumes the tail next page.

    /// <summary>Tolerance (px) for the line-split fit accumulation + the block-end-chrome reservation.</summary>
    private const double InlineOnlyLineSplitEpsilonPx = 0.01;

    /// <summary>One wrapped line's block-axis height, matching the painter EXACTLY: the
    /// per-line height when the computation carries one (a tall inline atomic grew the line),
    /// else the uniform pitch (= content extent / line count, which equals the painter's
    /// <c>line-height</c> for a text-only block).</summary>
    private static double InlineOnlyLineHeightAt(
        InlineOnlyBlockComputation comp, int lineIndex, int totalLines)
        => comp.PerLineHeightsPx is { } h && lineIndex < h.Count
            ? h[lineIndex]
            : (totalLines > 0 ? comp.ContentBlockSize / totalLines : 0);

    private static double SumInlineOnlyLineHeights(
        InlineOnlyBlockComputation comp, int startLine, int endLine, int totalLines)
    {
        var sum = 0.0;
        for (var i = startLine; i < endLine; i++)
        {
            sum += InlineOnlyLineHeightAt(comp, i, totalLines);
        }
        return sum;
    }

    /// <summary>Whether the block's wrapped lines CAN be sliced across pages. Text, inline atomics
    /// (inline-block / <c>&lt;img&gt;</c>), block-axis PADDING + BORDER, and every box decoration —
    /// a background gradient / image, an outline, a box-shadow, and a border-radius — all slice now
    /// (box-decoration-break: slice — each decoration is painted over the WHOLE composite box, with the
    /// block-axis chrome / radius distributed per cut, then clipped to the slice). The ONLY requirement is
    /// more than one line (a single line can't split).</summary>
    private static bool CanSplitInlineOnlyLines(InlineOnlyBlockComputation comp)
        => comp.InlineResult.Lines.Length > 1;

    /// <summary>Number of lines from <paramref name="startLine"/> that fit in
    /// <paramref name="availableBlockPx"/>, honoring widows; always &gt;= 1 so pagination
    /// makes forward progress, and == the remaining count when they all fit.</summary>
    private static int ComputeInlineOnlyFitLines(
        InlineOnlyBlockComputation comp, int startLine, double availableBlockPx,
        int orphans, int widows)
    {
        var total = comp.InlineResult.Lines.Length;
        var remaining = total - startLine;
        if (remaining <= 0)
        {
            return 0;
        }

        var fit = 0;
        var acc = 0.0;
        for (var i = startLine; i < total; i++)
        {
            var h = InlineOnlyLineHeightAt(comp, i, total);
            if (fit > 0 && acc + h > availableBlockPx + InlineOnlyLineSplitEpsilonPx)
            {
                break;
            }
            acc += h;
            fit++;
        }
        if (fit >= remaining)
        {
            return remaining;   // everything left fits — no further split
        }

        // CSS Fragmentation L3 §4.2 widows — keep at least `widows` lines for the tail page; pull
        // lines back from this page when the break would otherwise leave a too-short remainder, BUT
        // not so far that this page itself drops below `orphans` lines (when widows + orphans can't
        // both be met on a page this small, orphans wins — the geometric fill keeps a full earlier
        // page well past both). A block taller than a page fills each page past `orphans` anyway, so
        // this bites only near the final boundary.
        var tail = remaining - fit;
        if (tail < widows)
        {
            var reduced = remaining - widows;
            if (reduced >= orphans && reduced < fit)
            {
                fit = reduced;
            }
        }
        return fit;
    }

    /// <summary>box-decoration-break: slice — a FINAL slice (all remaining lines fit) must also fit its
    /// block-END chrome (<paramref name="endChromePx"/> = block-end border + padding) on the page. When the
    /// lines fit but the lines + that chrome don't, pull lines back here so the chrome moves with them to a
    /// new final page. CSS Fragmentation L3 §4.2 widows: pull back at least <paramref name="widows"/> lines
    /// so the new final page keeps &gt;= widows (the old single-line pull-back could strand 1 line on the
    /// final page and violate widows — PR #221 review [P1]); bound by <paramref name="orphans"/> (keep
    /// &gt;= orphans on THIS page, which wins over widows when the page can't hold both), and always keep
    /// &gt;= 1 line here for forward progress — so a single line + chrome that can't fit a whole page still
    /// force-overflows. A NON-final slice carries no block-end chrome, so it is returned unchanged.</summary>
    private static (int Fit, int EndLine) ReserveFinalSliceEndChrome(
        InlineOnlyBlockComputation comp, int startLine, int endLine, int total,
        int fit, double availableBlockPx, double endChromePx, int orphans, int widows)
    {
        if (endLine >= total && endChromePx > InlineOnlyLineSplitEpsilonPx && fit > 1
            && SumInlineOnlyLineHeights(comp, startLine, endLine, total) + endChromePx
                > availableBlockPx + InlineOnlyLineSplitEpsilonPx)
        {
            // Lines stay HERE; the rest (>= widows when the page can hold them) + the chrome go to a new
            // final page. Keep >= orphans here (orphans bounds the widows pull-back per §4.2), and always
            // >= 1 here (and so push >= 1) for forward progress.
            var minKeepHere = System.Math.Min(System.Math.Max(1, orphans), fit - 1);  // >= orphans, leave >= 1 to push
            var keepHere = fit - System.Math.Max(1, widows);                          // push >= widows
            if (keepHere < minKeepHere) keepHere = minKeepHere;                        // orphans bound wins over widows
            if (keepHere < 1) keepHere = 1;
            if (keepHere > fit - 1) keepHere = fit - 1;                                // always push >= 1
            fit = keepHere;
            endLine = startLine + fit;
        }
        return (fit, endLine);
    }

    /// <summary>box-decoration-break: slice (PR #222 review [P1]) — the composite box's TOTAL physical
    /// block-axis extent across all page slices, reconstructed from the ACTUAL fragment areas. Each non-final
    /// slice FILLS its fragmentainer to the edge (a broken box occupies the rest of the fragmentainer), so
    /// the physical extent exceeds the natural line-height sum by the per-page fill gaps; a continuous
    /// decoration (gradient / background-image / outline) must span THIS total + offset each slice by its
    /// cumulative physical extent, else it restarts before the previous physical slice ended (overlap /
    /// discontinuity), worst at a mid-page start or when widows / orphans strand unused space. Walks the same
    /// fit / end-chrome / fill rules the streaming emit (<see cref="EmitInlineOnlyBlockSlice"/>) uses: the
    /// first slice starts at <paramref name="firstBlockBorderBoxTop"/> and fills the page remainder; each
    /// later slice resumes at a fresh fragmentainer top (offset 0) and fills it; the final slice takes its
    /// natural content + block-end-chrome height. Resume pages are assumed to share
    /// <paramref name="fragmentainerBlockSize"/> (the uniform-page common case; a varying <c>@page</c>
    /// block-size is a documented residual). The result is precomputed ONCE at the first slice and threaded
    /// forward through the <see cref="InlineOnlyLineSplitContinuation"/>.</summary>
    private static double ComputeInlineOnlySlicedDecorationTotalExtent(
        InlineOnlyBlockComputation comp, InlineOnlyBlockMetrics metrics,
        double firstBlockBorderBoxTop, double fragmentainerBlockSize, int orphans, int widows)
    {
        var total = comp.InlineResult.Lines.Length;
        var topChromeFull = metrics.BorderBlockStart + metrics.PaddingBlockStart;
        var endChrome = metrics.BorderBlockEnd + metrics.PaddingBlockEnd;
        var cumulative = 0.0;
        var startLine = 0;
        var blockTop = firstBlockBorderBoxTop;
        // ComputeInlineOnlyFitLines always advances >= 1 line, so the loop runs at most `total` times; the
        // guard is a belt-and-braces backstop against a future non-progressing change.
        for (var guard = 0; startLine < total && guard <= total; guard++)
        {
            var topChrome = startLine == 0 ? topChromeFull : 0.0;
            var remaining = fragmentainerBlockSize - blockTop;
            var available = remaining - topChrome;
            var fit = ComputeInlineOnlyFitLines(comp, startLine, available, orphans, widows);
            var endLine = System.Math.Min(startLine + fit, total);
            (fit, endLine) = ReserveFinalSliceEndChrome(
                comp, startLine, endLine, total, fit, available, endChrome, orphans, widows);
            var content = SumInlineOnlyLineHeights(comp, startLine, endLine, total);
            var sliceBottomChrome = endLine == total ? endChrome : 0.0;
            var naturalBorderBox = topChrome + content + sliceBottomChrome;
            // Mirror EmitInlineOnlyBlockSlice's `borderBoxBlockSize`: a non-final slice fills the remaining
            // fragmentainer extent when it exceeds the natural box; the final slice is its natural height.
            var physical = endLine < total && remaining > naturalBorderBox ? remaining : naturalBorderBox;
            cumulative += physical;
            startLine = endLine;
            blockTop = 0.0;   // resume slices start at the content-area top of a fresh page
        }
        return cumulative;
    }

    /// <summary>Emits lines <c>[startLine, endLine)</c> of an inline-only block as ONE
    /// <see cref="BoxFragment"/> at the page-relative border-box top, reusing
    /// <see cref="EmitInlineOnlyBlockFragment"/> via a sliced computation (a sub-array of <c>Lines</c> +
    /// matching per-line heights + the slice's content extent). The block-axis chrome (border + padding)
    /// is distributed across the slices per box-decoration-break: slice — block-start on the first, block-end
    /// on the last — and a NON-final slice fills to <paramref name="fillToBlockExtent"/> (the page edge).
    /// Returns the slice's PHYSICAL border-box block extent (the filled size for a non-final slice), so the
    /// caller can accumulate the next slice's cumulative decoration offset.</summary>
    private double EmitInlineOnlyBlockSlice(
        Box block, InlineOnlyBlockMetrics metrics, InlineOnlyBlockComputation comp,
        int startLine, int endLine, double inlineOffsetFromContentOrigin, double blockBorderBoxTop,
        // box-decoration-break: slice (PR #222 review [P1]) — this slice's cumulative PHYSICAL block-axis
        // offset within the composite box (the summed border-box sizes of every PRIOR slice, including the
        // page-fill gaps) + the composite box's full physical block extent. A continuous decoration (gradient
        // / background-image / outline) is painted over the WHOLE composite box (height = the total, virtual
        // top = this slice's top − the offset) + clipped to this slice, so it stays continuous across the
        // page-fill gaps instead of restarting at every break. 0/0 → the slice's own box (no actual split).
        double decorationBlockOffsetPx = 0.0,
        double decorationBlockTotalExtentPx = 0.0,
        // PR #221 review [P1] — the remaining fragmentainer extent from this slice's border-box top to the
        // page bottom. A NON-final slice (more lines remain) FILLS it (a broken box occupies the rest of the
        // fragmentainer in the block axis), so its inline-axis borders + solid background span the page
        // instead of stopping after the last line. Null / a final slice → the natural content-spanning size.
        double? fillToBlockExtent = null)
    {
        var total = comp.InlineResult.Lines.Length;
        if (startLine == 0 && endLine == total)
        {
            // The whole block fits — no actual split. Emit the original computation directly (no
            // array copy, byte-identical to the pre-split whole-block emit).
            EmitInlineOnlyBlockFragment(
                block, metrics, comp, inlineOffsetFromContentOrigin, blockBorderBoxTop);
            return comp.BorderBoxBlockSize;
        }
        var slicedLines = comp.InlineResult.Lines[startLine..endLine];
        var slicedInline = new InlineLayoutResult(
            slicedLines, comp.InlineResult.ShapedRuns, comp.InlineResult.PreprocessedRuns);

        double[]? slicedHeights = null;
        if (comp.PerLineHeightsPx is { } ph)
        {
            slicedHeights = new double[endLine - startLine];
            for (var i = 0; i < slicedHeights.Length; i++)
            {
                slicedHeights[i] = ph[startLine + i];
            }
        }
        var slicedContent = SumInlineOnlyLineHeights(comp, startLine, endLine, total);

        // inline-only-block-line-splitting (atomics) — slice the per-line baselines like the heights,
        // and filter the inline-atomic placements to the lines in THIS slice, re-basing each to the
        // slice's content origin (original line `startLine` becomes block-offset 0). The atomic's own
        // positioned fragment then paints on the page its line landed on.
        double[]? slicedBaselines = null;
        if (comp.PerLineBaselineTopPx is { } pb)
        {
            slicedBaselines = new double[endLine - startLine];
            for (var i = 0; i < slicedBaselines.Length; i++) slicedBaselines[i] = pb[startLine + i];
        }
        var slicedPlacements = comp.AtomicPlacements;
        var slicedContents = comp.InlineBlockContents;
        if (comp.AtomicPlacements is { Count: > 0 } allPlacements)
        {
            // Keep the atomics whose OWNING LINE is in this slice — filter by the layout-recorded
            // LineIndex (robust to vertical-align / zero-height / negative margins moving the rendered
            // top, PR #220 review [P2]) and re-base their block offset to the slice's content origin
            // (original line `startLine` becomes block-offset 0).
            var sliceStartTop = SumInlineOnlyLineHeights(comp, 0, startLine, total);
            var kept = new System.Collections.Generic.List<InlineAtomicPlacement>();
            System.Collections.Generic.Dictionary<Box, InlineAtomicContent>? keptContents = null;
            foreach (var p in allPlacements)
            {
                if (p.LineIndex < startLine || p.LineIndex >= endLine) continue;
                kept.Add(p with { ContentBlockOffsetPx = p.ContentBlockOffsetPx - sliceStartTop });
                if (comp.InlineBlockContents is { } all && all.TryGetValue(p.Box, out var content))
                    (keptContents ??= new System.Collections.Generic.Dictionary<Box, InlineAtomicContent>())[p.Box] = content;
            }
            slicedPlacements = kept;
            slicedContents = keptContents;
        }

        // box-decoration-break: slice — the block-axis CHROME (border + padding) is distributed across
        // the slices: the block-start border+padding sits on the FIRST slice (above its content) and the
        // block-end border+padding on the LAST. A non-first slice's block-start chrome is CUT
        // (suppressBlockStartChrome — the painter starts the content at the border-box top + skips the top
        // border); a non-last slice's block-end border is cut (suppressBlockEndChrome). The slice's border
        // box grows by whichever chrome it carries (so the fragment's background spans it). A border-RADIUS
        // rounds only this slice's real corners — the FragmentPainter decomposes it per cut.
        var sliceTopChrome = startLine == 0 ? metrics.BorderBlockStart + metrics.PaddingBlockStart : 0.0;
        var sliceBottomChrome = endLine == total ? metrics.BorderBlockEnd + metrics.PaddingBlockEnd : 0.0;
        // PR #221 review [P1] — a NON-final slice is a BROKEN box: per CSS Fragmentation it occupies the
        // REMAINING fragmentainer extent in the block axis, so its left/right borders + solid background
        // fill down to the page edge rather than stopping after the last fitting line (the undecorated-gap
        // bug). The text-line extent stays SEPARATE — the painter walks the sliced Lines[] by per-line
        // height from BlockOffset, so growing the border box doesn't move a glyph. A final slice keeps its
        // natural content-spanning height (ContentBlockSize is always the text extent, used for the pitch).
        var naturalBorderBox = sliceTopChrome + slicedContent + sliceBottomChrome;
        var borderBoxBlockSize = endLine < total && fillToBlockExtent is { } fill && fill > naturalBorderBox
            ? fill
            : naturalBorderBox;
        var slicedComp = comp with
        {
            InlineResult = slicedInline,
            BorderBoxBlockSize = borderBoxBlockSize,
            ContentBlockSize = slicedContent,
            PerLineHeightsPx = slicedHeights,
            PerLineBaselineTopPx = slicedBaselines ?? comp.PerLineBaselineTopPx,
            AtomicPlacements = slicedPlacements,
            InlineBlockContents = slicedContents,
        };
        // box-decoration-break: slice — slice-aware CONTINUOUS-decoration (gradient / background-image /
        // outline) geometry, reconstructed from the ACTUAL fragment areas (PR #222 review [P1]). The whole
        // composite box's PHYSICAL block extent (`decorationBlockTotalExtentPx`, summed over every slice's
        // FILLED border-box size — not the natural line-height sum, which ignores the page-fill gaps) + this
        // slice's cumulative physical offset within it (`decorationBlockOffsetPx`, both threaded by the
        // caller from the precomputed break plan). The painter uses these to paint a gradient /
        // background-image / outline over the whole box + clip to this slice, so it stays continuous across
        // the fill gaps; set on every slice but consulted only by those painters, so other slices are
        // byte-identical. 0/0 → the slice's own box.
        EmitInlineOnlyBlockFragment(
            block, metrics, slicedComp, inlineOffsetFromContentOrigin, blockBorderBoxTop,
            // A non-final slice (more lines remain) continues on a later page, so its last line is an
            // interior line (justifies) rather than the paragraph end.
            lastLineContinues: endLine < total,
            // box-decoration-break: slice — a non-first slice's block-start chrome is cut, a non-last
            // slice's block-end border is cut.
            suppressBlockStartChrome: startLine > 0,
            suppressBlockEndChrome: endLine < total,
            decorationBlockExtentPx: decorationBlockTotalExtentPx,
            decorationBlockOffsetPx: decorationBlockOffsetPx);
        return borderBoxBlockSize;
    }

    /// <summary>box-decoration-break: slice — when the slice the progress rule forced is taller than the
    /// remaining fragmentainer extent (a block-start border/padding taller than the page, or a final slice
    /// whose single line + block-end chrome can't fit a whole page), the slice still emits to make
    /// pagination progress but it OVERFLOWS the page. Emit the same loud <c>PAGINATION-FORCED-OVERFLOW-001</c>
    /// the non-sliceable force-overflow path emits, so a sliceable block's overflow isn't silent
    /// (PR #221 review [P2]). No-op when the slice fits the remaining extent.</summary>
    private void ReportSliceForcedOverflowIfNeeded(
        int childIdx, int pageIndex, double requiredExtentPx, double remainingExtentPx)
    {
        if (requiredExtentPx <= remainingExtentPx + InlineOnlyLineSplitEpsilonPx) return;
        OptimizingBreakResolver.SafeEmit(_capturedDiagSink, new PaginateDiagnostic(
            PaginateDiagnosticCodes.PaginationForcedOverflow001,
            $"BlockLayouter: forced overflow slicing an inline-only block at child index {childIdx} on "
            + $"fragmentainer page index {pageIndex} — the slice's block-axis chrome + its minimum line "
            + $"(extent={requiredExtentPx:0.##}) is taller than the remaining fragmentainer extent "
            + $"({remainingExtentPx:0.##}). Committed anyway to make pagination progress.",
            PaginateDiagnosticSeverity.Warning));
    }

    /// <summary>Fail-fast guard on a resumed line index (PR #211 Copilot review): the producer
    /// (<see cref="EmitInlineOnlyBlockSplitting"/> / <see cref="EmitInlineOnlyBlockInRecursionSplitting"/>)
    /// only ever resumes at a line it emitted up to, so a value outside <c>[0, totalLines]</c> is an
    /// <see cref="InlineOnlyLineSplitContinuation"/> contract violation — throw rather than clamp +
    /// silently emit an empty / wrong slice (matching the other continuation guards in this
    /// layouter).</summary>
    private static void ValidateInlineResumeLine(int resumeLine, int totalLines)
    {
        if (resumeLine < 0 || resumeLine > totalLines)
        {
            throw new InvalidOperationException(
                $"InlineOnlyLineSplitContinuation.ResumeLineIndex={resumeLine} is out of range "
                + $"[0, {totalLines}] for the inline-only block's wrapped lines — a continuation-"
                + "contract violation (the producer only resumes at a line index it emitted up to).");
        }
    }

    /// <summary>Emits an inline-only block starting at <paramref name="startLine"/>, slicing
    /// its lines across pages when they don't all fit (`inline-only-block-line-splitting`).
    /// Returns a <c>PageComplete</c> carrying an <see cref="InlineOnlyLineSplitContinuation"/>
    /// when lines remain for the next page; returns <see langword="null"/> when the block
    /// FINISHED on this page (this method advances <see cref="FragmentainerContext.UsedBlockSize"/>
    /// past the final slice so the caller's next sibling flows below it).</summary>
    private LayoutAttemptResult? EmitInlineOnlyBlockSplitting(
        Box block, int childIdx, InlineOnlyBlockMetrics metrics, InlineOnlyBlockComputation comp,
        FragmentainerContext fragmentainer, int startLine, double blockBorderBoxTop,
        double priorPagesConsumed, out bool emitted,
        // box-decoration-break: slice (PR #222 review [P1]) — a RESUME slice's cumulative physical decoration
        // offset + the composite box's total physical extent, carried from the prior slice's continuation. At
        // the FIRST slice (startLine == 0) these are unused — the total is precomputed here.
        double resumeDecorationOffsetPx = 0.0, double resumeDecorationTotalPx = 0.0)
    {
        emitted = true;
        var total = comp.InlineResult.Lines.Length;
        ValidateInlineResumeLine(startLine, total);
        var orphans = block.Style.ReadOrphansOrDefault();
        var widows = block.Style.ReadWidowsOrDefault();
        // box-decoration-break: slice — the FIRST slice's content sits below the block-start chrome
        // (border + padding), so it has that much less room; a resume slice carries no block-start chrome
        // (it was on slice 0). The final slice also reserves the block-end chrome (border + padding).
        var topChrome = startLine == 0 ? metrics.BorderBlockStart + metrics.PaddingBlockStart : 0.0;
        var endChrome = metrics.BorderBlockEnd + metrics.PaddingBlockEnd;
        var remaining = fragmentainer.BlockSize - blockBorderBoxTop;   // page room from the border-box top
        var available = remaining - topChrome;                        // room left for content after top chrome
        var fit = ComputeInlineOnlyFitLines(comp, startLine, available, orphans, widows);
        var endLine = System.Math.Min(startLine + fit, total);
        (fit, endLine) = ReserveFinalSliceEndChrome(
            comp, startLine, endLine, total, fit, available, endChrome, orphans, widows);

        var sliceExtent = SumInlineOnlyLineHeights(comp, startLine, endLine, total);
        var isFinalSlice = endLine >= total;
        // box-decoration-break: slice (PR #222 review [P1]) — the continuous-decoration geometry uses the
        // PHYSICAL composite box. The first slice precomputes the total extent (reconstructed from the actual
        // page-filled fragment areas); a resume slice reuses the carried total + its cumulative offset.
        var (decorationOffset, decorationTotal) = startLine == 0
            ? (0.0, ComputeInlineOnlySlicedDecorationTotalExtent(
                comp, metrics, blockBorderBoxTop, fragmentainer.BlockSize, orphans, widows))
            : (resumeDecorationOffsetPx, resumeDecorationTotalPx);
        var slicePhysicalExtent = EmitInlineOnlyBlockSlice(
            block, metrics, comp, startLine, endLine,
            inlineOffsetFromContentOrigin: 0, blockBorderBoxTop: blockBorderBoxTop,
            decorationBlockOffsetPx: decorationOffset,
            decorationBlockTotalExtentPx: decorationTotal,
            fillToBlockExtent: remaining);
        // PR #221 review [P2] — a slice taller than the remaining page (a huge block-start border, or a
        // final single line + block-end chrome that can't fit a whole page) overflows; surface it loudly
        // like the non-sliceable force-overflow path rather than emitting a silently clipped fragment.
        ReportSliceForcedOverflowIfNeeded(
            childIdx, fragmentainer.PageIndex,
            topChrome + sliceExtent + (isFinalSlice ? endChrome : 0.0), remaining);

        if (isFinalSlice)
        {
            // The block finished on this page — advance past the slice (its block-start chrome if this is
            // also the first slice + the lines + its block-end chrome) + margin-bottom so a following
            // sibling flows below it.
            fragmentainer.UsedBlockSize = System.Math.Max(0,
                blockBorderBoxTop + topChrome + sliceExtent + endChrome + metrics.MarginBlockEnd);
            return null;
        }

        // Lines remain → the page is full; resume the block's tail at `endLine` next page. Carry the next
        // slice's cumulative physical decoration offset (this slice's offset + its FILLED extent) + the total.
        fragmentainer.UsedBlockSize = fragmentainer.BlockSize;
        return LayoutAttemptResult.PageComplete(
            new BlockContinuation(
                ResumeAtChild: childIdx,
                ConsumedBlockSize: priorPagesConsumed
                    + (fragmentainer.UsedBlockSize - _pageStartUsedBlockSize),
                LayouterState: new InlineOnlyLineSplitContinuation(
                    endLine, decorationOffset + slicePhysicalExtent, decorationTotal)),
            cost: 0);
    }

    /// <summary>Per `inline-only-block-line-splitting` — the recursion's split-aware emit for an
    /// inline-only block. Emits the block (or, when <paramref name="startLine"/> &gt; 0, its
    /// resumed tail), SLICING its wrapped lines across pages when they don't all fit the
    /// fragmentainer. Returns a <see cref="BlockContinuation"/> carrying an
    /// <see cref="InlineOnlyLineSplitContinuation"/> when lines remain (the recursive caller
    /// returns it to page-break); returns <see langword="null"/> when the block FINISHED on this
    /// page, with <paramref name="marginBoxExtentAdvance"/> set to the cursor advance
    /// (margin-start when this is the first slice + the emitted border-box extent + margin-end).
    /// Falls back to a whole-block emit (the pre-existing force-overflow behavior) when the lines
    /// aren't sliceable (<see cref="CanSplitInlineOnlyLines"/>) or there's no paginating
    /// fragmentainer.</summary>
    private LayoutContinuation? EmitInlineOnlyBlockInRecursionSplitting(
        Box inlineOnlyBlock,
        double parentContentInlineSize,
        double inlineOffsetFromContentOrigin,
        double blockBorderBoxTop,
        int childIdx,
        int startLine,
        FragmentainerContext? fragmentainer,
        out double marginBoxExtentAdvance,
        CancellationToken cancellationToken,
        // box-decoration-break: slice (PR #222 review [P1]) — a RESUME slice's cumulative physical decoration
        // offset + the composite box's total physical extent, carried from the prior slice's continuation.
        double resumeDecorationOffsetPx = 0.0, double resumeDecorationTotalPx = 0.0)
    {
        marginBoxExtentAdvance = 0;
        var metrics = ReadInlineOnlyBlockMetrics(
            inlineOnlyBlock, parentContentInlineSize, _measurePurpose);
        var computation = ComputeInlineOnlyBlockLayoutCached(
            inlineOnlyBlock, metrics, containingInlineSize: parentContentInlineSize,
            out var notSupportedMessage, out var atomicInlineSkipCount, cancellationToken);

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
            return null;
        }
        if (computation is null)
        {
            return null;
        }

        var comp = computation.Value;
        var marginStartAdvance = startLine == 0 ? metrics.MarginBlockStart : 0;

        // Not sliceable (single line or an unsliceable decoration) OR an unpaginated
        // fragmentainer → emit the whole block at the border-box top (the pre-existing
        // force-overflow path; `startLine` is 0 here because a split only ever resumes a
        // sliceable block).
        if (!CanSplitInlineOnlyLines(comp)
            || fragmentainer is not { SuppressBlockPagination: false })
        {
            EmitInlineOnlyBlockFragment(
                inlineOnlyBlock, metrics, comp,
                inlineOffsetFromContentOrigin, blockBorderBoxTop);
            marginBoxExtentAdvance = metrics.MarginBlockStart
                + comp.BorderBoxBlockSize + metrics.MarginBlockEnd;
            return null;
        }

        var total = comp.InlineResult.Lines.Length;
        ValidateInlineResumeLine(startLine, total);
        var orphans = inlineOnlyBlock.Style.ReadOrphansOrDefault();
        var widows = inlineOnlyBlock.Style.ReadWidowsOrDefault();
        // box-decoration-break: slice — the FIRST slice reserves its block-start chrome (border +
        // padding) above the content; a resume slice has none (it was consumed by slice 0). The final
        // slice also reserves the block-end chrome (border + padding).
        var topChrome = startLine == 0 ? metrics.BorderBlockStart + metrics.PaddingBlockStart : 0.0;
        var endChrome = metrics.BorderBlockEnd + metrics.PaddingBlockEnd;
        var remaining = fragmentainer.BlockSize - blockBorderBoxTop;   // page room from the border-box top
        var available = remaining - topChrome;                        // room left for content after top chrome
        var fit = ComputeInlineOnlyFitLines(comp, startLine, available, orphans, widows);
        var endLine = System.Math.Min(startLine + fit, total);
        (fit, endLine) = ReserveFinalSliceEndChrome(
            comp, startLine, endLine, total, fit, available, endChrome, orphans, widows);
        var sliceExtent = SumInlineOnlyLineHeights(comp, startLine, endLine, total);
        var isFinalSlice = endLine >= total;
        // box-decoration-break: slice (PR #222 review [P1]) — the first slice precomputes the composite box's
        // PHYSICAL total extent; a resume slice reuses the carried total + its cumulative offset.
        var (decorationOffset, decorationTotal) = startLine == 0
            ? (0.0, ComputeInlineOnlySlicedDecorationTotalExtent(
                comp, metrics, blockBorderBoxTop, fragmentainer.BlockSize, orphans, widows))
            : (resumeDecorationOffsetPx, resumeDecorationTotalPx);
        var slicePhysicalExtent = EmitInlineOnlyBlockSlice(
            inlineOnlyBlock, metrics, comp, startLine, endLine,
            inlineOffsetFromContentOrigin, blockBorderBoxTop,
            decorationBlockOffsetPx: decorationOffset,
            decorationBlockTotalExtentPx: decorationTotal,
            fillToBlockExtent: remaining);
        // PR #221 review [P2] — the recursion path also surfaces a slice taller than the remaining page.
        ReportSliceForcedOverflowIfNeeded(
            childIdx, fragmentainer.PageIndex,
            topChrome + sliceExtent + (isFinalSlice ? endChrome : 0.0), remaining);

        if (isFinalSlice)
        {
            // The final slice carries the block-end chrome; the first slice (marginStartAdvance != 0 ⇔
            // startLine == 0) also carries the block-start chrome + margin.
            marginBoxExtentAdvance =
                marginStartAdvance + topChrome + sliceExtent + endChrome + metrics.MarginBlockEnd;
            return null;
        }
        // Lines remain → resume the tail at `endLine` on the next page; carry the next slice's cumulative
        // physical decoration offset (this slice's offset + its FILLED extent) + the total.
        return new BlockContinuation(
            ResumeAtChild: childIdx, ConsumedBlockSize: 0,
            LayouterState: new InlineOnlyLineSplitContinuation(
                endLine, decorationOffset + slicePhysicalExtent, decorationTotal));
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
        out double visualChunkBelowBorderTopPx,
        CancellationToken cancellationToken)
    {
        var metrics = ReadInlineOnlyBlockMetrics(
            inlineOnlyBlock, parentContentInlineSize, _measurePurpose);
        var computation = ComputeInlineOnlyBlockLayout(
            inlineOnlyBlock,
            metrics,
            containingInlineSize: parentContentInlineSize,
            out _,
            out _,
            cancellationToken);
        if (computation is null)
        {
            visualChunkBelowBorderTopPx = 0;
            return 0;
        }
        var comp = computation.Value;
        // The VISUAL extent BELOW the border-box top (border-box + a NON-negative margin-bottom — a negative
        // margin-bottom is clamped to 0, mirroring the top-level inline path's `visualBlockExtent`). The
        // prose-pagination break uses this as its chunk so a negative margin-bottom can't shrink the break
        // below the real text border-box (review P2).
        visualChunkBelowBorderTopPx = comp.BorderBoxBlockSize + Math.Max(0, metrics.MarginBlockEnd);
        // Return the margin-box block-axis extent so the parent's
        // measure pass reserves enough page space (matches the
        // in-flow block path's measure semantics).
        return metrics.MarginBlockStart + comp.BorderBoxBlockSize
            + metrics.MarginBlockEnd;
    }

    /// <summary>Resolve an element's effective BCP-47 language for hyphenation routing, following the HTML
    /// language algorithm (https://html.spec.whatwg.org/multipage/dom.html#language). Walks up the ancestor
    /// chain; the <b>nearest element carrying a <c>lang</c> attribute decides</b> (or <c>xml:lang</c> when
    /// <c>lang</c> is absent on that element) — even an <b>empty</b> value, which HTML defines as "language
    /// unknown" and which therefore OVERRIDES an ancestor rather than inheriting from it (e.g.
    /// <c>&lt;html lang="de"&gt;&lt;p lang=""&gt;</c> is unknown, not German). Returns the non-empty value,
    /// or <see langword="null"/> when the deciding attribute is empty/whitespace or no ancestor declares one
    /// — in both cases the caller resolves to the bundled English hyphenator. Only the <c>hyphens: auto</c>
    /// hyphenator is selected from this (via <see cref="HyphenationRegistry.ResolveOrDefault"/>); shaping is
    /// unaffected, so output is byte-identical unless a <c>NetPdf.Languages.*</c> pack registered the
    /// language. Clean-room: reads the DOM attributes through AngleSharp's public <c>IElement</c> API.
    /// <c>internal</c> (not <c>private</c>) so the lang-routing end-to-end test can exercise it against a
    /// real <see cref="BoxBuilder"/>-produced box tree.</summary>
    internal static string? ResolveEffectiveLanguage(IElement? element)
    {
        for (var e = element; e is not null; e = e.ParentElement)
        {
            // A `lang` attribute present on this element decides (empty included); `xml:lang` is the
            // fallback only when `lang` is absent. GetAttribute returns "" for a present-but-empty value and
            // null for an absent one — so `lang=""` reaches this branch and short-circuits the walk.
            var lang = e.GetAttribute("lang") ?? e.GetAttribute("xml:lang");
            if (lang is not null)
            {
                var trimmed = lang.Trim();
                return trimmed.Length == 0 ? null : trimmed; // empty/whitespace → "unknown" → default hyphenator
            }
        }

        return null;
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
    ///   <item>Atomic inlines — an inline <c>&lt;img&gt;</c>
    ///   (<see cref="BoxKind.InlineReplacedElement"/>) with a resolved size + a
    ///   <see cref="BoxKind.InlineBlockContainer"/> (inline-block first cut, via
    ///   <see cref="TryBuildInlineBlockAtomic"/>) become a one-char U+FFFC atomic TextRun;
    ///   <see cref="BoxKind.InlineFlexContainer"/> /
    ///   <see cref="BoxKind.InlineGridContainer"/> /
    ///   <see cref="BoxKind.InlineTable"/> (+ an unsized inline-replaced / a failed
    ///   inline-block layout) are still counted + the caller emits one
    ///   LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001 diagnostic per skipped atomic.</item>
    /// </list>
    /// </para></summary>
    /// <param name="parent">The current parent box being walked.</param>
    /// <param name="textRuns">The accumulator; runs are appended in
    /// document order.</param>
    /// <param name="parentBlockStyle">The inline-only block's own
    /// style. Used for the synthetic <c>"\n"</c> TextRun on
    /// <see cref="BoxKind.LineBreak"/> so the mandatory break
    /// inherits font / line metrics from the surrounding block.</param>
    /// <param name="availInlineContentSize">The available inline CONTENT width of the
    /// inline-only block — the width an inline-block atomic's content is measured at (it
    /// shrink-to-fits within this).</param>
    /// <param name="atomicContents">Accumulates each inline-block atomic's laid-out content
    /// (keyed by its box) for <see cref="EmitInlineOnlyBlockFragment"/> to flush.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Count of atomic-inline descendants skipped; caller
    /// emits one diagnostic per skip (or one aggregated diagnostic
    /// for the total count).</returns>
    private int CollectInlineTextRuns(
        Box parent,
        List<NetPdf.Layout.Inline.TextRun> textRuns,
        ComputedStyle parentBlockStyle,
        double availInlineContentSize,
        Dictionary<Box, InlineAtomicContent> atomicContents,
        CancellationToken cancellationToken)
    {
        var skipCount = 0;
        for (var i = 0; i < parent.Children.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var child = parent.Children[i];
            // PR-#182 Copilot review — an out-of-flow (position: absolute /
            // fixed) inline-level descendant does NOT participate in inline
            // flow (CSS 2.2 §9.3 — it's removed from the normal flow). Skip it
            // (and don't recurse into its subtree) so its text never contributes
            // to the line layout / measurement here; the abspos / fixed passes
            // emit + anchor it. Without this an inline-only block (incl. the
            // nested-content root) would render the positioned text inline AND
            // at its positioned offset (duplicate + wrong sizing).
            if (child.Style.IsOutOfFlow()) continue;
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
                        child, textRuns, parentBlockStyle, availInlineContentSize,
                        atomicContents, cancellationToken);
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
                        child, textRuns, child.Style, availInlineContentSize,
                        atomicContents, cancellationToken);
                    break;
                case BoxKind.InlineReplacedElement:
                    // Inline-atomic-boxes cycle — an inline `<img>` with a resolved used size (written
                    // into its `width`/`height` slots by the pre-layout ReplacedSizeResolver) becomes an
                    // ATOMIC inline box: a one-char U+FFFC OBJECT REPLACEMENT CHARACTER TextRun carrying
                    // the box + its used border-box size. LineBuilder reserves the advance + line-height;
                    // BlockLayouter emits a positioned fragment; ImagePainter paints it from the image
                    // cache. An img with no usable size (no CSS/attribute size AND a failed load → 0
                    // slots) can't be laid out — it falls through to the skip + diagnostic below.
                    var atomicContentWidthPx = child.Style.ReadLengthPxOrZero(PropertyId.Width);
                    var atomicContentHeightPx = child.Style.ReadLengthPxOrZero(PropertyId.Height);
                    if (atomicContentWidthPx > 0 && atomicContentHeightPx > 0)
                    {
                        // Box model (post-PR-#186 review P1) \u2014 the used CONTENT size is wrapped by the
                        // img's own padding + border (\u2192 the BORDER box the emitted fragment carries, since
                        // ImagePainter subtracts them back to the content) + margins (\u2192 the line advance).
                        var s = child.Style;
                        var borderBoxWidthPx = atomicContentWidthPx
                            + s.ReadLengthPxOrZero(PropertyId.PaddingLeft) + s.ReadLengthPxOrZero(PropertyId.PaddingRight)
                            + s.ReadLengthPxOrZero(PropertyId.BorderLeftWidth) + s.ReadLengthPxOrZero(PropertyId.BorderRightWidth);
                        var borderBoxHeightPx = atomicContentHeightPx
                            + s.ReadLengthPxOrZero(PropertyId.PaddingTop) + s.ReadLengthPxOrZero(PropertyId.PaddingBottom)
                            + s.ReadLengthPxOrZero(PropertyId.BorderTopWidth) + s.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
                        var marginInlineStartPx = s.ReadLengthPxOrZero(PropertyId.MarginLeft);
                        var advancePx = borderBoxWidthPx
                            + marginInlineStartPx + s.ReadLengthPxOrZero(PropertyId.MarginRight);
                        textRuns.Add(new NetPdf.Layout.Inline.TextRun(
                            "\uFFFC", s,
                            new NetPdf.Layout.Inline.InlineAtomic(
                                child, advancePx, borderBoxWidthPx, borderBoxHeightPx,
                                marginInlineStartPx,
                                s.ReadLengthPxOrZero(PropertyId.MarginTop),
                                s.ReadLengthPxOrZero(PropertyId.MarginBottom))));
                        break;
                    }
                    skipCount++;
                    break;
                case BoxKind.InlineBlockContainer:
                    // Inline-atomic-boxes cycle (inline-block first cut) — lay the box's content
                    // out into a sub-box + place it as an ATOMIC inline (its content does NOT flow
                    // into this line — no recurse). A failed layout (no shaper / empty / a
                    // NotSupported inline pass) falls through to the skip + diagnostic.
                    var inlineBlockAtomic = TryBuildInlineBlockAtomic(
                        child, availInlineContentSize, atomicContents, cancellationToken);
                    if (inlineBlockAtomic is { } iba)
                    {
                        textRuns.Add(new NetPdf.Layout.Inline.TextRun("\uFFFC", child.Style, iba));
                        break;
                    }
                    skipCount++;
                    break;
                // Atomic inlines still unsupported — inline-flex / inline-grid / inline-table
                // (+ an unsized inline-replaced, above). Per Finding #4 — count + caller emits one
                // LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001 diagnostic per skip.
                case BoxKind.InlineFlexContainer:
                case BoxKind.InlineGridContainer:
                case BoxKind.InlineTable:
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

    /// <summary>Inline-atomic-boxes cycle (inline-block first cut) — lay a <c>display:
    /// inline-block</c> box out as an atomic inline. Measures its content via
    /// <see cref="NestedContentMeasurer"/> at the available content width, computes its used
    /// BORDER-box size (a definite <c>width</c>/<c>height</c> honors <c>box-sizing</c> via
    /// <see cref="BoxSizingHelper"/>; an <c>auto</c> width SHRINK-TO-FITs to the measured content
    /// width + the inline chrome; an <c>auto</c> height = the measured content block extent + the
    /// block chrome, or — for an inline-only-root buffer — the already-chrome-folded extent), and
    /// records the buffer in <paramref name="atomicContents"/> for
    /// <see cref="EmitInlineOnlyBlockFragment"/> to flush at the content-box origin. Returns the
    /// <see cref="NetPdf.Layout.Inline.InlineAtomic"/> to add to the line, or <see langword="null"/>
    /// (no shaper / a <see cref="NotSupportedException"/> inline pass / a zero-size box) so the caller
    /// skips + diagnoses.
    ///
    /// <para><b>First-cut approximations.</b> The atomic is BASELINE-aligned like an inline
    /// <c>&lt;img&gt;</c> (its margin-box bottom on the line's text baseline) — the spec's
    /// last-in-flow-line-box baseline (CSS 2.2 §10.8.1) is deferred; shrink-to-fit uses the
    /// max-content measured at the available width (no separate min-content pass); LTR
    /// horizontal-tb; <c>-flex</c>/<c>-grid</c>/<c>-table</c> atomics still skip.</para></summary>
    private NetPdf.Layout.Inline.InlineAtomic? TryBuildInlineBlockAtomic(
        Box inlineBlock,
        double availInlineContentSize,
        Dictionary<Box, InlineAtomicContent> atomicContents,
        CancellationToken cancellationToken)
    {
        if (_shaperResolver is null) return null;
        var s = inlineBlock.Style;
        var inlineChrome = s.InlineBorderPaddingPx();
        var blockChrome = s.BlockBorderPaddingPx();
        var marginInlineStart = s.ReadLengthPxOrZero(PropertyId.MarginLeft);
        var marginInlineEnd = s.ReadLengthPxOrZero(PropertyId.MarginRight);

        // The MEASURE content width + the used inline border box depend on the `width` slot
        // (post-PR-#190 Copilot review — a definite-width box must be measured at its OWN content
        // width, not the whole line, else multi-word content under-counts its height / wrong wrap):
        //  • a DEFINITE width (LengthPx) → its used border box honors box-sizing; content is laid
        //    out at the box's content width (border box minus inline chrome);
        //  • `auto` → measured at the available content width (the line minus this box's margins +
        //    chrome, clamped > 0), then SHRINK-TO-FITs to the measured content width
        //    (ContentInlineExtent — the widest LINE advance, ≤ the available width by wrapping)
        //    plus the inline chrome.
        var widthSlot = s.Get(PropertyId.Width);
        var definiteWidth = widthSlot.Tag == ComputedSlotTag.LengthPx;
        var availContent = availInlineContentSize - marginInlineStart - marginInlineEnd - inlineChrome;
        if (!(availContent > 0)) availContent = 1;

        double borderBoxW;
        double measureContentWidth;
        if (definiteWidth)
        {
            borderBoxW = BoxSizingHelper.DeclaredToBorderBox(s, Math.Max(0, widthSlot.AsLengthPx()), inlineChrome);
            measureContentWidth = borderBoxW - inlineChrome;          // the box's OWN content width
            if (!(measureContentWidth > 0)) measureContentWidth = 1;  // degenerate (chrome ≥ width)
        }
        else
        {
            measureContentWidth = availContent;
            borderBoxW = 0;                                           // set after measuring (shrink-to-fit)
        }

        BufferingMeasureSink buffer;
        try
        {
            buffer = NestedContentMeasurer.Measure(
                inlineBlock, measureContentWidth,
                blockBudget: NestedContentMeasurer.EffectivelyUnboundedBlockBudgetPx,
                shaperResolver: _shaperResolver,
                writingMode: WritingMode.HorizontalTb, isRtl: false,
                cancellationToken: cancellationToken);
        }
        catch (NotSupportedException)
        {
            return null;   // an inline-pass mismatch in the sub-box → skip + diagnose (no crash)
        }

        if (!definiteWidth) borderBoxW = buffer.ContentInlineExtent + inlineChrome;   // shrink-to-fit

        // Used BLOCK (border-box) size: a definite `height` honors box-sizing; `auto` = the measured
        // content block extent + the block chrome for BLOCK-child content (an inline-only-root
        // buffer's extent already folds the chrome in — same two-shape rule as the flex content-inset).
        var heightSlot = s.Get(PropertyId.Height);
        var borderBoxH = heightSlot.Tag == ComputedSlotTag.LengthPx
            ? BoxSizingHelper.DeclaredToBorderBox(s, Math.Max(0, heightSlot.AsLengthPx()), blockChrome)
            : (buffer.ContainsDecorationOwnerFragment
                ? buffer.ContentBlockExtent
                : buffer.ContentBlockExtent + blockChrome);

        if (!(borderBoxW > 0) || !(borderBoxH > 0)) return null;   // empty, no chrome → nothing to show

        atomicContents[inlineBlock] = new InlineAtomicContent(
            buffer,
            buffer.ContainsDecorationOwnerFragment,
            s.InlineStartBorderPaddingPx(),
            s.BlockStartBorderPaddingPx());

        // Inline-block last-line baseline (CSS 2.2 §10.8.1) — the box aligns by its LAST in-flow line
        // box's baseline, so it sits ON the surrounding text baseline (not by its bottom margin edge,
        // the img-ish first cut). Approximate the last line's descent below its baseline from the box's
        // ACTUAL last line-bearing fragment's metrics (post-PR-#194 task 2): the buffer captures the
        // descent below the deepest line box's baseline from THAT fragment's own font + line-height (its
        // TextMetricsStyle ?? box style + its real last-line height), so a nested-block inline-block whose
        // content has a different font-size / line-height than the outer box gets an exact baseline.
        // baseline = the content's last line bottom minus that descent, mapped from the border-box top
        // (the buffer's ContentBlockExtent folds the box chrome in for an inline-only root, but is
        // content-only for block children — the two-shape rule). With NO in-flow line box (e.g. only empty
        // blocks) — OR a computed `overflow` other than `visible` (CSS 2.2 §10.8.1 exception — a scroll/clip
        // container's last line isn't the box's baseline) — the baseline is the bottom margin edge (null →
        // the bottom-on-baseline placement). APPROXIMATION: an inline SPAN that overrides the font on the
        // last line (the layout layer has no per-RUN metrics); the 0.8/0.2-em ascent/descent.
        double? baselineFromBorderTopPx = null;
        if (buffer.HasInFlowLineBox && IsOverflowVisible(s))
        {
            var descentBelowLastLinePx = buffer.LastLineBoxDescentBelowBaselinePx;
            var blockStartChrome = s.BlockStartBorderPaddingPx();
            // Anchor to the LAST LINE BOX's bottom (post-PR-#192 review P1) — NOT ContentBlockExtent,
            // which a trailing non-line block / padding after the last line would push down. The buffer
            // tracks LastLineBoxBottom already net of the line fragment's own block-end chrome, so the
            // inline-only-root branch (C = border top) adds nothing further; the block-children branch
            // (C = content top) adds the inline-block's block-start chrome.
            var rawBaseline = buffer.ContainsDecorationOwnerFragment
                ? buffer.LastLineBoxBottom - descentBelowLastLinePx
                : blockStartChrome + buffer.LastLineBoxBottom - descentBelowLastLinePx;
            baselineFromBorderTopPx = Math.Clamp(rawBaseline, 0.0, borderBoxH);
        }

        return new NetPdf.Layout.Inline.InlineAtomic(
            inlineBlock,
            AdvancePx: borderBoxW + marginInlineStart + marginInlineEnd,
            BorderBoxWidthPx: borderBoxW,
            BorderBoxHeightPx: borderBoxH,
            MarginInlineStartPx: marginInlineStart,
            MarginBlockStartPx: s.ReadLengthPxOrZero(PropertyId.MarginTop),
            MarginBlockEndPx: s.ReadLengthPxOrZero(PropertyId.MarginBottom),
            BaselineFromBorderTopPx: baselineFromBorderTopPx);
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
    /// the same depth limit. The measure pass accepts a
    /// <see cref="CancellationToken"/> and threads it into
    /// <see cref="MeasureSubtreeVisualBlockExtentRecursive"/>, which checks it
    /// so a large-fan-out subtree (not just deep nesting) stays responsive to
    /// cancellation between children.</para></summary>
    private double MeasureSubtreeVisualBlockExtent(
        Box parent, CancellationToken cancellationToken, double parentContentBlockSize = 0,
        double blockOffsetOnPage = 0)
        => MeasureSubtreeVisualBlockExtentRecursive(
            parent, cancellationToken, depth: 0, parentContentBlockSize, blockOffsetOnPage);

    /// <summary>Phase 3 cycle-2d ("mid-split") — whether <paramref name="container"/> is eligible to
    /// be ENTERED and split between its children instead of moved wholly to the next page. Requires
    /// a block-flow container (the caller already gates on
    /// <see cref="IsBlockFlowContainerOwnedByBlockLayouter"/>) that is NOT <c>break-inside: avoid</c>
    /// and has &gt;= 2 in-flow block-level children — a single-child or leaf block can't be split at a
    /// child boundary (intra-block line splitting is a separate mechanism).</summary>
    private static bool IsEnterAndSplitEligible(Box container)
    {
        if (container.Style.AvoidsBreakInside())
        {
            return false;
        }
        var inFlowBlockChildren = 0;
        foreach (var c in container.Children)
        {
            if (c.Style.IsOutOfFlow() || c.Style.ReadFloatSide().HasValue || !c.IsBlockLevel)
            {
                continue;
            }
            if (++inFlowBlockChildren >= 2)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Phase 3 cycle-2d ("mid-split") — an estimate of the FIRST in-flow block-level child's
    /// block extent (the minimum unbreakable unit for the enter decision). Measures the first child
    /// directly; when that comes back opaque (~0 — a non-block-flow box whose content the measure
    /// doesn't fold), it measures a flex child's real content extent with the same premeasure helpers
    /// the container's fold uses. For an opaque grid / table / replaced first child (which we can't
    /// measure cheaply here) it FAILS CLOSED, returning 0 so the caller declines to enter-and-split
    /// (the container moves wholly) rather than entering on an under-estimate. Accurate for a skewed
    /// first child (much taller than its siblings). Returns 0 when there is no in-flow block child.</summary>
    private double EstimateFirstInFlowChildExtent(
        Box container, CancellationToken cancellationToken, double parentContentBlockSize)
    {
        Box? first = null;
        foreach (var c in container.Children)
        {
            if (c.Style.IsOutOfFlow() || c.Style.ReadFloatSide().HasValue || !c.IsBlockLevel)
            {
                continue;
            }
            first = c;
            break;
        }
        if (first is null)
        {
            return 0;
        }
        var direct = MeasureSubtreeVisualBlockExtent(first, cancellationToken, parentContentBlockSize);
        if (direct > 1.0)
        {
            return direct;
        }
        // The standalone measure came back opaque (~0) — the first child is a non-block-flow box whose
        // content the measure doesn't fold (e.g. an auto-height flex that isn't paginatable). Measure
        // its real content extent DIRECTLY (the same premeasure the container's own fold uses), so a
        // SKEWED first child that is much taller than its siblings is not under-reported by the naive
        // per-child average — which would wrongly enter-and-split (then force-overflow the first child
        // off the page) when it should move wholly.
        if (IsFlexContainer(first) && IsHeightAuto(first))
        {
            var flexChrome =
                first.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth)
                + first.Style.ReadLengthPxOrZero(PropertyId.PaddingTop)
                + first.Style.ReadLengthPxOrZero(PropertyId.PaddingBottom)
                + first.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
            var flexInline = Math.Max(0.0, _bfcContentInlineSize
                - first.Style.ReadLengthPxOrZero(PropertyId.MarginLeft)
                - first.Style.ReadLengthPxOrZero(PropertyId.MarginRight));
            var flexContent = first.Style.ReadFlexDirection().IsFlexColumnDirection()
                ? PreMeasureFlexMainExtent(first, flexInline, cancellationToken)
                : PreMeasureFlexCrossExtent(first, flexInline, cancellationToken);
            if (flexContent + flexChrome > 1.0)
            {
                return flexContent + flexChrome;
            }
        }
        // Grid / table / replaced opaque box whose real extent we can't measure cheaply here — FAIL
        // CLOSED: return 0 so the caller (which enters only when `firstChildExtent > 0 && <= remaining`)
        // does NOT enter-and-split; the container moves wholly to the next page. A per-child average
        // could under-report a skewed tall grid/table first child and wrongly enter (force-overflowing
        // it). Grid / table have their own row/track pagination on the fresh page, so declining the
        // mid-split here is safe (at worst slightly less dense for a grid/table-first container).
        return 0;
    }

    /// <summary>Per cycle 2c post-PR-29 review #1 (P0) + #3 (P1) —
    /// recursive worker for <see cref="MeasureSubtreeVisualBlockExtent(Box, CancellationToken, double, double)"/>.
    /// Renamed from the same-name overload to fix CS0419 ambiguous cref
    /// errors in class-level XML docs. CT plumbing matches
    /// <see cref="EmitBlockSubtreeRecursive"/> so an oversized broad
    /// subtree (large fan-out, not just deep nesting) doesn't make
    /// AttemptLayout unresponsive to cancellation.</summary>
    private double MeasureSubtreeVisualBlockExtentRecursive(
        Box parent, CancellationToken cancellationToken, int depth,
        // Body % height (percent-height cycle, post-PR-#164 review P1): the measured box's
        // DEFINITE containing height — its own `height: N%` resolves against it, and ITS resolved
        // content height chains to the children, mirroring the EMIT pass exactly (a desync here
        // under-reserved the cursor/pagination for %-height subtrees). 0 = indefinite → auto.
        double parentContentBlockSize = 0,
        // This box's block-start offset on the CURRENT page (accumulated through the recursion). Used so a
        // nested TABLE's dry-run reserves only the remaining page space when the table starts below the
        // page top — otherwise the subtree extent overshoots the page and a false forced-overflow clips
        // the table (offset-table clip fix). 0 = top of page (byte-identical to the pre-fix behavior).
        double blockOffsetOnPage = 0)
    {
        if (depth > MaxRecursionDepth)
        {
            throw new LayoutDepthExceededException(
                $"BlockLayouter measure-pass recursion depth exceeded {MaxRecursionDepth}; "
                + "pathologically deep box tree. This is a DoS guard against "
                + "untrusted HTML; legitimate documents rarely exceed depth 32.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        // Corpus-fidelity — an AnonymousBlock wrapper (block-in-inline / whitespace fixup,
        // BoxBuilder.FixupAnonymousBlocks) REUSES its real parent's ComputedStyle, so reading
        // padding / border / height / min-max off `parent.Style` here would charge the parent's chrome
        // to a box that PAINTS none (FragmentPainter skips anonymous boxes). The measure must match the
        // painter: an anonymous box has zero OWN chrome (its inline content still contributes its line
        // extent below). Without this, a padded auto-height container whose block children are separated
        // by pretty-print whitespace inflated by ~padding per whitespace gap (05 receipt box, 06
        // code-band).
        var isAnonymous = parent.Kind == BoxKind.AnonymousBlock;
        // Parent's own border-box block size from style.
        var pBorderStart = isAnonymous ? 0 : parent.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
        var pPaddingStart = isAnonymous ? 0 : parent.Style.ReadLengthPxOrZero(PropertyId.PaddingTop);
        var pBorderEnd = isAnonymous ? 0 : parent.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
        var pPaddingEnd = isAnonymous ? 0 : parent.Style.ReadLengthPxOrZero(PropertyId.PaddingBottom);
        var pHeight = isAnonymous ? 0 : parent.Style.ReadLengthOrPercentPx(PropertyId.Height, parentContentBlockSize);
        // Box-sizing + min/max-height MUST mirror the EMIT paths (the recursive
        // emit ~line 4784 + the main dispatch ~line 1372), else this measure
        // reserves a different block extent than the fragment paints — a
        // `box-sizing: border-box` block emits at its declared height while the
        // measure would over-reserve `declared + chrome`, opening a phantom gap or
        // a premature page break (PR #203 review [P1]). Byte-identical for
        // content-box without min/max (the common case). An anonymous box skips
        // the box-model + min/max clamp (its own chrome is zero, above).
        var parentBorderBoxBlockSize = isAnonymous
            ? 0
            : BoxSizingHelper.DeclaredToBorderBox(
                parent.Style, pHeight, pBorderStart + pPaddingStart + pPaddingEnd + pBorderEnd);
        if (!isAnonymous)
            parentBorderBoxBlockSize = parent.ClampBorderBoxToMinMax(
                parentBorderBoxBlockSize, PropertyId.MinHeight, PropertyId.MaxHeight,
                parentContentBlockSize);

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
                out _,
                cancellationToken);
            return Math.Max(parentBorderBoxBlockSize, inlineMargined);
        }

        // Per Phase 3 Task 12 sub-cycle 2 hardening (Finding 2) —
        // Table / InlineTable wrappers contribute the row-stack height
        // ON TOP of their own border-box-from-style. Pre-Finding-2 the
        // measure pass returned only the wrapper's own border-box
        // (style-derived height = 0 for auto), so a parent block whose
        // pagination decision depends on a nested table's true visual
        // extent (e.g., a div that should force a page break because
        // its embedded table is taller than the page remainder) would
        // see 0 from the measure pass + skip the break.
        if (parent.Kind is BoxKind.Table or BoxKind.InlineTable
            && _capturedFragmentainer is not null)
        {
            // Compute the wrapper's content-inline-size (the only
            // geometry the table measure consumes). Inline offsets are
            // unknown here — the emit recursion supplies real ones via
            // its own PreMeasureTableIfNeeded call. Cache by Box so
            // the recursion doesn't re-measure on revisits within the
            // same outer walk.
            var tBorderInlineStart = parent.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth);
            var tPaddingInlineStart = parent.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft);
            var tBorderInlineEnd = parent.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth);
            var tPaddingInlineEnd = parent.Style.ReadLengthPxOrZero(PropertyId.PaddingRight);
            // The wrapper's border-box inline size isn't directly
            // known to this recursion either; treat the BFC content-
            // inline-size as the available inline space. The emit pass
            // will use the actual wrapper-derived size; an over-
            // estimate of the column width here may yield a slight
            // under-estimate of row height (cells lay out wider than
            // they actually will, so wraps land later) which is a
            // safe-side error for the pagination break decision
            // (rounds toward NOT breaking — the emit pass then ships
            // the exact geometry).
            var wrapperInlineSize = _bfcContentInlineSize;
            var contentInlineSize = Math.Max(0,
                wrapperInlineSize - tBorderInlineStart - tPaddingInlineStart
                - tBorderInlineEnd - tPaddingInlineEnd);
            // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) — use
            // dry-run committed height. The lifted recursion-continuation
            // propagation now splits the table across pages cleanly; the
            // outer subtree-extent measure must reflect what fits on THIS
            // page so the outer break decision doesn't fire a false
            // PAGINATION-FORCED-OVERFLOW-001.
            var contentHeight = MeasureNestedTableContentExtent(
                parent, contentInlineSize, cancellationToken,
                useDryRunCommittedHeight: true,
                // The table's rows start after the wrapper's top chrome, at this page offset — so the
                // dry-run reserves only the remaining page space (offset-table clip fix).
                tableStartOffsetOnPage: blockOffsetOnPage + pBorderStart + pPaddingStart);
            var wrapperBorderPaddingBlock = pBorderStart + pPaddingStart
                + pPaddingEnd + pBorderEnd;
            var tableDriven = contentHeight + wrapperBorderPaddingBlock;
            return Math.Max(parentBorderBoxBlockSize, tableDriven);
        }

        // Per Phase 3 Task 17 cycle 5c.3 post-PR-#110 review P1#2 —
        // paginatable grid descendants project to the fragment budget
        // for the break-check measure. Pre-fix the body's subtree-
        // extent walk got back the grid's AUTHORED extent (= the
        // wrapper's own border-box from style — e.g., 200 for
        // <c>height: 200</c>) which exceeded page-remaining (= 150
        // on a 150px page) → body's outer break-check fired
        // BreakHere → forced-overflow path emitted
        // <see cref="PaginateDiagnosticCodes.PaginationForcedOverflow001"/>
        // + advanced the body's cursor by the full authored extent,
        // producing an empty trailing page (= ResumeAtChild past
        // the last child + a stale diagnostic) even though the
        // recursive grid dispatch actually paginated cleanly via
        // F3.
        //
        // <para>Projection: <c>min(authoredExtent, pageBlockSize)</c>
        // — the grid contributes at most one page's worth of extent
        // to the ancestor's break-check measure. The recursive grid
        // dispatch site computes its own pageRemaining based on
        // <c>childBlockOffset</c> + handles the row-by-row
        // pagination separately, so the over-projection here doesn't
        // affect emission; only the ancestor's break-check decision.</para>
        //
        // <para>Side effect: the ancestor's <c>marginBoxBlockSizeForCursor</c>
        // also reads <c>subtreeBlockExtent</c>, so the cursor
        // advance after the ancestor emit is bounded by
        // pageBlockSize too. F2 wrapper-resize at the recursive
        // grid dispatch site sets the actual grid wrapper extent
        // to the emitted-rows extent; any over-advance at the
        // ancestor level is bounded by the projection (= page
        // budget) instead of the unbounded authored extent —
        // ancestor's emit returns AllDone instead of spurious
        // PageComplete.</para>
        if (IsPaginatableGrid(parent)
            && !_disableGridPagination
            && _capturedFragmentainer is not null)
        {
            return Math.Min(parentBorderBoxBlockSize,
                _capturedFragmentainer.BlockSize);
        }

        // Non-block-pagination arc (explicit-height column-flex spurious
        // forced-overflow fix) — a paginatable FLEX descendant projects to the
        // fragment budget for the ancestor's break-check measure, EXACTLY like
        // the paginatable grid above. An EXPLICIT-height column flex (height
        // taller than the page) returns its rigid authored border-box from
        // style here; left un-projected, the ancestor's break-check sees an
        // oversized chunk + fires a spurious PAGINATION-FORCED-OVERFLOW-001 per
        // page even though the flex splits cleanly at ITEM boundaries (the
        // recursive flex dispatch handles the per-page split separately, based
        // on its own childBlockOffset-derived page-remaining). An AUTO-height
        // flex returns 0 here (no explicit height) so it never tripped the path
        // — which is why only the explicit-height case showed the warning.
        // Projection min(authored, page): the flex contributes at most one
        // page's worth to the ancestor measure. Gated by `!_disableFlexPagination`
        // (mirrors the grid projection's `!_disableGridPagination` above) — a
        // nested-content layouter that suppresses flex pagination renders the
        // flex ATOMICALLY (full extent), so it must NOT project to one page here
        // (that would under-measure + clip / overlap). For the normal document
        // layouter (pagination enabled) the flex genuinely paginates, so the
        // min-cap is correct.
        if (IsPaginatableFlex(parent)
            && !_disableFlexPagination
            && _capturedFragmentainer is not null)
        {
            var flexNatural = parentBorderBoxBlockSize;
            // Corpus-fidelity (03 itinerary footer overlap) — fold in an AUTO-height flex's CONTENT extent
            // so a block that STACKS such flex rows (a `.timeline` of `.day` rows) reports its true block
            // extent and a trailing sibling (`.note`) lands below the content instead of overlapping it.
            // FlexLinePacker sizes the flex from the items' DECLARED cross only (0 for auto), so without
            // this the measure is chrome-only. The emission independently resizes the wrapper via the
            // dispatch's LastEmittedBlockExtent consumer, so painted geometry stays consistent.
            if (IsHeightAuto(parent))
            {
                var flexChrome = pBorderStart + pPaddingStart + pPaddingEnd + pBorderEnd;
                var flexBorderBoxInline = Math.Max(0.0,
                    _bfcContentInlineSize
                    - parent.Style.ReadLengthPxOrZero(PropertyId.MarginLeft)
                    - parent.Style.ReadLengthPxOrZero(PropertyId.MarginRight));
                var flexContentExtent = parent.Style.ReadFlexDirection().IsFlexColumnDirection()
                    ? PreMeasureFlexMainExtent(parent, flexBorderBoxInline, cancellationToken)
                    : PreMeasureFlexCrossExtent(parent, flexBorderBoxInline, cancellationToken);
                flexNatural = Math.Max(flexNatural, flexContentExtent + flexChrome);
            }
            return Math.Min(flexNatural, _capturedFragmentainer.BlockSize);
        }

        // Non-flow block kinds (Flex/Grid/Replaced) are atomic to the
        // BlockLayouter — their inner geometry belongs to a dedicated
        // layouter. Return own size. (Tables were special-cased above
        // — Finding 2.)
        if (!IsBlockFlowContainerOwnedByBlockLayouter(parent))
        {
            return parentBorderBoxBlockSize;
        }

        // Per Phase 3 Task 14 cycle 1 hardening (Finding 1) — multicol
        // containers contribute the COLUMNIZED extent, not the serial
        // sum of their children. Without this branch the children walk
        // below stacks the multicol's children serially → reports a
        // serial total that grossly overestimates the actual columnized
        // block extent (a 2-column container with two 90-px children
        // reports ~180 px instead of the columnized ~90 px). The outer
        // cursor-advance + outer pagination then reserve false blank
        // space + dispatch wrong page breaks for siblings AFTER the
        // multicol container.
        //
        // The pre-measure runs a dry-run multicol layout against a
        // discarding sink + returns the maximum column-relative
        // BlockOffset+BlockSize reached. The result is folded with the
        // wrapper's border + padding to produce the wrapper's true
        // visual block extent.
        if (IsMulticolContainer(parent) && _capturedFragmentainer is not null)
        {
            var mBorderInlineStart = parent.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth);
            var mPaddingInlineStart = parent.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft);
            var mBorderInlineEnd = parent.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth);
            var mPaddingInlineEnd = parent.Style.ReadLengthPxOrZero(PropertyId.PaddingRight);
            var multicolWrapperInlineSize = _bfcContentInlineSize;
            var multicolContentInlineSize = Math.Max(1.0,
                multicolWrapperInlineSize - mBorderInlineStart - mPaddingInlineStart
                - mBorderInlineEnd - mPaddingInlineEnd);
            // For the measure pass we don't know the wrapper's actual
            // CSS height vs. fragmentainer-remaining; use the CSS
            // height when explicit, else the captured fragmentainer's
            // total block-size as a generous upper bound (the dry-run
            // only reports the actual used extent, not the bound).
            var multicolContentBlockBound = IsHeightAuto(parent)
                ? _capturedFragmentainer.BlockSize
                : Math.Max(1.0,
                    pHeight); // = ReadLengthPxOrZero(Height); auto returned 0 but we just gated out.
            // Synthesize a transient layout context for the
            // measure-time recursion. The captured outer layout
            // (LayoutContext) isn't accessible here; the discarding
            // sink doesn't write to the outer sink so the absolute
            // anchors don't matter.
            var measureLayout = new LayoutContext(_capturedFragmentainer)
            {
                Diagnostics = _diagnostics,
            };
            var columnExtent = PreMeasureMulticolColumnExtent(
                parent,
                contentInlineSize: multicolContentInlineSize,
                contentBlockSize: multicolContentBlockBound,
                fragmentainer: _capturedFragmentainer,
                layout: ref measureLayout,
                cancellationToken: cancellationToken);
            var wrapperBorderPadding = pBorderStart + pPaddingStart + pPaddingEnd + pBorderEnd;
            var multicolVisualExtent = columnExtent + wrapperBorderPadding;
            return Math.Max(parentBorderBoxBlockSize, multicolVisualExtent);
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
            // Corpus-fidelity — skip out-of-flow (absolute / fixed) children, mirroring the EMIT loop
            // (~line 1122). They don't advance the in-flow cursor and are placed by the post-flow abspos
            // / fixed passes; counting their subtree extent here over-inflated the container (05's
            // `.paid-stamp` added ~48pt). Preserve margin adjacency across them (same as the emit skip).
            if (child.Style.IsOutOfFlow())
            {
                continue;
            }
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

            // Child's top edge in the parent's border-box coordinates. Computed BEFORE the recursion so we
            // can thread the child's page offset (this box's page offset + the child's top within it) — a
            // nested table's dry-run then reserves only the remaining page space (offset-table clip fix).
            var childTopInParent = contentAreaTop + childCursor + topShift;

            // Recurse — child's subtree extent (= max of child's own border-box + its descendants).
            var childSubtreeExtent = MeasureSubtreeVisualBlockExtentRecursive(
                child, cancellationToken, depth + 1,
                parentContentBlockSize: pHeight,   // % height base chains (percent-height cycle).
                blockOffsetOnPage: blockOffsetOnPage + childTopInParent);
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

    /// <summary>Per Phase 3 Task 12 sub-cycle 1 hardening (Finding 1)
    /// — pre-measure phase for <see cref="BoxKind.Table"/> /
    /// <see cref="BoxKind.InlineTable"/> wrappers. Returns a
    /// configured <see cref="TableLayouter"/> with cached per-row
    /// measurements + the total row-stack content height, OR
    /// <see langword="null"/> when <paramref name="wrapperChild"/>
    /// is not a table wrapper.
    ///
    /// <para>The caller uses the returned content height to size the
    /// wrapper's border-box block extent (= <c>max(cssHeight,
    /// tableContentHeight)</c>) BEFORE emitting the wrapper fragment;
    /// the wrapper's emitted extent then drives
    /// <see cref="FragmentainerContext.UsedBlockSize"/>, preventing
    /// siblings from overlapping the table.</para>
    ///
    /// <para>The returned <see cref="TableLayouter"/> is then passed
    /// to <see cref="EmitTableInner"/> (alongside the final wrapper
    /// geometry) to run the emit pass. The two-step protocol avoids
    /// double-construction of <see cref="TableLayouter"/> (one for
    /// measure + another for emit).</para>
    /// </summary>
    private TableLayouter? PreMeasureTableIfNeeded(
        Box wrapperChild,
        double wrapperInlineOffset,
        double wrapperBlockOffsetExpected,
        double wrapperInlineSize,
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        out double tableContentHeight,
        out double tableMeasuredUsedInlineSize,
        CancellationToken cancellationToken,
        LayoutContinuation? incomingTableContinuation = null,
        bool useDryRunCommittedHeight = false)
    {
        tableContentHeight = 0;
        tableMeasuredUsedInlineSize = 0;
        if (wrapperChild.Kind is not (BoxKind.Table or BoxKind.InlineTable))
        {
            return null;
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
        var contentBlockOffset = wrapperBlockOffsetExpected
            + borderBlockStart + paddingBlockStart;
        var contentInlineSize = Math.Max(0,
            wrapperInlineSize
            - borderInlineStart - paddingInlineStart
            - borderInlineEnd - paddingInlineEnd);

        // Per Phase 3 Task 13 cycle 1 — thread the carried
        // TableContinuation (if any) through to the new TableLayouter.
        // The continuation came from a prior page's TableLayouter
        // returning PageComplete(TableContinuation) stashed in the
        // outer BlockContinuation.LayouterState; this method's
        // dispatch point unpacks it + passes through here.
        var tableLayouter = new TableLayouter(
            rootBox: wrapperChild,
            sink: _sink,
            incomingContinuation: incomingTableContinuation,
            diagnostics: _diagnostics,
            shaperResolver: _shaperResolver);
        tableLayouter.ConfigureEmission(
            contentInlineOffset: contentInlineOffset,
            contentBlockOffset: contentBlockOffset,
            contentInlineSize: contentInlineSize);

        // Per Finding 1 — run measure NOW so the caller can fold the
        // measured content extent into the wrapper's border-box size.
        // The caller is responsible for calling EmitTableInner after
        // emitting the wrapper fragment (which advances the cursor by
        // the post-Finding-1 wrapper extent).
        //
        // NOTE (`multi-page-allocation-churn`, PR #211 review [P1]): this DISPATCH path is the
        // EMIT path — its TableLayouter's buffered cell/caption fragments are later flushed by
        // EmitTableInner with their inline offsets BAKED IN at the wrapper's real content-inline
        // offset. It must NOT restore the cross-page `TableMeasurementCache`, which is populated by
        // the SUBTREE-extent measure at a placeholder inline offset of 0 — restoring it here would
        // flush cell content at x=0 for any indented / margined / nested table. The dispatch
        // already reuses the prior page's REAL-offset column layout via the incoming
        // `TableContinuation` (MeasureContentHeight's cache path); the cross-page cache is owned
        // exclusively by the measure-only subtree pass (which never emits).
        var naturalContentHeight = tableLayouter.MeasureContentHeight(
            fragmentainer, ref layout, cancellationToken);
        // Per Phase 3 Task 13 cycle 1 hardening (Finding 2) — when the
        // wrapper is sized for a single page (= the outer dispatch
        // path, NOT the nested-recursion atomic path) AND the table
        // will need to split, return the COMMITTED rows' block-size
        // for THIS page rather than the full natural extent. The
        // outer BlockLayouter then sizes the wrapper to the committed
        // size, suppressing the false PAGINATION-FORCED-OVERFLOW-001
        // diagnostic that would otherwise fire (the wrapper extent
        // would have been too tall for the page, but the table itself
        // splits cleanly internally).
        if (useDryRunCommittedHeight)
        {
            var resumeCont = incomingTableContinuation as TableContinuation;
            var resumeAt = resumeCont?.NextRowIndex ?? 0;
            // Per Phase 3 Task 17 cycle 5c.2d — thread the intra-cell row
            // split offset so the dry-run sizes the resume-page wrapper to
            // the row's REMAINING tail, not its full height.
            var resumeSplitOffset = resumeCont?.RowSplitOffset ?? 0.0;
            var dryRunCommitted = tableLayouter.DryRunCommittedBlockSize(
                fragmentainer, resumeAt,
                out _, out _, resumeSplitOffset,
                // Size the wrapper for the rows that fit at the table's ACTUAL page offset. In the outer
                // emit path this equals fragmentainer.UsedBlockSize, but in the nested-recursion path
                // UsedBlockSize is 0 while the table really starts at `wrapperBlockOffsetExpected` — using
                // the latter keeps the wrapper's committed size in lockstep with what the emission places,
                // so an offset table isn't over-sized (which clipped its last-fitting row).
                startOffsetOverride: wrapperBlockOffsetExpected);
            tableContentHeight = dryRunCommitted;
        }
        else
        {
            tableContentHeight = naturalContentHeight;
        }
        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 6 — also
        // surface the grid's used inline-size so the caller can widen
        // the wrapper's border-box inline extent when the grid
        // overflows the wrapper's content-inline-size (auto-table-
        // layout min-content overflow + fixed-layout overflow).
        tableMeasuredUsedInlineSize = tableLayouter.MeasuredUsedInlineSize;
        return tableLayouter;
    }

    /// <summary>Per Phase 3 Task 12 sub-cycle 2 hardening (Finding 2)
    /// — light-weight, offsets-agnostic measure of a nested table's
    /// content block extent. Called from
    /// <see cref="MeasureSubtreeVisualBlockExtentRecursive"/> so the
    /// outer block-flow pagination decision sees the table's true
    /// visual extent.
    ///
    /// <para>The wrapper's content-inline-offset / content-block-
    /// offset aren't known at measure-recursion time; they're
    /// computed only during the emit walk. We pass placeholder zeros
    /// to <see cref="TableLayouter.ConfigureEmission"/> — the
    /// MeasureContentHeight call doesn't write to the outer sink so
    /// the placeholders never reach the painter. The returned extent
    /// depends only on <paramref name="contentInlineSize"/> (it
    /// drives the equal-column split + cell content layout).</para>
    ///
    /// <para>Cached per <paramref name="wrapper"/> Box in
    /// <see cref="_measuredTableContentHeightCache"/> so repeated
    /// visits from the same outer walk don't re-run cell content
    /// layout. Cache cleared at AttemptLayout entry.</para></summary>
    private double MeasureNestedTableContentExtent(
        Box wrapper,
        double contentInlineSize,
        CancellationToken cancellationToken,
        bool useDryRunCommittedHeight = false,
        // The table's block-start offset on the current page within the subtree being measured. Passed to
        // the dry-run so it reserves only the REMAINING page space when the table starts below the page top
        // (offset-table clip fix). NaN = "no override" → fall back to fragmentainer.UsedBlockSize (the
        // top-of-page default); a NaN sentinel (not < 0) keeps a legitimately NEGATIVE offset — from
        // margin collapsing / negative margins — distinct from "unset".
        double tableStartOffsetOnPage = double.NaN)
    {
        _measuredTableContentHeightCache ??= new Dictionary<Box, double>();
        if (!useDryRunCommittedHeight
            && _measuredTableContentHeightCache.TryGetValue(wrapper, out var cached))
        {
            return cached;
        }
        if (contentInlineSize <= 0 || _capturedFragmentainer is null)
        {
            if (!useDryRunCommittedHeight)
            {
                _measuredTableContentHeightCache[wrapper] = 0;
            }
            return 0;
        }

        using var transientLayouter = new TableLayouter(
            rootBox: wrapper,
            sink: _sink,
            incomingContinuation: null,
            diagnostics: _diagnostics,
            shaperResolver: _shaperResolver);
        // Placeholder offsets (0/0) — MeasureContentHeight doesn't
        // write to the outer sink so the wrapper-anchor irrelevance
        // is safe. The cell-content inline translation baked into
        // each MeasuringFragmentSink uses these offsets, but the
        // buffers are discarded when the transient layouter goes
        // out of scope (Dispose drops the buffers).
        transientLayouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: contentInlineSize);
        // Per `multi-page-allocation-churn` — reuse a prior page's column layout from the cross-page
        // cache so this transient measure doesn't re-shape every cell on every page (the O(n²)
        // churn). RestoreMeasuredStateFromReuse pre-populates the deterministic, page-invariant
        // measured state so MeasureContentHeight below returns immediately; on a miss we measure
        // once + store. The dry-run extent below depends ONLY on row heights (offset-independent),
        // so it is byte-identical to the un-cached path.
        //
        // INVARIANT (PR #211 review [P1]): this MEASURE-ONLY pass is the SOLE owner of the
        // `TableMeasurementCache`. It measures at a PLACEHOLDER inline offset of 0 and uses a
        // discarding sink (MeasureContentHeight never writes to `_sink`, and the buffered cell
        // fragments are dropped on Dispose), so the offset-0 inline positions the cache's buffers
        // carry are NEVER flushed. The EMIT path (PreMeasureTableIfNeeded → EmitTableInner) must
        // NOT read this cache — it would place cells at x=0 for any indented table; it reuses the
        // real-offset column layout via the `TableContinuation` instead.
        object? tableReuse = null;
        var tableMeasureHit = _tableMeasureCache is not null
            && _tableMeasureCache.TryGet(wrapper, contentInlineSize, out tableReuse)
            && tableReuse is not null;
        if (tableMeasureHit)
        {
            transientLayouter.RestoreMeasuredStateFromReuse(tableReuse!);
        }
        var transientLayout = new LayoutContext(_capturedFragmentainer)
        {
            Diagnostics = _diagnostics,
        };
        var contentHeight = transientLayouter.MeasureContentHeight(
            _capturedFragmentainer, ref transientLayout, cancellationToken);
        if (!tableMeasureHit && _tableMeasureCache is not null
            && transientLayouter.ExtractColumnLayoutCacheForReuse() is { } freshTableLayout)
        {
            _tableMeasureCache.Store(wrapper, contentInlineSize, freshTableLayout);
        }

        // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) — when
        // the caller is doing a measure pass for a wrapper whose
        // nested table will now split cleanly via the lifted
        // recursion-continuation propagation, return the DRY-RUN
        // COMMITTED extent (= rows committed on the current page)
        // rather than the natural full-content extent. Without this
        // the outer subtree-extent measure would overestimate the
        // wrapper's height + the outer break decision would still
        // dispatch a forced-overflow path even though the table
        // splits cleanly internally.
        if (useDryRunCommittedHeight)
        {
            var dryRunCommitted = transientLayouter.DryRunCommittedBlockSize(
                _capturedFragmentainer, resumeAtRow: 0,
                out _, out _,
                startOffsetOverride: tableStartOffsetOnPage);
            // Caller doesn't cache the dry-run mode — different pages
            // may have different committed extents.
            return dryRunCommitted;
        }

        _measuredTableContentHeightCache[wrapper] = contentHeight;
        return contentHeight;
    }

    /// <summary>Per Phase 3 Task 14 cycle 1 hardening (Finding 1) —
    /// dry-run measure of a multicol container's actual columnized
    /// block extent (= the MAXIMUM block-axis position reached by any
    /// per-column content fragment, NOT the serial sum of the
    /// container's children).
    ///
    /// <para>Pre-fix: <see cref="MeasureSubtreeVisualBlockExtentRecursive"/>
    /// walked a multicol container's children as if they stacked
    /// serially, returning the sum of their block-axis extents. For a
    /// 2-column container with two 90-px children, the serial sum was
    /// ~180 px while the actual columnized layout (90 px in column 0 +
    /// 90 px in column 1) reaches only ~90 px in the block axis. The
    /// outer cursor advance + outer pagination decision saw the false
    /// 180 px → reserved blank space + dispatched wrong page breaks
    /// for siblings AFTER the multicol container.</para>
    ///
    /// <para>Post-fix: this helper constructs a transient
    /// <see cref="MulticolLayouter"/> with a DISCARDING sink + runs
    /// AttemptLayout against a synthetic fragmentainer sized to the
    /// container's allocated content-block-size; the discarding sink
    /// records the maximum BlockOffset + BlockSize reached by any
    /// emitted fragment. The maximum block-extent reached is the
    /// columnized height (= the longest column).</para>
    ///
    /// <para><b>Caveat: 2× cost.</b> Cycle 1 hardening doesn't cache;
    /// each multicol container is measured + emitted twice (once
    /// against the discarding sink, once against the real outer sink).
    /// Sub-cycle 2+ may cache the measurement per Box similarly to
    /// <see cref="MeasureNestedTableContentExtent"/>.</para>
    ///
    /// <para>Returns the columnized content-block extent — DOES NOT
    /// include the wrapper's own border + padding. The caller folds in
    /// the wrapper-axis border + padding to get the full border-box
    /// extent (mirrors the table wrapper pattern in
    /// <see cref="PreMeasureTableIfNeeded"/>).</para></summary>
    /// <param name="multicolContainer">The multicol container box
    /// (must satisfy <see cref="IsMulticolContainer"/>; the caller is
    /// the gate).</param>
    /// <param name="contentInlineSize">The container's content-box
    /// inline extent (border-box minus border + padding inline).</param>
    /// <param name="contentBlockSize">The container's content-box
    /// block extent that the layouter should fit the columns into.
    /// For <c>height: auto</c> containers the caller passes the
    /// fragmentainer-derived available block-size (Finding 2); for
    /// explicit-height containers it's the CSS height.</param>
    /// <param name="fragmentainer">The outer fragmentainer (for
    /// non-finite-geometry diagnostic threading + carrying the
    /// pagination context).</param>
    /// <param name="layout">The outer layout context (carries the
    /// ambient diagnostics sink).</param>
    /// <param name="cancellationToken">Threaded into the dry-run
    /// layouter so a long-running content measurement responds to
    /// cancellation.</param>
    /// <returns>The maximum block-axis extent reached by any column,
    /// measured RELATIVE TO THE CONTAINER'S CONTENT-BOX TOP. Returns
    /// 0 when the container has no in-flow content.</returns>
    private double PreMeasureMulticolColumnExtent(
        Box multicolContainer,
        double contentInlineSize,
        double contentBlockSize,
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        CancellationToken cancellationToken)
    {
        if (contentInlineSize <= 0 || contentBlockSize <= 0)
        {
            return 0;
        }

        var discardingSink = new MulticolDiscardingMeasureSink();
        using var dryRunLayouter = new MulticolLayouter(
            rootBox: multicolContainer,
            sink: discardingSink,
            incomingContinuation: null,
            diagnostics: null,
            shaperResolver: _shaperResolver);
        // ConfigureEmission anchors the COLUMN content in the OUTER
        // fragmentainer's coordinate space; for the dry-run the
        // absolute anchors don't matter (the discarding sink reads
        // BlockOffset relative to whatever anchor we pass). Anchor at
        // 0,0 so the measured max BlockOffset is the column-relative
        // extent we want to return.
        dryRunLayouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: contentInlineSize,
            contentBlockSize: contentBlockSize);

        // Synthetic outer fragmentainer for the dry-run. The
        // BlockSize is generous (= the supplied contentBlockSize)
        // since each column's sub-fragmentainer uses contentBlockSize
        // as its own block-size; the outer fragmentainer's size only
        // matters for the discarded forced-overflow diagnostic.
        var dryRunFragmentainer = new FragmentainerContext(
            contentInlineSize: contentInlineSize,
            blockSize: contentBlockSize);
        // Use a fresh dry-run resolver so the outer resolver isn't
        // polluted by per-column measurement checkpoints. Pass a
        // null-diagnostics layout context so the dry-run's
        // forced-overflow diagnostic (if any) doesn't double-emit.
        var dryRunLayout = new LayoutContext(dryRunFragmentainer)
        {
            Diagnostics = null,
        };
        using var dryRunResolver = new BreakResolver();
        _ = dryRunLayouter.AttemptLayout(
            dryRunFragmentainer,
            ref dryRunLayout,
            dryRunResolver,
            LayoutAttemptStrategy.LastResort,
            cancellationToken);

        return discardingSink.MaxColumnBlockExtent;
    }

    /// <summary>Per Phase 3 Task 15 L3 post-PR-#63 hardening F#1 —
    /// compute the flex wrapper's auto cross-extent WITHOUT stacking
    /// its children vertically. The default
    /// <see cref="MeasureSubtreeVisualBlockExtent(Box, CancellationToken, double, double)"/>
    /// path (used by every non-flex wrapper) sums the children's
    /// block-axis extents as if they were laid out in block-flow — for
    /// a flex container with <c>height: auto</c> + items 50/100/75 it
    /// returns ~225, while the actual single-line flex cross-extent is
    /// max(50,100,75) = 100. Without this helper the wrapper's
    /// <see cref="BoxFragment.BlockSize"/> is over-large + the outer
    /// cursor advance over-allocates page space, leaving phantom gaps
    /// after the flex container.
    ///
    /// <para>For the L3 single-line <c>flex-direction: row</c> case the
    /// cross-extent = max(item natural block-size). Mirrors
    /// <see cref="FlexLayouter.AttemptLayout"/>'s own
    /// <c>containerCrossSize</c> derivation for <c>height: auto</c>
    /// containers (the spec's max-content cross-size simplification per
    /// CSS Flexbox L1 §9.4). Surfacing it at premeasure time fixes the
    /// wrapper's painted size + the cursor advance.</para>
    ///
    /// <para><b>Sub-cycle L4+ scope.</b> Outer cross-extent (item
    /// margins + borders + padding contributions), baseline alignment's
    /// hypothetical baseline-stretch math, and multi-line wrap's
    /// per-line cross-extent sum all require integration the L3
    /// FlexLayouter doesn't yet have — same scope boundary as the
    /// FlexLayouter's own placement math. Both must move in lockstep.</para></summary>
    /// <param name="flexContainer">The flex container box (must satisfy
    /// <see cref="IsFlexContainer"/>; the caller is the gate).</param>
    /// <param name="borderBoxInlineSize">The flex wrapper's border-box inline size
    /// — its inline border + padding are subtracted to get the content inline size
    /// an auto-height row item's content is measured at (auto-width items fill it).</param>
    /// <param name="cancellationToken">Per Phase 3 Task 15 L4 post-PR-#64
    /// review hardening F#4 — propagate cancellation into the per-item
    /// loop so a long flex container with many children honors the
    /// caller's cancellation request. Mirrors the FlexLayouter's
    /// own per-item cancellation checks in <c>AttemptLayout</c>'s
    /// containerCrossSize fallback (FlexLayouter.cs ~449).</param>
    /// <returns>The maximum cross-axis (= block-axis under
    /// <c>flex-direction: row</c>) extent reached by any item. Returns
    /// 0 when the container has no block-level children — matching
    /// FlexLayouter's own derivation, which skips inline-level
    /// children + anonymous-flex-item wrapping in L3 scope.</returns>
    private double PreMeasureFlexCrossExtent(
        Box flexContainer,
        double borderBoxInlineSize,
        CancellationToken cancellationToken)
    {
        // Row-nowrap intra-item content fragmentation (auto-height completion) — the
        // container's CONTENT inline size, for measuring an auto-height row item's
        // content (an auto-WIDTH stretch item fills it; an explicit-width item uses
        // its width). The pre-measure only needs the cross extent to detect overflow
        // + grow the wrapper; the emission re-measures at the flex-resolved width.
        var flexContentInlineSize = Math.Max(0,
            borderBoxInlineSize
            - flexContainer.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth)
            - flexContainer.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth)
            - flexContainer.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft)
            - flexContainer.Style.ReadLengthPxOrZero(PropertyId.PaddingRight));
        // PR #189 review P1 — resolve each block-level item's MAIN (inline) width
        // through the SAME CSS Flexbox §9.7 path FlexLayouter emits at, so an
        // auto-height item is measured at its flex-resolved width (flex-basis /
        // grow / shrink / min-max), not the raw declared/container width. A
        // `flex: 0 0 150px` item in a 300px row pre-measured at 300 would
        // under-count its wrapped height, skip the pagination gate, then emit at
        // 150 + clip. Row-nowrap is a single line; the resolved main size is
        // order-independent, so DOM order is fine.
        var lineItems = new List<Box>();
        foreach (var child in flexContainer.Children)
            if (child.IsBlockLevel) lineItems.Add(child);
        // PR #204 review [P1] — the row-nowrap main-axis gutter (column-gap) consumes
        // the §9.7 free space, so the pre-measure resolves each item's width at the
        // SAME flex-resolved value FlexLayouter emits at (an item pre-measured too wide
        // under-counts its wrapped height → mis-skips the row-nowrap pagination gate).
        // PR #206 review [P1] — a `%` column-gap resolves against the inline content
        // size (definite here = flexContentInlineSize), matching the emission's
        // inlineGapBase; without the base a `%` gap collapsed to 0 in pre-measure only.
        var mainGap = flexContainer.Style.ReadFlexGridGapOrZero(
            PropertyId.ColumnGap, flexContentInlineSize);
        // Flex intrinsic-basis cycle — build the SAME max-content / min-content base-size
        // map FlexLayouter's emission builds (via the shared helper) so an item with an
        // explicit intrinsic `flex-basis` is resolved to the SAME width here as at
        // emission. Without it the pre-measure would size the item by its declared width
        // (→ wrong wrapped height → mis-triggered row-nowrap pagination). Null when no
        // shaper / no intrinsic item → the declared-size path, byte-identical.
        var intrinsicBaseSizes = FlexLayouter.BuildRowIntrinsicMainBaseSizes(
            lineItems, _shaperResolver, cancellationToken);
        var resolvedMain = lineItems.Count > 0
            ? FlexLayouter.ResolveFlexLineMainSizes(
                lineItems, PropertyId.Width, PropertyId.MinWidth, PropertyId.MaxWidth,
                flexContentInlineSize, mainGap, cancellationToken, intrinsicBaseSizes)
            : System.Array.Empty<double>();

        // Flex baseline-alignment cycle [P1] — when the container uses align-items /
        // align-self: baseline, a down-shifted item can extend below the packed line cross
        // size, so the auto-height wrapper extent must be the baseline-ADJUSTED extent (the
        // SAME formula the FlexLayouter emission uses). Gated so the common non-baseline
        // path is byte-identical. Per item we collect (baseline, cross, isBaselineAligned)
        // via the shared helpers; baseline items also need a content measurement EVEN when
        // explicit-height (to read their first baseline).
        var baselineActive = _shaperResolver is not null
            && FlexLayouter.ContainerUsesBaselineAlignment(flexContainer, lineItems);
        var containerAlign = flexContainer.Style.ReadAlignItems();
        List<(double Baseline, double CrossSize, bool IsBaselineAligned)>? baselineItems =
            baselineActive ? new List<(double, double, bool)>(lineItems.Count) : null;

        Dictionary<Box, BufferingMeasureSink>? measureCache = null;
        var maxCross = 0.0;
        var itemPos = 0;
        foreach (var item in flexContainer.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.IsBlockLevel) continue;
            var resolvedItemMain = resolvedMain[itemPos++];
            // Flex box-sizing cycle — the §9.7-resolved main size + the declared cross
            // (height) are BORDER-box sizes; subtract the item's inline border + padding
            // to measure content at its CONTENT width, and the declared height is mapped to
            // a border box honoring `box-sizing` so a padded explicit-height item sizes the
            // wrapper by its border box.
            var inlineChrome = item.Style.InlineBorderPaddingPx();
            var blockChrome = item.Style.BlockBorderPaddingPx();
            var itemCross = BoxSizingHelper.DeclaredToBorderBox(
                item.Style, item.Style.ReadLengthPxOrZero(PropertyId.Height), blockChrome);
            var itemIsBaseline = baselineActive
                && item.Style.ReadAlignSelf().ResolveAgainstContainerAlignItems(containerAlign).Value
                    == AlignItemsValue.Baseline;

            // Measure the item's content when (a) it is an auto-height content-determined
            // row item (so its measured block extent sizes the wrapper — mirrors
            // PreMeasureFlexMainExtent / column; the row-nowrap intra-item split engages on
            // overflow), OR (b) it is baseline-aligned (so its first baseline is known even
            // for an explicit-height item). Skipped without a shaper or for childless items.
            BufferingMeasureSink? buffer = null;
            var needsMeasure = _shaperResolver is not null && item.Children.Count > 0
                && (IsRowCrossSizeContentDetermined(item) || itemIsBaseline);
            if (needsMeasure)
            {
                // Measure at the flex-RESOLVED main (inline) CONTENT width (border box minus
                // the item's inline chrome); a non-positive resolution falls back to the
                // container content inline size.
                var itemInline = resolvedItemMain > inlineChrome
                    ? resolvedItemMain - inlineChrome
                    : flexContentInlineSize;
                measureCache ??= new Dictionary<Box, BufferingMeasureSink>(ReferenceEqualityComparer.Instance);
                if (!measureCache.TryGetValue(item, out buffer))
                {
                    // Effectively-unbounded block budget (not the page size) — the point
                    // of this pre-measure is to detect a row item TALLER than the page so
                    // the intra-item split engages; a page-sized inner fragmentainer would
                    // CLIP the content + under-report the natural cross extent (so the
                    // wrapper wouldn't overflow + never paginate). See the budget const doc
                    // for the (no-real-document) cap.
                    buffer = NestedContentMeasurer.Measure(
                        item, itemInline,
                        blockBudget: NestedContentMeasurer.EffectivelyUnboundedBlockBudgetPx,
                        shaperResolver: _shaperResolver,
                        writingMode: WritingMode.HorizontalTb, isRtl: false,
                        cancellationToken: cancellationToken,
                        // A cross/block-extent measure at the item's definite inline width — % padding
                        // resolves against it (PR #218 [P1 #2]); inherits an intrinsic outer measure.
                        purpose: _measurePurpose.ForNested(MeasurePurpose.DefiniteWidthExtent));
                    measureCache[item] = buffer;
                }
                if (IsRowCrossSizeContentDetermined(item))
                {
                    // The item's cross BORDER box = measured content + its block chrome for
                    // BLOCK-CHILD content; an inline-only-root buffer's extent already folds
                    // in the item's own border + padding (flex box-sizing cycle).
                    var measuredBorderBox = buffer.ContainsDecorationOwnerFragment
                        ? buffer.ContentBlockExtent
                        : buffer.ContentBlockExtent + blockChrome;
                    if (measuredBorderBox > itemCross) itemCross = measuredBorderBox;
                }
            }
            if (itemCross > maxCross) maxCross = itemCross;

            if (baselineItems is not null)
            {
                // SHARED with the FlexLayouter emission (ComputeItemBaselineAndCrossSize) so
                // the pre-measured wrapper extent matches the emitted item geometry.
                var (baseline, crossSize) = FlexLayouter.ComputeItemBaselineAndCrossSize(
                    item, buffer, FlexDirectionValue.Row, PropertyId.Height);
                baselineItems.Add((baseline, crossSize, itemIsBaseline));
            }
        }

        return baselineItems is not null
            ? System.Math.Max(maxCross, FlexLayouter.ComputeBaselineAdjustedLineExtent(baselineItems))
            : maxCross;
    }

    /// <summary>Row-nowrap intra-item fragmentation (auto-height) — whether a flex
    /// item's CROSS (block / height) size is content-determined under
    /// <c>flex-direction: row</c>: the declared <c>height</c> is <c>auto</c> (Unset /
    /// Keyword slot). Unlike the MAIN axis, the cross size doesn't depend on
    /// flex-basis (that's a main-axis input), so a definite height is the only thing
    /// that fixes a non-content cross size.</summary>
    private static bool IsRowCrossSizeContentDetermined(Box item)
    {
        var slot = item.Style.Get(PropertyId.Height);
        return slot.Tag is ComputedSlotTag.Unset or ComputedSlotTag.Keyword;
    }

    /// <summary>Per Phase 3 Task 15 L4 post-PR-#64 review hardening F#1 —
    /// compute the flex wrapper's auto main-extent for column direction.
    /// For L4 single-line column direction, the main extent = sum(item
    /// natural main-size). Mirrors what <see cref="FlexLayouter"/>
    /// itself uses for its main-axis cursor advance; surfacing it at
    /// premeasure time fixes the wrapper's <see cref="BoxFragment.BlockSize"/>
    /// + the outer cursor advance for column-direction auto-height
    /// containers. Without this helper the wrapper paints at the
    /// auto-resolved default (often 0 or the fragmentainer-remainder
    /// fallback) + the FlexLayouter's <c>containerMainSize</c>
    /// reads that tiny value as the column main-axis content extent,
    /// producing negative <c>freeSpace</c> for <c>justify-content: center</c>
    /// / <c>flex-end</c> + items overflowing the wrapper.
    ///
    /// <para><b>Mirrors</b> <see cref="PreMeasureFlexCrossExtent"/>'s
    /// shape (same call-site discipline, same per-item cancellation
    /// check). The two helpers are direction-orthogonal: row direction
    /// uses cross-extent (max of items' block-sizes); column direction
    /// uses main-extent (sum of items' block-sizes — the main axis IS
    /// the block axis in column direction).</para>
    ///
    /// <para><b>Sub-cycle L5+ scope.</b> Outer main-size (item margins
    /// + borders + padding contributions) + <c>flex-grow</c> /
    /// <c>flex-shrink</c> / <c>flex-basis</c> interpolation. The current
    /// L4 model treats each item's main-size as its declared
    /// <c>height</c> directly — sufficient for L4 Hello World column-
    /// stacking; sub-cycles refine.</para></summary>
    /// <param name="flexContainer">The flex container box (must satisfy
    /// <see cref="IsFlexContainer"/>; the caller is also responsible
    /// for gating on <c>flex-direction: column</c>).</param>
    /// <param name="borderBoxInlineSize">The flex wrapper's border-box inline
    /// size — its inline border + padding are subtracted to get the content
    /// inline size that auto-width items stretch to (= the width a
    /// content-determined item's content is measured at).</param>
    /// <param name="cancellationToken">Propagate cancellation into the
    /// per-item loop.</param>
    /// <returns>The sum of items' natural main-axis (= block-axis under
    /// column direction) sizes — a content-determined (auto-height) item
    /// contributes its measured content block extent. Returns 0 when the
    /// container has no block-level children.</returns>
    private double PreMeasureFlexMainExtent(
        Box flexContainer,
        double borderBoxInlineSize,
        CancellationToken cancellationToken)
    {
        // Backlog #7 (content-aware flex pre-measure) — the flex container's
        // CONTENT inline size (auto-width column items stretch to it).
        var flexContentInlineSize = Math.Max(0,
            borderBoxInlineSize
            - flexContainer.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth)
            - flexContainer.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth)
            - flexContainer.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft)
            - flexContainer.Style.ReadLengthPxOrZero(PropertyId.PaddingRight));
        Dictionary<Box, double>? measureCache = null;
        var totalMain = 0.0;
        var blockLevelCount = 0;
        foreach (var item in flexContainer.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.IsBlockLevel) continue;
            blockLevelCount++;
            // Flex box-sizing cycle — the declared height (main) + width (cross) are
            // BORDER-box sizes; map the height to a border box honoring `box-sizing`, and
            // measure content at the CONTENT width (border box minus the inline chrome).
            var inlineChrome = item.Style.InlineBorderPaddingPx();
            var blockChrome = item.Style.BlockBorderPaddingPx();
            // For column direction (the only caller as of L4 hardening),
            // main = block axis = Height (mapped to a border box).
            var mainExtent = BoxSizingHelper.DeclaredToBorderBox(
                item.Style, item.Style.ReadLengthPxOrZero(PropertyId.Height), blockChrome);
            // Backlog #7 — a CONTENT-determined (auto-height) item contributes
            // its measured CONTENT block extent (+ block chrome) instead of just the chrome,
            // so an auto-height column whose items are content-sized reports its real natural
            // extent + the wrapper overflows + (paginatable-flex) pagination engages. Mirrors
            // grid's content-aware PreMeasureGridRowExtent. Explicit-height items keep their
            // (box-sizing-mapped) declared height. Skipped without a shaper.
            if (_shaperResolver is not null
                && item.Children.Count > 0
                && IsColumnHeightContentDetermined(item))
            {
                // Measure at the item's cross (inline) CONTENT width: a stretch (auto-width)
                // item fills the container content inline size; an explicit-width item uses
                // its box-sizing-mapped width. Both minus the inline chrome. Memoized per box.
                var crossAuto = item.Style.Get(PropertyId.Width).Tag
                    is ComputedSlotTag.Unset or ComputedSlotTag.Keyword;
                var borderBoxCross = crossAuto
                    ? flexContentInlineSize
                    : BoxSizingHelper.DeclaredToBorderBox(
                        item.Style, item.Style.ReadLengthPxOrZero(PropertyId.Width), inlineChrome);
                var itemInline = borderBoxCross - inlineChrome;
                if (!(itemInline > 0)) itemInline = flexContentInlineSize;
                measureCache ??= new Dictionary<Box, double>(ReferenceEqualityComparer.Instance);
                if (!measureCache.TryGetValue(item, out var measuredBorderBox))
                {
                    var buf = NestedContentMeasurer.Measure(
                        item, itemInline,
                        blockBudget: _capturedFragmentainer?.BlockSize ?? 1_000_000,
                        shaperResolver: _shaperResolver,
                        writingMode: WritingMode.HorizontalTb, isRtl: false,
                        cancellationToken: cancellationToken,
                        // A main/block-extent measure at the item's definite inline width (PR #218 [P1 #2]).
                        purpose: _measurePurpose.ForNested(MeasurePurpose.DefiniteWidthExtent));
                    // The item's main BORDER box = measured content + its block chrome for
                    // BLOCK-CHILD content; an inline-only-root buffer's extent already folds
                    // in the item's own border + padding (flex box-sizing cycle).
                    measuredBorderBox = buf.ContainsDecorationOwnerFragment
                        ? buf.ContentBlockExtent
                        : buf.ContentBlockExtent + blockChrome;
                    measureCache[item] = measuredBorderBox;
                }
                if (measuredBorderBox > mainExtent) mainExtent = measuredBorderBox;
            }
            totalMain += mainExtent;
        }
        // PR #204 review [P1] — for column direction the main-axis gutter is row-gap;
        // the N-1 gutters add to the natural main extent (emission advances the main
        // cursor by mainGap between items, AttemptLayout ~L1550), so the overflow /
        // pagination trigger must count them too rather than under-reporting the height.
        // PR #206 review [P1] — no base is passed: a `%` row-gap resolves against the
        // BLOCK content size, which this pre-measure is itself deriving (indefinite), so
        // it resolves to 0. That matches the emission's blockGapBase gate (this path runs
        // only for auto-height columns, where the block axis is indefinite at emission too).
        var columnMainGap = flexContainer.Style.ReadFlexGridGapOrZero(PropertyId.RowGap);
        totalMain += System.Math.Max(0, blockLevelCount - 1) * columnMainGap;
        return totalMain;
    }

    /// <summary>Backlog #7 — mirrors <c>FlexLayouter.IsMainSizeContentDetermined</c>
    /// for the COLUMN (Height) axis: content-determined iff the used flex-basis
    /// is <c>content</c>, OR <c>auto</c> with the declared <c>height</c> also
    /// auto (Unset / Keyword slot). A definite flex-basis length / percentage or
    /// a definite height gives a definite main size that content must not
    /// override.</summary>
    private static bool IsColumnHeightContentDetermined(Box item)
    {
        var basis = item.Style.ReadFlexBasis();
        // Flex intrinsic-basis cycle — content / max-content / min-content all size the
        // COLUMN main (block) axis from the content (the existing block-extent measure).
        if (basis.Kind is FlexBasisKind.Content or FlexBasisKind.MaxContent or FlexBasisKind.MinContent)
            return true;
        if (basis.Kind == FlexBasisKind.Auto)
        {
            var slot = item.Style.Get(PropertyId.Height);
            return slot.Tag is ComputedSlotTag.Unset or ComputedSlotTag.Keyword;
        }
        return false;
    }

    /// <summary>Per Phase 3 Task 15 L6 — compute the flex wrapper's auto
    /// cross-extent for the multi-line algorithm (= sum of line cross-
    /// extents per CSS Flexbox L1 §9.4). Runs the same greedy line-
    /// packing as <see cref="FlexLayouter"/>'s own <c>PackLines</c> but
    /// returns only the cross-extent sum the wrapper's pre-measure
    /// needs — there's no shared FlexLine list because the pre-measure
    /// runs BEFORE FlexLayouter is constructed.
    ///
    /// <para><b>Algorithm.</b> Walk block-level items; accumulate main-
    /// axis size + track per-line max cross-axis size. When adding the
    /// next item would exceed <paramref name="containerMainSize"/>, the
    /// current line is committed (its cross-extent added to the running
    /// sum) and a new line starts. The first item on a line ALWAYS
    /// lands per CSS Flexbox L1 §9.3 (oversized solo items emit on
    /// their own line + overflow). After the loop the trailing line is
    /// committed.</para>
    ///
    /// <para><b>Direction-agnostic.</b> The caller passes the resolved
    /// <see cref="FlexDirectionValue"/>; this helper reads the
    /// direction-appropriate property for each item (main + cross). For
    /// the L6 outer-dispatch + recursion sites this is only called with
    /// the row directions (the column-direction main extent doesn't
    /// change with wrap in a single-pass auto derivation; see the
    /// caller comments). The direction parameter still rides along so
    /// the helper is reusable for the column case in L7+ if/when the
    /// auto-block-size column-wrap derivation matures (e.g., once a
    /// two-pass measure pipeline lands).</para>
    ///
    /// <para><b>Phase 3 Task 16 cycle 4c (P3 #8 from PR-#79):</b> the
    /// duplicate line-packing implementation that used to live here
    /// has been consolidated into the shared
    /// <see cref="FlexLinePacker.Pack"/> helper. The pre-measure
    /// now delegates: it calls Pack to get the FlexLine list + sums
    /// <see cref="FlexLine.LineCrossSize"/>. Line-boundary parity
    /// with <see cref="FlexLayouter"/>'s emission packer is
    /// guaranteed by construction — both call the same Pack
    /// implementation against the same sorted-by-order input.</para>
    ///
    /// <para>Per Phase 3 Task 15 L8 post-PR-#68 hardening F#1 — the
    /// per-item main-size contribution comes from
    /// <see cref="ComputedStyleLayoutExtensions.ResolveFlexItemHypotheticalMainSize"/>
    /// (inside <see cref="FlexLinePacker.Pack"/>) so flex-basis
    /// drives the wrap boundary, NOT the raw declared width.</para></summary>
    /// <param name="flexContainer">The flex container box.</param>
    /// <param name="direction">Resolved <c>flex-direction</c>; selects
    /// which property feeds main + cross.</param>
    /// <param name="containerMainSize">The container's main-axis content
    /// extent — the line-packing budget. Must be the same value
    /// FlexLayouter will see at AttemptLayout time (= the wrapper's
    /// content-axis size after border + padding subtraction); otherwise
    /// the pre-measure's packed lines could differ from the layouter's
    /// + the wrapper would paint at the wrong cross-extent.</param>
    /// <param name="cancellationToken">Propagates cancellation into the
    /// per-item loop.</param>
    /// <returns>The sum of line cross-extents. Returns 0 when the
    /// container has no block-level children — matches the L1-L5
    /// single-line path's behavior.</returns>
    private static double PreMeasureFlexMultiLineCrossExtent(
        Box flexContainer,
        FlexDirectionValue direction,
        double containerMainSize,
        CancellationToken cancellationToken)
    {
        // Per Phase 3 Task 16 cycle 4c — delegate to the shared
        // packer. Per post-PR-#84 review P2 #1, the pre-measure uses
        // the streaming <see cref="FlexLinePacker.SumCrossExtent"/>
        // entry point so we don't allocate a
        // <see cref="List{FlexLine}"/> just to sum + discard. Same
        // packing algorithm as <see cref="FlexLayouter"/>'s
        // emission-time call; only the return value differs.
        var sortedChildIndices =
            flexContainer.GetFlexChildrenInOrderSequence(cancellationToken);
        // §8 — pass the gap gutters so the pre-measure wrap decision + cross
        // extent match emission (FlexLayouter). Row: main=column-gap, cross=row-gap;
        // column swaps. No-op when no gap is set.
        // PR #206 review [P1] — a `%` MAIN gutter drives the wrap boundary, so it must
        // resolve against the same base emission uses or pre-measure packs different
        // lines. For row the main gutter is column-gap → the definite inline content
        // size (= containerMainSize, the line-packing budget). For column the main
        // gutter is row-gap → the block axis, which is still being derived here
        // (indefinite), so it resolves to 0 — matching the emission's blockGapBase
        // gate for an auto-height container. The cross (row-gap) gutter likewise
        // resolves against the indefinite block extent → 0.
        var isColumn = direction.IsFlexColumnDirection();
        var mainGap = flexContainer.Style.ReadFlexGridGapOrZero(
            isColumn ? PropertyId.RowGap : PropertyId.ColumnGap,
            isColumn ? double.NaN : containerMainSize);
        var crossGap = flexContainer.Style.ReadFlexGridGapOrZero(
            isColumn ? PropertyId.ColumnGap : PropertyId.RowGap);
        // Percentage cross-size (review P3-4): `containerDefiniteCrossSize` is intentionally left at the
        // default NaN here, so a `%` cross size a child declares reads 0 in this pre-measure. That is
        // CORRECT for what this sum feeds: it sizes an AUTO-cross-size (auto-height) wrap container from
        // its content, and a `%` cross against an INDEFINITE parent is auto → 0 (CSS Sizing 3 §5.1.1) —
        // matching emission's own `blockGapBase = auto ? NaN` gate. A DEFINITE-cross-size container
        // takes its declared cross size, not this sum, so the sum is discarded there (the resolved `%`
        // cross flows through `PackLines` + emission, which DO receive the definite size). No desync.
        return FlexLinePacker.SumCrossExtent(
            flexContainer, sortedChildIndices, direction,
            containerMainSize, isWrapping: true, cancellationToken, mainGap, crossGap);
    }

    /// <summary>Per Phase 3 Task 14 cycle 1 hardening (Finding 1) —
    /// discarding sink for the dry-run multicol pre-measure. Records
    /// the maximum (BlockOffset + BlockSize) seen for any emitted
    /// fragment — that maximum is the columnized block extent
    /// (= the longest column's bottom edge in the container's
    /// content-box coordinate space, because
    /// <see cref="PreMeasureMulticolColumnExtent"/> anchors the
    /// emission at 0,0).
    ///
    /// <para>Drops the fragments themselves; the caller only needs
    /// the extent. Rollback is a no-op since dry-run pagination
    /// never asks for one (the dry-run uses a fresh BreakResolver
    /// that doesn't know about earlier checkpoints).</para></summary>
    private sealed class MulticolDiscardingMeasureSink : IBlockFragmentSink
    {
        private int _cursor;

        public double MaxColumnBlockExtent { get; private set; }

        public int Cursor => _cursor;

        public void Emit(BoxFragment fragment)
        {
            _cursor++;
            // Anchored at 0,0 so BlockOffset is column-relative.
            var bottom = fragment.BlockOffset + fragment.BlockSize;
            if (bottom > MaxColumnBlockExtent)
            {
                MaxColumnBlockExtent = bottom;
            }
        }

        public void RollbackTo(int cursor)
        {
            // The dry-run uses a fresh BreakResolver so the resolver
            // never names a pre-existing checkpoint; this method is
            // unreachable in practice. Defensive no-op for forward-
            // compat with future multi-step measure paths.
            if (cursor < _cursor)
            {
                _cursor = cursor;
            }
        }

        public void UpdateFragmentBlockSize(int cursor, double newBlockSize)
        {
            // Per Phase 3 Task 17 cycle 5c.2b — measure-only sink
            // discards fragments after extent tracking, so there's no
            // stored fragment to mutate. The wrapper-resize doesn't
            // affect this sink's measurement output (= column natural
            // extent is captured at Emit time from BlockOffset +
            // BlockSize). Forward-compat no-op.
        }
    }

    /// <summary>Per Phase 3 Task 14 cycle 1 hardening (Finding 2) —
    /// predicate distinguishing <c>height: auto</c> from an explicit
    /// <c>height: 0</c> or <c>height: &lt;positive px&gt;</c> on a
    /// box's computed style. Returns <see langword="true"/> only when
    /// the height slot's tag is <see cref="ComputedSlotTag.Unset"/> (=
    /// the default <c>auto</c>) OR <see cref="ComputedSlotTag.Keyword"/>
    /// (= the explicit <c>auto</c> keyword).
    ///
    /// <para><b>Post-PR-#59 review hardening (Finding #7).</b> Mirrors
    /// the fix in <see cref="ComputedStyleLayoutExtensions.IsHeightAuto"/>
    /// — the pre-fix predicate returned <c>slot.Tag !=
    /// ComputedSlotTag.LengthPx</c>, which incorrectly reported
    /// <c>height: 50%</c> (Percentage) and <c>height: calc(...)</c>
    /// (Calc) as auto. Per CSS 2.1 §10.5 a percentage height resolves
    /// against the containing block's height — that's EXPLICIT sizing,
    /// not auto.</para>
    ///
    /// <para>The pre-cycle-3 <see cref="ComputedStyleLayoutExtensions.ReadLengthPxOrZero"/>
    /// path returns 0 for BOTH <c>auto</c> AND explicit <c>0px</c>;
    /// callers that need to distinguish these cases (Finding 2's
    /// auto-height multicol path) read the slot directly via this
    /// predicate.</para></summary>
    private static bool IsHeightAuto(Box box)
    {
        var slot = box.Style.Get(PropertyId.Height);
        // Height is type LengthPercentageAuto. Only the `auto` keyword OR
        // unset (= default `auto`) are auto. Percentage values are
        // explicit sizing relative to the containing block; LengthPx is
        // explicit absolute sizing. Per CSS 2.1 §10.5 percentage height
        // resolves against the containing block's height; treating it as
        // auto would route balanced multicol into the wrong layout path.
        return slot.Tag is ComputedSlotTag.Unset or ComputedSlotTag.Keyword;
    }

    /// <summary>CSS 2.1 §10.6.3 — the painted border box of an AUTO-height
    /// block-flow container must SPAN its in-flow children, not just its own
    /// chrome (padding + border). Returns the border-box block size to EMIT
    /// (= what the painter draws for background / border / border-radius):
    /// the content-spanning <paramref name="effectiveBlockSize"/>, CAPPED to
    /// the current page fragment so a subtree taller than the page paints a
    /// page-bounded rectangle (its content resumes on later pages) rather than
    /// a rectangle running off the page edge — the regression that reverted
    /// the first attempt (`auto-height-emit-vs-pagination`).
    ///
    /// <para>Byte-identical for every other box: explicit-height blocks, leaves
    /// (<paramref name="effectiveBlockSize"/> == <paramref name="borderBoxBlockSize"/>),
    /// and flex / grid / table / multicol wrappers (excluded by
    /// <see cref="IsBlockFlowContainerOwnedByBlockLayouter"/> — they own their own
    /// block sizing via the F2 wrapper-resize path) all return
    /// <paramref name="borderBoxBlockSize"/> unchanged. Crucially this affects
    /// ONLY the emitted fragment the painter consumes: pagination, the cursor
    /// advance, the break checks, and continuation accounting keep using the
    /// chrome <paramref name="borderBoxBlockSize"/> and the uncapped
    /// effective extent, so page splitting stays byte-identical.</para></summary>
    private static double ResolveAutoHeightEmittedBlockSize(
        Box box, double borderBoxBlockSize, double effectiveBlockSize,
        double blockOffset, FragmentainerContext? fragmentainer)
    {
        if (effectiveBlockSize <= borderBoxBlockSize
            || !IsHeightAuto(box)
            || !IsBlockFlowContainerOwnedByBlockLayouter(box))
        {
            return borderBoxBlockSize;
        }

        var spanned = effectiveBlockSize;
        if (fragmentainer is { SuppressBlockPagination: false })
        {
            // The rectangle starts at blockOffset (page-relative) and must not
            // extend past the fragmentainer bottom; cap to the page remainder,
            // never below the chrome.
            var pageRemaining = fragmentainer.BlockSize - blockOffset;
            spanned = pageRemaining > borderBoxBlockSize
                ? Math.Min(spanned, pageRemaining)
                : borderBoxBlockSize;
        }

        return Math.Max(borderBoxBlockSize, spanned);
    }

    /// <summary>Per Phase 3 Task 16 cycle 4a (PR #82, following the
    /// PR #81 execution order; closes P3 #7 from PR-#79) — shared
    /// helper consolidating the FlexLayouter construction +
    /// ConfigureEmission + AttemptLayout pattern used by BOTH the
    /// direct dispatch (in the top-level block-children loop at
    /// line ~2160) AND the recursive dispatch (in
    /// <see cref="EmitBlockSubtreeRecursive"/> at line ~3300).
    /// Pre-extraction the two sites had ~135 LOC + ~107 LOC of
    /// near-identical code that drifted between cycles 1 and 3 (=
    /// the recursive path stayed atomic after the direct path
    /// gained continuation handling, which is exactly the regression
    /// the PR-#79 + PR-#80 reviews caught).
    ///
    /// <para>Returns the layouter's <see cref="LayoutAttemptResult"/>
    /// verbatim so the caller can implement its own propagation
    /// logic (direct path returns <see cref="LayoutAttemptResult.PageComplete"/>
    /// from BlockLayouter; recursive path packages the result as a
    /// nested <see cref="BlockContinuation"/>).</para>
    ///
    /// <para><b>Resolver + strategy contract (per PR-#82 review P2
    /// #3, refreshed post-PR-#87 review).</b> The helper always
    /// constructs a fresh <see cref="BreakResolver"/> + always
    /// passes <see cref="LayoutAttemptStrategy.LastResort"/>.
    /// <b>Currently both parameters are INERT for FlexLayouter</b>:
    /// <see cref="FlexLayouter.AttemptLayout"/> null-checks the
    /// resolver argument but never consults it for break
    /// decisions; the strategy argument is entirely unused. The
    /// FlexLayouter's pagination logic is self-contained (=
    /// fragment-range determination via line packing in
    /// <see cref="FlexLayouter"/>'s emission loop; no resolver
    /// callout). Cycle-4b's production pagination ships using
    /// these inert defaults — no caller has surfaced a legitimate
    /// need for parameterization.</para>
    ///
    /// <para><b>Why we still pass them at all.</b>
    /// <list type="bullet">
    ///   <item><b>API symmetry</b> — the <c>ILayouter</c> /
    ///   AttemptLayout signature is shared with TableLayouter +
    ///   MulticolLayouter (which DO consult the resolver for
    ///   nested break decisions). Calling with fresh resolver +
    ///   LastResort matches the established nested-layouter
    ///   dispatch pattern in this codebase.</item>
    ///   <item><b>Future-isolation contract</b> — if FlexLayouter
    ///   ever starts consulting the resolver (= e.g., to defer
    ///   to outer break decisions for nested-flex resume
    ///   continuations), the fresh-resolver pattern prevents
    ///   inner break decisions from unwinding outer checkpointed
    ///   work. Mirrors TableLayouter's per-cell + MulticolLayouter's
    ///   per-column resolver isolation.</item>
    /// </list>
    /// If FlexLayouter starts using the resolver/strategy, revisit
    /// this helper — parameterize the args at that point so the
    /// caller can choose isolation policy / pass through its own
    /// strategy.</para>
    ///
    /// <para><b>Disposal contract.</b> The helper owns the
    /// FlexLayouter + BreakResolver lifetime via <c>using var</c>
    /// declarations; callers don't need to dispose anything from
    /// the returned <see cref="LayoutAttemptResult"/> (it carries
    /// only value-type fields + a LayoutContinuation record).</para></summary>
    private LayoutAttemptResult DispatchFlexInner(
        Box flexBox,
        double contentInlineOffset,
        double contentBlockOffset,
        double contentInlineSize,
        double contentBlockSize,
        FlexContinuation? incomingContinuation,
        bool allowPagination,
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        CancellationToken cancellationToken,
        out double lastEmittedBlockExtent,
        double? pageBlockBudget = null)
    {
        using var flexLayouter = new FlexLayouter(
            rootBox: flexBox,
            sink: _sink,
            incomingContinuation: incomingContinuation,
            diagnostics: _diagnostics,
            shaperResolver: _shaperResolver,
            // RC2 — flex is not a delegation boundary, so a positioned flex ITEM's
            // geometry must be recorded in THIS layouter's map for the outer abspos
            // pass to anchor an abspos descendant to it (else it defers/drops).
            recordPositionedGeometry: RecordPositionedBoxGeometry);
        // Per the flex-column pagination dual-input (mirrors DispatchGridInner's
        // grid page-budget): for a paginating column the contentBlockSize stays
        // the NATURAL main extent (so flex-grow/shrink resolve correctly) while
        // pageBlockBudget carries the page-remaining cut-off. Row-wrap +
        // non-paginating callers pass null ⇒ contentBlockSize doubles as the
        // budget (the pre-existing single-input behavior, byte-identical).
        flexLayouter.ConfigureEmission(
            contentInlineOffset: contentInlineOffset,
            contentBlockOffset: contentBlockOffset,
            contentInlineSize: contentInlineSize,
            contentBlockSize: contentBlockSize,
            allowPagination: allowPagination,
            pageBlockBudget: pageBlockBudget);
        using var flexResolver = new BreakResolver();
        var flexResult = flexLayouter.AttemptLayout(
            fragmentainer,
            ref layout,
            flexResolver,
            LayoutAttemptStrategy.LastResort,
            cancellationToken);
        // PR-#180 review P2 — surface the emitted content extent for the
        // caller's wrapper resize (mirrors DispatchGridInner's out param).
        lastEmittedBlockExtent = flexLayouter.LastEmittedBlockExtent;
        return flexResult;
    }

    /// <summary>Pre-measure helper for auto-height grid containers — the natural
    /// block-axis row extent the wrapper must reserve so following block-flow
    /// siblings don't overlap the grid AND so the (paginatable-grid) clamp can
    /// fire when the grid overflows the page.
    ///
    /// <para><b>Implicit rows (non-block-pagination completion).</b> Delegates to
    /// <see cref="GridSizing.Resolve"/>, which generates IMPLICIT row tracks from
    /// <c>grid-auto-rows</c> + auto-placement (CSS Grid §7.4) — so a grid whose
    /// rows come ONLY from implicit tracks (the common <c>grid-template-columns: …</c>
    /// + auto-flowed-rows case) reports its real row extent, not 0. The earlier
    /// early-return on an empty <c>grid-template-rows</c> left such grids at
    /// chrome-only height, so they never overflowed + never paginated.</para>
    ///
    /// <para><b>Real content INLINE size (PR-#181 review P1).</b> The caller
    /// threads the grid's actual content-box inline size so an auto-repeat column
    /// template (<c>repeat(auto-fill|auto-fit, …)</c>) resolves the SAME column
    /// count the real dispatch will — <see cref="GridSizing.ComputeAutoRepeatIterations"/>
    /// derives the repeat count from the inline extent, so a fake width of 1 would
    /// resolve 1 column + inflate the row count (false pagination / wrapper growth).
    /// The BLOCK axis stays the indefinite signal (1): these callers are
    /// <c>IsHeightAuto</c> grids whose grown block extent is exactly what this
    /// method computes (chicken-and-egg), and an indefinite block collapses fr
    /// ROWS per the <c>LayoutGridFrUnderIndefiniteApproximated001</c> path.</para>
    ///
    /// <para>A null diagnostic emitter keeps this dry-run from double-emitting the
    /// warnings the real emission pass surfaces; <paramref name="cancellationToken"/>
    /// (PR-#181 review P2) is threaded so the full placement/sizing pass honors
    /// caller cancellation on a large grid.</para></summary>
    private double PreMeasureGridRowExtent(
        Box gridBox, double contentInlineSize, CancellationToken cancellationToken)
    {
        // Cross-component cache (measurement-cache cycle) — captured at AttemptLayout
        // entry; reaches both the outer + recursive pre-grow sites without a
        // LayoutContext param (EmitBlockSubtreeRecursive has none).
        var sharedCache = _gridMeasureCache;
        // Non-block-pagination arc (grid CONTENT-sized rows) — wire the SAME
        // content measurer the real emission uses so an auto-row grid's
        // pre-measured extent reflects its cells' content. Without it a
        // content-sized grid stays chrome-height in the pre-measure + never
        // overflows the page → never paginates (and a following block-flow
        // sibling would overlap it). The dry-run defaults writing-mode /
        // direction to horizontal-tb / LTR (the real emission at GridLayouter
        // applies the cascaded values); the inner block budget is the captured
        // fragmentainer's block size — a single cell's content fits it.
        // Skipped when there's no shaper (text wouldn't shape anyway).
        // Memoize per item box (a row-spanning intrinsic item is otherwise
        // re-measured per intersected row track in ResolveIntrinsicTracks).
        // Keyed by (item, availInline) — post-PR-#184 review F1: the block-extent measurement depends on
        // the available (column) width, so the cache must not return a height measured at a stale width.
        // Cross-COMPONENT per-conversion cache (measurement-cache cycle) — PREFER
        // the shared cache when the root pipeline wired one, so this pre-measure +
        // the GridLayouter emission Resolve of the SAME grid shape each cell ONCE
        // (the cross-component win). The budget (the captured fragmentainer's block
        // size) + the horizontal-tb / LTR dry-run defaults match the emission for the
        // common case (the cache key carries them, so a writing-mode mismatch just
        // misses + re-measures — correct, never stale). Null → the local caches.
        var measureBudget = _capturedFragmentainer?.BlockSize ?? 1_000_000;
        // Per Phase 3 Task 18 (grid-fragment-plan-shared-sizing-deferral, partial) —
        // the natural row extent is page-invariant for a fixed-width grid (block budget
        // is the indefinite signal `1`; the inline size + measure budget don't change
        // across the pages it spans). Memoize it on the shared cache so resume pages +
        // rewind retries skip the redundant §11 sizing pass entirely (the cell shaping
        // was already shared; this elides the arithmetic too). Byte-identical: Resolve
        // is deterministic for the key.
        if (sharedCache is not null
            && sharedCache.TryGetRowExtentSum(gridBox, contentInlineSize, measureBudget, out var cachedRowExtent))
        {
            return cachedRowExtent;
        }
        var measureCache = new Dictionary<(Box Item, double AvailInline), double>();
        GridSizing.GridContentMeasurer? measurer = _shaperResolver is null
            ? null
            : (item, availInline) =>
            {
                if (sharedCache is not null)
                {
                    return sharedCache.BlockExtent(
                        item, availInline, measureBudget, _shaperResolver,
                        WritingMode.HorizontalTb, isRtl: false, cancellationToken);
                }
                var key = (item, availInline);
                if (measureCache.TryGetValue(key, out var cached)) return cached;
                var extent = NestedContentMeasurer.Measure(
                    item, availInline,
                    blockBudget: measureBudget,
                    shaperResolver: _shaperResolver,
                    writingMode: WritingMode.HorizontalTb, isRtl: false,
                    cancellationToken: cancellationToken,
                    // Grid row block-extent at the column's definite width — % padding real (PR #218 [P1 #2]).
                    purpose: _measurePurpose.ForNested(MeasurePurpose.DefiniteWidthExtent)).ContentBlockExtent;
                measureCache[key] = extent;
                return extent;
            };
        // Grid content-width cycle — also wire the MAX-CONTENT inline measurer so the pre-measure's
        // auto / intrinsic COLUMN widths match the real GridLayouter emission (else the pre-measure's
        // narrower columns would over-estimate row heights → mispredict pagination).
        var widthMeasureCache = new Dictionary<Box, double>(ReferenceEqualityComparer.Instance);
        GridSizing.GridContentMeasurer? widthMeasurer = _shaperResolver is null
            ? null
            : (item, availInline) =>
            {
                if (sharedCache is not null)
                {
                    return sharedCache.InlineExtent(
                        item, availInline, measureBudget, _shaperResolver,
                        WritingMode.HorizontalTb, isRtl: false, cancellationToken);
                }
                if (widthMeasureCache.TryGetValue(item, out var cached)) return cached;
                var extent = NestedContentMeasurer.Measure(
                    item, availInline,
                    blockBudget: measureBudget,
                    shaperResolver: _shaperResolver,
                    writingMode: WritingMode.HorizontalTb, isRtl: false,
                    cancellationToken: cancellationToken).ContentInlineExtent;
                widthMeasureCache[item] = extent;
                return extent;
            };
        var sizing = GridSizing.Resolve(
            gridBox: gridBox,
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: contentInlineSize > 0 ? contentInlineSize : 1,
            contentBlockSize: 1,  // indefinite block: auto-height grid's extent is what we compute
            emit: null,
            cancellationToken: cancellationToken,
            contentMeasurer: measurer,
            widthMeasurer: widthMeasurer);
        // Per Phase 3 Task 18 — memoize for the grid's subsequent pages / retries.
        sharedCache?.CacheRowExtentSum(
            gridBox, contentInlineSize, measureBudget, sizing.RowExtentSum);
        return sizing.RowExtentSum;
    }

    /// <summary>Per Phase 3 Task 17 cycle 5c.2a F1 — pre-dispatch row-fit
    /// probe for paginatable grids. Returns the natural extent of the row
    /// at <paramref name="rowIndex"/> in <paramref name="gridBox"/>'s
    /// resolved track sizing (= the next row about to emit on this page
    /// given the resume contract). Used by the outer-site
    /// <see cref="AttemptLayout"/> dispatch loop to predict the
    /// <c>GridLayouter</c> Strict-defer outcome BEFORE the wrapper
    /// fragment is emitted.
    ///
    /// <para>Per PR-#99 review P1#2 the probe uses the SAME sizing
    /// inputs as the actual <see cref="DispatchGridInner"/> below —
    /// <paramref name="contentInlineSize"/> + <paramref name="contentBlockSize"/>
    /// are the content-box geometry that
    /// <see cref="GridLayouter.ConfigureEmission"/> would receive
    /// (mirrors the <see cref="GridGeometryHelper.ComputeContentGeometry"/>
    /// derivation at the dispatch site). When an incoming
    /// <c>GridResumeCache</c> is supplied via
    /// <paramref name="incomingCache"/> AND its identity + inline-size
    /// match (mirrors
    /// <see cref="GridLayouter"/>'s cache-reuse validation), the probe
    /// reads <c>cache.RowBaseSizes[rowIndex]</c> directly — same row
    /// geometry GridLayouter would emit. On identity-match but inline-
    /// size mismatch, or no cache at all, the probe runs a fresh
    /// <see cref="GridSizing.Resolve"/> with the dispatch's actual
    /// geometry so fr / minmax / intrinsic tracks resolve identically.
    /// </para>
    ///
    /// <para>The probe mirrors <see cref="PreMeasureGridRowExtent"/>'s
    /// dry-run pattern (null diagnostic emitter; positions discarded).
    /// Returns 0 for any degenerate case — empty
    /// <c>grid-template-rows</c>, no resolved tracks, or an
    /// out-of-range <paramref name="rowIndex"/> — so the caller's
    /// row-fit comparison naturally falls through to the normal
    /// dispatch path. Cancellation propagates so a long item-walking
    /// sizing pass honors caller cancellation.</para>
    ///
    /// <para>Cycle 5c.2b will reactivate the cycle-5b outer-site
    /// extent-clamp + gate-flip + wire a shared
    /// <c>GridFragmentPlan</c> across pre-measure + F1 + dispatch (=
    /// PR-#99 review P2#2) so the §11 work runs once per attempt.
    /// Until then the probe duplicates sizing on non-cached calls;
    /// the cache hit path AVOIDS the duplicate work on resume.</para></summary>
    internal static double PreMeasureGridRowExtentAt(
        Box gridBox,
        int rowIndex,
        double contentInlineSize,
        double contentBlockSize,
        GridResumeCache? incomingCache,
        CancellationToken cancellationToken)
    {
        if (rowIndex < 0) return 0;

        // Per PR-#99 review P1#2 — when a valid incoming resume cache
        // exists, read the row size directly from the cache. Mirrors
        // GridLayouter's cache-reuse decision (identity + inline-size
        // match → use the cache). When inline-size mismatches the
        // cache (or no cache provided), fall through to a fresh
        // GridSizing.Resolve at the dispatch's actual content-box
        // geometry.
        if (incomingCache is not null
            && ReferenceEquals(incomingCache.GridIdentity, gridBox)
            && System.Math.Abs(
                incomingCache.OriginalContentInlineSize - contentInlineSize)
                <= GridSizing.SizeEpsilonPublic
            && rowIndex < incomingCache.RowBaseSizes.Length)
        {
            return incomingCache.RowBaseSizes[rowIndex];
        }

        var rows = gridBox.Style.ReadGridTemplateRows();
        if (rows.Items.IsDefaultOrEmpty) return 0;
        var sizing = GridSizing.Resolve(
            gridBox: gridBox,
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: System.Math.Max(1.0, contentInlineSize),
            contentBlockSize: System.Math.Max(1.0, contentBlockSize),
            emit: null,
            cancellationToken: cancellationToken);
        if (rowIndex >= sizing.RowSizes.Count) return 0;
        return sizing.RowSizes[rowIndex];
    }

    /// <summary>Per Phase 3 Task 17 cycle 1 (Hello World) + cycle 5
    /// (pagination parameters added; cycle 5b initial ship kept gates
    /// DORMANT) + cycle 5c.2b (gate flipped at the outer-site clamp;
    /// F2 wrapper-resize wired) — mirrors
    /// <see cref="DispatchFlexInner"/> for grid containers. The single
    /// helper used by all 3 dispatch sites (outer / recursive /
    /// forced-overflow-reroute).
    ///
    /// <para>Per Phase 3 Task 17 cycle 5c.2b F2 — exposes
    /// <c>gridLayouter.LastEmittedBlockExtent</c> via the
    /// <paramref name="lastEmittedBlockExtent"/> out parameter so the
    /// outer-site caller can resize the wrapper fragment + adjust
    /// cursor advance to the actual emitted-rows extent (not the
    /// clamped budget). The property is populated by
    /// <see cref="GridLayouter.AttemptLayout"/> on EVERY outcome
    /// (PageComplete, AllDone, Strict-defer, early-return). Callers
    /// that don't consume the value (= the forced-overflow re-route
    /// site, which dispatches atomically without
    /// <c>allowPagination</c>) pass <c>out _</c>. The outer + recursive
    /// dispatch sites consume the value for F2 wrapper-resize.</para>
    ///
    /// <para>Per Phase 3 Task 17 cycle 5c.3 — the optional
    /// <paramref name="pageBlockBudget"/> separates row-sizing input
    /// from pagination cut-off. The outer + recursive dispatch sites
    /// pass it when their clamp fired (= explicit-height grid + the
    /// authored extent exceeds page-remaining); the forced-overflow
    /// site passes <see langword="null"/> (= legacy single-input
    /// behavior where contentBlockSize doubles as the budget).</para></summary>
    private LayoutAttemptResult DispatchGridInner(
        Box gridBox,
        double contentInlineOffset,
        double contentBlockOffset,
        double contentInlineSize,
        double contentBlockSize,
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        CancellationToken cancellationToken,
        out double lastEmittedBlockExtent,
        bool allowPagination = false,
        GridContinuation? incomingContinuation = null,
        double? pageBlockBudget = null)
    {
        using var gridLayouter = new GridLayouter(
            rootBox: gridBox,
            sink: _sink,
            incomingContinuation: incomingContinuation,
            diagnostics: _diagnostics,
            shaperResolver: _shaperResolver);
        // Per Phase 3 Task 17 cycle 5c.3 — pass the page-budget through
        // to ConfigureEmission so explicit-height grids resolve rows
        // against the authored content-block-size while paginating
        // against the page-remaining budget. Auto-height callers + the
        // forced-overflow + recursive sites pass null which falls back
        // to using contentBlockSize as the budget (= pre-5c.3
        // behavior). Per the grid-explicit-height-paginate-deferral the
        // separation here is the minimum viable F3 — the full shared
        // GridFragmentPlan refactor (= grid-fragment-plan-shared-sizing-
        // deferral) is a future optimization that consolidates the
        // §11 sizing work; this cycle only fixes the correctness gap.
        gridLayouter.ConfigureEmission(
            contentInlineOffset: contentInlineOffset,
            contentBlockOffset: contentBlockOffset,
            contentInlineSize: contentInlineSize,
            contentBlockSize: contentBlockSize,
            allowPagination: allowPagination,
            pageBlockBudget: pageBlockBudget);
        using var gridResolver = new BreakResolver();
        var result = gridLayouter.AttemptLayout(
            fragmentainer,
            ref layout,
            gridResolver,
            LayoutAttemptStrategy.LastResort,
            cancellationToken);
        lastEmittedBlockExtent = gridLayouter.LastEmittedBlockExtent;
        return result;
    }

    /// <summary>Per Phase 3 Task 14 cycle 2 hardening (Finding #5) —
    /// shared content-box geometry computation for the 4 multicol-
    /// wrapper sites in <see cref="BlockLayouter"/>:
    /// <list type="bullet">
    /// <item>Outer dispatch (pre-cycle-2 site for top-level multicol children of the BlockLayouter root)</item>
    /// <item>Recursion dispatch (nested multicol descendants emitted by <see cref="EmitBlockSubtreeRecursive"/>)</item>
    /// <item>Premeasure outer (the dry-run pass that grows the wrapper's border-box block size before emit)</item>
    /// <item>Premeasure recursion (mirrors the outer premeasure for nested wrappers)</item>
    /// </list>
    /// Each site previously read 8 style properties (4 borders + 4
    /// paddings) inline, derived the same content-box inline / block
    /// sizes (clamped to ≥ 1.0 to avoid degenerate column boxes), and
    /// — for the emit sites — added inline/block-axis content offsets.
    /// Per the cycle 2 hardening Finding 5 refactor, all four sites
    /// now call <see cref="ComputeContentGeometry"/> to share the
    /// derivation. Behavior is byte-identical with the prior inline
    /// arithmetic.
    ///
    /// <para>The <c>borderBoxBlockOffset</c> parameter represents the
    /// wrapper's border-box top edge (in fragmentainer-block-axis
    /// coordinates) — for emit sites this is the pre-computed
    /// <c>blockOffset</c> / <c>childBlockOffset</c>; for premeasure
    /// sites it is the hypothetical <c>fragmentainer.UsedBlockSize +
    /// topShift</c> / <c>childBlockOffset</c>. The
    /// <c>borderBoxInlineOffset</c> parameter similarly represents the
    /// wrapper's border-box left edge (<c>inFlowInlineOffset</c> for
    /// the outer site, <c>childInlineOffset</c> for the recursion
    /// site). Premeasure sites compute the offsets too but the
    /// caller may discard them — the helper-side cost is negligible.
    /// </para></summary>
    private static class MulticolGeometryHelper
    {
        /// <summary>Computed content-box geometry of a multicol
        /// wrapper. <c>ContentInlineSize</c> / <c>ContentBlockSize</c>
        /// are clamped to ≥ 1.0 (mirrors the pre-extraction inline
        /// <c>Math.Max(1.0, ...)</c> clamps that prevented
        /// <see cref="MulticolLayouter"/> from receiving a degenerate
        /// per-column extent). <c>BorderPaddingBlockSum</c> is the
        /// vertical border + padding sum (used by the caller to grow
        /// the wrapper's border-box block size to fit the columnized
        /// content extent).</summary>
        public readonly record struct ContentBox(
            double ContentInlineSize,
            double ContentBlockSize,
            double ContentInlineOffset,
            double ContentBlockOffset,
            double BorderPaddingBlockSum);

        /// <summary>Compute the wrapper's content-box geometry from
        /// its border-box size + the wrapper's own borders + paddings
        /// (read from <paramref name="multicolBox"/>'s computed
        /// style). For <c>height: auto</c> wrappers the per-column
        /// block-size is derived from the fragmentainer's REMAINING
        /// block-space (= <c>fragmentainer.BlockSize -
        /// borderBoxBlockOffset - BorderPaddingBlockSum</c>) per CSS
        /// Multi-column L1 §3.5 "fill available column space"
        /// semantics for auto-height containers. For explicit-height
        /// wrappers the per-column block-size is derived from the
        /// wrapper's content area (= <c>borderBoxBlockSize -
        /// BorderPaddingBlockSum</c>).
        ///
        /// <para>The <paramref name="fragmentainer"/> parameter is
        /// nullable to accommodate the recursion-dispatch call site
        /// where <c>_capturedFragmentainer</c> may be null (BlockLayouter
        /// not entered via the main outer loop). When null AND
        /// <paramref name="isHeightAuto"/> is true, the helper falls
        /// through to the explicit-height path — matching the legacy
        /// recursion-dispatch behavior that guarded the auto-height
        /// branch with <c>&amp;&amp; _capturedFragmentainer is not
        /// null</c>.</para></summary>
        public static ContentBox ComputeContentGeometry(
            Box multicolBox,
            double borderBoxInlineSize,
            double borderBoxBlockSize,
            double borderBoxInlineOffset,
            double borderBoxBlockOffset,
            FragmentainerContext? fragmentainer,
            bool isHeightAuto)
        {
            var borderInlineStart =
                multicolBox.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth);
            var paddingInlineStart =
                multicolBox.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft);
            var borderInlineEnd =
                multicolBox.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth);
            var paddingInlineEnd =
                multicolBox.Style.ReadLengthPxOrZero(PropertyId.PaddingRight);
            var borderBlockStart =
                multicolBox.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
            var paddingBlockStart =
                multicolBox.Style.ReadLengthPxOrZero(PropertyId.PaddingTop);
            var borderBlockEnd =
                multicolBox.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
            var paddingBlockEnd =
                multicolBox.Style.ReadLengthPxOrZero(PropertyId.PaddingBottom);

            var borderPaddingBlockSum =
                borderBlockStart + paddingBlockStart + borderBlockEnd + paddingBlockEnd;

            var contentInlineSize = Math.Max(1.0,
                borderBoxInlineSize
                - borderInlineStart - paddingInlineStart
                - borderInlineEnd - paddingInlineEnd);

            double contentBlockSize;
            if (isHeightAuto && fragmentainer is not null)
            {
                var remaining =
                    fragmentainer.BlockSize - borderBoxBlockOffset - borderPaddingBlockSum;
                contentBlockSize = Math.Max(1.0, remaining);
            }
            else
            {
                contentBlockSize = Math.Max(1.0,
                    borderBoxBlockSize - borderPaddingBlockSum);
            }

            var contentInlineOffset =
                borderBoxInlineOffset + borderInlineStart + paddingInlineStart;
            var contentBlockOffset =
                borderBoxBlockOffset + borderBlockStart + paddingBlockStart;

            return new ContentBox(
                ContentInlineSize: contentInlineSize,
                ContentBlockSize: contentBlockSize,
                ContentInlineOffset: contentInlineOffset,
                ContentBlockOffset: contentBlockOffset,
                BorderPaddingBlockSum: borderPaddingBlockSum);
        }
    }

    /// <summary>Per Phase 3 Task 12 sub-cycle 1 hardening (Finding 1)
    /// — emit phase for <see cref="BoxKind.Table"/> /
    /// <see cref="BoxKind.InlineTable"/> wrappers. The caller has
    /// already emitted the wrapper fragment + advanced the cursor by
    /// the wrapper's border-box extent. This method runs the
    /// cached-measurement-driven emit pass that appends the row +
    /// cell fragments + flushes the buffered cell content in paint-
    /// safe order.
    ///
    /// <para>No-op when <paramref name="tableLayouter"/> is
    /// <see langword="null"/> — the predicate keeps the call site
    /// simple (the same site handles both table + non-table children
    /// uniformly).</para></summary>
    private static LayoutAttemptResult EmitTableInner(
        TableLayouter? tableLayouter,
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        IBreakResolver resolver,
        CancellationToken cancellationToken)
    {
        if (tableLayouter is null)
        {
            return LayoutAttemptResult.AllDone(cost: 0);
        }

        // Per Phase 3 Task 13 cycle 1 — propagate the table's emit
        // result. Pre-cycle-1 the result was discarded because the
        // table always emitted atomically. Now the table may return
        // PageComplete(TableContinuation) when its rows overflow; the
        // BlockLayouter dispatch site repackages this into its own
        // BlockContinuation with the TableContinuation stashed in
        // LayouterState so the resume page can re-construct the
        // TableLayouter with the resume row.
        //
        // Per the existing contract, this call uses LastResort strategy
        // because the OUTER BlockLayouter dispatch already committed to
        // emitting the wrapper fragment + advanced its cursor past it
        // (= the table can't return NeedsRewind without dirtying the
        // outer-emitted wrapper). Cycle 2+ may relax the contract by
        // capturing a pre-table-emit checkpoint on the outer layouter.
        var result = tableLayouter.AttemptLayout(
            fragmentainer,
            ref layout,
            resolver,
            LayoutAttemptStrategy.LastResort,
            cancellationToken);
        // Per Phase 3 Task 13 cycle 1 — dispose unconditionally. The
        // resume-page constructs a FRESH TableLayouter (cycle 1
        // hardening Finding 8 plumbs a ColumnLayoutCache through the
        // TableContinuation so the resume doesn't re-measure; the
        // current layouter's per-instance buffers + diagnostic sink
        // are no longer needed regardless of AllDone vs PageComplete).
        // The caller has already consumed the returned LayoutAttemptResult
        // before this line runs.
        tableLayouter.Dispose();
        return result;
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
