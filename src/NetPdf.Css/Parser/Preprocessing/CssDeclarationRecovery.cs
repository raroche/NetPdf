// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// A single declaration recovered from raw CSS by the preprocessor — used to restore
/// authored values that AngleSharp.Css 1.0.0-beta.144 corrupts or drops. Captured only
/// for declarations whose value contains a modern function the parser mishandles
/// (<c>oklch()</c>, <c>oklab()</c>, <c>color-mix()</c>, <c>light-dark()</c>).
/// </summary>
/// <param name="Property">The longhand property name as authored (lower-cased).</param>
/// <param name="RawValueText">The exact authored value text (with the modern function
/// preserved verbatim — <i>not</i> AngleSharp's corrupted <c>rgba</c> rewrite or empty
/// fallback). Trailing <c>!important</c> already stripped if present.</param>
/// <param name="IsImportant">Whether the declaration carried <c>!important</c>.</param>
/// <param name="IsFromShorthandExpansion">Per Phase 3 Task 15 L16 post-PR-#76 review
/// finding #1 (P1) — <see langword="true"/> when this recovery record was synthesized
/// by expanding a shorthand declaration (e.g., <c>flex</c> → <c>flex-grow</c> /
/// <c>flex-shrink</c> / <c>flex-basis</c>, <c>flex-flow</c> → <c>flex-direction</c> /
/// <c>flex-wrap</c>). The merge in <c>CssParserAdapter.AdaptDeclarationsWithRecovery</c>
/// uses this flag to switch to <b>append-only</b> semantics for shorthand-derived
/// recoveries: AngleSharp's existing longhand emit is trusted (= preserves CSS Cascade
/// §7.4 last-decl-wins source order for cases like
/// <c>flex-flow: row wrap; flex-wrap: nowrap;</c> where the explicit later longhand
/// must beat the earlier shorthand expansion). For non-shorthand recoveries (the
/// original modern-color / align-items / etc. surface), the merge still overrides
/// AngleSharp's emit verbatim because those recoveries are the AUTHOR'S original
/// value that AngleSharp corrupted / fell back from.</param>
internal sealed record CssDeclarationRecovery(
    string Property,
    string RawValueText,
    bool IsImportant,
    bool IsFromShorthandExpansion = false);
