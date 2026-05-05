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
/// <see cref="CssDiagnosticCodes.CssVarCircular001"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Token scanning.</b> The scanner is character-level + quote-aware: it skips
/// single- / double-quoted string contents so a value like
/// <c>content: "var(--x)"</c> isn't treated as a real <c>var()</c> reference. Comments
/// are not stripped here — AngleSharp.Css already does that during parsing.
/// </para>
/// <para>
/// <b>Nested var() in fallbacks.</b> A value like <c>var(--a, var(--b, red))</c> is
/// supported: the fallback is itself processed through the substitution pipeline. Each
/// recursion guards against the same name appearing on the visited stack.
/// </para>
/// <para>
/// <b>Output text.</b> Returns the substituted string with every <c>var(...)</c>
/// reference replaced by its resolved value (or fallback / unset). The returned text is
/// what Tasks 9–10 typed-value parsers will tokenize. Custom-property names that resolve
/// to nothing (no value + no fallback) become the literal token <c>unset</c> — Tasks
/// 9–10 know to interpret it.
/// </para>
/// </remarks>
internal static class VarSubstitution
{
    /// <summary>The literal text emitted when a <c>var()</c> can't be resolved AND has
    /// no fallback. Tasks 9–10 typed-value parsers handle the <c>unset</c> keyword.</summary>
    public const string UnsetSentinel = "unset";

    /// <summary>Substitute every <c>var(--name, fallback)</c> in <paramref name="rawValue"/>.
    /// Returns the substituted string. Emits <see cref="CssDiagnosticCodes.CssVarCircular001"/>
    /// to <paramref name="diagnostics"/> when a circular reference is detected.</summary>
    /// <param name="rawValue">The raw declaration value text (post-AngleSharp normalization).</param>
    /// <param name="customProperties">The resolved custom-property table for the element.
    /// Lookup is case-sensitive per CSS Custom Properties L1 §2.</param>
    /// <param name="diagnostics">Optional sink for circular-reference warnings.</param>
    /// <param name="location">Source location attached to any emitted diagnostic.</param>
    /// <param name="visited">Names currently being resolved on the recursion stack —
    /// callers pass <see langword="null"/> for the top-level entry; recursive calls
    /// carry it through to detect cycles. Always-non-null when consumed inside, but
    /// the parameter is nullable so callers don't need to allocate the empty case.</param>
    public static string Substitute(
        string rawValue,
        CustomPropertyTable customProperties,
        ICssDiagnosticsSink? diagnostics = null,
        CssSourceLocation location = default,
        HashSet<string>? visited = null)
    {
        if (string.IsNullOrEmpty(rawValue)) return rawValue ?? string.Empty;
        if (rawValue.IndexOf("var(", StringComparison.Ordinal) < 0) return rawValue;

        var output = new StringBuilder(rawValue.Length);
        var pos = 0;
        while (pos < rawValue.Length)
        {
            var c = rawValue[pos];
            // Skip quoted-string contents so `content: "var(--x)"` isn't misparsed.
            if (c == '"' || c == '\'')
            {
                var stringEnd = SkipString(rawValue, pos);
                output.Append(rawValue, pos, stringEnd - pos);
                pos = stringEnd;
                continue;
            }
            // Detect `var(`.
            if (c == 'v' && pos + 4 <= rawValue.Length
                && rawValue[pos + 1] == 'a' && rawValue[pos + 2] == 'r' && rawValue[pos + 3] == '(')
            {
                // Found a var() call. Find the matching close paren (paren-balanced,
                // string-aware so commas inside string-valued fallbacks don't split).
                var bodyStart = pos + 4;
                var bodyEnd = FindMatchingCloseParen(rawValue, bodyStart);
                if (bodyEnd < 0)
                {
                    // Unterminated var() — pass through verbatim. Browsers also tolerate
                    // this by leaving the rest of the value alone.
                    output.Append(rawValue, pos, rawValue.Length - pos);
                    return output.ToString();
                }
                var body = rawValue[bodyStart..bodyEnd];
                var resolved = ResolveVar(body, customProperties, diagnostics, location, visited);
                output.Append(resolved);
                pos = bodyEnd + 1; // skip past `)`
                continue;
            }
            output.Append(c);
            pos++;
        }
        return output.ToString();
    }

    /// <summary>Resolve one <c>var(...)</c> body — the portion between <c>var(</c> and
    /// the matching <c>)</c>. Splits on the first top-level comma into name + fallback,
    /// looks the name up in the table, and recurses on the resolved value (or the
    /// fallback when the name is absent).</summary>
    private static string ResolveVar(
        string body,
        CustomPropertyTable customProperties,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location,
        HashSet<string>? visited)
    {
        // Split on the first paren-balanced, string-aware comma into name + fallback.
        var (nameRaw, fallbackRaw) = SplitOnTopLevelComma(body);
        var name = nameRaw.Trim();
        if (string.IsNullOrEmpty(name) || !name.StartsWith("--", StringComparison.Ordinal))
        {
            // Malformed var() — treat as unset/fallback.
            return ResolveFallback(fallbackRaw, customProperties, diagnostics, location, visited);
        }

        // Circular-reference guard — if the name is already being resolved on the stack,
        // emit the diagnostic and short-circuit to fallback / unset.
        var visitedSet = visited;
        if (visitedSet is not null && visitedSet.Contains(name))
        {
            diagnostics?.Emit(new CssDiagnostic(
                CssDiagnosticCodes.CssVarCircular001,
                $"Circular var() reference detected at '{name}'. Resolved to fallback or unset.",
                CssDiagnosticSeverity.Warning,
                location));
            return ResolveFallback(fallbackRaw, customProperties, diagnostics, location, visited);
        }

        if (!customProperties.TryGetValue(name, out var value))
        {
            // Name not declared on this element — fall back per spec.
            return ResolveFallback(fallbackRaw, customProperties, diagnostics, location, visited);
        }

        // The custom-property value itself may contain var() references — recurse with
        // the name pushed onto the visited set.
        visitedSet = visitedSet is null
            ? new HashSet<string>(StringComparer.Ordinal) { name }
            : new HashSet<string>(visitedSet, StringComparer.Ordinal) { name };
        return Substitute(value, customProperties, diagnostics, location, visitedSet);
    }

    private static string ResolveFallback(
        string? fallbackRaw,
        CustomPropertyTable customProperties,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location,
        HashSet<string>? visited)
    {
        if (fallbackRaw is null) return UnsetSentinel;
        var trimmed = fallbackRaw.Trim();
        if (string.IsNullOrEmpty(trimmed)) return UnsetSentinel;
        // Fallback may itself contain var() — recurse.
        return Substitute(trimmed, customProperties, diagnostics, location, visited);
    }

    /// <summary>Find the index of the closing <c>)</c> that matches the implicit opening
    /// paren at <paramref name="start"/>-1 (caller is past the open paren). Skips strings
    /// and counts nested parens. Returns -1 if no matching close is found.</summary>
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

    /// <summary>Split a var()-body on the FIRST paren-balanced, string-aware comma.
    /// Returns (name, fallback) — fallback is <see langword="null"/> when no comma is
    /// present.</summary>
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

    /// <summary>Advance past a quoted-string literal starting at <paramref name="start"/>.
    /// Handles backslash escapes — <c>"a\"b"</c> doesn't terminate at the embedded quote.
    /// Returns the position just past the closing quote.</summary>
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
