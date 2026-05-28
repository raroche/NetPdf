// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Per Phase 3 Task 18 cycle 7a — resolves the
/// <see cref="PropertyType.GridTemplateAreas"/> property
/// (<c>grid-template-areas</c>) per CSS Grid L1 §7.3.
///
/// <para><b>Grammar accepted</b>:</para>
/// <code>
/// grid-template-areas = none | &lt;string&gt;+
/// </code>
///
/// <para>Each <c>&lt;string&gt;</c> declares one row of the area
/// template. The string is split on whitespace into cell tokens:</para>
/// <list type="bullet">
///   <item><c>&lt;custom-ident&gt;</c> tokens — name a cell.
///   Same-name adjacent cells merge into one rectangular area.</item>
///   <item><c>.</c> (one or more periods) — a null cell (no name).</item>
/// </list>
///
/// <para><b>Validation</b> per §7.3:</para>
/// <list type="bullet">
///   <item>Every row string must produce the same column count
///   (= ragged rows are invalid).</item>
///   <item>Every named area must form a SINGLE rectangle. A name
///   appearing in non-rectangular positions is invalid.</item>
///   <item>The empty string OR <c>none</c> resolves to
///   <see cref="GridTemplateAreas.None"/>.</item>
/// </list>
///
/// <para><b>Invalid input</b> emits
/// <see cref="CssDiagnosticCodes.CssPropertyValueInvalid001"/> with a
/// spec-referencing reason. The cascade falls back to the property's
/// initial value (= <c>none</c>) on Invalid.</para>
/// </summary>
internal static class GridTemplateAreasResolver
{
    public const int KeywordIdNone = 0;

