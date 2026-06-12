// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Properties;
using NetPdf.Layout.Layouters;
using NetPdf.Pdf;

namespace NetPdf.Rendering;

/// <summary>
/// The <see cref="BoxFragment"/> → PDF paint bridge. Walks the laid-out fragments
/// for one page and emits each box's background fill + border edges as PDF
/// content-stream rectangles, applying the CSS-px → PDF-pt scale
/// (<see cref="PdfUnits.PointsPerPixel"/>) and the y-axis flip (CSS y-down /
/// page-top origin → PDF y-up / page-bottom origin).
/// </summary>
/// <remarks>
/// <para>
/// <b>Cycle-2/3 scope.</b> Paints <c>background-color</c> fills + the four
/// <c>border-*</c> edges — no text yet (that needs the CSS font-property
/// resolvers). The bridge emits straight to <c>IContentStream</c> operators (via
/// <see cref="PdfPage.FillRectangle"/>); the <c>NetPdf.Paint</c>
/// <c>DisplayCommand</c> IR consumer arrives with the full paint pipeline.
/// </para>
/// <para>
/// <b>Borders.</b> Only <c>solid</c> is rendered faithfully; the other painted
/// styles (<c>dotted</c> / <c>dashed</c> / <c>double</c> / <c>groove</c> /
/// <c>ridge</c> / <c>inset</c> / <c>outset</c>) are approximated as a solid fill of
/// the border color and surfaced via <c>PAINT-BORDER-STYLE-APPROXIMATED-001</c>.
/// <c>none</c> and <c>hidden</c> paint nothing (CSS Backgrounds &amp; Borders 3
/// §4.3 — the used border width is 0 for those styles; layout reserves no space and
/// the painter skips them). Edges span the full box extent on their long axis, so
/// corners overlap — exact for uniform borders; mitered / per-corner joins are a
/// refinement. Border <c>border-radius</c> is deferred.
/// </para>
/// <para>
/// <b>Alpha.</b> A partial-alpha background or border color is composited faithfully
/// via the page's ExtGState constant-alpha (<c>/ca</c>, see
/// <see cref="PdfPage.FillRectangle"/>) — no longer painted fully opaque. A fully
/// transparent fill is skipped. Background images / gradients are deferred. (The old
/// <c>PAINT-*-ALPHA-APPROXIMATED-001</c> diagnostics are no longer emitted.)
/// </para>
/// </remarks>
internal static class FragmentPainter
{
    // border-style keyword ids — the stable zero-based ordering the cascade's
    // KeywordResolver assigns from CSS Backgrounds & Borders 3 §4.3:
    // none, hidden, dotted, dashed, solid, double, groove, ridge, inset, outset.
    private const int BorderStyleNone = 0;
    private const int BorderStyleHidden = 1;
    private const int BorderStyleSolid = 4;

    /// <summary>Fallback when <c>currentcolor</c> can't be resolved — opaque
    /// black, the canvas default text color.</summary>
    private const uint DefaultColorArgb = 0xFF000000;

    private enum BorderEdge { Top, Right, Bottom, Left }

