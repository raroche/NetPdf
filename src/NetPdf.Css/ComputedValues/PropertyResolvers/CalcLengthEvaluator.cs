// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Evaluates a CSS <c>calc()</c> expression to a used LENGTH in px (CSS Values 4 §10), given the
/// contexts its terms scale by — the percent base (the property's containing-block extent), the
/// owning element's font-size (<c>em</c>/<c>ex</c>/<c>ch</c>), the root font-size (<c>rem</c>),
/// and the viewport/page box (<c>vw</c>/<c>vh</c>/<c>vmin</c>/<c>vmax</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Grammar (§10.3, the length subset).</b> <c>calc( &lt;sum&gt; )</c>;
/// <c>&lt;sum&gt; = &lt;product&gt; [ [ '+' | '-' ] &lt;product&gt; ]*</c> (the operator MUST be
/// surrounded by whitespace — <c>calc(10px+5px)</c> is invalid, §10.3);
/// <c>&lt;product&gt; = &lt;value&gt; [ '*' &lt;value&gt; | '/' &lt;value&gt; ]*</c>;
/// <c>&lt;value&gt; = &lt;number&gt; | &lt;dimension&gt; | &lt;percentage&gt; | ( &lt;sum&gt; ) |
/// calc( &lt;sum&gt; )</c> (a nested <c>calc()</c> is equivalent to parentheses, §10.1).
/// </para>
/// <para>
/// <b>Type checking (§10.4).</b> <c>+</c>/<c>-</c> require both operands to be the SAME type
/// (length+length or number+number); <c>*</c> requires at least one NUMBER operand;
/// <c>/</c> requires a NUMBER divisor (a near-zero divisor is invalid — division by zero, §10.4).
/// The whole expression must evaluate to a LENGTH (a bare-number result is invalid for a length
/// property). Intermediates may be negative; the final used value is CLAMPED to the property's
/// allowed range by the caller contract here — ≥ 0, per §10.5 ("clamped at used-value time") and
/// the non-negative consumers (margin-box <c>width</c>/<c>height</c>/<c>padding</c>, calc cycle).
/// </para>
/// <para>
/// <b>Out of scope</b> (evaluate → <see langword="false"/>, the caller surfaces + falls back):
/// container-relative units, <c>min()</c>/<c>max()</c>/<c>clamp()</c>, attr(), non-finite results.
/// </para>
/// </remarks>
internal static class CalcLengthEvaluator
{
    /// <summary>Near-zero divisor guard — a smaller magnitude is treated as division by zero
    /// (invalid per §10.4) rather than producing an absurd/non-finite quotient.</summary>
    private const double MinDivisorMagnitude = 1e-12;

    /// <summary>The contexts a <c>calc()</c> term resolves against: the percent base (the
    /// property's containing-block extent on its axis), the owning element's resolved font-size,
    /// the root element's, and the viewport (page box) dimensions — all CSS px.</summary>
    internal readonly record struct CalcContext(
        double PercentBasePx, double EmPx, double RootEmPx, double ViewportWidthPx, double ViewportHeightPx);

    /// <summary>Whether <paramref name="rawText"/> is shaped like a <c>calc()</c> expression
    /// (<c>calc(</c> … <c>)</c>, case-insensitive). Shape only — <see cref="TryEvaluate"/> decides
    /// validity; callers use this as the keep-vs-drop gate (like
    /// <see cref="RelativeLengthResolver.IsSupported"/>, the contextual evaluation can still fail
    /// and must then be surfaced, not silently dropped).</summary>
    public static bool IsCalc(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return false;
        var v = rawText.AsSpan().Trim();
        return v.Length > 6
            && v[..5].Equals("calc(", StringComparison.OrdinalIgnoreCase)
            && v[^1] == ')';
    }

    /// <summary>Evaluate <paramref name="rawText"/> (a full <c>calc(…)</c> value) to a used length
    /// (px, ≥ 0 — range-clamped per §10.5). Returns <see langword="false"/> on a parse/type error,
    /// an unsupported unit/function, division by (near-)zero, a bare-number result, or a non-finite
    /// result.</summary>
    public static bool TryEvaluate(string rawText, in CalcContext context, out double px)
    {
        px = 0;
        if (!IsCalc(rawText)) return false;
        var expr = rawText.AsSpan().Trim();
        expr = expr[5..^1]; // strip "calc(" + ")".

        var pos = 0;
        if (!TryParseSum(expr, ref pos, context, out var result)) return false;
        SkipWhitespace(expr, ref pos);
        if (pos != expr.Length) return false;            // trailing junk.
        if (result.IsNumber) return false;               // a length property needs a length result.
        if (!double.IsFinite(result.Value)) return false;
        px = Math.Max(0, result.Value);                  // §10.5 used-value range clamp (non-negative consumers).
        return true;
    }

    /// <summary>A partially-evaluated term: a px length or a unitless number (§10.4 type system —
    /// only these two types occur in the length subset).</summary>
    private readonly record struct Term(double Value, bool IsNumber);

