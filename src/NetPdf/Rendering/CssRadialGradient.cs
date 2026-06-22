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
        // A SINGLE radial-gradient() layer only — the same paren-balance guard as the linear
        // parser, so a multi-layer value (`radial-gradient(...), url(...)`) is rejected instead
        // of mis-terminating on a later layer's `)` (PR #209 Copilot).
        const string prefix = "radial-gradient(";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        if (!CssLinearGradient_Parser.TryExtractSingleFunctionBody(value, prefix, out var inner)) return null;
        if (inner.Length == 0) return null;

        var args = CssLinearGradient_Parser.SplitTopLevelCommas(inner);
        if (args.Count < 2) return null;

        var isCircle = false;
        var extent = RadialExtent.FarthestCorner;
        var cx = 0.5;
        var cy = 0.5;
        var stopStart = 0;

        // The first arg is a prelude when it is NOT a color stop (it mentions a shape, an
        // extent keyword, or `at <position>`). A prelude that is clearly intended (it has
        // `at ...` or a shape/extent keyword) but malformed makes the WHOLE value unsupported
        // (→ null, the bg-color shows) rather than silently centering on a wrong axis or
        // demoting the bad prelude to a color stop (PR #209 review [P2]).
        switch (TryParsePrelude(args[0], ref isCircle, ref extent, ref cx, ref cy))
        {
            case PreludeResult.Malformed: return null;
            case PreludeResult.Consumed: stopStart = 1; break;
            case PreludeResult.NotPrelude: break; // args[0] is the first color stop
        }

        if (args.Count - stopStart < 2) return null;
        var stops = new List<CssGradientStop>(args.Count - stopStart);
        for (var i = stopStart; i < args.Count; i++)
        {
            if (!CssLinearGradient_Parser.TryParseStop(args[i], out var stop)) return null;
            stops.Add(stop);
        }
        return new CssRadialGradient(isCircle, extent, cx, cy, stops);
    }

    /// <summary>The outcome of reading the first gradient arg as a prelude (PR #209 review [P2]):
    /// <see cref="Consumed"/> = a valid <c>[shape || extent] [at &lt;position&gt;]?</c> prelude;
    /// <see cref="NotPrelude"/> = a plain color stop (parse it as the first stop);
    /// <see cref="Malformed"/> = a clear-but-invalid prelude (bad position or unknown shape token)
    /// → the whole gradient is unsupported.</summary>
    private enum PreludeResult { NotPrelude, Consumed, Malformed }

    /// <summary>Try to read <paramref name="arg"/> as the gradient prelude. A leading
    /// <c>at &lt;position&gt;</c> or a recognized shape/extent keyword marks it as a prelude;
    /// once marked, an invalid position or an unknown shape/extent token is
    /// <see cref="PreludeResult.Malformed"/> (not a silent demotion to a color stop).</summary>
    private static PreludeResult TryParsePrelude(string arg, ref bool isCircle, ref RadialExtent extent,
        ref double cx, ref double cy)
    {
        var t = arg.Trim().ToLowerInvariant();
        if (t.Length == 0) return PreludeResult.NotPrelude;

        string shapeExtent = t;
        var atIndex = t.IndexOf("at ", StringComparison.Ordinal);
        var hasAt = atIndex >= 0;
        if (hasAt)
        {
            shapeExtent = atIndex > 0 ? t.Substring(0, atIndex).Trim() : string.Empty;
            var posPart = t.Substring(atIndex + 3).Trim();
            // `at ...` makes this unambiguously a prelude — a position that doesn't parse makes
            // the whole gradient unsupported, NOT a fallback color stop.
            if (!TryParsePosition(posPart, ref cx, ref cy)) return PreludeResult.Malformed;
        }

        foreach (var tok in shapeExtent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (tok)
            {
                case "circle": isCircle = true; break;
                case "ellipse": isCircle = false; break;
                case "closest-side": extent = RadialExtent.ClosestSide; break;
                case "closest-corner": extent = RadialExtent.ClosestCorner; break;
                case "farthest-side": extent = RadialExtent.FarthestSide; break;
                case "farthest-corner": extent = RadialExtent.FarthestCorner; break;
                // An unrecognized shape/extent token: malformed if we already committed to a
                // prelude (it had `at ...`); otherwise this whole arg is just a color stop.
                default: return hasAt ? PreludeResult.Malformed : PreludeResult.NotPrelude;
            }
        }
        // A prelude was recognized when there was an `at` OR ≥1 shape/extent token.
        return hasAt || shapeExtent.Length > 0 ? PreludeResult.Consumed : PreludeResult.NotPrelude;
    }

    /// <summary>The axis a position token belongs to (CSS Backgrounds §3.6 / CSS Values
    /// position grammar): a horizontal edge (<c>left</c>/<c>right</c>), a vertical edge
    /// (<c>top</c>/<c>bottom</c>), <c>center</c> (either axis), an <c>&lt;percentage&gt;</c>
    /// offset (either axis, by position), or an unrecognized token.</summary>
    private enum PosKind { Horizontal, Vertical, Center, Offset, Invalid }

    private static PosKind ClassifyPosToken(string tok, out double value)
    {
        value = 0.5;
        switch (tok)
        {
            case "left": value = 0.0; return PosKind.Horizontal;
            case "right": value = 1.0; return PosKind.Horizontal;
            case "top": value = 0.0; return PosKind.Vertical;
            case "bottom": value = 1.0; return PosKind.Vertical;
            case "center": value = 0.5; return PosKind.Center;
            default: return TryPercent(tok, out value) ? PosKind.Offset : PosKind.Invalid;
        }
    }

    /// <summary>Parse an <c>at &lt;position&gt;</c> value into box-relative center fractions
    /// (PR #209 review [P2]). A single token sets its named axis; two tokens are classified per
    /// the CSS position grammar — two unambiguous keywords may appear in EITHER order, but the
    /// moment one component is a percentage the order is fixed (first = horizontal, second =
    /// vertical), and a duplicate-axis pair (<c>left right</c>, <c>top bottom</c>) or a misordered
    /// keyword (<c>25% left</c>, <c>top 25%</c>) is REJECTED rather than silently mis-centered.</summary>
    private static bool TryParsePosition(string pos, ref double cx, ref double cy)
    {
        var tokens = pos.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens.Length > 2) return false;

        if (tokens.Length == 1)
        {
            // One token sets its named axis (the other stays center); a lone percentage is the
            // horizontal position (§3.6 — `at 25%` ≡ `at 25% center`).
            return ClassifyPosToken(tokens[0], out var v) switch
            {
                PosKind.Horizontal => Set(ref cx, v),
                PosKind.Vertical => Set(ref cy, v),
                PosKind.Center => true,
                PosKind.Offset => Set(ref cx, v),
                _ => false,
            };
        }

        var k0 = ClassifyPosToken(tokens[0], out var v0);
        var k1 = ClassifyPosToken(tokens[1], out var v1);
        if (k0 == PosKind.Invalid || k1 == PosKind.Invalid) return false;

        if (k0 != PosKind.Offset && k1 != PosKind.Offset)
        {
            // Both keywords — either order, but two edges on the SAME axis are invalid.
            if (k0 == PosKind.Horizontal && k1 == PosKind.Horizontal) return false;
            if (k0 == PosKind.Vertical && k1 == PosKind.Vertical) return false;
            if (k0 == PosKind.Horizontal) cx = v0; else if (k0 == PosKind.Vertical) cy = v0;
            if (k1 == PosKind.Horizontal) cx = v1; else if (k1 == PosKind.Vertical) cy = v1;
            return true; // any `center` leaves its axis at the 0.5 default
        }

        // At least one percentage ⇒ strict positional order: [0] horizontal, [1] vertical. A
        // keyword that names the WRONG axis for its slot rejects.
        if (k0 == PosKind.Vertical) return false;   // e.g. `top 25%`
        if (k1 == PosKind.Horizontal) return false; // e.g. `25% left`
        cx = k0 == PosKind.Center ? 0.5 : v0;
        cy = k1 == PosKind.Center ? 0.5 : v1;
        return true;

        static bool Set(ref double target, double v) { target = v; return true; }
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
