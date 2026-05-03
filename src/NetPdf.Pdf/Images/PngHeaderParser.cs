// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;

namespace NetPdf.Pdf.Images;

/// <summary>
/// PNG chunk walker. Validates the 8-byte PNG signature, walks the chunk stream once,
/// extracts <c>IHDR</c> + <c>PLTE</c> + concatenated <c>IDAT</c> + <c>tRNS</c> bytes
/// into a <see cref="PngImageInfo"/>, and rejects malformed or unsupported files.
/// </summary>
/// <remarks>
/// <para>
/// Spec basis: W3C "Portable Network Graphics (PNG) Specification (Third Edition)"
/// (<c>https://www.w3.org/TR/png-3/</c>) §5 (Datastream structure), §11 (Chunk
/// specifications), §13.2.3 (Chunk ordering rules). Clean-room implementation.
/// </para>
/// <para>
/// <b>Trust-boundary contract.</b>
/// </para>
/// <list type="bullet">
///   <item>The 8-byte signature must match exactly.</item>
///   <item>The first chunk must be IHDR; the last chunk must be IEND. Any bytes after
///         IEND are rejected.</item>
///   <item>Every chunk's CRC-32 (per §5.5, IEEE 802.3 polynomial) is verified.</item>
///   <item>IDAT chunks must appear consecutively (§13.2.3) — no other chunks may
///         interleave once the IDAT sequence begins.</item>
///   <item>PLTE must appear before the first IDAT, and is required for
///         <see cref="PngColorType.Indexed"/>. tRNS, if present, must appear after PLTE
///         and before the first IDAT.</item>
///   <item>For indexed images, palette entry count must not exceed
///         <c>2^bitDepth</c> (§11.2.3).</item>
///   <item>tRNS is rejected for color types 4 (GA) and 6 (RGBA) per §11.3.2.1.</item>
///   <item>Unknown <i>critical</i> chunks (uppercase first letter) are rejected per
///         §5.4 — only IHDR / PLTE / IDAT / IEND are defined as critical and allowed.
///         Unknown ancillary chunks (lowercase first letter) are skipped silently.</item>
///   <item>Adam7 interlaced PNGs (IHDR interlace method 1) are rejected as
///         out-of-Phase-1.</item>
/// </list>
/// </remarks>
internal static class PngHeaderParser
{
    /// <summary>The 8-byte PNG signature: <c>89 50 4E 47 0D 0A 1A 0A</c>.</summary>
    public static ReadOnlySpan<byte> Signature => SignatureBytes;
    private static readonly byte[] SignatureBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static PngImageInfo Parse(ReadOnlySpan<byte> pngBytes)
    {
        if (pngBytes.Length < Signature.Length)
        {
            throw new InvalidDataException(
                $"PNG: too short to contain the 8-byte signature ({pngBytes.Length} bytes).");
        }
        if (!pngBytes[..Signature.Length].SequenceEqual(Signature))
        {
            throw new InvalidDataException("PNG: missing 8-byte signature.");
        }

        var cursor = Signature.Length;

        // First chunk MUST be IHDR per §5.6.
        var ihdrType = ReadAndValidateChunk(pngBytes, ref cursor, out var ihdrData);
        if (ihdrType != ChunkType.Ihdr)
        {
            throw new InvalidDataException(
                $"PNG: first chunk must be IHDR; got '{TagToString(ihdrType)}'.");
        }
        var ihdrInfo = ParseIhdr(ihdrData);

        byte[]? palette = null;
        byte[]? transparency = null;
        using var idatStream = new MemoryStream();
        var sawIdat = false;
        var idatSequenceClosed = false; // true after we leave the consecutive IDAT run
        var sawIend = false;
        var sawPlte = false;

        while (cursor < pngBytes.Length && !sawIend)
        {
            var chunkType = ReadAndValidateChunk(pngBytes, ref cursor, out var chunkData);
            switch (chunkType)
            {
                case ChunkType.Ihdr:
                    throw new InvalidDataException("PNG: duplicate IHDR chunk.");

                case ChunkType.Plte:
                    if (sawPlte)
                    {
                        throw new InvalidDataException("PNG: multiple PLTE chunks.");
                    }
                    if (sawIdat)
                    {
                        throw new InvalidDataException("PNG: PLTE must appear before IDAT.");
                    }
                    if (chunkData.Length == 0 || chunkData.Length % 3 != 0 || chunkData.Length > 256 * 3)
                    {
                        throw new InvalidDataException(
                            $"PNG: PLTE length {chunkData.Length} is invalid (must be multiple of 3, ≤ 768).");
                    }
                    if (ihdrInfo.ColorType == PngColorType.Grayscale
                        || ihdrInfo.ColorType == PngColorType.GrayscaleAlpha)
                    {
                        // Spec §11.2.3 forbids PLTE for grayscale variants.
                        throw new InvalidDataException(
                            $"PNG: PLTE chunk is not allowed for color type {ihdrInfo.ColorType}.");
                    }
                    if (ihdrInfo.ColorType == PngColorType.Indexed)
                    {
                        var entries = chunkData.Length / 3;
                        var maxEntries = 1 << ihdrInfo.BitDepth;
                        if (entries > maxEntries)
                        {
                            throw new InvalidDataException(
                                $"PNG: PLTE has {entries} entries which exceeds 2^bitDepth = {maxEntries} for {ihdrInfo.BitDepth}-bit indexed.");
                        }
                    }
                    palette = chunkData.ToArray();
                    sawPlte = true;
                    break;

                case ChunkType.Trns:
                    if (sawIdat)
                    {
                        throw new InvalidDataException("PNG: tRNS must appear before IDAT.");
                    }
                    if (transparency is not null)
                    {
                        throw new InvalidDataException("PNG: multiple tRNS chunks.");
                    }
                    ValidateTransparencyForColorType(chunkData.Length, ihdrInfo, palette);
                    transparency = chunkData.ToArray();
                    break;

                case ChunkType.Idat:
                    if (idatSequenceClosed)
                    {
                        // §13.2.3: IDAT chunks must be consecutive. Any non-IDAT chunk in
                        // between closes the sequence and a subsequent IDAT is malformed.
                        throw new InvalidDataException("PNG: IDAT chunks must be consecutive.");
                    }
                    idatStream.Write(chunkData);
                    sawIdat = true;
                    break;

                case ChunkType.Iend:
                    if (chunkData.Length != 0)
                    {
                        throw new InvalidDataException(
                            $"PNG: IEND must be a zero-length chunk; got {chunkData.Length} byte(s).");
                    }
                    sawIend = true;
                    break;

                default:
                    if (IsCriticalChunk(chunkType))
                    {
                        throw new InvalidDataException(
                            $"PNG: unknown critical chunk '{TagToString(chunkType)}' is not defined by the spec.");
                    }
                    // Ancillary chunks (lowercase first letter) are forward-compat: skip.
                    if (sawIdat) idatSequenceClosed = true;
                    break;
            }
            if (chunkType != ChunkType.Idat && sawIdat) idatSequenceClosed = true;
        }

        if (!sawIend)
        {
            throw new InvalidDataException("PNG: missing IEND chunk.");
        }
        if (!sawIdat)
        {
            throw new InvalidDataException("PNG: no IDAT chunks found.");
        }
        if (cursor != pngBytes.Length)
        {
            throw new InvalidDataException(
                $"PNG: file has {pngBytes.Length - cursor} byte(s) of trailing data after IEND.");
        }
        if (ihdrInfo.ColorType == PngColorType.Indexed && palette is null)
        {
            throw new InvalidDataException("PNG: indexed-color PNG missing required PLTE chunk.");
        }

        return new PngImageInfo
        {
            Width = ihdrInfo.Width,
            Height = ihdrInfo.Height,
            BitDepth = ihdrInfo.BitDepth,
            ColorType = ihdrInfo.ColorType,
            IsInterlaced = ihdrInfo.IsInterlaced,
            Palette = palette,
            TransparencyChunk = transparency,
            CompressedIdatBytes = idatStream.ToArray(),
        };
    }

