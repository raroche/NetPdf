// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using NetPdf.Pdf;
using SkiaSharp;

namespace NetPdf.Svg;

/// <summary>Phase 4 native vector SVG → PDF (first cut). Emits an SVG's shape geometry as native PDF path
/// operators (<c>m</c>/<c>l</c>/<c>c</c> + <c>f</c>/<c>S</c>) drawn straight onto a <see cref="PdfPage"/> — so
/// the vector art stays CRISP at any zoom, unlike the <see cref="SvgRasterizer"/> raster path. This first cut
/// handles the tractable subset (the basic shapes + <c>&lt;path&gt;</c>, <c>&lt;g&gt;</c> nesting, element
/// <c>transform</c>s, solid <c>fill</c>/<c>stroke</c> with opacity/dash/cap/join, the <c>fill-rule</c>, and the
/// root <c>viewBox</c>/<c>preserveAspectRatio</c> mapping). Anything richer — gradient/pattern paint servers,
/// <c>&lt;text&gt;</c>, <c>&lt;image&gt;</c>, <c>&lt;use&gt;</c>, clip/mask/filter/marker refs, nested viewports
/// — makes <see cref="TryEmit"/> return <see langword="false"/> so the caller falls back to the raster path.
/// The walk is ALL-OR-NOTHING: it collects the paint ops into a buffer and only writes them to the page if the
/// whole document is supported, so a partial document never leaves half-drawn native ops on the page.</summary>
internal static class SvgNativeEmitter
{
    /// <summary>Try to render <paramref name="svgBytes"/> natively onto <paramref name="page"/> inside the PDF
    /// rectangle (<paramref name="leftPt"/>, <paramref name="bottomPt"/>) with size
    /// (<paramref name="widthPt"/> × <paramref name="heightPt"/>) — PDF points, bottom-left origin (the same
    /// rect the raster <c>&lt;img&gt;</c> XObject would occupy). Returns <see langword="true"/> only when the
    /// ENTIRE SVG was emitted natively; on <see langword="false"/> the page is untouched and the caller should
    /// fall back to the raster path. <paramref name="sawUnsupported"/> reports why it bailed (a feature outside
    /// the supported subset), for a diagnostic.</summary>
    internal static bool TryEmit(
        byte[] svgBytes, PdfPage page,
        double leftPt, double bottomPt, double widthPt, double heightPt,
        out bool sawUnsupported)
    {
        sawUnsupported = false;
        ArgumentNullException.ThrowIfNull(svgBytes);
        ArgumentNullException.ThrowIfNull(page);
        if (!(widthPt > 0) || !(heightPt > 0)) return false;

        XDocument doc;
        try
        {
            // XXE-hardened parse, identical to SvgRasterizer (no DTDs, no external resolver / entities).
            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 1024,
                MaxCharactersInDocument = 8L * 1024 * 1024,
            };
            using var ms = new System.IO.MemoryStream(svgBytes);
            using var reader = System.Xml.XmlReader.Create(ms, settings);
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (Exception) { return false; }

        var root = doc.Root;
        if (root is null || !root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase)) return false;

        // viewBox (or the intrinsic width/height as the user-space box) → the target rect, honoring the root
        // preserveAspectRatio (default xMidYMid meet). Build one affine matrix that maps SVG user coordinates
        // straight to PDF points, folding in the Y-flip (SVG y-down → PDF y-up).
        var (vbX, vbY, vbW, vbH) = SvgRasterizer.ParseViewBox(SvgRasterizer.Attr(root, "viewBox"));
        var intrinsicW = SvgRasterizer.ParseLengthPx(SvgRasterizer.Attr(root, "width"), vbW > 0 ? vbW : 150);
        var intrinsicH = SvgRasterizer.ParseLengthPx(SvgRasterizer.Attr(root, "height"), vbH > 0 ? vbH : 150);
        var vpW = vbW > 0 ? vbW : intrinsicW;
        var vpH = vbH > 0 ? vbH : intrinsicH;
        var vpX = vbW > 0 ? vbX : 0;
        var vpY = vbH > 0 ? vbY : 0;
        if (!(vpW > 0) || !(vpH > 0)) return false;

