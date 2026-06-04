// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using AngleSharp.Dom;
using NetPdf.Css.ComputedValues;
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
/// <b>Style (cycles 4–8 + border + padding).</b> The box's declared <c>font-family</c> /
/// <c>font-size</c> / <c>font-weight</c> / <c>font-style</c> / <c>color</c> flow through the shaper +
/// painter (inherited along root → page context → box; relative sizes + the <c>font</c> shorthand
/// resolved); a declared <c>text-align</c> / <c>vertical-align</c> overrides the box's name-derived
/// alignment. A declared <c>background-color</c> (cycle 8, non-inherited) fills a band over the box's
/// full region behind the content — collected as a <see cref="MarginBoxBackgroundFill"/>, painted by
/// <see cref="PaintBackgrounds"/>; a declared <c>border</c> (non-inherited, the <c>border</c> /
/// <c>border-&lt;side&gt;</c> shorthands expanded for margin-box bodies) strokes the box's full
/// region — collected as a <see cref="MarginBoxBorder"/>, painted by <see cref="PaintBorders"/> via
/// the shared <see cref="FragmentPainter.PaintBorders"/>; a declared <c>padding</c> (non-inherited,
/// the 1–4-value box shorthand + per-side longhands) insets the text content origin, together with the
/// used border-width per side (CSS box model). Unspecified properties fall back to the reader defaults
/// (16px / default family / black / transparent / no border / no padding / name-derived alignment).
/// The <c>border-width</c>/<c>-style</c>/<c>-color</c> box shorthands distribute across the edges too
/// (border-box cycle); <c>border-radius</c> + background images are tracked follow-ups
/// (deferrals.md#layout-to-pdf-pipeline).
/// </para>
/// <para>
/// <b>Content scope.</b> Literal-string + <c>attr()</c> content, plus <c>counter(page)</c> /
/// <c>counter(pages)</c> page numbers (cycle 9 — resolved against the page being painted; a single
/// page renders <c>1</c> until the multi-page driver lands). A winning <c>none</c> / <c>normal</c>
/// suppresses the box upstream (the resolver omits it). Still-unsupported content (a non-page
/// <c>counter()</c> / <c>counters()</c> / <c>string()</c> / <c>element()</c>) emits
/// <c>CSS-CONTENT-FUNCTION-UNSUPPORTED-001</c> and the box is skipped. The §5.3 three-box-per-edge
/// sizing is a later cycle.
/// </para>
/// </remarks>
internal static class PageMarginBoxPainter
{
    private const double NormalLineHeightFactor = 1.2; // mirrors TextPainter's line-height: normal.
    private const uint DefaultColorArgb = 0xFF000000;   // CSS initial `color` (opaque black) — currentColor fallback.

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
    /// <param name="shaper">The SAME resolver the body shaped with, kept alive past layout.</param>
    /// <param name="diagnostics">Sink for unsupported-content / unresolved-font diagnostics.</param>
    public static MarginBoxLayoutResult Layout(
        ImmutableArray<AtPageMarginBoxResolver.ResolvedMarginBox> boxes,
        double pageWidthPx, double pageHeightPx,
        double marginTopPx, double marginRightPx, double marginBottomPx, double marginLeftPx,
        double contentOriginLeftPx, double contentOriginTopPx,
        IElement host, ComputedStyle? rootElementStyle, ImmutableArray<CssDeclaration> pageDeclarations,
        CssContentList.PageCounters pageCounters,
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

        var fragments = new List<BoxFragment>(boxes.Length);
        var backgrounds = new List<MarginBoxBackgroundFill>();
        var borders = new List<MarginBoxBorder>();
        foreach (var mb in boxes)
        {
            if (!CssContentList.TryParse(mb.ContentRawValue, host, pageCounters, out var text))
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
            // inset below). The style is box-owned, so the rented instance isn't pooled — a negligible
            // per-render miss.
            var style = MarginBoxStyle.Build(mb.Declarations, pageContextStyle, styleDiagnostics);

            // Background-color (cycle 8): a declared (non-inherited) background-color fills a band over
            // the box's FULL region, behind the content. `currentcolor` resolves against the box's own
            // color; transparent (the initial value) or unset paints nothing. Collected BEFORE the text
            // layout so the band still shows when the text is empty or its font fails to resolve.
            var currentColor = FragmentPainter.TryResolveColor(style.Get(PropertyId.Color), DefaultColorArgb, out var fg)
                ? fg : DefaultColorArgb;
            if (FragmentPainter.TryResolveColor(style.Get(PropertyId.BackgroundColor), currentColor, out var bgArgb)
                && FragmentPainter.Alpha(bgArgb) != 0)
            {
                backgrounds.Add(new MarginBoxBackgroundFill(region.X, region.Y, region.Width, region.Height, bgArgb));
            }

            // Border (border cycle): a declared border strokes the box's FULL region (currentcolor =
            // the box's own color). Collected with the style (which carries the border-* longhands)
            // before the text layout, so an empty/failed-font box still shows its border.
            if (HasBorder(style))
                borders.Add(new MarginBoxBorder(region.X, region.Y, region.Width, region.Height, style, currentColor));

            // `content: ""` generates the box (CSS Page 3 §6.1: content is not none/normal) — the
            // band above still paints, but there is no text to lay out, so skip the text fragment
            // (post-PR-#137 review P2).
            if (string.IsNullOrEmpty(text)) continue;

            var box = Box.TextRun(string.Empty, style);
            var lineHeightPx = style.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16) * NormalLineHeightFactor;

            InlineLayoutResult inline;
            try
            {
                inline = InlineLayouter.Layout(
                    sourceTextRuns: new[] { new TextRun(text, style) },
                    availableInlineSize: availableInlinePx,
                    resolver: shaper,
                    scriptIso15924: "Latn",
                    language: "en",
                    paragraphDirection: ParagraphDirection.LeftToRight,
                    whiteSpace: WhiteSpace.NoWrap);
            }
            catch (FontResolutionException ex)
            {
                // Shaping the margin-box text needs a font; if none resolves (the default
                // SystemFontResolver path, no bundled fallback yet) degrade to a diagnostic
                // instead of crashing the render (CLAUDE.md #7) — mirrors the body path.
                EmitFontUnresolved(diagnostics, mb.Name, ex.Message);
                continue;
            }
            if (inline.Lines.Length == 0) continue;

            // Alignment within the CONTENT box: a text-align / vertical-align DECLARED ON THE BOX
            // overrides the box's name-derived default (e.g. top-left → start); otherwise keep the
            // default. Read from the box's own declarations (not the inherited style) so the
            // page/root's UA-default text-align can't override the name-derived alignment (review).
            var hAlign = MarginBoxStyle.HorizontalAlignFactor(mb.Declarations) ?? region.HAlign;
            var vAlign = MarginBoxStyle.VerticalAlignFactor(mb.Declarations) ?? region.VAlign;

            // Content box (padding cycle): the text is inset from the region by the used border-width
            // + padding on each side. TextPainter.CollectFragment ALREADY shifts a fragment's inline
            // content right/down by the box's border-left/top + padding-left/top (the body path's
            // border-box → content-box step), so here we only SHRINK the alignment extent to the
            // content box and place the line at the BORDER-box origin — adding the inset to the
            // placement here too would DOUBLE it. ReadLengthPxOrZero applies the §4.3 border-width
            // used-value gate (none/hidden → 0), so the reserved space matches what the painter strokes.
            // Clamp the content box to >= 0 so an over-large inset (padding/border exceeding the band)
            // stays finite.
            var insetLeftPx = style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth) + style.ReadLengthPxOrZero(PropertyId.PaddingLeft);
            var insetTopPx = style.ReadLengthPxOrZero(PropertyId.BorderTopWidth) + style.ReadLengthPxOrZero(PropertyId.PaddingTop);
            var insetRightPx = style.ReadLengthPxOrZero(PropertyId.BorderRightWidth) + style.ReadLengthPxOrZero(PropertyId.PaddingRight);
            var insetBottomPx = style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth) + style.ReadLengthPxOrZero(PropertyId.PaddingBottom);
            var contentWidthPx = Math.Max(0, region.Width - insetLeftPx - insetRightPx);
            var contentHeightPx = Math.Max(0, region.Height - insetTopPx - insetBottomPx);

            // Place + align the line within the content box, expressed at the BORDER-box origin
            // (region.X/Y); TextPainter adds the border+padding inset to land it in the content box.
            var lineWidthPx = inline.Lines[0].TotalAdvance;
            var absLeftPx = region.X + (contentWidthPx - lineWidthPx) * hAlign;
            var absTopPx = region.Y + (contentHeightPx - lineHeightPx) * vAlign;

            fragments.Add(new BoxFragment(
                Box: box,
                InlineOffset: absLeftPx - contentOriginLeftPx,
                BlockOffset: absTopPx - contentOriginTopPx,
                InlineSize: contentWidthPx,
                BlockSize: lineHeightPx * inline.Lines.Length,
                InlineLayout: inline));
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

    private static void EmitContentUnsupported(IDiagnosticsSink diagnostics, string boxName, string raw) =>
        diagnostics.Emit(new Diagnostic(
            DiagnosticCodes.CssContentFunctionUnsupported001,
            // Sanitize the author-supplied value (strip control chars + cap length) before
            // interpolating it into a host-visible message — same hardening as the CSS pipeline's
            // diagnostic path (post-PR-#132 review P2; DiagnosticTextSanitizer).
            $"The page margin box @{boxName} uses a `content` value that is not yet supported " +
            $"(\"{DiagnosticTextSanitizer.Sanitize(raw)}\"). Supported: literal strings, attr(name), " +
            "and counter(page)/counter(pages) with the default decimal style. Any other generated content " +
            "— a non-page counter() name, a non-decimal counter style (e.g. lower-roman), counters(), " +
            "url()/image()/image-set(), string()/element(), open-quote/close-quote — is a tracked follow-up " +
            "(deferrals.md#layout-to-pdf-pipeline). The box was not painted.",
            DiagnosticSeverity.Warning));

    private static void EmitFontUnresolved(IDiagnosticsSink diagnostics, string boxName, string detail) =>
        diagnostics.Emit(new Diagnostic(
            DiagnosticCodes.PaintTextFontUnresolved001,
            $"The page margin box @{boxName} was not painted because its font could not be " +
            "resolved during layout: " + DiagnosticTextSanitizer.Sanitize(detail) + " A bundled " +
            "deterministic last-resort font (so the default path always resolves) is a tracked " +
            "follow-up (deferrals.md#layout-to-pdf-pipeline).",
            DiagnosticSeverity.Warning));
}
