// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Runtime.InteropServices;
using SkiaSharp;

namespace NetPdf.Pdf.Images;

/// <summary>
/// Decodes raster image formats that NetPdf does not natively parse (WebP, AVIF,
/// GIF) into an unpremultiplied RGBA8888 pixel buffer via SkiaSharp. JPEG and PNG
/// have dedicated passthrough paths (<see cref="JpegImageXObject"/>,
/// <see cref="PngImageXObject"/>) that avoid the decode + re-encode round trip
/// — this class is the fallback for everything else.
/// </summary>
/// <remarks>
/// <para>
/// SkiaSharp's bundled native assets include libwebp, libavif, and libgif so all
/// three formats decode without an external native dep. Animated GIFs and
/// multi-image AVIFs are reduced to the first frame — the use case is static
/// embedding in PDF, not animation playback.
/// </para>
/// <para>
/// The output is always RGBA8888 unpremultiplied so downstream consumers
/// (<see cref="RasterImageXObject"/>) get a predictable byte layout regardless of
/// the source format's native channel encoding. A scan over the alpha channel
/// determines whether the image is "really" opaque (every pixel α = 255) — for
/// such images <see cref="RasterImageInfo.HasAlpha"/> is set false and the PDF
/// emit path skips the SMask plane entirely.
/// </para>
/// </remarks>
internal static class RasterImageDecoder
{
    public static RasterImageInfo Decode(byte[] imageBytes)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
        {
            throw new InvalidDataException("Raster: empty image byte stream.");
        }

        using var inputStream = new SKMemoryStream(imageBytes);
        using var codec = SKCodec.Create(inputStream);
        if (codec is null)
        {
            throw new InvalidDataException(
                "Raster: SkiaSharp could not identify the image format. Supported codecs: WebP, AVIF, GIF, plus JPEG / PNG / BMP / ICO if no dedicated path applies.");
        }

        // Force RGBA8888 unpremultiplied for predictable byte layout.
        var targetInfo = new SKImageInfo(codec.Info.Width, codec.Info.Height,
            SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new byte[targetInfo.BytesSize];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var result = codec.GetPixels(targetInfo, handle.AddrOfPinnedObject());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                throw new InvalidDataException(
                    $"Raster: SkiaSharp decode failed ({result}).");
            }
        }
        finally
        {
            handle.Free();
        }

        // Detect "really opaque" by scanning the alpha channel (every 4th byte).
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0xFF)
            {
                hasAlpha = true;
                break;
            }
        }

        return new RasterImageInfo
        {
            Width = codec.Info.Width,
            Height = codec.Info.Height,
            HasAlpha = hasAlpha,
            PixelBytes = pixels,
        };
    }
}
