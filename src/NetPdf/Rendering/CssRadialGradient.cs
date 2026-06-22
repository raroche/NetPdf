// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace NetPdf.Rendering;

/// <summary>Phase 4 gradients — the CSS <c>radial-gradient</c> ending-shape size keyword
/// (CSS Images L3 §3.2). Default is <see cref="FarthestCorner"/>.</summary>
internal enum RadialExtent { ClosestSide, ClosestCorner, FarthestSide, FarthestCorner }

/// <summary>Phase 4 gradients — a parsed CSS <c>radial-gradient(...)</c>: the ending shape
/// (<see cref="IsCircle"/>), its <see cref="Extent"/>, the center as box-relative fractions
/// (<see cref="CenterXFraction"/> / <see cref="CenterYFraction"/>, default 0.5/0.5 = center),
/// and the color stops. The painter derives the circle/ellipse radii from the box geometry.</summary>
internal sealed record CssRadialGradient(
    bool IsCircle,
    RadialExtent Extent,
    double CenterXFraction,
    double CenterYFraction,
    IReadOnlyList<CssGradientStop> Stops);

/// <summary>Phase 4 gradients — a minimal parser for the <c>radial-gradient()</c> form
/// (CSS Images L3 §3.2). Supports an optional leading
/// <c>[ &lt;shape&gt; || &lt;extent-keyword&gt; ] [ at &lt;position&gt; ]?</c> prelude
/// (shape <c>circle</c>/<c>ellipse</c>; the four extent keywords; a position of <c>center</c>,
/// side keywords, or percentages) followed by 2+ color stops. Explicit radius lengths,
/// <c>repeating-radial-gradient</c>, and length-positioned stops are deferred (→ null, the
/// caller falls back to the background-color).</summary>
internal static class CssRadialGradient_Parser
{
    public static CssRadialGradient? TryParse(string? rawBackgroundImage)
    {
        if (string.IsNullOrWhiteSpace(rawBackgroundImage)) return null;
        var value = rawBackgroundImage.Trim();
        const string prefix = "radial-gradient(";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        if (!value.EndsWith(")", StringComparison.Ordinal)) return null;
        var inner = value.Substring(prefix.Length, value.Length - prefix.Length - 1).Trim();
        if (inner.Length == 0) return null;

        var args = CssLinearGradient_Parser.SplitTopLevelCommas(inner);
        if (args.Count < 2) return null;

        var isCircle = false;
        var extent = RadialExtent.FarthestCorner;
        var cx = 0.5;
        var cy = 0.5;
        var stopStart = 0;

        // The first arg is a prelude when it is NOT a color stop (it mentions a shape, an
        // extent keyword, or `at`). Heuristic: if it contains "at " or a known shape/extent
        // keyword token, treat it as the prelude.
        if (TryParsePrelude(args[0], ref isCircle, ref extent, ref cx, ref cy))
            stopStart = 1;

        if (args.Count - stopStart < 2) return null;
        var stops = new List<CssGradientStop>(args.Count - stopStart);
        for (var i = stopStart; i < args.Count; i++)
        {
            if (!CssLinearGradient_Parser.TryParseStop(args[i], out var stop)) return null;
            stops.Add(stop);
        }
        return new CssRadialGradient(isCircle, extent, cx, cy, stops);
    }

    /// <summary>Try to read <paramref name="arg"/> as the gradient prelude. Returns true only
    /// when it is unambiguously a prelude (a shape, extent keyword, or <c>at &lt;position&gt;</c>);
    /// a plain color stop returns false so it is parsed as the first stop.</summary>
    private static bool TryParsePrelude(string arg, ref bool isCircle, ref RadialExtent extent,
        ref double cx, ref double cy)
    {
        var t = arg.Trim().ToLowerInvariant();
        if (t.Length == 0) return false;

        string shapeExtent = t;
        var atIndex = t.IndexOf("at ", StringComparison.Ordinal);
        var hasAt = atIndex >= 0 || t.StartsWith("at ", StringComparison.Ordinal);
        if (hasAt)
        {
            shapeExtent = atIndex > 0 ? t.Substring(0, atIndex).Trim() : string.Empty;
            var posPart = t.Substring(atIndex + 3).Trim();
            if (!TryParsePosition(posPart, ref cx, ref cy)) return false;
        }

        var sawPrelude = hasAt;
        foreach (var tok in shapeExtent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (tok)
            {
                case "circle": isCircle = true; sawPrelude = true; break;
                case "ellipse": isCircle = false; sawPrelude = true; break;
                case "closest-side": extent = RadialExtent.ClosestSide; sawPrelude = true; break;
                case "closest-corner": extent = RadialExtent.ClosestCorner; sawPrelude = true; break;
                case "farthest-side": extent = RadialExtent.FarthestSide; sawPrelude = true; break;
                case "farthest-corner": extent = RadialExtent.FarthestCorner; sawPrelude = true; break;
                default: return false; // not a recognized prelude token → it's a color stop
            }
        }
        return sawPrelude;
    }

    private static bool TryParsePosition(string pos, ref double cx, ref double cy)
    {
        var tokens = pos.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens.Length > 2) return false;

        // One token sets the named axis (the other stays center); two tokens are X then Y
        // (keywords may appear in either order only for the unambiguous named forms — first
        // cut: treat the first as horizontal unless it is top/bottom).
        if (tokens.Length == 1)
        {
            return tokens[0] switch
            {
                "center" => true,
                "left" => Set(ref cx, 0.0),
                "right" => Set(ref cx, 1.0),
                "top" => Set(ref cy, 0.0),
                "bottom" => Set(ref cy, 1.0),
                _ => TryPercent(tokens[0], out cx),
            };
        }
        // Two tokens.
        var okX = AxisValue(tokens[0], horizontal: true, ref cx, ref cy);
        var okY = AxisValue(tokens[1], horizontal: false, ref cx, ref cy);
        return okX && okY;

        static bool Set(ref double target, double v) { target = v; return true; }
    }

    private static bool AxisValue(string tok, bool horizontal, ref double cx, ref double cy)
    {
        switch (tok)
        {
            case "center": return true;
            case "left": cx = 0.0; return true;
            case "right": cx = 1.0; return true;
            case "top": cy = 0.0; return true;
            case "bottom": cy = 1.0; return true;
            default:
                if (TryPercent(tok, out var f)) { if (horizontal) cx = f; else cy = f; return true; }
                return false;
        }
    }

    private static bool TryPercent(string token, out double fraction)
    {
        fraction = 0.5;
        var t = token.Trim();
        if (!t.EndsWith("%", StringComparison.Ordinal)) return false;
        if (double.TryParse(t.AsSpan(0, t.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
        {
            fraction = Math.Clamp(pct / 100.0, 0.0, 1.0);
            return true;
        }
        return false;
    }
}
