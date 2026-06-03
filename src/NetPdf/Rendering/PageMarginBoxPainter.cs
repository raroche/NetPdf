// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using AngleSharp.Dom;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.PagedMedia;
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
/// Lays out + paints CSS Paged Media L3 §6.4 page margin boxes (running headers/footers). Phase 3
/// Task 21 cycle 3 — the keystone for headers/footers.
/// </summary>
/// <remarks>
/// <para>
/// Takes the margin boxes <see cref="AtPageMarginBoxResolver"/> resolved (name + raw <c>content</c>),
/// resolves each <c>content</c> to text (literal strings + <c>attr()</c> via
/// <see cref="CssContentList"/>), computes its page-box region via
/// <see cref="PageMarginBoxGeometry"/>, lays the text out as one line with
/// <see cref="InlineLayouter"/>, and paints it through the same <see cref="TextPainter"/>
/// machinery the body text uses — so font subsetting/embedding is shared. Margin-box fragments are
/// positioned in ABSOLUTE page px, so the painter is invoked with a (0,0) content origin (the body
/// pass uses the page margins as its origin).
/// </para>
/// <para>
/// <b>Cycle 3 scope.</b> Literal-string + <c>attr()</c> content only; <c>counter()</c> /
/// <c>string()</c> / <c>element()</c> emit <c>CSS-CONTENT-FUNCTION-UNSUPPORTED-001</c> and the box
/// is skipped. Default typography (a fresh initial <see cref="ComputedStyle"/> — 16px, the
/// resolver's default family, black); per-box font/color/alignment declarations + the §5.3
/// three-box-per-edge sizing are later cycles (deferrals.md#layout-to-pdf-pipeline).
/// </para>
/// </remarks>
internal static class PageMarginBoxPainter
{
    private const double NormalLineHeightFactor = 1.2; // mirrors TextPainter's line-height: normal.

    /// <summary>Paint every resolved margin box's content onto <paramref name="page"/>. A no-op
    /// when <paramref name="boxes"/> is empty.</summary>
    /// <param name="boxes">Margin boxes resolved by <see cref="AtPageMarginBoxResolver"/>.</param>
    /// <param name="pageWidthPx">Resolved page width (CSS px).</param>
    /// <param name="pageHeightPx">Resolved page height (CSS px).</param>
    /// <param name="marginTopPx">Resolved top page margin (CSS px) — the band the top boxes live in.</param>
    /// <param name="marginRightPx">Resolved right page margin (CSS px).</param>
    /// <param name="marginBottomPx">Resolved bottom page margin (CSS px).</param>
    /// <param name="marginLeftPx">Resolved left page margin (CSS px).</param>
    /// <param name="host">A document element for <c>attr()</c> resolution (the box tree's root
    /// element). <c>attr()</c> against page furniture has no real element, so this is an
    /// approximation; literal content ignores it.</param>
    /// <param name="page">Destination page (painted over the body text).</param>
    /// <param name="document">Owns embedded-font objects.</param>
    /// <param name="shaper">The SAME resolver the body shaped with, kept alive past layout.</param>
    /// <param name="pageHeightPt">Full page height in PDF points — the y-flip pivot.</param>
    /// <param name="diagnostics">Sink for unsupported-content / unresolved-font diagnostics.</param>
    public static void Paint(
        ImmutableArray<AtPageMarginBoxResolver.ResolvedMarginBox> boxes,
        double pageWidthPx, double pageHeightPx,
        double marginTopPx, double marginRightPx, double marginBottomPx, double marginLeftPx,
        IElement host,
        PdfPage page, PdfDocument document, HarfBuzzShaperResolver shaper,
        double pageHeightPt, IDiagnosticsSink diagnostics)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(shaper);
        if (boxes.IsDefaultOrEmpty) return;

        // One default-typography style + Box, shared across boxes (cycle 3 doesn't read per-box
        // style). No border/padding set → the painter's content-origin math collapses to the
        // fragment offset. ComputedStyle.Rent() is box-owned once wrapped (Dispose is a no-op),
        // so it isn't returned to the pool — a negligible per-render miss, not a leak.
        var style = ComputedStyle.Rent();
        var box = Box.TextRun(string.Empty, style);
        var lineHeightPx = style.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16) * NormalLineHeightFactor;
        var availableInlinePx = Math.Max(pageWidthPx, 1.0); // positive + finite; NoWrap keeps one line.

        var fragments = new List<BoxFragment>(boxes.Length);
        foreach (var mb in boxes)
        {
            if (!CssContentList.TryParse(mb.ContentRawValue, host, out var text))
            {
                EmitContentUnsupported(diagnostics, mb.Name, mb.ContentRawValue);
                continue;
            }
            if (string.IsNullOrEmpty(text)) continue; // e.g. content: "" — nothing to paint.

            if (!PageMarginBoxGeometry.TryGetRegion(
                    mb.Name, pageWidthPx, pageHeightPx,
                    marginTopPx, marginRightPx, marginBottomPx, marginLeftPx, out var region))
                continue; // unknown name (resolver filters these out already).

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

            var lineWidthPx = inline.Lines[0].TotalAdvance;
            var inlineOffset = region.X + (region.Width - lineWidthPx) * region.HAlign;
            var blockOffset = region.Y + (region.Height - lineHeightPx) * region.VAlign;

            fragments.Add(new BoxFragment(
                Box: box,
                InlineOffset: inlineOffset,
                BlockOffset: blockOffset,
                InlineSize: region.Width,
                BlockSize: lineHeightPx * inline.Lines.Length,
                InlineLayout: inline));
        }

        if (fragments.Count == 0) return;

        // Absolute page-px coordinates → paint with a (0,0) content origin (the painter adds the
        // origin + does the y-flip). Same shaper instance that laid the text out.
        TextPainter.PaintText(
            fragments, page, document, shaper, pageHeightPt,
            contentOriginLeftPx: 0, contentOriginTopPx: 0, diagnostics);
    }

    private static void EmitContentUnsupported(IDiagnosticsSink diagnostics, string boxName, string raw) =>
        diagnostics.Emit(new Diagnostic(
            DiagnosticCodes.CssContentFunctionUnsupported001,
            $"The page margin box @{boxName} uses a `content` value that is not yet supported " +
            $"(\"{raw}\"). Cycle 3 renders literal strings + attr() only; counter()/string()/" +
            "element() generated content is a tracked follow-up (deferrals.md#layout-to-pdf-pipeline). " +
            "The box was not painted.",
            DiagnosticSeverity.Warning));

    private static void EmitFontUnresolved(IDiagnosticsSink diagnostics, string boxName, string detail) =>
        diagnostics.Emit(new Diagnostic(
            DiagnosticCodes.PaintTextFontUnresolved001,
            $"The page margin box @{boxName} was not painted because its font could not be " +
            "resolved during layout: " + detail + " A bundled deterministic last-resort font (so " +
            "the default path always resolves) is a tracked follow-up (deferrals.md#layout-to-pdf-pipeline).",
            DiagnosticSeverity.Warning));
}
