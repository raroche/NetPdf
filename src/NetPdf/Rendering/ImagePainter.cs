// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
using NetPdf.Pdf;
using NetPdf.Pdf.Objects;

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
        IDiagnosticsSink? diagnostics = null,
        // Phase 4 transforms (review [P1]): box → effective cm. A transformed <img> wraps its
        // image XObject placement in the matrix (not just decoration). Null = nothing transformed.
        IReadOnlyDictionary<Box, (double A, double B, double C, double D, double E, double F)>? effectiveTransforms = null,
        // Phase 4 native SVG (opt-in): draw a supported SVG <img> as crisp native PDF path ops instead of the
        // rasterized XObject. Falls back to raster for anything outside the emitter's supported subset.
        bool nativeSvg = false)
    {
        if (cache.ImageBoxes.Count == 0) return;
        var unknownPositionReported = false;   // object-position cycle — once per render.
        var filterRasterReported = false;      // Phase 4 filters — once-per-render raster Info.
        var maskRasterReported = false;        // Phase 4 mask — once-per-render raster Info.
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

            // The CONCRETE OBJECT SIZE per object-fit (§5.5), positioned per object-position
            // (object-position cycle, CSS Images 3 §5.6 — the SAME §3.6 component grammar as
            // background-position: keywords / lengths / percentages, one value → the other
            // axis centers; the initial is 50% 50%, so an unset raw centres — the pre-cycle
            // behavior, byte-identical). An unsupported form (edge-offsets, relative units)
            // surfaces once + falls back to the centre.
            var (objWPx, objHPx) = ConcreteObjectSize(
                spec.ObjectFitKeyword, widthPx, heightPx, entry.WidthPx, entry.HeightPx);
            if (objWPx <= 0 || objHPx <= 0) continue;
            double objLeftPx;
            double objTopPx;
            if (spec.ObjectPositionRaw is { } posRaw)
            {
                if (!FragmentPainter.TryParseBackgroundPosition(
                        posRaw, widthPx, heightPx, objWPx, objHPx, out var posX, out var posY))
                {
                    if (!unknownPositionReported && diagnostics is not null)
                    {
                        diagnostics.Emit(new Diagnostic(
                            DiagnosticCodes.CssPropertyValueInvalid001,
                            BuildInvalidObjectPositionDiagnostic(posRaw),
                            DiagnosticSeverity.Warning));
                        unknownPositionReported = true;
                    }
                    posX = (widthPx - objWPx) / 2.0;
                    posY = (heightPx - objHPx) / 2.0;
                }
                objLeftPx = leftPx + posX;
                objTopPx = topPx + posY;
            }
            else
            {
                objLeftPx = leftPx + (widthPx - objWPx) / 2.0;
                objTopPx = topPx + (heightPx - objHPx) / 2.0;
            }

            // Native vector SVG is eligible only for an SVG source with NO CSS filter/mask on the <img> (the
            // emitter draws geometry, not those effects). When eligible, defer resolving (registering) the
            // raster XObject — if native emission succeeds the raster is never built into the PDF.
            var goNative = nativeSvg && entry.SvgSourceBytes is not null
                && spec.Filter is null && spec.MaskUriKey is null;
            PdfIndirectRef? imageRef = null;
            NetPdf.Pdf.Images.FilterPadding filterPad = default;
            if (!goNative)
                (imageRef, filterPad) = ResolveImageRef(document, style, spec, entry, cache, diagnostics,
                    ref filterRasterReported, ref maskRasterReported);
            // A drop-shadow filter pads the raster outward (the shadow extends past the image box);
            // the image CONTENT still maps to the object box, so expand the placement by the padding
            // fractions of the content size. Zero for colour / blur filters (byte-identical).
            var placeLeftPx = objLeftPx - objWPx * filterPad.LeftFrac;
            var placeTopPx = objTopPx - objHPx * filterPad.TopFrac;
            var placeWPx = objWPx * (1 + filterPad.LeftFrac + filterPad.RightFrac);
            var placeHPx = objHPx * (1 + filterPad.TopFrac + filterPad.BottomFrac);
            // transform (Phase 4, review [P1]) — a transformed <img> wraps its XObject placement
            // (and the overflow clip) in the box's effective cm, so the image moves with the box.
            // opacity (Phase 4) — fade the whole <img> (the box decoration faded in FragmentPainter with the
            // same alpha). Outermost, so the image + its clip composite at the box opacity. 1.0 → no wrap.
            var faded = cache.OpacityBoxes.TryGetValue(fragment.Box, out var imgOpacity) && imgOpacity < 1.0;
            if (faded) page.BeginConstantAlpha(imgOpacity);
            (double A, double B, double C, double D, double E, double F) effCm = default;
            var transformed = effectiveTransforms is not null
                && effectiveTransforms.TryGetValue(fragment.Box, out effCm);
            if (transformed) page.BeginTransform(effCm.A, effCm.B, effCm.C, effCm.D, effCm.E, effCm.F);
            // clip-path (Phase 4 PR 3) — clip the IMAGE to the box's basic shape too (FragmentPainter
            // already clipped the box decoration + emitted any diagnostics, so suppress them here).
            // Reconstruct the BORDER box (the clip reference) from the content box + insets.
            var clipPathClipped = false;
            if (cache.ClipPathBoxes.TryGetValue(fragment.Box, out var clipShape))
            {
                var suppressed = true;
                clipPathClipped = FragmentPainter.BeginClipPath(
                    page, clipShape, pageHeightPt, leftPx - insetL, topPx - insetT,
                    widthPx + insetL + insetR, heightPx + insetT + insetB, fragment.Box,
                    diagnostics: null, ref suppressed, ref suppressed);
            }
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
                placeLeftPx, placeTopPx, placeWPx, placeHPx, pageHeightPt,
                out var x, out var y, out var w, out var h);
            // Native vector SVG (opt-in) — draw the SVG as crisp native path ops into the SAME rect the raster
            // XObject would occupy (inside the transform/clip/opacity wraps). Only when the whole document is
            // supported; otherwise TryEmit leaves the page untouched and we place the rasterized XObject.
            var emittedNative = goNative
                && NetPdf.Svg.SvgNativeEmitter.TryEmit(entry.SvgSourceBytes!, page, x, y, w, h, out _);
            if (!emittedNative)
            {
                if (goNative) // native bailed → resolve the raster XObject now (no filter → padding irrelevant)
                    (imageRef, _) = ResolveImageRef(document, style, spec, entry, cache, diagnostics,
                        ref filterRasterReported, ref maskRasterReported);
                page.PlaceImage(imageRef!, x, y, w, h);
            }
            if (clips) page.RestoreGraphicsState();
            if (clipPathClipped) page.RestoreGraphicsState(); // balance BeginClipPath's q
            if (transformed) page.RestoreGraphicsState();
            if (faded) page.RestoreGraphicsState(); // balance BeginConstantAlpha's q
        }
    }

    /// <summary>Phase 4 filters (PR 2) — the image XObject ref to place: a <c>filter</c>-bearing
    /// <c>&lt;img&gt;</c> gets a Skia-filtered variant (deduped, with <c>CSS-FILTER-RASTER-FALLBACK-001</c>
    /// once per render); a filter NetPdf can't build in this cut, or an undecodable / over-cap image,
    /// falls back to the unfiltered XObject so the picture still shows.</summary>
    private static (PdfIndirectRef Ref, NetPdf.Pdf.Images.FilterPadding Pad) ResolveImageRef(
        PdfDocument document, ComputedStyle style, ImageResourceCache.ImgSpec spec,
        ImageResourceCache.Entry entry, ImageResourceCache cache, IDiagnosticsSink? diagnostics,
        ref bool rasterReported, ref bool maskRasterReported)
    {
        // mask / mask-image (Phase 4 PR 4) — a masked <img> gets a Skia alpha-masked variant (deduped by
        // mask URI). Applied to the UNFILTERED source this cut (filter + mask combined is a follow-up); the
        // masked image still shows if the mask decodes, else falls through to the filter / plain path.
        if (spec.MaskUriKey is { } maskKey && cache.TryGet(maskKey, out var maskEntry)
            && ImageResourceCache.GetOrRegisterMasked(document, entry, maskEntry, maskKey) is { } masked)
        {
            if (!maskRasterReported)
            {
                diagnostics?.Emit(new Diagnostic(
                    DiagnosticCodes.CssMaskRasterFallback001,
                    "A CSS mask on an image was applied via the Skia raster fallback (PDF has no native "
                    + "mask primitive); the image's alpha was multiplied by the mask and re-embedded as a "
                    + "raster XObject.",
                    DiagnosticSeverity.Info));
                maskRasterReported = true;
            }
            return (masked, NetPdf.Pdf.Images.FilterPadding.None);
        }

        if (spec.Filter is { } filter
            && ImageFilterBridge.TryBuildSteps(filter, FragmentPainter.ResolveCurrentColor(style), out var steps))
        {
            var key = ImageFilterBridge.FilterKey(steps); // keyed on the RESOLVED steps (incl. shadow RGBA)
            var filtered = ImageResourceCache.GetOrRegisterFiltered(document, entry, steps, key);
            if (filtered is { } f)
            {
                if (!rasterReported)
                {
                    diagnostics?.Emit(new Diagnostic(
                        DiagnosticCodes.CssFilterRasterFallback001,
                        "A CSS filter on an image was applied via the Skia raster fallback (PDF has no "
                        + "native filter primitive); the filtered image was re-embedded as a raster XObject.",
                        DiagnosticSeverity.Info));
                    rasterReported = true;
                }
                return (f.Ref, f.Pad);
            }
        }
        return (ImageResourceCache.GetOrRegister(document, entry), NetPdf.Pdf.Images.FilterPadding.None);
    }

    /// <summary>The §5.5 concrete object size for the computed <c>object-fit</c> keyword
    /// (the KeywordResolver table order: 0 fill, 1 contain, 2 cover, 3 none, 4 scale-down —
    /// object-fit is a registered property, so the cascade already validated the value; an
    /// invalid declaration was diagnosed there and computed to the initial) in a
    /// <paramref name="boxW"/> × <paramref name="boxH"/> content box with the
    /// <paramref name="intrinsicW"/> × <paramref name="intrinsicH"/> px natural size.</summary>
    private static (double W, double H) ConcreteObjectSize(
        int fitKeyword, double boxW, double boxH, double intrinsicW, double intrinsicH)
    {
        if (fitKeyword == 0) return (boxW, boxH);                  // fill — the initial.
        if (intrinsicW <= 0 || intrinsicH <= 0) return (boxW, boxH);
        switch (fitKeyword)
        {
            case 1:   // contain
            case 2:   // cover
            {
                var sx = boxW / intrinsicW;
                var sy = boxH / intrinsicH;
                var s = fitKeyword == 1 ? Math.Min(sx, sy) : Math.Max(sx, sy);
                return (intrinsicW * s, intrinsicH * s);
            }
            case 3:   // none
                return (intrinsicW, intrinsicH);
            case 4:   // scale-down — the smaller of `none` and `contain` (§5.5).
            {
                var s = Math.Min(1.0, Math.Min(boxW / intrinsicW, boxH / intrinsicH));
                return (intrinsicW * s, intrinsicH * s);
            }
            default:
                return (boxW, boxH);   // unreachable for a table-validated slot — fill.
        }
    }

    /// <summary>The CSS-PROPERTY-VALUE-INVALID-001 message for an unsupported <c>object-position</c>
    /// (object-position cycle; PR #169 review P3): the raw value flows through
    /// <see cref="NetPdf.Css.Diagnostics.DiagnosticTextSanitizer"/> — C0/C1 control-char redaction
    /// + a length cap — like
    /// every other untrusted fragment reaching a diagnostics sink (an attacker-supplied inline
    /// style could otherwise inject ANSI escapes / bloat the log). INTERNAL so the sanitization is
    /// unit-tested directly: this path is otherwise reachable only via raw-recovery, not the facade
    /// (AngleSharp drops an invalid <c>object-position</c> upstream).</summary>
    internal static string BuildInvalidObjectPositionDiagnostic(string? rawPosition) =>
        $"object-position value '{NetPdf.Css.Diagnostics.DiagnosticTextSanitizer.Sanitize(rawPosition)}' "
        + "is outside the supported set (per-axis keywords, absolute lengths, percentages; one value "
        + "→ the other axis centers); the initial 50% 50% is used.";
}
