// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Runtime.InteropServices;
using SkiaSharp;

namespace NetPdf.Pdf.Images;

/// <summary>
/// Decodes raster image formats that NetPdf does not natively parse (WebP, AVIF, GIF)
/// into an unpremultiplied RGBA8888 pixel buffer via SkiaSharp. JPEG and PNG have
/// dedicated passthrough paths (<see cref="JpegImageXObject"/>,
/// <see cref="PngImageXObject"/>) that avoid the decode + re-encode round trip — this
/// class is the fallback for everything else.
/// </summary>
/// <remarks>
/// <para>
/// SkiaSharp's bundled native assets include libwebp, libavif, and libgif so all
/// three formats decode without an external native dep. Animated GIFs and multi-image
/// AVIFs are reduced to the first frame — the use case is static embedding in PDF,
/// not animation playback.
/// </para>
/// <para>
/// <b>Trust-boundary contract.</b> Only <see cref="SKCodecResult.Success"/> is accepted.
/// <see cref="SKCodecResult.IncompleteInput"/> per the Skia spec means "a partial image
/// was decoded from incomplete input" — embedding such a result in a PDF would silently
/// produce a corrupted page. The decoder also caps the maximum decoded image size at
/// <see cref="MaxPixelCount"/> (100 megapixels = 400 MB raw RGBA) and validates every
/// arithmetic step against int32 overflow, protecting against malicious inputs that
/// declare absurd dimensions in their headers.
/// </para>
/// <para>
/// <b>Color-management scope (Phase 1).</b> The decoder discards source ICC profiles
/// and color-primary metadata; the output buffer is interpreted as device RGB and the
/// downstream PDF emit path attaches <c>DeviceRGB</c> as the color space. Colors render
/// faithfully on viewers configured for sRGB but profiled-display workflows (Adobe RGB,
/// Display P3, profiled CMYK) lose their target color space. Full ICCBased emission
/// for the raster fallback path is post-Phase-1 work; the same limitation applies to
/// JPEG (<see cref="JpegImageInfo.HasIccProfile"/>) and PNG ICC chunks (not yet parsed).
/// </para>
/// </remarks>
internal static class RasterImageDecoder
{
    /// <summary>
    /// Maximum total pixel count accepted from any single image (100 megapixels,
    /// 400 MB raw RGBA). Bounds memory consumption regardless of input size.
    /// </summary>
    public const int MaxPixelCount = 100_000_000;

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

        var width = codec.Info.Width;
        var height = codec.Info.Height;
        ValidateDecodedDimensions(width, height);

        // Force RGBA8888 unpremultiplied for predictable byte layout.
        var targetInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixelByteCount = checked(width * height * 4);
        var pixels = new byte[pixelByteCount];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var result = codec.GetPixels(targetInfo, handle.AddrOfPinnedObject());
            // Per Skia spec only Success means "fully decoded image data". IncompleteInput
            // means "a partial image was decoded from incomplete input" — silently embedding
            // that in a PDF produces a corrupted page. Reject both that and every other
            // non-Success result with a uniform InvalidDataException contract.
            if (result != SKCodecResult.Success)
            {
                throw new InvalidDataException(
                    $"Raster: SkiaSharp decode produced non-success result '{result}' — image is incomplete or malformed.");
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
            Width = width,
            Height = height,
            HasAlpha = hasAlpha,
            PixelBytes = pixels,
        };
    }

    /// <summary>
    /// Validate dimensions before any allocation: positive, non-overflow, within the
    /// per-image pixel cap. Internal so <see cref="RasterImageXObject"/> can apply the
    /// same contract to <see cref="RasterImageInfo"/> values that arrive through the
    /// direct-build entry point.
    /// </summary>
    internal static void ValidateDecodedDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException(
                $"Raster: invalid decoded dimensions ({width}×{height}); both axes must be positive.");
        }
        // Use checked arithmetic so a malicious header reporting (65535, 65535) cannot
        // wrap into a negative or zero value before reaching the cap check.
        long pixelCount;
        try
        {
            pixelCount = checked((long)width * height);
        }
        catch (OverflowException)
        {
            throw new InvalidDataException(
                $"Raster: decoded dimensions {width}×{height} overflow int64 pixel count.");
        }
        if (pixelCount > MaxPixelCount)
        {
            throw new InvalidDataException(
                $"Raster: decoded image has {pixelCount} pixels which exceeds the per-image cap of {MaxPixelCount}.");
        }
    }
}
