// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace NetPdf.Rendering;

/// <summary>Phase 4 gradients (PR #209 review [P2]) — a <c>to &lt;corner&gt;</c> linear-gradient
/// direction. The gradient-line angle for a corner is ASPECT-RATIO DEPENDENT (CSS Images L3 §3.1:
/// the line points into the corner's quadrant AND is perpendicular to the diagonal joining the two
/// NEIGHBORING corners), so the parser records the corner symbolically and the painter computes the
/// angle from the painted box — a fixed 45° is correct only for a SQUARE box.</summary>
internal enum LinearGradientCorner { TopLeft, TopRight, BottomRight, BottomLeft }

/// <summary>Phase 4 gradients — a parsed CSS <c>linear-gradient()</c> (CSS Images L3 §3.1):
/// the gradient-line <see cref="AngleDeg"/> (CSS convention — 0° = "to top", clockwise) plus
/// the ordered color stops. Each stop carries the RAW color text (resolved to RGBA by the
/// painter via the shared color resolver) and an optional position as a fraction in [0, 1]
/// (percentages; <see langword="null"/> = unpositioned → spread evenly per CSS Images §3.4).
/// <para>When the direction was a <c>to &lt;corner&gt;</c>, <see cref="Corner"/> is set and the
/// painter derives the true angle from the box geometry; <see cref="AngleDeg"/> then holds the
/// square-box approximation as a fallback.</para></summary>
internal sealed record CssLinearGradient(
    double AngleDeg, LinearGradientCorner? Corner, IReadOnlyList<CssGradientStop> Stops,
    bool Repeating = false);

/// <summary>Phase 4 gradients — one parsed color stop: the raw color token + an optional position.
/// The position is EITHER a <see cref="Position"/> fraction in [0, 1] (from a <c>%</c>) OR a
/// <see cref="PositionPx"/> length in CSS px (from <c>px</c> + the absolute units — PR 1 refinements);
/// the painter resolves a length to a fraction against the gradient-line length. Both null =
/// unpositioned (spread evenly per CSS Images §3.4). At most one is set.</summary>
/// <summary>One parsed gradient color stop, or — when <paramref name="IsHint"/> is true — a
/// color-interpolation HINT (CSS Images §3.4.2: a bare position between two color stops marking where
/// the 50% color falls). A hint carries only a position (<see cref="Position"/> / <see cref="PositionPx"/>);
/// its <see cref="ColorRaw"/> is empty and the resolver replaces it with a synthetic midpoint stop.</summary>
internal readonly record struct CssGradientStop(string ColorRaw, double? Position, double? PositionPx = null, bool IsHint = false);

/// <summary>Phase 4 gradients — a minimal, allocation-light parser for the
/// <c>linear-gradient()</c> background-image form. Supports the common authored shapes:
/// an optional leading direction (<c>&lt;angle&gt;</c> in deg/grad/rad/turn, or
/// <c>to &lt;side&gt;</c> / <c>to &lt;corner&gt;</c>) followed by 2+ comma-separated color
/// stops (each <c>&lt;color&gt; [ &lt;percentage&gt; | &lt;length&gt; ]?</c>). The
/// <c>repeating-linear-gradient</c> form sets <see cref="CssLinearGradient.Repeating"/> (the painter
/// tiles the stop period). Double-position stops (<c>§3.4</c>) + color-interpolation hints
/// (<c>§3.4.2</c>, a bare position between two color stops — approximated by a synthetic midpoint stop
/// at resolve time) are supported. Returns
/// <see langword="null"/> for any value that isn't a single supported (repeating-)<c>linear-gradient()</c>.</summary>
internal static class CssLinearGradient_Parser
{
    public static CssLinearGradient? TryParse(string? rawBackgroundImage)
    {
        if (string.IsNullOrWhiteSpace(rawBackgroundImage)) return null;
        var value = rawBackgroundImage.Trim();

        // A SINGLE linear-gradient() / repeating-linear-gradient() layer: the function's opening
        // paren must be matched by a closing paren at the very end of the value (PR #209 Copilot) —
        // a multi-layer list like `linear-gradient(...), url(...)` must NOT mis-terminate on a later
        // layer's `)` and parse as one gradient (which would suppress the unsupported diagnostic).
        const string plainPrefix = "linear-gradient(";
        const string repeatPrefix = "repeating-linear-gradient(";
        var repeating = false;
        string prefix;
        if (value.StartsWith(repeatPrefix, StringComparison.OrdinalIgnoreCase)) { prefix = repeatPrefix; repeating = true; }
        else if (value.StartsWith(plainPrefix, StringComparison.OrdinalIgnoreCase)) prefix = plainPrefix;
        else return null;
        if (!TryExtractSingleFunctionBody(value, prefix, out var inner)) return null;
        if (inner.Length == 0) return null;

        var args = SplitTopLevelCommas(inner);
        if (args.Count < 2) return null; // a gradient needs ≥ 2 stops (or a direction + ≥ 1, but ≥ 2 stops is the real floor)

        var angleDeg = 180.0; // CSS default direction = "to bottom".
        LinearGradientCorner? corner = null;
        var firstIsDirection = TryParseDirection(args[0], out var parsedAngle, out corner);
        if (firstIsDirection) angleDeg = parsedAngle;
        var stopStart = firstIsDirection ? 1 : 0;
        if (args.Count - stopStart < 2) return null; // need ≥ 2 color stops

        var stops = new List<CssGradientStop>(args.Count - stopStart);
        for (var i = stopStart; i < args.Count; i++)
        {
            foreach (var part in ExpandDoublePositionStop(args[i]))   // §3.4 double-position → two stops
            {
                if (!TryParseStop(part, out var stop)) return null; // any unparseable stop → unsupported
                stops.Add(stop);
            }
        }
        return new CssLinearGradient(angleDeg, corner, stops, repeating);
    }

