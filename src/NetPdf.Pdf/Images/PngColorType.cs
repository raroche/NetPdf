// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf.Images;

/// <summary>
/// PNG color types per W3C PNG (Third Edition) §11.2.2 IHDR. The numeric values are the
/// on-disk byte values of the IHDR <c>Color type</c> field.
/// </summary>
internal enum PngColorType : byte
{
    /// <summary>1 channel: grayscale. Each pixel is a single intensity sample.</summary>
    Grayscale = 0,

    /// <summary>3 channels: RGB. Each pixel is three samples (red, green, blue).</summary>
    Rgb = 2,

    /// <summary>1 channel: palette index. Each pixel indexes into a PLTE chunk that gives the actual RGB.</summary>
    Indexed = 3,

    /// <summary>2 channels: grayscale + alpha.</summary>
    GrayscaleAlpha = 4,

    /// <summary>4 channels: red, green, blue, alpha.</summary>
    Rgba = 6,
}