    /// <summary>
    /// Paint every fragment's background + borders onto <paramref name="page"/>.
    /// </summary>
    /// <param name="fragments">The page's fragments in paint order (back to front).</param>
    /// <param name="page">The destination PDF page.</param>
    /// <param name="pageHeightPt">Full page height in PDF points — the y-flip pivot.</param>
    /// <param name="contentOriginLeftPx">Left page margin in CSS px (fragments are
    /// content-area-relative; this offsets them into page space).</param>
    /// <param name="contentOriginTopPx">Top page margin in CSS px.</param>
    /// <param name="paintBackgrounds">Honors <c>HtmlPdfOptions.PrintBackgrounds</c> — when
    /// <see langword="false"/>, no background is painted (borders, being foreground, still are).</param>
    /// <param name="diagnostics">Sink for paint diagnostics (alpha / border-style
    /// approximation); <see langword="null"/> drops them.</param>
    /// <param name="imageCache">The per-render image store (bg-image cycle) — fragments whose
    /// box has a decoded <c>background-image</c> tile it over their border box. Null (every
    /// pre-cycle caller) paints color-only, byte-identical.</param>
    /// <param name="document">The owning document for XObject registration; required (non-null)
    /// only when <paramref name="imageCache"/> is supplied.</param>
    public static void PaintFragments(
        IReadOnlyList<BoxFragment> fragments,
        PdfPage page,
        double pageHeightPt,
        double contentOriginLeftPx,
        double contentOriginTopPx,
        bool paintBackgrounds,
        IDiagnosticsSink? diagnostics,
        // bg-image cycle: the per-render image store + owning document. Null (the default and
        // every pre-cycle caller) paints color-only — byte-identical output.
        ImageResourceCache? imageCache = null,
        PdfDocument? document = null)
    {
        var borderStyleApproximationReported = false;
        var tileCapReported = false;
        var variantUnsupportedReported = false;   // bg-variants cycle — once per render.

        for (var i = 0; i < fragments.Count; i++)
        {
            var fragment = fragments[i];
            var style = fragment.Box.Style;
            if (style is null) continue;

            // Border-box rect in CSS px, page-top-relative (y-down).
            var leftPx = contentOriginLeftPx + fragment.InlineOffset;
            var topPx = contentOriginTopPx + fragment.BlockOffset;
            var widthPx = fragment.InlineSize;
            var heightPx = fragment.BlockSize;
            if (widthPx <= 0 || heightPx <= 0) continue;

            var currentColorArgb = ResolveCurrentColor(style);

            // Background first (behind borders), gated by PrintBackgrounds.
            if (paintBackgrounds)
            {
                PaintBackground(page, style, pageHeightPt, leftPx, topPx, widthPx, heightPx, currentColorArgb);
                // background-image tiles paint OVER this fragment's color and UNDER its borders
                // (CSS B&B §14.2 layer order), gated by PrintBackgrounds like the color.
                if (imageCache is not null && document is not null
                    && imageCache.BackgroundImageBoxes.TryGetValue(fragment.Box, out var bgSpec)
                    && imageCache.TryGet(bgSpec.UriKey, out var bgEntry))
                {
                    PaintBackgroundImageTiles(
                        page, document, bgEntry, pageHeightPt, leftPx, topPx, widthPx, heightPx,
                        diagnostics, ref tileCapReported, ref variantUnsupportedReported,
                        bgSpec.RepeatRaw, bgSpec.SizeRaw, bgSpec.PositionRaw);
                }
            }

            // Borders (foreground — always painted regardless of PrintBackgrounds).
            PaintBorders(page, style, pageHeightPt, leftPx, topPx, widthPx, heightPx,
                currentColorArgb, diagnostics, ref borderStyleApproximationReported);
        }
    }

    /// <summary>Per-fragment <c>background-image</c> tile cap — bounds the content-stream size
    /// against a tiny tile over a large box (a 1×1 px image across an A4 page would otherwise
    /// emit ~500k placements). PDF tiling-pattern objects (O(1) regardless of count) are the
    /// tracked follow-up.</summary>
    private const int MaxBackgroundTilesPerFragment = 4096;