    public static ResolverResult Resolve(
        string value,
        PropertyId propertyId,
        string propertyName,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location)
    {
        // Defense in depth: CSS-wide keywords cannot serve as a
        // grid-template-areas value at this resolver (the cascade
        // should have intercepted them). Mirrors the pattern from
        // GridLineResolver / GridTemplateListResolver.
        if (GridLineResolver.IsCssWideKeyword(value))
        {
            EmitInvalid(diagnostics, propertyName, value,
                "CSS-wide keyword reached the grid-template-areas resolver "
                + "(cycle-7a defense-in-depth path)",
                location);
            return ResolverResult.Invalid();
        }

        var trimmed = value.AsSpan().Trim();
        if (trimmed.IsEmpty
            || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return ResolverResult.Resolved(ComputedSlot.FromKeyword(KeywordIdNone));
        }

        // Tokenize as a sequence of CSS strings. Each <string> token is
        // enclosed in single or double quotes per CSS Values L4 §3.2.
        // Whitespace between strings is permitted; anything else is an
        // error.
        var rowStrings = new List<string>();
        if (!TryTokenizeRowStrings(value, rowStrings, out var tokenizeError))
        {
            EmitInvalid(diagnostics, propertyName, value, tokenizeError, location);
            return ResolverResult.Invalid();
        }

        if (rowStrings.Count == 0)
        {
            EmitInvalid(diagnostics, propertyName, value,
                "grid-template-areas requires at least one <string> row "
                + "per CSS Grid L1 §7.3", location);
            return ResolverResult.Invalid();
        }

        // Split each row string on whitespace into cell tokens.
        var cellsByRow = new List<string?[]>(rowStrings.Count);
        var columnCount = -1;
        for (var r = 0; r < rowStrings.Count; r++)
        {
            var cells = SplitRowIntoCells(rowStrings[r]);
            if (cells.Length == 0)
            {
                EmitInvalid(diagnostics, propertyName, value,
                    $"grid-template-areas row {r + 1} is empty — each "
                    + "row string must contain at least one cell token "
                    + "(<custom-ident> or .) per CSS Grid L1 §7.3",
                    location);
                return ResolverResult.Invalid();
            }
            if (columnCount < 0)
            {
                columnCount = cells.Length;
            }
            else if (cells.Length != columnCount)
            {
                EmitInvalid(diagnostics, propertyName, value,
                    $"grid-template-areas row {r + 1} has {cells.Length} "
                    + $"cell tokens but row 1 has {columnCount} (= ragged "
                    + "rows are invalid per CSS Grid L1 §7.3)",
                    location);
                return ResolverResult.Invalid();
            }
            cellsByRow.Add(cells);
        }

        // Validate every named cell forms a single rectangle.
        var rowCount = cellsByRow.Count;
        var flatCells = ImmutableArray.CreateBuilder<string?>(rowCount * columnCount);
        for (var r = 0; r < rowCount; r++)
        {
            for (var c = 0; c < columnCount; c++)
            {
                flatCells.Add(cellsByRow[r][c]);
            }
        }
        var flat = flatCells.ToImmutable();

        var nameToRect = ImmutableDictionary.CreateBuilder<string, GridAreaRect>(
            StringComparer.Ordinal);
        for (var r = 0; r < rowCount; r++)
        {
            for (var c = 0; c < columnCount; c++)
            {
                var name = flat[r * columnCount + c];
                if (name is null) continue;
                if (nameToRect.ContainsKey(name)) continue;

                // Find the rectangle extent of this name. Walk right
                // along this row to find the column extent; then walk
                // down to find the row extent. Then verify EVERY cell
                // in the rectangle has the same name AND no cell with
                // this name exists outside the rectangle.
                var colEnd = c + 1;
                while (colEnd < columnCount
                    && flat[r * columnCount + colEnd] == name)
                {
                    colEnd++;
                }
                var rowEnd = r + 1;
                while (rowEnd < rowCount
                    && flat[rowEnd * columnCount + c] == name)
                {
                    rowEnd++;
                }
                // Validate rectangle interior.
                for (var rr = r; rr < rowEnd; rr++)
                {
                    for (var cc = c; cc < colEnd; cc++)
                    {
                        if (flat[rr * columnCount + cc] != name)
                        {
                            EmitInvalid(diagnostics, propertyName, value,
                                $"grid-template-areas named area '{name}' "
                                + $"does not form a rectangle (cell at row "
                                + $"{rr + 1}, column {cc + 1} is "
                                + (flat[rr * columnCount + cc] is null
                                    ? "null"
                                    : $"'{flat[rr * columnCount + cc]}'")
                                + $" instead of '{name}') per CSS Grid L1 §7.3",
                                location);
                            return ResolverResult.Invalid();
                        }
                    }
                }
                // Validate no cell with this name exists OUTSIDE the
                // rectangle. Scan the full grid.
                for (var rr = 0; rr < rowCount; rr++)
                {
                    for (var cc = 0; cc < columnCount; cc++)
                    {
                        var outside = rr < r || rr >= rowEnd
                            || cc < c || cc >= colEnd;
                        if (outside && flat[rr * columnCount + cc] == name)
                        {
                            EmitInvalid(diagnostics, propertyName, value,
                                $"grid-template-areas named area '{name}' "
                                + "occupies non-rectangular cells (cell at "
                                + $"row {rr + 1}, column {cc + 1} is "
                                + "outside the area's bounding rectangle) "
                                + "per CSS Grid L1 §7.3",
                                location);
                            return ResolverResult.Invalid();
                        }
                    }
                }
                // 1-based line numbers; end is exclusive.
                nameToRect[name] = new GridAreaRect(
                    RowStart: r + 1, RowEnd: rowEnd + 1,
                    ColumnStart: c + 1, ColumnEnd: colEnd + 1);
            }
        }

        var ast = new GridTemplateAreas(
            RowCount: rowCount,
            ColumnCount: columnCount,
            Cells: flat,
            NameToRect: nameToRect.ToImmutable());
        return ResolverResult.ResolvedSideTable((object)ast);
    }

