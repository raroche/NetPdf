// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;
using System.Xml.Linq;
using SkiaSharp;

namespace NetPdf.Svg;

/// <summary>SVG <c>filter</c> support (§15) for the raster renderer — resolves a <c>filter="url(#id)"</c>
/// reference and composes a <c>&lt;filter&gt;</c>'s primitive children into a single Skia
/// <see cref="SKImageFilter"/> applied to the element subtree. Extracted from <see cref="SvgRasterizer"/>
/// (PR-244 review — keep feature areas as small internal collaborators).</summary>
internal static class SvgFilters
{
    /// <summary>Resolve a <c>filter="url(#id)"</c> reference to its <c>&lt;filter&gt;</c> element, or
    /// <see langword="null"/> for <c>none</c> / absent. A url() that doesn't resolve to a <c>&lt;filter&gt;</c>
    /// is flagged (and the element renders unfiltered).</summary>
    public static XElement? ResolveFilter(XElement el, SvgRenderState state)
    {
        var raw = SvgAttr.Presentation(el, "filter");
        if (raw is null) return null;
        raw = raw.Trim();
        if (raw.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        if (SvgRasterizer.PaintServerId(raw) is { } id && state.Ids.TryGetValue(id, out var f)
            && f.Name.LocalName.Equals("filter", StringComparison.OrdinalIgnoreCase))
            return f;
        state.SawUnsupported = true;
        return null;
    }

    /// <summary>Compose a <c>&lt;filter&gt;</c>'s primitive children into a single Skia
    /// <see cref="SKImageFilter"/> applied to the element's rendering (the <c>SourceGraphic</c>). A LINEAR
    /// chain is supported — each primitive feeds the next — covering <c>feGaussianBlur</c>, <c>feOffset</c>,
    /// <c>feDropShadow</c>, and <c>feColorMatrix</c> (matrix / saturate / hueRotate / luminanceToAlpha).
    /// Graph-routing primitives (<c>feMerge</c> / <c>feComposite</c> / <c>feBlend</c> / <c>feFlood</c> /
    /// <c>feImage</c> / …), the <c>in</c>/<c>result</c> named-input routing, primitive subregions, and the
    /// filter region / <c>*Units</c> aren't modeled this cut → flagged. Returns <see langword="null"/> when no
    /// supported primitive contributes (the element renders unfiltered).</summary>
    public static SKImageFilter? BuildImageFilter(XElement filter, SvgRenderState state)
    {
        SKImageFilter? current = null; // null input = SourceGraphic
        // The filter region (x/y/width/height) and *Units change the result geometry but aren't applied here.
        var sawUnsupported = SvgRasterizer.HasAnyAttr(filter, "x", "y", "width", "height", "filterUnits", "primitiveUnits");
        foreach (var prim in filter.Elements())
        {
            // A named result, a primitive subregion, or an `in` we can't honor implies a routing/region model
            // this linear chain doesn't model → flag (the chain still renders as a best effort). An explicit
            // `in` is honored ONLY as the FIRST primitive's `in="SourceGraphic"` (≡ the implicit chain input);
            // once a prior result exists we feed the chain regardless of `in`, so any `in` then is wrong.
            if (SvgRasterizer.HasAnyAttr(prim, "result", "x", "y", "width", "height"))
                sawUnsupported = true;
            else if (SvgRasterizer.Attr(prim, "in") is { } input)
            {
                var inIsSource = input.Trim().Equals("SourceGraphic", StringComparison.OrdinalIgnoreCase);
                if (current is not null || !inIsSource) sawUnsupported = true;
            }

            switch (prim.Name.LocalName.ToLowerInvariant())
            {
                case "fegaussianblur":
                {
                    var (sx, sy) = ParseStdDeviation(SvgRasterizer.Attr(prim, "stdDeviation"));
                    if (sx > 0 || sy > 0) current = SKImageFilter.CreateBlur(sx, sy, current);
                    break;
                }
                case "feoffset":
                    current = SKImageFilter.CreateOffset((float)SvgRasterizer.Num(prim, "dx"), (float)SvgRasterizer.Num(prim, "dy"), current);
                    break;
                case "fedropshadow":
                {
                    // A self-contained drop shadow: blur + offset + flood, with the source drawn on top.
                    var (bx, by) = ParseStdDeviation(SvgRasterizer.Attr(prim, "stdDeviation"));
                    if (bx == 0 && prim.Attribute("stdDeviation") is null) { bx = 2; by = 2; } // default σ=2
                    var dx = prim.Attribute("dx") is not null ? (float)SvgRasterizer.Num(prim, "dx") : 2f;
                    var dy = prim.Attribute("dy") is not null ? (float)SvgRasterizer.Num(prim, "dy") : 2f;
                    var color = SKColors.Black;
                    if (SvgAttr.Presentation(prim, "flood-color") is { } fc) SvgColor.TryParse(fc, out color);
                    var floodOpacity = ParseFloodOpacity(SvgAttr.Presentation(prim, "flood-opacity"));
                    color = color.WithAlpha((byte)Math.Clamp((int)Math.Round(color.Alpha / 255f * floodOpacity * 255f), 0, 255));
                    current = SKImageFilter.CreateDropShadow(dx, dy, bx, by, color, current);
                    break;
                }
                case "fecolormatrix":
                {
                    var matrix = BuildColorMatrix(prim);
                    if (matrix is not null)
                    {
                        using var cf = SKColorFilter.CreateColorMatrix(matrix);
                        current = SKImageFilter.CreateColorFilter(cf, current);
                    }
                    break;
                }
                default:
                    sawUnsupported = true; // unsupported primitive / graph routing
                    break;
            }
        }
        if (sawUnsupported) state.SawUnsupported = true;
        return current;
    }

    /// <summary><c>flood-opacity</c> (0..1, or a percentage) — default 1.</summary>
    private static float ParseFloodOpacity(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 1f;
        raw = raw.Trim();
        if (raw.EndsWith("%", StringComparison.Ordinal))
            return double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) ? (float)Math.Clamp(pct / 100.0, 0, 1) : 1f;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (float)Math.Clamp(v, 0, 1) : 1f;
    }

