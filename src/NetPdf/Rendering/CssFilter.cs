// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace NetPdf.Rendering;

/// <summary>Phase 4 filters (PR 2) — one CSS <c>filter</c> function (CSS Filter Effects L1 §2).
/// The amount semantics depend on the kind: a fraction (1.0 = 100%) for the proportional functions
/// (<see cref="Brightness"/> / <see cref="Contrast"/> / <see cref="Grayscale"/> / <see cref="Invert"/>
/// / <see cref="Opacity"/> / <see cref="Saturate"/> / <see cref="Sepia"/>), CSS px for
/// <see cref="Blur"/>, and degrees for <see cref="HueRotate"/>. <see cref="DropShadow"/> carries its
/// own <see cref="DropShadowParams"/> instead.</summary>
internal enum FilterKind { Blur, Brightness, Contrast, Grayscale, HueRotate, Invert, Opacity, Saturate, Sepia, DropShadow }

/// <summary>Phase 4 filters — a <c>drop-shadow(&lt;offset-x&gt; &lt;offset-y&gt; &lt;blur&gt;?
/// &lt;color&gt;?)</c> parameter set (offsets + blur in CSS px; the raw color resolved by the
/// painter, null = <c>currentColor</c>). Unlike box-shadow there is no spread or inset.</summary>
internal readonly record struct DropShadowParams(double OffsetXPx, double OffsetYPx, double BlurPx, string? ColorRaw);

/// <summary>Phase 4 filters — one parsed filter function. <see cref="Amount"/> is the kind-dependent
/// scalar (see <see cref="FilterKind"/>); <see cref="Shadow"/> is set only for
/// <see cref="FilterKind.DropShadow"/>.</summary>
internal readonly record struct FilterOp(FilterKind Kind, double Amount, DropShadowParams? Shadow = null);

/// <summary>Phase 4 filters — a parsed CSS <c>filter</c> value: the ordered function list applied in
/// sequence (the first listed is applied first, CSS Filter Effects §2).</summary>
internal sealed record CssFilter(IReadOnlyList<FilterOp> Ops);

/// <summary>Phase 4 filters (PR 2) — a parser for the <c>filter</c> property: a space-separated list
/// of filter functions (<c>blur() brightness() contrast() drop-shadow() grayscale() hue-rotate()
/// invert() opacity() saturate() sepia()</c>). Returns <see langword="null"/> for <c>none</c> /
/// empty / any unparseable function (the whole value is then unsupported). <c>url()</c> SVG filter
/// references are out of scope (→ null).</summary>
internal static class CssFilter_Parser
{
    public static CssFilter? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim();
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;

        var functions = CssLengthParsing.SplitTopLevelSpaces(v); // each "name(args)" stays whole
        if (functions.Count == 0) return null;

