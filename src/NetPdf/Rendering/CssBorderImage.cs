// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace NetPdf.Rendering;

/// <summary>Phase 4 border-image — a per-axis <c>border-image-repeat</c> mode (CSS Backgrounds &amp;
/// Borders L3 §6.3). Edge tiling (<c>repeat</c> / <c>round</c> / <c>space</c>) is honored by the painter
/// (PR — border-image completion); <c>stretch</c> is the initial.</summary>
internal enum BorderImageRepeat { Stretch, Repeat, Round, Space }

/// <summary>Phase 4 border-image — the kind of a <c>border-image-width</c> / <c>-outset</c> component.
/// <c>Multiple</c> = a unitless number × the element's border width (the §6.4/§6.5 default unit);
/// <c>LengthPx</c> = an absolute length; <c>Percent</c> = a fraction of the border-image area (width-only);
/// <c>Auto</c> = the intrinsic slice size (width-only).</summary>
internal enum BorderImageLenKind { Multiple, LengthPx, Percent, Auto }

/// <summary>One resolved <c>border-image-width</c>/<c>-outset</c> component (CSS B&amp;B L3 §6.4/§6.5).</summary>
internal readonly record struct BorderImageLen(BorderImageLenKind Kind, double Value)
{
    public static BorderImageLen Multiple(double v) => new(BorderImageLenKind.Multiple, v);
}

/// <summary>Phase 4 border-image (CSS B&amp;B L3 §6). Resolves the <c>border-image-source</c> URL, the four
/// <c>border-image-slice</c> offsets (number = image px, % = fraction; stored fraction-encoded — a negative
/// sentinel for px, resolved against the decoded image), the optional <c>fill</c> keyword, the two repeat
/// axes, and the four <c>border-image-width</c> / <c>-outset</c> components (T/R/B/L). Width defaults to
/// <c>1×</c> the border width; outset defaults to <c>0</c>.</summary>
internal sealed record CssBorderImage(
    string SourceUrl,
    double SliceTopFrac, double SliceRightFrac, double SliceBottomFrac, double SliceLeftFrac,
    bool Fill,
    BorderImageRepeat RepeatX, BorderImageRepeat RepeatY,
    BorderImageLen WidthTop, BorderImageLen WidthRight, BorderImageLen WidthBottom, BorderImageLen WidthLeft,
    BorderImageLen OutsetTop, BorderImageLen OutsetRight, BorderImageLen OutsetBottom, BorderImageLen OutsetLeft);

/// <summary>Phase 4 border-image (PR 4) — a parser for the <c>border-image</c> LONGHANDS
/// (<c>border-image-source</c> / <c>-slice</c> / <c>-repeat</c>). The <c>border-image</c> SHORTHAND is
/// expanded into these longhands by <c>BorderImageShorthandExpander</c> in the CSS preprocessor, so the
/// cascade resolves shorthand-vs-longhand by source order (PR-229 review [P2]) before the painter reads the
/// winners. Returns <see langword="null"/> when there is no usable <c>url(...)</c> source (a gradient /
/// <c>none</c> → no border-image).</summary>
internal static class CssBorderImage_Parser
{
    /// <summary>Parse from the resolved longhand winners (any may be null = the longhand's initial).</summary>
    public static CssBorderImage? TryParse(string? source, string? slice, string? repeat, string? width = null, string? outset = null)
    {
        var url = ExtractUrl(source);
        if (url is null) return null; // none / gradient / unset → no border-image (gradient diagnosed by caller)

        var fill = false;
        if (!TryResolveSlices(slice, ref fill, out var st, out var sr, out var sb, out var sl)) return null;
        ParseRepeat(repeat, out var rx, out var ry);
        // width: <length-percentage> | <number> | auto, default 1 (× border width); outset: <length> |
        // <number>, default 0. 1–4 value top/right/bottom/left shorthand (CSS B&B L3 §6.4/§6.5).
        var w = ParseSides(width, BorderImageLen.Multiple(1), allowAutoPercent: true);
        var o = ParseSides(outset, BorderImageLen.Multiple(0), allowAutoPercent: false);
        return new CssBorderImage(url, st, sr, sb, sl, fill, rx, ry,
            w[0], w[1], w[2], w[3], o[0], o[1], o[2], o[3]);
    }

    /// <summary>Parse a 1–4 value <c>border-image-width</c>/<c>-outset</c> into top/right/bottom/left.
    /// Any unparseable token falls the whole property back to <paramref name="initial"/> (the longhand's
    /// initial value) so a malformed value never throws.</summary>
    private static BorderImageLen[] ParseSides(string? raw, BorderImageLen initial, bool allowAutoPercent)
    {
        var fallback = new[] { initial, initial, initial, initial };
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var toks = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (toks.Length is < 1 or > 4) return fallback;
        var v = new BorderImageLen[4];
        for (var i = 0; i < 4; i++)
            if (!TrySide(toks[ShorthandIndex(toks.Length, i)], allowAutoPercent, out v[i])) return fallback;
        return v;
    }

    private static bool TrySide(string token, bool allowAutoPercent, out BorderImageLen len)
    {
        len = default;
        var t = token.Trim();
        if (t.Length == 0) return false;
        if (allowAutoPercent && t.Equals("auto", StringComparison.OrdinalIgnoreCase)) { len = new BorderImageLen(BorderImageLenKind.Auto, 0); return true; }
        if (t.EndsWith("%", StringComparison.Ordinal))
        {
            if (!allowAutoPercent) return false; // outset takes no percentage
            if (!double.TryParse(t.AsSpan(0, t.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) || pct < 0) return false;
            len = new BorderImageLen(BorderImageLenKind.Percent, pct / 100.0);
            return true;
        }
        if (CssLengthParsing.TryLengthPx(t, out var px)) { if (px < 0) return false; len = new BorderImageLen(BorderImageLenKind.LengthPx, px); return true; }
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n >= 0) { len = BorderImageLen.Multiple(n); return true; }
        return false;
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
