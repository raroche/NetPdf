// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;

namespace NetPdf.Svg;

/// <summary>Phase 4 SVG part 2 (PR 7) — render an SVG <c>&lt;text&gt;</c> element (with <c>&lt;tspan&gt;</c>
/// runs) onto the raster canvas using Skia's text shaping. Honors <c>x</c>/<c>y</c>, per-run
/// <c>dx</c>/<c>dy</c> and absolute <c>x</c>/<c>y</c>, <c>text-anchor</c> (start/middle/end), the inherited
/// font properties (family / size / weight / style), and fill / stroke incl. a gradient paint server.
/// Out of scope (raster fallback): <c>textPath</c>, <c>rotate</c>, <c>textLength</c>, bidi/complex-script
/// reordering, and per-glyph positioning lists.</summary>
internal static class SvgText
{
    private readonly record struct Run(string Text, SvgStyle Style, float? AbsX, float? AbsY, float Dx, float Dy);

    public static void Draw(SKCanvas canvas, XElement text, SvgStyle style, SvgRenderState state)
    {
        var runs = new List<Run>();
        CollectRuns(text, style, runs, topLevel: true);
        if (runs.Count == 0) return;

        var startX = (float)NumOr(text, "x", 0);
        var startY = (float)NumOr(text, "y", 0);

        // A run with an absolute x establishes a new "text chunk" (SVG §10.5); text-anchor is resolved
        // independently PER chunk (PR-231 review [P2/P3]) — so multiple centered <tspan x=…> labels inside
        // one <text> each center on their own x, not on the flattened sequence.
        var penY = startY;
        var i = 0;
        while (i < runs.Count)
        {
            var j = i + 1;
            while (j < runs.Count && runs[j].AbsX is null) j++; // chunk = runs[i..j)

            var chunkWidth = 0f;
            for (var k = i; k < j; k++)
            {
                using var f = BuildFont(runs[k].Style);
                chunkWidth += runs[k].Dx + f.MeasureText(runs[k].Text);
            }
            var anchorFactor = (runs[i].Style.TextAnchor?.Trim().ToLowerInvariant()) switch
            {
                "middle" => 0.5f,
                "end" => 1f,
                _ => 0f,
            };
            var chunkStartX = runs[i].AbsX ?? startX; // only the first chunk lacks an absolute x
            var penX = chunkStartX - anchorFactor * chunkWidth;

            for (var k = i; k < j; k++)
            {
                var run = runs[k];
                using var font = BuildFont(run.Style);
                if (run.AbsY is { } ay) penY = ay;
                penX += run.Dx;
                penY += run.Dy;
                var width = font.MeasureText(run.Text);
                DrawRun(canvas, run.Text, penX, penY, width, font, run.Style, state);
                penX += width;
            }
            i = j;
        }
    }

