// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using NetPdf.Pdf.Images;
using SkiaSharp;

namespace NetPdf.Svg;

/// <summary>Phase 4 SVG renderer — parse an SVG document and draw its shapes (<c>rect</c> / <c>circle</c> /
/// <c>ellipse</c> / <c>line</c> / <c>polyline</c> / <c>polygon</c> / <c>path</c>), <c>&lt;text&gt;</c> runs,
/// and reusable references (<c>&lt;use&gt;</c> / <c>&lt;symbol&gt;</c>) with fills, strokes, gradient paint
/// servers, and element <c>transform</c>s onto a Skia canvas via <see cref="SubtreeRasterizer"/>, returning an
/// RGBA <see cref="RasterImageInfo"/> the image pipeline embeds as an XObject + <c>/SMask</c>. This is a
/// RASTER renderer (part 1 = shapes, PR 5; part 2 = gradients / text / use, PR 7). Native vector SVG → PDF
/// operators is a later refinement.</summary>
internal static class SvgRasterizer
{
    private const int DefaultIntrinsicPx = 150; // SVG default when no width/height/viewBox (CSS Images §4).

    /// <summary>Detect an SVG document by sniffing the leading bytes (an XML prolog or a root
    /// <c>&lt;svg</c>). Cheap pre-check before the full parse.</summary>
    public static bool LooksLikeSvg(ReadOnlySpan<byte> bytes)
    {
        // Skip a UTF-8 BOM + leading whitespace, then look for "<svg" or "<?xml".
        var s = System.Text.Encoding.UTF8.GetString(bytes.Length > 512 ? bytes[..512] : bytes);
        var t = s.TrimStart('﻿', ' ', '\t', '\r', '\n');
        return t.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || (t.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && s.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            || t.StartsWith("<!doctype svg", StringComparison.OrdinalIgnoreCase);
    }

    // DoS guards (PR-230 review [P1]): a malicious SVG can nest thousands of <g>/<use> to drive a
    // StackOverflowException (which SubtreeRasterizer's try/catch CANNOT recover) or pile up elements to
    // burn CPU. Cap the recursion DEPTH (a hard depth cap is sufficient + far below the stack limit), the
    // total ELEMENT count, and the parsed CHARACTER count.
    internal const int MaxDepth = 80;
    private const int MaxElements = 50_000;
    private const long MaxCharactersInDocument = 8L * 1024 * 1024; // 8M chars

    /// <summary>Parse + rasterize <paramref name="svgBytes"/>. Returns <see langword="null"/> on a parse
    /// failure or an over-cap document; <paramref name="sawUnsupported"/> is set when the SVG used a feature
    /// this renderer doesn't support (image / a pattern paint-server / an unresolved reference / an unknown
    /// element, or content truncated by the depth / element budget) so the caller can diagnose it.</summary>
    public static RasterImageInfo? TryRender(byte[] svgBytes, out bool sawUnsupported)
    {
        sawUnsupported = false;
        ArgumentNullException.ThrowIfNull(svgBytes);
        XDocument doc;
        try
        {
            // XXE-hardened parse: DTD processing prohibited + no external resolver, so a malicious SVG
            // cannot pull external entities / files (the security reason image/svg+xml was gated). The
            // renderer also never fetches external resources (no <image>/href/url() following this cut).
            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 1024,
                MaxCharactersInDocument = MaxCharactersInDocument,
            };
            using var ms = new System.IO.MemoryStream(svgBytes);
            using var reader = System.Xml.XmlReader.Create(ms, settings);
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (Exception) { return null; }

        var root = doc.Root;
        if (root is null || !root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase)) return null;

        // Intrinsic pixel size: width/height attrs, else the viewBox extent, else the 150px default.
        var (vbX, vbY, vbW, vbH) = ParseViewBox(Attr(root, "viewBox"));
        var w = ParseLengthPx(Attr(root, "width"), vbW > 0 ? vbW : DefaultIntrinsicPx);
        var h = ParseLengthPx(Attr(root, "height"), vbH > 0 ? vbH : DefaultIntrinsicPx);
        var pxW = (int)Math.Ceiling(w);
        var pxH = (int)Math.Ceiling(h);
        if (pxW <= 0 || pxH <= 0) return null;

        // Index every id'd element once: gradient paint servers (url(#id)) and <use>/<symbol> targets
        // resolve against this map (SVG §5.3 / §13.2). Budget-aware — definitions (<defs>/<symbol>/gradients)
        // are skipped by the render walk, so without a cap here a document with hundreds of thousands of
        // id'd elements inside <defs> would bypass the MaxElements DoS guard (PR-231 review [P1]).
        var ids = BuildIdMap(root, out var idMapOverBudget);
        var state = new SvgRenderState
        {
            Ids = ids,
            ViewportW = vbW > 0 ? vbW : pxW,
            ViewportH = vbH > 0 ? vbH : pxH,
            SawUnsupported = idMapOverBudget, // an over-cap definition tree is truncated → flag it
        };
        state.PatternShaderFactory = (pat, bounds, opacity, style) => SvgPatterns.BuildPatternShader(pat, bounds, opacity, style, state);
        var raster = SubtreeRasterizer.Render(pxW, pxH, canvas =>
        {
            // Map the root viewBox onto the raster honoring the root preserveAspectRatio (§8.2) — none /
            // meet / slice + alignment (default xMidYMid meet). Slice overflow is clipped by the raster bounds.
            if (vbW > 0 && vbH > 0)
            {
                var par = SvgPreserveAspectRatio.Compute(Attr(root, "preserveAspectRatio"), vbW, vbH, pxW, pxH);
                canvas.Translate(par.Tx, par.Ty);
                canvas.Scale(par.ScaleX, par.ScaleY);
                canvas.Translate((float)-vbX, (float)-vbY);
            }
            // The root <svg>'s own presentation attributes (color / fill / stroke / font-*) seed the
            // inherited context for its children (PR-231 review [P3] — e.g. <svg color="red"> + fill="currentColor").
            var rootStyle = ResolveStyle(root, SvgStyle.Initial, state);
            foreach (var child in root.Elements())
                RenderElement(canvas, child, rootStyle, state, depth: 1);
        });
        sawUnsupported = state.SawUnsupported;
        return raster;
    }

