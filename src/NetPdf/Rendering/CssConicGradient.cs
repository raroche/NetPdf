// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace NetPdf.Rendering;

/// <summary>Phase 4 gradients (PR 1 refinements) — a parsed CSS <c>conic-gradient(...)</c> /
/// <c>repeating-conic-gradient(...)</c> (CSS Images L4 §3.3): the starting angle
/// (<see cref="FromAngleDeg"/>, CSS convention — 0° = "up" / 12 o'clock, increasing clockwise),
/// the center as box-relative fractions (<see cref="CenterXFraction"/> / <see cref="CenterYFraction"/>,
/// default 0.5 / 0.5), <see cref="Repeating"/>, and the angular color stops (each position is a
/// fraction of a full turn in [0, 1]; <see langword="null"/> = unpositioned → spread evenly).
/// PDF has no native conic shading, so the painter rasterizes this via Skia (a sweep gradient).</summary>
internal sealed record CssConicGradient(
    double FromAngleDeg,
    double CenterXFraction,
    double CenterYFraction,
    bool Repeating,
    IReadOnlyList<CssGradientStop> Stops);

/// <summary>Phase 4 gradients — a minimal parser for the <c>conic-gradient()</c> /
/// <c>repeating-conic-gradient()</c> form. Supports an optional
/// <c>[ from &lt;angle&gt; ]? [ at &lt;position&gt; ]?</c> prelude followed by 2+ angular color
/// stops (<c>&lt;color&gt; [ &lt;angle&gt; | &lt;percentage&gt; ]?</c>; an angle in
/// deg/grad/rad/turn or a percentage of a full turn). Double-position stops + length positions are
/// out of this first cut (→ null, the caller falls back to the background-color). The stop position
/// is normalized to a turn fraction so it composes with the shared <see cref="CssGradientStop"/>.</summary>
internal static class CssConicGradient_Parser
{
    public static CssConicGradient? TryParse(string? rawBackgroundImage)
    {
        if (string.IsNullOrWhiteSpace(rawBackgroundImage)) return null;
        var value = rawBackgroundImage.Trim();

        var repeating = false;
        const string repeatPrefix = "repeating-conic-gradient(";
        const string plainPrefix = "conic-gradient(";
        string prefix;
        if (value.StartsWith(repeatPrefix, StringComparison.OrdinalIgnoreCase)) { prefix = repeatPrefix; repeating = true; }
        else if (value.StartsWith(plainPrefix, StringComparison.OrdinalIgnoreCase)) prefix = plainPrefix;
        else return null;

        if (!CssLinearGradient_Parser.TryExtractSingleFunctionBody(value, prefix, out var inner)) return null;
        if (inner.Length == 0) return null;

        var args = CssLinearGradient_Parser.SplitTopLevelCommas(inner);
        if (args.Count < 2) return null;

        var fromAngleDeg = 0.0;
        var cx = 0.5;
        var cy = 0.5;
        var stopStart = 0;

        // The first arg is the prelude when it mentions `from <angle>` and/or `at <position>`. A
        // prelude that is clearly intended but malformed makes the WHOLE value unsupported (→ null)
        // rather than demoting it to a bad color stop (mirrors the radial parser's [P2] rule).
        switch (TryParsePrelude(args[0], ref fromAngleDeg, ref cx, ref cy))
        {
            case PreludeResult.Malformed: return null;
            case PreludeResult.Consumed: stopStart = 1; break;
            case PreludeResult.NotPrelude: break;
        }

        if (args.Count - stopStart < 2) return null;
        var stops = new List<CssGradientStop>(args.Count - stopStart);
        for (var i = stopStart; i < args.Count; i++)
        {
            if (!TryParseConicStop(args[i], out var stop)) return null;
            stops.Add(stop);
        }
        return new CssConicGradient(fromAngleDeg, cx, cy, repeating, stops);
    }

    private enum PreludeResult { NotPrelude, Consumed, Malformed }

    /// <summary>Read the optional <c>[ from &lt;angle&gt; ]? [ at &lt;position&gt; ]?</c> prelude.
    /// Either keyword marks the arg as a prelude; once marked, a bad angle or position is
    /// <see cref="PreludeResult.Malformed"/>.</summary>
    private static PreludeResult TryParsePrelude(string arg, ref double fromAngleDeg, ref double cx, ref double cy)
    {
        var t = arg.Trim().ToLowerInvariant();
        if (t.Length == 0) return PreludeResult.NotPrelude;

        var hasFrom = t.StartsWith("from ", StringComparison.Ordinal);
        var atIndex = t.IndexOf(" at ", StringComparison.Ordinal);
        // `at ` can also lead the prelude (no `from`): `conic-gradient(at 30% 70%, ...)`.
        if (!hasFrom && t.StartsWith("at ", StringComparison.Ordinal)) atIndex = 0;
        var hasAt = atIndex >= 0;
        if (!hasFrom && !hasAt) return PreludeResult.NotPrelude;

        // Split the prelude into the `from <angle>` part and the `at <position>` part.
        string fromPart, atPart;
        if (hasAt)
        {
            var atKeywordLen = atIndex == 0 ? 3 : 4; // "at " vs " at "
            fromPart = atIndex > 0 ? t.Substring(0, atIndex).Trim() : string.Empty;
            atPart = t.Substring(atIndex + atKeywordLen).Trim();
        }
        else { fromPart = t; atPart = string.Empty; }

        if (hasFrom)
        {
            if (!fromPart.StartsWith("from ", StringComparison.Ordinal)) return PreludeResult.Malformed;
            if (!TryAngleDeg(fromPart.Substring(5).Trim(), out fromAngleDeg)) return PreludeResult.Malformed;
        }
        if (hasAt && !TryParsePosition(atPart, ref cx, ref cy)) return PreludeResult.Malformed;
        return PreludeResult.Consumed;
    }

