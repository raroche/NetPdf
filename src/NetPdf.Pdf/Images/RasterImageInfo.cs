// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf.Images;

/// <summary>
/// Decoded raster image — width, height, and a tightly-packed unpremultiplied
/// RGBA8888 byte buffer (4 bytes per pixel, scanline-major). Produced by
/// <see cref="RasterImageDecoder"/> from formats NetPdf does not natively decode
/// (WebP, AVIF, GIF). The PDF emit path then strips the alpha channel into a
/// separate plane for the SMask when the image is non-opaque.
/// </summary>
internal sealed record RasterImageInfo
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>True when at least one pixel has alpha &lt; 255. False = fully opaque.</summary>
    public required bool HasAlpha { get; init; }

    /// <summary>Unpremultiplied RGBA8888 pixels, scanline-major (4 × Width × Height bytes).</summary>
    public required byte[] PixelBytes { get; init; }
}
