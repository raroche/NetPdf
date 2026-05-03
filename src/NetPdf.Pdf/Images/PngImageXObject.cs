// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.IO.Compression;
using NetPdf.Pdf.Objects;

namespace NetPdf.Pdf.Images;

/// <summary>
/// Builds PDF Image XObject(s) from a PNG. Two paths are taken:
/// </summary>
/// <list type="bullet">
///   <item><b>Opaque PNG (color types 0, 2, 3):</b> the IDAT bytes are zlib-compressed
///         already and PDF's <c>FlateDecode</c> filter accepts zlib input directly.
///         The wrapper emits <c>/Filter /FlateDecode</c> with
///         <c>/DecodeParms &lt;&lt; /Predictor 15 /Columns W /Colors C
///         /BitsPerComponent BPC &gt;&gt;</c> so the PDF reader runs the same
///         per-scanline filter reversal a PNG decoder would. <b>True passthrough.</b></item>
///   <item><b>PNG with alpha (color types 4 = GA, 6 = RGBA):</b> PDF cannot mix color
///         and alpha in the same XObject (DeviceRGB has no alpha channel). We must
///         decompress the IDAT, reverse the per-scanline PNG filters (via
///         <see cref="PngFilterReverser"/>), split the raw pixel buffer into a color
///         plane and an alpha plane, re-zlib-compress each, and emit two XObjects: the
///         primary Image XObject (color) and an SMask XObject (alpha) referenced via
///         <c>/SMask</c>. Phase 1 supports 8-bit alpha types only — 16-bit alpha PNGs
///         are rejected.</item>
/// </list>
/// <remarks>
/// <para>
/// Spec basis: ISO 32000-2:2020 §8.9.5 (Image XObjects), §11.6 (Soft masks for
/// transparency), §7.4.4 + Table 8 (FlateDecode predictors). PNG side: W3C PNG (Third
/// Edition) §9 (Filtering) + §11 (Chunk specifications). Clean-room.
/// </para>
/// </remarks>
internal sealed class PngImageXObjectResult
{
    public required PdfStream Image { get; init; }
    public PdfStream? SMask { get; init; }
}

internal static class PngImageXObject
{
    /// <summary>
    /// Build a PDF Image XObject (and optional SMask) from raw PNG bytes.
    /// </summary>
    public static PngImageXObjectResult Build(byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        var info = PngHeaderParser.Parse(pngBytes);
        return Build(info);
    }

    public static PngImageXObjectResult Build(PngImageInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.IsInterlaced)
        {
            throw new NotSupportedException(
                "PNG: Adam7 interlaced PNGs are not supported in Phase 1. Re-save the image without interlacing.");
        }