    /// <summary>Parse one angular color stop: <c>&lt;color&gt; [ &lt;angle&gt; | &lt;percentage&gt; ]?</c>.
    /// The trailing position (if present) is normalized to a turn fraction in [0, 1]; a bare color
    /// yields a null position (spread evenly later). A trailing length is unsupported (→ false).</summary>
    private static bool TryParseConicStop(string arg, out CssGradientStop stop)
    {
        stop = default;
        var t = arg.Trim();
        if (t.Length == 0) return false;

        var lastSpace = t.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            var tail = t.Substring(lastSpace + 1);
            if (!tail.Contains(')')) // not part of a function color tail like rgb(…)
            {
                // Out-of-range angular positions (e.g. -180deg, 540deg) are LEGAL and shape the
                // interpolation / repeating period (PR 226 review [P1]) — keep them raw; the painter
                // clips to [0, 1] only when building the visible sweep.
                if (tail.EndsWith("%", StringComparison.Ordinal))
                {
                    if (!double.TryParse(tail.AsSpan(0, tail.Length - 1), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var pct)) return false;
                    stop = new CssGradientStop(t.Substring(0, lastSpace).Trim(), pct / 100.0);
                    return true;
                }
                if (TryAngleDeg(tail, out var deg))
                {
                    stop = new CssGradientStop(t.Substring(0, lastSpace).Trim(), deg / 360.0);
                    return true;
                }
            }
        }
        stop = new CssGradientStop(t, null);
        return true;
    }

    /// <summary>Parse a CSS <c>&lt;angle&gt;</c> (deg/grad/rad/turn) to RAW degrees — NOT wrapped to
    /// [0, 360): a conic stop at <c>540deg</c> is 1.5 turns (out of range, kept raw for the painter's
    /// clip — PR 226 review [P1]); the <c>from</c> angle is a rotation, so its raw value is equivalent
    /// mod 360 anyway. A bare finite <c>0</c> is also accepted (angle keyword grammar).</summary>
    private static bool TryAngleDeg(string token, out double deg)
    {
        deg = 0;
        var t = token.Trim().ToLowerInvariant();
        if (t.Length == 0) return false;
        (string Unit, double Factor)[] units =
        {
            ("deg", 1.0), ("grad", 0.9), ("rad", 180.0 / Math.PI), ("turn", 360.0),
        };
        foreach (var (unit, factor) in units)
        {
            if (t.EndsWith(unit, StringComparison.Ordinal))
            {
                if (double.TryParse(t.AsSpan(0, t.Length - unit.Length), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var v))
                {
                    deg = v * factor;
                    return true;
                }
                return false;
            }
        }
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var z) && z == 0.0;
    }

    /// <summary>Parse an <c>at &lt;position&gt;</c> into box-relative center fractions. Supports
    /// <c>center</c>, side keywords (<c>left/right/top/bottom</c>), and percentages, in 1 or 2
    /// tokens (a single percentage is the horizontal position). Reused grammar from the radial
    /// parser, kept compact.</summary>
    private static bool TryParsePosition(string pos, ref double cx, ref double cy)
    {
        var tokens = pos.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is 0 or > 2) return false;

        if (tokens.Length == 1)
        {
            return Classify(tokens[0], out var v) switch
            {
                PosKind.Horizontal => Set(ref cx, v),
                PosKind.Vertical => Set(ref cy, v),
                PosKind.Center => true,
                PosKind.Offset => Set(ref cx, v),
                _ => false,
            };
        }

        var k0 = Classify(tokens[0], out var v0);
        var k1 = Classify(tokens[1], out var v1);
        if (k0 == PosKind.Invalid || k1 == PosKind.Invalid) return false;
        if (k0 != PosKind.Offset && k1 != PosKind.Offset)
        {
            if (k0 == PosKind.Horizontal && k1 == PosKind.Horizontal) return false;
            if (k0 == PosKind.Vertical && k1 == PosKind.Vertical) return false;
            if (k0 == PosKind.Horizontal) cx = v0; else if (k0 == PosKind.Vertical) cy = v0;
            if (k1 == PosKind.Horizontal) cx = v1; else if (k1 == PosKind.Vertical) cy = v1;
            return true;
        }
        if (k0 == PosKind.Vertical) return false;
        if (k1 == PosKind.Horizontal) return false;
        cx = k0 == PosKind.Center ? 0.5 : v0;
        cy = k1 == PosKind.Center ? 0.5 : v1;
        return true;

        static bool Set(ref double target, double v) { target = v; return true; }
    }

    private enum PosKind { Horizontal, Vertical, Center, Offset, Invalid }

    private static PosKind Classify(string tok, out double value)
    {
        value = 0.5;
        switch (tok.ToLowerInvariant())
        {
            case "left": value = 0.0; return PosKind.Horizontal;
            case "right": value = 1.0; return PosKind.Horizontal;
            case "top": value = 0.0; return PosKind.Vertical;
            case "bottom": value = 1.0; return PosKind.Vertical;
            case "center": value = 0.5; return PosKind.Center;
        }
        var t = tok.Trim();
        if (t.EndsWith("%", StringComparison.Ordinal)
            && double.TryParse(t.AsSpan(0, t.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
        {
            value = Math.Clamp(pct / 100.0, 0.0, 1.0);
            return PosKind.Offset;
        }
        return PosKind.Invalid;
    }
}
