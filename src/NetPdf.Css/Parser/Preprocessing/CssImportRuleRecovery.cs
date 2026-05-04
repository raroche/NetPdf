// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Information about an <c>@import</c> rule recovered from raw CSS by the pre-pass
/// tokenizer. Closes the third Task 3 blocker from review cycle 1: AngleSharp.Css
/// 1.0.0-beta.144 mangles <c>@import url(...) layer(name)</c> and
/// <c>@import url(...) supports(condition)</c> into a malformed <c>"not all"</c>
/// media query before the CSSOM, losing both the layer name and the supports condition.
/// The pre-pass parses the directive itself and surfaces all four pieces typed.
/// </summary>
/// <param name="OrdinalIndex">0-indexed position among <c>@import</c> rules in source order.
/// AngleSharp emits @import rules in the same order, so the adapter can match by index
/// to overlay this recovery on top of the AngleSharp-derived <see cref="CssImportRule"/>.</param>
/// <param name="Url">The URL inside the <c>url(...)</c> function (or after <c>@import</c>
/// if the source used the bare-string form). Quotes (if any) are stripped.</param>
/// <param name="MediaQuery">The media query authored after the URL/layer/supports clauses.
/// Empty when no media query was present. This is the AUTHORED media query — not
/// AngleSharp's mangled <c>"not all"</c>.</param>
/// <param name="LayerName"><c>null</c> when no <c>layer</c> clause was authored. Empty
/// string for an anonymous <c>layer</c> keyword. Non-empty for <c>layer(name)</c>.</param>
/// <param name="SupportsCondition"><c>null</c> when no <c>supports(...)</c> clause was
/// authored. The condition text without the surrounding <c>supports(</c> and <c>)</c>.</param>
/// <param name="Location">Source position of the <c>@import</c> at-keyword.</param>
internal sealed record CssImportRuleRecovery(
    int OrdinalIndex,
    string Url,
    string MediaQuery,
    string? LayerName,
    string? SupportsCondition,
    CssSourceLocation Location);