    /// <summary>Tile <paramref name="entry"/> over the fragment's BORDER box (bg-image +
    /// bg-variants cycles): the tile size comes from <paramref name="sizeRaw"/>
    /// (<c>auto</c> = intrinsic px — the initial; <c>contain</c> / <c>cover</c>;
    /// <c>&lt;length|%&gt;{1,2}</c>, % against the area, a missing/auto side aspect-completed),
    /// the grid's phase from <paramref name="positionRaw"/> (keywords / <c>&lt;length|%&gt;</c>
    /// per the CSS B&amp;B §3.6 percentage rule — <c>x%</c> aligns the image's x% point with the
    /// area's; one value → the other axis centers), and each axis repeats per
    /// <paramref name="repeatRaw"/> (<c>repeat</c> — the initial — / <c>no-repeat</c> /
    /// <c>repeat-x</c> / <c>repeat-y</c> / the two-value axis form). An unsupported form
    /// (<c>space</c> / <c>round</c> / 3-4-value positions / non-absolute units) surfaces once
    /// per render and that longhand falls back to its initial. Partial tiles clip at the border
    /// box. Approximation (documented): the positioning area is the BORDER box (the spec's
    /// initial <c>background-origin: padding-box</c> would use the padding box). Null raws =
    /// the initial everywhere (the margin-box caller). INTERNAL so the pipeline reuses the
    /// tiler for page-margin-box background images (margin-box-bg-image cycle).</summary>
    internal static void PaintBackgroundImageTiles(
        PdfPage page, PdfDocument document, ImageResourceCache.Entry entry, double pageHeightPt,
        double leftPx, double topPx, double widthPx, double heightPx,
        IDiagnosticsSink? diagnostics, ref bool tileCapReported, ref bool variantUnsupportedReported,
        string? repeatRaw = null, string? sizeRaw = null, string? positionRaw = null)
    {
        // Each unsupported longhand falls back to ITS initial WHOLE (a failed parse may have
        // half-assigned its outs — e.g. a valid first axis before an invalid second).
        var anyVariantUnsupported = false;
        if (!TryParseBackgroundRepeat(repeatRaw, out var repeatX, out var repeatY))
        {
            anyVariantUnsupported = true;
            repeatX = repeatY = true;                           // the initial: repeat.
        }
        if (!TryParseBackgroundSize(
                sizeRaw, widthPx, heightPx, entry.WidthPx, entry.HeightPx, out var tileW, out var tileH))
        {
            anyVariantUnsupported = true;
            tileW = entry.WidthPx;                              // the initial: auto (intrinsic).
            tileH = entry.HeightPx;
        }
        if (!TryParseBackgroundPosition(
                positionRaw, widthPx, heightPx, tileW, tileH, out var posX, out var posY))
        {
            anyVariantUnsupported = true;
            posX = posY = 0;                                    // the initial: 0% 0%.
        }
        if (anyVariantUnsupported && !variantUnsupportedReported && diagnostics is not null)
        {
            diagnostics.Emit(new Diagnostic(
                DiagnosticCodes.CssBackgroundImageUnsupported001,
                "A background-repeat/-size/-position value outside the supported set "
                + "(repeat/no-repeat/repeat-x/repeat-y incl. the two-value axis form; "
                + "auto/contain/cover/<length|percentage>{1,2}; keyword/<length|percentage>{1,2}) "
                + "was ignored — that longhand fell back to its initial value.",
                DiagnosticSeverity.Warning));
            variantUnsupportedReported = true;
        }
        if (tileW <= 0 || tileH <= 0) return;

        // The grid: a repeating axis covers the whole area at the position's PHASE (the first
        // tile starts ≤ the area edge); a non-repeating axis paints exactly ONE tile at the
        // position (it may protrude — the clip below bounds it).
        double firstX;
        long nx;
        if (repeatX)
        {
            var phase = posX % tileW;
            if (phase > 0) phase -= tileW;
            firstX = phase;
            nx = (long)Math.Ceiling((widthPx - firstX) / tileW);
        }
        else
        {
            firstX = posX;
            nx = 1;
        }
        double firstY;
        long ny;
        if (repeatY)
        {
            var phase = posY % tileH;
            if (phase > 0) phase -= tileH;
            firstY = phase;
            ny = (long)Math.Ceiling((heightPx - firstY) / tileH);
        }
        else
        {
            firstY = posY;
            ny = 1;
        }
        if (nx <= 0 || ny <= 0) return;
        // OVERFLOW-SAFE cap compare (PR #166 review P1): `nx * ny` can wrap for a huge box /
        // tiny tile (a 4e9 × 4e9 count wraps long negative and would bypass the cap into a
        // ~1.6e19-iteration loop). Per-axis bound first, then a division-based product bound;
        // the diagnostic reports the count as a double product (never wraps).
        if (nx > MaxBackgroundTilesPerFragment
            || ny > MaxBackgroundTilesPerFragment
            || nx > MaxBackgroundTilesPerFragment / ny)
        {
            if (!tileCapReported && diagnostics is not null)
            {
                diagnostics.Emit(new Diagnostic(
                    DiagnosticCodes.PaintBgImageTileCap001,
                    $"A background-image tiling needs {(double)nx * ny:0} tiles for one box (tile "
                    + $"{tileW:0.##}×{tileH:0.##}px over {widthPx:0.##}×{heightPx:0.##}px) — over the "
                    + $"{MaxBackgroundTilesPerFragment}-tile cap; the image is skipped for this box "
                    + "(its background-color still paints). PDF tiling patterns are the tracked "
                    + "follow-up (deferrals.md#layout-to-pdf-pipeline).",
                    DiagnosticSeverity.Warning));
                tileCapReported = true;
            }
            return;
        }

        var imageRef = ImageResourceCache.GetOrRegister(document, entry);
        // Clip partial / protruding tiles to the border box (background-clip: border-box, the
        // initial).
        ToPdfRect(leftPx, topPx, widthPx, heightPx, pageHeightPt, out var cx, out var cy, out var cw, out var ch);
        page.BeginRectangleClip(cx, cy, cw, ch);
        for (long row = 0; row < ny; row++)
        {
            for (long col = 0; col < nx; col++)
            {
                ToPdfRect(
                    leftPx + firstX + col * tileW, topPx + firstY + row * tileH, tileW, tileH,
                    pageHeightPt, out var x, out var y, out var w, out var h);
                page.PlaceImage(imageRef, x, y, w, h);
            }
        }
        page.RestoreGraphicsState();
    }

