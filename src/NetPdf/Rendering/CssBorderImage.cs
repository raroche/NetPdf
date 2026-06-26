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

/// <summary>Phase 4 border-image (PR 4) — a parsed <c>border-image</c> (CSS B&amp;B L3 §6). The first cut
/// resolves the <c>border-image-source</c> URL, the four <c>border-image-slice</c> offsets (number = image
/// px, % = fraction; stored as a fraction-of-image-dimension in <see cref="SliceTopFrac"/> etc.), the
/// optional <c>fill</c> keyword (paint the middle region), and the two repeat axes. <c>border-image-width</c>
/// / <c>-outset</c> are a documented follow-up (the painter uses the element's border widths, outset 0).</summary>
internal sealed record CssBorderImage(
    string SourceUrl,
    double SliceTopFrac, double SliceRightFrac, double SliceBottomFrac, double SliceLeftFrac,
    bool Fill,
    BorderImageRepeat RepeatX, BorderImageRepeat RepeatY);

/// <summary>Phase 4 border-image (PR 4) — a parser for the <c>border-image</c> shorthand + its longhands
/// (<c>border-image-source</c> / <c>-slice</c> / <c>-repeat</c>). Returns <see langword="null"/> when there
/// is no usable <c>url(...)</c> source (a gradient source / <c>none</c> → no border-image). The slice
/// offsets are resolved to image-dimension fractions; a number is taken as a fraction of the SHORTER axis
/// is NOT done — numbers are image pixels resolved by the painter against the decoded image, so the parser
/// stores RAW slice values + a per-value "is-percent" flag folded into the fraction at paint time. To keep
/// the record simple the parser resolves percentages here and leaves numbers as a NEGATIVE sentinel encoding
/// (px = -(value+1)) the painter decodes against the image dimensions.</summary>
internal static class CssBorderImage_Parser
{
    /// <summary>Parse from the resolved longhand winners (any may be null/empty). <paramref name="shorthand"/>
    /// is the <c>border-image</c> shorthand value (parsed first; the explicit longhands override).</summary>
    public static CssBorderImage? TryParse(
        string? shorthand, string? source, string? slice, string? repeat)
    {
        // Start from the shorthand, then let explicit longhands win.
        string? src = null;
        var sliceRaw = "100%";
        var fill = false;
        var repeatRaw = "stretch";

        if (!string.IsNullOrWhiteSpace(shorthand))
            ParseShorthand(shorthand!, ref src, ref sliceRaw, ref fill, ref repeatRaw);

        if (!string.IsNullOrWhiteSpace(source)) src = ExtractUrl(source!);
        if (!string.IsNullOrWhiteSpace(slice)) ParseSlice(slice!, ref sliceRaw, ref fill);
        if (!string.IsNullOrWhiteSpace(repeat)) repeatRaw = repeat!.Trim();

        if (string.IsNullOrWhiteSpace(src)) return null; // no usable url() source

        if (!TryResolveSlices(sliceRaw, out var st, out var sr, out var sb, out var sl)) return null;
        ParseRepeat(repeatRaw, out var rx, out var ry);
        return new CssBorderImage(src!, st, sr, sb, sl, fill, rx, ry);
    }

    /// <summary>border-image shorthand: <c>&lt;source&gt; || &lt;slice&gt; [ / &lt;width&gt; [ / &lt;outset&gt; ]]?
    /// || &lt;repeat&gt;</c>. The first cut reads the url() source, the slice list (left of the first <c>/</c>,
    /// minus a trailing url()/repeat token), the <c>fill</c> keyword, and the repeat keywords. width / outset
    /// (the <c>/</c>-separated groups) are skipped (the painter uses the border widths).</summary>
    private static void ParseShorthand(string value, ref string? src, ref string sliceRaw, ref bool fill, ref string repeatRaw)
    {
        // Pull the url() first so its internal spaces/slashes don't confuse tokenization.
        var url = ExtractUrl(value);
        if (url is not null) src = url;
        var rest = RemoveUrl(value);

        // Drop the width / outset groups (everything from the first top-level '/').
        var slashIdx = rest.IndexOf('/');
        var sliceAndRepeat = slashIdx >= 0 ? rest.Substring(0, slashIdx) : rest;

        var toks = new List<string>(sliceAndRepeat.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var sliceToks = new List<string>();
        var repeatToks = new List<string>();
        foreach (var t in toks)
        {
            var lt = t.ToLowerInvariant();
            if (lt is "stretch" or "repeat" or "round" or "space") repeatToks.Add(lt);
            else if (lt == "fill") fill = true;
            else sliceToks.Add(t);
        }
        if (sliceToks.Count > 0) sliceRaw = string.Join(' ', sliceToks);
        if (repeatToks.Count > 0) repeatRaw = string.Join(' ', repeatToks);
    }

    private static void ParseSlice(string value, ref string sliceRaw, ref bool fill)
    {
        var nums = new List<string>();
        foreach (var t in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (t.Equals("fill", StringComparison.OrdinalIgnoreCase)) fill = true;
            else nums.Add(t);
        }
        if (nums.Count > 0) sliceRaw = string.Join(' ', nums);
    }

    private static void ParseRepeat(string value, out BorderImageRepeat x, out BorderImageRepeat y)
    {
        var toks = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        x = y = BorderImageRepeat.Stretch;
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

    /// <summary>Resolve 1–4 slice offsets (top/right/bottom/left shorthand) to image-dimension fractions.
    /// A <c>%</c> is a direct fraction; a unitless number is image pixels, encoded as a NEGATIVE sentinel
    /// <c>-(px + 1)</c> the painter resolves against the decoded image dimensions (so a 30-px slice of a
    /// 90-px image becomes ⅓). Clamped to [0, 1] after resolution.</summary>
    private static bool TryResolveSlices(string raw, out double t, out double r, out double b, out double l)
    {
        t = r = b = l = 0;
        var toks = new List<string>(raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (toks.Count is < 1 or > 4) return false;
        var v = new double[4];
        for (var i = 0; i < 4; i++)
            if (!TrySliceValue(toks[ShorthandIndex(toks.Count, i)], out v[i])) return false;
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

    private static string? ExtractUrl(string value)
    {
        var idx = value.IndexOf("url(", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var open = idx + 4;
        var close = value.IndexOf(')', open);
        if (close < 0) return null;
        return value.Substring(open, close - open).Trim().Trim('"', '\'');
    }

    private static string RemoveUrl(string value)
    {
        var idx = value.IndexOf("url(", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return value;
        var close = value.IndexOf(')', idx + 4);
        if (close < 0) return value;
        return (value.Substring(0, idx) + " " + value.Substring(close + 1)).Trim();
    }
}
