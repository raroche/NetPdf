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
    public string? PostScriptName { get; init; }
    public Uri? Source { get; init; }
}
