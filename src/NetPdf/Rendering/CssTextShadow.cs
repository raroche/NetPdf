// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;

namespace NetPdf.Rendering;

/// <summary>Phase 4 shadows — one parsed <c>text-shadow</c> layer (CSS Text Decoration L3 §3): the
/// offset + blur in CSS px + the raw shadow color (resolved by the painter; <see langword="null"/>
/// = the initial <c>currentColor</c> = the run's text color). There is no <c>inset</c> / spread on
/// a text-shadow.</summary>
internal readonly record struct CssTextShadow(double OffsetXPx, double OffsetYPx, double BlurPx, string? ColorRaw);

/// <summary>Phase 4 shadows — a parser for the <c>text-shadow</c> property: a comma-separated list
/// of <c>[ &lt;color&gt;? &amp;&amp; &lt;length&gt;{2,3} ]</c> layers (offset-x, offset-y, optional
/// blur; an optional color). Lengths resolve as <c>px</c> + the absolute units (shared with the
/// box-shadow parser); <c>em</c>/<c>rem</c>/percentage reject the value. Returns
/// <see langword="null"/> for <c>none</c> / empty / any unparseable layer.</summary>
internal static class CssTextShadow_Parser
{
    public static IReadOnlyList<CssTextShadow>? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim();
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;

        var layers = CssLinearGradient_Parser.SplitTopLevelCommas(v);
        var result = new List<CssTextShadow>(layers.Count);
        foreach (var layer in layers)
        {
            if (!TryParseLayer(layer, out var shadow)) return null; // a bad layer rejects the value
            result.Add(shadow);
        }
        return result.Count > 0 ? result : null;
    }

    private static bool TryParseLayer(string layer, out CssTextShadow shadow)
    {
        shadow = default;
        string? colorRaw = null;
        Span<double> lengths = stackalloc double[3];
        var lengthCount = 0;

        foreach (var token in CssLengthParsing.SplitTopLevelSpaces(layer))
        {
            if (CssLengthParsing.LooksNumeric(token))
            {
                if (!CssLengthParsing.TryLengthPx(token, out var px) || lengthCount >= 3) return false;
                lengths[lengthCount++] = px;
                continue;
            }
            if (colorRaw is not null) return false; // a second color token
            colorRaw = token;
        }

        if (lengthCount < 2) return false; // need offset-x + offset-y
        var blur = lengthCount >= 3 ? lengths[2] : 0.0;
        if (blur < 0) return false;
        shadow = new CssTextShadow(lengths[0], lengths[1], blur, colorRaw);
        return true;
    }
}
