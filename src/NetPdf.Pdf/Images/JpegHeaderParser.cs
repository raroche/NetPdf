// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;

namespace NetPdf.Pdf.Images;

/// <summary>
/// Strict JPEG/JFIF parser for PDF passthrough embedding. Walks the marker stream from
/// SOI through to EOI, extracting only the metadata required to wrap a JPEG byte stream
/// in a PDF Image XObject (<see cref="JpegImageInfo"/>).
/// </summary>
/// <remarks>
/// <para>
/// Spec basis: ITU-T T.81 / ISO/IEC 10918-1 §B.1 (Compressed data formats) for the
/// marker-segment structure, with PDF-side encoding rules from ISO 32000-2:2020 §7.4.8
/// (DCTDecode filter — "decodes data that has been encoded in the Baseline JPEG format").
/// Clean-room implementation from the spec text alone; no third-party JPEG-decoder
/// source consulted.
/// </para>
/// <para>
/// <b>Trust-boundary contract.</b> The parser rejects any JPEG that is not provably
/// valid for PDF passthrough embedding. The full file structure must be present:
/// SOI → ... → SOFn → ... → SOS → entropy-coded data → EOI. Header-only files,
/// truncated streams, or streams that don't reach EOI are rejected via
/// <see cref="InvalidDataException"/>. This produces stricter behavior than typical
/// JPEG decoders (which often render partial files) — the trade-off is intentional: a
/// PDF emitted with a header-only JPEG would surface as a broken page in every reader.
/// </para>
/// <para>
/// <b>Supported SOFn process envelope.</b> Only <c>SOF0</c> (baseline DCT, marker
/// <c>0xFFC0</c>) and <c>SOF2</c> (progressive DCT, marker <c>0xFFC2</c>) are accepted.
/// PDF DCTDecode is specified for "Baseline JPEG"; SOF2 is universally supported by
/// production PDF readers (Acrobat / Preview / Chrome / Firefox PDF.js / Edge) so we
/// allow it. SOF1 (extended sequential), SOF3 (lossless), SOF9..SOF11 (arithmetic
/// coding) are rejected — Acrobat itself does not render these reliably and they are
/// not part of the documented DCTDecode interoperability surface.
/// </para>
/// <para>
/// <b>Sample precision.</b> Only 8-bit precision is accepted. Although the JPEG spec
/// defines a 12-bit baseline mode, no production PDF reader renders 12-bit DCTDecode
/// reliably and Acrobat does not enable it by default. Rejection is intentional.
/// </para>
/// <para>
/// <b>Adobe APP14 detection.</b> Photoshop saves CMYK JPEGs with the channel values
/// INVERTED. The Adobe APP14 marker (<c>0xFFEE</c>) carries a <c>ColorTransform</c>
/// byte: 0 = unknown (uninterpreted CMYK, treated as inverted), 1 = YCbCr, 2 = YCCK.
/// APP14 may appear anywhere before SOS — this parser keeps reading APP14 segments
/// even after the SOFn frame header so a "delayed" APP14 that appears between SOFn
/// and SOS is still picked up (some encoders emit it there).
/// </para>
/// <para>
/// <b>ICC profiles.</b> JPEG APP2 markers may carry an embedded ICC profile chunked
/// across one or more <c>"ICC_PROFILE\0"</c>-tagged segments. The parser surfaces
/// presence via <see cref="JpegImageInfo.HasIccProfile"/>; full ICCBased color-space
/// emission is post-v1. v1 emits the device color space (DeviceRGB/Gray/CMYK) for
/// every JPEG; profiled images render with default rendering intent until ICCBased
/// support lands.
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
        var hasIccProfile = false;
        JpegImageInfo? sofnInfo = null;
        var sawSos = false;

        while (cursor < jpegBytes.Length)
        {
            // Each segment starts with 0xFF then a marker byte. Multiple 0xFF padding bytes
            // are allowed per spec — skip them.
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
            if (marker == 0xD9)
            {
                // EOI reached. Must have already seen SOFn + SOS.
                if (sofnInfo is null)
                {
                    throw new InvalidDataException("JPEG: reached EOI before encountering an SOFn frame header.");
                }
                if (!sawSos)
                {
                    throw new InvalidDataException("JPEG: reached EOI before encountering an SOS scan header.");
                }
                return sofnInfo with { HasIccProfile = hasIccProfile, IsAdobeInvertedCmyk = ResolveAdobeInversion(sofnInfo.ComponentCount, adobeColorTransform) };
            }
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }

            // SOS (0xDA) starts the entropy-coded segment. After SOS, the parser stops
            // reading marker segments by length — it instead skips entropy-coded bytes
            // (with byte-stuffing rules) until the next non-RST 0xFF marker, which must
            // be the EOI.
            if (marker == 0xDA)
            {
                if (sofnInfo is null)
                {
                    throw new InvalidDataException("JPEG: SOS encountered before SOFn frame header.");
                }
                // Skip the SOS segment header (length-prefixed).
                if (cursor + 2 > jpegBytes.Length)
                {
                    throw new InvalidDataException("JPEG: truncated SOS segment length.");
                }
                var sosLen = BinaryPrimitives.ReadUInt16BigEndian(jpegBytes[cursor..(cursor + 2)]);
                if (sosLen < 2 || cursor + sosLen > jpegBytes.Length)
                {
                    throw new InvalidDataException(
                        $"JPEG: SOS segment length {sosLen} extends past end of stream.");
                }
                cursor += sosLen;
                sawSos = true;

                // Walk entropy-coded bytes: any 0xFF encountered must be either followed by
                // 0x00 (escape — stuffed byte, real value is 0xFF) OR by an RSTn marker
                // (0xD0..0xD7, restart) OR by EOI (0xD9). Any other follower terminates
                // the scan; the parser then re-enters the marker-segment loop.
                while (cursor < jpegBytes.Length)
                {
                    if (jpegBytes[cursor] != 0xFF)
                    {
                        cursor++;
                        continue;
                    }
                    // Skip any run of 0xFF padding.
                    var ffStart = cursor;
                    while (cursor < jpegBytes.Length && jpegBytes[cursor] == 0xFF) cursor++;
                    if (cursor >= jpegBytes.Length)
                    {
                        throw new InvalidDataException("JPEG: entropy-coded segment truncated after 0xFF run.");
                    }
                    var follower = jpegBytes[cursor];
                    if (follower == 0x00)
                    {
                        // Stuffed 0xFF — keep scanning.
                        cursor++;
                        continue;
                    }
                    if (follower >= 0xD0 && follower <= 0xD7)
                    {
                        // RSTn — restart marker, scan continues after it.
                        cursor++;
                        continue;
                    }
                    // Real marker: rewind to the FF run start so the outer loop can read it.
                    cursor = ffStart;
                    break;
                }
                continue;
            }

            // All other markers carry a 16-bit big-endian segment length.
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

            // SOFn frame header. Phase 1 accepts only SOF0 (baseline) and SOF2 (progressive)
            // — see class XML for the rationale. Other SOFn variants are explicitly rejected.
            if (marker == 0xC0 || marker == 0xC2)
            {
                if (sofnInfo is not null)
                {
                    throw new InvalidDataException("JPEG: multiple SOFn frame headers — file is malformed.");
                }
                sofnInfo = ParseSofnSegment(jpegBytes[cursor..segmentEnd]);
            }
            else if (marker is >= 0xC0 and <= 0xCF
                     && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                // SOF1 (0xC1), SOF3 (0xC3), SOF5/6/7 (0xC5/C6/C7), SOF9/10/11 (0xC9/CA/CB),
                // SOF13/14/15 (0xCD/CE/CF) — all in the SOFn range but not in the supported
                // PDF DCTDecode envelope. Reject with a specific message.
                throw new InvalidDataException(
                    $"JPEG: SOFn variant 0xFF{marker:X2} is not supported for PDF DCTDecode passthrough. " +
                    "Phase 1 accepts only SOF0 (baseline DCT, 0xFFC0) and SOF2 (progressive DCT, 0xFFC2).");
            }
            else if (marker == 0xEE)
            {
                // APP14 (Adobe). Per spec it may appear anywhere before SOS — accept the
                // last value seen (an encoder may emit a placeholder early then rewrite
                // the actual value later, though this is unusual).
                var ct = TryReadAdobeColorTransform(jpegBytes[cursor..segmentEnd]);
                if (ct is not null) adobeColorTransform = ct.Value;
            }
            else if (marker == 0xE2)
            {
                // APP2 — may carry an ICC profile.
                if (IsIccProfileApp2(jpegBytes[cursor..segmentEnd]))
                {
                    hasIccProfile = true;
                }
            }

            cursor = segmentEnd;
        }
        throw new InvalidDataException("JPEG: reached end of stream without encountering EOI.");
    }

    private static bool ResolveAdobeInversion(int componentCount, int adobeColorTransform)
        => componentCount == 4 && adobeColorTransform == 0;

    private static JpegImageInfo ParseSofnSegment(ReadOnlySpan<byte> segment)
    {
        // Segment layout (the 2-byte length is at offset 0..2 of 'segment'):
        //   [0..2]   length
        //   [2..3]   precision (UInt8)
        //   [3..5]   Y / Height (UInt16 BE)
        //   [5..7]   X / Width  (UInt16 BE)
        //   [7..8]   Nf — number of components (UInt8)
        //   [8..]    Nf × 3 bytes — per-component info
        // Required total: 8 + 3 × Nf. Validate.
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
        if (precision != 8)
        {
            throw new InvalidDataException(
                $"JPEG: SOFn precision {precision} is not supported for PDF DCTDecode passthrough. " +
                "Phase 1 accepts only 8-bit samples; 12-bit JPEGs are not interoperable across PDF readers.");
        }
        var expectedLength = 8 + (3 * nf);
        if (segment.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"JPEG: SOFn segment length {segment.Length} inconsistent with declared component count {nf} (expected {expectedLength}).");
        }

        return new JpegImageInfo
        {
            Width = width,
            Height = height,
            BitsPerComponent = precision,
            ComponentCount = nf,
            IsAdobeInvertedCmyk = false, // resolved by caller after the full marker walk
            HasIccProfile = false,        // same — caller fills in
        };
    }

    private static int? TryReadAdobeColorTransform(ReadOnlySpan<byte> segment)
    {
        // APP14 Adobe segment layout (after the 2-byte length already in 'segment'):
        //   [0..2]   length
        //   [2..7]   "Adobe"
        //   [7..9]   version
        //   [9..11]  flags0
        //   [11..13] flags1
        //   [13..14] transform
        // = 14 bytes total.
        if (segment.Length < 14) return null;
        if (segment[2] != (byte)'A' || segment[3] != (byte)'d' || segment[4] != (byte)'o'
            || segment[5] != (byte)'b' || segment[6] != (byte)'e')
        {
            return null;
        }
        return segment[13];
    }

    private static bool IsIccProfileApp2(ReadOnlySpan<byte> segment)
    {
        // APP2 ICC profile layout (after the 2-byte length):
        //   "ICC_PROFILE\0" — 12 bytes
        //   chunk number    — 1 byte
        //   chunk count     — 1 byte
        //   profile bytes   — chunked
        // We only need to detect presence; chunk reassembly + ICC-stream emission is post-v1.
        if (segment.Length < 14) return false;
        ReadOnlySpan<byte> tag = "ICC_PROFILE\0"u8;
        if (segment.Length < 2 + tag.Length) return false;
        for (var i = 0; i < tag.Length; i++)
        {
            if (segment[2 + i] != tag[i]) return false;
        }
        return true;
    }
}
