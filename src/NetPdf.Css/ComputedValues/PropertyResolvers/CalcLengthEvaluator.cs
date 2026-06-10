// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;
using NetPdf.Css.Cascade;

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
/// calc( &lt;sum&gt; ) | min( &lt;sum&gt;# ) | max( &lt;sum&gt;# ) | clamp( [&lt;sum&gt;|none],
/// &lt;sum&gt;, [&lt;sum&gt;|none] )</c> (a nested <c>calc()</c> is equivalent to parentheses per §10.1; the §10.2
/// comparison functions take same-type arguments — <c>clamp(MIN, VAL, MAX)</c> =
/// <c>max(MIN, min(VAL, MAX))</c> — and are also valid as the WHOLE value, min/max/clamp cycle).
/// </para>
/// <para>
/// <b>Type checking (§10.4).</b> <c>+</c>/<c>-</c> require both operands to be the SAME type
/// (length+length or number+number); <c>*</c> requires at least one NUMBER operand;
/// <c>/</c> requires a NUMBER divisor and rejects an EXACTLY-zero one (division by zero, §10.4 —
/// a tiny non-zero divisor is valid per spec; post-PR-#157 Copilot review). A non-finite result
/// (e.g. an overflowing quotient) is rejected at the end. The whole expression must evaluate to a
/// LENGTH (a bare-number result is invalid for a length property). Intermediates may be negative;
/// the final used value is CLAMPED to the property's allowed range by the caller contract here —
/// ≥ 0, per §10.5 ("clamped at used-value time") and the non-negative consumers (margin-box
/// <c>width</c>/<c>height</c>/<c>padding</c>, calc cycle).
/// </para>
/// <para>
/// <b>Out of scope</b> (evaluate → <see langword="false"/>, the caller surfaces + falls back):
/// container-relative units, the §10.8+ trigonometric/exponential functions
/// (<c>sin()</c>/<c>pow()</c>/…), attr(), non-finite results. The §10.6 stepped-value
/// (<c>round()</c>/<c>mod()</c>/<c>rem()</c>) and §10.7 sign-related (<c>abs()</c>/<c>sign()</c>)
/// functions ARE evaluated (math-fns cycle).
/// </para>
/// </remarks>
internal static class CalcLengthEvaluator
{

    /// <summary>The contexts a <c>calc()</c> term resolves against: the percent base (the
    /// property's containing-block extent on its axis), the owning element's resolved font-size,
    /// the root element's, and the viewport (page box) dimensions — all CSS px.</summary>
    internal readonly record struct CalcContext(
        double PercentBasePx, double EmPx, double RootEmPx, double ViewportWidthPx, double ViewportHeightPx);