        var par = SvgPreserveAspectRatio.Compute(
            SvgRasterizer.Attr(root, "preserveAspectRatio"), vpW, vpH, widthPt, heightPt);
        // box-local (top-left, y-down): bx = par.Tx + par.ScaleX*(x - vpX); by = par.Ty + par.ScaleY*(y - vpY)
        // PDF: X = leftPt + bx; Y = bottomPt + heightPt - by  (flip within the box)
        var svgToPdf = new SKMatrix
        {
            ScaleX = par.ScaleX,
            SkewX = 0,
            TransX = (float)(leftPt + par.Tx - par.ScaleX * vpX),
            SkewY = 0,
            ScaleY = -par.ScaleY,
            TransY = (float)(bottomPt + heightPt - par.Ty + par.ScaleY * vpY),
            Persp0 = 0,
            Persp1 = 0,
            Persp2 = 1,
        };

        // One shared walk state, seeded with the user-space viewport extent so a `%` length in a shape /
        // stroke resolves against the viewBox (not zero). Ids stay empty — any url(#…) paint server bails the
        // document to raster before it would be looked up.
        var state = new SvgRenderState { ViewportW = vpW, ViewportH = vpH };
        var ops = new List<NativeOp>();
        var rootStyle = SvgRasterizer.ResolveStyle(root, SvgStyle.Initial, state);
        var supported = true;
        foreach (var child in root.Elements())
        {
            if (!Walk(child, svgToPdf, rootStyle, ops, state, depth: 1))
            {
                supported = false;
                break;
            }
        }
        if (!supported) { sawUnsupported = true; return false; }

