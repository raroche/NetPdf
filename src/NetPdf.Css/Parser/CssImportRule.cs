// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;

namespace NetPdf.Css.Parser;

/// <summary>
/// A typed <c>@import</c> rule. Separate subtype (rather than a generic <see cref="CssAtRule"/>
/// with a stringly-typed prelude) because the cascade + resource loader need structured
/// access to the URL, the media list, the cascade layer, and the supports condition without
/// re-parsing the prelude string.
/// </summary>
/// <param name="Url">The import target's URL exactly as it appeared in the source (relative
/// or absolute). Resolution against the parent sheet's <c>Href</c> happens at resource-load
/// time, not in the parser.</param>
/// <param name="MediaQuery">The media query attached to the import (everything after
/// <c>url(...)</c> when AngleSharp.Css recognized it as a media query). Empty when no media
/// list was authored.</param>
/// <param name="LayerName">The cascade layer the imported sheet should join: <c>null</c> when
/// no <c>layer()</c> was authored, the empty string for an anonymous <c>layer</c> keyword, a
/// non-empty string for <c>layer(name)</c>. Always <c>null</c> in Task 2 — AngleSharp.Css
/// 1.0.0-beta.144 collapses the layer clause into a malformed media query before the CSSOM,
/// so the only way to recover the layer name is the Task 3 pre-pass tokenizer running over
/// the raw stylesheet text before AngleSharp parses it.</param>
/// <param name="SupportsCondition">The <c>supports(...)</c> condition: <c>null</c> when not
/// authored. Always <c>null</c> in Task 2 — same AngleSharp.Css limitation as
/// <paramref name="LayerName"/>.</param>
/// <param name="ImportedRules">Rules from the resolved imported sheet, when ResourceLoader
/// integration lands (Phase 2 Task 12+ or Phase 5). <see cref="ImmutableArray{T}.Empty"/>
/// until then.</param>
/// <param name="Location">Source position of the <c>@import</c> at-keyword.</param>
internal sealed record CssImportRule(
    string Url,
    string MediaQuery,
    string? LayerName,
    string? SupportsCondition,
    ImmutableArray<CssRule> ImportedRules,
    CssSourceLocation Location) : CssRule(Location);
