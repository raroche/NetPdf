// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace NetPdf.Rendering;

/// <summary>Phase 4 clip-path (PR 3) — the basic-shape kind of a parsed <c>clip-path</c>
/// (CSS Masking L1 §3 / CSS Shapes L1). <see cref="Path"/> (an SVG path string) is parsed but
/// rendered via the raster fallback.</summary>
internal enum ClipShapeKind { Inset, Circle, Ellipse, Polygon, Path }

/// <summary>Phase 4 clip-path — a length-percentage stored as a (px, fraction) pair so the painter
/// resolves it against the box at paint time (a fraction × the box width for X / inline lengths, ×
/// the height for Y / block lengths). A <c>NaN</c> fraction marks an OMITTED circle/ellipse radius
/// (→ closest-side).</summary>
internal readonly record struct ClipLen(double Px, double Frac);

/// <summary>Phase 4 clip-path — one parsed <c>clip-path</c> basic shape; the <c>Kind</c> selects which
/// fields apply: <c>Inset</c> → <c>Edges</c> (top/right/bottom/left) + optional <c>Radii</c>;
/// <c>Circle</c> → <c>Radius</c> + center (<c>Cx</c>, <c>Cy</c>); <c>Ellipse</c> → (<c>Rx</c>,
/// <c>Ry</c>) + center; <c>Polygon</c> → <c>Points</c>; <c>Path</c> → <c>PathData</c> (raster).</summary>
internal sealed record CssClipPath(
    ClipShapeKind Kind,
    ClipLen[]? Edges = null,
    ClipLen[]? Radii = null,           // 4 corners (X==Y), optional for inset round
    ClipLen Radius = default, ClipLen Rx = default, ClipLen Ry = default,
    ClipLen Cx = default, ClipLen Cy = default,
    (ClipLen X, ClipLen Y)[]? Points = null,
    string? PathData = null);

/// <summary>Phase 4 clip-path — a parser for the <c>clip-path</c> basic shapes
/// <c>inset()</c> / <c>circle()</c> / <c>ellipse()</c> / <c>polygon()</c> / <c>path()</c>. The
/// <c>&lt;geometry-box&gt;</c> reference box keyword + <c>url(#clip)</c> SVG references are out of
/// scope (→ null). Returns <see langword="null"/> for <c>none</c> / unparseable values.</summary>
internal static class CssClipPath_Parser
{
    public static CssClipPath? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim();
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;

