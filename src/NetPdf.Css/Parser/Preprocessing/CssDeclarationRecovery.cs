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
internal sealed record CssDeclarationRecovery(
    string Property,
    string RawValueText,
    bool IsImportant);