    private static void ValidateTransparencyForColorType(int trnsLength, IhdrFields ihdrInfo, byte[]? palette)
    {
        switch (ihdrInfo.ColorType)
        {
            case PngColorType.Grayscale:
                if (trnsLength != 2)
                {
                    throw new InvalidDataException(
                        $"PNG: tRNS for grayscale must be 2 bytes; got {trnsLength}.");
                }
                break;
            case PngColorType.Rgb:
                if (trnsLength != 6)
                {
                    throw new InvalidDataException(
                        $"PNG: tRNS for RGB must be 6 bytes; got {trnsLength}.");
                }
                break;
            case PngColorType.Indexed:
                if (palette is null)
                {
                    throw new InvalidDataException(
                        "PNG: tRNS for indexed image must follow PLTE chunk.");
                }
                var paletteEntries = palette.Length / 3;
                if (trnsLength > paletteEntries)
                {
                    throw new InvalidDataException(
                        $"PNG: tRNS length {trnsLength} exceeds palette entry count {paletteEntries}.");
                }
                break;
            case PngColorType.GrayscaleAlpha:
            case PngColorType.Rgba:
                throw new InvalidDataException(
                    $"PNG: tRNS chunk is not allowed for color type {ihdrInfo.ColorType} (alpha already in image data).");
        }
    }

    private static bool IsCriticalChunk(uint chunkType)
    {
        // §5.4: chunk-naming convention. The 5th bit of the first byte (bit 5 of the
        // high byte of the 32-bit type) determines critical (0) vs ancillary (1).
        // Equivalently: bit-5 = 0 means uppercase ASCII first letter = critical.
        return ((chunkType >> 24) & 0x20) == 0;
    }

