// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// A single declaration recovered from raw CSS by the preprocessor — used to restore
/// authored values that AngleSharp.Css 1.0.0-beta.144 corrupts or drops. Captured only
/// for declarations whose value contains a modern function the parser mishandles
/// (<c>oklch()</c>, <c>oklab()</c>, <c>color-mix()</c>, <c>light-dark()</c>) or whose
/// property name is in <c>CssPreprocessor.KnownDroppedProperties</c>.
/// </summary>
/// <param name="Property">The longhand property name as authored (lower-cased).</param>
/// <param name="RawValueText">The exact authored value text (with the modern function
/// preserved verbatim — <i>not</i> AngleSharp's corrupted <c>rgba</c> rewrite or empty
/// fallback). Trailing <c>!important</c> already stripped if present.</param>
/// <param name="IsImportant">Whether the declaration carried <c>!important</c>.</param>
/// <param name="IsFromShorthandExpansion">Per Phase 3 Task 15 L17 — <see langword="true"/>
/// when this recovery record was synthesized by expanding a shorthand declaration
/// (e.g., <c>flex</c> → <c>flex-grow</c> / <c>flex-shrink</c> / <c>flex-basis</c>;
/// <c>flex-flow</c> → <c>flex-direction</c> / <c>flex-wrap</c>). The merge in
/// <c>CssParserAdapter.AdaptDeclarationsWithRecovery</c> uses this flag together with
/// <see cref="SourceOrdinal"/> + <see cref="IsImportant"/> + the rule's
/// <c>ExplicitLonghandOrdinals</c> list to apply CSS Cascade §7.4 + §5 ordering
/// rules properly when a shorthand-derived recovery conflicts with an explicit
/// longhand declaration in the same rule.</param>
/// <param name="SourceOrdinal">Per Phase 3 Task 15 L17 post-PR-#77 review — 0-based
/// position in source order within the rule body. Every declaration (shorthand or
/// longhand, recovered or not) gets a unique ordinal during the
/// <c>CssPreprocessor.ScanDeclarations</c> pass. Multiple recovery records emitted
/// from a single shorthand declaration share the SAME ordinal (= they expanded from
/// the SAME source-position declaration). The merge compares this against the
/// rule's <c>ExplicitLonghandOrdinals</c> entries to determine which declaration
/// wins per the cascade — supports multi-shorthand cases
/// (<c>flex-flow ...; flex-wrap ...; flex-flow ...</c>) AND <c>!important</c>
/// interactions that a per-property set cannot represent.</param>
internal sealed record CssDeclarationRecovery(
    string Property,
    string RawValueText,
    bool IsImportant,
    bool IsFromShorthandExpansion = false,
    int SourceOrdinal = -1);
