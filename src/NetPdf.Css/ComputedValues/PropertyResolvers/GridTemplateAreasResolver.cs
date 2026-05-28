// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
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
/// <para><b>Per PR-#105 review F1 — DoS guards</b>: the resolver caps
/// the source-text length, max rows, max columns, and max total cells
/// it will process before emitting a truncation diagnostic + Invalid
/// result. Hostile CSS like 100,000 rows × 1 cell or a 50,000-char
/// area-name string cannot exhaust memory or CPU.</para>
///
/// <para><b>Per PR-#105 review F1 — single-pass rectangle validation</b>:
/// instead of the original cycle-7a O(uniqueNames × cells) outside-
/// scan, the resolver walks the cells once tracking per-name
/// <c>(minRow, maxRow, minCol, maxCol, count)</c>. At the end each
/// name's bounding-rectangle area must equal its cell count — both
/// conditions detect L-shapes and disjoint regions.</para>
///
/// <para><b>Invalid input</b> emits
/// <see cref="CssDiagnosticCodes.CssPropertyValueInvalid001"/> with a
/// spec-referencing reason. The cascade falls back to the property's
/// initial value (= <c>none</c>) on Invalid.</para>
/// </summary>
internal static class GridTemplateAreasResolver
{
    public const int KeywordIdNone = 0;

    // ---- PR-#105 review F1 — DoS guards ----
    /// <summary>Maximum source-text length the resolver will tokenize.
    /// 16 KiB is generous for hand-authored CSS but blocks the
    /// pathological 100,000-character input.</summary>
    internal const int MaxSourceLength = 16 * 1024;

    /// <summary>Maximum rows (= number of CSS strings). 256 is well
    /// above any realistic invoice / report template (typically ≤ 10).
    /// Combined with <see cref="MaxColumns"/> the total cells stay
    /// bounded.</summary>
    internal const int MaxRows = 256;

    /// <summary>Maximum columns per row. Same rationale as
    /// <see cref="MaxRows"/>.</summary>
    internal const int MaxColumns = 256;

    /// <summary>Hard ceiling on total cells across all rows. Even with
    /// <see cref="MaxRows"/> × <see cref="MaxColumns"/> = 65536, this
    /// ceiling is enforced separately because the typical template is
    /// much smaller and we'd rather diagnose 32K cells (which is
    /// already suspect) than allocate it.</summary>
    internal const int MaxTotalCells = 16 * 1024;

    /// <summary>Maximum length of an area-name custom-ident. CSS
    /// identifiers are essentially unbounded in spec, but a 64-char
    /// cap is well above realistic use + blocks DoS via long names.</summary>
    internal const int MaxIdentLength = 64;

