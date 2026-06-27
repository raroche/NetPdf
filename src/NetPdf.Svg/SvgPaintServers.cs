// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using SkiaSharp;

namespace NetPdf.Svg;

/// <summary>Phase 4 SVG part 2 (PR 7) — resolves an SVG gradient paint server (<c>&lt;linearGradient&gt;</c>
/// / <c>&lt;radialGradient&gt;</c> referenced by <c>fill="url(#id)"</c> or <c>stroke="url(#id)"</c>) into a
/// Skia <see cref="SKShader"/> for the raster renderer. Honors <c>gradientUnits</c>
/// (<c>objectBoundingBox</c> default + <c>userSpaceOnUse</c>), <c>spreadMethod</c> (pad/reflect/repeat),
/// <c>gradientTransform</c>, the <c>&lt;stop&gt;</c> list (offset / stop-color / stop-opacity), a radial
/// focal point (<c>fx</c>/<c>fy</c>), and stop/attribute inheritance through an <c>href</c> chain (SVG
/// §13.2). Patterns and CSS color-interpolation hints are out of scope (raster fallback).</summary>
internal static class SvgPaintServers
{
    private const int MaxHrefDepth = 16;

    private readonly record struct GradStop(float Offset, SKColor Color);

    /// <summary>Build a shader for <paramref name="grad"/> filling a shape whose user-space bounding box is
    /// <paramref name="bbox"/>. <paramref name="opacityMult"/> (0..1) folds an element fill/stroke-opacity
    /// into every stop's alpha (Skia can't multiply a shader's alpha through the paint). Returns
    /// <see langword="null"/> when the element isn't a gradient, has no stops, or is geometrically
    /// degenerate.</summary>
    public static SKShader? BuildShader(
        XElement grad, SKRect bbox, IReadOnlyDictionary<string, XElement> ids,
        double viewportW, double viewportH, float opacityMult)
    {
        var name = grad.Name.LocalName;
        var linear = name.Equals("linearGradient", StringComparison.OrdinalIgnoreCase);
        var radial = name.Equals("radialGradient", StringComparison.OrdinalIgnoreCase);
        if (!linear && !radial) return null;

        var stops = CollectStops(grad, ids, opacityMult);
        if (stops.Count == 0) return null;
        if (stops.Count == 1) stops.Add(new GradStop(1f, stops[0].Color)); // single stop = a flat fill

        var colors = new SKColor[stops.Count];
        var positions = new float[stops.Count];
        for (var i = 0; i < stops.Count; i++) { colors[i] = stops[i].Color; positions[i] = stops[i].Offset; }

        var units = ResolveAttr(grad, ids, "gradientUnits");
        var obb = units is null || !units.Equals("userSpaceOnUse", StringComparison.OrdinalIgnoreCase);
        if (obb && (bbox.Width <= 0 || bbox.Height <= 0)) return null;

        var tile = (ResolveAttr(grad, ids, "spreadMethod")?.Trim().ToLowerInvariant()) switch
        {
            "reflect" => SKShaderTileMode.Mirror,
            "repeat" => SKShaderTileMode.Repeat,
            _ => SKShaderTileMode.Clamp,
        };

        // The shader's local matrix maps gradient coordinate space → the shape's user space. For
        // objectBoundingBox the unit square [0,1]² is mapped onto the bbox (so a "circle" can become an
        // ellipse over a non-square box, matching browsers); userSpaceOnUse needs no remap. gradientTransform
        // then composes within that space.
        var matrix = obb
            ? SKMatrix.CreateTranslation(bbox.Left, bbox.Top).PreConcat(SKMatrix.CreateScale(bbox.Width, bbox.Height))
            : SKMatrix.Identity;
        if (ParseTransform(ResolveAttr(grad, ids, "gradientTransform")) is { } gt) matrix = matrix.PreConcat(gt);

        var refW = obb ? 1.0 : viewportW;
        var refH = obb ? 1.0 : viewportH;
        // userSpaceOnUse uses the diagonal reference for r per SVG (sqrt((w²+h²)/2)); obb r is a unit fraction.
        var refR = obb ? 1.0 : Math.Sqrt((viewportW * viewportW + viewportH * viewportH) / 2.0);

        if (linear)
        {
            var p1 = new SKPoint(Coord(ResolveAttr(grad, ids, "x1"), obb, refW, 0),
                                 Coord(ResolveAttr(grad, ids, "y1"), obb, refH, 0));
            var p2 = new SKPoint(Coord(ResolveAttr(grad, ids, "x2"), obb, refW, 1),
                                 Coord(ResolveAttr(grad, ids, "y2"), obb, refH, 0));
            if (p1 == p2) return SKShader.CreateColor(colors[^1]); // zero-length vector = last stop (SVG §13.2.4)
            return SKShader.CreateLinearGradient(p1, p2, colors, positions, tile, matrix);
        }

        var cx = Coord(ResolveAttr(grad, ids, "cx"), obb, refW, 0.5f);
        var cy = Coord(ResolveAttr(grad, ids, "cy"), obb, refH, 0.5f);
        var r = Coord(ResolveAttr(grad, ids, "r"), obb, refR, 0.5f);
        if (!(r > 0)) return SKShader.CreateColor(colors[^1]); // r=0 = last stop
        var center = new SKPoint(cx, cy);
        var fxRaw = ResolveAttr(grad, ids, "fx");
        var fyRaw = ResolveAttr(grad, ids, "fy");
        var focal = new SKPoint(
            fxRaw is null ? cx : Coord(fxRaw, obb, refW, 0.5f),
            fyRaw is null ? cy : Coord(fyRaw, obb, refH, 0.5f));
        return focal == center
            ? SKShader.CreateRadialGradient(center, r, colors, positions, tile, matrix)
            : SKShader.CreateTwoPointConicalGradient(focal, 0, center, r, colors, positions, tile, matrix);
    }

