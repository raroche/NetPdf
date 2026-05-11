// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace NetPdf.UnitTests.Diagnostics;

/// <summary>
/// Parity tests between <see cref="DiagnosticCodes"/> constants and the registry doc at
/// <c>docs/diagnostics-codes.md</c>. The doc is the single source of truth for code names
/// and severities; the constants are emit-side conveniences. If they ever drift, every
/// diagnostic emitted via the constant misses the registry — these tests catch that.
/// </summary>
public sealed class DiagnosticCodesTests
{
    [Fact]
    public void Html_script_ignored_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(
            registry,
            @"\|\s*`?(HTML-SCRIPT-IGNORED-001)`?\s*\|\s*(\w+)\s*\|");

        Assert.True(match.Success, "HTML-SCRIPT-IGNORED-001 row not found in docs/diagnostics-codes.md");

        // Get the typed constant via reflection (DiagnosticCodes is internal — InternalsVisibleTo
        // already covers NetPdf.UnitTests, so a direct ref also works; reflection here keeps the
        // test resilient to future renames of the constant identifier itself.)
        var constantValue = typeof(DiagnosticCodes)
            .GetField(nameof(DiagnosticCodes.HtmlScriptIgnored001), BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null) as string;

        Assert.Equal(match.Groups[1].Value, constantValue);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Html_script_ignored_001_constant_value_is_stable()
    {
        // Codes are documented as stable once published. Pin the literal so a cosmetic
        // rename of the constant identifier never silently changes the wire format.
        Assert.Equal("HTML-SCRIPT-IGNORED-001", DiagnosticCodes.HtmlScriptIgnored001);
    }

    [Fact]
    public void Html_javascript_url_ignored_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(
            registry,
            @"\|\s*`?(HTML-JAVASCRIPT-URL-IGNORED-001)`?\s*\|\s*(\w+)\s*\|");

        Assert.True(match.Success, "HTML-JAVASCRIPT-URL-IGNORED-001 row not found in docs/diagnostics-codes.md");

        var constantValue = typeof(DiagnosticCodes)
            .GetField(nameof(DiagnosticCodes.HtmlJavaScriptUrlIgnored001), BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null) as string;

        Assert.Equal(match.Groups[1].Value, constantValue);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Html_javascript_url_ignored_001_constant_value_is_stable()
    {
        Assert.Equal("HTML-JAVASCRIPT-URL-IGNORED-001", DiagnosticCodes.HtmlJavaScriptUrlIgnored001);
    }

    [Fact]
    public void Css_parse_warning_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(
            registry,
            @"\|\s*`?(CSS-PARSE-WARNING-001)`?\s*\|\s*(\w+)\s*\|");

