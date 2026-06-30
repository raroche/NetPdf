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
    /// <c>feComposite</c> (over/in/out/atop/xor/lighter/arithmetic), <c>feBlend</c>, (SVG part 8)
    /// <c>feMorphology</c> (erode/dilate), <c>feComponentTransfer</c> (identity/table/discrete/linear/gamma),
    /// <c>feDisplacementMap</c>, <c>feConvolveMatrix</c>, <c>feTurbulence</c> (turbulence/fractalNoise), and
    /// (SVG part 9) <c>feDiffuseLighting</c> / <c>feSpecularLighting</c> (distant/point/spot lights),
    /// <c>feImage</c> (a raster href), and <c>feTile</c>. The filter region (x/y/width/height + filterUnits) is
    /// honored by the caller's clip (see <see cref="ResolveFilterRegion"/>); primitive SUBREGIONS (per-primitive
    /// x/y/width/height), <c>primitiveUnits</c>, <c>BackgroundImage</c>/<c>FillPaint</c> inputs, and a
    /// <c>feImage</c> ELEMENT reference (<c>href="#id"</c>) aren't modeled → flagged. Returns
    /// <see langword="null"/> when no primitive contributes (the element renders unfiltered).</summary>
    public static SKImageFilter? BuildImageFilter(XElement filter, SvgRenderState state)
    {
        // The filter region (x/y/width/height + filterUnits) is now honored by the caller's clip
        // (ResolveFilterRegion). primitiveUnits (objectBoundingBox primitive coordinates) is still not modeled.
        var sawUnsupported = SvgRasterizer.HasAnyAttr(filter, "primitiveUnits");

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
                case "femorphology":
                {
                    var input = ResolveInput(prim, "in", last, results, ref sawUnsupported);
                    var (rx, ry) = ParseRadius(SvgRasterizer.Attr(prim, "radius"));
                    var op = (SvgRasterizer.Attr(prim, "operator") ?? "erode").Trim().ToLowerInvariant();
                    if (op != "erode" && op != "dilate") sawUnsupported = true; // unknown operator (default to erode)
                    // §9.6 — a radius of 0 (or negative, or absent) on EITHER axis disables the primitive.
                    // A positive FRACTIONAL radius is rounded to Skia's integer morphology radius (so a
                    // value < 0.5 rounds to 0 → no visible effect — a documented approximation).
                    var irx = (int)Math.Round(rx);
                    var iry = (int)Math.Round(ry);
                    output = rx <= 0 || ry <= 0 || (irx <= 0 && iry <= 0)
                        ? input
                        : op == "dilate"
                            ? SKImageFilter.CreateDilate(irx, iry, input)
                            : SKImageFilter.CreateErode(irx, iry, input);
                    break;
                }
                case "fecomponenttransfer":
                {
                    var input = ResolveInput(prim, "in", last, results, ref sawUnsupported);
                    var cf = BuildComponentTransfer(prim);
                    output = cf is not null ? SKImageFilter.CreateColorFilter(cf, input) : input;
                    cf?.Dispose();
                    break;
                }
                case "fedisplacementmap":
                {
                    var input = ResolveInput(prim, "in", last, results, ref sawUnsupported);
                    var disp = ResolveInput(prim, "in2", last, results, ref sawUnsupported)
                        ?? SKImageFilter.CreateOffset(0, 0, null); // null in2 = SourceGraphic → an identity filter
                    var scale = (float)SvgRasterizer.Num(prim, "scale");
                    var xs = ChannelSelector(SvgRasterizer.Attr(prim, "xChannelSelector"));
                    var ys = ChannelSelector(SvgRasterizer.Attr(prim, "yChannelSelector"));
                    output = SKImageFilter.CreateDisplacementMapEffect(xs, ys, scale, disp, input);
                    break;
                }
                case "feconvolvematrix":
                {
                    var input = ResolveInput(prim, "in", last, results, ref sawUnsupported);
                    output = BuildConvolveMatrix(prim, input, ref sawUnsupported);
                    break;
                }
                case "feturbulence":
                {
                    // A GENERATOR — it must never pass the previous content through (PR-248 review [P2]). A
                    // degenerate (≤ 0) baseFrequency we can't faithfully produce is flagged + an EMPTY
                    // (transparent) result, not a silent SourceGraphic pass-through.
                    var shader = BuildTurbulence(prim);
                    if (shader is null) { sawUnsupported = true; shader = SKShader.CreateColor(SKColors.Transparent); }
                    output = SKImageFilter.CreateShader(shader);
                    shader.Dispose();
                    break;
                }
                case "feimage":
                {
                    // A GENERATOR — a data: raster href decodes (through the image-safety validator) to an
                    // image filter. An external href or an ELEMENT reference (href="#id") isn't modeled →
                    // flagged + an empty (transparent) result, never a content pass-through.
                    var href = SvgAttr.HrefRaw(prim);
                    if (href is not null && href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                        && SvgRasterizer.DecodeDataUriImage(href) is { } image)
                    {
                        // The filter references the image (kept alive by the composed chain, like the other
                        // intermediate filters here); do not dispose it out from under the filter.
                        output = SKImageFilter.CreateImage(image);
                    }
                    else
                    {
                        sawUnsupported = true;
                        using var transparent = SKShader.CreateColor(SKColors.Transparent);
                        output = SKImageFilter.CreateShader(transparent);
                    }
                    break;
                }
                case "fediffuselighting":
                {
                    var input = ResolveInput(prim, "in", last, results, ref sawUnsupported);
                    output = BuildLighting(prim, input, specular: false, ref sawUnsupported);
                    break;
                }
                case "fespecularlighting":
                {
                    var input = ResolveInput(prim, "in", last, results, ref sawUnsupported);
                    output = BuildLighting(prim, input, specular: true, ref sawUnsupported);
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

    /// <summary>The SVG filter region (§15.7.4) in the filtered element's OWN coordinate space — the rect the
    /// caller hard-clips the composited filter result to (so an unbounded primitive / blur halo can't paint
    /// past it, PR-246 review [P1]). Honors an EXPLICIT <c>x</c>/<c>y</c>/<c>width</c>/<c>height</c> +
    /// <c>filterUnits</c> on the <c>&lt;filter&gt;</c>: <c>objectBoundingBox</c> (default) maps each value as a
    /// FRACTION of the element bbox (a <c>%</c> → /100; the §15 default is <c>-10% -10% 120% 120%</c>);
    /// <c>userSpaceOnUse</c> maps them as user-space lengths (all four required, else the bbox default).
    /// Returns <see langword="null"/> when no geometry bbox is available (text / image / empty subtree) and no
    /// explicit userSpace rect is given; the caller then leaves the result uncropped.</summary>
    public static SKRect? ResolveFilterRegion(XElement filter, XElement el, SvgStyle style, SvgRenderState state)
    {
        var userSpace = (SvgRasterizer.Attr(filter, "filterUnits") ?? "objectBoundingBox").Trim()
            .Equals("userSpaceOnUse", StringComparison.OrdinalIgnoreCase);
        if (userSpace
            && SvgRasterizer.HasAnyAttr(filter, "x") && SvgRasterizer.HasAnyAttr(filter, "y")
            && SvgRasterizer.HasAnyAttr(filter, "width") && SvgRasterizer.HasAnyAttr(filter, "height"))
        {
            var x = (float)SvgRasterizer.Len(filter, "x", state, style, SvgRasterizer.LenAxis.X);
            var y = (float)SvgRasterizer.Len(filter, "y", state, style, SvgRasterizer.LenAxis.Y);
            var w = (float)SvgRasterizer.Len(filter, "width", state, style, SvgRasterizer.LenAxis.X);
            var h = (float)SvgRasterizer.Len(filter, "height", state, style, SvgRasterizer.LenAxis.Y);
            if (w > 0 && h > 0) return new SKRect(x, y, x + w, y + h);
        }
        if (SvgClipMask.ComputeBBox(el, style, state, depth: 0, SKMatrix.Identity, isRoot: true) is { Width: > 0, Height: > 0 } b)
        {
            var fx = RegionFraction(filter, "x", -0.1f);
            var fy = RegionFraction(filter, "y", -0.1f);
            var fw = RegionFraction(filter, "width", 1.2f);
            var fh = RegionFraction(filter, "height", 1.2f);
            if (fw <= 0 || fh <= 0) return null;
            return new SKRect(b.Left + fx * b.Width, b.Top + fy * b.Height,
                b.Left + (fx + fw) * b.Width, b.Top + (fy + fh) * b.Height);
        }
        return null;
    }

    /// <summary>An <c>objectBoundingBox</c> filter-region value as a bbox FRACTION: a <c>%</c> → value/100, a
    /// plain number → as-is, absent → <paramref name="fallback"/> (the §15 default).</summary>
    private static float RegionFraction(XElement filter, string name, float fallback)
    {
        var raw = SvgRasterizer.Attr(filter, name)?.Trim();
        if (string.IsNullOrEmpty(raw)) return fallback;
        if (raw.EndsWith("%", StringComparison.Ordinal))
            return float.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) ? pct / 100f : fallback;
        return float.TryParse(SvgRasterizer.TrimUnit(raw), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
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
                case "feturbulence":
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
                case "fedisplacementmap":
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

    /// <summary>Split a whitespace/comma-separated number list (filter primitive attributes:
    /// <c>radius</c> / <c>order</c> / <c>kernelMatrix</c> / <c>tableValues</c> / <c>baseFrequency</c>);
    /// a non-numeric token truncates the list.</summary>
    private static float[] SplitNumbers(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var t = raw.Split(new[] { ' ', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var r = new float[t.Length];
        var n = 0;
        foreach (var s in t)
        {
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) break;
            r[n++] = v;
        }
        return n == t.Length ? r : r[..n];
    }

    /// <summary>STRICT split: every token must be a finite number, else <see langword="null"/> (so trailing
    /// junk can't be silently ignored — PR-248 review [P3]). Used for <c>order</c> / <c>kernelMatrix</c> /
    /// <c>tableValues</c> / <c>baseFrequency</c>, where a malformed value should flag, not partial-parse.
    /// <paramref name="max"/> bounds the count (a hostile huge list returns null).</summary>
    private static float[]? SplitNumbersStrict(string? raw, int max = 1024)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Split(new[] { ' ', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        if (t.Length == 0 || t.Length > max) return null;
        var r = new float[t.Length];
        for (var i = 0; i < t.Length; i++)
            if (!float.TryParse(t[i], NumberStyles.Float, CultureInfo.InvariantCulture, out r[i]) || !float.IsFinite(r[i]))
                return null;
        return r;
    }

    /// <summary><c>feMorphology radius</c> — one value (isotropic) or two (x then y); a non-positive radius
    /// disables the effect (§9.6 → 0).</summary>
    private static (float X, float Y) ParseRadius(string? raw)
    {
        var t = SplitNumbers(raw);
        float P(int i) => i < t.Length && t[i] > 0 ? t[i] : 0;
        var x = P(0);
        return (x, t.Length > 1 ? P(1) : x);
    }

    /// <summary>The <c>feDisplacementMap</c> channel selector (<c>R</c>/<c>G</c>/<c>B</c>/<c>A</c>, default
    /// <c>A</c>).</summary>
    private static SKColorChannel ChannelSelector(string? raw) => (raw?.Trim().ToUpperInvariant()) switch
    {
        "R" => SKColorChannel.R,
        "G" => SKColorChannel.G,
        "B" => SKColorChannel.B,
        _ => SKColorChannel.A,
    };

    /// <summary><c>feComponentTransfer</c> → a per-channel 256-entry lookup color filter built from the
    /// <c>feFuncR/G/B/A</c> children (Filter Effects §9.13). Returns <see langword="null"/> when every
    /// channel is identity (a no-op).</summary>
    private static SKColorFilter? BuildComponentTransfer(XElement prim)
    {
        byte[]? r = null, g = null, b = null, a = null;
        foreach (var fn in prim.Elements())
            switch (fn.Name.LocalName.ToLowerInvariant())
            {
                case "fefuncr": r = ComponentTable(fn); break;
                case "fefuncg": g = ComponentTable(fn); break;
                case "fefuncb": b = ComponentTable(fn); break;
                case "fefunca": a = ComponentTable(fn); break;
            }
        if (r is null && g is null && b is null && a is null) return null;
        return SKColorFilter.CreateTable(a ?? IdentityTable(), r ?? IdentityTable(), g ?? IdentityTable(), b ?? IdentityTable());
    }

    private static byte[] IdentityTable()
    {
        var t = new byte[256];
        for (var i = 0; i < 256; i++) t[i] = (byte)i;
        return t;
    }

    /// <summary>Evaluate one <c>feFuncX</c> transfer function (identity / table / discrete / linear /
    /// gamma) into a 256-entry [0,255] lookup table, or <see langword="null"/> for identity.</summary>
    private static byte[]? ComponentTable(XElement fn)
    {
        var type = (SvgRasterizer.Attr(fn, "type") ?? "identity").Trim().ToLowerInvariant();
        var t = new byte[256];
        switch (type)
        {
            case "table":
            case "discrete":
            {
                var v = SplitNumbersStrict(SvgRasterizer.Attr(fn, "tableValues"), max: 256);
                if (v is null) return null; // empty / malformed tableValues → identity (no transform)
                if (v.Length == 1) { var only = To255(Clamp01(v[0])); Array.Fill(t, only); return t; }
                for (var i = 0; i < 256; i++)
                {
                    var c = i / 255.0;
                    double o;
                    if (type == "table")
                    {
                        var n = v.Length - 1;
                        var k = Math.Min((int)(c * n), n - 1);
                        o = v[k] + (c * n - k) * (v[k + 1] - v[k]);
                    }
                    else // discrete
                    {
                        var n = v.Length;
                        var k = Math.Min((int)(c * n), n - 1);
                        o = v[k];
                    }
                    t[i] = To255(Clamp01(o));
                }
                return t;
            }
            case "linear":
            {
                var slope = ReadFloat(fn, "slope", 1f);
                var intercept = ReadFloat(fn, "intercept", 0f);
                for (var i = 0; i < 256; i++) t[i] = To255(Clamp01(slope * (i / 255.0) + intercept));
                return t;
            }
            case "gamma":
            {
                var amplitude = ReadFloat(fn, "amplitude", 1f);
                var exponent = ReadFloat(fn, "exponent", 1f);
                var offset = ReadFloat(fn, "offset", 0f);
                for (var i = 0; i < 256; i++) t[i] = To255(Clamp01(amplitude * Math.Pow(i / 255.0, exponent) + offset));
                return t;
            }
            default: // identity / unknown
                return null;
        }
    }

    /// <summary>Max <c>feConvolveMatrix</c> kernel side / cell count — a hostile SVG can give a huge
    /// <c>order</c> (or one that overflows an int product) and an empty kernel; bound it BEFORE the native
    /// Skia call (PR-248 review [P1]). A real convolution kernel is tiny (≤ 7×7).</summary>
    private const int MaxConvolveOrder = 100;
    private const long MaxConvolveCells = 1024;

    /// <summary><c>feConvolveMatrix</c> → a Skia matrix convolution (Filter Effects §9.5). <c>order</c> is
    /// the kernel size (an integer-optional-integer, bounded — a fractional / non-positive / oversize /
    /// overflowing order is rejected); <c>kernelMatrix</c> the row-major kernel, length == order_x · order_y
    /// (SVG applies it rotated 180° vs the raw sum, so it's reversed for Skia); gain = 1/<c>divisor</c>
    /// (default = the kernel sum, else 1); plus <c>bias</c>, the <c>targetX</c>/<c>targetY</c> origin,
    /// <c>edgeMode</c> (duplicate→clamp / wrap→repeat / none→decal), and <c>preserveAlpha</c>. An invalid
    /// order / kernel passes the input through + flags.</summary>
    private static SKImageFilter? BuildConvolveMatrix(XElement prim, SKImageFilter? input, ref bool sawUnsupported)
    {
        // order = the kernel dimensions. Absent → 3×3. Present → exactly 1 or 2 POSITIVE INTEGERS in range;
        // anything else (fractional, negative, oversize, or non-numeric) flags. The cell count is computed in
        // `long` and capped, so a value like `65536 65536` can't overflow the int product to 0 and slip an
        // empty kernel past the length check into native Skia.
        var orderRaw = SvgRasterizer.Attr(prim, "order");
        int ox, oy;
        if (string.IsNullOrWhiteSpace(orderRaw)) { ox = oy = 3; }
        else
        {
            var order = SplitNumbersStrict(orderRaw, max: 2);
            if (order is null || !WholeOrder(order[0], out ox) || !WholeOrder(order.Length > 1 ? order[1] : order[0], out oy))
            {
                sawUnsupported = true;
                return input;
            }
        }
        var cells = (long)ox * oy;
        var kernel = SplitNumbersStrict(SvgRasterizer.Attr(prim, "kernelMatrix"), max: (int)MaxConvolveCells);
        if (cells > MaxConvolveCells || kernel is null || kernel.Length != cells)
        {
            sawUnsupported = true;
            return input;
        }
        var k = new float[kernel.Length]; // SVG kernel is rotated 180° relative to a direct convolution.
        for (var i = 0; i < kernel.Length; i++) k[i] = kernel[kernel.Length - 1 - i];
        var sum = 0f;
        foreach (var kv in kernel) sum += kv;
        var divisor = ReadFloat(prim, "divisor", sum != 0 ? sum : 1f);
        if (divisor == 0) divisor = 1f;
        var bias = (float)SvgRasterizer.Num(prim, "bias");
        var tx = SvgRasterizer.Attr(prim, "targetX") is { } txa && int.TryParse(txa, out var txi) ? txi : ox / 2;
        var ty = SvgRasterizer.Attr(prim, "targetY") is { } tya && int.TryParse(tya, out var tyi) ? tyi : oy / 2;
        var offX = Math.Clamp(ox - 1 - tx, 0, ox - 1); // mirror the target to match the reversed kernel
        var offY = Math.Clamp(oy - 1 - ty, 0, oy - 1);
        var tile = (SvgRasterizer.Attr(prim, "edgeMode") ?? "duplicate").Trim().ToLowerInvariant() switch
        {
            "wrap" => SKShaderTileMode.Repeat,
            "none" => SKShaderTileMode.Decal,
            _ => SKShaderTileMode.Clamp,
        };
        var preserveAlpha = (SvgRasterizer.Attr(prim, "preserveAlpha") ?? "false").Trim()
            .Equals("true", StringComparison.OrdinalIgnoreCase);
        return SKImageFilter.CreateMatrixConvolution(
            new SKSizeI(ox, oy), k, 1f / divisor, bias, new SKPointI(offX, offY), tile, convolveAlpha: !preserveAlpha, input);
    }

    /// <summary>A valid <c>feConvolveMatrix</c> <c>order</c> component: a POSITIVE WHOLE number ≤
    /// <see cref="MaxConvolveOrder"/>.</summary>
    private static bool WholeOrder(float f, out int v)
    {
        v = 0;
        if (!(f >= 1) || f != MathF.Floor(f) || f > MaxConvolveOrder) return false;
        v = (int)f;
        return true;
    }

    /// <summary><c>feTurbulence</c> → a Perlin-noise shader (Filter Effects §9.21): <c>type</c> =
    /// fractalNoise / turbulence; <c>baseFrequency</c> (one or two ≥ 0), <c>numOctaves</c>, <c>seed</c>.
    /// Returns <see langword="null"/> for a degenerate (≤ 0 on both axes, omitted, or malformed) frequency —
    /// the caller flags it + emits an EMPTY result rather than passing content through. <c>stitchTiles</c>
    /// and the exact SVG noise sums differ slightly from Skia's generator — a first cut.</summary>
    private static SKShader? BuildTurbulence(XElement prim)
    {
        var freq = SplitNumbersStrict(SvgRasterizer.Attr(prim, "baseFrequency"), max: 2);
        var fx = freq is { Length: > 0 } ? Math.Max(0f, freq[0]) : 0f;
        var fy = freq is { Length: > 1 } ? Math.Max(0f, freq[1]) : fx;
        if (fx <= 0 && fy <= 0) return null; // degenerate / omitted / malformed → no noise (caller flags)
        var octaves = SvgRasterizer.Attr(prim, "numOctaves") is { } no && int.TryParse(no, out var n) ? Math.Max(1, n) : 1;
        var seed = (float)SvgRasterizer.Num(prim, "seed");
        var type = (SvgRasterizer.Attr(prim, "type") ?? "turbulence").Trim().ToLowerInvariant();
        return type == "fractalnoise"
            ? SKShader.CreatePerlinNoiseFractalNoise(fx, fy, octaves, seed)
            : SKShader.CreatePerlinNoiseTurbulence(fx, fy, octaves, seed);
    }

    /// <summary><c>feDiffuseLighting</c> / <c>feSpecularLighting</c> (Filter Effects §9.16/§9.17) → a Skia
    /// lighting image filter. The input ALPHA is the height field; a single light-source child
    /// (<c>feDistantLight</c> / <c>fePointLight</c> / <c>feSpotLight</c>) lights it with the
    /// <c>lighting-color</c>, <c>surfaceScale</c>, and <c>diffuseConstant</c> / (<c>specularConstant</c> +
    /// <c>specularExponent</c>). A missing / unknown light source flags + passes the input through.</summary>
    private static SKImageFilter? BuildLighting(XElement prim, SKImageFilter? input, bool specular, ref bool sawUnsupported)
    {
        var surfaceScale = ReadFloat(prim, "surfaceScale", 1f);
        var color = LightingColor(prim);
        XElement? light = null;
        foreach (var c in prim.Elements())
            if (c.Name.LocalName.ToLowerInvariant() is "fedistantlight" or "fepointlight" or "fespotlight") { light = c; break; }
        if (light is null) { sawUnsupported = true; return input; }

        if (specular)
        {
            var ks = ReadFloat(prim, "specularConstant", 1f);
            var shininess = ReadFloat(prim, "specularExponent", 1f);
            return light.Name.LocalName.ToLowerInvariant() switch
            {
                "fedistantlight" => SKImageFilter.CreateDistantLitSpecular(DistantDirection(light), color, surfaceScale, ks, shininess, input),
                "fepointlight" => SKImageFilter.CreatePointLitSpecular(PointLocation(light), color, surfaceScale, ks, shininess, input),
                "fespotlight" => SKImageFilter.CreateSpotLitSpecular(PointLocation(light), SpotTarget(light), ReadFloat(light, "specularExponent", 1f), SpotCutoff(light), color, surfaceScale, ks, shininess, input),
                _ => Flag(ref sawUnsupported, input),
            };
        }
        var kd = ReadFloat(prim, "diffuseConstant", 1f);
        return light.Name.LocalName.ToLowerInvariant() switch
        {
            "fedistantlight" => SKImageFilter.CreateDistantLitDiffuse(DistantDirection(light), color, surfaceScale, kd, input),
            "fepointlight" => SKImageFilter.CreatePointLitDiffuse(PointLocation(light), color, surfaceScale, kd, input),
            "fespotlight" => SKImageFilter.CreateSpotLitDiffuse(PointLocation(light), SpotTarget(light), ReadFloat(light, "specularExponent", 1f), SpotCutoff(light), color, surfaceScale, kd, input),
            _ => Flag(ref sawUnsupported, input),
        };
    }

    private static SKImageFilter? Flag(ref bool sawUnsupported, SKImageFilter? input) { sawUnsupported = true; return input; }

    /// <summary>The <c>lighting-color</c> presentation property (default white).</summary>
    private static SKColor LightingColor(XElement prim)
    {
        var color = SKColors.White;
        if (SvgAttr.Presentation(prim, "lighting-color") is { } lc) SvgColor.TryParse(lc, out color);
        return color;
    }

    /// <summary>An <c>feDistantLight</c> direction from <c>azimuth</c>/<c>elevation</c> (degrees): the unit
    /// vector (cos·az·cos·el, sin·az·cos·el, sin·el).</summary>
    private static SKPoint3 DistantDirection(XElement light)
    {
        var az = ReadFloat(light, "azimuth", 0f) * (float)(Math.PI / 180.0);
        var el = ReadFloat(light, "elevation", 0f) * (float)(Math.PI / 180.0);
        return new SKPoint3((float)(Math.Cos(az) * Math.Cos(el)), (float)(Math.Sin(az) * Math.Cos(el)), (float)Math.Sin(el));
    }

    private static SKPoint3 PointLocation(XElement light) =>
        new(ReadFloat(light, "x", 0f), ReadFloat(light, "y", 0f), ReadFloat(light, "z", 0f));

    private static SKPoint3 SpotTarget(XElement light) =>
        new(ReadFloat(light, "pointsAtX", 0f), ReadFloat(light, "pointsAtY", 0f), ReadFloat(light, "pointsAtZ", 0f));

    /// <summary>The <c>feSpotLight limitingConeAngle</c> in degrees (absent → 90° = effectively no cone
    /// restriction).</summary>
    private static float SpotCutoff(XElement light) => ReadFloat(light, "limitingConeAngle", 90f);

    private static float ReadFloat(XElement el, string attr, float fallback) =>
        SvgRasterizer.Attr(el, attr) is { } s && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : fallback;

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static byte To255(double v) => (byte)Math.Round(v * 255.0);

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