    public static ResolverResult Resolve(
        string value,
        PropertyId propertyId,
        string propertyName,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location)
    {
        // Per PR-#105 review F1 — pre-check source length before any
        // allocation.
        if (value is not null && value.Length > MaxSourceLength)
        {
            EmitInvalid(diagnostics, propertyName, value,
                $"source length {value.Length} exceeds the "
                + $"grid-template-areas cap of {MaxSourceLength} chars",
                location);
            return ResolverResult.Invalid();
        }

        // Defense in depth: CSS-wide keywords cannot serve as a
        // grid-template-areas value at this resolver (the cascade
        // should have intercepted them). Mirrors the pattern from
        // GridLineResolver / GridTemplateListResolver.
        if (GridLineResolver.IsCssWideKeyword(value ?? string.Empty))
        {
            EmitInvalid(diagnostics, propertyName, value ?? string.Empty,
                "CSS-wide keyword reached the grid-template-areas resolver "
                + "(cycle-7a defense-in-depth path)",
                location);
            return ResolverResult.Invalid();
        }

        var span = (value ?? string.Empty).AsSpan();
        var trimmed = span.Trim();
        if (trimmed.IsEmpty
            || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return ResolverResult.Resolved(ComputedSlot.FromKeyword(KeywordIdNone));
        }

        // Tokenize quoted-string rows + split each into cell tokens
        // in a single pass. Cells flatten directly into a row-major
        // array; we also accumulate per-name bounds for the single-
        // pass rectangle validation (per PR-#105 review F1).
        var parseResult = ParseCells(value!);
        if (!parseResult.Ok)
        {
            EmitInvalid(diagnostics, propertyName, value!,
                parseResult.Error, location);
            return ResolverResult.Invalid();
        }

        var cellsBuilder = ImmutableArray.CreateBuilder<string?>(
            parseResult.RowCount * parseResult.ColumnCount);
        for (var i = 0; i < parseResult.Cells.Count; i++)
        {
            cellsBuilder.Add(parseResult.Cells[i]);
        }
        var flatCells = cellsBuilder.ToImmutable();

        // Derive name → rectangle from the per-name accumulators.
        // Validate: bounding-rect area must equal count (= no holes,
        // no outside-rectangle occurrences).
        var nameToRect = ImmutableDictionary.CreateBuilder<string, GridAreaRect>(
            StringComparer.Ordinal);
        foreach (var (name, bounds) in parseResult.NameBounds)
        {
            var rowSpan = bounds.MaxRow - bounds.MinRow + 1;
            var colSpan = bounds.MaxCol - bounds.MinCol + 1;
            if ((long)rowSpan * colSpan != bounds.Count)
            {
                EmitInvalid(diagnostics, propertyName, value!,
                    $"named area '{name}' is not a rectangle (= cells "
                    + $"with this name don't fill a contiguous bounding "
                    + $"box of {rowSpan}×{colSpan}={rowSpan * colSpan} "
                    + $"cells; saw {bounds.Count} occurrences)",
                    location);
                return ResolverResult.Invalid();
            }
            nameToRect[name] = new GridAreaRect(
                RowStart: bounds.MinRow + 1, RowEnd: bounds.MaxRow + 2,
                ColumnStart: bounds.MinCol + 1, ColumnEnd: bounds.MaxCol + 2);
        }

        var ast = new GridTemplateAreas(
            RowCount: parseResult.RowCount,
            ColumnCount: parseResult.ColumnCount,
            Cells: flatCells,
            NameToRect: nameToRect.ToImmutable());
        return ResolverResult.ResolvedSideTable((object)ast);
    }

