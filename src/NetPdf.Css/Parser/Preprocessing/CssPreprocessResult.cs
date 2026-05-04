// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Side-channel data produced by <see cref="CssPreprocessor.Process"/> from raw CSS text.
/// The <see cref="CssParserAdapter"/> consumes this alongside the AngleSharp-derived CSSOM
/// to populate fields AngleSharp.Css 1.0.0-beta.144 cannot supply on its own — page
/// selectors and margin-boxes, modern <c>@import</c> clauses, modern at-rules AngleSharp
/// drops entirely, and rule source positions.
/// </summary>
/// <param name="PageRecoveries">One entry per <c>@page</c> rule in source order.</param>
/// <param name="ImportRecoveries">One entry per <c>@import</c> rule in source order.</param>
/// <param name="RuleSlots">Top-level rule plan in source order. Each entry is either a
/// <see cref="CssAngleSharpRuleSlot"/> (signals "the next AngleSharp rule lands here") or a
/// <see cref="CssOpaqueAtRuleSlot"/> (a modern at-rule AngleSharp drops, recovered as opaque).
/// The adapter walks slots in order to assemble the final AST.</param>
internal sealed record CssPreprocessResult(
    ImmutableArray<CssPageRuleRecovery> PageRecoveries,
    ImmutableArray<CssImportRuleRecovery> ImportRecoveries,
    ImmutableArray<CssPreprocessRuleSlot> RuleSlots)
{
    public static CssPreprocessResult Empty { get; } = new(
        ImmutableArray<CssPageRuleRecovery>.Empty,
        ImmutableArray<CssImportRuleRecovery>.Empty,
        ImmutableArray<CssPreprocessRuleSlot>.Empty);
}