    /// <summary><c>feGaussianBlur stdDeviation</c> — one value (isotropic) or two (x then y). The SVG std
    /// deviation IS the Gaussian sigma Skia's blur takes directly.</summary>
    private static (float X, float Y) ParseStdDeviation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (0, 0);
        var t = raw.Split(new[] { ' ', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        float P(int i) => i < t.Length && float.TryParse(t[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 0;
        var x = P(0);
        return (x, t.Length > 1 ? P(1) : x);
    }

    /// <summary>Build the 20-entry color matrix for a <c>feColorMatrix</c> (<c>type</c> = matrix [default] /
    /// saturate / hueRotate / luminanceToAlpha), or <see langword="null"/> when it's a degenerate identity /
    /// unparseable. Row-major RGBA with the 5th column the bias, matching Skia's
    /// <c>SKColorFilter.CreateColorMatrix</c>.</summary>
    private static float[]? BuildColorMatrix(XElement prim)
    {
        var type = (SvgRasterizer.Attr(prim, "type") ?? "matrix").Trim().ToLowerInvariant();
        switch (type)
        {
            case "matrix":
            {
                var raw = SvgRasterizer.Attr(prim, "values");
                if (string.IsNullOrWhiteSpace(raw)) return null;
                var t = raw.Split(new[] { ' ', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length != 20) return null;
                var m = new float[20];
                for (var i = 0; i < 20; i++)
                    if (!float.TryParse(t[i], NumberStyles.Float, CultureInfo.InvariantCulture, out m[i])) return null;
                return m;
            }
            case "saturate":
            {
                var s = 1f;
                if (SvgRasterizer.Attr(prim, "values") is { } sv) float.TryParse(sv.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out s);
                // SVG §15 saturate matrix.
                return
                [
                    0.213f + 0.787f * s, 0.715f - 0.715f * s, 0.072f - 0.072f * s, 0, 0,
                    0.213f - 0.213f * s, 0.715f + 0.285f * s, 0.072f - 0.072f * s, 0, 0,
                    0.213f - 0.213f * s, 0.715f - 0.715f * s, 0.072f + 0.928f * s, 0, 0,
                    0, 0, 0, 1, 0,
                ];
            }
            case "huerotate":
            {
                var deg = 0f;
                if (SvgRasterizer.Attr(prim, "values") is { } hv) float.TryParse(hv.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out deg);
                var c = (float)Math.Cos(deg * Math.PI / 180.0);
                var s = (float)Math.Sin(deg * Math.PI / 180.0);
                return
                [
                    0.213f + c * 0.787f - s * 0.213f, 0.715f - c * 0.715f - s * 0.715f, 0.072f - c * 0.072f + s * 0.928f, 0, 0,
                    0.213f - c * 0.213f + s * 0.143f, 0.715f + c * 0.285f + s * 0.140f, 0.072f - c * 0.072f - s * 0.283f, 0, 0,
                    0.213f - c * 0.213f - s * 0.787f, 0.715f - c * 0.715f + s * 0.715f, 0.072f + c * 0.928f + s * 0.072f, 0, 0,
                    0, 0, 0, 1, 0,
                ];
            }
            case "luminancetoalpha":
                return
                [
                    0, 0, 0, 0, 0,
                    0, 0, 0, 0, 0,
                    0, 0, 0, 0, 0,
                    0.2125f, 0.7154f, 0.0721f, 0, 0,
                ];
            default:
                return null;
        }
    }
}
