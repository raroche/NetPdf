// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues.PropertyResolvers;
using Xunit;

namespace NetPdf.UnitTests.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Unit tests for <see cref="CalcLengthEvaluator"/> — the CSS Values 4 §10 <c>calc()</c> length
/// evaluator (calc cycle; first consumer: margin-box explicit <c>width</c>/<c>height</c> + padding).
/// </summary>
public sealed class CalcLengthEvaluatorTests
{
    // Percent base 400, em base 20, root em 10, viewport 800 × 600.
    private static readonly CalcLengthEvaluator.CalcContext Ctx = new(400.0, 20.0, 10.0, 800.0, 600.0);

    [Theory]
    [InlineData("calc(10px + 5px)", 15.0)]
    [InlineData("calc(100% - 10px)", 390.0)]      // 400 − 10
    [InlineData("calc(50% + 2em)", 240.0)]        // 200 + 40
    [InlineData("calc(2 * 10px)", 20.0)]          // number × length
    [InlineData("calc(10px * 2)", 20.0)]          // length × number
    [InlineData("calc(100px / 4)", 25.0)]         // length ÷ number
    [InlineData("calc((10px + 5px) * 2)", 30.0)]  // parenthesized sub-sum
    [InlineData("calc(calc(10px + 5px) * 2)", 30.0)]   // nested calc() ≡ parens (§10.1)
    [InlineData("calc(1in - 24pt)", 64.0)]        // absolute units: 96 − 32
    [InlineData("calc(10vw - 1rem)", 70.0)]       // 80 − 10
    [InlineData("calc(10px - 20px)", 0.0)]        // §10.5: the used value range-clamps to ≥ 0
    [InlineData("CALC(10px + 5px)", 15.0)]        // function name is case-insensitive
    [InlineData("calc(1IN - 24PT)", 64.0)]        // UNITS are case-insensitive too (CSS Syntax §4;
    [InlineData("calc(10PX + 2px)", 12.0)]        //  post-PR-#157 review P2 — absolute-unit lookup)
    [InlineData("calc(2EM + 10Px)", 50.0)]        // relative units uppercase: 2 × 20 + 10
    [InlineData("calc(1e2px + 0px)", 100.0)]      // e-notation number (CSS Values §4.1)
    [InlineData("calc(10px / 1e-13)", 1e14)]      // a TINY non-zero divisor is valid (§10.4 — only
                                                  //  exact zero is division by zero; Copilot review)
    public void TryEvaluate_resolves_supported_expressions(string raw, double expectedPx)
    {
        Assert.True(CalcLengthEvaluator.TryEvaluate(raw, Ctx, out var px), raw);
        Assert.Equal(expectedPx, px, 3);
    }

    [Theory]
    [InlineData("min(50%, 300px)", 200.0)]            // §10.2 — standalone (no calc() wrapper needed)
    [InlineData("max(10px, 2em, 1rem)", 40.0)]        // 1+ args; the largest of 10/40/10
    [InlineData("min(10px)", 10.0)]                   // a single argument is valid
    [InlineData("clamp(50px, 10px, 200px)", 50.0)]    // VAL below MIN → MIN
    [InlineData("clamp(50px, 100px, 200px)", 100.0)]  // VAL within → VAL
    [InlineData("clamp(50px, 999px, 200px)", 200.0)]  // VAL above MAX → MAX
    [InlineData("clamp(200px, 100px, 50px)", 200.0)]  // MIN > MAX → MIN wins (max(MIN, min(VAL, MAX)))
    [InlineData("calc(min(10px, 2em) + 5px)", 15.0)]  // nested inside calc (min(10, 40) + 5)
    [InlineData("MIN(10px, 20px)", 10.0)]             // function names are case-insensitive
    [InlineData("calc(100% - max(20px, 10px))", 380.0)]
    [InlineData("clamp(none, 5vw, 24px)", 24.0)]      // §10.2: `none` MIN → min(VAL, MAX); 5vw = 40
    [InlineData("clamp(12px, 5vw, none)", 40.0)]      // `none` MAX → max(MIN, VAL)
    [InlineData("clamp(NONE, 5vw, none)", 40.0)]      // both bounds none (case-insensitive) → VAL
    public void TryEvaluate_resolves_min_max_clamp(string raw, double expectedPx)
    {
        Assert.True(CalcLengthEvaluator.TryEvaluate(raw, Ctx, out var px), raw);
        Assert.Equal(expectedPx, px, 3);
    }

