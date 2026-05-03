// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;

namespace NetPdf.Pdf.Images;

/// <summary>
/// PNG chunk walker. Validates the 8-byte PNG signature, walks the chunk stream once,
/// extracts <c>IHDR</c> + <c>PLTE</c> + concatenated <c>IDAT</c> bytes into a
/// <see cref="PngImageInfo"/>, and rejects malformed or unsupported files.
/// </summary>
/// <remarks>
/// <para>
/// Spec basis: W3C "Portable Network Graphics (PNG) Specification (Third Edition)"
/// (<c>https://www.w3.org/TR/png-3/</c>) §5 (Datastream structure), §11 (Chunk
/// specifications). Clean-room implementation from spec text.
/// </para>
/// <para>
/// <b>Phase 1 scope.</b> Adam7 interlaced PNGs (IHDR interlace method 1) are rejected —
/// they require a separate decode path that runs 7 passes and merges the result. A
/// follow-up round adds interlacing support if real-world inputs need it.
/// </para>
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
        var ihdrType = ReadChunk(pngBytes, ref cursor, out var ihdrData);
        if (ihdrType != ChunkType.Ihdr)
        {
            throw new InvalidDataException(
                $"PNG: first chunk must be IHDR; got '{TagToString(ihdrType)}'.");
        }
        var ihdrInfo = ParseIhdr(ihdrData);

        byte[]? palette = null;
        using var idatStream = new MemoryStream();
        var sawIdat = false;
        var sawIend = false;

        while (cursor < pngBytes.Length)
        {
            var chunkType = ReadChunk(pngBytes, ref cursor, out var chunkData);
            switch (chunkType)
            {
                case ChunkType.Plte:
                    if (palette is not null)
                    {
                        throw new InvalidDataException("PNG: multiple PLTE chunks.");
                    }
                    if (chunkData.Length == 0 || chunkData.Length % 3 != 0 || chunkData.Length > 256 * 3)
                    {
                        throw new InvalidDataException(
                            $"PNG: PLTE length {chunkData.Length} is invalid (must be multiple of 3, ≤ 768).");
                    }
                    palette = chunkData.ToArray();
                    break;

                case ChunkType.Idat:
                    if (sawIend)
                    {
                        throw new InvalidDataException("PNG: IDAT chunk found after IEND.");
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
                    // Skip ancillary chunks (lowercase first letter per spec §5.4 chunk
                    // naming convention). Critical chunks (uppercase first letter) that
                    // we don't recognize are also skipped here for forward compatibility
                    // — the only critical chunks defined are IHDR, PLTE, IDAT, IEND, all
                    // handled above.
                    break;
            }
            if (sawIend) break;
        }

        if (!sawIend)
        {
            throw new InvalidDataException("PNG: missing IEND chunk.");
        }
        if (!sawIdat)
        {
            throw new InvalidDataException("PNG: no IDAT chunks found.");
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
            CompressedIdatBytes = idatStream.ToArray(),
        };
    }

    private static uint ReadChunk(ReadOnlySpan<byte> bytes, scoped ref int cursor, out ReadOnlySpan<byte> data)
    {
        // 4-byte length + 4-byte type + N bytes data + 4-byte CRC. We don't validate the
        // CRC — PDF embedding doesn't depend on it, and validation would slow happy path.
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
        var type = BinaryPrimitives.ReadUInt32BigEndian(bytes[(cursor + 4)..(cursor + 8)]);
        var dataStart = cursor + 8;
        var dataEnd = dataStart + (int)length;
        if (dataEnd + 4 > bytes.Length)
        {
            throw new InvalidDataException(
                $"PNG: chunk '{TagToString(type)}' (length {length}) extends past end of stream.");
        }
        data = bytes[dataStart..dataEnd];
        cursor = dataEnd + 4; // skip CRC
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
        // §11.2.2 Table 11.1: which bit depths each color type supports.
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
        public const uint Ihdr = 0x49484452u;
        public const uint Plte = 0x504C5445u;
        public const uint Idat = 0x49444154u;
        public const uint Iend = 0x49454E44u;
    }
}
