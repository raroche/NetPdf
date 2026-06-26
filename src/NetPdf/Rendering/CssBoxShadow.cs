// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;

namespace NetPdf.Rendering;

/// <summary>Phase 4 shadows — one parsed <c>box-shadow</c> layer (CSS Backgrounds &amp; Borders 3
/// §7.2): <see cref="Inset"/> + the offset / blur / spread in CSS px + the raw shadow color
/// (resolved by the painter via the shared color resolver; <see langword="null"/> = the initial
/// <c>currentColor</c>). OUTSET layers paint under the background; INSET layers (PR 1 refinements)
/// paint over it, clipped to the padding box — both sharp (native) and blurred (Skia raster).</summary>
internal readonly record struct CssBoxShadow(
    bool Inset, double OffsetXPx, double OffsetYPx, double BlurPx, double SpreadPx, string? ColorRaw);

/// <summary>Phase 4 shadows — a parser for the <c>box-shadow</c> property: a comma-separated list
/// of <c>[ inset? &amp;&amp; &lt;length&gt;{2,4} &amp;&amp; &lt;color&gt;? ]</c> layers. Offsets /
/// blur / spread are resolved as <c>px</c> + the absolute units (<c>pt/pc/in/cm/mm/Q</c>);
/// font-relative (<c>em</c>/<c>rem</c>) and percentage lengths are NOT resolved here (the whole
/// value is rejected → the caller emits <c>CSS-BOXSHADOW-UNSUPPORTED-001</c>). The <c>inset</c>
/// keyword is parsed onto <see cref="CssBoxShadow.Inset"/> (the painter renders both outset + inset).
/// Returns <see langword="null"/> for <c>none</c> / empty / any unparseable layer.</summary>
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

        foreach (var token in CssLengthParsing.SplitTopLevelSpaces(layer))
        {
            if (token.Equals("inset", StringComparison.OrdinalIgnoreCase)) { inset = true; continue; }

            if (CssLengthParsing.LooksNumeric(token))
            {
                if (!CssLengthParsing.TryLengthPx(token, out var px) || lengthCount >= 4) return false;
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
}
