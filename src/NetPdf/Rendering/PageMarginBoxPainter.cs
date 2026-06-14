// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using AngleSharp.Dom;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.PagedMedia;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;
using NetPdf.Diagnostics;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Inline;
using NetPdf.Layout.Layouters;
using NetPdf.Pdf;
using NetPdf.Shaping;
using NetPdf.Text.Bidi;

namespace NetPdf.Rendering;

/// <summary>
/// Lays out CSS Paged Media L3 §6.4 page margin boxes (running headers/footers) into
/// <see cref="BoxFragment"/>s the shared <see cref="TextPainter"/> pass paints, plus the background
/// bands the pipeline fills behind them. Phase 3 Task 21 cycle 3 (content) … cycle 4–7 (per-box
/// style: font/color, inheritance, the <c>font</c> shorthand, relative sizes) + cycle 8
/// (<c>background-color</c>) — the keystone for headers/footers.
/// </summary>
/// <remarks>
/// <para>
/// Takes the margin boxes <see cref="AtPageMarginBoxResolver"/> resolved (name + raw <c>content</c>
/// + declarations), resolves each <c>content</c> to text (literal strings + <c>attr()</c> via
/// <see cref="CssContentList"/>), builds the box's <see cref="ComputedStyle"/> from its declared
/// longhands (<see cref="MarginBoxStyle"/>), computes its page-box region via
/// <see cref="PageMarginBoxGeometry"/>, and lays the text out as one line with
/// <see cref="InlineLayouter"/> in that style — returning the fragments rather than painting them.
/// The pipeline appends these to the body fragments and runs ONE <see cref="TextPainter.PaintText"/>
/// over both, so a font used by body text AND a header/footer is subset + embedded ONCE
/// (post-PR-#132 review P3). Fragment offsets are made relative to the shared content origin.
/// </para>
/// <para>
/// <b>Style (cycles 4–8 + border + padding + explicit size).</b> The box's declared <c>font-family</c> /
/// <c>font-size</c> / <c>font-weight</c> / <c>font-style</c> / <c>color</c> flow through the shaper +
/// painter (inherited along root → page context → box; relative sizes + the <c>font</c> shorthand
/// resolved); a declared <c>text-align</c> / <c>vertical-align</c> aligns the line WITHIN the box's
/// content area — the box's PLACEMENT in its edge band stays its §5.3.2.4 name-derived role
/// (start/center/end). A declared <c>background-color</c> (cycle 8, non-inherited) fills the box's
/// region behind the content — collected as a <see cref="MarginBoxBackgroundFill"/>, painted by
/// <see cref="PaintBackgrounds"/>; a declared <c>border</c> (non-inherited, the <c>border</c> /
/// <c>border-&lt;side&gt;</c> shorthands expanded for margin-box bodies) strokes the box's
/// region — collected as a <see cref="MarginBoxBorder"/>, painted by <see cref="PaintBorders"/> via
/// the shared <c>FragmentPainter.PaintBorders</c>; a declared <c>padding</c> (non-inherited,
/// the 1–4-value box shorthand + per-side longhands) insets the text content origin, together with the
/// used border-width per side (CSS box model); a declared <c>width</c> (top/bottom) / <c>height</c>
/// (left/right) sizes the box along its §5.3 variable axis (explicit-size cycle — see <see cref="Layout"/>).
/// Unspecified properties fall back to the reader defaults
/// (16px / default family / black / transparent / no border / no padding / auto size / name-derived alignment).
/// The <c>border-width</c>/<c>-style</c>/<c>-color</c> box shorthands distribute across the edges as
/// well. A single uniform absolute <c>border-radius</c> ROUNDS the background band (border-radius
/// cycle); rounded border STROKES, per-corner/elliptical/relative radii, the body path, and
/// background images stay tracked follow-ups (deferrals.md#layout-to-pdf-pipeline).
/// </para>
/// <para>
/// <b>Content scope.</b> Literal-string + <c>attr()</c> content, plus <c>counter(page)</c> /
/// <c>counter(pages)</c> page numbers (cycle 9 — resolved against the page being painted; a single
/// page renders <c>1</c> until the multi-page driver lands), <c>string(name)</c> (Task 22 — the named
/// string set by <c>string-set</c>), and <c>element(name)</c> (Task 23 — the text of a
/// <c>position: running()</c> element, rendered in the element's OWN font/color, and for a STANDALONE
/// <c>element(name)</c> also its OWN <c>background-color</c> + <c>border-*</c> + <c>padding-*</c> +
/// <c>text-align</c> cascaded under the box's own declarations — full-block first cut); both resolved from <c>marginContext</c> (a
/// single-page first cut; cross-page "running" persistence is deferred). A winning <c>none</c> / <c>normal</c> suppresses
/// the box upstream (the resolver omits it). Still-unsupported content (a non-page <c>counter()</c> /
/// <c>counters()</c>) emits <c>CSS-CONTENT-FUNCTION-UNSUPPORTED-001</c> and the box is skipped. §5.3 three-box-per-edge sizing
/// is shrink-to-fit + explicit <c>width</c>/<c>height</c> + the §5.3.2 overlap DISTRIBUTION
/// (<see cref="ResolveEdgeOverlaps"/> — the centre box stays centred, flexed against the imaginary
/// <c>2 × max(A, C)</c> box, with the sides sized in the gaps; no centre box → the sides flex, or go
/// proportional to min-content when the mins don't fit; a flexed/shrunk box re-wraps its content to fit,
/// honouring the box's computed white-space — CASCADED + inherited as a <see cref="MarginBoxStyle"/>
/// longhand (white-space cycle), so a declared <c>nowrap</c>/<c>pre</c> keeps a rigid single line. An
/// explicit size is content- or border-box per the box's
/// <c>box-sizing</c> (box-sizing cycle); a VERTICAL (left/right) or CORNER box's content WRAPS at its
/// fixed band/corner width (vertical-wrap cycle). Wrapped lines are aligned PER LINE by the box's
/// alignment (Task 21 — via the fragment's <c>LineAlignFactor</c>). OVERFLOW is CLIPPED + DIAGNOSED
/// (<c>PAINT-MARGIN-BOX-CONTENT-OVERFLOW-001</c>): height overflow at line granularity
/// (overflow-clipping cycle), horizontal glyph overflow at the padding-box edge via a PDF clip path
/// (clip-path cycle — the fragment's <c>ClipRect</c>); an explicit <c>overflow: visible</c> on the box
/// opts out of all of it.
/// </para>
/// </remarks>
internal static class PageMarginBoxPainter
{
    private const double NormalLineHeightFactor = 1.2; // mirrors TextPainter's line-height: normal.
    private const uint DefaultColorArgb = 0xFF000000;   // CSS initial `color` (opaque black) — currentColor fallback.
    private const double OverflowEpsilonPx = 0.5;       // content-height overflow tolerance (sub-px rounding slack).
    private const int BoxSizingBorderBox = 1;           // KeywordResolver box-sizing table: 0 = content-box, 1 = border-box.

    /// <summary>A background band to fill behind a margin box: its page-px region rect (CSS-px,
    /// page-top origin) + the resolved packed 0xAARRGGBB color. Painted by
    /// <see cref="PaintBackgrounds"/> before the shared text pass, so the text paints over it.</summary>
    internal readonly record struct MarginBoxBackgroundFill(
        double LeftPx, double TopPx, double WidthPx, double HeightPx, uint Argb,
        CornerRadii Radii = default); // per-corner border-radius (margin-box-border-radius cycle); default = square.

    /// <summary>A margin box's declared borders to stroke around its region rect (border cycle):
    /// the page-px rect + the box's <see cref="ComputedStyle"/> (carrying the <c>border-*</c>
    /// longhands) + the PER-EDGE <c>currentcolor</c> fallbacks (per-edge currentcolor cycle — each
    /// edge falls back to its OWNER's colour: the box's when the box declares that edge, else the
    /// running element's). Painted by <see cref="PaintBorders"/> via the shared
    /// <c>FragmentPainter.PaintBorders</c>.</summary>
    internal readonly record struct MarginBoxBorder(
        double LeftPx, double TopPx, double WidthPx, double HeightPx, ComputedStyle Style,
        FragmentPainter.BorderEdgeCurrentColors CurrentColors);

    /// <summary>A margin box's declared <c>background-image: url(...)</c> (margin-box-bg-image
    /// cycle): the band rect (the SAME border-box rect its color fill uses) + the RAW url +
    /// the RAW <c>background-repeat</c>/<c>-size</c>/<c>-position</c> winners (PR #167 review
    /// P1 — margin-box declarations are raw CSS, so the authored values arrive intact; null =
    /// unset → the initial). The pipeline resolves the url against the per-render image cache
    /// and tiles it over the rect via the SHARED body tiler
    /// (<c>PrintBackgrounds</c>-gated like the fill). A declared border-radius now ROUNDS the tile clip
    /// (margin-box-border-radius cycle): <see cref="ClipRadiiPx"/> carries the per-corner radii of the
    /// background-clip box (the box radii inset to it), and the shared tiler clips to a rounded path.
    /// <c>LeftPx..HeightPx</c> is the background-origin (positioning) area;
    /// <c>ClipLeftPx..ClipHeightPx</c> the background-clip (paint) rect (bg-origin / bg-clip cycles).</summary>
    internal readonly record struct MarginBoxBackgroundImage(
        double LeftPx, double TopPx, double WidthPx, double HeightPx, string RawUrl,
        string? RepeatRaw, string? SizeRaw, string? PositionRaw,
        double ClipLeftPx, double ClipTopPx, double ClipWidthPx, double ClipHeightPx,
        CornerRadii ClipRadiiPx = default); // per-corner border-radius of the clip box (margin-box-border-radius cycle).

    /// <summary>The result of laying out the page margin boxes: the text fragments (for the shared
    /// <see cref="TextPainter"/> pass) + the background bands (cycle 8) + the borders (border cycle)
    /// + the background images (margin-box-bg-image cycle).</summary>
    internal sealed record MarginBoxLayoutResult(
        IReadOnlyList<BoxFragment> Fragments,
        IReadOnlyList<MarginBoxBackgroundFill> Backgrounds,
        IReadOnlyList<MarginBoxBorder> Borders,
        IReadOnlyList<MarginBoxBackgroundImage> BackgroundImages)
    {
        public static readonly MarginBoxLayoutResult Empty = new(
            Array.Empty<BoxFragment>(), Array.Empty<MarginBoxBackgroundFill>(), Array.Empty<MarginBoxBorder>(),
            Array.Empty<MarginBoxBackgroundImage>());
    }

    /// <summary>Lay out every resolved margin box's content into a fragment positioned for the
    /// shared text pass. Returns an empty list when nothing paints.</summary>
    /// <param name="boxes">Margin boxes resolved by <see cref="AtPageMarginBoxResolver"/>.</param>
    /// <param name="pageWidthPx">Resolved page width (CSS px).</param>
    /// <param name="pageHeightPx">Resolved page height (CSS px).</param>
    /// <param name="marginTopPx">Resolved top page margin (CSS px) — the band the top boxes live in.</param>
    /// <param name="marginRightPx">Resolved right page margin (CSS px).</param>
    /// <param name="marginBottomPx">Resolved bottom page margin (CSS px).</param>
    /// <param name="marginLeftPx">Resolved left page margin (CSS px).</param>
    /// <param name="contentOriginLeftPx">The left content origin the shared <see cref="TextPainter"/>
    /// pass uses (the body's left margin). Fragment <c>InlineOffset</c>s are made relative to it.</param>
    /// <param name="contentOriginTopPx">The top content origin (the body's top margin).</param>
    /// <param name="host">A document element for <c>attr()</c> resolution (the box tree's root
    /// element). <c>attr()</c> against page furniture has no real element, so this is an
    /// approximation; literal content ignores it.</param>
    /// <param name="rootElementStyle">The document root element's resolved style — the top of the
    /// CSS Page 3 inheritance chain (root → page context → margin box). <see langword="null"/> for
    /// an element-free document.</param>
    /// <param name="pageDeclarations">The bare <c>@page</c> rules' own declarations
    /// (<see cref="AtPageMarginBoxResolver.PageContextDeclarations"/>) — the page-context style the
    /// margin boxes inherit from.</param>
    /// <param name="pageCounters">Page counters for <c>counter(page)</c> / <c>counter(pages)</c>
    /// content — the page being painted's number + the total (cycle 9; single page → (1, 1)).</param>
    /// <param name="marginContext">Named strings (<c>string-set</c> → <c>string(name)</c>, Task 22) +
    /// running-element text (<c>position: running()</c> → <c>element(name)</c>, Task 23) collected from
    /// the document — see <see cref="MarginContentCollector"/>.</param>
    /// <param name="shaper">The SAME resolver the body shaped with, kept alive past layout.</param>
    /// <param name="diagnostics">Sink for unsupported-content / unresolved-font diagnostics.</param>
    public static MarginBoxLayoutResult Layout(
        ImmutableArray<AtPageMarginBoxResolver.ResolvedMarginBox> boxes,
        double pageWidthPx, double pageHeightPx,
        double marginTopPx, double marginRightPx, double marginBottomPx, double marginLeftPx,
        double contentOriginLeftPx, double contentOriginTopPx,
        IElement host, ComputedStyle? rootElementStyle, ImmutableArray<CssDeclaration> pageDeclarations,
        CssContentList.PageCounters pageCounters,
        CssContentList.MarginContentContext marginContext,
        HarfBuzzShaperResolver shaper, IDiagnosticsSink diagnostics)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(shaper);
        if (boxes.IsDefaultOrEmpty) return MarginBoxLayoutResult.Empty;

