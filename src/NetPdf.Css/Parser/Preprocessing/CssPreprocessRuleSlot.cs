// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// One slot in the preprocessor's source-order plan for top-level rules. The adapter walks
/// these in order to assemble the final AST, splicing AngleSharp-emitted rules into the
/// places where AngleSharp will produce them and inserting opaque modern at-rules in the
/// places where AngleSharp drops them.
/// </summary>
/// <remarks>
/// <para>
/// This shape exists to fix two related problems:
/// </para>
/// <list type="bullet">
///   <item><description><b>Ordinal drift:</b> AngleSharp.Css 1.0.0-beta.144 silently drops
///   <c>@container</c>, <c>@layer</c> block-form, and <c>@layer</c> statement-form rules.
///   Without this slot list, my preprocessor's per-rule source positions would index into
///   AngleSharp's reduced rule list and report the wrong line for normal rules following
///   any modern at-rule.</description></item>
///   <item><description><b>Modern at-rule preservation:</b> the Phase 2 plan calls for
///   modern at-rules to be preserved as opaque AST nodes. Storing them here as
///   <see cref="CssOpaqueAtRuleSlot"/> entries means the adapter sees them in source order
///   and can emit corresponding <see cref="CssAtRule"/> entries even though AngleSharp
///   never produced one.</description></item>
/// </list>
/// </remarks>
internal abstract record CssPreprocessRuleSlot(CssSourceLocation Location);

/// <summary>
/// A slot for a rule the preprocessor expects AngleSharp to emit. Carries only the source
/// location; the adapter pairs each consecutive <c>CssAngleSharpRuleSlot</c> with the next
/// rule from <c>ICssStyleSheet.Rules</c>.
/// </summary>
internal sealed record CssAngleSharpRuleSlot(CssSourceLocation Location)
    : CssPreprocessRuleSlot(Location);

/// <summary>
/// A slot for an at-rule AngleSharp drops. Carries everything needed to construct an
/// opaque <see cref="CssAtRule"/>: the at-keyword name (e.g., <c>"container"</c>,
/// <c>"layer"</c>) and the prelude (everything between the at-keyword and the opening
/// <c>{</c> or terminating <c>;</c>).
/// </summary>
/// <param name="Name">At-keyword name without the leading <c>@</c>.</param>
/// <param name="Prelude">Trimmed text after the name.</param>
/// <param name="Location">Source position of the at-keyword.</param>
internal sealed record CssOpaqueAtRuleSlot(string Name, string Prelude, CssSourceLocation Location)
    : CssPreprocessRuleSlot(Location);
