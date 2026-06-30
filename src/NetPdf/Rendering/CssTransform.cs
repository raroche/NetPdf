// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using NetPdf.Pdf;

namespace NetPdf.Rendering;

/// <summary>Phase 4 transforms — a resolved CSS <c>transform</c> as a 2D affine matrix in CSS px
/// space (the <c>matrix(a, b, c, d, e, f)</c> form every 2D function reduces to: x' = a·x + c·y + e,
/// y' = b·x + d·y + f) plus <see cref="Had3D"/> (a 3D function was flattened — the caller emits
/// CSS-TRANSFORM-3D-UNSUPPORTED-001). The translation e/f are split into an absolute px part plus
/// coefficients of the box border-box <c>width</c>/<c>height</c>, so a <c>translate(%)</c> resolves
/// against the box at paint time (CSS Transforms L1 §6 — a translate percentage resolves against the
/// box dimensions): <c>e = EPx + EW·width + EH·height</c>, <c>f = FPx + FW·width + FH·height</c>.</summary>
internal sealed record CssTransform(
    double A, double B, double C, double D,
    double EPx, double EW, double EH,
    double FPx, double FW, double FH,
    bool Had3D)
{
    /// <summary>Back-compat constructor for an absolute (px-only) translation — no percentage parts.</summary>
    public CssTransform(double a, double b, double c, double d, double e, double f, bool had3D)
        : this(a, b, c, d, e, 0, 0, f, 0, 0, had3D) { }

    /// <summary>The absolute (px) X translation — the percentage-free part (for px-only transforms,
    /// the whole translation).</summary>
    public double E => EPx;

    /// <summary>The absolute (px) Y translation.</summary>
    public double F => FPx;

    public bool IsIdentity => A == 1 && B == 0 && C == 0 && D == 1
        && EPx == 0 && EW == 0 && EH == 0 && FPx == 0 && FW == 0 && FH == 0;
}

/// <summary>Phase 4 transforms — a resolved <c>transform-origin</c>: per axis a percentage
/// FRACTION of the box (keywords / <c>%</c>) plus an absolute px offset, so the painter computes
/// the origin point as <c>boxOrigin + Fraction·boxExtent + Px</c>. Default is <c>50% 50%</c>.</summary>
internal readonly record struct TransformOrigin(double XFraction, double XPx, double YFraction, double YPx)
{
    public static readonly TransformOrigin Center = new(0.5, 0.0, 0.5, 0.0);
}

/// <summary>Phase 4 transforms — parse the CSS <c>transform</c> list into one composed 2D matrix.
/// Supports <c>translate/translateX/translateY</c>, <c>scale/scaleX/scaleY</c>, <c>rotate</c>,
/// <c>skew/skewX/skewY</c>, and <c>matrix()</c>. 3D functions are flattened: the 2D-meaningful part
/// of <c>translate3d</c>/<c>scale3d</c> is kept and <c>rotateZ</c> is a plain 2D rotation, while
/// genuinely-3D functions (<c>rotateX/Y</c>, <c>translateZ</c>, <c>perspective</c>, <c>rotate3d</c>,
/// <c>matrix3d</c>) flatten to identity — all set <see cref="CssTransform.Had3D"/>. Returns
/// <see langword="null"/> for <c>none</c> / empty / any unparseable function.</summary>
internal static class CssTransform_Parser
{
    /// <summary>Parse a CSS <c>transform</c>. <paramref name="emPx"/> / <paramref name="remPx"/> are the
    /// element / root font-sizes used to resolve <c>em</c> / <c>rem</c> translate offsets; pass
    /// <see cref="double.NaN"/> (the default) when no font context is available, in which case an
    /// <c>em</c> / <c>rem</c> offset makes the value unsupported. A <c>%</c> translate is always parseable
    /// — it is carried as a width/height fraction and resolved against the box at paint time.</summary>
    public static CssTransform? TryParse(string? raw, double emPx = double.NaN, double remPx = double.NaN)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim();
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;

        var functions = CssLengthParsing.SplitTopLevelSpaces(v); // each "name(args)" stays whole
        if (functions.Count == 0) return null;