    [Theory]
    [InlineData("round(7px, 2px)", 8.0)]              // §10.6 nearest (default): 3.5 ties UP → 4 × 2
    [InlineData("round(nearest, 7px, 2px)", 8.0)]     // explicit strategy keyword
    [InlineData("round(up, 11px, 10px)", 20.0)]       // toward +∞
    [InlineData("round(down, 19px, 10px)", 10.0)]     // toward −∞
    [InlineData("round(to-zero, 19px, 10px)", 10.0)]  // toward zero
    [InlineData("round(7, 2)", 8.0, true)]            // number args (B may default/match) — number result
    [InlineData("mod(7px, 3px)", 1.0)]                // §10.6: A − B·⌊A/B⌋
    [InlineData("calc(mod(0px - 7px, 3px) + 0px)", 2.0)]   // mod takes B's sign: −7 mod 3 = 2
    [InlineData("rem(7px, 3px)", 1.0)]                // §10.6: A − B·trunc(A/B)
    [InlineData("calc(rem(0px - 7px, 3px) + 5px)", 4.0)]   // rem takes A's sign: −1 + 5
    [InlineData("abs(0px - 7px)", 7.0)]               // §10.7: |−7px|
    [InlineData("calc(10px * sign(0px - 5px))", 0.0)] // sign → −1 (number) → −10px → §10.5 clamp
    [InlineData("calc(10px * sign(5px) + 2px)", 12.0)]
    [InlineData("ROUND(UP, 1px, 10px)", 10.0)]        // names + strategies are case-insensitive
    // Post-PR-#159 Copilot review — a NEGATIVE step normalizes to |B| (multiples of B and |B|
    // are the same set); pre-fix the flipped ratio inverted the strategies + the tie-break.
    [InlineData("round(nearest, 5px, -2px)", 6.0)]    // tie between 4 and 6 → toward +∞ (was 4)
    [InlineData("round(up, 11px, -10px)", 20.0)]      // up stays toward +∞ (was 10)
    [InlineData("round(down, 11px, -10px)", 10.0)]
    [InlineData("calc(round(nearest, 0px - 5px, -2px) + 10px)", 6.0)]  // −5 ties −6/−4 → −4; +10
    public void TryEvaluate_resolves_stepped_and_sign_functions(string raw, double expectedPx, bool isNumber = false)
    {
        if (isNumber)
        {
            // A number-typed result is rejected as a LENGTH at the top level — assert via composition.
            Assert.True(CalcLengthEvaluator.TryEvaluate($"calc(1px * {raw})", Ctx, out var composed), raw);
            Assert.Equal(expectedPx, composed, 3);
            return;
        }
        Assert.True(CalcLengthEvaluator.TryEvaluate(raw, Ctx, out var px), raw);
        Assert.Equal(expectedPx, px, 3);
    }

    [Theory]
    [InlineData("round(7px)")]            // B defaults to the NUMBER 1 → type mismatch with a length A
    [InlineData("round(7px, 2)")]         // mixed-type arguments (§10.4)
    [InlineData("mod(7px, 0px)")]         // a zero step/divisor is invalid
    [InlineData("rem(7px, 0px)")]
    [InlineData("round(7px, 0px)")]
    [InlineData("sign(5px)")]             // sign yields a NUMBER — invalid as a whole length value
    [InlineData("mod(7px)")]              // mod/rem take exactly two arguments
    [InlineData("round(sideways, 7px, 2px)")]   // unknown strategy keyword
    public void TryEvaluate_rejects_invalid_stepped_and_sign_functions(string raw)
    {
        Assert.False(CalcLengthEvaluator.TryEvaluate(raw, Ctx, out _), raw);
    }

