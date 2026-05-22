// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// A style rule's raw declaration recoveries — the property/value pairs whose authored
/// values contained modern CSS functions AngleSharp.Css 1.0.0-beta.144 mishandles. The
/// adapter merges these on top of AngleSharp's emitted declarations: existing properties
/// get their values overridden with the recovered raw text; missing properties (AngleSharp
/// dropped them when it produced an empty rule body) get added.
/// </summary>
/// <param name="OrdinalIndex">0-indexed position of this rule among style rules in source
/// order. Aligns with the adapter's iteration over AngleSharp's emitted style rules.</param>
/// <param name="Declarations">Recovered declarations. Empty array means no modern-function
/// declarations were detected; the rule is fully described by AngleSharp's output.</param>
/// <param name="ExplicitLonghandsAfterShorthand">Per Phase 3 Task 15 L17 — the closed
/// set of longhand property names that appear in the rule body AT A LATER source-order
/// position than a shorthand expansion that would set the same longhand. Used by
/// <c>CssParserAdapter.AdaptDeclarationsWithRecovery</c> to skip the shorthand-expansion
/// recovery's override per CSS Cascade §7.4 last-decl-wins: when this set contains
/// (say) <c>flex-wrap</c>, an explicit <c>flex-wrap:</c> declaration appeared AFTER
/// a <c>flex-flow:</c> shorthand in the same rule + must win over the shorthand's
/// expansion. Empty array means no shorthand-vs-explicit-longhand conflicts in this
/// rule.</param>
internal sealed record CssStyleRuleRecovery(
    int OrdinalIndex,
    ImmutableArray<CssDeclarationRecovery> Declarations,
    ImmutableArray<string> ExplicitLonghandsAfterShorthand = default);
