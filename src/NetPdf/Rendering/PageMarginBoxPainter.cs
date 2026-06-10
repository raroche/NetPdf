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
/// the shared <see cref="FragmentPainter.PaintBorders"/>; a declared <c>padding</c> (non-inherited,
/// the 1–4-value box shorthand + per-side longhands) insets the text content origin, together with the
/// used border-width per side (CSS box model); a declared <c>width</c> (top/bottom) / <c>height</c>
/// (left/right) sizes the box along its §5.3 variable axis (explicit-size cycle — see <see cref="Layout"/>).
/// Unspecified properties fall back to the reader defaults
/// (16px / default family / black / transparent / no border / no padding / auto size / name-derived alignment).
/// The <c>border-width</c>/<c>-style</c>/<c>-color</c> box shorthands distribute across the edges as
/// well; <c>border-radius</c> + background images are tracked follow-ups
/// (deferrals.md#layout-to-pdf-pipeline).
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
        double LeftPx, double TopPx, double WidthPx, double HeightPx, uint Argb);

    /// <summary>A margin box's declared borders to stroke around its region rect (border cycle):
    /// the page-px rect + the box's <see cref="ComputedStyle"/> (carrying the <c>border-*</c>
    /// longhands) + the <c>currentcolor</c> for an unset border-color (the box's own color). Painted
    /// by <see cref="PaintBorders"/> via the shared <see cref="FragmentPainter.PaintBorders"/>.</summary>
    internal readonly record struct MarginBoxBorder(
        double LeftPx, double TopPx, double WidthPx, double HeightPx, ComputedStyle Style, uint CurrentColorArgb);

    /// <summary>The result of laying out the page margin boxes: the text fragments (for the shared
    /// <see cref="TextPainter"/> pass) + the background bands (cycle 8) + the borders (border cycle).</summary>
    internal sealed record MarginBoxLayoutResult(
        IReadOnlyList<BoxFragment> Fragments,
        IReadOnlyList<MarginBoxBackgroundFill> Backgrounds,
        IReadOnlyList<MarginBoxBorder> Borders)
    {
        public static readonly MarginBoxLayoutResult Empty = new(
            Array.Empty<BoxFragment>(), Array.Empty<MarginBoxBackgroundFill>(), Array.Empty<MarginBoxBorder>());
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
            // decorated nesting); the element's nested BLOCK children stay deferred (deferrals.md). Relative
            // units / `inherit` resolve against the page context (exact for the common absolute values).
            ComputedStyle style, contentStyle;
            double? elementHAlign = null;   // the running element's own text-align factor (Task 23), if any.
            if (CssContentList.TryGetStandaloneElement(mb.ContentRawValue, out var elName, out var elFirst)
                && TryGetRunningElementOwnStyle(marginContext, elName, elFirst) is { } ownPairs)
            {
                contentStyle = BuildFromOwnStyle(ownPairs, decorationOnly: false,
                    ImmutableArray<CssDeclaration>.Empty, pageContextStyle, styleDiagnostics);
                style = BuildFromOwnStyle(ownPairs, decorationOnly: true,
                    mb.Declarations, pageContextStyle, styleDiagnostics);
                elementHAlign = ElementHorizontalAlignFactor(ownPairs);
            }
            else
            {
                style = MarginBoxStyle.Build(mb.Declarations, pageContextStyle, styleDiagnostics);
                contentStyle = style;
            }

            // currentcolor ORIGIN (CSS Color 4 §6.2 — currentcolor resolves to the `color` of the style that
            // OWNS the property). The box's declarations WIN the cascade (appended last), so a decoration
            // property is BOX-owned iff the box declares it, else ELEMENT-owned (post-PR-#152 review P1): a
            // box-declared `background-color`/`border` currentcolor resolves against the BOX's colour, an
            // element-declared one against the RUNNING ELEMENT's. `style.Get(Color)` is the box's colour (the
            // decoration build excludes the element's colour + appends the box's), `contentStyle` the
            // element's. For the non-element path `style == contentStyle`, so both reduce to the box colour —
            // byte-identical to the prior single currentColor. (A box that declares ONE edge + the element
            // another is an approximation — it uses the box colour for the whole border; deferrals.md.)
            var elementColor = FragmentPainter.TryResolveColor(contentStyle.Get(PropertyId.Color), DefaultColorArgb, out var ec)
                ? ec : DefaultColorArgb;
            var boxColor = FragmentPainter.TryResolveColor(style.Get(PropertyId.Color), DefaultColorArgb, out var bc)
                ? bc : DefaultColorArgb;
            var bgCurrentColor = MarginBoxDeclares(mb.Declarations, "background-color") ? boxColor : elementColor;
            var borderCurrentColor = MarginBoxDeclaresBorder(mb.Declarations) ? boxColor : elementColor;

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
            var lineHeightPx = contentStyle.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16) * NormalLineHeightFactor;
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
            if (!string.IsNullOrEmpty(text))
            {
                try
                {
                    var laid = InlineLayouter.Layout(
                        sourceTextRuns: new[] { new TextRun(text, contentStyle) },
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
                    boxHeightPx = Math.Min(region.Height, (lineHeightPx * inline.Lines.Length) + insetTopPx + insetBottomPx);
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
                && TryMeasureMinContentWidthPx(text, contentStyle, shaper, reflowWhiteSpace, out var minContentWidthPx))
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
                BgCurrentColorArgb = bgCurrentColor, BorderCurrentColorArgb = borderCurrentColor,
                Inline = inline, HasLine = hasLine, Text = text, MinVarSizePx = minVarSizePx,
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
                && TryLayoutContent(item.Text, item.ContentStyle, shaper, contentWidthPx, item.ReflowWhiteSpace, out var wrapped))
            {
                item.Inline = wrapped;
            }
        }

        // PASS 2 — emit each box's background band (cycle 8) + border (border cycle) + text fragment from
        // its (possibly adjusted) box rect. Aliased back to the per-box locals the emission code uses.
        var fragments = new List<BoxFragment>(items.Count);
        var backgrounds = new List<MarginBoxBackgroundFill>();
        var borders = new List<MarginBoxBorder>();
        foreach (var item in items)
        {
            var style = item.Style;
            var bgCurrentColor = item.BgCurrentColorArgb;
            var borderCurrentColor = item.BorderCurrentColorArgb;
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
            if (FragmentPainter.TryResolveColor(style.Get(PropertyId.BackgroundColor), bgCurrentColor, out var bgArgb)
                && FragmentPainter.Alpha(bgArgb) != 0)
            {
                backgrounds.Add(new MarginBoxBackgroundFill(boxXPx, boxYPx, boxWidthPx, boxHeightPx, bgArgb));
            }

            // Border (border cycle): strokes the BOX. `currentcolor` resolves against the OWNER's color
            // (the box's if the box declares the border, else the running element's — review P1).
            if (HasBorder(style))
                borders.Add(new MarginBoxBorder(boxXPx, boxYPx, boxWidthPx, boxHeightPx, style, borderCurrentColor));

            if (!hasLine) continue; // empty / failed-font box: decoration only, no text fragment.

            // Place the content box at the BORDER-box origin (boxX/boxY); TextPainter adds the
            // border+padding inset to reach the content-box origin. Horizontal alignment is handed to
            // TextPainter as a PER-LINE factor (LineAlignFactor = hAlign) so a WRAPPED multi-line run is
            // aligned line-by-line within the content box (Task 21 — wrapped-line content-alignment), not
            // just block-left. A single line reduces to the old block-level alignment (byte-identical):
            // its leftover (contentBoxWidth − lineAdvance) × hAlign is exactly what was pre-applied before.
            // A SHRINK-TO-FIT box is content-sized so the leftover is ~0 (no visible shift); an explicit-
            // width / flexed / wrapped box has room, so the alignment is observable.
            var blockHeightPx = lineHeightPx * inline.Lines.Length;
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
                var keptLines = MaxLinesThatFit(contentBoxHeightPx, lineHeightPx, inline.Lines.Length);
                EmitContentOverflow(
                    diagnostics, item.Name, blockHeightPx, contentBoxHeightPx, keptLines, inline.Lines.Length);
                if (keptLines <= 0) continue; // nothing fits — decoration only, no text fragment.
                inline = inline with { Lines = inline.Lines[..keptLines] };
                blockHeightPx = lineHeightPx * keptLines;
            }
            // HORIZONTAL overflow of the surviving lines (clip-path cycle): a line that protrudes past
            // the box's CLIP EDGE — an unbreakable run wider than the box (a long word in a narrow
            // band / a clamped rigid sibling), or a `nowrap` box — would previously SPILL over the page.
            // Now the fragment carries a CLIP RECT (the box's PADDING box, per CSS Overflow 3 §3 — the
            // clip edge of overflow ≠ visible) and the shared TextPainter wraps its glyph runs in a PDF
            // `q <rect> re W n … Q` clip path, so the protruding GLYPHS clip at the box edge
            // (partial-glyph clipping — the vertically-truncated lines above stay whole-line). The
            // predicate uses the SAME geometry as the rect (post-PR-#156 review P2): a line starts at the
            // CONTENT-box left (= padding-box left + padding-left; a line wider than the content box
            // isn't alignment-shifted — TextPainter clamps its shift to ≥ 0), so it crosses the clip edge
            // only when padding-left + advance exceeds the PADDING-box width — overflow into the right
            // padding stays inside the clip edge and must trip neither the clip nor the diagnostic.
            // Surfaced per box (same code, width-phrased — CLAUDE.md #7); `overflow: visible` opts out. A
            // box whose content fits carries no clip rect, so its stream is byte-identical.
            FragmentClipRect? clipRect = null;
            var widestSurvivingPx = WidestLineAdvancePx(inline);
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
                TextMetricsStyle: item.ContentStyle,
                // The padding-box clip for horizontally-overflowing content (clip-path cycle) — null for
                // a fitting box, so its stream is byte-identical.
                ClipRect: clipRect));
        }

        // The page-context style is only a parent — each box copied the slots it needs — so return
        // it to the pool now instead of leaking it (post-PR-#134 review; the per-box styles stay
        // box-owned with their synthetic Box).
        pageContextStyle.ReleaseFromBox();
        return new MarginBoxLayoutResult(fragments, backgrounds, borders);
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
            page.FillRectangle(x, y, w, h, r, g, b, FragmentPainter.Alpha(bg.Argb) / 255.0);
        }
    }

    /// <summary>Stroke the resolved margin-box borders (border cycle) onto <paramref name="page"/>,
    /// over the background bands but behind the shared text pass — reusing the shared
    /// <see cref="FragmentPainter.PaintBorders"/> (so margin + body borders render identically). Call
    /// AFTER <see cref="PaintBackgrounds"/> and BEFORE <see cref="TextPainter.PaintText"/>.</summary>
    public static void PaintBorders(
        PdfPage page, IReadOnlyList<MarginBoxBorder> borders, double pageHeightPt, IDiagnosticsSink diagnostics)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (borders is null) return;
        var styleApproximationReported = false;
        foreach (var b in borders)
            FragmentPainter.PaintBorders(page, b.Style, pageHeightPt, b.LeftPx, b.TopPx, b.WidthPx, b.HeightPx,
                b.CurrentColorArgb, diagnostics, ref styleApproximationReported);
    }

    /// <summary>Whether <paramref name="style"/> declares any border edge (a <c>border-*-style</c> is
    /// set) — so a borderless box isn't collected for the border pass.</summary>
    private static bool HasBorder(ComputedStyle style) =>
        style.IsSet(PropertyId.BorderTopStyle) || style.IsSet(PropertyId.BorderRightStyle)
        || style.IsSet(PropertyId.BorderBottomStyle) || style.IsSet(PropertyId.BorderLeftStyle);

    /// <summary>Lay <paramref name="text"/> out in <paramref name="style"/> at <paramref name="widthPx"/>
    /// with the given wrapping mode. Returns <see langword="false"/> on empty text, no line, or a
    /// font-resolution failure. Used for the §5.3 min-content measurement + the overflow re-wrap.</summary>
    private static bool TryLayoutContent(
        string text, ComputedStyle style, HarfBuzzShaperResolver shaper, double widthPx, WhiteSpace whiteSpace,
        out InlineLayoutResult result)
    {
        result = default;
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            var laid = InlineLayouter.Layout(
                sourceTextRuns: new[] { new TextRun(text, style) },
                availableInlineSize: Math.Max(widthPx, 1.0), resolver: shaper,
                scriptIso15924: "Latn", language: "en",
                paragraphDirection: ParagraphDirection.LeftToRight, whiteSpace: whiteSpace);
            if (laid.Lines.Length == 0) return false;
            result = laid;
            return true;
        }
        catch (FontResolutionException) { return false; }
    }

    /// <summary>The min-content WIDTH (px) of <paramref name="text"/> in <paramref name="whiteSpace"/> — the
    /// longest unbreakable run, found by wrapping at a tiny width so each break opportunity splits a line;
    /// the widest resulting line is the min-content. The caller passes the box's COMPUTED white-space (and
    /// only calls this for a wrapping mode) so a <c>nowrap</c>/<c>pre</c> box isn't measured as if it could
    /// wrap (Copilot review). <see langword="false"/> on a font-resolution failure.</summary>
    private static bool TryMeasureMinContentWidthPx(
        string text, ComputedStyle style, HarfBuzzShaperResolver shaper, WhiteSpace whiteSpace, out double minContentPx)
    {
        minContentPx = 0;
        if (!TryLayoutContent(text, style, shaper, 1.0, whiteSpace, out var laid)) return false;
        minContentPx = WidestLineAdvancePx(laid);
        return true;
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

    /// <summary>The bases a deferred relative explicit size resolves against (relative-units cycle):
    /// the box's resolved font-size (<c>em</c>/<c>ex</c>/<c>ch</c>), the root element's (<c>rem</c>),
    /// and the page box (viewport units — CSS Paged Media maps the viewport to the page).</summary>
    private readonly record struct RelativeSizeBases(
        double EmPx, double RootEmPx, double PageWidthPx, double PageHeightPx);

    /// <summary>The explicit size (CSS px) a margin box declares on its §5.3 VARIABLE axis
    /// — <c>width</c> for top/bottom boxes, <c>height</c> for left/right — or <see langword="null"/>
    /// when it's <c>auto</c>/unresolved (the caller shrink-to-fits). An absolute length is used as-is; a
    /// percentage resolves against <paramref name="bandExtentPx"/> (the box's containing block on that
    /// axis); a DEFERRED font-/viewport-relative length (<c>10em</c> / <c>1.5rem</c> / <c>50vw</c> —
    /// kept as a deferred raw by <see cref="MarginBoxStyle"/>, relative-units cycle) resolves against
    /// <paramref name="bases"/> via <see cref="RelativeLengthResolver"/>. The caller maps the size to
    /// the border-box per the box's <c>box-sizing</c> (<see cref="IsBorderBoxSizing"/>): content-box
    /// (the initial) adds the border+padding insets, border-box floors at them. A <c>calc()</c> /
    /// container-relative / malformed size is diagnosed + DROPPED upstream by
    /// <see cref="MarginBoxStyle"/> (post-PR-#144 review), so it never reaches here — this reads it as
    /// <c>auto</c> and the caller shrink-to-fits. A kept relative size that still fails to resolve IN
    /// CONTEXT (a syntactically-supported value whose product is non-finite, e.g. <c>1e308em</c>) is
    /// SURFACED via <paramref name="diagnostics"/> before the shrink-to-fit fallback (post-PR-#156
    /// review P2 — the keep gate is syntactic, so the contextual failure must not be silent). Negatives
    /// are rejected upstream (non-negative property); the <c>Max(0, …)</c> is defensive.</summary>
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

    /// <summary>Resolve a KEPT deferred length raw — a <c>calc()</c> expression (calc cycle, percent
    /// terms against <paramref name="percentBasePx"/>) or a font-/viewport-relative length
    /// (relative-units cycle) — to used px against <paramref name="bases"/>. Shared by the explicit
    /// §5.3 size and the padding resolve.</summary>
    private static bool TryResolveDeferredLengthPx(
        string raw, double percentBasePx, in RelativeSizeBases bases, out double px)
    {
        return CalcLengthEvaluator.IsCalc(raw)
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

    /// <summary>Whether the margin box declares a BORDER of its own — any per-edge <c>border-*-style</c>
    /// (a border paints only with a style, so the style longhands are what make it the box's). A box-declared
    /// border's currentcolor resolves against the box's colour (review P1). The per-side mixed-origin case
    /// (box declares one edge, the element another) uses the box colour for the whole border — an
    /// approximation, since the shared <see cref="FragmentPainter.PaintBorders"/> takes one currentcolor
    /// (deferrals.md).</summary>
    private static bool MarginBoxDeclaresBorder(ImmutableArray<CssDeclaration> declarations) =>
        MarginBoxDeclares(declarations, "border-top-style") || MarginBoxDeclares(declarations, "border-right-style")
        || MarginBoxDeclares(declarations, "border-bottom-style") || MarginBoxDeclares(declarations, "border-left-style");

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
        public uint BorderCurrentColorArgb; // currentcolor for the border — the OWNER's colour (review P1).
        public InlineLayoutResult Inline;
        public bool HasLine;
        public WhiteSpace ReflowWhiteSpace;     // white-space for re-wrap / min-content (box's own, or pre-line/pre for forced breaks).
        public bool CanWrap;                    // white-space allows wrapping (Normal/PreWrap/PreLine/BreakSpaces).
        public bool OverflowVisible;            // explicit `overflow: visible` — opt OUT of clipping (clip-path cycle).
        public string Name = string.Empty;      // the margin-box name (e.g. "top-center") — for the overflow diagnostic.
        public string Text = string.Empty;     // raw content text — re-laid-out (wrapped) if the box shrinks.
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
