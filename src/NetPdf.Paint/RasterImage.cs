// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Paint;

/// <summary>
/// Side-table entry for a raster image. Stored in <see cref="DisplayList"/> rather than
/// inlined into <see cref="DisplayCommand"/> because the encoded payload is variable-size.
/// <see cref="DisplayCommandKind.ImageDraw"/> commands reference an entry here by index.
/// </summary>
/// <remarks>
/// <para>
/// The same instance can also represent a Skia-rendered subtree raster fallback (Phase 4)
/// — the painter re-encodes such tiles as PNG and embeds them through this same path.
/// </para>
/// <para>
/// <b>Buffer ownership contract.</b> Unlike <see cref="TextRun"/> (which copies its small
/// glyph buffers on assignment), <see cref="EncodedBytes"/> is held by reference. Encoded
/// payloads can be tens or hundreds of kilobytes and the typical lifecycle is:
/// </para>
/// <list type="bullet">
///   <item><description>An image-decoder cache (Phase 4) loads the bytes once.</description></item>
///   <item><description>Each page that draws the image references the same cache slot.</description></item>
///   <item><description>The cache treats slots as frozen between flushes.</description></item>
/// </list>
/// <para>
/// Copying on every insert would defeat that sharing. Callers therefore <b>must</b> ensure
/// the underlying buffer behind <see cref="EncodedBytes"/> is not mutated for the lifetime
/// of this <see cref="RasterImage"/>. The image-cache layer (when it lands) will satisfy
/// this contract structurally; ad-hoc producers should pass owned arrays they don't keep
/// references into.
/// </para>
/// </remarks>
internal sealed class RasterImage
{
    /// <summary>
    /// The encoded image bytes (PNG or JPEG bitstream). The caller-owned buffer behind this
    /// memory MUST be treated as frozen for the lifetime of this <see cref="RasterImage"/>;
    /// see the type-level remarks for the rationale.
    /// </summary>
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
