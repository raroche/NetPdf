// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// One slot in the preprocessor's source-order plan for top-level rules. The unified
/// representation carries enough metadata for the adapter to either pair the slot with
/// AngleSharp's next emitted rule (if the kinds match) OR treat the slot as opaque
/// (if AngleSharp dropped it). This robust merge replaces the earlier two-type slot scheme:
/// any rule AngleSharp drops — current allowlist (<c>@container</c>, <c>@layer</c>) or
/// future regression — is detected at adapter-time by mismatch and gracefully demoted to
/// opaque without ordinal drift.
/// </summary>
/// <param name="Kind">What kind of rule this is in the source: a style rule, or an at-rule
/// (with the at-keyword on <see cref="AtKeyword"/>).</param>
/// <param name="AtKeyword">For at-rules, the at-keyword without the leading <c>@</c>
/// (e.g. <c>"media"</c>, <c>"page"</c>, <c>"container"</c>). <see cref="string.Empty"/> for
/// style rules.</param>
/// <param name="Prelude">For at-rules, everything between the at-keyword and the first
/// <c>{</c> or <c>;</c>. For style rules, the selector text.</param>
/// <param name="RawBody">For block-form rules, the raw text between <c>{</c> and <c>}</c>
/// (without the braces). Empty for statement-form rules. Populated whenever the body
/// content might be needed downstream (opaque modern at-rules) or for diagnostic context.</param>
/// <param name="NestedSlots">For grouping at-rules (<c>@media</c>, <c>@supports</c>,
/// <c>@keyframes</c>): the nested rule plan walked recursively from the body. Empty for
/// non-grouping rules. Lets the adapter splice nested modern at-rules (<c>@container</c>
/// inside <c>@media</c>, etc.) at their proper positions.</param>
/// <param name="Location">Source position of the rule's first character.</param>
internal sealed record CssRuleSlot(
    CssRuleSlotKind Kind,
    string AtKeyword,
    string Prelude,
    string RawBody,
    ImmutableArray<CssRuleSlot> NestedSlots,
    CssSourceLocation Location);

/// <summary>
/// Discriminator on <see cref="CssRuleSlot.Kind"/>.
/// </summary>
internal enum CssRuleSlotKind
{
    /// <summary>A style rule: selector + declaration block.</summary>
    StyleRule = 0,
    /// <summary>An at-rule (block- or statement-form).</summary>
    AtRule = 1,
}
