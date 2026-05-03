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
        var totalPixels = info.Width * info.Height;
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
        image.Dictionary.Set(PdfNames.SMask, smask);
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
