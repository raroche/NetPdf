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
/// <param name="Location">Source position of the at-keyword. Currently
/// <see cref="CssSourceLocation.Unknown"/> until Task 3 wires real positions.</param>
/// <remarks>
/// At most one of <paramref name="Declarations"/> and <paramref name="ChildRules"/> is
/// non-empty for any given at-rule, but the field structure intentionally allows both so
/// future at-rules that mix the two (none in CSS today) don't require a schema change.
/// </remarks>
internal sealed record CssAtRule(
    string Name,
    string Prelude,
    ImmutableArray<CssDeclaration> Declarations,
    ImmutableArray<CssRule> ChildRules,
    CssSourceLocation Location) : CssRule(Location);