        // Accumulate M = M · m in list order (the first-listed function is outermost — applied last).
        double a = 1, b = 0, c = 0, d = 1, ePx = 0, eW = 0, eH = 0, fPx = 0, fW = 0, fH = 0;
        var had3D = false;
        foreach (var fn in functions)
        {
            if (!TryParseFunction(fn, emPx, remPx, out var m, out var fn3D)) return null;
            had3D |= fn3D;
            if (m is { } mm)
                (a, b, c, d, ePx, eW, eH, fPx, fW, fH) = Multiply(
                    a, b, c, d, ePx, eW, eH, fPx, fW, fH,
                    mm.A, mm.B, mm.C, mm.D, mm.EPx, mm.EW, mm.EH, mm.FPx, mm.FW, mm.FH);
        }
        // A composed matrix can blow up to non-finite (e.g. skew near 90° → tan → huge) — reject it
        // so it never reaches PDF emission (PR #210 review [P2]).
        if (!(double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c) && double.IsFinite(d)
            && double.IsFinite(ePx) && double.IsFinite(eW) && double.IsFinite(eH)
            && double.IsFinite(fPx) && double.IsFinite(fW) && double.IsFinite(fH))) return null;
        return new CssTransform(a, b, c, d, ePx, eW, eH, fPx, fW, fH, had3D);
    }

    private readonly record struct M(
        double A, double B, double C, double D,
        double EPx, double EW, double EH, double FPx, double FW, double FH);

    private static (double, double, double, double, double, double, double, double, double, double) Multiply(
        double a1, double b1, double c1, double d1, double ePx1, double eW1, double eH1, double fPx1, double fW1, double fH1,
        double a2, double b2, double c2, double d2, double ePx2, double eW2, double eH2, double fPx2, double fW2, double fH2) =>
        (a1 * a2 + c1 * b2, b1 * a2 + d1 * b2,
         a1 * c2 + c1 * d2, b1 * c2 + d1 * d2,
         a1 * ePx2 + c1 * fPx2 + ePx1, a1 * eW2 + c1 * fW2 + eW1, a1 * eH2 + c1 * fH2 + eH1,
         b1 * ePx2 + d1 * fPx2 + fPx1, b1 * eW2 + d1 * fW2 + fW1, b1 * eH2 + d1 * fH2 + fH1);

    private static bool TryParseFunction(string fn, double emPx, double remPx, out M? matrix, out bool is3D)
    {
        matrix = null;
        is3D = false;
        var open = fn.IndexOf('(');
        if (open < 0 || !fn.EndsWith(")", StringComparison.Ordinal)) return false;
        var name = fn.Substring(0, open).Trim().ToLowerInvariant();
        var argsText = fn.Substring(open + 1, fn.Length - open - 2);
        var args = SplitArgs(argsText);

        switch (name)
        {
            case "translate":
                if (!TransLengths(args, emPx, remPx, 1, 2, out var tr)) return false;
                return SetM(out matrix, 1, 0, 0, 1,
                    tr[0].Px, tr[0].Frac, 0,
                    tr.Length > 1 ? tr[1].Px : 0, 0, tr.Length > 1 ? tr[1].Frac : 0);
            case "translatex":
                if (!TransLengths(args, emPx, remPx, 1, 1, out var trx)) return false;
                return SetM(out matrix, 1, 0, 0, 1, trx[0].Px, trx[0].Frac, 0, 0, 0, 0);
            case "translatey":
                if (!TransLengths(args, emPx, remPx, 1, 1, out var trY)) return false;
                return SetM(out matrix, 1, 0, 0, 1, 0, 0, 0, trY[0].Px, 0, trY[0].Frac);
            case "scale":
                return Numbers(args, 1, 2, out var sc)
                    && Set(out matrix, sc[0], 0, 0, sc.Length > 1 ? sc[1] : sc[0], 0, 0);
            case "scalex":
                return Numbers(args, 1, 1, out var scx) && Set(out matrix, scx[0], 0, 0, 1, 0, 0);
            case "scaley":
                return Numbers(args, 1, 1, out var scy) && Set(out matrix, 1, 0, 0, scy[0], 0, 0);
            case "rotate":
            case "rotatez": // a 2D rotation — not a flattened 3D function
                return Angle(args, out var rot) && Set(out matrix,
                    Math.Cos(rot), Math.Sin(rot), -Math.Sin(rot), Math.Cos(rot), 0, 0);
            case "skew":
                if (!Angles(args, 1, 2, out var sk)) return false;
                return Set(out matrix, 1, Math.Tan(sk.Length > 1 ? sk[1] : 0), Math.Tan(sk[0]), 1, 0, 0);
            case "skewx":
                return Angles(args, 1, 1, out var skx) && Set(out matrix, 1, 0, Math.Tan(skx[0]), 1, 0, 0);
            case "skewy":
                return Angles(args, 1, 1, out var sky) && Set(out matrix, 1, Math.Tan(sky[0]), 0, 1, 0, 0);
            case "matrix":
                return Numbers(args, 6, 6, out var mx)
                    && Set(out matrix, mx[0], mx[1], mx[2], mx[3], mx[4], mx[5]);

            // 3D functions — flatten (keep the 2D-meaningful part) + flag for the diagnostic.
            case "translate3d":
                is3D = true;
                if (!TransLengths(args, emPx, remPx, 3, 3, out var t3)) return false;
                return SetM(out matrix, 1, 0, 0, 1, t3[0].Px, t3[0].Frac, 0, t3[1].Px, 0, t3[1].Frac);
            case "scale3d":
                is3D = true;
                return Numbers(args, 3, 3, out var s3) && Set(out matrix, s3[0], 0, 0, s3[1], 0, 0);
            case "translatez":
            case "scalez":
            case "rotatex":
            case "rotatey":
            case "perspective":
            case "rotate3d":
            case "matrix3d":
                is3D = true;       // genuinely 3D → flatten to identity (matrix stays null)
                return true;
            default:
                return false;      // an unrecognized function makes the whole value unsupported
        }
    }

    private static bool SetM(out M? matrix, double a, double b, double c, double d,
        double ePx, double eW, double eH, double fPx, double fW, double fH)
    {
        matrix = new M(a, b, c, d, ePx, eW, eH, fPx, fW, fH);
        return true;
    }

    /// <summary>A px-only (percentage-free) function matrix — scale/rotate/skew/matrix.</summary>
    private static bool Set(out M? matrix, double a, double b, double c, double d, double e, double f)
        => SetM(out matrix, a, b, c, d, e, 0, 0, f, 0, 0);

    /// <summary>Parse 1–<paramref name="max"/> translate-length arguments, each resolved to an absolute px
    /// part plus a percentage fraction (% of the axis dimension, applied later).</summary>
    private static bool TransLengths(IReadOnlyList<string> args, double emPx, double remPx, int min, int max,
        out (double Px, double Frac)[] vals)
    {
        vals = [];
        if (args.Count < min || args.Count > max) return false;
        var result = new (double, double)[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            if (!TryTransformLen(args[i], emPx, remPx, out var px, out var frac)) return false;
            result[i] = (px, frac);
        }
        vals = result;
        return true;
    }

    /// <summary>Resolve one translate length to (absolute px, percentage fraction). <c>px</c> + the
    /// absolute units and a finite zero resolve to px; <c>em</c> / <c>rem</c> fold into px using the font
    /// context (unresolvable when that context is <see cref="double.NaN"/>); <c>%</c> becomes a fraction
    /// (value / 100) resolved against the axis dimension at paint time. Exactly one of px / frac is
    /// non-zero.</summary>
    private static bool TryTransformLen(string token, double emPx, double remPx, out double px, out double frac)
    {
        px = 0;
        frac = 0;
        var t = token.Trim();
        if (t.Length == 0) return false;
        var lower = t.ToLowerInvariant();
        if (lower.EndsWith("%", StringComparison.Ordinal))
        {
            if (!CssLengthParsing.TryFinite(lower.AsSpan(0, lower.Length - 1), out frac)) return false;
            frac /= 100.0;
            return true;
        }
        if (lower.EndsWith("rem", StringComparison.Ordinal))
        {
            if (!double.IsFinite(remPx) || !CssLengthParsing.TryFinite(lower.AsSpan(0, lower.Length - 3), out var rv)) return false;
            px = rv * remPx;
            return double.IsFinite(px);
        }
        if (lower.EndsWith("em", StringComparison.Ordinal))
        {
            if (!double.IsFinite(emPx) || !CssLengthParsing.TryFinite(lower.AsSpan(0, lower.Length - 2), out var ev)) return false;
            px = ev * emPx;
            return double.IsFinite(px);
        }
        return CssLengthParsing.TryLengthPx(t, out px);
    }

    private static bool Numbers(IReadOnlyList<string> args, int min, int max, out double[] values)
    {
        values = [];
        if (args.Count < min || args.Count > max) return false;
        var result = new double[args.Count];
        for (var i = 0; i < args.Count; i++)
            if (!CssLengthParsing.TryFinite(args[i].Trim(), out result[i])) // non-finite rejected (review [P2])
                return false;
        values = result;
        return true;
    }

    private static bool Angle(IReadOnlyList<string> args, out double radians)
    {
        radians = 0;
        return args.Count == 1 && TryAngleRad(args[0], out radians);
    }

    private static bool Angles(IReadOnlyList<string> args, int min, int max, out double[] radians)
    {
        radians = [];
        if (args.Count < min || args.Count > max) return false;
        var result = new double[args.Count];
        for (var i = 0; i < args.Count; i++)
            if (!TryAngleRad(args[i], out result[i])) return false;
        radians = result;
        return true;
    }

    private static bool TryAngleRad(string token, out double radians)
    {
        radians = 0;
        var t = token.Trim().ToLowerInvariant();
        if (t.Length == 0) return false;
        (string Unit, double ToRad)[] units =
        {
            ("deg", Math.PI / 180.0), ("grad", Math.PI / 200.0), ("rad", 1.0), ("turn", 2.0 * Math.PI),
        };
        foreach (var (unit, toRad) in units)
        {
            if (t.EndsWith(unit, StringComparison.Ordinal))
            {
                if (CssLengthParsing.TryFinite(t.AsSpan(0, t.Length - unit.Length), out var v))
                {
                    radians = v * toRad;
                    return double.IsFinite(radians);
                }
                return false;
            }
        }
        // Unitless is valid ONLY as a finite zero (a bare angle needs a unit otherwise) — PR #210 [P3].
        return CssLengthParsing.TryFinite(t, out var z) && z == 0.0;
    }

    /// <summary>Split a function's argument list on commas (CSS transforms use commas between args;
    /// a legacy space separator is normalized by also splitting whitespace).</summary>
    private static List<string> SplitArgs(string argsText)
    {
        var parts = new List<string>();
        foreach (var piece in argsText.Split(','))
            foreach (var tok in CssLengthParsing.SplitTopLevelSpaces(piece))
                parts.Add(tok);
        return parts;
    }

    /// <summary>Phase 4 transforms — the PDF <c>cm</c> matrix (a, b, c, d, e, f) that applies the CSS
    /// transform about <paramref name="origin"/> to already-emitted PDF-space ops. Composes the CSS
    /// transform with the CSS-px→PDF-pt y-flip: cm = F ∘ T_css ∘ F where F is the page y-flip, so the
    /// transform-origin stays fixed and CSS clockwise rotation reads correctly in PDF's y-up space.
    /// <paramref name="leftPx"/>/<paramref name="topPx"/>/<paramref name="widthPx"/>/
    /// <paramref name="heightPx"/> are the box's border-box in CSS px, page-top-relative.</summary>
    public static (double A, double B, double C, double D, double E, double F) ToPdfMatrix(
        CssTransform t, TransformOrigin origin,
        double leftPx, double topPx, double widthPx, double heightPx, double pageHeightPt)
    {
        var ox = leftPx + origin.XFraction * widthPx + origin.XPx;
        var oy = topPx + origin.YFraction * heightPx + origin.YPx;
        // A translate percentage resolves against the box border-box here (CSS Transforms L1 §6).
        var tE = t.EPx + t.EW * widthPx + t.EH * heightPx;
        var tF = t.FPx + t.FW * widthPx + t.FH * heightPx;
        double a = t.A, b = t.B, c = t.C, d = t.D;
        var bx = ox * (1 - a) - c * oy + tE;
        var by = -b * ox + oy * (1 - d) + tF;
        var h = pageHeightPt;
        // `+ 0.0` canonicalizes IEEE −0.0 to +0.0 so the cm never emits an ugly/non-deterministic "-0".
        return (a + 0.0, -b + 0.0, -c + 0.0, d + 0.0,
            c * h + PdfUnits.PxToPt(bx) + 0.0, h * (1 - d) - PdfUnits.PxToPt(by) + 0.0);
    }
}