        return info.HasAlpha ? BuildAlphaSplit(info) : BuildOpaquePassthrough(info);
    }

    // ───── Path 1: opaque passthrough ────────────────────────────────────────

    private static PngImageXObjectResult BuildOpaquePassthrough(PngImageInfo info)
    {
        var dict = new PdfDictionary();
        dict.Set(PdfNames.Type, PdfNames.XObject);
        dict.Set(PdfNames.Subtype, PdfNames.Image);
        dict.Set(PdfNames.Width, new PdfInteger(info.Width));
        dict.Set(PdfNames.Height, new PdfInteger(info.Height));
        dict.Set(PdfNames.BitsPerComponent, new PdfInteger(info.BitDepth));
        dict.Set(PdfNames.Filter, PdfNames.FlateDecode);

        // ColorSpace.
        if (info.IsIndexed)
        {
            // [/Indexed /DeviceRGB N <palette-bytes>]
            //   N = number of palette entries - 1 ("hival")
            //   palette bytes = R G B R G B ... (3 × number of entries)
            var indexed = new PdfArray();
            indexed.Add(PdfNames.Indexed);
            indexed.Add(PdfNames.DeviceRGB);
            var paletteEntries = info.Palette!.Length / 3;
            indexed.Add(new PdfInteger(paletteEntries - 1));
            indexed.Add(new PdfHexString(info.Palette!));
            dict.Set(PdfNames.ColorSpace, indexed);
        }
        else
        {
            dict.Set(PdfNames.ColorSpace, info.ColorType == PngColorType.Grayscale
                ? PdfNames.DeviceGray
                : PdfNames.DeviceRGB);
        }

        // PNG predictor (FlateDecode parameter): predictor 15 = "PNG optimum", which
        // tells the reader the stream is per-scanline-filtered with one filter prefix
        // byte per scanline (the actual reverse-filter algorithm runs reader-side).
        var decodeParms = new PdfDictionary();
        decodeParms.Set(PdfNames.Predictor, new PdfInteger(15));
        decodeParms.Set(PdfNames.Columns, new PdfInteger(info.Width));
        decodeParms.Set(PdfNames.Colors, new PdfInteger(info.ColorComponents));
        decodeParms.Set(PdfNames.BitsPerComponent, new PdfInteger(info.BitDepth));
        dict.Set(PdfNames.DecodeParms, decodeParms);

        var idatArray = info.CompressedIdatBytes.ToArray();
        var image = new PdfStream(idatArray, dict);
        return new PngImageXObjectResult { Image = image };
    }

    // ───── Path 2: alpha split into Image + SMask ────────────────────────────

    private static PngImageXObjectResult BuildAlphaSplit(PngImageInfo info)
    {
        // Phase 1 limitation: 8-bit alpha types only.
        if (info.BitDepth != 8)
        {
            throw new NotSupportedException(
                $"PNG: {info.BitDepth}-bit alpha (color type {info.ColorType}) is not supported in Phase 1; only 8-bit RGBA / Gray+Alpha are handled.");
        }

        // Decompress the IDAT zlib stream.
        var rawFiltered = ZlibDecompress(info.CompressedIdatBytes.Span);

        // Reverse per-scanline filters → raw pixel data.
        var rawPixels = PngFilterReverser.Reverse(
            rawFiltered,
            info.Height,
            info.ScanlineByteWidth,
            info.BytesPerPixelForFilter);

        // Split into color + alpha planes.
        var (colorPlane, alphaPlane) = SplitColorAndAlpha(rawPixels, info);

        // Re-compress each plane with zlib (PDF FlateDecode = zlib).
        var colorCompressed = ZlibCompress(colorPlane);
        var alphaCompressed = ZlibCompress(alphaPlane);

        // Build the SMask first so we can reference its in-process Pdf object from /SMask.
        var smask = BuildSimpleImageStream(
            width: info.Width,
            height: info.Height,
            bitsPerComponent: 8,
            colorSpace: PdfNames.DeviceGray,
            compressedBytes: alphaCompressed);

        var image = BuildSimpleImageStream(
            width: info.Width,
            height: info.Height,
            bitsPerComponent: 8,
            colorSpace: info.ColorType == PngColorType.GrayscaleAlpha
                ? PdfNames.DeviceGray
                : PdfNames.DeviceRGB,
            compressedBytes: colorCompressed);

        // Wire /SMask reference via direct dictionary embedding. The serializer recursively
        // emits the SMask stream as a child indirect object when the Image XObject is
        // written; for now we embed it directly so the decoder result is structurally
        // complete. The high-level builder chooses indirect vs inline based on document
        // policy.
        image.Dictionary.Set(PdfNames.SMask, smask);

        return new PngImageXObjectResult { Image = image, SMask = smask };
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

    private static (byte[] Color, byte[] Alpha) SplitColorAndAlpha(byte[] rawPixels, PngImageInfo info)
    {
        // For 8-bit alpha types: GA = (G, A) per pixel; RGBA = (R, G, B, A) per pixel.
        var totalPixels = info.Width * info.Height;
        var colorChannels = info.ColorComponents; // 1 for GA, 3 for RGBA
        var color = new byte[totalPixels * colorChannels];
        var alpha = new byte[totalPixels];

        // Source layout: rawPixels has stride = width × channels (color + alpha) per row.
        var srcStride = info.Width * info.Channels;
        var dstColorStride = info.Width * colorChannels;

        for (var y = 0; y < info.Height; y++)
        {
            var srcRow = rawPixels.AsSpan(y * srcStride, srcStride);
            var dstColorRow = color.AsSpan(y * dstColorStride, dstColorStride);
            var dstAlphaRow = alpha.AsSpan(y * info.Width, info.Width);

            for (var x = 0; x < info.Width; x++)
            {
                var srcPix = srcRow.Slice(x * info.Channels, info.Channels);
                var dstColorPix = dstColorRow.Slice(x * colorChannels, colorChannels);
                // First N bytes are color, last byte is alpha.
                srcPix[..colorChannels].CopyTo(dstColorPix);
                dstAlphaRow[x] = srcPix[colorChannels];
            }
        }
        return (color, alpha);
    }

    private static byte[] ZlibDecompress(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
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
