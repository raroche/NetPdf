// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Immutable;
using System.Text;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Resolves CSS <c>font-family</c> (CSS Fonts 4 §2.1): a comma-separated list of
/// <c>&lt;family-name&gt;</c> (quoted string or space-separated identifiers) and
/// <c>&lt;generic-family&gt;</c> keywords. Produces a <see cref="FontFamilyList"/>
/// stored in the side table (a <see cref="ResolverResult.ResolvedSideTable"/>).
/// </summary>
/// <remarks>
/// Scope is the family LIST itself: it is parsed AND validated against the grammar
/// (a malformed list is <see cref="ResolverResult.Invalid"/> + a diagnostic, not
/// silently sanitized). Per-entry case folding / generic classification + the
/// font-stack fallback walk live in the shaper (<c>HarfBuzzShaperResolver</c>). Quoted
/// names keep their case + spaces; unquoted multi-word names are whitespace-collapsed.
/// </remarks>
internal static class FontFamilyListResolver
{
    public static ResolverResult Resolve(
        string value,
        PropertyId propertyId,
        string propertyName,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location)
    {
        if (!TryParseList(value, out var families))
        {
            diagnostics?.Emit(new CssDiagnostic(
                CssDiagnosticCodes.CssPropertyValueInvalid001,
                $"Could not parse '{propertyName}: {DiagnosticTextSanitizer.Sanitize(value)}' — " +
                "expected a comma-separated list of <family-name> (quoted string, or unquoted " +
                "identifiers) and <generic-family> keywords.",
                CssDiagnosticSeverity.Warning,
                location));
            return ResolverResult.Invalid();
        }

        return ResolverResult.ResolvedSideTable(new FontFamilyList(families));
    }

    /// <summary>Parse + VALIDATE the comma-separated family list per the CSS Fonts 4
    /// §2.1 <c>&lt;family-name&gt;</c> grammar. Returns <see langword="false"/> (→ the
    /// declaration is invalid + dropped, falling back to inherited) for a malformed
    /// list: empty entries (leading / trailing / doubled commas), an unclosed quote,
    /// junk after a quoted string, or an unquoted name that isn't a sequence of CSS
    /// identifiers (e.g. a digit- or punctuation-leading token). Quoted names keep
    /// their case + interior spaces; unquoted multi-word names are whitespace-collapsed.</summary>
    private static bool TryParseList(string value, out ImmutableArray<string> families)
    {
        families = default;
        var builder = ImmutableArray.CreateBuilder<string>();
        var i = 0;
        var n = value.Length;

        while (true)
        {
            // Skip whitespace before the entry. Reaching the end here means the list
            // ended on a comma (trailing / doubled) or was empty → invalid.
            while (i < n && IsWhitespace(value[i])) i++;
            if (i >= n) return false;

            string entry;
            if (value[i] is '"' or '\'')
            {
                // Quoted <string> family name — keep case + interior spaces. The quote
                // MUST close, and only whitespace may follow before the comma / end.
                var quote = value[i++];
                var sb = new StringBuilder();
                var closed = false;
                while (i < n)
                {
                    var c = value[i++];
                    if (c == quote) { closed = true; break; }
                    sb.Append(c);
                }
                if (!closed) return false;                      // unclosed quote.
                while (i < n && IsWhitespace(value[i])) i++;
                if (i < n && value[i] != ',') return false;     // junk after the string.
                entry = sb.ToString();
                if (entry.Length == 0) return false;            // empty quoted name "".
            }
            else
            {
                // Unquoted <custom-ident>+ : a whitespace-separated run of identifiers
                // up to the next top-level comma. A quote may not appear mid-name.
                var start = i;
                while (i < n && value[i] != ',')
                {
                    if (value[i] is '"' or '\'') return false;
                    i++;
                }
                entry = CollapseWhitespace(value.AsSpan(start, i - start));
                if (!IsValidUnquotedFamily(entry)) return false;
            }

            builder.Add(entry);
            if (i >= n) break;     // end of input.
            i++;                   // consume the comma; parse the next entry.
        }

        families = builder.ToImmutable();
        return families.Length > 0;
    }

    /// <summary>A valid unquoted family name is one or more CSS identifiers separated
    /// by single spaces (the input is already whitespace-collapsed).</summary>
    private static bool IsValidUnquotedFamily(string entry)
    {
        if (entry.Length == 0) return false;
        var start = 0;
        for (var i = 0; i <= entry.Length; i++)
        {
            if (i == entry.Length || entry[i] == ' ')
            {
                if (!IsValidIdentifier(entry.AsSpan(start, i - start))) return false;
                start = i + 1;
            }
        }
        return true;
    }

    /// <summary>A CSS identifier per CSS Syntax 3 §4.3.11 (escapes aside): an
    /// ident-start char (letter / <c>_</c> / non-ASCII), or a leading <c>-</c> followed
    /// by an ident-start char or a second <c>-</c>; then ident chars (those plus digits
    /// and <c>-</c>). Rejects digit- or punctuation-leading tokens.</summary>
    private static bool IsValidIdentifier(ReadOnlySpan<char> ident)
    {
        if (ident.Length == 0) return false;
        var c0 = ident[0];
        if (c0 == '-')
        {
            if (ident.Length == 1) return false;               // "-" alone is not a name.
            var c1 = ident[1];
            if (c1 != '-' && !IsIdentStart(c1)) return false;  // "-2"/"-." invalid; "--x" ok.
        }
        else if (!IsIdentStart(c0))
        {
            return false;                                      // digit / punctuation start.
        }
        for (var i = 1; i < ident.Length; i++)
            if (!IsIdentChar(ident[i])) return false;
        return true;
    }

    /// <summary>Collapse runs of ASCII whitespace to single spaces + trim.</summary>
    private static string CollapseWhitespace(ReadOnlySpan<char> raw)
    {
        var sb = new StringBuilder(raw.Length);
        var prevSpace = false;
        foreach (var c in raw)
        {
            if (IsWhitespace(c))
            {
                if (sb.Length > 0) prevSpace = true;
                continue;
            }
            if (prevSpace) { sb.Append(' '); prevSpace = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';
    private static bool IsIdentStart(char c) => c == '_' || c >= 0x80 || IsAsciiLetter(c);
    private static bool IsIdentChar(char c) => c == '_' || c == '-' || c >= 0x80 || IsAsciiLetter(c) || IsDigit(c);
    private static bool IsAsciiLetter(char c) => (uint)((c | 0x20) - 'a') <= 'z' - 'a';
    private static bool IsDigit(char c) => (uint)(c - '0') <= 9;
}
