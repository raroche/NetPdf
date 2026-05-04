// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser;

/// <summary>
/// The abstract base for every CSS rule the adapter emits. Concrete subtypes are
/// <see cref="CssStyleRule"/> (a selector + declaration block, e.g., <c>.foo { color: red }</c>)
/// and <see cref="CssAtRule"/> (any at-rule, e.g., <c>@media</c>, <c>@font-face</c>).
/// </summary>
internal abstract record CssRule;
