// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Layouters;
using NetPdf.Pdf;

namespace NetPdf.Rendering;

/// <summary>
/// Paint replaced-element (<c>&lt;img&gt;</c>) content (img-pipeline + object-fit cycles): each
/// fragment whose box is a successfully decoded image places its XObject within the fragment's
/// CONTENT box (the border box inset by the used border + padding — the same slots the
/// band/borders painted), fitted per the element's <c>object-fit</c> (CSS Images 3 §5.5):
/// <c>fill</c> (the initial — stretch to the content box, the pre-cycle behavior, byte-identical),
/// <c>contain</c> / <c>cover</c> (aspect-preserving fit / fill of the box), <c>none</c> (the
/// intrinsic size), <c>scale-down</c> (the smaller of <c>none</c> and <c>contain</c>) — all
/// centred (the <c>object-position</c> initial 50% 50%; the property itself stays deferred), an
/// overflowing concrete size (<c>cover</c> / <c>none</c>) clipped at the content box. Runs AFTER
/// <see cref="FragmentPainter.PaintFragments"/> (all bands + borders) and BEFORE the text pass —
/// replaced content paints over every band, under the glyphs (a documented paint-order
/// approximation, like text-last; fine for non-overlapping flows).
/// </summary>
internal static class ImagePainter
{
    public static void PaintImages(
        IReadOnlyList<BoxFragment> fragments,
        PdfPage page,
        PdfDocument document,
        ImageResourceCache cache,
        double pageHeightPt,
        double contentOriginLeftPx,
        double contentOriginTopPx,
        IDiagnosticsSink? diagnostics = null)
    {
        if (cache.ImageBoxes.Count == 0) return;
        var unknownFitReported = false;
        for (var i = 0; i < fragments.Count; i++)
        {
            var fragment = fragments[i];
            if (!cache.ImageBoxes.TryGetValue(fragment.Box, out var spec)
                || !cache.TryGet(spec.UriKey, out var entry))
            {
                continue;
            }
            var style = fragment.Box.Style;

            // Content box = border box minus used border + padding (the slots are used px —
            // the in-flow pass rewrote % padding in place before emitting the fragment).
            var insetL = style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth)
                + style.ReadLengthPxOrZero(PropertyId.PaddingLeft);
            var insetT = style.ReadLengthPxOrZero(PropertyId.BorderTopWidth)
                + style.ReadLengthPxOrZero(PropertyId.PaddingTop);
            var insetR = style.ReadLengthPxOrZero(PropertyId.BorderRightWidth)
                + style.ReadLengthPxOrZero(PropertyId.PaddingRight);
            var insetB = style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth)
                + style.ReadLengthPxOrZero(PropertyId.PaddingBottom);

            var leftPx = contentOriginLeftPx + fragment.InlineOffset + insetL;
            var topPx = contentOriginTopPx + fragment.BlockOffset + insetT;
            var widthPx = fragment.InlineSize - insetL - insetR;
            var heightPx = fragment.BlockSize - insetT - insetB;
            if (widthPx <= 0 || heightPx <= 0) continue;

            // The CONCRETE OBJECT SIZE per object-fit (§5.5), centred in the content box.
            var (objWPx, objHPx) = ConcreteObjectSize(
                spec.ObjectFitRaw, widthPx, heightPx, entry.WidthPx, entry.HeightPx,
                diagnostics, ref unknownFitReported);
            if (objWPx <= 0 || objHPx <= 0) continue;
            var objLeftPx = leftPx + (widthPx - objWPx) / 2.0;
            var objTopPx = topPx + (heightPx - objHPx) / 2.0;

            var imageRef = ImageResourceCache.GetOrRegister(document, entry);
            // cover / none can overflow the content box — clip there (CSS Images 3 §5.5: the
            // content's painted area is the content box).
            const double overflowEps = 0.01;
            var clips = objWPx > widthPx + overflowEps || objHPx > heightPx + overflowEps;
            if (clips)
            {
                FragmentPainter.ToPdfRect(
                    leftPx, topPx, widthPx, heightPx, pageHeightPt,
                    out var cx, out var cy, out var cw, out var ch);
                page.BeginRectangleClip(cx, cy, cw, ch);
            }
            FragmentPainter.ToPdfRect(
                objLeftPx, objTopPx, objWPx, objHPx, pageHeightPt,
                out var x, out var y, out var w, out var h);
            page.PlaceImage(imageRef, x, y, w, h);
            if (clips) page.RestoreGraphicsState();
        }
    }

    /// <summary>The §5.5 concrete object size for <paramref name="rawFit"/> in a
    /// <paramref name="boxW"/> × <paramref name="boxH"/> content box with the
    /// <paramref name="intrinsicW"/> × <paramref name="intrinsicH"/> px natural size. An unknown
    /// value surfaces once per render and falls back to the initial <c>fill</c>.</summary>
    private static (double W, double H) ConcreteObjectSize(
        string? rawFit, double boxW, double boxH, double intrinsicW, double intrinsicH,
        IDiagnosticsSink? diagnostics, ref bool unknownFitReported)
    {
        var fit = rawFit?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(fit) || fit == "fill") return (boxW, boxH);
        if (intrinsicW <= 0 || intrinsicH <= 0) return (boxW, boxH);
        switch (fit)
        {
            case "contain":
            case "cover":
            {
                var sx = boxW / intrinsicW;
                var sy = boxH / intrinsicH;
                var s = fit == "contain" ? Math.Min(sx, sy) : Math.Max(sx, sy);
                return (intrinsicW * s, intrinsicH * s);
            }
            case "none":
                return (intrinsicW, intrinsicH);
            case "scale-down":
            {
                // The smaller of `none` and `contain` (§5.5) — never upscale.
                var s = Math.Min(1.0, Math.Min(boxW / intrinsicW, boxH / intrinsicH));
                return (intrinsicW * s, intrinsicH * s);
            }
            default:
                if (!unknownFitReported && diagnostics is not null)
                {
                    diagnostics.Emit(new Diagnostic(
                        DiagnosticCodes.CssPropertyValueInvalid001,
                        $"object-fit value '{fit}' is not recognized (fill / contain / cover / "
                        + "none / scale-down are supported); the initial `fill` is used.",
                        DiagnosticSeverity.Warning));
                    unknownFitReported = true;
                }
                return (boxW, boxH);
        }
    }
}
