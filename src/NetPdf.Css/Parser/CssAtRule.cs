// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;

namespace NetPdf.Css.Parser;

/// <summary>
/// A CSS at-rule, covering both block-form (<c>@media (...) { ... }</c>) and statement-form
/// (<c>@charset</c>, <c>@namespace</c>) variants. <c>@import</c> has its own typed subtype
/// (<see cref="CssImportRule"/>) so cascade and resource-loader stages don't have to parse a
/// stringly-typed prelude.
/// </summary>
/// <param name="Name">The at-rule name without the leading <c>@</c> (e.g., <c>"media"</c>,
/// <c>"font-face"</c>, <c>"keyframes"</c>, <c>"page"</c>).</param>
/// <param name="Prelude">Everything between the at-rule name and the opening <c>{</c> (or the
/// terminating <c>;</c> for statement-form rules).</param>
/// <param name="Declarations">For declaration-bearing at-rules (<c>@font-face</c>, <c>@page</c>,
/// page-margin boxes such as <c>@top-center</c>): the declarations inside the block. Empty
/// for grouping at-rules and statement-form at-rules.</param>
/// <param name="ChildRules">For grouping at-rules (<c>@media</c>, <c>@supports</c>,
/// <c>@keyframes</c>): the contained rules. Empty for declaration-bearing at-rules and
/// statement-form at-rules.</param>
/// <param name="Location">Source position of the at-keyword.</param>
/// <param name="RawBody">For at-rules whose body AngleSharp.Css 1.0.0-beta.144 cannot
/// decompose for us — currently the modern at-rules <c>@container</c> and <c>@layer</c>
/// block-form — the raw text inside the braces (without the braces themselves). Empty for
/// rules that were decomposed by AngleSharp normally. Lets the cascade resolver in Task 7
/// re-parse opaque bodies on demand without re-reading the original stylesheet.</param>
/// <remarks>
/// At most one of <paramref name="Declarations"/> and <paramref name="ChildRules"/> is
/// non-empty for any given at-rule, but the field structure intentionally allows both so
/// future at-rules that mix the two (none in CSS today) don't require a schema change.
/// <paramref name="RawBody"/> is independent of both — it can coexist with empty
/// declarations + empty children when the body is opaque to this stage of the pipeline.
/// </remarks>
internal sealed record CssAtRule(
    string Name,
    string Prelude,
    ImmutableArray<CssDeclaration> Declarations,
    ImmutableArray<CssRule> ChildRules,
    CssSourceLocation Location,
    string RawBody = "") : CssRule(Location);