    /// <summary>Parse a leading direction token: <c>&lt;angle&gt;</c> (deg/grad/rad/turn) or
    /// <c>to &lt;side&gt;</c> / <c>to &lt;corner&gt;</c>. Sides map to the CSS angle convention;
    /// a corner sets <paramref name="corner"/> (the painter computes the aspect-ratio-correct
    /// angle from the box) and <paramref name="angleDeg"/> to the square-box approximation as a
    /// fallback. An <c>&lt;angle&gt;</c> or single side leaves <paramref name="corner"/> null.</summary>
    private static bool TryParseDirection(string arg, out double angleDeg, out LinearGradientCorner? corner)
    {
        angleDeg = 180.0;
        corner = null;
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
            // Corner — record it symbolically (the painter derives the true, aspect-ratio-correct
            // angle from the box) and keep the 45° square-box value as the AngleDeg fallback.
            if (top && right) { angleDeg = 45; corner = LinearGradientCorner.TopRight; return true; }
            if (bottom && right) { angleDeg = 135; corner = LinearGradientCorner.BottomRight; return true; }
            if (bottom && left) { angleDeg = 225; corner = LinearGradientCorner.BottomLeft; return true; }
            if (top && left) { angleDeg = 315; corner = LinearGradientCorner.TopLeft; return true; }
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

    /// <summary>Parse a color stop: <c>&lt;color&gt; [ &lt;percentage&gt; | &lt;length&gt; ]?</c>.
    /// The color is the raw text (the painter resolves it); a trailing <c>%</c> token is a fraction
    /// position, and a trailing <c>px</c> / absolute-unit length (PR 1 refinements) is a
    /// <see cref="CssGradientStop.PositionPx"/> the painter resolves against the gradient-line length.
    /// A trailing font-relative / viewport unit (<c>em</c>/<c>rem</c>/<c>vw</c>/…) has no length
    /// context here → unsupported (false). A bare color yields a null position (spread evenly later).
    /// Shared with the radial parser.</summary>
    internal static bool TryParseStop(string arg, out CssGradientStop stop)
    {
        stop = default;
        var t = arg.Trim();
        if (t.Length == 0) return false;

        // A color-interpolation HINT (CSS Images §3.4.2) — the WHOLE arg is a bare position (no color):
        // a % or absolute length here, marking where the 50% color falls between the bracketing stops.
        if (IsPositionToken(t) && !t.Contains(')'))
        {
            if (t.EndsWith("%", StringComparison.Ordinal)
                && double.TryParse(t.AsSpan(0, t.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var hintPct))
            {
                stop = new CssGradientStop(string.Empty, Math.Clamp(hintPct / 100.0, 0.0, 1.0), null, IsHint: true);
                return true;
            }
            if (CssLengthParsing.TryLengthPx(t, out var hintPx))
            {
                stop = new CssGradientStop(string.Empty, null, hintPx, IsHint: true);
                return true;
            }
        }

        // The position, if present, is the LAST whitespace-separated token. A function color
        // (rgb()/hsl()/etc.) contains spaces but no top-level position split is needed: we only
        // peel a trailing token when it's a bare percentage or length (not part of a function tail).
        var lastSpace = t.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            var tail = t.Substring(lastSpace + 1);
            if (!tail.Contains(')')) // not part of a function tail like rgb(…)
            {
                if (tail.EndsWith("%", StringComparison.Ordinal)
                    && double.TryParse(tail.AsSpan(0, tail.Length - 1), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var pct))
                {
                    stop = new CssGradientStop(t.Substring(0, lastSpace).Trim(), Math.Clamp(pct / 100.0, 0.0, 1.0));
                    return true;
                }
                // A trailing length in px + the absolute units (PR 1 refinements) — stored raw; the
                // painter divides by the gradient-line length to get the fraction.
                if (CssLengthParsing.TryLengthPx(tail, out var lenPx))
                {
                    stop = new CssGradientStop(t.Substring(0, lastSpace).Trim(), null, lenPx);
                    return true;
                }
            }
            // A trailing token in a unit we can't resolve here (em/rem/vw/vh/ex/ch) → unsupported.
            if (EndsWithLengthUnit(tail)) return false;
        }
        stop = new CssGradientStop(t, null);
        return true;
    }

    /// <summary>CSS Images §3.4 — a DOUBLE-POSITION stop (<c>&lt;color&gt; &lt;pos&gt; &lt;pos&gt;</c>)
    /// is shorthand for two consecutive stops of the same color at each position. Returns the 1 or 2
    /// stop-arg strings to parse (a single-position or bare-color arg is returned unchanged). A position
    /// token is a <c>%</c>, an absolute length, or an angle (for conic) — never a function tail. Shared
    /// by the linear / radial / conic stop builders.</summary>
    internal static IReadOnlyList<string> ExpandDoublePositionStop(string arg)
    {
        var single = new[] { arg };
        var t = arg.Trim();
        var lastSpace = t.LastIndexOf(' ');
        if (lastSpace <= 0) return single;
        var lastTok = t.Substring(lastSpace + 1);
        if (lastTok.Contains(')') || !IsPositionToken(lastTok)) return single;
        var head = t.Substring(0, lastSpace).TrimEnd();
        var prevSpace = head.LastIndexOf(' ');
        if (prevSpace <= 0) return single; // only one position — the head is the color
        var prevTok = head.Substring(prevSpace + 1);
        if (prevTok.Contains(')') || !IsPositionToken(prevTok)) return single;
        var color = head.Substring(0, prevSpace).Trim();
        if (color.Length == 0) return single;
        return new[] { color + " " + prevTok, color + " " + lastTok };
    }

    /// <summary>True when <paramref name="token"/> is a gradient-stop POSITION: a <c>%</c>, an absolute
    /// length (px/pt/…), or an angle (deg/grad/rad/turn — conic). Not a bare number / color.</summary>
    private static bool IsPositionToken(string token)
    {
        var t = token.Trim();
        if (t.Length == 0) return false;
        if (t.EndsWith("%", StringComparison.Ordinal))
            return double.TryParse(t.AsSpan(0, t.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        if (CssLengthParsing.TryLengthPx(t, out _)) return true;
        foreach (var unit in new[] { "deg", "grad", "rad", "turn" })
            if (t.EndsWith(unit, StringComparison.OrdinalIgnoreCase)
                && double.TryParse(t.AsSpan(0, t.Length - unit.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return true;
        return false;
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

    /// <summary>Confirm <paramref name="value"/> is a SINGLE function token whose opening paren
    /// (the last char of <paramref name="prefix"/>, e.g. <c>"linear-gradient("</c>) is matched by
    /// a closing paren at the very end (only trailing whitespace after) — so a multi-layer list
    /// such as <c>linear-gradient(...), url(...)</c> is rejected rather than mis-terminated on a
    /// later layer's <c>)</c> (PR #209 Copilot). Returns the trimmed body between the outer
    /// parens. Shared with the radial parser.</summary>
    internal static bool TryExtractSingleFunctionBody(string value, string prefix, out string inner)
    {
        inner = string.Empty;
        var open = prefix.Length - 1; // index of '(' within "<name>("
        var depth = 0;
        var close = -1;
        for (var i = open; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) { close = i; break; }
            }
        }
        if (close < 0) return false; // unbalanced parens
        for (var i = close + 1; i < value.Length; i++)
            if (!char.IsWhiteSpace(value[i])) return false; // a trailing layer / extra tokens
        inner = value.Substring(prefix.Length, close - prefix.Length).Trim();
        return true;
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
