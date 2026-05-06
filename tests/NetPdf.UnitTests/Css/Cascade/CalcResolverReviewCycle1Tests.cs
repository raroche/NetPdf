// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.Cascade;
using NetPdf.Css.Diagnostics;
using Xunit;

namespace NetPdf.UnitTests.Css.Cascade;

/// <summary>
/// Regression coverage for the four spec-correctness recommendations in the Task 9
/// review cycle 1 — kept in a separate file from <see cref="CalcResolverTests"/> so
/// the rationale stays attached to the rule it enforces:
/// <list type="number">
///   <item>Rec 1 — font/context-relative units defer (no hard-coded em→16px).</item>
///   <item>Rec 2 — nested math functions inside <c>calc()</c> reduce.</item>
///   <item>Rec 3 — <c>+</c> / <c>-</c> require whitespace on both sides per §10.4.</item>
///   <item>Rec 4 — same-unit dimension division yields a unitless Number; cross-class
///       division has no algebra and emits <c>CSS-CALC-INVALID-001</c>.</item>
/// </list>
/// </summary>
public sealed class CalcResolverReviewCycle1Tests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    // ============================================================
    // Rec 1 — Font / viewport / container units defer (no hard-coded ratio)
    // ============================================================

    [Theory]
    // Font-relative — need typed font context to resolve.
    [InlineData("calc(2em)")]
    [InlineData("calc(1.5rem)")]
    [InlineData("calc(2ch + 1ch)")]
    [InlineData("calc(1ex)")]
    [InlineData("calc(2lh)")]
    [InlineData("calc(1rlh)")]
    [InlineData("calc(2cap)")]
    [InlineData("calc(1ic)")]
    // Viewport-relative — need viewport context.
    [InlineData("calc(50vw)")]
    [InlineData("calc(50vh)")]
    [InlineData("calc(10svw)")]
    [InlineData("calc(10lvw)")]
    [InlineData("calc(10dvw)")]
    [InlineData("calc(10svh)")]
    [InlineData("calc(10vmin)")]
    [InlineData("calc(10vmax)")]
    // Container-relative — need container context.
    [InlineData("calc(10cqw)")]
    [InlineData("calc(10cqh)")]
    [InlineData("calc(10cqi)")]
    [InlineData("calc(10cqb)")]
    [InlineData("calc(10cqmin)")]
    [InlineData("calc(10cqmax)")]
    public void Context_relative_units_defer_verbatim(string input)
    {
        // Per CSS Values L4: these unit families need a context the cascade stage doesn't
        // have (font metrics, viewport, container). Defer = preserve text + no diagnostic.
        var sink = new CapturingSink();
        Assert.Equal(input, CalcResolver.Resolve(input, sink));
        Assert.Empty(sink.Diagnostics);
    }

    [Fact]
    public void Em_inside_addition_defers_whole_expression()
    {
        // If any operand defers, the entire calc() defers — partial reduction would
        // lose precision once the deferred operand resolves later.
        Assert.Equal("calc(1em + 8px)", CalcResolver.Resolve("calc(1em + 8px)"));
    }

    [Fact]
    public void Vh_with_px_defers_whole_expression()
    {
        Assert.Equal("calc(100vh - 50px)", CalcResolver.Resolve("calc(100vh - 50px)"));
    }

    // ============================================================
    // Rec 2 — Nested math functions reduce
    // ============================================================

    [Fact]
    public void Calc_with_nested_min_reduces()
    {
        // calc(min(8px, 4px) + 2px) → calc(4px + 2px) → 6px.
        Assert.Equal("6px", CalcResolver.Resolve("calc(min(8px, 4px) + 2px)"));
    }

    [Fact]
    public void Calc_with_nested_max_reduces()
    {
        Assert.Equal("18px", CalcResolver.Resolve("calc(max(8px, 16px) + 2px)"));
    }

    [Fact]
    public void Calc_with_nested_clamp_reduces()
    {
        // clamp(8px, 16px, 32px) = 16px → calc(16px * 2) = 32px.
        Assert.Equal("32px", CalcResolver.Resolve("calc(clamp(8px, 16px, 32px) * 2)"));
    }

    [Fact]
    public void Min_with_nested_calc_reduces()
    {
        // min(calc(8px + 4px), 16px) → min(12px, 16px) → 12px.
        Assert.Equal("12px", CalcResolver.Resolve("min(calc(8px + 4px), 16px)"));
    }

    [Fact]
    public void Max_with_nested_calc_reduces()
    {
        Assert.Equal("16px", CalcResolver.Resolve("max(calc(8px + 4px), 16px)"));
    }

    [Fact]
    public void Calc_with_nested_abs_reduces()
    {
        Assert.Equal("20px", CalcResolver.Resolve("calc(abs(-16px) + 4px)"));
    }

    [Fact]
    public void Calc_with_nested_sign_reduces()
    {
        // sign(-16px) = -1 (Number), then calc(-1 * 4px) = -4px (number * dimension).
        Assert.Equal("-4px", CalcResolver.Resolve("calc(sign(-16px) * 4px)"));
    }

    [Fact]
    public void Triple_nested_math_functions_reduce()
    {
        // max(min(8px, 16px), abs(-4px)) → max(8px, 4px) → 8px.
        Assert.Equal("8px", CalcResolver.Resolve("max(min(8px, 16px), abs(-4px))"));
    }

    [Fact]
    public void Nested_math_with_deferred_operand_defers_whole_expression()
    {
        // min(8px, 4em) defers (mixed-unit) → outer calc defers too.
        const string input = "calc(min(8px, 4em) + 2px)";
        Assert.Equal(input, CalcResolver.Resolve(input));
    }

    // ============================================================
    // Rec 3 — Whitespace required on both sides of + and - per §10.4
    // ============================================================

    [Theory]
    [InlineData("calc(1+2)")]      // no whitespace
    [InlineData("calc(1 +2)")]     // missing right-side whitespace
    [InlineData("calc(1+ 2)")]     // missing left-side whitespace
    [InlineData("calc(16px-4px)")] // unitful, no whitespace
    [InlineData("calc(16px -4px)")] // missing right whitespace (parses as two values)
    [InlineData("calc(16px- 4px)")] // missing left whitespace
    public void Plus_or_minus_without_whitespace_emits_diagnostic(string input)
    {
        // Per CSS Values L4 §10.4 the +/- operators require whitespace on both sides
        // (CSS allows `-4px` as a signed numeric, so the syntax is ambiguous without
        // the whitespace rule).
        var sink = new CapturingSink();
        var output = CalcResolver.Resolve(input, sink);
        Assert.Equal(input, output); // text preserved
        Assert.Contains(sink.Diagnostics, d => d.Code == CssDiagnosticCodes.CssCalcInvalid001);
    }

    [Theory]
    [InlineData("calc(1 + 2)", "3")]
    [InlineData("calc(16px + 4px)", "20px")]
    [InlineData("calc(16px - 4px)", "12px")]
    public void Plus_or_minus_with_whitespace_reduces(string input, string expected)
    {
        Assert.Equal(expected, CalcResolver.Resolve(input));
    }

    [Theory]
    [InlineData("calc(2*3)", "6")]
    [InlineData("calc(2 * 3)", "6")]
    [InlineData("calc(8/2)", "4")]
    [InlineData("calc(8 / 2)", "4")]
    public void Multiplication_and_division_do_not_require_whitespace(string input, string expected)
    {
        // Per §10.4, only +/- have the whitespace requirement (no ambiguity for * /).
        Assert.Equal(expected, CalcResolver.Resolve(input));
    }

    [Fact]
    public void Negative_literal_in_function_arg_does_not_need_surrounding_whitespace()
    {
        // abs(-16px) — the leading minus binds to the literal, not as a binary operator.
        Assert.Equal("16px", CalcResolver.Resolve("abs(-16px)"));
    }

    // ============================================================
    // Rec 4 — Same-unit dimension division → Number; cross-class invalid
    // ============================================================

    [Theory]
    [InlineData("calc(16px / 2px)", "8")]
    [InlineData("calc(50% / 25%)", "2")]
    [InlineData("calc(90deg / 30deg)", "3")]
    [InlineData("calc(2000ms / 500ms)", "4")]
    [InlineData("calc(8 / 2)", "4")] // already a number
    public void Same_unit_division_yields_unitless_number(string input, string expected)
    {
        // Per CSS Values L4 §10.10: dividing same-unit values cancels the dimension —
        // 16px / 2px = 8 (Number), no longer carries px. The result composes naturally
        // into further math: calc((16px / 2px) * 4px) = 32px.
        Assert.Equal(expected, CalcResolver.Resolve(input));
    }

    [Fact]
    public void Same_unit_division_composes_into_outer_arithmetic()
    {
        // (16px / 2px) = 8, then 8 * 4px = 32px.
        Assert.Equal("32px", CalcResolver.Resolve("calc((16px / 2px) * 4px)"));
    }

    [Theory]
    [InlineData("calc(16px / 2deg)")]   // length / angle
    [InlineData("calc(90deg / 2px)")]   // angle / length
    [InlineData("calc(2000ms / 100Hz)")] // time / frequency
    public void Cross_class_division_emits_diagnostic(string input)
    {
        // No unit algebra exists between dimension classes — must surface as invalid.
        var sink = new CapturingSink();
        Assert.Equal(input, CalcResolver.Resolve(input, sink));
        Assert.Contains(sink.Diagnostics, d => d.Code == CssDiagnosticCodes.CssCalcInvalid001);
    }

    [Theory]
    [InlineData("calc(16px * 2px)")]    // length * length still invalid (no area unit in CSS)
    [InlineData("calc(2deg * 3deg)")]   // angle * angle invalid
    public void Same_unit_multiplication_remains_invalid(string input)
    {
        // Multiplication has no unit-cancellation rule — `dim * dim` would need an area
        // unit (px²) which CSS doesn't define. Stays an error per §10.4.
        var sink = new CapturingSink();
        Assert.Equal(input, CalcResolver.Resolve(input, sink));
        Assert.Contains(sink.Diagnostics, d => d.Code == CssDiagnosticCodes.CssCalcInvalid001);
    }
}
