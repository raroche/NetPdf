// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;

namespace NetPdf.Pdf.Images;

/// <summary>
/// Minimal JPEG/JFIF header parser — extracts only the metadata required to wrap a JPEG
/// byte stream in a PDF Image XObject (<see cref="JpegImageInfo"/>). The pixel data is
/// passed through unchanged via the PDF <c>DCTDecode</c> filter, so this parser never
/// touches Huffman tables, quantization tables, or scan data.
/// </summary>
/// <remarks>
/// <para>
/// Spec basis: ITU-T T.81 / ISO/IEC 10918-1 §B.1 (Compressed data formats) for the
/// marker-segment structure, with PDF-side encoding rules from ISO 32000-2:2020 §8.9.5
/// (DCTDecode filter). Clean-room implementation from spec; no third-party JPEG-decoder
/// source consulted.
/// </para>
/// <para>
/// <b>Walked markers.</b> The parser consumes SOI (Start Of Image), then walks each
/// marker segment until it finds an SOF<i>n</i> (Start Of Frame) carrying the frame
/// header — that's where Width / Height / BitsPerComponent / ComponentCount live. SOFn
/// codes 0xC0..0xCF are recognized except 0xC4 (DHT), 0xC8 (JPG reserved), and 0xCC (DAC).
/// </para>
/// <para>
/// <b>Adobe APP14 detection.</b> Photoshop saves CMYK JPEGs with the channel values
/// INVERTED (Y = 255 − cmyk). The Adobe APP14 marker (<c>0xFFEE</c>) carries a
/// <c>ColorTransform</c> byte: 0 = unknown (likely uninterpreted CMYK), 1 = YCbCr,
/// 2 = YCCK. A 4-component JPEG with ColorTransform = 0 is treated as inverted-CMYK so
/// the PDF embedder can emit <c>/Decode [1 0 1 0 1 0 1 0]</c> to compensate. This is the
/// standard convention every PDF library follows for Photoshop-saved CMYK JPEGs.
/// </para>
/// </remarks>
internal static class JpegHeaderParser
{
    public static JpegImageInfo Parse(ReadOnlySpan<byte> jpegBytes)
    {
        if (jpegBytes.Length < 4)
        {
            throw new InvalidDataException(
                $"JPEG: too short to contain even SOI + a marker ({jpegBytes.Length} bytes).");
        }
        // SOI = 0xFFD8.
        if (jpegBytes[0] != 0xFF || jpegBytes[1] != 0xD8)
        {
            throw new InvalidDataException(
                $"JPEG: missing SOI marker (expected 0xFFD8, got 0x{jpegBytes[0]:X2}{jpegBytes[1]:X2}).");
        }

        var cursor = 2;
        var adobeColorTransform = -1; // -1 = no APP14 marker seen
        while (cursor < jpegBytes.Length)
        {
            // Each segment starts with 0xFF then a marker byte. Multiple 0xFF padding
            // bytes are allowed per spec — skip them.
            if (jpegBytes[cursor] != 0xFF)
            {
                throw new InvalidDataException(
                    $"JPEG: expected marker prefix 0xFF at offset {cursor}, got 0x{jpegBytes[cursor]:X2}.");
            }
            while (cursor < jpegBytes.Length && jpegBytes[cursor] == 0xFF)
            {
                cursor++;
            }
            if (cursor >= jpegBytes.Length)
            {
                throw new InvalidDataException("JPEG: truncated after 0xFF marker prefix.");
            }
            var marker = jpegBytes[cursor++];

            // Markers without payload: SOI (0xD8), EOI (0xD9), TEM (0x01), RSTn (0xD0..0xD7).
            if (marker == 0xD8 || marker == 0xD9 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                if (marker == 0xD9)
                {
                    throw new InvalidDataException("JPEG: reached EOI before encountering an SOFn frame header.");
                }
                continue;
            }

            // All other markers carry a 16-bit big-endian segment length (including the 2
            // length bytes themselves but excluding the marker code).
            if (cursor + 2 > jpegBytes.Length)
            {
                throw new InvalidDataException(
                    $"JPEG: truncated segment length after marker 0xFF{marker:X2} at offset {cursor - 2}.");
            }
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(jpegBytes[cursor..(cursor + 2)]);
            if (segmentLength < 2)
            {
                throw new InvalidDataException(
                    $"JPEG: invalid segment length {segmentLength} for marker 0xFF{marker:X2} (must be ≥ 2).");
            }
            var segmentEnd = cursor + segmentLength;
            if (segmentEnd > jpegBytes.Length)
            {
                throw new InvalidDataException(
                    $"JPEG: segment for marker 0xFF{marker:X2} (length {segmentLength}) extends past end of stream.");
            }

            // SOFn frame header markers: 0xC0..0xCF, excluding 0xC4 (DHT), 0xC8 (JPG reserved), 0xCC (DAC).
            if (marker >= 0xC0 && marker <= 0xCF
                && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                return ParseSofnSegment(jpegBytes[cursor..segmentEnd], adobeColorTransform);
            }

            // APP14 (Adobe): tag = "Adobe" + version + flags + ColorTransform.
            if (marker == 0xEE)
            {
                adobeColorTransform = TryReadAdobeColorTransform(jpegBytes[cursor..segmentEnd]) ?? adobeColorTransform;
            }

            cursor = segmentEnd;
        }
        throw new InvalidDataException("JPEG: reached end of stream before encountering an SOFn frame header.");
    }

