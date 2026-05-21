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
///   <c>PreMeasureFlexMultiLineCrossExtent</c>). <c>flex-wrap:
///   wrap-reverse</c> decodes to <see cref="FlexWrapValue.WrapReverse"/>
///   but L6 treats it identically to <see cref="FlexWrapValue.Wrap"/>
///   — the cross-axis line-stacking reversal is L8+ scope; tracked in
///   <c>docs/deferrals.md#flex-layouter-features</c>. Per Phase 3
///   Task 15 L6 post-PR-#66 review F#4 the layouter emits the
///   <c>LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001</c> warning
///   diagnostic on each <c>AttemptLayout</c> invocation that
///   encounters <c>wrap-reverse</c>, so the silent approximation is
///   visible to authors.</item>
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
///   following families fall through to <c>stretch</c> (the safe
///   default): <c>baseline</c> / <c>first baseline</c> /
///   <c>last baseline</c> (text-shaping integration needed) and
///   <c>anchor-center</c> (CSS Anchor Positioning, out of Flexbox L1
///   scope). The per-item <c>align-self</c> override SHIPPED in
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
///   <item>No multi-page flex container splitting. The flex container
///   is atomic to the outer pagination — the entire container's
///   items emit on the page the wrapper landed on, same as
///   <see cref="MulticolLayouter"/> cycle 1. Cycle 2 will introduce
///   <see cref="FlexContinuation"/> resume.</item>
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
    private readonly IBlockFragmentSink _sink;
    private readonly IPaginateDiagnosticsSink? _diagnostics;
    private readonly IShaperResolver? _shaperResolver;

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

    // Per Phase 3 Task 15 L6 post-PR-#66 review F#4 — one-shot guard
    // for the wrap-reverse-approximated diagnostic. Reset on each
    // AttemptLayout entry so a re-invocation (= different
    // fragmentainer / retry attempt) re-emits the warning. Within a
    // single AttemptLayout we only need to surface it once — the
    // approximation is a property of the container declaration, not
    // a per-item event.
    private bool _emittedWrapReverseDiagnostic;

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
        IShaperResolver? shaperResolver = null)
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

        // Per cycle 1 (Hello World) — multi-page flex container
        // splitting is sub-cycle 2+ scope; the container is atomic to
        // the outer pagination. Reject any non-null continuation so
        // misrouted continuations surface loudly. Cycle 2 will accept
        // a FlexContinuation here. Mirrors MulticolLayouter cycle 1's
        // blanket non-null rejection (Finding 5 on the post-PR-#60
        // review hardening pass).
        if (incomingContinuation is not null)
        {
            throw new ArgumentException(
                "FlexLayouter cycle 1 (Hello World) does not support "
                + "multi-page flex container splitting; "
                + $"incomingContinuation must be null, got "
                + $"{incomingContinuation.GetType().Name}. The cycle 1 "
                + "contract treats the flex container as atomic to the "
                + "outer pagination (= the wrapper's first-page fit "
                + "succeeds OR the entire container is deferred). "
                + "FlexContinuation-based multi-page resume is sub-"
                + "cycle 2+ scope; see docs/deferrals.md#flex-layouter-features.",
                nameof(incomingContinuation));
        }

        _rootBox = rootBox;
        _sink = sink;
        _diagnostics = diagnostics;
        _shaperResolver = shaperResolver;
    }

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
    public void ConfigureEmission(
        double contentInlineOffset,
        double contentBlockOffset,
        double contentInlineSize,
        double contentBlockSize)
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
        _contentInlineOffset = contentInlineOffset;
        _contentBlockOffset = contentBlockOffset;
        _contentInlineSize = contentInlineSize;
        _contentBlockSize = contentBlockSize;
        _emissionConfigured = true;
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
        // Per Phase 3 Task 15 L5 post-PR-#65 review F#1 — the axis
        // mapping below assumes LTR horizontal-tb (the L1 default
        // writing mode). Per CSS Flexbox §3.1 the spec-correct axis
        // mapping depends on the cascaded `direction` + `writing-mode`
        // properties; under RTL or vertical writing modes the physical
        // mapping differs (e.g., RTL row = right-to-left along the
        // inline axis = visually equivalent to LTR row-reverse).
        // Plumbing `direction` / `writing-mode` through the layout
        // pipeline is L7+ scope (L6 shipped `flex-wrap: wrap` without
        // expanding the direction pipeline); tracked in
        // `docs/deferrals.md#flex-layouter-features`.
        var flexDirection = _rootBox.Style.ReadFlexDirection();
        var isColumn = flexDirection.IsFlexColumnDirection();
        var isReverse = flexDirection.IsFlexReverseDirection();
        var (mainSizeProperty, crossSizeProperty) = GetAxisProperties(flexDirection);

        // Per Phase 3 Task 15 L6 — read flex-wrap. When the value is
        // `wrap` (or `wrap-reverse`; the latter behaves as `wrap` for
        // L6) the layouter switches into multi-line mode: items are
        // greedy-packed onto lines along the main axis, and lines stack
        // along the cross axis at cross-start. The L1-L5 single-line
        // behavior is preserved verbatim for `nowrap` (the default) by
        // PackLines emitting exactly one line covering all items.
        var flexWrap = _rootBox.Style.ReadFlexWrap();
        var isWrapping = flexWrap.IsFlexWrapping();

        // Per Phase 3 Task 15 L6 post-PR-#66 review F#4 — emit a
        // one-shot warning when the author declared `wrap-reverse`
        // but the layouter treats it as `wrap` (the cross-axis line-
        // stacking reversal is L7+ scope; see
        // `docs/deferrals.md#flex-layouter-features`). Without this
        // diagnostic the wrong rendering is silent — the CSS
        // declaration parses successfully + items wrap in the
        // correct main-axis order, but the lines stack in the
        // natural cross-axis direction rather than the author-
        // requested reversed direction. Reset the guard at function
        // entry so a re-invocation surfaces the warning again.
        _emittedWrapReverseDiagnostic = false;
        if (flexWrap == FlexWrapValue.WrapReverse && !_emittedWrapReverseDiagnostic)
        {
            OptimizingBreakResolver.SafeEmit(
                layout.Diagnostics ?? _diagnostics,
                new PaginateDiagnostic(
                    PaginateDiagnosticCodes.LayoutFlexWrapReverseApproximated001,
                    "FlexLayouter: `flex-wrap: wrap-reverse` is approximated as "
                    + "`flex-wrap: wrap` in L6 — the cross-axis line stacking "
                    + "reversal is L7+ scope (see "
                    + "docs/deferrals.md#flex-layouter-features). The visual "
                    + "result preserves item order but stacks lines in the "
                    + "natural cross-axis direction rather than the reversed "
                    + "direction the author requested.",
                    PaginateDiagnosticSeverity.Warning));
            _emittedWrapReverseDiagnostic = true;
        }

        // Resolve the container's main-axis + cross-axis content extents
        // + offsets. For row direction the main axis is the inline axis
        // (= _contentInlineSize / _contentInlineOffset); the cross axis
        // is the block axis. For column direction the assignment swaps.
        var containerMainSize = isColumn ? _contentBlockSize : _contentInlineSize;
        var contentMainOffset = isColumn ? _contentBlockOffset : _contentInlineOffset;
        var contentCrossOffset = isColumn ? _contentInlineOffset : _contentBlockOffset;

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
        var lines = PackLines(
            _rootBox, _sortedFlexChildIndices, flexDirection,
            containerMainSize, isWrapping, cancellationToken);

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
        var freeCrossSpace = containerCrossSize - sumLineCrossExtents;

        var (lineStartOffset, lineBetweenSpacing, lineStretchAddend) =
            ComputeAlignContentOffsets(resolvedAC, freeCrossSpace, lines.Count, isWrapping);

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
        var resolvedItemMainSizes = ResolveFlexibleMainSizes(
            lines, mainSizeProperty, containerMainSize, cancellationToken);

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
        var lineCrossCursor = contentCrossOffset + lineStartOffset;

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Per Phase 3 Task 15 L6 — `align-items` operates against
            // EACH LINE'S cross-extent (CSS Flexbox L1 §6.3): items on
            // a line center / end-pack / stretch relative to that
            // line's max item cross-size, not the container's full
            // cross-extent. For nowrap (single line), line.LineCrossSize
            // equals containerCrossSize so behavior is identical to L5.
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

            // L2 — resolve justify-content + compute the main-axis
            // start-offset + between-spacing per CSS Box Alignment L3
            // §4.5. Per Phase 3 Task 15 L6 each line runs its own
            // justify-content with the container's main extent as the
            // alignment target — items within a line align relative to
            // the container's main-axis range, not the line's used
            // main-size. (CSS Flexbox L1 §6.3 says wrapped lines are
            // formatted independently for main-axis alignment; the
            // container's main extent remains the alignment basis.)
            var freeSpace = containerMainSize - line.LineMainSize;
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
                var itemCrossSize = item.Style.ReadLengthPxOrZero(crossSizeProperty);
                // Per Phase 3 Task 15 L3 post-PR-#63 hardening F#2 +
                // L4 — the stretch branch needs to distinguish `auto`
                // (Unset / Keyword slot) from explicit `0` (LengthPx
                // slot with value 0). The cross-axis property is
                // direction-dependent — IsCrossSizeAuto receives the
                // resolved direction.
                var itemIsCrossSizeAuto = IsCrossSizeAuto(item, flexDirection);

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
                var (itemCrossOffsetWithinLine, itemEffectiveCrossSize) =
                    ComputeAlignItemsPlacement(
                        effectiveAlign.Value, effectiveAlign.Mode,
                        lineCrossExtent, itemCrossSize,
                        itemIsCrossSizeAuto,
                        lineCrossCursor);

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
                var mainOffsetForEmission = isReverse
                    ? (contentMainOffset + containerMainSize) - (mainCursor - contentMainOffset) - itemMainSize
                    : mainCursor;

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

                _sink.Emit(new BoxFragment(
                    Box: item,
                    InlineOffset: inlineOffset,
                    BlockOffset: blockOffset,
                    InlineSize: inlineSize,
                    BlockSize: blockSize));

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
            }

            // Advance the line cross cursor past this line's cross
            // extent + the L7 between-line spacing (= the per-line gap
            // contributed by align-content's distribution values:
            // space-between / -around / -evenly). For positional values
            // (flex-start / flex-end / center) and stretch, the
            // between-spacing is 0 — the gap is absorbed by the
            // startOffset (positional) or by the line cross-extent
            // (stretch).
            lineCrossCursor += line.LineCrossSize + lineBetweenSpacing;
        }

        // Cycle 1 (Hello World) — flex container is atomic to outer
        // pagination; all items committed on this page. No
        // continuation.
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
    /// <returns>The packed flex lines; never null. Returns an empty list
    /// when the container has no block-level children (matches the L1-L5
    /// behavior of emitting no item fragments). Per Phase 3 Task 15 L10,
    /// FlexLine.FirstItemIndex is a POSITION in
    /// <paramref name="sortedChildIndices"/>, not a DOM-children index.
    /// (Removed L1-L9 semantics): the first line is always
    /// at FlexLine.FirstItemIndex of the first block-level child; later
    /// lines reference their first block-level child's index in the
    /// original Children list.</returns>
    // Per Phase 3 Task 15 L6 post-PR-#66 review F#5 TODO: this
    // line-packing algorithm duplicates the line-packing inside
    // <see cref="BlockLayouter"/>'s row+wrap pre-measure branch (see
    // <c>PreMeasureFlexMultiLineCrossExtent</c> + its row-direction
    // helper). Both walk the items + apply the greedy packing rule
    // ("first item on a line always lands; later items wrap when
    // adding would exceed containerMainSize") against the same
    // container main-axis budget. L7+ scope: extract a shared
    // <c>FlexLinePacker</c> consumed by both sites so they can't
    // drift. Not done now (= medium-scope refactor; risk of
    // regression).
    //
    // Per Phase 3 Task 15 L8 post-PR-#68 hardening F#1 — line packing
    // now uses each item's HYPOTHETICAL main-size (driven by
    // flex-basis) per CSS Flexbox §9.3, NOT the raw declared main-size.
    // Both PackLines AND BlockLayouter's pre-measure share the same
    // <c>ResolveFlexItemHypotheticalMainSize</c> extension so the two
    // passes always pack into the same lines. Pre-fix an item with
    // <c>width: 300px; flex-basis: 0; flex-grow: 1</c> would land alone
    // on its own line (because PackLines saw width=300 and a 300px
    // container can only fit one); post-fix the item contributes 0 to
    // the line packing (= flex-basis 0) so three such items fit on a
    // single line and grow to 100px each per §9.7.
    private static List<FlexLine> PackLines(
        Box flexContainer,
        List<int> sortedChildIndices,
        FlexDirectionValue direction,
        double containerMainSize,
        bool isWrapping,
        CancellationToken cancellationToken)
    {
        var lines = new List<FlexLine>();
        var (mainProp, crossProp) = GetAxisProperties(direction);

        // Per Phase 3 Task 15 L10 — <paramref name="sortedChildIndices"/>
        // is the block-level children in EFFECTIVE FLEX ORDER per
        // CSS Flexbox L1 §5.4 (sorted by (order, DOM-index)). The
        // caller produces this via Box.GetFlexChildrenInOrderSequence;
        // non-block-level children are already filtered out so this
        // method drops the IsBlockLevel skip the L1-L9 code held
        // inline. FlexLine.FirstItemIndex is now a POSITION in this
        // sorted sequence (NOT a DOM-children index) — the emission
        // loop dereferences via sortedChildIndices[FirstItemIndex + i].
        if (sortedChildIndices.Count == 0)
        {
            return lines;
        }

        if (!isWrapping)
        {
            // Single-line algorithm — L1-L5 preserved verbatim. Sum all
            // items' hypothetical main-axis sizes + take the max cross-
            // axis size. (L8 post-PR-#68 F#1: uses flex-basis-driven
            // hypothetical main-size; pre-L8 used the raw declared
            // width. L10: walks the sorted sequence so visual order
            // honors `order`.)
            var totalMain = 0.0;
            var maxCross = 0.0;
            foreach (var idx in sortedChildIndices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = flexContainer.Children[idx];
                totalMain += item.ResolveFlexItemHypotheticalMainSize(mainProp, containerMainSize);
                var c = item.Style.ReadLengthPxOrZero(crossProp);
                if (c > maxCross) maxCross = c;
            }
            lines.Add(new FlexLine(
                FirstItemIndex: 0,  // sorted-sequence position
                ItemCount: sortedChildIndices.Count,
                LineMainSize: totalMain,
                LineCrossSize: maxCross));
            return lines;
        }

        // Wrap — greedy line packing. Per CSS Flexbox L1 §9.3 the spec
        // says line packing uses each item's "flex item's hypothetical
        // main size" — derived from flex-basis per §9.2. The L8
        // post-PR-#68 F#1 hardening switches the contribution from
        // ReadLengthPxOrZero(mainProp) to
        // ResolveFlexItemHypotheticalMainSize. The L10 refactor walks
        // the sorted sequence so wrap boundaries respect both
        // flex-basis AND `order`.
        var currentFirstSortedPos = 0;
        var currentCount = 0;
        var currentMain = 0.0;
        var currentCross = 0.0;

        for (var sortedPos = 0; sortedPos < sortedChildIndices.Count; sortedPos++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var domIdx = sortedChildIndices[sortedPos];
            var item = flexContainer.Children[domIdx];
            var itemMain = item.ResolveFlexItemHypotheticalMainSize(mainProp, containerMainSize);
            var itemCross = item.Style.ReadLengthPxOrZero(crossProp);

            // Per CSS Flexbox L1 §9.3 — "if the very first uncollected
            // item wouldn't fit, collect just it into the line". The
            // first item on a line always lands (it overflows alone)
            // and the "would exceed" check applies only to subsequent
            // items.
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
        double containerMainSize,
        CancellationToken cancellationToken)
    {
        var resolved = new double[_rootBox.Children.Count];

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Pass 1 — compute hypothetical main-size for each item on
            // the line. Sum the hypotheticals + the grow/shrink factor
            // totals. Per Phase 3 Task 15 L10 — walks the sorted
            // sequence via FirstItemIndex (= sorted-position, not
            // DOM-children index); the resolved[] array is still keyed
            // by DOM-children index since downstream callers (emission
            // loop) dereference by DOM index.
            var sumHypothetical = 0.0;
            var sumFlexGrow = 0.0;
            var sumScaledShrinks = 0.0;
            var endPos = line.FirstItemIndex + line.ItemCount;
            for (var sortedPos = line.FirstItemIndex; sortedPos < endPos; sortedPos++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var itemIdx = _sortedFlexChildIndices[sortedPos];
                var item = _rootBox.Children[itemIdx];

                var hypothetical = ResolveHypotheticalMainSize(
                    item, mainSizeProperty, containerMainSize);
                resolved[itemIdx] = hypothetical;
                sumHypothetical += hypothetical;
                sumFlexGrow += item.Style.ReadFlexGrow();
                sumScaledShrinks += item.Style.ReadFlexShrink() * hypothetical;
            }

            var freeMainSpace = containerMainSize - sumHypothetical;

            // Pass 2 — distribute free space. Three exclusive branches:
            //   (a) positive free space + at least one growable item →
            //       grow phase.
            //   (b) negative free space + at least one shrinkable item
            //       with non-zero scaled shrink → shrink phase.
            //   (c) otherwise → items keep their hypothetical sizes.
            if (freeMainSpace > 0 && sumFlexGrow > 0)
            {
                // Per Phase 3 Task 15 L8 post-PR-#68 hardening F#2 +
                // CSS Flexbox L1 §9.7 — when the sum of flex-grow
                // factors on the line is LESS THAN 1, the items only
                // take a fraction of the free space; the remainder
                // stays as alignment-axis free-space for
                // justify-content. Equivalent formula: each item's
                // share = (grow_i / max(sumFlexGrow, 1)) * freeSpace.
                var growDivisor = sumFlexGrow >= 1 ? sumFlexGrow : 1.0;
                for (var sortedPos = line.FirstItemIndex; sortedPos < endPos; sortedPos++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var itemIdx = _sortedFlexChildIndices[sortedPos];
                    var item = _rootBox.Children[itemIdx];
                    var grow = item.Style.ReadFlexGrow();
                    if (grow > 0)
                    {
                        resolved[itemIdx] += (grow / growDivisor) * freeMainSpace;
                    }
                }
            }
            else if (freeMainSpace < 0 && sumScaledShrinks > 0)
            {
                var deficit = -freeMainSpace; // positive amount to absorb
                for (var sortedPos = line.FirstItemIndex; sortedPos < endPos; sortedPos++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var itemIdx = _sortedFlexChildIndices[sortedPos];
                    var item = _rootBox.Children[itemIdx];
                    var shrink = item.Style.ReadFlexShrink();
                    if (shrink > 0)
                    {
                        var scaledShrink = shrink * resolved[itemIdx];
                        var absorb = (scaledShrink / sumScaledShrinks) * deficit;
                        // Floor at 0 — items never shrink below 0 main-
                        // size in the L8 Hello World (proper min-size
                        // clamping per §9.7 step 4 is L9+ scope).
                        resolved[itemIdx] = Math.Max(0, resolved[itemIdx] - absorb);
                    }
                }
            }
            // else: items stay at hypothetical (free space goes to
            // justify-content / overflows the container).
        }

        return resolved;
    }

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
        double containerMainSize) =>
        item.ResolveFlexItemHypotheticalMainSize(mainSizeProperty, containerMainSize);

    /// <summary>Per Phase 3 Task 15 L6 — a flex line produced by
    /// <see cref="PackLines"/>. The line carries an index range into
    /// the SORTED-SEQUENCE list (per Phase 3 Task 15 L10) plus the
    /// line's pre-computed main + cross extents (so per-line
    /// justify-content + align-items math doesn't have to re-walk
    /// the items).
    ///
    /// <para><b>Index range semantics</b> (per Phase 3 Task 15 L10
    /// — UPDATED from the original L6 contract).
    /// <see cref="FirstItemIndex"/> is a POSITION in
    /// <c>_sortedFlexChildIndices</c>, NOT a DOM-children index.
    /// <see cref="ItemCount"/> is the count of items on the line.
    /// The emission loop walks
    /// <c>sortedChildIndices[FirstItemIndex .. FirstItemIndex +
    /// ItemCount)</c> and dereferences each position to a DOM-
    /// children index. The sorted sequence is pre-filtered to block-
    /// level children only, so the emission loop no longer carries
    /// the per-item IsBlockLevel skip the L1-L9 code held inline.</para></summary>
    private readonly record struct FlexLine(
        int FirstItemIndex,
        int ItemCount,
        double LineMainSize,
        double LineCrossSize);

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
            bool isWrapping)
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

        // Compute the NATURAL offset for the value (ignoring overflow
        // for now). This is the spec-defined offset assuming positive
        // free space exists; the overflow branch below either preserves
        // it (positional values + the unsafe modifier) or replaces it
        // with safe-start fallback (distribution values + stretch +
        // the safe modifier).
        var natural = resolved.Value switch
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
            return resolved.Value switch
            {
                AlignContentValue.SpaceBetween or
                AlignContentValue.SpaceAround or
                AlignContentValue.SpaceEvenly or
                AlignContentValue.Stretch => (0, 0, 0),
                _ => natural, // FlexStart / FlexEnd / Center — allow overflow
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
            double contentCrossOffset)
    {
        // Stretch — auto-cross-sized items get resized to fill the
        // container's cross extent; explicitly-sized items keep their
        // declared size per CSS Flexbox L1 §7.2. Per post-PR-#63 F#2
        // we test the SLOT TYPE (via `itemIsCrossSizeAuto`) rather
        // than `itemCrossSize > 0`: an explicit cross-size of 0 (= a
        // LengthPx slot with payload 0) is NOT auto and must keep its
        // declared 0 cross-size (e.g., a deliberate spacer).
        if (value == AlignItemsValue.Stretch)
        {
            var effectiveCross = itemIsCrossSizeAuto ? containerCrossSize : itemCrossSize;
            return (contentCrossOffset, effectiveCross);
        }

        // Positional alignment — compute the natural offset for the
        // value (ignoring overflow for now).
        var crossSpace = containerCrossSize - itemCrossSize;
        var natural = value switch
        {
            AlignItemsValue.FlexEnd => contentCrossOffset + crossSpace,
            AlignItemsValue.Center => contentCrossOffset + crossSpace / 2.0,
            _ => contentCrossOffset,  // FlexStart
        };

        // Overflow handling per CSS Box Alignment L3 §5.3. Only the
        // `crossSpace < 0` branch applies the overflow rules; non-
        // negative crossSpace falls through to the natural return.
        if (crossSpace < 0)
        {
            // Explicit `safe` modifier — always fall back to safe-start
            // (= contentCrossOffset) regardless of value. Items pack at
            // the container's cross-start edge + overflow off the
            // cross-end.
            if (mode == OverflowAlignmentMode.Safe)
            {
                return (contentCrossOffset, itemCrossSize);
            }
            // Unsafe modifier OR default — positional values keep their
            // natural (possibly-negative) offset, allowing items to
            // overflow equally on both sides for `center` etc. (Unlike
            // justify-content, align-items has no distribution values
            // in L3 scope.)
            return (natural, itemCrossSize);
        }

        return (natural, itemCrossSize);
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

    /// <summary>Per Phase 3 Task 15 L4 — return the property IDs to
    /// read for an item's main-axis + cross-axis sizes given the
    /// resolved <c>flex-direction</c>.
    /// <list type="bullet">
    ///   <item><c>row</c> (L1-L3 default): main = inline (<c>width</c>);
    ///   cross = block (<c>height</c>).</item>
    ///   <item><c>column</c> (L4 new): main = block (<c>height</c>);
    ///   cross = inline (<c>width</c>).</item>
    /// </list>
    /// The reversed variants (<c>row-reverse</c> / <c>column-reverse</c>)
    /// share the same axis assignment as their non-reversed counterparts
    /// per CSS Flexbox L1 §5.1 — reversal only swaps main-start and
    /// main-end edges, not the row/column axis itself. The L5
    /// reversal logic flips the main-axis offset at the emission site
    /// (see <c>mainOffsetForEmission</c> in <see cref="AttemptLayout"/>);
    /// the property reads here stay direction-agnostic.</summary>
    private static (PropertyId mainSize, PropertyId crossSize) GetAxisProperties(
        FlexDirectionValue direction)
    {
        return direction.IsFlexColumnDirection()
            ? (PropertyId.Height, PropertyId.Width)
            : (PropertyId.Width, PropertyId.Height);
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
