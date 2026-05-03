// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// Raw font-file bytes plus optional metadata. NetPdf parses the bytes to extract metrics,
/// glyph outlines, and OpenType tables. Supported formats in v1: TTF, OTF, WOFF.
/// </summary>
public sealed class FontFaceData
{
    public required ReadOnlyMemory<byte> Bytes { get; init; }
    public string? Family { get; init; }
    public int? WeightCss { get; init; }
    public FontStyle? Style { get; init; }

    /// <summary>
    /// CSS stretch value (1..9). <c>5</c> = normal width; <c>1</c> = ultra-condensed;
    /// <c>9</c> = ultra-expanded. Sourced from <c>OS/2.usWidthClass</c> per the OpenType
    /// spec. Null when the resolver did not surface a width — callers should treat null
    /// as "normal" (5) for matching purposes per CSS Fonts Level 4 §5.2.3.
    /// </summary>
    public int? StretchCss { get; init; }

    public string? PostScriptName { get; init; }
    public Uri? Source { get; init; }
}
