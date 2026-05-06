// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Text;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;

namespace NetPdf.Css.Cascade;

/// <summary>
/// Substitutes <c>var(--name, fallback)</c> references in a raw CSS value text against a
/// resolved <see cref="CustomPropertyTable"/>. Implements the recursion + circular-
/// reference detection rules from CSS Custom Properties L1 §3.5: each name being
/// resolved is tracked in a visited set; if a recursive substitution re-enters a name in
/// the set, the substitution stops and resolves to the fallback (or to the
/// <see cref="UnsetSentinel"/> when no fallback is provided), emitting
/// <see cref="CssDiagnosticCodes.CssVarCircular001"/>. Non-cyclic pathologies (depth or
/// output-length overrun) emit <see cref="CssDiagnosticCodes.CssVarExpansionLimit001"/>
/// instead — distinct from circular per the diagnostic registry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Token scanning.</b> Per CSS Syntax L3, function names (including <c>var</c>) are
/// matched ASCII case-insensitively, so <c>VAR(--x)</c> / <c>Var(--x)</c> are recognized
/// alongside lowercase <c>var(--x)</c>. Custom-property names INSIDE the parentheses
/// stay case-sensitive per CSS Custom Properties L1 §2. The scanner is character-level
/// + quote-aware: it skips single- / double-quoted string contents so a value like
/// <c>content: "var(--x)"</c> isn't treated as a real <c>var()</c> reference.
/// </para>
/// <para>
/// <b>Structured result.</b> <see cref="Substitute"/> returns a
/// <see cref="SubstitutionResult"/> carrying both the text AND an
/// <see cref="SubstitutionResult.IsInvalid"/> flag so callers can distinguish "successful
/// substitution producing the literal <c>unset</c> keyword" from "invalid at computed
/// value time per CSS Custom Properties L1 §3.5". <see cref="VarResolver"/> uses this
/// distinction to mark a custom-property name invalid in the table when its substituted
/// value would have been invalid — the earlier behavior of storing <c>"unset"</c> as a
/// real value let external <c>var(--name, fallback)</c> references pick up <c>"unset"</c>
/// instead of the fallback.
/// </para>
/// <para>
/// <b>Nested var() in fallbacks.</b> A value like <c>var(--a, var(--b, red))</c> is
/// supported: the fallback is itself processed through the substitution pipeline. Each
/// recursion guards against the same name appearing on the visited stack.
/// </para>
/// </remarks>
internal static class VarSubstitution
{
    /// <summary>The literal text emitted when a <c>var()</c> can't be resolved AND has
    /// no fallback. Tasks 9–10 typed-value parsers handle the <c>unset</c> keyword.</summary>
    public const string UnsetSentinel = "unset";

    /// <summary>Maximum recursion depth before the substitution bails per the
    /// "invalid at computed value time" rule (CSS Custom Properties L1). Bounded so a
    /// pathological long chain (`--a → --b → --c → ... → --z`) doesn't blow the stack
    /// or run unboundedly. Browsers use similar limits — Chromium bounds at ~32 frames.
    /// </summary>
    public const int MaxRecursionDepth = 32;

    /// <summary>Maximum total expansion size (in chars) before the substitution bails.
    /// Catches the exponential case: <c>--a: var(--b) var(--b); --b: var(--c) var(--c); …</c>
    /// can produce 2^N output. 1 MiB is generous for any sane CSS but small enough to
    /// fail fast on adversarial input.</summary>
    public const int MaxOutputLength = 1024 * 1024;

    /// <summary>Convenience wrapper around <see cref="Substitute"/> that discards the
    /// invalid flag and returns just the substituted text. Use the structured form
    /// (<see cref="Substitute"/>) when the caller needs to react to "invalid at
    /// computed value time" semantics — e.g., custom-property invalidation in
    /// <see cref="VarResolver"/>.</summary>
    public static string SubstituteToString(
        string rawValue,
        CustomPropertyTable customProperties,
        ICssDiagnosticsSink? diagnostics = null,
        CssSourceLocation location = default,
        HashSet<string>? visited = null,
        int depth = 0)
        => Substitute(rawValue, customProperties, diagnostics, location, visited, depth).Value;

