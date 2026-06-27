// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using NetPdf.Pdf.Images;
using SkiaSharp;

namespace NetPdf.Svg;

/// <summary>Phase 4 SVG part 1 (PR 5) — a FIRST-CUT static SVG renderer: parse an SVG document, draw its
/// basic shapes (<c>rect</c> / <c>circle</c> / <c>ellipse</c> / <c>line</c> / <c>polyline</c> /
/// <c>polygon</c> / <c>path</c>) with fills, strokes, stroke-width, and element <c>transform</c>s onto a
/// Skia canvas via <see cref="SubtreeRasterizer"/>, and return it as a <see cref="RasterImageInfo"/> (an
/// RGBA raster the image pipeline embeds as an XObject + <c>/SMask</c>). This is a RASTER first cut; native
/// vector SVG → PDF operators, plus gradients / <c>&lt;text&gt;</c> / <c>&lt;use&gt;</c> / <c>&lt;defs&gt;</c>,
/// are later refinements. Group nesting (<c>&lt;g&gt;</c>) + inherited presentation attributes are honored
/// one level via attribute inheritance through the walk.</summary>
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

    // DoS guards (PR-230 review [P1]): a malicious SVG can nest thousands of <g> to drive a
    // StackOverflowException (which SubtreeRasterizer's try/catch CANNOT recover) or pile up elements to
    // burn CPU. Cap the recursion DEPTH (iterative would be safest but a hard depth cap is sufficient + far
    // below the stack limit), the total ELEMENT count, and the parsed CHARACTER count.
    private const int MaxDepth = 80;
    private const int MaxElements = 50_000;
    private const long MaxCharactersInDocument = 8L * 1024 * 1024; // 8M chars

    /// <summary>Parse + rasterize <paramref name="svgBytes"/>. Returns <see langword="null"/> on a parse
    /// failure or an over-cap document; <paramref name="sawUnsupported"/> is set when the SVG used a feature
    /// this cut doesn't render (text / image / use / defs / gradients / a paint-server fill, or content
    /// truncated by the depth / element budget) so the caller can diagnose it.</summary>
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

        var state = new RenderState();
        var raster = SubtreeRasterizer.Render(pxW, pxH, canvas =>
        {
            // Map the viewBox onto the raster (preserveAspectRatio xMidYMid meet — the default §8.2).
            if (vbW > 0 && vbH > 0)
            {
                var scale = Math.Min(pxW / vbW, pxH / vbH);
                var tx = (pxW - vbW * scale) / 2.0 - vbX * scale;
                var ty = (pxH - vbH * scale) / 2.0 - vbY * scale;
                canvas.Translate((float)tx, (float)ty);
                canvas.Scale((float)scale);
            }
            var initial = new SvgStyle(Fill: SKColors.Black, Stroke: null, StrokeWidth: 1, HasExplicitFill: false);
            foreach (var child in root.Elements())
                RenderElement(canvas, child, initial, state, depth: 1);
        });
        sawUnsupported = state.SawUnsupported;
        return raster;
    }

    private readonly record struct SvgStyle(SKColor Fill, SKColor? Stroke, float StrokeWidth, bool HasExplicitFill);

    private sealed class RenderState { public int Elements; public bool SawUnsupported; }

    private static void RenderElement(SKCanvas canvas, XElement el, SvgStyle inherited, RenderState state, int depth)
    {
        // DoS guards: stop at the depth / element budget (truncation is flagged unsupported).
        if (depth > MaxDepth || ++state.Elements > MaxElements) { state.SawUnsupported = true; return; }

        var style = ResolveStyle(el, inherited, state);
        var transform = ParseTransform(Attr(el, "transform"));
        var restore = canvas.Save();
        if (transform is { } m) canvas.Concat(in m);

        switch (el.Name.LocalName.ToLowerInvariant())
        {
            case "g":
            case "svg": // nested viewports approximated as a group this cut
                foreach (var child in el.Elements()) RenderElement(canvas, child, style, state, depth + 1);
                break;
            case "rect": DrawRect(canvas, el, style); break;
            case "circle": DrawCircle(canvas, el, style); break;
            case "ellipse": DrawEllipse(canvas, el, style); break;
            case "line": DrawLine(canvas, el, style); break;
            case "polyline": DrawPoly(canvas, el, style, close: false); break;
            case "polygon": DrawPoly(canvas, el, style, close: true); break;
            case "path": DrawPath(canvas, el, style); break;
            // Non-rendering metadata — ignore WITHOUT flagging (legitimately skippable).
            case "title": case "desc": case "metadata": break;
            // Everything else (text / image / use / defs / symbol / linear|radialGradient / …) is not
            // rendered this cut — flag it so the caller surfaces one diagnostic per image (PR-230 [P2]).
            default: state.SawUnsupported = true; break;
        }
        canvas.RestoreToCount(restore);
    }

    private static void DrawRect(SKCanvas canvas, XElement el, SvgStyle style)
    {
        var x = Num(el, "x"); var y = Num(el, "y");
        var w = Num(el, "width"); var h = Num(el, "height");
        if (!(w > 0) || !(h > 0)) return;
        var rx = Num(el, "rx"); var ry = Num(el, "ry");
        using var path = new SKPath();
        if (rx > 0 || ry > 0) path.AddRoundRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), (float)(rx > 0 ? rx : ry), (float)(ry > 0 ? ry : rx));
        else path.AddRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)));
        Paint(canvas, path, style);
    }

    private static void DrawCircle(SKCanvas canvas, XElement el, SvgStyle style)
    {
        var r = Num(el, "r");
        if (!(r > 0)) return;
        using var path = new SKPath();
        path.AddCircle((float)Num(el, "cx"), (float)Num(el, "cy"), (float)r);
        Paint(canvas, path, style);
    }

    private static void DrawEllipse(SKCanvas canvas, XElement el, SvgStyle style)
    {
        var rx = Num(el, "rx"); var ry = Num(el, "ry");
        if (!(rx > 0) || !(ry > 0)) return;
        var cx = Num(el, "cx"); var cy = Num(el, "cy");
        using var path = new SKPath();
        path.AddOval(new SKRect((float)(cx - rx), (float)(cy - ry), (float)(cx + rx), (float)(cy + ry)));
        Paint(canvas, path, style);
    }

    private static void DrawLine(SKCanvas canvas, XElement el, SvgStyle style)
    {
        if (style.Stroke is null) return;
        using var path = new SKPath();
        path.MoveTo((float)Num(el, "x1"), (float)Num(el, "y1"));
        path.LineTo((float)Num(el, "x2"), (float)Num(el, "y2"));
        StrokeOnly(canvas, path, style);
    }

    private static void DrawPoly(SKCanvas canvas, XElement el, SvgStyle style, bool close)
    {
        var pts = ParsePoints(Attr(el, "points"));
        if (pts.Count < 2) return;
        using var path = new SKPath();
        path.MoveTo(pts[0]);
        for (var i = 1; i < pts.Count; i++) path.LineTo(pts[i]);
        if (close) path.Close();
        Paint(canvas, path, style);
    }

    private static void DrawPath(SKCanvas canvas, XElement el, SvgStyle style)
    {
        var d = Attr(el, "d");
        if (string.IsNullOrWhiteSpace(d)) return;
        using var path = SKPath.ParseSvgPathData(d);
        if (path is null) return; // malformed path data → skip
        Paint(canvas, path, style);
    }

    /// <summary>Fill then stroke (SVG paint order §13.3): fill with the resolved fill (default black), then
    /// stroke if a stroke is set.</summary>
    private static void Paint(SKCanvas canvas, SKPath path, SvgStyle style)
    {
        if (style.Fill.Alpha > 0)
        {
            using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = style.Fill, IsAntialias = true };
            canvas.DrawPath(path, fill);
        }
        StrokeOnly(canvas, path, style);
    }

    private static void StrokeOnly(SKCanvas canvas, SKPath path, SvgStyle style)
    {
        if (style.Stroke is not { } sc || !(style.StrokeWidth > 0)) return;
        using var stroke = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = sc,
            StrokeWidth = style.StrokeWidth,
            IsAntialias = true,
        };
        canvas.DrawPath(path, stroke);
    }

    private static SvgStyle ResolveStyle(XElement el, SvgStyle inherited, RenderState state)
    {
        var fill = inherited.Fill;
        var hasFill = inherited.HasExplicitFill;
        var fillRaw = PresentationAttr(el, "fill");
        if (fillRaw is not null)
        {
            if (fillRaw.Equals("none", StringComparison.OrdinalIgnoreCase)) { fill = SKColors.Transparent; hasFill = true; }
            else if (IsPaintServer(fillRaw)) { fill = SKColors.Transparent; hasFill = true; state.SawUnsupported = true; } // url(#grad) — not the inherited/default black (PR-230 [P2])
            else if (TryColor(fillRaw, out var fc)) { fill = fc; hasFill = true; }
        }

        var stroke = inherited.Stroke;
        var strokeRaw = PresentationAttr(el, "stroke");
        if (strokeRaw is not null)
        {
            if (strokeRaw.Equals("none", StringComparison.OrdinalIgnoreCase)) stroke = null;
            else if (IsPaintServer(strokeRaw)) { stroke = null; state.SawUnsupported = true; } // url() stroke — no paint
            else if (TryColor(strokeRaw, out var stc)) stroke = stc;
        }

        var strokeWidth = inherited.StrokeWidth;
        var swRaw = PresentationAttr(el, "stroke-width");
        if (swRaw is not null && double.TryParse(TrimUnit(swRaw), NumberStyles.Float, CultureInfo.InvariantCulture, out var sw))
            strokeWidth = (float)sw;

        return new SvgStyle(fill, stroke, strokeWidth, hasFill);
    }

    /// <summary>A paint server reference (<c>url(#id)</c> — a gradient / pattern) the renderer can't
    /// resolve this cut. Treated as no-paint (NOT inherited/default black) so a gradient-filled logo goes
    /// transparent rather than turning into a black blob.</summary>
    private static bool IsPaintServer(string raw) =>
        raw.TrimStart().StartsWith("url(", StringComparison.OrdinalIgnoreCase);

    // ---- attribute / value helpers ----

    private static string? Attr(XElement el, string name) => el.Attribute(name)?.Value;

    /// <summary>A presentation value from either the attribute or an inline <c>style="…"</c> declaration
    /// (the style wins, per SVG §6.4). Returns null if unset.</summary>
    private static string? PresentationAttr(XElement el, string name)
    {
        var style = el.Attribute("style")?.Value;
        if (style is not null)
        {
            foreach (var decl in style.Split(';'))
            {
                var c = decl.IndexOf(':');
                if (c <= 0) continue;
                if (decl.AsSpan(0, c).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return decl[(c + 1)..].Trim();
            }
        }
        return el.Attribute(name)?.Value;
    }

    private static double Num(XElement el, string name) =>
        double.TryParse(TrimUnit(Attr(el, name)), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double ParseLengthPx(string? raw, double fallback) =>
        double.TryParse(TrimUnit(raw), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    private static string? TrimUnit(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        s = s.Trim();
        // Strip a trailing px / pt unit (other units are a refinement); '%' returns null (unsupported here).
        if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase)) return s[..^2];
        if (s.EndsWith("pt", StringComparison.OrdinalIgnoreCase)) return s[..^2];
        if (s.EndsWith("%", StringComparison.Ordinal)) return null;
        return s;
    }

    private static (double X, double Y, double W, double H) ParseViewBox(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (0, 0, 0, 0);
        var p = raw.Split(new[] { ' ', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 4) return (0, 0, 0, 0);
        double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return (D(p[0]), D(p[1]), D(p[2]), D(p[3]));
    }

    private static List<SKPoint> ParsePoints(string? raw)
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

    /// <summary>Parse an SVG <c>transform</c> list (translate / scale / rotate / matrix) into a single
    /// matrix, or null if empty / unparseable.</summary>
    private static SKMatrix? ParseTransform(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = SKMatrix.Identity;
        var any = false;
        var i = 0;
        while (i < raw.Length)
        {
            var open = raw.IndexOf('(', i);
            if (open < 0) break;
            var name = raw[i..open].Trim().TrimStart(',', ' ').ToLowerInvariant();
            var close = raw.IndexOf(')', open);
            if (close < 0) break;
            var args = raw[(open + 1)..close].Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            float A(int k) => k < args.Length && float.TryParse(args[k], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
            SKMatrix step = name switch
            {
                "translate" => SKMatrix.CreateTranslation(A(0), args.Length > 1 ? A(1) : 0),
                "scale" => SKMatrix.CreateScale(A(0), args.Length > 1 ? A(1) : A(0)),
                "rotate" => args.Length >= 3
                    ? SKMatrix.CreateRotationDegrees(A(0), A(1), A(2))
                    : SKMatrix.CreateRotationDegrees(A(0)),
                "matrix" => new SKMatrix { ScaleX = A(0), SkewY = A(1), SkewX = A(2), ScaleY = A(3), TransX = A(4), TransY = A(5), Persp2 = 1 },
                _ => SKMatrix.Identity,
            };
            m = m.PreConcat(step);
            any = true;
            i = close + 1;
        }
        return any ? m : null;
    }

    private static bool TryColor(string raw, out SKColor color)
    {
        color = SKColors.Black;
        var s = raw.Trim();
        if (s.Length == 0) return false;
        if (s.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) { color = SKColors.Black; return true; }
        if (s.Equals("transparent", StringComparison.OrdinalIgnoreCase)) { color = SKColors.Transparent; return true; }
        if (s.StartsWith('#') && SKColor.TryParse(s, out color)) return true;
        if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)) return TryRgb(s, out color);
        if (NamedColors.TryGetValue(s, out var named)) { color = named; return true; }
        return SKColor.TryParse(s, out color); // best-effort (Skia parses some names)
    }

    private static bool TryRgb(string s, out SKColor color)
    {
        color = SKColors.Black;
        var open = s.IndexOf('(');
        var close = s.IndexOf(')');
        if (open < 0 || close <= open) return false;
        var parts = s[(open + 1)..close].Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        byte Ch(string p) => (byte)Math.Clamp(
            p.EndsWith('%')
                ? (int)Math.Round(double.Parse(p[..^1], CultureInfo.InvariantCulture) / 100.0 * 255)
                : (int)Math.Round(double.Parse(p, CultureInfo.InvariantCulture)), 0, 255);
        try
        {
            var a = parts.Length >= 4
                ? (byte)Math.Clamp((int)Math.Round(double.Parse(parts[3], CultureInfo.InvariantCulture) * 255), 0, 255)
                : (byte)255;
            color = new SKColor(Ch(parts[0]), Ch(parts[1]), Ch(parts[2]), a);
            return true;
        }
        catch (Exception) { return false; }
    }

    private static readonly Dictionary<string, SKColor> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = SKColors.Black, ["white"] = SKColors.White, ["red"] = SKColors.Red,
        ["green"] = new SKColor(0, 128, 0), ["blue"] = SKColors.Blue, ["yellow"] = SKColors.Yellow,
        ["cyan"] = SKColors.Cyan, ["magenta"] = SKColors.Magenta, ["gray"] = SKColors.Gray,
        ["grey"] = SKColors.Gray, ["orange"] = new SKColor(255, 165, 0), ["purple"] = new SKColor(128, 0, 128),
        ["silver"] = new SKColor(192, 192, 192), ["maroon"] = new SKColor(128, 0, 0),
        ["navy"] = new SKColor(0, 0, 128), ["teal"] = new SKColor(0, 128, 128),
        ["lime"] = SKColors.Lime, ["olive"] = new SKColor(128, 128, 0),
    };
}
