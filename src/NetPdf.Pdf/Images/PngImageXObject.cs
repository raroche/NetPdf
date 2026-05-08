// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.IO.Compression;
using NetPdf.Pdf.Objects;

namespace NetPdf.Pdf.Images;

/// <summary>
/// Builds PDF Image XObject(s) from a PNG. Three paths are taken:
/// </summary>
/// <list type="bullet">
///   <item><b>Opaque PNG (no tRNS) for color types 0 / 2 / 3:</b> the IDAT bytes are
///         zlib-compressed already and PDF's <c>FlateDecode</c> filter accepts zlib input
///         directly. Emits <c>/Filter /FlateDecode</c> with
///         <c>/DecodeParms &lt;&lt; /Predictor 15 /Columns W /Colors C
///         /BitsPerComponent BPC &gt;&gt;</c> so the PDF reader runs the same per-scanline
///         filter reversal a PNG decoder would. <b>True passthrough.</b></item>
///   <item><b>tRNS-flagged PNG (color types 0 / 2 with tRNS, color types 3 with binary
///         tRNS):</b> opaque-passthrough with an additional <c>/Mask</c> color-key array.
///         Color-key masking renders pixels exactly equal to the transparent value(s) as
///         transparent without requiring an SMask plane.</item>
///   <item><b>PNG with full alpha (color types 4 / 6, or color type 3 with non-binary
///         tRNS):</b> the IDAT must be decompressed, per-scanline filters reversed, the
///         pixels split into a color plane and an alpha plane, and each re-zlib-compressed
///         independently. Emits a primary Image XObject (color) plus an SMask XObject
///         referenced via <c>/SMask</c>. Phase 1 supports 8-bit alpha types only —
///         16-bit alpha PNGs are rejected.</item>
/// </list>
/// <remarks>
/// <para>
/// Spec basis: ISO 32000-2:2020 §8.9.5 (Image XObjects), §8.9.6.4 (Color key masks),
/// §11.6 (Soft masks for transparency), §7.4.4 + Table 8 (FlateDecode predictors).
/// PNG side: W3C PNG (Third Edition) §9 (Filtering) + §11 (Chunk specifications).
/// Clean-room.
/// </para>
/// </remarks>
internal static class PngImageXObject
{
    public static ImageXObjectResult Build(byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        // Per Phase C C-1 — pre-decode safety gate. Catches oversized inputs,
        // format-confusion (e.g., a non-PNG renamed to .png), and declared
        // dimensions past the per-image cap before PngHeaderParser walks
        // chunks of structurally-malformed bytes.
        var verdict = ImageSafetyValidator.Validate(pngBytes);
        if (!verdict.IsSafe)
        {
            throw new InvalidOperationException(
                $"PNG image rejected by pre-decode safety validator: {verdict.Reason}");
        }
        var info = PngHeaderParser.Parse(pngBytes);
        return Build(info);
    }

    public static ImageXObjectResult Build(PngImageInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.IsInterlaced)
        {
            throw new NotSupportedException(
                "PNG: Adam7 interlaced PNGs are not supported in Phase 1. Re-save the image without interlacing.");
        }