    private static uint ReadAndValidateChunk(ReadOnlySpan<byte> bytes, scoped ref int cursor, out ReadOnlySpan<byte> data)
    {
        if (cursor + 8 > bytes.Length)
        {
            throw new InvalidDataException(
                $"PNG: truncated chunk header at offset {cursor} (need 8 bytes).");
        }
        var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[cursor..(cursor + 4)]);
        if (length > 0x7FFFFFFFu)
        {
            throw new InvalidDataException(
                $"PNG: chunk length {length} exceeds spec maximum (2^31 - 1).");
        }
        var typeStart = cursor + 4;
        var typeBytes = bytes[typeStart..(typeStart + 4)];
        var type = BinaryPrimitives.ReadUInt32BigEndian(typeBytes);
        var dataStart = cursor + 8;
        var dataEnd = dataStart + (int)length;
        if (dataEnd + 4 > bytes.Length)
        {
            throw new InvalidDataException(
                $"PNG: chunk '{TagToString(type)}' (length {length}) extends past end of stream.");
        }
        data = bytes[dataStart..dataEnd];

        // Verify CRC-32 over type + data per §5.5.
        var declaredCrc = BinaryPrimitives.ReadUInt32BigEndian(bytes[dataEnd..(dataEnd + 4)]);
        var computedCrc = PngCrc32.Compute(typeBytes, data);
        if (declaredCrc != computedCrc)
        {
            throw new InvalidDataException(
                $"PNG: chunk '{TagToString(type)}' CRC mismatch (declared 0x{declaredCrc:X8}, computed 0x{computedCrc:X8}).");
        }

        cursor = dataEnd + 4;
        return type;
    }

    private static IhdrFields ParseIhdr(ReadOnlySpan<byte> ihdrData)
    {
        if (ihdrData.Length != 13)
        {
            throw new InvalidDataException(
                $"PNG: IHDR chunk must be exactly 13 bytes; got {ihdrData.Length}.");
        }
        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdrData[0..4]);
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdrData[4..8]);
        var bitDepth = ihdrData[8];
        var colorType = ihdrData[9];
        var compression = ihdrData[10];
        var filter = ihdrData[11];
        var interlace = ihdrData[12];

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"PNG: invalid IHDR dimensions ({width}×{height}).");
        }
        if (compression != 0)
        {
            throw new InvalidDataException(
                $"PNG: unsupported compression method {compression} (only 0 = deflate is defined).");
        }
        if (filter != 0)
        {
            throw new InvalidDataException(
                $"PNG: unsupported filter method {filter} (only 0 is defined).");
        }
        if (interlace is not (0 or 1))
        {
            throw new InvalidDataException(
                $"PNG: invalid interlace method {interlace} (must be 0 or 1).");
        }
        var typeEnum = colorType switch
        {
            0 => PngColorType.Grayscale,
            2 => PngColorType.Rgb,
            3 => PngColorType.Indexed,
            4 => PngColorType.GrayscaleAlpha,
            6 => PngColorType.Rgba,
            _ => throw new InvalidDataException($"PNG: invalid color type {colorType}."),
        };
        ValidateBitDepth(typeEnum, bitDepth);
        return new IhdrFields
        {
            Width = width,
            Height = height,
            BitDepth = bitDepth,
            ColorType = typeEnum,
            IsInterlaced = interlace == 1,
        };
    }

    private static void ValidateBitDepth(PngColorType type, int bitDepth)
    {
        // §11.2.2 Table 11.1.
        var ok = type switch
        {
            PngColorType.Grayscale => bitDepth is 1 or 2 or 4 or 8 or 16,
            PngColorType.Rgb => bitDepth is 8 or 16,
            PngColorType.Indexed => bitDepth is 1 or 2 or 4 or 8,
            PngColorType.GrayscaleAlpha => bitDepth is 8 or 16,
            PngColorType.Rgba => bitDepth is 8 or 16,
            _ => false,
        };
        if (!ok)
        {
            throw new InvalidDataException(
                $"PNG: bit depth {bitDepth} is not valid for color type {type}.");
        }
    }

    private static string TagToString(uint tag)
    {
        Span<char> buf = stackalloc char[4];
        buf[0] = (char)((tag >> 24) & 0xFF);
        buf[1] = (char)((tag >> 16) & 0xFF);
        buf[2] = (char)((tag >> 8) & 0xFF);
        buf[3] = (char)(tag & 0xFF);
        return new string(buf);
    }

    private readonly record struct IhdrFields
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int BitDepth { get; init; }
        public required PngColorType ColorType { get; init; }
        public required bool IsInterlaced { get; init; }
    }

    /// <summary>4-byte big-endian PNG chunk type values.</summary>
    internal static class ChunkType
    {
        public const uint Ihdr = 0x49484452u; // 'IHDR'
        public const uint Plte = 0x504C5445u; // 'PLTE'
        public const uint Idat = 0x49444154u; // 'IDAT'
        public const uint Iend = 0x49454E44u; // 'IEND'
        public const uint Trns = 0x74524E53u; // 'tRNS'
    }
}
