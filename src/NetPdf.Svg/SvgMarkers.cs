// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using SkiaSharp;

namespace NetPdf.Svg;

/// <summary>SVG marker support (§11.6) for the raster renderer — paints <c>marker-start</c>/<c>-mid</c>/
/// <c>-end</c> at a shape's vertices. Extracted from <see cref="SvgRasterizer"/> (PR-245 refactor — keep
/// feature areas as small internal collaborators).</summary>
internal static class SvgMarkers
{
    private readonly record struct MarkerVertex(SKPoint P, double InDeg, double OutDeg);

    /// <summary>Paint <c>marker-start</c> / <c>marker-mid</c> / <c>marker-end</c> (resolved through the
    /// inherited <paramref name="style"/>) at a shape's vertices: start at the first vertex (oriented along
    /// the outgoing segment), end at the last (incoming), mid at the interior vertices (bisector). Honors
    /// <c>markerWidth</c>/<c>Height</c>, <c>refX</c>/<c>refY</c>, <c>markerUnits</c> (strokeWidth default /
    /// userSpaceOnUse), <c>orient</c> (auto / auto-start-reverse / angle), and the marker's
    /// <c>viewBox</c>/<c>preserveAspectRatio</c>. Path markers orient to the EXACT curve tangent at each
    /// vertex (from the segment's control points), not the chord between endpoints.</summary>
    public static void DrawMarkers(SKCanvas canvas, XElement el, SvgStyle style, SvgRenderState state)
    {
        var startRef = style.MarkerStart;
        var midRef = style.MarkerMid;
        var endRef = style.MarkerEnd;
        if (startRef is null && midRef is null && endRef is null) return;
        var verts = ExtractVertices(el, style, state);
        if (verts.Count == 0) return;

        for (var i = 0; i < verts.Count; i++)
        {
            var refId = i == 0 ? startRef : i == verts.Count - 1 ? endRef : midRef;
            if (refId is null || !state.Ids.TryGetValue(refId, out var marker)
                || !marker.Name.LocalName.Equals("marker", StringComparison.OrdinalIgnoreCase))
            {
                if (refId is not null) state.SawUnsupported = true;
                continue;
            }
            var v = verts[i];
            var orientDeg = i == 0 ? v.OutDeg : i == verts.Count - 1 ? v.InDeg : Bisector(v.InDeg, v.OutDeg);
            DrawOneMarker(canvas, marker, v.P, orientDeg, style.StrokeWidth, isStart: i == 0, state);
        }
    }