    /// <summary>Side-effect-free validation for shorthand expanders.
    /// Per PR-#105 review F2, shares the full validation logic
    /// (including rectangle invariants) with <see cref="Resolve"/> so
    /// invalid templates can't pass pre-validation. Differs from
    /// <see cref="Resolve"/> only in that it doesn't allocate the
    /// flat-cells immutable array OR the side-table payload + doesn't
    /// emit diagnostics.</summary>
    internal static bool TryValidate(string value)
    {
        if (value is null) return false;
        if (value.Length > MaxSourceLength) return false;
        if (GridLineResolver.IsCssWideKeyword(value)) return false;
        var trimmed = value.AsSpan().Trim();
        if (trimmed.IsEmpty
            || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var parseResult = ParseCells(value);
        if (!parseResult.Ok) return false;
        // Per PR-#105 review F2 — apply the same rectangle validation
        // as Resolve. Without this, invalid L-shaped / disjoint
        // templates would pass pre-validation in shorthand expanders.
        foreach (var (_, bounds) in parseResult.NameBounds)
        {
            var rowSpan = bounds.MaxRow - bounds.MinRow + 1;
            var colSpan = bounds.MaxCol - bounds.MinCol + 1;
            if ((long)rowSpan * colSpan != bounds.Count) return false;
        }
        return true;
    }

    // =================================================================
    //  Single-pass parser. Tokenizes + cell-splits + name-bounds
    //  accumulation in one pass. Returns a `ParseResult` that the
    //  caller turns into either the side-table payload (Resolve) or
    //  a bool (TryValidate).
    // =================================================================

    private readonly struct NameBounds(int minRow, int maxRow, int minCol, int maxCol, int count)
    {
        public int MinRow { get; } = minRow;
        public int MaxRow { get; } = maxRow;
        public int MinCol { get; } = minCol;
        public int MaxCol { get; } = maxCol;
        public int Count { get; } = count;

        public NameBounds Extend(int row, int col) => new(
            Math.Min(MinRow, row), Math.Max(MaxRow, row),
            Math.Min(MinCol, col), Math.Max(MaxCol, col),
            Count + 1);
    }

    private sealed class ParseResult
    {
        public bool Ok;
        public string Error = string.Empty;
        public int RowCount;
        public int ColumnCount;
        public List<string?> Cells = new();
        public Dictionary<string, NameBounds> NameBounds =
            new(StringComparer.Ordinal);
    }

    private static ParseResult ParseCells(string value)
    {
        var result = new ParseResult();
        var span = value.AsSpan();
        var i = 0;
        var rowIndex = 0;
        var firstRowColumnCount = -1;

        while (i < span.Length)
        {
            // Skip whitespace between row strings.
            var c = span[i];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f')
            {
                i++;
                continue;
            }
            if (c != '"' && c != '\'')
            {
                result.Error = $"expected a CSS string (quoted with \"…\" "
                    + $"or '…') but found '{c}' at position {i}";
                return result;
            }

            if (rowIndex >= MaxRows)
            {
                result.Error = $"row count exceeds MaxRows={MaxRows} cap "
                    + "(grid-template-areas DoS guard)";
                return result;
            }

            // Read the quoted string with escape handling
            // (PR-#105 review F3). Returns the decoded string + the
            // new cursor position.
            if (!TryReadCssString(span, ref i, out var rowString, out var stringError))
            {
                result.Error = stringError;
                return result;
            }

            // Split the row string into cell tokens with proper error
            // reporting (PR-#105 review F10 — actual reason instead
            // of empty-array sentinel).
            var rowCellsStart = result.Cells.Count;
            var splitError = SplitRowIntoCellsInto(
                rowString, rowIndex, result.Cells, result.NameBounds);
            if (splitError is not null)
            {
                result.Error = splitError;
                return result;
            }
            var thisRowCellCount = result.Cells.Count - rowCellsStart;
            if (thisRowCellCount == 0)
            {
                result.Error = $"row {rowIndex + 1} is empty — each "
                    + "row string must contain at least one cell token "
                    + "(<custom-ident> or .) per CSS Grid L1 §7.3";
                return result;
            }
            if (thisRowCellCount > MaxColumns)
            {
                result.Error = $"row {rowIndex + 1} has {thisRowCellCount} "
                    + $"cells, exceeding MaxColumns={MaxColumns} cap";
                return result;
            }
            if (firstRowColumnCount < 0)
            {
                firstRowColumnCount = thisRowCellCount;
            }
            else if (thisRowCellCount != firstRowColumnCount)
            {
                result.Error = $"row {rowIndex + 1} has {thisRowCellCount} "
                    + $"cell tokens but row 1 has {firstRowColumnCount} "
                    + "(= ragged rows are invalid per CSS Grid L1 §7.3)";
                return result;
            }
            if (result.Cells.Count > MaxTotalCells)
            {
                result.Error = $"total cell count exceeds "
                    + $"MaxTotalCells={MaxTotalCells} cap";
                return result;
            }
            rowIndex++;
        }

        if (rowIndex == 0)
        {
            result.Error = "grid-template-areas requires at least one "
                + "<string> row per CSS Grid L1 §7.3";
            return result;
        }

        result.Ok = true;
        result.RowCount = rowIndex;
        result.ColumnCount = firstRowColumnCount;
        return result;
    }

    /// <summary>Per PR-#105 review F3 — read one CSS string token per
    /// CSS Syntax L3 §4.3.5 with escape handling. Decodes the four
    /// escape forms per §4.3.7: hex escape (up to 6 hex digits +
    /// optional whitespace), line continuation (backslash + newline,
    /// ignored), literal-next-char escape, and null/surrogate fallback
    /// to U+FFFD. Rejects raw newlines (per spec §4.3.5 a CSS string
    /// is terminated at a raw newline; we treat that as parse error
    /// to surface the issue rather than producing a "bad string"
    /// token).</summary>
    private static bool TryReadCssString(
        ReadOnlySpan<char> span, ref int cursor,
        out string decoded, out string error)
    {
        decoded = string.Empty;
        error = string.Empty;
        if (cursor >= span.Length)
        {
            error = "expected a CSS string but reached end of input";
            return false;
        }
        var quote = span[cursor];
        if (quote != '"' && quote != '\'')
        {
            error = $"expected quote character but saw '{span[cursor]}' "
                + $"at position {cursor}";
            return false;
        }
        cursor++; // skip opening quote
        var startPos = cursor - 1;
        var sb = new StringBuilder();
        while (cursor < span.Length)
        {
            var c = span[cursor];
            if (c == quote)
            {
                cursor++; // skip closing quote
                decoded = sb.ToString();
                return true;
            }
            if (c == '\n' || c == '\r' || c == '\f')
            {
                error = $"row string starting at position {startPos} "
                    + $"contains a raw newline (forbidden per CSS Syntax "
                    + "L3 §4.3.5; each row must be a single-line CSS "
                    + "string)";
                return false;
            }
            if (c == '\\')
            {
                cursor++;
                if (cursor >= span.Length)
                {
                    error = $"row string starting at position {startPos} "
                        + "ends with a dangling backslash escape";
                    return false;
                }
                var next = span[cursor];
                // Line continuation: backslash + newline → empty.
                if (next == '\n' || next == '\r' || next == '\f')
                {
                    // Consume CR/LF/CRLF pair as a single newline.
                    if (next == '\r' && cursor + 1 < span.Length && span[cursor + 1] == '\n')
                    {
                        cursor += 2;
                    }
                    else
                    {
                        cursor++;
                    }
                    continue;
                }
                // Hex escape: 1..6 hex digits + optional whitespace.
                if (IsHexDigit(next))
                {
                    var hexStart = cursor;
                    var hexLen = 0;
                    while (hexLen < 6 && cursor < span.Length
                        && IsHexDigit(span[cursor]))
                    {
                        cursor++;
                        hexLen++;
                    }
                    // Optional trailing whitespace consumed (per §4.3.7).
                    if (cursor < span.Length)
                    {
                        var ws = span[cursor];
                        if (ws == ' ' || ws == '\t' || ws == '\n' || ws == '\r' || ws == '\f')
                        {
                            // CRLF → consume both.
                            if (ws == '\r' && cursor + 1 < span.Length && span[cursor + 1] == '\n')
                            {
                                cursor += 2;
                            }
                            else
                            {
                                cursor++;
                            }
                        }
                    }
                    var hexStr = span.Slice(hexStart, hexLen).ToString();
                    if (uint.TryParse(hexStr, NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out var cp))
                    {
                        // Per §4.3.7: null code point and surrogates →
                        // U+FFFD; values > U+10FFFF → U+FFFD.
                        if (cp == 0 || (cp >= 0xD800 && cp <= 0xDFFF) || cp > 0x10FFFF)
                        {
                            sb.Append('�');
                        }
                        else
                        {
                            sb.Append(char.ConvertFromUtf32((int)cp));
                        }
                    }
                    continue;
                }
                // Literal-next-char escape (= \X for any non-hex, non-
                // newline char). Append the literal character.
                sb.Append(next);
                cursor++;
                continue;
            }
            sb.Append(c);
            cursor++;
        }
        error = $"row string starting at position {startPos} is missing "
            + $"its closing quote '{quote}'";
        return false;
    }

    private static bool IsHexDigit(char c)
        => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    /// <summary>Per PR-#105 review F10 — split a row string into cell
    /// tokens, appending each token to <paramref name="cells"/> and
    /// updating <paramref name="nameBounds"/> in a single pass.
    /// Returns <see langword="null"/> on success; an error message
    /// (with the offending character and its row-relative position)
    /// on failure.</summary>
    private static string? SplitRowIntoCellsInto(
        string row, int rowIndex,
        List<string?> cells,
        Dictionary<string, NameBounds> nameBounds)
    {
        var span = row.AsSpan();
        var i = 0;
        var colInRow = 0;
        while (i < span.Length)
        {
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
                colInRow++;
                continue;
            }
            if (IsIdentStart(span[i]))
            {
                var start = i;
                while (i < span.Length && IsIdentContinue(span[i])) i++;
                var len = i - start;
                if (len > MaxIdentLength)
                {
                    return $"row {rowIndex + 1} contains a custom-ident "
                        + $"of length {len} (exceeds MaxIdentLength="
                        + $"{MaxIdentLength} cap)";
                }
                var name = span.Slice(start, len).ToString();
                cells.Add(name);
                if (nameBounds.TryGetValue(name, out var existing))
                {
                    nameBounds[name] = existing.Extend(rowIndex, colInRow);
                }
                else
                {
                    nameBounds[name] = new NameBounds(
                        rowIndex, rowIndex, colInRow, colInRow, 1);
                }
                colInRow++;
                continue;
            }
            // Unrecognized character — report with row-relative position.
            return $"row {rowIndex + 1} contains an unexpected character "
                + $"'{span[i]}' at row position {i} (expected "
                + "<custom-ident>, '.', or whitespace)";
        }
        return null;
    }

    private static bool IsIdentStart(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_'
            // Per CSS Syntax §4.4 — non-ASCII identifier code points are
            // allowed. Cycle-7a accepts U+0080+ as ident-start so escaped
            // / Unicode area names parse.
            || c >= 0x80;

    private static bool IsIdentContinue(char c)
        => IsIdentStart(c) || (c >= '0' && c <= '9') || c == '-';

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