    private static bool TryParseSum(ReadOnlySpan<char> s, ref int pos, in CalcContext ctx, out Term result)
    {
        if (!TryParseProduct(s, ref pos, ctx, out result)) return false;
        while (true)
        {
            var beforeWs = pos;
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length || (s[pos] != '+' && s[pos] != '-')) { pos = beforeWs; return true; }
            // §10.3: + and - MUST be surrounded by whitespace (else "10px -5px" / "10px+5px" would
            // be ambiguous with a signed dimension).
            var hadLeadingWs = pos > beforeWs;
            var op = s[pos];
            pos++;
            if (!hadLeadingWs || pos >= s.Length || !char.IsWhiteSpace(s[pos])) return false;
            if (!TryParseProduct(s, ref pos, ctx, out var right)) return false;
            if (result.IsNumber != right.IsNumber) return false; // §10.4: same-type operands only.
            result = result with { Value = op == '+' ? result.Value + right.Value : result.Value - right.Value };
        }
    }

    private static bool TryParseProduct(ReadOnlySpan<char> s, ref int pos, in CalcContext ctx, out Term result)
    {
        if (!TryParseValue(s, ref pos, ctx, out result)) return false;
        while (true)
        {
            var beforeWs = pos;
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length || (s[pos] != '*' && s[pos] != '/')) { pos = beforeWs; return true; }
            var op = s[pos];
            pos++; // * and / need no surrounding whitespace (§10.3).
            if (!TryParseValue(s, ref pos, ctx, out var right)) return false;
            if (op == '*')
            {
                if (!result.IsNumber && !right.IsNumber) return false; // length × length has no CSS type.
                result = new Term(result.Value * right.Value, result.IsNumber && right.IsNumber);
            }
            else
            {
                // §10.4: the divisor must be a NUMBER, and not (near-)zero.
                if (!right.IsNumber || Math.Abs(right.Value) < MinDivisorMagnitude) return false;
                result = result with { Value = result.Value / right.Value };
            }
        }
    }

    private static bool TryParseValue(ReadOnlySpan<char> s, ref int pos, in CalcContext ctx, out Term result)
    {
        result = default;
        SkipWhitespace(s, ref pos);
        if (pos >= s.Length) return false;

        // A parenthesized sub-sum — or a nested calc(), which §10.1 defines as equivalent.
        if (s[pos] == '(')
        {
            pos++;
            if (!TryParseSum(s, ref pos, ctx, out result)) return false;
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length || s[pos] != ')') return false;
            pos++;
            return true;
        }
        if (pos + 5 <= s.Length && s.Slice(pos, 5).Equals("calc(", StringComparison.OrdinalIgnoreCase))
        {
            pos += 4; // leave the '(' for the branch above.
            return TryParseValue(s, ref pos, ctx, out result);
        }

        // <number> with an optional unit / '%'. A sign is part of the value here (a value position),
        // unlike the whitespace-required binary +/- (an operator position).
        var start = pos;
        if (s[pos] is '+' or '-') pos++;
        var digits = pos;
        while (pos < s.Length && (char.IsAsciiDigit(s[pos]) || s[pos] == '.')) pos++;
        if (pos == digits) { pos = start; return false; } // no digits → not a value.
        // e-notation (CSS Values §4.1 admits it) — consume e[+-]?digits when present.
        if (pos < s.Length && (s[pos] is 'e' or 'E'))
        {
            var ePos = pos + 1;
            if (ePos < s.Length && (s[ePos] is '+' or '-')) ePos++;
            if (ePos < s.Length && char.IsAsciiDigit(s[ePos]))
            {
                pos = ePos;
                while (pos < s.Length && char.IsAsciiDigit(s[pos])) pos++;
            }
        }
        if (!double.TryParse(s[start..pos], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number))
            return false;

        // Unit: consecutive letters, or '%'.
        if (pos < s.Length && s[pos] == '%')
        {
            pos++;
            result = new Term(number / 100.0 * ctx.PercentBasePx, IsNumber: false);
            return true;
        }
        var unitStart = pos;
        while (pos < s.Length && char.IsAsciiLetter(s[pos])) pos++;
        if (pos == unitStart) { result = new Term(number, IsNumber: true); return true; }

        var unit = s[unitStart..pos].ToString();
        if (LengthResolver.TryAbsoluteUnitToPx(unit, number, out var absPx))
        {
            result = new Term(absPx, IsNumber: false);
            return true;
        }
        if (RelativeLengthResolver.TryResolveNumberUnit(
                number, unit, ctx.EmPx, ctx.RootEmPx, ctx.ViewportWidthPx, ctx.ViewportHeightPx, out var relPx))
        {
            result = new Term(relPx, IsNumber: false);
            return true;
        }
        return false; // container units / unknown units — unsupported.
    }

    private static void SkipWhitespace(ReadOnlySpan<char> s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }
}