    private static void DrawOneMarker(
        SKCanvas canvas, XElement marker, SKPoint at, double orientDeg, float strokeWidth, bool isStart,
        SvgRenderState state)
    {
        // markerWidth/Height are <length> values (px/pt/unitless) — default 3 when absent (§11.6.2).
        var mw = marker.Attribute("markerWidth") is not null ? SvgRasterizer.Num(marker, "markerWidth") : 3;
        var mh = marker.Attribute("markerHeight") is not null ? SvgRasterizer.Num(marker, "markerHeight") : 3;
        if (!(mw > 0) || !(mh > 0)) return;
        var refX = SvgRasterizer.Num(marker, "refX");
        var refY = SvgRasterizer.Num(marker, "refY");
        var unitsStroke = !(SvgRasterizer.Attr(marker, "markerUnits") ?? "strokeWidth").Trim().Equals("userSpaceOnUse", StringComparison.OrdinalIgnoreCase);

        // orient: auto follows the path; auto-start-reverse reverses the START marker; a number is fixed.
        var orient = (SvgRasterizer.Attr(marker, "orient") ?? "0").Trim();
        double angle;
        if (orient.Equals("auto", StringComparison.OrdinalIgnoreCase)) angle = orientDeg;
        else if (orient.Equals("auto-start-reverse", StringComparison.OrdinalIgnoreCase)) angle = orientDeg + (isStart ? 180 : 0);
        else angle = double.TryParse(orient.TrimEnd('d', 'e', 'g', 'D', 'E', 'G', ' '), NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : 0;

        var vb = SvgRasterizer.ParseViewBox(SvgRasterizer.Attr(marker, "viewBox"));
        var par = vb.W > 0 && vb.H > 0
            ? SvgPreserveAspectRatio.Compute(SvgRasterizer.Attr(marker, "preserveAspectRatio"), vb.W, vb.H, mw, mh)
            : new SvgPar(1, 1, 0, 0, false);
        // The reference point (in content coords) mapped into the marker viewport, aligned to the vertex.
        var refVx = vb.W > 0 ? par.Tx + (refX - vb.X) * par.ScaleX : refX;
        var refVy = vb.H > 0 ? par.Ty + (refY - vb.Y) * par.ScaleY : refY;

        var save = canvas.Save();
        canvas.Translate(at.X, at.Y);
        canvas.RotateDegrees((float)angle);
        if (unitsStroke && strokeWidth > 0) canvas.Scale(strokeWidth);
        canvas.Translate((float)-refVx, (float)-refVy);
        canvas.ClipRect(new SKRect(0, 0, (float)mw, (float)mh)); // overflow hidden (the default)
        if (vb.W > 0 && vb.H > 0)
        {
            canvas.Translate(par.Tx, par.Ty);
            canvas.Scale(par.ScaleX, par.ScaleY);
            canvas.Translate((float)-vb.X, (float)-vb.Y);
        }
        // Marker content renders in a fresh context seeded by the marker's own presentation attributes.
        var markerStyle = SvgRasterizer.ResolveStyle(marker, SvgStyle.Initial, state);
        foreach (var child in marker.Elements()) SvgRasterizer.RenderElement(canvas, child, markerStyle, state, depth: 1);
        canvas.RestoreToCount(save);
    }

    /// <summary>The bisector orientation (degrees) for a mid marker, averaging the incoming + outgoing
    /// directions as unit vectors (handles the angle wraparound).</summary>
    private static double Bisector(double inDeg, double outDeg)
    {
        var ir = inDeg * Math.PI / 180.0;
        var or = outDeg * Math.PI / 180.0;
        var x = Math.Cos(ir) + Math.Cos(or);
        var y = Math.Sin(ir) + Math.Sin(or);
        return x == 0 && y == 0 ? inDeg : Math.Atan2(y, x) * 180.0 / Math.PI;
    }

    /// <summary>The shape's vertices in order, each with the incoming + outgoing segment direction (degrees).
    /// line / polyline / polygon use their points; a path uses its on-path verb endpoints (chord tangents).</summary>
    private static List<MarkerVertex> ExtractVertices(XElement el, SvgStyle style, SvgRenderState state)
    {
        switch (el.Name.LocalName.ToLowerInvariant())
        {
            case "line":
            {
                var pts = new List<SKPoint>
                {
                    new((float)SvgRasterizer.Len(el, "x1", state, style, SvgRasterizer.LenAxis.X), (float)SvgRasterizer.Len(el, "y1", state, style, SvgRasterizer.LenAxis.Y)),
                    new((float)SvgRasterizer.Len(el, "x2", state, style, SvgRasterizer.LenAxis.X), (float)SvgRasterizer.Len(el, "y2", state, style, SvgRasterizer.LenAxis.Y)),
                };
                return ToVertices(pts);
            }
            case "polyline":
            case "polygon":
            {
                var pts = new List<SKPoint>(SvgRasterizer.ParsePoints(SvgRasterizer.Attr(el, "points")));
                if (el.Name.LocalName.Equals("polygon", StringComparison.OrdinalIgnoreCase) && pts.Count > 0) pts.Add(pts[0]);
                return ToVertices(pts);
            }
            case "path":
                using (var p = SvgRasterizer.BuildShapePath(el, style, state))
                    return p is not null ? PathVertices(p) : new List<MarkerVertex>();
        }
        return new List<MarkerVertex>();
    }

    /// <summary>Marker vertices for a <c>&lt;path&gt;</c> with EXACT curve tangents (SVG §11.6.2): a vertex's
    /// orientation follows the curve's tangent at that point, computed from the segment's control points
    /// (the first control distinct from the start for the OUTGOING direction; the last control distinct from
    /// the end for the INCOMING direction) rather than the straight chord between on-path endpoints. A line /
    /// close segment's tangent IS its chord. A shared vertex carries the incoming tangent of the arriving
    /// segment and the outgoing tangent of the leaving segment (the mid-marker bisects them).</summary>
    private static List<MarkerVertex> PathVertices(SKPath path)
    {
        var result = new List<MarkerVertex>();
        using var it = path.CreateRawIterator();
        var buf = new SKPoint[4];
        SKPathVerb verb;
        var subpathStart = new SKPoint();

        // Append the segment's END vertex (incoming tangent = arriveDir) and back-fill the PREVIOUS vertex's
        // outgoing tangent (leaveDir) — the previous vertex is where this segment starts.
        void Segment(double leaveDir, double arriveDir, SKPoint end)
        {
            if (result.Count > 0) result[^1] = result[^1] with { OutDeg = leaveDir };
            result.Add(new MarkerVertex(end, arriveDir, arriveDir));
        }

        while ((verb = it.Next(buf)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move: subpathStart = buf[0]; result.Add(new MarkerVertex(buf[0], 0, 0)); break;
                case SKPathVerb.Line: { var d = Dir(buf[0], buf[1]); Segment(d, d, buf[1]); break; }
                case SKPathVerb.Quad:
                case SKPathVerb.Conic: Segment(LeaveDir(buf[0], buf[1], buf[2]), ArriveDir(buf[0], buf[1], buf[2]), buf[2]); break;
                case SKPathVerb.Cubic: Segment(LeaveDir(buf[0], buf[1], buf[2], buf[3]), ArriveDir(buf[0], buf[1], buf[2], buf[3]), buf[3]); break;
                case SKPathVerb.Close: { var d = Dir(buf[0], subpathStart); Segment(d, d, subpathStart); break; } // closing segment
            }
        }
        return result;
    }

    private static bool Near(SKPoint a, SKPoint b) => Math.Abs(a.X - b.X) < 1e-4f && Math.Abs(a.Y - b.Y) < 1e-4f;

    /// <summary>The OUTGOING tangent leaving <c>pts[0]</c>: the direction to the first later point distinct
    /// from it (a control point, else the segment end) — the curve's tangent at its start.</summary>
    private static double LeaveDir(params SKPoint[] pts)
    {
        for (var i = 1; i < pts.Length; i++) if (!Near(pts[0], pts[i])) return Dir(pts[0], pts[i]);
        return 0;
    }

    /// <summary>The INCOMING tangent arriving at the last point: the direction from the last earlier point
    /// distinct from it (a control point, else the segment start) — the curve's tangent at its end.</summary>
    private static double ArriveDir(params SKPoint[] pts)
    {
        var to = pts[^1];
        for (var i = pts.Length - 2; i >= 0; i--) if (!Near(pts[i], to)) return Dir(pts[i], to);
        return 0;
    }

    private static List<MarkerVertex> ToVertices(List<SKPoint> pts)
    {
        var result = new List<MarkerVertex>(pts.Count);
        for (var i = 0; i < pts.Count; i++)
        {
            var inDeg = i > 0 ? Dir(pts[i - 1], pts[i]) : (i + 1 < pts.Count ? Dir(pts[i], pts[i + 1]) : 0);
            var outDeg = i + 1 < pts.Count ? Dir(pts[i], pts[i + 1]) : inDeg;
            result.Add(new MarkerVertex(pts[i], inDeg, outDeg));
        }
        return result;
    }

    private static double Dir(SKPoint a, SKPoint b) => Math.Atan2(b.Y - a.Y, b.X - a.X) * 180.0 / Math.PI;
}
