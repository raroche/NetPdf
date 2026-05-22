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
/// <param name="ExplicitLonghandOrdinals">Per Phase 3 Task 15 L17 post-PR-#77 review —
/// the list of EXPLICIT longhand declarations (NOT shorthand expansions) that appear
/// in the rule body, each with their source ordinal + importance. Used by
/// <c>CssParserAdapter.AdaptDeclarationsWithRecovery</c> to apply CSS Cascade §5
/// importance + §7.4 source-order rules when a shorthand-expansion recovery conflicts
/// with an explicit longhand for the same property. Each entry:
/// <list type="bullet">
///   <item><c>Property</c>: the lower-cased longhand property name.</item>
///   <item><c>Ordinal</c>: 0-based source-order position within the rule body.</item>
///   <item><c>IsImportant</c>: whether the declaration carried <c>!important</c>.</item>
/// </list>
/// Multiple entries for the same property are kept in source order (= a property
/// declared twice produces two entries). The merge walks the list when deciding
/// whether to skip a shorthand-expansion override:
/// <list type="number">
///   <item>If the recovery is <c>!important</c>: only an EXPLICIT
///   <c>!important</c> longhand at a HIGHER ordinal wins (= the recovery survives
///   against later normal-importance explicit longhands).</item>
///   <item>If the recovery is normal: any EXPLICIT
///   <c>!important</c> longhand wins regardless of ordinal; otherwise the highest
///   normal explicit-longhand ordinal wins if it is &gt; the recovery's ordinal.</item>
/// </list>
/// Empty array means no shorthand-vs-explicit-longhand conflicts in this rule.</param>
internal sealed record CssStyleRuleRecovery(
    int OrdinalIndex,
    ImmutableArray<CssDeclarationRecovery> Declarations,
    ImmutableArray<ExplicitLonghandRef> ExplicitLonghandOrdinals = default);

/// <summary>Per Phase 3 Task 15 L17 — minimal record carrying the
/// cascade-relevant metadata for an explicit longhand declaration (= one
/// that is NOT a shorthand expansion). The merge in
/// <c>CssParserAdapter.AdaptDeclarationsWithRecovery</c> reads
/// <see cref="Ordinal"/> + <see cref="IsImportant"/> when comparing
/// against a shorthand-expansion <see cref="CssDeclarationRecovery"/>
/// for the same property per CSS Cascade §5 + §7.4.</summary>
/// <param name="Property">Lower-cased longhand property name.</param>
/// <param name="Ordinal">0-based source-order position within the rule body.</param>
/// <param name="IsImportant">Whether the declaration carried <c>!important</c>.</param>
internal readonly record struct ExplicitLonghandRef(
    string Property,
    int Ordinal,
    bool IsImportant);