    [Theory]
    // §10.8 trigonometric — a NUMBER argument is radians; an <angle> dimension canonicalizes to
    // radians (360deg = 400grad = 1turn = 2π rad); the result is a NUMBER (composes in products).
    [InlineData("calc(100px * cos(0))", 100.0)]
    [InlineData("calc(10px * sin(pi / 2))", 10.0)]            // pi constant; number arg = radians
    [InlineData("calc(10px * sin(30deg) + 5px)", 10.0)]       // sin 30° = 0.5
    [InlineData("calc(10px * tan(45deg))", 10.0)]
    [InlineData("calc(10px * cos(200grad) + 20px)", 10.0)]    // cos 180° = −1
    [InlineData("calc(10px * sin(0.25turn))", 10.0)]
    [InlineData("calc(10px * sin(45deg + 45deg))", 10.0)]     // angle + angle (same-type sum)
    [InlineData("calc(10px * sin(asin(0.5)) * 2)", 10.0)]     // inverse trig yields an ANGLE arg
    [InlineData("calc(10px * sin(atan2(1, 1)) * 2)", 14.142)] // atan2 → 45°; ×2·sin → √2 ×10
    [InlineData("calc(10px * sign(45deg))", 10.0)]            // §10.7 sign accepts any type
    // §10.9 exponential — number-typed except hypot, which keeps its arguments' type.
    [InlineData("hypot(30px, 40px)", 50.0)]                   // length args → LENGTH (whole value!)
    [InlineData("hypot(5px)", 5.0)]                           // single argument is valid
    [InlineData("HYPOT(3px, 4px)", 5.0)]                      // names are case-insensitive
    [InlineData("calc(1px * pow(2, 10))", 1024.0)]
    [InlineData("calc(1px * sqrt(144))", 12.0)]
    [InlineData("calc(1px * log(e))", 1.0)]                   // e constant; log defaults to base e
    [InlineData("calc(1px * log(8, 2))", 3.0)]
    [InlineData("calc(1px * exp(0))", 1.0)]
    [InlineData("calc(1px * pi)", 3.1416)]
    [InlineData("calc(2em * cos(0))", 40.0)]                  // relative term × trig (em base 20)
    public void TryEvaluate_resolves_trig_and_exponential_functions(string raw, double expectedPx)
    {
        Assert.True(CalcLengthEvaluator.TryEvaluate(raw, Ctx, out var px), raw);
        Assert.Equal(expectedPx, px, 3);
    }

    [Theory]
    [InlineData("sin(10px)")]                  // trig argument must be a number or angle
    [InlineData("calc(10px * sin(5px))")]
    [InlineData("atan(1)")]                    // an ANGLE result is invalid as a whole length
    [InlineData("calc(45deg)")]                // …and as a calc() result
    [InlineData("calc(10px + 45deg)")]         // §10.4: length + angle is a type mismatch
    [InlineData("pow(2px, 2)")]                // pow/sqrt/log/exp take numbers only
    [InlineData("pow(2)")]                     // pow takes exactly two arguments
    [InlineData("sqrt(4px)")]
    [InlineData("calc(1px * log(2px))")]
    [InlineData("hypot(1px, 2)")]              // §10.4: same-type arguments only
    [InlineData("hypot()")]                    // at least one argument
    [InlineData("calc(1px * asin(1px))")]      // inverse trig takes a number
    [InlineData("cosh(1)")]                    // §10.8 hyperbolic — not a CSS math function
    [InlineData("calc(e)")]                    // a bare-number result is not a length
    public void TryEvaluate_rejects_invalid_trig_and_exponential_functions(string raw)
    {
        Assert.False(CalcLengthEvaluator.TryEvaluate(raw, Ctx, out _), raw);
    }

