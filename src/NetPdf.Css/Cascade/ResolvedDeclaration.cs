// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Parser;

namespace NetPdf.Css.Cascade;

/// <summary>
/// One winning <see cref="MatchedDeclaration"/> with its <c>var(--name, fallback)</c>
/// references substituted against the element's resolved custom-property table.
/// Produced by <see cref="VarResolver"/>; consumed by Tasks 9–10 typed-value parsers.
/// </summary>
/// <param name="Property">The longhand property name (lowercased — same shape as
/// <see cref="CssDeclaration.Property"/>).</param>
/// <param name="ResolvedValue">The value text after <c>var()</c> substitution. May
/// contain the <see cref="VarSubstitution.UnsetSentinel"/> token (<c>"unset"</c>) when a
/// referenced custom property had no value AND no fallback. Tasks 9–10 know to interpret
/// the sentinel.</param>
/// <param name="OriginalDeclaration">The pre-substitution declaration — useful for
/// diagnostics / debugging when the resolved value differs from the source text.</param>
/// <param name="Key">The cascade-ordering key carried over from
/// <see cref="MatchedDeclaration.Key"/>, so downstream stages still know origin /
/// importance / specificity / source-order without re-deriving them.</param>
internal sealed record ResolvedDeclaration(
    string Property,
    string ResolvedValue,
    CssDeclaration OriginalDeclaration,
    CascadeKey Key);
