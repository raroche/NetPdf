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
/// CSS-TRANSFORM-3D-UNSUPPORTED-001).</summary>
internal sealed record CssTransform(double A, double B, double C, double D, double E, double F, bool Had3D)
{
    public bool IsIdentity => A == 1 && B == 0 && C == 0 && D == 1 && E == 0 && F == 0;
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
    public static CssTransform? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim();
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;

        var functions = CssLengthParsing.SplitTopLevelSpaces(v); // each "name(args)" stays whole
        if (functions.Count == 0) return null;

        // Accumulate M = M · m in list order (the first-listed function is outermost — applied last).
        double a = 1, b = 0, c = 0, d = 1, e = 0, f = 0;
        var had3D = false;
        foreach (var fn in functions)
        {
            if (!TryParseFunction(fn, out var m, out var fn3D)) return null;
            had3D |= fn3D;
            if (m is { } mm)
                (a, b, c, d, e, f) = Multiply(a, b, c, d, e, f, mm.A, mm.B, mm.C, mm.D, mm.E, mm.F);
        }
        // A composed matrix can blow up to non-finite (e.g. skew near 90° → tan → huge) — reject it
        // so it never reaches PDF emission (PR #210 review [P2]).
        if (!(double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c)
            && double.IsFinite(d) && double.IsFinite(e) && double.IsFinite(f))) return null;
        return new CssTransform(a, b, c, d, e, f, had3D);
    }

    private readonly record struct M(double A, double B, double C, double D, double E, double F);

    private static (double, double, double, double, double, double) Multiply(
        double a1, double b1, double c1, double d1, double e1, double f1,
        double a2, double b2, double c2, double d2, double e2, double f2) =>
        (a1 * a2 + c1 * b2, b1 * a2 + d1 * b2,
         a1 * c2 + c1 * d2, b1 * c2 + d1 * d2,
         a1 * e2 + c1 * f2 + e1, b1 * e2 + d1 * f2 + f1);

    private static bool TryParseFunction(string fn, out M? matrix, out bool is3D)
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
                return Lengths(args, 1, 2, out var tr)
                    && Set(out matrix, 1, 0, 0, 1, tr[0], tr.Length > 1 ? tr[1] : 0);
            case "translatex":
                return Lengths(args, 1, 1, out var trx) && Set(out matrix, 1, 0, 0, 1, trx[0], 0);
            case "translatey":
                return Lengths(args, 1, 1, out var trY) && Set(out matrix, 1, 0, 0, 1, 0, trY[0]);
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
                return Lengths(args, 3, 3, out var t3) && Set(out matrix, 1, 0, 0, 1, t3[0], t3[1]);
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

    private static bool Set(out M? matrix, double a, double b, double c, double d, double e, double f)
    {
        matrix = new M(a, b, c, d, e, f);
        return true;
    }

    private static bool Lengths(IReadOnlyList<string> args, int min, int max, out double[] px)
    {
        px = [];
        if (args.Count < min || args.Count > max) return false;
        var result = new double[args.Count];
        for (var i = 0; i < args.Count; i++)
            if (!CssLengthParsing.TryLengthPx(args[i], out result[i])) return false;
        px = result;
        return true;
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
        double a = t.A, b = t.B, c = t.C, d = t.D;
        var bx = ox * (1 - a) - c * oy + t.E;
        var by = -b * ox + oy * (1 - d) + t.F;
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
    public static TransformOrigin Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return TransformOrigin.Center;
        var tokens = CssLengthParsing.SplitTopLevelSpaces(raw.Trim());
        if (tokens.Count is 0 or > 3) return TransformOrigin.Center;
        // A 3rd value is the z-length (CSS Transforms L1 §3) — it must be a length, and is ignored.
        if (tokens.Count == 3 && !CssLengthParsing.TryLengthPx(tokens[2], out _)) return TransformOrigin.Center;

        var k0 = Classify(tokens[0], out var f0, out var p0);
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

        var k1 = Classify(tokens[1], out var f1, out var p1);
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
    private static OriginKind Classify(string token, out double frac, out double px)
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
        if (CssLengthParsing.TryLengthPx(t, out var lengthPx))
        {
            frac = 0;
            px = lengthPx;
            return OriginKind.Offset;
        }
        return OriginKind.Invalid;
    }
}
