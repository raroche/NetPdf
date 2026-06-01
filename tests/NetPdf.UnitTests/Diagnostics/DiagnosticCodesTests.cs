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

    // ============================================================
    // Phase 3 Task 12 sub-cycle 2 hardening Finding 4 —
    // LAYOUT-TABLE-SLOT-BUDGET-EXCEEDED-001 parity.
    // ============================================================

    [Fact]
    public void Layout_table_slot_budget_exceeded_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(LAYOUT-TABLE-SLOT-BUDGET-EXCEEDED-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success,
            "LAYOUT-TABLE-SLOT-BUDGET-EXCEEDED-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.LayoutTableSlotBudgetExceeded001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Layout_table_slot_budget_exceeded_001_constant_value_is_stable()
    {
        Assert.Equal("LAYOUT-TABLE-SLOT-BUDGET-EXCEEDED-001",
            DiagnosticCodes.LayoutTableSlotBudgetExceeded001);
    }

    [Fact]
    public void Layout_table_slot_budget_exceeded_001_facade_and_paginate_constants_agree()
    {
        Assert.Equal(DiagnosticCodes.LayoutTableSlotBudgetExceeded001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutTableSlotBudgetExceeded001);
    }

    // ============================================================
    // Phase 3 Task 12 sub-cycle 4 hardening Finding 1 —
    // LAYOUT-TABLE-INLINE-OVERFLOW-001 parity.
    // ============================================================

    [Fact]
    public void Layout_table_inline_overflow_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(LAYOUT-TABLE-INLINE-OVERFLOW-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success,
            "LAYOUT-TABLE-INLINE-OVERFLOW-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.LayoutTableInlineOverflow001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Layout_table_inline_overflow_001_constant_value_is_stable()
    {
        Assert.Equal("LAYOUT-TABLE-INLINE-OVERFLOW-001",
            DiagnosticCodes.LayoutTableInlineOverflow001);
    }

    [Fact]
    public void Layout_table_inline_overflow_001_facade_and_paginate_constants_agree()
    {
        Assert.Equal(DiagnosticCodes.LayoutTableInlineOverflow001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutTableInlineOverflow001);
    }

    // ============================================================
    // Phase 3 Task 12 sub-cycle 5 hardening Finding 4 —
    // LAYOUT-TABLE-INTRINSIC-MEASUREMENT-BUDGET-EXCEEDED-001 parity.
    // ============================================================

    [Fact]
    public void Layout_table_intrinsic_measurement_budget_exceeded_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(LAYOUT-TABLE-INTRINSIC-MEASUREMENT-BUDGET-EXCEEDED-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success,
            "LAYOUT-TABLE-INTRINSIC-MEASUREMENT-BUDGET-EXCEEDED-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.LayoutTableIntrinsicMeasurementBudgetExceeded001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Layout_table_intrinsic_measurement_budget_exceeded_001_constant_value_is_stable()
    {
        Assert.Equal("LAYOUT-TABLE-INTRINSIC-MEASUREMENT-BUDGET-EXCEEDED-001",
            DiagnosticCodes.LayoutTableIntrinsicMeasurementBudgetExceeded001);
    }

    [Fact]
    public void Layout_table_intrinsic_measurement_budget_exceeded_001_facade_and_paginate_constants_agree()
    {
        Assert.Equal(DiagnosticCodes.LayoutTableIntrinsicMeasurementBudgetExceeded001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutTableIntrinsicMeasurementBudgetExceeded001);
    }

    // ============================================================
    // Phase 3 Task 13 cycle 1 hardening Finding 5 —
    // LAYOUT-TABLE-REWIND-NOT-SUPPORTED-001 parity.
    // ============================================================

    [Fact]
    public void Layout_table_rewind_not_supported_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(LAYOUT-TABLE-REWIND-NOT-SUPPORTED-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success,
            "LAYOUT-TABLE-REWIND-NOT-SUPPORTED-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.LayoutTableRewindNotSupported001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Layout_table_rewind_not_supported_001_constant_value_is_stable()
    {
        Assert.Equal("LAYOUT-TABLE-REWIND-NOT-SUPPORTED-001",
            DiagnosticCodes.LayoutTableRewindNotSupported001);
    }

    [Fact]
    public void Layout_table_rewind_not_supported_001_facade_and_paginate_constants_agree()
    {
        Assert.Equal(DiagnosticCodes.LayoutTableRewindNotSupported001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutTableRewindNotSupported001);
    }

    // ============================================================
    // Phase 3 Task 13 cycle 1 hardening Finding 6 —
    // LAYOUT-TABLE-ROWSPAN-CROSSES-PAGE-001 parity.
    // ============================================================

    [Fact]
    public void Layout_table_rowspan_crosses_page_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(LAYOUT-TABLE-ROWSPAN-CROSSES-PAGE-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success,
            "LAYOUT-TABLE-ROWSPAN-CROSSES-PAGE-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.LayoutTableRowspanCrossesPage001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Layout_table_rowspan_crosses_page_001_constant_value_is_stable()
    {
        Assert.Equal("LAYOUT-TABLE-ROWSPAN-CROSSES-PAGE-001",
            DiagnosticCodes.LayoutTableRowspanCrossesPage001);
    }

    [Fact]
    public void Layout_table_rowspan_crosses_page_001_facade_and_paginate_constants_agree()
    {
        Assert.Equal(DiagnosticCodes.LayoutTableRowspanCrossesPage001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutTableRowspanCrossesPage001);
    }

    // ============================================================
    // Phase 3 Task 13 cycle 2 —
    // LAYOUT-TABLE-HEADER-FOOTER-OVERSIZED-001 parity.
    // ============================================================

    [Fact]
    public void Layout_table_header_footer_oversized_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(LAYOUT-TABLE-HEADER-FOOTER-OVERSIZED-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success,
            "LAYOUT-TABLE-HEADER-FOOTER-OVERSIZED-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.LayoutTableHeaderFooterOversized001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Layout_table_header_footer_oversized_001_constant_value_is_stable()
    {
        Assert.Equal("LAYOUT-TABLE-HEADER-FOOTER-OVERSIZED-001",
            DiagnosticCodes.LayoutTableHeaderFooterOversized001);
    }

    [Fact]
    public void Layout_table_header_footer_oversized_001_facade_and_paginate_constants_agree()
    {
        Assert.Equal(DiagnosticCodes.LayoutTableHeaderFooterOversized001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutTableHeaderFooterOversized001);
    }

    // ============================================================
    // Phase 3 Task 14 cycle 1 —
    // LAYOUT-MULTICOL-FORCED-OVERFLOW-001 parity.
    // ============================================================

    [Fact]
    public void Layout_multicol_forced_overflow_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(LAYOUT-MULTICOL-FORCED-OVERFLOW-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success,
            "LAYOUT-MULTICOL-FORCED-OVERFLOW-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.LayoutMulticolForcedOverflow001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Layout_multicol_forced_overflow_001_constant_value_is_stable()
    {
        Assert.Equal("LAYOUT-MULTICOL-FORCED-OVERFLOW-001",
            DiagnosticCodes.LayoutMulticolForcedOverflow001);
    }

    [Fact]
    public void Layout_multicol_forced_overflow_001_facade_and_paginate_constants_agree()
    {
        Assert.Equal(DiagnosticCodes.LayoutMulticolForcedOverflow001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001);
    }

    // ============================================================
    // Phase 3 Task 14 cycle 1 hardening (Finding 4) —
    // LAYOUT-MULTICOL-NON-FINITE-GEOMETRY-001 parity.
    // ============================================================

    [Fact]
    public void Layout_multicol_non_finite_geometry_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(LAYOUT-MULTICOL-NON-FINITE-GEOMETRY-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success,
            "LAYOUT-MULTICOL-NON-FINITE-GEOMETRY-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.LayoutMulticolNonFiniteGeometry001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Layout_multicol_non_finite_geometry_001_constant_value_is_stable()
    {
        Assert.Equal("LAYOUT-MULTICOL-NON-FINITE-GEOMETRY-001",
            DiagnosticCodes.LayoutMulticolNonFiniteGeometry001);
    }

    [Fact]
    public void Layout_multicol_non_finite_geometry_001_facade_and_paginate_constants_agree()
    {
        Assert.Equal(DiagnosticCodes.LayoutMulticolNonFiniteGeometry001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutMulticolNonFiniteGeometry001);
    }

    // ============================================================
    // Phase 3 Task 14 cycle 2 hardening (Finding #3) —
    // LAYOUT-MULTICOL-COLUMN-COUNT-CLAMPED-001 parity.
    // ============================================================

    [Fact]
    public void Layout_multicol_column_count_clamped_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(LAYOUT-MULTICOL-COLUMN-COUNT-CLAMPED-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success,
            "LAYOUT-MULTICOL-COLUMN-COUNT-CLAMPED-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.LayoutMulticolColumnCountClamped001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Layout_multicol_column_count_clamped_001_constant_value_is_stable()
    {
        Assert.Equal("LAYOUT-MULTICOL-COLUMN-COUNT-CLAMPED-001",
            DiagnosticCodes.LayoutMulticolColumnCountClamped001);
    }

    [Fact]
    public void Layout_multicol_column_count_clamped_001_facade_and_paginate_constants_agree()
    {
        Assert.Equal(DiagnosticCodes.LayoutMulticolColumnCountClamped001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutMulticolColumnCountClamped001);
    }

    // ============================================================
    // Phase 3 Task 14 cycle 2 hardening (Finding #1) —
    // LAYOUT-FLOAT-BREAK-INSIDE-NESTED-001 parity.
    // ============================================================

    [Fact]
    public void Layout_float_break_inside_nested_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(LAYOUT-FLOAT-BREAK-INSIDE-NESTED-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success,
            "LAYOUT-FLOAT-BREAK-INSIDE-NESTED-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.LayoutFloatBreakInsideNested001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Layout_float_break_inside_nested_001_constant_value_is_stable()
    {
        Assert.Equal("LAYOUT-FLOAT-BREAK-INSIDE-NESTED-001",
            DiagnosticCodes.LayoutFloatBreakInsideNested001);
    }

    [Fact]
    public void Layout_float_break_inside_nested_001_facade_and_paginate_constants_agree()
    {
        Assert.Equal(DiagnosticCodes.LayoutFloatBreakInsideNested001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutFloatBreakInsideNested001);
    }

    // ============================================================
    // Phase 3 Task 15 L6 post-PR-#66 review F#4 —
    // LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001 parity.
    // ============================================================

    [Fact]
    public void Layout_flex_wrap_reverse_approximated_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(registry,
            @"\|\s*`?(LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001)`?\s*\|\s*(\w+)\s*\|");
        Assert.True(match.Success,
            "LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal(DiagnosticCodes.LayoutFlexWrapReverseApproximated001, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Layout_flex_wrap_reverse_approximated_001_constant_value_is_stable()
    {
        Assert.Equal("LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001",
            DiagnosticCodes.LayoutFlexWrapReverseApproximated001);
    }

    [Fact]
    public void Layout_flex_wrap_reverse_approximated_001_facade_and_paginate_constants_agree()
    {
        Assert.Equal(DiagnosticCodes.LayoutFlexWrapReverseApproximated001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutFlexWrapReverseApproximated001);
    }

    // ============================================================
    // Phase 3 Task 17 cycle 1 (Hello World) + post-PR-#92 review F7 —
    // LAYOUT-GRID-* parity across facade + Paginate constants + docs.
    // ============================================================

    [Theory]
    [InlineData("LAYOUT-GRID-TRACK-KIND-UNSUPPORTED-001")]
    [InlineData("LAYOUT-GRID-PLACEMENT-APPROXIMATED-001")]
    [InlineData("LAYOUT-GRID-IMPLICIT-TRACK-UNSUPPORTED-001")]
    [InlineData("LAYOUT-GRID-NON-FINITE-GEOMETRY-001")]
    [InlineData("LAYOUT-GRID-ZERO-SIZED-CELL-CONTENT-SKIPPED-001")]
    [InlineData("LAYOUT-GRID-FR-UNDER-INDEFINITE-APPROXIMATED-001")]
    public void Layout_grid_codes_appear_in_registry_doc_as_Warning(string code)
    {
        var registry = LoadRegistry();
        var pattern = $@"\|\s*`?({Regex.Escape(code)})`?\s*\|\s*(\w+)\s*\|";
        var match = Regex.Match(registry, pattern);
        Assert.True(match.Success,
            $"{code} row not found in docs/diagnostics-codes.md");
        Assert.Equal(code, match.Groups[1].Value);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Layout_grid_track_kind_unsupported_001_constants_are_stable()
    {
        Assert.Equal("LAYOUT-GRID-TRACK-KIND-UNSUPPORTED-001",
            DiagnosticCodes.LayoutGridTrackKindUnsupported001);
        Assert.Equal(DiagnosticCodes.LayoutGridTrackKindUnsupported001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridTrackKindUnsupported001);
    }

    [Fact]
    public void Layout_grid_placement_approximated_001_constants_are_stable()
    {
        Assert.Equal("LAYOUT-GRID-PLACEMENT-APPROXIMATED-001",
            DiagnosticCodes.LayoutGridPlacementApproximated001);
        Assert.Equal(DiagnosticCodes.LayoutGridPlacementApproximated001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    [Fact]
    public void Layout_grid_implicit_track_unsupported_001_constants_are_stable()
    {
        Assert.Equal("LAYOUT-GRID-IMPLICIT-TRACK-UNSUPPORTED-001",
            DiagnosticCodes.LayoutGridImplicitTrackUnsupported001);
        Assert.Equal(DiagnosticCodes.LayoutGridImplicitTrackUnsupported001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridImplicitTrackUnsupported001);
    }

    [Fact]
    public void Layout_grid_non_finite_geometry_001_constants_are_stable()
    {
        Assert.Equal("LAYOUT-GRID-NON-FINITE-GEOMETRY-001",
            DiagnosticCodes.LayoutGridNonFiniteGeometry001);
        Assert.Equal(DiagnosticCodes.LayoutGridNonFiniteGeometry001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridNonFiniteGeometry001);
    }

    [Fact]
    public void Layout_grid_zero_sized_cell_content_skipped_001_constants_are_stable()
    {
        Assert.Equal("LAYOUT-GRID-ZERO-SIZED-CELL-CONTENT-SKIPPED-001",
            DiagnosticCodes.LayoutGridZeroSizedCellContentSkipped001);
        Assert.Equal(DiagnosticCodes.LayoutGridZeroSizedCellContentSkipped001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridZeroSizedCellContentSkipped001);
    }

    [Fact]
    public void Layout_grid_fr_under_indefinite_approximated_001_constants_are_stable()
    {
        Assert.Equal("LAYOUT-GRID-FR-UNDER-INDEFINITE-APPROXIMATED-001",
            DiagnosticCodes.LayoutGridFrUnderIndefiniteApproximated001);
        Assert.Equal(DiagnosticCodes.LayoutGridFrUnderIndefiniteApproximated001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridFrUnderIndefiniteApproximated001);
    }

    [Fact]
    public void Layout_grid_percentage_track_approximated_001_constants_are_stable()
    {
        Assert.Equal("LAYOUT-GRID-PERCENTAGE-TRACK-APPROXIMATED-001",
            DiagnosticCodes.LayoutGridPercentageTrackApproximated001);
        Assert.Equal(DiagnosticCodes.LayoutGridPercentageTrackApproximated001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridPercentageTrackApproximated001);
    }

    [Fact]
    public void Layout_grid_max_expanded_tracks_truncated_001_constants_are_stable()
    {
        Assert.Equal("LAYOUT-GRID-MAX-EXPANDED-TRACKS-TRUNCATED-001",
            DiagnosticCodes.LayoutGridMaxExpandedTracksTruncated001);
        Assert.Equal(DiagnosticCodes.LayoutGridMaxExpandedTracksTruncated001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridMaxExpandedTracksTruncated001);
    }

    [Fact]
    public void Layout_grid_forced_overflow_001_constants_are_stable()
    {
        Assert.Equal("LAYOUT-GRID-FORCED-OVERFLOW-001",
            DiagnosticCodes.LayoutGridForcedOverflow001);
        Assert.Equal(DiagnosticCodes.LayoutGridForcedOverflow001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridForcedOverflow001);
    }

    [Fact]
    public void Layout_grid_resume_inline_size_mismatch_001_constants_are_stable()
    {
        Assert.Equal("LAYOUT-GRID-RESUME-INLINE-SIZE-MISMATCH-001",
            DiagnosticCodes.LayoutGridResumeInlineSizeMismatch001);
        Assert.Equal(DiagnosticCodes.LayoutGridResumeInlineSizeMismatch001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridResumeInlineSizeMismatch001);
    }

    [Fact]
    public void Layout_grid_resume_cache_rejected_001_constants_are_stable()
    {
        Assert.Equal("LAYOUT-GRID-RESUME-CACHE-REJECTED-001",
            DiagnosticCodes.LayoutGridResumeCacheRejected001);
        Assert.Equal(DiagnosticCodes.LayoutGridResumeCacheRejected001,
            NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridResumeCacheRejected001);
    }

    // ============================================================
    // Phase 5 layout→PDF wiring cycle 2 — PDF-* + PAINT-* facade/paint codes.
    // ============================================================

    [Fact]
    public void Pdf_content_overflow_truncated_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(
            registry,
            @"\|\s*`?(PDF-CONTENT-OVERFLOW-TRUNCATED-001)`?\s*\|\s*(\w+)\s*\|");

        Assert.True(match.Success, "PDF-CONTENT-OVERFLOW-TRUNCATED-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal("PDF-CONTENT-OVERFLOW-TRUNCATED-001", DiagnosticCodes.PdfContentOverflowTruncated001);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Paint_background_alpha_approximated_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(
            registry,
            @"\|\s*`?(PAINT-BACKGROUND-ALPHA-APPROXIMATED-001)`?\s*\|\s*(\w+)\s*\|");

        Assert.True(match.Success, "PAINT-BACKGROUND-ALPHA-APPROXIMATED-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal("PAINT-BACKGROUND-ALPHA-APPROXIMATED-001", DiagnosticCodes.PaintBackgroundAlphaApproximated001);
        Assert.Equal("Info", match.Groups[2].Value);
    }

    [Fact]
    public void Paint_border_style_approximated_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(
            registry,
            @"\|\s*`?(PAINT-BORDER-STYLE-APPROXIMATED-001)`?\s*\|\s*(\w+)\s*\|");

        Assert.True(match.Success, "PAINT-BORDER-STYLE-APPROXIMATED-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal("PAINT-BORDER-STYLE-APPROXIMATED-001", DiagnosticCodes.PaintBorderStyleApproximated001);
        Assert.Equal("Info", match.Groups[2].Value);
    }

    [Fact]
    public void Paint_border_alpha_approximated_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(
            registry,
            @"\|\s*`?(PAINT-BORDER-ALPHA-APPROXIMATED-001)`?\s*\|\s*(\w+)\s*\|");

        Assert.True(match.Success, "PAINT-BORDER-ALPHA-APPROXIMATED-001 row not found in docs/diagnostics-codes.md");
        Assert.Equal("PAINT-BORDER-ALPHA-APPROXIMATED-001", DiagnosticCodes.PaintBorderAlphaApproximated001);
        Assert.Equal("Info", match.Groups[2].Value);
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
