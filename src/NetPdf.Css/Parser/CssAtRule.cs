// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser;

/// <summary>
/// A CSS at-rule, covering both block-form (<c>@media (...) { ... }</c>) and statement-form
/// (<c>@import url(...);</c>) variants. The shape decisions:
/// </summary>
/// <param name="Name">The at-rule name without the leading <c>@</c> (e.g., <c>"media"</c>,
/// <c>"font-face"</c>, <c>"keyframes"</c>, <c>"page"</c>).</param>
/// <param name="Prelude">Everything between the at-rule name and the opening <c>{</c> (or the
/// terminating <c>;</c> for statement-form rules). For <c>@media print and (color)</c> this is
/// <c>"print and (color)"</c>; for <c>@import url("foo.css") screen</c> it is the URL function +
/// optional media list joined by a single space.</param>
/// <param name="Declarations">For declaration-bearing at-rules (<c>@font-face</c>, <c>@page</c>,
/// page-margin boxes such as <c>@top-center</c>): the declarations inside the block. Empty list
/// for grouping at-rules and statement-form at-rules.</param>
/// <param name="ChildRules">For grouping at-rules (<c>@media</c>, <c>@supports</c>,
/// <c>@keyframes</c>): the contained rules. Empty list for declaration-bearing at-rules and
/// statement-form at-rules.</param>
/// <remarks>
/// At most one of <paramref name="Declarations"/> and <paramref name="ChildRules"/> is
/// non-empty for any given at-rule, but the field structure intentionally allows both so
/// future at-rules that mix the two (none in CSS today) don't require a schema change.
/// </remarks>
internal sealed record CssAtRule(
    string Name,
    string Prelude,
    IReadOnlyList<CssDeclaration> Declarations,
    IReadOnlyList<CssRule> ChildRules) : CssRule;