/// <summary>Phase 4 transforms — parse <c>transform-origin</c> into per-axis fraction + px offset.
/// Supports keywords (<c>left/center/right/top/bottom</c>), percentages, and px + absolute lengths,
/// in 1 or 2 values (a 3rd z value is ignored). Unparseable / absent → the <c>50% 50%</c> default
/// (lenient — a bad origin must not drop the whole transform).</summary>
internal static class CssTransformOrigin_Parser
{
    /// <summary>Parse <c>transform-origin</c>. <paramref name="emPx"/> / <paramref name="remPx"/> resolve
    /// <c>em</c> / <c>rem</c> offsets (pass <see cref="double.NaN"/> when no font context exists — an
    /// <c>em</c> / <c>rem</c> token then fails to classify and the origin falls back to center).</summary>
    public static TransformOrigin Parse(string? raw, double emPx = double.NaN, double remPx = double.NaN)
    {
        if (string.IsNullOrWhiteSpace(raw)) return TransformOrigin.Center;
        var tokens = CssLengthParsing.SplitTopLevelSpaces(raw.Trim());
        if (tokens.Count is 0 or > 3) return TransformOrigin.Center;
        // A 3rd value is the z-length (CSS Transforms L1 §3) — it must be a length (not a keyword / %),
        // and is ignored.
        if (tokens.Count == 3 && !CssLengthParsing.TryLengthPxEmRem(tokens[2], emPx, remPx, out _))
            return TransformOrigin.Center;

        var k0 = Classify(tokens[0], emPx, remPx, out var f0, out var p0);
        if (k0 == OriginKind.Invalid) return TransformOrigin.Center;
        if (tokens.Count == 1)
        {
            // One value: a keyword sets its named axis (the other stays center); a lone offset is X.
            return k0 switch
            {
                OriginKind.Horizontal => new TransformOrigin(f0, p0, 0.5, 0),
                OriginKind.Vertical => new TransformOrigin(0.5, 0, f0, p0),
                OriginKind.Offset => new TransformOrigin(f0, p0, 0.5, 0),
                _ => TransformOrigin.Center, // center
            };
        }

        var k1 = Classify(tokens[1], emPx, remPx, out var f1, out var p1);
        if (k1 == OriginKind.Invalid) return TransformOrigin.Center;

        if (k0 != OriginKind.Offset && k1 != OriginKind.Offset)
        {
            // Both keywords — either order, but two edges on the SAME axis are invalid (PR #210 [P2]).
            if (k0 == OriginKind.Horizontal && k1 == OriginKind.Horizontal) return TransformOrigin.Center;
            if (k0 == OriginKind.Vertical && k1 == OriginKind.Vertical) return TransformOrigin.Center;
            double xf = 0.5, xp = 0, yf = 0.5, yp = 0;
            if (k0 == OriginKind.Horizontal) { xf = f0; xp = p0; } else if (k0 == OriginKind.Vertical) { yf = f0; yp = p0; }
            if (k1 == OriginKind.Horizontal) { xf = f1; xp = p1; } else if (k1 == OriginKind.Vertical) { yf = f1; yp = p1; }
            return new TransformOrigin(xf, xp, yf, yp);
        }

        // At least one offset ⇒ strict positional order: [0] = horizontal, [1] = vertical. A keyword
        // naming the wrong axis for its slot rejects (`25% left`, `top 25%`).
        if (k0 == OriginKind.Vertical) return TransformOrigin.Center;
        if (k1 == OriginKind.Horizontal) return TransformOrigin.Center;
        var (xfr, xpx) = k0 == OriginKind.Center ? (0.5, 0.0) : (f0, p0);
        var (yfr, ypx) = k1 == OriginKind.Center ? (0.5, 0.0) : (f1, p1);
        return new TransformOrigin(xfr, xpx, yfr, ypx);
    }

