// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.Cascade;
using NetPdf.Css.Diagnostics;
using Xunit;

namespace NetPdf.UnitTests.Css.Cascade;

/// <summary>
/// Unit tests for <see cref="CalcResolver"/> — covers the full <c>calc()</c> /
/// <c>min()</c> / <c>max()</c> / <c>clamp()</c> / <c>abs()</c> / <c>sign()</c> reduction
/// pipeline per CSS Values L4 §10. Tests the v1 contract: fully reduce when possible
/// (single-unit operands), preserve verbatim when deferred (mixed % + length, viewport
/// units), emit diagnostics on syntax error / div-by-zero.
/// </summary>
public sealed class CalcResolverTests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    // ============================================================
    // No math function — pass-through
    // ============================================================

    [Fact]
    public void Value_without_math_function_passes_through()
    {
        Assert.Equal("16px solid red", CalcResolver.Resolve("16px solid red"));
    }

    [Fact]
    public void Empty_value_returns_empty()
    {
        Assert.Equal("", CalcResolver.Resolve(""));
    }

    // ============================================================
    // calc() — basic arithmetic
    // ============================================================

    [Theory]
    [InlineData("calc(16px + 4px)", "20px")]
    [InlineData("calc(20px - 4px)", "16px")]
    [InlineData("calc(8px * 2)", "16px")]
    [InlineData("calc(2 * 8px)", "16px")]
    [InlineData("calc(32px / 2)", "16px")]
    [InlineData("calc(10 + 5)", "15")]
    [InlineData("calc(10 - 5)", "5")]
    [InlineData("calc(0.5 + 0.5)", "1")]
    public void Calc_with_single_unit_reduces(string input, string expected)
    {
        Assert.Equal(expected, CalcResolver.Resolve(input));
    }

    [Fact]
    public void Calc_respects_operator_precedence()
    {
        // 2 + 3 * 4 = 14, not 20.
        Assert.Equal("14", CalcResolver.Resolve("calc(2 + 3 * 4)"));
    }

    [Fact]
    public void Calc_parentheses_override_precedence()
    {
        // (2 + 3) * 4 = 20.
        Assert.Equal("20", CalcResolver.Resolve("calc((2 + 3) * 4)"));
    }

    [Fact]
    public void Calc_with_explicit_unit_conversion()
    {
        // 1in = 96px → calc(1in + 4px) = 100px.
        Assert.Equal("100px", CalcResolver.Resolve("calc(1in + 4px)"));
    }

    [Fact]
    public void Calc_with_em_defers_to_typed_value_stage()
    {
        // Per CSS Values L4 §10.4: font-relative units (em/rem/ch/ex/lh/rlh/cap/ic)
        // can't be resolved at compute time without a typed font context. The resolver
        // defers — preserves the source text verbatim for the layout-time pipeline to
        // re-reduce once font metrics are known. No diagnostic: deferral is normal.
        Assert.Equal("calc(2em)", CalcResolver.Resolve("calc(2em)"));
        Assert.Equal("calc(1rem + 16px)", CalcResolver.Resolve("calc(1rem + 16px)"));
    }

    [Fact]
    public void Calc_with_negative_result()
    {
        Assert.Equal("-4px", CalcResolver.Resolve("calc(4px - 8px)"));
    }

    // ============================================================
    // calc() — deferred (mixed-unit) preserved verbatim
    // ============================================================

    [Theory]
    [InlineData("calc(100% - 16px)")]
    [InlineData("calc(50% + 8px)")]
    [InlineData("calc(100vh - 50px)")]
    [InlineData("calc(2vw + 16px)")]
    public void Calc_with_deferred_operands_preserves_text(string input)
    {
        // Mixed % + px or viewport units present → can't reduce at compute time.
        Assert.Equal(input, CalcResolver.Resolve(input));
    }

    [Fact]
    public void Calc_with_only_percent_reduces_in_unit()
    {
        // 100% + 50% = 150% — same unit, can reduce.
        Assert.Equal("150%", CalcResolver.Resolve("calc(100% + 50%)"));
    }

    // ============================================================
    // min / max / clamp
    // ============================================================

    [Fact]
    public void Min_picks_smallest()
    {
        Assert.Equal("4px", CalcResolver.Resolve("min(16px, 8px, 4px)"));
    }

    [Fact]
    public void Max_picks_largest()
    {
        Assert.Equal("16px", CalcResolver.Resolve("max(16px, 8px, 4px)"));
    }

    [Fact]
    public void Clamp_three_arg_picks_middle_value()
    {
        // clamp(min, val, max) = max(min, min(val, max))
        Assert.Equal("16px", CalcResolver.Resolve("clamp(8px, 16px, 32px)"));
    }

    [Fact]
    public void Clamp_value_below_min_returns_min()
    {
        Assert.Equal("8px", CalcResolver.Resolve("clamp(8px, 4px, 32px)"));
    }

    [Fact]
    public void Clamp_value_above_max_returns_max()
    {
        Assert.Equal("32px", CalcResolver.Resolve("clamp(8px, 64px, 32px)"));
    }

    [Fact]
    public void Min_with_mixed_units_is_deferred()
    {
        // min(100%, 800px) — can't pick at compute time, preserve.
        const string input = "min(100%, 800px)";
        Assert.Equal(input, CalcResolver.Resolve(input));
    }

    [Fact]
    public void Clamp_with_wrong_arg_count_emits_diagnostic_and_preserves()
    {
        var sink = new CapturingSink();
        const string input = "clamp(8px, 16px)"; // 2 args, needs 3
        var output = CalcResolver.Resolve(input, sink);
        Assert.Equal(input, output);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssCalcInvalid001);
    }

    // ============================================================
    // abs / sign
    // ============================================================

    [Theory]
    [InlineData("abs(-16px)", "16px")]
    [InlineData("abs(16px)", "16px")]
    [InlineData("abs(-0.5)", "0.5")]
    public void Abs_returns_magnitude(string input, string expected)
    {
        Assert.Equal(expected, CalcResolver.Resolve(input));
    }

    [Theory]
    [InlineData("sign(16px)", "1")]
    [InlineData("sign(-16px)", "-1")]
    [InlineData("sign(0)", "0")]
    public void Sign_returns_polarity_as_unitless_number(string input, string expected)
    {
        Assert.Equal(expected, CalcResolver.Resolve(input));
    }

    // ============================================================
    // Diagnostics
    // ============================================================

    [Fact]
    public void Division_by_zero_emits_diagnostic_and_preserves_text()
    {
        var sink = new CapturingSink();
        const string input = "calc(16px / 0)";
        var output = CalcResolver.Resolve(input, sink);
        Assert.Equal(input, output);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssCalcDivByZero001);
    }

    [Fact]
    public void Multiplying_two_dimensioned_values_emits_diagnostic()
    {
        // 16px * 2px is invalid per L4 §10.4.
        var sink = new CapturingSink();
        const string input = "calc(16px * 2px)";
        var output = CalcResolver.Resolve(input, sink);
        Assert.Equal(input, output);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssCalcInvalid001);
    }

    [Fact]
    public void Calc_same_unit_dimension_division_returns_unitless_number()
    {
        // Per CSS Values L4 §10.10: dividing two values of the same unit cancels the
        // dimension and yields a Number. calc(16px / 2px) = 8, calc(50% / 25%) = 2.
        Assert.Equal("8", CalcResolver.Resolve("calc(16px / 2px)"));
        Assert.Equal("2", CalcResolver.Resolve("calc(50% / 25%)"));
    }

    [Fact]
    public void Calc_dividing_by_dimensioned_value_of_different_class_emits_diagnostic()
    {
        // Cross-class division (e.g. length / angle) has no unit-algebra solution per L4.
        var sink = new CapturingSink();
        const string input = "calc(16px / 2deg)";
        var output = CalcResolver.Resolve(input, sink);
        Assert.Equal(input, output);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssCalcInvalid001);
    }

    [Theory]
    [InlineData("calc(16px +)")]            // dangling operator
    [InlineData("calc()")]                   // empty
    [InlineData("calc(16px @ 4px)")]         // unknown operator
    [InlineData("calc(16px 4px)")]           // missing operator
    [InlineData("min()")]                    // no args
    public void Malformed_expressions_emit_diagnostic(string input)
    {
        var sink = new CapturingSink();
        var output = CalcResolver.Resolve(input, sink);
        // Original text preserved (for layout to react).
        Assert.Equal(input, output);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssCalcInvalid001);
    }

    // ============================================================
    // Case-insensitive function names + nested context
    // ============================================================

    [Theory]
    [InlineData("CALC(16px + 4px)", "20px")]
    [InlineData("Calc(16px + 4px)", "20px")]
    [InlineData("MIN(16px, 4px)", "4px")]
    [InlineData("Clamp(8px, 16px, 32px)", "16px")]
    public void Function_names_are_ascii_case_insensitive(string input, string expected)
    {
        Assert.Equal(expected, CalcResolver.Resolve(input));
    }

    [Fact]
    public void Math_function_inside_other_text_is_substituted()
    {
        // Inline-block context: padding shorthand could carry math.
        Assert.Equal("16px solid red",
            CalcResolver.Resolve("calc(8px + 8px) solid red"));
    }

    [Fact]
    public void Math_function_inside_quoted_string_is_not_substituted()
    {
        Assert.Equal("\"calc(2 + 2)\"", CalcResolver.Resolve("\"calc(2 + 2)\""));
    }

    // ============================================================
    // sign() boundary
    // ============================================================

    [Fact]
    public void Sign_of_zero_returns_zero()
    {
        Assert.Equal("0", CalcResolver.Resolve("sign(0)"));
    }

    [Fact]
    public void Sign_of_positive_decimal_returns_one()
    {
        Assert.Equal("1", CalcResolver.Resolve("sign(0.001)"));
    }
}