    /// <summary>Substitute every <c>var(--name, fallback)</c> in <paramref name="rawValue"/>.
    /// Returns a <see cref="SubstitutionResult"/> with the resolved text + an
    /// <see cref="SubstitutionResult.IsInvalid"/> flag. Emits
    /// <see cref="CssDiagnosticCodes.CssVarCircular001"/> on cycles and
    /// <see cref="CssDiagnosticCodes.CssVarExpansionLimit001"/> on depth / output overruns
    /// to <paramref name="diagnostics"/>.</summary>
    /// <param name="rawValue">The raw declaration value text (post-AngleSharp normalization).</param>
    /// <param name="customProperties">The resolved custom-property table for the element.
    /// Lookup is case-sensitive per CSS Custom Properties L1 §2.</param>
    /// <param name="diagnostics">Optional sink for cycle / limit warnings.</param>
    /// <param name="location">Source location attached to any emitted diagnostic.</param>
    /// <param name="visited">Names currently being resolved on the recursion stack —
    /// callers pass <see langword="null"/> for the top-level entry; recursive calls
    /// carry it through to detect cycles.</param>
    /// <param name="depth">Recursion-depth counter; bounded by
    /// <see cref="MaxRecursionDepth"/>. Top-level callers pass 0.</param>
    public static SubstitutionResult Substitute(
        string rawValue,
        CustomPropertyTable customProperties,
        ICssDiagnosticsSink? diagnostics = null,
        CssSourceLocation location = default,
        HashSet<string>? visited = null,
        int depth = 0)
    {
        if (string.IsNullOrEmpty(rawValue)) return SubstitutionResult.Valid(rawValue ?? string.Empty);
        // Cheap fast-path: ASCII-CI search for "var(" with any case combination.
        if (!ContainsVarFunction(rawValue)) return SubstitutionResult.Valid(rawValue);

        if (depth >= MaxRecursionDepth)
        {
            diagnostics?.Emit(new CssDiagnostic(
                CssDiagnosticCodes.CssVarExpansionLimit001,
                $"var() substitution exceeded the maximum depth of {MaxRecursionDepth}. Resolved to unset.",
                CssDiagnosticSeverity.Warning,
                location));
            return SubstitutionResult.Invalid(UnsetSentinel);
        }

        var output = new StringBuilder(rawValue.Length);
        var anyInvalid = false;
        var pos = 0;
        while (pos < rawValue.Length)
        {
            if (output.Length > MaxOutputLength)
            {
                diagnostics?.Emit(new CssDiagnostic(
                    CssDiagnosticCodes.CssVarExpansionLimit001,
                    $"var() substitution exceeded the maximum output size of {MaxOutputLength} chars. Resolved to unset.",
                    CssDiagnosticSeverity.Warning,
                    location));
                return SubstitutionResult.Invalid(UnsetSentinel);
            }
            var c = rawValue[pos];
            if (c == '"' || c == '\'')
            {
                var stringEnd = SkipString(rawValue, pos);
                output.Append(rawValue, pos, stringEnd - pos);
                pos = stringEnd;
                continue;
            }
            // ASCII-case-insensitive `var(` detection per CSS Syntax L3 (function names
            // are matched ASCII case-insensitively). Custom-property names INSIDE the
            // parens stay case-sensitive per Custom Properties L1 §2.
            if (IsVarFunctionStart(rawValue, pos))
            {
                var bodyStart = pos + 4;
                var bodyEnd = FindMatchingCloseParen(rawValue, bodyStart);
                if (bodyEnd < 0)
                {
                    output.Append(rawValue, pos, rawValue.Length - pos);
                    return SubstitutionResult.Valid(output.ToString());
                }
                var body = rawValue[bodyStart..bodyEnd];
                var resolved = ResolveVar(body, customProperties, diagnostics, location, visited, depth + 1);
                output.Append(resolved.Value);
                if (resolved.IsInvalid) anyInvalid = true;
                pos = bodyEnd + 1; // skip past `)`
                continue;
            }
            output.Append(c);
            pos++;
        }
        return anyInvalid
            ? SubstitutionResult.Invalid(output.ToString())
            : SubstitutionResult.Valid(output.ToString());
    }

    /// <summary>Cheap ASCII-CI scan for any <c>var(</c> in the value, skipping quoted
    /// string contents. Used as a fast-path to skip the per-character walk when the
    /// value has no var() at all.</summary>
    private static bool ContainsVarFunction(string value)
    {
        var pos = 0;
        while (pos < value.Length)
        {
            var c = value[pos];
            if (c == '"' || c == '\'') { pos = SkipString(value, pos); continue; }
            if (IsVarFunctionStart(value, pos)) return true;
            pos++;
        }
        return false;
    }

    /// <summary>True when <paramref name="text"/> at <paramref name="pos"/> starts a
    /// case-insensitive <c>var(</c> token.</summary>
    private static bool IsVarFunctionStart(string text, int pos)
    {
        if (pos + 4 > text.Length) return false;
        return AsciiToLower(text[pos]) == 'v'
            && AsciiToLower(text[pos + 1]) == 'a'
            && AsciiToLower(text[pos + 2]) == 'r'
            && text[pos + 3] == '(';
    }

