// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace NetPdf.Rendering;

/// <summary>Phase 4 — shared absolute-length + token-splitting helpers for the shadow / transform
/// parsers (box-shadow, text-shadow, transform). Resolves <c>px</c> + the absolute CSS units
/// (96px = 1in); font-relative (<c>em</c>/<c>rem</c>) and percentage lengths are NOT resolved here
/// (the caller rejects the value), since the parser has no computed font-size / containing block.</summary>
internal static class CssLengthParsing
{
    /// <summary>A CSS length to CSS px — <c>px</c> + the absolute units. A unitless token is valid
    /// ONLY when it is a finite ZERO (<c>0</c> / <c>0.0</c> / <c>+0</c> / <c>-0</c>); a unitless
    /// non-zero, a font-relative / percentage unit, or a non-finite value (<c>NaNpx</c>,
    /// <c>Infinitypx</c>, an overflowing exponent) returns false (PR #210 review [P2]/[P3]).</summary>
    public static bool TryLengthPx(string token, out double px)
    {
        px = 0;
        var t = token.Trim();
        if (t.Length == 0) return false;

        ReadOnlySpan<(string Unit, double PerUnitPx)> units =
        [
            ("px", 1.0), ("pt", 96.0 / 72.0), ("pc", 16.0), ("in", 96.0),
            ("cm", 96.0 / 2.54), ("mm", 96.0 / 25.4), ("q", 96.0 / 101.6),
        ];
        var lower = t.ToLowerInvariant();
        foreach (var (unit, perUnitPx) in units)
        {
            if (lower.EndsWith(unit, StringComparison.Ordinal))
            {
                var num = lower.AsSpan(0, lower.Length - unit.Length);
                if (TryFinite(num, out var v))
                {
                    px = v * perUnitPx;
                    return double.IsFinite(px);
                }
                return false;
            }
        }
        // Unitless: a length needs a unit EXCEPT for a finite zero (CSS Values §6).
        return TryFinite(lower, out var z) && z == 0.0;
    }

    /// <summary>A CSS length to CSS px including the font-relative units <c>em</c> / <c>rem</c> (resolved
    /// against <paramref name="emPx"/> / <paramref name="remPx"/>), plus everything <see cref="TryLengthPx"/>
    /// accepts (<c>px</c> + absolute units + a finite zero). A percentage is NOT a length here (returns
    /// false). When the font context is <see cref="double.NaN"/>, an <c>em</c> / <c>rem</c> token returns
    /// false. Used for transform offsets / <c>transform-origin</c> lengths (Phase 4).</summary>
    public static bool TryLengthPxEmRem(string token, double emPx, double remPx, out double px)
    {
        px = 0;
        var t = token.Trim();
        if (t.Length == 0) return false;
        var lower = t.ToLowerInvariant();
        if (lower.EndsWith("rem", StringComparison.Ordinal))
            return double.IsFinite(remPx)
                && TryFinite(lower.AsSpan(0, lower.Length - 3), out var rv) && SetFinite(rv * remPx, out px);
        if (lower.EndsWith("em", StringComparison.Ordinal))
            return double.IsFinite(emPx)
                && TryFinite(lower.AsSpan(0, lower.Length - 2), out var ev) && SetFinite(ev * emPx, out px);
        return TryLengthPx(t, out px);
    }

    private static bool SetFinite(double value, out double px)
    {
        px = value;
        return double.IsFinite(px);
    }

    /// <summary>Parse <paramref name="span"/> as an invariant float AND require it be finite —
    /// rejecting <c>NaN</c> / <c>Infinity</c> / overflowing exponents that would otherwise reach
    /// PDF emission and throw (PR #210 review [P2]).</summary>
    public static bool TryFinite(ReadOnlySpan<char> span, out double value) =>
        double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    /// <summary>True when a token starts like a number (so it must be a length, never a color) —
    /// a digit, sign, or decimal point.</summary>
    public static bool LooksNumeric(string token) =>
        token.Length > 0 && (char.IsDigit(token[0]) || token[0] is '-' or '+' or '.');

    /// <summary>Split a value on whitespace that is NOT inside parentheses, so a function token like
    /// <c>rgb(1, 2, 3)</c> or <c>translate(1px, 2px)</c> stays whole. Trims + drops empties.</summary>
    public static List<string> SplitTopLevelSpaces(string s)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '(') depth++;
            else if (c == ')') { if (depth > 0) depth--; }
            else if (char.IsWhiteSpace(c) && depth == 0)
            {
                if (i > start) parts.Add(s.Substring(start, i - start));
                start = i + 1;
            }
        }
        if (s.Length > start) parts.Add(s.Substring(start));
        return parts;
    }
}