        var availableInlinePx = Math.Max(pageWidthPx, 1.0); // positive + finite; NoWrap keeps one line.
        // Surface invalid margin-box style values (e.g. `color: bogus`) via the CSS diagnostic path
        // (post-PR-#133 review P3) instead of silently defaulting.
        var styleDiagnostics = new PublicDiagnosticsSinkAdapter(diagnostics);
        var bgImageUnsupportedReported = false;   // margin-box-bg-image cycle — once per Layout.
        // CSS Page 3 inheritance chain (cycle 5): root element → page context (@page declarations) →
        // each margin box. Build the page-context style once; every box inherits from it.
        var pageContextStyle = MarginBoxStyle.Build(pageDeclarations, rootElementStyle, styleDiagnostics);
        // The `rem` base for relative explicit sizes (relative-units cycle) — the root element's
        // resolved font-size, 16px when the document has none / it didn't resolve to px.
        var rootEmPx = 16.0;
        if (rootElementStyle is not null
            && rootElementStyle.Get(PropertyId.FontSize) is { Tag: ComputedSlotTag.LengthPx } rootSizeSlot)
        {
            rootEmPx = rootSizeSlot.AsLengthPx();
        }
        // Resolve a root-/viewport-relative PAGE-CONTEXT font-size (`@page { font-size: 2rem }`)
        // BEFORE the boxes inherit from it (font-size cycle): a box's parent-relative `1.5em` resolves
        // against the parent's LengthPx slot (DeferredFontResolver), which must be the used px — a
        // still-deferred parent raw would read as the 16px default.
        ResolveDeferredFontSizeInPlace(
            pageContextStyle, rootEmPx, rootEmPx, pageWidthPx, pageHeightPx, "page context", diagnostics);
        // The boxes' PARENT font-size — read AFTER the page-context resolve so a box's parent-relative
        // % / em (in a math function) scales by the page context's USED px.
        var pageContextEmPx = pageContextStyle.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16);

        // PASS 1 — compute each box's style, content layout, and DESIRED box geometry (shrink-to-fit /
        // explicit size, positioned by its name-derived role). The boxes are COLLECTED (not emitted yet)
        // so the §5.3 distribution can resolve overlapping siblings before anything paints.
        var items = new List<MarginBoxItem>(boxes.Length);
        foreach (var mb in boxes)
        {
            if (!CssContentList.TryParse(mb.ContentRawValue, host, pageCounters, marginContext, out var text))
            {
                EmitContentUnsupported(diagnostics, mb.Name, mb.ContentRawValue);
                continue;
            }

            if (!PageMarginBoxGeometry.TryGetRegion(
                    mb.Name, pageWidthPx, pageHeightPx,
                    marginTopPx, marginRightPx, marginBottomPx, marginLeftPx, out var region))
                continue; // unknown name (resolver filters these out already).

            // Per-box style from the box's declared longhands (font-* / color → shaping + fill;
            // background-color + border-* → decoration; padding + border-width → the text content-origin
            // inset). The style is box-owned, so the rented instance isn't pooled — a negligible miss.
            //
            // STANDALONE element() OWN STYLE + DECORATION (Task 23 full-block first cut): a standalone
            // `content: element(name)` makes the running element's box the margin box's content box. Its OWN
            // font/color (captured, with inherited values walked from ancestors) drive the CONTENT shaping
            // (`contentStyle`); its OWN (non-inherited) `background-color` + `border-*` + `padding-*` decorate
            // the box + inset the text, cascaded UNDER the box's own declarations (an explicit box
            // `background`/`border`/`padding` overrides) into `style`; its OWN (inherited) `text-align` aligns
            // the line (`elementHAlign`, the box's own text-align still winning). The element decoration stays
            // in `style` (NOT `contentStyle`), preserving the box/content split: the box font-size can still
            // differ from the content's, so the paint-time line metrics follow `contentStyle` (post-PR-#151
            // review P1, BoxFragment.TextMetricsStyle). Mixed / non-element() content keeps the box's own style
            // for both. APPROXIMATION: the running element's box and the margin box COINCIDE (no separately-
            // decorated nesting). Its nested BLOCK children DO render — stacked lines, 16-deep recursion,
            // per-line leaf font/colour via the segment runs below (deep-recursion + segment-style cycles);
            // real nested block LAYOUT (sub-boxes with own decoration/margins) stays deferred
            // (deferrals.md). Relative
            // units / `inherit` resolve against the page context (exact for the common absolute values).
            ComputedStyle style, contentStyle;
            double? elementHAlign = null;   // the running element's own text-align factor (Task 23), if any.
            IReadOnlyList<KeyValuePair<string, string>>? elementOwnPairs = null;
            var isStandaloneElement =
                CssContentList.TryGetStandaloneElement(mb.ContentRawValue, out var elName, out var elFirst);
            if (isStandaloneElement
                && TryGetRunningElementOwnStyle(marginContext, elName, elFirst) is { } ownPairs)
            {
                contentStyle = BuildFromOwnStyle(ownPairs, decorationOnly: false,
                    ImmutableArray<CssDeclaration>.Empty, pageContextStyle, styleDiagnostics);
                style = BuildFromOwnStyle(ownPairs, decorationOnly: true,
                    mb.Declarations, pageContextStyle, styleDiagnostics);
                elementHAlign = ElementHorizontalAlignFactor(ownPairs);
                elementOwnPairs = ownPairs;   // kept for the nested-decoration build below.
            }
            else
            {
                style = MarginBoxStyle.Build(mb.Declarations, pageContextStyle, styleDiagnostics);
                contentStyle = style;
            }

            // A still-DEFERRED root-/viewport-relative font-size (`2rem` / `5vw` — DeferredFontResolver
            // resolves only the PARENT-relative forms) resolves HERE against the root font-size / page
            // box (font-size cycle), BEFORE anything reads the font: the shaper, the line height, and
            // the `em` bases of the size/padding resolves below all see the used px.
            ResolveDeferredFontSizeInPlace(
                style, pageContextEmPx, rootEmPx, pageWidthPx, pageHeightPx, $"page margin box @{mb.Name}", diagnostics);
            if (!ReferenceEquals(contentStyle, style))
                ResolveDeferredFontSizeInPlace(
                    contentStyle, pageContextEmPx, rootEmPx, pageWidthPx, pageHeightPx,
                    $"page margin box @{mb.Name}", diagnostics);

            // SEGMENT RUNS (Task 23, segment-style cycle): a standalone element()'s stacked lines
            // each shape as their own TextRun in the LEAF block's own (ancestor-walked) font/colour
            // — an h1 title line over a styled subtitle renders heterogeneously ("real nested block
            // layout" first cut: per-line text style; per-line decoration/margins stay deferred).
            // The '\n' terminators ride inside the run texts, so the pre-line/pre layout still sees
            // the mandatory breaks. The line PITCH + TextMetricsStyle follow the LARGEST segment
            // font (uniform-pitch approximation — no line can overlap a taller neighbour; true
            // per-line pitch is the refinement). A SINGLE segment also takes this path (post-PR-#160
            // review P2: `<div class=rh><h1>…</h1></div>` records ONE styled leaf segment whose
            // font/colour must drive the shaping — the root's own-style capture sees only the
            // ROOT's declarations, not the leaf's); an unstyled segment falls back to contentStyle,
            // so flat unstyled content stays on an identical single-run layout. Non-element content
            // keeps the single-run path untouched.
            TextRun[]? contentRuns = null;
            var contentMetricsStyle = contentStyle;
            double[]? segmentLineHeightsPx = null;   // per-SEGMENT pitch (segment-pitch cycle).
            double?[]? segmentAlignFactors = null;   // per-SEGMENT own text-align (segment-align cycle).
            ComputedStyle?[]? segmentDecorStyles = null; // per-SEGMENT own decoration (segment-decor cycle).
            uint[]? segmentColorArgbs = null;        // per-SEGMENT colour — the band's currentcolor owner.
            double[]? segmentMarginTopsPx = null;    // per-SEGMENT vertical margins (segment-margins cycle).
            double[]? segmentMarginBottomsPx = null;
            double[]? segmentPaddingTopsPx = null;   // per-SEGMENT vertical padding (segment-padding cycle).
            double[]? segmentPaddingBottomsPx = null;
            double[]? segmentPaddingLeftsPx = null;  // per-SEGMENT horizontal padding (hpadding cycle).
            double[]? segmentPaddingRightsPx = null;
            double[]? segmentMarginLeftsPx = null;   // per-SEGMENT horizontal margins (segment-hmargins cycle).
            double[]? segmentMarginRightsPx = null;
            List<ContainerBand>? containerBands = null;  // nested CONTAINER bands (container-bands cycle).
            if (isStandaloneElement
                && TryGetRunningElementSegments(marginContext, elName, elFirst) is { } segs)
            {
                var runs = new TextRun[segs.Count];
                segmentLineHeightsPx = new double[segs.Count];
                segmentAlignFactors = new double?[segs.Count];
                segmentDecorStyles = new ComputedStyle?[segs.Count];
                segmentColorArgbs = new uint[segs.Count];
                segmentMarginTopsPx = new double[segs.Count];
                segmentMarginBottomsPx = new double[segs.Count];
                segmentPaddingTopsPx = new double[segs.Count];
                segmentPaddingBottomsPx = new double[segs.Count];
                segmentPaddingLeftsPx = new double[segs.Count];
                segmentPaddingRightsPx = new double[segs.Count];
                segmentMarginLeftsPx = new double[segs.Count];
                segmentMarginRightsPx = new double[segs.Count];
                var maxFontPx = 0.0;
                for (var si = 0; si < segs.Count; si++)
                {
                    var seg = segs[si];
                    var segStyle = seg.OwnStyle.Count > 0
                        ? BuildFromOwnStyle(seg.OwnStyle, decorationOnly: false,
                            ImmutableArray<CssDeclaration>.Empty, pageContextStyle, styleDiagnostics)
                        : contentStyle;
                    if (!ReferenceEquals(segStyle, contentStyle) && !ReferenceEquals(segStyle, style))
                        ResolveDeferredFontSizeInPlace(
                            segStyle, pageContextEmPx, rootEmPx, pageWidthPx, pageHeightPx,
                            $"page margin box @{mb.Name}", diagnostics);
                    var segFontPx = segStyle.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16);
                    if (segFontPx > maxFontPx)
                    {
                        maxFontPx = segFontPx;
                        contentMetricsStyle = segStyle;
                    }
                    // Per-segment metadata (segments part 2): the segment's own line pitch (its font
                    // × the normal factor — true per-line pitch replaces the max-font approximation),
                    // its own text-align factor (the box's declared text-align still WINS — see the
                    // hAlign cascade below), its own per-LINE decoration band style, and its colour
                    // (the band's currentcolor owner).
                    // Per-segment BASE pitch: the leaf's own line-height (segment-line-height
                    // cycle — read straight from the captured pairs, like text-align: an absolute
                    // length, a unitless multiplier, or an em factor; `normal`/%/other → the font
                    // default). The vertical padding (segment-padding cycle) is kept SEPARATE and
                    // applied at the segment's BOUNDARY lines by PerLineGeometry — pad-top on its
                    // FIRST line, pad-bottom on its LAST — so a WRAPPED segment pads the block
                    // once, not per line (post-PR-#164 review P2; CSS B&B §4.2 — the band still
                    // covers the padding box; glyphs centre in the padded boundary line, a
                    // documented approximation).
                    segmentLineHeightsPx[si] =
                        SegmentLineHeightPx(seg.OwnStyle, segFontPx) ?? segFontPx * NormalLineHeightFactor;
                    segmentPaddingTopsPx[si] = seg.PaddingTopPx;
                    segmentPaddingBottomsPx[si] = seg.PaddingBottomPx;
                    segmentPaddingLeftsPx[si] = seg.PaddingLeftPx;   // hpadding cycle — per-line X insets.
                    segmentPaddingRightsPx[si] = seg.PaddingRightPx;
                    segmentMarginLeftsPx[si] = seg.MarginLeftPx;     // segment-hmargins cycle — band +
                    segmentMarginRightsPx[si] = seg.MarginRightPx;   //   glyph insets (margins sit outside the border box).
                    segmentAlignFactors[si] = ElementHorizontalAlignFactor(seg.OwnStyle);
                    segmentMarginTopsPx[si] = seg.MarginTopPx;       // segment-margins cycle —
                    segmentMarginBottomsPx[si] = seg.MarginBottomPx; //   inter-line gaps below.
                    if (seg.Decoration is { Count: > 0 } segDecor)
                        segmentDecorStyles[si] = BuildFromOwnStyle(segDecor, decorationOnly: true,
                            ImmutableArray<CssDeclaration>.Empty, pageContextStyle, styleDiagnostics);
                    segmentColorArgbs[si] =
                        FragmentPainter.TryResolveColor(segStyle.Get(PropertyId.Color), DefaultColorArgb, out var segC)
                            ? segC : DefaultColorArgb;
                    runs[si] = new TextRun(
                        si < segs.Count - 1 ? seg.Text + "\n" : seg.Text, segStyle);
                }
                contentRuns = runs;

                // Nested CONTAINER bands (container-bands cycle): a decorated intermediate
                // block's band spans its descendants' segment range — built like the per-segment
                // decoration (its own colour as the band's currentcolor owner), painted between
                // the element band and the per-line leaf bands (PRE-order = outer under inner).
                if (TryGetRunningElementContainers(marginContext, elName, elFirst) is { } rawContainers)
                {
                    var built = new List<ContainerBand>(rawContainers.Count);
                    foreach (var rc in rawContainers)
                    {
                        if (rc.Decoration is not { Count: > 0 } || rc.LastSegment < rc.FirstSegment)
                            continue;   // inert record (empty recursion) or undecorated.
                        var bandStyle = BuildFromOwnStyle(rc.Decoration, decorationOnly: true,
                            ImmutableArray<CssDeclaration>.Empty, pageContextStyle, styleDiagnostics);
                        var bandColorStyle = rc.OwnStyle.Count > 0
                            ? BuildFromOwnStyle(rc.OwnStyle, decorationOnly: false,
                                ImmutableArray<CssDeclaration>.Empty, pageContextStyle, styleDiagnostics)
                            : contentStyle;
                        var bandColor = FragmentPainter.TryResolveColor(
                            bandColorStyle.Get(PropertyId.Color), DefaultColorArgb, out var cc)
                            ? cc : DefaultColorArgb;
                        built.Add(new ContainerBand(
                            bandStyle, bandColor, rc.FirstSegment, rc.LastSegment,
                            rc.MarginLeftPx, rc.MarginRightPx,
                            rc.LeadingInsidePx, rc.TrailingInsidePx));
                    }
                    if (built.Count > 0) containerBands = built;
                }
            }

            // currentcolor ORIGIN (CSS Color 4 §6.2 — currentcolor resolves to the `color` of the style that
            // OWNS the property). The box's declarations WIN the cascade (appended last), so a decoration
            // property is BOX-owned iff the box declares it, else ELEMENT-owned (post-PR-#152 review P1): a
            // box-declared `background-color`/`border` currentcolor resolves against the BOX's colour, an
            // element-declared one against the RUNNING ELEMENT's. `style.Get(Color)` is the box's colour (the
            // decoration build excludes the element's colour + appends the box's), `contentStyle` the
            // element's. For the non-element path `style == contentStyle`, so both reduce to the box colour —
            // byte-identical to the prior single currentColor. (Per-edge currentcolor
            // ownership ships in the per-edge-currentcolor cycle — see EdgeCurrentColor below.)
            var elementColor = FragmentPainter.TryResolveColor(contentStyle.Get(PropertyId.Color), DefaultColorArgb, out var ec)
                ? ec : DefaultColorArgb;
            var boxColor = FragmentPainter.TryResolveColor(style.Get(PropertyId.Color), DefaultColorArgb, out var bc)
                ? bc : DefaultColorArgb;
            var bgCurrentColor = MarginBoxDeclares(mb.Declarations, "background-color") ? boxColor : elementColor;
            // Border currentcolor is resolved PER EDGE (per-edge currentcolor cycle — refines the
            // whole-border rule): an edge falls back to the BOX's colour when the box's own
            // declarations claim it — its color longhand (the property currentcolor appears in) or,
            // failing that, its style longhand (whoever turns the edge on owns it) — else the running
            // element's. Non-element content has elementColor == boxColor, so this is uniform there.
            var borderCurrentColors = new FragmentPainter.BorderEdgeCurrentColors(
                EdgeCurrentColor(mb.Declarations, "border-top-color", "border-top-style", boxColor, elementColor),
                EdgeCurrentColor(mb.Declarations, "border-right-color", "border-right-style", boxColor, elementColor),
                EdgeCurrentColor(mb.Declarations, "border-bottom-color", "border-bottom-style", boxColor, elementColor),
                EdgeCurrentColor(mb.Declarations, "border-left-color", "border-left-style", boxColor, elementColor));

            // CONTENT alignment WITHIN the box: a text-align / vertical-align DECLARED ON THE BOX aligns
            // the line inside the box's content area (read from the box's OWN declarations so the
            // page/root's UA-default text-align can't leak in). For a standalone element() the running
            // element's OWN (inherited) text-align is the next fallback (Task 23 — so a `.rh { text-align:
            // right }` aligns its line when the box declares none), then the §5.3.2.4 name-derived default.
            // The element fallback applies ONLY when the box declares NO text-align: a box that DECLARES
            // text-align but as a CSS-wide / unrecognized keyword (HorizontalAlignFactor → null) keeps its
            // NAME-DERIVED default rather than deferring to the element (post-PR-#153 Copilot review — a
            // margin box's alignment isn't inherited, so `@top-center { text-align: inherit }` stays
            // centered, it does NOT pick up the running element's alignment). This is NOT the box's
            // PLACEMENT in the band — that's the name-derived role (region.HAlign/VAlign), applied below
            // independent of this content alignment. (Like the box's own text-align, it's observable only
            // when the content box is wider than the line — an explicit box width or wrapped content; a
            // shrink-to-fit box's content area equals its line, so alignment is a no-op.)
            double hAlign;
            if (MarginBoxStyle.HorizontalAlignFactor(mb.Declarations) is double boxHAlign)
                hAlign = boxHAlign;                                  // box declares a recognized text-align — wins
            else if (MarginBoxDeclares(mb.Declarations, "text-align"))
                hAlign = region.HAlign;                              // box declares text-align but CSS-wide/unknown → name-derived (Copilot)
            else
                hAlign = elementHAlign ?? region.HAlign;             // box silent → the element's own, then name-derived
            var vAlign = MarginBoxStyle.VerticalAlignFactor(mb.Declarations) ?? region.VAlign;

            // NESTED element decoration (separately-decorated cycle, first cut): when the BOX
            // declares its own decoration AND the running element also carries some, the element's
            // no longer vanishes under the box's cascade win — it paints as a NESTED band at the
            // element's content block (PASS 2), the box's at the box rect. A box that declares NO
            // decoration keeps the coinciding single-band behavior (the element decorates the box
            // band — byte-identical to the full-block first cut). APPROXIMATION: the nested band
            // sits at the CONTENT-box rect — the element's own border/padding share of the merged
            // insets isn't re-attributed per side (deferrals.md).
            ComputedStyle? elementDecorStyle = null;
            if (elementOwnPairs is not null
                && HasDecorationPair(elementOwnPairs)
                && BoxDeclaresDecoration(mb.Declarations))
            {
                elementDecorStyle = BuildFromOwnStyle(elementOwnPairs, decorationOnly: true,
                    ImmutableArray<CssDeclaration>.Empty, pageContextStyle, styleDiagnostics);
            }

            // Margin-box BORDER-RADIUS (margin-box-border-radius cycle): the four corner-radius longhands
            // are now cascaded onto the box's ComputedStyle (the `border-radius` shorthand expands to
            // them via BorderRadiusShorthandExpander), so the band fill, the rounded border, and the
            // image clip read PER-CORNER radii (absolute or `%`) from the style at PASS 2 — against the
            // FINAL box dimensions. The elliptical `Rx / Ry` slash form stays deferred (square fallback).
            // Margin-box background-image (margin-box-bg-image cycle) — the raw declared winner
            // (the established non-slot read); a single url(...) is carried to PASS 2 (the band
            // rect is final there); any other non-none form surfaces once per Layout. The three
            // tiling variants ride along RAW (PR #167 review P1 — margin-box bodies never pass
            // through AngleSharp, so the authored values arrive intact, no -x/-y recompose).
            var backgroundImageUrl = TryReadBackgroundImageUrl(
                mb.Declarations, diagnostics, ref bgImageUnsupportedReported);
            var backgroundRepeatRaw = RawDeclarationWinner(mb.Declarations, "background-repeat");
            var backgroundSizeRaw = RawDeclarationWinner(mb.Declarations, "background-size");
            var backgroundPositionRaw = RawDeclarationWinner(mb.Declarations, "background-position");
            // background-origin / background-clip flow through MarginBoxStyle's cascade now (they joined
            // CascadedStyleIds), so the box's ComputedStyle carries the validated keyword — respecting
            // !important, CSS-wide keywords, and invalid-value diagnostics — instead of the
            // last-declaration-only RawDeclarationWinner (post-PR-#171 review P2). Read it back as the
            // area string the shared tiler expects (unset → null → the property's initial below).
            var backgroundOriginRaw = ReadBackgroundAreaKeyword(style, PropertyId.BackgroundOrigin);
            var backgroundClipRaw = ReadBackgroundAreaKeyword(style, PropertyId.BackgroundClip);

            // Relative-size bases (relative-units cycle): `em` against the BOX's resolved font-size
            // (width/height/padding are box properties — CSS Values 4 §6.1.1), `rem` against the
            // root's, viewport units against the page box. Shared by the explicit size, calc(), and
            // the padding resolve below.
            var sizeBases = new RelativeSizeBases(
                style.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16),
                rootEmPx, pageWidthPx, pageHeightPx);

            // Padding → USED px, IN PLACE (relative-padding cycle): a percentage (against the box's
            // containing-block width — the region width on this edge, CSS B&B §8.4: all four sides use
            // the INLINE-axis base), a font-/viewport-relative length, or a calc() is resolved and the
            // slot REWRITTEN as LengthPx — so every downstream reader (the insets below, TextPainter's
            // content origin, FragmentPainter) sees the same used value. A kept-but-unresolvable raw is
            // surfaced + dropped (reads as 0).
            ResolveUsedPaddingInPlace(style, region.Width, sizeBases, mb.Name, diagnostics);

            // Content-origin insets: the used border-width + padding per side (the §4.3 used-width gate
            // makes the reserved space match what FragmentPainter strokes; TextPainter then adds the
            // same inset to land the line in the content box).
            var insetLeftPx = style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth) + style.ReadLengthPxOrZero(PropertyId.PaddingLeft);
            var insetTopPx = style.ReadLengthPxOrZero(PropertyId.BorderTopWidth) + style.ReadLengthPxOrZero(PropertyId.PaddingTop);
            var insetRightPx = style.ReadLengthPxOrZero(PropertyId.BorderRightWidth) + style.ReadLengthPxOrZero(PropertyId.PaddingRight);
            var insetBottomPx = style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth) + style.ReadLengthPxOrZero(PropertyId.PaddingBottom);

            // Lay out the content line FIRST — its size drives the §5.3 shrink-to-fit box below. An
            // empty `content: ""` box (CSS Page 3 §6.1: content not none/normal) or one whose font won't
            // resolve has no content size, so it keeps the FULL band.
            var lineHeightPx = contentMetricsStyle.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16) * NormalLineHeightFactor;
            // The box's computed white-space decides whether its content can WRAP: only Normal / PreWrap /
            // PreLine / BreakSpaces wrap, so only they can flex narrower than the single-line width and
            // re-wrap to fit. A `nowrap` / `pre` box is rigid (min-content == max-content) → the
            // center-priority clamp, no re-wrap, and a vertical box keeps its single (clipped) line.
            // `white-space` IS cascaded now (white-space cycle — an inherited MarginBoxStyle longhand, so
            // a declared `nowrap`/`pre` on the box or inherited from the page context/root drives this);
            // unset boxes compute the `normal` default.
            var boxWhiteSpace = style.ReadInlineTextPolicy().WhiteSpace;
            var canWrap = boxWhiteSpace is WhiteSpace.Normal or WhiteSpace.PreWrap
                or WhiteSpace.PreLine or WhiteSpace.BreakSpaces;
            // A standalone element() whose running element has BLOCK-LEVEL children arrives as
            // U+000A-separated lines (nested BLOCK children first cut — MarginContentCollector). Those
            // authored block boundaries are MANDATORY breaks, but the text WITHIN each block still wraps per
            // the box's white-space (post-PR-#154 review P2). So forced-break content lays out as `pre-line`
            // when the box allows wrapping (preserve every U+000A + wrap long blocks) or `pre` when it
            // doesn't (`nowrap`/`pre` — preserve U+000A, no soft wrap); BOTH preserve the breaks (a plain
            // `nowrap` would COLLAPSE U+000A to a space and lose the stacking). Plain content has no U+000A →
            // the existing `nowrap`-first single-line measurement path is byte-identical. Unlike the initial
            // first cut, forced-break content now DOES min-content-flex + re-wrap (so a long block child wraps
            // under sibling distribution / an explicit width) — both using `reflowWhiteSpace`.
            var hasForcedBreaks = !string.IsNullOrEmpty(text) && text.IndexOf('\n') >= 0;
            var forcedBreakWhiteSpace = canWrap ? WhiteSpace.PreLine : WhiteSpace.Pre;
            var reflowWhiteSpace = hasForcedBreaks ? forcedBreakWhiteSpace : boxWhiteSpace;
            // The INITIAL layout differs by axis (vertical-edge wrapping cycle). A HORIZONTAL (top/bottom)
            // box is measured UNCONSTRAINED first (`NoWrap` at the page width, or pre-line/pre for forced
            // breaks) — that single-line advance is its max-content width, driving shrink-to-fit + the §5.3
            // flex, and a shrunk box re-wraps below. A VERTICAL (left/right) or CORNER box has a FIXED
            // inline axis — the band/corner width — so its content wraps AT THAT WIDTH immediately (a block
            // container wraps at its content width; the old single-line NoWrap let a long header spill
            // horizontally out of the narrow band): lines stack down the variable axis (height
            // shrink-to-fit) and clip when they exceed it. Wrapping honours the box's white-space via
            // `reflowWhiteSpace` (`normal` wraps; a `nowrap`/`pre` box keeps the single line — the
            // pre-cycle behavior; forced breaks stay mandatory).
            var fixedAxisContentWidthPx = Math.Max(region.Width - insetLeftPx - insetRightPx, 1.0);
            var horizontalAxis = region.VariableAxis == PageMarginBoxGeometry.MarginBoxAxis.Horizontal;
            InlineLayoutResult inline = default;
            var hasLine = false;
            var layoutRuns = contentRuns ?? [new TextRun(text, contentStyle)];
            if (!string.IsNullOrEmpty(text))
            {
                try
                {
                    var laid = InlineLayouter.Layout(
                        sourceTextRuns: layoutRuns,
                        availableInlineSize: horizontalAxis ? availableInlinePx : fixedAxisContentWidthPx,
                        resolver: shaper,
                        scriptIso15924: "Latn", language: "en",
                        paragraphDirection: ParagraphDirection.LeftToRight,
                        whiteSpace: horizontalAxis
                            ? (hasForcedBreaks ? forcedBreakWhiteSpace : WhiteSpace.NoWrap)
                            : reflowWhiteSpace);
                    if (laid.Lines.Length > 0) { inline = laid; hasLine = true; }
                }
                catch (FontResolutionException ex)
                {
                    // No font resolves (the default SystemFontResolver, no bundled fallback yet) →
                    // degrade to a diagnostic instead of crashing (CLAUDE.md #7). The box keeps the full
                    // band for its decoration (hasLine stays false), but no text fragment is emitted.
                    EmitFontUnresolved(diagnostics, mb.Name, ex.Message);
                }
            }

            // §5.3 three-box-per-edge sizing: a box's VARIABLE axis (top/bottom edges → width; left/right
            // → height) is sized either from an EXPLICIT `width`/`height` (explicit-size cycle) or, when
            // that's `auto`, by SHRINKING a content-bearing box to its border-box content size (cycle 14
            // first cut) — so its background/border cover the box rather than the whole band. The box is
            // then positioned in the band by its §5.3.2.4 name-derived ROLE (start/center/end by name,
            // NOT the declared content alignment). An explicit size specifies the box per its `box-sizing`
            // (CSS Basic UI 4 §10, box-sizing cycle): the CONTENT box by default (the border-box adds the
            // border+padding insets) or, under `box-sizing: border-box`, the BORDER box itself (the insets
            // come out of the content area; the used border-box never goes below the insets, flooring the
            // content box at 0). Either way it applies even to an empty/failed-font box (a sized decorative
            // box). Corner boxes (no variable axis) + auto-sized empty/failed-font boxes keep the full band
            // (the cycle-8 decorative band). Box size is clamped to the band. This is each box's DESIRED
            // (max-content / explicit) size; siblings sharing an edge that would overlap are then resolved
            // by ResolveEdgeOverlaps (§5.3 min/max-content flex or center-priority clamp), and a
            // flexed/shrunk box's content is re-wrapped to fit. Vertical-edge overflow is CLIPPED at line
            // granularity + DIAGNOSED below (overflow-clipping cycle).
            var boxWidthPx = region.Width;
            var boxHeightPx = region.Height;
            var borderBoxSizing = IsBorderBoxSizing(style);
            // Resolve the variable-axis explicit size ONCE per box (post-PR-#156 review P2): the same
            // value feeds the sizing AND the min-content gate below, and the single call site lets an
            // unresolvable-in-context kept relative size (e.g. `1e308em` → a non-finite product) surface
            // ONE diagnostic instead of silently shrink-to-fitting.
            var explicitVarSizePx = region.VariableAxis switch
            {
                PageMarginBoxGeometry.MarginBoxAxis.Horizontal =>
                    TryReadExplicitSizePx(style, PropertyId.Width, region.Width, sizeBases, mb.Name, diagnostics),
                PageMarginBoxGeometry.MarginBoxAxis.Vertical =>
                    TryReadExplicitSizePx(style, PropertyId.Height, region.Height, sizeBases, mb.Name, diagnostics),
                _ => null, // corner boxes have no variable axis — width/height don't apply.
            };
            if (region.VariableAxis == PageMarginBoxGeometry.MarginBoxAxis.Horizontal)
            {
                if (explicitVarSizePx is double w)
                    boxWidthPx = Math.Min(region.Width,
                        borderBoxSizing ? Math.Max(w, insetLeftPx + insetRightPx) : w + insetLeftPx + insetRightPx);
                else if (hasLine)
                    boxWidthPx = Math.Min(region.Width, WidestLineAdvancePx(inline) + insetLeftPx + insetRightPx);
            }
            else if (region.VariableAxis == PageMarginBoxGeometry.MarginBoxAxis.Vertical)
            {
                if (explicitVarSizePx is double h)
                    boxHeightPx = Math.Min(region.Height,
                        borderBoxSizing ? Math.Max(h, insetTopPx + insetBottomPx) : h + insetTopPx + insetBottomPx);
                else if (hasLine)
                    boxHeightPx = Math.Min(region.Height,
                        SumLineHeightsPx(inline, segmentLineHeightsPx, segmentMarginTopsPx,
                            segmentMarginBottomsPx, segmentPaddingTopsPx, segmentPaddingBottomsPx,
                            lineHeightPx, inline.Lines.Length)
                        + insetTopPx + insetBottomPx);
            }

            // Min-content border-box size along the VARIABLE axis — drives the §5.3 min/max-content flex
            // (ResolveEdgeOverlaps). Only a HORIZONTAL auto box (top/bottom, no explicit width) can flex:
            // its min-content WIDTH is the longest unbreakable run (measured by re-laying the content out
            // at a tiny width). Every other box (vertical, explicit, empty/failed-font) reports its desired
            // size as the min, so it takes the (rigid) center-priority clamp path — preserving cycle 16.
            var minVarSizePx = region.VariableAxis == PageMarginBoxGeometry.MarginBoxAxis.Horizontal
                ? boxWidthPx : boxHeightPx;
            if (region.VariableAxis == PageMarginBoxGeometry.MarginBoxAxis.Horizontal && hasLine && canWrap
                && explicitVarSizePx is null
                && TryMeasureMinContentWidthPx(layoutRuns, shaper, reflowWhiteSpace, out var minContentWidthPx))
            {
                minVarSizePx = Math.Min(region.Width, minContentWidthPx + insetLeftPx + insetRightPx);
            }

            // Place the box in the band by its §5.3.2.4 ROLE (region.HAlign/VAlign — A flush start,
            // B centered, C flush end, BY NAME), NOT the declared content alignment: e.g. @top-center
            // stays centered even under `text-align: left`. On the box's FIXED axis it spans the band,
            // so the leftover term is 0 and this reduces to region.X / region.Y.
            var boxXPx = region.X + (region.Width - boxWidthPx) * region.HAlign;
            var boxYPx = region.Y + (region.Height - boxHeightPx) * region.VAlign;

            items.Add(new MarginBoxItem
            {
                Region = region, Style = style, ContentStyle = contentStyle,
                BgCurrentColorArgb = bgCurrentColor, BorderCurrentColors = borderCurrentColors,
                Inline = inline, HasLine = hasLine, Text = text, MinVarSizePx = minVarSizePx,
                ContentRuns = layoutRuns, MetricsStyle = contentRuns is null ? null : contentMetricsStyle,
                SegmentLineHeightsPx = contentRuns is null ? null : segmentLineHeightsPx,
                SegmentMarginTopsPx = contentRuns is null ? null : segmentMarginTopsPx,
                SegmentMarginBottomsPx = contentRuns is null ? null : segmentMarginBottomsPx,
                SegmentPaddingTopsPx = contentRuns is null ? null : segmentPaddingTopsPx,
                SegmentPaddingBottomsPx = contentRuns is null ? null : segmentPaddingBottomsPx,
                SegmentPaddingLeftsPx = contentRuns is null ? null : segmentPaddingLeftsPx,
                SegmentPaddingRightsPx = contentRuns is null ? null : segmentPaddingRightsPx,
                SegmentMarginLeftsPx = contentRuns is null ? null : segmentMarginLeftsPx,
                SegmentMarginRightsPx = contentRuns is null ? null : segmentMarginRightsPx,
                ContainerBands = contentRuns is null ? null : containerBands,
                SegmentAlignFactors = contentRuns is null ? null : segmentAlignFactors,
                SegmentDecorStyles = contentRuns is null ? null : segmentDecorStyles,
                SegmentColorArgbs = contentRuns is null ? null : segmentColorArgbs,
                BoxAlignDeclared = MarginBoxDeclares(mb.Declarations, "text-align"),
                ElementDecorStyle = elementDecorStyle, ElementColorArgb = elementColor,
                BackgroundImageUrl = backgroundImageUrl,
                BackgroundRepeatRaw = backgroundRepeatRaw,
                BackgroundSizeRaw = backgroundSizeRaw,
                BackgroundPositionRaw = backgroundPositionRaw,
                BackgroundOriginRaw = backgroundOriginRaw,
                BackgroundClipRaw = backgroundClipRaw,
                ReflowWhiteSpace = reflowWhiteSpace, CanWrap = canWrap, Name = mb.Name,
                OverflowVisible = MarginBoxStyle.OverflowVisible(mb.Declarations),
                InsetLeftPx = insetLeftPx, InsetTopPx = insetTopPx,
                InsetRightPx = insetRightPx, InsetBottomPx = insetBottomPx, HAlign = hAlign,
                VAlign = vAlign, LineHeightPx = lineHeightPx, BoxXPx = boxXPx, BoxYPx = boxYPx,
                BoxWidthPx = boxWidthPx, BoxHeightPx = boxHeightPx,
            });
        }

        // §5.3 DISTRIBUTION (first cut) — resolve overlap among the boxes sharing each edge band via the
        // center-priority clamp (PageMarginBoxGeometry.ResolveEdgeOverlap). A NO-OP when the boxes don't
        // overlap, so single boxes + the common short-content case keep the per-box (cycle 14/15)
        // geometry byte-for-byte. Corner boxes (no variable axis) don't participate.
        ResolveEdgeOverlaps(items);

        // §5.3 overflow WRAPPING — a HORIZONTAL box the distribution shrank below its single-line
        // (max-content) width re-wraps its content to the assigned content width (multi-line), so the
        // content FITS instead of overflowing. Only horizontal edges (variable axis = width); a box still
        // narrower than its longest unbreakable word overflows that word (inherent). Forced-break
        // (nested-block) content re-wraps too — its `reflowWhiteSpace` (pre-line/pre) preserves the authored
        // U+000A block breaks while wrapping a long block child (post-PR-#154 review P2). Wrapped lines are
        // aligned per line by the box's alignment (LineAlignFactor); vertical-edge (height) overflow is
        // CLIPPED at line granularity + DIAGNOSED in PASS 2 (overflow-clipping cycle).
        foreach (var item in items)
        {
            if (!item.HasLine || !item.CanWrap
                || item.Region.VariableAxis != PageMarginBoxGeometry.MarginBoxAxis.Horizontal
                || item.Inline.Lines.Length == 0)
                continue;
            var contentWidthPx = item.BoxWidthPx - item.InsetLeftPx - item.InsetRightPx;
            // Re-wrap when the assigned content box is narrower than the content's WIDEST line (NOT line 0
            // — a mandatory break can put a wider line later), wrapping with the reflow white-space (the
            // box's own, or pre-line/pre for forced-break content so the block breaks survive).
            if (contentWidthPx > 0 && contentWidthPx < WidestLineAdvancePx(item.Inline) - 0.5
                && TryLayoutContent(item.ContentRuns, shaper, contentWidthPx, item.ReflowWhiteSpace, out var wrapped))
            {
                item.Inline = wrapped;
            }
        }

        // PASS 2 — emit each box's background band (cycle 8) + border (border cycle) + text fragment from
        // its (possibly adjusted) box rect. Aliased back to the per-box locals the emission code uses.
        var fragments = new List<BoxFragment>(items.Count);
        var backgrounds = new List<MarginBoxBackgroundFill>();
        var borders = new List<MarginBoxBorder>();
        var backgroundImages = new List<MarginBoxBackgroundImage>();
        foreach (var item in items)
        {
            var style = item.Style;
            var bgCurrentColor = item.BgCurrentColorArgb;
            var borderCurrentColors = item.BorderCurrentColors;
            var inline = item.Inline;
            var hasLine = item.HasLine;
            var boxXPx = item.BoxXPx;
            var boxYPx = item.BoxYPx;
            var boxWidthPx = item.BoxWidthPx;
            var boxHeightPx = item.BoxHeightPx;
            var insetLeftPx = item.InsetLeftPx;
            var insetTopPx = item.InsetTopPx;
            var insetRightPx = item.InsetRightPx;
            var insetBottomPx = item.InsetBottomPx;
            var hAlign = item.HAlign;
            var vAlign = item.VAlign;
            var lineHeightPx = item.LineHeightPx;

            // Background-color (cycle 8): fills the BOX behind the content. `currentcolor` resolves
            // against the OWNER's color (the box's if the box declares the background, else the running
            // element's — review P1); transparent (initial) / unset paints nothing.
            // Per-corner border-radius (margin-box-border-radius cycle) — read from the box's cascaded
            // style (the corner longhands joined MarginBoxStyle's CascadedStyleIds) against the FINAL box
            // dimensions, so a `%` radius resolves correctly; absolute or `%` (→ ellipse on a non-square
            // box). Reuses the body's reader. Shared by the band fill, the rounded border (free, via
            // FragmentPainter.PaintBorders reading the same longhands), and the image clip below.
            var radii = FragmentPainter.ReadCornerRadii(style, boxWidthPx, boxHeightPx);
            if (FragmentPainter.TryResolveColor(style.Get(PropertyId.BackgroundColor), bgCurrentColor, out var bgArgb)
                && FragmentPainter.Alpha(bgArgb) != 0)
            {
                backgrounds.Add(new MarginBoxBackgroundFill(
                    boxXPx, boxYPx, boxWidthPx, boxHeightPx, bgArgb, radii));
            }

            // background-image (margin-box-bg-image cycle): the SAME band rect as the fill —
            // the pipeline resolves the url against the per-render cache + tiles it over the
            // rect (over the fill, under borders/text; PrintBackgrounds-gated there), the
            // declared repeat/size/position raws riding along (PR #167 review P1).
            if (item.BackgroundImageUrl is { } bgImageUrl)
            {
                // background-origin (initial padding-box) sets the positioning area, background-clip
                // (initial border-box) the paint rect — the box rect inset by the used border /
                // border+padding (bg-origin / bg-clip cycles; the body helper reused).
                var (oT, oR, oB, oL) = FragmentPainter.BackgroundAreaInset(style, item.BackgroundOriginRaw, 'p');
                var (cT, cR, cB, cL) = FragmentPainter.BackgroundAreaInset(style, item.BackgroundClipRaw, 'b');
                // A narrow box with large border/padding + a content-box origin/clip can drive the inset
                // sum past the box dimension, so clamp the positioning-area + clip width/height to ≥ 0 —
                // a negative dimension must never reach the tiler (post-PR-#171 review P1; the body call
                // site + PaintBackgroundImageTiles' empty-clip skip mirror this).
                // A border-radius rounds the image clip too (margin-box-border-radius cycle, Task 3): the
                // box's corner radii inset per side to the background-clip box (border-box clip → no
                // reduction). Reuses the body inset helper.
                var clipRadii = FragmentPainter.InsetRadii(radii, cT, cR, cB, cL);
                backgroundImages.Add(new MarginBoxBackgroundImage(
                    boxXPx + oL, boxYPx + oT, Math.Max(0, boxWidthPx - oL - oR), Math.Max(0, boxHeightPx - oT - oB), bgImageUrl,
                    item.BackgroundRepeatRaw, item.BackgroundSizeRaw, item.BackgroundPositionRaw,
                    boxXPx + cL, boxYPx + cT, Math.Max(0, boxWidthPx - cL - cR), Math.Max(0, boxHeightPx - cT - cB),
                    clipRadii));
            }

            // Border (border cycle): strokes the BOX. `currentcolor` resolves against the OWNER's color
            // (the box's if the box declares the border, else the running element's — review P1).
            if (HasBorder(style))
                borders.Add(new MarginBoxBorder(boxXPx, boxYPx, boxWidthPx, boxHeightPx, style, borderCurrentColors));

            if (!hasLine) continue; // empty / failed-font box: decoration only, no text fragment.

            // Place the content box at the BORDER-box origin (boxX/boxY); TextPainter adds the
            // border+padding inset to reach the content-box origin. Horizontal alignment is handed to
            // TextPainter as a PER-LINE factor (LineAlignFactor = hAlign) so a WRAPPED multi-line run is
            // aligned line-by-line within the content box (Task 21 — wrapped-line content-alignment), not
            // just block-left. A single line reduces to the old block-level alignment (byte-identical):
            // its leftover (contentBoxWidth − lineAdvance) × hAlign is exactly what was pre-applied before.
            // A SHRINK-TO-FIT box is content-sized so the leftover is ~0 (no visible shift); an explicit-
            // width / flexed / wrapped box has room, so the alignment is observable.
            // Per-line PITCH (segment-pitch cycle): each line advances by ITS segment's height
            // (line→segment via the first glyph slice's source-run index); uniform for non-segment
            // content (heights == null → lineHeightPx × count, byte-identical). The geometry is
            // computed ONCE here (post-PR-#163 review P3) — the truncation decision, the kept-sum,
            // the per-line bands, and the fragment arrays all consume the same pair (a truncation
            // slices the prefix: line→segment mapping is positional, so the prefix is identical).
            var lineSegments = LineSegmentIndices(inline);
            double[]? perLineHeights = null;
            double[]? perLineGaps = null;
            double[]? perLineInsetL = null;
            double[]? perLineInsetR = null;
            if (item.SegmentLineHeightsPx is { } segHeightsForGeometry)
            {
                (perLineHeights, perLineGaps) = PerLineGeometry(
                    inline, segHeightsForGeometry, item.SegmentMarginTopsPx, item.SegmentMarginBottomsPx,
                    item.SegmentPaddingTopsPx, item.SegmentPaddingBottomsPx, lineHeightPx);
                // Per-line HORIZONTAL insets (hpadding + segment-hmargins cycles): a leaf's own
                // padding-left/right shifts ITS lines' glyphs + shrinks their alignment extent
                // (the band keeps the full content-box width — a block's background spans its
                // border box); its own margin-left/right insets the glyphs AND the band (margins
                // sit OUTSIDE the border box — the per-line band loop below subtracts them).
                var segPadL = item.SegmentPaddingLeftsPx;
                var segPadR = item.SegmentPaddingRightsPx;
                var segMargL = item.SegmentMarginLeftsPx;
                var segMargR = item.SegmentMarginRightsPx;
                if (segPadL is not null || segMargL is not null)
                {
                    perLineInsetL = new double[lineSegments.Length];
                    perLineInsetR = new double[lineSegments.Length];
                    var anyInset = false;
                    for (var li = 0; li < lineSegments.Length; li++)
                    {
                        var segIdx = lineSegments[li];
                        if (segIdx < 0) continue;
                        var l = (segPadL is not null && segIdx < segPadL.Length ? segPadL[segIdx] : 0)
                            + (segMargL is not null && segIdx < segMargL.Length ? segMargL[segIdx] : 0);
                        var r = (segPadR is not null && segIdx < segPadR.Length ? segPadR[segIdx] : 0)
                            + (segMargR is not null && segIdx < segMargR.Length ? segMargR[segIdx] : 0);
                        perLineInsetL[li] = l;
                        perLineInsetR[li] = r;
                        anyInset |= l > 0 || r > 0;
                    }
                    if (!anyInset) { perLineInsetL = null; perLineInsetR = null; }
                }
            }
            var blockHeightPx = perLineHeights is null
                ? lineHeightPx * inline.Lines.Length
                : PrefixSum(perLineHeights, perLineGaps!, perLineHeights.Length);
            var contentBoxWidthPx = Math.Max(0, boxWidthPx - insetLeftPx - insetRightPx);
            var contentBoxHeightPx = Math.Max(0, boxHeightPx - insetTopPx - insetBottomPx);
            // Vertical-edge (height) OVERFLOW (Task 23 + the overflow-clipping cycle): when the content
            // block-height exceeds the box's content-box height — the box was clamped to the page-margin
            // band but its content is TALLER (the common case is a vertical left/right edge box at the band
            // limit, or a multi-line element() running header) — the overflow is CLIPPED at line
            // granularity: the first lines that fit the content-box height paint (reading-order truncation;
            // a partially-fitting line is dropped whole), and the truncated block is then vertical-aligned
            // within the content box. The diagnostic keeps the truncation visible (CLAUDE.md #7 — never
            // drop content silently), naming the box + the measured vs available height + the kept/total
            // lines (post-PR-#154 review P3); reported once PER BOX (each item is visited once here), so
            // multiple overflowing headers/footers are each diagnosable. When not even one line fits, the
            // box paints its decoration only (like an empty `content: ""` box). An EXPLICIT
            // `overflow: visible` on the box opts OUT (clip-path cycle): the content spills like
            // pre-clipping builds — authored overflow, so no truncation, no clip, and no diagnostic.
            if (!item.OverflowVisible && blockHeightPx > contentBoxHeightPx + OverflowEpsilonPx)
            {
                var keptLines = perLineHeights is null
                    ? MaxLinesThatFit(contentBoxHeightPx, lineHeightPx, inline.Lines.Length)
                    : MaxLinesThatFitCumulative(contentBoxHeightPx, perLineHeights, perLineGaps!);
                EmitContentOverflow(
                    diagnostics, item.Name, blockHeightPx, contentBoxHeightPx, keptLines, inline.Lines.Length);
                if (keptLines <= 0) continue; // nothing fits — decoration only, no text fragment.
                inline = inline with { Lines = inline.Lines[..keptLines] };
                if (perLineHeights is not null)
                {
                    blockHeightPx = PrefixSum(perLineHeights, perLineGaps!, keptLines);
                    perLineHeights = perLineHeights[..keptLines];
                    perLineGaps = perLineGaps![..keptLines];
                }
                else
                {
                    blockHeightPx = lineHeightPx * keptLines;
                }
            }
            // HORIZONTAL overflow of the surviving lines (clip-path cycle): a line that protrudes past
            // the box's CLIP EDGE — an unbreakable run wider than the box (a long word in a narrow
            // band / a clamped rigid sibling), or a `nowrap` box — would previously SPILL over the page.
            // Now the fragment carries a CLIP RECT (the box's PADDING box, per CSS Overflow 3 §3 — the
            // clip edge of overflow ≠ visible) and the shared TextPainter wraps its glyph runs in a PDF
            // `q <rect> re W n … Q` clip path, so the protruding GLYPHS clip at the box edge
            // (partial-glyph clipping — the vertically-truncated lines above stay whole-line). The
            // predicate uses the SAME geometry as the rect (post-PR-#156 review P2): a line starts at the
            // CONTENT-box left + its own per-line LEFT inset (segment padding, hpadding cycle — 0 when
            // none; a line wider than its extent isn't alignment-shifted — TextPainter clamps its shift
            // to ≥ 0), so it crosses the clip edge only when padding-left + inset + advance exceeds the
            // PADDING-box width (PR #165 review P2 — the bare advance missed inset-pushed lines) —
            // overflow into the right padding stays inside the clip edge and must trip neither the clip
            // nor the diagnostic.
            // Surfaced per box (same code, width-phrased — CLAUDE.md #7); `overflow: visible` opts out. A
            // box whose content fits carries no clip rect, so its stream is byte-identical.
            FragmentClipRect? clipRect = null;
            var widestSurvivingPx = WidestLineOccupiedPx(inline, perLineInsetL);
            var borderLeftPx = style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth);
            var paddingBoxWidthPx = Math.Max(
                0, boxWidthPx - borderLeftPx - style.ReadLengthPxOrZero(PropertyId.BorderRightWidth));
            var paddingLeftPx = insetLeftPx - borderLeftPx;
            if (!item.OverflowVisible
                && paddingLeftPx + widestSurvivingPx > paddingBoxWidthPx + OverflowEpsilonPx)
            {
                EmitContentOverflowWidth(
                    diagnostics, item.Name, widestSurvivingPx, Math.Max(0, paddingBoxWidthPx - paddingLeftPx));
                var borderTopPx = style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
                clipRect = new FragmentClipRect(
                    boxXPx + borderLeftPx - contentOriginLeftPx,
                    boxYPx + borderTopPx - contentOriginTopPx,
                    paddingBoxWidthPx,
                    Math.Max(0, boxHeightPx - borderTopPx - style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth)));
            }
            // Vertical alignment uses the FULL wrapped block height (lineHeight × line count), not one line:
            // a re-wrapped multi-line header would otherwise be positioned as if it were a single line and
            // spill out of its band (review P2). The whole block is centered / top- / bottom-aligned in the
            // box. (Single-line content → blockHeight == lineHeight, so this is byte-identical there.)
            var absTopPx = boxYPx + (contentBoxHeightPx - blockHeightPx) * vAlign;

            // NESTED element decoration (separately-decorated cycle): the running element's own
            // band at its content block — background then border, ON TOP of the box's band (list
            // order = paint order). currentcolor resolves to the ELEMENT's own colour (it owns
            // every property here, CSS Color 4 §6.2). Wraps the PAINTED (possibly line-clipped)
            // block, so the band never exceeds the box.
            if (item.ElementDecorStyle is { } elemDecor)
            {
                // The band's origin is the CONTENT-BOX top-left: TextPainter adds the box style's
                // border+padding to the fragment offset before placing glyphs, so the band must add
                // the SAME insets on BOTH axes — post-PR-#162 review P1: only X added them, shifting
                // the band up into the box's padding/border while the glyphs painted lower.
                var elemXPx = boxXPx + insetLeftPx;
                var elemYPx = absTopPx + insetTopPx;
                if (FragmentPainter.TryResolveColor(
                        elemDecor.Get(PropertyId.BackgroundColor), item.ElementColorArgb, out var elemBgArgb)
                    && FragmentPainter.Alpha(elemBgArgb) != 0)
                {
                    backgrounds.Add(new MarginBoxBackgroundFill(
                        elemXPx, elemYPx, contentBoxWidthPx, blockHeightPx, elemBgArgb));
                }
                if (HasBorder(elemDecor))
                {
                    borders.Add(new MarginBoxBorder(
                        elemXPx, elemYPx, contentBoxWidthPx, blockHeightPx, elemDecor,
                        FragmentPainter.BorderEdgeCurrentColors.Uniform(item.ElementColorArgb)));
                }
            }

            // Per-line DECORATION + ALIGNMENT + PITCH (element segments part 2): each surviving
            // line maps to its SEGMENT (the first glyph slice's source-run index — runs == segments);
            // a decorated segment paints a per-LINE band behind its line (background + border, the
            // segment's own colour as currentcolor owner, spanning the content-box width per the
            // block-fills-its-containing-block rule); a segment's own text-align aligns ITS line
            // (the box's declared text-align still WINS — uniform hAlign); each line advances by
            // its segment's pitch. Non-segment content: all three stay null → byte-identical.
            double[]? perLineAligns = null;
            if (perLineHeights is not null)
            {
                // Nested CONTAINER bands (container-bands cycle): each decorated intermediate
                // block paints ONE band spanning its descendant lines — added AFTER the element
                // band, BEFORE the per-line leaf bands below (list order = paint order; the
                // capture is PRE-order, so an outer container paints under an inner one). The
                // Y range derives from the SAME per-line geometry the leaf loop uses: the first
                // spanned surviving line's band top (after ITS gap) to the last's bottom — a
                // vertical truncation clamps the range (a fully-truncated container paints
                // nothing). The container's own horizontal margins inset ITS band (its
                // children's line geometry is untouched — the documented first cut).
                if (item.ContainerBands is { } cBands)
                {
                    foreach (var cb in cBands)
                    {
                        var firstLi = -1;
                        var lastLi = -1;
                        for (var li = 0; li < inline.Lines.Length && li < lineSegments.Length; li++)
                        {
                            var s = lineSegments[li];
                            if (s < cb.FirstSegment || s > cb.LastSegment) continue;
                            if (firstLi < 0) firstLi = li;
                            lastLi = li;
                        }
                        if (firstLi < 0) continue;   // fully truncated.
                        // The band extends over its own border+padding strip (container-vpad
                        // cycle): the boundary gaps reserved margin + border + padding + the
                        // inner gap; the INSIDE part (Leading/TrailingInsidePx) belongs to the
                        // band, the container's own margin stays outside it.
                        var topPx = absTopPx + insetTopPx
                            + PrefixSum(perLineHeights, perLineGaps!, firstLi) + perLineGaps![firstLi]
                            - cb.LeadingInsidePx;
                        var bottomPx = absTopPx + insetTopPx
                            + PrefixSum(perLineHeights, perLineGaps!, lastLi + 1)
                            + cb.TrailingInsidePx;
                        var cbX = boxXPx + insetLeftPx + cb.MarginLeftPx;
                        var cbW = Math.Max(0, contentBoxWidthPx - cb.MarginLeftPx - cb.MarginRightPx);
                        var cbH = Math.Max(0, bottomPx - topPx);
                        if (cbW <= 0 || cbH <= 0) continue;
                        if (FragmentPainter.TryResolveColor(
                                cb.Style.Get(PropertyId.BackgroundColor), cb.ColorArgb, out var cbBg)
                            && FragmentPainter.Alpha(cbBg) != 0)
                        {
                            backgrounds.Add(new MarginBoxBackgroundFill(cbX, topPx, cbW, cbH, cbBg));
                        }
                        if (HasBorder(cb.Style))
                        {
                            borders.Add(new MarginBoxBorder(cbX, topPx, cbW, cbH, cb.Style,
                                FragmentPainter.BorderEdgeCurrentColors.Uniform(cb.ColorArgb)));
                        }
                    }
                }

                // Per-line gaps (segment-margins cycle) ride the SAME precomputed geometry the
                // sizing + truncation used (review P3 — no recompute) — a leaf block's collapsed
                // vertical margin pushes its line down; the band starts AFTER the gap (margins
                // stay transparent).
                // Content-box top (review P1) — the same + insetTop the glyph pass applies.
                var lineTopCursorPx = absTopPx + insetTopPx;
                for (var li = 0; li < inline.Lines.Length && li < perLineHeights.Length; li++)
                {
                    var segIdx = li < lineSegments.Length ? lineSegments[li] : -1;
                    var h = perLineHeights[li];
                    lineTopCursorPx += perLineGaps![li];
                    if (segIdx >= 0 && item.SegmentDecorStyles?[segIdx] is { } segDecorStyle)
                    {
                        var segColor = item.SegmentColorArgbs is { } sc && segIdx < sc.Length
                            ? sc[segIdx] : item.ElementColorArgb;
                        // Horizontal MARGINS inset the band too (segment-hmargins cycle) —
                        // margins sit OUTSIDE the leaf's border box, so its band starts after
                        // margin-left and ends before margin-right (padding stays INSIDE the
                        // band — the background covers the padding box per CSS B&B §4.2).
                        var bandMargL = item.SegmentMarginLeftsPx is { } bml && segIdx < bml.Length
                            ? bml[segIdx] : 0;
                        var bandMargR = item.SegmentMarginRightsPx is { } bmr && segIdx < bmr.Length
                            ? bmr[segIdx] : 0;
                        var bandXPx = boxXPx + insetLeftPx + bandMargL;
                        var bandWidthPx = Math.Max(0, contentBoxWidthPx - bandMargL - bandMargR);
                        if (FragmentPainter.TryResolveColor(
                                segDecorStyle.Get(PropertyId.BackgroundColor), segColor, out var segBgArgb)
                            && FragmentPainter.Alpha(segBgArgb) != 0)
                        {
                            backgrounds.Add(new MarginBoxBackgroundFill(
                                bandXPx, lineTopCursorPx, bandWidthPx, h, segBgArgb));
                        }
                        if (HasBorder(segDecorStyle))
                        {
                            borders.Add(new MarginBoxBorder(
                                bandXPx, lineTopCursorPx, bandWidthPx, h, segDecorStyle,
                                FragmentPainter.BorderEdgeCurrentColors.Uniform(segColor)));
                        }
                    }
                    lineTopCursorPx += h;
                }
                if (!item.BoxAlignDeclared && item.SegmentAlignFactors is { } segAligns)
                {
                    perLineAligns = new double[inline.Lines.Length];
                    for (var li = 0; li < inline.Lines.Length; li++)
                    {
                        var segIdx = li < lineSegments.Length ? lineSegments[li] : -1;
                        perLineAligns[li] = segIdx >= 0 && segIdx < segAligns.Length
                            ? segAligns[segIdx] ?? hAlign : hAlign;
                    }
                }
            }

            fragments.Add(new BoxFragment(
                Box: Box.TextRun(string.Empty, style),
                InlineOffset: boxXPx - contentOriginLeftPx,   // border-box X; TextPainter aligns each line
                BlockOffset: absTopPx - contentOriginTopPx,
                InlineSize: contentBoxWidthPx,
                BlockSize: blockHeightPx,
                InlineLayout: inline,
                LineAlignFactor: hAlign,
                // The TEXT line metrics (line-height / pitch / baseline) follow the CONTENT style, not the
                // box style (post-PR-#151 review P1): a standalone element() shapes glyphs at the running
                // element's font-size, so the pitch must match (else a 32px header overlaps at 16px pitch).
                // For non-element content ContentStyle == Style, so this is byte-identical there. The box
                // style still drives the border/padding origin + decoration in TextPainter/FragmentPainter.
                TextMetricsStyle: item.MetricsStyle ?? item.ContentStyle,
                // The padding-box clip for horizontally-overflowing content (clip-path cycle) — null for
                // a fitting box, so its stream is byte-identical.
                ClipRect: clipRect,
                PerLineHeightsPx: perLineHeights,
                PerLineAlignFactors: perLineAligns,
                PerLineTopOffsetsPx: perLineGaps,
                PerLineInsetLeftPx: perLineInsetL,
                PerLineInsetRightPx: perLineInsetR));
        }

        // The page-context style is only a parent — each box copied the slots it needs — so return
        // it to the pool now instead of leaking it (post-PR-#134 review; the per-box styles stay
        // box-owned with their synthetic Box).
        pageContextStyle.ReleaseFromBox();
        return new MarginBoxLayoutResult(fragments, backgrounds, borders, backgroundImages);
    }

    /// <summary>Paint the resolved margin-box background bands (cycle 8) onto <paramref name="page"/>,
    /// behind the shared text pass. Reuses <see cref="FragmentPainter"/>'s px→pt rect conversion +
    /// constant-alpha (<c>/ca</c>) compositing, exactly like body backgrounds. Call AFTER the body
    /// fragments are painted and BEFORE <see cref="TextPainter.PaintText"/>.</summary>
    public static void PaintBackgrounds(
        PdfPage page, IReadOnlyList<MarginBoxBackgroundFill> backgrounds, double pageHeightPt)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (backgrounds is null) return;
        foreach (var bg in backgrounds)
        {
            FragmentPainter.ColorChannels(bg.Argb, out var r, out var g, out var b);
            FragmentPainter.ToPdfRect(
                bg.LeftPx, bg.TopPx, bg.WidthPx, bg.HeightPx, pageHeightPt, out var x, out var y, out var w, out var h);
            // Per-corner border-radius (margin-box-border-radius cycle): the UNIFORM-circular case keeps
            // the byte-stable single-radius path (px→pt ×0.75; FillRoundedRectangle clamps to half the
            // shorter side); the general per-corner / `%`-ellipse case uses the per-corner fill. Square
            // (no radius) stays a plain rectangle.
            if (bg.Radii.IsUniformCircular(out var rPx) && rPx > 0)
            {
                page.FillRoundedRectangle(x, y, w, h, PdfUnits.PxToPt(rPx),
                    r, g, b, FragmentPainter.Alpha(bg.Argb) / 255.0);
            }
            else if (bg.Radii.AnyPositive)
            {
                page.FillRoundedRectangle(x, y, w, h, FragmentPainter.ToPt(bg.Radii),
                    r, g, b, FragmentPainter.Alpha(bg.Argb) / 255.0);
            }
            else
            {
                page.FillRectangle(x, y, w, h, r, g, b, FragmentPainter.Alpha(bg.Argb) / 255.0);
            }
        }
    }

    /// <summary>Stroke the resolved margin-box borders (border cycle) onto <paramref name="page"/>,
    /// over the background bands but behind the shared text pass — reusing the shared
    /// <c>FragmentPainter.PaintBorders</c> (so margin + body borders render identically). Call
    /// AFTER <see cref="PaintBackgrounds"/> and BEFORE <see cref="TextPainter.PaintText"/>.</summary>
    public static void PaintBorders(
        PdfPage page, IReadOnlyList<MarginBoxBorder> borders, double pageHeightPt, IDiagnosticsSink diagnostics)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (borders is null) return;
        var styleApproximationReported = false;
        foreach (var b in borders)
            FragmentPainter.PaintBorders(page, b.Style, pageHeightPt, b.LeftPx, b.TopPx, b.WidthPx, b.HeightPx,
                b.CurrentColors, diagnostics, ref styleApproximationReported);
    }

    /// <summary>Whether <paramref name="style"/> declares any border edge (a <c>border-*-style</c> is
    /// set) — so a borderless box isn't collected for the border pass.</summary>
    private static bool HasBorder(ComputedStyle style) =>
        style.IsSet(PropertyId.BorderTopStyle) || style.IsSet(PropertyId.BorderRightStyle)
        || style.IsSet(PropertyId.BorderBottomStyle) || style.IsSet(PropertyId.BorderLeftStyle);

    /// <summary>Lay <paramref name="runs"/> out at <paramref name="widthPx"/> with the given wrapping
    /// mode (segment-style cycle: the runs carry per-segment styles, so a re-wrap/measure shapes each
    /// line exactly like the initial layout). Returns <see langword="false"/> on empty runs, no line,
    /// or a font-resolution failure. Used for the §5.3 min-content measurement + the overflow
    /// re-wrap.</summary>
    private static bool TryLayoutContent(
        IReadOnlyList<TextRun> runs, HarfBuzzShaperResolver shaper, double widthPx, WhiteSpace whiteSpace,
        out InlineLayoutResult result)
    {
        result = default;
        if (runs.Count == 0 || (runs.Count == 1 && string.IsNullOrEmpty(runs[0].Text))) return false;
        try
        {
            var laid = InlineLayouter.Layout(
                sourceTextRuns: runs,
                availableInlineSize: Math.Max(widthPx, 1.0), resolver: shaper,
                scriptIso15924: "Latn", language: "en",
                paragraphDirection: ParagraphDirection.LeftToRight, whiteSpace: whiteSpace);
            if (laid.Lines.Length == 0) return false;
            result = laid;
            return true;
        }
        catch (FontResolutionException) { return false; }
    }

    /// <summary>The min-content WIDTH (px) of <paramref name="runs"/> in <paramref name="whiteSpace"/> — the
    /// longest unbreakable run, found by wrapping at a tiny width so each break opportunity splits a line;
    /// the widest resulting line is the min-content. The caller passes the box's COMPUTED white-space (and
    /// only calls this for a wrapping mode) so a <c>nowrap</c>/<c>pre</c> box isn't measured as if it could
    /// wrap (Copilot review). <see langword="false"/> on a font-resolution failure.</summary>
    private static bool TryMeasureMinContentWidthPx(
        IReadOnlyList<TextRun> runs, HarfBuzzShaperResolver shaper, WhiteSpace whiteSpace, out double minContentPx)
    {
        minContentPx = 0;
        if (!TryLayoutContent(runs, shaper, 1.0, whiteSpace, out var laid)) return false;
        minContentPx = WidestLineAdvancePx(laid);
        return true;
    }

    /// <summary>Per-line GEOMETRY for segment content (segments part 2): each line's HEIGHT (its
    /// segment's pitch — line→segment via <see cref="LineSegmentIndices"/>, unmapped lines at the
    /// uniform pitch) and its TOP GAP (segment-margins cycle): the first line takes its segment's
    /// own margin-top; a line where the SEGMENT CHANGES takes the COLLAPSED gap
    /// (max(previous segment's margin-bottom, this segment's margin-top) — CSS 2.2 §8.3.1's
    /// adjoining-siblings case), floored at 0 so bands can't overlap (a net-negative collapsed
    /// margin is approximated as touching); WRAPPED lines within one segment get no gap.</summary>
    private static (double[] HeightsPx, double[] GapsPx) PerLineGeometry(
        InlineLayoutResult inline, double[] segmentLineHeightsPx,
        double[]? segmentMarginTopsPx, double[]? segmentMarginBottomsPx,
        double[]? segmentPaddingTopsPx, double[]? segmentPaddingBottomsPx,
        double uniformLineHeightPx)
    {
        var lineSegments = LineSegmentIndices(inline);
        var heights = new double[inline.Lines.Length];
        var gaps = new double[inline.Lines.Length];
        var prevSeg = -1;
        for (var li = 0; li < inline.Lines.Length; li++)
        {
            var segIdx = li < lineSegments.Length ? lineSegments[li] : -1;
            heights[li] = segIdx >= 0 && segIdx < segmentLineHeightsPx.Length
                ? segmentLineHeightsPx[segIdx] : uniformLineHeightPx;
            // Vertical PADDING applies at the segment's BOUNDARY lines only (post-PR-#164 review
            // P2 — a wrapped segment pads the BLOCK once, not every line): pad-top on the
            // segment's FIRST line (the segment changes here), pad-bottom on its LAST (the next
            // line belongs to a different segment, or this is the final line).
            if (segIdx >= 0 && segmentPaddingTopsPx is not null && segIdx != prevSeg
                && segIdx < segmentPaddingTopsPx.Length)
            {
                heights[li] += segmentPaddingTopsPx[segIdx];
            }
            if (segIdx >= 0 && segmentPaddingBottomsPx is not null
                && segIdx < segmentPaddingBottomsPx.Length
                && (li + 1 >= lineSegments.Length || lineSegments[li + 1] != segIdx))
            {
                heights[li] += segmentPaddingBottomsPx[segIdx];
            }
            if (segIdx >= 0 && segIdx != prevSeg && segmentMarginTopsPx is not null)
            {
                var topMargin = segIdx < segmentMarginTopsPx.Length ? segmentMarginTopsPx[segIdx] : 0;
                var prevBottom = prevSeg >= 0 && segmentMarginBottomsPx is not null
                    && prevSeg < segmentMarginBottomsPx.Length ? segmentMarginBottomsPx[prevSeg] : 0;
                gaps[li] = Math.Max(0, li == 0 ? topMargin : Math.Max(prevBottom, topMargin));
            }
            prevSeg = segIdx;
        }
        return (heights, gaps);
    }

    /// <summary>The block height of the first <paramref name="lineCount"/> lines incl. their
    /// per-line GAPS when segment geometry is present, else
    /// <paramref name="uniformLineHeightPx"/> × the count (byte-identical pre-cycle).</summary>
    private static double SumLineHeightsPx(
        InlineLayoutResult inline, double[]? segmentLineHeightsPx, double[]? segmentMarginTopsPx,
        double[]? segmentMarginBottomsPx, double[]? segmentPaddingTopsPx,
        double[]? segmentPaddingBottomsPx, double uniformLineHeightPx, int lineCount)
    {
        if (segmentLineHeightsPx is null) return uniformLineHeightPx * lineCount;
        // Single pass, no array allocations (PR #163 Copilot — this runs in the pass-1 sizing
        // path; the same gap/collapse + boundary-padding rules as PerLineGeometry, summed inline).
        var lineSegments = LineSegmentIndices(inline);
        var sum = 0.0;
        var prevSeg = -1;
        for (var li = 0; li < lineCount && li < inline.Lines.Length; li++)
        {
            var segIdx = li < lineSegments.Length ? lineSegments[li] : -1;
            sum += segIdx >= 0 && segIdx < segmentLineHeightsPx.Length
                ? segmentLineHeightsPx[segIdx] : uniformLineHeightPx;
            if (segIdx >= 0 && segmentPaddingTopsPx is not null && segIdx != prevSeg
                && segIdx < segmentPaddingTopsPx.Length)
            {
                sum += segmentPaddingTopsPx[segIdx];
            }
            if (segIdx >= 0 && segmentPaddingBottomsPx is not null
                && segIdx < segmentPaddingBottomsPx.Length
                && (li + 1 >= lineSegments.Length || lineSegments[li + 1] != segIdx))
            {
                sum += segmentPaddingBottomsPx[segIdx];
            }
            if (segIdx >= 0 && segIdx != prevSeg && segmentMarginTopsPx is not null)
            {
                var topMargin = segIdx < segmentMarginTopsPx.Length ? segmentMarginTopsPx[segIdx] : 0;
                var prevBottom = prevSeg >= 0 && segmentMarginBottomsPx is not null
                    && prevSeg < segmentMarginBottomsPx.Length ? segmentMarginBottomsPx[prevSeg] : 0;
                sum += Math.Max(0, li == 0 ? topMargin : Math.Max(prevBottom, topMargin));
            }
            prevSeg = segIdx;
        }
        return sum;
    }

    /// <summary>The per-line-pitch counterpart of <see cref="MaxLinesThatFit"/>: keeps whole lines
    /// while their CUMULATIVE (gap + height) fits the content-box height (+ the shared epsilon),
    /// in reading order — over the PRECOMPUTED geometry (post-PR-#163 review P3).</summary>
    private static int MaxLinesThatFitCumulative(
        double contentBoxHeightPx, double[] heights, double[] gaps)
    {
        var budget = Math.Max(0, contentBoxHeightPx) + OverflowEpsilonPx;
        var kept = 0;
        var sum = 0.0;
        for (var li = 0; li < heights.Length; li++)
        {
            sum += gaps[li] + heights[li];
            if (sum > budget) break;
            kept++;
        }
        return kept;
    }

    /// <summary>Σ (gap + height) of the first <paramref name="lineCount"/> lines of the
    /// precomputed per-line geometry.</summary>
    private static double PrefixSum(double[] heights, double[] gaps, int lineCount)
    {
        var sum = 0.0;
        for (var li = 0; li < lineCount && li < heights.Length; li++) sum += gaps[li] + heights[li];
        return sum;
    }

    /// <summary>Each line's SEGMENT index — the source-run index of the line's first GLYPH slice
    /// (segment runs are one TextRun per segment, so run index == segment index). A line with no
    /// glyph slice (defensive) inherits the previous line's segment (first line → 0).</summary>
    private static int[] LineSegmentIndices(InlineLayoutResult inline)
    {
        var lines = inline.Lines;
        var result = new int[lines.Length];
        var current = 0;
        for (var li = 0; li < lines.Length; li++)
        {
            foreach (var slice in lines[li].Slices)
            {
                if (slice.GlyphLength <= 0) continue;
                current = inline.ShapedRuns[slice.ShapedRunIndex].Source.SourceTextRunIndex;
                break;
            }
            result[li] = current;
        }
        return result;
    }

    /// <summary>The number of whole lines (at <paramref name="lineHeightPx"/> pitch) that fit
    /// <paramref name="contentBoxHeightPx"/>, clamped to <c>[0, totalLines]</c> — the overflow-clipping
    /// cap (line-granularity truncation; <see cref="OverflowEpsilonPx"/> absorbs sub-px rounding so an
    /// exactly-fitting block isn't clipped). A non-positive line-height can't have produced an overflowing
    /// block-height (it would be ≤ 0), so it keeps every line — defensive; the caller only clips after
    /// measuring an overflow. The range is narrowed BEFORE the int cast (post-PR-#155 review P2): a huge
    /// ratio (a tiny positive line-height under a tall box) would make the double→int conversion overflow
    /// into an unspecified value — e.g. int.MinValue, clamping to 0 and clipping EVERY line.</summary>
    internal static int MaxLinesThatFit(double contentBoxHeightPx, double lineHeightPx, int totalLines)
    {
        if (lineHeightPx <= 0) return totalLines;
        var ratio = (Math.Max(0, contentBoxHeightPx) + OverflowEpsilonPx) / lineHeightPx;
        if (ratio >= totalLines) return totalLines; // huge/∞ ratio: every line fits — decided before any cast
        if (!(ratio > 0)) return 0;                 // ≤ 0 or NaN (defensive)
        return (int)Math.Floor(ratio);              // 0 < ratio < totalLines (an int) → the cast is safe
    }

    /// <summary>The widest line's advance (px) in <paramref name="inline"/> — the content's max-content
    /// width. The first line is NOT always the widest: a mandatory break (a newline in the content) can put
    /// a wider line later (Copilot review), so the desired-size + re-wrap-trigger comparisons use this.</summary>
    private static double WidestLineAdvancePx(InlineLayoutResult inline)
    {
        var widest = 0.0;
        foreach (var line in inline.Lines)
            if (line.TotalAdvance > widest) widest = line.TotalAdvance;
        return widest;
    }

    /// <summary>The widest line's OCCUPIED width (px) — its LEFT inset + advance. With per-line
    /// horizontal insets (segment padding, hpadding cycle) a line paints from its OWN left inset
    /// inside the content box, so the horizontal-overflow predicate must measure inset + advance,
    /// not the bare advance: a 40px line with an 80px padding-left in a 100px box paints to 120px
    /// (PR #165 review P2). The RIGHT inset is deliberately EXCLUDED — it only shrinks the
    /// alignment EXTENT (TextPainter clamps the alignment shift to ≥ 0 and an aligned-within-extent
    /// line stays left of content-right − insetRight), so it never pushes glyphs past the clip
    /// edge. No insets → identical to <see cref="WidestLineAdvancePx"/>.</summary>
    private static double WidestLineOccupiedPx(InlineLayoutResult inline, double[]? perLineInsetLeft)
    {
        var widest = 0.0;
        for (var li = 0; li < inline.Lines.Length; li++)
        {
            var occupied = inline.Lines[li].TotalAdvance
                + (perLineInsetLeft is not null && li < perLineInsetLeft.Length
                    ? perLineInsetLeft[li] : 0.0);
            if (occupied > widest) widest = occupied;
        }
        return widest;
    }

    /// <summary>The bases a deferred relative explicit size resolves against (relative-units cycle):
    /// the box's resolved font-size (<c>em</c>/<c>ex</c>/<c>ch</c>), the root element's (<c>rem</c>),
    /// and the page box (viewport units — CSS Paged Media maps the viewport to the page).</summary>
    private readonly record struct RelativeSizeBases(
        double EmPx, double RootEmPx, double PageWidthPx, double PageHeightPx);

    /// <summary>The explicit size (CSS px) a margin box declares on its §5.3 VARIABLE axis
    /// — <c>width</c> for top/bottom boxes, <c>height</c> for left/right — or <see langword="null"/>
    /// when it's <c>auto</c>/unresolved (the caller shrink-to-fits). An absolute length is used as-is; a
    /// percentage resolves against <paramref name="bandExtentPx"/> (the box's containing block on that
    /// axis); a DEFERRED raw kept by <see cref="MarginBoxStyle"/> — a font-/viewport-relative length
    /// (<c>10em</c> / <c>1.5rem</c> / <c>50vw</c>, relative-units cycle) or a <c>calc()</c> expression
    /// (calc cycle, percent terms against the band) — resolves HERE against <paramref name="bases"/>
    /// via <see cref="TryResolveDeferredLengthPx"/> (post-PR-#157 Copilot review — this doc previously
    /// said calc never reaches here). The caller maps the size to the border-box per the box's
    /// <c>box-sizing</c> (<see cref="IsBorderBoxSizing"/>): content-box (the initial) adds the
    /// border+padding insets, border-box floors at them. Only a container-relative / malformed /
    /// negative size is diagnosed + DROPPED upstream by <see cref="MarginBoxStyle"/> (post-PR-#144
    /// review) and reads as <c>auto</c> here. A kept raw that still fails to resolve IN CONTEXT (a
    /// non-finite product like <c>1e308em</c>, or a calc() that fails evaluation — the keep gates are
    /// syntactic) is SURFACED via <paramref name="diagnostics"/> before the shrink-to-fit fallback
    /// (post-PR-#156 review P2 — the contextual failure must not be silent). Negatives are rejected
    /// upstream (non-negative property); the <c>Max(0, …)</c> is defensive.</summary>
    private static double? TryReadExplicitSizePx(
        ComputedStyle style, PropertyId id, double bandExtentPx, in RelativeSizeBases bases,
        string boxName, IDiagnosticsSink diagnostics)
    {
        var slot = style.Get(id);
        switch (slot.Tag)
        {
            case ComputedSlotTag.LengthPx:
                return Math.Max(0, slot.AsLengthPx());
            // AsPercentage() returns the percentage number (50 for "50%"), per the codebase convention
            // (e.g. AbsoluteLayouter / BlockLayouter use `AsPercentage() / 100.0 * base`).
            case ComputedSlotTag.Percentage:
                return Math.Max(0, slot.AsPercentage() / 100.0 * bandExtentPx);
        }
        if (style.TryGetDeferred(id, out var raw) && raw is not null)
        {
            if (TryResolveDeferredLengthPx(raw, bandExtentPx, bases, out var resolvedPx))
                return resolvedPx; // both resolvers guarantee finite + ≥ 0 (calc range-clamps per §10.5).
            diagnostics.Emit(new Diagnostic(
                DiagnosticCodes.CssPropertyValueInvalid001,
                $"The page margin box @{boxName} {(id == PropertyId.Width ? "width" : "height")} " +
                $"'{DiagnosticTextSanitizer.Sanitize(raw)}' could not be resolved against its context — " +
                "the result is not a finite non-negative size (e.g. an extreme multiplier overflowing the " +
                "font-size/page product), or the calc() expression is malformed/unsupported. The box " +
                "falls back to shrink-to-fit.",
                DiagnosticSeverity.Warning));
        }
        return null;
    }

    /// <summary>Resolve a KEPT deferred length raw — a <c>calc()</c>/<c>min()</c>/<c>max()</c>/
    /// <c>clamp()</c> math function (calc + min/max/clamp cycles, percent terms against
    /// <paramref name="percentBasePx"/>) or a font-/viewport-relative length (relative-units cycle) —
    /// to used px against <paramref name="bases"/>. Shared by the explicit §5.3 size and the padding
    /// resolve.</summary>
    private static bool TryResolveDeferredLengthPx(
        string raw, double percentBasePx, in RelativeSizeBases bases, out double px)
    {
        return CalcLengthEvaluator.IsMathFunction(raw)
            ? CalcLengthEvaluator.TryEvaluate(
                raw,
                new CalcLengthEvaluator.CalcContext(
                    percentBasePx, bases.EmPx, bases.RootEmPx, bases.PageWidthPx, bases.PageHeightPx),
                out px)
            : RelativeLengthResolver.TryResolve(
                raw, bases.EmPx, bases.RootEmPx, bases.PageWidthPx, bases.PageHeightPx, out px);
    }

    /// <summary>The four <c>padding-*</c> longhands, paired with the side name for diagnostics.</summary>
    private static readonly (PropertyId Id, string Side)[] PaddingSides =
    [
        (PropertyId.PaddingLeft, "left"), (PropertyId.PaddingTop, "top"),
        (PropertyId.PaddingRight, "right"), (PropertyId.PaddingBottom, "bottom"),
    ];

    /// <summary>Resolve the box's padding to USED px IN PLACE (relative-padding cycle): a PERCENTAGE
    /// slot resolves against <paramref name="containingWidthPx"/> (the box's region width — CSS B&amp;B
    /// §8.4: padding percentages on ALL FOUR sides resolve against the containing block's INLINE-axis
    /// size; the margin-box containing block is approximated as its edge region), a kept deferred
    /// font-/viewport-relative or <c>calc()</c> raw resolves via <see cref="TryResolveDeferredLengthPx"/>
    /// — and the slot is REWRITTEN as a LengthPx so every downstream reader (the painter's insets,
    /// <c>TextPainter</c>'s content origin, <c>FragmentPainter</c>) sees the SAME used value. A
    /// kept-but-unresolvable-in-context raw is SURFACED + unset (reads as 0 — mirroring the explicit-size
    /// policy); an already-absolute LengthPx (or unset) padding is untouched.</summary>
    private static void ResolveUsedPaddingInPlace(
        ComputedStyle style, double containingWidthPx, in RelativeSizeBases bases,
        string boxName, IDiagnosticsSink diagnostics)
    {
        foreach (var (id, side) in PaddingSides)
        {
            var slot = style.Get(id);
            if (slot.Tag == ComputedSlotTag.Percentage)
            {
                style.Set(id, ComputedSlot.FromLengthPx(
                    Math.Max(0, slot.AsPercentage() / 100.0 * containingWidthPx)));
                continue;
            }
            if (slot.Tag == ComputedSlotTag.LengthPx) continue; // absolute — already the used value.
            if (!style.TryGetDeferred(id, out var raw) || raw is null) continue; // unset → no padding.

            if (TryResolveDeferredLengthPx(raw, containingWidthPx, bases, out var px))
            {
                style.Set(id, ComputedSlot.FromLengthPx(px));
                continue;
            }
            diagnostics.Emit(new Diagnostic(
                DiagnosticCodes.CssPropertyValueInvalid001,
                $"The page margin box @{boxName} padding-{side} " +
                $"'{DiagnosticTextSanitizer.Sanitize(raw)}' could not be resolved against its context — " +
                "the result is not a finite non-negative length, or the calc() expression is " +
                "malformed/unsupported. The padding is treated as 0.",
                DiagnosticSeverity.Warning));
            style.Unset(id);
        }
    }

    /// <summary>Resolve a still-DEFERRED <c>font-size</c> raw on <paramref name="style"/> — a
    /// root-/viewport-relative form (<c>rem</c>/<c>vw</c>/<c>vh</c>/<c>vmin</c>/<c>vmax</c>) that
    /// <see cref="DeferredFontResolver"/> deliberately leaves for a later stage (it resolves only the
    /// PARENT-relative <c>em</c>/<c>ex</c>/<c>ch</c>/<c>%</c>/keywords), or a MATH FUNCTION
    /// (<c>font-size: clamp(12px, 5vw, 24px)</c> — admitted by <see cref="MarginBoxStyle"/>,
    /// post-PR-#158 review P2) — to used px IN PLACE (font-size cycle; closes the long-standing
    /// "rem/viewport font-size falls back to 16px" gap for margin boxes). For <c>font-size</c>, both
    /// <c>%</c> and <c>em</c> terms scale by the PARENT's font-size (CSS Fonts 4 §3.4) —
    /// <paramref name="parentEmPx"/> feeds both calc bases. A failed math-function evaluation is
    /// SURFACED (the admit gate is syntactic — same contract as sizes/padding) before the inherited/
    /// default fallback; a non-math unresolvable raw (container units) stays deferred → the default
    /// (documented).</summary>
    private static void ResolveDeferredFontSizeInPlace(
        ComputedStyle style, double parentEmPx, double rootEmPx, double pageWidthPx, double pageHeightPx,
        string scopeName, IDiagnosticsSink diagnostics)
    {
        if (!style.TryGetDeferred(PropertyId.FontSize, out var raw) || raw is null) return;
        double px;
        var isMathFunction = CalcLengthEvaluator.IsMathFunction(raw);
        var resolved = isMathFunction
            ? CalcLengthEvaluator.TryEvaluate(raw,
                new CalcLengthEvaluator.CalcContext(parentEmPx, parentEmPx, rootEmPx, pageWidthPx, pageHeightPx),
                out px)
            : RelativeLengthResolver.TryResolve(raw, parentEmPx, rootEmPx, pageWidthPx, pageHeightPx, out px);
        if (resolved)
        {
            style.Set(PropertyId.FontSize, ComputedSlot.FromLengthPx(px));
            return;
        }
        if (isMathFunction)
        {
            diagnostics.Emit(new Diagnostic(
                DiagnosticCodes.CssPropertyValueInvalid001,
                $"The {scopeName} font-size '{DiagnosticTextSanitizer.Sanitize(raw)}' could not be " +
                "resolved — the math-function expression is malformed/unsupported or its result is not " +
                "a finite non-negative size. The inherited/default font-size is used instead.",
                DiagnosticSeverity.Warning));
        }
    }

    /// <summary>Whether the box computes <c>box-sizing: border-box</c> (CSS Basic UI 4 §10) — its
    /// explicit §5.3 variable-axis <c>width</c>/<c>height</c> then specifies the BORDER box (the
    /// border+padding insets come out of the content area) instead of the content box (the initial
    /// <c>content-box</c> — insets add around it). Unset / <c>content-box</c> / an invalid keyword
    /// (rejected by the resolver) → <see langword="false"/>. <c>box-sizing</c> has no effect on a
    /// shrink-to-fit (<c>auto</c>) box — it only changes what an EXPLICIT size specifies.</summary>
    private static bool IsBorderBoxSizing(ComputedStyle style)
    {
        var slot = style.Get(PropertyId.BoxSizing);
        return slot.Tag == ComputedSlotTag.Keyword && slot.AsKeyword() == BoxSizingBorderBox;
    }

    /// <summary>Look up the running element's captured OWN-STYLE pairs (font/color + the element's own
    /// background-color / border-* — see <c>MarginContentCollector.CaptureOwnStyle</c>) for a standalone
    /// element(). <paramref name="first"/> selects the first vs last occurrence (matching the resolved
    /// text — in lockstep, post-PR-#151 review P1). Returns <see langword="null"/> when the name has no
    /// non-empty captured style (the caller then keeps the box's own style).</summary>
    private static IReadOnlyList<KeyValuePair<string, string>>? TryGetRunningElementOwnStyle(
        CssContentList.MarginContentContext marginContext, string name, bool first)
    {
        var dict = first ? marginContext.RunningElementStylesFirst : marginContext.RunningElementStyles;
        return dict is not null && dict.TryGetValue(name, out var pairs) && pairs.Count > 0 ? pairs : null;
    }

    /// <summary>The selected occurrence's per-line SEGMENTS (segment-style cycle), or
    /// <see langword="null"/> when none were collected — lockstep with the text/own-style
    /// dictionaries, so the segments always describe the SAME occurrence as the text.</summary>
    private static IReadOnlyList<CssContentList.RunningSegment>? TryGetRunningElementSegments(
        CssContentList.MarginContentContext marginContext, string name, bool first)
    {
        var dict = first ? marginContext.RunningElementSegmentsFirst : marginContext.RunningElementSegments;
        return dict is not null && dict.TryGetValue(name, out var segs) && segs.Count > 0 ? segs : null;
    }

    /// <summary>The selected occurrence's nested CONTAINER bands (container-bands cycle) —
    /// lockstep with <see cref="TryGetRunningElementSegments"/>'s occurrence selection.</summary>
    private static IReadOnlyList<CssContentList.RunningContainer>? TryGetRunningElementContainers(
        CssContentList.MarginContentContext marginContext, string name, bool first)
    {
        var dict = first ? marginContext.RunningElementContainersFirst : marginContext.RunningElementContainers;
        return dict is not null && dict.TryGetValue(name, out var c) && c.Count > 0 ? c : null;
    }

    /// <summary>Build a <see cref="ComputedStyle"/> from the running element's captured <paramref name="pairs"/>
    /// cascaded over <paramref name="pageContextStyle"/> via <see cref="MarginBoxStyle.Build"/>, split by
    /// <paramref name="decorationOnly"/>: <see langword="true"/> takes the element's NON-content pairs (the
    /// DECORATION — background-color / border-* / padding-*) so the result carries the decoration without
    /// overriding the box's font (used for <c>style</c>); <see langword="false"/> takes the CONTENT pairs
    /// (font/color) only — NOT the decoration — so a deferred-and-dropped padding isn't diagnosed twice (used
    /// for <c>contentStyle</c>, which only needs the shaping font/color). <paramref name="appendDeclarations"/>
    /// (the box's own declarations) are appended LAST so they WIN the cascade (the box overrides the element).
    /// The result is box-owned (not pooled), like the per-box style.</summary>
    private static ComputedStyle BuildFromOwnStyle(
        IReadOnlyList<KeyValuePair<string, string>> pairs, bool decorationOnly,
        ImmutableArray<CssDeclaration> appendDeclarations,
        ComputedStyle pageContextStyle, ICssDiagnosticsSink diagnostics)
    {
        var builder = ImmutableArray.CreateBuilder<CssDeclaration>(pairs.Count + appendDeclarations.Length);
        foreach (var kv in pairs)
            if (decorationOnly ? !IsElementContentProperty(kv.Key) : IsElementContentProperty(kv.Key))
                builder.Add(new CssDeclaration(kv.Key, new CssValue(kv.Value), false, CssSourceLocation.Unknown));
        if (!appendDeclarations.IsDefaultOrEmpty)
            builder.AddRange(appendDeclarations);
        return MarginBoxStyle.Build(builder.ToImmutable(), pageContextStyle, diagnostics);
    }

    /// <summary>The running element's OWN content alignment (Task 23) — its captured (inherited)
    /// <c>text-align</c> mapped to a leftover-space factor via <see cref="MarginBoxStyle.HorizontalAlignFactor"/>,
    /// or <see langword="null"/> when it declared none. <c>text-align</c> isn't a <c>MarginBoxStyle</c>
    /// longhand (so it isn't in the built styles); it's read straight from the captured pairs. The box's
    /// OWN <c>text-align</c> still takes precedence (the caller tries it first).</summary>
    /// <summary>The segment's own <c>line-height</c> pitch in px (segment-line-height cycle), read
    /// straight from the captured pairs (like <c>text-align</c> — not a <c>MarginBoxStyle</c>
    /// longhand): an absolute <c>&lt;length&gt;</c>, a UNITLESS multiplier (× the segment font), or
    /// an <c>em</c> factor (same base). <c>normal</c>, percentages, other relative units, or no
    /// declaration → <see langword="null"/> (the caller's font × 1.2 default).</summary>
    private static double? SegmentLineHeightPx(
        IReadOnlyList<KeyValuePair<string, string>> pairs, double segFontPx)
    {
        string? raw = null;
        foreach (var kv in pairs)
        {
            // The capture list holds ONE winner per property (NearestDeclaredWinner) — break on
            // the first match (PR #163 Copilot: the precedence is explicit, no wasted scan).
            if (kv.Key.Equals("line-height", StringComparison.OrdinalIgnoreCase))
            {
                raw = kv.Value;
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim();
        if (v.Equals("normal", StringComparison.OrdinalIgnoreCase)) return null;
        if (!LengthResolver.TrySplitNumberAndUnit(v, out var n, out var unit) || n < 0) return null;
        if (unit.Length == 0) return n * segFontPx;                       // unitless multiplier
        if (unit.Equals("em", StringComparison.OrdinalIgnoreCase)) return n * segFontPx;
        // px >= 0 (post-PR-#163 review P2): CSS line-height admits any NON-NEGATIVE value —
        // `line-height: 0px`/`0pt` collapses the pitch like the unitless 0, not the font default.
        return LengthResolver.TryAbsoluteUnitToPx(unit.ToLowerInvariant(), n, out var px)
            && double.IsFinite(px) && px >= 0 ? px : null;
    }

    private static double? ElementHorizontalAlignFactor(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        foreach (var kv in pairs)
            if (string.Equals(kv.Key, "text-align", StringComparison.OrdinalIgnoreCase))
                return MarginBoxStyle.HorizontalAlignFactor(ImmutableArray.Create(
                    new CssDeclaration(kv.Key, new CssValue(kv.Value), false, CssSourceLocation.Unknown)));
        return null;
    }

    /// <summary>The CSS-inherited CONTENT longhands (color / font-*) of a running element's captured
    /// own-style — the complement (background-color / border-*) is the element's DECORATION. Used to split
    /// the captured pairs when building the decoration-only <c>style</c> (the content pairs would otherwise
    /// override the box's font).</summary>
    private static bool IsElementContentProperty(string property) => property switch
    {
        "color" or "font-family" or "font-size" or "font-weight" or "font-style" => true,
        _ => false,
    };

    /// <summary>Whether the margin box's OWN declarations set <paramref name="property"/> (a longhand) —
    /// the currentcolor ORIGIN test (review P1). The box's declarations win the cascade, so a decoration
    /// property the box declares is box-owned (its currentcolor resolves against the box's colour), else it
    /// is the running element's. The box's `border`/`background` shorthands are already expanded to longhands
    /// upstream (the same declarations <see cref="MarginBoxStyle.Build"/> consumes), so this reads them
    /// directly.</summary>
    private static bool MarginBoxDeclares(ImmutableArray<CssDeclaration> declarations, string property)
    {
        if (declarations.IsDefaultOrEmpty) return false;
        foreach (var d in declarations)
            if (string.Equals(d.Property, property, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Whether the captured element pairs carry any DECORATION the nested band could paint
    /// (separately-decorated cycle): a background-color or a border-* longhand. Padding alone doesn't
    /// warrant a band (nothing visible to nest).</summary>
    private static bool HasDecorationPair(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        foreach (var kv in pairs)
        {
            if (kv.Key.Equals("background-color", StringComparison.OrdinalIgnoreCase)
                || kv.Key.StartsWith("border-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether the BOX's own declarations carry decoration (separately-decorated cycle) —
    /// the nesting trigger: only when the box paints its OWN band does the element's need a separate
    /// one (otherwise the element decorates the box band, the coinciding pre-cycle behavior).</summary>
    private static bool BoxDeclaresDecoration(ImmutableArray<CssDeclaration> declarations)
    {
        if (declarations.IsDefaultOrEmpty) return false;
        foreach (var d in declarations)
        {
            if (d.Property.Equals("background-color", StringComparison.OrdinalIgnoreCase)
                || d.Property.StartsWith("border-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The margin box's declared <c>background-image</c> as a single parsed url
    /// (margin-box-bg-image cycle), or <see langword="null"/> for unset / <c>none</c> / an
    /// unsupported form (gradient / multi-layer / unrecognized — surfaced once per Layout via
    /// the shared body code; callers that only PROBE pass a pre-set flag to suppress it). The
    /// RAW declaration winner is read via <see cref="RawDeclarationWinner"/> (last declaration
    /// wins — cascade order). INTERNAL so the pipeline's prefetch can collect the urls.</summary>
    internal static string? TryReadBackgroundImageUrl(
        ImmutableArray<CssDeclaration> declarations, IDiagnosticsSink diagnostics,
        ref bool unsupportedReported)
    {
        var raw = RawDeclarationWinner(declarations, "background-image");
        if (raw is null) return null;
        var v = raw.Trim();
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        if (ImageResourceCache.TryParseCssUrl(v, out var url)) return url;
        if (!unsupportedReported)
        {
            diagnostics.Emit(new Diagnostic(
                DiagnosticCodes.CssBackgroundImageUnsupported001,
                "A page margin box's background-image supports a single url(...) this cycle; a "
                + "gradient function, multi-layer list, or unrecognized form was ignored "
                + "(background-color still paints). Gradients are the Phase 4 shading-pattern work.",
                DiagnosticSeverity.Warning));
            unsupportedReported = true;
        }
        return null;
    }

    /// <summary>The LAST declaration of <paramref name="property"/> in the margin box's body
    /// (cascade order — last wins), or <see langword="null"/> when unset/blank. The raw-read
    /// fold shared by the background-image url + variant reads.</summary>
    private static string? RawDeclarationWinner(
        ImmutableArray<CssDeclaration> declarations, string property)
    {
        if (declarations.IsDefaultOrEmpty) return null;
        string? raw = null;
        foreach (var d in declarations)
            if (d.Property.Equals(property, StringComparison.OrdinalIgnoreCase))
                raw = d.Value.RawText;
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    /// <summary>Read a margin box's cascaded <c>background-origin</c>/<c>-clip</c> keyword (now in
    /// <c>MarginBoxStyle.CascadedStyleIds</c>, so it carries the production cascade's importance +
    /// CSS-wide + invalid-value handling — post-PR-#171 review P2) back as the area string
    /// <see cref="FragmentPainter.BackgroundAreaInset"/> expects. Indices match the keyword resolver's
    /// <c>T("border-box","padding-box","content-box")</c> table; an unset (or invalid → cleared) slot
    /// returns <see langword="null"/> so the inset helper applies the property's own initial
    /// (padding-box for origin, border-box for clip).</summary>
    private static string? ReadBackgroundAreaKeyword(ComputedStyle style, PropertyId id)
    {
        var slot = style.Get(id);
        if (slot.Tag != ComputedSlotTag.Keyword) return null;
        return slot.AsKeyword() switch
        {
            0 => "border-box",
            1 => "padding-box",
            2 => "content-box",
            _ => null,
        };
    }

    /// <summary>The <c>currentcolor</c> OWNER colour for ONE border edge (per-edge currentcolor
    /// cycle — replaces the whole-border ownership rule, whose mixed-origin case used the box colour
    /// for every edge): the BOX's colour when the box's own declarations claim the edge — its
    /// <paramref name="colorProperty"/> (the longhand a <c>currentcolor</c> value appears in) or,
    /// failing that, its <paramref name="styleProperty"/> (whoever turns the edge on owns it) — else
    /// the running element's. The box's shorthands are cascade-expanded to longhands upstream, so
    /// declaring <c>border</c>/<c>border-left</c> claims the matching edges here.</summary>
    private static uint EdgeCurrentColor(
        ImmutableArray<CssDeclaration> declarations, string colorProperty, string styleProperty,
        uint boxColor, uint elementColor) =>
        MarginBoxDeclares(declarations, colorProperty) || MarginBoxDeclares(declarations, styleProperty)
            ? boxColor : elementColor;

    /// <summary>A BUILT nested-container band (container-bands cycle): the container's own
    /// decoration style + its colour (the band's currentcolor owner) + the segment range it
    /// spans + its own horizontal margins (insetting its band only) + the boundary-gap parts
    /// INSIDE the band (its vertical border+padding strip + the inner gap — container-vpad
    /// cycle).</summary>
    private readonly record struct ContainerBand(
        ComputedStyle Style, uint ColorArgb, int FirstSegment, int LastSegment,
        double MarginLeftPx, double MarginRightPx,
        double LeadingInsidePx, double TrailingInsidePx);

    /// <summary>Per-box state computed in PASS 1 (style + content layout + DESIRED box rect), carried to
    /// PASS 2 so the §5.3 distribution can adjust the box rect for overlapping siblings in between. The
    /// box rect (<see cref="BoxXPx"/> … <see cref="BoxHeightPx"/>) is mutable for that adjustment.</summary>
    private sealed class MarginBoxItem
    {
        public PageMarginBoxGeometry.MarginBoxRegion Region;
        public ComputedStyle Style = null!;        // the BOX style — decoration (bg/border/padding) + insets.
        public ComputedStyle ContentStyle = null!; // the CONTENT style — usually == Style, but the running
                                                   // element's own font/colour for a standalone element().
        public uint BgCurrentColorArgb;     // currentcolor for the background — the OWNER's colour (review P1).
        public FragmentPainter.BorderEdgeCurrentColors BorderCurrentColors; // per-edge border currentcolor — each edge's OWNER colour.
        public InlineLayoutResult Inline;
        public bool HasLine;
        public WhiteSpace ReflowWhiteSpace;     // white-space for re-wrap / min-content (box's own, or pre-line/pre for forced breaks).
        public bool CanWrap;                    // white-space allows wrapping (Normal/PreWrap/PreLine/BreakSpaces).
        public bool OverflowVisible;            // explicit `overflow: visible` — opt OUT of clipping (clip-path cycle).
        public string Name = string.Empty;      // the margin-box name (e.g. "top-center") — for the overflow diagnostic.
        public string Text = string.Empty;     // raw content text — re-laid-out (wrapped) if the box shrinks.
        public TextRun[] ContentRuns = [];      // the layout runs (per-SEGMENT styles when >1 — segment-style cycle).
        public double[]? SegmentLineHeightsPx;   // per-SEGMENT pitch (segment-pitch cycle); null = uniform.
        public double[]? SegmentMarginTopsPx;    // per-SEGMENT vertical margins (segment-margins cycle).
        public double[]? SegmentMarginBottomsPx;
        public double[]? SegmentPaddingTopsPx;   // per-SEGMENT vertical padding (segment-padding cycle).
        public double[]? SegmentPaddingBottomsPx;
        public double[]? SegmentPaddingLeftsPx;  // per-SEGMENT horizontal padding (hpadding cycle).
        public double[]? SegmentPaddingRightsPx;
        public double[]? SegmentMarginLeftsPx;   // per-SEGMENT horizontal margins (segment-hmargins cycle).
        public double[]? SegmentMarginRightsPx;
        public List<ContainerBand>? ContainerBands; // nested CONTAINER bands (container-bands cycle), pre-order.
        public double?[]? SegmentAlignFactors;   // per-SEGMENT own text-align (segment-align cycle).
        public ComputedStyle?[]? SegmentDecorStyles; // per-SEGMENT own decoration band (segment-decor cycle).
        public uint[]? SegmentColorArgbs;        // per-SEGMENT colour — the band's currentcolor owner.
        public bool BoxAlignDeclared;            // the box declares text-align → it wins over per-segment.
        public ComputedStyle? ElementDecorStyle; // the running element's OWN decoration for the NESTED band
                                                 // (separately-decorated cycle) — null = coinciding single band.
        public uint ElementColorArgb;            // the element's own colour — the nested band's currentcolor.
        public string? BackgroundImageUrl;       // raw url from background-image (margin-box-bg-image cycle).
        public string? BackgroundRepeatRaw;      // raw background-repeat/-size/-position winners
        public string? BackgroundSizeRaw;        //   (PR #167 review P1 — margin-box variants
        public string? BackgroundPositionRaw;    //   wired through to the shared tiler).
        public string? BackgroundOriginRaw;      // raw background-origin/-clip winners (bg-origin /
        public string? BackgroundClipRaw;        //   bg-clip cycles — the box's positioning/paint area).
        public ComputedStyle? MetricsStyle;     // line-pitch style override (the LARGEST segment font) — null = ContentStyle.
        public double MinVarSizePx;             // min-content border-box size along the VARIABLE axis (= the
                                                // box's desired/max size for rigid/explicit/vertical boxes,
                                                // so they take the clamp path — only horizontal auto boxes flex).
        public double InsetLeftPx;
        public double InsetTopPx;
        public double InsetRightPx;
        public double InsetBottomPx;
        public double HAlign;
        public double VAlign;
        public double LineHeightPx;
        public double BoxXPx;
        public double BoxYPx;
        public double BoxWidthPx;
        public double BoxHeightPx;
    }

    /// <summary>Resolve §5.3 overlap among the boxes sharing each edge band, IN PLACE. Groups the
    /// non-corner items by edge (variable axis + the fixed-axis band coordinate), identifies each box's
    /// name-derived role (A start / B center / C end) from its region alignment on the variable axis,
    /// and runs <see cref="PageMarginBoxGeometry.ResolveEdgeOverlap"/> to size + place the boxes without
    /// overlap (a no-op when they already don't). Writes the resolved variable-axis size + start back
    /// onto each item's box rect. Edges are resolved INDEPENDENTLY, so the grouping order doesn't affect
    /// the output (PASS 2 still emits in the original box order).</summary>
    private static void ResolveEdgeOverlaps(List<MarginBoxItem> items)
    {
        if (items.Count < 2) return; // a single box can't overlap a sibling.

        // Group by edge: same variable axis + same fixed-axis band coordinate (region.Y for a horizontal
        // edge band, region.X for a vertical one). Corner boxes (axis None) are fixed — they don't group.
        Dictionary<(PageMarginBoxGeometry.MarginBoxAxis, double), List<MarginBoxItem>>? edges = null;
        foreach (var item in items)
        {
            var axis = item.Region.VariableAxis;
            if (axis == PageMarginBoxGeometry.MarginBoxAxis.None) continue;
            var key = (axis, axis == PageMarginBoxGeometry.MarginBoxAxis.Horizontal ? item.Region.Y : item.Region.X);
            edges ??= new Dictionary<(PageMarginBoxGeometry.MarginBoxAxis, double), List<MarginBoxItem>>();
            if (!edges.TryGetValue(key, out var list)) edges[key] = list = new List<MarginBoxItem>(3);
            list.Add(item);
        }
        if (edges is null) return;

        foreach (var edgeItems in edges.Values)
        {
            if (edgeItems.Count < 2) continue; // one box on the edge → nothing to resolve.
            var horizontal = edgeItems[0].Region.VariableAxis == PageMarginBoxGeometry.MarginBoxAxis.Horizontal;
            var available = horizontal ? edgeItems[0].Region.Width : edgeItems[0].Region.Height;

            // Identify the up-to-three boxes by their name-derived role on the variable axis (0 = start,
            // 0.5 = center, 1 = end). Distinct names → distinct roles, so at most one of each.
            MarginBoxItem? a = null, b = null, c = null;
            foreach (var item in edgeItems)
            {
                var role = horizontal ? item.Region.HAlign : item.Region.VAlign;
                if (role <= 0.25) a = item;
                else if (role >= 0.75) c = item;
                else b = item;
            }

            var resolved = PageMarginBoxGeometry.ResolveEdgeOverlap(
                new PageMarginBoxGeometry.EdgeTriple(
                    a is null ? 0 : VarSize(a, horizontal), a is not null,
                    b is null ? 0 : VarSize(b, horizontal), b is not null,
                    c is null ? 0 : VarSize(c, horizontal), c is not null,
                    a?.MinVarSizePx ?? 0, b?.MinVarSizePx ?? 0, c?.MinVarSizePx ?? 0),
                available);

            if (a is not null) ApplyResolved(a, horizontal, resolved.SizeA, resolved.StartA);
            if (b is not null) ApplyResolved(b, horizontal, resolved.SizeB, resolved.StartB);
            if (c is not null) ApplyResolved(c, horizontal, resolved.SizeC, resolved.StartC);
        }

        static double VarSize(MarginBoxItem item, bool horizontal) =>
            horizontal ? item.BoxWidthPx : item.BoxHeightPx;

        static void ApplyResolved(MarginBoxItem item, bool horizontal, double size, double start)
        {
            if (horizontal) { item.BoxWidthPx = size; item.BoxXPx = item.Region.X + start; }
            else { item.BoxHeightPx = size; item.BoxYPx = item.Region.Y + start; }
        }
    }

    private static void EmitContentUnsupported(IDiagnosticsSink diagnostics, string boxName, string raw) =>
        diagnostics.Emit(new Diagnostic(
            DiagnosticCodes.CssContentFunctionUnsupported001,
            // Sanitize the author-supplied value (strip control chars + cap length) before
            // interpolating it into a host-visible message — same hardening as the CSS pipeline's
            // diagnostic path (post-PR-#132 review P2; DiagnosticTextSanitizer).
            $"The page margin box @{boxName} uses a `content` value that is not yet supported " +
            $"(\"{DiagnosticTextSanitizer.Sanitize(raw)}\"). Supported: literal strings, attr(name), " +
            "counter(page)/counter(pages) (with a predefined counter style — decimal / decimal-leading-zero / " +
            "lower+upper-roman / lower+upper-alpha / -latin / lower-greek; an unknown style falls back to " +
            "decimal), string(name[, first|last]), and element(name[, first|last]). " +
            "Any other generated content — a non-page counter() name, counters(), the start/first-except " +
            "string()/element() keywords, " +
            "url()/image()/image-set(), open-quote/close-quote — is a tracked " +
            "follow-up (deferrals.md#layout-to-pdf-pipeline). The box was not painted.",
            DiagnosticSeverity.Warning));

    private static void EmitFontUnresolved(IDiagnosticsSink diagnostics, string boxName, string detail) =>
        diagnostics.Emit(new Diagnostic(
            DiagnosticCodes.PaintTextFontUnresolved001,
            $"The page margin box @{boxName} was not painted because its font could not be " +
            "resolved during layout: " + DiagnosticTextSanitizer.Sanitize(detail) + " A bundled " +
            "deterministic last-resort font (so the default path always resolves) is a tracked " +
            "follow-up (deferrals.md#layout-to-pdf-pipeline).",
            DiagnosticSeverity.Warning));

    private static void EmitContentOverflow(
        IDiagnosticsSink diagnostics, string boxName, double contentHeightPx, double availableHeightPx,
        int keptLines, int totalLines) =>
        diagnostics.Emit(new Diagnostic(
            DiagnosticCodes.PaintMarginBoxContentOverflow001,
            $"The page margin box @{boxName} has content taller than its area " +
            $"({contentHeightPx:0.#}px content vs {availableHeightPx:0.#}px available): the box was clamped " +
            "to the page-margin band but its content block-height exceeds the available height (a vertical " +
            "left/right edge box at the band limit, or a multi-line element() running header). The overflow " +
            $"was clipped at line granularity — {keptLines} of {totalLines} line(s) painted. Declare " +
            "`overflow: visible` on the box to let the content spill instead.",
            DiagnosticSeverity.Warning));

    private static void EmitContentOverflowWidth(
        IDiagnosticsSink diagnostics, string boxName, double contentWidthPx, double availableWidthPx) =>
        diagnostics.Emit(new Diagnostic(
            DiagnosticCodes.PaintMarginBoxContentOverflow001,
            $"The page margin box @{boxName} has content wider than its area " +
            $"({contentWidthPx:0.#}px content vs {availableWidthPx:0.#}px available to the clip edge): a " +
            "line's unbreakable content crosses the box's padding-box edge — the clip edge of CSS " +
            "overflow — (a long word in a narrow band, a clamped rigid sibling, or a nowrap box). The " +
            "protruding glyphs were clipped there via a PDF clip path. Declare `overflow: visible` on the " +
            "box to let the content spill instead.",
            DiagnosticSeverity.Warning));
}