    private static char AsciiToLower(char c) =>
        (c >= 'A' && c <= 'Z') ? (char)(c + ('a' - 'A')) : c;

    /// <summary>Resolve one <c>var(...)</c> body. Splits on the first top-level comma
    /// into name + fallback, looks the name up in the table, and recurses on the
    /// resolved value (or the fallback when the name is absent / invalid).</summary>
    private static SubstitutionResult ResolveVar(
        string body,
        CustomPropertyTable customProperties,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location,
        HashSet<string>? visited,
        int depth)
    {
        var (nameRaw, fallbackRaw) = SplitOnTopLevelComma(body);
        var name = nameRaw.Trim();
        if (string.IsNullOrEmpty(name) || !name.StartsWith("--", StringComparison.Ordinal))
        {
            // Malformed var() — treat as missing/fallback.
            return ResolveFallback(fallbackRaw, customProperties, diagnostics, location, visited, depth);
        }

        if (visited is not null && visited.Contains(name))
        {
            diagnostics?.Emit(new CssDiagnostic(
                CssDiagnosticCodes.CssVarCircular001,
                $"Circular var() reference detected at '{name}'. Resolved to fallback or unset.",
                CssDiagnosticSeverity.Warning,
                location));
            return ResolveFallback(fallbackRaw, customProperties, diagnostics, location, visited, depth);
        }

        if (!customProperties.TryGetValue(name, out var value))
        {
            // Name not declared OR marked invalid by the cycle-detection pre-pass —
            // either way, fall through to fallback.
            return ResolveFallback(fallbackRaw, customProperties, diagnostics, location, visited, depth);
        }

        // Lazily allocate the visited set once per resolution and mutate in place across
        // the recursion (add before descending, remove on return). Avoids the per-frame
        // copy the previous implementation took on every nested var(). Cycle detection
        // still works because Add happens BEFORE the recursive Substitute call; sibling
        // refs to the same name (e.g. `var(--a) var(--a)` at the same level) aren't
        // blocked because we Remove on the way back up.
        var visitedSet = visited ?? new HashSet<string>(StringComparer.Ordinal);
        visitedSet.Add(name);
        try
        {
            return Substitute(value, customProperties, diagnostics, location, visitedSet, depth);
        }
        finally
        {
            visitedSet.Remove(name);
        }
    }

    /// <summary>Resolve a fallback per CSS Custom Properties L1 §3.5. Distinguishes
    /// MISSING fallback (no comma — invalid) from EMPTY fallback (comma + nothing —
    /// valid empty string).</summary>
    private static SubstitutionResult ResolveFallback(
        string? fallbackRaw,
        CustomPropertyTable customProperties,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location,
        HashSet<string>? visited,
        int depth)
    {
        // No comma in the var() at all — INVALID at computed value time per spec.
        if (fallbackRaw is null) return SubstitutionResult.Invalid(UnsetSentinel);
        // Comma present, fallback empty/whitespace — VALID empty string per spec.
        var trimmed = fallbackRaw.Trim();
        if (string.IsNullOrEmpty(trimmed)) return SubstitutionResult.Valid(string.Empty);
        // Fallback may itself contain var() — recurse with the same depth + visited set.
        return Substitute(trimmed, customProperties, diagnostics, location, visited, depth);
    }

    private static int FindMatchingCloseParen(string text, int start)
    {
        int depth = 1;
        var pos = start;
        while (pos < text.Length)
        {
            var c = text[pos];
            if (c == '"' || c == '\'')
            {
                pos = SkipString(text, pos);
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return pos;
            }
            pos++;
        }
        return -1;
    }

    private static (string Name, string? Fallback) SplitOnTopLevelComma(string body)
    {
        int depth = 0;
        var pos = 0;
        while (pos < body.Length)
        {
            var c = body[pos];
            if (c == '"' || c == '\'')
            {
                pos = SkipString(body, pos);
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                return (body[..pos], body[(pos + 1)..]);
            }
            pos++;
        }
        return (body, null);
    }

    private static int SkipString(string text, int start)
    {
        var quote = text[start];
        var pos = start + 1;
        while (pos < text.Length)
        {
            var c = text[pos];
            if (c == '\\' && pos + 1 < text.Length)
            {
                pos += 2;
                continue;
            }
            pos++;
            if (c == quote) return pos;
        }
        return pos; // unterminated; bail out
    }
}
