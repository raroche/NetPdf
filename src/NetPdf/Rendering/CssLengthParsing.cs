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
    /// <summary>A CSS length to CSS px — <c>px</c> + the absolute units. A bare <c>0</c> is zero; a
    /// font-relative / percentage / unitless-non-zero token returns false.</summary>
    public static bool TryLengthPx(string token, out double px)
    {
        px = 0;
        var t = token.Trim();
        if (t.Length == 0) return false;
        if (t == "0") return true;

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
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    px = v * perUnitPx;
                    return true;
                }
                return false;
            }
        }
        return false; // unitless non-zero, em/rem/%, or garbage
    }

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
