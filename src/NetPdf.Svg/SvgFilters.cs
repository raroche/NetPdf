// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using SkiaSharp;

namespace NetPdf.Svg;

/// <summary>SVG <c>filter</c> support (§15) for the raster renderer — resolves a <c>filter="url(#id)"</c>
/// reference and composes a <c>&lt;filter&gt;</c>'s primitive children into a single Skia
/// <see cref="SKImageFilter"/> applied to the element subtree. A filter GRAPH is modeled (SVG part 7): each
/// primitive resolves its <c>in</c>/<c>in2</c> (the previous result / <c>SourceGraphic</c> / <c>SourceAlpha</c>
/// / a named <c>result</c>) and its output is stored under <c>result</c>. Extracted from
/// <see cref="SvgRasterizer"/> (PR-245 refactor — keep feature areas as small internal collaborators).</summary>
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
    /// <see cref="SKImageFilter"/> applied to the element's rendering. Modeled as a filter GRAPH: each
    /// primitive resolves its <c>in</c>/<c>in2</c> (the previous primitive's result — <c>SourceGraphic</c>
    /// for the first — or an explicit <c>SourceGraphic</c>/<c>SourceAlpha</c>/named <c>result</c>) and stores
    /// its output under <c>result</c>. Only the PRIMARY tree — the primitives reachable backward from the last
    /// primitive through their input references — is evaluated; disconnected primitive trees are ignored (they
    /// neither build a filter nor flag unsupported, PR-246 review [P2]). Supports <c>feGaussianBlur</c>,
    /// <c>feOffset</c>, <c>feDropShadow</c>, <c>feColorMatrix</c>, <c>feFlood</c>, <c>feMerge</c>,
    /// <c>feComposite</c> (over/in/out/atop/xor/lighter/arithmetic), and <c>feBlend</c>. Primitive SUBREGIONS
    /// (x/y/width/height), the EXPLICIT filter region / <c>*Units</c>, <c>BackgroundImage</c>/<c>FillPaint</c>
    /// inputs, and other primitives aren't modeled → flagged. The DEFAULT filter region is applied by the
    /// caller as a clip (see <see cref="DefaultFilterRegion"/>). Returns <see langword="null"/> when no
    /// primitive contributes (the element renders unfiltered).</summary>
    public static SKImageFilter? BuildImageFilter(XElement filter, SvgRenderState state)
    {
        // The filter region (x/y/width/height) and *Units change the result geometry but aren't applied here.
        var sawUnsupported = SvgRasterizer.HasAnyAttr(filter, "x", "y", "width", "height", "filterUnits", "primitiveUnits");

        var prims = new List<XElement>();
        foreach (var e in filter.Elements()) prims.Add(e);
        if (prims.Count == 0)
        {
            if (sawUnsupported) state.SawUnsupported = true;
            return null;
        }

        // Evaluate only the primary tree (reachable backward from the last primitive). Skipped primitives in a
        // disconnected tree contribute nothing and must not flag — only the primary tree is the filter result.
        var reachable = ComputeReachableTree(prims);
        var results = new Dictionary<string, SKImageFilter?>(StringComparer.Ordinal);
        SKImageFilter? last = null; // null = SourceGraphic; tracks the previous primitive's output

        for (var idx = 0; idx < prims.Count; idx++)
        {
            if (!reachable[idx]) continue;
            var prim = prims[idx];
            if (SvgRasterizer.HasAnyAttr(prim, "x", "y", "width", "height")) sawUnsupported = true; // subregion not modeled

            SKImageFilter? output;
            switch (prim.Name.LocalName.ToLowerInvariant())
            {
                case "fegaussianblur":
                {
                    var input = ResolveInput(prim, "in", last, results, ref sawUnsupported);
                    var (sx, sy) = ParseStdDeviation(SvgRasterizer.Attr(prim, "stdDeviation"));
                    output = sx > 0 || sy > 0 ? SKImageFilter.CreateBlur(sx, sy, input) : input;
                    break;
                }
                case "feoffset":
                    output = SKImageFilter.CreateOffset((float)SvgRasterizer.Num(prim, "dx"), (float)SvgRasterizer.Num(prim, "dy"),
                        ResolveInput(prim, "in", last, results, ref sawUnsupported));
                    break;
                case "fedropshadow":
                {
                    // A self-contained drop shadow: blur + offset + flood, with the source drawn on top.
                    var input = ResolveInput(prim, "in", last, results, ref sawUnsupported);
                    var (bx, by) = ParseStdDeviation(SvgRasterizer.Attr(prim, "stdDeviation"));
                    if (bx == 0 && prim.Attribute("stdDeviation") is null) { bx = 2; by = 2; } // default σ=2
                    var dx = prim.Attribute("dx") is not null ? (float)SvgRasterizer.Num(prim, "dx") : 2f;
                    var dy = prim.Attribute("dy") is not null ? (float)SvgRasterizer.Num(prim, "dy") : 2f;
                    output = SKImageFilter.CreateDropShadow(dx, dy, bx, by, FloodColor(prim), input);
                    break;
                }
                case "fecolormatrix":
                {
                    var input = ResolveInput(prim, "in", last, results, ref sawUnsupported);
                    var matrix = BuildColorMatrix(prim);
                    if (matrix is not null)
                    {
                        using var cf = SKColorFilter.CreateColorMatrix(matrix);
                        output = SKImageFilter.CreateColorFilter(cf, input);
                    }
                    else output = input;
                    break;
                }
                case "feflood":
                {
                    using var shader = SKShader.CreateColor(FloodColor(prim));
                    output = SKImageFilter.CreateShader(shader);
                    break;
                }
                case "femerge":
                    output = BuildMerge(prim, last, results, ref sawUnsupported);
                    break;
                case "fecomposite":
                    output = BuildComposite(prim, last, results, ref sawUnsupported);
                    break;
                case "feblend":
                {
                    var fg = ResolveInput(prim, "in", last, results, ref sawUnsupported);
                    var bg = ResolveInput(prim, "in2", last, results, ref sawUnsupported);
                    output = SKImageFilter.CreateBlendMode(BlendMode(SvgRasterizer.Attr(prim, "mode")), bg, fg);
                    break;
                }
                default:
                    sawUnsupported = true; // unsupported primitive
                    output = last;         // pass the previous result through
                    break;
            }

            last = output;
            if (SvgRasterizer.Attr(prim, "result") is { } r && !string.IsNullOrWhiteSpace(r)) results[r.Trim()] = output;
        }

        if (sawUnsupported) state.SawUnsupported = true;
        return last;
    }

    /// <summary>The DEFAULT SVG filter region (§15.7.4) in the filtered element's OWN coordinate space: the
    /// element geometry bounding box inflated 10% on each side (<c>filterUnits=objectBoundingBox</c>,
    /// x/y=-10%, width/height=120%). The caller hard-clips the composited filter result to this rect so an
    /// otherwise unbounded primitive (a final <c>feFlood</c>) — and a blur halo — can't paint past the region
    /// (PR-246 review [P1]). Returns <see langword="null"/> when no geometry bbox is available (text / image /
    /// empty subtree); the caller then leaves the result uncropped. An EXPLICIT filter region (x/y/width/height
    /// on the <c>&lt;filter&gt;</c>) is not modeled and is flagged in <see cref="BuildImageFilter"/>.</summary>
    public static SKRect? DefaultFilterRegion(XElement el, SvgStyle style, SvgRenderState state)
    {
        if (SvgClipMask.ComputeBBox(el, style, state, depth: 0, SKMatrix.Identity, isRoot: true) is { Width: > 0, Height: > 0 } b)
        {
            var dx = b.Width * 0.1f;
            var dy = b.Height * 0.1f;
            return new SKRect(b.Left - dx, b.Top - dy, b.Right + dx, b.Bottom + dy);
        }
        return null;
    }

    /// <summary>Mark the filter primitives reachable backward from the LAST primitive through their input
    /// references (<c>in</c>/<c>in2</c>/<c>feMergeNode in</c>), so only the PRIMARY tree (§15) is evaluated.
    /// An absent input depends on the previous primitive; a dangling / forward custom <c>result</c> name is
    /// treated as unspecified → the previous primitive (Filter Effects §9.2); a standard source
    /// (<c>SourceGraphic</c>/…) and a generator (<c>feFlood</c>/<c>feImage</c>) are terminal (no edge).</summary>
    private static bool[] ComputeReachableTree(List<XElement> prims)
    {
        var reachable = new bool[prims.Count];
        var stack = new Stack<int>();
        reachable[prims.Count - 1] = true;
        stack.Push(prims.Count - 1);
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            void Visit(int src) { if (src >= 0 && !reachable[src]) { reachable[src] = true; stack.Push(src); } }
            var prim = prims[i];
            switch (prim.Name.LocalName.ToLowerInvariant())
            {
                case "feflood":
                case "feimage":
                    break; // generators take no input
                case "femerge":
                {
                    var any = false;
                    foreach (var node in prim.Elements())
                        if (node.Name.LocalName.Equals("feMergeNode", StringComparison.OrdinalIgnoreCase))
                        { any = true; Visit(SourceIndex(prims, i, SvgRasterizer.Attr(node, "in"))); }
                    if (!any) Visit(i - 1); // empty merge falls back to the previous result
                    break;
                }
                case "fecomposite":
                case "feblend":
                    Visit(SourceIndex(prims, i, SvgRasterizer.Attr(prim, "in")));
                    Visit(SourceIndex(prims, i, SvgRasterizer.Attr(prim, "in2")));
                    break;
                default: // single-input primitives + unknown primitives (which pass the previous result through)
                    Visit(SourceIndex(prims, i, SvgRasterizer.Attr(prim, "in")));
                    break;
            }
        }
        return reachable;
    }

    /// <summary>Resolve a primitive input reference to its source primitive index: the nearest PRECEDING
    /// primitive whose <c>result</c> matches a custom name; -1 for a standard source
    /// (<c>SourceGraphic</c>/<c>SourceAlpha</c>/<c>BackgroundImage</c>/<c>BackgroundAlpha</c>/<c>FillPaint</c>/
    /// <c>StrokePaint</c>); the previous primitive (<c>i-1</c>) for an absent or dangling/forward reference
    /// (treated as unspecified — Filter Effects §9.2).</summary>
    private static int SourceIndex(List<XElement> prims, int i, string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return i - 1;        // absent → previous (i-1 = -1 → SourceGraphic for first)
        if (IsStandardInput(name)) return -1;                // a terminal standard source, not a primitive
        for (var j = i - 1; j >= 0; j--)
            if (SvgRasterizer.Attr(prims[j], "result")?.Trim() == name) return j;
        return i - 1;                                        // dangling / forward custom name → unspecified → previous
    }

    /// <summary>A standard SVG filter input keyword (not a custom <c>result</c> name).</summary>
    private static bool IsStandardInput(string name) =>
        name.Equals("SourceGraphic", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SourceAlpha", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("BackgroundImage", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("BackgroundAlpha", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("FillPaint", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("StrokePaint", StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolve a primitive input attribute to a filter (<see langword="null"/> = <c>SourceGraphic</c>,
    /// i.e. the layer content). Absent → the previous primitive's result; <c>SourceGraphic</c> → null;
    /// <c>SourceAlpha</c> → an alpha-only filter; a named <c>result</c> → that result. A standard input we don't
    /// model (<c>BackgroundImage</c>/<c>BackgroundAlpha</c>/<c>FillPaint</c>/<c>StrokePaint</c>) flags; a
    /// dangling / forward custom <c>result</c> name is treated as unspecified (Filter Effects §9.2) → falls
    /// back to the previous result WITHOUT a diagnostic (PR-246 review [P2]).</summary>
    private static SKImageFilter? ResolveInput(XElement prim, string attr, SKImageFilter? last, Dictionary<string, SKImageFilter?> results, ref bool sawUnsupported)
    {
        var name = SvgRasterizer.Attr(prim, attr)?.Trim();
        if (string.IsNullOrEmpty(name)) return last;
        if (name.Equals("SourceGraphic", StringComparison.OrdinalIgnoreCase)) return null;
        if (name.Equals("SourceAlpha", StringComparison.OrdinalIgnoreCase)) return SourceAlpha();
        if (results.TryGetValue(name, out var r)) return r;
        if (IsStandardInput(name)) sawUnsupported = true; // BackgroundImage/BackgroundAlpha/FillPaint/StrokePaint
        return last;                                       // else a dangling / forward custom name → previous result
    }

    /// <summary>An <see cref="SKImageFilter"/> producing the ALPHA of <c>SourceGraphic</c> with black RGB
    /// (the SVG <c>SourceAlpha</c> input).</summary>
    private static SKImageFilter SourceAlpha()
    {
        using var cf = SKColorFilter.CreateColorMatrix(
        [
            0, 0, 0, 0, 0,
            0, 0, 0, 0, 0,
            0, 0, 0, 0, 0,
            0, 0, 0, 1, 0,
        ]);
        return SKImageFilter.CreateColorFilter(cf, null);
    }

    /// <summary>Stack a <c>feMerge</c>'s <c>feMergeNode</c> inputs bottom-to-top (the first node is the
    /// bottom layer, SVG §15).</summary>
    private static SKImageFilter? BuildMerge(XElement prim, SKImageFilter? last, Dictionary<string, SKImageFilter?> results, ref bool sawUnsupported)
    {
        var inputs = new List<SKImageFilter?>();
        foreach (var node in prim.Elements())
            if (node.Name.LocalName.Equals("feMergeNode", StringComparison.OrdinalIgnoreCase))
                inputs.Add(ResolveInput(node, "in", last, results, ref sawUnsupported));
        if (inputs.Count == 0) return last;
        var arr = new SKImageFilter[inputs.Count]; // null entries (= SourceGraphic) are valid for Skia
        for (var i = 0; i < inputs.Count; i++) arr[i] = inputs[i]!;
        return SKImageFilter.CreateMerge(arr);
    }

    /// <summary>Porter-Duff / arithmetic compositing of <c>in</c> (source) over <c>in2</c> (destination),
    /// SVG §15 <c>feComposite</c>.</summary>
    private static SKImageFilter? BuildComposite(XElement prim, SKImageFilter? last, Dictionary<string, SKImageFilter?> results, ref bool sawUnsupported)
    {
        var fg = ResolveInput(prim, "in", last, results, ref sawUnsupported);
        var bg = ResolveInput(prim, "in2", last, results, ref sawUnsupported);
        var op = (SvgRasterizer.Attr(prim, "operator") ?? "over").Trim().ToLowerInvariant();
        if (op == "arithmetic")
        {
            float K(string n) => (float)SvgRasterizer.Num(prim, n);
            return SKImageFilter.CreateArithmetic(K("k1"), K("k2"), K("k3"), K("k4"), enforcePMColor: true, background: bg, foreground: fg);
        }
        SKBlendMode mode;
        switch (op)
        {
            case "over": mode = SKBlendMode.SrcOver; break;
            case "in": mode = SKBlendMode.SrcIn; break;
            case "out": mode = SKBlendMode.SrcOut; break;
            case "atop": mode = SKBlendMode.SrcATop; break;
            case "xor": mode = SKBlendMode.Xor; break;
            case "lighter": mode = SKBlendMode.Plus; break;                  // additive (Filter Effects §9.8)
            default: mode = SKBlendMode.SrcOver; sawUnsupported = true; break; // an unknown operator → flag
        }
        return SKImageFilter.CreateBlendMode(mode, bg, fg);
    }

    /// <summary>Map a <c>feBlend</c> <c>mode</c> to a Skia blend mode (unknown → normal).</summary>
    private static SKBlendMode BlendMode(string? mode) => (mode?.Trim().ToLowerInvariant()) switch
    {
        "multiply" => SKBlendMode.Multiply,
        "screen" => SKBlendMode.Screen,
        "darken" => SKBlendMode.Darken,
        "lighten" => SKBlendMode.Lighten,
        "overlay" => SKBlendMode.Overlay,
        "color-dodge" => SKBlendMode.ColorDodge,
        "color-burn" => SKBlendMode.ColorBurn,
        "hard-light" => SKBlendMode.HardLight,
        "soft-light" => SKBlendMode.SoftLight,
        "difference" => SKBlendMode.Difference,
        "exclusion" => SKBlendMode.Exclusion,
        "hue" => SKBlendMode.Hue,
        "saturation" => SKBlendMode.Saturation,
        "color" => SKBlendMode.Color,
        "luminosity" => SKBlendMode.Luminosity,
        _ => SKBlendMode.SrcOver, // "normal"
    };

    /// <summary>The <c>flood-color</c> × <c>flood-opacity</c> for <c>feFlood</c> / <c>feDropShadow</c>
    /// (default black, fully opaque).</summary>
    private static SKColor FloodColor(XElement prim)
    {
        var color = SKColors.Black;
        if (SvgAttr.Presentation(prim, "flood-color") is { } fc) SvgColor.TryParse(fc, out color);
        var floodOpacity = ParseFloodOpacity(SvgAttr.Presentation(prim, "flood-opacity"));
        return color.WithAlpha((byte)Math.Clamp((int)Math.Round(color.Alpha / 255f * floodOpacity * 255f), 0, 255));
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
