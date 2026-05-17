// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
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
/// <c>flex-direction: row</c>).
///
/// <para><b>Cycle 1 (Hello World) scope.</b> The layouter implements
/// the absolute minimum end-to-end path so subsequent cycles have a
/// scaffold to grow:</para>
/// <list type="bullet">
///   <item>Only <c>flex-direction: row</c> (default). Items flow
///   left-to-right along the inline axis. Column / row-reverse /
///   column-reverse are sub-cycle 2+ scope.</item>
///   <item>Only <c>flex-wrap: nowrap</c> (default). Single-line; items
///   may overflow the container's inline extent if their natural
///   widths don't fit. <c>wrap</c> / <c>wrap-reverse</c> are sub-cycle
///   2+ scope.</item>
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
///   default) in L3: <c>baseline</c> / <c>first baseline</c> /
///   <c>last baseline</c> (text-shaping integration needed),
///   <c>anchor-center</c> (CSS Anchor Positioning, out of Flexbox L1
///   scope), and the per-item <c>align-self</c> override (an extra
///   cascade read per item). See
///   <c>docs/deferrals.md#flex-layouter-features</c>.</item>
///   <item>Items use their <b>natural inline-size</b>: the item's
///   declared <c>width</c> if set (read as a length-px slot), else 0.
///   No <c>flex-grow</c> / <c>flex-shrink</c> / <c>flex-basis</c>
///   interpolation.</item>
///   <item>Items use their <b>natural block-size</b>: the item's
///   declared <c>height</c> if set, else 0.</item>
///   <item>No <c>order</c> property handling (children render in
///   source order).</item>
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

        // Per cycle 1 (Hello World) — fixed-axis assumptions:
        //   flex-direction: row (main axis = inline axis)
        //   flex-wrap: nowrap (single line)
        //   align-items: stretch (cycle 1 emits at content-block-start
        //                         regardless of the value)
        // Per L2 — justify-content is now honored (see ReadJustifyContent
        //   + ComputeJustifyContentOffsets below).
        // Cycle 2+ will read the actual computed values from
        //   _rootBox.Style for PropertyId.FlexDirection / FlexWrap /
        //   AlignItems.

        // L2 — first pass: count block-level items + sum their natural
        // inline-sizes so the alignment math knows the free-space + N.
        // Mirrors the row-packing loop's filter (only block-level
        // children participate in cycle 1) so the count matches the
        // emission pass below. The two-pass design is necessary because
        // start-offset + between-spacing both depend on N AND total
        // item size, neither of which is known until the items are
        // walked. The double pass is O(N) overall — same complexity
        // class as cycle 1.
        var itemCount = 0;
        var totalItemInlineSize = 0.0;
        for (var itemIdx = 0; itemIdx < _rootBox.Children.Count; itemIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = _rootBox.Children[itemIdx];
            if (!item.IsBlockLevel) continue;
            itemCount++;
            totalItemInlineSize += item.Style.ReadLengthPxOrZero(PropertyId.Width);
        }

        // L2 — resolve justify-content + compute the start-offset +
        // between-spacing per CSS Box Alignment L3 §4.5. Per Phase 3
        // Task 15 L2 post-PR-#62 hardening F#1 + F#2 — the resolved
        // value now carries TWO channels: the base alignment value +
        // an overflow modifier (Default / Safe / Unsafe). Per §5.3 the
        // overflow handling depends on BOTH: positional values keep
        // their natural (possibly-negative) offset on overflow while
        // distribution values fall back to safe-start; the explicit
        // `safe` modifier forces safe-start fallback regardless; the
        // explicit `unsafe` modifier preserves the alignment even on
        // overflow.
        var resolvedJC = _rootBox.Style.ReadJustifyContent();
        var freeSpace = _contentInlineSize - totalItemInlineSize;
        var (startOffset, betweenSpacing) = ComputeJustifyContentOffsets(
            resolvedJC.Value, resolvedJC.Mode, freeSpace, itemCount);

        // Per Phase 3 Task 15 L3 — resolve align-items + derive the
        // container's cross-axis extent so the per-item placement loop
        // can position items along the block axis (the cross axis for
        // the L1 default `flex-direction: row`). L1 + L2 emitted items
        // at `_contentBlockOffset` regardless of align-items; L3 honors
        // the full matrix (flex-start / flex-end / center / stretch +
        // safe / unsafe overflow modes).
        //
        // containerCrossSize derivation:
        //   - Explicit `height` (a LengthPx slot on the container's
        //     ComputedStyle) → use that length.
        //   - `height: auto` (Unset OR the `auto` Keyword) → use the
        //     max of the items' natural block-sizes. This is the spec's
        //     "max-content cross-size" simplification for the L1 default
        //     single-line case (CSS Flexbox L1 §9.4). The full spec uses
        //     the line's largest hypothetical cross size after the
        //     min/max-content cascade; sub-cycle L4+ will refine.
        var alignItems = _rootBox.Style.ReadAlignItems();
        var heightSlot = _rootBox.Style.Get(PropertyId.Height);
        double containerCrossSize;
        if (heightSlot.Tag == ComputedSlotTag.LengthPx)
        {
            containerCrossSize = heightSlot.AsLengthPx();
        }
        else
        {
            // height: auto (or any non-length slot) — use max(item natural block-size).
            // The block-level filter mirrors the row-packing loop so the
            // max only counts items the emission loop will actually emit.
            var maxItemCross = 0.0;
            for (var i = 0; i < _rootBox.Children.Count; i++)
            {
                var c = _rootBox.Children[i];
                if (!c.IsBlockLevel) continue;
                var ih = c.Style.ReadLengthPxOrZero(PropertyId.Height);
                if (ih > maxItemCross) maxItemCross = ih;
            }
            containerCrossSize = maxItemCross;
        }

        // The main-axis cursor walks from the container's content-
        // inline-start edge plus the alignment start-offset across each
        // item's natural inline-size. Between-spacing extends each
        // post-item advance.
        var mainCursor = _contentInlineOffset + startOffset;

        for (var itemIdx = 0; itemIdx < _rootBox.Children.Count; itemIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = _rootBox.Children[itemIdx];

            // Per cycle 1 (Hello World) — skip non-block-level
            // children. CSS Flexbox L1 §4 says any in-flow child
            // (block OR inline, including text runs) becomes a flex
            // item (text is wrapped in an anonymous flex item).
            // Cycle 1 punts on the anonymous-flex-item wrapping +
            // ignores inline-level children to keep scope tight; the
            // canonical Hello-World case is a flex container with
            // explicit block-level <div> children. Sub-cycle 2+ will
            // process inline runs through the anonymous-flex-item
            // path (mirroring how BoxBuilder synthesizes
            // AnonymousBlock wrappers for inline runs in block flow).
            // Production BoxBuilder also inserts TextRun children for
            // whitespace between flex item elements; cycle 1 skips
            // those silently so the canonical case works end-to-end.
            if (!item.IsBlockLevel)
            {
                continue;
            }

            // Per cycle 1 — item's natural inline-size = item's
            // declared `width` if set, else 0. The cycle-1
            // BlockLayouter contract returns 0 for `width: auto`;
            // production callers can declare an explicit width to
            // exercise the layout. Cycle 2+ will derive a real
            // intrinsic / flex-basis size.
            var itemInlineSize = item.Style.ReadLengthPxOrZero(PropertyId.Width);

            // Per cycle 1 — item's natural block-size = item's
            // declared `height` if set, else 0. Cycle 2+ will derive
            // a content-based block-size; L3's ComputeAlignItemsPlacement
            // applies the stretch override below when itemBlockSize == 0
            // (= the item's auto cross-size).
            var itemBlockSize = item.Style.ReadLengthPxOrZero(PropertyId.Height);

            // Per Phase 3 Task 15 L3 — cross-axis placement per CSS Box
            // Alignment L3 §6 + CSS Flexbox L1 §8.3. The helper returns
            // (a) the item's block-axis offset for the BoxFragment +
            // (b) the EFFECTIVE cross-axis size (= stretch overrides
            // auto-sized items to the container's cross extent; all
            // other values keep the item's declared block-size). The
            // L1 + L2 behavior was equivalent to always emitting at
            // (_contentBlockOffset, itemBlockSize) — this preserves
            // that for the legacy flex-start path while opening up the
            // full alignment matrix.
            var (itemBlockOffset, itemEffectiveCrossSize) = ComputeAlignItemsPlacement(
                alignItems.Value, alignItems.Mode,
                containerCrossSize, itemBlockSize, _contentBlockOffset);

            _sink.Emit(new BoxFragment(
                Box: item,
                InlineOffset: mainCursor,
                BlockOffset: itemBlockOffset,
                InlineSize: itemInlineSize,
                BlockSize: itemEffectiveCrossSize));

            // Advance the main-axis cursor past this item + the
            // L2 between-spacing. With flex-wrap: nowrap the cursor
            // walks past _contentInlineSize for items that don't fit —
            // overflow is the spec'd behavior for nowrap (CSS Flexbox
            // L1 §6 + §9.4). The between-spacing only contributes for
            // the distribution values (space-between / -around /
            // -evenly); position values leave it at 0.
            mainCursor += itemInlineSize + betweenSpacing;
        }

        // Cycle 1 (Hello World) — flex container is atomic to outer
        // pagination; all items committed on this page. No
        // continuation.
        return LayoutAttemptResult.AllDone(cost: 0);
    }

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

    /// <summary>Per Phase 3 Task 15 L3 — compute the per-item cross-
    /// axis placement (block-axis offset + effective block-size) per
    /// CSS Box Alignment L3 §6 + CSS Flexbox L1 §8.3. The returned
    /// tuple is consumed by AttemptLayout's row-packing loop directly
    /// inside the per-item <c>BoxFragment</c> emission.
    ///
    /// <para><b>Stretch.</b> Per CSS Flexbox L1 §8.3 + §7.2, when the
    /// container's <c>align-items</c> resolves to <c>stretch</c> (= the
    /// computed default for <c>normal</c>), an item's cross-axis size
    /// is resized to fill the container's cross extent IF the item's
    /// own cross-axis size is <c>auto</c>. The L3 implementation uses
    /// <c>itemCrossSize &gt; 0</c> as the proxy for "explicit cross-
    /// size declared" — items with a declared <c>height</c> (a non-
    /// zero LengthPx slot) keep their declared height; auto-height
    /// items (= the cycle-1 reader returns 0) get resized to
    /// <paramref name="containerCrossSize"/>. The block-axis offset
    /// for stretch is always <paramref name="contentBlockOffset"/>
    /// (= cross-start; stretch by definition fills the container so
    /// the item starts at the cross-start edge).</para>
    ///
    /// <para><b>Positional alignment.</b> For <see cref="AlignItemsValue.FlexStart"/>,
    /// <see cref="AlignItemsValue.FlexEnd"/>, and <see cref="AlignItemsValue.Center"/>
    /// the cross-axis space is <c>containerCrossSize - itemCrossSize</c>;
    /// the natural offset is then <c>contentBlockOffset</c> (start),
    /// <c>contentBlockOffset + crossSpace</c> (end), or
    /// <c>contentBlockOffset + crossSpace / 2</c> (center). Items keep
    /// their declared cross-size — positional alignment never resizes,
    /// it only positions.</para>
    ///
    /// <para><b>Overflow handling per CSS Box Alignment L3 §5.3.</b>
    /// Mirrors the L2 pattern in <see cref="ComputeJustifyContentOffsets"/>:
    /// <list type="bullet">
    ///   <item><see cref="OverflowAlignmentMode.Safe"/> on overflow
    ///   (= negative crossSpace) — fall back to safe-start (=
    ///   <paramref name="contentBlockOffset"/>) regardless of value.
    ///   Items pack at the container's cross-start edge + overflow off
    ///   the cross-end. On non-overflow the modifier is transparent.</item>
    ///   <item><see cref="OverflowAlignmentMode.Unsafe"/> on overflow —
    ///   honor the natural offset even if negative; items may be
    ///   pushed past the container's start edge.</item>
    ///   <item><see cref="OverflowAlignmentMode.Default"/> on overflow
    ///   — positional values keep their natural (possibly-negative)
    ///   offset, allowing items to overflow EQUALLY on both sides for
    ///   <c>center</c> etc. (Unlike L2's distribution values for
    ///   justify-content, align-items has no distribution values in
    ///   L3 scope; all bare values are positional + share the
    ///   "natural offset on overflow" branch.)</item>
    /// </list></para>
    ///
    /// <para><b>Stretch is overflow-immune.</b> The stretch branch
    /// returns BEFORE the overflow check: stretch by definition resizes
    /// the item to fit the container, so crossSpace is always non-
    /// negative AT the emission point (= the item's effective cross-
    /// size IS the containerCrossSize when auto). Authoring
    /// <c>safe stretch</c> / <c>unsafe stretch</c> compounds is admitted
    /// by the keyword resolver but has no behavioral effect — stretch
    /// can't overflow.</para></summary>
    private static (double itemBlockOffset, double itemEffectiveCrossSize)
        ComputeAlignItemsPlacement(
            AlignItemsValue value,
            OverflowAlignmentMode mode,
            double containerCrossSize,
            double itemCrossSize,
            double contentBlockOffset)
    {
        // Stretch — auto-sized items get resized to fill the container's
        // cross extent; explicitly-sized items keep their declared size
        // per CSS Flexbox L1 §7.2 (the item's `height` property wins
        // over the container's `align-items: stretch`).
        if (value == AlignItemsValue.Stretch)
        {
            var effectiveCross = itemCrossSize > 0 ? itemCrossSize : containerCrossSize;
            return (contentBlockOffset, effectiveCross);
        }

        // Positional alignment — compute the natural offset for the
        // value (ignoring overflow for now).
        var crossSpace = containerCrossSize - itemCrossSize;
        var natural = value switch
        {
            AlignItemsValue.FlexEnd => contentBlockOffset + crossSpace,
            AlignItemsValue.Center => contentBlockOffset + crossSpace / 2.0,
            _ => contentBlockOffset,  // FlexStart
        };

        // Overflow handling per CSS Box Alignment L3 §5.3. Only the
        // `crossSpace < 0` branch applies the overflow rules; non-
        // negative crossSpace falls through to the natural return.
        if (crossSpace < 0)
        {
            // Explicit `safe` modifier — always fall back to safe-start
            // (= contentBlockOffset) regardless of value. Items pack at
            // the container's cross-start edge + overflow off the
            // cross-end.
            if (mode == OverflowAlignmentMode.Safe)
            {
                return (contentBlockOffset, itemCrossSize);
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