    /// <summary>Whether <paramref name="rawText"/> is shaped like a CSS math function —
    /// <c>calc(</c> / <c>min(</c> / <c>max(</c> / <c>clamp(</c> … <c>)</c>, case-insensitive
    /// (min/max/clamp are valid as a WHOLE value, not just inside calc — CSS Values 4 §10.1).
    /// Shape only — <c>TryEvaluate</c> decides validity; callers use this as the
    /// keep-vs-drop gate (like <see cref="RelativeLengthResolver.IsSupported"/>, the contextual
    /// evaluation can still fail and must then be surfaced, not silently dropped).</summary>
    public static bool IsMathFunction(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return false;
        var v = rawText.AsSpan().Trim();
        if (v.Length < 6 || v[^1] != ')') return false; // shortest: "min(x)".
        return StartsWithName(v, "calc(") || StartsWithName(v, "min(") || StartsWithName(v, "max(")
            || StartsWithName(v, "clamp(")
            // §10.6 stepped-value + §10.7 sign-related functions (math-fns cycle). `sign()` yields a
            // NUMBER, so as a WHOLE length value it admits here then fails evaluation (surfaced) —
            // correct: it IS invalid as a length, but it's still a math function.
            || StartsWithName(v, "round(") || StartsWithName(v, "mod(") || StartsWithName(v, "rem(")
            || StartsWithName(v, "abs(") || StartsWithName(v, "sign(");

        static bool StartsWithName(ReadOnlySpan<char> v, string nameWithParen) =>
            v.Length > nameWithParen.Length + 1
            && v[..nameWithParen.Length].Equals(nameWithParen, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Evaluate <paramref name="rawText"/> (a full <c>calc(…)</c> / <c>min(…)</c> /
    /// <c>max(…)</c> / <c>clamp(…)</c> value) to a used length (px, ≥ 0 — range-clamped per §10.5).
    /// Returns <see langword="false"/> on a parse/type error, an unsupported unit/function, division
    /// by zero, a bare-number result, a non-finite result, or an expression exceeding the
    /// adversarial-input guards — the body length cap (<see cref="CalcResolver.MaxBodyLength"/>) and
    /// the nesting-depth cap (<see cref="CalcResolver.MaxDepth"/>), mirrored from the cascade calc
    /// resolver so both calc front doors bound CPU/stack identically (post-PR-#157 review P2).</summary>
    public static bool TryEvaluate(string rawText, in CalcContext context, out double px) =>
        TryEvaluate(rawText, context, clampNonNegative: true, out px);

    /// <summary><paramref name="clampNonNegative"/> controls the §10.5 used-value range clamp: the
    /// margin-box consumers (width/height/padding/font-size) are non-negative properties, so the
    /// default clamps at 0; the BODY length path (body-calc cycle) passes the property's actual
    /// range — a body <c>margin-left: calc(0px - 10px)</c> legitimately evaluates to −10px.</summary>
    public static bool TryEvaluate(
        string rawText, in CalcContext context, bool clampNonNegative, out double px)
    {
        px = 0;
        if (!IsMathFunction(rawText)) return false;
        var expr = rawText.AsSpan().Trim();
        // Breadth guard (long operand chains): cap the function BODY — the text between the outer
        // parens — the SAME measure CalcResolver.MaxBodyLength is defined for (post-PR-#158 review
        // P3; capping the full raw also counted the function name + parens, rejecting
        // boundary-valid inputs the cascade resolver accepts). IsMathFunction guarantees a '('.
        var bodyStartIndex = expr.IndexOf('(');
        if (expr.Length - bodyStartIndex - 2 > CalcResolver.MaxBodyLength) return false;

        // The whole value is one math-function <value> — TryParseValue recognizes calc( (≡ parens,
        // §10.1) and min(/max(/clamp( (§10.2 comparison functions) and consumes through the
        // matching ')'.
        var pos = 0;
        if (!TryParseValue(expr, ref pos, context, depth: 0, out var result)) return false;
        SkipWhitespace(expr, ref pos);
        if (pos != expr.Length) return false;            // trailing junk.
        if (result.IsNumber) return false;               // a length property needs a length result.
        if (!double.IsFinite(result.Value)) return false;
        px = clampNonNegative ? Math.Max(0, result.Value) : result.Value; // §10.5 used-value range clamp.
        return true;
    }

    /// <summary>A partially-evaluated term: a px length or a unitless number (§10.4 type system —
    /// only these two types occur in the length subset).</summary>
    private readonly record struct Term(double Value, bool IsNumber);

    private static bool TryParseSum(
        ReadOnlySpan<char> s, ref int pos, in CalcContext ctx, int depth, out Term result)
    {
        if (!TryParseProduct(s, ref pos, ctx, depth, out result)) return false;
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
            if (!TryParseProduct(s, ref pos, ctx, depth, out var right)) return false;
            if (result.IsNumber != right.IsNumber) return false; // §10.4: same-type operands only.
            result = result with { Value = op == '+' ? result.Value + right.Value : result.Value - right.Value };
        }
    }

    private static bool TryParseProduct(
        ReadOnlySpan<char> s, ref int pos, in CalcContext ctx, int depth, out Term result)
    {
        if (!TryParseValue(s, ref pos, ctx, depth, out result)) return false;
        while (true)
        {
            var beforeWs = pos;
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length || (s[pos] != '*' && s[pos] != '/')) { pos = beforeWs; return true; }
            var op = s[pos];
            pos++; // * and / need no surrounding whitespace (§10.3).
            if (!TryParseValue(s, ref pos, ctx, depth, out var right)) return false;
            if (op == '*')
            {
                if (!result.IsNumber && !right.IsNumber) return false; // length × length has no CSS type.
                result = new Term(result.Value * right.Value, result.IsNumber && right.IsNumber);
            }
            else
            {
                // §10.4: the divisor must be a NUMBER and not EXACTLY zero. A tiny non-zero divisor
                // is a valid expression per spec (post-PR-#157 Copilot review — a near-zero guard
                // wrongly rejected e.g. `calc(10px / 1e-13)`); an overflowing quotient is caught by
                // the final IsFinite gate instead.
                if (!right.IsNumber || right.Value == 0.0) return false;
                result = result with { Value = result.Value / right.Value };
            }
        }
    }

    private static bool TryParseValue(
        ReadOnlySpan<char> s, ref int pos, in CalcContext ctx, int depth, out Term result)
    {
        result = default;
        SkipWhitespace(s, ref pos);
        if (pos >= s.Length) return false;

        // A parenthesized sub-sum — or a nested calc(), which §10.1 defines as equivalent. Depth-
        // capped (CalcResolver.MaxDepth, shared with the cascade resolver) so pathological nesting
        // can't grow the recursion unboundedly (post-PR-#157 review P2).
        if (s[pos] == '(')
        {
            if (depth >= CalcResolver.MaxDepth) return false;
            pos++;
            if (!TryParseSum(s, ref pos, ctx, depth + 1, out result)) return false;
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length || s[pos] != ')') return false;
            pos++;
            return true;
        }
        if (pos + 5 <= s.Length && s.Slice(pos, 5).Equals("calc(", StringComparison.OrdinalIgnoreCase))
        {
            pos += 4; // leave the '(' for the branch above (which applies the depth cap).
            return TryParseValue(s, ref pos, ctx, depth, out result);
        }
        // min() / max() / clamp() — the §10.2 comparison functions, valid wherever a calc value is
        // (and as the whole expression — TryEvaluate enters here). Each consumes a depth level.
        if (TryMatchFunctionName(s, ref pos, "min("))
            return TryParseMinMax(s, ref pos, ctx, depth, isMin: true, out result);
        if (TryMatchFunctionName(s, ref pos, "max("))
            return TryParseMinMax(s, ref pos, ctx, depth, isMin: false, out result);
        if (TryMatchFunctionName(s, ref pos, "clamp("))
            return TryParseClamp(s, ref pos, ctx, depth, out result);
        // §10.6 stepped-value + §10.7 sign-related functions (math-fns cycle). `rem(` the FUNCTION
        // only matches with its paren — the `rem` UNIT still parses via the number path below.
        if (TryMatchFunctionName(s, ref pos, "round("))
            return TryParseRound(s, ref pos, ctx, depth, out result);
        if (TryMatchFunctionName(s, ref pos, "mod("))
            return TryParseModRem(s, ref pos, ctx, depth, isMod: true, out result);
        if (TryMatchFunctionName(s, ref pos, "rem("))
            return TryParseModRem(s, ref pos, ctx, depth, isMod: false, out result);
        if (TryMatchFunctionName(s, ref pos, "abs("))
            return TryParseAbsSign(s, ref pos, ctx, depth, isAbs: true, out result);
        if (TryMatchFunctionName(s, ref pos, "sign("))
            return TryParseAbsSign(s, ref pos, ctx, depth, isAbs: false, out result);

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

        // CSS units are ASCII case-insensitive (CSS Syntax §4) — normalize before the lookups:
        // TryAbsoluteUnitToPx matches lowercase only, so `calc(1IN - 24PT)` would otherwise be
        // rejected as an unknown unit (post-PR-#157 review P2).
        var unit = s[unitStart..pos].ToString().ToLowerInvariant();
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

    /// <summary>Match a case-insensitive function-name prefix (incl. its <c>(</c>) at
    /// <paramref name="pos"/>, advancing past it on success.</summary>
    private static bool TryMatchFunctionName(ReadOnlySpan<char> s, ref int pos, string nameWithParen)
    {
        if (pos + nameWithParen.Length > s.Length
            || !s.Slice(pos, nameWithParen.Length).Equals(nameWithParen, StringComparison.OrdinalIgnoreCase))
            return false;
        pos += nameWithParen.Length;
        return true;
    }

    /// <summary><c>min( &lt;sum&gt; [, &lt;sum&gt;]* )</c> / <c>max(…)</c> — §10.2: the smallest /
    /// largest of one-or-more comma-separated arguments, all of the SAME type. The caller consumed
    /// the name + <c>(</c>; this consumes through the matching <c>)</c>. Depth-capped like
    /// parentheses.</summary>
    private static bool TryParseMinMax(
        ReadOnlySpan<char> s, ref int pos, in CalcContext ctx, int depth, bool isMin, out Term result)
    {
        result = default;
        if (depth >= CalcResolver.MaxDepth) return false;
        var first = true;
        while (true)
        {
            if (!TryParseSum(s, ref pos, ctx, depth + 1, out var arg)) return false;
            if (first) { result = arg; first = false; }
            else
            {
                if (result.IsNumber != arg.IsNumber) return false; // §10.4: same-type arguments only.
                result = result with
                {
                    Value = isMin ? Math.Min(result.Value, arg.Value) : Math.Max(result.Value, arg.Value),
                };
            }
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ',') { pos++; continue; }
            if (pos < s.Length && s[pos] == ')') { pos++; return true; }
            return false; // unterminated / junk between arguments.
        }
    }

