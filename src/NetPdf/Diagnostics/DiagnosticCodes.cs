// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// Stable string constants for every diagnostic code emitted by NetPdf. The single
/// source of truth for the registry is <c>docs/diagnostics-codes.md</c>; the constants
/// here let emission sites and tests share one literal so a typo at the call site
/// is impossible. Constants are kept <see langword="internal"/>: consumers receive the
/// codes as <see cref="Diagnostic.Code"/> string values, not via a named-constant API.
/// </summary>
internal static class DiagnosticCodes
{
    // region HTML-*

    /// <summary>
    /// A <c>&lt;script&gt;</c> element was encountered. NetPdf does not execute JavaScript in v1.
    /// The element was removed from the rendering tree. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string HtmlScriptIgnored001 = "HTML-SCRIPT-IGNORED-001";

    /// <summary>
    /// An <c>href</c> / <c>xlink:href</c> attribute carried a <c>javascript:</c> URL. The
    /// attribute was removed so the link will not appear in the emitted PDF; the surrounding
    /// element and its text content remain. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string HtmlJavaScriptUrlIgnored001 = "HTML-JAVASCRIPT-URL-IGNORED-001";

    // endregion HTML-*

    // region CSS-*

    /// <summary>
    /// A CSS rule was malformed and skipped. The cascade resolver emits this when a rule's
    /// selector text fails to parse (e.g., unsupported pseudo-class, unterminated function).
    /// The rest of the stylesheet still loads. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssParseWarning001 = "CSS-PARSE-WARNING-001";

    /// <summary>
    /// A <c>:has()</c> selector was encountered. NetPdf's v1 contract is that <c>:has()</c>
    /// parses but never matches — the rule has no rendering effect. Roadmap v1.4 will plug
    /// in real <c>:has()</c> evaluation. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssHasRenderingNotImplemented001 = "CSS-HAS-RENDERING-NOT-IMPLEMENTED-001";

    /// <summary>
    /// An unrecognized at-rule was preserved in the AST but had no rendering effect — the
    /// cascade resolver couldn't decompose its body or its conditions weren't evaluable.
    /// Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string CssAtRuleUnknown001 = "CSS-AT-RULE-UNKNOWN-001";

    /// <summary>
    /// An <c>@container</c> rule was encountered. NetPdf does not evaluate container queries
    /// in v1 — the contained rules are skipped. Roadmap v1.4 will add container-query
    /// evaluation. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssContainerQueryUnsupported001 = "CSS-CONTAINER-QUERY-UNSUPPORTED-001";

    /// <summary>
    /// A <c>var()</c> chain produced a circular reference (e.g.,
    /// <c>--a: var(--b); --b: var(--a)</c>); the substitution stopped + resolved to the
    /// fallback value or <c>unset</c>. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssVarCircular001 = "CSS-VAR-CIRCULAR-001";

    /// <summary>
    /// A <c>var()</c> substitution exceeded the user-agent's depth or output-length
    /// safety limit. Distinct from a circular reference — the chain is acyclic but
    /// pathological (e.g., long non-cyclic chain past 32 frames, or an exponentially
    /// expanding chain past 1 MiB output). The substitution is treated as "invalid at
    /// computed value time" per CSS Custom Properties L1 §3.5; references resolve to
    /// the fallback or <c>unset</c>. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssVarExpansionLimit001 = "CSS-VAR-EXPANSION-LIMIT-001";

    /// <summary>
    /// A <c>calc()</c> / <c>min()</c> / <c>max()</c> / <c>clamp()</c> / <c>abs()</c> /
    /// <c>sign()</c> expression was syntactically invalid or had a type mismatch
    /// (e.g., adding a number to a length). Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssCalcInvalid001 = "CSS-CALC-INVALID-001";

    /// <summary>
    /// A <c>calc()</c> expression divided by zero. Per CSS Values L4 §10.1, the result
    /// is treated as "invalid at computed value time"; the property's initial value
    /// applies. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssCalcDivByZero001 = "CSS-CALC-DIV-BY-ZERO-001";

    /// <summary>
    /// A property's value text could not be parsed into the property's typed value
    /// per its declared <c>PropertyType</c> (e.g., <c>color: not-a-color</c>,
    /// <c>width: nonsense</c>, <c>display: foo</c>). The cascade's "invalid at computed
    /// value time" rule applies — the property's initial value (or the inherited value
    /// for inherited properties) is used. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssPropertyValueInvalid001 = "CSS-PROPERTY-VALUE-INVALID-001";

    /// <summary>
    /// A <c>content:</c> value (on <c>::before</c> / <c>::after</c> / <c>::marker</c>)
    /// used a function / keyword that cycle 1 doesn't yet handle (<c>counter()</c> /
    /// <c>counters()</c> / <c>url()</c> / <c>image()</c> / <c>image-set()</c> /
    /// <c>linear-gradient()</c> / <c>open-quote</c> / <c>close-quote</c> /
    /// <c>no-open-quote</c> / <c>no-close-quote</c>). The pseudo-element generates no
    /// box. Roadmap cycle 2 ships counter machinery + the resource pipeline +
    /// quotation-stack tracking. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssContentFunctionUnsupported001 = "CSS-CONTENT-FUNCTION-UNSUPPORTED-001";

    /// <summary>
    /// A modern <c>attr(name type, fallback)</c> form was rejected — cycle 1 supports
    /// the bare <c>attr(name)</c> form only. The pseudo-element generates no box rather
    /// than silently dropping the type / fallback args. Roadmap cycle 2 delivers the
    /// typed-value pipeline. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssAttrMultiArgUnsupported001 = "CSS-ATTR-MULTI-ARG-UNSUPPORTED-001";

    /// <summary>
    /// A modern color function (<c>oklch()</c> / <c>oklab()</c> / <c>lab()</c> /
    /// <c>lch()</c> / <c>color()</c> / <c>color-mix()</c>) was used in a property
    /// value. Cycle 1 rejects these — the cascade's "invalid at computed value
    /// time" rule applies. Roadmap cycle 2 ships sRGB-conversion so the rendered
    /// color is approximate but visible. Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string CssModernColorFunctionUnsupported001 = "CSS-MODERN-COLOR-FUNCTION-UNSUPPORTED-001";

    /// <summary>
    /// A <c>::before</c> / <c>::after</c> rule targeted a replaced element
    /// (<c>&lt;img&gt;</c> / <c>&lt;video&gt;</c> / <c>&lt;canvas&gt;</c> /
    /// <c>&lt;iframe&gt;</c> / <c>&lt;object&gt;</c> / <c>&lt;embed&gt;</c>); per
    /// CSS Pseudo L4 §3 the pseudo-element is suppressed because replaced elements
    /// are atomic and can't host generated content. The author rule has no effect.
    /// Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string CssPseudoSuppressedOnReplaced001 = "CSS-PSEUDO-SUPPRESSED-ON-REPLACED-001";

    // endregion CSS-*
}
