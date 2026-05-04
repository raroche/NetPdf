// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser;

/// <summary>
/// A single CSS declaration: a property name, a value, and the <c>!important</c> flag.
/// </summary>
/// <param name="Property">The longhand property name. AngleSharp.Css expands shorthands at
/// parse time (e.g., <c>background</c> → <c>background-image</c>, <c>background-color</c>,
/// <c>background-repeat-x</c>, …), so this name is always a longhand.</param>
/// <param name="Value">The declaration value as text. Task 9–10 of Phase 2 will replace the
/// raw-text wrapper with a typed value tree (lengths, colors, gradients, etc.); for Task 2 the
/// value is the AngleSharp.Css normalized text (named colors expanded to <c>rgba(...)</c>,
/// units preserved).</param>
/// <param name="IsImportant">Whether the declaration carries the <c>!important</c> annotation.
/// Used by the cascade resolver (Task 7) when resolving conflicting declarations.</param>
internal sealed record CssDeclaration(
    string Property,
    CssValue Value,
    bool IsImportant);