        if (info.HasAlpha)
        {
            return BuildAlphaSplit(info);
        }
        if (info.TransparencyChunk is not null)
        {
            // Indexed + non-binary tRNS requires the SMask path; gray/RGB + tRNS use /Mask.
            if (info.IsIndexed && !IsBinaryAlphaPalette(info.TransparencyChunk))
            {
                return BuildIndexedTrnsSMaskSplit(info);
            }
            return BuildOpaquePassthroughWithMask(info);
        }
        return BuildOpaquePassthrough(info);
    }

    // ───── Path 1: opaque passthrough ────────────────────────────────────────

    private static ImageXObjectResult BuildOpaquePassthrough(PngImageInfo info)
        => new() { Image = BuildOpaqueImageStream(info, mask: null) };

    // ───── Path 2: opaque passthrough + color-key /Mask ──────────────────────

    private static ImageXObjectResult BuildOpaquePassthroughWithMask(PngImageInfo info)
    {
        var mask = BuildColorKeyMask(info);
        return new() { Image = BuildOpaqueImageStream(info, mask) };
    }

    private static PdfStream BuildOpaqueImageStream(PngImageInfo info, PdfArray? mask)
    {
        var dict = new PdfDictionary();
        dict.Set(PdfNames.Type, PdfNames.XObject);
        dict.Set(PdfNames.Subtype, PdfNames.Image);
        dict.Set(PdfNames.Width, new PdfInteger(info.Width));
        dict.Set(PdfNames.Height, new PdfInteger(info.Height));
        dict.Set(PdfNames.BitsPerComponent, new PdfInteger(info.BitDepth));
        dict.Set(PdfNames.Filter, PdfNames.FlateDecode);

        if (info.IsIndexed)
        {
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

        var decodeParms = new PdfDictionary();
        decodeParms.Set(PdfNames.Predictor, new PdfInteger(15));
        decodeParms.Set(PdfNames.Columns, new PdfInteger(info.Width));
        decodeParms.Set(PdfNames.Colors, new PdfInteger(info.ColorComponents));
        decodeParms.Set(PdfNames.BitsPerComponent, new PdfInteger(info.BitDepth));
        dict.Set(PdfNames.DecodeParms, decodeParms);

        if (mask is not null)
        {
            dict.Set(PdfNames.Mask, mask);
        }

        return new PdfStream(info.CompressedIdatBytes.ToArray(), dict);
    }

    private static PdfArray BuildColorKeyMask(PngImageInfo info)
    {
        // PDF color-key /Mask array layout: [min1 max1 min2 max2 ...] where each pair
        // specifies one component's transparent range. PNG tRNS gives a single transparent
        // value per channel, so min == max for every component.
        var mask = new PdfArray();
        switch (info.ColorType)
        {
            case PngColorType.Grayscale:
                {
                    // 2 bytes BE — single 16-bit value. For 8-bit images the reader uses the
                    // low byte; we emit the value at the bit-depth's natural width.
                    var v = BinaryPrimitives.ReadUInt16BigEndian(info.TransparencyChunk!);
                    mask.Add(new PdfInteger(v));
                    mask.Add(new PdfInteger(v));
                    break;
                }
            case PngColorType.Rgb:
                {
                    // 6 bytes — three 16-bit BE values for R, G, B.
                    var r = BinaryPrimitives.ReadUInt16BigEndian(info.TransparencyChunk.AsSpan(0, 2));
                    var g = BinaryPrimitives.ReadUInt16BigEndian(info.TransparencyChunk.AsSpan(2, 2));
                    var b = BinaryPrimitives.ReadUInt16BigEndian(info.TransparencyChunk.AsSpan(4, 2));
                    mask.Add(new PdfInteger(r)); mask.Add(new PdfInteger(r));
                    mask.Add(new PdfInteger(g)); mask.Add(new PdfInteger(g));
                    mask.Add(new PdfInteger(b)); mask.Add(new PdfInteger(b));
                    break;
                }
            case PngColorType.Indexed:
                {
                    // Binary tRNS for indexed: emit a /Mask array covering each fully-
                    // transparent palette index. Indexed color-key /Mask uses 1 component
                    // (the palette index) so each min/max pair corresponds to a single
                    // transparent index. PDF readers AND-combine the ranges, but with a
                    // single component it's effectively a list of forbidden indices.
                    for (var i = 0; i < info.TransparencyChunk!.Length; i++)
                    {
                        if (info.TransparencyChunk[i] == 0) // fully transparent index
                        {
                            mask.Add(new PdfInteger(i));
                            mask.Add(new PdfInteger(i));
                        }
                    }
                    break;
                }
        }
        return mask;
    }

    private static bool IsBinaryAlphaPalette(byte[] trns)
    {
        // True when every entry is either 0 (transparent) or 255 (opaque).
        foreach (var a in trns)
        {
            if (a != 0 && a != 255) return false;
        }
        return true;
    }

    // ───── Path 3: alpha split into Image + SMask ────────────────────────────

    private static ImageXObjectResult BuildAlphaSplit(PngImageInfo info)
    {
        if (info.BitDepth != 8)
        {
            throw new NotSupportedException(
                $"PNG: {info.BitDepth}-bit alpha (color type {info.ColorType}) is not supported in Phase 1; only 8-bit RGBA / Gray+Alpha are handled.");
        }

        var expectedFilteredSize = checked((long)info.Height * (1L + info.ScanlineByteWidth));
        if (expectedFilteredSize > int.MaxValue)
        {
            throw new InvalidDataException(
                $"PNG: declared image is too large for in-memory decode ({expectedFilteredSize} bytes).");
        }
        var rawFiltered = ZlibDecompressBounded(info.CompressedIdatBytes.Span, (int)expectedFilteredSize);
        var rawPixels = PngFilterReverser.Reverse(
            rawFiltered, info.Height, info.ScanlineByteWidth, info.BytesPerPixelForFilter);

        var (colorPlane, alphaPlane) = SplitColorAndAlpha(rawPixels, info);
        var colorCompressed = ZlibCompress(colorPlane);
        var alphaCompressed = ZlibCompress(alphaPlane);

        var smask = BuildSimpleImageStream(
            info.Width, info.Height, 8, PdfNames.DeviceGray, alphaCompressed);
        var image = BuildSimpleImageStream(
            info.Width, info.Height, 8,
            info.ColorType == PngColorType.GrayscaleAlpha ? PdfNames.DeviceGray : PdfNames.DeviceRGB,
            colorCompressed);
        // /SMask wiring is intentionally NOT set here: ISO 32000-2 §11.6 + §7.3.8 require
        // the value of /SMask to reference an indirect Image XObject. The high-level
        // PdfDocument.RegisterImage(ImageXObjectResult) path allocates indirect slots for
        // both streams and writes /SMask as an indirect ref. Setting it inline would
        // either embed a duplicate direct stream (malformed PDF) or force every consumer
        // to hand-allocate slots.
        return new() { Image = image, SMask = smask };
    }

    // ───── Path 4: indexed + non-binary tRNS → SMask ────────────────────────

    private static ImageXObjectResult BuildIndexedTrnsSMaskSplit(PngImageInfo info)
    {
        // Indexed images with a tRNS that contains intermediate alpha values (1..254)
        // can't be expressed as a color-key /Mask. We must materialize a per-pixel alpha
        // plane by mapping each pixel index through the tRNS array.
        if (info.BitDepth != 8)
        {
            // Sub-byte indexed pixels need bit-level demuxing for the index → alpha
            // lookup. Phase 1 covers 8-bit indexed only.
            throw new NotSupportedException(
                $"PNG: {info.BitDepth}-bit indexed PNG with non-binary tRNS is not supported in Phase 1; re-save at 8-bit indexed or convert to RGBA.");
        }

        var expectedFilteredSize = checked((long)info.Height * (1L + info.ScanlineByteWidth));
        if (expectedFilteredSize > int.MaxValue)
        {
            throw new InvalidDataException(
                $"PNG: declared image is too large for in-memory decode ({expectedFilteredSize} bytes).");
        }
        var rawFiltered = ZlibDecompressBounded(info.CompressedIdatBytes.Span, (int)expectedFilteredSize);
        var rawPixels = PngFilterReverser.Reverse(
            rawFiltered, info.Height, info.ScanlineByteWidth, info.BytesPerPixelForFilter);

        var totalPixels = info.Width * info.Height;
        var alpha = new byte[totalPixels];
        var trns = info.TransparencyChunk!;
        for (var i = 0; i < totalPixels; i++)
        {
            var idx = rawPixels[i];
            alpha[i] = idx < trns.Length ? trns[idx] : (byte)255;
        }
        var alphaCompressed = ZlibCompress(alpha);
        var smask = BuildSimpleImageStream(info.Width, info.Height, 8, PdfNames.DeviceGray, alphaCompressed);

        // Color image stays as opaque-passthrough indexed; /SMask wiring is deferred to
        // PdfDocument.RegisterImage(ImageXObjectResult) (see BuildAlphaSplit notes).
        var image = BuildOpaqueImageStream(info, mask: null);
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

    private static (byte[] Color, byte[] Alpha) SplitColorAndAlpha(byte[] rawPixels, PngImageInfo info)
    {
        var totalPixels = info.Width * info.Height;
        var colorChannels = info.ColorComponents;
        var color = new byte[totalPixels * colorChannels];
        var alpha = new byte[totalPixels];
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
                srcPix[..colorChannels].CopyTo(dstColorPix);
                dstAlphaRow[x] = srcPix[colorChannels];
            }
        }
        return (color, alpha);
    }

    private static byte[] ZlibDecompressBounded(ReadOnlySpan<byte> compressed, int expectedSize)
    {
        // Bounded decompression — protects against zlib-bomb inputs by capping the
        // output buffer at expectedSize and probing for one extra byte. Any
        // decompressor exception is wrapped as InvalidDataException so the failure
        // contract matches the rest of the parser.
        try
        {
            using var input = new MemoryStream(compressed.ToArray(), writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: false);
            var buffer = new byte[expectedSize];
            var read = 0;
            while (read < expectedSize)
            {
                var n = zlib.Read(buffer, read, expectedSize - read);
                if (n == 0) break;
                read += n;
            }
            Span<byte> overflowProbe = stackalloc byte[1];
            if (zlib.Read(overflowProbe) != 0)
            {
                throw new InvalidDataException(
                    $"PNG: decompressed image data exceeds expected size ({expectedSize} bytes) — possible decompression-bomb input.");
            }
            if (read != expectedSize)
            {
                throw new InvalidDataException(
                    $"PNG: decompressed image data is short — got {read} bytes, expected {expectedSize}.");
            }
            return buffer;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"PNG: zlib decompression of IDAT failed: {ex.Message}", ex);
        }
    }

    private static byte[] ZlibCompress(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, PdfFormat.PdfDeflateCompressionLevel, leaveOpen: true))
        {
            zlib.Write(raw);
        }
        return output.ToArray();
    }
}
