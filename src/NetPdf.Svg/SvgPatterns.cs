// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using SkiaSharp;

namespace NetPdf.Svg;

/// <summary>SVG <c>&lt;pattern&gt;</c> paint-server support (§13.3) for the raster renderer — renders the
/// pattern content once into a tile and wraps it in a repeating shader. Extracted from
/// <see cref="SvgRasterizer"/> (PR-245 refactor — keep feature areas as small internal collaborators).</summary>
internal static class SvgPatterns
{
    private const int MaxPatternDepth = 4;          // a pattern's content can reference another pattern
    private const long MaxPatternTilePixels = 4L * 1024 * 1024; // tile-bitmap area cap (DoS)

    /// <summary>Build a repeating-tile <see cref="SKShader"/> for a <c>&lt;pattern&gt;</c> paint server:
    /// resolve the tile rectangle (honoring <c>patternUnits</c> — objectBoundingBox default vs
    /// userSpaceOnUse), render the pattern's content ONCE into a tile bitmap (mapping a <c>viewBox</c> to the
    /// tile, or scaling by <c>patternContentUnits="objectBoundingBox"</c>), and wrap it in a Repeat/Repeat
    /// shader positioned at the tile origin with the optional <c>patternTransform</c>. Geometry attributes +
    /// content inherit through an <c>href</c> chain. Returns <see langword="null"/> (caller flags unsupported)
    /// for a missing size, no content, an over-cap tile, or a self-referential nesting beyond the depth cap.</summary>
    public static SvgResolvedShader? BuildPatternShader(XElement pattern, SKRect bbox, float opacity, SvgStyle style, SvgRenderState state)
    {
        if (state.PatternDepth >= MaxPatternDepth) { state.SawUnsupported = true; return null; }

        var unitsObb = !string.Equals(ResolveHrefAttr(pattern, state.Ids, "patternUnits") ?? "objectBoundingBox",
            "userSpaceOnUse", StringComparison.OrdinalIgnoreCase);
        var contentObb = string.Equals(ResolveHrefAttr(pattern, state.Ids, "patternContentUnits") ?? "userSpaceOnUse",
            "objectBoundingBox", StringComparison.OrdinalIgnoreCase);

        var tileW = PatternCoord(ResolveHrefAttr(pattern, state.Ids, "width"), unitsObb, bbox.Width);
        var tileH = PatternCoord(ResolveHrefAttr(pattern, state.Ids, "height"), unitsObb, bbox.Height);
        if (!(tileW > 0) || !(tileH > 0)) { state.SawUnsupported = true; return null; }
        var rawX = PatternCoord(ResolveHrefAttr(pattern, state.Ids, "x"), unitsObb, bbox.Width);
        var rawY = PatternCoord(ResolveHrefAttr(pattern, state.Ids, "y"), unitsObb, bbox.Height);
        var tileX = unitsObb ? bbox.Left + rawX : rawX;
        var tileY = unitsObb ? bbox.Top + rawY : rawY;

        var pxW = (int)Math.Ceiling(tileW);
        var pxH = (int)Math.Ceiling(tileH);
        if (pxW <= 0 || pxH <= 0 || (long)pxW * pxH > MaxPatternTilePixels) { state.SawUnsupported = true; return null; }

        var content = ResolveHrefContent(pattern, state.Ids);
        if (content is null) return null; // an empty pattern paints nothing (not a feature gap)
        var viewBox = SvgRasterizer.ParseViewBox(ResolveHrefAttr(pattern, state.Ids, "viewBox"));

        SKImage image;
        state.PatternDepth++;
        try
        {
            using var bmp = new SKBitmap(pxW, pxH, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var tileCanvas = new SKCanvas(bmp))
            {
                tileCanvas.Clear(SKColors.Transparent);
                var opLayer = opacity < 1f;
                if (opLayer)
                {
                    using var op = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(opacity * 255f)) };
                    tileCanvas.SaveLayer(op);
                }
                if (viewBox.W > 0 && viewBox.H > 0)
                {
                    var sc = Math.Min(tileW / viewBox.W, tileH / viewBox.H); // xMidYMid meet
                    tileCanvas.Translate((float)((tileW - viewBox.W * sc) / 2.0), (float)((tileH - viewBox.H * sc) / 2.0));
                    tileCanvas.Scale((float)sc);
                    tileCanvas.Translate((float)-viewBox.X, (float)-viewBox.Y);
                }
                else if (contentObb)
                {
                    tileCanvas.Scale(bbox.Width, bbox.Height);
                }
                // The pattern element's own presentation attributes seed the content context (a fresh tree).
                var contentStyle = SvgRasterizer.ResolveStyle(pattern, SvgStyle.Initial, state);
                foreach (var child in content.Elements()) SvgRasterizer.RenderElement(tileCanvas, child, contentStyle, state, depth: 1);
                if (opLayer) tileCanvas.Restore();
            }
            image = SKImage.FromBitmap(bmp); // copies the mutable bitmap → owns its pixels
        }
        finally { state.PatternDepth--; }

        var local = SKMatrix.CreateTranslation((float)tileX, (float)tileY);
        if (SvgTransform.Parse(SvgRasterizer.Attr(pattern, "patternTransform")) is { } pt) local = pt.PreConcat(local);
        var shader = image.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, local);
        return new SvgResolvedShader(shader, image);
    }

    /// <summary>A pattern tile coordinate / length: with <c>objectBoundingBox</c> units a bare number /
    /// fraction and a percentage are both fractions of <paramref name="reference"/>; with
    /// <c>userSpaceOnUse</c> a bare number is an absolute length and a percentage resolves against the
    /// reference. Absent → 0.</summary>
    private static double PatternCoord(string? raw, bool obb, double reference)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        raw = raw.Trim();
        if (raw.EndsWith("%", StringComparison.Ordinal))
            return double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) ? pct / 100.0 * reference : 0;
        if (!double.TryParse(SvgRasterizer.TrimUnit(raw), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return 0;
        return obb ? v * reference : v;
    }

    /// <summary>Resolve an attribute on a paint server, following its <c>href</c> chain when the element
    /// itself doesn't set it (SVG §13.3 — geometry + config attributes inherit). Cycle-guarded.</summary>
    private static string? ResolveHrefAttr(XElement el, IReadOnlyDictionary<string, XElement> ids, string name)
    {
        var src = el;
        var visited = new HashSet<XElement>();
        for (var depth = 0; src is not null && depth < 16 && visited.Add(src); depth++)
        {
            if (SvgAttr.Get(src, name) is { } v) return v;
            src = SvgAttr.HrefId(src) is { } id && ids.TryGetValue(id, out var t) ? t : null;
        }
        return null;
    }

    /// <summary>The nearest element in the <c>href</c> chain that has child content (a pattern with no
    /// children inherits its tile content from its referenced pattern). Cycle-guarded.</summary>
    private static XElement? ResolveHrefContent(XElement el, IReadOnlyDictionary<string, XElement> ids)
    {
        var src = el;
        var visited = new HashSet<XElement>();
        for (var depth = 0; src is not null && depth < 16 && visited.Add(src); depth++)
        {
            if (src.HasElements) return src;
            src = SvgAttr.HrefId(src) is { } id && ids.TryGetValue(id, out var t) ? t : null;
        }
        return null;
    }
}
