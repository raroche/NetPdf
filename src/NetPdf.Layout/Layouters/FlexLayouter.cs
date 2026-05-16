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
///   <item>Only <c>justify-content: normal</c> / <c>flex-start</c>
///   (default packing). Items pack at the main-start (= the
///   container's content-inline-start edge). Other values are
///   sub-cycle 2+ scope.</item>
///   <item>Only <c>align-items: normal</c> / <c>stretch</c> (default).
///   Cycle 1 emits each item at the container's content-block-start
///   edge regardless of <c>align-items</c> — equivalent to
///   <c>flex-start</c> for non-stretch values and visually identical
///   to <c>stretch</c> when no cross-axis size constraint is in play.
///   Real <c>stretch</c> + baseline alignment are sub-cycle 2+ scope.</item>
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
        //   justify-content: flex-start (pack at main-start)
        //   align-items: stretch (cycle 1 emits at content-block-start
        //                         regardless of the value)
        // Cycle 2+ will read the actual computed values from
        //   _rootBox.Style for PropertyId.FlexDirection / FlexWrap /
        //   JustifyContent / AlignItems.

        // The main-axis cursor walks from the container's content-
        // inline-start edge across each item's natural inline-size.
        // Mirror's MulticolLayouter's column-cursor pattern but
        // without the per-column subdivision: cycle 1 packs every
        // item at flex-start in a single line.
        var mainCursor = _contentInlineOffset;

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
            // a content-based block-size and honor align-items:
            // stretch.
            var itemBlockSize = item.Style.ReadLengthPxOrZero(PropertyId.Height);

            // Emit the per-item BoxFragment at the cursor + the
            // container's content-block-start. Cycle 1 always uses
            // contentBlockOffset for the block-axis (= align-items:
            // flex-start equivalent regardless of computed value);
            // cycle 2+ will honor align-items: center / end /
            // baseline / stretch.
            _sink.Emit(new BoxFragment(
                Box: item,
                InlineOffset: mainCursor,
                BlockOffset: _contentBlockOffset,
                InlineSize: itemInlineSize,
                BlockSize: itemBlockSize));

            // Advance the main-axis cursor past this item. With
            // flex-wrap: nowrap the cursor walks past _contentInlineSize
            // for items that don't fit — overflow is the spec'd
            // behavior for nowrap (CSS Flexbox L1 §6 + §9.4).
            mainCursor += itemInlineSize;
        }

        // Cycle 1 (Hello World) — flex container is atomic to outer
        // pagination; all items committed on this page. No
        // continuation.
        return LayoutAttemptResult.AllDone(cost: 0);
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
