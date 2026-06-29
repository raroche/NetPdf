// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Svg;

/// <summary>The scale + translate mapping a source content box onto a viewport, resolved from a
/// <c>preserveAspectRatio</c> value. With <c>none</c> the X/Y scales differ (stretch); with <c>meet</c>/
/// <c>slice</c> they're equal and <see cref="Slice"/> indicates the content may overflow (caller clips).</summary>
internal readonly record struct SvgPar(float ScaleX, float ScaleY, float Tx, float Ty, bool Slice);

/// <summary>Parse a <c>preserveAspectRatio</c> value (<c>[defer] &lt;align&gt; [&lt;meetOrSlice&gt;]</c>, SVG
/// 1.1 §8.8) and compute the transform that maps a source box of size <c>(sw, sh)</c> into a viewport of size
/// <c>(vw, vh)</c> at the origin. <c>none</c> scales each axis independently; otherwise a uniform scale
/// (<c>meet</c> = fit inside / <c>slice</c> = cover) is aligned per the x/y MIN/MID/MAX keywords.</summary>
internal static class SvgPreserveAspectRatio
{
    public static SvgPar Compute(string? raw, double sw, double sh, double vw, double vh)
    {
        if (sw <= 0 || sh <= 0) return new SvgPar(1, 1, 0, 0, false);
        var (alignX, alignY, none, slice) = Parse(raw);
        if (none)
            return new SvgPar((float)(vw / sw), (float)(vh / sh), 0, 0, false);
        var s = slice ? Math.Max(vw / sw, vh / sh) : Math.Min(vw / sw, vh / sh);
        var tx = alignX * (vw - sw * s);
        var ty = alignY * (vh - sh * s);
        return new SvgPar((float)s, (float)s, (float)tx, (float)ty, slice);
    }

    /// <summary>Parse to (alignX, alignY) factors (0 = MIN, 0.5 = MID, 1 = MAX), plus the <c>none</c> /
    /// <c>slice</c> flags. An empty / unrecognized value is the default <c>xMidYMid meet</c>.</summary>
    public static (double AlignX, double AlignY, bool None, bool Slice) Parse(string? raw)
    {
        var v = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (v.StartsWith("defer", StringComparison.Ordinal)) v = v[5..].Trim();
        if (v.Length == 0) return (0.5, 0.5, false, false);
        var parts = v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var align = parts.Length > 0 ? parts[0] : "xmidymid";
        var slice = parts.Length > 1 && parts[1] == "slice";
        if (align == "none") return (0, 0, true, false);
        var ax = align.Contains("xmin", StringComparison.Ordinal) ? 0.0
               : align.Contains("xmax", StringComparison.Ordinal) ? 1.0 : 0.5;
        var ay = align.Contains("ymin", StringComparison.Ordinal) ? 0.0
               : align.Contains("ymax", StringComparison.Ordinal) ? 1.0 : 0.5;
        return (ax, ay, false, slice);
    }
}
