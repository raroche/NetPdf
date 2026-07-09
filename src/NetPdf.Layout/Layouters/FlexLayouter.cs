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

namespace NetPdf.Layout.Layouters;

/// <summary>
/// Per Phase 3 Task 15 cycle 1 (Hello World) — flexbox layout per
/// CSS Flexible Box Layout L1 (<see href="https://www.w3.org/TR/css-flexbox-1/"/>).
/// A block container with <c>display: flex</c> (= <see cref="BoxKind.FlexContainer"/>)
/// or <c>display: inline-flex</c> (= <see cref="BoxKind.InlineFlexContainer"/>)
/// becomes a flex container; its direct children become flex items
/// laid out along the main axis (the inline axis for the L1 default
/// <c>flex-direction: row</c>, the block axis for <c>flex-direction:
/// column</c> per Phase 3 Task 15 L4).
///
/// <para><b>Cycle 1 (Hello World) scope.</b> The layouter implements
/// the absolute minimum end-to-end path so subsequent cycles have a
/// scaffold to grow:</para>
/// <list type="bullet">
///   <item><b>Per Phase 3 Task 15 L4 + L5</b> — L5 honors all 4
///   <c>flex-direction</c> values <b>for LTR horizontal-tb</b> (the
///   L1 default writing mode). Per CSS Flexbox §3.1 the axis mapping
///   depends on the cascaded <c>direction</c> + <c>writing-mode</c>
///   properties; for RTL or vertical writing modes the spec-correct
///   axis mappings differ. Pipeline support for <c>direction</c> /
///   <c>writing-mode</c> is L7+ scope (L6 shipped <c>flex-wrap:
///   wrap</c> without expanding the direction pipeline); tracked in
///   <c>docs/deferrals.md#flex-layouter-features</c>. Under the LTR
///   horizontal-tb assumption: for <c>row</c>: main = inline axis
///   (horizontal, left-to-right); cross = block axis (vertical); items
///   flow left-to-right. For <c>column</c>: main = block axis
///   (vertical, top-to-bottom); cross = inline axis (horizontal); items
///   stack top-to-bottom. The axis-mapping layer (see
///   <c>GetAxisProperties</c> below) translates the spec-abstract
///   "main / cross" terminology to the property IDs the layouter reads
///   (<c>PropertyId.Width</c> / <c>Height</c>) at each direction.
///   <c>justify-content</c> always controls main-axis packing (so
///   column direction makes it govern block-axis offsets), and
///   <c>align-items</c> always controls cross-axis placement (so
///   column direction makes it govern inline-axis offsets). For the
///   reversed variants <c>row-reverse</c> + <c>column-reverse</c>
///   (L5 new): per CSS Flexbox L1 §5.1 "same as row / column but
///   main-start and main-end are swapped". The per-item placement
///   math (cross-axis alignment, stretch, justify-content start-offset
///   + between-spacing) is unchanged; only the FINAL main-axis offset
///   assigned to each fragment is flipped around the container's
///   main-extent (see the <c>mainOffsetForEmission</c> branch in the
///   emission loop). The effect is that main-start and main-end swap
///   per CSS Flexbox §5.1 — items are placed using the SAME justify-
///   content algorithm (flex-start / flex-end / center / space-* etc.)
///   but the resulting main-axis offsets are mirrored across the
///   container's main-extent, which yields reverse DOM ordering at
///   the emission level. The specific packing edge depends on the
///   justify-content value (flex-start in reverse → right/bottom
///   edge; flex-end in reverse → left/top edge; center stays
///   centered with items in reverse DOM order; distribution values
///   honor their natural distribution in the reversed direction).
///   Cross-axis behavior is unchanged: <c>row-reverse</c> still has
///   block as cross axis; <c>column-reverse</c> still has inline as
///   cross axis.</item>
///   <item><b>Per Phase 3 Task 15 L6</b> — <c>flex-wrap: nowrap</c>
///   (the L1-L5 default) preserves the single-line algorithm; items
///   may overflow the container's main-axis extent if their natural
///   sizes don't fit. <c>flex-wrap: wrap</c> activates the multi-line
///   algorithm per CSS Flexbox L1 §6.3 + §9.3: items are greedy-packed
///   onto lines along the main axis (adding an item that would exceed
///   the container's main extent starts a new line; the first item on
///   a line always lands even if it itself overflows), lines stack on
///   the cross axis at cross-start with line[i] anchored at
///   <c>contentCrossOffset + sum(line[0..i-1].LineCrossSize)</c>, and
///   each line's <c>align-items</c> placement targets the LINE'S
///   cross-extent (= max(item cross-size on the line)) rather than the
///   container's full cross extent. Per CSS Flexbox L1 §9.4 the
///   container's auto cross-size with wrapping = sum of line cross-
///   extents (handled by <see cref="BlockLayouter"/>'s
///   <c>PreMeasureFlexMultiLineCrossExtent</c>). Per Phase 3 Task 15
///   L11 + post-PR-#71 hardening F#1/F#2: <c>flex-wrap: wrap-reverse</c>
///   ships proper cross-axis SWAP per CSS Flexbox L1 §6.3 — the
///   container's cross-start moves to the physical cross-end (= the
///   line stack lands at the swapped origin), and each LINE's cross-
///   start swaps too (= per-item <c>align-items</c> / <c>align-self</c>
///   FlexStart/FlexEnd anchor to the line's new cross-edges). The
///   <c>LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001</c> diagnostic is
///   no longer emitted (the approximation closed in L11); the code
///   stays registered for backward-compat.</item>
///   <item><b>L7 — <c>align-content</c> multi-line cross-axis
///   distribution.</b> Per CSS Flexbox L1 §8.4 + CSS Box Alignment L3
///   §6 the layouter honors seven base values for cross-axis line
///   distribution on multi-line containers (= <c>flex-wrap: wrap</c>
///   producing &gt;= 2 lines): <c>flex-start</c> (pack at cross-start
///   — the L1-L6 default), <c>flex-end</c> (pack at cross-end),
///   <c>center</c> (centered on cross axis), <c>space-between</c>
///   (equal gaps between adjacent lines, no edge spacing),
///   <c>space-around</c> (equal gaps with half-size leading + trailing
///   edges), <c>space-evenly</c> (equal gaps including edges), and
///   <c>stretch</c> (the spec default for <c>normal</c> per §8.4 —
///   grows each line's cross-extent by an equal share of the free
///   cross-space; items in a stretched line use the LARGER extent for
///   their align-items math). The logical-axis aliases (<c>start</c> /
///   <c>end</c>) and directional aliases (<c>left</c> / <c>right</c>)
///   map to <c>flex-start</c> / <c>flex-end</c> under the L1 default
///   LTR + <c>flex-direction: row</c>; writing-mode-aware mapping is
///   L8+ scope. Per Phase 3 Task 15 L7 post-PR-#67 hardening F#1 + F#2:
///   the single-line gate is <c>flex-wrap: nowrap</c> (NOT <c>lineCount
///   &lt;= 1</c>) per CSS Flexbox §9.4 — a wrapping container with one
///   produced line still gets align-content applied (the default
///   <c>normal → stretch</c> still stretches that single line; <c>safe
///   X</c> / <c>unsafe X</c> overflow modifiers are now applied per CSS
///   Box Alignment L3 §5.3 mirroring the L2 justify-content pattern:
///   <c>safe X</c> forces safe-start fallback on overflow regardless of
///   value; <c>unsafe X</c> honors the alignment even on overflow;
///   default mode gives distribution values + stretch the safe-start
///   fallback (stretch never shrinks lines) + positional values their
///   natural (possibly-negative) offset.</item>
///   <item><b>L2 — <c>justify-content</c> main-axis alignment.</b>
///   The layouter honors the six effective values per CSS Box
///   Alignment L3 §4.5: <c>flex-start</c> (default packing at main-
///   start), <c>flex-end</c> (pack at main-end), <c>center</c>
///   (center on main-axis), <c>space-between</c> (equal gaps between
///   items, no edge spacing), <c>space-around</c> (equal gaps with
///   half-size edges), <c>space-evenly</c> (equal gaps including
///   edges). The logical aliases <c>start</c> / <c>end</c> + the
///   directional aliases <c>left</c> / <c>right</c> map to
///   <c>flex-start</c> / <c>flex-end</c> under L1's default LTR +
///   <c>flex-direction: row</c>. The <c>normal</c> initial value
///   maps to <c>flex-start</c> per CSS Flexbox L1 §8.2. The
///   <c>stretch</c> value (the grid default) has no effect on flex
///   main-axis packing per spec, so it maps to <c>flex-start</c>.
///   Per Phase 3 Task 15 L2 post-PR-#62 hardening F#1 + F#2 the
///   <c>safe</c> / <c>unsafe</c> overflow modifiers (= compound
///   keywords like <c>safe center</c>) are now decoded into the
///   overflow-mode channel of <see cref="ResolvedJustifyContent"/>
///   + applied per CSS Box Alignment L3 §5.3: <c>safe X</c> forces
///   safe-start fallback on overflow regardless of value;
///   <c>unsafe X</c> honors the specified alignment even on
///   overflow; default (no modifier) gives distribution values the
///   safe-start fallback + positional values their natural
///   (possibly-negative) offset. Writing-mode-aware <c>left</c> /
///   <c>right</c> mapping is L3+ scope.</item>
///   <item><b>L3 — <c>align-items</c> cross-axis alignment.</b> Per
///   CSS Flexbox L1 §8.3 + CSS Box Alignment L3 §6 the layouter honors
///   four base values: <c>flex-start</c> (cross-start pack),
///   <c>flex-end</c> (cross-end pack), <c>center</c> (cross-axis
///   centering), and <c>stretch</c> (auto-sized items resized to fill
///   the container's cross extent; explicitly-sized items keep their
///   declared block-size per §7.2). <c>normal</c> resolves to
///   <c>stretch</c> per §8.3 (the computed default). Logical aliases
///   (<c>start</c> / <c>end</c> / <c>self-start</c> / <c>self-end</c>)
///   map to <c>flex-start</c> / <c>flex-end</c> under the L1 default
///   LTR + <c>flex-direction: row</c> — writing-mode-aware mapping is
///   L4+ scope. The <c>safe</c> / <c>unsafe</c> overflow-position
///   modifiers (compound keywords like <c>safe center</c>) decode into
///   the overflow-mode channel of <see cref="ResolvedAlignItems"/> +
///   apply per CSS Box Alignment L3 §5.3: <c>safe X</c> forces safe-
///   start fallback on overflow regardless of value; <c>unsafe X</c>
///   honors the alignment even on overflow; default mode gives
///   positional values their natural (possibly-negative) offset on
///   overflow. The container's cross-axis extent
///   (<c>containerCrossSize</c>) derives from the container's explicit
///   <c>height</c> when set, else the max of the items' natural block-
///   sizes (= the spec's max-content cross-size simplification for the
///   L1 default single-line case; sub-cycle L4+ will refine). The
///   <c>align-content</c> baseline family resolves to its SPEC FALLBACK
///   (CSS Box Alignment L3 §5.3 — flex lines are not a baseline-sharing
///   group, so baseline content-alignment doesn't apply): <c>baseline</c> /
///   <c>first baseline</c> → safe start, <c>last baseline</c> → safe end.
///   <c>anchor-center</c> still falls through to <c>stretch</c> (CSS Anchor
///   Positioning, out of Flexbox L1 scope). The per-item <c>align-self</c> override SHIPPED in
///   Phase 3 Task 15 L9 (per CSS Box Alignment §4.3); see
///   <c>docs/deferrals.md#flex-layouter-features</c>.</item>
///   <item><b>Item main-size resolution</b> (per Phase 3 Task 15
///   L8 + L8 post-PR-#68 F#1 hardening): each item's main-size
///   comes from the §9.7 flexibility resolution using the item's
///   <c>flex-basis</c> (= hypothetical main-size; defaults to the
///   declared <c>width</c>/<c>height</c> when <c>flex-basis: auto</c>),
///   <c>flex-grow</c>, and <c>flex-shrink</c> factors. Cross-size
///   still uses the declared property; cross-axis flexibility (=
///   align-items stretch with auto cross-size) is L10+ scope for
///   intrinsic cross-axis sizing.</item>
///   <item><b>Item order</b> (per Phase 3 Task 15 L10): items
///   visually pack in EFFECTIVE FLEX ORDER per CSS Flexbox §5.4 (=
///   stable sort by (<c>order</c>, DOM index)). The cascade default
///   <c>order: 0</c> preserves source order, so the L1-L9
///   behavior is identical when no item declares a non-zero order.</item>
///   <item>Multi-page flex container splitting ACTIVE as of Task 16
///   cycle 4b. When eligible per <see cref="IsPaginatablePerStyle"/>
///   (row + wrap + non-wrap-reverse) AND the dispatching BlockLayouter
///   passes <c>allowPagination: true</c> (= the grown natural extent
///   overflows the remaining fragmentainer space), this layouter emits
///   only the lines that fit + returns
///   <see cref="LayoutAttemptOutcome.PageComplete"/> carrying a
///   <see cref="FlexContinuation"/> for the rest (CSS Flexbox L1 §10
///   + CSS Fragmentation L3 §4.4 progress rule). Ineligible
///   containers (column / wrap-reverse / nowrap) + eligible
///   containers that fit on a single page remain atomic.</item>
/// </list>
///
/// <para><b>Dispatch contract.</b> The dispatching
/// <see cref="BlockLayouter"/> emits the flex wrapper's
/// <see cref="BoxFragment"/> FIRST (the wrapper's border-box geometry
/// is owned by the regular block-flow sizing path), then constructs
/// this layouter, calls <see cref="ConfigureEmission"/> with the
/// wrapper's content-area geometry, then calls
/// <see cref="AttemptLayout"/>. The layouter emits one
/// <see cref="BoxFragment"/> per flex item INSIDE the wrapper's
/// content-area at the item's main-axis cursor + the container's
/// content-block-start. Mirrors <see cref="TableLayouter"/>'s and
/// <see cref="MulticolLayouter"/>'s "caller emits wrapper first"
/// contract.</para>
///
/// <para><b>Why no dedicated <see cref="LayoutContinuation"/> in cycle 1.</b>
/// The atomic-to-outer-pagination contract means cycle 1 always
/// returns <see cref="LayoutAttemptOutcome.AllDone"/>; there is no
/// resume state to carry. The constructor REJECTS any non-null
/// <see cref="LayoutContinuation"/> so a future caller bug doesn't
/// silently restart from item 0 + duplicate content. Cycle 2 will
/// relax this for <see cref="FlexContinuation"/> just as cycle 2 of
/// multicol relaxed it for <see cref="MulticolContinuation"/>.</para>
///
/// <para><b>Cancellation.</b> The token is checked before iteration
/// + between each item so a long item list doesn't burn through the
/// caller's deadline.</para>
/// </summary>
internal sealed class FlexLayouter : ILayouter, IDisposable
{
    private readonly Box _rootBox;

    /// <summary>PR #218 review [P1 #1] — the pass purpose captured from the (transitive) layout
    /// context at <see cref="AttemptLayout"/> entry, so a flex container measured intrinsically
    /// propagates that to its item-content measures (the flush + the intrinsic-basis probe) instead
    /// of letting them emit out-of-flow content / persist a probe-derived percentage inset.</summary>
    private MeasurePurpose _measurePurpose;
    private readonly IBlockFragmentSink _sink;
    private readonly IPaginateDiagnosticsSink? _diagnostics;
    private readonly IShaperResolver? _shaperResolver;

    /// <summary>Per RC2 (abspos-in-flex-item fidelity) — the dispatching
    /// <see cref="BlockLayouter"/>'s <c>RecordPositionedBoxGeometry</c>, invoked
    /// as each flex ITEM's border box is emitted so an item that establishes an
    /// absolute containing block (<c>position</c> != <c>static</c>) is recorded
    /// in the outer layouter's positioned-geometry map. FlexLayouter is NOT a
    /// delegation boundary (it spawns no per-item nested BlockLayouter), so an
    /// abspos descendant of a positioned flex item is resolved by the OUTER
    /// abspos pass — which walks <c>Box.Parent</c> to the item and needs its
    /// geometry here, else it defers (drops) the descendant. No-ops for the
    /// non-positioned item (the callee filters). Null when unused (tests /
    /// measure passes). Args: (item box, inlineOffset, blockOffset, inlineSize,
    /// blockSize) in sink coordinates — the same values passed to
    /// <see cref="_sink"/>.<c>Emit</c>.</summary>
    private readonly System.Action<Box, double, double, double, double>? _recordPositionedGeometry;

    private double _contentInlineOffset;
    private double _contentBlockOffset;
    private double _contentInlineSize;
    private double _contentBlockSize;
    private bool _emissionConfigured;

    // Per Phase 3 Task 15 L10 — block-level flex children in
    // EFFECTIVE FLEX ORDER per CSS Flexbox L1 §5.4 (= sorted by
    // (order, DOM-index)). Populated once per AttemptLayout entry by
    // <see cref="ComputedStyleLayoutExtensions.GetFlexChildrenInOrderSequence"/>. Both
    // PackLines (line packing) AND the emission loop / flexibility
    // helpers walk this list, so item visual ordering follows the
    // `order` property while preserving DOM order for ties. When no
    // item declares a non-zero <c>order</c>, the helper short-circuits
    // to DOM order, so the L1-L9 behavior is preserved verbatim.
    private List<int> _sortedFlexChildIndices = new();

    // Per Phase 3 Task 15 L11 — the `_emittedWrapReverseDiagnostic`
    // one-shot guard was removed when the L6 hardening F#4
    // diagnostic (LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001) stopped
    // emitting (wrap-reverse now ships proper line-stacking
    // reversal). The diagnostic code stays registered for backward
    // compatibility but no longer fires from this layouter.

    /// <summary>Construct a layouter for the flex container
    /// <paramref name="rootBox"/>. The box's <see cref="Box.Kind"/>
    /// MUST be <see cref="BoxKind.FlexContainer"/> or
    /// <see cref="BoxKind.InlineFlexContainer"/>; otherwise the
    /// constructor throws.
    ///
    /// <para>Per Phase 3 Task 15 cycle 1 (Hello World) — the cycle 1
    /// dispatch gate is BoxKind-based (set by <see cref="BoxKind"/>
    /// from <see cref="DisplayMapper"/> when <c>display: flex</c> /
    /// <c>inline-flex</c> is declared). Contrast with
    /// <see cref="MulticolLayouter"/>'s property-based gate
    /// (<c>column-count</c> / <c>column-width</c> on a regular
    /// <see cref="BoxKind.BlockContainer"/>).</para></summary>
    /// <param name="rootBox">The flex container box.</param>
    /// <param name="sink">The same sink the caller uses; per-item
    /// fragments append after the flex wrapper fragment (which the
    /// caller has already emitted).</param>
    /// <param name="incomingContinuation">Per cycle 1 — MUST be
    /// <see langword="null"/>. Multi-page flex splitting via
    /// <see cref="FlexContinuation"/> is sub-cycle 2+ scope. A
    /// non-null value throws so misrouted continuations surface
    /// loudly rather than silently restarting from item 0.</param>
    /// <param name="diagnostics">Optional diagnostic sink for future
    /// cycles' flex-specific warnings (e.g., forced-overflow
    /// fallback). Cycle 1 reads it for parity with the other
    /// layouters' constructor shapes but doesn't currently emit any
    /// flex-specific code.</param>
    /// <param name="shaperResolver">Optional inline shaper resolver
    /// for future cycles that lay out inline content inside flex
    /// items. Cycle 1 doesn't yet route through the inline pass so
    /// the parameter is stashed but unused.</param>
    /// <param name="recordPositionedGeometry">Per RC2 — optional callback
    /// (the dispatching <see cref="BlockLayouter"/>'s
    /// <c>RecordPositionedBoxGeometry</c>) invoked as each flex ITEM's border
    /// box is emitted, so a positioned item is recorded in the outer layouter's
    /// abspos-geometry map and an abspos descendant anchors to it. Null (the
    /// default) disables recording — used by tests / measure passes.</param>
    /// <exception cref="ArgumentNullException">When
    /// <paramref name="rootBox"/> or <paramref name="sink"/> is null.</exception>
    /// <exception cref="ArgumentException">When
    /// <paramref name="rootBox"/>'s kind is neither
    /// <see cref="BoxKind.FlexContainer"/> nor
    /// <see cref="BoxKind.InlineFlexContainer"/>, OR when
    /// <paramref name="incomingContinuation"/> is non-null.</exception>
    public FlexLayouter(
        Box rootBox,
        IBlockFragmentSink sink,
        LayoutContinuation? incomingContinuation = null,
        IPaginateDiagnosticsSink? diagnostics = null,
        IShaperResolver? shaperResolver = null,
        System.Action<Box, double, double, double, double>? recordPositionedGeometry = null)
    {
        ArgumentNullException.ThrowIfNull(rootBox);
        ArgumentNullException.ThrowIfNull(sink);

        if (rootBox.Kind is not (BoxKind.FlexContainer or BoxKind.InlineFlexContainer))
        {
            throw new ArgumentException(
                "FlexLayouter expects a box with BoxKind.FlexContainer or "
                + $"BoxKind.InlineFlexContainer; got BoxKind.{rootBox.Kind}. "
                + "The dispatching BlockLayouter's IsFlexContainer predicate "
                + "is the guard for this contract — the wrong kind would "
                + "silently emit no items and drop all content, hiding the "
                + "integration bug.",
                nameof(rootBox));
        }

        // Per Phase 3 Task 16 cycle 1 — accept a non-null
        // <see cref="FlexContinuation"/> for the multi-page flex split
        // resume contract. The continuation's <c>LineIndex</c> is the
        // first line to emit on this page (= lines [0, LineIndex)
        // were emitted on a prior page). Any other continuation type
        // is misrouted + surfaces loudly. Mirrors MulticolLayouter
        // cycle 2's pattern (Finding 5 on the post-PR-#60 review
        // hardening pass).
        if (incomingContinuation is not null
            && incomingContinuation is not FlexContinuation)
        {
            throw new ArgumentException(
                "FlexLayouter accepts only FlexContinuation; "
                + $"got {incomingContinuation.GetType().Name}. "
                + "Misrouted continuation is a layouter-dispatch bug.",
                nameof(incomingContinuation));
        }
        _incomingFlexContinuation = incomingContinuation as FlexContinuation;

        _rootBox = rootBox;
        _sink = sink;
        _diagnostics = diagnostics;
        _shaperResolver = shaperResolver;
        _recordPositionedGeometry = recordPositionedGeometry;
    }

    /// <summary>Per Phase 3 Task 16 cycle 1 — the incoming
    /// <see cref="FlexContinuation"/> when this layouter was created
    /// to resume a multi-page flex split, or <see langword="null"/>
    /// when this is a fresh layout (= first page).</summary>
    private readonly FlexContinuation? _incomingFlexContinuation;

