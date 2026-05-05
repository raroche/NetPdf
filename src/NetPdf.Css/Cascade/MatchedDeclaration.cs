// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Parser;

namespace NetPdf.Css.Cascade;

/// <summary>
/// One <see cref="CssDeclaration"/> together with the <see cref="CascadeKey"/> that places
/// it in the cascade. Produced by <see cref="CascadeResolver"/> for every successful
/// (selector, element) pairing; the cascade's per-property winner is the
/// <see cref="MatchedDeclaration"/> with the largest <see cref="Key"/> for that property.
/// </summary>
/// <param name="Declaration">The CSS declaration as adapted from AngleSharp.Css's parsed
/// stylesheet. Property name is always lowercased.</param>
/// <param name="Key">Total cascade-ordering key.</param>
internal sealed record MatchedDeclaration(
    CssDeclaration Declaration,
    CascadeKey Key);