        var open = v.IndexOf('(');
        if (open < 0 || !v.EndsWith(")", StringComparison.Ordinal)) return null;
        var name = v.Substring(0, open).Trim().ToLowerInvariant();
        var args = v.Substring(open + 1, v.Length - open - 2).Trim();
        return name switch
        {
            "inset" => ParseInset(args),
            "circle" => ParseCircle(args),
            "ellipse" => ParseEllipse(args),
            "polygon" => ParsePolygon(args),
            "path" => ParsePath(args),
            _ => null,
        };
    }

    private static CssClipPath? ParseInset(string args)
    {
        // inset( <length-percentage>{1,4} [ round <border-radius> ]? )
        var roundIdx = args.IndexOf(" round ", StringComparison.OrdinalIgnoreCase);
        var insetPart = roundIdx >= 0 ? args.Substring(0, roundIdx) : args;
        var roundPart = roundIdx >= 0 ? args.Substring(roundIdx + 7) : null;
        var nums = SplitWs(insetPart);
        if (nums.Count is < 1 or > 4) return null;
        var e = new ClipLen[4];
        for (var i = 0; i < 4; i++)
            if (!TryLen(nums[ShorthandIndex(nums.Count, i)], out e[i])) return null;

        ClipLen[]? radii = null;
        if (roundPart is not null)
        {
            var rNums = SplitWs(roundPart);
            if (rNums.Count is < 1 or > 4) return null;
            radii = new ClipLen[4];
            for (var i = 0; i < 4; i++)
                if (!TryLen(rNums[ShorthandIndex(rNums.Count, i)], out radii[i])) return null;
        }
        return new CssClipPath(ClipShapeKind.Inset, Edges: e, Radii: radii);
    }

    // CSS 1-4 value shorthand expansion (top, right, bottom, left).
    private static int ShorthandIndex(int count, int i) => count switch
    {
        1 => 0,
        2 => i % 2,                 // top/bottom = 0, left/right = 1
        3 => i == 3 ? 1 : i,        // left = right
        _ => i,
    };

    private static CssClipPath? ParseCircle(string args)
    {
        // circle( <radius>? [ at <position> ]? )
        SplitAt(args, out var shape, out var pos);
        var radius = new ClipLen(0, double.NaN); // NaN frac = OMITTED → closest-side (painter resolves)
        if (shape.Length > 0)
        {
            var toks = SplitWs(shape);
            if (toks.Count != 1 || !TryLen(toks[0], out radius)) return null;
        }
        if (!ParsePosition(pos, out var cx, out var cy)) return null;
        return new CssClipPath(ClipShapeKind.Circle, Radius: radius, Cx: cx, Cy: cy);
    }

    private static CssClipPath? ParseEllipse(string args)
    {
        // ellipse( [ <rx> <ry> ]? [ at <position> ]? )
        SplitAt(args, out var shape, out var pos);
        var rx = new ClipLen(0, double.NaN); // OMITTED → closest-side per axis
        var ry = new ClipLen(0, double.NaN);
        if (shape.Length > 0)
        {
            var toks = SplitWs(shape);
            if (toks.Count != 2 || !TryLen(toks[0], out rx) || !TryLen(toks[1], out ry)) return null;
        }
        if (!ParsePosition(pos, out var cx, out var cy)) return null;
        return new CssClipPath(ClipShapeKind.Ellipse, Rx: rx, Ry: ry, Cx: cx, Cy: cy);
    }

    private static CssClipPath? ParsePolygon(string args)
    {
        // polygon( [<fill-rule>,]? <length-percentage> <length-percentage> [, ...]+ )
        var parts = args.Split(',');
        var start = 0;
        // An optional leading fill-rule (nonzero|evenodd) — accepted + ignored (we always use nonzero).
        var firstToks = SplitWs(parts[0]);
        if (firstToks.Count == 1 && (firstToks[0].Equals("nonzero", StringComparison.OrdinalIgnoreCase)
            || firstToks[0].Equals("evenodd", StringComparison.OrdinalIgnoreCase)))
            start = 1;
        var pts = new List<(ClipLen, ClipLen)>();
        for (var i = start; i < parts.Length; i++)
        {
            var toks = SplitWs(parts[i]);
            if (toks.Count != 2 || !TryLen(toks[0], out var x) || !TryLen(toks[1], out var y)) return null;
            pts.Add((x, y));
        }
        return pts.Count >= 3 ? new CssClipPath(ClipShapeKind.Polygon, Points: pts.ToArray()) : null;
    }

    private static CssClipPath? ParsePath(string args)
    {
        // path( [<fill-rule>,]? <string> ) — the SVG path is a string in single OR double quotes.
        var quote = args.IndexOfAny(['"', '\'']);
        if (quote < 0) return null;
        var qch = args[quote];
        var end = args.LastIndexOf(qch);
        if (end <= quote) return null;
        var data = args.Substring(quote + 1, end - quote - 1);
        return data.Length > 0 ? new CssClipPath(ClipShapeKind.Path, PathData: data) : null;
    }

    /// <summary>Split a shape's args at a leading <c>at &lt;position&gt;</c> clause.</summary>
    private static void SplitAt(string args, out string shape, out string pos)
    {
        var at = args.IndexOf("at ", StringComparison.OrdinalIgnoreCase);
        if (at < 0) { shape = args.Trim(); pos = string.Empty; return; }
        shape = args.Substring(0, at).Trim();
        pos = args.Substring(at + 3).Trim();
    }

    /// <summary>Parse a 1–2 token position (center / side keywords / percentages / lengths) into
    /// center (x, y) clip-lengths. Empty → 50% 50%.</summary>
    private static bool ParsePosition(string pos, out ClipLen cx, out ClipLen cy)
    {
        cx = new ClipLen(0, 0.5); cy = new ClipLen(0, 0.5);
        if (pos.Length == 0) return true;
        var toks = SplitWs(pos);
        if (toks.Count is 0 or > 2) return false;
        // Keyword → fraction; otherwise a length/percentage on the matching axis (X first, Y second).
        if (toks.Count == 1) return AxisPos(toks[0], ref cx, ref cy, single: true);
        return AxisPos(toks[0], ref cx, ref cy, single: false, isX: true)
            && AxisPos(toks[1], ref cx, ref cy, single: false, isX: false);
    }

    private static bool AxisPos(string tok, ref ClipLen cx, ref ClipLen cy, bool single, bool isX = true)
    {
        switch (tok.ToLowerInvariant())
        {
            case "left": cx = new ClipLen(0, 0); return true;
            case "right": cx = new ClipLen(0, 1); return true;
            case "top": cy = new ClipLen(0, 0); return true;
            case "bottom": cy = new ClipLen(0, 1); return true;
            case "center": return true;
        }
        if (!TryLen(tok, out var len)) return false;
        if (single || isX) cx = len; else cy = len;
        return true;
    }

    /// <summary>A length-percentage → (px, fraction). <c>px</c> + the absolute units resolve to px;
    /// a <c>%</c> stores the fraction (resolved against the box at paint time). em/rem reject.</summary>
    private static bool TryLen(string token, out ClipLen len)
    {
        len = default;
        var t = token.Trim();
        if (t.Length == 0) return false;
        if (t.EndsWith("%", StringComparison.Ordinal))
        {
            if (!double.TryParse(t.AsSpan(0, t.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                return false;
            len = new ClipLen(0, pct / 100.0);
            return true;
        }
        if (CssLengthParsing.TryLengthPx(t, out var px)) { len = new ClipLen(px, 0); return true; }
        return false;
    }

    private static List<string> SplitWs(string s) =>
        new(s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