        var ops = new List<FilterOp>(functions.Count);
        foreach (var fn in functions)
        {
            if (!TryParseFunction(fn, out var op)) return null;
            ops.Add(op);
        }
        return ops.Count > 0 ? new CssFilter(ops) : null;
    }

    private static bool TryParseFunction(string fn, out FilterOp op)
    {
        op = default;
        var open = fn.IndexOf('(');
        if (open < 0 || !fn.EndsWith(")", StringComparison.Ordinal)) return false;
        var name = fn.Substring(0, open).Trim().ToLowerInvariant();
        var args = fn.Substring(open + 1, fn.Length - open - 2).Trim();

        switch (name)
        {
            case "blur":
                // A single non-negative length; an empty arg defaults to 0 (a no-op).
                if (args.Length == 0) { op = new FilterOp(FilterKind.Blur, 0); return true; }
                if (!CssLengthParsing.TryLengthPx(args, out var blurPx) || blurPx < 0) return false;
                op = new FilterOp(FilterKind.Blur, blurPx);
                return true;
            // All proportional functions default to 1 when the argument is OMITTED (CSS Filter
            // Effects §2 — `grayscale()` ≡ `grayscale(1)`). grayscale / invert / sepia / opacity clamp
            // amounts above 1 to 1 (§2.x); brightness / contrast / saturate may exceed 1 (no upper clamp).
            case "brightness": return Proportional(FilterKind.Brightness, args, clampToOne: false, out op);
            case "contrast": return Proportional(FilterKind.Contrast, args, clampToOne: false, out op);
            case "saturate": return Proportional(FilterKind.Saturate, args, clampToOne: false, out op);
            case "grayscale": return Proportional(FilterKind.Grayscale, args, clampToOne: true, out op);
            case "invert": return Proportional(FilterKind.Invert, args, clampToOne: true, out op);
            case "sepia": return Proportional(FilterKind.Sepia, args, clampToOne: true, out op);
            case "opacity": return Proportional(FilterKind.Opacity, args, clampToOne: true, out op);
            case "hue-rotate":
                if (args.Length == 0) { op = new FilterOp(FilterKind.HueRotate, 0); return true; }
                if (!TryAngleDeg(args, out var deg)) return false;
                op = new FilterOp(FilterKind.HueRotate, deg);
                return true;
            case "drop-shadow":
                if (!TryParseDropShadow(args, out var shadow)) return false;
                op = new FilterOp(FilterKind.DropShadow, 0, shadow);
                return true;
            default:
                return false; // url() SVG refs + unknown functions are unsupported
        }
    }

    /// <summary>A proportional function argument: a <c>&lt;number&gt;</c> or <c>&lt;percentage&gt;</c>
    /// (100% = 1.0), non-negative, defaulting to 1 when omitted. <paramref name="clampToOne"/> clamps
    /// the amount above 1 to 1 (grayscale / invert / sepia / opacity); the others keep amounts &gt; 1.</summary>
    private static bool Proportional(FilterKind kind, string args, bool clampToOne, out FilterOp op)
    {
        op = default;
        if (args.Length == 0) { op = new FilterOp(kind, 1.0); return true; } // `grayscale()` ≡ `grayscale(1)`
        if (!TryNumberOrPercent(args, out var amount) || amount < 0) return false;
        if (clampToOne && amount > 1.0) amount = 1.0;
        op = new FilterOp(kind, amount);
        return true;
    }

    /// <summary>Parse a <c>&lt;number&gt;</c> (e.g. <c>0.5</c>) or <c>&lt;percentage&gt;</c> (e.g.
    /// <c>50%</c>) to a fraction (100% → 1.0).</summary>
    private static bool TryNumberOrPercent(string token, out double value)
    {
        value = 0;
        var t = token.Trim();
        if (t.Length == 0) return false;
        if (t.EndsWith("%", StringComparison.Ordinal))
            return CssLengthParsing.TryFinite(t.AsSpan(0, t.Length - 1), out value) && Scale(ref value, 0.01);
        return CssLengthParsing.TryFinite(t, out value);

        static bool Scale(ref double v, double by) { v *= by; return true; }
    }

    private static bool TryAngleDeg(string token, out double deg)
    {
        deg = 0;
        var t = token.Trim().ToLowerInvariant();
        if (t.Length == 0) return false;
        (string Unit, double Factor)[] units = { ("deg", 1.0), ("grad", 0.9), ("rad", 180.0 / Math.PI), ("turn", 360.0) };
        foreach (var (unit, factor) in units)
            if (t.EndsWith(unit, StringComparison.Ordinal))
                return CssLengthParsing.TryFinite(t.AsSpan(0, t.Length - unit.Length), out deg) && Scale(ref deg, factor);
        return CssLengthParsing.TryFinite(t, out var z) && z == 0.0; // bare 0 is a valid angle
        static bool Scale(ref double v, double by) { v *= by; return true; }
    }

    /// <summary>Parse <c>drop-shadow(&lt;length&gt;{2,3} &amp;&amp; &lt;color&gt;?)</c> — two or three
    /// lengths (offset-x, offset-y, blur?) + an optional color (any order for the color vs lengths).</summary>
    private static bool TryParseDropShadow(string args, out DropShadowParams shadow)
    {
        shadow = default;
        if (args.Length == 0) return false;
        string? colorRaw = null;
        Span<double> lengths = stackalloc double[3];
        var lengthCount = 0;
        foreach (var token in CssLengthParsing.SplitTopLevelSpaces(args))
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
        shadow = new DropShadowParams(lengths[0], lengths[1], blur, colorRaw);
        return true;
    }
}
