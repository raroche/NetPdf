// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace NetPdf.Rendering;

/// <summary>Phase 4 border-image (PR 4) — a per-axis <c>border-image-repeat</c> mode (CSS Backgrounds
/// &amp; Borders L3 §6.3). The first cut paints every mode as <c>Stretch</c> (the initial); the
/// non-stretch modes surface <c>CSS-BORDER-IMAGE-UNSUPPORTED-001</c> until edge tiling lands.</summary>
internal enum BorderImageRepeat { Stretch, Repeat, Round, Space }

/// <summary>Phase 4 border-image (PR 4) — a parsed <c>border-image</c> (CSS B&amp;B L3 §6). Resolves the
/// <c>border-image-source</c> URL, the four <c>border-image-slice</c> offsets (number = image px, % =
/// fraction; stored fraction-encoded — a negative sentinel for px, resolved against the decoded image), the
/// optional <c>fill</c> keyword, and the two repeat axes. <c>border-image-width</c> / <c>-outset</c> are a
/// documented follow-up (the painter uses the element's border widths, outset 0; their presence is
/// diagnosed at collection).</summary>
internal sealed record CssBorderImage(
    string SourceUrl,
    double SliceTopFrac, double SliceRightFrac, double SliceBottomFrac, double SliceLeftFrac,
    bool Fill,
    BorderImageRepeat RepeatX, BorderImageRepeat RepeatY);

/// <summary>Phase 4 border-image (PR 4) — a parser for the <c>border-image</c> LONGHANDS
/// (<c>border-image-source</c> / <c>-slice</c> / <c>-repeat</c>). The <c>border-image</c> SHORTHAND is
/// expanded into these longhands by <c>BorderImageShorthandExpander</c> in the CSS preprocessor, so the
/// cascade resolves shorthand-vs-longhand by source order (PR-229 review [P2]) before the painter reads the
/// winners. Returns <see langword="null"/> when there is no usable <c>url(...)</c> source (a gradient /
/// <c>none</c> → no border-image).</summary>
internal static class CssBorderImage_Parser
{
    /// <summary>Parse from the resolved longhand winners (any may be null = the longhand's initial).</summary>
    public static CssBorderImage? TryParse(string? source, string? slice, string? repeat)
    {
        var url = ExtractUrl(source);
        if (url is null) return null; // none / gradient / unset → no border-image (gradient diagnosed by caller)

        var fill = false;
        if (!TryResolveSlices(slice, ref fill, out var st, out var sr, out var sb, out var sl)) return null;
        ParseRepeat(repeat, out var rx, out var ry);
        return new CssBorderImage(url, st, sr, sb, sl, fill, rx, ry);
    }

    /// <summary>True when the value is a non-<c>none</c>, non-empty source that is NOT a <c>url(...)</c>
    /// (e.g. a gradient) — the caller diagnoses it as an unsupported border-image source.</summary>
    public static bool IsUnsupportedSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        var s = source.Trim();
        if (s.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
        return ExtractUrl(s) is null;
    }

    private static void ParseRepeat(string? value, out BorderImageRepeat x, out BorderImageRepeat y)
    {
        x = y = BorderImageRepeat.Stretch;
        if (string.IsNullOrWhiteSpace(value)) return;
        var toks = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (toks.Length >= 1) x = y = Keyword(toks[0]);
        if (toks.Length >= 2) y = Keyword(toks[1]);

        static BorderImageRepeat Keyword(string t) => t.ToLowerInvariant() switch
        {
            "repeat" => BorderImageRepeat.Repeat,
            "round" => BorderImageRepeat.Round,
            "space" => BorderImageRepeat.Space,
            _ => BorderImageRepeat.Stretch,
        };
    }

    /// <summary>Resolve 1–4 slice offsets (top/right/bottom/left shorthand, + an optional <c>fill</c>
    /// keyword) to image-dimension fractions. A <c>%</c> is a direct fraction; a unitless number is image
    /// pixels, encoded as a NEGATIVE sentinel <c>-(px + 1)</c> resolved against the decoded image. A null /
    /// empty value defaults to the <c>100%</c> initial.</summary>
    private static bool TryResolveSlices(string? raw, ref bool fill, out double t, out double r, out double b, out double l)
    {
        t = r = b = l = 0;
        var nums = new List<string>();
        foreach (var tok in (raw ?? "100%").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok.Equals("fill", StringComparison.OrdinalIgnoreCase)) fill = true;
            else nums.Add(tok);
        }
        if (nums.Count is < 1 or > 4) { if (nums.Count == 0) { nums.Add("100%"); } else return false; }
        var v = new double[4];
        for (var i = 0; i < 4; i++)
            if (!TrySliceValue(nums[ShorthandIndex(nums.Count, i)], out v[i])) return false;
        (t, r, b, l) = (v[0], v[1], v[2], v[3]);
        return true;
    }

    private static bool TrySliceValue(string token, out double frac)
    {
        frac = 0;
        var t = token.Trim();
        if (t.Length == 0) return false;
        if (t.EndsWith("%", StringComparison.Ordinal))
        {
            if (!double.TryParse(t.AsSpan(0, t.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                return false;
            frac = pct / 100.0;
            return true;
        }
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var px) || px < 0) return false;
        frac = -(px + 1.0); // negative sentinel = image-pixel offset (resolved against the image at paint time)
        return true;
    }

    // CSS 1-4 value shorthand (top, right, bottom, left).
    private static int ShorthandIndex(int count, int i) => count switch
    {
        1 => 0,
        2 => i % 2,
        3 => i == 3 ? 1 : i,
        _ => i,
    };

    private static string? ExtractUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var idx = value.IndexOf("url(", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var open = idx + 4;
        var close = value.IndexOf(')', open);
        if (close < 0) return null;
        var inner = value.Substring(open, close - open).Trim().Trim('"', '\'');
        return inner.Length == 0 ? null : inner;
    }
}
