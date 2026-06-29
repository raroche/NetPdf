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
        // A <textPath> child lays the text along a referenced path instead of the normal horizontal flow.
        foreach (var child in text.Elements())
            if (child.Name.LocalName.Equals("textPath", StringComparison.OrdinalIgnoreCase))
            {
                DrawTextPath(canvas, child, style, state);
                return;
            }

        var runs = new List<Run>();
        CollectRuns(text, style, runs, state, topLevel: true);
        if (runs.Count == 0) return;

        var startX = HasLen(text, "x", state, style, LenAxis.X, out var sx) ? sx : 0f;
        var startY = HasLen(text, "y", state, style, LenAxis.Y, out var sy) ? sy : 0f;

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
                chunkWidth += runs[k].Dx + MeasureRun(f, runs[k].Text, runs[k].Style);
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
                var width = MeasureRun(font, run.Text, run.Style);
                DrawSpacedOrWhole(canvas, run.Text, penX, penY, font, run.Style, state);
                penX += width;
            }
            i = j;
        }
    }

    /// <summary>The run's total advance including letter-spacing (after each glyph) + word-spacing (after
    /// each space). With both zero this is exactly <c>SKFont.MeasureText</c> → the default stays unchanged.</summary>
    private static float MeasureRun(SKFont font, string text, SvgStyle style)
    {
        var w = font.MeasureText(text);
        if (style.LetterSpacing == 0 && style.WordSpacing == 0) return w;
        w += style.LetterSpacing * text.Length;
        foreach (var ch in text) if (ch == ' ') w += style.WordSpacing;
        return w;
    }

    /// <summary>Draw a run honoring letter-/word-spacing. With no spacing it's a single whole-string draw
    /// (byte-identical default); otherwise each glyph is drawn at its own pen position.</summary>
    private static void DrawSpacedOrWhole(SKCanvas canvas, string text, float x, float baseline, SKFont font, SvgStyle style, SvgRenderState state)
    {
        if (style.LetterSpacing == 0 && style.WordSpacing == 0)
        {
            DrawRun(canvas, text, x, baseline, font.MeasureText(text), font, style, state);
            return;
        }
        var gx = x;
        foreach (var ch in text)
        {
            var glyph = ch.ToString();
            var gw = font.MeasureText(glyph);
            DrawRun(canvas, glyph, gx, baseline, gw, font, style, state);
            gx += gw + style.LetterSpacing + (ch == ' ' ? style.WordSpacing : 0);
        }
    }

    /// <summary>Lay the text of a <c>&lt;textPath&gt;</c> along its referenced <c>&lt;path&gt;</c> (SVG §10.13):
    /// each glyph is positioned at its advance-midpoint along the path arc length (offset by
    /// <c>startOffset</c> + the <c>text-anchor</c>) and rotated to the path tangent. Only a <c>&lt;path&gt;</c>
    /// reference is supported; a missing / non-path target is flagged. Glyphs whose midpoint falls off the
    /// path are dropped.</summary>
    private static void DrawTextPath(SKCanvas canvas, XElement textPath, SvgStyle parentStyle, SvgRenderState state)
    {
        var id = SvgAttr.HrefId(textPath);
        if (id is null || !state.Ids.TryGetValue(id, out var pathEl)
            || !pathEl.Name.LocalName.Equals("path", StringComparison.OrdinalIgnoreCase)
            || pathEl.Attribute("d")?.Value is not { } d || string.IsNullOrWhiteSpace(d))
        {
            state.SawUnsupported = true;
            return;
        }
        using var path = SKPath.ParseSvgPathData(d);
        if (path is null) { state.SawUnsupported = true; return; }

        var style = ResolveRunStyle(textPath, parentStyle);
        var content = Collapse(AllText(textPath));
        if (content.Length == 0) return;
        using var font = BuildFont(style);
        using var measure = new SKPathMeasure(path, false);
        var pathLen = measure.Length;
        if (!(pathLen > 0)) return;

        var startOffset = ParseStartOffset(SvgAttr.Get(textPath, "startOffset"), pathLen);
        var total = MeasureRun(font, content, style);
        var anchor = (style.TextAnchor ?? string.Empty).Trim().ToLowerInvariant();
        var distance = startOffset + anchor switch { "middle" => -total / 2f, "end" => -total, _ => 0f };

        using var fillShader = style.FillRef is { } fref ? state.ResolveShader(fref, path.Bounds, style.FillOpacity, style) : null;
        for (var i = 0; i < content.Length; i++)
        {
            var glyph = content[i].ToString();
            var adv = font.MeasureText(glyph);
            var mid = distance + adv / 2f;
            if (mid >= 0 && mid <= pathLen && measure.GetPositionAndTangent(mid, out var pos, out var tan))
            {
                var save = canvas.Save();
                canvas.Translate(pos.X, pos.Y);
                canvas.RotateRadians((float)Math.Atan2(tan.Y, tan.X));
                DrawPathGlyph(canvas, glyph, -adv / 2f, font, style, fillShader?.Shader, state);
                canvas.RestoreToCount(save);
            }
            distance += adv + style.LetterSpacing + (content[i] == ' ' ? style.WordSpacing : 0);
        }
    }

    private static void DrawPathGlyph(
        SKCanvas canvas, string glyph, float x, SKFont font, SvgStyle style, SKShader? fillShader, SvgRenderState state)
    {
        if (fillShader is not null)
        {
            using var p = new SKPaint { Style = SKPaintStyle.Fill, Shader = fillShader, IsAntialias = true };
            canvas.DrawText(glyph, x, 0, font, p);
        }
        else if (style.Fill.Alpha > 0 && style.FillOpacity > 0)
        {
            using var p = new SKPaint { Style = SKPaintStyle.Fill, Color = WithOpacity(style.Fill, style.FillOpacity), IsAntialias = true };
            canvas.DrawText(glyph, x, 0, font, p);
        }
        if (style.StrokeWidth > 0 && style.Stroke is { } sc && style.StrokeRef is null)
        {
            using var p = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = style.StrokeWidth, Color = WithOpacity(sc, style.StrokeOpacity), IsAntialias = true };
            canvas.DrawText(glyph, x, 0, font, p);
        }
    }

    /// <summary>Parse a <c>startOffset</c> — a length (px/pt/unitless) or a percentage of the path length.</summary>
    private static float ParseStartOffset(string? raw, float pathLen)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        raw = raw.Trim();
        if (raw.EndsWith("%", StringComparison.Ordinal))
            return double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) ? (float)(pct / 100.0 * pathLen) : 0;
        return TryUnit(raw, out var v) ? v : 0;
    }

    /// <summary>Concatenate all descendant text content (textPath glyphs ignore inner element structure
    /// this cut).</summary>
    private static string AllText(XElement el)
    {
        var sb = new StringBuilder();
        foreach (var node in el.DescendantNodes())
            if (node is XText t) sb.Append(t.Value);
        return sb.ToString();
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
            if (state.ResolveShader(fref, bounds, style.FillOpacity, style) is { } rp)
                using (rp)
                using (var p = new SKPaint { Style = SKPaintStyle.Fill, Shader = rp.Shader, IsAntialias = true })
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
                if (state.ResolveShader(sref, bounds, style.StrokeOpacity, style) is { } rp)
                    using (rp)
                    using (var p = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = style.StrokeWidth, Shader = rp.Shader, IsAntialias = true })
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
    private static void CollectRuns(XElement el, SvgStyle inherited, List<Run> runs, SvgRenderState state, bool topLevel)
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
                    // x/y/dx/dy resolve against the viewport (% — x against width, y against height) and the
                    // run's font-size (em/rem); px/pt/unitless pass through (SVG §7.10 / §10.5).
                    var ax = HasLen(child, "x", state, childStyle, LenAxis.X, out var xv) ? (float?)xv : null;
                    var ay = HasLen(child, "y", state, childStyle, LenAxis.Y, out var yv) ? (float?)yv : null;
                    var dx = HasLen(child, "dx", state, childStyle, LenAxis.X, out var dxv) ? dxv : 0f;
                    var dy = HasLen(child, "dy", state, childStyle, LenAxis.Y, out var dyv) ? dyv : 0f;
                    // A tspan can hold its own text + nested tspans; flatten the text, apply the offset to the first run.
                    var before = runs.Count;
                    CollectRuns(child, childStyle, runs, state, topLevel: false);
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
        var lsRaw = SvgAttr.Presentation(el, "letter-spacing");
        if (lsRaw is not null) s = s with { LetterSpacing = ParseSpacing(lsRaw) };
        var wsRaw = SvgAttr.Presentation(el, "word-spacing");
        if (wsRaw is not null) s = s with { WordSpacing = ParseSpacing(wsRaw) };
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

    /// <summary>Which viewport extent a <c>%</c> coordinate resolves against (SVG §7.10): an X-axis
    /// coordinate (x/dx) → the viewport WIDTH; a Y-axis coordinate (y/dy) → the HEIGHT.</summary>
    private enum LenAxis { X, Y }

    /// <summary>Resolve a text coordinate honoring units: <c>%</c> (against the viewport per
    /// <paramref name="axis"/>), <c>em</c>/<c>rem</c> (against the run's font-size), and
    /// <c>px</c>/<c>pt</c>/unitless (as-is). Returns <see langword="false"/> when the attribute is absent
    /// (so an absolute <c>x</c> establishing a text chunk is distinguished from an unset one) or unparseable.</summary>
    private static bool HasLen(XElement el, string name, SvgRenderState state, SvgStyle style, LenAxis axis, out float value)
    {
        value = 0;
        var raw = el.Attribute(name)?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        raw = raw.Trim();
        if (raw.EndsWith("%", StringComparison.Ordinal))
        {
            if (!double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) return false;
            value = (float)(pct / 100.0 * (axis == LenAxis.X ? state.ViewportW : state.ViewportH));
            return true;
        }
        if (raw.EndsWith("rem", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.TryParse(raw[..^3], NumberStyles.Float, CultureInfo.InvariantCulture, out var rem)) return false;
            value = (float)(rem * style.FontSizePx);
            return true;
        }
        if (raw.EndsWith("em", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.TryParse(raw[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var em)) return false;
            value = (float)(em * style.FontSizePx);
            return true;
        }
        return TryUnit(raw, out value);
    }

    /// <summary><c>letter-spacing</c> / <c>word-spacing</c> — <c>normal</c> → 0; else a length.</summary>
    private static float ParseSpacing(string raw)
    {
        raw = raw.Trim();
        if (raw.Equals("normal", StringComparison.OrdinalIgnoreCase)) return 0;
        return TryUnit(raw, out var v) ? v : 0;
    }

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