    /// <summary><c>clamp([MIN | none], VAL, [MAX | none])</c> — §10.2: three same-type arguments,
    /// computed as <c>max(MIN, min(VAL, MAX))</c> (when MIN &gt; MAX, MIN wins — per spec). Either
    /// BOUND may be the keyword <c>none</c> (post-PR-#158 review P2): <c>clamp(none, VAL, MAX)</c> =
    /// <c>min(VAL, MAX)</c>, <c>clamp(MIN, VAL, none)</c> = <c>max(MIN, VAL)</c>,
    /// <c>clamp(none, VAL, none)</c> = <c>VAL</c>; the CENTER argument must be a real
    /// <c>&lt;calc-sum&gt;</c>. The caller consumed the name + <c>(</c>. Depth-capped like
    /// parentheses.</summary>
    private static bool TryParseClamp(
        ReadOnlySpan<char> s, ref int pos, in CalcContext ctx, int depth, out Term result)
    {
        result = default;
        if (depth >= CalcResolver.MaxDepth) return false;
        Span<Term> args = stackalloc Term[3];
        Span<bool> isNone = stackalloc bool[3];
        for (var i = 0; i < 3; i++)
        {
            // `none` is admitted for the BOUNDS (i == 0 / 2) only — never the center value.
            if (i != 1 && TryMatchNoneKeyword(s, ref pos))
                isNone[i] = true;
            else if (!TryParseSum(s, ref pos, ctx, depth + 1, out args[i]))
                return false;
            SkipWhitespace(s, ref pos);
            var wantComma = i < 2;
            if (pos >= s.Length || s[pos] != (wantComma ? ',' : ')')) return false;
            pos++;
        }
        if (!isNone[0] && args[0].IsNumber != args[1].IsNumber) return false; // §10.4: same-type
        if (!isNone[2] && args[1].IsNumber != args[2].IsNumber) return false; //   arguments only.
        var value = args[1].Value;
        if (!isNone[2]) value = Math.Min(value, args[2].Value);
        if (!isNone[0]) value = Math.Max(args[0].Value, value); // MIN applied last → MIN wins over MAX.
        result = args[1] with { Value = value };
        return true;
    }

