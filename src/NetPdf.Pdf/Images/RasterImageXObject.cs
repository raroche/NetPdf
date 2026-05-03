// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.IO.Compression;
using NetPdf.Pdf.Objects;

namespace NetPdf.Pdf.Images;

/// <summary>
/// Wraps a <see cref="RasterImageInfo"/> (decoded via <see cref="RasterImageDecoder"/>
/// from WebP / AVIF / GIF / etc.) into a PDF Image XObject — emits the raw RGB pixels
/// under <c>/Filter /FlateDecode</c> and, when the source had alpha, an SMask
/// XObject under <c>/SMask</c>. Spec basis: ISO 32000-2:2020 §8.9.5
/// (Image XObjects), §11.6 (Soft masks for transparency).
/// </summary>
internal static class RasterImageXObject
{
    /// <summary>Decode + wrap in one step — convenient for byte-stream inputs.</summary>
    public static ImageXObjectResult Build(byte[] imageBytes)
    {
        var info = RasterImageDecoder.Decode(imageBytes);
        return Build(info);
    }

    public static ImageXObjectResult Build(RasterImageInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        // Apply the same dimension/cap rules the streaming decode entry point enforces,
        // so callers that hand-construct a RasterImageInfo through this overload can't
        // produce malformed buffers that fail with late array-bounds exceptions.
        RasterImageDecoder.ValidateDecodedDimensions(info.Width, info.Height);
        var totalPixels = checked(info.Width * info.Height);
        var expectedPixelByteCount = checked(totalPixels * 4);
        if (info.PixelBytes.Length != expectedPixelByteCount)
        {
            throw new InvalidDataException(
                $"Raster: PixelBytes length {info.PixelBytes.Length} does not match width × height × 4 ({expectedPixelByteCount}).");
        }
        // Verify HasAlpha contract: when the caller declares the image opaque, every
        // alpha byte must actually be 0xFF. A hand-built input that says HasAlpha=false
        // but carries translucent pixel data would silently lose transparency in the
        // emitted PDF. Cheap check (one pass over alpha bytes) gives a clean reject.
        if (!info.HasAlpha)
        {
            for (var i = 3; i < info.PixelBytes.Length; i += 4)
            {
                if (info.PixelBytes[i] != 0xFF)
                {
                    throw new InvalidDataException(
                        $"Raster: RasterImageInfo has HasAlpha=false but pixel buffer contains alpha={info.PixelBytes[i]} at byte offset {i}. Set HasAlpha=true so the SMask plane is emitted, or zero-fill non-opaque pixels before passing the buffer.");
                }
            }
        }
        var rgb = new byte[totalPixels * 3];
        byte[]? alpha = info.HasAlpha ? new byte[totalPixels] : null;

        // RGBA8888 unpremultiplied → split into RGB color plane + (optional) alpha plane.
        for (var i = 0; i < totalPixels; i++)
        {
            var src = i * 4;
            rgb[i * 3 + 0] = info.PixelBytes[src + 0];
            rgb[i * 3 + 1] = info.PixelBytes[src + 1];
            rgb[i * 3 + 2] = info.PixelBytes[src + 2];
            if (alpha is not null) alpha[i] = info.PixelBytes[src + 3];
        }

        var rgbCompressed = ZlibCompress(rgb);
        var image = BuildSimpleImageStream(info.Width, info.Height, 8, PdfNames.DeviceRGB, rgbCompressed);

        if (alpha is null)
        {
            return new() { Image = image };
        }
        var alphaCompressed = ZlibCompress(alpha);
        var smask = BuildSimpleImageStream(info.Width, info.Height, 8, PdfNames.DeviceGray, alphaCompressed);
        // /SMask wiring is intentionally NOT set here: ISO 32000-2 §11.6 + §7.3.8 require
        // the value of /SMask to reference an indirect Image XObject. The high-level
        // PdfDocument.RegisterImage(ImageXObjectResult) path allocates indirect slots for
        // both streams and writes /SMask as an indirect ref.
        return new() { Image = image, SMask = smask };
    }

    private static PdfStream BuildSimpleImageStream(int width, int height, int bitsPerComponent, PdfName colorSpace, byte[] compressedBytes)
    {
        var dict = new PdfDictionary();
        dict.Set(PdfNames.Type, PdfNames.XObject);
        dict.Set(PdfNames.Subtype, PdfNames.Image);
        dict.Set(PdfNames.Width, new PdfInteger(width));
        dict.Set(PdfNames.Height, new PdfInteger(height));
        dict.Set(PdfNames.BitsPerComponent, new PdfInteger(bitsPerComponent));
        dict.Set(PdfNames.ColorSpace, colorSpace);
        dict.Set(PdfNames.Filter, PdfNames.FlateDecode);
        return new PdfStream(compressedBytes, dict);
    }

    private static byte[] ZlibCompress(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }
        return output.ToArray();
    }
}