    private static JpegImageInfo ParseSofnSegment(ReadOnlySpan<byte> segment, int adobeColorTransform)
    {
        // Segment layout (after the 2-byte length, which is included in 'segment'):
        //   length      uint16  (already consumed conceptually; segment starts at the length bytes)
        //   precision   uint8   (BitsPerComponent)
        //   Y           uint16  (Height)
        //   X           uint16  (Width)
        //   Nf          uint8   (ComponentCount)
        // We need at least 2 (length) + 1 + 2 + 2 + 1 = 8 bytes.
        if (segment.Length < 8)
        {
            throw new InvalidDataException(
                $"JPEG: SOFn segment too short ({segment.Length} bytes; need ≥ 8).");
        }
        var precision = segment[2];
        var height = BinaryPrimitives.ReadUInt16BigEndian(segment[3..5]);
        var width = BinaryPrimitives.ReadUInt16BigEndian(segment[5..7]);
        var nf = segment[7];

        if (width == 0 || height == 0)
        {
            throw new InvalidDataException(
                $"JPEG: SOFn declares zero dimension (width={width}, height={height}).");
        }
        if (nf is not (1 or 3 or 4))
        {
            throw new InvalidDataException(
                $"JPEG: SOFn component count {nf} is not supported for PDF embedding (expected 1, 3, or 4).");
        }
        if (precision is not (8 or 12))
        {
            // PDF DCTDecode supports 8 and 12 bits per component; other values are rare
            // and outside the PDF embedding contract.
            throw new InvalidDataException(
                $"JPEG: SOFn precision {precision} not supported for PDF embedding (expected 8 or 12).");
        }

        return new JpegImageInfo
        {
            Width = width,
            Height = height,
            BitsPerComponent = precision,
            ComponentCount = nf,
            IsAdobeInvertedCmyk = nf == 4 && adobeColorTransform == 0,
        };
    }

    private static int? TryReadAdobeColorTransform(ReadOnlySpan<byte> segment)
    {
        // APP14 Adobe segment layout (after the 2-byte length already in 'segment'):
        //   length     uint16
        //   "Adobe"    5 bytes ASCII
        //   version    uint16
        //   flags0     uint16
        //   flags1     uint16
        //   transform  uint8
        // = 14 bytes total.
        if (segment.Length < 14) return null;
        if (segment[2] != (byte)'A' || segment[3] != (byte)'d' || segment[4] != (byte)'o'
            || segment[5] != (byte)'b' || segment[6] != (byte)'e')
        {
            return null;
        }
        return segment[13];
    }
}