    /// <summary>Index every id'd element, counting ALL parsed elements (including skipped definition
    /// subtrees) against the <see cref="MaxElements"/> DoS budget. <paramref name="overBudget"/> is set when
    /// the document exceeds the cap; indexing stops there so a giant <c>&lt;defs&gt;</c> can't burn unbounded
    /// CPU/memory in the id map (PR-231 review [P1]).</summary>
    private static Dictionary<string, XElement> BuildIdMap(XElement root, out bool overBudget)
    {
        var map = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var count = 0;
        overBudget = false;
        foreach (var el in root.DescendantsAndSelf())
        {
            if (++count > MaxElements) { overBudget = true; break; }
            var id = el.Attribute("id")?.Value;
            if (!string.IsNullOrEmpty(id)) map.TryAdd(id, el); // first definition wins
        }
        return map;
    }

    internal static void RenderElement(SKCanvas canvas, XElement el, SvgStyle inherited, SvgRenderState state, int depth)
    {
        // DoS guards: stop at the depth / element budget (truncation is flagged unsupported).
        if (depth > MaxDepth || ++state.Elements > MaxElements) { state.SawUnsupported = true; return; }

        var style = ResolveStyle(el, inherited, state);
        // Group `opacity` (NOT inherited; default 1) composites the element's WHOLE subtree at once — a
        // transparency layer (SaveLayer), distinct from fill-/stroke-opacity. 0 → nothing renders.
        var opacity = ReadOpacity(el, "opacity", 1f);
        if (opacity <= 0f) return;
        var transform = SvgTransform.Parse(Attr(el, "transform"));
        var restore = canvas.Save();
        if (transform is { } m) canvas.Concat(in m);
        // clip-path="url(#id)" intersects the element (and its subtree) with the union of the referenced
        // <clipPath>'s child geometry (§14.3). Applied in the element's local space, before the opacity /
        // mask layers so the group is clipped.
        SvgClipMask.ApplyClipPath(canvas, el, style, state);

        // mask="url(#id)" composites the element's subtree against the LUMINANCE of a <mask>'s content
        // (§14.4). Open an element-content layer; after the content + opacity are drawn, the mask's luminance
        // is multiplied into its alpha (DstIn).
        var mask = SvgClipMask.ResolveMask(el, state);
        var maskLayer = mask is not null;
        if (maskLayer) canvas.SaveLayer(null);
        var opacityLayer = opacity < 1f;
        if (opacityLayer)
        {
            using var layerPaint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(opacity * 255f)) };
            canvas.SaveLayer(layerPaint);
        }

        // filter="url(#id)" applies a composed Skia image filter to the element's whole subtree (§15) — the
        // INNERMOST effect layer (the filtered result is then composited by opacity, then masked).
        var filterEl = el.Name.LocalName.Equals("filter", StringComparison.OrdinalIgnoreCase) ? null : SvgFilters.ResolveFilter(el, state);
        // The filter region (§15) — resolved ONCE: it both clips the result AND positions a feImage primitive.
        var filterRegion = filterEl is not null ? SvgFilters.ResolveFilterRegion(filterEl, el, style, state) : null;
        // feImage decodes a raster the composed filter references; the managed SKImage is held alive in this
        // list and disposed only AFTER the filter layer is composited (Skia refs the native image, so an early
        // managed dispose would risk freeing it out from under the filter graph).
        var ownedImages = filterEl is not null ? new List<SKImage>() : null;
        var imageFilter = filterEl is not null ? SvgFilters.BuildImageFilter(filterEl, state, filterRegion, ownedImages!) : null;
        SKPaint? filterPaint = null;
        var filterRegionClipped = false;
        // The filter region (§15) clips the whole filter result, applied as a HARD canvas clip — a blur halo
        // spreads past a SaveLayer bounds hint, so an unbounded primitive (a final feFlood) must be clipped
        // here (PR-246 review [P1]). An EXPLICIT x/y/width/height + filterUnits is honored; an EMPTY region
        // (zero / negative width or height, §15.7.4) clips everything out so the element renders nothing —
        // applied even when no primitive produced a visible filter; else the default (element bbox + 10%).
        if (filterRegion is { } region && (imageFilter is not null || region.Empty))
        {
            canvas.Save();
            canvas.ClipRect(region.Empty ? SKRect.Empty : region.Rect);
            filterRegionClipped = true;
        }
        if (imageFilter is not null)
        {
            filterPaint = new SKPaint { ImageFilter = imageFilter };
            canvas.SaveLayer(filterPaint);
        }

        switch (el.Name.LocalName.ToLowerInvariant())
        {
            case "g":
            case "a": // an anchor wraps children for rendering purposes
                foreach (var child in el.Elements()) RenderElement(canvas, child, style, state, depth + 1);
                break;
            case "svg": // a NESTED viewport — clip to x/y/width/height + scale a viewBox to fit
                DrawNestedSvg(canvas, el, style, state, depth);
                break;
            case "rect": DrawRect(canvas, el, style, state); break;
            case "circle": DrawCircle(canvas, el, style, state); break;
            case "ellipse": DrawEllipse(canvas, el, style, state); break;
            case "line": DrawLine(canvas, el, style, state); break;
            case "polyline":
            case "polygon": DrawPoly(canvas, el, style, state); break;
            case "path": DrawPath(canvas, el, style, state); break;
            case "text": SvgText.Draw(canvas, el, style, state); break;
            case "image": DrawImage(canvas, el, style, state); break;
            case "use": DrawUse(canvas, el, style, state, depth); break;
            // Definitions / metadata — not rendered in place, NOT flagged by mere presence (a definition is
            // only "unsupported" when REFERENCED, which the url(#…) resolution flags). A <symbol> renders only
            // when referenced by <use>; a gradient/pattern/clipPath/etc. defines a resource for a reference.
            case "defs": case "symbol": case "title": case "desc": case "metadata": case "style":
            case "lineargradient": case "radialgradient": case "stop":
            case "pattern": case "clippath": case "mask": case "filter": case "marker": break;
            // foreignObject / switch / textPath / … aren't rendered — flag so the caller surfaces one
            // diagnostic per image (PR-230 [P2]).
            default: state.SawUnsupported = true; break;
        }
        if (imageFilter is not null)
        {
            canvas.Restore();        // composite the filtered layer
            filterPaint!.Dispose();
            imageFilter.Dispose();   // releases the native filter graph's ref to any feImage raster
        }
        if (filterRegionClipped) canvas.Restore();   // pop the filter-region clip
        if (ownedImages is not null) foreach (var img in ownedImages) img.Dispose(); // feImage rasters, now safe
        if (opacityLayer) canvas.Restore();          // composite the opacity layer into the element layer
        if (mask is not null) SvgClipMask.ApplyMask(canvas, mask, el, style, state, depth); // multiply the mask luminance in
        canvas.RestoreToCount(restore);
    }

    // ---- shapes ----

    /// <summary>Build the geometry <see cref="SKPath"/> for a basic shape element (rect / circle / ellipse /
    /// line / polyline / polygon / path) in the element's user space, or <see langword="null"/> when the
    /// element isn't a basic shape or is geometrically empty. Shared by the draw walk, the <c>clip-path</c>
    /// resolver (a <c>&lt;clipPath&gt;</c> unions its children's geometry), and <c>&lt;textPath&gt;</c>
    /// (glyphs along any basic shape).</summary>
    internal static SKPath? BuildShapePath(XElement el, SvgStyle style, SvgRenderState state)
    {
        switch (el.Name.LocalName.ToLowerInvariant())
        {
            case "rect":
            {
                var x = Len(el, "x", state, style, LenAxis.X); var y = Len(el, "y", state, style, LenAxis.Y);
                var w = Len(el, "width", state, style, LenAxis.X); var h = Len(el, "height", state, style, LenAxis.Y);
                if (!(w > 0) || !(h > 0)) return null;
                var rx = Len(el, "rx", state, style, LenAxis.X); var ry = Len(el, "ry", state, style, LenAxis.Y);
                var path = new SKPath();
                if (rx > 0 || ry > 0) path.AddRoundRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), (float)(rx > 0 ? rx : ry), (float)(ry > 0 ? ry : rx));
                else path.AddRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)));
                return path;
            }
            case "circle":
            {
                var r = Len(el, "r", state, style, LenAxis.Other);
                if (!(r > 0)) return null;
                var path = new SKPath();
                path.AddCircle((float)Len(el, "cx", state, style, LenAxis.X), (float)Len(el, "cy", state, style, LenAxis.Y), (float)r);
                return path;
            }
            case "ellipse":
            {
                var rx = Len(el, "rx", state, style, LenAxis.X); var ry = Len(el, "ry", state, style, LenAxis.Y);
                if (!(rx > 0) || !(ry > 0)) return null;
                var cx = Len(el, "cx", state, style, LenAxis.X); var cy = Len(el, "cy", state, style, LenAxis.Y);
                var path = new SKPath();
                path.AddOval(new SKRect((float)(cx - rx), (float)(cy - ry), (float)(cx + rx), (float)(cy + ry)));
                return path;
            }
            case "line":
            {
                var path = new SKPath();
                path.MoveTo((float)Len(el, "x1", state, style, LenAxis.X), (float)Len(el, "y1", state, style, LenAxis.Y));
                path.LineTo((float)Len(el, "x2", state, style, LenAxis.X), (float)Len(el, "y2", state, style, LenAxis.Y));
                return path;
            }
            case "polyline":
            case "polygon":
            {
                var pts = ParsePoints(Attr(el, "points"));
                if (pts.Count < 2) return null;
                var path = new SKPath();
                path.MoveTo(pts[0]);
                for (var i = 1; i < pts.Count; i++) path.LineTo(pts[i]);
                if (el.Name.LocalName.Equals("polygon", StringComparison.OrdinalIgnoreCase)) path.Close();
                return path;
            }
            case "path":
            {
                var d = Attr(el, "d");
                if (string.IsNullOrWhiteSpace(d)) return null;
                return SKPath.ParseSvgPathData(d); // null on malformed data
            }
            default: return null;
        }
    }

    private static void DrawRect(SKCanvas canvas, XElement el, SvgStyle style, SvgRenderState state)
    {
        using var path = BuildShapePath(el, style, state);
        if (path is not null) FillAndStroke(canvas, path, style, state);
    }

    private static void DrawCircle(SKCanvas canvas, XElement el, SvgStyle style, SvgRenderState state)
    {
        using var path = BuildShapePath(el, style, state);
        if (path is not null) FillAndStroke(canvas, path, style, state);
    }

    private static void DrawEllipse(SKCanvas canvas, XElement el, SvgStyle style, SvgRenderState state)
    {
        using var path = BuildShapePath(el, style, state);
        if (path is not null) FillAndStroke(canvas, path, style, state);
    }

    private static void DrawLine(SKCanvas canvas, XElement el, SvgStyle style, SvgRenderState state)
    {
        using var path = BuildShapePath(el, style, state);
        if (path is not null) StrokeOnly(canvas, path, style, state);
        SvgMarkers.DrawMarkers(canvas, el, style, state);
    }

    private static void DrawPoly(SKCanvas canvas, XElement el, SvgStyle style, SvgRenderState state)
    {
        using var path = BuildShapePath(el, style, state);
        if (path is not null) FillAndStroke(canvas, path, style, state);
        SvgMarkers.DrawMarkers(canvas, el, style, state);
    }

    private static void DrawPath(SKCanvas canvas, XElement el, SvgStyle style, SvgRenderState state)
    {
        using var path = BuildShapePath(el, style, state);
        if (path is not null) FillAndStroke(canvas, path, style, state);
        SvgMarkers.DrawMarkers(canvas, el, style, state);
    }

    /// <summary>Draw an <c>&lt;image&gt;</c> from a SELF-CONTAINED <c>data:</c> URI (no external fetch —
    /// the renderer never follows network hrefs). The raster is decoded via Skia and drawn into the
    /// element's <c>x</c>/<c>y</c>/<c>width</c>/<c>height</c> rect, preserving aspect ratio
    /// (<c>xMidYMid meet</c> — the default; an explicit <c>preserveAspectRatio="none"</c> stretches to
    /// fill). A non-<c>data:</c> href, a missing/zero rect, or undecodable bytes → unsupported flag.</summary>
    private static void DrawImage(SKCanvas canvas, XElement el, SvgStyle style, SvgRenderState state)
    {
        var w = Len(el, "width", state, style, LenAxis.X);
        var h = Len(el, "height", state, style, LenAxis.Y);
        var href = SvgAttr.HrefRaw(el);
        if (!(w > 0) || !(h > 0) || href is null
            || !href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || DecodeDataUriImage(href) is not { } image)
        {
            state.SawUnsupported = true; // external href / malformed / undecodable → not rendered
            return;
        }
        using (image)
        {
            var x = (float)Len(el, "x", state, style, LenAxis.X);
            var y = (float)Len(el, "y", state, style, LenAxis.Y);
            var dest = new SKRect(x, y, x + (float)w, y + (float)h);
            if (image.Width <= 0 || image.Height <= 0)
            {
                canvas.DrawImage(image, dest);
                return;
            }
            // preserveAspectRatio (§8.8) — none stretches; meet fits inside; slice covers + clips to the rect.
            var par = SvgPreserveAspectRatio.Compute(Attr(el, "preserveAspectRatio"), image.Width, image.Height, w, h);
            var dx = x + par.Tx;
            var dy = y + par.Ty;
            var fitted = new SKRect(dx, dy, dx + image.Width * par.ScaleX, dy + image.Height * par.ScaleY);
            if (par.Slice)
            {
                var save = canvas.Save();
                canvas.ClipRect(dest);
                canvas.DrawImage(image, fitted);
                canvas.RestoreToCount(save);
            }
            else
                canvas.DrawImage(image, fitted);
        }
    }

    // The encoded base64 payload is capped so a hostile data: URI can't force a huge allocation before
    // the byte-level caps run (base64 ≈ 4/3 of the decoded bytes; the decoded bytes are capped by
    // ImageSafetyValidator.MaxBytes).
    private const int MaxDataUriBase64Chars = (NetPdf.Pdf.Images.ImageSafetyValidator.MaxBytes / 3) * 4 + 8;

    /// <summary>Decode a <c>data:[mime];base64,payload</c> image URI to an <see cref="SKImage"/> — running
    /// the SAME pre-decode safety gates the rest of the engine applies to images (PR #240 [P1]): an
    /// <c>image/*</c> MIME allowlist, the encoded-size cap, the magic-byte + DECLARED-dimension caps
    /// (<see cref="NetPdf.Pdf.Images.ImageSafetyValidator"/> — guards a decompression bomb: a tiny
    /// compressed file declaring huge dimensions), and a decoded-raster pixel cap. Returns
    /// <see langword="null"/> (→ the caller flags unsupported) for any non-data / non-image / over-cap /
    /// undecodable input. Self-contained — no I/O / fetch. (Also used by <c>feImage</c> in
    /// <see cref="SvgFilters"/>.)</summary>
    internal static SKImage? DecodeDataUriImage(string dataUri)
    {
        var comma = dataUri.IndexOf(',');
        if (comma <= 5) return null; // need "data:" + a non-empty meta + a comma
        var meta = dataUri.Substring(5, comma - 5);
        if (!meta.Contains("base64", StringComparison.OrdinalIgnoreCase)) return null; // only base64 payloads
        // MIME allowlist: the type must be image/* (a defense-in-depth check before the magic sniff).
        var semi = meta.IndexOf(';');
        var mime = (semi >= 0 ? meta[..semi] : meta).Trim();
        if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
        var payload = dataUri[(comma + 1)..].Trim();
        if (payload.Length == 0 || payload.Length > MaxDataUriBase64Chars) return null; // encoded-size cap
        byte[] bytes;
        try { bytes = Convert.FromBase64String(payload); }
        catch (FormatException) { return null; }
        if (bytes.Length == 0) return null;
        // Header safety: format magic-byte allowlist + DECLARED-dimension caps (before any decode).
        if (!NetPdf.Pdf.Images.ImageSafetyValidator.Validate(bytes).IsSafe) return null;
        using var data = SKData.CreateCopy(bytes);
        var image = SKImage.FromEncodedData(data);
        if (image is null) return null;
        // Defense in depth: reject a decoded raster beyond the raster pixel-area cap.
        if ((long)image.Width * image.Height > NetPdf.Pdf.Images.ImageSafetyValidator.MaxRasterPixelArea)
        {
            image.Dispose();
            return null;
        }
        return image;
    }

    /// <summary>Render a NESTED <c>&lt;svg&gt;</c> as a proper viewport (SVG §7.2): translate to its
    /// <c>x</c>/<c>y</c>, CLIP to its <c>width</c>/<c>height</c>, and — when it carries a <c>viewBox</c> —
    /// SCALE the content to fit (<c>xMidYMid meet</c>, centered). An OMITTED width/height defaults to 100%
    /// of the parent viewport; an EXPLICIT zero / negative renders nothing (PR #240 [P2]). The nested
    /// viewport becomes the new <c>%</c> reference for descendants.</summary>
    private static void DrawNestedSvg(SKCanvas canvas, XElement el, SvgStyle style, SvgRenderState state, int depth)
    {
        var x = Len(el, "x", state, style, LenAxis.X);
        var y = Len(el, "y", state, style, LenAxis.Y);
        // Distinguish an OMITTED width/height (→ 100% of the parent viewport) from an EXPLICIT non-positive
        // one (→ nothing renders). Len resolves a present value; absence falls back to the viewport.
        var w = el.Attribute("width") is not null ? Len(el, "width", state, style, LenAxis.X) : state.ViewportW;
        var h = el.Attribute("height") is not null ? Len(el, "height", state, style, LenAxis.Y) : state.ViewportH;
        if (!(w > 0) || !(h > 0)) return; // explicit 0 / negative → render nothing

        var save = canvas.Save();
        canvas.Translate((float)x, (float)y);
        RenderViewport(canvas, el, el.Elements(), style, state, depth, w, h);
        canvas.RestoreToCount(save);
    }

    /// <summary>Establish an SVG viewport at the current canvas origin (SVG §7.2): CLIP to
    /// <paramref name="vpW"/> × <paramref name="vpH"/>, and — when <paramref name="viewportEl"/> carries a
    /// <c>viewBox</c> — SCALE its content to fit (<c>xMidYMid meet</c>, centered), making the viewBox extent
    /// the new <c>%</c> reference for descendants. Shared by a nested <c>&lt;svg&gt;</c> and a
    /// <c>&lt;use&gt;</c> → <c>&lt;symbol&gt;</c>/<c>&lt;svg&gt;</c> reference (both set up the same viewport).</summary>
    private static void RenderViewport(
        SKCanvas canvas, XElement viewportEl, IEnumerable<XElement> children, SvgStyle childStyle,
        SvgRenderState state, int depth, double vpW, double vpH)
    {
        canvas.ClipRect(new SKRect(0, 0, (float)vpW, (float)vpH));
        var vb = ParseViewBox(Attr(viewportEl, "viewBox"));
        var newVpW = vpW;
        var newVpH = vpH;
        if (vb.W > 0 && vb.H > 0)
        {
            // preserveAspectRatio (§8.8) maps the viewBox onto the viewport; slice overflow is clipped above.
            var par = SvgPreserveAspectRatio.Compute(Attr(viewportEl, "preserveAspectRatio"), vb.W, vb.H, vpW, vpH);
            canvas.Translate(par.Tx, par.Ty);
            canvas.Scale(par.ScaleX, par.ScaleY);
            canvas.Translate((float)-vb.X, (float)-vb.Y);
            newVpW = vb.W;   // descendants resolve % against the viewBox extent
            newVpH = vb.H;
        }
        var prevW = state.ViewportW;
        var prevH = state.ViewportH;
        state.ViewportW = newVpW;
        state.ViewportH = newVpH;
        foreach (var child in children) RenderElement(canvas, child, childStyle, state, depth + 1);
        state.ViewportW = prevW;
        state.ViewportH = prevH;
    }

    /// <summary>Clone a referenced element / symbol at the <c>&lt;use&gt;</c> position (SVG §5.6). A
    /// <c>&lt;symbol&gt;</c> / nested <c>&lt;svg&gt;</c> target establishes a VIEWPORT — its
    /// <c>width</c>/<c>height</c> (from the <c>&lt;use&gt;</c> if set, else the target, else 100% of the
    /// current viewport) clip the content and a <c>viewBox</c> scales to fit (§7.2 / §5.6) — and renders its
    /// children as a group; any other target renders as itself. A symbol/svg target's OWN presentation
    /// attributes + transform are resolved before its children inherit (PR-231 review [P2]).</summary>
    private static void DrawUse(SKCanvas canvas, XElement el, SvgStyle style, SvgRenderState state, int depth)
    {
        if (depth + 1 > MaxDepth || ++state.Elements > MaxElements) { state.SawUnsupported = true; return; }
        var id = SvgAttr.HrefId(el);
        if (id is null || !state.Ids.TryGetValue(id, out var target)) { state.SawUnsupported = true; return; }
        var x = Len(el, "x", state, style, LenAxis.X); var y = Len(el, "y", state, style, LenAxis.Y);
        var restore = canvas.Save();
        if (x != 0 || y != 0) canvas.Translate((float)x, (float)y);
        if (target.Name.LocalName.Equals("symbol", StringComparison.OrdinalIgnoreCase)
            || target.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
        {
            // The symbol/svg establishes its own resolved style + transform for its children.
            var targetStyle = ResolveStyle(target, style, state);
            if (SvgTransform.Parse(Attr(target, "transform")) is { } tm) canvas.Concat(in tm);
            // Viewport size: the <use> width/height override the target's; an omitted dimension is 100% of
            // the current viewport (§5.6 — the symbol is instanced as a viewport). A non-positive explicit
            // size renders nothing.
            var w = el.Attribute("width") is not null ? Len(el, "width", state, style, LenAxis.X)
                  : target.Attribute("width") is not null ? Len(target, "width", state, targetStyle, LenAxis.X)
                  : state.ViewportW;
            var h = el.Attribute("height") is not null ? Len(el, "height", state, style, LenAxis.Y)
                  : target.Attribute("height") is not null ? Len(target, "height", state, targetStyle, LenAxis.Y)
                  : state.ViewportH;
            if (w > 0 && h > 0)
                RenderViewport(canvas, target, target.Elements(), targetStyle, state, depth, w, h);
        }
        else
            RenderElement(canvas, target, style, state, depth + 1);
        canvas.RestoreToCount(restore);
    }

    // ---- fill / stroke ----

    /// <summary>Fill then stroke (SVG paint order §13.3): fill with the resolved fill / gradient, then stroke
    /// if a stroke is set.</summary>
    private static void FillAndStroke(SKCanvas canvas, SKPath path, SvgStyle style, SvgRenderState state)
    {
        if (style.FillRef is { } fref)
        {
            if (state.ResolveShader(fref, path.Bounds, style.FillOpacity, style) is { } rp)
                using (rp)
                using (var p = new SKPaint { Style = SKPaintStyle.Fill, Shader = rp.Shader, IsAntialias = true })
                    canvas.DrawPath(path, p);
            else state.SawUnsupported = true; // unresolved / non paint server → no fill
        }
        else if (style.Fill.Alpha > 0 && style.FillOpacity > 0)
        {
            using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = WithOpacity(style.Fill, style.FillOpacity), IsAntialias = true };
            canvas.DrawPath(path, fill);
        }
        StrokeOnly(canvas, path, style, state);
    }

    private static void StrokeOnly(SKCanvas canvas, SKPath path, SvgStyle style, SvgRenderState state)
    {
        if (!(style.StrokeWidth > 0)) return;
        // The dash pattern (stroke-dasharray) is a PathEffect; cap/join/miter map directly. A valid dash
        // array (even-length, ≥1 positive entry) is required by Skia; ParseDashArray guarantees that.
        var dash = style.StrokeDash is { Length: > 0 } intervals
            ? SKPathEffect.CreateDash(intervals, style.StrokeDashOffset)
            : null;
        void ApplyStrokeProps(SKPaint p)
        {
            p.StrokeCap = style.StrokeCap;
            p.StrokeJoin = style.StrokeJoin;
            p.StrokeMiter = style.StrokeMiter;
            if (dash is not null) p.PathEffect = dash;
        }
        try
        {
            if (style.StrokeRef is { } sref)
            {
                if (state.ResolveShader(sref, path.Bounds, style.StrokeOpacity, style) is { } rp)
                    using (rp)
                    using (var p = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = style.StrokeWidth, Shader = rp.Shader, IsAntialias = true })
                    {
                        ApplyStrokeProps(p);
                        canvas.DrawPath(path, p);
                    }
                else state.SawUnsupported = true;
                return;
            }
            if (style.Stroke is not { } sc) return;
            using var stroke = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = WithOpacity(sc, style.StrokeOpacity),
                StrokeWidth = style.StrokeWidth,
                IsAntialias = true,
            };
            ApplyStrokeProps(stroke);
            canvas.DrawPath(path, stroke);
        }
        finally
        {
            dash?.Dispose();
        }
    }

    private static SKColor WithOpacity(SKColor c, float opacity) =>
        opacity >= 1f ? c : c.WithAlpha((byte)Math.Clamp((int)Math.Round(c.Alpha * opacity), 0, 255));

    // ---- style cascade ----

    /// <summary>Resolve an element's presentation style over an inherited base. Internal so
    /// <c>&lt;textPath&gt;</c> can resolve a referenced shape's OWN style before building its geometry
    /// (PR-244 review [P3]).</summary>
    internal static SvgStyle ResolveStyle(XElement el, SvgStyle inherited, SvgRenderState state)
    {
        // The `color` property (inherited) is what `currentColor` resolves to (PR-231 review [P3]).
        var currentColor = inherited.CurrentColor;
        var colorRaw = SvgAttr.Presentation(el, "color");
        if (colorRaw is not null && !colorRaw.Equals("currentColor", StringComparison.OrdinalIgnoreCase)
            && SvgColor.TryParse(colorRaw, out var cc)) currentColor = cc;

        var fill = inherited.Fill;
        var fillRef = inherited.FillRef;
        var hasFill = inherited.HasExplicitFill;
        var fillRaw = SvgAttr.Presentation(el, "fill");
        if (fillRaw is not null)
        {
            if (fillRaw.Equals("none", StringComparison.OrdinalIgnoreCase)) { fill = SKColors.Transparent; fillRef = null; hasFill = true; }
            else if (fillRaw.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) { fill = currentColor; fillRef = null; hasFill = true; }
            else if (PaintServerId(fillRaw) is { } id) { fillRef = id; fill = SKColors.Transparent; hasFill = true; }
            else if (SvgColor.TryParse(fillRaw, out var fc)) { fill = fc; fillRef = null; hasFill = true; }
        }

        var stroke = inherited.Stroke;
        var strokeRef = inherited.StrokeRef;
        var strokeRaw = SvgAttr.Presentation(el, "stroke");
        if (strokeRaw is not null)
        {
            if (strokeRaw.Equals("none", StringComparison.OrdinalIgnoreCase)) { stroke = null; strokeRef = null; }
            else if (strokeRaw.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) { stroke = currentColor; strokeRef = null; }
            else if (PaintServerId(strokeRaw) is { } id) { strokeRef = id; stroke = null; }
            else if (SvgColor.TryParse(strokeRaw, out var stc)) { stroke = stc; strokeRef = null; }
        }

        var strokeWidth = inherited.StrokeWidth;
        var swRaw = SvgAttr.Presentation(el, "stroke-width");
        if (swRaw is not null && double.TryParse(TrimUnit(swRaw), NumberStyles.Float, CultureInfo.InvariantCulture, out var sw))
            strokeWidth = (float)sw;

        var fillOpacity = ReadOpacity(el, "fill-opacity", inherited.FillOpacity);
        var strokeOpacity = ReadOpacity(el, "stroke-opacity", inherited.StrokeOpacity);

        // Stroke dash + cap/join (all inherited). `stroke-dasharray: none` (or an all-zero / negative
        // array) → solid; an ODD-length array repeats per SVG 1.1 §11.4 (Skia needs an even count).
        var strokeDash = inherited.StrokeDash;
        var dashRaw = SvgAttr.Presentation(el, "stroke-dasharray");
        if (dashRaw is not null) strokeDash = ParseDashArray(dashRaw);
        var strokeDashOffset = inherited.StrokeDashOffset;
        var doRaw = SvgAttr.Presentation(el, "stroke-dashoffset");
        if (doRaw is not null && double.TryParse(TrimUnit(doRaw), NumberStyles.Float, CultureInfo.InvariantCulture, out var dof))
            strokeDashOffset = (float)dof;
        var strokeCap = ParseLineCap(SvgAttr.Presentation(el, "stroke-linecap"), inherited.StrokeCap);
        var strokeJoin = ParseLineJoin(SvgAttr.Presentation(el, "stroke-linejoin"), inherited.StrokeJoin);
        var strokeMiter = inherited.StrokeMiter;
        var mlRaw = SvgAttr.Presentation(el, "stroke-miterlimit");
        if (mlRaw is not null && double.TryParse(mlRaw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ml) && ml >= 1)
            strokeMiter = (float)ml;

        // Font properties (inherited) — drive <text> shaping.
        var fontSize = inherited.FontSizePx;
        var fsRaw = SvgAttr.Presentation(el, "font-size");
        if (fsRaw is not null && double.TryParse(TrimUnit(fsRaw), NumberStyles.Float, CultureInfo.InvariantCulture, out var fs) && fs > 0)
            fontSize = (float)fs;
        var fontFamily = SvgAttr.Presentation(el, "font-family") ?? inherited.FontFamily;
        var fontWeight = ParseFontWeight(SvgAttr.Presentation(el, "font-weight"), inherited.FontWeight);
        var italic = ParseItalic(SvgAttr.Presentation(el, "font-style"), inherited.Italic);
        var textAnchor = SvgAttr.Presentation(el, "text-anchor") ?? inherited.TextAnchor;
        var letterSpacing = ParseSpacing(SvgAttr.Presentation(el, "letter-spacing"), inherited.LetterSpacing);
        var wordSpacing = ParseSpacing(SvgAttr.Presentation(el, "word-spacing"), inherited.WordSpacing);
        // Marker properties inherit (the `marker` shorthand sets all three).
        var markerStart = ResolveMarkerProp(el, "marker-start", inherited.MarkerStart);
        var markerMid = ResolveMarkerProp(el, "marker-mid", inherited.MarkerMid);
        var markerEnd = ResolveMarkerProp(el, "marker-end", inherited.MarkerEnd);
        var dominantBaseline = SvgAttr.Presentation(el, "dominant-baseline") ?? inherited.DominantBaseline;

        return new SvgStyle(fill, fillRef, hasFill, stroke, strokeRef, strokeWidth,
            fillOpacity, strokeOpacity, currentColor, fontSize, fontFamily, fontWeight, italic, textAnchor,
            strokeDash, strokeDashOffset, strokeCap, strokeJoin, strokeMiter, letterSpacing, wordSpacing,
            markerStart, markerMid, markerEnd, dominantBaseline);
    }

    /// <summary>Resolve a marker property (<c>marker-start/-mid/-end</c>) — the specific property wins, else
    /// the <c>marker</c> shorthand; <c>none</c> → null, <c>url(#id)</c> → the id, absent → inherited.</summary>
    private static string? ResolveMarkerProp(XElement el, string which, string? inherited)
    {
        var raw = SvgAttr.Presentation(el, which) ?? SvgAttr.Presentation(el, "marker");
        if (raw is null) return inherited;
        raw = raw.Trim();
        if (raw.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        return PaintServerId(raw) ?? inherited;
    }

    /// <summary>Parse a <c>stroke-dasharray</c> value (comma/space-separated lengths). Returns
    /// <see langword="null"/> for <c>none</c> / empty / an all-zero or any-negative list (→ solid), an
    /// EVEN-length dash array otherwise (an odd list is repeated once, per SVG 1.1 §11.4, so Skia — which
    /// requires an even count — gets the right pattern).</summary>
    private static float[]? ParseDashArray(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0 || raw.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        var toks = raw.Split(new[] { ',', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var vals = new List<float>(toks.Length);
        var anyPositive = false;
        foreach (var tok in toks)
        {
            if (!double.TryParse(TrimUnit(tok), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0)
                return null; // a negative / unparseable entry makes the whole list invalid → solid
            vals.Add((float)v);
            if (v > 0) anyPositive = true;
        }
        if (vals.Count == 0 || !anyPositive) return null; // all zero → solid
        if (vals.Count % 2 == 1) vals.AddRange(vals); // odd → repeat (Skia needs an even count)
        return vals.ToArray();
    }

    private static SKStrokeCap ParseLineCap(string? raw, SKStrokeCap inherited) => raw?.Trim().ToLowerInvariant() switch
    {
        "butt" => SKStrokeCap.Butt,
        "round" => SKStrokeCap.Round,
        "square" => SKStrokeCap.Square,
        _ => inherited,
    };

    private static SKStrokeJoin ParseLineJoin(string? raw, SKStrokeJoin inherited) => raw?.Trim().ToLowerInvariant() switch
    {
        "miter" => SKStrokeJoin.Miter,
        "round" => SKStrokeJoin.Round,
        "bevel" => SKStrokeJoin.Bevel,
        _ => inherited,
    };

    /// <summary>The fragment id of a <c>url(#id)</c> paint server, or <see langword="null"/> when the value
    /// isn't a local <c>url(#…)</c> reference.</summary>
    internal static string? PaintServerId(string raw)
    {
        var s = raw.TrimStart();
        if (!s.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) return null;
        var open = s.IndexOf('(');
        var close = s.IndexOf(')', open + 1);
        if (close <= open) return null;
        var inner = s[(open + 1)..close].Trim().Trim('\'', '"');
        return inner.StartsWith('#') && inner.Length > 1 ? inner[1..] : null;
    }

    private static float ReadOpacity(XElement el, string name, float inherited)
    {
        var raw = SvgAttr.Presentation(el, name);
        if (raw is null) return inherited;
        raw = raw.Trim();
        if (raw.EndsWith('%') && double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            return (float)Math.Clamp(pct / 100.0, 0, 1);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? (float)Math.Clamp(v, 0, 1) : inherited;
    }

    private static int ParseFontWeight(string? raw, int inherited)
    {
        if (raw is null) return inherited;
        raw = raw.Trim();
        if (raw.Equals("bold", StringComparison.OrdinalIgnoreCase)) return 700;
        if (raw.Equals("normal", StringComparison.OrdinalIgnoreCase)) return 400;
        if (raw.Equals("bolder", StringComparison.OrdinalIgnoreCase)) return Math.Min(900, inherited + 300);
        if (raw.Equals("lighter", StringComparison.OrdinalIgnoreCase)) return Math.Max(100, inherited - 300);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 1, 1000) : inherited;
    }

    private static bool ParseItalic(string? raw, bool inherited) => raw is null
        ? inherited
        : raw.Trim().Equals("italic", StringComparison.OrdinalIgnoreCase) || raw.Trim().Equals("oblique", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>letter-spacing</c> / <c>word-spacing</c> — <c>normal</c> (or absent) → 0; otherwise a
    /// length (px/pt/unitless user units).</summary>
    private static float ParseSpacing(string? raw, float inherited)
    {
        if (raw is null) return inherited;
        raw = raw.Trim();
        if (raw.Equals("normal", StringComparison.OrdinalIgnoreCase)) return 0;
        return double.TryParse(TrimUnit(raw), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (float)v : inherited;
    }

    // ---- attribute / value helpers ----

    internal static string? Attr(XElement el, string name) => el.Attribute(name)?.Value;

    /// <summary>True if the element carries any of the named attributes (used to detect unmodeled
    /// filter routing / subregion / region attributes).</summary>
    internal static bool HasAnyAttr(XElement el, params string[] names)
    {
        foreach (var n in names) if (el.Attribute(n) is not null) return true;
        return false;
    }

    internal static double Num(XElement el, string name) =>
        double.TryParse(TrimUnit(Attr(el, name)), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>Which reference a <c>%</c> length resolves against (SVG 1.1 §7.10): an X-axis length
    /// (x/width/cx/rx) → the viewport WIDTH; a Y-axis length (y/height/cy/ry) → the HEIGHT; an
    /// "other" length (r) → the normalized diagonal <c>√((w²+h²)/2)</c>.</summary>
    internal enum LenAxis { X, Y, Other }

    /// <summary>Resolve a geometry length attribute honoring units: <c>px</c>/<c>pt</c>/unitless (as-is),
    /// <c>%</c> (against the viewport per <paramref name="axis"/>), and <c>em</c>/<c>rem</c> (against the
    /// current font-size). Absent / unparseable → 0.</summary>
    internal static double Len(XElement el, string name, SvgRenderState state, SvgStyle style, LenAxis axis)
    {
        var raw = Attr(el, name);
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        raw = raw.Trim();
        if (raw.EndsWith("%", StringComparison.Ordinal))
        {
            if (!double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) return 0;
            var basis = axis switch
            {
                LenAxis.X => state.ViewportW,
                LenAxis.Y => state.ViewportH,
                _ => Math.Sqrt((state.ViewportW * state.ViewportW + state.ViewportH * state.ViewportH) / 2.0),
            };
            return pct / 100.0 * basis;
        }
        // em / rem resolve against the current font-size (rem ≈ em here — no separate root cascade).
        if (raw.EndsWith("rem", StringComparison.OrdinalIgnoreCase))
            return double.TryParse(raw[..^3], NumberStyles.Float, CultureInfo.InvariantCulture, out var rem) ? rem * style.FontSizePx : 0;
        if (raw.EndsWith("em", StringComparison.OrdinalIgnoreCase))
            return double.TryParse(raw[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var em) ? em * style.FontSizePx : 0;
        return double.TryParse(TrimUnit(raw), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static double ParseLengthPx(string? raw, double fallback) =>
        double.TryParse(TrimUnit(raw), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    internal static string? TrimUnit(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        s = s.Trim();
        // Strip a trailing px / pt unit (other units are a refinement); '%' returns null (unsupported here).
        if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase)) return s[..^2];
        if (s.EndsWith("pt", StringComparison.OrdinalIgnoreCase)) return s[..^2];
        if (s.EndsWith("%", StringComparison.Ordinal)) return null;
        return s;
    }

    internal static (double X, double Y, double W, double H) ParseViewBox(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (0, 0, 0, 0);
        var p = raw.Split(new[] { ' ', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 4) return (0, 0, 0, 0);
        double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return (D(p[0]), D(p[1]), D(p[2]), D(p[3]));
    }

    internal static List<SKPoint> ParsePoints(string? raw)
    {
        var list = new List<SKPoint>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        var nums = raw.Split(new[] { ' ', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < nums.Length; i += 2)
            if (double.TryParse(nums[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && double.TryParse(nums[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                list.Add(new SKPoint((float)x, (float)y));
        return list;
    }
}
