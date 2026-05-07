// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Diagnostics;

/// <summary>
/// Diagnostic-code constants emitted by the CSS pipeline. The string values mirror the
/// <c>NetPdf.DiagnosticCodes</c> facade-side constants — but defined here so
/// <c>NetPdf.Css</c> doesn't need a back-reference to the facade. Code values are
/// stable per <c>docs/diagnostics-codes.md</c>; the diagnostic-codes parity tests
/// verify the two sides agree.
/// </summary>
internal static class CssDiagnosticCodes
{
    /// <summary>A CSS rule was malformed and skipped. Severity: Warning.</summary>
    public const string CssParseWarning001 = "CSS-PARSE-WARNING-001";

    /// <summary>A <c>:has()</c> selector was encountered. NetPdf does not evaluate
    /// <c>:has()</c> in v1 — the rule has no effect. Severity: Warning.</summary>
    public const string CssHasRenderingNotImplemented001 = "CSS-HAS-RENDERING-NOT-IMPLEMENTED-001";

    /// <summary>An unrecognized at-rule was preserved in the AST but had no rendering
    /// effect. Severity: Info.</summary>
    public const string CssAtRuleUnknown001 = "CSS-AT-RULE-UNKNOWN-001";

    /// <summary>An <c>@container</c> rule was encountered. NetPdf does not evaluate
    /// container queries in v1; nested rules are skipped. Severity: Warning.</summary>
    public const string CssContainerQueryUnsupported001 = "CSS-CONTAINER-QUERY-UNSUPPORTED-001";

    /// <summary>A <c>var()</c> chain produced a circular reference; substitution
    /// resolved to the fallback or <c>unset</c>. Severity: Warning.</summary>
    public const string CssVarCircular001 = "CSS-VAR-CIRCULAR-001";

    /// <summary>A <c>var()</c> substitution exceeded the user-agent's depth or output
    /// safety limit (non-cyclic pathological chain). Severity: Warning.</summary>
    public const string CssVarExpansionLimit001 = "CSS-VAR-EXPANSION-LIMIT-001";

    /// <summary>A math-function expression (<c>calc()</c>/<c>min()</c>/<c>max()</c>/<c>clamp()</c>/
    /// <c>abs()</c>/<c>sign()</c>) was syntactically invalid or had a type mismatch.
    /// Severity: Warning.</summary>
    public const string CssCalcInvalid001 = "CSS-CALC-INVALID-001";

    /// <summary>A <c>calc()</c> expression divided by zero. Severity: Warning.</summary>
    public const string CssCalcDivByZero001 = "CSS-CALC-DIV-BY-ZERO-001";

    /// <summary>A property's value text could not be parsed into the property's typed
    /// value per its declared <see cref="NetPdf.Css.Properties.PropertyType"/>. The
    /// cascade's "invalid at computed value time" rule applies — initial value (or
    /// inherited for inherited properties) is used. Severity: Warning.</summary>
    public const string CssPropertyValueInvalid001 = "CSS-PROPERTY-VALUE-INVALID-001";

    /// <summary>A <c>content:</c> value used a function / keyword cycle 1 doesn't
    /// yet handle (<c>counter()</c> / <c>counters()</c> / <c>url()</c> /
    /// <c>image()</c> / <c>image-set()</c> / <c>linear-gradient()</c> /
    /// <c>open-quote</c> / <c>close-quote</c> / <c>no-open-quote</c> /
    /// <c>no-close-quote</c>). The pseudo-element generates no box. Severity: Warning.</summary>
    public const string CssContentFunctionUnsupported001 = "CSS-CONTENT-FUNCTION-UNSUPPORTED-001";

    /// <summary>A modern <c>attr(name type, fallback)</c> form was rejected — cycle 1
    /// supports the bare <c>attr(name)</c> form only. The pseudo-element generates no
    /// box rather than silently dropping the type / fallback args. Severity: Warning.</summary>
    public const string CssAttrMultiArgUnsupported001 = "CSS-ATTR-MULTI-ARG-UNSUPPORTED-001";

    /// <summary>A modern color function (<c>oklch()</c> / <c>oklab()</c> /
    /// <c>lab()</c> / <c>lch()</c> / <c>color()</c> / <c>color-mix()</c>) was used
    /// in a property value. Cycle 1 rejects these — initial / inherited value
    /// applies via the "invalid at computed value time" rule. Severity: Info.</summary>
    public const string CssModernColorFunctionUnsupported001 = "CSS-MODERN-COLOR-FUNCTION-UNSUPPORTED-001";

    /// <summary>A <c>::before</c> / <c>::after</c> rule targeted a replaced element
    /// (<c>&lt;img&gt;</c> / <c>&lt;video&gt;</c> / <c>&lt;canvas&gt;</c> /
    /// <c>&lt;iframe&gt;</c> / <c>&lt;object&gt;</c> / <c>&lt;embed&gt;</c>); per
    /// CSS Pseudo L4 §3 the pseudo-element is suppressed because replaced elements
    /// are atomic and can't host generated content. Severity: Info.</summary>
    public const string CssPseudoSuppressedOnReplaced001 = "CSS-PSEUDO-SUPPRESSED-ON-REPLACED-001";

    /// <summary>A stylesheet exceeded one of the per-stylesheet DoS caps:
    /// total rule count, declarations per rule, or selector alternatives per
    /// rule. The cascade resolver stops processing rules past the cap and
    /// truncates declaration / alternative lists to the configured limit.
    /// Per Phase B B-2. Severity: Warning.</summary>
    public const string CssRuleLimitExceeded001 = "CSS-RULE-LIMIT-EXCEEDED-001";
}
