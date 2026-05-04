// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser;

/// <summary>
/// The abstract base for every CSS rule the adapter emits. Concrete subtypes:
/// <see cref="CssStyleRule"/> (a selector + declaration block), <see cref="CssAtRule"/>
/// (block- and statement-form at-rules), and <see cref="CssImportRule"/> (a typed
/// specialization of <c>@import</c>).
/// </summary>
/// <param name="Location">Source position of the rule in its parent stylesheet. Phase 2
/// Task 3's pre-pass tokenizer backfills real values; for Task 2 every rule gets
/// <see cref="CssSourceLocation.Unknown"/>.</param>
internal abstract record CssRule(CssSourceLocation Location);
