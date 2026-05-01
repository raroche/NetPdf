// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Paint;

/// <summary>
/// Side-table entry for a raster image. Stored in <see cref="DisplayList"/> rather than
/// inlined into <see cref="DisplayCommand"/> because the encoded payload is variable-size.
/// <see cref="DisplayCommandKind.ImageDraw"/> commands reference an entry here by index.
/// </summary>
/// <remarks>
/// The same instance can also represent a Skia-rendered subtree raster fallback (Phase 4)
/// — the painter re-encodes such tiles as PNG and embeds them through this same path.
/// </remarks>
internal sealed class RasterImage
{
    /// <summary>The encoded image bytes (PNG or JPEG bitstream).</summary>
    public required ReadOnlyMemory<byte> EncodedBytes { get; init; }

    /// <summary>Encoding of <see cref="EncodedBytes"/>.</summary>
    public required ImageEncoding Encoding { get; init; }

    /// <summary>Pixel width — required so the painter can pick raster DPI without re-decoding.</summary>
    public required int PixelWidth { get; init; }

    /// <summary>Pixel height — required so the painter can pick raster DPI without re-decoding.</summary>
    public required int PixelHeight { get; init; }

    /// <summary>True if the image carries a non-trivial alpha channel; the embedder will then write an <c>SMask</c>.</summary>
    public bool HasAlpha { get; init; }
}