        Assert.True(match.Success, "CSS-PARSE-WARNING-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.CssParseWarning001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Css_parse_warning_001_constant_value_is_stable()
    {
        Assert.Equal("CSS-PARSE-WARNING-001", DiagnosticCodes.CssParseWarning001);
    }

    [Fact]
    public void Css_has_rendering_not_implemented_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(
            registry,
            @"\|\s*`?(CSS-HAS-RENDERING-NOT-IMPLEMENTED-001)`?\s*\|\s*(\w+)\s*\|");

        Assert.True(match.Success, "CSS-HAS-RENDERING-NOT-IMPLEMENTED-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.CssHasRenderingNotImplemented001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Css_has_rendering_not_implemented_001_constant_value_is_stable()
    {
        Assert.Equal("CSS-HAS-RENDERING-NOT-IMPLEMENTED-001", DiagnosticCodes.CssHasRenderingNotImplemented001);
    }

    [Fact]
    public void NetPdf_Css_constants_match_facade_constants()
    {
        // The NetPdf.Css internal sub-pipeline (cascade resolver) ships its own constants
        // because it can't reference the facade. Verify the strings agree exactly.
        Assert.Equal(DiagnosticCodes.CssParseWarning001,
            NetPdf.Css.Diagnostics.CssDiagnosticCodes.CssParseWarning001);
        Assert.Equal(DiagnosticCodes.CssHasRenderingNotImplemented001,
            NetPdf.Css.Diagnostics.CssDiagnosticCodes.CssHasRenderingNotImplemented001);
        Assert.Equal(DiagnosticCodes.CssAtRuleUnknown001,
            NetPdf.Css.Diagnostics.CssDiagnosticCodes.CssAtRuleUnknown001);
        Assert.Equal(DiagnosticCodes.CssContainerQueryUnsupported001,
            NetPdf.Css.Diagnostics.CssDiagnosticCodes.CssContainerQueryUnsupported001);
    }

    [Fact]
    public void Css_at_rule_unknown_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(
            registry,
            @"\|\s*`?(CSS-AT-RULE-UNKNOWN-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success, "CSS-AT-RULE-UNKNOWN-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.CssAtRuleUnknown001, match.Groups[1].Value);
        Assert.Equal("Info", match.Groups[2].Value);
    }

    [Fact]
    public void Css_at_rule_unknown_001_constant_value_is_stable()
    {
        Assert.Equal("CSS-AT-RULE-UNKNOWN-001", DiagnosticCodes.CssAtRuleUnknown001);
    }

    [Fact]
    public void Css_container_query_unsupported_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(
            registry,
            @"\|\s*`?(CSS-CONTAINER-QUERY-UNSUPPORTED-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success, "CSS-CONTAINER-QUERY-UNSUPPORTED-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.CssContainerQueryUnsupported001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Css_container_query_unsupported_001_constant_value_is_stable()
    {
        Assert.Equal("CSS-CONTAINER-QUERY-UNSUPPORTED-001", DiagnosticCodes.CssContainerQueryUnsupported001);
    }

    [Fact]
    public void Css_var_circular_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(
            registry,
            @"\|\s*`?(CSS-VAR-CIRCULAR-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success, "CSS-VAR-CIRCULAR-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.CssVarCircular001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Css_var_circular_001_constant_value_is_stable()
    {
        Assert.Equal("CSS-VAR-CIRCULAR-001", DiagnosticCodes.CssVarCircular001);
    }

    [Fact]
    public void Css_var_circular_001_facade_and_internal_constants_agree()
    {
        Assert.Equal(DiagnosticCodes.CssVarCircular001,
            NetPdf.Css.Diagnostics.CssDiagnosticCodes.CssVarCircular001);
    }

    [Fact]
    public void Css_var_expansion_limit_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(CSS-VAR-EXPANSION-LIMIT-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success, "CSS-VAR-EXPANSION-LIMIT-001 row not found");
        Assert.Equal(DiagnosticCodes.CssVarExpansionLimit001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
        Assert.Equal(DiagnosticCodes.CssVarExpansionLimit001,
            NetPdf.Css.Diagnostics.CssDiagnosticCodes.CssVarExpansionLimit001);
    }

    [Fact]
    public void Css_calc_invalid_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(CSS-CALC-INVALID-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success, "CSS-CALC-INVALID-001 row not found");
        Assert.Equal(DiagnosticCodes.CssCalcInvalid001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
        Assert.Equal(DiagnosticCodes.CssCalcInvalid001,
            NetPdf.Css.Diagnostics.CssDiagnosticCodes.CssCalcInvalid001);
    }

    [Fact]
    public void Css_calc_div_by_zero_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(CSS-CALC-DIV-BY-ZERO-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success, "CSS-CALC-DIV-BY-ZERO-001 row not found");
        Assert.Equal(DiagnosticCodes.CssCalcDivByZero001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
        Assert.Equal(DiagnosticCodes.CssCalcDivByZero001,
            NetPdf.Css.Diagnostics.CssDiagnosticCodes.CssCalcDivByZero001);
    }

    [Fact]
    public void Css_property_value_invalid_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(CSS-PROPERTY-VALUE-INVALID-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success, "CSS-PROPERTY-VALUE-INVALID-001 row not found");
        Assert.Equal(DiagnosticCodes.CssPropertyValueInvalid001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
        Assert.Equal(DiagnosticCodes.CssPropertyValueInvalid001,
            NetPdf.Css.Diagnostics.CssDiagnosticCodes.CssPropertyValueInvalid001);
    }

    [Fact]
    public void Css_property_value_invalid_001_constant_value_is_stable()
    {
        Assert.Equal("CSS-PROPERTY-VALUE-INVALID-001", DiagnosticCodes.CssPropertyValueInvalid001);
    }

    // ============================================================
    // Task 16 cycle 1 — new diagnostic codes
    // ============================================================

    [Fact]
    public void Css_content_function_unsupported_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(CSS-CONTENT-FUNCTION-UNSUPPORTED-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success, "CSS-CONTENT-FUNCTION-UNSUPPORTED-001 row not found");
        Assert.Equal(DiagnosticCodes.CssContentFunctionUnsupported001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
        Assert.Equal(DiagnosticCodes.CssContentFunctionUnsupported001,
            NetPdf.Css.Diagnostics.CssDiagnosticCodes.CssContentFunctionUnsupported001);
    }

    [Fact]
    public void Css_content_function_unsupported_001_constant_value_is_stable()
    {
        Assert.Equal("CSS-CONTENT-FUNCTION-UNSUPPORTED-001",
            DiagnosticCodes.CssContentFunctionUnsupported001);
    }

    [Fact]
    public void Css_attr_multi_arg_unsupported_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(CSS-ATTR-MULTI-ARG-UNSUPPORTED-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success, "CSS-ATTR-MULTI-ARG-UNSUPPORTED-001 row not found");
        Assert.Equal(DiagnosticCodes.CssAttrMultiArgUnsupported001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
        Assert.Equal(DiagnosticCodes.CssAttrMultiArgUnsupported001,
            NetPdf.Css.Diagnostics.CssDiagnosticCodes.CssAttrMultiArgUnsupported001);
    }

    [Fact]
    public void Css_attr_multi_arg_unsupported_001_constant_value_is_stable()
    {
        Assert.Equal("CSS-ATTR-MULTI-ARG-UNSUPPORTED-001",
            DiagnosticCodes.CssAttrMultiArgUnsupported001);
    }

    [Fact]
    public void Css_modern_color_function_unsupported_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(CSS-MODERN-COLOR-FUNCTION-UNSUPPORTED-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success, "CSS-MODERN-COLOR-FUNCTION-UNSUPPORTED-001 row not found");
        Assert.Equal(DiagnosticCodes.CssModernColorFunctionUnsupported001, match.Groups[1].Value);
        Assert.Equal("Info", match.Groups[2].Value);
        Assert.Equal(DiagnosticCodes.CssModernColorFunctionUnsupported001,
            NetPdf.Css.Diagnostics.CssDiagnosticCodes.CssModernColorFunctionUnsupported001);
    }

    [Fact]
    public void Css_modern_color_function_unsupported_001_constant_value_is_stable()
    {
        Assert.Equal("CSS-MODERN-COLOR-FUNCTION-UNSUPPORTED-001",
            DiagnosticCodes.CssModernColorFunctionUnsupported001);
    }

    [Fact]
    public void Css_pseudo_suppressed_on_replaced_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(CSS-PSEUDO-SUPPRESSED-ON-REPLACED-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success, "CSS-PSEUDO-SUPPRESSED-ON-REPLACED-001 row not found");
        Assert.Equal(DiagnosticCodes.CssPseudoSuppressedOnReplaced001, match.Groups[1].Value);
        Assert.Equal("Info", match.Groups[2].Value);
        Assert.Equal(DiagnosticCodes.CssPseudoSuppressedOnReplaced001,
            NetPdf.Css.Diagnostics.CssDiagnosticCodes.CssPseudoSuppressedOnReplaced001);
    }

    [Fact]
    public void Css_pseudo_suppressed_on_replaced_001_constant_value_is_stable()
    {
        Assert.Equal("CSS-PSEUDO-SUPPRESSED-ON-REPLACED-001",
            DiagnosticCodes.CssPseudoSuppressedOnReplaced001);
    }

    // ============================================================
    // Phase 3 Task 11 cycle 1 sub-cycle 1 — LAYOUT-* diagnostic
    // parity. The existing constant for
    // LAYOUT-INLINE-SKIPPED-NO-SHAPER-RESOLVER-001 shipped without
    // a parity test (Finding #7 hardening review); the new
    // LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001 (Finding #4) +
    // LAYOUT-INLINE-UNSUPPORTED-001 (Finding #6) ship with parity
    // tests from the start.
    // ============================================================

    [Fact]
    public void Layout_inline_skipped_no_shaper_resolver_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(LAYOUT-INLINE-SKIPPED-NO-SHAPER-RESOLVER-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success,
            "LAYOUT-INLINE-SKIPPED-NO-SHAPER-RESOLVER-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.LayoutInlineSkippedNoShaperResolver001,
            match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Layout_inline_skipped_no_shaper_resolver_001_constant_value_is_stable()
    {
        Assert.Equal("LAYOUT-INLINE-SKIPPED-NO-SHAPER-RESOLVER-001",
            DiagnosticCodes.LayoutInlineSkippedNoShaperResolver001);
    }

    [Fact]
    public void Layout_inline_skipped_no_shaper_resolver_001_facade_and_paginate_constants_agree()
    {
        Assert.Equal(DiagnosticCodes.LayoutInlineSkippedNoShaperResolver001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutInlineSkippedNoShaperResolver001);
    }

    [Fact]
    public void Layout_inline_atomic_not_supported_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success,
            "LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.LayoutInlineAtomicNotSupported001,
            match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Layout_inline_atomic_not_supported_001_constant_value_is_stable()
    {
        Assert.Equal("LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001",
            DiagnosticCodes.LayoutInlineAtomicNotSupported001);
    }

    [Fact]
    public void Layout_inline_atomic_not_supported_001_facade_and_paginate_constants_agree()
    {
        Assert.Equal(DiagnosticCodes.LayoutInlineAtomicNotSupported001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutInlineAtomicNotSupported001);
    }

    [Fact]
    public void Layout_inline_unsupported_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(LAYOUT-INLINE-UNSUPPORTED-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success,
            "LAYOUT-INLINE-UNSUPPORTED-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.LayoutInlineUnsupported001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Layout_inline_unsupported_001_constant_value_is_stable()
    {
        Assert.Equal("LAYOUT-INLINE-UNSUPPORTED-001",
            DiagnosticCodes.LayoutInlineUnsupported001);
    }

    [Fact]
    public void Layout_inline_unsupported_001_facade_and_paginate_constants_agree()
    {
        Assert.Equal(DiagnosticCodes.LayoutInlineUnsupported001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutInlineUnsupported001);
    }

    // ============================================================
    // Phase 3 Task 12 sub-cycle 1 — LAYOUT-TABLE-FEATURE-
    // UNSUPPORTED-001 parity.
    // ============================================================

    [Fact]
    public void Layout_table_feature_unsupported_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(LAYOUT-TABLE-FEATURE-UNSUPPORTED-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success,
            "LAYOUT-TABLE-FEATURE-UNSUPPORTED-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.LayoutTableFeatureUnsupported001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Layout_table_feature_unsupported_001_constant_value_is_stable()
    {
        Assert.Equal("LAYOUT-TABLE-FEATURE-UNSUPPORTED-001",
            DiagnosticCodes.LayoutTableFeatureUnsupported001);
    }

    [Fact]
    public void Layout_table_feature_unsupported_001_facade_and_paginate_constants_agree()
    {
        Assert.Equal(DiagnosticCodes.LayoutTableFeatureUnsupported001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001);
    }

    private static string LoadRegistry()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "diagnostics-codes.md");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate docs/diagnostics-codes.md by walking up from " + AppContext.BaseDirectory);
    }
}