    private enum OriginKind { Horizontal, Vertical, Center, Offset, Invalid }

    /// <summary>Classify a transform-origin token + read its value as a box fraction (keyword / %)
    /// OR a px offset (length). Exactly one of <paramref name="frac"/> / <paramref name="px"/> is
    /// non-trivial; the caller assigns whichever to the resolved axis.</summary>
    private static OriginKind Classify(string token, double emPx, double remPx, out double frac, out double px)
    {
        frac = 0.5;
        px = 0.0;
        var t = token.Trim().ToLowerInvariant();
        switch (t)
        {
            case "left": frac = 0.0; return OriginKind.Horizontal;
            case "right": frac = 1.0; return OriginKind.Horizontal;
            case "top": frac = 0.0; return OriginKind.Vertical;
            case "bottom": frac = 1.0; return OriginKind.Vertical;
            case "center": frac = 0.5; return OriginKind.Center;
        }
        if (t.EndsWith("%", StringComparison.Ordinal)
            && CssLengthParsing.TryFinite(t.AsSpan(0, t.Length - 1), out var pct))
        {
            frac = pct / 100.0;
            px = 0;
            return OriginKind.Offset;
        }
        if (CssLengthParsing.TryLengthPxEmRem(t, emPx, remPx, out var lengthPx))
        {
            frac = 0;
            px = lengthPx;
            return OriginKind.Offset;
        }
        return OriginKind.Invalid;
    }
}
