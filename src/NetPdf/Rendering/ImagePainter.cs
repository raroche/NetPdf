// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Layouters;
using NetPdf.Pdf;

namespace NetPdf.Rendering;

/// <summary>
/// Paint replaced-element (<c>&lt;img&gt;</c>) content (img-pipeline cycle): each fragment whose
/// box is a successfully decoded image places its XObject at the fragment's CONTENT box (the
/// border box inset by the used border + padding — the same slots the band/borders painted).
/// Runs AFTER <see cref="FragmentPainter.PaintFragments"/> (all bands + borders) and BEFORE the
/// text pass — replaced content paints over every band, under the glyphs (a documented paint-order
/// approximation, like text-last; fine for non-overlapping flows). The image stretches to the
/// content box (the §10.3.2 used size the sizing pre-pass wrote), preserving its aspect ratio
/// exactly when the pre-pass derived one side from the other; <c>object-fit</c> is deferred.
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
        double contentOriginTopPx)
    {
        if (cache.ImageBoxes.Count == 0) return;
        for (var i = 0; i < fragments.Count; i++)
        {
            var fragment = fragments[i];
            if (!cache.ImageBoxes.TryGetValue(fragment.Box, out var uri)
                || !cache.TryGet(uri, out var entry))
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

            var imageRef = ImageResourceCache.GetOrRegister(document, entry);
            FragmentPainter.ToPdfRect(
                leftPx, topPx, widthPx, heightPx, pageHeightPt,
                out var x, out var y, out var w, out var h);
            page.PlaceImage(imageRef, x, y, w, h);
        }
    }
}
