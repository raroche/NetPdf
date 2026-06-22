// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace NetPdf.Rendering;

/// <summary>Phase 4 gradients — a parsed CSS <c>linear-gradient()</c> (CSS Images L3 §3.1):
/// the gradient-line <see cref="AngleDeg"/> (CSS convention — 0° = "to top", clockwise) plus
/// the ordered color stops. Each stop carries the RAW color text (resolved to RGBA by the
/// painter via the shared color resolver) and an optional position as a fraction in [0, 1]
/// (percentages; <see langword="null"/> = unpositioned → spread evenly per CSS Images §3.4).</summary>
internal sealed record CssLinearGradient(double AngleDeg, IReadOnlyList<CssGradientStop> Stops);

/// <summary>Phase 4 gradients — one parsed color stop: the raw color token + an optional
/// position fraction in [0, 1] (null = unpositioned).</summary>
internal readonly record struct CssGradientStop(string ColorRaw, double? Position);

/// <summary>Phase 4 gradients — a minimal, allocation-light parser for the
/// <c>linear-gradient()</c> background-image form. Supports the common authored shapes:
/// an optional leading direction (<c>&lt;angle&gt;</c> in deg/grad/rad/turn, or
/// <c>to &lt;side&gt;</c> / <c>to &lt;corner&gt;</c>) followed by 2+ comma-separated color
/// stops (each <c>&lt;color&gt; &lt;percentage&gt;?</c>). <c>repeating-linear-gradient</c>,
/// length-positioned stops, and color-interpolation hints are out of this first cut and make
/// the whole value unsupported (the caller falls back to the background-color). Returns
/// <see langword="null"/> for any value that isn't a single supported <c>linear-gradient()</c>.</summary>
internal static class CssLinearGradient_Parser
{
    public static CssLinearGradient? TryParse(string? rawBackgroundImage)
    {
        if (string.IsNullOrWhiteSpace(rawBackgroundImage)) return null;
        var value = rawBackgroundImage.Trim();

        // Only a single, non-repeating linear-gradient() layer (no commas BETWEEN layers — the
        // commas inside are the arg separators; a multi-LAYER background is a documented residual).
        const string prefix = "linear-gradient(";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        if (!value.EndsWith(")", StringComparison.Ordinal)) return null;
        var inner = value.Substring(prefix.Length, value.Length - prefix.Length - 1).Trim();
        if (inner.Length == 0) return null;

        var args = SplitTopLevelCommas(inner);
        if (args.Count < 2) return null; // a gradient needs ≥ 2 stops (or a direction + ≥ 1, but ≥ 2 stops is the real floor)

        var angleDeg = 180.0; // CSS default direction = "to bottom".
        var firstIsDirection = TryParseDirection(args[0], out var parsedAngle);
        if (firstIsDirection) angleDeg = parsedAngle;
        var stopStart = firstIsDirection ? 1 : 0;
        if (args.Count - stopStart < 2) return null; // need ≥ 2 color stops

        var stops = new List<CssGradientStop>(args.Count - stopStart);
        for (var i = stopStart; i < args.Count; i++)
        {
            if (!TryParseStop(args[i], out var stop)) return null; // any unparseable stop → unsupported
            stops.Add(stop);
        }
        return new CssLinearGradient(angleDeg, stops);
    }

