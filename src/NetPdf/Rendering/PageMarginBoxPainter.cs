// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using AngleSharp.Dom;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.PagedMedia;
using NetPdf.Css.Properties;
using NetPdf.Diagnostics;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Inline;
using NetPdf.Layout.Layouters;
using NetPdf.Shaping;
using NetPdf.Text.Bidi;

namespace NetPdf.Rendering;

/// <summary>
/// Lays out CSS Paged Media L3 §6.4 page margin boxes (running headers/footers) into
/// <see cref="BoxFragment"/>s the shared <see cref="TextPainter"/> pass paints. Phase 3 Task 21
/// cycle 3 (content) + cycle 4 (per-box style) — the keystone for headers/footers.
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
/// <b>Style (cycle 4).</b> The box's declared <c>font-family</c> / <c>font-size</c> /
/// <c>font-weight</c> / <c>font-style</c> / <c>color</c> flow through the shaper + painter; a
/// declared <c>text-align</c> / <c>vertical-align</c> overrides the box's name-derived alignment.
/// Unspecified properties fall back to the reader defaults (16px / default family / black /
/// name-derived alignment). Page/root inheritance, the <c>font</c> shorthand, and relative font
/// sizes are tracked follow-ups (deferrals.md#layout-to-pdf-pipeline).
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
    /// <param name="shaper">The SAME resolver the body shaped with, kept alive past layout.</param>
    /// <param name="diagnostics">Sink for unsupported-content / unresolved-font diagnostics.</param>
    public static IReadOnlyList<BoxFragment> Layout(
        ImmutableArray<AtPageMarginBoxResolver.ResolvedMarginBox> boxes,
        double pageWidthPx, double pageHeightPx,
        double marginTopPx, double marginRightPx, double marginBottomPx, double marginLeftPx,
        double contentOriginLeftPx, double contentOriginTopPx,
        IElement host, HarfBuzzShaperResolver shaper, IDiagnosticsSink diagnostics)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(shaper);
        if (boxes.IsDefaultOrEmpty) return Array.Empty<BoxFragment>();

        var availableInlinePx = Math.Max(pageWidthPx, 1.0); // positive + finite; NoWrap keeps one line.
        // Surface invalid margin-box style values (e.g. `color: bogus`) via the CSS diagnostic path
        // (post-PR-#133 review P3) instead of silently defaulting.
        var styleDiagnostics = new PublicDiagnosticsSinkAdapter(diagnostics);

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

            // Per-box style from the box's declared longhands (font-* / color → shaping + fill).
            // No border/padding is set, so the painter's content-origin math collapses to the
            // fragment offset. The style is box-owned, so the rented instance isn't pooled — a
            // negligible per-render miss, not a leak.
            var style = MarginBoxStyle.Build(mb.Declarations, styleDiagnostics);
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

            // Alignment within the region: a DECLARED text-align / vertical-align overrides the
            // box's name-derived default (e.g. top-left → start); otherwise keep the default.
            var hAlign = MarginBoxStyle.HorizontalAlignFactor(style) ?? region.HAlign;
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

        return fragments;
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
