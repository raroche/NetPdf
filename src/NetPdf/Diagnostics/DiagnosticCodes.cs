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

    // endregion CSS-*
}