    /// <summary>Validates a <c>grid-template-areas</c> value without
    /// emitting a diagnostic. Mirrors
    /// <see cref="GridLineResolver.TryValidate"/>; used by the
    /// CSS-shorthand expanders for cycle-7 features.</summary>
    internal static bool TryValidate(string value)
    {
        if (value is null) return false;
        if (GridLineResolver.IsCssWideKeyword(value)) return false;
        var trimmed = value.AsSpan().Trim();
        if (trimmed.IsEmpty
            || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var rowStrings = new List<string>();
        if (!TryTokenizeRowStrings(value, rowStrings, out _)) return false;
        if (rowStrings.Count == 0) return false;
        var columnCount = -1;
        var cellsByRow = new List<string?[]>(rowStrings.Count);
        foreach (var rowString in rowStrings)
        {
            var cells = SplitRowIntoCells(rowString);
            if (cells.Length == 0) return false;
            if (columnCount < 0) columnCount = cells.Length;
            else if (cells.Length != columnCount) return false;
            cellsByRow.Add(cells);
        }
        // Skip the rectangle-validation pass; this is a lighter
        // pre-validation for shorthand expanders. The full resolver
        // does the strict pass.
        return true;
    }

    /// <summary>Tokenize the input as a sequence of CSS strings
    /// (quoted single or double). Returns <see langword="false"/> with
    /// a <paramref name="error"/> describing the failure on bad
    /// input.</summary>
    private static bool TryTokenizeRowStrings(
        string value, List<string> rowStrings, out string error)
    {
        error = string.Empty;
        var i = 0;
        var span = value.AsSpan();
        while (i < span.Length)
        {
            var c = span[i];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f')
            {
                i++;
                continue;
            }
            if (c != '"' && c != '\'')
            {
                error = $"grid-template-areas expects a sequence of CSS "
                    + $"strings but found unexpected character '{c}' at "
                    + $"position {i}";
                return false;
            }
            var quote = c;
            i++;
            var start = i;
            while (i < span.Length && span[i] != quote)
            {
                // Reject embedded newlines per CSS Syntax §4.3.5 (bad
                // string token). The author should use one quoted
                // string per row.
                if (span[i] == '\n' || span[i] == '\r' || span[i] == '\f')
                {
                    error = $"grid-template-areas row string contains a "
                        + $"raw newline at position {i}; each row must be "
                        + "a single-line CSS string";
                    return false;
                }
                i++;
            }
            if (i >= span.Length)
            {
                error = $"grid-template-areas row string starting at "
                    + $"position {start - 1} is missing its closing "
                    + $"quote '{quote}'";
                return false;
            }
            rowStrings.Add(span.Slice(start, i - start).ToString());
            i++; // skip closing quote
        }
        return true;
    }

    /// <summary>Split a row string on whitespace into cell tokens.
    /// Each token is either <c>&lt;custom-ident&gt;</c> (= returned
    /// verbatim) or one or more <c>.</c> characters (= null cell —
    /// returned as <see langword="null"/>). Multiple <c>.</c>
    /// characters in a row count as a single null cell per §7.3
    /// (= same effect as a single <c>.</c>).</summary>
    private static string?[] SplitRowIntoCells(string row)
    {
        var cells = new List<string?>();
        var span = row.AsSpan();
        var i = 0;
        while (i < span.Length)
        {
            // Skip whitespace.
            if (span[i] == ' ' || span[i] == '\t')
            {
                i++;
                continue;
            }
            // A run of '.' characters → one null cell.
            if (span[i] == '.')
            {
                while (i < span.Length && span[i] == '.') i++;
                cells.Add(null);
                continue;
            }
            // A custom-ident: run of identifier characters (= per
            // cycle-6a's GridLineResolver tokenizer convention,
            // alphanumeric + dash + underscore is sufficient for the
            // custom-ident grammar at this layer).
            var start = i;
            while (i < span.Length && IsIdentChar(span[i])) i++;
            if (i == start)
            {
                // Unrecognized character — return an empty array to
                // signal failure to the caller (= caller will emit a
                // diagnostic via the "row is empty" check).
                return System.Array.Empty<string?>();
            }
            cells.Add(span.Slice(start, i - start).ToString());
        }
        return cells.ToArray();
    }

    private static bool IsIdentChar(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
        || (c >= '0' && c <= '9') || c == '-' || c == '_';

    private static void EmitInvalid(
        ICssDiagnosticsSink? sink, string propertyName, string value,
        string reason, CssSourceLocation location)
    {
        var safeValue = DiagnosticTextSanitizer.Sanitize(value);
        var safeReason = DiagnosticTextSanitizer.Sanitize(reason);
        sink?.Emit(new CssDiagnostic(
            CssDiagnosticCodes.CssPropertyValueInvalid001,
            $"Could not parse '{propertyName}: {safeValue}' — {safeReason}.",
            CssDiagnosticSeverity.Warning,
            location));
    }
}