    /// <summary>Per cycle 1 — set the flex container's content-box
    /// geometry in the outer fragmentainer's coordinate space. The
    /// dispatching <see cref="BlockLayouter"/> has already emitted the
    /// wrapper fragment; this method tells the layouter WHERE to anchor
    /// the per-item content fragments inside that wrapper's content
    /// area.
    ///
    /// <para>MUST be called before <see cref="AttemptLayout"/>;
    /// otherwise AttemptLayout throws. Mirrors
    /// <see cref="MulticolLayouter.ConfigureEmission"/>.</para></summary>
    /// <param name="contentInlineOffset">Inline-axis offset (CSS px
    /// from the fragmentainer's content-area origin) of the container's
    /// content-box inline-start edge.</param>
    /// <param name="contentBlockOffset">Block-axis offset of the
    /// container's content-box block-start edge.</param>
    /// <param name="contentInlineSize">Container's content-box inline
    /// extent.</param>
    /// <param name="contentBlockSize">Container's content-box block
    /// extent.</param>
    /// <param name="allowPagination">Per Phase 3 Task 16 cycle 1 —
    /// when <see langword="true"/>, the layouter splits at line
    /// boundaries when cumulative cross-extent exceeds the
    /// container's cross size + emits a <see cref="FlexContinuation"/>
    /// for the next page. When <see langword="false"/> (default),
    /// lines overflow freely (= the L1-L17 atomic behavior). The
    /// BlockLayouter dispatch will pass <see langword="true"/> in
    /// sub-cycle 2 when wiring the multi-page integration.</param>
    /// <param name="pageBlockBudget">Flex-column pagination — the
    /// page-remaining block extent at which to CUT a
    /// <c>flex-direction: column</c> + <c>nowrap</c> container between
    /// items. SEPARATE from <paramref name="contentBlockSize"/> because
    /// the column main-size (= <paramref name="contentBlockSize"/>) drives
    /// flex-grow/shrink resolution + must stay the NATURAL content extent
    /// (clamping it to the page would make items SHRINK to fit rather than
    /// paginate). Mirrors <c>DispatchGridInner</c>'s grid page-budget
    /// dual-input. <see langword="null"/> (the default + every row-wrap /
    /// non-paginating caller) falls back to <paramref name="contentBlockSize"/>
    /// as the budget — preserving the line-split path byte-for-byte.</param>
    public void ConfigureEmission(
        double contentInlineOffset,
        double contentBlockOffset,
        double contentInlineSize,
        double contentBlockSize,
        bool allowPagination = false,
        double? pageBlockBudget = null)
    {
        if (!double.IsFinite(contentInlineSize) || contentInlineSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentInlineSize),
                $"contentInlineSize must be finite + positive; got {contentInlineSize}");
        }
        if (!double.IsFinite(contentBlockSize) || contentBlockSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentBlockSize),
                $"contentBlockSize must be finite + positive; got {contentBlockSize}");
        }
        if (pageBlockBudget is { } budget && (!double.IsFinite(budget) || budget <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(pageBlockBudget),
                $"pageBlockBudget, when supplied, must be finite + positive; got {budget}");
        }
        _contentInlineOffset = contentInlineOffset;
        _contentBlockOffset = contentBlockOffset;
        _contentInlineSize = contentInlineSize;
        _contentBlockSize = contentBlockSize;
        _allowPagination = allowPagination;
        _pageBlockBudget = pageBlockBudget;
        _emissionConfigured = true;
    }

    /// <summary>Per Phase 3 Task 16 cycle 1 → cycle 4b — when
    /// <see langword="true"/>, the layouter splits a multi-line flex
    /// container at line boundaries when lines exceed the container's
    /// cross extent + emits a <see cref="FlexContinuation"/> for the
    /// next page. When <see langword="false"/> (the cycle-0 default),
    /// lines overflow the container freely (= the existing L1-L17
    /// behavior); this preserves the L7 + L8 hardening overflow tests
    /// that deliberately exercise the "lines exceed container cross
    /// extent" path. Cycle 4b's paginatable-flex extent clamp in
    /// BlockLayouter flips this flag ON via
    /// <see cref="ConfigureEmission"/> when the container is eligible
    /// per <see cref="IsPaginatablePerStyle"/> AND its grown natural
    /// extent overflows the remaining fragmentainer space.</summary>
    private bool _allowPagination;

    /// <summary>Flex-column pagination — the page-remaining block extent at
    /// which to cut a column container between items, kept SEPARATE from
    /// <see cref="_contentBlockSize"/> (= the column main-size that drives
    /// flex resolution). <see langword="null"/> ⇒ use
    /// <see cref="_contentBlockSize"/> as the budget. See
    /// <see cref="ConfigureEmission"/>'s <c>pageBlockBudget</c> param.</summary>
    private double? _pageBlockBudget;

    /// <summary>PR-#180 review P2 — the block-axis content extent ACTUALLY
    /// emitted on this fragment (0-based, content-box relative). For a
    /// COLUMN item-split this is the deepest re-anchored item bottom; for a
    /// ROW-wrap line-split it is the deepest line bottom. The dispatching
    /// <see cref="BlockLayouter"/> reads it (mirroring
    /// <c>GridLayouter.LastEmittedBlockExtent</c>) to RESIZE the wrapper
    /// fragment to the emitted content instead of the clamped page budget —
    /// works on BOTH PageComplete and AllDone (a final resume page whose
    /// remaining items occupy less than the budget carries no continuation
    /// to read <see cref="FlexContinuation.EmittedBlockExtent"/> from).</summary>
    public double LastEmittedBlockExtent { get; private set; }

    /// <summary>Per Phase 3 Task 16 cycle 4b post-PR-#83 review P3 #6
    /// — SHARED predicate identifying flex containers eligible for
    /// multi-page line splitting per their box / style. Both
    /// <see cref="FlexLayouter"/>'s internal
    /// <c>isRowNormalWrapPaginationSupported</c> gate (line ~560) and
    /// <c>BlockLayouter.IsPaginatableFlex</c> delegate here to keep
    /// the two predicates in lockstep. If they drift the dispatch
    /// could flip <c>allowPagination: true</c> for an ineligible
    /// container + the FlexLayouter would treat it as
    /// <c>false</c> = silently dropped continuation = lost content.
    ///
    /// <para><b>Eligibility</b> per CSS Flexbox L1 §10 (Fragmenting
    /// Flex Layout). TWO axis-specific paths qualify:</para>
    /// <list type="bullet">
    ///   <item><b>ROW + wrap + NOT wrap-reverse → LINE split.</b> The
    ///   cross axis is the block axis, so wrapped lines stack along a
    ///   fragment boundary + split between LINES.</item>
    ///   <item><b>ROW + nowrap → intra-item CONTENT split.</b> The single
    ///   line's items share the cross (block) axis; a line whose items'
    ///   CONTENT is taller than the page splits every item's content at a
    ///   SHARED cross cut, so all items continue at the same cross position
    ///   (the page top) on the next page (<see cref="FlexContinuation.ConsumedCrossExtent"/>,
    ///   child-boundary granularity). wrap-reverse derives its cross-axis
    ///   SWAP origin from the UNFRAGMENTED size, so partial content would
    ///   land at the wrong offset (deferred).</item>
    ///   <item><b>COLUMN + nowrap (non-reverse) → ITEM split</b>
    ///   (non-block-pagination arc). The MAIN axis IS the block axis, so
    ///   the single line's items stack along a fragment boundary + split
    ///   between ITEMS (see the column item-split in
    ///   <see cref="AttemptLayout"/> + <see cref="FlexContinuation.ItemIndex"/>).
    ///   column + wrap stacks lines on the inline (cross) axis (not a
    ///   fragment boundary; an auto-height column can't wrap anyway);
    ///   column-reverse packs from the unfragmented main-END (per-fragment
    ///   re-anchoring deferred, mirroring row wrap-reverse).</item>
    /// </list>
    /// </summary>
    public static bool IsPaginatablePerStyle(Box box)
    {
        if (box.Kind is not (BoxKind.FlexContainer or BoxKind.InlineFlexContainer))
        {
            return false;
        }
        var direction = box.Style.ReadFlexDirection();
        var wrap = box.Style.ReadFlexWrap();
        if (direction.IsFlexColumnDirection())
        {
            // Non-block-pagination arc — flex-column main-axis item split.
            // For `flex-direction: column` the MAIN axis IS the block axis (=
            // a fragment boundary), so a single (nowrap) line of items stacked
            // down the block axis splits cleanly BETWEEN items across pages
            // (CSS Flexbox L1 §10). Excluded:
            //   * column + wrap — items wrap into multiple columns stacked on
            //     the INLINE (cross) axis, which is not a fragment boundary
            //     (an auto-height column can't wrap anyway: no main constraint).
            //   * column-reverse — NOW PAGINATES (backlog #4, first cut): the
            //     emission REVERSES the item order + emits it FORWARD per page
            //     (the per-fragment reverse-origin re-derivation), so a single
            //     (nowrap) column-reverse line splits between items across pages
            //     in VISUAL (reverse-DOM) order, like column-forward. The
            //     NON-paginating column-reverse keeps its bottom-packed flip.
            return !wrap.IsFlexWrapping();
        }
        // Row direction (= cross axis is the block axis). TWO paths qualify:
        //   * wrap (non-reverse) → LINE split (lines stack down the block axis).
        //   * nowrap → intra-item CONTENT split — the single line's items share
        //     the cross/block axis, so a line taller than the page splits every
        //     item's CONTENT at a SHARED cross cut (all items continue at the
        //     same cross position on the next page; child-boundary granularity).
        // wrap-reverse derives its cross-axis SWAP origin from the UNFRAGMENTED
        // size, so partial content would land at the wrong offset — deferred.
        if (wrap == FlexWrapValue.WrapReverse)
        {
            return false;
        }
        return true;
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
        _measurePurpose = layout.MeasurePurpose;

        if (!_emissionConfigured)
        {
            throw new InvalidOperationException(
                "FlexLayouter.AttemptLayout was called before "
                + "ConfigureEmission. The dispatching BlockLayouter "
                + "must supply the wrapper's content-area geometry so "
                + "the layouter knows where to anchor the items.");
        }

        // Per Phase 3 Task 15 L4 + L5 — resolve flex-direction. L1-L3
        // default is `row` (main = inline axis); L4 adds `column` (main
        // = block axis); L5 adds `row-reverse` + `column-reverse` which
        // per CSS Flexbox L1 §5.1 swap main-start and main-end along
        // the same row / column axis. The reversal is applied as a
        // single offset-flip transform at the per-item emission site
        // (see `isReverse` branch below) — the per-item math (cross-
        // axis alignment, stretch, justify-content start-offset +
        // between-spacing) is direction-agnostic and operates on the
        // natural (= non-reversed) cursor; only the FINAL main-axis
        // offset assigned to each fragment flips for reverse directions.
        //
        // Per CSS Flexbox §3.1 the physical axis mapping depends on the cascaded
        // `direction` + `writing-mode`. ROW RTL is now ACTIVE (PR 2 task 6): a `row`
        // (inline-main-axis) container under `direction: rtl` flips the physical main
        // axis right-to-left — the XOR into `isReverse` below makes it equivalent to LTR
        // `row-reverse`. STILL deferred (tracked in `docs/deferrals.md#flex-layouter-features`):
        // the COLUMN cross-axis under RTL (a column's cross axis is the inline axis), and
        // all VERTICAL writing modes (`writing-mode` is not yet a registered property).
        var flexDirection = _rootBox.Style.ReadFlexDirection();
        var isColumn = flexDirection.IsFlexColumnDirection();
        var isReverse = flexDirection.IsFlexReverseDirection();
        // Direction pipeline (PR 2 task 6) — for a ROW (inline-main-axis) container, `direction: rtl`
        // reverses the physical main-axis direction (CSS Flexbox §5.1: row main-start = inline-start =
        // RIGHT under RTL). So `row` under RTL lays out right-to-left — physically identical to
        // `row-reverse` under LTR — and `row-reverse` under RTL flips back to left-to-right. RTL therefore
        // XORs the reverse flag, which (re)routes the main-axis emission AND the flex-start/flex-end
        // justify-content mapping through the SAME `isReverse` path. Column directions (block main axis)
        // are unaffected by `direction` on the MAIN axis; the column CROSS-axis (inline) under RTL IS
        // handled below via `isColumnRtl` / `isCrossAxisReversed` (PR #215 review).
        var isRtl = _rootBox.Style.IsRtl();
        if (!isColumn && isRtl)
            isReverse = !isReverse;
        // flex-layouter-features — a COLUMN flex container's cross axis IS the inline axis, so
        // `direction: rtl` permutes its cross-start / cross-end (inline-start = RIGHT under RTL),
        // exactly like `wrap-reverse` permutes the cross axis per CSS Flexbox L1 §6.3. The two flips
        // combine (XOR) below into `isCrossAxisReversed`, so a column-rtl container anchors
        // align-items / align-content at the right edge and a wrap-reverse + column-rtl cancels back.
        // ROW RTL is handled by the main-axis `isReverse` above (a row's cross axis is the block axis,
        // unaffected by `direction`), so this is column-only.
        var isColumnRtl = isColumn && isRtl;
        var (mainSizeProperty, crossSizeProperty) = GetAxisProperties(flexDirection);

        // Per Phase 3 Task 15 L6 — read flex-wrap. When the value is
        // `wrap` OR `wrap-reverse` the layouter switches into multi-
        // line mode: items are greedy-packed onto lines along the
        // main axis, and lines stack along the cross axis. For wrap
        // the stack origin is the cross-start edge; for wrap-reverse
        // (per Phase 3 Task 15 L11 + post-PR-#71 F#1) the cross-axis
        // is SWAPPED — the stack origin becomes the physical cross-
        // end, and per-line align-items / align-self FlexStart/FlexEnd
        // anchors swap too. The L1-L5 single-line behavior is preserved
        // verbatim for `nowrap` (the default) by PackLines emitting
        // exactly one line covering all items.
        var flexWrap = _rootBox.Style.ReadFlexWrap();
        var isWrapping = flexWrap.IsFlexWrapping();

        // Per Phase 3 Task 15 L11 — `flex-wrap: wrap-reverse` is now
        // implemented properly per CSS Flexbox L1 §6.3 ("Behaves the
        // same as wrap but cross-start and cross-end are permuted").
        // The cross-axis line-stacking reversal happens at line
        // 670-ish via `lines.Reverse()` after PackLines completes:
        // line 0 (DOM order's first line) ends up at the NEW cross-
        // start (= the physical cross-END for the L1 LTR + horizontal-
        // tb default), so the visual result reflects the author's
        // request.
        //
        // The L6 hardening F#4 diagnostic
        // (LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001) is no longer
        // emitted because the approximation is gone — wrap-reverse
        // now produces the spec-correct stacking. The PaginateDiagnostic
        // code remains registered for backward compat / cross-reference
        // (older versions could have emitted it).
        var isWrapReverse = flexWrap == FlexWrapValue.WrapReverse;
        // The cross axis is reversed when `wrap-reverse` permutes it OR a column container is RTL (see
        // `isColumnRtl` above) — combined so the two cancel. PR #215 review [P1]: ONE flag drives BOTH
        // the per-LINE stacking (`CrossAxisFlow.IsReversed`) AND the per-ITEM align-items / align-self
        // anchor (`ComputeAlignItemsPlacement`), so line distribution and item placement agree. (A
        // `self-start`/`self-end` item overrides the container component with its OWN direction — see
        // the per-item callsite.)
        var isCrossAxisReversed = isWrapReverse ^ isColumnRtl;

        // Resolve the container's main-axis + cross-axis content extents
        // + offsets. For row direction the main axis is the inline axis
        // (= _contentInlineSize / _contentInlineOffset); the cross axis
        // is the block axis. For column direction the assignment swaps.
        var containerMainSize = isColumn ? _contentBlockSize : _contentInlineSize;
        var contentMainOffset = isColumn ? _contentBlockOffset : _contentInlineOffset;
        var contentCrossOffset = isColumn ? _contentInlineOffset : _contentBlockOffset;

        // CSS Box Alignment L3 §8 — gap gutters. The MAIN-axis gutter sits between
        // items in a line (column-gap for row direction, row-gap for column); the
        // CROSS-axis gutter sits between wrapped lines (the swapped pair). `normal`
        // / unset → 0 for flex (ReadFlexGridGapOrZero).
        // §8.3 — a `%` gutter resolves against the matching content dimension:
        // column-gap → the inline content size, row-gap → the block content size
        // (independent of flex-direction). A `%` row-gap, though, only resolves when
        // the block size is DEFINITE; for an auto / content-height container the block
        // axis is indefinite, so the `%` resolves to 0 (the indefinite-reference rule,
        // matching browsers AND the BlockLayouter pre-measure, which sums the block
        // extent with gap 0 because that extent is still being derived). Without this
        // gate the emission would space items by a gap the pre-measured wrapper never
        // grew for → overflow + following-sibling overlap. column-gap's inline base is
        // always definite for a block-level flex container, so it needs no gate.
        var inlineGapBase = _contentInlineSize;
        var blockGapBase = _rootBox.IsHeightAuto() ? double.NaN : _contentBlockSize;
        // Corpus-fidelity — the container's DEFINITE cross-axis content extent for resolving a flex
        // item's PERCENTAGE cross size (e.g. `height: 100%` bars in a definite-height row-flex, 10's
        // barcodes). Row cross = block: definite only when the container height isn't auto (the same
        // indefinite-reference rule blockGapBase uses); column cross = inline: always definite. NaN →
        // a `%` cross size stays 0 (auto against an indefinite parent, so auto-height flex stays
        // byte-identical).
        var containerDefiniteCrossSize = isColumn ? _contentInlineSize : blockGapBase;
        // Corpus-fidelity (10-event-ticket runaway) — the DEFINITE main-axis size for resolving a flex
        // item's PERCENTAGE main size (e.g. `height: 100%` in a COLUMN flex). Column main = block: definite
        // only when the container height isn't auto (same indefinite-reference rule as blockGapBase); a
        // column flex with AUTO height has an INDEFINITE main size, so a `%` main size must compute to auto
        // (0 / content) per CSS 2.2 §10.5 — NOT resolve against `containerMainSize`, which during a nested
        // MEASURE is the ~1,000,000px unbounded budget (→ a 1M-tall item → thousands of blank pages). Row
        // main = inline: always definite for a block-level flex container.
        var containerDefiniteMainSize = isColumn ? blockGapBase : _contentInlineSize;
        var mainGap = _rootBox.Style.ReadFlexGridGapOrZero(
            isColumn ? PropertyId.RowGap : PropertyId.ColumnGap,
            isColumn ? blockGapBase : inlineGapBase);
        var crossGap = _rootBox.Style.ReadFlexGridGapOrZero(
            isColumn ? PropertyId.ColumnGap : PropertyId.RowGap,
            isColumn ? inlineGapBase : blockGapBase);

        // Per Phase 3 Task 15 L10 — compute the effective flex order
        // ONCE per AttemptLayout entry; both PackLines (line packing)
        // and the per-line emission below walk this list so item
        // visual order follows the `order` property per CSS Flexbox
        // L1 §5.4. The helper short-circuits to DOM order when no
        // item declares a non-zero `order`, preserving the L1-L9
        // behavior verbatim. Non-block-level children (whitespace
        // TextRuns from BoxBuilder) are already filtered out by the
        // helper, so the downstream loops can drop their IsBlockLevel
        // skip guards.
        _sortedFlexChildIndices = _rootBox.GetFlexChildrenInOrderSequence(cancellationToken);

        // Per Phase 3 Task 15 L6 — pack items into lines. For nowrap,
        // PackLines returns a single FlexLine containing every block-
        // level child (matches the L1-L5 single-line algorithm byte-
        // for-byte: same total main-size + same max cross-size). For
        // wrap, the greedy algorithm starts a new line when adding the
        // next item would exceed containerMainSize (the first item on a
        // line always lands even if it itself overflows). See
        // PackLines's XML doc for the spec citation.
        //
        // Per Phase 3 Task 15 L10 — PackLines walks the sorted
        // sequence; FlexLine.FirstItemIndex is now a POSITION in
        // <c>_sortedFlexChildIndices</c>, not a DOM-children index.
        // The emission loop below + the ResolveFlexibleMainSizes pass
        // dereference DOM-children indices via
        // <c>_sortedFlexChildIndices[firstItemIndex + i]</c>.
        // Flex intrinsic-basis cycle — for a single-line (nowrap) ROW container, measure
        // up front the max-content / min-content inline base size of each item with an
        // explicit intrinsic `flex-basis`. The same map threads through line packing +
        // the §9.7 resolution below (so totalMain, free space, grow/shrink + justify all
        // agree). Built only for nowrap rows; the BlockLayouter row-flex height
        // pre-measure builds an identical map via the same shared helper so the wrapper
        // height stays in lockstep. Null for column / wrap / no-shaper / no-intrinsic-item
        // → the declared-size path (byte-identical to the pre-cycle behavior).
        IReadOnlyDictionary<Box, double>? intrinsicBaseSizes = null;
        if (!isColumn && !isWrapping)
        {
            var blockLevelItems = new List<Box>(_sortedFlexChildIndices.Count);
            foreach (var idx in _sortedFlexChildIndices)
                blockLevelItems.Add(_rootBox.Children[idx]);
            intrinsicBaseSizes = BuildRowIntrinsicMainBaseSizes(
                blockLevelItems, _shaperResolver, cancellationToken);
        }

        var lines = PackLines(
            _rootBox, _sortedFlexChildIndices, flexDirection,
            containerMainSize, isWrapping, cancellationToken, mainGap,
            intrinsicBaseSizes, containerDefiniteCrossSize);

        // Per Phase 3 Task 16 cycle 1 (Hello World) — multi-page flex
        // split fragment range determination per CSS Flexbox L1 §10
        // (Fragmenting Flex Layout). Sample algorithm: "lay out as
        // many row flex lines as possible BEFORE finishing Cross
        // Axis Alignment for each fragment". We compute the fragment
        // range (= which lines belong to this page) HERE — BEFORE
        // the align-content offsets are computed below at line ~632
        // — using a RAW cursor that only sums `line.LineCrossSize`
        // (= ignoring align-content's lineStartOffset +
        // lineBetweenSpacing, which the post-PR-#78 P1 review
        // correctly flagged as wrong: align-content's
        // center/flex-end/space-* would mis-compute which lines fit
        // a fragment).
        //
        // Cycle 1 support boundary (per P2 review): pagination only
        // for ROW direction + WRAP mode + NOT wrap-reverse. Other
        // modes fall through to atomic behavior:
        //   - column direction: lines stack on inline axis (doesn't
        //     paginate naturally without a writing-mode rotation).
        //   - nowrap: only one line; per-item splitting is a later
        //     sub-cycle.
        //   - wrap-reverse: the cross-axis SWAP uses the
        //     unfragmented containerCrossSize; emitting partial
        //     content on a resumed page would place lines at the
        //     wrong physical offset. Sub-cycle 2 will recompute
        //     CrossAxisFlow against the per-fragment cross extent.
        // Per Phase 3 Task 16 cycle 4b post-PR-#83 review P3 #6 —
        // delegate the style-side gate to <see cref="IsPaginatablePerStyle"/>
        // so BlockLayouter's dispatch-side predicate +
        // FlexLayouter's emission-side gate stay in lockstep. The
        // call-site decision (= <c>_allowPagination</c>) remains
        // distinct because BlockLayouter only flips it ON when the
        // container's grown extent actually overflows the remaining
        // page space; eligible containers that fit get
        // <c>_allowPagination: false</c> (= atomic emission, same as
        // cycle-pre-4b behavior).
        // Pagination eligibility (style-gated) splits by AXIS:
        //   * ROW + wrap → line-split (cumulative cross-extent overflow),
        //     handled here via fragmentEndIndex slicing.
        //   * COLUMN + nowrap → item-split (main = block axis), handled in
        //     the emission loop below (per-item cut + resume-shift). The two
        //     are mutually exclusive (IsPaginatablePerStyle accepts one form
        //     per direction), so a single _allowPagination flag drives both.
        var paginationEligible = _allowPagination && IsPaginatablePerStyle(_rootBox);
        var isRowPaginationEligible = paginationEligible && !isColumn;
        // Row + wrap → LINE split (cumulative cross-extent overflow, fragmentEndIndex
        // slicing below); row + nowrap → intra-item CONTENT split (the single line's
        // items share the cross/block axis; a line taller than the page slices every
        // item's buffered content at a SHARED cross cut — see the emission loop +
        // FlexContinuation.ConsumedCrossExtent). Mutually exclusive.
        var isRowNormalWrapPaginationSupported = isRowPaginationEligible && isWrapping;
        var isRowNowrapContentPagination = isRowPaginationEligible && !isWrapping;
        var isColumnItemPagination = paginationEligible && isColumn;

        // Backlog #4 (column-reverse pagination, first cut) — a PAGINATING
        // column-REVERSE container paginates in VISUAL (reverse-DOM) order: we
        // REVERSE the item sequence here + emit it FORWARD (top-to-bottom)
        // below, reusing the entire forward column item-split (re-anchor + cut
        // by page budget) instead of the bottom-packed reverse FLIP. This is
        // the paginating case ONLY — non-paginating column-reverse keeps the
        // flip, byte-identical. Reversing the (fresh, per-attempt) order list is
        // order-SAFE: the single column-nowrap line covers every item, and the
        // flex / content-measure results are indexed by DOM index (not sorted
        // position), so only the EMISSION order changes.
        var columnReverseEmitForward = isColumnItemPagination && isReverse;
        if (columnReverseEmitForward)
        {
            _sortedFlexChildIndices.Reverse();
        }

        // Per Phase 3 Task 16 post-PR-#78 P1 #3 — validate the
        // resume index against the packed line count. Out-of-range
        // values silently drop content or produce nonsensical resume
        // behavior; surface them loudly with
        // <see cref="ArgumentOutOfRangeException"/>. The boundary
        // value `LineIndex == lines.Count` is allowed (= the
        // "everything was emitted on the prior page" case which
        // resumes as a no-op AllDone).
        var resumeLineIndex = _incomingFlexContinuation?.LineIndex ?? 0;
        if (resumeLineIndex < 0 || resumeLineIndex > lines.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(_incomingFlexContinuation),
                $"FlexContinuation.LineIndex must be in [0, {lines.Count}]; "
                + $"got {resumeLineIndex}. A misrouted continuation has "
                + "corrupted the resume index — surface immediately rather "
                + "than silently dropping the remaining flex content.");
        }

        // Flex-column item-split resume index + page budget (see the emission
        // loop). resumeItemIndex is the sorted-position to resume at; the
        // budget is the page-remaining block extent (kept distinct from the
        // column main-size so flex resolution stays natural). Symmetric
        // validation with the line index — the boundary `ItemIndex ==
        // itemCount` is allowed (= every item committed on a prior page → this
        // resume emits nothing → AllDone).
        var resumeItemIndex = isColumnItemPagination
            ? (_incomingFlexContinuation?.ItemIndex ?? 0)
            : 0;
        var columnBudget = _pageBlockBudget ?? _contentBlockSize;
        if (isColumnItemPagination
            && (resumeItemIndex < 0 || resumeItemIndex > _sortedFlexChildIndices.Count))
        {
            throw new ArgumentOutOfRangeException(
                nameof(_incomingFlexContinuation),
                $"FlexContinuation.ItemIndex must be in [0, {_sortedFlexChildIndices.Count}]; "
                + $"got {resumeItemIndex}. A misrouted continuation has "
                + "corrupted the column resume index — surface immediately "
                + "rather than silently dropping the remaining flex items.");
        }

        // Row-nowrap intra-item content-split resume cut + page budget. The cut
        // (ConsumedCrossExtent) accumulates the cross extent emitted on prior
        // pages (content-cross coords, 0 = content-box cross-start); the budget
        // is the dual-input page-remaining block extent (like columnBudget). The
        // window for THIS page is [rowNowrapResumeCut, rowNowrapResumeCut +
        // rowNowrapBudget). The actual slicing happens per item in the emission
        // loop (FlushRangeTo); these locals drive the window + the continuation.
        var rowNowrapResumeCut = isRowNowrapContentPagination
            ? (_incomingFlexContinuation?.ConsumedCrossExtent ?? 0.0)
            : 0.0;
        var rowNowrapBudget = _pageBlockBudget ?? _contentBlockSize;
        // Accumulated across the emission loop: the deepest emitted content
        // bottom (relative to the page window top) + whether any item still has
        // content beyond this page's window (→ emit a continuation).
        var rowNowrapEmittedExtent = 0.0;
        var rowNowrapAnyRemaining = false;

        // Determine the fragment's end-of-range (exclusive). Default:
        // emit every remaining line. Pagination: emit lines up to
        // (but not including) the first one that overflows the
        // available block extent. Always emit at least the FIRST
        // remaining line per CSS Fragmentation L3 §4.4 ("at least
        // one line must commit per page") — prevents infinite defer
        // when a single line is taller than the fragmentainer.
        var fragmentEndIndex = lines.Count;
        if (isRowNormalWrapPaginationSupported && resumeLineIndex < lines.Count)
        {
            var rawCursor = 0.0;
            for (var i = resumeLineIndex; i < lines.Count; i++)
            {
                var isFirstOnPage = i == resumeLineIndex;
                // §8 — a cross-axis gutter precedes every NON-first line on the page;
                // include it in the fit cursor so the slice can't approve more lines
                // than emission (which advances by LineCrossSize + crossGap, ~L1591)
                // will actually fit on the page (PR #204 review [P1]). The "first line
                // always commits" rule is preserved: both the gutter and the overflow
                // check are gated on !isFirstOnPage, so a lone over-tall first line
                // still lands rather than deferring forever.
                var lineAdvance = isFirstOnPage
                    ? lines[i].LineCrossSize
                    : crossGap + lines[i].LineCrossSize;
                if (!isFirstOnPage
                    && rawCursor + lineAdvance > _contentBlockSize)
                {
                    fragmentEndIndex = i;
                    break;
                }
                rawCursor += lineAdvance;
            }
        }

        // Slice `lines` to the fragment range. Pre-resume lines (=
        // emitted on a prior page) + post-split lines (= deferred to
        // the next page) are removed. The rest of AttemptLayout
        // (align-content offsets, line emission) operates on this
        // fragment's lines only — = the spec's "lay out as many
        // lines as possible, THEN apply Cross Axis Alignment per
        // fragment" model.
        var originalLineCount = lines.Count;
        if (resumeLineIndex > 0 || fragmentEndIndex < originalLineCount)
        {
            lines = lines.GetRange(
                resumeLineIndex, fragmentEndIndex - resumeLineIndex);
        }
        var outgoingContinuationLineIndex = fragmentEndIndex < originalLineCount
            ? fragmentEndIndex
            : -1;

        // Per Phase 3 Task 15 L11 post-PR-#71 hardening F#1 — the
        // cross-axis SWAP per CSS Flexbox L1 §6.3 ("Behaves the same
        // as wrap but the cross-start and cross-end directions are
        // swapped") is applied at line-emission time below, NOT by
        // mutating the `lines` list. Items + lines iterate in DOM
        // order; the emission loop computes each line's PHYSICAL
        // cross-offset via a swap formula when `isWrapReverse`:
        //   physical = contentCrossOffset + containerCrossSize
        //            - swappedCursor - line.LineCrossSize
        // Where `swappedCursor` is the same align-content-derived
        // cursor used for wrap (= cursor in the swapped-axis space
        // where 0 = swapped cross-start = physical cross-end).
        //
        // The L11 Hello World shipped only the iteration-order
        // reversal (= `lines.Reverse()`) which produced reversed
        // DOM-to-physical mapping for the LINE STACK but did not
        // move the stack to the swapped cross-start origin. The
        // post-PR-#71 F#1 hardening introduces the origin swap;
        // the per-line emission cursor walks DOM order without
        // mutating the lines list.
        //
        // Per Phase 3 Task 15 L14 — the swap logic that was previously
        // inline at the line-cursor site lives in <see cref="CrossAxisFlow"/>
        // now (= one record carrying isReversed + contentCrossOffset
        // + containerCrossSize; the emission loop calls
        // <see cref="CrossAxisFlow.PhysicalLineOffset"/> to convert
        // the swapped-axis cursor to the line's physical TOP edge).
        // Two other swap callsites remain pinned to the loop-local
        // state for cycle 1: (a) the <c>isCrossAxisReversed</c>
        // parameter on ComputeAlignItemsPlacement which threads the
        // FlexStart/FlexEnd anchor swap into the per-line item
        // placement helper — they're per-item rather than per-line
        // and live inside the inner loop body, so passing
        // <c>isWrapReverse</c> directly stays simpler than threading
        // the whole flow record; (b) the swappedAxisCursor cursor
        // walks the SWAPPED axis (always 0 → end regardless of swap)
        // so its accumulation site doesn't need swap-aware math. Both
        // can fold into the flow helper if a later sub-cycle adds
        // writing-mode-aware logical mapping that needs to swap them
        // too.

        var resolvedJC = _rootBox.Style.ReadJustifyContent();
        var alignItems = _rootBox.Style.ReadAlignItems();

        // Per Phase 3 Task 15 L6 — for align-items resolution against
        // each line's cross-extent we need the CONTAINER's cross extent
        // only in the stretch branch when there's a single line + no
        // explicit cross-size declaration (then the container's auto
        // cross-size IS the single line's cross-extent + matches the
        // L1-L5 fallback exactly). When wrapping, each line gets its
        // OWN cross-extent (= max(item cross-size on that line)) which
        // the align-items algorithm targets per CSS Flexbox L1 §6.3
        // ("each flex line is treated as the alignment container for
        // its items along the cross axis"). The container's full cross
        // extent (= sum of line cross-extents for wrapping; explicit
        // declared cross-size when set) drives the cross-axis offset of
        // line 0, not the per-line align-items math.
        //
        // For nowrap the L1-L5 derivation still applies. Note: the
        // single line's per-line cross-extent (= max item cross-size)
        // equals the container's cross-extent only when the container
        // is auto-sized (height/width: auto). With an explicit cross-
        // size (e.g., `height: 200px` in row direction), the line's
        // cross-extent can be SMALLER than the container's, and items
        // align against the container's cross-extent (not the line's).
        // We still compute containerCrossSize uniformly for the
        // wrapper's own sizing signal; the per-line align-items math
        // below uses each line's own cross-extent (which for nowrap
        // is the single line's max-item-cross — possibly smaller than
        // containerCrossSize when explicit).
        var containerCrossSize = ResolveContainerCrossSize(
            isColumn, lines, cancellationToken);

        // Per Phase 3 Task 15 L7 — resolve align-content + compute the
        // cross-axis line distribution per CSS Flexbox L1 §8.4 + CSS Box
        // Alignment L3 §6. Per Phase 3 Task 15 L7 post-PR-#67 hardening
        // F#1: the gate for "single-line container" is `flex-wrap:
        // nowrap` (NOT lineCount <= 1) — a wrapping container that
        // produces one line is still a multi-line container per §9.4
        // and align-content still applies to it (the default normal →
        // stretch still stretches that single line). Per F#2: overflow
        // handling now mirrors justify-content's per-family + per-mode
        // semantics (CSS Box Alignment L3 §5.3).
        //
        // freeCrossSpace = container's cross extent - sum of each line's
        // natural cross extent. Positive = lines fit with space left over
        // (the L7 stretch / distribution branch). Zero = lines exactly
        // fill the container (L1-L6 default + auto-cross-size case).
        // Negative = lines overflow (now per-mode + per-family handling).
        var resolvedAC = _rootBox.Style.ReadAlignContent();
        var sumLineCrossExtents = 0.0;
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sumLineCrossExtents += line.LineCrossSize;
        }
        // §8 — cross-axis gaps between wrapped lines consume free space BEFORE
        // align-content distributes the remainder. N lines → N-1 gutters.
        var crossGapTotal = System.Math.Max(0, lines.Count - 1) * crossGap;
        var freeCrossSpace = containerCrossSize - sumLineCrossExtents - crossGapTotal;

        var (lineStartOffset, lineBetweenSpacing, lineStretchAddend) =
            ComputeAlignContentOffsets(resolvedAC, freeCrossSpace, lines.Count, isWrapping, isWrapReverse);

        // Per Phase 3 Task 15 L7 — apply the stretch addend BEFORE the
        // emission loop. When align-content: stretch (the default per
        // CSS Flexbox §8.4) AND multi-line AND freeCrossSpace > 0, grow
        // each line's cross extent by lineStretchAddend so the lines
        // collectively fill the container. Items within a stretched line
        // use the LARGER cross extent for their align-items math (the
        // emission loop reads line.LineCrossSize, so updating it in
        // place propagates the new extent to the align-items helper).
        if (lineStretchAddend > 0)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lines[i] = lines[i] with
                {
                    LineCrossSize = lines[i].LineCrossSize + lineStretchAddend,
                };
            }
        }

        // Per Phase 3 Task 15 L8 — resolve flexible lengths per CSS
        // Flexbox L1 §7 + §9.7 (the flexibility algorithm). For each
        // line: compute each item's hypothetical main-size from its
        // flex-basis (Auto / Content delegate to declared main-size,
        // LengthPx uses the explicit pixel value, Percentage resolves
        // against containerMainSize); then distribute freeMainSpace
        // among items by flex-grow factors (positive freeMainSpace) or
        // by flex-shrink × flex-basis factors (negative freeMainSpace).
        // The resolved sizes feed the emission loop's main-axis
        // placement.
        //
        // Algorithm sketch (§9.7):
        //   1. resolved[i] = hypothetical[i] initially.
        //   2. If freeMainSpace > 0 AND sumFlexGrow > 0:
        //        each item grows by (item.flexGrow / sumFlexGrow) * freeSpace.
        //      Else: items stay at hypothetical (free space goes to
        //      justify-content's between-spacing).
        //   3. If freeMainSpace < 0 AND sumScaledShrinks > 0:
        //        scaledShrink_i = item.flexShrink * hypothetical_i
        //        each item shrinks by
        //          (scaledShrink_i / sumScaledShrinks) * |freeSpace|.
        //      Else: items stay at hypothetical (overflow).
        //   4. (L9+ scope) clamp resolved[i] to [min-main-size,
        //      max-main-size] and recompute.
        //
        // The resolved sizes are stored in a parallel array indexed by
        // child index in _rootBox.Children. Items NOT on a flex line
        // (e.g., non-block-level filler children) keep entry 0 — they
        // get filtered out by IsBlockLevel in the emission loop anyway.
        // The justify-content freeSpace also needs to be recomputed
        // post-flexibility (= containerMainSize - sum(resolved on
        // line)) so the between-spacing matches the flexed widths;
        // ComputeJustifyContentOffsets now sees the post-flex line
        // main-size.
        var (minSizeProperty, maxSizeProperty) = GetMinMaxMainAxisProperties(flexDirection);
        var resolvedItemMainSizes = ResolveFlexibleMainSizes(
            lines, mainSizeProperty, minSizeProperty, maxSizeProperty,
            containerMainSize, containerDefiniteMainSize, mainGap, cancellationToken, intrinsicBaseSizes);

        // Non-block-pagination arc (flex item CONTENT layout) — lay out each
        // flex item's INNER content (text / nested blocks) via a nested
        // BlockLayouter into a per-item buffer, and for a COLUMN container
        // CONTENT-SIZE auto-height items from the measured block extent (the
        // common `<div>text</div>` case: an auto-height item has main-size 0
        // until content sizes it). Runs BEFORE the LineMainSize recompute below
        // so a content-sized item's grown main-size feeds justify-content's
        // free space + the column item-split page budget. The per-item buffers
        // are flushed at each COMMITTED item's final re-anchored content-box
        // origin in the emission loop. See <see cref="MeasureFlexItemContents"/>.
        // PR-#182 review P2 — the effective diagnostics sink for flex item
        // content. Diagnostics are BUFFERED per item during measurement +
        // flushed only when the item COMMITS on this page (a deferred item's
        // buffer is discarded + re-generated on its page), so a paginating
        // column doesn't duplicate per-item diagnostics across pages.
        var effectiveDiagnostics = layout.Diagnostics ?? _diagnostics;
        // Row-nowrap intra-item content split — the content is taller than the
        // page, so measuring it into a page-sized inner fragmentainer would CLIP
        // the part beyond the page (the nested LastResort pass clips past its
        // budget). Measure into the effectively-unbounded buffer so the WHOLE item
        // content is captured (an auto-height item's natural cross extent EQUALS its
        // content, so a budget of exactly the natural extent would clip the last line
        // at the float boundary) + can be sliced per page. Other cases keep the
        // page-sized budget (byte-identical). The budget is a practical cap, not truly
        // unbounded (see the const doc); a measured extent that REACHES it is surfaced
        // as LAYOUT-FLEX-ITEM-CONTENT-TRUNCATED-001 below so truncation isn't silent.
        var contentMeasureBudget = isRowNowrapContentPagination
            ? NestedContentMeasurer.EffectivelyUnboundedBlockBudgetPx
            : fragmentainer.BlockSize;
        var (itemContentBuffers, itemDiagnosticBuffers) = MeasureFlexItemContents(
            lines, resolvedItemMainSizes, flexDirection, isColumn,
            mainSizeProperty, crossSizeProperty, containerCrossSize,
            containerDefiniteCrossSize, containerDefiniteMainSize, isWrapping,
            fragmentainer, layout.WritingMode, layout.IsRtl,
            effectiveDiagnostics, contentMeasureBudget, cancellationToken);

        // Flex baseline-alignment cycle (CSS Flexbox L1 §8.3 + §8.5) — for a ROW
        // container (cross axis = block) precompute, per flex item, (a) the
        // alignment baseline = the item's first text baseline measured from its
        // border-box cross-start (synthesized from the cross-end edge when the item
        // has no line box) and (b) the cross size a baseline-aligned item uses
        // (content height for an auto-cross item, else the declared cross size).
        // The per-line MAX of (a) over baseline-aligned items anchors them so their
        // first baselines coincide. Computed only for row (COLUMN baseline falls back
        // to flex-start — the inline cross axis has no first baseline without vertical
        // text); the arrays are otherwise unused so non-baseline layouts stay
        // byte-identical.
        double[]? itemBaselineOffsets = null;
        double[]? itemBaselineCrossSizes = null;
        if (!isColumn)
        {
            (itemBaselineOffsets, itemBaselineCrossSizes) =
                ComputeRowBaselineData(itemContentBuffers, flexDirection, crossSizeProperty);
        }

        // Corpus-fidelity (03 itinerary footer overlap) — an AUTO-height ROW flex container's used cross
        // (block) size is its CONTENT cross extent (CSS Flexbox L1 §9.4: the sum/max of the flex lines'
        // used cross sizes). FlexLinePacker sizes each line from the items' DECLARED cross only (0 for an
        // auto-height item — it doesn't content-measure), so `containerCrossSize` / `maxEmittedCrossBottom`
        // come out 0 and the container reports a chrome-only block box even though its item content paints
        // tall. `itemBaselineCrossSizes` (already computed for every row flex, above) holds each item's
        // content-measured cross border box, so the max is the real content cross. Fold it into
        // `LastEmittedBlockExtent` (below) so the dispatching BlockLayouter resizes the wrapper + advances
        // the sibling cursor past the real content — otherwise a trailing sibling overlaps the flex content
        // (03 `.note` over the `.timeline`). Byte-identical when the content is no taller than the resolved
        // cross (max ≤ existing).
        // SCOPE (review) — NOWRAP single-line only: `max(item cross)` is the line's cross size for one
        // line. For `flex-wrap: wrap` the auto cross is the SUM of each packed line's max content cross +
        // row gaps, and the wrap cross pre-measure (FlexLinePacker.SumCrossExtent) still reads DECLARED
        // cross sizes — so a content-sized wrapped flex is a separate (still-open) case; not folded here to
        // avoid under/over-counting. An explicit height is the used block size, so gate that out too.
        var autoRowContentCross = 0.0;
        var isAutoHeightRow = !isColumn && !isWrapping
            && _rootBox.Style.Get(PropertyId.Height).Tag is ComputedSlotTag.Unset or ComputedSlotTag.Keyword;
        if (isAutoHeightRow && itemBaselineCrossSizes is not null)
        {
            foreach (var cs in itemBaselineCrossSizes)
                if (!double.IsNaN(cs) && cs > autoRowContentCross) autoRowContentCross = cs;
        }
        // RC-2b / RC-11 — for an AUTO-height NOWRAP row the resolved container cross (and thus the
        // single line's cross extent, `lineCrossExtent = containerCrossSize` below) came out 0 because
        // FlexLinePacker sizes each line from the items' DECLARED cross only (an auto item declares 0).
        // Fold in the content-measured max (`autoRowContentCross`) so a `stretch` item grows to the
        // real content cross and every item's emission line extent is non-zero — otherwise each item's
        // geometry fragment has BlockSize 0 and FragmentPainter culls its background/border/radius. Safe
        // to update here: the wrap-only align-content free-space (L1037) and the item-content measure
        // (L1130, which INTENTIONALLY measures at auto cross to PRODUCE these sizes) both already ran,
        // and the emission loop reads `containerCrossSize` further below. Byte-identical when the content
        // is no taller than the already-resolved cross (fold is a max).
        if (isAutoHeightRow && autoRowContentCross > containerCrossSize)
        {
            containerCrossSize = autoRowContentCross;
        }

        // Corpus-fidelity (06 travel-voucher "What's Included" overlap) — the WRAP companion to
        // the nowrap auto-height fold above, closing the "still-open case" flagged in the SCOPE note.
        // For a MULTI-LINE (`flex-wrap: wrap`) AUTO-height ROW container CSS Flexbox L1 §9.4 sizes
        // each flex line to the MAX used cross size of the items ON THAT LINE (step 8) and the
        // container's auto cross size to the SUM of the lines' cross sizes + row gaps (step 15).
        // FlexLinePacker sized every line from the items' DECLARED cross only (0 for an auto-height
        // item — it doesn't content-measure), so absent this fold EVERY line's `LineCrossSize` is 0:
        // the emission cursor never advances, so all lines stack at the same cross offset (items
        // overlap), and `maxEmittedCrossBottom` → `LastEmittedBlockExtent` come out 0 so the wrapper
        // reports a chrome-only box + the trailing sibling overlaps it. `itemBaselineCrossSizes`
        // (computed for every ROW flex above) holds each item's content-measured cross border box;
        // take the per-line max, grow each line, and re-derive the auto container cross from the line
        // sum + `crossGapTotal` (§9.4 step 15). Gated to auto-height, non-wrap-reverse rows: an
        // explicit container cross is the used size and feeds align-content distribution (a separate,
        // still-open path); wrap-reverse derives its cross-axis SWAP origin from the UNFRAGMENTED
        // extent (deferred, mirroring the emission-loop swap). Byte-identical when every line's
        // content is no taller than its declared cross (the per-line grow + the container fold are
        // both `max`), and the auto container's freeCrossSpace was 0 so align-content contributed no
        // offset/stretch to disturb.
        var isWrapAutoHeightRow = !isColumn && isWrapping && !isWrapReverse
            && _rootBox.Style.Get(PropertyId.Height).Tag is ComputedSlotTag.Unset or ComputedSlotTag.Keyword;
        if (isWrapAutoHeightRow && itemBaselineCrossSizes is not null)
        {
            var sumLineCrossFromContent = 0.0;
            for (var li = 0; li < lines.Count; li++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = lines[li];
                var contentLineCross = 0.0;
                var endPos = line.FirstItemIndex + line.ItemCount;
                for (var sp = line.FirstItemIndex; sp < endPos; sp++)
                {
                    var idx = _sortedFlexChildIndices[sp];
                    var cs = itemBaselineCrossSizes[idx];
                    if (!double.IsNaN(cs) && cs > contentLineCross) contentLineCross = cs;
                }
                if (contentLineCross > line.LineCrossSize)
                {
                    lines[li] = line with { LineCrossSize = contentLineCross };
                }
                sumLineCrossFromContent += lines[li].LineCrossSize;
            }
            var autoWrapCross = sumLineCrossFromContent + crossGapTotal;
            if (autoWrapCross > containerCrossSize) containerCrossSize = autoWrapCross;
        }

        // PR #189 review P2 — the row-nowrap content-measure budget is a PRACTICAL cap
        // (NestedContentMeasurer.EffectivelyUnboundedBlockBudgetPx), not truly unbounded:
        // an item whose measured content REACHES it was clipped by the single atomic pass.
        // Surface it (CLAUDE.md #7 — never drop content silently) so the (no-real-document)
        // truncation isn't silent. The page-by-page streaming measure is the documented
        // follow-up. Once per attempt suffices for a warning this rare.
        if (isRowNowrapContentPagination && effectiveDiagnostics is not null)
        {
            foreach (var b in itemContentBuffers)
            {
                if (b is not null
                    && b.ContentBlockExtent >= NestedContentMeasurer.EffectivelyUnboundedBlockBudgetPx)
                {
                    effectiveDiagnostics.Emit(new PaginateDiagnostic(
                        Code: PaginateDiagnosticCodes.LayoutFlexItemContentTruncated001,
                        Message: "A row-nowrap flex item's measured content block extent reached the "
                            + "intra-item measurement budget cap; content taller than the cap is clipped "
                            + "(the atomic measure pass does not paginate). Unreachable for any real "
                            + "document; the page-by-page streaming measure is the documented follow-up.",
                        Severity: PaginateDiagnosticSeverity.Warning));
                    break;
                }
            }
        }

        // After flexibility resolution, each line's LineMainSize must
        // be updated to the sum of RESOLVED item main-sizes (NOT the
        // pre-flex sum from PackLines) so justify-content's freeSpace
        // = containerMainSize - line.LineMainSize sees the post-flex
        // layout. Recompute LineMainSize per line.
        for (var i = 0; i < lines.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[i];
            var sumResolved = 0.0;
            // Per Phase 3 Task 15 L10 — walk the sorted sequence (=
            // FirstItemIndex is a sorted-position, not a DOM-index).
            // No IsBlockLevel skip needed: the sorted sequence only
            // contains block-level children. Loop bound is precise:
            // ItemCount items starting at the sorted-position.
            var endPos = line.FirstItemIndex + line.ItemCount;
            for (var sortedPos = line.FirstItemIndex; sortedPos < endPos; sortedPos++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var domIdx = _sortedFlexChildIndices[sortedPos];
                sumResolved += resolvedItemMainSizes[domIdx];
            }
            lines[i] = line with { LineMainSize = sumResolved };
        }

        // Per Phase 3 Task 15 L7 — apply the line start offset to the
        // cross cursor. The cursor starts at the alignment-adjusted
        // origin (cross-start + lineStartOffset) instead of L6's
        // unadjusted contentCrossOffset. For align-content: flex-start
        // (the L6 default behavior) the offset is 0 and we get the
        // L6 cross-start stacking back unchanged.
        //
        // Per Phase 3 Task 15 L11 post-PR-#71 hardening F#1: the
        // `swappedAxisCursor` walks in SWAPPED-axis coordinates (=
        // 0 at the (possibly-swapped) cross-start, increasing toward
        // the (possibly-swapped) cross-end). For wrap (`!isWrapReverse`)
        // the swapped axis IS the physical axis: physicalCrossOffset
        // = contentCrossOffset + swappedAxisCursor. For wrap-reverse,
        // the swap formula converts to physical:
        //   physicalCrossOffset = contentCrossOffset + containerCrossSize
        //                       - swappedAxisCursor - line.LineCrossSize
        // (the line's PHYSICAL-TOP edge is at containerCrossEnd minus
        // the line's "depth" from the swapped cross-start). Both
        // formulas produce the spec-correct stacking origin + the
        // correct align-content offsets relative to the swapped
        // edges.
        var swappedAxisCursor = lineStartOffset;

        // Per Phase 3 Task 16 cycle 4e post-PR-#86 review P1 #1 —
        // track the PHYSICAL cross-axis bottom of the deepest emitted
        // line so the post-loop PageComplete return can report the
        // ACTUAL occupied block extent (= NOT the naive
        // sum(LineCrossSize), which ignores align-content's
        // lineStartOffset + lineBetweenSpacing). For align-content:
        // space-between with 2 lines of 30 in a 100 budget the lines
        // occupy [0,30] + [70,100] — occupied extent = 100, while
        // sum(LineCrossSize) = 60. Cycle 4f will consume the
        // accurate value for wrapper resize + ConsumedBlockSize
        // accounting; a wrong value would either clip children
        // (under-counted) or leave dead space (over-counted) at the
        // wrapper bottom.
        //
        // Coordinate system: the value is in content-cross-box
        // 0-based coords (= relative to the wrapper's content-cross-
        // start, NOT the absolute fragmentainer coord). For
        // non-wrap-reverse (= the only paginatable case per
        // <see cref="IsPaginatablePerStyle"/>),
        // <c>swappedAxisCursor</c> already IS the 0-based content-
        // cross offset of the current line's top; the line's bottom
        // = <c>swappedAxisCursor + LineCrossSize</c>.
        var maxEmittedCrossBottom = 0.0;

        // Flex-column pagination state. For `flex-direction: column` + nowrap
        // the single line's items stack down the MAIN = block axis, so the
        // split is BETWEEN items: emit items at sorted-position
        // [resumeItemIndex, splitItemIndex), defer the rest via
        // FlexContinuation.ItemIndex. The page CUT-OFF is _pageBlockBudget
        // (falling back to _contentBlockSize) — kept distinct from
        // containerMainSize so flex resolution sized items naturally. Items
        // are re-anchored on the resume page by columnResumeShift (= the
        // natural main-offset of the first item emitted on this page minus
        // the content-main origin), so the resumed item starts at the page's
        // content-block-start. -1 = no split (all remaining items fit).
        // resumeItemIndex + columnBudget were computed + validated alongside
        // the line index above.
        var outgoingItemIndex = -1;
        var columnResumeShift = 0.0;
        var firstColumnItemSeen = false;
        var maxEmittedColumnBottom = 0.0;

        // Per Phase 3 Task 15 L14 — encapsulate the cross-axis swap
        // state into one record so the line-emission formula has a
        // single named expression. Pre-L14 the swap math
        //   isWrapReverse
        //     ? contentCrossOffset + containerCrossSize - swappedAxisCursor - line.LineCrossSize
        //     : contentCrossOffset + swappedAxisCursor
        // was inline at the cursor site. Extracting it makes the swap
        // a first-class concept that future writing-mode work can
        // reuse without re-deriving the formula. See
        // <see cref="CrossAxisFlow"/>'s xmldoc for the coordinate-
        // system contract.
        // PR #215 review [P1] — the cross-axis orientation is resolved ONCE (`isCrossAxisReversed`)
        // and shared between the per-LINE stacking (here) AND the per-ITEM anchor (below), so a
        // column-rtl `wrap` stacks lines from the physical right and a wrap-reverse from the physical
        // top, consistently. The double-count an earlier cut hit came from feeding `PhysicalLineOffset`
        // the UNSTRETCHED `line.LineCrossSize` while the items used the (stretched/container) effective
        // extent — fixed below by passing `lineCrossExtent` to `PhysicalLineOffset` (the SAME extent the
        // item placement uses), so a single full-extent line reverses to a no-op.
        var crossFlow = new CrossAxisFlow(
            IsReversed: isCrossAxisReversed,
            ContentCrossOffset: contentCrossOffset,
            ContainerCrossSize: containerCrossSize);

        // Per Phase 3 Task 16 cycle 1 — the fragment range
        // determination above already sliced `lines` to the lines
        // for THIS fragment. The emission loop iterates them
        // verbatim from index 0 (= the swap formula in
        // CrossAxisFlow + the align-content offsets computed above
        // operate on the fragment's lines, not the full unfragmented
        // line list). The outgoing continuation's LineIndex was
        // already computed (`outgoingContinuationLineIndex`).
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Per Phase 3 Task 15 L6 — `align-items` operates against
            // EACH LINE'S cross-extent (CSS Flexbox L1 §6.3): items on
            // a line center / end-pack / stretch relative to that
            // line's max item cross-size, not the container's full
            // cross-extent. For nowrap (single line), the line fills the
            // container cross extent so behavior is identical to L5.
            //
            // For wrapping, the line's cross extent is max(item cross-
            // size) of the items it contains; an explicit container
            // cross-size doesn't expand each line on its own. Per Phase
            // 3 Task 15 L7 — align-content now distributes the
            // remaining cross-axis space (positional values absorb it
            // into the line cursor's start offset + between-spacing;
            // the stretch default grows each line's cross extent in
            // place, which propagates to the align-items math below
            // via the mutated lines[i].LineCrossSize).
            var lineCrossExtent = isWrapping
                ? line.LineCrossSize
                : containerCrossSize;

            // Per Phase 3 Task 15 L11 post-PR-#71 F#1 + L14 — convert
            // the swapped-axis cursor to a physical cross-offset for
            // emission. The cursor walks in the swapped axis (always
            // increasing toward the swapped cross-end); the helper
            // returns the line's TOP edge in the wrapper's content-
            // box coordinate system per the
            // <see cref="CrossAxisFlow.PhysicalLineOffset"/> formula.
            // PR #215 review [P1] — pass the line's EFFECTIVE cross extent
            // (`lineCrossExtent`, = the container extent for nowrap, the
            // stretched line size for wrap) — NOT the raw `line.LineCrossSize`
            // — so a reversed full-extent line is a no-op and the per-line +
            // per-item reversals use the same extent (no double-count).
            var lineCrossCursor = crossFlow.PhysicalLineOffset(
                swappedAxisCursor, lineCrossExtent);

            // Flex baseline-alignment cycle — the line's baseline reference: the MAX
            // first-baseline over the line's baseline-aligned items (CSS Box Alignment
            // L3 §6.2 "baseline-sharing group"). Each baseline-aligned item is then
            // shifted so its own baseline sits on this reference. NaN when no item on
            // the line is baseline-aligned (the per-item baseline branch is gated on the
            // item's effective align, so the value is read only when it's finite).
            var lineMaxBaseline = itemBaselineOffsets is null
                ? double.NaN
                : ComputeLineMaxBaseline(line, itemBaselineOffsets, alignItems);

            // PR #208 [P1] — a baseline-aligned item shifted DOWN to align its baseline can
            // extend below the line's packed cross extent (`line.LineCrossSize`). Track the
            // deepest such item bottom RELATIVE to the line's cross-start so the cursor
            // advance + the wrapper extent grow to contain it (else following lines /
            // siblings overlap). 0 for lines with no down-shifted baseline item → the
            // effective line cross size stays `line.LineCrossSize` (byte-identical).
            var lineBaselineOverflowBottom = 0.0;

            // L2 — resolve justify-content + compute the main-axis
            // start-offset + between-spacing per CSS Box Alignment L3
            // §4.5. Per Phase 3 Task 15 L6 each line runs its own
            // justify-content with the container's main extent as the
            // alignment target — items within a line align relative to
            // the container's main-axis range, not the line's used
            // main-size. (CSS Flexbox L1 §6.3 says wrapped lines are
            // formatted independently for main-axis alignment; the
            // container's main extent remains the alignment basis.)
            // §8 — the main-axis gaps consume free space BEFORE justify-content
            // distributes the remainder (gaps are not part of the distributed
            // space). N items → N-1 gutters.
            var lineMainGapTotal = System.Math.Max(0, line.ItemCount - 1) * mainGap;
            var freeSpace = containerMainSize - line.LineMainSize - lineMainGapTotal;
            var (startOffset, betweenSpacing) = ComputeJustifyContentOffsets(
                resolvedJC.Value, resolvedJC.Mode, freeSpace, line.ItemCount);

            // The main-axis cursor walks from the container's content-
            // main-start edge plus the alignment start-offset across
            // each item's natural main-size. Between-spacing extends
            // each post-item advance. For row direction the cursor is
            // an inline-axis position; for column direction it's a
            // block-axis position.
            var mainCursor = contentMainOffset + startOffset;

            // Per Phase 3 Task 15 L6 — iterate only the items on this
            // line. Per Phase 3 Task 15 L10 — `line.FirstItemIndex` is
            // a POSITION in `_sortedFlexChildIndices` (NOT a DOM-
            // children index); the sorted sequence is pre-filtered to
            // block-level children only, so the inner IsBlockLevel
            // skip the L1-L9 loop carried is no longer needed. The
            // loop bound is precise: ItemCount items starting at the
            // line's first sorted-position.
            var endSortedPos = line.FirstItemIndex + line.ItemCount;
            for (var sortedPos = line.FirstItemIndex; sortedPos < endSortedPos; sortedPos++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var itemIdx = _sortedFlexChildIndices[sortedPos];
                var item = _rootBox.Children[itemIdx];

                // Per L4 — read item sizes via direction-resolved
                // property IDs. For row: mainSize = width, crossSize =
                // height. For column: mainSize = height, crossSize =
                // width. Per Phase 3 Task 15 L8 the main-size comes
                // from the §9.7 flexibility resolution
                // (`resolvedItemMainSizes`) indexed by DOM-children
                // index: an item with `flex-grow: 1` in a row of
                // hypothetical-100 items splitting 200 free space grows
                // to 150 (= 100 + 200/2), and the resolved array carries
                // that flexed value. Cross-size still uses the declared
                // property; cross-axis flexibility (= L8 align-self
                // stretch + cross-axis intrinsic sizing) is L9+ scope.
                var itemMainSize = resolvedItemMainSizes[itemIdx];
                // Per Phase 3 Task 15 L3 post-PR-#63 hardening F#2 +
                // L4 — the stretch branch needs to distinguish `auto`
                // (Unset / Keyword slot) from explicit `0` (LengthPx
                // slot with value 0). The cross-axis property is
                // direction-dependent — IsCrossSizeAuto receives the
                // resolved direction. (Read BEFORE the cross size so the
                // box-sizing map below can skip the auto case.)
                var itemIsCrossSizeAuto = IsCrossSizeAuto(item, flexDirection);
                // Flex box-sizing cycle — a DEFINITE (LengthPx) cross size is the item's BORDER
                // box honoring `box-sizing`, via the shared `CrossBorderBoxSizePx`; `auto` /
                // an unresolved percentage → 0 (the stretch / align path uses the line cross
                // extent instead — ComputeAlignItemsPlacement reads itemIsCrossSizeAuto). The
                // shared helper keeps this in lockstep with FlexLinePacker's line-cross packing
                // (post-PR-#190 Copilot review — a percentage cross no longer floors to chrome).
                var itemCrossSize = item.Style.CrossBorderBoxSizePx(crossSizeProperty, containerDefiniteCrossSize);

                // Per Phase 3 Task 15 L6 — pass the line's cross
                // extent (= max(item cross-size on this line) for
                // wrap, or the container's full cross extent for
                // nowrap) and the line's cross cursor (= start of
                // this line on the cross axis) into the align-items
                // helper. Items align against the LINE'S cross
                // extent per CSS Flexbox L1 §6.3.
                //
                // Per Phase 3 Task 15 L9 — `align-self` overrides
                // `align-items` per item per CSS Box Alignment L3 §4.3:
                // "If the value of align-self is auto, its used value
                // is the value of align-items on the parent" — i.e.,
                // align-self: auto (the cascade default) preserves the
                // L1-L8 container-only behavior; any other value
                // overrides the container's align-items for this one
                // item. The reader returns ResolvedAlignSelf; the
                // ResolveAgainstContainerAlignItems extension folds the
                // per-item enum + the container's (value, mode) tuple
                // into the effective ResolvedAlignItems passed to the
                // placement helper.
                var alignSelf = item.Style.ReadAlignSelf();
                var effectiveAlign = alignSelf.ResolveAgainstContainerAlignItems(alignItems);
                // Per Phase 3 Task 15 L11 post-PR-#71 F#2 — under
                // wrap-reverse the cross-axis is swapped uniformly:
                // each LINE's cross-start = the line's PHYSICAL-
                // BOTTOM edge (for row + horizontal-tb LTR). Pass
                // `isWrapReverse` to ComputeAlignItemsPlacement so
                // flex-start/flex-end positional values swap within
                // the line (FlexStart now means "physical bottom of
                // line"; FlexEnd means "physical top of line").
                // Center is symmetric and unaffected. Stretch is
                // unaffected (= grow the item to the line's full
                // cross-extent). The line's PHYSICAL-TOP edge
                // (`lineCrossCursor`) is the helper's reference
                // origin in both modes; the swap parameter only
                // affects which END of the line FlexStart/FlexEnd
                // anchor to.
                double itemCrossOffsetWithinLine, itemEffectiveCrossSize;
                if (effectiveAlign.Value == AlignItemsValue.Baseline
                    && itemBaselineOffsets is not null && itemBaselineCrossSizes is not null
                    && !isWrapReverse)
                {
                    // Flex baseline alignment (ROW, non-wrap-reverse; CSS Flexbox L1 §8.3).
                    // Shift the item on the cross axis so its first baseline coincides with
                    // the line's max baseline (`lineMaxBaseline`); the item keeps its own
                    // cross size (baseline never resizes). A baseline item is never
                    // stretched, so this bypasses the stretch / positional helper.
                    // wrap-reverse baseline (the line's cross axis is swapped) falls through
                    // to the helper's flex-start fallback (PR #208 Copilot review — the
                    // mirrored shift is a documented residual, see deferrals.md).
                    var itemBaseline = itemBaselineOffsets[itemIdx];
                    itemEffectiveCrossSize = itemBaselineCrossSizes[itemIdx];
                    itemCrossOffsetWithinLine = lineCrossCursor + (lineMaxBaseline - itemBaseline);
                    // [P1] track this item's bottom relative to the line cross-start.
                    var itemBaselineBottom = (itemCrossOffsetWithinLine - lineCrossCursor) + itemEffectiveCrossSize;
                    if (itemBaselineBottom > lineBaselineOverflowBottom)
                        lineBaselineOverflowBottom = itemBaselineBottom;
                }
                else
                {
                    // The NATURAL cross-axis anchor reversal, by keyword family (CSS Box Alignment
                    // L3 §6.2):
                    //   • `flex-start`/`flex-end` (FlexRelative) — the flex-flow cross-start, which
                    //     `wrap-reverse` permutes → `isCrossAxisReversed` (isWrapReverse ^ isColumnRtl).
                    //   • `start`/`end` (Container) — the CONTAINER's writing-mode/direction, which
                    //     `wrap-reverse` does NOT permute → `isColumnRtl` only. Coincides with
                    //     flex-start except under wrap-reverse, where they correctly diverge.
                    //   • `self-start`/`self-end` (Subject) — the ITEM's OWN writing-mode/direction;
                    //     `wrap-reverse` does NOT permute these either (PR #217 review [P1]). A ROW
                    //     container's cross axis is the block axis (direction-independent); a COLUMN
                    //     container's cross axis is the inline axis, so an LTR child in an RTL column
                    //     stays at its own start → `isColumn && item.Style.IsRtl()` (NO wrap-reverse).
                    // For an item matching the container's direction with no wrap-reverse these all
                    // coincide → byte-identical; only wrap-reverse `start`/`end` and column-rtl
                    // `self-*` diverge.
                    var naturalReversed = effectiveAlign.Reference switch
                    {
                        CrossAlignReference.Container => isColumnRtl,
                        CrossAlignReference.Subject => isColumn && item.Style.IsRtl(),
                        _ => isCrossAxisReversed,
                    };
                    // PR #217 review [P2] — the `safe` overflow fallback is INDEPENDENT of the natural
                    // anchor: per CSS Box Alignment L3 §5.3 an overflowing `safe` subject falls back to
                    // the flex-flow start (flex-start), which IS the line's permuted cross-start under
                    // `wrap-reverse`. So the helper takes the flex-flow `isCrossAxisReversed` for that
                    // fallback separately from the (possibly container-/item-logical) natural orientation.
                    // RC-2 / RC-11 — an AUTO cross-size item was placed (and its geometry fragment
                    // emitted) with a cross size of 0: `center`/`flex-end` centered it as if it were
                    // zero-height (off by half / a full line cross), and the emitted item box had
                    // BlockSize 0 so FragmentPainter culled its ENTIRE background/border/radius (the
                    // dropped card/chip/pill decorations across the corpus). Feed the CONTENT-measured
                    // cross border box (`itemBaselineCrossSizes`, computed for every ROW flex above) as
                    // the align cross size for an auto item. The Stretch branch ignores it (it grows to
                    // the line cross extent regardless), so stretch stays byte-identical; the positional
                    // branches now use the real content size for both the space math AND the returned
                    // effective cross size the geometry fragment paints. Row-only (column baseline data
                    // isn't computed — a documented residual, deferrals.md).
                    var alignCrossSize = itemCrossSize;
                    if (itemIsCrossSizeAuto && itemBaselineCrossSizes is not null
                        && !double.IsNaN(itemBaselineCrossSizes[itemIdx]))
                    {
                        alignCrossSize = itemBaselineCrossSizes[itemIdx];
                    }
                    (itemCrossOffsetWithinLine, itemEffectiveCrossSize) =
                        ComputeAlignItemsPlacement(
                            effectiveAlign.Value, effectiveAlign.Mode,
                            lineCrossExtent, alignCrossSize,
                            itemIsCrossSizeAuto,
                            lineCrossCursor,
                            naturalCrossReversed: naturalReversed,
                            safeFallbackReversed: isCrossAxisReversed);
                }

                // Per Phase 3 Task 15 L5 — for reversed directions,
                // flip the main-axis offset around the container's
                // main extent per CSS Flexbox L1 §5.1 ("row-reverse /
                // column-reverse: same as row / column but main-start
                // and main-end are swapped").
                //
                // The natural cursor walks 0 → containerMainSize in
                // DOM order. The flip transform
                //   actualMainOffset = (contentMainOffset + containerMainSize)
                //                    - (mainCursor - contentMainOffset)
                //                    - itemMainSize
                // rewrites each item's main-axis offset relative to
                // the container's main-END edge instead of its main-
                // start edge, producing items packed against the
                // reversed main-start (= the original main-end) in
                // REVERSE DOM order without reversing the iteration
                // loop.
                //
                // The flip is direction-agnostic (operates on the
                // abstract main axis); only the physical mapping
                // below differs between row-reverse (flip applies to
                // inline) and column-reverse (flip applies to block).
                //
                // For Phase 3 Task 15 L6 wrapping — the reverse
                // transform still operates per-line: each line's
                // items reverse their own main-axis ordering, but
                // line stacking on the cross axis is unaffected. This
                // matches CSS Flexbox L1 §5.1 + §6.3: row-reverse
                // swaps main-start / main-end PER LINE; wrap-reverse
                // (the cross-axis reversal) is what would flip line
                // ordering, and that's L7+ scope.
                // Backlog #4 — a PAGINATING column-reverse already reversed the
                // item order above, so it emits FORWARD (no flip): the reverse
                // is achieved by the order, and the per-page column item-split
                // re-anchors each page from the top. Every other reverse case
                // (non-paginating column-reverse, row-reverse) keeps the flip.
                var mainOffsetForEmission = (isReverse && !columnReverseEmitForward)
                    ? (contentMainOffset + containerMainSize) - (mainCursor - contentMainOffset) - itemMainSize
                    : mainCursor;

                // Flex-column item pagination: decide whether THIS item commits
                // to the current page (and, if so, re-anchor it). The natural
                // mainCursor walk above gives each item its UNFRAGMENTED main
                // offset; here we (a) skip items already committed on a prior
                // page or deferred after this page's cut, (b) re-anchor the
                // first item emitted on this page to the content-block-start,
                // and (c) cut before the first item whose re-anchored bottom
                // exceeds the page budget — the first item on a page always
                // commits (CSS Fragmentation L3 §4.4 forward-progress, so an
                // item taller than the page force-overflows rather than looping).
                var emitThisItem = true;
                if (isColumnItemPagination)
                {
                    if (sortedPos < resumeItemIndex || outgoingItemIndex >= 0)
                    {
                        emitThisItem = false;
                    }
                    else if (!firstColumnItemSeen)
                    {
                        columnResumeShift = mainOffsetForEmission - contentMainOffset;
                        firstColumnItemSeen = true;
                        mainOffsetForEmission = contentMainOffset;
                    }
                    else
                    {
                        var shifted = mainOffsetForEmission - columnResumeShift;
                        var itemPageBottom = (shifted - contentMainOffset) + itemMainSize;
                        if (itemPageBottom > columnBudget)
                        {
                            outgoingItemIndex = sortedPos;
                            emitThisItem = false;
                        }
                        else
                        {
                            mainOffsetForEmission = shifted;
                        }
                    }
                    if (emitThisItem)
                    {
                        var emittedBottom = (mainOffsetForEmission - contentMainOffset) + itemMainSize;
                        if (emittedBottom > maxEmittedColumnBottom)
                        {
                            maxEmittedColumnBottom = emittedBottom;
                        }
                    }
                }

                // Map the abstract (main, cross) tuple to the physical
                // (inline, block) axes per direction.
                double inlineOffset, blockOffset, inlineSize, blockSize;
                if (isColumn)
                {
                    // Column: main = block, cross = inline.
                    blockOffset = mainOffsetForEmission;
                    inlineOffset = itemCrossOffsetWithinLine;
                    blockSize = itemMainSize;
                    inlineSize = itemEffectiveCrossSize;
                }
                else
                {
                    // Row: main = inline, cross = block.
                    inlineOffset = mainOffsetForEmission;
                    blockOffset = itemCrossOffsetWithinLine;
                    inlineSize = itemMainSize;
                    blockSize = itemEffectiveCrossSize;
                }

                // Flex content-inset cycle — BLOCK-CHILD content is inset from the item's
                // border-box origin (inlineOffset, blockOffset) to its CONTENT-box origin by
                // the item's own inline-start / block-start border + padding (LTR
                // horizontal-tb; NestedContentMeasurer lays block children at the content
                // box's (0,0) WITHOUT the item's own chrome). An INLINE-ONLY-root buffer
                // (box == item) is NOT inset — it sits at the border-box origin and
                // TextPainter insets its glyphs by the same chrome (insetting it again would
                // double-inset). The applied inset is gated per buffer below. The emitted box
                // border box (inlineSize / blockSize) already includes the chrome via the
                // box-sizing-mapped cross size + the §9.7-resolved main size.
                var contentInsetInline = item.Style.InlineStartBorderPaddingPx();
                var contentInsetBlock = item.Style.BlockStartBorderPaddingPx();

                if (emitThisItem && isRowNowrapContentPagination)
                {
                    // Row-nowrap intra-item content split — slice this item's box
                    // decoration + buffered content to the page's SHARED cross
                    // window [windowFrom, windowTo) (content-cross coords), so all
                    // items continue at the same cross position (the page top) on
                    // the next page. `blockOffset` is the item BORDER-box
                    // cross-start (abs); `contentCrossOffset` is the container
                    // content-box cross-start (abs).
                    var windowFrom = rowNowrapResumeCut;
                    var windowTo = rowNowrapResumeCut + rowNowrapBudget;
                    var itemCrossTop = blockOffset - contentCrossOffset;
                    var itemCrossBottom = itemCrossTop + blockSize;
                    // Box decoration slice, clamped to the window (a per-page
                    // slice of the box — box-decoration-break: clone, a documented
                    // first-cut approximation; the spec-default `slice` would omit
                    // the border at the break edges).
                    var boxSliceTop = System.Math.Max(itemCrossTop, windowFrom);
                    var boxSliceBottom = System.Math.Min(itemCrossBottom, windowTo);
                    if (boxSliceBottom > boxSliceTop)
                    {
                        _sink.Emit(new BoxFragment(
                            Box: item,
                            InlineOffset: inlineOffset,
                            BlockOffset: contentCrossOffset + (boxSliceTop - windowFrom),
                            InlineSize: inlineSize,
                            BlockSize: boxSliceBottom - boxSliceTop));
                        var boxExtentRel = boxSliceBottom - windowFrom;
                        if (boxExtentRel > rowNowrapEmittedExtent)
                        {
                            rowNowrapEmittedExtent = boxExtentRel;
                        }
                    }
                    if (itemCrossBottom > windowTo)
                    {
                        rowNowrapAnyRemaining = true;       // box continues past this page
                    }
                    if (itemContentBuffers[itemIdx] is { } buf)
                    {
                        // Inset BLOCK-CHILD content to the item's content box (skip an
                        // inline-only-root buffer — TextPainter insets its glyphs): the main
                        // (inline) axis shifts by the inline-start chrome; the cross-start
                        // chrome shifts the content's cross origin within the shared window math.
                        var insetI = buf.ContainsDecorationOwnerFragment ? 0 : contentInsetInline;
                        var insetB = buf.ContainsDecorationOwnerFragment ? 0 : contentInsetBlock;
                        var (emitted, anyRem) = buf.FlushRangeTo(
                            _sink, inlineOffset + insetI,
                            blockOffset + insetB, contentCrossOffset,
                            windowFrom, windowTo);
                        if (emitted > rowNowrapEmittedExtent)
                        {
                            rowNowrapEmittedExtent = emitted;
                        }
                        rowNowrapAnyRemaining |= anyRem;
                    }
                    // Surface this committed item's buffered diagnostics now.
                    itemDiagnosticBuffers[itemIdx]?.FlushTo(effectiveDiagnostics);
                }
                else if (emitThisItem)
                {
                    _sink.Emit(new BoxFragment(
                        Box: item,
                        InlineOffset: inlineOffset,
                        BlockOffset: blockOffset,
                        InlineSize: inlineSize,
                        BlockSize: blockSize));
                    // RC2 — record this item's border-box geometry IFF it establishes an
                    // absolute containing block, so an abspos descendant anchors to the item
                    // (FlexLayouter is not a delegation boundary; the outer abspos pass resolves
                    // it). The callback no-ops for the common non-positioned item. Only the
                    // whole-item emit records — a page-sliced item (the row-nowrap branch above)
                    // leaves its abspos descendant deferred, consistent with abspos-pagination.
                    _recordPositionedGeometry?.Invoke(item, inlineOffset, blockOffset, inlineSize, blockSize);

                    // Non-block-pagination arc (flex item CONTENT layout) — re-emit the
                    // item's measured inner content at its FINAL (re-anchored) position.
                    // BLOCK-CHILD content is inset to the item's CONTENT-box origin (=
                    // border-box origin + the item's inline-/block-start border + padding,
                    // flex content-inset cycle — content now sits INSIDE the item's
                    // border/padding); an INLINE-ONLY-root buffer stays at the border-box
                    // origin (TextPainter insets its glyphs by the item's chrome). Only
                    // COMMITTED items flush; the column-split skips deferred items, so their
                    // buffers are simply discarded (a resumed page re-measures them). FlushTo
                    // clears the buffer.
                    if (itemContentBuffers[itemIdx] is { } contentBuf)
                    {
                        var insetI = contentBuf.ContainsDecorationOwnerFragment ? 0 : contentInsetInline;
                        var insetB = contentBuf.ContainsDecorationOwnerFragment ? 0 : contentInsetBlock;
                        // RC2 residual (1) — also record positioned-CB geometry for any positioned BLOCK
                        // DESCENDANT flushed from this item's content buffer, so an abspos box anchored to a
                        // `position:relative` block nested inside the flex item (not just the item itself)
                        // resolves against it instead of being dropped. The callback self-filters to
                        // CB-establishers; no-op for the common all-static item content.
                        contentBuf.FlushTo(_sink, inlineOffset + insetI, blockOffset + insetB,
                            _recordPositionedGeometry);
                    }
                    // PR-#182 review P2 — surface this committed item's buffered
                    // content diagnostics now (deferred items' diagnostics stay
                    // buffered + are discarded, re-generated when they commit).
                    itemDiagnosticBuffers[itemIdx]?.FlushTo(effectiveDiagnostics);
                }

                // Advance the main-axis cursor past this item + the
                // L2 between-spacing. With flex-wrap: nowrap the
                // cursor walks past containerMainSize for items that
                // don't fit — overflow is the spec'd behavior for
                // nowrap (CSS Flexbox L1 §6 + §9.4). With wrap the
                // cursor stays within the line's items only; the
                // outer line loop resets mainCursor on each line. The
                // between-spacing only contributes for the
                // distribution values (space-between / -around /
                // -evenly); position values leave it at 0.
                mainCursor += itemMainSize + betweenSpacing;
                // §8 — a main-axis gutter follows every item except the last on
                // the line (so an auto-sized container isn't inflated by a
                // trailing gap).
                if (sortedPos < endSortedPos - 1) mainCursor += mainGap;
            }

            // Advance the line cross cursor past this line's cross
            // extent + the L7 between-line spacing (= the per-line gap
            // contributed by align-content's distribution values:
            // space-between / -around / -evenly). For positional values
            // (flex-start / flex-end / center) and stretch, the
            // between-spacing is 0 — the gap is absorbed by the
            // startOffset (positional) or by the line cross-extent
            // (stretch).
            // Per Phase 3 Task 16 cycle 4e post-PR-#86 review P1 #1 —
            // record the deepest line bottom seen so far so the
            // post-loop PageComplete return reports the TRUE occupied
            // extent (= accounts for align-content offsets +
            // between-line spacing). For non-wrap-reverse,
            // swappedAxisCursor IS the line's content-cross 0-based
            // top, so bottom = swappedAxisCursor + LineCrossSize.
            // For paginatable containers (= non-wrap-reverse per
            // IsPaginatablePerStyle), lines emit in increasing
            // cursor order, so the max-bottom is always the last
            // line's bottom. Tracking max defensively makes this
            // robust to future sub-cycle changes (e.g., should
            // wrap-reverse ever join the paginatable set, the same
            // accumulator works).
            // PR #208 [P1] — the line's EFFECTIVE cross extent is the larger of its packed
            // cross size and the deepest baseline-down-shifted item bottom, so a shifted
            // item is contained by the cursor advance + the wrapper extent.
            var effectiveLineCrossSize = System.Math.Max(line.LineCrossSize, lineBaselineOverflowBottom);
            var lineBottom = swappedAxisCursor + effectiveLineCrossSize;
            if (lineBottom > maxEmittedCrossBottom)
            {
                maxEmittedCrossBottom = lineBottom;
            }

            // Per Phase 3 Task 15 L11 post-PR-#71 F#1 — the SWAPPED-
            // axis cursor advances toward the swapped cross-end (=
            // physical cross-start for wrap-reverse). The next line's
            // physical offset is computed by the swap formula at the
            // top of the loop body. For wrap (= !isWrapReverse) this
            // is identical to L1-L10 cursor behavior.
            // §8 — a cross-axis gutter follows every line; the trailing one (after
            // the last line) only advances the discarded cursor — the container's
            // cross extent reads maxEmittedCrossBottom (the true last-line bottom),
            // mirroring the lineBetweenSpacing handling.
            swappedAxisCursor += effectiveLineCrossSize + lineBetweenSpacing + crossGap;
        }

        // Per Phase 3 Task 16 cycle 1 → cycle 4b — multi-page flex
        // split outcome. When `outgoingContinuationLineIndex` is set
        // (≥ 0), at least one line did NOT fit on this page and we
        // emit a <see cref="LayoutAttemptResult.PageComplete"/>
        // carrying a <see cref="FlexContinuation"/> so the outer
        // pagination can call back with the continuation on the
        // next page. When -1 (= every remaining line fit), the
        // container is fully emitted + we return
        // <see cref="LayoutAttemptResult.AllDone"/>.
        //
        // BlockLayouter dispatch integration (cycle 4b ACTIVE):
        // both the outer + recursive dispatch sites apply the
        // paginatable-flex extent clamp BEFORE dispatch — eligible
        // containers (per <see cref="IsPaginatablePerStyle"/>) whose
        // grown natural extent overflows the remaining fragmentainer
        // space get <c>allowPagination: true</c> + a clamped
        // content-block-size. The forced-overflow path also routes
        // through the dispatch helper (cycle-4b P1 #2 fix) so flex
        // items emit correctly even when the cycle-4b clamp
        // didn't fire (e.g. column / wrap-reverse atomic
        // overflow). The PageComplete branch below propagates up via
        // <c>BlockContinuation(LayouterState=FlexContinuation)</c>;
        // the resume page's BlockLayouter forwards the leaf back to
        // this layouter via the cycle-4b inbound chain-walk in
        // EmitBlockSubtreeRecursive's nested flex branch.
        // PR-#180 review P2 — record the emitted content extent for the
        // dispatching BlockLayouter's wrapper resize. Column → deepest emitted
        // item bottom; row-wrap → deepest emitted line bottom.
        LastEmittedBlockExtent = isColumnItemPagination
            ? maxEmittedColumnBottom
            : isRowNowrapContentPagination
                ? (rowNowrapAnyRemaining
                    ? System.Math.Min(rowNowrapEmittedExtent, rowNowrapBudget)
                    : rowNowrapEmittedExtent)
                // Auto-height row (03 fix) — the packer-derived maxEmittedCrossBottom is 0 for
                // content-determined items; use the content cross extent so the wrapper resizes to fit.
                : System.Math.Max(maxEmittedCrossBottom, autoRowContentCross);

        // Row-nowrap intra-item content split — if ANY item still has content (or
        // box) below this page's cross window, resume on the next page at the
        // accumulated cut (windowFrom + budget). All items continue at the same
        // cross position (the page top), so LineIndex + ItemIndex stay 0 and the
        // resume rides ConsumedCrossExtent. A fresh per-page layouter re-measures
        // every item, so no per-item resume state is needed beyond the cut.
        if (isRowNowrapContentPagination && rowNowrapAnyRemaining)
        {
            return LayoutAttemptResult.PageComplete(
                new FlexContinuation(
                    LineIndex: 0,
                    BaselineState: null,
                    EmittedBlockExtent: System.Math.Min(rowNowrapEmittedExtent, rowNowrapBudget),
                    ItemIndex: 0,
                    ConsumedCrossExtent: rowNowrapResumeCut + rowNowrapBudget),
                cost: 0);
        }

        // Flex-column item split — the emission loop set outgoingItemIndex to
        // the first sorted-position that didn't fit this page's block budget.
        // Resume there on the next page (LineIndex stays 0 — the single column
        // line is re-packed identically; only the item window differs).
        // maxEmittedColumnBottom is the deepest re-anchored item bottom (page
        // 0-based) for the wrapper-resize / ConsumedBlockSize accounting.
        if (outgoingItemIndex >= 0)
        {
            return LayoutAttemptResult.PageComplete(
                new FlexContinuation(
                    LineIndex: 0,
                    BaselineState: null,
                    EmittedBlockExtent: maxEmittedColumnBottom,
                    ItemIndex: outgoingItemIndex),
                cost: 0);
        }

        if (outgoingContinuationLineIndex >= 0)
        {
            // Per Phase 3 Task 16 cycle 4e (P2 #5 from PR-#79)
            // post-PR-#86 review P1 #1 — report the TRUE occupied
            // block extent on the PageComplete continuation: the
            // physical bottom (in content-cross-box 0-based coords)
            // of the deepest emitted line, tracked in
            // <c>maxEmittedCrossBottom</c> during the emission loop
            // above. This INCLUDES align-content's lineStartOffset
            // + lineBetweenSpacing contributions — pre-hardening the
            // value was a naive sum(LineCrossSize) which
            // under-counted for space-between / space-around /
            // space-evenly / center / flex-end alignment families.
            // Cycle 4f's wrapper-resize will consume this value
            // directly; if it were the naive sum, the resized
            // wrapper would clip its children for the
            // alignment-distribution cases.
            return LayoutAttemptResult.PageComplete(
                new FlexContinuation(
                    LineIndex: outgoingContinuationLineIndex,
                    BaselineState: null,
                    EmittedBlockExtent: maxEmittedCrossBottom),
                cost: 0);
        }
        return LayoutAttemptResult.AllDone(cost: 0);
    }

    /// <summary>Per Phase 3 Task 15 L6 — pack items into lines for the
    /// multi-line algorithm per CSS Flexbox L1 §6.3 + §9.3.
    /// <list type="bullet">
    ///   <item><c>nowrap</c> — every block-level item lands on a single
    ///   line (L1-L5 single-line algorithm preserved verbatim; the
    ///   single FlexLine carries total main-size + max cross-size).</item>
    ///   <item><c>wrap</c> — greedy packing: walk items in DOM order,
    ///   accumulate main-axis sizes; when adding the next item would
    ///   exceed <paramref name="containerMainSize"/>, commit the current
    ///   line + start a new one. The first item on a line ALWAYS lands
    ///   even if it itself overflows the container — per CSS Flexbox L1
    ///   §9.3 "if the very first uncollected item wouldn't fit, collect
    ///   just it into the line" (= an oversized solo item still emits
    ///   on its own line + overflows).</item>
    /// </list>
    ///
    /// <para><b>Hypothetical main-size</b> (per Phase 3 Task 15 L8
    /// post-PR-#68 F#1): the greedy decision uses each item's
    /// flex-basis-driven hypothetical main-size (= CSS Flexbox L1
    /// §9.2) via
    /// <see cref="ComputedStyleLayoutExtensions.ResolveFlexItemHypotheticalMainSize"/>.
    /// Pre-L8 the decision used the raw declared main-size; L8 made
    /// PackLines + ResolveFlexibleMainSizes + the BlockLayouter pre-
    /// measure all consume identical sizing for line-boundary parity.</para>
    ///
    /// <para><b>Cancellation.</b> The token is checked once per item.
    /// Mirrors the per-item check in the AttemptLayout emission
    /// loop.</para>
    ///
    /// <para><b>Index semantics</b> (per Phase 3 Task 15 L10): the
    /// returned <see cref="FlexLine.FirstItemIndex"/> is a POSITION
    /// in <paramref name="sortedChildIndices"/> (NOT a DOM-children
    /// index). The emission loop dereferences DOM-children indices
    /// via <c>sortedChildIndices[FirstItemIndex + i]</c>. The sorted
    /// sequence is PRE-FILTERED to block-level children only — the
    /// emission loop no longer carries the per-item IsBlockLevel
    /// skip the L1-L9 code held inline.</para></summary>
    /// <param name="flexContainer">The flex container box.</param>
    /// <param name="sortedChildIndices">Block-level children of
    /// <paramref name="flexContainer"/> in effective flex order
    /// (= (order, DOM-index) sorted per CSS Flexbox L1 §5.4).
    /// Produced by
    /// <see cref="ComputedStyleLayoutExtensions.GetFlexChildrenInOrderSequence"/>.</param>
    /// <param name="direction">Resolved <c>flex-direction</c>; selects
    /// which property feeds the main + cross axes.</param>
    /// <param name="containerMainSize">The container's main-axis content
    /// extent — the line-packing budget for wrap.</param>
    /// <param name="isWrapping">When <see langword="true"/>, run the
    /// greedy multi-line packing; otherwise emit a single line covering
    /// every block-level item (L1-L5 single-line algorithm).</param>
    /// <param name="cancellationToken">Propagates cancellation into the
    /// per-item loops.</param>
    /// <param name="mainGap">CSS Box Alignment L3 §8 main-axis gutter between
    /// items on a line (0 = none); folded into the wrap decision.</param>
    /// <param name="precomputedIntrinsicBaseSizes">Flex intrinsic-basis cycle —
    /// optional pre-measured max-content / min-content base sizes for ROW items with
    /// an explicit intrinsic <c>flex-basis</c> (null for column / wrap / no-shaper).</param>
    /// <param name="containerDefiniteCrossSize">Corpus-fidelity — the container's DEFINITE cross-axis
    /// content extent for resolving a flex item's PERCENTAGE cross size; <see cref="double.NaN"/> when
    /// indefinite (percentage cross reads 0).</param>
    /// <returns>The packed flex lines; never null. Returns an empty list
    /// when the container has no block-level children (matches the L1-L5
    /// behavior of emitting no item fragments). Per Phase 3 Task 15 L10,
    /// FlexLine.FirstItemIndex is a POSITION in
    /// <paramref name="sortedChildIndices"/>, not a DOM-children index.
    /// (Removed L1-L9 semantics): the first line is always
    /// at FlexLine.FirstItemIndex of the first block-level child; later
    /// lines reference their first block-level child's index in the
    /// original Children list.</returns>
    // Per Phase 3 Task 16 cycle 4c (P3 #8 from PR-#79) — the
    // line-packing algorithm has been extracted to the shared
    // <see cref="FlexLinePacker.Pack"/> helper. Both this layouter +
    // <c>BlockLayouter.PreMeasureFlexMultiLineCrossExtent</c> now
    // delegate to that one implementation; line-boundary parity is
    // guaranteed by construction (no more duplicate algorithm to
    // keep in lockstep through L8 F#1, L10 sort-by-order, etc.).
    //
    // Per Phase 3 Task 15 L8 post-PR-#68 hardening F#1 — line packing
    // uses each item's HYPOTHETICAL main-size (driven by flex-basis)
    // per CSS Flexbox §9.3, NOT the raw declared main-size. The
    // shared packer uses
    // <see cref="ComputedStyleLayoutExtensions.ResolveFlexItemHypotheticalMainSize"/>
    // so an item with <c>width: 300; flex-basis: 0; flex-grow: 1</c>
    // contributes 0 to the line packing + three such items fit on a
    // single line in a 300-px container.
    private static List<FlexLine> PackLines(
        Box flexContainer,
        List<int> sortedChildIndices,
        FlexDirectionValue direction,
        double containerMainSize,
        bool isWrapping,
        CancellationToken cancellationToken,
        double mainGap,
        IReadOnlyDictionary<Box, double>? precomputedIntrinsicBaseSizes = null,
        double containerDefiniteCrossSize = double.NaN)
        => FlexLinePacker.Pack(
            flexContainer, sortedChildIndices, direction,
            containerMainSize, isWrapping, cancellationToken, mainGap,
            precomputedIntrinsicBaseSizes, containerDefiniteCrossSize);

    /// <summary>Per Phase 3 Task 15 L6 — resolve the container's cross-
    /// axis extent for the L1-L5 single-line case + the L6 wrap case.
    /// For column direction the wrapper's resolved inline-size IS the
    /// container's cross extent in all cases (see L4 + L5 derivation;
    /// preserved here verbatim). For row direction the cross axis is
    /// the block axis: explicit <c>height</c> wins; for <c>height:
    /// auto</c> the auto cross-size derives from sum(line cross-extents)
    /// per CSS Flexbox L1 §9.4 — for nowrap this collapses to the
    /// single line's max-item-cross (= L1-L5 behavior); for wrap it
    /// sums each line's cross-extent.
    ///
    /// <para><b>Why the helper.</b> The two-channel derivation (explicit
    /// height vs. auto-height fallback) is direction-specific + read
    /// twice (once for the wrapper's cross-axis sizing signal here +
    /// once at premeasure time in BlockLayouter). Extracting the read
    /// from AttemptLayout keeps the main loop focused on the per-line
    /// emission; the helper itself is direction-agnostic for the wrap
    /// path (it just sums the pre-packed line cross-extents).</para></summary>
    private double ResolveContainerCrossSize(
        bool isColumn,
        List<FlexLine> lines,
        CancellationToken cancellationToken)
    {
        if (isColumn)
        {
            // Column direction — cross axis = inline axis. For column
            // direction the wrapper's resolved inline-size IS the
            // container's cross extent in all cases (BlockLayouter
            // inline-sizing). Per PR #64 Copilot review this branch
            // returns _contentInlineSize regardless of explicit
            // `width` — the wrapper has already applied any declared
            // width.
            return _contentInlineSize;
        }

        // Row direction — cross axis = block axis.
        var heightSlot = _rootBox.Style.Get(PropertyId.Height);
        if (heightSlot.Tag == ComputedSlotTag.LengthPx)
        {
            return heightSlot.AsLengthPx();
        }

        // height: auto — sum of line cross-extents per CSS Flexbox L1
        // §9.4. For nowrap (single line) this collapses to the L1-L5
        // max(item natural block-size). For wrap the sum is the spec-
        // correct multi-line cross extent.
        var sum = 0.0;
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sum += line.LineCrossSize;
        }
        // §8 — cross-axis (row-gap) gutters between wrapped lines add to the auto
        // cross-size so the container contains its gap-spaced lines. No-op for a
        // single line / no gap.
        sum += System.Math.Max(0, lines.Count - 1)
            * _rootBox.Style.ReadFlexGridGapOrZero(PropertyId.RowGap);
        return sum;
    }

    /// <summary>Per Phase 3 Task 15 L8 — resolve flexible main-sizes
    /// per CSS Flexbox L1 §9.7 (the flexibility algorithm). Walks each
    /// packed line, computes each item's hypothetical main-size from
    /// its <c>flex-basis</c>, then distributes the line's free main-
    /// space among items via their <c>flex-grow</c> (positive free
    /// space) or <c>flex-shrink × flex-basis</c> (negative free space)
    /// factors. Returns an array indexed by child index in
    /// <c>_rootBox.Children</c> carrying each item's RESOLVED main-size;
    /// non-block-level children (filtered out by the emission loop's
    /// <c>IsBlockLevel</c> gate) get entry 0.
    ///
    /// <para><b>Hypothetical main-size derivation</b> (per CSS Flexbox
    /// L1 §9.2 + ReadFlexBasis):
    /// <list type="bullet">
    ///   <item><c>flex-basis: &lt;length&gt;</c> (LengthPx) — use the
    ///   pixel value directly.</item>
    ///   <item><c>flex-basis: &lt;percentage&gt;</c> — resolve against
    ///   <paramref name="containerMainSize"/> per §9.2.3 (definite-
    ///   container case; indefinite-container percentage flex-basis
    ///   is L9+ scope).</item>
    ///   <item><c>flex-basis: auto</c> / <c>content</c> — delegate to
    ///   the item's declared main-size property (= the L1-L7 behavior
    ///   reading <c>ReadLengthPxOrZero(mainSizeProperty)</c>). Content
    ///   is approximated as Auto until intrinsic sizing lands.</item>
    /// </list></para>
    ///
    /// <para><b>Free-space distribution</b> (per §9.7):
    /// <list type="bullet">
    ///   <item>Compute <c>freeMainSpace = containerMainSize -
    ///   sum(hypothetical on line)</c>.</item>
    ///   <item>If <c>freeMainSpace &gt; 0</c> AND any item has
    ///   <c>flex-grow &gt; 0</c>: each item grows by <c>(item.flexGrow
    ///   / sumFlexGrow) × freeMainSpace</c>. Items with
    ///   <c>flex-grow: 0</c> don't grow; their share of free space goes
    ///   to the items that do.</item>
    ///   <item>If <c>freeMainSpace &lt; 0</c> AND any item has
    ///   <c>flex-shrink &gt; 0</c>: each item shrinks by <c>(item.flexShrink
    ///   × item.hypothetical / sumScaledShrinks) × |freeMainSpace|</c>.
    ///   Items with <c>flex-shrink: 0</c> don't shrink.</item>
    ///   <item>Otherwise (no grow or no shrink direction matches the
    ///   free-space sign): items keep their hypothetical sizes —
    ///   positive free space falls through to justify-content's
    ///   between-spacing, negative free space causes overflow per
    ///   justify-content's overflow semantics.</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>Min/max clamps deferred (L9+).</b> Per §9.7 step 4 the
    /// resolved sizes should clamp to <c>[min-main-size,
    /// max-main-size]</c> and the algorithm iterate to redistribute
    /// the clamped-off space. L8 Hello World skips clamping; min/max
    /// resolution is L9+ scope (depends on intrinsic sizing for
    /// <c>min-width: auto</c> / <c>min-height: auto</c>). Pinned by
    /// a known-gap test.</para>
    ///
    /// <para><b>Cancellation:</b> Checked at each line + each item to
    /// honor cancellation in hostile inputs with very many items.</para></summary>
    private double[] ResolveFlexibleMainSizes(
        List<FlexLine> lines,
        PropertyId mainSizeProperty,
        PropertyId minSizeProperty,
        PropertyId maxSizeProperty,
        double containerMainSize,
        // RC — the DEFINITE main size (NaN when the container's main axis is indefinite, e.g. an auto-height
        // COLUMN flex). Used ONLY to resolve a PERCENTAGE main / min / max-size (which computes to auto
        // against an indefinite reference); the flex free-space algorithm keeps the numeric containerMainSize.
        double containerDefiniteMainSize,
        double mainGap,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<Box, double>? precomputedIntrinsicBaseSizes = null)
    {
        var resolved = new double[_rootBox.Children.Count];

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveLineWithMinMaxClamping(
                line, resolved, mainSizeProperty,
                minSizeProperty, maxSizeProperty,
                containerMainSize, containerDefiniteMainSize, mainGap, cancellationToken,
                precomputedIntrinsicBaseSizes);
        }

        return resolved;
    }

    /// <summary>Per Phase 3 Task 15 L12 — runs the full CSS Flexbox L1
    /// §9.7 flexibility algorithm for one line, including step 4's
    /// min/max clamping iteration. Mutates <paramref name="resolved"/>
    /// in place (keyed by DOM-children index).
    ///
    /// <para><b>Algorithm</b> (§9.7 simplified):
    /// <list type="number">
    ///   <item>Compute each item's hypothetical main-size from its
    ///   flex-basis (delegate to <see cref="ResolveHypotheticalMainSize"/>).</item>
    ///   <item>Compute initial free-space = containerMainSize -
    ///   sum(hypothetical).</item>
    ///   <item>Distribute free-space among unfrozen items:
    ///     <list type="bullet">
    ///       <item>Positive free-space + sumFlexGrow > 0 → grow phase:
    ///       each item += (grow / max(sumFlexGrow, 1)) * freeSpace.</item>
    ///       <item>Negative free-space + sumScaledShrinks > 0 → shrink
    ///       phase: each item -= (scaledShrink / sumScaledShrinks) *
    ///       |freeSpace|.</item>
    ///     </list></item>
    ///   <item>Clamp each item to [min, max]. Items with violations
    ///   are FROZEN at their clamped value; the clamped-off space is
    ///   redistributed among unfrozen items in the next iteration.</item>
    ///   <item>Repeat steps 2-4 until no items have violations OR an
    ///   iteration cap is reached (= items per line, since each
    ///   iteration freezes at least one item).</item>
    /// </list></para>
    ///
    /// <para><b>Pre-L12 behavior</b> (= L8 Hello World): the
    /// distribution ran once + items were floored at 0 without honoring
    /// min/max. The L8 known-gap test
    /// (<c>L8_known_gap_min_width_does_not_clamp_resolved_size_yet</c>)
    /// pinned that incomplete behavior; L12 closes the deferral + flips
    /// the test to assert spec-correct clamping.</para>
    ///
    /// <para><b>Hello World scope:</b> only explicit pixel min/max
    /// values are honored. `min-width: auto` for flex items (spec-
    /// correct = intrinsic content size per CSS Sizing §5.5) is L13+
    /// scope pending intrinsic-sizing integration. Percentage min/max
    /// values are also L13+ scope.</para></summary>
    private void ResolveLineWithMinMaxClamping(
        FlexLine line,
        double[] resolved,
        PropertyId mainSizeProperty,
        PropertyId minSizeProperty,
        PropertyId maxSizeProperty,
        double containerMainSize,
        double containerDefiniteMainSize,
        double mainGap,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<Box, double>? precomputedIntrinsicBaseSizes = null)
    {
        // Resolve via the shared static §9.7 helper (position-keyed), then scatter
        // back to the DOM-children-keyed `resolved` array the emission loop reads.
        var count = line.ItemCount;
        var lineItems = new Box[count];
        for (var i = 0; i < count; i++)
            lineItems[i] = _rootBox.Children[_sortedFlexChildIndices[line.FirstItemIndex + i]];
        var lineResolved = ResolveFlexLineMainSizes(
            lineItems, mainSizeProperty, minSizeProperty, maxSizeProperty,
            containerMainSize, containerDefiniteMainSize, mainGap, cancellationToken, precomputedIntrinsicBaseSizes);
        for (var i = 0; i < count; i++)
            resolved[_sortedFlexChildIndices[line.FirstItemIndex + i]] = lineResolved[i];
    }

    /// <summary>Box-sizing / pre-measure share (PR #189 review P1) — the CSS
    /// Flexbox L1 §9.7 flexibility algorithm (incl. the step-4 min/max clamping
    /// iteration) for ONE line, keyed by POSITION within <paramref name="lineItems"/>
    /// (not DOM index). The resolved main size per item is ORDER-INDEPENDENT (the
    /// distribution is by flex-grow/shrink ratios, not source order), so callers may
    /// pass items in any order. The instance
    /// <see cref="ResolveLineWithMinMaxClamping"/> wraps this for the DOM-keyed
    /// emission array; the BlockLayouter row-flex pre-measure calls it DIRECTLY so an
    /// auto-height row item is measured at the SAME flex-resolved width FlexLayouter
    /// will emit it at (a pre-measure at the declared/container width would under- or
    /// over-count wrapped height + mis-trigger pagination).</summary>
    internal static double[] ResolveFlexLineMainSizes(
        IReadOnlyList<Box> lineItems,
        PropertyId mainSizeProperty,
        PropertyId minSizeProperty,
        PropertyId maxSizeProperty,
        double containerMainSize,
        // RC — the DEFINITE main size (NaN when the container's main axis is indefinite, e.g. an auto-height
        // COLUMN flex) for resolving a PERCENTAGE main / min / max-size to auto against an indefinite
        // reference. Defaults to containerMainSize so existing callers that pass a definite size are
        // unchanged; the auto-height column case passes NaN.
        double containerDefiniteMainSize,
        double mainGap,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<Box, double>? precomputedIntrinsicBaseSizes = null)
    {
        var itemCount = lineItems.Count;
        var resolved = new double[itemCount];

        // CSS Box Alignment L3 §8 — the N-1 main-axis gutters consume free space
        // BEFORE flex grow/shrink distributes the remainder (PR #204 review [P1]).
        // The gutter total is removed from the distributable free space only; a
        // percentage flex-basis still resolves against the FULL container main size
        // (ResolveHypotheticalMainSize below is unchanged), exactly mirroring the
        // emission site where mainGap is subtracted before justify-content
        // distributes (AttemptLayout ~L1224). Without this, `gap` + flex-grow/shrink
        // sized items as if the gap didn't exist, then still inserted the gap at
        // emission → overflow / wrong widths.
        var mainGutterTotal = System.Math.Max(0, itemCount - 1) * mainGap;

        // Stack-friendly per-item state: hypothetical + (min, max) +
        // frozen flag, keyed by sorted-position within the line.
        // Heap-allocated arrays of length itemCount; pool/Span refactor
        // is L13+ scope.
        var hypotheticals = new double[itemCount];
        var mins = new double[itemCount];
        var maxs = new double[itemCount];
        var frozen = new bool[itemCount];

        // Pass 1 — read hypothetical + min/max per item. The hypothetical
        // is also the INITIAL resolved value (= the §9.7 starting point).
        // All arrays here are POSITION-keyed (0..itemCount within the line).
        for (var i = 0; i < itemCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = lineItems[i];

            var hypothetical = ResolveHypotheticalMainSize(
                item, mainSizeProperty, containerDefiniteMainSize, precomputedIntrinsicBaseSizes);
            hypotheticals[i] = hypothetical;
            resolved[i] = hypothetical;

            // Pass the DEFINITE container main size so a PERCENTAGE main min/max-* resolves against it —
            // and computes to auto (0) against an INDEFINITE reference (auto-height column flex), mirroring
            // the hypothetical main-size resolution above. The numeric containerMainSize is still used for
            // the free-space distribution below.
            var (min, max) = item.ResolveFlexItemMinMaxMainSize(
                minSizeProperty, maxSizeProperty, containerDefiniteMainSize);
            mins[i] = min;
            maxs[i] = max;
        }

        // §9.7 iteration cap: each iteration freezes at least one item
        // (when a violation occurs), so the loop converges in at most
        // itemCount iterations. The +1 covers the no-violation final
        // iteration that just confirms convergence.
        for (var iter = 0; iter <= itemCount; iter++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Compute remaining free-space: containerMainSize minus
            // frozen-items' resolved sizes minus unfrozen-items'
            // hypothetical sizes (the unfrozen items' resolved values
            // are recomputed below).
            var sumFrozenResolved = 0.0;
            var sumUnfrozenHypothetical = 0.0;
            var sumFlexGrow = 0.0;
            var sumScaledShrinks = 0.0;
            for (var i = 0; i < itemCount; i++)
            {
                var item = lineItems[i];
                if (frozen[i])
                {
                    sumFrozenResolved += resolved[i];
                }
                else
                {
                    sumUnfrozenHypothetical += hypotheticals[i];
                    sumFlexGrow += item.Style.ReadFlexGrow();
                    sumScaledShrinks += item.Style.ReadFlexShrink() * hypotheticals[i];
                    // Reset unfrozen items to hypothetical before the
                    // distribution pass — each iteration redistributes
                    // the (smaller) remaining free-space from scratch.
                    resolved[i] = hypotheticals[i];
                }
            }

            // CSS Flexbox L1 §9.7 — flex-grow/shrink distribute the container's FREE SPACE, which only
            // exists when the container's main size is DEFINITE. For an INDEFINITE main size (an
            // auto-height COLUMN flex — content-sized), there is no free space to distribute: the used main
            // size IS the sum of the items, so free space = 0 and the items stay at their hypothetical
            // (base) sizes. Without this gate, a `flex-grow` item in an auto-height column flex that is
            // being MEASURED (as a row-flex item, at the ~1,000,000px unbounded budget) grows to fill the
            // budget → a ~1M-tall item → thousands of blank pages (10-event-ticket / 02-travel-quote).
            var remainingFreeSpace = double.IsFinite(containerDefiniteMainSize)
                ? containerMainSize - mainGutterTotal - sumFrozenResolved - sumUnfrozenHypothetical
                : 0.0;

            // Distribute remainingFreeSpace among unfrozen items.
            if (remainingFreeSpace > 0 && sumFlexGrow > 0)
            {
                // Per Phase 3 Task 15 L8 post-PR-#68 hardening F#2: when
                // sum < 1 the items take only that fraction of free
                // space; the remainder stays for justify-content.
                var growDivisor = sumFlexGrow >= 1 ? sumFlexGrow : 1.0;
                for (var i = 0; i < itemCount; i++)
                {
                    if (frozen[i]) continue;
                    var grow = lineItems[i].Style.ReadFlexGrow();
                    if (grow > 0)
                    {
                        resolved[i] += (grow / growDivisor) * remainingFreeSpace;
                    }
                }
            }
            else if (remainingFreeSpace < 0 && sumScaledShrinks > 0)
            {
                var deficit = -remainingFreeSpace;
                for (var i = 0; i < itemCount; i++)
                {
                    if (frozen[i]) continue;
                    var shrink = lineItems[i].Style.ReadFlexShrink();
                    if (shrink > 0)
                    {
                        var scaledShrink = shrink * hypotheticals[i];
                        var absorb = (scaledShrink / sumScaledShrinks) * deficit;
                        resolved[i] = Math.Max(0, resolved[i] - absorb);
                    }
                }
            }
            // else: items keep their hypothetical sizes (= no
            // distribution this iteration).

            // §9.7 step 4 — clamp each unfrozen item + freeze those
            // with violations. Track totalViolation per spec:
            //   positive → items were forced UP (min violations)
            //   negative → items were forced DOWN (max violations)
            //   zero → no violations, algorithm terminates.
            var totalViolation = 0.0;
            for (var i = 0; i < itemCount; i++)
            {
                if (frozen[i]) continue;
                var pre = resolved[i];
                var post = Math.Max(mins[i], Math.Min(maxs[i], pre));
                if (post != pre)
                {
                    resolved[i] = post;
                    totalViolation += post - pre;
                }
            }

            if (totalViolation == 0)
            {
                // Convergence — no violations OR all items already at
                // their clamped values. Algorithm terminates.
                break;
            }

            // Freeze items whose violation matches the totalViolation
            // sign: when totalViolation > 0 freeze min-violators (=
            // items clamped UP), and vice versa. After freezing, loop
            // re-enters with the remaining free-space recomputed.
            var freezeMinViolators = totalViolation > 0;
            for (var i = 0; i < itemCount; i++)
            {
                if (frozen[i]) continue;
                var pre = hypotheticals[i];
                var post = resolved[i];
                if (freezeMinViolators ? (post > pre && post == mins[i])
                    : (post < pre && post == maxs[i]))
                {
                    frozen[i] = true;
                }
                else if (post == mins[i] && mins[i] > 0 && freezeMinViolators)
                {
                    // Defensive: items already at their min that were
                    // distributed (= reset to hypothetical) and clamped
                    // back to min freeze here.
                    frozen[i] = true;
                }
                else if (post == maxs[i] && !double.IsPositiveInfinity(maxs[i]) && !freezeMinViolators)
                {
                    frozen[i] = true;
                }
            }
        }
        return resolved;
    }

    /// <summary>Flex intrinsic-basis cycle (CSS Flexbox L1 §7.2 + §9.2.3 + CSS Sizing L3
    /// §5.1) — for a single-line (nowrap) ROW flex container, measure the intrinsic main
    /// (inline) base size of every item with an EXPLICIT intrinsic <c>flex-basis</c>
    /// (<c>max-content</c> / <c>min-content</c> / <c>content</c>) and return a map from
    /// item box to its BORDER-box base size; <see langword="null"/> when no shaper is
    /// available or no item qualifies.
    ///
    /// <para><b>Why a shared static helper.</b> The base size feeds the §9.7 flexibility
    /// resolution, which the FlexLayouter emission AND the
    /// <c>BlockLayouter.PreMeasureFlexCrossExtent</c> row-flex height pre-measure BOTH
    /// run through <see cref="ResolveFlexLineMainSizes"/>. Building the map HERE (one
    /// place, called from both sites with the same items + shaper) guarantees the
    /// pre-measure resolves each item at the SAME flex-resolved width the emission uses,
    /// so the wrapper height + the row-nowrap pagination gate can't desync.</para>
    ///
    /// <para><b>Scope.</b> Only the EXPLICIT intrinsic keywords trigger a measurement
    /// (<c>content</c> ≡ max-content per §9.2.3); <c>auto</c> keeps the declared-size
    /// path, so existing auto-basis layouts stay byte-identical. Restricted to nowrap
    /// rows by the callers — wrap line-breaking depends on the base size in the
    /// (shaper-less) multi-line pre-measure, so it stays a documented residual. The
    /// max-content measure lays the content out with no wrap pressure
    /// (<see cref="MaxContentMeasureInlinePx"/>); min-content at minimal width so the
    /// widest line is the longest unbreakable run.</para></summary>
    internal static IReadOnlyDictionary<Box, double>? BuildRowIntrinsicMainBaseSizes(
        IReadOnlyList<Box> blockLevelItems,
        IShaperResolver? shaperResolver,
        CancellationToken cancellationToken)
    {
        if (shaperResolver is null) return null;
        Dictionary<Box, double>? map = null;
        for (var i = 0; i < blockLevelItems.Count; i++)
        {
            var item = blockLevelItems[i];
            var basisKind = item.Style.ReadFlexBasis().Kind;
            // content ≡ max-content per §9.2.3; the explicit max-content/min-content
            // keywords map to their own measure. Length / percentage bases are NOT here
            // (they resolve to a definite base in ResolveHypotheticalMainSize).
            var isMinContent = basisKind == FlexBasisKind.MinContent;
            // CSS Flexbox §7.2.3: `flex-basis: auto` retrieves the main-size property; when
            // that is ALSO auto (Unset / a keyword like `auto`), it falls through to `content`
            // (= max-content). Pre-fix such items measured as ZERO (they read `width` = 0),
            // which collapsed a row's items and made justify-content:space-between distribute
            // the whole container width as the gap → the last item overflowed the container
            // (the "Bill to column runs off the page" invoice bug). Treat auto+auto as
            // max-content so the item gets its real content base size.
            var autoResolvesToContent = basisKind == FlexBasisKind.Auto
                && item.Style.Get(PropertyId.Width).Tag is ComputedSlotTag.Unset or ComputedSlotTag.Keyword;
            var isMaxContent = basisKind is FlexBasisKind.MaxContent or FlexBasisKind.Content
                || autoResolvesToContent;
            if (!isMinContent && !isMaxContent) continue;

            var availInline = isMinContent ? 1.0 : MaxContentMeasureInlinePx;
            var buffer = NestedContentMeasurer.Measure(
                item, availInline,
                blockBudget: NestedContentMeasurer.EffectivelyUnboundedBlockBudgetPx,
                shaperResolver: shaperResolver,
                writingMode: WritingMode.HorizontalTb, isRtl: false,
                cancellationToken: cancellationToken,
                // [P2] min-content ignores break-word's soft opportunities (CSS Text L3
                // §5.1) so a `break-word` item isn't collapsed to glyph width; max-content
                // has no wrap pressure so the flag is moot there.
                intrinsicSizingMode: isMinContent);
            // ContentInlineExtent is the widest shaped LINE advance (the natural content
            // width). The flex base size is a BORDER box → add the item's inline chrome.
            var borderBox = buffer.ContentInlineExtent
                + item.Style.AxisBorderPaddingPx(PropertyId.Width);
            (map ??= new Dictionary<Box, double>(ReferenceEqualityComparer.Instance))[item] = borderBox;
        }
        return map;
    }

    /// <summary>Flex intrinsic-basis cycle — the available inline size the max-content
    /// pre-measure lays content out at: large enough that text never wraps, so the
    /// widest line advance equals the content's natural (max-content) width. Mirrors the
    /// table/grid max-content idiom (<c>1e6</c>).</summary>
    private const double MaxContentMeasureInlinePx = 1_000_000.0;

    /// <summary>Per Phase 3 Task 15 L8 + post-PR-#68 hardening F#1 —
    /// compute one flex item's hypothetical main-size. Delegates to the
    /// shared <see cref="ComputedStyleLayoutExtensions.ResolveFlexItemHypotheticalMainSize"/>
    /// extension so the line-packing pass (<see cref="PackLines"/>) and
    /// the flexibility-resolution pass
    /// (<see cref="ResolveFlexibleMainSizes"/>) AND the BlockLayouter
    /// pre-measure all use IDENTICAL sizing. Pre-PR-#68 the line-
    /// packing pass used <c>ReadLengthPxOrZero</c> while the
    /// flexibility-resolution pass used a private helper here that
    /// honored flex-basis — line boundaries could drift from the
    /// post-flex layout when flex-basis differed from the declared
    /// main-size (e.g., <c>flex-basis: 0</c> with three items in 300px
    /// = one line of three post-fix, three lines pre-fix).</summary>
    private static double ResolveHypotheticalMainSize(
        Box item,
        PropertyId mainSizeProperty,
        double containerMainSize,
        IReadOnlyDictionary<Box, double>? precomputedIntrinsicBaseSizes = null) =>
        item.ResolveFlexItemHypotheticalMainSize(
            mainSizeProperty, containerMainSize, precomputedIntrinsicBaseSizes);

    // Per Phase 3 Task 16 cycle 4c (P3 #8) — <c>FlexLine</c> promoted
    // to an internal type in <see cref="FlexLinePacker"/> + the
    // packing algorithm moved out so BlockLayouter's pre-measure
    // shares the same packer. The previously-private nested struct
    // is replaced by <see cref="FlexLine"/> at the namespace level;
    // FlexLayouter consumes it via the shared
    // <see cref="FlexLinePacker.Pack"/> entry point.

    /// <summary>Per Phase 3 Task 15 L2 — compute the start-offset +
    /// between-spacing for the main-axis cursor per CSS Box Alignment
    /// L3 §4.5 + §5.3. The returned tuple is consumed by
    /// AttemptLayout's row-packing loop:
    /// <c>cursor = contentInlineOffset + startOffset</c> before the
    /// first item, then <c>cursor += itemInlineSize + betweenSpacing</c>
    /// after each emission.
    ///
    /// <para>Per Phase 3 Task 15 L2 post-PR-#62 review hardening F#2 —
    /// overflow handling now follows CSS Box Alignment L3 §5.3 instead
    /// of a blanket flex-start fallback. The pre-fix all-modes
    /// collapse-to-zero behavior was wrong for two reasons: (a)
    /// positional values (flex-start / flex-end / center) on overflow
    /// should keep their natural offset — <c>center</c> with negative
    /// free-space puts items at <c>freeSpace/2</c> (a negative value)
    /// so items overflow EQUALLY on both sides; (b) the explicit
    /// <c>safe</c> / <c>unsafe</c> modifiers were ignored entirely.
    /// The new behavior:</para>
    /// <list type="bullet">
    ///   <item><b>Distribution values</b> (space-between /
    ///   space-around / space-evenly) on overflow always fall back to
    ///   safe start (= return <c>(0, 0)</c>). The spec admits subtle
    ///   per-value differences (some readings say space-around /
    ///   space-evenly fall back to safe center) but Hello World scope
    ///   treats all distribution-value overflows as safe start — the
    ///   consistent fallback that all three share.</item>
    ///   <item><b>Positional values</b> (flex-start / flex-end /
    ///   center) on overflow keep their natural offset (which may be
    ///   negative — e.g., center with negative free-space yields
    ///   <c>freeSpace/2</c>).</item>
    ///   <item><b><c>safe</c> modifier</b> (= <see cref="OverflowAlignmentMode.Safe"/>)
    ///   on overflow forces safe-start fallback for ALL values; on
    ///   non-overflow it's transparent (= behaves like
    ///   <see cref="OverflowAlignmentMode.Default"/>).</item>
    ///   <item><b><c>unsafe</c> modifier</b> (= <see cref="OverflowAlignmentMode.Unsafe"/>)
    ///   on overflow honors the natural offset regardless of value
    ///   family; positional values overflow per their natural offset
    ///   + distribution values produce their (now-negative) gap math.</item>
    /// </list>
    ///
    /// <para><b>N == 1 special case.</b> <c>space-between</c> with a
    /// single item has no "between" gaps to distribute, so it falls
    /// back to flex-start per spec. <c>space-around</c> +
    /// <c>space-evenly</c> with N=1 naturally degenerate to center
    /// (the formulas below produce <c>startOffset = freeSpace / 2</c>
    /// for both, which is correct).</para>
    ///
    /// <para><b>N == 0 special case.</b> Empty flex container — the
    /// alignment math is moot since there are no items to emit. The
    /// caller's row-packing loop won't execute, but the return values
    /// are still defined (zeros) so the caller doesn't need a special
    /// case.</para></summary>
    private static (double startOffset, double betweenSpacing) ComputeJustifyContentOffsets(
        JustifyContentValue value,
        OverflowAlignmentMode mode,
        double freeSpace,
        int itemCount)
    {
        if (itemCount == 0) return (0, 0);

        // Compute the NATURAL offset for the value (ignoring overflow
        // for now). This is the spec-defined offset assuming free space
        // exists; the overflow branch below either preserves it (for
        // positional values + the unsafe modifier) or replaces it with
        // safe-start fallback.
        var natural = value switch
        {
            JustifyContentValue.FlexEnd => (freeSpace, 0.0),
            JustifyContentValue.Center => (freeSpace / 2.0, 0.0),
            JustifyContentValue.SpaceBetween => itemCount >= 2
                ? (0.0, freeSpace / (itemCount - 1))
                : (0.0, 0.0), // N=1 → flex-start fallback per spec
            JustifyContentValue.SpaceAround => (
                freeSpace / (2.0 * itemCount),
                freeSpace / itemCount),
            JustifyContentValue.SpaceEvenly => (
                freeSpace / (itemCount + 1),
                freeSpace / (itemCount + 1)),
            _ => (0.0, 0.0), // FlexStart
        };

        // F#2 hardening — overflow handling per CSS Box Alignment L3
        // §5.3. Only the `freeSpace < 0` branch applies the overflow
        // rules; `freeSpace == 0` falls through to the natural return
        // (which is (0, 0) for all values anyway, since every formula
        // multiplies by freeSpace).
        if (freeSpace < 0)
        {
            // Explicit `safe` modifier — always fall back to safe start
            // regardless of value family. Items pack at the container's
            // start edge + overflow off the end.
            if (mode == OverflowAlignmentMode.Safe) return (0, 0);
            // Explicit `unsafe` modifier — honor the natural offset
            // (= the value's overflow behavior is opt-in even for
            // distribution values; the resulting between-spacing may
            // be negative, causing items to overlap).
            if (mode == OverflowAlignmentMode.Unsafe) return natural;
            // Default mode (no overflow modifier) — distribution values
            // fall back to safe start; positional values keep their
            // natural (possibly-negative) offset, allowing items to
            // overflow equally on both sides for `center` etc.
            return value switch
            {
                JustifyContentValue.SpaceBetween or
                JustifyContentValue.SpaceAround or
                JustifyContentValue.SpaceEvenly => (0, 0),
                _ => natural, // positional values — allow overflow
            };
        }

        return natural;
    }

    /// <summary>Per Phase 3 Task 15 L7 — compute the cross-axis line
    /// distribution offsets for <c>align-content</c> per CSS Box
    /// Alignment L3 §6 + CSS Flexbox L1 §8.4. Returns the triple
    /// <c>(startOffset, betweenSpacing, stretchAddend)</c> consumed by
    /// AttemptLayout's per-line emission loop:
    /// <list type="bullet">
    ///   <item><b>startOffset</b> — where the FIRST line begins,
    ///   relative to the container's content cross-start. Positional
    ///   values (<c>flex-end</c>, <c>center</c>) push the first line
    ///   away from cross-start; distribution values
    ///   (<c>space-around</c>, <c>space-evenly</c>) absorb a leading
    ///   half-gap (or full gap) into the first line's offset.</item>
    ///   <item><b>betweenSpacing</b> — extra space added BETWEEN
    ///   adjacent lines on the cross axis. Non-zero only for the three
    ///   distribution values (<c>space-between</c> / -around / -evenly).
    ///   Positional values fold their entire offset into startOffset.</item>
    ///   <item><b>stretchAddend</b> — extra cross-extent added to EACH
    ///   line for <c>align-content: stretch</c> (= the spec default per
    ///   §8.4 for the initial <c>normal</c> value). The caller mutates
    ///   each line's <c>LineCrossSize</c> by this amount BEFORE the
    ///   emission loop so the per-line align-items math sees the
    ///   stretched extent.</item>
    /// </list>
    ///
    /// <para><b>Single-line gate (Phase 3 Task 15 L7 post-PR-#67 F#1).</b>
    /// Per CSS Flexbox L1 §9.4 a flex container is "single-line" iff
    /// <c>flex-wrap: nowrap</c> — NOT iff <c>flex-wrap: wrap</c> produced
    /// a single line. align-content has no effect on a true single-line
    /// container per §8.4 (nowrap forces the line cross-size to the
    /// container's cross-size, leaving no space to distribute) but a
    /// wrapping container that just happens to fit on one line is still
    /// a multi-line container — the default <c>normal → stretch</c>
    /// still stretches it, <c>align-content: center</c> still centers
    /// it, etc. The helper short-circuits when EITHER <c>lineCount ==
    /// 0</c> (no lines) OR <c>!isWrapping</c> (= nowrap; spec says no
    /// effect).</para>
    ///
    /// <para><b>Overflow handling (Phase 3 Task 15 L7 post-PR-#67 F#2).</b>
    /// Mirrors the L2 pattern in <see cref="ComputeJustifyContentOffsets"/>
    /// per CSS Box Alignment L3 §5.3:
    /// <list type="bullet">
    ///   <item>Default mode + positional values
    ///   (<see cref="AlignContentValue.FlexStart"/> /
    ///   <see cref="AlignContentValue.FlexEnd"/> /
    ///   <see cref="AlignContentValue.Center"/>) keep their natural
    ///   (possibly-negative) offset on overflow — lines overflow
    ///   equally on both sides for <c>center</c> etc.</item>
    ///   <item>Default mode + distribution values
    ///   (<see cref="AlignContentValue.SpaceBetween"/> /
    ///   <see cref="AlignContentValue.SpaceAround"/> /
    ///   <see cref="AlignContentValue.SpaceEvenly"/>) fall back to
    ///   safe-start on overflow per §5.3.</item>
    ///   <item>Default mode + <see cref="AlignContentValue.Stretch"/>
    ///   on overflow returns no growth (a negative stretchAddend would
    ///   SHRINK lines, which the spec forbids — stretch never reduces
    ///   the cross-size).</item>
    ///   <item><see cref="OverflowAlignmentMode.Safe"/> on overflow —
    ///   always fall back to safe-start regardless of value family.</item>
    ///   <item><see cref="OverflowAlignmentMode.Unsafe"/> on overflow —
    ///   honor the natural offset even if negative (= author opt-in to
    ///   overflow behavior).</item>
    /// </list></para>
    ///
    /// <para><b>Stretch arithmetic.</b> For <c>align-content: stretch</c>
    /// with N lines and positive freeCrossSpace F, each line grows by
    /// F/N (= equal share). This is the CSS Flexbox §8.4-defined
    /// "increase each line's cross-size by an equal portion of the
    /// container's free cross-space" rule, simplified by Hello-World's
    /// natural-size item model (no min-content / max-content
    /// constraints folded in yet — that's L8+ scope when the full
    /// hypothetical-cross-size math lands).</para>
    ///
    /// <para><b>N == 1 distribution edge cases.</b> <c>space-between</c>
    /// with 1 line has no "between" gap to distribute, so it falls
    /// back to <c>flex-start</c> (= natural <c>(0, 0, 0)</c>) per spec.
    /// <c>space-around</c> + <c>space-evenly</c> with N=1 naturally
    /// degenerate to center (formulas produce a leading half-gap or
    /// full gap that places the single line in the middle — which is
    /// the spec-correct behavior).</para></summary>
    private static (double startOffset, double betweenSpacing, double stretchAddend)
        ComputeAlignContentOffsets(
            ResolvedAlignContent resolved,
            double freeCrossSpace,
            int lineCount,
            bool isWrapping,
            bool isWrapReverse)
    {
        // F#1 — short-circuit only when no lines OR nowrap. A wrapping
        // container with one produced line still gets align-content
        // applied per §9.4 (the line is a real multi-line line whose
        // cross-size is the max item cross-size, NOT the container's
        // cross-size — so there's space to distribute / stretch).
        if (lineCount == 0 || !isWrapping)
        {
            return (0, 0, 0);
        }

        // F#10 — NaN/Infinity sanity check. Box/extent measurement
        // upstream can theoretically yield non-finite values
        // (e.g., infinity propagation from a parent's content extent).
        // Fall back to safe-start so the layout completes deterministically
        // rather than emitting NaN-tainted offsets.
        if (!double.IsFinite(freeCrossSpace))
        {
            return (0, 0, 0);
        }

        // PR #221 review [P1] — baseline content-alignment doesn't apply to a flex container's lines
        // (CSS Box Alignment §5.3 — they're not a baseline-sharing group), so FirstBaseline / LastBaseline
        // fall back HERE to LOGICAL safe start / safe end. Logical start/end is the writing-mode cross
        // edge, which — unlike flex-start/flex-end — does NOT follow the flex-flow `wrap-reverse` reversal:
        // under wrap-reverse the logical start IS the flex-flow END, so pre-invert the mapping. (The
        // column-RTL cross flip is already applied downstream by CrossAxisFlow.PhysicalLineOffset via
        // isCrossAxisReversed, so it must NOT be folded in here — only wrap-reverse distinguishes logical
        // from flex-flow on this axis.)
        var effectiveValue = resolved.Value switch
        {
            AlignContentValue.FirstBaseline => isWrapReverse ? AlignContentValue.FlexEnd : AlignContentValue.FlexStart,
            AlignContentValue.LastBaseline => isWrapReverse ? AlignContentValue.FlexStart : AlignContentValue.FlexEnd,
            _ => resolved.Value,
        };

        // Compute the NATURAL offset for the value (ignoring overflow
        // for now). This is the spec-defined offset assuming positive
        // free space exists; the overflow branch below either preserves
        // it (positional values + the unsafe modifier) or replaces it
        // with safe-start fallback (distribution values + stretch +
        // the safe modifier).
        var natural = effectiveValue switch
        {
            AlignContentValue.FlexEnd => (freeCrossSpace, 0.0, 0.0),
            AlignContentValue.Center => (freeCrossSpace / 2.0, 0.0, 0.0),
            AlignContentValue.SpaceBetween => lineCount >= 2
                ? (0.0, freeCrossSpace / (lineCount - 1), 0.0)
                : (0.0, 0.0, 0.0), // N=1 → flex-start fallback per spec
            AlignContentValue.SpaceAround =>
                (freeCrossSpace / (2.0 * lineCount), freeCrossSpace / lineCount, 0.0),
            AlignContentValue.SpaceEvenly =>
                (freeCrossSpace / (lineCount + 1), freeCrossSpace / (lineCount + 1), 0.0),
            AlignContentValue.Stretch =>
                (0.0, 0.0, freeCrossSpace / lineCount),
            _ => (0.0, 0.0, 0.0), // FlexStart + unknown
        };

        // F#2 — overflow handling per CSS Box Alignment L3 §5.3. Only
        // the `freeCrossSpace < 0` branch applies overflow rules;
        // `freeCrossSpace == 0` falls through to natural (which is
        // (0, 0, 0) for all values anyway since every formula
        // multiplies by freeCrossSpace).
        if (freeCrossSpace < 0)
        {
            // Explicit `safe` modifier — always fall back to safe start
            // regardless of value family. Lines stack at cross-start.
            if (resolved.Mode == OverflowAlignmentMode.Safe) return (0, 0, 0);
            // Explicit `unsafe` modifier — honor the natural offset
            // (= author opt-in to overflow even for distribution
            // values; the resulting between-spacing may be negative,
            // causing lines to overlap).
            if (resolved.Mode == OverflowAlignmentMode.Unsafe) return natural;
            // Default mode (no overflow modifier) — distribution
            // values + stretch fall back to safe start; positional
            // values keep their natural (possibly-negative) offset.
            // Stretch falls back because a negative stretchAddend would
            // shrink lines, which the spec forbids.
            return effectiveValue switch
            {
                AlignContentValue.SpaceBetween or
                AlignContentValue.SpaceAround or
                AlignContentValue.SpaceEvenly or
                AlignContentValue.Stretch => (0, 0, 0),
                _ => natural, // FlexStart / FlexEnd / Center (+ resolved baseline) — allow overflow
            };
        }

        return natural;
    }

    /// <summary>Per Phase 3 Task 15 L3 + Phase 3 Task 15 L4 — compute
    /// the per-item cross-axis placement (cross-axis offset + effective
    /// cross-axis size) per CSS Box Alignment L3 §6 + CSS Flexbox L1
    /// §8.3. The returned tuple is consumed by AttemptLayout's
    /// per-item emission loop and is mapped to the appropriate
    /// physical axis based on <c>flex-direction</c>:
    /// <list type="bullet">
    ///   <item><c>row</c> direction — cross axis = block axis, so the
    ///   returned <c>itemCrossOffset</c> becomes the
    ///   <c>BoxFragment.BlockOffset</c> + <c>itemEffectiveCrossSize</c>
    ///   becomes the <c>BoxFragment.BlockSize</c>.</item>
    ///   <item><c>column</c> direction (Phase 3 Task 15 L4) — cross
    ///   axis = inline axis, so the returned <c>itemCrossOffset</c>
    ///   becomes the <c>BoxFragment.InlineOffset</c> +
    ///   <c>itemEffectiveCrossSize</c> becomes the
    ///   <c>BoxFragment.InlineSize</c>.</item>
    /// </list>
    /// The placement math itself is direction-agnostic: it operates on
    /// abstract "cross-axis space" + the spec's positional alignment
    /// formulas. Only the caller mapping at the emission site differs.
    ///
    /// <para><b>Stretch.</b> Per CSS Flexbox L1 §8.3 + §7.2, when the
    /// container's <c>align-items</c> resolves to <c>stretch</c> (= the
    /// computed default for <c>normal</c>), an item's cross-axis size
    /// is resized to fill the container's cross extent IF the item's
    /// own cross-size property computes to <c>auto</c>. The
    /// implementation reads the slot type via
    /// <see cref="IsCrossSizeAuto(Box, FlexDirectionValue)"/> — Unset /
    /// Keyword (= explicit <c>auto</c>) slots stretch; LengthPx slots
    /// (whether 0 or positive) keep their declared cross-size. Per
    /// Phase 3 Task 15 L3 post-PR-#63 hardening F#2, this replaces the
    /// pre-fix proxy of <c>itemCrossSize &gt; 0</c> which incorrectly
    /// stretched explicit <c>height: 0</c> spacers to the full
    /// container cross extent. The cross-axis offset for stretch is
    /// always <paramref name="contentCrossOffset"/> (= cross-start;
    /// stretch by definition fills the container so the item starts at
    /// the cross-start edge).</para>
    ///
    /// <para><b>Positional alignment.</b> For <see cref="AlignItemsValue.FlexStart"/>,
    /// <see cref="AlignItemsValue.FlexEnd"/>, and <see cref="AlignItemsValue.Center"/>
    /// the cross-axis space is <c>containerCrossSize - itemCrossSize</c>;
    /// the natural offset is then <c>contentCrossOffset</c> (start),
    /// <c>contentCrossOffset + crossSpace</c> (end), or
    /// <c>contentCrossOffset + crossSpace / 2</c> (center). Items keep
    /// their declared cross-size — positional alignment never resizes,
    /// it only positions.</para>
    ///
    /// <para><b>Overflow handling per CSS Box Alignment L3 §5.3.</b>
    /// Mirrors the L2 pattern in <see cref="ComputeJustifyContentOffsets"/>:
    /// <list type="bullet">
    ///   <item><see cref="OverflowAlignmentMode.Safe"/> on overflow
    ///   (= negative crossSpace) — fall back to safe-start (=
    ///   <paramref name="contentCrossOffset"/>) regardless of value.
    ///   Items pack at the container's cross-start edge + overflow off
    ///   the cross-end. On non-overflow the modifier is transparent.</item>
    ///   <item><see cref="OverflowAlignmentMode.Unsafe"/> on overflow —
    ///   honor the natural offset even if negative; items may be
    ///   pushed past the container's start edge.</item>
    ///   <item><see cref="OverflowAlignmentMode.Default"/> on overflow
    ///   — positional values keep their natural (possibly-negative)
    ///   offset, allowing items to overflow EQUALLY on both sides for
    ///   <c>center</c> etc.</item>
    /// </list></para>
    ///
    /// <para><b>Stretch is overflow-immune.</b> The stretch branch
    /// returns BEFORE the overflow check: stretch by definition resizes
    /// the item to fit the container, so crossSpace is always non-
    /// negative AT the emission point (= the item's effective cross-
    /// size IS the containerCrossSize when auto).</para></summary>
    private static (double itemCrossOffset, double itemEffectiveCrossSize)
        ComputeAlignItemsPlacement(
            AlignItemsValue value,
            OverflowAlignmentMode mode,
            double containerCrossSize,
            double itemCrossSize,
            bool itemIsCrossSizeAuto,
            double contentCrossOffset,
            // PR #217 review [P2] — two SEPARATE orientations. `naturalCrossReversed` is the keyword's
            // own reference (flex-flow / container-logical / item-logical) and drives the natural
            // positional placement; `safeFallbackReversed` is the flex-flow cross-start the `safe`
            // overflow fallback anchors to (CSS Box Alignment L3 §5.3). They differ for the logical
            // `start`/`end`/`self-*` keywords under `wrap-reverse` / column-rtl.
            bool naturalCrossReversed = false,
            bool safeFallbackReversed = false)
    {
        // Stretch — auto-cross-sized items get resized to fill the
        // container's cross extent; explicitly-sized items keep their
        // declared size per CSS Flexbox L1 §7.2. Per post-PR-#63 F#2
        // we test the SLOT TYPE (via `itemIsCrossSizeAuto`) rather
        // than `itemCrossSize > 0`: an explicit cross-size of 0 (= a
        // LengthPx slot with payload 0) is NOT auto and must keep its
        // declared 0 cross-size (e.g., a deliberate spacer). Stretch
        // is direction-agnostic — the item fills the line regardless
        // of wrap-reverse.
        if (value == AlignItemsValue.Stretch)
        {
            var effectiveCross = itemIsCrossSizeAuto ? containerCrossSize : itemCrossSize;
            return (contentCrossOffset, effectiveCross);
        }

        // Per Phase 3 Task 15 L11 post-PR-#71 F#2 — under wrap-reverse
        // the LINE's cross axis is swapped: FlexStart means the line's
        // NEW cross-start (= physical-BOTTOM edge of the line for row
        // + horizontal-tb LTR), FlexEnd means the new cross-end
        // (= physical-TOP edge of the line). The math swaps the
        // FlexStart ↔ FlexEnd offsets:
        //   wrap (default):  FlexStart → contentCrossOffset (top)
        //                    FlexEnd   → contentCrossOffset + crossSpace (bottom)
        //   wrap-reverse:    FlexStart → contentCrossOffset + crossSpace (bottom)
        //                    FlexEnd   → contentCrossOffset (top)
        // Center is symmetric → unaffected. Stretch (above) is
        // unaffected. The container's PHYSICAL-TOP edge of the line
        // (`contentCrossOffset`) stays the same — only the FlexStart
        // / FlexEnd anchor flips.
        var crossSpace = containerCrossSize - itemCrossSize;
        var natural = (value, naturalCrossReversed) switch
        {
            (AlignItemsValue.FlexEnd, false) => contentCrossOffset + crossSpace,
            (AlignItemsValue.FlexEnd, true) => contentCrossOffset,
            (AlignItemsValue.Center, _) => contentCrossOffset + crossSpace / 2.0,
            (AlignItemsValue.FlexStart, false) => contentCrossOffset,
            (AlignItemsValue.FlexStart, true) => contentCrossOffset + crossSpace,
            // Baseline reaches this helper for a COLUMN container (no first baseline on
            // the inline cross axis without vertical text) OR a wrap-reverse ROW (the
            // mirrored shift is a documented residual). Either way it behaves as
            // flex-start per CSS Box Alignment L3 §9.3's fallback to `start` — which under
            // wrap-reverse anchors to the line's PHYSICAL cross-end (PR #208 Copilot review:
            // mirror FlexStart's swap so the fallback matches the stated behavior).
            (AlignItemsValue.Baseline, false) => contentCrossOffset,
            (AlignItemsValue.Baseline, true) => contentCrossOffset + crossSpace,
            _ => contentCrossOffset, // defensive — unknown value
        };

        // Overflow handling per CSS Box Alignment L3 §5.3. Only the
        // `crossSpace < 0` branch applies the overflow rules; non-
        // negative crossSpace falls through to the natural return.
        if (crossSpace < 0)
        {
            // Explicit `safe` modifier — always fall back to the flex-flow start
            // regardless of the keyword's natural reference (PR #217 review [P2]).
            // Per Phase 3 Task 15 L11 post-PR-#71 F#2: that start under wrap-reverse
            // is the line's NEW cross-start (= line bottom = contentCrossOffset +
            // crossSpace, which is negative). Items pack at the new cross-start edge
            // + overflow off the new cross-end edge. `safeFallbackReversed` is the
            // flex-flow `isCrossAxisReversed` (NOT the container-/item-logical
            // natural orientation), so a `safe start` / `safe self-start` that
            // overflows still falls back to the flex cross-start.
            if (mode == OverflowAlignmentMode.Safe)
            {
                var safeStartOffset = safeFallbackReversed
                    ? contentCrossOffset + crossSpace
                    : contentCrossOffset;
                return (safeStartOffset, itemCrossSize);
            }
            // Unsafe modifier OR default — positional values keep their
            // natural (possibly-negative) offset, allowing items to
            // overflow equally on both sides for `center` etc. (Unlike
            // justify-content, align-items has no distribution values
            // in L3 scope.) The `natural` value above already accounts
            // for the wrap-reverse swap.
            return (natural, itemCrossSize);
        }

        return (natural, itemCrossSize);
    }

    /// <summary>Flex baseline-alignment cycle (CSS Flexbox L1 §8.3 + §8.5) — for a ROW
    /// container, precompute per DOM-child-index two parallel arrays: the item's
    /// alignment BASELINE (distance from its border-box cross-start to its first text
    /// baseline) and the cross SIZE a baseline-aligned item uses.
    ///
    /// <para><b>Baseline</b> — when the item has an in-flow line box, its first baseline
    /// comes from the measured content (<see cref="BufferingMeasureSink.FirstBaselineFromOrigin"/>):
    /// the buffer origin is the item's BORDER-box cross-start for an inline-only ROOT
    /// buffer (so the value is used as-is) or its CONTENT-box cross-start for a
    /// block-child buffer (so the item's own block-start border+padding is added) — the
    /// same origin split <see cref="MeasureFlexItemContents"/> uses for content sizing.
    /// When the item has NO line box its baseline is SYNTHESIZED from its cross-end edge
    /// (= the cross size) per CSS Flexbox L1 §8.5.</para>
    ///
    /// <para><b>Cross size</b> — an auto-cross-sized item with measured content uses its
    /// content border-box block extent (so a baseline-aligned `&lt;div&gt;text&lt;/div&gt;`
    /// is content-height, not 0); an explicitly-sized item keeps its declared cross
    /// size. Indices for non-flex children stay NaN (never read — the emission loop only
    /// reads an index it is emitting).</para></summary>
    private (double[] Baselines, double[] CrossSizes) ComputeRowBaselineData(
        BufferingMeasureSink?[] buffers,
        FlexDirectionValue flexDirection,
        PropertyId crossSizeProperty)
    {
        var count = _rootBox.Children.Count;
        var baselines = new double[count];
        var crossSizes = new double[count];
        System.Array.Fill(baselines, double.NaN);
        System.Array.Fill(crossSizes, double.NaN);

        foreach (var itemIdx in _sortedFlexChildIndices)
        {
            var item = _rootBox.Children[itemIdx];
            var (baseline, crossSize) = ComputeItemBaselineAndCrossSize(
                item, buffers[itemIdx], flexDirection, crossSizeProperty);
            baselines[itemIdx] = baseline;
            crossSizes[itemIdx] = crossSize;
        }

        return (baselines, crossSizes);
    }

    /// <summary>Flex baseline-alignment cycle — for ONE ROW flex item, compute its
    /// alignment baseline (distance from its border-box cross-start to its first text
    /// baseline) + the cross SIZE a baseline-aligned item uses, from its content
    /// <paramref name="buffer"/> (null when the item was not measured). SHARED by the
    /// FlexLayouter emission (<see cref="ComputeRowBaselineData"/>) and the
    /// BlockLayouter row-flex height pre-measure so the baseline-adjusted wrapper extent
    /// matches the emitted geometry (PR #208 [P1]).
    /// <list type="bullet">
    ///   <item><b>Cross size</b> — a definite cross is its declared border box; an auto
    ///   cross is the measured content border box (an inline-only ROOT buffer's extent
    ///   already folds in the chrome; a block-child buffer adds it), or just the block
    ///   border+padding when there is no content (a text-less auto item is NOT 0 —
    ///   Copilot review).</item>
    ///   <item><b>Baseline</b> — the item's first line baseline (the buffer origin is the
    ///   item's border-box cross-start for an inline-only ROOT buffer, else its content-box
    ///   cross-start so the item's own block-start chrome is added), or SYNTHESIZED from
    ///   the cross-end edge (= the cross size) per §8.5 when the item has no line box.</item>
    /// </list></summary>
    internal static (double Baseline, double CrossSize) ComputeItemBaselineAndCrossSize(
        Box item, BufferingMeasureSink? buffer,
        FlexDirectionValue direction, PropertyId crossSizeProperty)
    {
        var itemCrossSize = item.Style.CrossBorderBoxSizePx(crossSizeProperty);
        var itemIsCrossSizeAuto = IsCrossSizeAuto(item, direction);

        double crossSize;
        if (itemIsCrossSizeAuto)
        {
            var blockChrome = item.Style.BlockBorderPaddingPx();
            crossSize = buffer is not null && buffer.ContentBlockExtent > 0
                ? (buffer.ContainsDecorationOwnerFragment
                    ? buffer.ContentBlockExtent
                    : buffer.ContentBlockExtent + blockChrome)
                : blockChrome;
        }
        else
        {
            crossSize = itemCrossSize; // definite → already a border box
        }

        double baseline;
        if (buffer is not null && buffer.HasFirstBaseline)
        {
            baseline = buffer.ContainsDecorationOwnerFragment
                ? buffer.FirstBaselineFromOrigin
                : item.Style.BlockStartBorderPaddingPx() + buffer.FirstBaselineFromOrigin;
        }
        else
        {
            // No line box → synthesize the baseline at the cross-end edge (§8.5).
            baseline = crossSize;
        }

        return (baseline, crossSize);
    }

    /// <summary>Flex baseline-alignment cycle — true when a ROW flex container's own
    /// <c>align-items</c> resolves to baseline OR any in-order item's <c>align-self</c>
    /// does. Lets the BlockLayouter row-flex pre-measure skip the baseline-adjusted extent
    /// path entirely (byte-identical) for the overwhelmingly common non-baseline case.</summary>
    internal static bool ContainerUsesBaselineAlignment(Box flexContainer, IReadOnlyList<Box> blockLevelItems)
    {
        var containerAlign = flexContainer.Style.ReadAlignItems();
        if (containerAlign.Value == AlignItemsValue.Baseline) return true;
        for (var i = 0; i < blockLevelItems.Count; i++)
        {
            var effective = blockLevelItems[i].Style.ReadAlignSelf()
                .ResolveAgainstContainerAlignItems(containerAlign);
            if (effective.Value == AlignItemsValue.Baseline) return true;
        }
        return false;
    }

    /// <summary>Flex baseline-alignment cycle [P1] — the baseline-adjusted cross extent of
    /// ONE nowrap ROW line: the deepest of (a) each item's plain cross size and (b) each
    /// BASELINE-aligned item's down-shifted bottom (the shift = lineMaxBaseline − itemBaseline,
    /// so its baseline meets the line's max baseline). When no item is baseline-aligned the
    /// result is just <c>max(crossSize)</c> (the pre-cycle extent). SHARED by emission +
    /// pre-measure. Each tuple is <c>(baseline, crossSize, isBaselineAligned)</c>.</summary>
    internal static double ComputeBaselineAdjustedLineExtent(
        IReadOnlyList<(double Baseline, double CrossSize, bool IsBaselineAligned)> items)
    {
        var maxBaseline = double.NaN;
        for (var i = 0; i < items.Count; i++)
        {
            if (!items[i].IsBaselineAligned) continue;
            var b = items[i].Baseline;
            if (double.IsNaN(maxBaseline) || b > maxBaseline) maxBaseline = b;
        }
        var extent = 0.0;
        for (var i = 0; i < items.Count; i++)
        {
            var (baseline, crossSize, isBaseline) = items[i];
            var bottom = isBaseline && !double.IsNaN(maxBaseline)
                ? (maxBaseline - baseline) + crossSize
                : crossSize;
            if (bottom > extent) extent = bottom;
        }
        return extent;
    }

    /// <summary>Flex baseline-alignment cycle — the line's baseline reference: the MAX
    /// first-baseline (from <paramref name="alignmentBaselines"/>) over the line's
    /// items whose effective <c>align-self</c>/<c>align-items</c> resolves to
    /// <see cref="AlignItemsValue.Baseline"/> (CSS Box Alignment L3 §6.2 baseline-sharing
    /// group). NaN when the line has no baseline-aligned item.</summary>
    private double ComputeLineMaxBaseline(
        FlexLine line, double[] alignmentBaselines, ResolvedAlignItems alignItems)
    {
        var maxBaseline = double.NaN;
        var endSortedPos = line.FirstItemIndex + line.ItemCount;
        for (var sortedPos = line.FirstItemIndex; sortedPos < endSortedPos; sortedPos++)
        {
            var itemIdx = _sortedFlexChildIndices[sortedPos];
            var item = _rootBox.Children[itemIdx];
            var effectiveAlign = item.Style.ReadAlignSelf().ResolveAgainstContainerAlignItems(alignItems);
            if (effectiveAlign.Value != AlignItemsValue.Baseline) continue;
            var b = alignmentBaselines[itemIdx];
            if (double.IsNaN(maxBaseline) || b > maxBaseline) maxBaseline = b;
        }
        return maxBaseline;
    }

    /// <summary>Per Phase 3 Task 15 L3 post-PR-#63 hardening F#2 +
    /// Phase 3 Task 15 L4 — distinguish a flex item's cross-size
    /// <c>auto</c> from an explicit length (<c>0</c> / positive px).
    /// Stretch applies only when the item's cross-size property
    /// computes to <c>auto</c> per CSS Flexbox §7.2; explicit <c>0</c>
    /// must be honored (e.g., a deliberate spacer that overlaps
    /// siblings on the cross axis).
    ///
    /// <para>Per L4 — the cross-axis property is direction-dependent:
    /// for <c>flex-direction: row</c> the cross axis is the block axis
    /// (read <see cref="PropertyId.Height"/>); for
    /// <c>flex-direction: column</c> the cross axis is the inline axis
    /// (read <see cref="PropertyId.Width"/>). The caller passes the
    /// resolved direction so this predicate stays direction-agnostic.</para>
    ///
    /// <para><b>Slot semantics</b> — per CSS Flexbox §7.2 "auto" means
    /// the cross-size property COMPUTES to <c>auto</c>:
    /// <list type="bullet">
    ///   <item><see cref="ComputedSlotTag.Unset"/> (= no declaration in
    ///   the cascade — the property's initial value applies, which is
    ///   <c>auto</c> for <c>width</c> / <c>height</c>) → auto.</item>
    ///   <item><see cref="ComputedSlotTag.Keyword"/> (= explicit
    ///   <c>auto</c> keyword, or one of the related intrinsic-sizing
    ///   keywords <c>min-content</c> / <c>max-content</c> /
    ///   <c>fit-content</c> — see L6+ refinement note below) → auto.</item>
    ///   <item><see cref="ComputedSlotTag.LengthPx"/> (= explicit pixel
    ///   value, including 0) → NOT auto.</item>
    ///   <item><see cref="ComputedSlotTag.Percentage"/> (= percentage
    ///   relative to the containing block's cross extent) → NOT auto.
    ///   The author wrote an explicit declaration; stretch should honor
    ///   it (resolving to 0 via <c>ReadLengthPxOrZero</c> in cycle 1
    ///   is a separate "percentage cross-size not yet resolved" gap
    ///   that's its own deferral — but it's NOT auto).</item>
    ///   <item>Any other tag (defensive) → NOT auto.</item>
    /// </list></para>
    ///
    /// <para><b>Pre-hardening bug</b> — per Phase 3 Task 15 L4 post-PR-#64
    /// review F#3 the previous implementation returned
    /// <c>slot.Tag != ComputedSlotTag.LengthPx</c>, which incorrectly
    /// reported <c>width: 50%</c> (Percentage) + <c>width: calc(...)</c>
    /// (SideTableIndex) as auto. A <c>width: 50%</c> flex item in a
    /// column container was stretched to 100% instead of remaining
    /// an explicit-declaration item. Per the F#3 fix the predicate
    /// now matches BlockLayouter's <c>IsHeightAuto</c>
    /// (BlockLayouter.cs ~5052 → ComputedStyleLayoutExtensions
    /// <see cref="ComputedStyleLayoutExtensions.IsHeightAuto"/>) — auto
    /// iff Unset OR Keyword. Percentage / SideTableIndex /
    /// LengthPx are all explicit declarations.</para>
    ///
    /// <para><b>Sub-cycle L7+ scope</b> — distinguishing the <c>auto</c>
    /// keyword from <c>min-content</c> / <c>max-content</c> / <c>fit-content</c>
    /// requires reading the keyword payload + cross-referencing the
    /// property-specific keyword table (L6 shipped <c>flex-wrap: wrap</c>
    /// without expanding the intrinsic-keyword resolution). For L4-L6
    /// all Keyword tags on width / height resolve as auto (the most
    /// common case + the others behave similarly for stretch in the
    /// L4-L6 simplification).</para>
    ///
    /// <para><b>Cancellation</b> — none needed; this is a single-slot
    /// read.</para></summary>
    /// <param name="item">A flex item box.</param>
    /// <param name="direction">Resolved <c>flex-direction</c> — selects
    /// which property is the cross axis.</param>
    private static bool IsCrossSizeAuto(Box item, FlexDirectionValue direction)
    {
        var crossProperty = direction.IsFlexColumnDirection()
            ? PropertyId.Width   // column: cross = inline (width)
            : PropertyId.Height; // row: cross = block (height)
        var slot = item.Style.Get(crossProperty);
        // Per CSS Flexbox §7.2 + the F#3 hardening — auto iff Unset
        // (no declaration → property's initial `auto` applies) OR
        // Keyword (explicit `auto` / `min-content` / etc.). All other
        // tags (LengthPx, Percentage, SideTableIndex, …) represent
        // explicit declarations + must NOT be treated as auto. Mirrors
        // the canonical IsHeightAuto predicate in
        // ComputedStyleLayoutExtensions.
        return slot.Tag is ComputedSlotTag.Unset or ComputedSlotTag.Keyword;
    }

    /// <summary>Per Phase 3 Task 15 L4 → cycle 4c post-PR-#84 review
    /// P3 #5 — return the property IDs to read for an item's
    /// main-axis + cross-axis sizes given the resolved
    /// <c>flex-direction</c>. Delegates to the shared
    /// <see cref="FlexDirectionValueExtensions.GetAxisProperties"/>
    /// extension so the layouter + <see cref="FlexLinePacker"/> share
    /// ONE axis-mapping source of truth (= no drift on writing-mode
    /// or axis updates).</summary>
    private static (PropertyId mainSize, PropertyId crossSize) GetAxisProperties(
        FlexDirectionValue direction)
        => direction.GetAxisProperties();

    /// <summary>Per Phase 3 Task 15 L12 — direction-resolved min/max
    /// main-size property ids for the §9.7 step-4 clamping iteration.
    /// For row direction the main axis is the inline axis: min/max =
    /// MinWidth/MaxWidth. For column: min/max = MinHeight/MaxHeight.
    /// Used by <see cref="ResolveLineWithMinMaxClamping"/>.</summary>
    private static (PropertyId minSize, PropertyId maxSize) GetMinMaxMainAxisProperties(
        FlexDirectionValue direction)
    {
        return direction.IsFlexColumnDirection()
            ? (PropertyId.MinHeight, PropertyId.MaxHeight)
            : (PropertyId.MinWidth, PropertyId.MaxWidth);
    }

    /// <summary>Non-block-pagination arc (flex item CONTENT layout) — lay out
    /// every flex item's INNER content (text / nested block children) via a
    /// nested <see cref="BlockLayouter"/> rooted at the item, buffering the
    /// produced fragments per item so the emission loop can re-emit them at the
    /// item's FINAL (possibly re-anchored) origin.
    ///
    /// <para>Mirrors <see cref="GridLayouter"/>'s
    /// <c>DispatchGridItemContents</c> / <see cref="TableLayouter"/>'s cell
    /// measure pass: the item is its own block-flow root; the inner layouter
    /// emits content-box-relative fragments which the buffer holds verbatim
    /// (the box's border / padding is NOT inset — the same box-model
    /// approximation grid ships) until <see cref="BufferingMeasureSink.FlushTo"/>
    /// applies the item's border-box origin.</para>
    ///
    /// <para><b>Content sizing.</b> For a COLUMN container, an item whose
    /// main-size (block-size) is content-determined
    /// (<see cref="IsMainSizeContentDetermined"/>) grows to the measured
    /// content block extent — closing the common `<c>&lt;div&gt;text&lt;/div&gt;</c>`
    /// gap where an auto-height column item collapsed to 0 and items stacked on
    /// top of each other. This runs BEFORE the LineMainSize recompute so the
    /// grown size feeds justify-content + the column item-split page budget.
    /// ROW main-axis content sizing (max-content width) stays deferred — a row
    /// item with auto width keeps its current (flex-resolved) width and its
    /// content renders at that width (overflowing if narrow), matching grid's
    /// zero-area-cell contract.</para>
    ///
    /// <para><b>Diagnostics (PR-#182 review P2).</b> Inner-content diagnostics
    /// are BUFFERED per item (one <see cref="BufferingDiagnosticsSink"/> each)
    /// and returned to the caller, which flushes ONLY a committed item's buffer
    /// (a deferred item's buffer is discarded + re-generated when it commits on
    /// its page) — so a paginating column doesn't duplicate per-item diagnostics
    /// across pages. When there's no effective sink the buffers are null (no
    /// buffering).</para></summary>
    /// <returns>Per-DOM-child-index arrays: the content buffers (null for a
    /// childless item) + the parallel diagnostic buffers (null when no item
    /// content was measured or no effective sink was supplied).</returns>
    private (BufferingMeasureSink?[] Content, BufferingDiagnosticsSink?[] Diagnostics)
        MeasureFlexItemContents(
        List<FlexLine> lines,
        double[] resolvedItemMainSizes,
        FlexDirectionValue flexDirection,
        bool isColumn,
        PropertyId mainSizeProperty,
        PropertyId crossSizeProperty,
        double containerCrossSize,
        double containerDefiniteCrossSize,
        double containerDefiniteMainSize,
        bool isWrapping,
        FragmentainerContext fragmentainer,
        WritingMode writingMode,
        bool isRtl,
        IPaginateDiagnosticsSink? effectiveDiagnostics,
        double contentMeasureBlockBudget,
        CancellationToken cancellationToken)
    {
        var buffers = new BufferingMeasureSink?[_rootBox.Children.Count];
        var diagBuffers = new BufferingDiagnosticsSink?[_rootBox.Children.Count];
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The line's cross extent: for wrap the line's own max-item-cross;
            // for nowrap the container's full cross extent. Mirrors the
            // emission loop's `lineCrossExtent`.
            var lineCrossExtent = isWrapping ? line.LineCrossSize : containerCrossSize;
            var endSortedPos = line.FirstItemIndex + line.ItemCount;
            for (var sortedPos = line.FirstItemIndex; sortedPos < endSortedPos; sortedPos++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var itemIdx = _sortedFlexChildIndices[sortedPos];
                var item = _rootBox.Children[itemIdx];
                if (item.Children.Count == 0) continue; // no inner content

                // The item's used INLINE (content) size for measuring inner content. For
                // column the inline axis is the cross axis (stretch → the line cross extent,
                // else the box-sizing-mapped declared cross size); for row it's the main axis
                // (the §9.7-resolved main size). Both are BORDER-box sizes, so subtract the
                // item's inline border + padding to get the CONTENT inline size content is
                // laid out at (flex box-sizing / content-inset cycle — was the border-box size,
                // measuring content too wide). A non-positive size falls back to the
                // container's content inline size so text still renders + overflows, mirroring
                // grid's zero-area-cell fallback.
                var inlineChrome = item.Style.InlineBorderPaddingPx();
                double borderBoxInline;
                if (isColumn)
                {
                    borderBoxInline = IsCrossSizeAuto(item, flexDirection)
                        ? lineCrossExtent
                        : item.Style.CrossBorderBoxSizePx(crossSizeProperty);
                }
                else
                {
                    borderBoxInline = resolvedItemMainSizes[itemIdx];
                }
                var usedInline = borderBoxInline - inlineChrome;
                if (!(usedInline > 0)) usedInline = _contentInlineSize;

                // PR-#182 review P2 — buffer this item's content diagnostics
                // (only when there's a sink to eventually flush to).
                var itemDiag = effectiveDiagnostics is null ? null : new BufferingDiagnosticsSink();
                diagBuffers[itemIdx] = itemDiag;

                // Corpus-fidelity (08 sales-report bar bleed) — the nested measure uses its block
                // budget as the percentage-HEIGHT base for the item's block children (BlockLayouter
                // resolves a top-level child's `height: N%` against `fragmentainer.BlockSize`). For a
                // ROW flex item with a DEFINITE cross size, that base must be the ITEM's content
                // height — NOT the page — so a `height: 100%` child (a gradient `.bar-fill`) fills the
                // track, not the whole page. A definite cross is either an explicit length OR a
                // percentage against a definite container cross (`.bar { height: 100% }` in a fixed-
                // height row container — 10's barcodes), resolved via the same container-base overload
                // the emission path uses. The content-cross clamps at 0 (border/padding may consume
                // the whole height, box-sizing:border-box), and a DEFINITE ZERO is preserved as the
                // %-height base (do NOT fall back to the page). Auto-cross / column items keep the page
                // budget (they content-size / their main axis is the block budget). Byte-identical
                // unless a definite-cross row item has a percentage-height child (the bug).
                var itemBlockBudget = contentMeasureBlockBudget;
                var crossSlot = item.Style.Get(crossSizeProperty);
                // A DEFINITE cross size: an explicit length, OR a percentage against a DEFINITE
                // container cross (finite base). A percentage against an INDEFINITE container is AUTO
                // per CSS Sizing 3 §5.1.1 — the item content-sizes, so it keeps the page budget (NOT a
                // spurious 0). Auto keywords are likewise not definite.
                var definiteCross = crossSlot.Tag == ComputedSlotTag.LengthPx
                    || (crossSlot.Tag == ComputedSlotTag.Percentage
                        && double.IsFinite(containerDefiniteCrossSize));
                if (!isColumn && definiteCross)
                {
                    var crossBorderBox = item.Style.CrossBorderBoxSizePx(
                        crossSizeProperty, containerDefiniteCrossSize);
                    itemBlockBudget = Math.Max(0, crossBorderBox - item.Style.BlockBorderPaddingPx());
                }
                // F4 (10-event-ticket barcode) — the COLUMN analogue of the row fix above: a column
                // item's BLOCK axis IS its MAIN axis. When the main size is DEFINITE the item's used
                // main size is already resolved in resolvedItemMainSizes (§9.7 flexing + min/max
                // clamping, a BORDER-box size), and it — not the page budget — is the percentage-height
                // base for the item's children (CSS 2.2 §10.5; the flex item is their containing block).
                // Pre-fix a definite-height column item (`.barcode { height: 58px }`) still handed its
                // children the page/fragmentainer budget, so a `height: 100%` bar resolved against the
                // A4 content box (751pt instead of 43.5pt). Definite = NOT content-determined
                // (flex-basis auto/content with auto height content-sizes → resolvedItemMainSizes is
                // stale 0 here, filled AFTER the measure at :3465–3475, so keep the page budget) AND
                // NOT a percentage main size / flex-basis against an INDEFINITE container main (a %
                // against indefinite is auto per css-sizing-3 §5.1.1 → keep the page budget).
                var mainSlot = item.Style.Get(mainSizeProperty);
                var mainIsPercentAgainstIndefinite =
                    (mainSlot.Tag == ComputedSlotTag.Percentage
                     || item.Style.ReadFlexBasis().Kind == FlexBasisKind.Percentage)
                    && !double.IsFinite(containerDefiniteMainSize);
                // Definite = an explicit length / definite flex-basis (resolvedItemMainSizes holds the
                // §9.7-flexed used main size — an explicit `height` + `flex-grow` in a definite-height
                // column grows correctly here). NOT content-determined items (flex-basis auto/content +
                // auto height): their resolvedItemMainSizes is either still 0 (auto-height container —
                // content-sizes AFTER this measure at :3465) or a page-sized hypothetical (their base
                // size is measured at the page budget), so it is NOT a usable %-base here — keep the
                // page budget. (Growing a `flex:1 1 auto` auto-height item to its DEFINITE-container
                // flexed height as the %-base needs an auto-basis column base size that isn't page-
                // sized — a separate flex base-sizing follow-up, review [P1].) Percentage main / basis
                // against an indefinite container is auto → page budget.
                var definiteMain = !IsMainSizeContentDetermined(item, mainSizeProperty)
                    && !mainIsPercentAgainstIndefinite;
                if (isColumn && definiteMain)
                {
                    itemBlockBudget = Math.Max(
                        0, resolvedItemMainSizes[itemIdx] - item.Style.BlockBorderPaddingPx());
                }

                var buffer = LayoutItemContentIntoBuffer(
                    item, usedInline, itemBlockBudget, writingMode, isRtl,
                    itemDiag, cancellationToken);
                buffers[itemIdx] = buffer;

                // Column content-sizing: grow an auto-height item to its content BORDER
                // box (flex box-sizing cycle). The block chrome is added ONLY for
                // block-CHILD content — an inline-only-root buffer's ContentBlockExtent is
                // ALREADY the item's border box (the nested layout folded the item's own
                // border + padding into the own-box fragment + TextPainter insets its
                // glyphs), so adding chrome again would double-count. Math.Max keeps any
                // flex-grown size; explicit-height items (not content-determined) are
                // untouched (their §9.7 size already box-sizing-mapped).
                if (isColumn && IsMainSizeContentDetermined(item, mainSizeProperty))
                {
                    var contentMainBorderBox = buffer.ContainsDecorationOwnerFragment
                        ? buffer.ContentBlockExtent
                        : buffer.ContentBlockExtent + item.Style.BlockBorderPaddingPx();
                    if (contentMainBorderBox > resolvedItemMainSizes[itemIdx])
                        resolvedItemMainSizes[itemIdx] = contentMainBorderBox;
                }
            }
        }
        return (buffers, diagBuffers);
    }

    /// <summary>Lay out one flex item's inner content into a fresh
    /// <see cref="BufferingMeasureSink"/> at <paramref name="availInlineContentSize"/>
    /// available inline size via the shared <see cref="NestedContentMeasurer"/>
    /// (atomic <c>LastResort</c> pass; grid + flex pagination suppressed;
    /// inline-only root opted in; <paramref name="itemDiagnostics"/> buffers
    /// the item's content diagnostics). Mirrors <see cref="TableLayouter"/>'s
    /// <c>MeasureCellContent</c> / <see cref="GridLayouter"/>'s
    /// <c>DispatchGridItemContents</c> nested-layout dispatch.</summary>
    private BufferingMeasureSink LayoutItemContentIntoBuffer(
        Box item,
        double availInlineContentSize,
        double blockBudget,
        WritingMode writingMode,
        bool isRtl,
        IPaginateDiagnosticsSink? itemDiagnostics,
        CancellationToken cancellationToken)
        => NestedContentMeasurer.Measure(
            item, availInlineContentSize,
            blockBudget: blockBudget,
            shaperResolver: _shaperResolver,
            writingMode: writingMode, isRtl: isRtl,
            cancellationToken: cancellationToken,
            diagnostics: itemDiagnostics,
            // This buffer FLUSHES into the final tree at the item's position (the flex item
            // emission), so it requests a real Layout pass (% padding resolves real, in-flow persists
            // for paint). PR #218 review [P1 #1] — but if the flex CONTAINER is itself being
            // measured intrinsically, ForNested inherits that so the flush stays a measure too.
            purpose: _measurePurpose.ForNested(MeasurePurpose.Layout),
            // RC2 residual (1) — but SKIP this nested pass's abspos emission: FlexLayouter is not an
            // abspos delegation boundary, so a flex item's abspos DESCENDANTS are owned + placed by the
            // TOP-LEVEL pass (which resolves them against the positioned-CB geometry the buffer flush
            // records). Letting the nested pass also run it only ever drops the descendant (unrecorded CB
            // in the transient nested map) + leaks a spurious LAYOUT-ABSOLUTE-FEATURE-UNSUPPORTED-001.
            suppressOutOfFlowEmission: true);

    /// <summary>Non-block-pagination arc — whether a flex item's MAIN-axis size
    /// is content-determined (so content measurement should size it). True iff
    /// the used flex-basis is <c>content</c>, OR <c>auto</c> with the declared
    /// main-size property also <c>auto</c> (Unset / Keyword slot). A definite
    /// flex-basis length / percentage, or a definite declared main-size, gives
    /// the item a definite base size that content must not override. Mirrors
    /// <see cref="IsCrossSizeAuto"/>'s slot-tag test +
    /// <see cref="ComputedStyleLayoutExtensions.ResolveFlexItemHypotheticalMainSize"/>'s
    /// flex-basis resolution.</summary>
    private static bool IsMainSizeContentDetermined(Box item, PropertyId mainSizeProperty)
    {
        var basis = item.Style.ReadFlexBasis();
        // Flex intrinsic-basis cycle — content / max-content / min-content are all
        // content-determined (the COLUMN block axis sizes from the measured content;
        // the ROW inline axis uses the precomputed intrinsic base size).
        if (basis.Kind is FlexBasisKind.Content or FlexBasisKind.MaxContent or FlexBasisKind.MinContent)
            return true;
        if (basis.Kind == FlexBasisKind.Auto)
        {
            var slot = item.Style.Get(mainSizeProperty);
            return slot.Tag is ComputedSlotTag.Unset or ComputedSlotTag.Keyword;
        }
        return false;
    }

    /// <summary>Per cycle 1 (Hello World) — no per-instance state to
    /// release. Sub-cycles 2+ that pool measurement caches will hook
    /// disposal here; cycle 1 ships as a no-op for parity with the
    /// other layouters' <c>using var</c> dispatch pattern.</summary>
    public void Dispose()
    {
        // No-op for cycle 1. The _diagnostics + _shaperResolver
        // references are caller-owned; the layouter holds no pooled
        // resources of its own.
    }
}
