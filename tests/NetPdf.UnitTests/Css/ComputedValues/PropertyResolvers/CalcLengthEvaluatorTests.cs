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
    public void TryEvaluate_resolves_min_max_clamp(string raw, double expectedPx)
    {
        Assert.True(CalcLengthEvaluator.TryEvaluate(raw, Ctx, out var px), raw);
        Assert.Equal(expectedPx, px, 3);
    }

    [Theory]
    [InlineData("min()")]                  // no arguments
    [InlineData("min(10px, 5)")]           // mixed-type arguments (§10.4)
    [InlineData("clamp(10px, 20px)")]      // clamp takes exactly three
    [InlineData("clamp(10px, 20px, 30px, 40px)")]
    [InlineData("min(10px 20px)")]         // missing comma
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
    [InlineData("calc(round(1px))")]    // round()/mod()/abs() etc. — unsupported
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
    public void TryEvaluate_rejects_a_body_beyond_the_shared_length_cap()
    {
        // Post-PR-#157 review P2: the evaluator shares CalcResolver.MaxBodyLength — a huge flat
        // operand chain (breadth, which the depth cap alone wouldn't catch) is rejected up front
        // instead of burning CPU.
        var manyTerms = "calc(10px" + string.Concat(
            System.Linq.Enumerable.Repeat(" + 10px", NetPdf.Css.Cascade.CalcResolver.MaxBodyLength / 7)) + ")";
        Assert.True(manyTerms.Length > NetPdf.Css.Cascade.CalcResolver.MaxBodyLength);
        Assert.False(CalcLengthEvaluator.TryEvaluate(manyTerms, Ctx, out _));
    }
}
