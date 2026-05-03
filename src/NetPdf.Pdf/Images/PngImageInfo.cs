// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf.Images;

/// <summary>
/// Metadata + raw streams extracted from a PNG file by <see cref="PngHeaderParser"/>,
/// consumed by <see cref="PngImageXObject"/> to emit a PDF Image XObject (and optional
/// SMask) without re-compressing the image data when possible.
/// </summary>
internal sealed record PngImageInfo
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>1, 2, 4, 8, or 16 — per IHDR.</summary>
    public required int BitDepth { get; init; }

    public required PngColorType ColorType { get; init; }

    /// <summary>True when <see cref="ColorType"/> is <see cref="PngColorType.GrayscaleAlpha"/> or <see cref="PngColorType.Rgba"/>.</summary>
    public bool HasAlpha => ColorType is PngColorType.GrayscaleAlpha or PngColorType.Rgba;

    /// <summary>True when <see cref="ColorType"/> is <see cref="PngColorType.Indexed"/>.</summary>
    public bool IsIndexed => ColorType == PngColorType.Indexed;

    /// <summary>
    /// PLTE bytes (3 bytes per entry: R, G, B). Required for <see cref="PngColorType.Indexed"/>;
    /// absent for other color types. Length is <c>3 × paletteEntryCount</c>.
    /// </summary>
    public byte[]? Palette { get; init; }

    /// <summary>
    /// Raw tRNS chunk bytes when the file declares transparency.
    /// </summary>
    /// <list type="bullet">
    ///   <item><b>Grayscale</b> (color type 0): 2 bytes — a single 16-bit big-endian
    ///         transparent gray value. Pixels exactly equal to this value render
    ///         transparent in PDF output via a color-key <c>/Mask</c>.</item>
    ///   <item><b>RGB</b> (color type 2): 6 bytes — three 16-bit big-endian transparent
    ///         channel values. Pixels exactly equal to (R, G, B) render transparent via
    ///         <c>/Mask [r r g g b b]</c>.</item>
    ///   <item><b>Indexed</b> (color type 3): 1 byte per palette entry, in palette order.
    ///         Each byte is the alpha for the corresponding palette index. PNG allows the
    ///         tRNS to carry fewer alpha bytes than there are palette entries — entries
    ///         past the array are assumed fully opaque (255).</item>
    /// </list>
    /// <para>
    /// Color types 4 (GA) and 6 (RGBA) carry alpha in the image data and must NOT have a
    /// tRNS chunk (the parser rejects this combination).
    /// </para>
    public byte[]? TransparencyChunk { get; init; }

    /// <summary>
    /// Concatenated raw IDAT bytes — already zlib-compressed (deflate stream wrapped in
    /// 2-byte zlib header + 4-byte Adler-32 trailer). For PDF passthrough, these can be
    /// emitted directly under <c>/Filter /FlateDecode</c>.
    /// </summary>
    public required ReadOnlyMemory<byte> CompressedIdatBytes { get; init; }

    /// <summary>True when the PNG uses Adam7 interlacing (IHDR interlace method 1). Phase 1 rejects these.</summary>
    public bool IsInterlaced { get; init; }

    /// <summary>
    /// Number of color components (excluding alpha). 1 for grayscale / indexed; 3 for RGB.
    /// </summary>
    public int ColorComponents => ColorType switch
    {
        PngColorType.Grayscale => 1,
        PngColorType.Rgb => 3,
        PngColorType.Indexed => 1,
        PngColorType.GrayscaleAlpha => 1,
        PngColorType.Rgba => 3,
        _ => throw new InvalidDataException($"PNG: unsupported color type {ColorType}."),
    };

    /// <summary>Total channel count (color + alpha).</summary>
    public int Channels => ColorType switch
    {
        PngColorType.Grayscale => 1,
        PngColorType.Rgb => 3,
        PngColorType.Indexed => 1,
        PngColorType.GrayscaleAlpha => 2,
        PngColorType.Rgba => 4,
        _ => throw new InvalidDataException($"PNG: unsupported color type {ColorType}."),
    };

    /// <summary>Number of bytes the per-scanline filter operates over for a single pixel position.</summary>
    public int BytesPerPixelForFilter => Math.Max(1, (Channels * BitDepth) / 8);

    /// <summary>Number of bytes per scanline (excluding the 1-byte filter prefix).</summary>
    public int ScanlineByteWidth => (Width * Channels * BitDepth + 7) / 8;
}
