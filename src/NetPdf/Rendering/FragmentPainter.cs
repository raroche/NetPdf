// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
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
/// <b>Borders.</b> Only <c>solid</c> is rendered faithfully; the other painted
/// styles (<c>dotted</c> / <c>dashed</c> / <c>double</c> / <c>groove</c> /
/// <c>ridge</c> / <c>inset</c> / <c>outset</c>) are approximated as a solid fill of
/// the border color and surfaced via <c>PAINT-BORDER-STYLE-APPROXIMATED-001</c>.
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
    private const int BorderStyleSolid = 4;

    // outline-style keyword ids (outline cycle; post-PR-#173 review P2): CSS UI 4 §5.2 admits the
    // border-style values EXCEPT `hidden`, PLUS `auto`. The KeywordResolver table is
    // none, dotted, dashed, solid, double, groove, ridge, inset, outset, auto — so the indices differ
    // from border-style (no hidden ⇒ solid is 3, not 4). `none` paints nothing; `auto` paints solid.
    private const int OutlineStyleNone = 0;
    private const int OutlineStyleSolid = 3;
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
            (double A, double B, double C, double D, double E, double F) effCm = default;
            var transformed = effectiveTransforms is not null
                && effectiveTransforms.TryGetValue(fragment.Box, out effCm);
            if (transformed) page.BeginTransform(effCm.A, effCm.B, effCm.C, effCm.D, effCm.E, effCm.F);

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
                // layer), as a PDF native axial shading clipped to the (rounded) border box.
                // inline-only-block-line-splitting (box-decoration-break: slice) — when this fragment is one
                // block-axis slice of a larger box (DecorationBlockExtentPx > 0), paint the gradient's AXIS
                // over the WHOLE box (virtual top = this slice's top − its offset within the box, height =
                // the full box extent) so it's CONTINUOUS across slices, while the shading is CLIPPED to the
                // slice's own box. Null → the slice's own box (every non-sliced fragment byte-identical).
                double? axisTopPx = null, axisHeightPx = null;
                var gradientSliced = fragment.DecorationBlockExtentPx > 0;
                if (gradientSliced)
                {
                    axisTopPx = topPx - fragment.DecorationBlockOffsetPx;
                    axisHeightPx = fragment.DecorationBlockExtentPx;
                }
                // box-decoration-break: slice — the shading + its rounded clip span the WHOLE composite box;
                // an outer per-slice rect clip limits the paint to this fragment (no-op for a non-slice).
                var gradientSliceClipped = false;
                if (gradientSliced
                    && imageCache is not null && document is not null
                    && (imageCache.BackgroundGradientBoxes.ContainsKey(fragment.Box)
                        || imageCache.BackgroundRadialGradientBoxes.ContainsKey(fragment.Box)))
                {
                    ToPdfRect(leftPx, topPx, widthPx, heightPx, pageHeightPt, out var gcx, out var gcy, out var gcw, out var gch);
                    page.BeginRectangleClip(gcx, gcy, gcw, gch);
                    gradientSliceClipped = true;
                }
                if (imageCache is not null && document is not null
                    && imageCache.BackgroundGradientBoxes.TryGetValue(fragment.Box, out var gradient))
                {
                    PaintLinearGradient(page, document, gradient, style, pageHeightPt,
                        leftPx, topPx, widthPx, heightPx, currentColorArgb, axisTopPx, axisHeightPx);
                }
                else if (imageCache is not null && document is not null
                    && imageCache.BackgroundRadialGradientBoxes.TryGetValue(fragment.Box, out var radial))
                {
                    PaintRadialGradient(page, document, radial, style, pageHeightPt,
                        leftPx, topPx, widthPx, heightPx, currentColorArgb, axisTopPx, axisHeightPx);
                }
                if (gradientSliceClipped) page.RestoreGraphicsState();
            }

            // Borders (foreground — always painted regardless of PrintBackgrounds). A sliced inline-only
            // block (box-decoration-break: slice) draws each block-axis border once: the block-start (top)
            // border edge is skipped on a non-first slice, the block-end (bottom) on a non-last slice.
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

            if (transformed) page.RestoreGraphicsState(); // balance BeginTransform's q
        }
    }

    /// <summary>Per-fragment tile-count threshold ABOVE which the tiling emits ONE PDF
    /// tiling-pattern fill instead of per-tile placements (tiling-patterns cycle, ISO 32000-2
    /// §8.7.3 — O(1) content-stream size regardless of count, so the old 4096-tile cap +
    /// <c>PAINT-BG-IMAGE-TILE-CAP-001</c> are RETIRED; a 1×1 px tile over a full page is now
    /// one pattern object). At or below the threshold the per-tile loop stays — fewer bytes
    /// than a pattern object for small grids, and every pre-cycle stream is byte-identical.</summary>
    private const int MaxLoopTilesPerFragment = 16;

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
            && TryUniformBorder(style, currentColors, out var borderWidthPx, out var borderArgb, out var nonSolid))
        {
            if (nonSolid && !styleApproximationReported)
            {
                diagnostics?.Emit(new Diagnostic(
                    DiagnosticCodes.PaintBorderStyleApproximated001,
                    "A non-solid border-style (dotted / dashed / double / groove / ridge / inset / outset) " +
                    "was painted as a solid line. Styled border rendering is a tracked follow-up " +
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
            ToPdfRect(leftPx, compTopPx, widthPx, compHeightPx, pageHeightPt, out var ox, out var oy, out var ow, out var oh);
            ToPdfRect(leftPx + borderWidthPx, compTopPx + borderWidthPx,
                widthPx - 2 * borderWidthPx, compHeightPx - 2 * borderWidthPx,
                pageHeightPt, out var ix, out var iy, out var iw, out var ih);
            ColorChannels(borderArgb, out var cr, out var cg, out var cb);
            page.FillRoundedRectangleRing(ox, oy, ow, oh, ToPt(outerRadiiPx),
                ix, iy, iw, ih, ToPt(innerRadiiPx), cr, cg, cb, Alpha(borderArgb) / 255.0);
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

        // `auto` paints solid (the UA's choice) WITHOUT a diagnostic; only a genuine non-solid LINE style
        // (dotted / dashed / double / groove / ridge / inset / outset) is the approximated case.
        if (styleKw != OutlineStyleSolid && styleKw != OutlineStyleAuto && !styleApproximationReported)
        {
            diagnostics?.Emit(new Diagnostic(
                DiagnosticCodes.PaintBorderStyleApproximated001,
                "A non-solid outline-style (dotted / dashed / double / groove / ridge / inset / outset) " +
                "was painted as a solid line. Styled outline rendering is a tracked follow-up " +
                "(deferrals.md#layout-to-pdf-pipeline).",
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
        // box-decoration-break: slice — the ring is computed over the WHOLE box (so the side outlines are
        // CONTINUOUS across slices); the per-slice CLIP below limits it to this slice's outline portion. A
        // sliced outline is always SQUARE (a border-radius gates the split), so no rounded-ring concern.
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
        ToPdfRect(outerLeft, outerTop, outerW, outerH, pageHeightPt, out var ox, out var oy, out var ow, out var oh);
        ToPdfRect(innerLeft, innerTop, innerW, innerH, pageHeightPt, out var ix, out var iy, out var iw, out var ih);
        ColorChannels(argb, out var r, out var g, out var b);
        page.FillRoundedRectangleRing(ox, oy, ow, oh, ToPt(outerRadii), ix, iy, iw, ih, ToPt(innerRadii),
            r, g, b, Alpha(argb) / 255.0);
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
        out double widthPx, out uint argb, out bool nonSolid)
    {
        widthPx = 0; argb = 0; nonSolid = false;

        bool ReadEdge(PropertyId styleId, PropertyId widthId, PropertyId colorId, uint edgeCc,
            out double w, out uint a, out bool solid)
        {
            w = 0; a = 0; solid = false;
            var styleSlot = style.Get(styleId);
            if (styleSlot.Tag != ComputedSlotTag.Keyword) return false;       // unset → none
            var kw = styleSlot.AsKeyword();
            if (kw is BorderStyleNone or BorderStyleHidden) return false;
            solid = kw == BorderStyleSolid;
            var widthSlot = style.Get(widthId);
            w = widthSlot.Tag == ComputedSlotTag.LengthPx ? widthSlot.AsLengthPx() : 0;
            if (w <= 0) return false;
            if (!TryResolveColor(style.Get(colorId), edgeCc, out a)) a = edgeCc; // initial: currentcolor
            return Alpha(a) != 0;
        }

        if (!ReadEdge(PropertyId.BorderTopStyle, PropertyId.BorderTopWidth, PropertyId.BorderTopColor, cc.Top, out var wt, out var at, out var solidT)
            || !ReadEdge(PropertyId.BorderRightStyle, PropertyId.BorderRightWidth, PropertyId.BorderRightColor, cc.Right, out var wr, out var ar, out var solidR)
            || !ReadEdge(PropertyId.BorderBottomStyle, PropertyId.BorderBottomWidth, PropertyId.BorderBottomColor, cc.Bottom, out var wb, out var ab, out var solidB)
            || !ReadEdge(PropertyId.BorderLeftStyle, PropertyId.BorderLeftWidth, PropertyId.BorderLeftColor, cc.Left, out var wl, out var al, out var solidL))
            return false;
        // Used widths come from unit conversions, so compare against the top edge with a tolerance well
        // below a visible difference (rather than exact `==`, which equivalent lengths from different
        // units / calc could miss); colours are exact 8-bit ARGB, so an exact compare is right there.
        static bool SameWidth(double a, double b) => Math.Abs(a - b) <= 1e-4;
        if (!SameWidth(wt, wr) || !SameWidth(wt, wb) || !SameWidth(wt, wl)) return false;
        if (at != ar || ar != ab || ab != al) return false;   // equal colours
        widthPx = wt; argb = at; nonSolid = !(solidT && solidR && solidB && solidL);
        return true;
    }

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
                ToPdfRect(shLeftPx, shTopPx, shWidthPx, shHeightPx, pageHeightPt,
                    out var x, out var y, out var w, out var h);
                FillShadowRect(page, x, y, w, h, shadowRadii, r, g, b, alpha);
            }
            else
            {
                PaintBlurredBoxShadow(page, document, shLeftPx, shTopPx, shWidthPx, shHeightPx,
                    shadowRadii, s.BlurPx, pageHeightPt, r, g, b, alpha, diagnostics,
                    ref rasterReported, ref capReported);
            }

            if (sliceClipped) page.RestoreGraphicsState();
        }
    }

    private static void FillShadowRect(
        PdfPage page, double x, double y, double w, double h, CornerRadii radiiPx,
        double r, double g, double b, double alpha)
    {
        if (radiiPx.AnyPositive)
            page.FillRoundedRectangle(x, y, w, h, ToPt(radiiPx), r, g, b, alpha);
        else
            page.FillRectangle(x, y, w, h, r, g, b, alpha);
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
            // Over-cap / degenerate → a sharp native shadow is better than nothing, but SURFACE the
            // approximation (PR #210 review [P2] — never silently degrade).
            ToPdfRect(shLeftPx, shTopPx, shWidthPx, shHeightPx, pageHeightPt,
                out var fx, out var fy, out var fw, out var fh);
            FillShadowRect(page, fx, fy, fw, fh, shadowRadii, r, g, b, alpha);
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
        page.PlaceImage(imageRef, x, y, w, h);

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

    /// <summary>Phase 4 gradients — paint a parsed <c>linear-gradient(...)</c> background layer
    /// as a PDF native axial shading (ISO 32000-2 §8.7.4.5.3) clipped to the box's (rounded)
    /// border box. Resolves each stop's color, normalizes the stop positions (CSS Images §3.4),
    /// derives the gradient-line endpoints in PDF user space from the angle + box, and registers
    /// + paints the shading. A gradient with &lt; 2 resolvable stops is skipped (the
    /// background-color already painted underneath).</summary>
    private static void PaintLinearGradient(
        PdfPage page, PdfDocument document, CssLinearGradient gradient, ComputedStyle style,
        double pageHeightPt, double leftPx, double topPx, double widthPx, double heightPx,
        uint currentColorArgb,
        // box-decoration-break: slice — when set, the gradient AXIS + the rounded CLIP span this (larger,
        // whole-box / composite) block extent instead of the painted slice, so a sliced block's gradient is
        // CONTINUOUS across slices and rounds correctly; the CALLER adds an outer per-slice rect clip to
        // limit the paint to this fragment (PR #223 review [P1]). Null → the box itself (byte-identical).
        double? axisTopPx = null, double? axisHeightPx = null)
    {
        var stops = ResolveGradientStops(gradient.Stops, currentColorArgb);
        if (stops.Count < 2) return;

        // CLIP rect = AXIS rect = the whole (composite) decoration box. The inline axis is shared (slices
        // differ only in the block axis); the radii resolve against the COMPOSITE height.
        var clipHeightPx = axisHeightPx ?? heightPx;
        ToPdfRect(leftPx, axisTopPx ?? topPx, widthPx, clipHeightPx, pageHeightPt,
            out var ax, out var ay, out var aw, out var ah);
        // A `to <corner>` direction's angle depends on the box aspect ratio (PR #209 review [P2]) —
        // compute it from the whole (axis) box; an explicit angle / side uses the parsed value as-is.
        var angleDeg = gradient.Corner is { } corner ? CornerAngleDeg(corner, aw, ah) : gradient.AngleDeg;
        var (x0, y0, x1, y1) = LinearGradientAxis(angleDeg, ax, ay, aw, ah);
        var shadingRef = document.RegisterAxialShading(x0, y0, x1, y1, stops);
        var radiiPx = ReadCornerRadii(style, widthPx, clipHeightPx);
        page.PaintShadingInRect(shadingRef, ax, ay, aw, ah,
            radiiPx.AnyPositive ? ToPt(radiiPx) : (CornerRadii?)null);
    }

    /// <summary>Phase 4 gradients — resolve a gradient's stops to <see cref="PdfGradientStop"/>
    /// (DeviceRGB + normalized offset). Colors resolve through the shared CSS color resolver
    /// (so named / hex / rgb()/hsl() all work; <c>currentColor</c> maps to the element color);
    /// an unresolvable stop is dropped. Positions follow CSS Images §3.4: a missing first → 0,
    /// missing last → 1, missing interior spread evenly between the nearest positioned stops,
    /// and the running max enforces non-decreasing offsets.</summary>
    private static List<PdfGradientStop> ResolveGradientStops(
        IReadOnlyList<CssGradientStop> gradientStops, uint currentColorArgb)
    {
        var n = gradientStops.Count;
        var rgb = new (double R, double G, double B)[n];
        var pos = new double?[n];
        var ok = new bool[n];
        for (var i = 0; i < n; i++)
        {
            var s = gradientStops[i];
            var resolved = NetPdf.Css.ComputedValues.PropertyResolvers.ColorResolver.Resolve(
                s.ColorRaw, PropertyId.Color, "color", diagnostics: null, location: default);
            if (TryResolveColor(resolved.Slot, currentColorArgb, out var argb))
            {
                ColorChannels(argb, out var r, out var g, out var b);
                rgb[i] = (r, g, b);
                ok[i] = true;
            }
            pos[i] = s.Position;
        }

        // §3.4 position defaults + even spread + non-decreasing clamp (over ALL stops first,
        // so an unresolved stop still anchors positions correctly), then drop the unresolved.
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
        var running = 0.0;
        var result = new List<PdfGradientStop>(n);
        for (var i = 0; i < n; i++)
        {
            var p = Math.Clamp(pos[i]!.Value, 0.0, 1.0);
            if (p < running) p = running; else running = p;
            if (ok[i]) result.Add(new PdfGradientStop(p, rgb[i].R, rgb[i].G, rgb[i].B));
        }
        return result;
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
    /// gradient's box-relative fractions; the radius from the ending-shape size keyword
    /// (closest/farthest side/corner) measured against the box. FIRST CUT: the ending shape is
    /// painted as a CIRCLE (an <c>ellipse</c> is approximated by the same scalar extent — exact
    /// for a centered gradient on a square box; a documented residual otherwise).</summary>
    private static void PaintRadialGradient(
        PdfPage page, PdfDocument document, CssRadialGradient radial, ComputedStyle style,
        double pageHeightPt, double leftPx, double topPx, double widthPx, double heightPx,
        uint currentColorArgb,
        // box-decoration-break: slice — when set, the gradient's center + radius + the rounded CLIP are
        // computed over this (larger, whole-box / composite) extent instead of the painted slice, so a
        // sliced radial gradient is CONTINUOUS + rounds correctly; the CALLER adds the per-slice rect clip
        // (PR #223 review [P1]). Null → the box itself (byte-identical).
        double? axisTopPx = null, double? axisHeightPx = null)
    {
        var stops = ResolveGradientStops(radial.Stops, currentColorArgb);
        if (stops.Count < 2) return;

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
        var radius = radial.Extent switch
        {
            RadialExtent.ClosestSide => Math.Min(minX, minY),
            RadialExtent.FarthestSide => Math.Max(maxX, maxY),
            RadialExtent.ClosestCorner => Math.Sqrt(minX * minX + minY * minY),
            _ => Math.Sqrt(maxX * maxX + maxY * maxY), // FarthestCorner (default)
        };
        if (!(radius > 0)) return;

        var shadingRef = document.RegisterRadialShading(pcx, pcy, 0.0, radius, stops);
        var radiiPx = ReadCornerRadii(style, widthPx, clipHeightPx);
        page.PaintShadingInRect(shadingRef, ax, ay, aw, ah,
            radiiPx.AnyPositive ? ToPt(radiiPx) : (CornerRadii?)null);
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
