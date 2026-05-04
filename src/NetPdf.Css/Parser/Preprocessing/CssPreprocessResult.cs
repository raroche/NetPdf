// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Side-channel data produced by <see cref="CssPreprocessor.Process"/> from raw CSS text.
/// The <see cref="CssParserAdapter"/> consumes this alongside the AngleSharp-derived CSSOM
/// to populate fields AngleSharp.Css 1.0.0-beta.144 cannot supply on its own — page
/// selectors and margin-boxes, modern <c>@import</c> clauses, and rule source positions.
/// </summary>
/// <param name="PageRecoveries">One entry per <c>@page</c> rule in source order. Empty when
/// the input had no <c>@page</c> rules.</param>
/// <param name="ImportRecoveries">One entry per <c>@import</c> rule in source order.</param>
/// <param name="RulePositions">One entry per top-level rule (style + at-rule), keyed by
/// ordinal index. Used to backfill <see cref="CssRule.Location"/> on adapted rules.</param>
internal sealed record CssPreprocessResult(
    ImmutableArray<CssPageRuleRecovery> PageRecoveries,
    ImmutableArray<CssImportRuleRecovery> ImportRecoveries,
    ImmutableArray<CssRuleSourcePosition> RulePositions)
{
    public static CssPreprocessResult Empty { get; } = new(
        ImmutableArray<CssPageRuleRecovery>.Empty,
        ImmutableArray<CssImportRuleRecovery>.Empty,
        ImmutableArray<CssRuleSourcePosition>.Empty);
}
