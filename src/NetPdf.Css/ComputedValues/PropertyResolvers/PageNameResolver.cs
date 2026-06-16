// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Validates the <c>page</c> property value (CSS Page 3 §3.4): <c>auto | &lt;custom-ident&gt;</c>
/// (backlog #6). <c>auto</c> (the initial) means "no named page"; a <c>&lt;custom-ident&gt;</c>
/// assigns the page a name that an <c>@page &lt;name&gt;</c> selector can target.
///
/// <para><b>Why this is registration-for-validation, not a computed value.</b> The named-page
/// machinery reads the assigned name RAW from the recovered declaration onto <c>Box.PageName</c>
/// at box-build time (AngleSharp drops <c>page</c>, so it's recovered by the preprocessor) — the
/// typed computed slot is not consumed. Registration makes <c>@supports (page: …)</c> answer
/// correctly + surfaces an INVALID value as <c>CSS-PROPERTY-VALUE-INVALID-001</c>; a VALID name
/// returns <see cref="ResolverResult.Deferred"/> carrying the raw text. Because the raw read is
/// independent of typed resolution, this can't regress named-page rendering.</para>
/// </summary>
internal static class PageNameResolver
{
    public static ResolverResult Resolve(
        string value, PropertyId propertyId, string propertyName,
        ICssDiagnosticsSink? diagnostics, CssSourceLocation location)
    {
        // `auto` — the initial value (no named page).
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return ResolverResult.Deferred(value);
        // Otherwise a <custom-ident> — a single identifier token, not a reserved word.
        if (IsCustomIdent(value))
            return ResolverResult.Deferred(value);
        var safeValue = DiagnosticTextSanitizer.Sanitize(value);
        diagnostics?.Emit(new CssDiagnostic(
            CssDiagnosticCodes.CssPropertyValueInvalid001,
            $"Could not parse '{propertyName}: {safeValue}' — expected `auto` or a <custom-ident> page name.",
            CssDiagnosticSeverity.Warning,
            location));
        return ResolverResult.Invalid();
    }

    /// <summary>A single CSS Syntax 3 §4.3.11 <c>&lt;custom-ident&gt;</c> token usable as a page
    /// name: a non-empty identifier (no whitespace), first code point an ident-start (letter / <c>_</c>
    /// / non-ASCII ≥ U+0080) OR a leading <c>-</c> before an ident-start, then ident chars; and NOT a
    /// CSS-wide keyword or the reserved <c>default</c> (<c>auto</c> is accepted by the caller).</summary>
    private static bool IsCustomIdent(string value)
    {
        if (value.Length == 0) return false;
        foreach (var ch in value)
            if (char.IsWhiteSpace(ch)) return false;   // a single token only
        var first = value[0];
        bool startOk = IsIdentStart(first)
            || (first == '-' && value.Length > 1 && IsIdentStart(value[1]));
        if (!startOk) return false;
        foreach (var ch in value)
            if (!IsIdentStart(ch) && !(ch >= '0' && ch <= '9') && ch != '-') return false;
        return !value.Equals("inherit", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("initial", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("unset", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("revert", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("revert-layer", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("default", StringComparison.OrdinalIgnoreCase);   // reserved (CSS Page 3)
    }

    private static bool IsIdentStart(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c >= '\u0080';
}
