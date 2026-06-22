// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace NetPdf.Rendering;

/// <summary>Phase 4 shadows — one parsed <c>box-shadow</c> layer (CSS Backgrounds &amp; Borders 3
/// §7.2): <see cref="Inset"/> + the offset / blur / spread in CSS px + the raw shadow color
/// (resolved by the painter via the shared color resolver; <see langword="null"/> = the initial
/// <c>currentColor</c>). The first cut paints OUTSET layers; <see cref="Inset"/> ones are skipped
/// with a diagnostic.</summary>
internal readonly record struct CssBoxShadow(
    bool Inset, double OffsetXPx, double OffsetYPx, double BlurPx, double SpreadPx, string? ColorRaw);

/// <summary>Phase 4 shadows — a parser for the <c>box-shadow</c> property: a comma-separated list
/// of <c>[ inset? &amp;&amp; &lt;length&gt;{2,4} &amp;&amp; &lt;color&gt;? ]</c> layers. Offsets /
/// blur / spread are resolved as <c>px</c> + the absolute units (<c>pt/pc/in/cm/mm/Q</c>);
/// font-relative (<c>em</c>/<c>rem</c>) and percentage lengths are NOT resolved here (the whole
/// value is rejected → the caller emits <c>CSS-BOXSHADOW-UNSUPPORTED-001</c>). Returns
/// <see langword="null"/> for <c>none</c> / empty / any unparseable layer.</summary>
internal static class CssBoxShadow_Parser
{
    public static IReadOnlyList<CssBoxShadow>? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim();
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;

        var layers = CssLinearGradient_Parser.SplitTopLevelCommas(v);
        var result = new List<CssBoxShadow>(layers.Count);
        foreach (var layer in layers)
        {
            if (!TryParseLayer(layer, out var shadow)) return null; // a bad layer rejects the whole value
            result.Add(shadow);
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>One layer: <c>inset?</c> (anywhere) + 2–4 lengths (offset-x, offset-y, blur?,
    /// spread?) + an optional single color token. The color (named / hex / a function like
    /// <c>rgb(…)</c>, which keeps its inner spaces because the split is paren-aware) is stored raw;
    /// a numeric-leading token is always a length, so a bad unit rejects the layer (it never falls
    /// through to "color").</summary>
    private static bool TryParseLayer(string layer, out CssBoxShadow shadow)
    {
        shadow = default;
        var inset = false;
        string? colorRaw = null;
        Span<double> lengths = stackalloc double[4];
        var lengthCount = 0;

        foreach (var token in SplitTopLevelSpaces(layer))
        {
            if (token.Equals("inset", StringComparison.OrdinalIgnoreCase)) { inset = true; continue; }

            var c0 = token[0];
            var looksNumeric = char.IsDigit(c0) || c0 is '-' or '+' or '.';
            if (looksNumeric)
            {
                if (!TryLengthPx(token, out var px) || lengthCount >= 4) return false;
                lengths[lengthCount++] = px;
                continue;
            }
            if (colorRaw is not null) return false; // a second color token
            colorRaw = token;
        }

        if (lengthCount < 2) return false; // need at least offset-x + offset-y
        var blur = lengthCount >= 3 ? lengths[2] : 0.0;
        if (blur < 0) return false; // negative blur radius is invalid
        var spread = lengthCount >= 4 ? lengths[3] : 0.0;
        shadow = new CssBoxShadow(inset, lengths[0], lengths[1], blur, spread, colorRaw);
        return true;
    }

    /// <summary>A CSS length to CSS px — <c>px</c> + the absolute units (96px = 1in). A bare
    /// <c>0</c> is zero; a font-relative / percentage / unitless-non-zero token returns false
    /// (the caller treats it as an unsupported layer).</summary>
    private static bool TryLengthPx(string token, out double px)
    {
        px = 0;
        var t = token.Trim();
        if (t.Length == 0) return false;
        if (t == "0") return true;

        // Longest units first so "mm"/"cm" win over a hypothetical "m"; all unambiguous here.
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

    /// <summary>Split a layer on spaces that are NOT inside parentheses, so a function color like
    /// <c>rgb(1, 2, 3)</c> stays one token. Trims + drops empties.</summary>
    private static List<string> SplitTopLevelSpaces(string s)
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
