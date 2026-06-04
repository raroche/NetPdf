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
/// <b>Style (cycles 4–8).</b> The box's declared <c>font-family</c> / <c>font-size</c> /
/// <c>font-weight</c> / <c>font-style</c> / <c>color</c> flow through the shaper + painter (inherited
/// along root → page context → box; relative sizes + the <c>font</c> shorthand resolved); a declared
/// <c>text-align</c> / <c>vertical-align</c> overrides the box's name-derived alignment. A declared
/// <c>background-color</c> (cycle 8, non-inherited) fills a band over the box's full region behind
/// the content — collected here as a <see cref="MarginBoxBackgroundFill"/>, painted by
/// <see cref="PaintBackgrounds"/>. Unspecified properties fall back to the reader defaults (16px /
/// default family / black / transparent / name-derived alignment). <c>border</c> / <c>padding</c> /
/// background images are tracked follow-ups (deferrals.md#layout-to-pdf-pipeline).
/// </para>
/// <para>
/// <b>Content scope.</b> Literal-string + <c>attr()</c> content only. A winning <c>none</c> /
/// <c>normal</c> suppresses the box upstream (the resolver omits it). Unsupported functions
/// (<c>counter()</c> / <c>string()</c> / <c>element()</c>) emit
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

    /// <summary>The result of laying out the page margin boxes: the text fragments (for the shared
    /// <see cref="TextPainter"/> pass) + the background bands (cycle 8, painted first behind the text).</summary>
    internal sealed record MarginBoxLayoutResult(
        IReadOnlyList<BoxFragment> Fragments,
        IReadOnlyList<MarginBoxBackgroundFill> Backgrounds)
    {
        public static readonly MarginBoxLayoutResult Empty =
            new(Array.Empty<BoxFragment>(), Array.Empty<MarginBoxBackgroundFill>());
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
    /// <param name="shaper">The SAME resolver the body shaped with, kept alive past layout.</param>
    /// <param name="diagnostics">Sink for unsupported-content / unresolved-font diagnostics.</param>
    public static MarginBoxLayoutResult Layout(
        ImmutableArray<AtPageMarginBoxResolver.ResolvedMarginBox> boxes,
        double pageWidthPx, double pageHeightPx,
        double marginTopPx, double marginRightPx, double marginBottomPx, double marginLeftPx,
        double contentOriginLeftPx, double contentOriginTopPx,
        IElement host, ComputedStyle? rootElementStyle, ImmutableArray<CssDeclaration> pageDeclarations,
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
        foreach (var mb in boxes)
        {
            if (!CssContentList.TryParse(mb.ContentRawValue, host, out var text))
            {
                EmitContentUnsupported(diagnostics, mb.Name, mb.ContentRawValue);
                continue;
            }

            if (!PageMarginBoxGeometry.TryGetRegion(
                    mb.Name, pageWidthPx, pageHeightPx,
                    marginTopPx, marginRightPx, marginBottomPx, marginLeftPx, out var region))
                continue; // unknown name (resolver filters these out already).

            // Per-box style from the box's declared longhands (font-* / color → shaping + fill).
            // No border/padding is set, so the painter's content-origin math collapses to the
            // fragment offset. The style is box-owned, so the rented instance isn't pooled — a
            // negligible per-render miss, not a leak.
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

            // Alignment within the region: a text-align / vertical-align DECLARED ON THE BOX
            // overrides the box's name-derived default (e.g. top-left → start); otherwise keep the
            // default. Read from the box's own declarations (not the inherited style) so the
            // page/root's UA-default text-align can't override the name-derived alignment (review).
            var hAlign = MarginBoxStyle.HorizontalAlignFactor(mb.Declarations) ?? region.HAlign;
            var vAlign = MarginBoxStyle.VerticalAlignFactor(mb.Declarations) ?? region.VAlign;

            // Absolute page-px placement, then make it relative to the shared text pass's content
            // origin (so the painter's `origin + offset` lands at the absolute position).
            var lineWidthPx = inline.Lines[0].TotalAdvance;
            var absLeftPx = region.X + (region.Width - lineWidthPx) * hAlign;
            var absTopPx = region.Y + (region.Height - lineHeightPx) * vAlign;

            fragments.Add(new BoxFragment(
                Box: box,
                InlineOffset: absLeftPx - contentOriginLeftPx,
                BlockOffset: absTopPx - contentOriginTopPx,
                InlineSize: region.Width,
                BlockSize: lineHeightPx * inline.Lines.Length,
                InlineLayout: inline));
        }

        // The page-context style is only a parent — each box copied the slots it needs — so return
        // it to the pool now instead of leaking it (post-PR-#134 review; the per-box styles stay
        // box-owned with their synthetic Box).
        pageContextStyle.ReleaseFromBox();
        return new MarginBoxLayoutResult(fragments, backgrounds);
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

    private static void EmitContentUnsupported(IDiagnosticsSink diagnostics, string boxName, string raw) =>
        diagnostics.Emit(new Diagnostic(
            DiagnosticCodes.CssContentFunctionUnsupported001,
            // Sanitize the author-supplied value (strip control chars + cap length) before
            // interpolating it into a host-visible message — same hardening as the CSS pipeline's
            // diagnostic path (post-PR-#132 review P2; DiagnosticTextSanitizer).
            $"The page margin box @{boxName} uses a `content` value that is not yet supported " +
            $"(\"{DiagnosticTextSanitizer.Sanitize(raw)}\"). Cycle 3 renders literal strings + attr() " +
            "only; counter()/string()/element() generated content is a tracked follow-up " +
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