    /// <summary>Parse a leading direction token: <c>&lt;angle&gt;</c> (deg/grad/rad/turn) or
    /// <c>to &lt;side&gt;</c> / <c>to &lt;corner&gt;</c>. Sides map to the CSS angle convention;
    /// a corner is APPROXIMATED by the angle pointing at it for a square box (documented — an
    /// exact corner gradient depends on the box aspect ratio, refined in the painter if needed).</summary>
    private static bool TryParseDirection(string arg, out double angleDeg)
    {
        angleDeg = 180.0;
        var t = arg.Trim();
        if (t.StartsWith("to ", StringComparison.OrdinalIgnoreCase))
        {
            var sides = t.Substring(3).Trim().ToLowerInvariant().Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            bool top = false, bottom = false, left = false, right = false;
            foreach (var s in sides)
            {
                switch (s)
                {
                    case "top": top = true; break;
                    case "bottom": bottom = true; break;
                    case "left": left = true; break;
                    case "right": right = true; break;
                    default: return false;
                }
            }
            // Single side.
            if (top && !left && !right) { angleDeg = 0; return true; }
            if (right && !top && !bottom) { angleDeg = 90; return true; }
            if (bottom && !left && !right) { angleDeg = 180; return true; }
            if (left && !top && !bottom) { angleDeg = 270; return true; }
            // Corner — approximate as the 45° diagonal toward the corner (exact value is
            // aspect-ratio-dependent; the painter recomputes from the box if it matters).
            if (top && right) { angleDeg = 45; return true; }
            if (bottom && right) { angleDeg = 135; return true; }
            if (bottom && left) { angleDeg = 225; return true; }
            if (top && left) { angleDeg = 315; return true; }
            return false;
        }
        return TryParseAngle(t, out angleDeg);
    }

    private static bool TryParseAngle(string token, out double angleDeg)
    {
        angleDeg = 0;
        var t = token.Trim().ToLowerInvariant();
        (string unit, double factor)[] units =
        {
            ("deg", 1.0), ("grad", 0.9), ("rad", 180.0 / Math.PI), ("turn", 360.0),
        };
        foreach (var (unit, factor) in units)
        {
            if (t.EndsWith(unit, StringComparison.Ordinal))
            {
                var num = t.Substring(0, t.Length - unit.Length);
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    angleDeg = ((v * factor) % 360 + 360) % 360;
                    return true;
                }
                return false;
            }
        }
        return false;
    }

    /// <summary>Parse a color stop: <c>&lt;color&gt; &lt;percentage&gt;?</c>. The color is the
    /// raw text (the painter resolves it); a trailing <c>%</c> token is the position. A trailing
    /// length position (e.g. <c>20px</c>) is unsupported (→ false). A bare color (no position)
    /// yields a null position (spread evenly later). Shared with the radial parser.</summary>
    internal static bool TryParseStop(string arg, out CssGradientStop stop)
    {
        stop = default;
        var t = arg.Trim();
        if (t.Length == 0) return false;

        // The position, if present, is the LAST whitespace-separated token AND ends with '%'.
        // A function color (rgb()/hsl()/etc.) contains spaces but no top-level position split is
        // needed: we only peel a trailing token when it's a bare percentage.
        var lastSpace = t.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            var tail = t.Substring(lastSpace + 1);
            if (tail.EndsWith("%", StringComparison.Ordinal)
                && !tail.Contains(')')   // not part of a function tail
                && double.TryParse(tail.AsSpan(0, tail.Length - 1), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var pct))
            {
                stop = new CssGradientStop(t.Substring(0, lastSpace).Trim(), Math.Clamp(pct / 100.0, 0.0, 1.0));
                return true;
            }
            // A trailing length position (px/em/…) is unsupported in this first cut.
            if (EndsWithLengthUnit(tail)) return false;
        }
        stop = new CssGradientStop(t, null);
        return true;
    }

    private static bool EndsWithLengthUnit(string token)
    {
        ReadOnlySpan<string> units = ["px", "em", "rem", "vw", "vh", "pt", "cm", "mm", "in", "pc", "ex", "ch"];
        var lower = token.ToLowerInvariant();
        foreach (var u in units)
            if (lower.EndsWith(u, StringComparison.Ordinal)
                && double.TryParse(lower.AsSpan(0, lower.Length - u.Length), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out _))
                return true;
        return false;
    }

    /// <summary>Split on commas that are NOT inside parentheses (so <c>rgb(1, 2, 3)</c> stays one
    /// token). Trims each segment. Shared with the radial parser.</summary>
    internal static List<string> SplitTopLevelCommas(string s)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '(') depth++;
            else if (c == ')') { if (depth > 0) depth--; }
            else if (c == ',' && depth == 0)
            {
                parts.Add(s.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }
        parts.Add(s.Substring(start).Trim());
        return parts;
    }
}