    /// <summary>Collect the <c>&lt;stop&gt;</c> list — from the gradient itself, or (when it has none) the
    /// nearest <c>href</c>-referenced gradient that does (SVG §13.2.4). Offsets are clamped to [0,1] and made
    /// non-decreasing; <paramref name="opacityMult"/> folds into each stop's alpha.</summary>
    private static List<GradStop> CollectStops(
        XElement grad, IReadOnlyDictionary<string, XElement> ids, float opacityMult)
    {
        var result = new List<GradStop>();
        var src = grad;
        var visited = new HashSet<XElement>();
        for (var depth = 0; src is not null && depth < MaxHrefDepth && visited.Add(src); depth++)
        {
            var last = 0f;
            var found = false;
            foreach (var stop in src.Elements())
            {
                if (!stop.Name.LocalName.Equals("stop", StringComparison.OrdinalIgnoreCase)) continue;
                found = true;
                var offset = ParseOffset(SvgAttr.Get(stop, "offset"));
                offset = Math.Clamp(offset, last, 1f);
                last = offset;

                var color = SKColors.Black;
                var colorRaw = SvgAttr.Presentation(stop, "stop-color");
                if (colorRaw is not null && !colorRaw.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
                    SvgColor.TryParse(colorRaw, out color);
                var alpha = color.Alpha / 255f * ParseOpacity(SvgAttr.Presentation(stop, "stop-opacity")) * opacityMult;
                result.Add(new GradStop(offset, color.WithAlpha((byte)Math.Clamp((int)Math.Round(alpha * 255f), 0, 255))));
            }
            if (found) break;
            src = SvgAttr.HrefId(src) is { } id && ids.TryGetValue(id, out var target) ? target : null;
        }
        return result;
    }

    /// <summary>Resolve a geometry/config attribute on the gradient, following the <c>href</c> chain when the
    /// element itself doesn't set it (SVG §13.2.4 — geometry + config attributes inherit).</summary>
    private static string? ResolveAttr(XElement grad, IReadOnlyDictionary<string, XElement> ids, string name)
    {
        var src = grad;
        var visited = new HashSet<XElement>();
        for (var depth = 0; src is not null && depth < MaxHrefDepth && visited.Add(src); depth++)
        {
            if (SvgAttr.Get(src, name) is { } v) return v;
            src = SvgAttr.HrefId(src) is { } id && ids.TryGetValue(id, out var target) ? target : null;
        }
        return null;
    }

    /// <summary>A gradient coordinate. With <c>objectBoundingBox</c> a bare number is a unit fraction and a
    /// percentage is its /100; with <c>userSpaceOnUse</c> a bare number is an absolute length and a percentage
    /// resolves against <paramref name="reference"/>. <paramref name="defaultFraction"/> applies when unset.</summary>
    private static float Coord(string? raw, bool obb, double reference, float defaultFraction)
    {
        if (raw is null) return (float)(defaultFraction * reference);
        raw = raw.Trim();
        if (raw.EndsWith('%') &&
            double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            return (float)(pct / 100.0 * reference);
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return (float)(defaultFraction * reference);
        return (float)(obb ? v * reference : v); // obb: bare number is a fraction (reference == 1)
    }

    private static float ParseOffset(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0f;
        raw = raw.Trim();
        if (raw.EndsWith('%') &&
            double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            return (float)Math.Clamp(pct / 100.0, 0, 1);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? (float)Math.Clamp(v, 0, 1) : 0f;
    }

    private static float ParseOpacity(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 1f;
        raw = raw.Trim();
        if (raw.EndsWith('%') &&
            double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            return (float)Math.Clamp(pct / 100.0, 0, 1);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? (float)Math.Clamp(v, 0, 1) : 1f;
    }

    /// <summary>Parse a <c>gradientTransform</c> list (translate / scale / rotate / matrix / skewX / skewY)
    /// into a single matrix, or <see langword="null"/> when empty / unparseable.</summary>
    private static SKMatrix? ParseTransform(string? raw) => SvgTransform.Parse(raw);
}
