// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
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
/// Cycle-4 scope is the family LIST itself; per-entry case folding / generic
/// classification + full font-stack fallback live in the shaper. Quoted names keep
/// their case + spaces; unquoted multi-word names are whitespace-collapsed per the
/// grammar.
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
        var families = ParseList(value);
        if (families.Length == 0)
        {
            diagnostics?.Emit(new CssDiagnostic(
                CssDiagnosticCodes.CssPropertyValueInvalid001,
                $"Could not parse '{propertyName}: {DiagnosticTextSanitizer.Sanitize(value)}' — " +
                "expected a comma-separated list of family names / generic families.",
                CssDiagnosticSeverity.Warning,
                location));
            return ResolverResult.Invalid();
        }

        return ResolverResult.ResolvedSideTable(new FontFamilyList(families));
    }

    /// <summary>Split the top-level (comma-separated) entries, honoring quotes, then
    /// unquote + whitespace-collapse each. Empty entries are dropped.</summary>
    private static ImmutableArray<string> ParseList(string value)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        var current = new StringBuilder();
        var quote = '\0';

        foreach (var c in value)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == ',')
            {
                AppendEntry(builder, current);
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        AppendEntry(builder, current);
        return builder.ToImmutable();
    }

    private static void AppendEntry(ImmutableArray<string>.Builder builder, StringBuilder raw)
    {
        // Collapse runs of ASCII whitespace to single spaces, then trim.
        var sb = new StringBuilder(raw.Length);
        var prevSpace = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                if (sb.Length > 0) prevSpace = true;
                continue;
            }
            if (prevSpace) { sb.Append(' '); prevSpace = false; }
            sb.Append(c);
        }
        if (sb.Length > 0) builder.Add(sb.ToString());
    }
}