        // All supported — commit the collected ops to the page (fills before their stroke, in document order).
        foreach (var op in ops)
        {
            if (op.IsFill)
                page.FillPath(op.Segments, op.R, op.G, op.B, op.Alpha, op.EvenOdd);
            else
                page.StrokePath(op.Segments, op.StrokeWidth, op.R, op.G, op.B, op.Alpha,
                    op.Dash, op.DashPhase, op.Cap, op.Join, op.Miter);
        }
        return true;
    }

    /// <summary>A single collected native paint (fill or stroke), applied to the page only if the whole
    /// document is supported.</summary>
    private readonly record struct NativeOp(
        IReadOnlyList<PdfPathSegment> Segments, bool IsFill,
        double R, double G, double B, double Alpha, bool EvenOdd,
        double StrokeWidth, double[]? Dash, double DashPhase, int Cap, int Join, double Miter);

    // Elements that DEFINE resources but render nothing themselves — safely skipped by the native walk (a
    // shape that REFERENCES one via url(#id) bails out as unsupported before reaching here).
    private static readonly HashSet<string> NonRendering = new(StringComparer.OrdinalIgnoreCase)
    {
        "defs", "title", "desc", "metadata", "symbol", "clippath", "mask", "filter", "marker",
        "lineargradient", "radialgradient", "pattern",
    };

    private static readonly HashSet<string> Shapes = new(StringComparer.OrdinalIgnoreCase)
    {
        "rect", "circle", "ellipse", "line", "polyline", "polygon", "path",
    };

    /// <summary>Recursively walk one element, appending its native paint ops. Returns <see langword="false"/>
    /// the moment an unsupported feature is hit (the caller aborts the whole document to the raster path).</summary>
    private static bool Walk(XElement el, SKMatrix ctm, SvgStyle inherited, List<NativeOp> ops, SvgRenderState state, int depth)
    {
        if (depth > SvgRasterizer.MaxDepth) return false;
        var name = el.Name.LocalName.ToLowerInvariant();
        if (NonRendering.Contains(name)) return true; // definition — renders nothing

        // Compose this element's own transform onto the CTM (child coords → parent coords → … → PDF).
        var local = ctm;
        if (SvgTransform.Parse(SvgRasterizer.Attr(el, "transform")) is { } t)
            local = ctm.PreConcat(t);

        var style = SvgRasterizer.ResolveStyle(el, inherited, state);

        if (name is "g" or "a")
        {
            foreach (var child in el.Elements())
                if (!Walk(child, local, style, ops, state, depth + 1)) return false;
            return true;
        }

        if (Shapes.Contains(name))
        {
            // A gradient / pattern paint server is out of scope for the native first cut → fall back to raster.
            if (style.FillRef is not null || style.StrokeRef is not null) return false;

            using var path = SvgRasterizer.BuildShapePath(el, style, state);
            if (path is null) return true; // a degenerate shape (e.g. zero radius) paints nothing — supported
            using var devicePath = new SKPath(path);
            devicePath.Transform(local);
            var segments = ToSegments(devicePath);
            if (segments.Count == 0) return true;

            var scale = Math.Sqrt(Math.Abs(local.ScaleX * local.ScaleY - local.SkewX * local.SkewY));
            var evenOdd = string.Equals(
                SvgRasterizer.Attr(el, "fill-rule")?.Trim(), "evenodd", StringComparison.OrdinalIgnoreCase);

            // Fill: a visible (non-none, non-transparent) fill at fill-opacity. FillRef was ruled out above.
            if (style.Fill.Alpha > 0 && style.FillOpacity > 0)
            {
                ops.Add(new NativeOp(
                    segments, IsFill: true,
                    style.Fill.Red / 255.0, style.Fill.Green / 255.0, style.Fill.Blue / 255.0,
                    style.Fill.Alpha / 255.0 * style.FillOpacity, evenOdd,
                    StrokeWidth: 0, Dash: null, DashPhase: 0, Cap: 0, Join: 0, Miter: 10));
            }

            // Stroke: a set stroke color with a positive width, scaled by the CTM into PDF points.
            if (style.Stroke is { } sc && style.StrokeWidth > 0 && style.StrokeOpacity > 0)
            {
                var widthPt = style.StrokeWidth * scale;
                double[]? dash = null;
                if (style.StrokeDash is { Length: > 0 } d)
                {
                    dash = new double[d.Length];
                    for (var i = 0; i < d.Length; i++) dash[i] = d[i] * scale;
                }
                ops.Add(new NativeOp(
                    segments, IsFill: false,
                    sc.Red / 255.0, sc.Green / 255.0, sc.Blue / 255.0,
                    sc.Alpha / 255.0 * style.StrokeOpacity, EvenOdd: false,
                    widthPt, dash, style.StrokeDashOffset * scale,
                    Cap: (int)style.StrokeCap, Join: (int)style.StrokeJoin, Miter: style.StrokeMiter));
            }
            return true;
        }

        // Any other element (text / tspan / image / use / switch / foreignObject / nested svg / …) is outside
        // the native subset → fall back to raster for the whole document.
        return false;
    }

    /// <summary>Flatten a device-space (already transformed to PDF points) <see cref="SKPath"/> into
    /// <see cref="PdfPathSegment"/>s, raising quads/conics to cubics — the same conversion the clip-path
    /// emitter uses (FragmentPainter.BuildPathClipSegments).</summary>
    private static List<PdfPathSegment> ToSegments(SKPath path)
    {
        var segs = new List<PdfPathSegment>();
        using var it = path.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    segs.Add(PdfPathSegment.Move(pts[0].X, pts[0].Y));
                    break;
                case SKPathVerb.Line:
                    segs.Add(PdfPathSegment.Line(pts[1].X, pts[1].Y));
                    break;
                case SKPathVerb.Quad:
                    AddQuad(segs, pts[0], pts[1], pts[2]);
                    break;
                case SKPathVerb.Conic:
                    // Conic → up to N quads (weight-driven); raise each to a cubic. 1 pow2 = 2 quads is plenty
                    // for the shapes this cut handles (arcs come only from <path> A commands, already rare).
                    var quads = new SKPoint[5];
                    SKPath.ConvertConicToQuads(pts[0], pts[1], pts[2], it.ConicWeight(), quads, 1);
                    AddQuad(segs, quads[0], quads[1], quads[2]);
                    AddQuad(segs, quads[2], quads[3], quads[4]);
                    break;
                case SKPathVerb.Cubic:
                    segs.Add(PdfPathSegment.Curve(pts[1].X, pts[1].Y, pts[2].X, pts[2].Y, pts[3].X, pts[3].Y));
                    break;
                case SKPathVerb.Close:
                    segs.Add(PdfPathSegment.Close);
                    break;
            }
        }
        return segs;
    }

    /// <summary>Raise a quadratic Bézier (p0→p1(control)→p2) to a cubic (2/3 rule) and append it.</summary>
    private static void AddQuad(List<PdfPathSegment> segs, SKPoint p0, SKPoint p1, SKPoint p2)
    {
        var c1 = new SKPoint(p0.X + 2f / 3f * (p1.X - p0.X), p0.Y + 2f / 3f * (p1.Y - p0.Y));
        var c2 = new SKPoint(p2.X + 2f / 3f * (p1.X - p2.X), p2.Y + 2f / 3f * (p1.Y - p2.Y));
        segs.Add(PdfPathSegment.Curve(c1.X, c1.Y, c2.X, c2.Y, p2.X, p2.Y));
    }
}
