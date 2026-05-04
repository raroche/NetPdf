// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Side-channel data produced by <see cref="CssPreprocessor.Process"/> from raw CSS text.
/// The <see cref="CssParserAdapter"/> consumes this alongside the AngleSharp-derived CSSOM
/// to populate fields AngleSharp.Css 1.0.0-beta.144 cannot supply on its own — page
/// selectors and margin-boxes, modern <c>@import</c> clauses, modern at-rules AngleSharp
/// drops entirely, declarations whose authored values contain modern functions AngleSharp
/// corrupts, and rule source positions.
/// </summary>
/// <param name="PageRecoveries">One entry per <c>@page</c> rule in source order.</param>
/// <param name="ImportRecoveries">One entry per <c>@import</c> rule in source order.</param>
/// <param name="StyleRuleRecoveries">One entry per style rule that contains at least one
/// declaration whose value uses a modern CSS function the AngleSharp parser mishandles.
/// Sparse — many style rules will have no recovery.</param>
/// <param name="RuleSlots">Top-level rule plan in source order. Each entry carries enough
/// metadata for the adapter to either pair the slot with AngleSharp's next rule (kind
/// match) or demote the slot to opaque (mismatch / known-dropped at-rule).</param>
internal sealed record CssPreprocessResult(
    ImmutableArray<CssPageRuleRecovery> PageRecoveries,
    ImmutableArray<CssImportRuleRecovery> ImportRecoveries,
    ImmutableArray<CssStyleRuleRecovery> StyleRuleRecoveries,
    ImmutableArray<CssRuleSlot> RuleSlots)
{
    public static CssPreprocessResult Empty { get; } = new(
        ImmutableArray<CssPageRuleRecovery>.Empty,
        ImmutableArray<CssImportRuleRecovery>.Empty,
        ImmutableArray<CssStyleRuleRecovery>.Empty,
        ImmutableArray<CssRuleSlot>.Empty);
}