    private static void DrawRun(
        SKCanvas canvas, string textRun, float x, float baseline, float width, SKFont font,
        SvgStyle style, SvgRenderState state)
    {
        // Geometry bounding box for an objectBoundingBox gradient: the run advance × the font extent.
        var metrics = font.Metrics;
        var bounds = new SKRect(x, baseline + metrics.Ascent, x + width, baseline + metrics.Descent);

        if (style.FillRef is { } fref)
        {
            if (state.ResolveShader(fref, bounds, style.FillOpacity, style.CurrentColor) is { } shader)
                using (shader)
                using (var p = new SKPaint { Style = SKPaintStyle.Fill, Shader = shader, IsAntialias = true })
                    canvas.DrawText(textRun, x, baseline, font, p);
            else state.SawUnsupported = true;
        }
        else if (style.Fill.Alpha > 0 && style.FillOpacity > 0)
        {
            using var p = new SKPaint { Style = SKPaintStyle.Fill, Color = WithOpacity(style.Fill, style.FillOpacity), IsAntialias = true };
            canvas.DrawText(textRun, x, baseline, font, p);
        }

        if (style.StrokeWidth > 0)
        {
            if (style.StrokeRef is { } sref)
            {
                if (state.ResolveShader(sref, bounds, style.StrokeOpacity, style.CurrentColor) is { } shader)
                    using (shader)
                    using (var p = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = style.StrokeWidth, Shader = shader, IsAntialias = true })
                        canvas.DrawText(textRun, x, baseline, font, p);
                else state.SawUnsupported = true;
            }
            else if (style.Stroke is { } sc)
            {
                using var p = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = style.StrokeWidth, Color = WithOpacity(sc, style.StrokeOpacity), IsAntialias = true };
                canvas.DrawText(textRun, x, baseline, font, p);
            }
        }
    }

    /// <summary>Flatten the text content into drawable runs: direct text nodes plus one level of
    /// <c>&lt;tspan&gt;</c> (each with its own overrides + dx/dy/x/y). Whitespace is collapsed (the SVG
    /// default).</summary>
    private static void CollectRuns(XElement el, SvgStyle inherited, List<Run> runs, bool topLevel)
    {
        foreach (var node in el.Nodes())
        {
            switch (node)
            {
                case XText t:
                    var s = Collapse(t.Value);
                    if (s.Length > 0) runs.Add(new Run(s, inherited, AbsX: null, AbsY: null, Dx: 0, Dy: 0));
                    break;
                case XElement child when child.Name.LocalName.Equals("tspan", StringComparison.OrdinalIgnoreCase):
                    var childStyle = ResolveRunStyle(child, inherited);
                    var ax = HasNum(child, "x", out var xv) ? (float?)xv : null;
                    var ay = HasNum(child, "y", out var yv) ? (float?)yv : null;
                    var dx = (float)NumOr(child, "dx", 0);
                    var dy = (float)NumOr(child, "dy", 0);
                    // A tspan can hold its own text + nested tspans; flatten the text, apply the offset to the first run.
                    var before = runs.Count;
                    CollectRuns(child, childStyle, runs, topLevel: false);
                    if (runs.Count > before && (ax is not null || ay is not null || dx != 0 || dy != 0))
                        runs[before] = runs[before] with { AbsX = ax, AbsY = ay, Dx = dx, Dy = dy };
                    break;
                default: break; // comments / unsupported children ignored
            }
        }
    }

    /// <summary>Resolve a <c>&lt;tspan&gt;</c>'s presentation overrides (color / fill / fill-opacity / stroke
    /// / stroke-width / stroke-opacity / font-size / font-weight / font-style) over the inherited text
    /// style.</summary>
    private static SvgStyle ResolveRunStyle(XElement el, SvgStyle inherited)
    {
        var s = inherited;
        var colorRaw = SvgAttr.Presentation(el, "color");
        if (colorRaw is not null && !colorRaw.Equals("currentColor", StringComparison.OrdinalIgnoreCase)
            && SvgColor.TryParse(colorRaw, out var cc)) s = s with { CurrentColor = cc };

        var fillRaw = SvgAttr.Presentation(el, "fill");
        if (fillRaw is not null)
        {
            if (fillRaw.Equals("none", StringComparison.OrdinalIgnoreCase)) s = s with { Fill = SKColors.Transparent, FillRef = null };
            else if (fillRaw.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) s = s with { Fill = s.CurrentColor, FillRef = null };
            else if (PaintServerId(fillRaw) is { } id) s = s with { FillRef = id };
            else if (SvgColor.TryParse(fillRaw, out var fc)) s = s with { Fill = fc, FillRef = null };
        }
        var foRaw = SvgAttr.Presentation(el, "fill-opacity");
        if (foRaw is not null && TryUnit(foRaw, out var fo)) s = s with { FillOpacity = Math.Clamp(fo, 0, 1) };

        var strokeRaw = SvgAttr.Presentation(el, "stroke");
        if (strokeRaw is not null)
        {
            if (strokeRaw.Equals("none", StringComparison.OrdinalIgnoreCase)) s = s with { Stroke = null, StrokeRef = null };
            else if (strokeRaw.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) s = s with { Stroke = s.CurrentColor, StrokeRef = null };
            else if (PaintServerId(strokeRaw) is { } id) s = s with { StrokeRef = id, Stroke = null };
            else if (SvgColor.TryParse(strokeRaw, out var stc)) s = s with { Stroke = stc, StrokeRef = null };
        }
        var swRaw = SvgAttr.Presentation(el, "stroke-width");
        if (swRaw is not null && TryUnit(swRaw, out var sw)) s = s with { StrokeWidth = sw };
        var soRaw = SvgAttr.Presentation(el, "stroke-opacity");
        if (soRaw is not null && TryUnit(soRaw, out var so)) s = s with { StrokeOpacity = Math.Clamp(so, 0, 1) };

        var fsRaw = SvgAttr.Presentation(el, "font-size");
        if (fsRaw is not null && TryUnit(fsRaw, out var fs) && fs > 0) s = s with { FontSizePx = fs };
        var fwRaw = SvgAttr.Presentation(el, "font-weight");
        if (fwRaw is not null)
        {
            if (fwRaw.Trim().Equals("bold", StringComparison.OrdinalIgnoreCase)) s = s with { FontWeight = 700 };
            else if (fwRaw.Trim().Equals("normal", StringComparison.OrdinalIgnoreCase)) s = s with { FontWeight = 400 };
            else if (int.TryParse(fwRaw.Trim(), out var w)) s = s with { FontWeight = Math.Clamp(w, 1, 1000) };
        }
        var fstRaw = SvgAttr.Presentation(el, "font-style");
        if (fstRaw is not null)
            s = s with { Italic = fstRaw.Trim().Equals("italic", StringComparison.OrdinalIgnoreCase) || fstRaw.Trim().Equals("oblique", StringComparison.OrdinalIgnoreCase) };
        return s;
    }

    private static SKFont BuildFont(SvgStyle style)
    {
        var slant = style.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
        var family = FirstFamily(style.FontFamily);
        var typeface = SKTypeface.FromFamilyName(family, (SKFontStyleWeight)style.FontWeight, SKFontStyleWidth.Normal, slant)
            ?? SKTypeface.Default;
        return new SKFont(typeface, style.FontSizePx);
    }

    /// <summary>The first concrete family from a CSS font-family list (drop quotes; map a generic to a Skia
    /// default by passing <see langword="null"/>).</summary>
    private static string? FirstFamily(string? list)
    {
        if (string.IsNullOrWhiteSpace(list)) return null;
        var first = list.Split(',')[0].Trim().Trim('\'', '"');
        return first.Length == 0 || IsGeneric(first) ? null : first;
    }

    private static bool IsGeneric(string f) =>
        f.Equals("serif", StringComparison.OrdinalIgnoreCase) || f.Equals("sans-serif", StringComparison.OrdinalIgnoreCase)
        || f.Equals("monospace", StringComparison.OrdinalIgnoreCase) || f.Equals("cursive", StringComparison.OrdinalIgnoreCase)
        || f.Equals("fantasy", StringComparison.OrdinalIgnoreCase) || f.Equals("system-ui", StringComparison.OrdinalIgnoreCase);

    private static string Collapse(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var prevSpace = false;
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace) { sb.Append(' '); prevSpace = true; }
            }
            else { sb.Append(ch); prevSpace = false; }
        }
        return sb.ToString();
    }

    private static SKColor WithOpacity(SKColor c, float opacity) =>
        opacity >= 1f ? c : c.WithAlpha((byte)Math.Clamp((int)Math.Round(c.Alpha * opacity), 0, 255));

    private static string? PaintServerId(string raw)
    {
        var s = raw.TrimStart();
        if (!s.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) return null;
        var open = s.IndexOf('(');
        var close = s.IndexOf(')', open + 1);
        if (close <= open) return null;
        var inner = s[(open + 1)..close].Trim().Trim('\'', '"');
        return inner.StartsWith('#') && inner.Length > 1 ? inner[1..] : null;
    }

    private static bool HasNum(XElement el, string name, out double value)
    {
        value = 0;
        if (el.Attribute(name)?.Value is { } raw && TryUnit(raw, out var v)) { value = v; return true; }
        return false;
    }

    private static double NumOr(XElement el, string name, double fallback) =>
        el.Attribute(name)?.Value is { } raw && TryUnit(raw, out var v) ? v : fallback;

    private static bool TryUnit(string raw, out float value)
    {
        value = 0;
        raw = raw.Trim();
        if (raw.EndsWith("px", StringComparison.OrdinalIgnoreCase)) raw = raw[..^2];
        else if (raw.EndsWith("pt", StringComparison.OrdinalIgnoreCase)) raw = raw[..^2];
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
        value = (float)v;
        return true;
    }
}
