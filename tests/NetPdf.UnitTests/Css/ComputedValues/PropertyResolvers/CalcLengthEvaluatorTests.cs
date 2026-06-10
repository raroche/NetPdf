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
    [InlineData("calc(1e2px + 0px)", 100.0)]      // e-notation number (CSS Values §4.1)
    public void TryEvaluate_resolves_supported_expressions(string raw, double expectedPx)
    {
        Assert.True(CalcLengthEvaluator.TryEvaluate(raw, Ctx, out var px), raw);
        Assert.Equal(expectedPx, px, 3);
    }

    [Theory]
    [InlineData("calc(10px+5px)")]      // §10.3: + must be surrounded by whitespace
    [InlineData("calc(10px -5px)")]     // §10.3: - needs whitespace on BOTH sides
    [InlineData("calc(10px * 5px)")]    // length × length has no CSS type (§10.4)
    [InlineData("calc(10px / 0)")]      // division by zero (§10.4)
    [InlineData("calc(10px / 2px)")]    // the divisor must be a number (§10.4)
    [InlineData("calc(10px + 5)")]      // mixed-type sum (length + number, §10.4)
    [InlineData("calc(5)")]             // a bare-number result is not a length
    [InlineData("calc(10cqw + 5px)")]   // container units — unsupported
    [InlineData("calc(min(1px, 2px))")] // min()/max()/clamp() — unsupported
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
    [InlineData("calc(", false)]
    [InlineData("10px", false)]
    [InlineData("", false)]
    public void IsCalc_matches_the_calc_shape(string raw, bool expected)
    {
        Assert.Equal(expected, CalcLengthEvaluator.IsCalc(raw));
    }

    [Fact]
    public void TryEvaluate_intermediates_may_be_negative_only_the_result_clamps()
    {
        // (10px − 30px) + 25px = 5px — the negative INTERMEDIATE must not be rejected or clamped
        // mid-expression (§10.5 clamps at used-value time only).
        Assert.True(CalcLengthEvaluator.TryEvaluate("calc((10px - 30px) + 25px)", Ctx, out var px));
        Assert.Equal(5.0, px, 3);
    }
}
