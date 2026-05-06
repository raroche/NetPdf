// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.Cascade;
using NetPdf.Css.Diagnostics;
using Xunit;

namespace NetPdf.UnitTests.Css.Cascade;

/// <summary>
/// Unit tests for <see cref="VarSubstitution"/> — verifies the var() scanner per CSS
/// Custom Properties L1 §3 + §3.5: name lookup against the resolved table, fallback
/// substitution when the name is missing, nested var() in fallbacks, quote-aware
/// scanning so var()-looking text inside string literals stays untouched, and circular
/// reference detection.
/// </summary>
public sealed class VarSubstitutionTests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    private static CustomPropertyTable Table(params (string Name, string Value)[] entries)
    {
        var t = new CustomPropertyTable(parent: null);
        foreach (var (n, v) in entries) t.Set(n, v);
        return t;
    }

    [Fact]
    public void Value_without_var_passes_through_unchanged()
    {
        var result = VarSubstitution.SubstituteToString("16px solid red", Table());
        Assert.Equal("16px solid red", result);
    }

    [Fact]
    public void Var_replaced_with_resolved_value()
    {
        var result = VarSubstitution.SubstituteToString("var(--color)", Table(("--color", "red")));
        Assert.Equal("red", result);
    }

    [Fact]
    public void Var_with_surrounding_text_preserves_context()
    {
        var result = VarSubstitution.SubstituteToString("16px solid var(--color)",
            Table(("--color", "red")));
        Assert.Equal("16px solid red", result);
    }

    [Fact]
    public void Multiple_vars_in_one_value_all_resolve()
    {
        var result = VarSubstitution.SubstituteToString(
            "var(--width) solid var(--color)",
            Table(("--width", "2px"), ("--color", "red")));
        Assert.Equal("2px solid red", result);
    }

    [Fact]
    public void Missing_name_with_fallback_uses_fallback()
    {
        var result = VarSubstitution.SubstituteToString("var(--missing, blue)", Table());
        Assert.Equal("blue", result);
    }

    [Fact]
    public void Missing_name_no_fallback_uses_unset_sentinel()
    {
        var result = VarSubstitution.SubstituteToString("var(--missing)", Table());
        Assert.Equal(VarSubstitution.UnsetSentinel, result);
    }

    [Fact]
    public void Nested_var_in_fallback_resolves()
    {
        var result = VarSubstitution.SubstituteToString(
            "var(--missing, var(--secondary))",
            Table(("--secondary", "blue")));
        Assert.Equal("blue", result);
    }

    [Fact]
    public void Nested_var_in_fallback_when_both_missing_uses_outer_unset()
    {
        var result = VarSubstitution.SubstituteToString(
            "var(--missing, var(--also-missing))",
            Table());
        Assert.Equal(VarSubstitution.UnsetSentinel, result);
    }

    [Fact]
    public void Var_text_inside_double_quoted_string_is_not_substituted()
    {
        // content: "var(--x)" — the "var(--x)" is a literal string value, NOT a real var().
        var result = VarSubstitution.SubstituteToString(
            "\"var(--x)\"",
            Table(("--x", "red")));
        Assert.Equal("\"var(--x)\"", result);
    }

    [Fact]
    public void Var_text_inside_single_quoted_string_is_not_substituted()
    {
        var result = VarSubstitution.SubstituteToString(
            "'var(--x)'",
            Table(("--x", "red")));
        Assert.Equal("'var(--x)'", result);
    }

    [Fact]
    public void Custom_property_value_containing_var_resolves_recursively()
    {
        // --primary: var(--brand)  → primary should resolve to brand's value.
        var result = VarSubstitution.SubstituteToString(
            "var(--primary)",
            Table(("--primary", "var(--brand)"), ("--brand", "red")));
        Assert.Equal("red", result);
    }

    [Fact]
    public void Direct_circular_reference_emits_diagnostic_and_uses_unset()
    {
        // --a: var(--a) — direct cycle.
        var sink = new CapturingSink();
        var result = VarSubstitution.SubstituteToString(
            "var(--a)",
            Table(("--a", "var(--a)")),
            sink);
        Assert.Equal(VarSubstitution.UnsetSentinel, result);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssVarCircular001);
    }

    [Fact]
    public void Indirect_circular_reference_emits_diagnostic()
    {
        // --a: var(--b); --b: var(--a) — indirect cycle.
        var sink = new CapturingSink();
        var result = VarSubstitution.SubstituteToString(
            "var(--a)",
            Table(("--a", "var(--b)"), ("--b", "var(--a)")),
            sink);
        Assert.Equal(VarSubstitution.UnsetSentinel, result);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssVarCircular001);
    }

    [Fact]
    public void Three_step_circular_reference_emits_diagnostic()
    {
        // --a: var(--b); --b: var(--c); --c: var(--a)
        var sink = new CapturingSink();
        var result = VarSubstitution.SubstituteToString(
            "var(--a)",
            Table(("--a", "var(--b)"), ("--b", "var(--c)"), ("--c", "var(--a)")),
            sink);
        Assert.Equal(VarSubstitution.UnsetSentinel, result);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssVarCircular001);
    }

    [Fact]
    public void Circular_reference_with_fallback_uses_fallback()
    {
        // --a: var(--a, blue) — cycle but with fallback. Fallback used.
        var sink = new CapturingSink();
        var result = VarSubstitution.SubstituteToString(
            "var(--a)",
            Table(("--a", "var(--a, blue)")),
            sink);
        Assert.Equal("blue", result);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssVarCircular001);
    }

    [Fact]
    public void Fallback_with_commas_uses_only_first_top_level_split()
    {
        // var(--missing, rgb(255, 0, 0)) — the commas inside rgb() are NOT separators.
        var result = VarSubstitution.SubstituteToString(
            "var(--missing, rgb(255, 0, 0))",
            Table());
        Assert.Equal("rgb(255, 0, 0)", result);
    }

    [Fact]
    public void Unterminated_var_passes_through_verbatim()
    {
        // No matching close paren — pass the rest of the value through verbatim. Browsers
        // tolerate this; we mirror the behavior to avoid eating unrelated CSS.
        var result = VarSubstitution.SubstituteToString("var(--x", Table());
        Assert.StartsWith("var(--x", result);
    }

    [Fact]
    public void Var_with_invalid_name_uses_fallback()
    {
        // Name without -- prefix is invalid → fallback (or unset).
        var result = VarSubstitution.SubstituteToString(
            "var(notadash, red)",
            Table());
        Assert.Equal("red", result);
    }

    [Fact]
    public void Empty_value_returns_empty()
    {
        Assert.Equal("", VarSubstitution.SubstituteToString("", Table()));
    }

    [Fact]
    public void Whitespace_around_name_tolerated()
    {
        var result = VarSubstitution.SubstituteToString(
            "var( --color , blue)",
            Table(("--color", "red")));
        Assert.Equal("red", result);
    }
}