    /// <summary>Parse <c>background-repeat</c> (bg-variants cycle): the four single keywords +
    /// the two-value per-axis form. <c>space</c> / <c>round</c> / unknown → <see langword="false"/>
    /// (the caller falls back to <c>repeat</c> + surfaces once). Null/empty = the initial.</summary>
    internal static bool TryParseBackgroundRepeat(string? raw, out bool repeatX, out bool repeatY)
    {
        repeatX = true;
        repeatY = true;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var parts = raw.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            switch (parts[0])
            {
                case "repeat": return true;
                case "no-repeat": repeatX = repeatY = false; return true;
                case "repeat-x": repeatY = false; return true;
                case "repeat-y": repeatX = false; return true;
                default: return false;
            }
        }
        return parts.Length == 2
            && TryAxisRepeat(parts[0], out repeatX)
            && TryAxisRepeat(parts[1], out repeatY);
    }

    private static bool TryAxisRepeat(string token, out bool repeat)
    {
        repeat = token == "repeat";
        return token is "repeat" or "no-repeat";
    }

    /// <summary>Parse <c>background-size</c> (bg-variants cycle): <c>auto</c> (intrinsic — the
    /// initial), <c>contain</c> / <c>cover</c> (aspect-preserving fit/fill of the area), or
    /// <c>&lt;length|%&gt;{1,2}</c> (% against the area axis; a missing or <c>auto</c> side
    /// completes from the intrinsic ratio). Non-absolute units / junk →
    /// <see langword="false"/> (fall back to auto + surface once).</summary>
    internal static bool TryParseBackgroundSize(
        string? raw, double areaW, double areaH, double intrinsicW, double intrinsicH,
        out double tileW, out double tileH)
    {
        tileW = intrinsicW;
        tileH = intrinsicH;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var v = raw.Trim().ToLowerInvariant();
        if (v is "auto" or "auto auto") return true;
        if (v is "contain" or "cover")
        {
            if (intrinsicW <= 0 || intrinsicH <= 0) return true;
            var sx = areaW / intrinsicW;
            var sy = areaH / intrinsicH;
            var s = v == "contain" ? Math.Min(sx, sy) : Math.Max(sx, sy);
            tileW = intrinsicW * s;
            tileH = intrinsicH * s;
            return true;
        }
        var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2) return false;
        if (!TryParseSizeComponent(parts[0], areaW, out var wAuto, out var w)) return false;
        var hAuto = true;
        var h = 0.0;
        if (parts.Length == 2 && !TryParseSizeComponent(parts[1], areaH, out hAuto, out h)) return false;
        var ratio = intrinsicW > 0 && intrinsicH > 0 ? intrinsicH / intrinsicW : 1.0;
        tileW = wAuto ? (hAuto ? intrinsicW : (ratio > 0 ? h / ratio : intrinsicW)) : w;
        tileH = hAuto ? (wAuto ? intrinsicH : tileW * ratio) : h;
        return true;
    }

    private static bool TryParseSizeComponent(string token, double areaPx, out bool auto, out double px)
    {
        auto = false;
        px = 0;
        if (token == "auto")
        {
            auto = true;
            return true;
        }
        if (token.EndsWith('%')
            && double.TryParse(token[..^1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pct)
            && double.IsFinite(pct))
        {
            px = areaPx * pct / 100.0;
            return true;
        }
        return LengthResolver.TrySplitNumberAndUnit(token, out var n, out var unit)
            && unit.Length > 0
            && LengthResolver.TryAbsoluteUnitToPx(unit.ToLowerInvariant(), n, out px)
            && double.IsFinite(px);
    }

    /// <summary>Parse <c>background-position</c> (bg-variants cycle): per-axis keywords
    /// (<c>left</c>/<c>center</c>/<c>right</c>, <c>top</c>/<c>center</c>/<c>bottom</c> — a
    /// swapped keyword pair like <c>top left</c> is accepted), <c>&lt;length&gt;</c> (absolute),
    /// or <c>&lt;percentage&gt;</c> per the CSS B&amp;B §3.6 rule (<c>x%</c> aligns the image's
    /// x% point with the area's: offset = (area − tile) × x%). ONE value → the other axis
    /// centers. 3-/4-value edge-offset forms / non-absolute units → <see langword="false"/>
    /// (fall back to 0% 0% + surface once).</summary>
    internal static bool TryParseBackgroundPosition(
        string? raw, double areaW, double areaH, double tileW, double tileH,
        out double posX, out double posY)
    {
        posX = 0;
        posY = 0;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var parts = raw.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2) return false;
        string xTok, yTok;
        if (parts.Length == 1)
        {
            xTok = parts[0];
            yTok = "center";
        }
        else
        {
            xTok = parts[0];
            yTok = parts[1];
            // Keyword order leniency: "top left" → axes resolved by keyword, not position.
            if (yTok is "left" or "right" && xTok is "top" or "bottom")
                (xTok, yTok) = (yTok, xTok);
        }
        return TryParsePositionComponent(xTok, areaW, tileW, isX: true, out posX)
            && TryParsePositionComponent(yTok, areaH, tileH, isX: false, out posY);
    }

    private static bool TryParsePositionComponent(
        string token, double areaPx, double tilePx, bool isX, out double pos)
    {
        pos = 0;
        var keywordPct = token switch
        {
            "left" when isX => 0.0,
            "right" when isX => 100.0,
            "top" when !isX => 0.0,
            "bottom" when !isX => 100.0,
            "center" => 50.0,
            _ => double.NaN,
        };
        if (!double.IsNaN(keywordPct))
        {
            pos = (areaPx - tilePx) * keywordPct / 100.0;
            return true;
        }
        if (token.EndsWith('%')
            && double.TryParse(token[..^1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pct)
            && double.IsFinite(pct))
        {
            pos = (areaPx - tilePx) * pct / 100.0;   // the §3.6 percentage rule.
            return true;
        }
        if (LengthResolver.TrySplitNumberAndUnit(token, out var n, out var unit))
        {
            if (unit.Length == 0 && n == 0.0) return true;   // the unitless zero.
            if (unit.Length > 0
                && LengthResolver.TryAbsoluteUnitToPx(unit.ToLowerInvariant(), n, out pos)
                && double.IsFinite(pos))
            {
                return true;
            }
        }
        pos = 0;
        return false;
    }

    /// <summary>The <c>currentcolor</c> fallback for each border edge — the colour of the style that
    /// OWNS that edge (CSS Color 4 §6.2). The body path uses one colour for all four (a body box's
    /// border and its <c>color</c> belong to the same element — see <see cref="BorderEdgeCurrentColors.Uniform(uint)"/>); the
    /// page-margin-box painter resolves ownership PER EDGE (per-edge currentcolor cycle — a
    /// box-declared edge falls back to the box's colour, an element-declared edge to the running
    /// element's).</summary>
    internal readonly record struct BorderEdgeCurrentColors(uint Top, uint Right, uint Bottom, uint Left)
    {
        /// <summary>All four edges fall back to the same colour (the body path / a single owner).</summary>
        public static BorderEdgeCurrentColors Uniform(uint argb) => new(argb, argb, argb, argb);
    }

    /// <summary>Paint all four border edges of a box (top / right / bottom / left) declared on
    /// <paramref name="style"/>, around the box rect (<paramref name="leftPx"/> /
    /// <paramref name="topPx"/> / <paramref name="widthPx"/> / <paramref name="heightPx"/>, CSS px,
    /// page-top origin). Reused by the page-margin-box painter. <paramref name="styleApproximationReported"/>
    /// is threaded so a non-solid-border-style approximation is diagnosed at most once per render.
    /// This overload uses ONE currentcolor for all four edges (the body path).</summary>
    internal static void PaintBorders(
        PdfPage page, ComputedStyle style, double pageHeightPt,
        double leftPx, double topPx, double widthPx, double heightPx,
        uint currentColorArgb, IDiagnosticsSink? diagnostics, ref bool styleApproximationReported) =>
        PaintBorders(page, style, pageHeightPt, leftPx, topPx, widthPx, heightPx,
            BorderEdgeCurrentColors.Uniform(currentColorArgb), diagnostics, ref styleApproximationReported);

    /// <summary>Per-edge-currentcolor overload (per-edge currentcolor cycle): each edge's
    /// <c>border-*-color: currentcolor</c> (or its initial) falls back to that edge's OWNER colour
    /// from <paramref name="currentColors"/>. The uniform overload delegates here, so the body path
    /// is byte-identical.</summary>
    internal static void PaintBorders(
        PdfPage page, ComputedStyle style, double pageHeightPt,
        double leftPx, double topPx, double widthPx, double heightPx,
        in BorderEdgeCurrentColors currentColors, IDiagnosticsSink? diagnostics, ref bool styleApproximationReported)
    {
        // A zero-area or non-finite border box has no edges to stroke. The body path guards this
        // upstream (PaintFragments skips width/height <= 0), but the page-margin-box painter forwards
        // region rects directly, and PageMarginBoxGeometry can yield a zero-width/height band (e.g.
        // `@page { margin-top: 0 }` → a zero-height top edge, or margins exceeding the page). Without
        // this, the top/bottom edges would still paint a full-width rectangle around a zero-height box.
        if (!(widthPx > 0 && heightPx > 0)
            || !double.IsFinite(leftPx) || !double.IsFinite(topPx)
            || !double.IsFinite(widthPx) || !double.IsFinite(heightPx))
            return;

        PaintBorderEdge(page, style, pageHeightPt, BorderEdge.Top, leftPx, topPx, widthPx, heightPx,
            currentColors.Top, diagnostics, ref styleApproximationReported);
        PaintBorderEdge(page, style, pageHeightPt, BorderEdge.Right, leftPx, topPx, widthPx, heightPx,
            currentColors.Right, diagnostics, ref styleApproximationReported);
        PaintBorderEdge(page, style, pageHeightPt, BorderEdge.Bottom, leftPx, topPx, widthPx, heightPx,
            currentColors.Bottom, diagnostics, ref styleApproximationReported);
        PaintBorderEdge(page, style, pageHeightPt, BorderEdge.Left, leftPx, topPx, widthPx, heightPx,
            currentColors.Left, diagnostics, ref styleApproximationReported);
    }

    private static void PaintBackground(
        PdfPage page, ComputedStyle style, double pageHeightPt,
        double leftPx, double topPx, double widthPx, double heightPx,
        uint currentColorArgb)
    {
        if (!TryResolveColor(style.Get(PropertyId.BackgroundColor), currentColorArgb, out var argb))
            return;
        var alpha = Alpha(argb);
        if (alpha == 0) return; // transparent (the initial value) paints nothing.

        ColorChannels(argb, out var r, out var g, out var b);
        ToPdfRect(leftPx, topPx, widthPx, heightPx, pageHeightPt, out var x, out var y, out var w, out var h);
        // A partial alpha (0 < alpha < 255) is composited faithfully via the page's ExtGState
        // constant-alpha (/ca) — no longer painted fully opaque.
        page.FillRectangle(x, y, w, h, r, g, b, alpha / 255.0);
    }

    private static void PaintBorderEdge(
        PdfPage page, ComputedStyle style, double pageHeightPt, BorderEdge edge,
        double boxLeftPx, double boxTopPx, double boxWidthPx, double boxHeightPx,
        uint currentColorArgb, IDiagnosticsSink? diagnostics,
        ref bool styleApproximationReported)
    {
        (PropertyId styleId, PropertyId widthId, PropertyId colorId) = edge switch
        {
            BorderEdge.Top => (PropertyId.BorderTopStyle, PropertyId.BorderTopWidth, PropertyId.BorderTopColor),
            BorderEdge.Right => (PropertyId.BorderRightStyle, PropertyId.BorderRightWidth, PropertyId.BorderRightColor),
            BorderEdge.Bottom => (PropertyId.BorderBottomStyle, PropertyId.BorderBottomWidth, PropertyId.BorderBottomColor),
            _ => (PropertyId.BorderLeftStyle, PropertyId.BorderLeftWidth, PropertyId.BorderLeftColor),
        };

        var styleSlot = style.Get(styleId);
        if (styleSlot.Tag != ComputedSlotTag.Keyword) return; // unset → initial `none`.
        var styleKeyword = styleSlot.AsKeyword();
        if (styleKeyword is BorderStyleNone or BorderStyleHidden) return;

        var widthSlot = style.Get(widthId);
        var edgeWidthPx = widthSlot.Tag == ComputedSlotTag.LengthPx ? widthSlot.AsLengthPx() : 0;
        if (edgeWidthPx <= 0) return;

        if (!TryResolveColor(style.Get(colorId), currentColorArgb, out var argb))
            argb = currentColorArgb; // border-color initial is currentcolor.
        var alpha = Alpha(argb);
        if (alpha == 0) return;

        if (styleKeyword != BorderStyleSolid && !styleApproximationReported)
        {
            diagnostics?.Emit(new Diagnostic(
                DiagnosticCodes.PaintBorderStyleApproximated001,
                "A non-solid border-style (dotted / dashed / double / groove / ridge / inset / outset) " +
                "was painted as a solid line. Styled border rendering is a tracked follow-up " +
                "(deferrals.md#layout-to-pdf-pipeline).",
                DiagnosticSeverity.Info));
            styleApproximationReported = true;
        }

        // Edge sub-rect within the border box (CSS px, page-top-relative). Edges span
        // the full box extent on their long axis; corners overlap, which is exact for
        // uniform borders (mitered / per-corner joins are a refinement).
        double edgeLeftPx, edgeTopPx, edgeBoxWidthPx, edgeBoxHeightPx;
        switch (edge)
        {
            case BorderEdge.Top:
                edgeLeftPx = boxLeftPx; edgeTopPx = boxTopPx;
                edgeBoxWidthPx = boxWidthPx; edgeBoxHeightPx = edgeWidthPx;
                break;
            case BorderEdge.Bottom:
                edgeLeftPx = boxLeftPx; edgeTopPx = boxTopPx + boxHeightPx - edgeWidthPx;
                edgeBoxWidthPx = boxWidthPx; edgeBoxHeightPx = edgeWidthPx;
                break;
            case BorderEdge.Left:
                edgeLeftPx = boxLeftPx; edgeTopPx = boxTopPx;
                edgeBoxWidthPx = edgeWidthPx; edgeBoxHeightPx = boxHeightPx;
                break;
            default: // Right
                edgeLeftPx = boxLeftPx + boxWidthPx - edgeWidthPx; edgeTopPx = boxTopPx;
                edgeBoxWidthPx = edgeWidthPx; edgeBoxHeightPx = boxHeightPx;
                break;
        }

        ColorChannels(argb, out var r, out var g, out var b);
        ToPdfRect(edgeLeftPx, edgeTopPx, edgeBoxWidthPx, edgeBoxHeightPx, pageHeightPt,
            out var x, out var y, out var w, out var h);
        // A partial border-color alpha is composited via the page's ExtGState constant-alpha (/ca).
        page.FillRectangle(x, y, w, h, r, g, b, alpha / 255.0);
    }

    /// <summary>
    /// Map a CSS-px rectangle expressed as (left, top, width, height) with a
    /// page-top origin and y growing downward to a PDF-point rectangle with a
    /// page-bottom origin and y growing upward (the <c>re</c> operator's
    /// lower-left-corner convention). Pure — unit-tested directly.
    /// </summary>
    internal static void ToPdfRect(
        double leftPx, double topPx, double widthPx, double heightPx, double pageHeightPt,
        out double xPt, out double yPt, out double wPt, out double hPt)
    {
        xPt = PdfUnits.PxToPt(leftPx);
        wPt = PdfUnits.PxToPt(widthPx);
        hPt = PdfUnits.PxToPt(heightPx);
        // The rect's lower edge sits (pageHeight - top - height) below the page top
        // in CSS px; convert that distance to points to get the PDF y origin.
        yPt = pageHeightPt - PdfUnits.PxToPt(topPx) - hPt;
    }

    /// <summary>
    /// Resolve a color-valued slot to a packed 0xAARRGGBB value, substituting
    /// <paramref name="currentColorArgb"/> for the <c>currentcolor</c> sentinel.
    /// Returns <see langword="false"/> when the slot carries no color (unset / a
    /// non-color value) so the caller can skip painting. Pure — unit-tested.
    /// </summary>
    internal static bool TryResolveColor(ComputedSlot slot, uint currentColorArgb, out uint argb)
    {
        if (slot.Tag == ComputedSlotTag.Color)
        {
            argb = slot.AsColor();
            return true;
        }
        if (slot.IsCurrentColor)
        {
            argb = currentColorArgb;
            return true;
        }
        argb = 0;
        return false;
    }

    /// <summary>Split a packed 0xAARRGGBB color into PDF [0, 1] RGB channels.</summary>
    internal static void ColorChannels(uint argb, out double r, out double g, out double b)
    {
        r = ((argb >> 16) & 0xFF) / 255.0;
        g = ((argb >> 8) & 0xFF) / 255.0;
        b = (argb & 0xFF) / 255.0;
    }

    /// <summary>The alpha channel (0–255) of a packed 0xAARRGGBB color.</summary>
    internal static int Alpha(uint argb) => (int)((argb >> 24) & 0xFF);

    private static uint ResolveCurrentColor(ComputedStyle style)
    {
        var slot = style.Get(PropertyId.Color);
        return slot.Tag == ComputedSlotTag.Color ? slot.AsColor() : DefaultColorArgb;
    }
}
