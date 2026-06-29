// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using SkiaSharp;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Parser.Preprocessing;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
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
/// <b>Borders.</b> <c>solid</c>, <c>dashed</c>, <c>dotted</c>, and <c>double</c> render
/// faithfully on the square per-edge path AND the uniform rounded ring (dashed/dotted
/// stroke a rounded centreline; double = two concentric rings); outlines too. Only the
/// 3D styles (<c>groove</c> / <c>ridge</c> / <c>inset</c> / <c>outset</c>) on a ROUNDED
/// ring/outline, and a NON-UNIFORM (mixed) non-solid style on a rounded border, stay a
/// solid-ring / clipped per-edge approximation surfaced via
/// <c>PAINT-BORDER-STYLE-APPROXIMATED-001</c>.
/// <c>none</c> and <c>hidden</c> paint nothing (CSS Backgrounds &amp; Borders 3
/// §4.3 — the used border width is 0 for those styles; layout reserves no space and
/// the painter skips them). Edges span the full box extent on their long axis, so
/// corners overlap — exact for uniform SQUARE borders; mitered / per-corner joins are a
/// refinement. A UNIFORM border on a box with a <c>border-radius</c> instead paints as ONE
/// filled rounded RING that follows the rounded corners (border-radius-completion cycle);
/// a non-uniform border keeps the square per-edge rects (rounded non-uniform borders deferred).
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
    private const int BorderStyleDotted = 2;
    private const int BorderStyleDashed = 3;
    private const int BorderStyleSolid = 4;
    private const int BorderStyleDouble = 5;
    private const int BorderStyleGroove = 6;
    private const int BorderStyleRidge = 7;
    private const int BorderStyleInset = 8;
    private const int BorderStyleOutset = 9;

    // outline-style keyword ids (outline cycle; post-PR-#173 review P2): CSS UI 4 §5.2 admits the
    // border-style values EXCEPT `hidden`, PLUS `auto`. The KeywordResolver table is
    // none, dotted, dashed, solid, double, groove, ridge, inset, outset, auto — so the indices differ
    // from border-style (no hidden ⇒ solid is 3, not 4). `none` paints nothing; `auto` paints solid.
    private const int OutlineStyleNone = 0;
    private const int OutlineStyleDotted = 1;
    private const int OutlineStyleDashed = 2;
    private const int OutlineStyleSolid = 3;
    private const int OutlineStyleDouble = 4;
    private const int OutlineStyleAuto = 9;

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
    /// <param name="effectiveTransforms">Phase 4 — box → effective (ancestor-composed) PDF cm; a
    /// fragment whose box is present wraps its decoration in that matrix. Null = nothing transformed.</param>
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
        PdfDocument? document = null,
        // Phase 4 transforms (review [P1]): box → effective (ancestor-composed) PDF cm. A fragment
        // whose box is present wraps its decoration in that matrix. Null = nothing transformed.
        IReadOnlyDictionary<Box, (double A, double B, double C, double D, double E, double F)>? effectiveTransforms = null)
    {
        // ONE "a line-style was approximated as solid" flag, SHARED by borders + outline (post-PR-#173
        // Copilot review): PAINT-BORDER-STYLE-APPROXIMATED-001 is a once-per-conversion diagnostic, so a
        // document with both a non-solid border and a non-solid outline still surfaces it only once.
        var styleApproximationReported = false;
        var variantUnsupportedReported = false;   // bg-variants cycle — once per render.
        var boxShadowRasterReported = false;      // Phase 4 shadows — once-per-render raster Info.
        var boxShadowCapReported = false;         // Phase 4 shadows — once-per-render over-cap Warning.
        var conicGradientRasterReported = false;  // Phase 4 gradients — once-per-render conic raster Info.
        var conicGradientCapReported = false;     // Phase 4 gradients — once-per-render conic over-cap Warning.
        var gradientAlphaCapReported = false;     // Phase 4 gradients — once-per-render translucent linear/radial over-cap Warning.
        var clipPathRasterReported = false;       // Phase 4 clip-path — once-per-render path() Warning.
        var clipPathSubtreeReported = false;      // Phase 4 clip-path — once-per-render subtree Warning.

        for (var i = 0; i < fragments.Count; i++)
        {
            var fragment = fragments[i];
            var style = fragment.Box.Style;
            if (style is null) continue;

            // Non-block-pagination arc (flex item / grid cell content) — a content
            // fragment whose box == the item paints TEXT ONLY (a separate pass): its box
            // decoration (background / borders / outline) is already painted by the item's
            // flex / grid GEOMETRY fragment, so skip it here to avoid a double paint.
            if (fragment.SuppressBoxDecoration) continue;

            // Border-box rect in CSS px, page-top-relative (y-down).
            var leftPx = contentOriginLeftPx + fragment.InlineOffset;
            var topPx = contentOriginTopPx + fragment.BlockOffset;
            var widthPx = fragment.InlineSize;
            var heightPx = fragment.BlockSize;
            if (widthPx <= 0 || heightPx <= 0) continue;

            // box-decoration-break: slice (inline-only line splitting) — when this fragment is a block-axis
            // slice of a larger box, the corners at a fragmentation CUT are square; the box rounds only at its
            // REAL top (first slice) / bottom (last slice). For a non-slice fragment both flags are true, so
            // every radius-aware painter is byte-identical.
            var isFirstSlice = !fragment.SuppressBlockStartChrome;
            var isLastSlice = !fragment.SuppressBlockEndChrome;

            var currentColorArgb = ResolveCurrentColor(style);

            // transform (Phase 4, review [P1]) — wrap this fragment's decoration in the box's
            // EFFECTIVE cm (its own transform composed with every ancestor's), so a child of a
            // transformed element transforms too. The text + image passes use the SAME map.
            // Non-transformed fragments emit no wrap (byte-identical).
            // mix-blend-mode (Phase 4 PR 4) — wrap this box's decoration in a PDF blend-mode graphics
            // state, OUTERMOST (so it composites the whole element against the backdrop). Text + image
            // blend is a documented residual (this cut blends the decoration). Non-blended → no wrap.
            string? blendModeName = null;
            var blended = imageCache is not null
                && imageCache.BlendModeBoxes.TryGetValue(fragment.Box, out blendModeName);
            if (blended) page.BeginBlendMode(blendModeName!);

            (double A, double B, double C, double D, double E, double F) effCm = default;
            var transformed = effectiveTransforms is not null
                && effectiveTransforms.TryGetValue(fragment.Box, out effCm);
            if (transformed) page.BeginTransform(effCm.A, effCm.B, effCm.C, effCm.D, effCm.E, effCm.F);

            // multicol-balancing-pagination (column rules, CSS Multi-column L1 §5) — a synthetic
            // column-rule fragment FILLS its rect with the container's column-rule-color
            // (currentcolor → the element color) and paints nothing else (no background / border /
            // outline / text). Foreground, like a border, so painted regardless of PrintBackgrounds.
            // Placed INSIDE the transform scope (PR #224 review [P2]) — the rule fragment's box IS
            // the multicol container, so a translated / rotated multicol moves its rules with its
            // content. Balance the BeginTransform's `q` before continuing.
            if (fragment.IsColumnRule)
            {
                PaintColumnRule(page, style, pageHeightPt, leftPx, topPx, widthPx, heightPx,
                    currentColorArgb, diagnostics, ref styleApproximationReported);
                if (transformed) page.RestoreGraphicsState();
                if (blended) page.RestoreGraphicsState();
                continue;
            }

            // clip-path (Phase 4 PR 3) — wrap this box's decoration in a native clip of its basic
            // shape (inside the transform, so a transformed + clipped box clips in its local space).
            // The descendant subtree + path() are documented residuals (diagnosed inside).
            var clipPathClipped = false;
            if (imageCache is not null
                && imageCache.ClipPathBoxes.TryGetValue(fragment.Box, out var clipShape))
            {
                clipPathClipped = BeginClipPath(page, clipShape, pageHeightPt, leftPx, topPx, widthPx,
                    heightPx, fragment.Box, diagnostics, ref clipPathRasterReported, ref clipPathSubtreeReported);
            }

            // Background first (behind borders), gated by PrintBackgrounds.
            if (paintBackgrounds)
            {
                // box-shadow (Phase 4 shadows) paints UNDERNEATH the background + border box
                // (CSS B&B §7.2 — shadows are drawn behind the element's own rendering).
                if (imageCache is not null && document is not null
                    && imageCache.BoxShadowBoxes.TryGetValue(fragment.Box, out var shadows))
                {
                    PaintBoxShadows(page, document, shadows, style, pageHeightPt,
                        leftPx, topPx, widthPx, heightPx, currentColorArgb, diagnostics,
                        ref boxShadowRasterReported, ref boxShadowCapReported,
                        // box-decoration-break: slice — when this fragment is one block-axis slice
                        // (DecorationBlockExtentPx > 0), the shadow shape is computed over the WHOLE box +
                        // CLIPPED to this slice's shadow portion (top shadow on the first slice, bottom on the
                        // last, the side shadows on every slice); a border-radius rounds the shadow only at
                        // this slice's real corners.
                        fragment.DecorationBlockExtentPx, fragment.DecorationBlockOffsetPx,
                        isFirstSlice: isFirstSlice, isLastSlice: isLastSlice);
                }

                PaintBackground(page, style, pageHeightPt, leftPx, topPx, widthPx, heightPx, currentColorArgb,
                    fragment.DecorationBlockExtentPx, fragment.DecorationBlockOffsetPx);
                // background-image tiles paint OVER this fragment's color and UNDER its borders
                // (CSS B&B §14.2 layer order), gated by PrintBackgrounds like the color.
                if (imageCache is not null && document is not null
                    && imageCache.BackgroundImageBoxes.TryGetValue(fragment.Box, out var bgSpec)
                    && imageCache.TryGet(bgSpec.UriKey, out var bgEntry))
                {
                    // background-origin (initial padding-box) sets the positioning area;
                    // background-clip (initial border-box) the paint rect — each insets the border
                    // box by the used border (padding-box) or border+padding (content-box) (bg-origin
                    // / bg-clip cycles).
                    var (oT, oR, oB, oL) = BackgroundAreaInset(style, bgSpec.OriginRaw, defaultArea: 'p');
                    var (cT, cR, cB, cL) = BackgroundAreaInset(style, bgSpec.ClipRaw, defaultArea: 'b');
                    // box-decoration-break: slice (PR #223 review [P1]) — both the POSITIONING area and the
                    // CLIP are built over the WHOLE composite box (virtual top = this slice's top − its
                    // offset, height = the full extent) so the tile grid / phase AND the rounded clip are
                    // CONTINUOUS, then an outer slice-rect clip (below) limits the paint to this fragment.
                    // The clip box's border-box corner radii resolve against the COMPOSITE height and inset
                    // per side to the background-clip box (border-box → no reduction; padding / content-box →
                    // reduced by the inset). The clip's top / bottom inset is the box's REAL border + padding
                    // (present at the composite top / bottom), so a content-box clip no longer over-clips a
                    // strip at a CUT edge — the slice rect clip cuts there instead (subsumes PR #222 [P2]).
                    // For a non-slice fragment the composite box IS the box → byte-identical.
                    var (compTopPx, compHeightPx, compRadiiPx) = CompositeRoundedBox(
                        style, topPx, widthPx, heightPx, fragment.DecorationBlockExtentPx, fragment.DecorationBlockOffsetPx);
                    var clipRadiiPx = InsetRadii(compRadiiPx, cT, cR, cB, cL);
                    var bgOriginTopPx = compTopPx + oT;
                    var bgOriginHeightPx = Math.Max(0, compHeightPx - oT - oB);
                    // Limit the paint to THIS slice (box-decoration-break: slice) — an outer rect clip around
                    // the whole-box positioning / rounded clip; no-op for a non-slice fragment.
                    var imageSliceClipped = false;
                    if (fragment.DecorationBlockExtentPx > 0)
                    {
                        ToPdfRect(leftPx, topPx, widthPx, heightPx, pageHeightPt, out var scx, out var scy, out var scw, out var sch);
                        page.BeginRectangleClip(scx, scy, scw, sch);
                        imageSliceClipped = true;
                    }
                    // Clamp the positioning-area + clip width/height to ≥ 0 — a thin box with large
                    // border/padding + a content-box origin/clip can drive the inset sum past the box
                    // dimension; a negative dimension must never reach the tiler (post-PR-#171 review P1).
                    PaintBackgroundImageTiles(
                        page, document, bgEntry, pageHeightPt,
                        leftPx + oL, bgOriginTopPx, Math.Max(0, widthPx - oL - oR), bgOriginHeightPx,
                        diagnostics, ref variantUnsupportedReported,
                        bgSpec.RepeatRaw, bgSpec.SizeRaw, bgSpec.PositionRaw,
                        clipLeftPx: leftPx + cL, clipTopPx: compTopPx + cT,
                        clipWidthPx: Math.Max(0, widthPx - cL - cR),
                        clipHeightPx: Math.Max(0, compHeightPx - cT - cB),
                        clipRadiiPx: clipRadiiPx);
                    if (imageSliceClipped) page.RestoreGraphicsState();
                }

                // Phase 4 gradients — a linear-gradient(...) background-image layer paints
                // over the background-color, under the borders (same z-order as an image
                // layer), as a PDF native axial shading clipped to the (rounded) clip box.
                // inline-only-block-line-splitting (box-decoration-break: slice) — when this fragment is one
                // block-axis slice of a larger box (DecorationBlockExtentPx > 0), the gradient's AXIS / center
                // / sweep spans the WHOLE composite box (so it's CONTINUOUS across slices) via the origin/clip
                // geometry below; an outer per-slice rect clip limits the paint to this fragment.
                var gradientSliced = fragment.DecorationBlockExtentPx > 0;
                // box-decoration-break: slice — the shading + its rounded clip span the WHOLE composite box;
                // an outer per-slice rect clip limits the paint to this fragment (no-op for a non-slice).
                var gradientSliceClipped = false;
                if (gradientSliced
                    && imageCache is not null && document is not null
                    && (imageCache.BackgroundGradientBoxes.ContainsKey(fragment.Box)
                        || imageCache.BackgroundRadialGradientBoxes.ContainsKey(fragment.Box)
                        || imageCache.BackgroundConicGradientBoxes.ContainsKey(fragment.Box)))
                {
                    ToPdfRect(leftPx, topPx, widthPx, heightPx, pageHeightPt, out var gcx, out var gcy, out var gcw, out var gch);
                    page.BeginRectangleClip(gcx, gcy, gcw, gch);
                    gradientSliceClipped = true;
                }
                // Single-layer gradient background-origin / -clip (parity with the url() image path +
                // multi-layer gradient layers, PR #235): the axis / center / sweep spans the ORIGIN box
                // (initial padding-box) and the shading is clipped to the CLIP box (initial border-box,
                // rounded). Both boxes are built over the WHOLE composite box (so a sliced gradient stays
                // continuous; the outer slice rect clip above limits the paint to this fragment), which
                // SUBSUMES the per-slice axisTopPx/axisHeightPx for the converted gradient types. Null
                // geometry (or a non-gradient box) → the box itself, byte-identical default.
                (double LeftPx, double TopPx, double WidthPx, double HeightPx, CornerRadii Radii)? gradClip = null;
                double gradOriginLeftPx = leftPx, gradOriginTopPx = topPx,
                    gradOriginWidthPx = widthPx, gradOriginHeightPx = heightPx;
                ImageResourceCache.GradientBgGeometry? gradGeomOpt = null;
                if (imageCache is not null
                    && imageCache.GradientGeometryBoxes.TryGetValue(fragment.Box, out var gradGeom))
                {
                    gradGeomOpt = gradGeom;
                    var (goT, goR, goB, goL) = BackgroundAreaInset(style, gradGeom.OriginRaw, defaultArea: 'p');
                    var (gcT, gcR, gcB, gcL) = BackgroundAreaInset(style, gradGeom.ClipRaw, defaultArea: 'b');
                    var (gCompTopPx, gCompHeightPx, gCompRadiiPx) = CompositeRoundedBox(
                        style, topPx, widthPx, heightPx,
                        fragment.DecorationBlockExtentPx, fragment.DecorationBlockOffsetPx);
                    gradOriginLeftPx = leftPx + goL;
                    gradOriginTopPx = gCompTopPx + goT;
                    gradOriginWidthPx = Math.Max(0, widthPx - goL - goR);
                    gradOriginHeightPx = Math.Max(0, gCompHeightPx - goT - goB);
                    gradClip = (
                        leftPx + gcL, gCompTopPx + gcT,
                        Math.Max(0, widthPx - gcL - gcR), Math.Max(0, gCompHeightPx - gcT - gcB),
                        InsetRadii(gCompRadiiPx, gcT, gcR, gcB, gcL));
                }
                // Paint the box's gradient (whichever type) once into the ORIGIN box, clipped to `clip`.
                void PaintGradientOnce(
                    double oL, double oT, double oW, double oH,
                    (double LeftPx, double TopPx, double WidthPx, double HeightPx, CornerRadii Radii)? clip)
                {
                    if (imageCache is null || document is null) return;
                    if (imageCache.BackgroundGradientBoxes.TryGetValue(fragment.Box, out var lin))
                        PaintLinearGradient(page, document, lin, style, pageHeightPt, oL, oT, oW, oH,
                            currentColorArgb, diagnostics, ref gradientAlphaCapReported, clipOverride: clip);
                    else if (imageCache.BackgroundRadialGradientBoxes.TryGetValue(fragment.Box, out var rad))
                        PaintRadialGradient(page, document, rad, style, pageHeightPt, oL, oT, oW, oH,
                            currentColorArgb, diagnostics, ref gradientAlphaCapReported, clipOverride: clip);
                    else if (imageCache.BackgroundConicGradientBoxes.TryGetValue(fragment.Box, out var con))
                        PaintConicGradient(page, document, con, style, pageHeightPt, oL, oT, oW, oH,
                            currentColorArgb, diagnostics,
                            ref conicGradientRasterReported, ref conicGradientCapReported, clipOverride: clip);
                }
                // background-size/-position/-repeat (now HONORED for a gradient): when any is NON-initial,
                // tile the gradient over the clip box; else a single paint over the origin box (byte-
                // identical default). A gradient TILE re-paints the shading sized + positioned per the grid.
                var gradientTiled = false;
                var gradientPaintNothing = false;
                if (gradGeomOpt is { } gg && gradClip is { } gclip)
                {
                    if (TryBeginGradientTiling(page, pageHeightPt, gg,
                            gradOriginLeftPx, gradOriginTopPx, gradOriginWidthPx, gradOriginHeightPx, gclip,
                            diagnostics, ref variantUnsupportedReported, out var grid, out gradientPaintNothing))
                    {
                        for (long row = 0; row < grid.CountY; row++)
                        for (long col = 0; col < grid.CountX; col++)
                        {
                            var tL = gradOriginLeftPx + grid.FirstXPx + col * grid.StepXPx;
                            var tT = gradOriginTopPx + grid.FirstYPx + row * grid.StepYPx;
                            PaintGradientOnce(tL, tT, grid.TileWidthPx, grid.TileHeightPx,
                                (tL, tT, grid.TileWidthPx, grid.TileHeightPx, default)); // tile-rect clip; the outer clip rounds
                        }
                        page.RestoreGraphicsState();
                        gradientTiled = true;
                    }
                }
                // A valid zero-sized tile paints nothing — suppress the single-paint fallback.
                if (!gradientTiled && !gradientPaintNothing)
                    PaintGradientOnce(gradOriginLeftPx, gradOriginTopPx, gradOriginWidthPx, gradOriginHeightPx, gradClip);
                if (gradientSliceClipped) page.RestoreGraphicsState();

                // Phase 4 multi-layer backgrounds — a comma-separated background-image list (a box is in this
                // dict ONLY when there are 2+ layers; single-layer paints above, byte-identical). Layers paint
                // BACK-TO-FRONT (last first, CSS B&B §3.10), each with its own position/size/repeat/origin/clip.
                if (imageCache is not null && document is not null
                    && imageCache.MultiLayerBackgroundBoxes.TryGetValue(fragment.Box, out var bgLayers))
                {
                    PaintMultiLayerBackground(page, document, imageCache, bgLayers, style, pageHeightPt,
                        leftPx, topPx, widthPx, heightPx, currentColorArgb,
                        fragment.DecorationBlockExtentPx, fragment.DecorationBlockOffsetPx, diagnostics,
                        ref variantUnsupportedReported, ref conicGradientRasterReported, ref conicGradientCapReported,
                        ref gradientAlphaCapReported);
                }

                // box-shadow INSET (Phase 4 PR 1) paints OVER the background (CSS B&B §7.2 — inset
                // shadows are drawn on top of the background, clipped to the padding box) but UNDER
                // the border (which paints next, occluding the band under it).
                if (imageCache is not null && document is not null
                    && imageCache.BoxShadowBoxes.TryGetValue(fragment.Box, out var insetShadows))
                {
                    PaintInsetBoxShadows(page, document, insetShadows, style, pageHeightPt,
                        leftPx, topPx, widthPx, heightPx, currentColorArgb, diagnostics,
                        ref boxShadowRasterReported, ref boxShadowCapReported);
                }
            }

            // border-image (Phase 4 PR 4) — a decoded border-image replaces the normal border rendering
            // (CSS B&B L3 §6: border-image is painted OVER the border area in place of border-style). The
            // image is sliced into the 9 regions + stretched to the box's border widths.
            var borderImagePainted = false;
            if (imageCache is not null && document is not null
                && imageCache.BorderImageBoxes.TryGetValue(fragment.Box, out var biEntry)
                && imageCache.TryGet(biEntry.UriKey, out var biImg))
            {
                PaintBorderImage(page, document, biImg, biEntry.Spec, style, pageHeightPt,
                    leftPx, topPx, widthPx, heightPx);
                borderImagePainted = true;
            }

            // Borders (foreground — always painted regardless of PrintBackgrounds). A sliced inline-only
            // block (box-decoration-break: slice) draws each block-axis border once: the block-start (top)
            // border edge is skipped on a non-first slice, the block-end (bottom) on a non-last slice.
            if (!borderImagePainted)
                PaintBorders(page, style, pageHeightPt, leftPx, topPx, widthPx, heightPx,
                    currentColorArgb, diagnostics, ref styleApproximationReported,
                    suppressTopEdge: fragment.SuppressBlockStartChrome,
                    suppressBottomEdge: fragment.SuppressBlockEndChrome,
                    decorationBlockExtentPx: fragment.DecorationBlockExtentPx,
                    decorationBlockOffsetPx: fragment.DecorationBlockOffsetPx);

            // Outline (CSS UI 4 §5 — outline cycle): painted OUTSIDE the border box, over everything,
            // and it does NOT affect layout. Always painted (it's not a background). It shares the
            // borders' style-approximation flag (one diagnostic per conversion). box-decoration-break:
            // slice — when this fragment is one block-axis slice (DecorationBlockExtentPx > 0), the outline
            // ring is computed over the WHOLE box (sides continuous across slices) + CLIPPED to this slice's
            // outline portion: the top outline only on the first slice (!SuppressBlockStartChrome), the
            // bottom only on the last (!SuppressBlockEndChrome).
            PaintOutline(page, style, pageHeightPt, leftPx, topPx, widthPx, heightPx,
                currentColorArgb, diagnostics, ref styleApproximationReported,
                fragment.DecorationBlockExtentPx, fragment.DecorationBlockOffsetPx,
                isFirstSlice: !fragment.SuppressBlockStartChrome,
                isLastSlice: !fragment.SuppressBlockEndChrome);

            if (clipPathClipped) page.RestoreGraphicsState(); // balance BeginClipPath's q
            if (transformed) page.RestoreGraphicsState(); // balance BeginTransform's q
            if (blended) page.RestoreGraphicsState(); // balance BeginBlendMode's q
        }
    }

    /// <summary>Phase 4 clip-path (PR 3) — begin a native PDF clip for the box's parsed basic shape
    /// (CSS Masking L1 §3), measured against the box's border box. <c>inset()</c> → a (rounded) rect
    /// clip; <c>circle()</c>/<c>ellipse()</c> → an ellipse clip (an omitted radius = closest-side; a
    /// <c>%</c> radius resolves against √(w²+h²)/√2 per §3.1); <c>polygon()</c> → a polygon clip.
    /// <c>path()</c> is deferred (raster follow-up) → no clip + a Warning. Returns <see langword="true"/>
    /// when a clip was opened (the caller balances with RestoreGraphicsState). A box with CHILDREN also
    /// warns — only its OWN decoration is clipped (the descendant subtree needs the Skia subtree
    /// renderer).</summary>
    internal static bool BeginClipPath(
        PdfPage page, CssClipPath clip, double pageHeightPt,
        double leftPx, double topPx, double widthPx, double heightPx, NetPdf.Layout.Boxes.Box box,
        IDiagnosticsSink? diagnostics, ref bool rasterReported, ref bool subtreeReported)
    {
        if (clip.Kind == ClipShapeKind.Path)
        {
            // Native path clip: parse the SVG path (Skia) → PDF path operators in the box's coordinate space
            // (px origin = the box's top-left), then `W n` / `W* n`. The path's own fill-rule
            // (path(evenodd, …)) selects the clip rule.
            var segs = BuildPathClipSegments(clip.PathData, leftPx, topPx, pageHeightPt);
            if (segs is null || segs.Count == 0)
            {
                if (!rasterReported)
                {
                    diagnostics?.Emit(new Diagnostic(DiagnosticCodes.CssClipPathRasterFallback001,
                        "A clip-path: path(\"…\") could not be parsed and was not applied; the element painted "
                        + "unclipped.", DiagnosticSeverity.Warning));
                    rasterReported = true;
                }
                return false;
            }
            page.BeginPathClip(segs, clip.EvenOdd);
            EmitClipSubtreeResidual(box, diagnostics, ref subtreeReported);
            return true;
        }
        EmitClipSubtreeResidual(box, diagnostics, ref subtreeReported);

        static double Resolve(ClipLen len, double extent) =>
            len.Px + (double.IsNaN(len.Frac) ? 0.0 : len.Frac) * extent;

        switch (clip.Kind)
        {
            case ClipShapeKind.Inset:
            {
                var t = Resolve(clip.Edges![0], heightPx);
                var rEdge = Resolve(clip.Edges![1], widthPx);
                var bEdge = Resolve(clip.Edges![2], heightPx);
                var l = Resolve(clip.Edges![3], widthPx);
                var w = Math.Max(0, widthPx - l - rEdge);
                var h = Math.Max(0, heightPx - t - bEdge);
                ToPdfRect(leftPx + l, topPx + t, w, h, pageHeightPt, out var px, out var py, out var pw, out var ph);
                if (clip.Radii is { } rad)
                {
                    // Per-corner radii (CSS Masking §inset — each corner is a length-percentage; X resolves
                    // against the inset box width, Y against its height). The `round X / Y` slash form gives
                    // separate Y radii (clip.RadiiY); without it, X is used for both axes.
                    var radY = clip.RadiiY ?? rad;
                    double Rx(int i) => PdfUnits.PxToPt(Math.Min(w, Resolve(rad[i], w)));
                    double Ry(int i) => PdfUnits.PxToPt(Math.Min(h, Resolve(radY[i], h)));
                    page.BeginRoundedRectangleClip(px, py, pw, ph,
                        new CornerRadii(Rx(0), Ry(0), Rx(1), Ry(1), Rx(2), Ry(2), Rx(3), Ry(3)));
                }
                else page.BeginRectangleClip(px, py, pw, ph);
                return true;
            }
            case ClipShapeKind.Circle:
            {
                var cxPx = leftPx + Resolve(clip.Cx, widthPx);
                var cyPx = topPx + Resolve(clip.Cy, heightPx);
                var rPx = ResolveCircleRadius(clip.RadiusExtent, clip.Radius,
                    cxPx - leftPx, leftPx + widthPx - cxPx, cyPx - topPx, topPx + heightPx - cyPx, widthPx, heightPx);
                if (!(rPx > 0)) return false;
                page.BeginEllipseClip(PdfUnits.PxToPt(cxPx), pageHeightPt - PdfUnits.PxToPt(cyPx), PdfUnits.PxToPt(rPx), PdfUnits.PxToPt(rPx));
                return true;
            }
            case ClipShapeKind.Ellipse:
            {
                var cxPx = leftPx + Resolve(clip.Cx, widthPx);
                var cyPx = topPx + Resolve(clip.Cy, heightPx);
                var rxPx = ResolveEllipseAxis(clip.RxExtent, clip.Rx, cxPx - leftPx, leftPx + widthPx - cxPx, widthPx);
                var ryPx = ResolveEllipseAxis(clip.RyExtent, clip.Ry, cyPx - topPx, topPx + heightPx - cyPx, heightPx);
                if (!(rxPx > 0) || !(ryPx > 0)) return false;
                page.BeginEllipseClip(PdfUnits.PxToPt(cxPx), pageHeightPt - PdfUnits.PxToPt(cyPx), PdfUnits.PxToPt(rxPx), PdfUnits.PxToPt(ryPx));
                return true;
            }
            case ClipShapeKind.Polygon:
            {
                var pts = new (double, double)[clip.Points!.Length];
                for (var i = 0; i < pts.Length; i++)
                {
                    var ptxPx = leftPx + Resolve(clip.Points[i].X, widthPx);
                    var ptyPx = topPx + Resolve(clip.Points[i].Y, heightPx);
                    pts[i] = (PdfUnits.PxToPt(ptxPx), pageHeightPt - PdfUnits.PxToPt(ptyPx));
                }
                page.BeginPolygonClip(pts, clip.EvenOdd); // polygon(evenodd, …) → W* n
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>A <c>clip-path</c> on an element with CHILDREN clips only the element's OWN decoration
    /// (background / border / image), not its descendant subtree — diagnosed once (the subtree clip needs the
    /// Skia subtree renderer).</summary>
    private static void EmitClipSubtreeResidual(NetPdf.Layout.Boxes.Box box, IDiagnosticsSink? diagnostics, ref bool subtreeReported)
    {
        if (box.Children.Count > 0 && !subtreeReported)
        {
            diagnostics?.Emit(new Diagnostic(DiagnosticCodes.CssClipPathSubtreeUnsupported001,
                "A clip-path on an element with children clipped only the element's own decoration "
                + "(background / border / image), not its descendant content (a subtree clip needs the "
                + "Skia subtree renderer NetPdf lacks).", DiagnosticSeverity.Warning));
            subtreeReported = true;
        }
    }

    // DoS guards (PR-234 review [P3]) — an author-supplied clip path is untrusted; cap the raw path-data
    // length and the emitted segment count so a giant path can't allocate an unbounded segment list / blow up
    // the PDF stream. Over-cap → null (the element paints unclipped + the raster-fallback diagnostic fires).
    private const int MaxClipPathDataLength = 64 * 1024;   // 64 KiB of path text
    private const int MaxClipPathSegments = 20_000;

    /// <summary>Convert a <c>clip-path: path("…")</c> SVG path string into PDF clip segments in the box's
    /// coordinate space (px origin = the box's top-left). Curves are preserved (quad → cubic; conic → cubics);
    /// the result feeds <see cref="PdfPage.BeginPathClip"/>. Returns <see langword="null"/> on an empty /
    /// unparseable / over-cap path.</summary>
    private static List<PdfPathSegment>? BuildPathClipSegments(string? pathData, double leftPx, double topPx, double pageHeightPt)
    {
        if (string.IsNullOrWhiteSpace(pathData) || pathData.Length > MaxClipPathDataLength) return null;
        using var skPath = SKPath.ParseSvgPathData(pathData);
        if (skPath is null) return null;

        double Xp(float px) => PdfUnits.PxToPt(leftPx + px);
        double Yp(float py) => pageHeightPt - PdfUnits.PxToPt(topPx + py);

        var segs = new List<PdfPathSegment>();
        void Quad(SKPoint p0, SKPoint p1, SKPoint p2)
        {
            // Quadratic → cubic: the two cubic controls are p0/p2 raised 2/3 toward the quad control.
            var c1 = new SKPoint(p0.X + 2f / 3f * (p1.X - p0.X), p0.Y + 2f / 3f * (p1.Y - p0.Y));
            var c2 = new SKPoint(p2.X + 2f / 3f * (p1.X - p2.X), p2.Y + 2f / 3f * (p1.Y - p2.Y));
            segs.Add(PdfPathSegment.Curve(Xp(c1.X), Yp(c1.Y), Xp(c2.X), Yp(c2.Y), Xp(p2.X), Yp(p2.Y)));
        }

        using var it = skPath.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move: segs.Add(PdfPathSegment.Move(Xp(pts[0].X), Yp(pts[0].Y))); break;
                case SKPathVerb.Line: segs.Add(PdfPathSegment.Line(Xp(pts[1].X), Yp(pts[1].Y))); break;
                case SKPathVerb.Quad: Quad(pts[0], pts[1], pts[2]); break;
                case SKPathVerb.Cubic:
                    segs.Add(PdfPathSegment.Curve(Xp(pts[1].X), Yp(pts[1].Y), Xp(pts[2].X), Yp(pts[2].Y), Xp(pts[3].X), Yp(pts[3].Y)));
                    break;
                case SKPathVerb.Conic:
                {
                    // Conic (rational quad, from an arc) → 2 quads → 2 cubics (a faithful native approximation).
                    var quads = new SKPoint[5];
                    SKPath.ConvertConicToQuads(pts[0], pts[1], pts[2], it.ConicWeight(), quads, 1);
                    Quad(quads[0], quads[1], quads[2]);
                    Quad(quads[2], quads[3], quads[4]);
                    break;
                }
                case SKPathVerb.Close: segs.Add(PdfPathSegment.Close); break;
            }
            if (segs.Count > MaxClipPathSegments) return null; // over the complexity cap → fall back
        }
        return segs.Count > 0 ? segs : null;
    }

    /// <summary>The circle() radius in px: <c>closest-side</c> = the min distance to the four edges,
    /// <c>farthest-side</c> = the max; a <c>%</c> length resolves against √(w²+h²)/√2 (CSS Masking
    /// §3.1); a plain length is direct.</summary>
    private static double ResolveCircleRadius(
        ClipRadiusExtent ext, ClipLen radius,
        double distL, double distR, double distT, double distB, double widthPx, double heightPx) => ext switch
    {
        ClipRadiusExtent.ClosestSide => Math.Min(Math.Min(distL, distR), Math.Min(distT, distB)),
        ClipRadiusExtent.FarthestSide => Math.Max(Math.Max(distL, distR), Math.Max(distT, distB)),
        _ => radius.Px + radius.Frac * (Math.Sqrt(widthPx * widthPx + heightPx * heightPx) / Math.Sqrt(2.0)),
    };

    /// <summary>One ellipse() axis radius in px: <c>closest-side</c>/<c>farthest-side</c> = the
    /// min/max distance from the center to the two edges on this axis; a length/% is direct against the
    /// axis's <paramref name="extentPx"/> (a <c>%</c> stored in <c>Frac</c>).</summary>
    private static double ResolveEllipseAxis(ClipRadiusExtent ext, ClipLen len, double distNear, double distFar, double extentPx) => ext switch
    {
        ClipRadiusExtent.ClosestSide => Math.Min(distNear, distFar),
        ClipRadiusExtent.FarthestSide => Math.Max(distNear, distFar),
        _ => len.Px + len.Frac * extentPx,
    };

    /// <summary>Per-fragment tile-count threshold ABOVE which the tiling emits ONE PDF
    /// tiling-pattern fill instead of per-tile placements (tiling-patterns cycle, ISO 32000-2
    /// §8.7.3 — O(1) content-stream size regardless of count, so the old 4096-tile cap +
    /// <c>PAINT-BG-IMAGE-TILE-CAP-001</c> are RETIRED; a 1×1 px tile over a full page is now
    /// one pattern object). At or below the threshold the per-tile loop stays — fewer bytes
    /// than a pattern object for small grids, and every pre-cycle stream is byte-identical.</summary>
    private const int MaxLoopTilesPerFragment = 16;

    /// <summary>Phase 4 multi-layer backgrounds — paint a comma-separated <c>background-image</c> list
    /// BACK-TO-FRONT (the last source layer is bottom-most, CSS B&amp;B §3.10). Each image layer uses its own
    /// <c>background-position</c>/<c>-size</c>/<c>-repeat</c>/<c>-origin</c>/<c>-clip</c> (cycled at
    /// collection). Each gradient layer honours its own <c>background-origin</c> (the shading spans the
    /// origin box) + <c>background-clip</c> (the shading is clipped, rounded), but NOT
    /// <c>background-size</c>/<c>-position</c>/<c>-repeat</c> — those are surfaced once
    /// (<c>CSS-BACKGROUND-IMAGE-UNSUPPORTED-001</c>) and the shading fills the origin box (a documented
    /// deferral). Mirrors the single-layer geometry so a 1-layer background (which never reaches here)
    /// stays byte-identical.</summary>
    private static void PaintMultiLayerBackground(
        PdfPage page, PdfDocument document, ImageResourceCache imageCache,
        System.Collections.Generic.List<ImageResourceCache.BgLayer> layers, ComputedStyle style, double pageHeightPt,
        double leftPx, double topPx, double widthPx, double heightPx, uint currentColorArgb,
        double decorationBlockExtentPx, double decorationBlockOffsetPx, IDiagnosticsSink? diagnostics,
        ref bool variantUnsupportedReported, ref bool conicRasterReported, ref bool conicCapReported,
        ref bool gradientAlphaCapReported)
    {
        var sliced = decorationBlockExtentPx > 0;
        var (compTopPx, compHeightPx, compRadiiPx) = CompositeRoundedBox(
            style, topPx, widthPx, heightPx, decorationBlockExtentPx, decorationBlockOffsetPx);

        for (var i = layers.Count - 1; i >= 0; i--)   // back-to-front
        {
            var layer = layers[i];
            if (layer.Kind == ImageResourceCache.BgLayerKind.Url)
            {
                if (layer.UriKey is not { } key || !imageCache.TryGet(key, out var entry)) continue;
                var (oT, oR, oB, oL) = BackgroundAreaInset(style, layer.OriginRaw, defaultArea: 'p');
                var (cT, cR, cB, cL) = BackgroundAreaInset(style, layer.ClipRaw, defaultArea: 'b');
                var clipRadiiPx = InsetRadii(compRadiiPx, cT, cR, cB, cL);
                var bgOriginTopPx = compTopPx + oT;
                var bgOriginHeightPx = Math.Max(0, compHeightPx - oT - oB);
                var imageSliceClipped = false;
                if (sliced)
                {
                    ToPdfRect(leftPx, topPx, widthPx, heightPx, pageHeightPt, out var scx, out var scy, out var scw, out var sch);
                    page.BeginRectangleClip(scx, scy, scw, sch);
                    imageSliceClipped = true;
                }
                PaintBackgroundImageTiles(
                    page, document, entry, pageHeightPt,
                    leftPx + oL, bgOriginTopPx, Math.Max(0, widthPx - oL - oR), bgOriginHeightPx,
                    diagnostics, ref variantUnsupportedReported,
                    layer.RepeatRaw, layer.SizeRaw, layer.PositionRaw,
                    clipLeftPx: leftPx + cL, clipTopPx: compTopPx + cT,
                    clipWidthPx: Math.Max(0, widthPx - cL - cR),
                    clipHeightPx: Math.Max(0, compHeightPx - cT - cB),
                    clipRadiiPx: clipRadiiPx);
                if (imageSliceClipped) page.RestoreGraphicsState();
            }
            else // a gradient layer
            {
                // A gradient layer honours its own background-origin + background-clip (like an image
                // layer): the shading is computed over the ORIGIN box (default padding-box) and CLIPPED
                // to the CLIP box (default border-box, rounded). background-size/-position/-repeat are
                // NOT honoured (the native shading fills the origin box) — surfaced once + ignored.
                var (goT, goR, goB, goL) = BackgroundAreaInset(style, layer.OriginRaw, defaultArea: 'p');
                var (gcT, gcR, gcB, gcL) = BackgroundAreaInset(style, layer.ClipRaw, defaultArea: 'b');
                // The origin box is whole-box (composite) so a sliced gradient stays continuous; the
                // per-slice outer rect clip below limits the paint to this fragment.
                var gOriginLeftPx = leftPx + goL;
                var gOriginWidthPx = Math.Max(0, widthPx - goL - goR);
                var gOriginTopPx = compTopPx + goT;
                var gOriginHeightPx = Math.Max(0, compHeightPx - goT - goB);
                var gClipRadiiPx = InsetRadii(compRadiiPx, gcT, gcR, gcB, gcL);
                (double LeftPx, double TopPx, double WidthPx, double HeightPx, CornerRadii Radii) gClip = (
                    leftPx + gcL, compTopPx + gcT,
                    Math.Max(0, widthPx - gcL - gcR), Math.Max(0, compHeightPx - gcT - gcB),
                    gClipRadiiPx);
                var gradientSliceClipped = false;
                if (sliced)
                {
                    ToPdfRect(leftPx, topPx, widthPx, heightPx, pageHeightPt, out var gcx, out var gcy, out var gcw, out var gch);
                    page.BeginRectangleClip(gcx, gcy, gcw, gch);
                    gradientSliceClipped = true;
                }
                // background-size/-position/-repeat on a gradient LAYER are now honored too (tiling) —
                // parity with the single-layer path; an all-initial layer single-paints (byte-identical).
                var layerGeom = new ImageResourceCache.GradientBgGeometry(
                    layer.OriginRaw, layer.ClipRaw, layer.SizeRaw, layer.PositionRaw, layer.RepeatRaw);
                var layerTiled = false;
                if (TryBeginGradientTiling(page, pageHeightPt, layerGeom,
                        gOriginLeftPx, gOriginTopPx, gOriginWidthPx, gOriginHeightPx, gClip,
                        diagnostics, ref variantUnsupportedReported, out var lgrid, out var layerPaintNothing))
                {
                    for (long row = 0; row < lgrid.CountY; row++)
                    for (long col = 0; col < lgrid.CountX; col++)
                    {
                        var tL = gOriginLeftPx + lgrid.FirstXPx + col * lgrid.StepXPx;
                        var tT = gOriginTopPx + lgrid.FirstYPx + row * lgrid.StepYPx;
                        PaintLayerGradient(page, document, layer, style, pageHeightPt,
                            tL, tT, lgrid.TileWidthPx, lgrid.TileHeightPx, currentColorArgb, diagnostics,
                            ref gradientAlphaCapReported, ref conicRasterReported, ref conicCapReported,
                            (tL, tT, lgrid.TileWidthPx, lgrid.TileHeightPx, default));
                    }
                    page.RestoreGraphicsState();
                    layerTiled = true;
                }
                // A valid zero-sized tile paints nothing — suppress the single-paint fallback.
                if (!layerTiled && !layerPaintNothing)
                    PaintLayerGradient(page, document, layer, style, pageHeightPt,
                        gOriginLeftPx, gOriginTopPx, gOriginWidthPx, gOriginHeightPx, currentColorArgb, diagnostics,
                        ref gradientAlphaCapReported, ref conicRasterReported, ref conicCapReported, gClip);
                if (gradientSliceClipped) page.RestoreGraphicsState();
            }
        }
    }

    /// <summary>Paint a multi-layer gradient LAYER (whichever type) once into the origin box
    /// (<paramref name="oL"/>, <paramref name="oT"/>, <paramref name="oW"/>, <paramref name="oH"/>),
    /// clipped to <paramref name="clip"/>. A static method (not a local function) so it can take the
    /// once-per-render <c>ref</c> diagnostic flags — used for both the single paint and each tile.</summary>
    private static void PaintLayerGradient(
        PdfPage page, PdfDocument document, ImageResourceCache.BgLayer layer, ComputedStyle style,
        double pageHeightPt, double oL, double oT, double oW, double oH, uint currentColorArgb,
        IDiagnosticsSink? diagnostics, ref bool gradientAlphaCapReported,
        ref bool conicRasterReported, ref bool conicCapReported,
        (double LeftPx, double TopPx, double WidthPx, double HeightPx, CornerRadii Radii)? clip)
    {
        if (layer.Linear is { } lin)
            PaintLinearGradient(page, document, lin, style, pageHeightPt, oL, oT, oW, oH, currentColorArgb, diagnostics, ref gradientAlphaCapReported, clipOverride: clip);
        else if (layer.Radial is { } rad)
            PaintRadialGradient(page, document, rad, style, pageHeightPt, oL, oT, oW, oH, currentColorArgb, diagnostics, ref gradientAlphaCapReported, clipOverride: clip);
        else if (layer.Conic is { } con)
            PaintConicGradient(page, document, con, style, pageHeightPt, oL, oT, oW, oH, currentColorArgb, diagnostics, ref conicRasterReported, ref conicCapReported, clipOverride: clip);
    }

    /// <summary>True when a gradient's <c>background-size</c>/<c>-position</c>/<c>-repeat</c> is
    /// NON-initial, so it needs the variant path — TILING for a supported value (the shading is sized /
    /// positioned / repeated), or a fallback + diagnostic for an unsupported value / over-cap grid.
    /// <see langword="false"/> (all initial) → the byte-identical single paint. Shared by the single-layer
    /// + multi-layer gradient paths.</summary>
    private static bool GradientVariantRequiresTilingOrDiagnostic(string? sizeRaw, string? repeatRaw, string? positionRaw)
    {
        // The recomposed -x/-y position can carry stray/double spaces — collapse to single-space lowercase
        // before matching against the initial forms.
        static string Norm(string? raw) => raw is null
            ? string.Empty
            : string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

        var st = Norm(sizeRaw);
        if (st.Length > 0 && st is not ("auto" or "auto auto")) return true;
        var rt = Norm(repeatRaw);
        if (rt.Length > 0 && rt is not ("repeat" or "repeat repeat")) return true;
        // background-position is initial-equivalent when EVERY axis token resolves to a zero offset —
        // parse each token (PR #238 [P3]) rather than allowlisting literal strings, so `0px 0%` /
        // `0% 0px` / `0 0px` etc. are all recognised as no-ops. AngleSharp canonicalizes `0% 0%` → `0 0`.
        var pt = Norm(positionRaw);
        if (pt.Length > 0)
        {
            foreach (var tok in pt.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (!IsZeroPositionToken(tok)) return true;
        }
        return false;
    }

    /// <summary>A <c>background-position</c> axis token that resolves to a ZERO start offset: the
    /// <c>left</c>/<c>top</c> keywords (= 0%), or a numeric <c>0</c> in any unit (<c>0</c>, <c>0%</c>,
    /// <c>0px</c>, …). <c>center</c>/<c>right</c>/<c>bottom</c> and any non-zero length/percentage are
    /// NOT zero (the gradient would be repositioned were size honoured).</summary>
    private static bool IsZeroPositionToken(string token)
    {
        if (token is "left" or "top") return true;
        // Strip a trailing % or alphabetic unit, then parse the numeric — zero iff it parses to 0.
        var end = token.Length;
        while (end > 0 && (char.IsLetter(token[end - 1]) || token[end - 1] == '%')) end--;
        var numberPart = token.AsSpan(0, end);
        return double.TryParse(numberPart, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v == 0.0;
    }

    internal const int MaxGradientTiles = 256; // safety cap on the gradient tile loop (each tile re-paints the shading)

    /// <summary>Phase 4 gradients — a single-layer / layer gradient's resolved tile grid for
    /// <c>background-size</c>/<c>-position</c>/<c>-repeat</c>: the tile size (px) + each axis's first
    /// tile offset (positioning-area-relative), tile count, and origin step (tile size, or tile + gap
    /// for <c>space</c>). The shading is painted once per tile (clipped to it).</summary>
    internal readonly record struct GradientTileGrid(
        double TileWidthPx, double TileHeightPx,
        double FirstXPx, long CountX, double StepXPx,
        double FirstYPx, long CountY, double StepYPx);

    /// <summary>Resolve a gradient's <c>background-size</c> to a concrete tile size. A gradient has NO
    /// intrinsic size or ratio (CSS Images §4.3), so <c>auto</c>/<c>contain</c>/<c>cover</c> all = the
    /// positioning area; an explicit <c>&lt;length|%&gt;</c> sets that axis and an <c>auto</c>/missing
    /// axis takes the area dimension (NO ratio completion — unlike an image). Returns
    /// <see langword="false"/> for a non-absolute unit / junk (the caller diagnoses + uses the area).</summary>
    internal static bool TryResolveGradientTileSize(
        string? raw, double areaW, double areaH, out double tileW, out double tileH)
    {
        tileW = areaW;
        tileH = areaH;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var v = raw.Trim().ToLowerInvariant();
        if (v is "auto" or "auto auto" or "contain" or "cover") return true; // no intrinsic size → the area
        var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2) return false;
        if (!TryParseSizeComponent(parts[0], areaW, out var wAuto, out var w)) return false;
        var hAuto = true;
        var h = 0.0;
        if (parts.Length == 2 && !TryParseSizeComponent(parts[1], areaH, out hAuto, out h)) return false;
        tileW = wAuto ? areaW : w;   // an auto axis = the area dimension (no aspect completion)
        tileH = hAuto ? areaH : h;
        return true;
    }

    /// <summary>Resolve a gradient's tile grid (<c>background-size</c>/<c>-position</c>/<c>-repeat</c>)
    /// over the positioning AREA (<paramref name="areaW"/> × <paramref name="areaH"/>, the background-
    /// origin box) covering the CLIP window (area-relative <c>[clipLeft, clipRight] × [clipTop,
    /// clipBottom]</c>, the background-clip box). Mirrors the <c>url()</c> image tiler: <c>round</c>
    /// rescales the tile so a whole number fills the axis BEFORE the position resolves, then
    /// <see cref="AxisTilingPlan"/> gives each axis's first/count/step. <paramref name="unsupportedValue"/>
    /// is set when a longhand value is outside the supported set (the caller diagnoses + uses the
    /// initial for that longhand).</summary>
    internal static GradientTileGrid ResolveGradientTileGrid(
        ImageResourceCache.GradientBgGeometry geom, double areaW, double areaH,
        double clipLeftPx, double clipRightPx, double clipTopPx, double clipBottomPx,
        out bool unsupportedValue)
    {
        unsupportedValue = false;
        if (!TryParseBackgroundRepeat(geom.RepeatRaw, out var modeX, out var modeY))
        {
            unsupportedValue = true;
            modeX = modeY = BackgroundRepeatMode.Repeat;
        }
        if (!TryResolveGradientTileSize(geom.SizeRaw, areaW, areaH, out var tileW, out var tileH))
        {
            unsupportedValue = true;
            tileW = areaW;
            tileH = areaH;
        }
        if (modeX == BackgroundRepeatMode.Round && tileW > 0)
            tileW = areaW / Math.Max(1, Math.Round(areaW / tileW));
        if (modeY == BackgroundRepeatMode.Round && tileH > 0)
            tileH = areaH / Math.Max(1, Math.Round(areaH / tileH));
        // A ZERO-sized tile (`background-size: 0` / `10px 0` / `0%`) is VALID and paints NOTHING (CSS
        // B&B — like the url() image path); return a no-tile grid BEFORE AxisTilingPlan, which would
        // otherwise divide by the zero tile (PR #239 [P1]). The caller suppresses painting (no fallback).
        if (tileW <= 0 || tileH <= 0)
            return new GradientTileGrid(tileW, tileH, 0, 0, 0, 0, 0, 0);
        if (!TryParseBackgroundPosition(geom.PositionRaw, areaW, areaH, tileW, tileH, out var posX, out var posY))
        {
            unsupportedValue = true;
            posX = posY = 0;
        }
        var (firstX, nx, stepX) = AxisTilingPlan(modeX, areaW, tileW, posX, clipLeftPx, clipRightPx);
        var (firstY, ny, stepY) = AxisTilingPlan(modeY, areaH, tileH, posY, clipTopPx, clipBottomPx);
        return new GradientTileGrid(tileW, tileH, firstX, nx, stepX, firstY, ny, stepY);
    }

    /// <summary>Decide whether a gradient should TILE for its <c>background-size</c>/<c>-position</c>/
    /// <c>-repeat</c> and, if so, BEGIN the outer (rounded) background-clip clip + return the tile
    /// <paramref name="grid"/> — the caller then loops the grid painting one gradient tile each and calls
    /// <see cref="PdfPage.RestoreGraphicsState"/>. Returns <see langword="false"/> (no clip pushed) when
    /// the values are all initial (the caller single-paints), the origin box is degenerate, or the grid
    /// over-caps (surfaced once + single-paint). When <paramref name="paintNothing"/> is set (a VALID
    /// zero-sized tile, e.g. <c>background-size: 0</c>) the caller must paint NOTHING — not fall back to a
    /// single paint, and no diagnostic. An unsupported VALUE is diagnosed but still tiles with the initial
    /// for that longhand. Shared by the single-layer + multi-layer gradient paths.</summary>
    private static bool TryBeginGradientTiling(
        PdfPage page, double pageHeightPt, ImageResourceCache.GradientBgGeometry geom,
        double originLeftPx, double originTopPx, double originWidthPx, double originHeightPx,
        (double LeftPx, double TopPx, double WidthPx, double HeightPx, CornerRadii Radii) clip,
        IDiagnosticsSink? diagnostics, ref bool variantUnsupportedReported,
        out GradientTileGrid grid, out bool paintNothing)
    {
        grid = default;
        paintNothing = false;
        if (!GradientVariantRequiresTilingOrDiagnostic(geom.SizeRaw, geom.RepeatRaw, geom.PositionRaw)
            || originWidthPx <= 0 || originHeightPx <= 0)
        {
            return false;
        }
        grid = ResolveGradientTileGrid(
            geom, originWidthPx, originHeightPx,
            clip.LeftPx - originLeftPx, clip.LeftPx + clip.WidthPx - originLeftPx,
            clip.TopPx - originTopPx, clip.TopPx + clip.HeightPx - originTopPx,
            out var unsupportedValue);
        if (unsupportedValue)
            EmitVariantUnsupported(diagnostics, anyVariantUnsupported: true, ref variantUnsupportedReported);
        // A VALID zero-sized tile (size 0 / Npx 0; an unsupported value already fell back to the non-zero
        // area) paints NOTHING — no single-paint fallback, no diagnostic (PR #239 [P1]).
        if (grid.TileWidthPx <= 0 || grid.TileHeightPx <= 0)
        {
            paintNothing = true;
            return false;
        }
        // Cap the tile loop (each tile re-paints the shading); the product bound is overflow-safe. Over-cap
        // → single paint (the gradient still shows untiled) + surface once rather than dropping silently.
        var withinCap = grid.CountX is > 0 and <= MaxGradientTiles
            && grid.CountY is > 0 and <= MaxGradientTiles
            && grid.CountX <= MaxGradientTiles / grid.CountY;
        if (!withinCap)
        {
            if (diagnostics is not null && !variantUnsupportedReported)
            {
                diagnostics.Emit(new Diagnostic(
                    DiagnosticCodes.CssBackgroundImageUnsupported001,
                    $"A gradient's background-size/-repeat tiled to more than {MaxGradientTiles} tiles; it "
                    + "was painted once (untiled) instead of as the full tile grid.",
                    DiagnosticSeverity.Warning));
                variantUnsupportedReported = true;
            }
            return false;
        }
        ToPdfRect(clip.LeftPx, clip.TopPx, clip.WidthPx, clip.HeightPx, pageHeightPt,
            out var cbx, out var cby, out var cbw, out var cbh);
        if (clip.Radii.AnyPositive) page.BeginRoundedRectangleClip(cbx, cby, cbw, cbh, ToPt(clip.Radii));
        else page.BeginRectangleClip(cbx, cby, cbw, cbh);
        return true;
    }

    /// <summary>Tile <paramref name="entry"/> over the fragment's BORDER box (bg-image +
    /// bg-variants cycles): the tile size comes from <paramref name="sizeRaw"/>
    /// (<c>auto</c> = intrinsic px — the initial; <c>contain</c> / <c>cover</c>;
    /// <c>&lt;length|%&gt;{1,2}</c>, % against the area, a missing/auto side aspect-completed),
    /// the grid's phase from <paramref name="positionRaw"/> (keywords / <c>&lt;length|%&gt;</c>
    /// per the CSS B&amp;B §3.6 percentage rule — <c>x%</c> aligns the image's x% point with the
    /// area's; one value → the other axis centers), and each axis repeats per
    /// <paramref name="repeatRaw"/> (<c>repeat</c> — the initial — / <c>no-repeat</c> /
    /// <c>repeat-x</c> / <c>repeat-y</c> / <c>space</c> / <c>round</c> / the two-value axis form).
    /// An unsupported form (non-absolute units / a malformed position) surfaces once per render and
    /// that longhand falls back to its initial. The positioning area is the caller-passed box — the
    /// body caller passes the <c>background-origin</c> box (initial padding-box; bg-origin cycle),
    /// the margin-box caller its band (origin/clip = the band, a documented approximation). The
    /// clip rect (<c>background-clip</c>, initial border-box; bg-clip cycle) is the optional clip
    /// params, defaulting to the positioning area. Null raws = the initial everywhere. INTERNAL so
    /// the pipeline reuses the tiler for page-margin-box background images (margin-box-bg-image
    /// cycle).</summary>
    internal static void PaintBackgroundImageTiles(
        PdfPage page, PdfDocument document, ImageResourceCache.Entry entry, double pageHeightPt,
        double leftPx, double topPx, double widthPx, double heightPx,
        IDiagnosticsSink? diagnostics, ref bool variantUnsupportedReported,
        string? repeatRaw = null, string? sizeRaw = null, string? positionRaw = null,
        double? clipLeftPx = null, double? clipTopPx = null,
        double? clipWidthPx = null, double? clipHeightPx = null,
        CornerRadii clipRadiiPx = default)
    {
        // background-clip (bg-clip cycle): the PAINT area, defaulting to the POSITIONING area —
        // the body caller passes the border box here while the area (leftPx..heightPx) is the
        // background-origin box; the margin-box caller leaves it = the band. Tiles are POSITIONED
        // in the area (background-origin) but PAINTED only within the clip rect.
        var clipL = clipLeftPx ?? leftPx;
        var clipT = clipTopPx ?? topPx;
        var clipW = clipWidthPx ?? widthPx;
        var clipH = clipHeightPx ?? heightPx;
        // An empty (or negative) clip rect paints nothing — a content-box clip on a box whose
        // border+padding meet/exceed its size collapses the paint window (post-PR-#171 review P1).
        // Bail before the pattern/loop paths so neither a zero-area pattern fill nor a degenerate
        // clip rectangle is emitted.
        if (clipW <= 0 || clipH <= 0) return;
        // Each unsupported longhand falls back to ITS initial WHOLE (a failed parse may have
        // half-assigned its outs — e.g. a valid first axis before an invalid second).
        var anyVariantUnsupported = false;
        if (!TryParseBackgroundRepeat(repeatRaw, out var modeX, out var modeY))
        {
            anyVariantUnsupported = true;
            modeX = modeY = BackgroundRepeatMode.Repeat;        // the initial: repeat.
        }
        if (!TryParseBackgroundSize(
                sizeRaw, widthPx, heightPx, entry.WidthPx, entry.HeightPx, out var tileW, out var tileH))
        {
            anyVariantUnsupported = true;
            tileW = entry.WidthPx;                              // the initial: auto (intrinsic).
            tileH = entry.HeightPx;
        }
        if (tileW <= 0 || tileH <= 0)
        {
            EmitVariantUnsupported(diagnostics, anyVariantUnsupported, ref variantUnsupportedReported);
            return;
        }
        // ROUND rescales the tile so a whole number fills its axis exactly (space-round cycle,
        // CSS B&B §3.2/§3.9 — the size adjustment happens BEFORE the position resolves against
        // the used tile size).
        if (modeX == BackgroundRepeatMode.Round)
            tileW = widthPx / Math.Max(1, Math.Round(widthPx / tileW));
        if (modeY == BackgroundRepeatMode.Round)
            tileH = heightPx / Math.Max(1, Math.Round(heightPx / tileH));
        if (!TryParseBackgroundPosition(
                positionRaw, widthPx, heightPx, tileW, tileH, out var posX, out var posY))
        {
            anyVariantUnsupported = true;
            posX = posY = 0;                                    // the initial: 0% 0%.
        }
        EmitVariantUnsupported(diagnostics, anyVariantUnsupported, ref variantUnsupportedReported);

        // Per-axis tiling plan (space-round cycle): the first tile's offset + count + the ORIGIN
        // STEP (= the tile size; tile + gap for `space`). `repeat` covers the background-clip
        // window (area-relative); space/round/no-repeat use the positioning area (review P1).
        var (firstX, nx, stepX) = AxisTilingPlan(modeX, widthPx, tileW, posX, clipL - leftPx, clipL + clipW - leftPx);
        var (firstY, ny, stepY) = AxisTilingPlan(modeY, heightPx, tileH, posY, clipT - topPx, clipT + clipH - topPx);
        if (nx <= 0 || ny <= 0) return;

        var imageRef = ImageResourceCache.GetOrRegister(document, entry);

        // ABOVE the loop threshold → ONE tiling-pattern fill (tiling-patterns cycle): the
        // pattern object carries the cell (the image stretched to the tile) + the grid phase
        // in its /Matrix (anchored at the (firstX, firstY) tile — pattern space is DEFAULT
        // user space, §8.7.3.1), and the fill RECT bounds the painted area, clamped per
        // NON-repeating axis to its single tile (a repeating axis spans the box). O(1)
        // content-stream size for any count — the old 4096-tile cap and its diagnostic are
        // retired. The compare stays overflow-safe (PR #166 review P1 — `nx * ny` wraps for
        // a 4e9 × 4e9 grid; per-axis bound first, then the division-based product bound).
        if (nx > MaxLoopTilesPerFragment
            || ny > MaxLoopTilesPerFragment
            || nx > MaxLoopTilesPerFragment / ny)
        {
            // The fill is the TILED extent (first tile → last tile's far edge) clamped to the
            // background-clip rect (review P1): a repeating axis now spans the clip [covering the
            // border strip], space/round span the positioning area, no-repeat the single tile.
            var fillLeftPx = Math.Max(clipL, leftPx + firstX);
            var fillRightPx = Math.Min(clipL + clipW, leftPx + firstX + (nx - 1) * stepX + tileW);
            var fillTopPx = Math.Max(clipT, topPx + firstY);
            var fillBottomPx = Math.Min(clipT + clipH, topPx + firstY + (ny - 1) * stepY + tileH);
            if (fillRightPx - fillLeftPx <= 0 || fillBottomPx - fillTopPx <= 0) return;
            // The anchor tile's PDF rect gives the pattern Matrix origin (its bottom-left)
            // + the tile size in pt; the ORIGIN STEPS convert with the same px→pt scale
            // (space-round cycle — a `space` gap rides /XStep //YStep; §8.7.3.1 allows steps
            // beyond the BBox). Any anchor congruent modulo the step reproduces the grid.
            ToPdfRect(
                leftPx + firstX, topPx + firstY, tileW, tileH, pageHeightPt,
                out var ax, out var ay, out var aw, out var ah);
            var patternRef = document.RegisterTilingPattern(
                imageRef, aw, ah, ax, ay,
                xStepPt: PdfUnits.PxToPt(stepX), yStepPt: PdfUnits.PxToPt(stepY));
            ToPdfRect(
                fillLeftPx, fillTopPx, fillRightPx - fillLeftPx, fillBottomPx - fillTopPx,
                pageHeightPt, out var fx, out var fy, out var fw, out var fh);
            // A border-radius rounds the pattern fill too (border-radius-completion cycle, Task 3):
            // wrap it in a rounded clip of the background-clip box. No radius → no clip wrap, so the
            // pattern path stays byte-identical.
            if (clipRadiiPx.AnyPositive)
            {
                ToPdfRect(clipL, clipT, clipW, clipH, pageHeightPt, out var pcx, out var pcy, out var pcw, out var pch);
                page.BeginRoundedRectangleClip(pcx, pcy, pcw, pch, ToPt(clipRadiiPx));
                page.FillRectangleWithPattern(patternRef, fx, fy, fw, fh);
                page.RestoreGraphicsState();
            }
            else
            {
                page.FillRectangleWithPattern(patternRef, fx, fy, fw, fh);
            }
            return;
        }
        // Clip partial / protruding tiles to the background-clip rect (bg-clip cycle; the initial
        // background-clip: border-box, which the body caller passes explicitly while the tiled area is
        // the background-origin box). A border-radius rounds this clip (border-radius-completion cycle,
        // Task 3); BeginRoundedRectangleClip falls back to the rectangular clip for zero radii, so the
        // common (no-radius) tile loop stays byte-identical.
        ToPdfRect(clipL, clipT, clipW, clipH, pageHeightPt, out var cx, out var cy, out var cw, out var ch);
        page.BeginRoundedRectangleClip(cx, cy, cw, ch, ToPt(clipRadiiPx));
        for (long row = 0; row < ny; row++)
        {
            for (long col = 0; col < nx; col++)
            {
                ToPdfRect(
                    leftPx + firstX + col * stepX, topPx + firstY + row * stepY, tileW, tileH,
                    pageHeightPt, out var x, out var y, out var w, out var h);
                page.PlaceImage(imageRef, x, y, w, h);
            }
        }
        page.RestoreGraphicsState();
    }

    private static void EmitVariantUnsupported(
        IDiagnosticsSink? diagnostics, bool anyVariantUnsupported, ref bool variantUnsupportedReported)
    {
        if (!anyVariantUnsupported || variantUnsupportedReported || diagnostics is null) return;
        diagnostics.Emit(new Diagnostic(
            DiagnosticCodes.CssBackgroundImageUnsupported001,
            "A background-repeat/-size/-position value outside the supported set "
            + "(repeat/no-repeat/repeat-x/repeat-y/space/round incl. the two-value axis form; "
            + "auto/contain/cover/<length|percentage>{1,2}; keyword/<length|percentage>{1,2}) "
            + "was ignored — that longhand fell back to its initial value.",
            DiagnosticSeverity.Warning));
        variantUnsupportedReported = true;
    }

    /// <summary>The per-side inset from the BORDER box for a background box-area keyword (bg-origin
    /// / bg-clip cycles): <c>border-box</c> → 0, <c>padding-box</c> → the used border widths,
    /// <c>content-box</c> → border + padding. An unset / unrecognized value uses
    /// <paramref name="defaultArea"/> — the property's initial (<c>'p'</c> padding-box for
    /// background-origin, <c>'b'</c> border-box for background-clip).</summary>
    internal static (double Top, double Right, double Bottom, double Left) BackgroundAreaInset(
        ComputedStyle style, string? raw, char defaultArea)
    {
        var area = raw?.Trim().ToLowerInvariant() switch
        {
            "border-box" => 'b',
            "padding-box" => 'p',
            "content-box" => 'c',
            _ => defaultArea,
        };
        if (area == 'b') return (0, 0, 0, 0);
        var bt = UsedBorderEdgeWidthPx(style, PropertyId.BorderTopStyle, PropertyId.BorderTopWidth);
        var br = UsedBorderEdgeWidthPx(style, PropertyId.BorderRightStyle, PropertyId.BorderRightWidth);
        var bb = UsedBorderEdgeWidthPx(style, PropertyId.BorderBottomStyle, PropertyId.BorderBottomWidth);
        var bl = UsedBorderEdgeWidthPx(style, PropertyId.BorderLeftStyle, PropertyId.BorderLeftWidth);
        if (area == 'p') return (bt, br, bb, bl);
        return (bt + style.ReadLengthPxOrZero(PropertyId.PaddingTop),
                br + style.ReadLengthPxOrZero(PropertyId.PaddingRight),
                bb + style.ReadLengthPxOrZero(PropertyId.PaddingBottom),
                bl + style.ReadLengthPxOrZero(PropertyId.PaddingLeft));
    }

    /// <summary>The USED width of a border edge — the gated value the border painter uses: 0 when
    /// the edge's style is unset / <c>none</c> / <c>hidden</c>, else its <c>border-*-width</c> px.
    /// The background origin/clip inset must match what actually paints (bg-origin / bg-clip).</summary>
    private static double UsedBorderEdgeWidthPx(ComputedStyle style, PropertyId styleId, PropertyId widthId)
    {
        var styleSlot = style.Get(styleId);
        if (styleSlot.Tag != ComputedSlotTag.Keyword) return 0;     // unset → initial `none`
        var kw = styleSlot.AsKeyword();
        if (kw is BorderStyleNone or BorderStyleHidden) return 0;
        var widthSlot = style.Get(widthId);
        return widthSlot.Tag == ComputedSlotTag.LengthPx ? Math.Max(0, widthSlot.AsLengthPx()) : 0;
    }

    /// <summary>A per-axis <c>background-repeat</c> mode (bg-variants + space-round cycles,
    /// CSS B&amp;B §3.2).</summary>
    internal enum BackgroundRepeatMode
    {
        Repeat,
        NoRepeat,
        Space,   // floor(area/tile) tiles, the leftover distributed as equal gaps.
        Round,   // the tile rescales so a whole number fills the area exactly.
    }

    /// <summary>Parse <c>background-repeat</c> (bg-variants + space-round cycles): the single
    /// keywords (<c>repeat</c> / <c>no-repeat</c> / <c>repeat-x</c> / <c>repeat-y</c> /
    /// <c>space</c> / <c>round</c>) + the two-value per-axis form. Unknown →
    /// <see langword="false"/> (the caller falls back to <c>repeat</c> + surfaces once).
    /// Null/empty = the initial.</summary>
    internal static bool TryParseBackgroundRepeat(
        string? raw, out BackgroundRepeatMode repeatX, out BackgroundRepeatMode repeatY)
    {
        repeatX = BackgroundRepeatMode.Repeat;
        repeatY = BackgroundRepeatMode.Repeat;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var parts = raw.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            switch (parts[0])
            {
                case "repeat": return true;
                case "no-repeat": repeatX = repeatY = BackgroundRepeatMode.NoRepeat; return true;
                case "repeat-x": repeatY = BackgroundRepeatMode.NoRepeat; return true;
                case "repeat-y": repeatX = BackgroundRepeatMode.NoRepeat; return true;
                case "space": repeatX = repeatY = BackgroundRepeatMode.Space; return true;
                case "round": repeatX = repeatY = BackgroundRepeatMode.Round; return true;
                default: return false;
            }
        }
        return parts.Length == 2
            && TryAxisRepeat(parts[0], out repeatX)
            && TryAxisRepeat(parts[1], out repeatY);
    }

    private static bool TryAxisRepeat(string token, out BackgroundRepeatMode mode)
    {
        switch (token)
        {
            case "repeat": mode = BackgroundRepeatMode.Repeat; return true;
            case "no-repeat": mode = BackgroundRepeatMode.NoRepeat; return true;
            case "space": mode = BackgroundRepeatMode.Space; return true;
            case "round": mode = BackgroundRepeatMode.Round; return true;
            default: mode = BackgroundRepeatMode.Repeat; return false;
        }
    }

    /// <summary>One axis's tiling plan (space-round cycle; bg-clip coverage — PR #170 review P1):
    /// the first tile's offset (positioning-area-relative), the tile count, and the step between
    /// tile ORIGINS (= the tile size for repeat; tile + gap for <c>space</c>). <c>repeat</c> tiles
    /// the PAINTING window <c>[coverStartPx, coverEndPx]</c> (the background-clip rect, area-relative)
    /// with the grid PHASED at <paramref name="posPx"/> in the positioning area — so a padding-box
    /// origin under a border-box clip still tiles the border strip; <c>space</c>/<c>round</c> fill
    /// the positioning AREA (CSS B&amp;B §3.6.1 — they ignore the wider clip), <c>no-repeat</c>
    /// places one positioned tile. INTERNAL so the unit tests pin the count/gap/coverage math.</summary>
    internal static (double First, long Count, double Step) AxisTilingPlan(
        BackgroundRepeatMode mode, double areaPx, double tilePx, double posPx,
        double coverStartPx, double coverEndPx)
    {
        switch (mode)
        {
            case BackgroundRepeatMode.NoRepeat:
                return (posPx, 1, tilePx);
            case BackgroundRepeatMode.Space:
            {
                // §3.6.1: floor(area/tile) whole tiles in the POSITIONING area; the leftover
                // becomes equal gaps between them (first/last flush with the area edges); 0–1
                // tiles degenerate to a single positioned tile.
                var n = (long)Math.Floor(areaPx / tilePx);
                if (n <= 1) return (posPx, 1, tilePx);
                var gap = (areaPx - n * tilePx) / (n - 1);
                return (0, n, tilePx + gap);
            }
            case BackgroundRepeatMode.Round:
                // The caller already rescaled the tile so a whole number fits the positioning
                // area exactly — tiles run edge-to-edge from the area start.
                return (0, Math.Max(1, (long)Math.Round(areaPx / tilePx)), tilePx);
            default:
            {
                // `repeat` covers the PAINTING (clip) window, the grid phased at posPx in the
                // positioning area — the first tile ≤ coverStart, the last ≥ coverEnd (review P1).
                var k = (long)Math.Floor((coverStartPx - posPx) / tilePx);
                var first = posPx + k * tilePx;
                var count = (long)Math.Ceiling((coverEndPx - first) / tilePx);
                return (first, Math.Max(0, count), tilePx);
            }
        }
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
        // Sizes are NON-NEGATIVE (CSS B&B §3.9 — a negative background-size is invalid; the
        // pre-fix accepted -10% / -10px, whose ≤ 0 tile silently skipped painting instead of
        // falling back with the diagnostic — PR #167 review P2).
        if (token.EndsWith('%')
            && double.TryParse(token[..^1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pct)
            && double.IsFinite(pct))
        {
            if (pct < 0) return false;
            px = areaPx * pct / 100.0;
            return true;
        }
        if (LengthResolver.TrySplitNumberAndUnit(token, out var n, out var unit))
        {
            // The unitless ZERO is a valid length (CSS Values §6.2) — `background-size: 0`
            // parses to a zero tile (which paints nothing), it is NOT an unsupported form
            // (PR #167 review P2).
            if (unit.Length == 0 && n == 0.0) return true;
            if (unit.Length > 0
                && LengthResolver.TryAbsoluteUnitToPx(unit.ToLowerInvariant(), n, out px)
                && double.IsFinite(px)
                && px >= 0)
            {
                return true;
            }
        }
        px = 0;
        return false;
    }

    /// <summary>Parse <c>background-position</c> (bg-variants cycle): per-axis keywords
    /// (<c>left</c>/<c>center</c>/<c>right</c>, <c>top</c>/<c>center</c>/<c>bottom</c> — a
    /// swapped keyword pair like <c>top left</c> is accepted), <c>&lt;length&gt;</c> (absolute),
    /// or <c>&lt;percentage&gt;</c> per the CSS B&amp;B §3.6 rule (<c>x%</c> aligns the image's
    /// x% point with the area's: offset = (area − tile) × x%). ONE value → the other axis
    /// centers. The THREE-/FOUR-value edge-offset form (<c>left 10px top 5px</c> — an offset FROM
    /// a named edge; edge-offset cycle) is supported; non-absolute units → <see langword="false"/>
    /// (fall back to 0% 0% + surface once). A math-function component (<c>calc(50% - 10px)</c>, …) is
    /// kept whole by the PAREN-AWARE tokenizer and evaluated against the §3.6 range (post-PR-#183 review
    /// P3); a font-/viewport-relative math function has no context here and falls back. Reused for
    /// <c>object-position</c> too.</summary>
    internal static bool TryParseBackgroundPosition(
        string? raw, double areaW, double areaH, double tileW, double tileH,
        out double posX, out double posY)
    {
        posX = 0;
        posY = 0;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        // Paren-aware tokenization (post-PR-#183 review P3): split on whitespace at paren depth 0 so a
        // math function (`calc(50% - 10px)`) stays one component instead of fragmenting into broken
        // tokens. Unbalanced parens → fall back. Lowercase PER token (a function body is case-handled
        // downstream) to keep the keyword matching below intact.
        if (!CssShorthandHelpers.SplitTopLevel(raw.Trim(), out var tokenList)) return false;
        if (tokenList.Count is < 1 or > 4) return false;
        var parts = new string[tokenList.Count];
        for (var t = 0; t < tokenList.Count; t++) parts[t] = tokenList[t].ToLowerInvariant();
        // The 3-/4-value edge-offset form (edge-offset cycle, CSS B&B §3.6): an edge keyword each
        // optionally followed by a <length-percentage> offset FROM that edge.
        if (parts.Length >= 3)
            return TryParseEdgeOffsetPosition(parts, areaW, areaH, tileW, tileH, out posX, out posY);
        string xTok, yTok;
        if (parts.Length == 1)
        {
            // ONE value: the other axis centers (§3.6). A single VERTICAL keyword belongs to
            // the Y axis — `background-position: top` means `center top` (PR #167 review P2;
            // the pre-fix assigned it to X and rejected, falling back to 0% 0%).
            if (parts[0] is "top" or "bottom")
            {
                xTok = "center";
                yTok = parts[0];
            }
            else
            {
                xTok = parts[0];
                yTok = "center";
            }
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
        if (TryEvalPositionMath(token, areaPx - tilePx, out pos)) return true;
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

    /// <summary>Evaluate a math-function position component (post-PR-#183 review P3): a
    /// <c>calc()</c>/<c>min()</c>/<c>max()</c>/<c>clamp()</c>/… offset whose PERCENTAGE base is the §3.6
    /// range <paramref name="rangePx"/> (area − tile), so <c>calc(50% - 10px)</c> evaluates to the same
    /// used offset a bare <c>50%</c> minus <c>10px</c> would. Returns <see langword="false"/> for a
    /// non-math token (so the caller's length path runs) AND for a math function whose only resolvable
    /// form needs FONT-/VIEWPORT-relative context — <c>em</c>/<c>vw</c>/… aren't threaded into this
    /// static helper, so those poison to NaN and fall back to the 0% 0% default + the "unsupported"
    /// diagnostic (a documented limitation; the common %/absolute calc renders exactly). Offsets may be
    /// negative, so the §10.5 non-negative clamp is OFF.</summary>
    private static bool TryEvalPositionMath(string token, double rangePx, out double pos)
    {
        pos = 0;
        if (!CalcLengthEvaluator.IsMathFunction(token)) return false;
        var ctx = new CalcLengthEvaluator.CalcContext(
            PercentBasePx: rangePx, EmPx: double.NaN, RootEmPx: double.NaN,
            ViewportWidthPx: double.NaN, ViewportHeightPx: double.NaN);
        if (CalcLengthEvaluator.TryEvaluate(token, ctx, clampNonNegative: false, out var px)
            && double.IsFinite(px))
        {
            pos = px;
            return true;
        }
        pos = 0;
        return false;
    }

    /// <summary>The §3.6 THREE-/FOUR-value edge-offset position form (edge-offset cycle): two
    /// components, each an edge keyword (<c>left</c>/<c>right</c>/<c>top</c>/<c>bottom</c>/
    /// <c>center</c>) optionally followed by a <c>&lt;length-percentage&gt;</c> offset FROM that
    /// edge. Components are assigned to axes by keyword (left/right → X, top/bottom → Y, center →
    /// the free axis), in either order; two same-axis edges or a leftover token reject.</summary>
    private static bool TryParseEdgeOffsetPosition(
        string[] parts, double areaW, double areaH, double tileW, double tileH,
        out double posX, out double posY)
    {
        posX = 0;
        posY = 0;
        // Parse exactly two (edge, offset?) components, consuming EVERY token.
        var i = 0;
        var e0 = parts[i++];
        var o0 = i < parts.Length && IsOffsetToken(parts[i]) ? parts[i++] : null;
        if (i >= parts.Length) return false;
        var e1 = parts[i++];
        var o1 = i < parts.Length && IsOffsetToken(parts[i]) ? parts[i++] : null;
        if (i != parts.Length) return false;   // a leftover token → invalid

        var a0 = EdgeAxis(e0);
        var a1 = EdgeAxis(e1);
        if (a0 == '\0' || a1 == '\0') return false;       // a non-edge keyword / bad token
        // Resolve which component is X vs Y (a `center` floats to whichever axis the other isn't).
        int xc;
        if (a0 == 'x' && a1 != 'x') xc = 0;
        else if (a1 == 'x' && a0 != 'x') xc = 1;
        else if (a0 == 'y' && a1 != 'y') xc = 1;          // comp0 is Y → comp1 is X
        else if (a1 == 'y' && a0 != 'y') xc = 0;
        else if (a0 == 'c' && a1 == 'c') xc = 0;          // both center → arbitrary
        else return false;                                // two same-axis edges
        var (xEdge, xOff) = xc == 0 ? (e0, o0) : (e1, o1);
        var (yEdge, yOff) = xc == 0 ? (e1, o1) : (e0, o0);
        return TryEdgeOffsetComponent(xEdge, xOff, areaW, tileW, out posX)
            && TryEdgeOffsetComponent(yEdge, yOff, areaH, tileH, out posY);
    }

    /// <summary>The axis an edge keyword belongs to: <c>x</c> (left/right), <c>y</c> (top/bottom),
    /// <c>c</c> (center — the free axis), or <c>\0</c> (not an edge keyword).</summary>
    private static char EdgeAxis(string token) => token switch
    {
        "left" or "right" => 'x',
        "top" or "bottom" => 'y',
        "center" => 'c',
        _ => '\0',
    };

    /// <summary>True for a token that is an OFFSET (a <c>&lt;length-percentage&gt;</c>) rather than
    /// an edge keyword — so the parser knows whether the token after an edge belongs to it.</summary>
    private static bool IsOffsetToken(string token) =>
        EdgeAxis(token) == '\0'
        && (token.EndsWith('%') || CalcLengthEvaluator.IsMathFunction(token)
            || LengthResolver.TrySplitNumberAndUnit(token, out _, out _));

    /// <summary>One edge-offset component's tile-origin offset: <c>left</c>/<c>top</c> measure FROM
    /// the start edge (0 + offset), <c>right</c>/<c>bottom</c> from the END edge (range − offset),
    /// <c>center</c> is the midpoint and takes no offset; a percentage offset is of the §3.6 range
    /// (area − tile).</summary>
    private static bool TryEdgeOffsetComponent(
        string edge, string? offset, double areaPx, double tilePx, out double pos)
    {
        pos = 0;
        var range = areaPx - tilePx;
        if (edge == "center")
        {
            if (offset is not null) return false;   // `center` takes no offset
            pos = range * 0.5;
            return true;
        }
        var off = 0.0;
        if (offset is not null && !TryOffsetValuePx(offset, range, out off)) return false;
        pos = edge is "left" or "top" ? off : range - off;   // start edge vs end edge
        return true;
    }

    /// <summary>An edge offset's px value: a percentage is of the §3.6 <paramref name="rangePx"/>
    /// (area − tile), an absolute length is its px, the unitless zero is 0; a non-absolute unit →
    /// <see langword="false"/>.</summary>
    private static bool TryOffsetValuePx(string token, double rangePx, out double px)
    {
        px = 0;
        if (token.EndsWith('%')
            && double.TryParse(token[..^1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pct)
            && double.IsFinite(pct))
        {
            px = rangePx * pct / 100.0;
            return true;
        }
        if (TryEvalPositionMath(token, rangePx, out px)) return true;   // a calc() edge offset (review P3)
        if (LengthResolver.TrySplitNumberAndUnit(token, out var n, out var unit))
        {
            if (unit.Length == 0 && n == 0.0) return true;   // the unitless zero
            if (unit.Length > 0
                && LengthResolver.TryAbsoluteUnitToPx(unit.ToLowerInvariant(), n, out px)
                && double.IsFinite(px))
            {
                return true;
            }
        }
        px = 0;
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
        uint currentColorArgb, IDiagnosticsSink? diagnostics, ref bool styleApproximationReported,
        bool suppressTopEdge = false, bool suppressBottomEdge = false,
        double decorationBlockExtentPx = 0.0, double decorationBlockOffsetPx = 0.0) =>
        PaintBorders(page, style, pageHeightPt, leftPx, topPx, widthPx, heightPx,
            BorderEdgeCurrentColors.Uniform(currentColorArgb), diagnostics, ref styleApproximationReported,
            suppressTopEdge, suppressBottomEdge, decorationBlockExtentPx, decorationBlockOffsetPx);

    /// <summary>Per-edge-currentcolor overload (per-edge currentcolor cycle): each edge's
    /// <c>border-*-color: currentcolor</c> (or its initial) falls back to that edge's OWNER colour
    /// from <paramref name="currentColors"/>. The uniform overload delegates here, so the body path
    /// is byte-identical.</summary>
    internal static void PaintBorders(
        PdfPage page, ComputedStyle style, double pageHeightPt,
        double leftPx, double topPx, double widthPx, double heightPx,
        in BorderEdgeCurrentColors currentColors, IDiagnosticsSink? diagnostics, ref bool styleApproximationReported,
        // box-decoration-break: slice (inline-only line splitting) — skip the block-start (top) / block-end
        // (bottom) border edge on a fragmentation CUT (the SQUARE per-edge path), so a sliced bordered block
        // draws each block-axis border once (top on the first slice, bottom on the last) instead of boxing
        // every slice.
        bool suppressTopEdge = false, bool suppressBottomEdge = false,
        // box-decoration-break: slice (PR #223 review [P1]) — when > 0 this is one block-axis slice; a ROUNDED
        // border is rendered as the WHOLE composite box's ring / clipped edges (this extent, virtual top =
        // topPx − the offset) clipped to this slice, so a percentage / oversized radius rounds across cuts.
        double decorationBlockExtentPx = 0.0, double decorationBlockOffsetPx = 0.0)
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

        // box-decoration-break: slice — a ROUNDED border is the WHOLE composite box's ring (the unbroken
        // geometry) clipped to this slice; the slice clip cuts off the edges / corners that belong to other
        // fragments, so no per-edge suppression is needed in the rounded paths. The radii resolve against
        // the COMPOSITE height. A non-slice fragment is the box itself (byte-identical).
        var (compTopPx, compHeightPx, radiiPx) = CompositeRoundedBox(
            style, topPx, widthPx, heightPx, decorationBlockExtentPx, decorationBlockOffsetPx);
        var sliced = decorationBlockExtentPx > 0;

        // Rounded UNIFORM border (border-radius-completion cycle, Task 2): a box with a border-radius
        // AND a uniform border (same paintable style, width, and resolved colour on all four edges) paints
        // ONE filled RING — the annulus between the border box (outer, the border-box radii) and the
        // padding box (inner, radii reduced by the FULL border width) — so the border follows the fill's
        // rounded corners. A FILLED ring (not a centerline stroke): its outer corner is EXACT for any
        // border width (a small radius under a thick border keeps its rounding; post-PR-#172 review P1+P2)
        // and it composites the border colour's alpha correctly (a fill, /ca). A non-uniform border falls
        // through to the per-edge rects below (CLIPPED to the rounded outline when there's a radius).
        if (radiiPx.AnyPositive
            && TryUniformBorder(style, currentColors, out var borderWidthPx, out var borderArgb, out var nonSolid, out var uniformStyleKw))
        {
            // Faithful dashed / dotted / double now render on the rounded ring; only the 3D styles
            // (groove / ridge / inset / outset) stay approximated as a solid ring — their per-SIDE light/dark
            // shading can't be expressed by a concentric rounded ring (a documented residual).
            var threeD = uniformStyleKw is BorderStyleGroove or BorderStyleRidge or BorderStyleInset or BorderStyleOutset;
            if (threeD && nonSolid && !styleApproximationReported)
            {
                diagnostics?.Emit(new Diagnostic(
                    DiagnosticCodes.PaintBorderStyleApproximated001,
                    "A 3D rounded border-style (groove / ridge / inset / outset) was painted as a solid ring; " +
                    "per-side bevel shading on rounded corners is a tracked follow-up " +
                    "(deferrals.md#layout-to-pdf-pipeline).",
                    DiagnosticSeverity.Info));
                styleApproximationReported = true;
            }
            // Outer = the COMPOSITE border box with its radii; inner = the padding box (inset by the border
            // width on every side) with radii reduced by the full border width. Clip to this slice (the
            // closed ring's top / bottom edge for another fragment is cut by the slice rect).
            var outerRadiiPx = radiiPx.NormalizedFor(widthPx, compHeightPx);
            var innerRadiiPx = ReduceRadii(outerRadiiPx, borderWidthPx);
            var uniformClipped = BeginSliceClip(page, sliced, pageHeightPt, leftPx, topPx, widthPx, heightPx);
            ColorChannels(borderArgb, out var cr, out var cg, out var cb);
            var ca = Alpha(borderArgb) / 255.0;
            // Outer = the composite border box; inner = the padding box (inset by the full border width).
            PaintStyledRing(page, BorderRingStyle(uniformStyleKw), pageHeightPt,
                leftPx, compTopPx, widthPx, compHeightPx, outerRadiiPx,
                leftPx + borderWidthPx, compTopPx + borderWidthPx, widthPx - 2 * borderWidthPx, compHeightPx - 2 * borderWidthPx, innerRadiiPx,
                borderWidthPx, cr, cg, cb, ca);
            if (uniformClipped) page.RestoreGraphicsState();
            return;
        }

        // Rounded NON-uniform border (rounded-nonuniform-borders cycle): a border-radius with
        // per-edge-differing widths / styles / colours can't use the single uniform RING, so the four
        // square edge rects are CLIPPED to the rounded BORDER-box outline — the box's OUTER corners follow
        // the radius (matching the rounded background band + image clip) instead of poking out square. The
        // per-edge widths / colours, the (still-square) INNER corners, and the hard colour transition where
        // two differently-coloured edges meet at a corner stay an approximation. Zero radii → no clip
        // (byte-identical to the prior square edges).
        // box-decoration-break: slice — for a rounded border the per-edge rects + the rounded-outline clip
        // are the WHOLE composite box, wrapped in a per-slice rect clip; the slice clip removes the edges /
        // corners of other fragments, so no edge suppression is needed (that is the SQUARE path's job).
        var rounded = radiiPx.AnyPositive;
        var edgeTopPx = rounded ? compTopPx : topPx;
        var edgeHeightPx = rounded ? compHeightPx : heightPx;
        var sliceClipped = rounded && BeginSliceClip(page, sliced, pageHeightPt, leftPx, topPx, widthPx, heightPx);
        if (rounded)
        {
            ToPdfRect(leftPx, compTopPx, widthPx, compHeightPx, pageHeightPt, out var bx, out var by, out var bw, out var bh);
            page.BeginRoundedRectangleClip(bx, by, bw, bh, ToPt(radiiPx));
            // A ROUNDED border reaches this per-edge fallback only when it ISN'T uniform (per-edge-differing
            // width / colour / style). A non-solid style here is approximated — the dashes / dots are STRAIGHT
            // strokes clipped to the rounded outline (cut at the corners) and double / 3D keep SQUARE inner
            // corners. Diagnose it (the uniform rounded ring path renders dashed/dotted/double faithfully).
            if (!styleApproximationReported && AnyNonSolidBorderEdge(style))
            {
                diagnostics?.Emit(new Diagnostic(
                    DiagnosticCodes.PaintBorderStyleApproximated001,
                    "A rounded border with non-uniform (per-edge-differing) non-solid styles was painted via " +
                    "the clipped per-edge path — straight dashes clipped to the rounded outline + square inner " +
                    "corners. A UNIFORM rounded dashed/dotted/double border renders faithfully; this mix is a " +
                    "tracked follow-up (deferrals.md#layout-to-pdf-pipeline).",
                    DiagnosticSeverity.Info));
                styleApproximationReported = true;
            }
        }
        // The SQUARE path keeps the per-slice rects + cut-edge suppression (byte-identical, #221); the
        // ROUNDED path draws all four composite edges (the slice clip cuts the ones off-fragment).
        if (rounded || !suppressTopEdge)
            PaintBorderEdge(page, style, pageHeightPt, BorderEdge.Top, leftPx, edgeTopPx, widthPx, edgeHeightPx,
                currentColors.Top, diagnostics, ref styleApproximationReported);
        PaintBorderEdge(page, style, pageHeightPt, BorderEdge.Right, leftPx, edgeTopPx, widthPx, edgeHeightPx,
            currentColors.Right, diagnostics, ref styleApproximationReported);
        if (rounded || !suppressBottomEdge)
            PaintBorderEdge(page, style, pageHeightPt, BorderEdge.Bottom, leftPx, edgeTopPx, widthPx, edgeHeightPx,
                currentColors.Bottom, diagnostics, ref styleApproximationReported);
        PaintBorderEdge(page, style, pageHeightPt, BorderEdge.Left, leftPx, edgeTopPx, widthPx, edgeHeightPx,
            currentColors.Left, diagnostics, ref styleApproximationReported);
        if (rounded) page.RestoreGraphicsState();         // rounded-outline clip
        if (sliceClipped) page.RestoreGraphicsState();    // per-slice rect clip
    }

    /// <summary>Phase 4 border-image (PR 4) — paint a decoded <c>border-image</c>: slice the source image
    /// into the 9 regions (4 corners, 4 edges, optional center) and place each scaled into its destination
    /// region of the border area (CSS B&amp;B L3 §6). Corners scale source→dest; edges + center STRETCH this
    /// first cut (non-stretch <c>border-image-repeat</c> + <c>border-image-width</c>/<c>-outset</c> are
    /// approximated, diagnosed once). The border widths come from the element's used border widths; an
    /// all-zero border area paints nothing.</summary>
    private static void PaintBorderImage(
        PdfPage page, PdfDocument document, ImageResourceCache.Entry entry, CssBorderImage spec,
        ComputedStyle style, double pageHeightPt,
        double leftPx, double topPx, double widthPx, double heightPx)
    {
        var bt = UsedBorderEdgeWidthPx(style, PropertyId.BorderTopStyle, PropertyId.BorderTopWidth);
        var br = UsedBorderEdgeWidthPx(style, PropertyId.BorderRightStyle, PropertyId.BorderRightWidth);
        var bb = UsedBorderEdgeWidthPx(style, PropertyId.BorderBottomStyle, PropertyId.BorderBottomWidth);
        var bl = UsedBorderEdgeWidthPx(style, PropertyId.BorderLeftStyle, PropertyId.BorderLeftWidth);

        // Slice offsets → image fractions (negative sentinel = an image-pixel offset, resolved here).
        var sl = SliceFrac(spec.SliceLeftFrac, entry.WidthPx);
        var sr = SliceFrac(spec.SliceRightFrac, entry.WidthPx);
        var st = SliceFrac(spec.SliceTopFrac, entry.HeightPx);
        var sb = SliceFrac(spec.SliceBottomFrac, entry.HeightPx);

        // border-image-outset (§6.5) grows the border-image AREA outward from the border box; the 9-grid is
        // laid out in this area. border-image-width (§6.4) sets the DEST thickness of each edge/corner band
        // (default = the element's border width). These resolve INDEPENDENTLY of the CSS border widths, so a
        // box with zero borders but an explicit border-image-width / -outset still paints (PR-232 review [P2]).
        var ot = ResolveOutset(spec.OutsetTop, bt);
        var or = ResolveOutset(spec.OutsetRight, br);
        var ob = ResolveOutset(spec.OutsetBottom, bb);
        var ol = ResolveOutset(spec.OutsetLeft, bl);
        var areaLeft = leftPx - ol; var areaTop = topPx - ot;
        var areaW = widthPx + ol + or; var areaH = heightPx + ot + ob;
        if (!(areaW > 0) || !(areaH > 0)) return;

        // Intrinsic slice sizes (px) for border-image-width: auto.
        var wt = ResolveWidth(spec.WidthTop, bt, areaH, st * entry.HeightPx);
        var wr = ResolveWidth(spec.WidthRight, br, areaW, sr * entry.WidthPx);
        var wb = ResolveWidth(spec.WidthBottom, bb, areaH, sb * entry.HeightPx);
        var wl = ResolveWidth(spec.WidthLeft, bl, areaW, sl * entry.WidthPx);
        // The dest widths may not exceed the area (CSS B&B §6.4): if top+bottom > area height OR left+right
        // > area width, reduce ALL FOUR by the SAME global factor f = min(areaW/(wl+wr), areaH/(wt+wb), 1) so
        // the corners keep their aspect ratio (PR-232 review [P1] — a per-axis clamp distorted corners).
        var f = 1.0;
        if (wl + wr > 0) f = Math.Min(f, areaW / (wl + wr));
        if (wt + wb > 0) f = Math.Min(f, areaH / (wt + wb));
        if (f < 1.0 && f > 0) { wt *= f; wr *= f; wb *= f; wl *= f; }

        // Nothing to paint when every dest band is zero and there's no center fill (e.g. zero borders +
        // default border-image-width:1 + no outset) — skip before registering the image XObject.
        if (!(wt > 0 || wr > 0 || wb > 0 || wl > 0) && !spec.Fill) return;

        var imageRef = ImageResourceCache.GetOrRegister(document, entry);
        var innerW = areaW - wl - wr;
        var innerH = areaH - wt - wb;
        var ix0 = sl; var ix1 = 1 - sr;   // inner source x band (between the left + right slices)
        var iy0 = st; var iy1 = 1 - sb;   // inner source y band (between the top + bottom slices)

        void Region(double dxPx, double dyPx, double dwPx, double dhPx,
            double sx0, double sy0, double sx1, double sy1)
        {
            if (!(dwPx > 0) || !(dhPx > 0) || !(sx1 > sx0) || !(sy1 > sy0)) return;
            ToPdfRect(dxPx, dyPx, dwPx, dhPx, pageHeightPt, out var x, out var y, out var w, out var h);
            page.PlaceImageSlice(imageRef, x, y, w, h, sx0, sy0, sx1, sy1);
        }

        var rightDx = areaLeft + areaW - wr;
        var bottomDy = areaTop + areaH - wb;
        // Corners (source corner → dest corner, scaled — always "stretch").
        Region(areaLeft, areaTop, wl, wt, 0, 0, sl, st);                 // top-left
        Region(rightDx, areaTop, wr, wt, 1 - sr, 0, 1, st);            // top-right
        Region(areaLeft, bottomDy, wl, wb, 0, 1 - sb, sl, 1);          // bottom-left
        Region(rightDx, bottomDy, wr, wb, 1 - sr, 1 - sb, 1, 1);       // bottom-right

        // Edges — tiled per border-image-repeat (repeat / round / space; stretch = one tile filling the edge).
        // Each tile reuses the full edge SOURCE band; only the dest position/size along the edge changes.
        var srcTopH = st * entry.HeightPx; var srcBotH = sb * entry.HeightPx;     // source slice thickness, px
        var srcLeftW = sl * entry.WidthPx; var srcRightW = sr * entry.WidthPx;
        var srcMidW = (ix1 - ix0) * entry.WidthPx;                                // source center-band length, px
        var srcMidH = (iy1 - iy0) * entry.HeightPx;
        // Top / bottom edges tile along X (RepeatX); their natural tile length scales the center-band width
        // so the slice thickness fits the dest edge thickness.
        TileEdge(Region, horizontal: true, areaLeft + wl, innerW, areaTop, wt, ix0, 0, ix1, st,
            NaturalTile(srcMidW, srcTopH, wt), spec.RepeatX, page, pageHeightPt);
        TileEdge(Region, horizontal: true, areaLeft + wl, innerW, bottomDy, wb, ix0, 1 - sb, ix1, 1,
            NaturalTile(srcMidW, srcBotH, wb), spec.RepeatX, page, pageHeightPt);
        // Left / right edges tile along Y (RepeatY).
        TileEdge(Region, horizontal: false, areaTop + wt, innerH, areaLeft, wl, 0, iy0, sl, iy1,
            NaturalTile(srcMidH, srcLeftW, wl), spec.RepeatY, page, pageHeightPt);
        TileEdge(Region, horizontal: false, areaTop + wt, innerH, rightDx, wr, 1 - sr, iy0, 1, iy1,
            NaturalTile(srcMidH, srcRightW, wr), spec.RepeatY, page, pageHeightPt);

        // Center (only with the `fill` keyword) — stretched (center tiling is a minor residual).
        if (spec.Fill)
            Region(areaLeft + wl, areaTop + wt, innerW, innerH, ix0, iy0, ix1, iy1);
    }

    /// <summary>The natural dest length of one edge tile: the source band's parallel extent scaled so its
    /// perpendicular extent matches the dest edge thickness (CSS B&amp;B §6.3). Returns 0 when the source
    /// band is degenerate (→ the caller stretches instead of tiling).</summary>
    private static double NaturalTile(double srcParallelPx, double srcPerpPx, double destPerpPx) =>
        srcPerpPx > 0 && srcParallelPx > 0 && destPerpPx > 0 ? srcParallelPx * (destPerpPx / srcPerpPx) : 0;

    /// <summary>Lay one border-image edge: tile the source band along the edge per <paramref name="mode"/>.
    /// <c>stretch</c> = one tile filling the edge; <c>repeat</c> = whole tiles at natural size, centered and
    /// clipped to the edge; <c>round</c> = the tile is scaled so a whole number fit exactly; <c>space</c> =
    /// whole tiles at natural size with equal gaps. <paramref name="horizontal"/> selects the parallel axis
    /// (X for top/bottom, Y for left/right).</summary>
    private static void TileEdge(
        Action<double, double, double, double, double, double, double, double> region,
        bool horizontal, double edgeStart, double edgeLen, double perpStart, double perpThick,
        double sx0, double sy0, double sx1, double sy1, double naturalTile, BorderImageRepeat mode,
        PdfPage page, double pageHeightPt)
    {
        if (!(edgeLen > 0) || !(perpThick > 0)) return;

        void Place(double pos, double len)
        {
            if (horizontal) region(pos, perpStart, len, perpThick, sx0, sy0, sx1, sy1);
            else region(perpStart, pos, perpThick, len, sx0, sy0, sx1, sy1);
        }

        // Stretch (or a degenerate natural size) → a single tile filling the whole edge.
        if (mode == BorderImageRepeat.Stretch || !(naturalTile > 0)) { Place(edgeStart, edgeLen); return; }

        switch (mode)
        {
            case BorderImageRepeat.Round:
            {
                var n = Math.Max(1, (int)Math.Round(edgeLen / naturalTile));
                var tile = edgeLen / n;
                for (var i = 0; i < n; i++) Place(edgeStart + i * tile, tile);
                break;
            }
            case BorderImageRepeat.Space:
            {
                var n = (int)Math.Floor(edgeLen / naturalTile);
                if (n < 1) return; // not even one whole tile fits → paint nothing (§6.3)
                var gap = (edgeLen - n * naturalTile) / (n + 1);
                for (var i = 0; i < n; i++) Place(edgeStart + gap * (i + 1) + naturalTile * i, naturalTile);
                break;
            }
            default: // Repeat — whole tiles at natural size, centered, overflow clipped to the edge.
            {
                var n = Math.Max(1, (int)Math.Ceiling(edgeLen / naturalTile));
                var start = edgeStart + (edgeLen - n * naturalTile) / 2.0; // centered (may overhang both ends)
                ToPdfRect(horizontal ? edgeStart : perpStart, horizontal ? perpStart : edgeStart,
                    horizontal ? edgeLen : perpThick, horizontal ? perpThick : edgeLen, pageHeightPt,
                    out var cx, out var cy, out var cw, out var ch);
                page.BeginRectangleClip(cx, cy, cw, ch);
                for (var i = 0; i < n; i++) Place(start + i * naturalTile, naturalTile);
                page.RestoreGraphicsState();
                break;
            }
        }
    }

    /// <summary>Resolve a <c>border-image-width</c> component to a dest px thickness.</summary>
    private static double ResolveWidth(BorderImageLen w, double borderWidthPx, double areaSizePx, double intrinsicSlicePx) => w.Kind switch
    {
        BorderImageLenKind.Auto => intrinsicSlicePx,
        BorderImageLenKind.LengthPx => w.Value,
        BorderImageLenKind.Percent => w.Value * areaSizePx,
        _ => w.Value * borderWidthPx, // Multiple
    };

    /// <summary>Resolve a <c>border-image-outset</c> component to a dest px length.</summary>
    private static double ResolveOutset(BorderImageLen o, double borderWidthPx) => o.Kind switch
    {
        BorderImageLenKind.LengthPx => o.Value,
        _ => o.Value * borderWidthPx, // Multiple (auto/percent are invalid for outset → parsed as the default)
    };

    /// <summary>Resolve a border-image-slice offset (CssBorderImage's encoding) to an image fraction in
    /// [0, 1]: a non-negative value is already a fraction (a <c>%</c> slice); a negative sentinel
    /// <c>-(px + 1)</c> is an image-pixel slice resolved against <paramref name="imageDimPx"/>.</summary>
    private static double SliceFrac(double encoded, double imageDimPx)
    {
        var frac = encoded >= 0 ? encoded : (imageDimPx > 0 ? (-encoded - 1.0) / imageDimPx : 0);
        return Math.Clamp(frac, 0, 1);
    }

    /// <summary>box-decoration-break: slice — push a per-slice rectangular clip (the fragment's own border
    /// box) when <paramref name="sliced"/>, so a decoration rendered over the WHOLE composite box is limited
    /// to this fragment. Returns whether a clip was pushed (the caller balances it). No-op for a non-slice
    /// fragment → byte-identical.</summary>
    private static bool BeginSliceClip(
        PdfPage page, bool sliced, double pageHeightPt,
        double leftPx, double topPx, double widthPx, double heightPx)
    {
        if (!sliced) return false;
        ToPdfRect(leftPx, topPx, widthPx, heightPx, pageHeightPt, out var cx, out var cy, out var cw, out var ch);
        page.BeginRectangleClip(cx, cy, cw, ch);
        return true;
    }

    /// <summary>Paint the CSS <c>outline</c> (CSS UI 4 §5 — outline cycle): a uniform line just OUTSIDE
    /// the border box that does NOT affect layout. It occupies the ring between the border box grown by
    /// <c>outline-offset</c> (inner edge) and grown again by <c>outline-width</c> (outer edge), filled
    /// with <c>outline-color</c> via the shared rounded-ring fill (the same machinery as a rounded border).
    /// <c>outline-style: none/hidden</c>, an unset style, or a non-positive width paints nothing; a
    /// non-solid style is approximated as solid + diagnosed once. A <c>border-radius</c> rounds the
    /// outline to follow the box (its corner radii grown by the gap to each outline edge; a sharp box
    /// corner stays sharp — CSS UI §5.3). <c>outline-color: invert</c> is approximated as currentcolor
    /// (deferred). A negative <c>outline-offset</c> pulls the outline inward (inner box clamped ≥ 0).</summary>
    private static void PaintOutline(
        PdfPage page, ComputedStyle style, double pageHeightPt,
        double leftPx, double topPx, double widthPx, double heightPx,
        uint currentColorArgb, IDiagnosticsSink? diagnostics, ref bool styleApproximationReported,
        // box-decoration-break: slice — when > 0, the outline ring is computed over the WHOLE box (this many
        // px tall, virtual top = topPx − decorationBlockOffsetPx) + CLIPPED to this slice; isFirstSlice /
        // isLastSlice gate the top / bottom outline edge. Default 0 / true / true → the box's own outline.
        double decorationBlockExtentPx = 0.0, double decorationBlockOffsetPx = 0.0,
        bool isFirstSlice = true, bool isLastSlice = true)
    {
        if (!(widthPx > 0 && heightPx > 0)
            || !double.IsFinite(leftPx) || !double.IsFinite(topPx)
            || !double.IsFinite(widthPx) || !double.IsFinite(heightPx))
            return;

        var styleSlot = style.Get(PropertyId.OutlineStyle);
        if (styleSlot.Tag != ComputedSlotTag.Keyword) return;          // unset → none
        var styleKw = styleSlot.AsKeyword();
        if (styleKw == OutlineStyleNone) return;                       // `hidden` is invalid for outline (rejected at cascade)

        var widthSlot = style.Get(PropertyId.OutlineWidth);
        var outlineWidthPx = widthSlot.Tag == ComputedSlotTag.LengthPx ? widthSlot.AsLengthPx() : 0;
        if (outlineWidthPx <= 0) return;

        // outline-color initial is `auto` → currentcolor (CSS UI 4 §5.3; `auto` is admitted by the
        // dispatch as the CurrentColor slot, `invert` is retired). currentcolor resolves to the box colour.
        if (!TryResolveColor(style.Get(PropertyId.OutlineColor), currentColorArgb, out var argb))
            argb = currentColorArgb;
        if (Alpha(argb) == 0) return;

        // dotted / dashed / double now render faithfully (below); `auto` + `solid` paint solid WITHOUT a
        // diagnostic. Only the 3D styles (groove / ridge / inset / outset) stay approximated as a solid ring
        // (their per-side bevel can't follow the outline ring) — diagnosed once.
        var outlineLineStyle = styleKw switch
        {
            OutlineStyleDashed => RingLineStyle.Dashed,
            OutlineStyleDotted => RingLineStyle.Dotted,
            OutlineStyleDouble => RingLineStyle.Double,
            _ => RingLineStyle.Solid,
        };
        var outline3D = styleKw is >= 5 and <= 8; // groove / ridge / inset / outset
        if (outline3D && !styleApproximationReported)
        {
            diagnostics?.Emit(new Diagnostic(
                DiagnosticCodes.PaintBorderStyleApproximated001,
                "A 3D outline-style (groove / ridge / inset / outset) was painted as a solid line; per-side " +
                "bevel shading is a tracked follow-up (deferrals.md#layout-to-pdf-pipeline).",
                DiagnosticSeverity.Info));
            styleApproximationReported = true;
        }

        // outline-offset (may be negative). AngleSharp drops the declaration, so it is recovered via the
        // Css preprocessor (outline-offset cycle); an unset/unresolved slot reads 0 (flush with the border box).
        var offsetSlot = style.Get(PropertyId.OutlineOffset);
        var offsetPx = offsetSlot.Tag == ComputedSlotTag.LengthPx ? offsetSlot.AsLengthPx() : 0;

        // The ring: inner = the border box grown by the offset; outer = grown again by the width. A
        // NEGATIVE offset insets the inner box; CLAMP the effective offset PER AXIS to ≥ −½ the box
        // dimension BEFORE computing the origin AND the size (post-PR-#173 review P2) — so an extreme
        // negative offset (past half the box) collapses the outline to a zero-size box CENTERED on the
        // box rather than letting the origin drift past one side. A positive (outward) offset isn't
        // clamped. The two axes clamp independently (the offset is a single value, the limits differ).
        // box-decoration-break: slice — the ring is computed over the WHOLE composite box (so the side
        // outlines are CONTINUOUS across slices); the per-slice CLIP below limits it to this slice's outline
        // portion. A border-radius rounds the ring over the composite box (its radii resolved against the
        // composite height), and the clip cuts the corners that belong to another fragment.
        var isSlice = decorationBlockExtentPx > 0;
        var ringTop = isSlice ? topPx - decorationBlockOffsetPx : topPx;
        var ringHeight = isSlice ? decorationBlockExtentPx : heightPx;
        var effOffX = Math.Max(offsetPx, -widthPx / 2.0);
        var effOffY = Math.Max(offsetPx, -ringHeight / 2.0);
        var innerLeft = leftPx - effOffX;
        var innerTop = ringTop - effOffY;
        var innerW = widthPx + 2 * effOffX;     // ≥ 0 by the per-axis clamp
        var innerH = ringHeight + 2 * effOffY;
        var outerLeft = innerLeft - outlineWidthPx;
        var outerTop = innerTop - outlineWidthPx;
        var outerW = innerW + 2 * outlineWidthPx;
        var outerH = innerH + 2 * outlineWidthPx;

        // Rounded outline (CSS UI §5.3): a box corner with a border-radius grows outward by the gap to
        // the outline edge (offset + width for the outer, offset for the inner); a SHARP box corner stays
        // sharp, and GrowRadii clamps a component a large negative offset would drive below 0. No
        // border-radius → a square outline (zero radii → the ring's `re` fast path).
        // box-decoration-break: slice (PR #223 review [P1]) — the outline ring is the WHOLE composite box's
        // ring (radii resolved against the composite ring height, the same height the ring spans); the
        // per-slice CLIP below limits it to this fragment, so an oversized / percentage radius rounds
        // correctly across cuts. ReadCornerRadii against `ringHeight` is the box's own height for a
        // non-slice fragment → byte-identical.
        var boxRadii = ReadCornerRadii(style, widthPx, ringHeight);
        var rounded = boxRadii.AnyPositive;
        var outerRadii = rounded ? GrowRadii(boxRadii, offsetPx + outlineWidthPx) : default;
        var innerRadii = rounded ? GrowRadii(boxRadii, offsetPx) : default;

        // box-decoration-break: slice — clip the whole-box ring to THIS slice's outline portion: the full
        // outer width inline, and block-wise from the OUTER-top (first slice) / the slice's border-box top
        // (otherwise) to the OUTER-bottom (last slice) / the slice's border-box bottom. So the first slice
        // shows the top outline + sides, a middle slice just the sides, the last slice the bottom + sides.
        // Non-sliced → no clip (byte-identical).
        var sliceClipped = false;
        if (isSlice)
        {
            var clipTop = isFirstSlice ? outerTop : topPx;
            var clipBottom = isLastSlice ? outerTop + outerH : topPx + heightPx;
            ToPdfRect(outerLeft, clipTop, outerW, Math.Max(0, clipBottom - clipTop), pageHeightPt,
                out var ccx, out var ccy, out var ccw, out var cch);
            page.BeginRectangleClip(ccx, ccy, ccw, cch);
            sliceClipped = true;
        }
        ColorChannels(argb, out var r, out var g, out var b);
        // dotted / dashed / double stroke or split the ring; solid + 3D fill one ring. The outline thickness
        // is outlineWidthPx; outer / inner are the ring's outer / inner boxes.
        PaintStyledRing(page, outlineLineStyle, pageHeightPt,
            outerLeft, outerTop, outerW, outerH, outerRadii,
            innerLeft, innerTop, innerW, innerH, innerRadii,
            outlineWidthPx, r, g, b, Alpha(argb) / 255.0);
        if (sliceClipped) page.RestoreGraphicsState();
    }

    /// <summary>Whether all four border edges form a UNIFORM border that a single rounded RING can
    /// render (border-radius-completion cycle, Task 2): every edge has a PAINTABLE style (not
    /// none/hidden/unset), the SAME positive width (compared with a small tolerance — used widths come
    /// from unit conversions [cm/mm/pt/…] and could differ by float rounding), and the SAME resolved
    /// colour (each edge's colour resolved against ITS currentcolor). <paramref name="widthPx"/>/
    /// <paramref name="argb"/> are the shared width/colour; <paramref name="nonSolid"/> flags a non-solid
    /// style (still painted, but as solid — the caller diagnoses it once). A non-uniform border returns
    /// <see langword="false"/> so the caller keeps the per-edge square rects (rounded non-uniform borders
    /// are deferred).</summary>
    private static bool TryUniformBorder(
        ComputedStyle style, in BorderEdgeCurrentColors cc,
        out double widthPx, out uint argb, out bool nonSolid, out int styleKw)
    {
        widthPx = 0; argb = 0; nonSolid = false; styleKw = BorderStyleSolid;

        bool ReadEdge(PropertyId styleId, PropertyId widthId, PropertyId colorId, uint edgeCc,
            out double w, out uint a, out int kwOut)
        {
            w = 0; a = 0; kwOut = BorderStyleNone;
            var styleSlot = style.Get(styleId);
            if (styleSlot.Tag != ComputedSlotTag.Keyword) return false;       // unset → none
            var kw = styleSlot.AsKeyword();
            if (kw is BorderStyleNone or BorderStyleHidden) return false;
            kwOut = kw;
            var widthSlot = style.Get(widthId);
            w = widthSlot.Tag == ComputedSlotTag.LengthPx ? widthSlot.AsLengthPx() : 0;
            if (w <= 0) return false;
            if (!TryResolveColor(style.Get(colorId), edgeCc, out a)) a = edgeCc; // initial: currentcolor
            return Alpha(a) != 0;
        }

        if (!ReadEdge(PropertyId.BorderTopStyle, PropertyId.BorderTopWidth, PropertyId.BorderTopColor, cc.Top, out var wt, out var at, out var kwT)
            || !ReadEdge(PropertyId.BorderRightStyle, PropertyId.BorderRightWidth, PropertyId.BorderRightColor, cc.Right, out var wr, out var ar, out var kwR)
            || !ReadEdge(PropertyId.BorderBottomStyle, PropertyId.BorderBottomWidth, PropertyId.BorderBottomColor, cc.Bottom, out var wb, out var ab, out var kwB)
            || !ReadEdge(PropertyId.BorderLeftStyle, PropertyId.BorderLeftWidth, PropertyId.BorderLeftColor, cc.Left, out var wl, out var al, out var kwL))
            return false;
        // Used widths come from unit conversions, so compare against the top edge with a tolerance well
        // below a visible difference (rather than exact `==`, which equivalent lengths from different
        // units / calc could miss); colours are exact 8-bit ARGB, so an exact compare is right there.
        static bool SameWidth(double a, double b) => Math.Abs(a - b) <= 1e-4;
        if (!SameWidth(wt, wr) || !SameWidth(wt, wb) || !SameWidth(wt, wl)) return false;
        if (at != ar || ar != ab || ab != al) return false;   // equal colours
        // The STYLE must also be uniform for the single-ring path to render it faithfully (a per-edge mix
        // like dashed-top + dotted-bottom falls through to the per-edge rounded path). Required so the new
        // faithful dashed/dotted/double dispatch has one well-defined style.
        if (kwT != kwR || kwR != kwB || kwB != kwL) return false;
        widthPx = wt; argb = at; styleKw = kwT; nonSolid = kwT != BorderStyleSolid;
        return true;
    }

    /// <summary>A non-solid line style the rounded ring paths render faithfully (dashed / dotted / double);
    /// <c>Solid</c> covers solid + the 3D approximation (one filled ring).</summary>
    private enum RingLineStyle { Solid, Dashed, Dotted, Double }

    /// <summary>Phase 4 — paint a uniform-style ROUNDED ring (a border or an outline) between an OUTER and an
    /// INNER rounded box of the given <paramref name="thicknessPx"/>. <c>Dashed</c> / <c>Dotted</c> stroke the
    /// rounded CENTRELINE (the outer box inset by half the thickness) with the thickness line + the per-style
    /// dash; <c>Double</c> draws two concentric rings (outer + inner thirds, a middle-third gap); <c>Solid</c>
    /// fills one ring. All inputs are CSS px (page-top-relative).</summary>
    private static void PaintStyledRing(
        PdfPage page, RingLineStyle ls, double pageHeightPt,
        double outerLeftPx, double outerTopPx, double outerWPx, double outerHPx, CornerRadii outerRadiiPx,
        double innerLeftPx, double innerTopPx, double innerWPx, double innerHPx, CornerRadii innerRadiiPx,
        double thicknessPx, double r, double g, double b, double a)
    {
        void Ring(double oL, double oT, double oW, double oH, CornerRadii oR,
            double iL, double iT, double iW, double iH, CornerRadii iR)
        {
            ToPdfRect(oL, oT, oW, oH, pageHeightPt, out var ox, out var oy, out var ow, out var oh);
            ToPdfRect(iL, iT, iW, iH, pageHeightPt, out var ix, out var iy, out var iw, out var ih);
            page.FillRoundedRectangleRing(ox, oy, ow, oh, ToPt(oR), ix, iy, iw, ih, ToPt(iR), r, g, b, a);
        }

        switch (ls)
        {
            case RingLineStyle.Dashed:
            case RingLineStyle.Dotted:
            {
                var hw = thicknessPx / 2.0;   // centreline = the outer box inset by half the thickness
                ToPdfRect(outerLeftPx + hw, outerTopPx + hw, outerWPx - thicknessPx, outerHPx - thicknessPx,
                    pageHeightPt, out var cx, out var cy, out var cw, out var ch);
                var wPt = PdfUnits.PxToPt(thicknessPx);
                // Dotted: [0 2w] round caps → a round dot Ø w spaced 2w centre-to-centre; dashed: [3w 3w]
                // butt caps (matching the square per-edge StrokeDashedEdge).
                var dash = ls == RingLineStyle.Dotted ? new[] { 0.0, 2 * wPt } : new[] { 3 * wPt, 3 * wPt };
                page.StrokeRoundedRectangle(cx, cy, cw, ch, ToPt(ReduceRadii(outerRadiiPx, hw)), wPt, r, g, b, a,
                    dash, dashPhase: 0.0, lineCap: ls == RingLineStyle.Dotted ? 1 : 0);
                return;
            }
            case RingLineStyle.Double:
            {
                var t = thicknessPx / 3.0;    // two solid thirds with a middle-third gap
                Ring(outerLeftPx, outerTopPx, outerWPx, outerHPx, outerRadiiPx,
                    outerLeftPx + t, outerTopPx + t, outerWPx - 2 * t, outerHPx - 2 * t, ReduceRadii(outerRadiiPx, t));
                Ring(outerLeftPx + 2 * t, outerTopPx + 2 * t, outerWPx - 4 * t, outerHPx - 4 * t, ReduceRadii(outerRadiiPx, 2 * t),
                    innerLeftPx, innerTopPx, innerWPx, innerHPx, innerRadiiPx);
                return;
            }
            default: // solid + the 3D approximation (one filled ring)
                Ring(outerLeftPx, outerTopPx, outerWPx, outerHPx, outerRadiiPx,
                    innerLeftPx, innerTopPx, innerWPx, innerHPx, innerRadiiPx);
                return;
        }
    }

    /// <summary>True when any of the four border edges has a painted NON-solid style (dotted / dashed /
    /// double / groove / ridge / inset / outset) — i.e. not none/hidden/solid. Used to diagnose the rounded
    /// per-edge fallback (which approximates non-solid styles).</summary>
    private static bool AnyNonSolidBorderEdge(ComputedStyle style)
    {
        static bool NonSolid(ComputedStyle s, PropertyId id)
        {
            var slot = s.Get(id);
            if (slot.Tag != ComputedSlotTag.Keyword) return false;
            var kw = slot.AsKeyword();
            return kw is not (BorderStyleNone or BorderStyleHidden or BorderStyleSolid);
        }
        return NonSolid(style, PropertyId.BorderTopStyle) || NonSolid(style, PropertyId.BorderRightStyle)
            || NonSolid(style, PropertyId.BorderBottomStyle) || NonSolid(style, PropertyId.BorderLeftStyle);
    }

    /// <summary>Map a uniform border-style keyword to the ring line style (3D + solid → one ring).</summary>
    private static RingLineStyle BorderRingStyle(int kw) => kw switch
    {
        BorderStyleDashed => RingLineStyle.Dashed,
        BorderStyleDotted => RingLineStyle.Dotted,
        BorderStyleDouble => RingLineStyle.Double,
        _ => RingLineStyle.Solid,
    };

    /// <summary>Each radius component reduced by <paramref name="amount"/> px (clamped ≥ 0) — the
    /// border-box radii minus the full border width give the INNER (padding-box) ring radii (Task 2).</summary>
    private static CornerRadii ReduceRadii(CornerRadii r, double amount) => new(
        Math.Max(0, r.TopLeftX - amount), Math.Max(0, r.TopLeftY - amount),
        Math.Max(0, r.TopRightX - amount), Math.Max(0, r.TopRightY - amount),
        Math.Max(0, r.BottomRightX - amount), Math.Max(0, r.BottomRightY - amount),
        Math.Max(0, r.BottomLeftX - amount), Math.Max(0, r.BottomLeftY - amount));

    /// <summary>Each POSITIVE radius component grown by <paramref name="amount"/> px, then CLAMPED ≥ 0 (a
    /// zero component stays 0 — a sharp box corner gives a sharp outline corner, CSS UI 4 §5.3; a large
    /// NEGATIVE amount, from a negative outline-offset, can't drive a corner below 0). The outline's
    /// outer/inner corner radii are the box radii grown by the gap from the border box to that outline
    /// edge — post-PR-#173 review P3 (clamp in the helper, matching <see cref="ReduceRadii"/>).</summary>
    private static CornerRadii GrowRadii(CornerRadii r, double amount)
    {
        static double G(double c, double by) => c > 0 ? Math.Max(0, c + by) : 0;
        return new CornerRadii(
            G(r.TopLeftX, amount), G(r.TopLeftY, amount), G(r.TopRightX, amount), G(r.TopRightY, amount),
            G(r.BottomRightX, amount), G(r.BottomRightY, amount), G(r.BottomLeftX, amount), G(r.BottomLeftY, amount));
    }

    /// <summary>Border-box radii inset PER SIDE to an inner box (the background-clip box, Task 3): each
    /// corner's horizontal radius reduces by the inset on its vertical edge, its vertical radius by the
    /// inset on its horizontal edge (clamped ≥ 0) — CSS B&amp;B 3 §4.1's inner-rounding rule. A zero
    /// inset (background-clip: border-box) leaves the radii unchanged.</summary>
    internal static CornerRadii InsetRadii(CornerRadii r, double top, double right, double bottom, double left) => new(
        Math.Max(0, r.TopLeftX - left), Math.Max(0, r.TopLeftY - top),
        Math.Max(0, r.TopRightX - right), Math.Max(0, r.TopRightY - top),
        Math.Max(0, r.BottomRightX - right), Math.Max(0, r.BottomRightY - bottom),
        Math.Max(0, r.BottomLeftX - left), Math.Max(0, r.BottomLeftY - bottom));

    /// <summary>box-decoration-break: slice (inline-only line splitting) — the COMPOSITE box a sliced
    /// decoration is rendered over BEFORE being clipped to this fragment (PR #223 review [P1]): the whole
    /// unbroken box (top = <paramref name="topPx"/> − the slice's offset within the box, height = the full
    /// decoration extent), with the corner radii resolved against the COMPOSITE height. Resolving against the
    /// composite (not the slice) height is what CSS requires for a percentage radius, and lets a radius
    /// TALLER than one slice / the §4.2 overlap-clamp span the cut correctly — the rounded path is built once
    /// over the whole box, then each fragment clips it. A non-slice fragment
    /// (<paramref name="decorationBlockExtentPx"/> == 0) returns the box itself → byte-identical.</summary>
    private static (double Top, double Height, CornerRadii Radii) CompositeRoundedBox(
        ComputedStyle style, double topPx, double widthPx, double heightPx,
        double decorationBlockExtentPx, double decorationBlockOffsetPx)
        => decorationBlockExtentPx > 0
            ? (topPx - decorationBlockOffsetPx, decorationBlockExtentPx,
               ReadCornerRadii(style, widthPx, decorationBlockExtentPx))
            : (topPx, heightPx, ReadCornerRadii(style, widthPx, heightPx));

    private static void PaintBackground(
        PdfPage page, ComputedStyle style, double pageHeightPt,
        double leftPx, double topPx, double widthPx, double heightPx,
        uint currentColorArgb,
        // box-decoration-break: slice — when > 0 this fragment is one block-axis slice of a larger box; the
        // rounded color band is built over the WHOLE composite box (this extent, virtual top = topPx − the
        // offset) + clipped to this slice, so a percentage / oversized radius rounds correctly across cuts.
        // Default 0 → the box itself (byte-identical).
        double decorationBlockExtentPx = 0.0, double decorationBlockOffsetPx = 0.0)
    {
        if (!TryResolveColor(style.Get(PropertyId.BackgroundColor), currentColorArgb, out var argb))
            return;
        var alpha = Alpha(argb);
        if (alpha == 0) return; // transparent (the initial value) paints nothing.

        ColorChannels(argb, out var r, out var g, out var b);
        // border-radius (body-radius / border-radius-completion cycles): per-corner ABSOLUTE + %
        // radii round the COLOR band. The UNIFORM-circular case keeps the byte-stable single-radius
        // path (PdfPage.FillRoundedRectangle, clamped to half the shorter side); the general case
        // (per-corner / elliptical-via-%) uses the per-corner fill (§4.2 overlap-clamped in PdfPage).
        // Border strokes (PaintBorders) + the background-image clip honor the SAME radii. The explicit
        // `Rx / Ry` slash form is an AngleSharp drop (deferred). A partial alpha composites via /ca.
        // box-decoration-break: slice (PR #223 review [P1]) — the rounded fill is the COMPOSITE box (radii
        // resolved against the whole-box height) clipped to this slice; the cut is square because the slice
        // rect clips off the rounding that belongs to another fragment. CompositeRoundedBox is the box
        // itself for a non-slice → byte-identical.
        var (compTopPx, compHeightPx, radiiPx) = CompositeRoundedBox(
            style, topPx, widthPx, heightPx, decorationBlockExtentPx, decorationBlockOffsetPx);
        if (radiiPx.AnyPositive)
        {
            var sliced = decorationBlockExtentPx > 0;
            if (sliced)
            {
                ToPdfRect(leftPx, topPx, widthPx, heightPx, pageHeightPt, out var cx, out var cy, out var cw, out var ch);
                page.BeginRectangleClip(cx, cy, cw, ch);
            }
            ToPdfRect(leftPx, compTopPx, widthPx, compHeightPx, pageHeightPt, out var x, out var y, out var w, out var h);
            if (radiiPx.IsUniformCircular(out var rPx))
            {
                var radiusPt = Math.Min(PdfUnits.PxToPt(rPx), Math.Min(w, h) / 2.0);
                page.FillRoundedRectangle(x, y, w, h, radiusPt, r, g, b, alpha / 255.0);
            }
            else
            {
                page.FillRoundedRectangle(x, y, w, h, ToPt(radiiPx), r, g, b, alpha / 255.0);
            }
            if (sliced) page.RestoreGraphicsState();
            return;
        }
        // A square background fills the SLICE's own rect (a non-final slice fills the page).
        ToPdfRect(leftPx, topPx, widthPx, heightPx, pageHeightPt, out var sx, out var sy, out var sw, out var sh);
        page.FillRectangle(sx, sy, sw, sh, r, g, b, alpha / 255.0);
    }

    // ───── Phase 4 shadows — box-shadow ─────────────────────────────────────

    private const double BoxShadowBlurEpsilonPx = 0.01; // ≤ this blur radius paints as a sharp shadow
    private const double BoxShadowRasterScale = 2.0;    // device px per CSS px for the blur raster

    /// <summary>Phase 4 shadows — paint a box's <c>box-shadow</c> layers UNDER its background
    /// (CSS B&amp;B §7.2). OUTSET layers only (the first cut): a sharp (blur ≈ 0) layer is a native
    /// filled (rounded) rect offset + spread-expanded from the border box; a blurred layer is
    /// rasterized through the Skia bridge and placed as an image. Layers paint in REVERSE list
    /// order so the FIRST-listed shadow ends up on top (§7.2.1). Inset layers are skipped (a
    /// diagnostic was emitted at collection).</summary>
    private static void PaintBoxShadows(
        PdfPage page, PdfDocument document, IReadOnlyList<CssBoxShadow> shadows, ComputedStyle style,
        double pageHeightPt, double leftPx, double topPx, double widthPx, double heightPx,
        uint currentColorArgb, IDiagnosticsSink? diagnostics, ref bool rasterReported, ref bool capReported,
        // box-decoration-break: slice — when > 0, the shadow is computed over the WHOLE composite box (this
        // many px tall, virtual top = topPx − decorationBlockOffsetPx) + CLIPPED to this slice; isFirstSlice /
        // isLastSlice gate the top / bottom shadow edge (the side shadows show on every slice). Default 0 /
        // true / true → the box's own shadow (no slicing, byte-identical).
        double decorationBlockExtentPx = 0.0, double decorationBlockOffsetPx = 0.0,
        bool isFirstSlice = true, bool isLastSlice = true)
    {
        // box-decoration-break: slice — the shadow is for the unfragmented (composite) box, then sliced at
        // the fragmentation CUTS (not the page edges). The box top / height the shadow grows from is the
        // whole composite box, and its radii resolve against the COMPOSITE height (PR #223 review [P1]), so
        // the spread-expanded shadow follows the same rounding the border / background do — across cuts.
        var isSlice = decorationBlockExtentPx > 0;
        var boxTopPx = isSlice ? topPx - decorationBlockOffsetPx : topPx;
        var boxHeightPx = isSlice ? decorationBlockExtentPx : heightPx;
        var borderRadii = ReadCornerRadii(style, widthPx, boxHeightPx);
        for (var i = shadows.Count - 1; i >= 0; i--)
        {
            var s = shadows[i];
            if (s.Inset) continue; // outset-only first cut

            uint argb;
            if (s.ColorRaw is null)
            {
                argb = currentColorArgb;
            }
            else
            {
                var resolved = NetPdf.Css.ComputedValues.PropertyResolvers.ColorResolver.Resolve(
                    s.ColorRaw, PropertyId.Color, "color", diagnostics: null, location: default);
                if (!TryResolveColor(resolved.Slot, currentColorArgb, out argb)) continue; // invalid color
            }
            var alpha = Alpha(argb) / 255.0;
            if (alpha <= 0) continue;
            ColorChannels(argb, out var r, out var g, out var b);

            // The shadow shape = the (whole composite) border box offset by (x, y) and grown by the spread.
            var shLeftPx = leftPx + s.OffsetXPx - s.SpreadPx;
            var shTopPx = boxTopPx + s.OffsetYPx - s.SpreadPx;
            var shWidthPx = widthPx + 2 * s.SpreadPx;
            var shHeightPx = boxHeightPx + 2 * s.SpreadPx;
            if (shWidthPx <= 0 || shHeightPx <= 0) continue;
            var shadowRadii = ExpandRadiiForSpread(borderRadii, s.SpreadPx);

            // box-decoration-break: slice — clip the whole-box shadow to THIS slice's shadow portion: the top
            // shadow (above the box top) only on the FIRST slice, the bottom only on the LAST, the side
            // shadows on EVERY slice. The clip is full-width (the side shadows + blur span it horizontally)
            // and block-wise from the shadow's top / the slice's border-box top (cut) to the shadow's bottom /
            // the slice's border-box bottom (cut). The blur margin (~3σ, the raster's pad) extends each edge.
            var sliceClipped = false;
            if (isSlice)
            {
                var blurMarginPx = s.BlurPx > BoxShadowBlurEpsilonPx ? Math.Ceiling(3.0 * (s.BlurPx / 2.0)) : 0.0;
                var clipTop = isFirstSlice ? shTopPx - blurMarginPx : topPx;
                var clipBottom = isLastSlice ? shTopPx + shHeightPx + blurMarginPx : topPx + heightPx;
                var clipLeft = shLeftPx - blurMarginPx;
                var clipWidth = shWidthPx + 2 * blurMarginPx;
                ToPdfRect(clipLeft, clipTop, clipWidth, Math.Max(0, clipBottom - clipTop), pageHeightPt,
                    out var ccx, out var ccy, out var ccw, out var cch);
                page.BeginRectangleClip(ccx, ccy, ccw, cch);
                sliceClipped = true;
            }

            if (s.BlurPx <= BoxShadowBlurEpsilonPx)
            {
                // Sharp shadow → a knockout RING (the shadow shape minus the composite border box).
                FillShadowRing(page, pageHeightPt, shLeftPx, shTopPx, shWidthPx, shHeightPx, shadowRadii,
                    leftPx, boxTopPx, widthPx, boxHeightPx, borderRadii, r, g, b, alpha);
            }
            else if (isSlice)
            {
                // box-decoration-break: slice (PR #223 review [P1]) — a BLURRED shadow on a block split
                // across pages is painted SHARP: rasterizing the whole composite shadow per slice would blow
                // the raster cap + amplify CPU / memory for untrusted HTML. Still knocked out (a ring).
                FillShadowRing(page, pageHeightPt, shLeftPx, shTopPx, shWidthPx, shHeightPx, shadowRadii,
                    leftPx, boxTopPx, widthPx, boxHeightPx, borderRadii, r, g, b, alpha);
                if (!capReported)
                {
                    diagnostics?.Emit(new Diagnostic(
                        DiagnosticCodes.CssBoxShadowUnsupported001,
                        "A blurred box-shadow on a block split across pages (box-decoration-break: slice) was "
                        + "painted as a SHARP shadow — the whole-box blur raster would exceed the cap.",
                        DiagnosticSeverity.Warning));
                    capReported = true;
                }
            }
            else
            {
                PaintBlurredBoxShadow(page, document, shLeftPx, shTopPx, shWidthPx, shHeightPx,
                    shadowRadii, s.BlurPx, pageHeightPt, r, g, b, alpha,
                    leftPx, boxTopPx, widthPx, boxHeightPx, borderRadii, diagnostics,
                    ref rasterReported, ref capReported);
            }

            if (sliceClipped) page.RestoreGraphicsState();
        }
    }

    /// <summary>CSS B&amp;B §6.1.1 — paint an OUTSET shadow as a RING: the (spread-expanded, offset) shadow
    /// shape with the composite BORDER box knocked out (an even-odd fill), so it does not show through a
    /// transparent / translucent background (PR #223 review [P2]).</summary>
    private static void FillShadowRing(
        PdfPage page, double pageHeightPt,
        double shLeftPx, double shTopPx, double shWidthPx, double shHeightPx, CornerRadii shadowRadii,
        double boxLeftPx, double boxTopPx, double boxWidthPx, double boxHeightPx, CornerRadii boxRadii,
        double r, double g, double b, double alpha)
    {
        ToPdfRect(shLeftPx, shTopPx, shWidthPx, shHeightPx, pageHeightPt, out var ox, out var oy, out var ow, out var oh);
        ToPdfRect(boxLeftPx, boxTopPx, boxWidthPx, boxHeightPx, pageHeightPt, out var ix, out var iy, out var iw, out var ih);
        page.FillRoundedRectangleRing(ox, oy, ow, oh, ToPt(shadowRadii), ix, iy, iw, ih, ToPt(boxRadii), r, g, b, alpha);
    }

    /// <summary>Rasterize a blurred OUTSET shadow through the Skia bridge and place it. The bitmap
    /// pads the (spread-expanded) shape by ~3σ on each side (where σ = blur/2, the Chromium
    /// convention) at <see cref="BoxShadowRasterScale"/>× resolution. Per-corner blur radii are a
    /// documented first-cut approximation (a single representative radius drives the raster; the
    /// sharp path is per-corner exact). An over-cap bitmap falls back to a sharp shadow.</summary>
    private static void PaintBlurredBoxShadow(
        PdfPage page, PdfDocument document,
        double shLeftPx, double shTopPx, double shWidthPx, double shHeightPx,
        CornerRadii shadowRadii, double blurPx, double pageHeightPt,
        double r, double g, double b, double alpha,
        // The composite BORDER box knocked out of the outset shadow (CSS B&B §6.1.1, PR #223 review [P2]).
        double boxLeftPx, double boxTopPx, double boxWidthPx, double boxHeightPx, CornerRadii boxRadii,
        IDiagnosticsSink? diagnostics, ref bool rasterReported, ref bool capReported)
    {
        var sigmaPx = blurPx / 2.0;
        var marginPx = Math.Ceiling(3.0 * sigmaPx);
        var bmpLeftPx = shLeftPx - marginPx;
        var bmpTopPx = shTopPx - marginPx;
        var bmpWidthPx = shWidthPx + 2 * marginPx;
        var bmpHeightPx = shHeightPx + 2 * marginPx;
        var deviceW = (int)Math.Ceiling(bmpWidthPx * BoxShadowRasterScale);
        var deviceH = (int)Math.Ceiling(bmpHeightPx * BoxShadowRasterScale);
        var radiusPx = MaxCorner(shadowRadii);

        var result = NetPdf.Pdf.Images.ShadowRasterizer.TryRasterize(
            deviceW, deviceH,
            shapeLeft: (float)(marginPx * BoxShadowRasterScale),
            shapeTop: (float)(marginPx * BoxShadowRasterScale),
            shapeWidth: (float)(shWidthPx * BoxShadowRasterScale),
            shapeHeight: (float)(shHeightPx * BoxShadowRasterScale),
            radius: (float)(radiusPx * BoxShadowRasterScale),
            blurSigma: (float)(sigmaPx * BoxShadowRasterScale),
            r, g, b, alpha);

        if (result is null)
        {
            // Over-cap / degenerate → a sharp native shadow RING is better than nothing, but SURFACE the
            // approximation (PR #210 review [P2] — never silently degrade).
            FillShadowRing(page, pageHeightPt, shLeftPx, shTopPx, shWidthPx, shHeightPx, shadowRadii,
                boxLeftPx, boxTopPx, boxWidthPx, boxHeightPx, boxRadii, r, g, b, alpha);
            if (!capReported)
            {
                diagnostics?.Emit(new Diagnostic(
                    DiagnosticCodes.CssBoxShadowUnsupported001,
                    "A blurred box-shadow was too large to rasterize (the shadow bitmap would exceed "
                    + $"the {NetPdf.Pdf.Images.ShadowRasterizer.MaxDeviceDimension} px cap); it was "
                    + "painted as a SHARP shadow instead of blurred.",
                    DiagnosticSeverity.Warning));
                capReported = true;
            }
            return;
        }

        var imageRef = document.RegisterImage(result);
        ToPdfRect(bmpLeftPx, bmpTopPx, bmpWidthPx, bmpHeightPx, pageHeightPt,
            out var x, out var y, out var w, out var h);
        // CSS B&B §6.1.1 — knock the composite border box out of the blurred shadow (an outset shadow does
        // not show inside the element), so a transparent background doesn't reveal the inner blur.
        ToPdfRect(boxLeftPx, boxTopPx, boxWidthPx, boxHeightPx, pageHeightPt, out var kx, out var ky, out var kw, out var kh);
        page.BeginRoundedRectangleHoleClip(x, y, w, h, kx, ky, kw, kh, ToPt(boxRadii));
        page.PlaceImage(imageRef, x, y, w, h);
        page.RestoreGraphicsState();

        if (!rasterReported)
        {
            diagnostics?.Emit(new Diagnostic(
                DiagnosticCodes.CssBoxShadowBlurRaster001,
                "A blurred box-shadow was painted via the Skia raster fallback (PDF has no native "
                + "Gaussian blur); the shadow shape was rasterized at "
                + $"{BoxShadowRasterScale:0}× and placed as an image XObject.",
                DiagnosticSeverity.Info));
            rasterReported = true;
        }
    }

    /// <summary>Grow each border-radius corner by the spread (CSS B&amp;B §7.2.1 — a 0 corner stays
    /// sharp; a positive corner radius increases by the spread, clamped ≥ 0).</summary>
    private static CornerRadii ExpandRadiiForSpread(CornerRadii radii, double spreadPx)
    {
        static double E(double c, double s) => c > 0 ? Math.Max(0, c + s) : 0;
        return new CornerRadii(
            E(radii.TopLeftX, spreadPx), E(radii.TopLeftY, spreadPx),
            E(radii.TopRightX, spreadPx), E(radii.TopRightY, spreadPx),
            E(radii.BottomRightX, spreadPx), E(radii.BottomRightY, spreadPx),
            E(radii.BottomLeftX, spreadPx), E(radii.BottomLeftY, spreadPx));
    }

    private static double MaxCorner(CornerRadii c) => Math.Max(
        Math.Max(Math.Max(c.TopLeftX, c.TopLeftY), Math.Max(c.TopRightX, c.TopRightY)),
        Math.Max(Math.Max(c.BottomRightX, c.BottomRightY), Math.Max(c.BottomLeftX, c.BottomLeftY)));

    /// <summary>Phase 4 shadows (PR 1 refinements) — paint a box's INSET <c>box-shadow</c> layers
    /// OVER the background, clipped to the PADDING box (CSS B&amp;B §7.2 — an inset shadow casts a
    /// soft band inward from the inside edges; the lit HOLE is the padding box offset by (x, y) and
    /// contracted by the spread). A sharp (blur ≈ 0) layer is a native even-odd RING (padding box
    /// minus the hole); a blurred layer rasterizes the band via the Skia bridge (fill + a
    /// <c>DstOut</c> blurred hole) and places it. Layers paint in REVERSE list order (first listed on
    /// top). Outset layers are skipped here (the under-background pass painted them). The box's OWN
    /// (fragment) geometry is used — a box-decoration-break: slice fragment paints relative to the
    /// slice (a documented residual; the common single-page box is exact).</summary>
    private static void PaintInsetBoxShadows(
        PdfPage page, PdfDocument document, IReadOnlyList<CssBoxShadow> shadows, ComputedStyle style,
        double pageHeightPt, double leftPx, double topPx, double widthPx, double heightPx,
        uint currentColorArgb, IDiagnosticsSink? diagnostics, ref bool rasterReported, ref bool capReported)
    {
        var (bt, br, bb, bl) = BackgroundAreaInset(style, "padding-box", defaultArea: 'p');
        var padLeftPx = leftPx + bl;
        var padTopPx = topPx + bt;
        var padWidthPx = widthPx - bl - br;
        var padHeightPx = heightPx - bt - bb;
        if (padWidthPx <= 0 || padHeightPx <= 0) return;
        var padRadii = InsetRadii(ReadCornerRadii(style, widthPx, heightPx), bt, br, bb, bl);

        for (var i = shadows.Count - 1; i >= 0; i--)
        {
            var s = shadows[i];
            if (!s.Inset) continue; // the outset pass handled non-inset layers

            uint argb;
            if (s.ColorRaw is null) argb = currentColorArgb;
            else
            {
                var resolved = NetPdf.Css.ComputedValues.PropertyResolvers.ColorResolver.Resolve(
                    s.ColorRaw, PropertyId.Color, "color", diagnostics: null, location: default);
                if (!TryResolveColor(resolved.Slot, currentColorArgb, out argb)) continue;
            }
            var alpha = Alpha(argb) / 255.0;
            if (alpha <= 0) continue;
            ColorChannels(argb, out var r, out var g, out var b);

            // The lit hole = the padding box offset by (x, y) and contracted by the spread.
            var holeLeftPx = padLeftPx + s.OffsetXPx + s.SpreadPx;
            var holeTopPx = padTopPx + s.OffsetYPx + s.SpreadPx;
            var holeWidthPx = padWidthPx - 2 * s.SpreadPx;
            var holeHeightPx = padHeightPx - 2 * s.SpreadPx;
            var holeRadii = ExpandRadiiForSpread(padRadii, -s.SpreadPx);

            ToPdfRect(padLeftPx, padTopPx, padWidthPx, padHeightPx, pageHeightPt,
                out var px, out var py, out var pw, out var ph);

            if (s.BlurPx <= BoxShadowBlurEpsilonPx)
            {
                // Sharp inset → a native even-odd ring (padding box minus the hole), clipped to the
                // padding box so the offset hole's overflow doesn't fill outside the box.
                page.BeginRoundedRectangleClip(px, py, pw, ph, ToPt(padRadii));
                if (holeWidthPx > 0 && holeHeightPx > 0)
                {
                    ToPdfRect(holeLeftPx, holeTopPx, holeWidthPx, holeHeightPx, pageHeightPt,
                        out var hx, out var hy, out var hw, out var hh);
                    page.FillRoundedRectangleRing(px, py, pw, ph, ToPt(padRadii),
                        hx, hy, hw, hh, ToPt(holeRadii), r, g, b, alpha);
                }
                else
                {
                    // The hole vanished (spread swallowed it) → the whole padding box is in shadow.
                    page.FillRoundedRectangleRing(px, py, pw, ph, ToPt(padRadii), 0, 0, 0, 0, default, r, g, b, alpha);
                }
                page.RestoreGraphicsState();
            }
            else
            {
                PaintBlurredInsetShadow(page, document, padLeftPx, padTopPx, padWidthPx, padHeightPx, padRadii,
                    holeLeftPx, holeTopPx, holeWidthPx, holeHeightPx, holeRadii, s.BlurPx, pageHeightPt,
                    r, g, b, alpha, diagnostics, ref rasterReported, ref capReported);
            }
        }
    }

    /// <summary>Rasterize a blurred INSET shadow band through the Skia bridge and place it clipped to
    /// the padding box. The bitmap IS the padding box (no blur-margin expansion — the band is
    /// inside); an over-cap bitmap falls back to a sharp inset ring.</summary>
    private static void PaintBlurredInsetShadow(
        PdfPage page, PdfDocument document,
        double padLeftPx, double padTopPx, double padWidthPx, double padHeightPx, CornerRadii padRadii,
        double holeLeftPx, double holeTopPx, double holeWidthPx, double holeHeightPx, CornerRadii holeRadii,
        double blurPx, double pageHeightPt, double r, double g, double b, double alpha,
        IDiagnosticsSink? diagnostics, ref bool rasterReported, ref bool capReported)
    {
        var sigmaPx = blurPx / 2.0;
        var deviceW = (int)Math.Ceiling(padWidthPx * BoxShadowRasterScale);
        var deviceH = (int)Math.Ceiling(padHeightPx * BoxShadowRasterScale);
        var result = NetPdf.Pdf.Images.ShadowRasterizer.TryRasterizeInset(
            deviceW, deviceH, (float)(MaxCorner(padRadii) * BoxShadowRasterScale),
            holeLeft: (float)((holeLeftPx - padLeftPx) * BoxShadowRasterScale),
            holeTop: (float)((holeTopPx - padTopPx) * BoxShadowRasterScale),
            holeWidth: (float)(holeWidthPx * BoxShadowRasterScale),
            holeHeight: (float)(holeHeightPx * BoxShadowRasterScale),
            holeRadius: (float)(MaxCorner(holeRadii) * BoxShadowRasterScale),
            blurSigma: (float)(sigmaPx * BoxShadowRasterScale), r, g, b, alpha);

        ToPdfRect(padLeftPx, padTopPx, padWidthPx, padHeightPx, pageHeightPt, out var px, out var py, out var pw, out var ph);
        if (result is null)
        {
            // Over-cap → a sharp inset ring is better than nothing; SURFACE the approximation.
            page.BeginRoundedRectangleClip(px, py, pw, ph, ToPt(padRadii));
            if (holeWidthPx > 0 && holeHeightPx > 0)
            {
                ToPdfRect(holeLeftPx, holeTopPx, holeWidthPx, holeHeightPx, pageHeightPt, out var hx, out var hy, out var hw, out var hh);
                page.FillRoundedRectangleRing(px, py, pw, ph, ToPt(padRadii), hx, hy, hw, hh, ToPt(holeRadii), r, g, b, alpha);
            }
            page.RestoreGraphicsState();
            if (!capReported)
            {
                diagnostics?.Emit(new Diagnostic(
                    DiagnosticCodes.CssBoxShadowUnsupported001,
                    "A blurred inset box-shadow was too large to rasterize (the band bitmap would exceed "
                    + $"the {NetPdf.Pdf.Images.ShadowRasterizer.MaxDeviceDimension} px cap); it was painted SHARP.",
                    DiagnosticSeverity.Warning));
                capReported = true;
            }
            return;
        }

        var imageRef = document.RegisterImage(result);
        page.BeginRoundedRectangleClip(px, py, pw, ph, ToPt(padRadii));
        page.PlaceImage(imageRef, px, py, pw, ph);
        page.RestoreGraphicsState();

        if (!rasterReported)
        {
            diagnostics?.Emit(new Diagnostic(
                DiagnosticCodes.CssBoxShadowBlurRaster001,
                "A blurred inset box-shadow was painted via the Skia raster fallback (PDF has no native "
                + $"Gaussian blur); the band was rasterized at {BoxShadowRasterScale:0}× and placed as an image XObject.",
                DiagnosticSeverity.Info));
            rasterReported = true;
        }
    }

    /// <summary>Phase 4 gradients — paint a parsed <c>linear-gradient(...)</c> background layer
    /// as a PDF native axial shading (ISO 32000-2 §8.7.4.5.3) clipped to the box's (rounded)
    /// border box. Resolves each stop's color, normalizes the stop positions (CSS Images §3.4),
    /// derives the gradient-line endpoints in PDF user space from the angle + box, and registers
    /// + paints the shading. A gradient with &lt; 2 resolvable stops is skipped (the
    /// background-color already painted underneath).</summary>
    private static void PaintLinearGradient(
        PdfPage page, PdfDocument document, CssLinearGradient gradient, ComputedStyle style,
        double pageHeightPt, double leftPx, double topPx, double widthPx, double heightPx,
        uint currentColorArgb, IDiagnosticsSink? diagnostics, ref bool gradientAlphaCapReported,
        // box-decoration-break: slice — when set, the gradient AXIS + the rounded CLIP span this (larger,
        // whole-box / composite) block extent instead of the painted slice, so a sliced block's gradient is
        // CONTINUOUS across slices and rounds correctly; the CALLER adds an outer per-slice rect clip to
        // limit the paint to this fragment (PR #223 review [P1]). Null → the box itself (byte-identical).
        double? axisTopPx = null, double? axisHeightPx = null,
        // Multi-layer backgrounds (PR #235 review [P2]) — when set, the box args are the background-ORIGIN
        // box (the axis spans it) while the shading is CLIPPED to THIS rect (the background-clip box,
        // rounded) instead of the axis box + style radii. Null → clip == axis box with the style radii
        // (the single-layer path, byte-identical).
        (double LeftPx, double TopPx, double WidthPx, double HeightPx, CornerRadii Radii)? clipOverride = null)
    {
        // CLIP rect = AXIS rect = the whole (composite) decoration box. The inline axis is shared (slices
        // differ only in the block axis); the radii resolve against the COMPOSITE height.
        var clipHeightPx = axisHeightPx ?? heightPx;
        ToPdfRect(leftPx, axisTopPx ?? topPx, widthPx, clipHeightPx, pageHeightPt,
            out var ax, out var ay, out var aw, out var ah);
        // A `to <corner>` direction's angle depends on the box aspect ratio (PR #209 review [P2]) —
        // compute it from the whole (axis) box; an explicit angle / side uses the parsed value as-is.
        var angleDeg = gradient.Corner is { } corner ? CornerAngleDeg(corner, aw, ah) : gradient.AngleDeg;
        // The gradient-line length in CSS px (CSS Images §3.1) — a length-positioned stop resolves
        // its fraction against this (PR 1 refinements).
        var theta = angleDeg * Math.PI / 180.0;
        var lineLengthCssPx = Math.Abs(widthPx * Math.Sin(theta)) + Math.Abs(clipHeightPx * Math.Cos(theta));
        var normalized = ResolveAndNormalizeStops(gradient.Stops, currentColorArgb, lineLengthCssPx, gradient.Repeating);
        if (normalized.Count < 2) return;

        // Per-stop alpha (gradient refinements): a native axial shading is DeviceRGB (no alpha), so a
        // translucent stop falls back to a Skia raster (image + /SMask, like conic). A fully-opaque
        // gradient stays the native shading (byte-identical).
        if (AnyTranslucent(normalized))
        {
            var painted = TryPaintAlphaLinearGradientRaster(page, document, normalized, angleDeg, pageHeightPt,
                leftPx, axisTopPx ?? topPx, widthPx, clipHeightPx, style, clipOverride);
            // Over-cap (false): the alpha bitmap exceeds the raster size cap → skip the gradient (the
            // background-color shows) and surface a distinct Warning rather than falling through to the
            // native opaque shading and silently DROPPING the transparency (PR #237 review [P1]). A
            // DEGENERATE origin box (null) is a no-op — nothing to paint, NOT an over-cap (PR #238 [P3]).
            if (painted == false) EmitGradientAlphaOverCap(diagnostics, ref gradientAlphaCapReported);
            return;
        }

        var stops = new List<PdfGradientStop>(normalized.Count);
        foreach (var s in normalized) stops.Add(new PdfGradientStop(s.Offset, s.R, s.G, s.B)); // native drops alpha
        var (x0, y0, x1, y1) = LinearGradientAxis(angleDeg, ax, ay, aw, ah);
        var shadingRef = document.RegisterAxialShading(x0, y0, x1, y1, stops);
        if (clipOverride is { } co)
        {
            ToPdfRect(co.LeftPx, co.TopPx, co.WidthPx, co.HeightPx, pageHeightPt, out var cx, out var cy, out var cw, out var ch);
            page.PaintShadingInRect(shadingRef, cx, cy, cw, ch,
                co.Radii.AnyPositive ? ToPt(co.Radii) : (CornerRadii?)null);
            return;
        }
        var radiiPx = ReadCornerRadii(style, widthPx, clipHeightPx);
        page.PaintShadingInRect(shadingRef, ax, ay, aw, ah,
            radiiPx.AnyPositive ? ToPt(radiiPx) : (CornerRadii?)null);
    }

    /// <summary>True when any resolved stop is translucent (alpha &lt; 1) — the native DeviceRGB
    /// shading can't represent it, so the caller rasters.</summary>
    private static bool AnyTranslucent(List<ResolvedGradientStop> stops)
    {
        foreach (var s in stops) if (s.A < 1.0 - 1e-9) return true;
        return false;
    }

    /// <summary>A translucent linear / radial gradient over-capped the raster (the alpha bitmap would
    /// exceed the device-pixel cap). Surface it ONCE per render as a Warning under
    /// <c>CSS-GRADIENT-ALPHA-UNSUPPORTED-001</c> — the caller skips the gradient (the background-color
    /// shows) rather than dropping the alpha into an opaque native shading (PR #237 review [P1]).</summary>
    private static void EmitGradientAlphaOverCap(IDiagnosticsSink? diagnostics, ref bool reported)
    {
        if (reported || diagnostics is null) return;
        diagnostics.Emit(new Diagnostic(
            DiagnosticCodes.CssGradientAlphaUnsupported001,
            "A translucent linear/radial gradient was too large to rasterize (the alpha bitmap would "
            + $"exceed the {NetPdf.Pdf.Images.ShadowRasterizer.MaxDeviceDimension} px cap); the gradient "
            + "was skipped (the background-color shows) rather than dropping its transparency.",
            DiagnosticSeverity.Warning));
        reported = true;
    }

    /// <summary>Paint a translucent linear gradient via the Skia raster (image + alpha <c>/SMask</c>),
    /// clipped to the (rounded) box — the <paramref name="clipOverride"/> background-clip box for a
    /// multi-layer gradient layer, else the style border-radius box. Tri-state: <see langword="true"/> =
    /// painted; <see langword="false"/> = OVER-CAP (the caller skips the gradient + warns, NOT a native
    /// fallback); <see langword="null"/> = a DEGENERATE (zero/negative) origin box → nothing to paint,
    /// a silent no-op (not an over-cap — PR #238 [P3]).</summary>
    private static bool? TryPaintAlphaLinearGradientRaster(
        PdfPage page, PdfDocument document, List<ResolvedGradientStop> normalized, double angleDeg,
        double pageHeightPt, double boxLeftPx, double boxTopPx, double boxWidthPx, double boxHeightPx,
        ComputedStyle style,
        (double LeftPx, double TopPx, double WidthPx, double HeightPx, CornerRadii Radii)? clipOverride)
    {
        if (boxWidthPx <= 0 || boxHeightPx <= 0) return null; // degenerate origin → no-op (not over-cap)
        var deviceW = (int)Math.Ceiling(boxWidthPx * ConicGradientRasterScale);
        var deviceH = (int)Math.Ceiling(boxHeightPx * ConicGradientRasterScale);
        var rstops = new List<NetPdf.Pdf.Images.LinearGradientRasterizer.Stop>(normalized.Count);
        foreach (var s in normalized)
            rstops.Add(new NetPdf.Pdf.Images.LinearGradientRasterizer.Stop(s.Offset, s.R, s.G, s.B, s.A));
        var result = NetPdf.Pdf.Images.LinearGradientRasterizer.TryRasterize(deviceW, deviceH, angleDeg, rstops);
        if (result is null) return false;
        var imageRef = document.RegisterImage(result);
        PlaceGradientRasterClipped(page, document, imageRef, pageHeightPt,
            boxLeftPx, boxTopPx, boxWidthPx, boxHeightPx, style, clipOverride);
        return true;
    }

    /// <summary>Paint a translucent radial gradient via the Skia raster (image + alpha <c>/SMask</c>),
    /// clipped to the (rounded) box. The center fractions + per-axis CSS-px radii come from the
    /// caller's geometry. Tri-state: <see langword="true"/> = painted; <see langword="false"/> =
    /// OVER-CAP (the caller skips the gradient + warns, NOT a native fallback); <see langword="null"/> =
    /// a DEGENERATE (zero/negative) origin box → a silent no-op (not over-cap — PR #238 [P3]).</summary>
    private static bool? TryPaintAlphaRadialGradientRaster(
        PdfPage page, PdfDocument document, List<ResolvedGradientStop> normalized, CssRadialGradient radial,
        double pageHeightPt, double boxLeftPx, double boxTopPx, double boxWidthPx, double boxHeightPx,
        double rxCssPx, double ryCssPx, ComputedStyle style,
        (double LeftPx, double TopPx, double WidthPx, double HeightPx, CornerRadii Radii)? clipOverride)
    {
        if (boxWidthPx <= 0 || boxHeightPx <= 0) return null; // degenerate origin → no-op (not over-cap)
        var deviceW = (int)Math.Ceiling(boxWidthPx * ConicGradientRasterScale);
        var deviceH = (int)Math.Ceiling(boxHeightPx * ConicGradientRasterScale);
        var rstops = new List<NetPdf.Pdf.Images.RadialGradientRasterizer.Stop>(normalized.Count);
        foreach (var s in normalized)
            rstops.Add(new NetPdf.Pdf.Images.RadialGradientRasterizer.Stop(s.Offset, s.R, s.G, s.B, s.A));
        var result = NetPdf.Pdf.Images.RadialGradientRasterizer.TryRasterize(
            deviceW, deviceH,
            centerX: (float)(radial.CenterXFraction * boxWidthPx * ConicGradientRasterScale),
            centerY: (float)(radial.CenterYFraction * boxHeightPx * ConicGradientRasterScale),
            radiusX: (float)(rxCssPx * ConicGradientRasterScale),
            radiusY: (float)(ryCssPx * ConicGradientRasterScale),
            rstops);
        if (result is null) return false;
        var imageRef = document.RegisterImage(result);
        PlaceGradientRasterClipped(page, document, imageRef, pageHeightPt,
            boxLeftPx, boxTopPx, boxWidthPx, boxHeightPx, style, clipOverride);
        return true;
    }

    /// <summary>Place a gradient raster image over its box, clipped to the (rounded) clip box — the
    /// background-clip <paramref name="clipOverride"/> when present (multi-layer layer), else the box's
    /// own border-radius. Shared by the translucent linear + radial raster paths.</summary>
    private static void PlaceGradientRasterClipped(
        PdfPage page, PdfDocument document, NetPdf.Pdf.Objects.PdfIndirectRef imageRef, double pageHeightPt,
        double boxLeftPx, double boxTopPx, double boxWidthPx, double boxHeightPx, ComputedStyle style,
        (double LeftPx, double TopPx, double WidthPx, double HeightPx, CornerRadii Radii)? clipOverride)
    {
        ToPdfRect(boxLeftPx, boxTopPx, boxWidthPx, boxHeightPx, pageHeightPt, out var ix, out var iy, out var iw, out var ih);
        if (clipOverride is { } co)
        {
            ToPdfRect(co.LeftPx, co.TopPx, co.WidthPx, co.HeightPx, pageHeightPt, out var cx, out var cy, out var cw, out var ch);
            if (co.Radii.AnyPositive) page.BeginRoundedRectangleClip(cx, cy, cw, ch, ToPt(co.Radii));
            else page.BeginRectangleClip(cx, cy, cw, ch);
            page.PlaceImage(imageRef, ix, iy, iw, ih);
            page.RestoreGraphicsState();
            return;
        }
        var radiiPx = ReadCornerRadii(style, boxWidthPx, boxHeightPx);
        if (radiiPx.AnyPositive)
        {
            page.BeginRoundedRectangleClip(ix, iy, iw, ih, ToPt(radiiPx));
            page.PlaceImage(imageRef, ix, iy, iw, ih);
            page.RestoreGraphicsState();
        }
        else
        {
            page.BeginRectangleClip(ix, iy, iw, ih);
            page.PlaceImage(imageRef, ix, iy, iw, ih);
            page.RestoreGraphicsState();
        }
    }

    /// <summary>Phase 4 gradients — resolve a gradient's stops to <see cref="PdfGradientStop"/>
    /// (DeviceRGB + normalized offset). Colors resolve through the shared CSS color resolver
    /// (so named / hex / rgb()/hsl() all work; <c>currentColor</c> maps to the element color);
    /// an unresolvable stop is dropped. Positions follow CSS Images §3.4: a missing first → 0,
    /// missing last → 1, missing interior spread evenly between the nearest positioned stops,
    /// and the running max enforces non-decreasing offsets.</summary>
    private static List<PdfGradientStop> ResolveGradientStops(
        IReadOnlyList<CssGradientStop> gradientStops, uint currentColorArgb, double lineLengthCssPx,
        bool repeating = false)
    {
        var normalized = ResolveAndNormalizeStops(gradientStops, currentColorArgb, lineLengthCssPx, repeating);
        var result = new List<PdfGradientStop>(normalized.Count);
        foreach (var s in normalized) result.Add(new PdfGradientStop(s.Offset, s.R, s.G, s.B)); // native shading drops alpha
        return result;
    }

    private const int GradientMaxReplicatedStops = 1024; // safety cap on repeating-gradient expansion
    private const double GradientPeriodEpsilon = 1e-9;    // a period ≤ this is degenerate (average-color)

    /// <summary>Phase 4 gradients — one fully-resolved gradient stop: an offset (a fraction of the
    /// gradient line) + a premultiply-free DeviceRGB color with per-stop alpha.</summary>
    internal readonly record struct ResolvedGradientStop(double Offset, double R, double G, double B, double A);

    private const int HintSampleCount = 16; // interior samples approximating a hint's easing curve

    /// <summary>Mix two resolved stop colors in PREMULTIPLIED RGBA (CSS Images 3 §3.4.2) at fraction
    /// <paramref name="f"/>: the alpha is the linear interpolant, each color channel is alpha-weighted
    /// (premultiply → lerp → unpremultiply) so a (semi-)transparent endpoint doesn't bleed its RGB.
    /// Opaque-only inputs reduce to the plain RGB lerp. <paramref name="f"/> = 0 → <paramref name="a"/>,
    /// 1 → <paramref name="b"/>. Shared by the color-hint sampler + the boundary-stop interpolation.</summary>
    internal static (double R, double G, double B, double A) PremulMix(
        (double R, double G, double B, double A) a, (double R, double G, double B, double A) b, double f)
    {
        var aOut = a.A + (b.A - a.A) * f;
        if (aOut <= 0) return (0, 0, 0, 0);
        double Ch(double ca, double cb) => (ca * a.A + (cb * b.A - ca * a.A) * f) / aOut;
        return (Ch(a.R, b.R), Ch(a.G, b.G), Ch(a.B, b.B), aOut);
    }

    /// <summary>Color-interpolation HINT easing (CSS Images §3.4.2) — append the exponential transition
    /// between two color stops as a row of stops SAMPLING the curve. The color at segment-relative
    /// position <c>t ∈ [0, 1]</c> is <c>mix(c1, c2, t^p)</c> with <c>p = ln(0.5) / ln(H)</c>, where
    /// <c>H</c> is the hint's relative position in the segment — so the 50% color lands at the hint
    /// (<c>H → 0</c> / <c>1</c> degenerate to a hard edge). A stop is emitted at the EXACT hint position
    /// (PR #238 [P2]) — carrying the 50% color regardless of grid alignment — alongside an even
    /// <see cref="HintSampleCount"/>-step grid; between samples the PDF interpolates linearly (a fine
    /// approximation of the curve, NOT bit-exact). The two endpoint stops are appended by the caller;
    /// this adds only INTERIOR stops (premultiplied). A degenerate (coincident-position) segment adds
    /// nothing.</summary>
    private static void AppendHintSamples(
        List<ResolvedGradientStop> raw,
        (double R, double G, double B, double A) c1, double pos1,
        (double R, double G, double B, double A) c2, double pos2, double hintPos)
    {
        var span = pos2 - pos1;
        if (span <= 0) return; // endpoints coincide → nothing between them
        var h = Math.Clamp((hintPos - pos1) / span, 1e-6, 1.0 - 1e-6); // away from 0/1 (avoids inf / 0 power)
        var p = Math.Log(0.5) / Math.Log(h);                          // f(H) = H^p = 0.5
        // The even sample grid PLUS the exact hint position H (so the 50% color is pinned at the hint
        // even for a non-grid-aligned hint like 37%), sorted + de-duplicated for a monotonic stop list.
        var ts = new List<double>(HintSampleCount + 1);
        for (var k = 1; k < HintSampleCount; k++) ts.Add((double)k / HintSampleCount);
        ts.Add(h);
        ts.Sort();
        var prev = double.NegativeInfinity;
        foreach (var t in ts)
        {
            if (t - prev < 1e-6) continue; // skip a sample coincident with the hint stop
            prev = t;
            var c = PremulMix(c1, c2, Math.Pow(t, p));
            raw.Add(new ResolvedGradientStop(pos1 + t * span, c.R, c.G, c.B, c.A));
        }
    }

    /// <summary>Phase 4 gradients (PR 1 review) — the SHARED stop pipeline for linear / radial / conic.
    /// Resolves each stop's color + position (a length resolves against <paramref name="lineLengthCssPx"/>),
    /// applies the CSS Images §3.4 defaults (missing first → 0, last → 1, interior spread evenly) and the
    /// non-decreasing fixup WITHOUT clamping to [0, 1] (out-of-range stops are legal and shape the
    /// interpolation — PR 226 review [P1]). For a REPEATING gradient the PERIOD is
    /// <c>last − first</c> specified position (NOT just the last); prior + next cycles are tiled so the
    /// whole [0, 1] line is covered, and a zero-width period collapses to the average color. Finally the
    /// covering stops are CLIPPED to [0, 1] by inserting boundary stops whose colors are interpolated from
    /// the surrounding raw stops — so the visible PDF function / sweep only ever sees offsets in [0, 1].</summary>
    private static List<ResolvedGradientStop> ResolveAndNormalizeStops(
        IReadOnlyList<CssGradientStop> gradientStops, uint currentColorArgb, double lineLengthCssPx, bool repeating)
    {
        var n = gradientStops.Count;
        var col = new (double R, double G, double B, double A)[n];
        var pos = new double?[n];
        var ok = new bool[n];
        var isHint = new bool[n]; // a color-interpolation hint (§3.4.2): position only, no color
        for (var i = 0; i < n; i++)
        {
            var s = gradientStops[i];
            isHint[i] = s.IsHint;
            // A hint carries no color (it eases between its neighbors) — only resolve real stops.
            if (!s.IsHint)
            {
                var resolved = NetPdf.Css.ComputedValues.PropertyResolvers.ColorResolver.Resolve(
                    s.ColorRaw, PropertyId.Color, "color", diagnostics: null, location: default);
                if (TryResolveColor(resolved.Slot, currentColorArgb, out var argb))
                {
                    ColorChannels(argb, out var r, out var g, out var b);
                    col[i] = (r, g, b, Alpha(argb) / 255.0);
                    ok[i] = true;
                }
            }
            pos[i] = s.Position
                ?? (s.PositionPx is { } px && lineLengthCssPx > 0 ? px / lineLengthCssPx : (double?)null);
        }

        // §3.4 defaults + even interior spread (over ALL stops so an unresolved stop still anchors
        // positions), NOT clamped to [0, 1].
        if (pos[0] is null) pos[0] = 0.0;
        if (pos[n - 1] is null) pos[n - 1] = 1.0;
        for (var i = 1; i < n - 1; i++)
        {
            if (pos[i] is not null) continue;
            var j = i + 1;
            while (j < n && pos[j] is null) j++;
            var prev = pos[i - 1]!.Value;
            var next = pos[j]!.Value;
            var span = j - (i - 1);
            for (var k = i; k < j; k++)
                pos[k] = prev + (next - prev) * (k - (i - 1)) / span;
            i = j - 1;
        }
        // Non-decreasing fixup (CSS Images §3.4.3) — a stop never precedes the prior one — keeping raw,
        // possibly out-of-range positions (running starts at −∞ so a negative first stop survives).
        var fixedPos = new double[n];
        var running = double.NegativeInfinity;
        for (var i = 0; i < n; i++)
        {
            var p = pos[i]!.Value;
            if (p < running) p = running; else running = p;
            fixedPos[i] = p;
        }
        // Build the raw stop list: a real stop as-is; a color-interpolation HINT (§3.4.2) expands into a
        // row of stops SAMPLING the exponential transition between the bracketing color stops (a far
        // closer approximation than the old single-midpoint, with the exact 50% color pinned at the
        // hint). A hint at an edge, or one whose neighbor color doesn't resolve, contributes nothing (the
        // plain gradient still renders). No hints → the real stops only (byte-identical pre-hint path).
        var raw = new List<ResolvedGradientStop>(n);
        for (var i = 0; i < n; i++)
        {
            if (isHint[i])
            {
                if (i > 0 && i < n - 1 && ok[i - 1] && ok[i + 1])
                    AppendHintSamples(raw, col[i - 1], fixedPos[i - 1], col[i + 1], fixedPos[i + 1], fixedPos[i]);
                continue;
            }
            if (ok[i]) raw.Add(new ResolvedGradientStop(fixedPos[i], col[i].R, col[i].G, col[i].B, col[i].A));
        }
        if (raw.Count == 0) return raw;
        if (raw.Count == 1)
            return [raw[0] with { Offset = Math.Clamp(raw[0].Offset, 0.0, 1.0) }];

        var covering = repeating ? TileRepeatingStops(raw) : raw;
        return ClipStopsToUnitInterval(covering);
    }

    /// <summary>Tile a repeating gradient: the PERIOD is the last − first specified offset; cycles are
    /// generated shifted by every integer multiple of the period that overlaps [0, 1] (so the partial
    /// cycles before the first stop and after the last are present). A zero-width period (all stops
    /// coincident) collapses to the average color (CSS Images §3.4.3). Bounded by
    /// <see cref="GradientMaxReplicatedStops"/>.</summary>
    private static List<ResolvedGradientStop> TileRepeatingStops(List<ResolvedGradientStop> raw)
    {
        var first = raw[0].Offset;
        var last = raw[^1].Offset;
        var period = last - first;
        if (period <= GradientPeriodEpsilon)
        {
            double r = 0, g = 0, b = 0, a = 0;
            foreach (var s in raw) { r += s.R; g += s.G; b += s.B; a += s.A; }
            var avg = new ResolvedGradientStop(0, r / raw.Count, g / raw.Count, b / raw.Count, a / raw.Count);
            return [avg, avg with { Offset = 1.0 }]; // a solid fill (two coincident-color endpoints)
        }
        var kMin = (int)Math.Floor((0.0 - first) / period);
        var kMax = (int)Math.Ceiling((1.0 - last) / period);
        var covering = new List<ResolvedGradientStop>((kMax - kMin + 1) * raw.Count);
        for (var k = kMin; k <= kMax && covering.Count < GradientMaxReplicatedStops; k++)
        {
            var shift = k * period;
            foreach (var s in raw)
            {
                covering.Add(s with { Offset = s.Offset + shift });
                if (covering.Count >= GradientMaxReplicatedStops) break;
            }
        }
        return covering;
    }

    /// <summary>Clip non-decreasing <paramref name="stops"/> (offsets possibly outside [0, 1]) to the
    /// [0, 1] gradient line: a boundary stop at 0 and 1 carries the color INTERPOLATED from the
    /// surrounding raw stops (so a stop at −50px / 150px still tints the visible ends), and only the
    /// strictly-interior raw stops are kept between them.</summary>
    private static List<ResolvedGradientStop> ClipStopsToUnitInterval(List<ResolvedGradientStop> stops)
    {
        var (r0, g0, b0, a0) = ColorAt(stops, 0.0);
        var (r1, g1, b1, a1) = ColorAt(stops, 1.0);
        var res = new List<ResolvedGradientStop>(stops.Count + 2) { new(0.0, r0, g0, b0, a0) };
        foreach (var s in stops)
            if (s.Offset > 0.0 && s.Offset < 1.0) res.Add(s);
        res.Add(new ResolvedGradientStop(1.0, r1, g1, b1, a1));
        return res;
    }

    /// <summary>The interpolated color at offset <paramref name="t"/> along non-decreasing
    /// <paramref name="stops"/>. Outside the stop range the nearest end color is held; a zero-width
    /// (hard-stop) segment is skipped so the boundary lands on the segment just after it.</summary>
    internal static (double R, double G, double B, double A) ColorAt(List<ResolvedGradientStop> stops, double t)
    {
        if (t <= stops[0].Offset) return (stops[0].R, stops[0].G, stops[0].B, stops[0].A);
        var lastStop = stops[^1];
        if (t >= lastStop.Offset) return (lastStop.R, lastStop.G, lastStop.B, lastStop.A);
        for (var i = 0; i < stops.Count - 1; i++)
        {
            var a = stops[i];
            var b = stops[i + 1];
            if (a.Offset <= t && t <= b.Offset)
            {
                var span = b.Offset - a.Offset;
                if (span <= 0) continue; // hard stop — fall through to the next positive-width segment
                var f = (t - a.Offset) / span;
                // Interpolate in PREMULTIPLIED RGBA (CSS Images 3 §3.4.2; PR #237 review [P2]) so a
                // boundary stop between a transparent / semi-transparent color and an opaque one carries
                // the correct color (opaque-only segments are unaffected — byte-identical).
                return PremulMix(
                    (a.R, a.G, a.B, a.A), (b.R, b.G, b.B, b.A), f);
            }
        }
        return (lastStop.R, lastStop.G, lastStop.B, lastStop.A);
    }

    /// <summary>Phase 4 gradients — the gradient-line endpoints (offset 0 → 1) in PDF user
    /// space for a CSS <c>linear-gradient</c> <paramref name="angleDeg"/> (0° = "to top",
    /// clockwise) over the box rect (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="w"/>, <paramref name="h"/>, PDF points, bottom-left origin). The line
    /// passes through the box center with length |w·sinθ| + |h·cosθ| (CSS Images §3.1); the CSS
    /// y-down direction (sinθ, −cosθ) flips to PDF y-up (sinθ, cosθ).</summary>
    private static (double X0, double Y0, double X1, double Y1) LinearGradientAxis(
        double angleDeg, double x, double y, double w, double h)
    {
        var theta = angleDeg * Math.PI / 180.0;
        var sin = Math.Sin(theta);
        var cos = Math.Cos(theta);
        var len = Math.Abs(w * sin) + Math.Abs(h * cos);
        var cx = x + w / 2.0;
        var cy = y + h / 2.0;
        var hx = len / 2.0 * sin;     // PDF dir = (sinθ, cosθ)
        var hy = len / 2.0 * cos;
        return (cx - hx, cy - hy, cx + hx, cy + hy);
    }

    /// <summary>Phase 4 gradients (PR #209 review [P2]) — the CSS <c>to &lt;corner&gt;</c>
    /// gradient-line angle (0° = "to top", clockwise) for a box <paramref name="w"/> wide ×
    /// <paramref name="h"/> tall. CSS Images L3 §3.1: the gradient line points into the corner's
    /// quadrant AND is perpendicular to the diagonal joining the two NEIGHBORING corners, so the
    /// angle is aspect-ratio dependent — a fixed 45° is correct only for a square box. For
    /// <c>to top right</c> the line is parallel to (h, −w) ⇒ angle = atan2(h, w); the other three
    /// corners reflect that base angle. Width/height in any consistent unit (the ratio is what
    /// matters). Pure — unit-tested directly.</summary>
    internal static double CornerAngleDeg(LinearGradientCorner corner, double w, double h)
    {
        var a = Math.Atan2(h, w) * 180.0 / Math.PI; // the `to top right` angle, in (0, 90)
        return corner switch
        {
            LinearGradientCorner.TopRight => a,
            LinearGradientCorner.BottomRight => 180.0 - a,
            LinearGradientCorner.BottomLeft => 180.0 + a,
            _ => 360.0 - a, // TopLeft
        };
    }

    /// <summary>Phase 4 gradients — paint a parsed <c>radial-gradient(...)</c> as a PDF native
    /// radial shading (ISO 32000-2 §8.7.4.5.4) clipped to the box. The center comes from the
    /// gradient's box-relative fractions; the per-axis ending radii from the shape + extent keyword
    /// (closest/farthest side/corner) measured against the box. An ELLIPSE (the default) is rendered
    /// by registering a CIRCLE of the larger radius and squashing the other axis with a CTM scale
    /// about the center (PR 1 refinements); a circle (or a centered ellipse on a square box) emits no
    /// CTM — byte-identical with the pre-ellipse output.</summary>
    private static void PaintRadialGradient(
        PdfPage page, PdfDocument document, CssRadialGradient radial, ComputedStyle style,
        double pageHeightPt, double leftPx, double topPx, double widthPx, double heightPx,
        uint currentColorArgb, IDiagnosticsSink? diagnostics, ref bool gradientAlphaCapReported,
        // box-decoration-break: slice — when set, the gradient's center + radius + the rounded CLIP are
        // computed over this (larger, whole-box / composite) extent instead of the painted slice, so a
        // sliced radial gradient is CONTINUOUS + rounds correctly; the CALLER adds the per-slice rect clip
        // (PR #223 review [P1]). Null → the box itself (byte-identical).
        double? axisTopPx = null, double? axisHeightPx = null,
        // Multi-layer backgrounds (PR #235 review [P2]) — when set, the box args are the background-ORIGIN
        // box (center + radius span it) while the shading is CLIPPED to THIS rect (the background-clip box,
        // rounded). Null → clip == axis box with the style radii (single-layer path, byte-identical).
        (double LeftPx, double TopPx, double WidthPx, double HeightPx, CornerRadii Radii)? clipOverride = null)
    {
        // CLIP rect = AXIS rect = the whole (composite) decoration box — the center + radius + radii are
        // measured against it for slice continuity; the radii resolve against the COMPOSITE height.
        var clipHeightPx = axisHeightPx ?? heightPx;
        ToPdfRect(leftPx, axisTopPx ?? topPx, widthPx, clipHeightPx, pageHeightPt,
            out var ax, out var ay, out var aw, out var ah);
        // Center in PDF user space (cy is a fraction from the CSS TOP → flip for PDF y-up).
        var pcx = ax + radial.CenterXFraction * aw;
        var pcy = ay + (1.0 - radial.CenterYFraction) * ah;
        // Per-axis extents from the center to the near/far sides.
        var minX = Math.Min(radial.CenterXFraction, 1.0 - radial.CenterXFraction) * aw;
        var maxX = Math.Max(radial.CenterXFraction, 1.0 - radial.CenterXFraction) * aw;
        var minY = Math.Min(radial.CenterYFraction, 1.0 - radial.CenterYFraction) * ah;
        var maxY = Math.Max(radial.CenterYFraction, 1.0 - radial.CenterYFraction) * ah;
        // The ending-shape radii (CSS Images L3 §3.2). A CIRCLE uses a single scalar extent; an
        // ELLIPSE (the default) is per-axis. For a corner ellipse the closest/farthest-side ellipse's
        // aspect ratio is scaled to pass through the corner — and since the corner's component
        // distances equal those sides' distances here (axis-aligned box, center inside), that scale
        // is exactly √2 on each axis (rx = sideX·√2, ry = sideY·√2).
        double rx, ry;
        if (radial.IsCircle)
        {
            var radius = radial.Extent switch
            {
                RadialExtent.ClosestSide => Math.Min(minX, minY),
                RadialExtent.FarthestSide => Math.Max(maxX, maxY),
                RadialExtent.ClosestCorner => Math.Sqrt(minX * minX + minY * minY),
                _ => Math.Sqrt(maxX * maxX + maxY * maxY), // FarthestCorner (default)
            };
            rx = ry = radius;
        }
        else
        {
            const double sqrt2 = 1.4142135623730951;
            (rx, ry) = radial.Extent switch
            {
                RadialExtent.ClosestSide => (minX, minY),
                RadialExtent.FarthestSide => (maxX, maxY),
                RadialExtent.ClosestCorner => (minX * sqrt2, minY * sqrt2),
                _ => (maxX * sqrt2, maxY * sqrt2), // FarthestCorner (default)
            };
        }
        // Register the shading as a CIRCLE of the larger radius, then squash the other axis with a
        // CTM (scale about the center) so a non-circular ellipse renders correctly. rx == ry (a
        // circle, or a centered ellipse on a square box) needs no CTM → byte-identical with the
        // pre-ellipse circular output.
        var baseRadius = Math.Max(rx, ry);
        if (!(baseRadius > 0)) return;

        // A length-positioned stop resolves against the gradient RAY — the rightward radius rx, where
        // it intersects the ending shape (CSS Images §3.2; PR 226 review [P2] — NOT max(rx, ry), which
        // placed stops too early on tall/narrow ellipses). rx is in pt, homogeneous degree 1, so
        // rxCssPx = rx / PointsPerPixel.
        var rxCssPx = rx / PdfUnits.PointsPerPixel;
        var normalized = ResolveAndNormalizeStops(
            radial.Stops, currentColorArgb, rxCssPx, radial.Repeating);
        if (normalized.Count < 2) return;

        // Per-stop alpha (gradient refinements) — a translucent stop falls back to a Skia raster (the
        // native radial shading is DeviceRGB); a fully-opaque gradient stays native (byte-identical).
        if (AnyTranslucent(normalized))
        {
            var painted = TryPaintAlphaRadialGradientRaster(page, document, normalized, radial, pageHeightPt,
                leftPx, axisTopPx ?? topPx, widthPx, clipHeightPx, rxCssPx, ry / PdfUnits.PointsPerPixel,
                style, clipOverride);
            // false = over-cap → skip + warn (not a native fallback that would drop the alpha, PR #237
            // [P1]); null = degenerate origin → silent no-op (not over-cap, PR #238 [P3]).
            if (painted == false) EmitGradientAlphaOverCap(diagnostics, ref gradientAlphaCapReported);
            return;
        }

        var stops = new List<PdfGradientStop>(normalized.Count);
        foreach (var s in normalized) stops.Add(new PdfGradientStop(s.Offset, s.R, s.G, s.B)); // native drops alpha
        var shadingRef = document.RegisterRadialShading(pcx, pcy, 0.0, baseRadius, stops);
        (double, double, double, double, double, double)? shadingCtm = null;
        if (rx != ry)
        {
            var sx = rx / baseRadius;
            var sy = ry / baseRadius;
            shadingCtm = (sx, 0.0, 0.0, sy, pcx * (1 - sx), pcy * (1 - sy));
        }
        if (clipOverride is { } co)
        {
            ToPdfRect(co.LeftPx, co.TopPx, co.WidthPx, co.HeightPx, pageHeightPt, out var cx, out var cy, out var cw, out var ch);
            page.PaintShadingInRect(shadingRef, cx, cy, cw, ch,
                co.Radii.AnyPositive ? ToPt(co.Radii) : (CornerRadii?)null, shadingCtm: shadingCtm);
            return;
        }
        var radiiPx = ReadCornerRadii(style, widthPx, clipHeightPx);
        page.PaintShadingInRect(shadingRef, ax, ay, aw, ah,
            radiiPx.AnyPositive ? ToPt(radiiPx) : (CornerRadii?)null, shadingCtm: shadingCtm);
    }

    private const double ConicGradientRasterScale = 2.0; // device px per CSS px for the conic raster

    /// <summary>Phase 4 gradients (PR 1 refinements) — paint a parsed <c>conic-gradient(...)</c> /
    /// <c>repeating-conic-gradient(...)</c> background layer. PDF has no native conic shading, so the
    /// sweep is rasterized via Skia at <see cref="ConicGradientRasterScale"/>× the box size and placed
    /// as an image XObject (RGB + alpha <c>/SMask</c>) clipped to the (rounded) border box —
    /// preserving per-stop alpha. A gradient with &lt; 2 resolvable stops is skipped; an over-cap
    /// bitmap is skipped with <c>CSS-CONIC-GRADIENT-RASTER-001</c> (the background-color shows).</summary>
    private static void PaintConicGradient(
        PdfPage page, PdfDocument document, CssConicGradient conic, ComputedStyle style,
        double pageHeightPt, double leftPx, double topPx, double widthPx, double heightPx,
        uint currentColorArgb, IDiagnosticsSink? diagnostics, ref bool rasterReported, ref bool capReported,
        // box-decoration-break: slice — the sweep is rasterized over the WHOLE (composite) box for
        // continuity; the CALLER adds the per-slice rect clip. Null → the box itself (byte-identical).
        double? axisTopPx = null, double? axisHeightPx = null,
        // Multi-layer backgrounds (PR #235 review [P2]) — when set, the box args are the background-ORIGIN
        // box (the sweep raster covers it) while the placed image is CLIPPED to THIS rect (the
        // background-clip box, rounded). Null → clip == the box with the style radii (byte-identical).
        (double LeftPx, double TopPx, double WidthPx, double HeightPx, CornerRadii Radii)? clipOverride = null)
    {
        var stops = ResolveConicStops(conic, currentColorArgb, conic.Repeating);
        if (stops.Count < 2) return;

        var boxTopPx = axisTopPx ?? topPx;
        var boxHeightPx = axisHeightPx ?? heightPx;
        if (widthPx <= 0 || boxHeightPx <= 0) return;

        var deviceW = (int)Math.Ceiling(widthPx * ConicGradientRasterScale);
        var deviceH = (int)Math.Ceiling(boxHeightPx * ConicGradientRasterScale);
        var result = NetPdf.Pdf.Images.ConicGradientRasterizer.TryRasterize(
            deviceW, deviceH,
            centerX: (float)(conic.CenterXFraction * deviceW),
            centerY: (float)(conic.CenterYFraction * deviceH),
            conic.FromAngleDeg, stops);

        if (result is null)
        {
            // Over-cap / degenerate raster → skip (the background-color shows) but SURFACE it as a
            // Warning under a DISTINCT code (the raster-fallback code stays Info-only — PR 226 [P2]).
            if (!capReported)
            {
                diagnostics?.Emit(new Diagnostic(
                    DiagnosticCodes.CssConicGradientUnsupported001,
                    "A conic-gradient was too large to rasterize (the sweep bitmap would exceed the "
                    + $"{NetPdf.Pdf.Images.ShadowRasterizer.MaxDeviceDimension} px cap); the "
                    + "background-color shows instead.",
                    DiagnosticSeverity.Warning));
                capReported = true;
            }
            return;
        }

        var imageRef = document.RegisterImage(result);
        ToPdfRect(leftPx, boxTopPx, widthPx, boxHeightPx, pageHeightPt, out var x, out var y, out var w, out var h);
        if (clipOverride is { } co)
        {
            // The raster covers the ORIGIN box (x,y,w,h); clip the placement to the background-clip box
            // (always clip, even non-rounded, so a tighter content-box clip crops the sweep).
            ToPdfRect(co.LeftPx, co.TopPx, co.WidthPx, co.HeightPx, pageHeightPt, out var cx, out var cy, out var cw, out var ch);
            if (co.Radii.AnyPositive) page.BeginRoundedRectangleClip(cx, cy, cw, ch, ToPt(co.Radii));
            else page.BeginRectangleClip(cx, cy, cw, ch);
            page.PlaceImage(imageRef, x, y, w, h);
            page.RestoreGraphicsState();
        }
        else
        {
            var radiiPx = ReadCornerRadii(style, widthPx, boxHeightPx);
            var rounded = radiiPx.AnyPositive;
            if (rounded) page.BeginRoundedRectangleClip(x, y, w, h, ToPt(radiiPx));
            page.PlaceImage(imageRef, x, y, w, h);
            if (rounded) page.RestoreGraphicsState();
        }

        if (!rasterReported)
        {
            diagnostics?.Emit(new Diagnostic(
                DiagnosticCodes.CssConicGradientRaster001,
                "A conic-gradient was painted via the Skia raster fallback (PDF has no native conic "
                + $"shading); the sweep was rasterized at {ConicGradientRasterScale:0}× and placed as "
                + "an image XObject with an alpha /SMask.",
                DiagnosticSeverity.Info));
            rasterReported = true;
        }
    }

    /// <summary>Phase 4 gradients — resolve a conic gradient's stops to the rasterizer's
    /// (turn-fraction, RGBA) form via the shared <see cref="ResolveAndNormalizeStops"/> pipeline,
    /// KEEPING per-stop alpha (the raster carries an /SMask). Conic positions are angular (turn
    /// fractions), so there is no length resolution (lineLength = 0); the §3.4 defaults, the
    /// <c>last − first</c> repeating period, the out-of-range handling, and the [0, 1] clip are all
    /// shared with linear / radial (PR 226 review [P1]).</summary>
    private static List<NetPdf.Pdf.Images.ConicGradientRasterizer.Stop> ResolveConicStops(
        CssConicGradient conic, uint currentColorArgb, bool repeating)
    {
        var normalized = ResolveAndNormalizeStops(conic.Stops, currentColorArgb, lineLengthCssPx: 0.0, repeating);
        var result = new List<NetPdf.Pdf.Images.ConicGradientRasterizer.Stop>(normalized.Count);
        foreach (var s in normalized)
            result.Add(new NetPdf.Pdf.Images.ConicGradientRasterizer.Stop(s.Offset, s.R, s.G, s.B, s.A));
        return result;
    }

    /// <summary>The four <c>border-radius</c> corners in CSS px (border-radius-completion cycle): each
    /// registered corner longhand read as an ABSOLUTE length (circular, X == Y) or a PERCENTAGE, which
    /// resolves against the box WIDTH for the horizontal radius and the box HEIGHT for the vertical
    /// (CSS B&amp;B 3 §4.1 — so a non-square box gets an ellipse). The explicit two-radii `Rx / Ry`
    /// spelling is dropped by AngleSharp upstream (all-zero → square, a documented deferral). §4.2
    /// overlap clamping happens later, in <see cref="CornerRadii.NormalizedFor"/>.</summary>
    internal static CornerRadii ReadCornerRadii(ComputedStyle style, double widthPx, double heightPx)
    {
        static double ResolveAxis(ComputedSlot slot, double extentPx) => slot.Tag switch
        {
            ComputedSlotTag.LengthPx => Math.Max(0, slot.AsLengthPx()),
            ComputedSlotTag.Percentage => Math.Max(0, slot.AsPercentage() / 100.0 * extentPx),
            _ => 0.0,
        };
        (double Rx, double Ry) Corner(PropertyId horizontalId, PropertyId verticalId)
        {
            var hSlot = style.Get(horizontalId);
            var rx = ResolveAxis(hSlot, widthPx);
            // The vertical radius comes from the INTERNAL `-netpdf-border-{corner}-radius-y` longhand
            // (the elliptical `Rx / Ry` slash form — border-radius-elliptical cycle) WHEN SET; otherwise
            // it falls back to the HORIZONTAL longhand resolved against the box HEIGHT — a circular
            // length gives X == Y, a percentage the §4.1 ellipse (the pre-cycle behavior).
            var vSlot = style.Get(verticalId);
            var ry = vSlot.Tag is ComputedSlotTag.LengthPx or ComputedSlotTag.Percentage
                ? ResolveAxis(vSlot, heightPx)
                : ResolveAxis(hSlot, heightPx);
            return (rx, ry);
        }
        var (tlX, tlY) = Corner(PropertyId.BorderTopLeftRadius, PropertyId.BorderTopLeftRadiusY);
        var (trX, trY) = Corner(PropertyId.BorderTopRightRadius, PropertyId.BorderTopRightRadiusY);
        var (brX, brY) = Corner(PropertyId.BorderBottomRightRadius, PropertyId.BorderBottomRightRadiusY);
        var (blX, blY) = Corner(PropertyId.BorderBottomLeftRadius, PropertyId.BorderBottomLeftRadiusY);
        return new CornerRadii(tlX, tlY, trX, trY, brX, brY, blX, blY);
    }

    /// <summary>Convert a px <see cref="CornerRadii"/> to PDF points (the unit PdfPage's path builder
    /// works in); the §4.2 overlap scaling is unit-invariant so it still happens in PdfPage.</summary>
    internal static CornerRadii ToPt(CornerRadii px) => new(
        PdfUnits.PxToPt(px.TopLeftX), PdfUnits.PxToPt(px.TopLeftY),
        PdfUnits.PxToPt(px.TopRightX), PdfUnits.PxToPt(px.TopRightY),
        PdfUnits.PxToPt(px.BottomRightX), PdfUnits.PxToPt(px.BottomRightY),
        PdfUnits.PxToPt(px.BottomLeftX), PdfUnits.PxToPt(px.BottomLeftY));

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

        // Phase 4 PR 3 — every border-style is now rendered on the square per-edge path (dashed /
        // dotted strokes; double + the 3D groove/ridge/inset/outset bands), so no approximation
        // diagnostic fires here (the uniform-ROUNDED ring path still approximates non-solid — a
        // documented residual). The `styleApproximationReported` flag is shared with that path + outlines.
        _ = styleApproximationReported;

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
        var a = alpha / 255.0;
        switch (styleKeyword)
        {
            case BorderStyleDashed:
            case BorderStyleDotted:
                // Stroke the edge CENTERLINE with line-width = the edge width and a dash pattern.
                StrokeDashedEdge(page, edge, edgeLeftPx, edgeTopPx, edgeBoxWidthPx, edgeBoxHeightPx,
                    edgeWidthPx, pageHeightPt, r, g, b, a, dotted: styleKeyword == BorderStyleDotted);
                return;
            case BorderStyleDouble:
            case BorderStyleGroove:
            case BorderStyleRidge:
            case BorderStyleInset:
            case BorderStyleOutset:
                PaintStyledBorderEdge(page, edge, edgeLeftPx, edgeTopPx, edgeBoxWidthPx, edgeBoxHeightPx,
                    edgeWidthPx, pageHeightPt, styleKeyword, r, g, b, a);
                return;
            default: // solid
                ToPdfRect(edgeLeftPx, edgeTopPx, edgeBoxWidthPx, edgeBoxHeightPx, pageHeightPt,
                    out var x, out var y, out var w, out var h);
                // A partial border-color alpha is composited via the page's constant-alpha (/ca).
                page.FillRectangle(x, y, w, h, r, g, b, a);
                return;
        }
    }

    /// <summary>Phase 4 borders (PR 3) — paint a <c>double</c> / 3D (<c>groove</c>/<c>ridge</c>/
    /// <c>inset</c>/<c>outset</c>) border edge (CSS B&amp;B §4.3) on the square per-edge path.
    /// <c>double</c> = two solid bands (outer + inner thirds, the middle third a gap). The 3D styles
    /// use a DARK (×0.5) and LIGHT (toward white) shade of the edge color: <c>inset</c> = top/left
    /// dark, bottom/right light (sunken); <c>outset</c> = the inverse (raised); <c>groove</c> = outer
    /// half inset-shaded + inner half outset-shaded (carved); <c>ridge</c> = the inverse.</summary>
    private static void PaintStyledBorderEdge(
        PdfPage page, BorderEdge edge, double edgeLeftPx, double edgeTopPx,
        double edgeBoxWidthPx, double edgeBoxHeightPx, double edgeWidthPx, double pageHeightPt,
        int styleKeyword, double r, double g, double b, double alpha)
    {
        // Fill a sub-band from fromFrac → toFrac of the edge thickness (0 = OUTER box edge, 1 = inner).
        void Band(double fromFrac, double toFrac, double br, double bg, double bb)
        {
            double sl, st, sw, sh;
            switch (edge)
            {
                case BorderEdge.Top:
                    sl = edgeLeftPx; sw = edgeBoxWidthPx;
                    st = edgeTopPx + fromFrac * edgeWidthPx; sh = (toFrac - fromFrac) * edgeWidthPx; break;
                case BorderEdge.Bottom:
                    sl = edgeLeftPx; sw = edgeBoxWidthPx;
                    st = edgeTopPx + (1 - toFrac) * edgeWidthPx; sh = (toFrac - fromFrac) * edgeWidthPx; break;
                case BorderEdge.Left:
                    st = edgeTopPx; sh = edgeBoxHeightPx;
                    sl = edgeLeftPx + fromFrac * edgeWidthPx; sw = (toFrac - fromFrac) * edgeWidthPx; break;
                default: // Right
                    st = edgeTopPx; sh = edgeBoxHeightPx;
                    sl = edgeLeftPx + (1 - toFrac) * edgeWidthPx; sw = (toFrac - fromFrac) * edgeWidthPx; break;
            }
            ToPdfRect(sl, st, sw, sh, pageHeightPt, out var x, out var y, out var w, out var h);
            page.FillRectangle(x, y, w, h, br, bg, bb, alpha);
        }

        double dr = r * 0.5, dg = g * 0.5, db = b * 0.5;             // dark shade
        double lr = 0.5 + 0.5 * r, lg = 0.5 + 0.5 * g, lb = 0.5 + 0.5 * b; // light shade
        var topLeft = edge is BorderEdge.Top or BorderEdge.Left;
        switch (styleKeyword)
        {
            case BorderStyleDouble:
                Band(0, 1.0 / 3.0, r, g, b);
                Band(2.0 / 3.0, 1.0, r, g, b);
                break;
            case BorderStyleInset:
                if (topLeft) Band(0, 1, dr, dg, db); else Band(0, 1, lr, lg, lb);
                break;
            case BorderStyleOutset:
                if (topLeft) Band(0, 1, lr, lg, lb); else Band(0, 1, dr, dg, db);
                break;
            case BorderStyleGroove:
                if (topLeft) { Band(0, 0.5, dr, dg, db); Band(0.5, 1, lr, lg, lb); }
                else { Band(0, 0.5, lr, lg, lb); Band(0.5, 1, dr, dg, db); }
                break;
            case BorderStyleRidge:
                if (topLeft) { Band(0, 0.5, lr, lg, lb); Band(0.5, 1, dr, dg, db); }
                else { Band(0, 0.5, dr, dg, db); Band(0.5, 1, lr, lg, lb); }
                break;
        }
    }

    /// <summary>Phase 4 borders (PR 3) — stroke a dashed / dotted border edge: a centerline along the
    /// edge's long axis, line-width = the edge width, with a dash pattern. Dashed → <c>[3w 3w]</c> butt
    /// caps. Dotted → a ZERO-length on-dash with ROUND caps (<c>[0 2w]</c>): a round cap on a 0-length
    /// dash renders a FILLED CIRCLE of diameter <c>w</c> (a true dot), spaced <c>2w</c> centre-to-centre
    /// (≈ one dot-diameter gap). <c>[w w]</c> with round caps would instead extend each dash by a
    /// half-cap on both ends → capsule / pill marks, not dots (PR #228 review P2). <c>w</c> = the edge
    /// width in pt. The exact spacing is a browser-tunable approximation (the visual-regression harness
    /// refines it).</summary>
    private static void StrokeDashedEdge(
        PdfPage page, BorderEdge edge, double edgeLeftPx, double edgeTopPx,
        double edgeBoxWidthPx, double edgeBoxHeightPx, double edgeWidthPx, double pageHeightPt,
        double r, double g, double b, double alpha, bool dotted)
    {
        var wPt = PdfUnits.PxToPt(edgeWidthPx);
        if (!(wPt > 0)) return;
        double x1Px, y1Px, x2Px, y2Px;
        if (edge is BorderEdge.Top or BorderEdge.Bottom)
        {
            var cy = edgeTopPx + edgeBoxHeightPx / 2.0; // edgeBoxHeightPx == the edge width
            x1Px = edgeLeftPx; x2Px = edgeLeftPx + edgeBoxWidthPx; y1Px = y2Px = cy;
        }
        else
        {
            var cx = edgeLeftPx + edgeBoxWidthPx / 2.0;  // edgeBoxWidthPx == the edge width
            x1Px = x2Px = cx; y1Px = edgeTopPx; y2Px = edgeTopPx + edgeBoxHeightPx;
        }
        var x1 = PdfUnits.PxToPt(x1Px);
        var y1 = pageHeightPt - PdfUnits.PxToPt(y1Px);
        var x2 = PdfUnits.PxToPt(x2Px);
        var y2 = pageHeightPt - PdfUnits.PxToPt(y2Px);
        // Dotted: [0 2w] round caps → a 0-length dash becomes a round dot of diameter w, spaced 2w
        // centre-to-centre (one dot-diameter gap). Dashed: [3w 3w] butt caps.
        var dash = dotted ? new[] { 0.0, 2 * wPt } : new[] { 3 * wPt, 3 * wPt };
        page.StrokeLine(x1, y1, x2, y2, wPt, r, g, b, alpha, dash, dashPhase: 0.0, lineCap: dotted ? 1 : 0);
    }

    /// <summary>multicol-balancing-pagination (column rules, CSS Multi-column L1 §5) — fill a
    /// synthetic <see cref="BoxFragment.IsColumnRule"/> fragment's rect with the multicol
    /// container's <c>column-rule-color</c> (<c>currentcolor</c> — the initial — resolves to the
    /// element <c>color</c>). The rect (position, the rule WIDTH, and the rule HEIGHT) is computed
    /// by <c>MulticolLayouter</c>; the style keyword is read here only to skip a defensive
    /// `none`/`hidden` and to flag a non-solid style as approximated-to-solid (sharing the
    /// once-per-conversion border/outline diagnostic). Mirrors <see cref="PaintBorderEdge"/>.</summary>
    private static void PaintColumnRule(
        PdfPage page, ComputedStyle style, double pageHeightPt,
        double leftPx, double topPx, double widthPx, double heightPx,
        uint currentColorArgb, IDiagnosticsSink? diagnostics,
        ref bool styleApproximationReported)
    {
        var styleSlot = style.Get(PropertyId.ColumnRuleStyle);
        var styleKeyword = styleSlot.Tag == ComputedSlotTag.Keyword ? styleSlot.AsKeyword() : BorderStyleNone;
        if (styleKeyword is BorderStyleNone or BorderStyleHidden) return; // defensive (layouter gated).

        if (!TryResolveColor(style.Get(PropertyId.ColumnRuleColor), currentColorArgb, out var argb))
            argb = currentColorArgb; // column-rule-color initial is currentcolor.
        var alpha = Alpha(argb);
        if (alpha == 0) return;

        if (styleKeyword != BorderStyleSolid && !styleApproximationReported)
        {
            diagnostics?.Emit(new Diagnostic(
                DiagnosticCodes.PaintBorderStyleApproximated001,
                "A non-solid column-rule-style (dotted / dashed / double / groove / ridge / inset / outset) " +
                "was painted as a solid line. Styled rule rendering is a tracked follow-up " +
                "(deferrals.md#multicol-balancing-pagination).",
                DiagnosticSeverity.Info));
            styleApproximationReported = true;
        }

        ColorChannels(argb, out var r, out var g, out var b);
        ToPdfRect(leftPx, topPx, widthPx, heightPx, pageHeightPt, out var x, out var y, out var w, out var h);
        // A partial column-rule-color alpha is composited via the page's ExtGState constant-alpha (/ca).
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

    internal static uint ResolveCurrentColor(ComputedStyle style)
    {
        var slot = style.Get(PropertyId.Color);
        return slot.Tag == ComputedSlotTag.Color ? slot.AsColor() : DefaultColorArgb;
    }
}
