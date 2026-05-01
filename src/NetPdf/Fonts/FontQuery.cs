// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// A request to find a font face. <see cref="Family"/> is the CSS-resolved family name
/// (after generic-family expansion). <see cref="WeightCss"/> is the CSS numeric weight
/// (100..900); 400 = normal, 700 = bold.
/// </summary>
public readonly record struct FontQuery
{
    public required string Family { get; init; }
    public required int WeightCss { get; init; }
    public FontStyle Style { get; init; }
    public int? StretchCss { get; init; }
    public string? Script { get; init; }
    public string? Language { get; init; }
}

public enum FontStyle
{
    Normal,
    Italic,
    Oblique,
}