    /// <summary><c>round(&lt;strategy&gt;?, A, B?)</c> — §10.6: round A to the nearest multiple of
    /// B per the strategy (<c>nearest</c> default / <c>up</c> / <c>down</c> / <c>to-zero</c>). A and
    /// B must be the SAME type; B defaults to the number 1 (so omitting B is valid only for a number
    /// A — for a length A the type check rejects it, per spec). <c>nearest</c> ties round UP (toward
    /// +∞, per spec). B must not be zero. The caller consumed the name + <c>(</c>.</summary>
    private static bool TryParseRound(
        ReadOnlySpan<char> s, ref int pos, in CalcContext ctx, int depth, out Term result)
    {
        result = default;
        if (depth >= CalcResolver.MaxDepth) return false;
        // Optional <rounding-strategy> keyword followed by a comma.
        var strategy = RoundStrategy.Nearest;
        var save = pos;
        SkipWhitespace(s, ref pos);
        foreach (var (name, st) in RoundStrategies)
        {
            if (pos + name.Length <= s.Length
                && s.Slice(pos, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                var after = pos + name.Length;
                var probe = after;
                SkipWhitespace(s, ref probe);
                if (probe < s.Length && s[probe] == ',')
                {
                    strategy = st;
                    pos = probe + 1;
                }
                break;
            }
        }
        if (strategy == RoundStrategy.Nearest && pos != save)
        {
            // "nearest," consumed explicitly is fine; otherwise pos only moved past whitespace.
        }

        if (!TryParseSum(s, ref pos, ctx, depth + 1, out var a)) return false;
        var b = new Term(1.0, IsNumber: true); // §10.6: B defaults to 1 (a NUMBER).
        SkipWhitespace(s, ref pos);
        if (pos < s.Length && s[pos] == ',')
        {
            pos++;
            if (!TryParseSum(s, ref pos, ctx, depth + 1, out b)) return false;
            SkipWhitespace(s, ref pos);
        }
        if (pos >= s.Length || s[pos] != ')') return false;
        pos++;
        if (a.IsNumber != b.IsNumber) return false;      // §10.4: same-type arguments only.
        if (b.Value == 0.0) return false;                // rounding to a zero step is invalid.
        // Post-PR-#159 Copilot review — normalize the step to |B|: the multiples of B and of
        // |B| are the same set, but a NEGATIVE B flips the ratio's sign, inverting the
        // directional strategies (round(up, 11px, -10px) returned 10px instead of 20px) and
        // the nearest tie-break (round(nearest, 5px, -2px) returned 4px instead of 6px —
        // ties go to the LARGER multiple, toward +∞).
        var step = Math.Abs(b.Value);
        var ratio = a.Value / step;
        var multiple = strategy switch
        {
            RoundStrategy.Up => Math.Ceiling(ratio),
            RoundStrategy.Down => Math.Floor(ratio),
            RoundStrategy.ToZero => Math.Truncate(ratio),
            _ => Math.Floor(ratio + 0.5),                // nearest — ties toward +∞ (the larger multiple).
        };
        result = a with { Value = multiple * step };
        return true;
    }

    private enum RoundStrategy { Nearest, Up, Down, ToZero }

    private static readonly (string Name, RoundStrategy Strategy)[] RoundStrategies =
    [
        ("nearest", RoundStrategy.Nearest), ("to-zero", RoundStrategy.ToZero),
        ("up", RoundStrategy.Up), ("down", RoundStrategy.Down),
    ];

    /// <summary><c>mod(A, B)</c> / <c>rem(A, B)</c> — §10.6: the modulus/remainder of two SAME-type
    /// arguments. <c>mod</c> takes the SIGN OF B (<c>A − B·⌊A/B⌋</c>), <c>rem</c> the sign of A
    /// (<c>A − B·trunc(A/B)</c>). A zero B is invalid. The caller consumed the name + <c>(</c>.</summary>
    private static bool TryParseModRem(
        ReadOnlySpan<char> s, ref int pos, in CalcContext ctx, int depth, bool isMod, out Term result)
    {
        result = default;
        if (depth >= CalcResolver.MaxDepth) return false;
        if (!TryParseSum(s, ref pos, ctx, depth + 1, out var a)) return false;
        SkipWhitespace(s, ref pos);
        if (pos >= s.Length || s[pos] != ',') return false;
        pos++;
        if (!TryParseSum(s, ref pos, ctx, depth + 1, out var b)) return false;
        SkipWhitespace(s, ref pos);
        if (pos >= s.Length || s[pos] != ')') return false;
        pos++;
        if (a.IsNumber != b.IsNumber) return false;      // §10.4: same-type arguments only.
        if (b.Value == 0.0) return false;
        var value = isMod
            ? a.Value - (b.Value * Math.Floor(a.Value / b.Value))
            : a.Value - (b.Value * Math.Truncate(a.Value / b.Value));
        result = a with { Value = value };
        return true;
    }

    /// <summary><c>abs(A)</c> / <c>sign(A)</c> — §10.7: <c>abs</c> keeps A's type; <c>sign</c>
    /// yields a unitless NUMBER (−1 / 0 / +1), so it only composes inside products — a bare
    /// <c>width: sign(…)</c> fails the length-result gate (correct per the type system). The caller
    /// consumed the name + <c>(</c>.</summary>
    private static bool TryParseAbsSign(
        ReadOnlySpan<char> s, ref int pos, in CalcContext ctx, int depth, bool isAbs, out Term result)
    {
        result = default;
        if (depth >= CalcResolver.MaxDepth) return false;
        if (!TryParseSum(s, ref pos, ctx, depth + 1, out var a)) return false;
        SkipWhitespace(s, ref pos);
        if (pos >= s.Length || s[pos] != ')') return false;
        pos++;
        // Post-PR-#159 Copilot review — Math.Sign(double.NaN) THROWS ArithmeticException;
        // the body-calc NaN context makes a %/relative argument NaN, so sign(50%) would
        // crash evaluation instead of failing cleanly. Propagate NaN (CSS Values 4 §10.9:
        // sign(NaN) is NaN) — the caller's finite gate then rejects it on the surfaced
        // path. Math.Sign handles ±∞ without throwing (±1, matching §10.7).
        result = isAbs
            ? a with { Value = Math.Abs(a.Value) }
            : new Term(double.IsNaN(a.Value) ? double.NaN : Math.Sign(a.Value), IsNumber: true);
        return true;
    }

    /// <summary>Match the <c>none</c> keyword (a clamp bound, §10.2) at <paramref name="pos"/> —
    /// case-insensitive, and only when followed by a non-identifier character (so a hypothetical
    /// <c>nonex</c> token is not half-consumed). Advances past it on success.</summary>
    private static bool TryMatchNoneKeyword(ReadOnlySpan<char> s, ref int pos)
    {
        SkipWhitespace(s, ref pos);
        if (pos + 4 > s.Length || !s.Slice(pos, 4).Equals("none", StringComparison.OrdinalIgnoreCase))
            return false;
        if (pos + 4 < s.Length && (char.IsAsciiLetterOrDigit(s[pos + 4]) || s[pos + 4] == '-'))
            return false;
        pos += 4;
        return true;
    }

    private static void SkipWhitespace(ReadOnlySpan<char> s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }
}
