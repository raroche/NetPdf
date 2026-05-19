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
/// <c>flex-direction: row</c>, the block axis for <c>flex-direction:
/// column</c> per Phase 3 Task 15 L4).
///
/// <para><b>Cycle 1 (Hello World) scope.</b> The layouter implements
/// the absolute minimum end-to-end path so subsequent cycles have a
/// scaffold to grow:</para>
/// <list type="bullet">
///   <item><b>Per Phase 3 Task 15 L4</b> — <c>flex-direction: row</c>
///   (L1-L3 default) and <c>flex-direction: column</c> (L4 new) are
///   both honored. For <c>row</c>: main = inline axis (horizontal);
///   cross = block axis (vertical); items flow left-to-right. For
///   <c>column</c>: main = block axis (vertical); cross = inline axis
///   (horizontal); items stack top-to-bottom. The axis-mapping layer
///   (see <c>GetAxisProperties</c> below) translates the spec-abstract
///   "main / cross" terminology to the property IDs the layouter reads
///   (<c>PropertyId.Width</c> / <c>Height</c>) at each direction.
///   <c>justify-content</c> always controls main-axis packing (so
///   column direction makes it govern block-axis offsets), and
///   <c>align-items</c> always controls cross-axis placement (so
///   column direction makes it govern inline-axis offsets). The
///   reversed variants <c>row-reverse</c> + <c>column-reverse</c>
///   are decoded by <c>ReadFlexDirection</c> but the layouter
///   currently treats them as their non-reversed counterparts; the
///   reversal of item order is L5+ scope per
///   <c>docs/deferrals.md#flex-layouter-features</c>.</item>
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

        // Per Phase 3 Task 15 L4 — resolve flex-direction. L1-L3 default
        // is `row` (main = inline axis); L4 adds `column` (main = block
        // axis). The reversed variants (`row-reverse` / `column-reverse`)
        // are decoded but currently treated as their non-reversed
        // counterparts — item-order reversal is L5+ scope per
        // docs/deferrals.md#flex-layouter-features.
        var flexDirection = _rootBox.Style.ReadFlexDirection();
        var isColumn = flexDirection.IsFlexColumnDirection();
        var (mainSizeProperty, crossSizeProperty) = GetAxisProperties(flexDirection);

        // Resolve the container's main-axis + cross-axis content extents
        // + offsets. For row direction the main axis is the inline axis
        // (= _contentInlineSize / _contentInlineOffset); the cross axis
        // is the block axis. For column direction the assignment swaps.
        var containerMainSize = isColumn ? _contentBlockSize : _contentInlineSize;
        var contentMainOffset = isColumn ? _contentBlockOffset : _contentInlineOffset;
        var contentCrossOffset = isColumn ? _contentInlineOffset : _contentBlockOffset;

        // L2 — first pass: count block-level items + sum their natural
        // main-axis sizes so the alignment math knows the free-space + N.
        // Mirrors the row-packing loop's filter (only block-level
        // children participate in L1+L2+L3) so the count matches the
        // emission pass below. The two-pass design is necessary because
        // start-offset + between-spacing both depend on N AND total
        // item size, neither of which is known until the items are
        // walked. The double pass is O(N) overall — same complexity
        // class as cycle 1.
        var itemCount = 0;
        var totalItemMainSize = 0.0;
        for (var itemIdx = 0; itemIdx < _rootBox.Children.Count; itemIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = _rootBox.Children[itemIdx];
            if (!item.IsBlockLevel) continue;
            itemCount++;
            totalItemMainSize += item.Style.ReadLengthPxOrZero(mainSizeProperty);
        }

        // L2 — resolve justify-content + compute the main-axis
        // start-offset + between-spacing per CSS Box Alignment L3 §4.5.
        // The math is direction-agnostic (operates on abstract main-
        // axis free-space + N); only the CURSOR-axis interpretation at
        // the emission site changes between row + column.
        var resolvedJC = _rootBox.Style.ReadJustifyContent();
        var freeSpace = containerMainSize - totalItemMainSize;
        var (startOffset, betweenSpacing) = ComputeJustifyContentOffsets(
            resolvedJC.Value, resolvedJC.Mode, freeSpace, itemCount);

        // Per Phase 3 Task 15 L3 + L4 — resolve align-items + derive
        // the container's cross-axis extent so the per-item placement
        // loop can position items along the cross axis.
        //
        // containerCrossSize derivation per direction:
        //   - Row: cross = block axis. Explicit `height` → use it;
        //     else max(item natural block-size).
        //   - Column: cross = inline axis. Explicit `width` → use it;
        //     else max(item natural inline-size).
        //
        // For the column-direction explicit-cross-size case, the
        // wrapper's content-inline-size IS the resolved width (the
        // BlockLayouter dispatch has already sized the wrapper); we use
        // _contentInlineSize directly without re-reading the style slot.
        // For the row-direction case we still consult the style slot
        // because the wrapper's content-block-size may have been padded
        // by remaining-fragmentainer derivation; reading the explicit
        // declared height matches the spec.
        var alignItems = _rootBox.Style.ReadAlignItems();
        double containerCrossSize;
        if (isColumn)
        {
            // Column direction — cross axis = inline axis.
            // For column direction the wrapper's resolved inline-size
            // IS the container's cross extent in all cases (block-flow
            // sizing always assigns the wrapper an inline-size, whether
            // from declared `width` or the available range). The
            // explicit-width-vs-auto distinction at the WRAPPER level
            // is invisible to the inner layouter — _contentInlineSize
            // is the authoritative cross extent.
            //
            // EXCEPTION: when the wrapper's resolved inline-size is the
            // default available-range fallback (a wide value like the
            // fragmentainer's contentInlineSize), authors using
            // `align-items: stretch` would get items stretched across
            // hundreds of pixels even when the natural max-item-inline-
            // size is much smaller. Per CSS Flexbox L1 §9.4 the spec's
            // simplification for `width: auto` is max(item natural
            // inline-size) — same shape as the row-direction `height:
            // auto` fallback. We detect "the wrapper has no explicit
            // width" by reading the style slot; when missing AND
            // _contentInlineSize is wider than max(item inline-size),
            // fall back to the max for the column direction's cross
            // derivation. (When the wrapper has an explicit width, the
            // declared length already constrained the layout so honor
            // _contentInlineSize.)
            var widthSlot = _rootBox.Style.Get(PropertyId.Width);
            if (widthSlot.Tag == ComputedSlotTag.LengthPx)
            {
                // Explicit width — _contentInlineSize already reflects
                // it (BlockLayouter inline-sizing). Use directly.
                containerCrossSize = _contentInlineSize;
            }
            else
            {
                // width: auto — derive cross from max(item natural
                // inline-size). Mirrors the row-direction `height: auto`
                // path's max(item block-size) derivation per
                // CSS Flexbox L1 §9.4.
                var maxItemCross = 0.0;
                for (var i = 0; i < _rootBox.Children.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var c = _rootBox.Children[i];
                    if (!c.IsBlockLevel) continue;
                    var iw = c.Style.ReadLengthPxOrZero(PropertyId.Width);
                    if (iw > maxItemCross) maxItemCross = iw;
                }
                containerCrossSize = maxItemCross;
            }
        }
        else
        {
            // Row direction — cross axis = block axis.
            var heightSlot = _rootBox.Style.Get(PropertyId.Height);
            if (heightSlot.Tag == ComputedSlotTag.LengthPx)
            {
                containerCrossSize = heightSlot.AsLengthPx();
            }
            else
            {
                // height: auto (or any non-length slot) — use max(item
                // natural block-size). The block-level filter mirrors
                // the main-axis loop so the max only counts items the
                // emission loop will actually emit. Per Phase 3 Task 15
                // L3 post-PR-#63 hardening F#6 — check cancellation
                // between items.
                var maxItemCross = 0.0;
                for (var i = 0; i < _rootBox.Children.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var c = _rootBox.Children[i];
                    if (!c.IsBlockLevel) continue;
                    var ih = c.Style.ReadLengthPxOrZero(PropertyId.Height);
                    if (ih > maxItemCross) maxItemCross = ih;
                }
                containerCrossSize = maxItemCross;
            }
        }

        // The main-axis cursor walks from the container's content-
        // main-start edge plus the alignment start-offset across each
        // item's natural main-size. Between-spacing extends each
        // post-item advance. For row direction the cursor is an
        // inline-axis position; for column direction it's a block-axis
        // position.
        var mainCursor = contentMainOffset + startOffset;

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

            // Per L4 — read item sizes via direction-resolved property
            // IDs. For row: mainSize = width, crossSize = height. For
            // column: mainSize = height, crossSize = width. The cycle-1
            // BlockLayouter contract returns 0 for `auto` lengths;
            // production callers can declare explicit sizes to exercise
            // the layout. Sub-cycle 2+ will derive intrinsic /
            // flex-basis sizes.
            var itemMainSize = item.Style.ReadLengthPxOrZero(mainSizeProperty);
            var itemCrossSize = item.Style.ReadLengthPxOrZero(crossSizeProperty);
            // Per Phase 3 Task 15 L3 post-PR-#63 hardening F#2 + L4 —
            // the stretch branch needs to distinguish `auto` (Unset /
            // Keyword slot) from explicit `0` (LengthPx slot with value
            // 0). The cross-axis property is direction-dependent —
            // IsCrossSizeAuto receives the resolved direction.
            var itemIsCrossSizeAuto = IsCrossSizeAuto(item, flexDirection);

            // Cross-axis placement per CSS Box Alignment L3 §6 + CSS
            // Flexbox L1 §8.3. The helper returns abstract (cross-
            // offset, effective-cross-size); the caller maps to the
            // correct physical axis below based on direction.
            var (itemCrossOffset, itemEffectiveCrossSize) = ComputeAlignItemsPlacement(
                alignItems.Value, alignItems.Mode,
                containerCrossSize, itemCrossSize, itemIsCrossSizeAuto,
                contentCrossOffset);

            // Map the abstract (main, cross) tuple to the physical
            // (inline, block) axes per direction.
            double inlineOffset, blockOffset, inlineSize, blockSize;
            if (isColumn)
            {
                // Column: main = block, cross = inline.
                blockOffset = mainCursor;
                inlineOffset = itemCrossOffset;
                blockSize = itemMainSize;
                inlineSize = itemEffectiveCrossSize;
            }
            else
            {
                // Row: main = inline, cross = block.
                inlineOffset = mainCursor;
                blockOffset = itemCrossOffset;
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
            // L2 between-spacing. With flex-wrap: nowrap the cursor
            // walks past containerMainSize for items that don't fit —
            // overflow is the spec'd behavior for nowrap (CSS Flexbox
            // L1 §6 + §9.4). The between-spacing only contributes for
            // the distribution values (space-between / -around /
            // -evenly); position values leave it at 0.
            mainCursor += itemMainSize + betweenSpacing;
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
    ///   <c>fit-content</c> — see L5+ refinement note below) → auto.</item>
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
    /// <para><b>Sub-cycle L5+ scope</b> — distinguishing the <c>auto</c>
    /// keyword from <c>min-content</c> / <c>max-content</c> / <c>fit-content</c>
    /// requires reading the keyword payload + cross-referencing the
    /// property-specific keyword table. For L4 hardening all Keyword
    /// tags on width / height resolve as auto (the most common case
    /// + the others behave similarly for stretch in the L4 simplification).</para>
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
    /// — reversal of item order is orthogonal to the row/column axis
    /// swap and is L5+ scope per
    /// <c>docs/deferrals.md#flex-layouter-features</c>.</summary>
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
