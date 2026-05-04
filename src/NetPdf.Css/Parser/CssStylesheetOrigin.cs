// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser;

/// <summary>
/// Cascade origin of a stylesheet, per CSS Cascade Level 4 §6.1. The cascade resolver
/// (Task 7) sorts declarations by origin first, then by importance, then by layer order,
/// then by specificity, then by source order. Distinct from <see cref="CssStylesheetOwnerKind"/>:
/// origin is the <i>conceptual</i> source (UA stylesheet, user preferences, page author),
/// while owner kind is the <i>physical</i> attachment point (link, style, imported, inline).
/// </summary>
internal enum CssStylesheetOrigin
{
    /// <summary>The browser / engine's built-in default stylesheet. Lowest cascade priority.</summary>
    UserAgent = 0,
    /// <summary>End-user customization stylesheet (uncommon for PDF rendering; reserved).</summary>
    User = 1,
    /// <summary>The page author's stylesheet — the dominant case for invoice / report rendering.</summary>
    Author = 2,
}