    [Fact]
    public void Sign_of_a_NaN_context_term_fails_cleanly_instead_of_throwing()
    {
        // Post-PR-#159 Copilot review — Math.Sign(double.NaN) THROWS; the body-calc NaN
        // context makes a %/relative sign() argument NaN, so `calc(10px * sign(50%))` on a
        // body property crashed the whole convert instead of failing to the surfaced path.
        var nanCtx = new CalcLengthEvaluator.CalcContext(
            double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        Assert.False(CalcLengthEvaluator.TryEvaluate("calc(10px * sign(50%))", nanCtx, out _));
    }

    [Theory]
    [InlineData("min()")]                  // no arguments
    [InlineData("min(10px, 5)")]           // mixed-type arguments (§10.4)
    [InlineData("clamp(10px, 20px)")]      // clamp takes exactly three
    [InlineData("clamp(10px, 20px, 30px, 40px)")]
    [InlineData("min(10px 20px)")]         // missing comma
    [InlineData("clamp(12px, none, 24px)")]   // `none` is a BOUNDS keyword — never the center VAL
    [InlineData("min(none, 1px)")]            // …and clamp-only (§10.2)
    public void TryEvaluate_rejects_invalid_min_max_clamp(string raw)
    {
        Assert.False(CalcLengthEvaluator.TryEvaluate(raw, Ctx, out _), raw);
    }

    [Theory]
    [InlineData("calc(10px+5px)")]      // §10.3: + must be surrounded by whitespace
    [InlineData("calc(10px -5px)")]     // §10.3: - needs whitespace on BOTH sides
    [InlineData("calc(10px * 5px)")]    // length × length has no CSS type (§10.4)
    [InlineData("calc(10px / 0)")]      // division by EXACTLY zero (§10.4)
    [InlineData("calc(10px / 0.0)")]
    [InlineData("calc(10px / 2px)")]    // the divisor must be a number (§10.4)
    [InlineData("calc(1px / 1e-310)")]  // a non-finite QUOTIENT is rejected at the final gate
    [InlineData("calc(10px + 5)")]      // mixed-type sum (length + number, §10.4)
    [InlineData("calc(5)")]             // a bare-number result is not a length
    [InlineData("calc(10cqw + 5px)")]   // container units — unsupported
    [InlineData("calc(round(1px))")]    // B defaults to the NUMBER 1 → type mismatch with a length A
    [InlineData("calc(10px")]           // unbalanced — not even calc-shaped
    [InlineData("calc()")]
    [InlineData("10px")]                // not a calc() at all
    public void TryEvaluate_rejects_invalid_or_unsupported_expressions(string raw)
    {
        Assert.False(CalcLengthEvaluator.TryEvaluate(raw, Ctx, out _), raw);
    }

    [Theory]
    [InlineData("calc(100% - 10px)", true)]
    [InlineData("CALC(1px)", true)]
    [InlineData("min(1px, 2px)", true)]     // §10.2 functions are whole-value math functions too
    [InlineData("max(1px, 2px)", true)]
    [InlineData("clamp(1px, 2px, 3px)", true)]
    [InlineData("calc(", false)]
    [InlineData("min(1px", false)]
    [InlineData("10px", false)]
    [InlineData("", false)]
    public void IsMathFunction_matches_the_math_function_shape(string raw, bool expected)
    {
        Assert.Equal(expected, CalcLengthEvaluator.IsMathFunction(raw));
    }

    [Fact]
    public void TryEvaluate_intermediates_may_be_negative_only_the_result_clamps()
    {
        // (10px − 30px) + 25px = 5px — the negative INTERMEDIATE must not be rejected or clamped
        // mid-expression (§10.5 clamps at used-value time only).
        Assert.True(CalcLengthEvaluator.TryEvaluate("calc((10px - 30px) + 25px)", Ctx, out var px));
        Assert.Equal(5.0, px, 3);
    }

    [Fact]
    public void TryEvaluate_rejects_nesting_beyond_the_shared_depth_cap()
    {
        // Post-PR-#157 review P2: the evaluator shares CalcResolver.MaxDepth so pathological nesting
        // can't grow the recursion unboundedly. The OUTER calc( consumes the first depth level
        // (min/max/clamp cycle — the whole value parses as one math-function <value>), so
        // MaxDepth − 1 inner parens still evaluate and MaxDepth inner parens reject.
        string Nested(int parens) =>
            "calc(" + new string('(', parens) + "10px" + new string(')', parens) + ")";
        Assert.True(CalcLengthEvaluator.TryEvaluate(Nested(NetPdf.Css.Cascade.CalcResolver.MaxDepth - 1), Ctx, out var px));
        Assert.Equal(10.0, px, 3);
        Assert.False(CalcLengthEvaluator.TryEvaluate(Nested(NetPdf.Css.Cascade.CalcResolver.MaxDepth), Ctx, out _));
    }

    [Fact]
    public void TryEvaluate_caps_the_function_BODY_length_at_the_exact_boundary()
    {
        // Post-PR-#157 review P2 + post-PR-#158 review P3: the cap measures the function BODY (the
        // text between the outer parens) — the same measure CalcResolver.MaxBodyLength is defined
        // for — NOT the full raw with the name + parens. A body of exactly MaxBodyLength evaluates;
        // one more char rejects.
        string WithBodyLength(int length) =>
            "calc(1px" + new string(' ', length - 3) + ")";   // body = "1px" + padding spaces
        var max = NetPdf.Css.Cascade.CalcResolver.MaxBodyLength;
        Assert.True(CalcLengthEvaluator.TryEvaluate(WithBodyLength(max), Ctx, out var px));
        Assert.Equal(1.0, px, 3);
        Assert.False(CalcLengthEvaluator.TryEvaluate(WithBodyLength(max + 1), Ctx, out _));
    }
}
