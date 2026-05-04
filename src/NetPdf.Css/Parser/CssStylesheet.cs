// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;

namespace NetPdf.Css.Parser;

/// <summary>
/// The top-level container for a parsed CSS stylesheet, in NetPdf's internal AST. One
/// instance per <c>&lt;style&gt;</c> element, per resolved <c>&lt;link rel="stylesheet"&gt;</c>,
/// per <c>@import</c> chain, and per user-agent default sheet that downstream stages consume.
/// </summary>
/// <param name="Rules">The rules in source order. Empty for an empty <c>&lt;style&gt;</c> block.</param>
/// <param name="Href">The sheet's source URL when known (the resolved absolute URL for an
/// external sheet, or <see langword="null"/> for a <c>&lt;style&gt;</c> element). Used by the
/// cascade for relative URL resolution inside the sheet (e.g., <c>url(...)</c> in declarations).</param>
/// <param name="Origin">Cascade origin per CSS Cascade L4 §6.1.</param>
/// <param name="OwnerKind">Where this sheet was attached in the document.</param>
/// <param name="MediaQuery">The sheet's <c>media</c> attribute / <c>@import</c>'s media list.
/// <see langword="null"/> means the sheet applies to every media type. The cascade evaluates
/// this against <c>HtmlPdfOptions.MediaType</c> (defaults to Print) and skips the sheet when
/// the query does not match.</param>
/// <param name="IsDisabled">When <see langword="true"/>, the sheet does not contribute to
/// the cascade. Mirrors <c>&lt;link disabled&gt;</c> and the CSSOM's <c>StyleSheet.disabled</c>
/// flag.</param>
/// <param name="Order">Global source order index across all stylesheets in the document.
/// 0-indexed; the first stylesheet (typically a UA default) gets <c>0</c>, then HEAD-order
/// follows. The cascade uses this as the final tie-breaker per CSS Cascade L4 §6.4.6.</param>
/// <param name="Location">Source position of the <c>&lt;style&gt;</c> element / <c>&lt;link&gt;</c>
/// element in the host HTML document (Phase 2 Task 3 backfills); <see cref="CssSourceLocation.Unknown"/>
/// for now.</param>
internal sealed record CssStylesheet(
    ImmutableArray<CssRule> Rules,
    string? Href,
    CssStylesheetOrigin Origin,
    CssStylesheetOwnerKind OwnerKind,
    string? MediaQuery,
    bool IsDisabled,
    int Order,
    CssSourceLocation Location);
