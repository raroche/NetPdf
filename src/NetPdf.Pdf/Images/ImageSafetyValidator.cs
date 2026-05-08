// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Pdf.Images;

/// <summary>
/// Per Phase C C-1 — pre-decode image safety gate. Every <see cref="JpegImageXObject"/>
/// + <see cref="PngImageXObject"/> + (future) <see cref="RasterImageXObject"/> entry
/// point routes incoming bytes through this validator before invoking the actual
/// header parser / Skia decoder. Defends against the libwebp CVE-2023-4863 class
/// (decoder bug triggered by malformed input) by enforcing format + size + dimension
/// caps that the spec doesn't bound:
///
/// <list type="number">
///   <item><description><b>Magic-byte sniffing.</b> The first 12 bytes must match a
///   recognized format signature (JPEG SOI <c>FF D8</c>; PNG <c>89 50 4E 47 0D 0A 1A 0A</c>;
///   GIF <c>GIF87a</c> / <c>GIF89a</c>; WebP <c>RIFF????WEBP</c>; BMP <c>BM</c>).
///   Anything else is rejected without entering decode paths — defense against
///   format-confusion attacks (<c>Content-Type: image/png</c> on a SVG payload, etc.).</description></item>
///   <item><description><b>Byte-size cap.</b> Encoded payload above
///   <see cref="MaxBytes"/> (32 MiB default) is rejected. The compressed size is
///   itself a useful upper bound on memory usage during decode.</description></item>
///   <item><description><b>Dimension caps.</b> After magic-byte sniff, peek the
///   header for declared width × height and reject above
///   <see cref="MaxDimension"/> (16 384 px) on either axis OR above
///   <see cref="MaxPixelArea"/> (≈268 megapixels = 16 384²) total. PNG IHDR + JPEG
///   SOFn frames carry these in the first ~100 bytes; the validator does NOT need
///   to walk the full file.</description></item>
/// </list>
///
/// <para><b>What this is NOT.</b> The validator does not detect malformed payloads
/// past the header — that's the decoder's job. It catches the size-of-bomb /
/// format-confusion / "1 x 1 declared, 100 GB encoded" classes. Decode-time bugs
/// in libwebp / libpng / libjpeg-turbo themselves are out of scope (they're fixed
/// upstream); the validator just bounds the attack surface.</para>
///
/// <para><b>Phase C contract (PR #17 follow-up).</b> Image entry points
/// (<c>JpegImageXObject.Build</c>, <c>PngImageXObject.Build</c>,
/// <c>RasterImageXObject.Build</c>) call <see cref="Validate"/> at the top +
/// throw <see cref="System.InvalidOperationException"/> on rejection,
/// containing the verdict reason. The Phase 5 resource-loader pipeline will
/// translate that exception into <c>RES-IMAGE-UNSAFE-001</c> through the
/// public diagnostics sink; today's code paths surface the failure as an
/// exception because there's no diagnostics-sink seam at the existing
/// callers (Phase 1 PDF-write paths). <see cref="MaxBytes"/> +
/// <see cref="MaxDimension"/> + <see cref="MaxPixelArea"/> are
/// <see langword="const"/> so future phases can override via a config
/// struct without breaking this contract.</para>
/// </summary>
public static class ImageSafetyValidator
{
    /// <summary>Per Phase C C-1 — maximum encoded image bytes accepted by the
    /// pre-decode gate. 32 MiB is generous for any sane invoice / report image
    /// (typical &lt; 500 KiB). Caps memory pressure during decode + bounds the
    /// pixel-area worst case (a 32 MiB JPEG can decode to ~50 megapixels).</summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    /// <summary>Maximum width / height (pixels) on either axis for the
    /// PASSTHROUGH path (JPEG / PNG go through directly to PDF as
    /// /DCTDecode / /FlateDecode streams; no full RGBA decode in process
    /// memory). 8 192 covers any realistic invoice / report input (an A4
    /// page at 600 DPI is 4 960 × 7 016).</summary>
    /// <remarks>Per PR #17 user-recommendation #2 + Phase D D-4 —
    /// per-path caps. Passthrough JPEG/PNG only briefly hold the
    /// encoded bytes; the image is wrapped + emitted to the PDF stream
    /// without a full RGBA decode buffer. 8 192² is fine for that
    /// path. The raster path has tighter caps below.</remarks>
    public const int MaxDimension = 8 * 1024;

    /// <summary>Maximum total decoded pixel area for the passthrough
    /// path. 8 192² ≈ 67 megapixels. JPEG/PNG passthrough never
    /// materializes a full RGBA buffer so this is the encoded-image
    /// bound only.</summary>
    public const long MaxPixelArea = (long)MaxDimension * MaxDimension;

    /// <summary>Per Phase D D-4 — maximum width / height (pixels) on
    /// either axis for the RASTER path (GIF / WebP /
    /// <see cref="RasterImageXObject"/> path). Raster decoding goes
    /// through Skia → RGBA8888 buffer → split into RGB plane + alpha
    /// plane + per-plane Flate-compressed buffers; peak memory is
    /// ~5× the encoded RGBA bytes. 4 096² caps the peak around
    /// 320 MiB worst case, comfortably below the 1 GiB band most
    /// process budgets allow.</summary>
    public const int MaxRasterDimension = 4 * 1024;

    /// <summary>Per Phase D D-4 — maximum decoded pixel area for the
    /// raster path. 4 096² ≈ 16.7 megapixels. Tight enough that even
    /// the worst-case RGBA + RGB-plane + alpha-plane + Flate-buffer
    /// combo stays under 320 MiB.</summary>
    public const long MaxRasterPixelArea = (long)MaxRasterDimension * MaxRasterDimension;

    /// <summary>Three-state verdict returned by <see cref="Validate"/>.
    /// Mirror of the validator pattern used by <c>NetPdf.UriSafetyValidator</c>
    /// (Phase B B-7) — a small enum keeps callers from confusing the boolean
    /// "safe" with the outcome metadata.</summary>
    public enum ImageSafetyVerdict
    {
        /// <summary>Bytes pass scheme + dimension caps; the decoder may proceed.</summary>
        Safe = 0,
        /// <summary>Bytes failed one of the caps; the decoder must reject + emit
        /// <c>RES-IMAGE-UNSAFE-001</c>.</summary>
        Unsafe = 1,
    }

    /// <summary>Result of a validation check.</summary>
    /// <param name="Verdict">Safe or Unsafe.</param>
    /// <param name="Reason">When unsafe, a human-readable reason suitable for
    /// inclusion in a diagnostic message. <see langword="null"/> when safe.</param>
    /// <param name="DetectedFormat">The format identified by magic-byte
    /// sniffing. <see cref="ImageFormat.Unknown"/> when sniffing failed
    /// (Unsafe verdict from too-short / unrecognized-magic). For
    /// post-magic Unsafe verdicts (oversized, dimensions over cap), the
    /// detected format IS reported so callers can produce specific
    /// diagnostics. Per PR #17 Copilot review #6.</param>
    public readonly record struct ValidationResult(
        ImageSafetyVerdict Verdict,
        string? Reason,
        ImageFormat DetectedFormat)
    {
        public bool IsSafe => Verdict == ImageSafetyVerdict.Safe;
    }

    /// <summary>Recognized image formats. The set is closed: anything else routes
    /// to <see cref="ImageFormat.Unknown"/> and the validator rejects it.</summary>
    public enum ImageFormat
    {
        Unknown = 0,
        Jpeg,
        Png,
        Gif,
        WebP,
        Bmp,
        /// <summary>AVIF — ISOBMFF-wrapped AV1. Per PR #17 review
        /// user-recommendation #1, AVIF input recognition lets the C-1 gate
        /// reject AVIF bytes explicitly (we don't decode them in v1) rather
        /// than letting them reach Skia / libavif as "Unknown".</summary>
        Avif,
    }

    /// <summary>Validate <paramref name="bytes"/> against the per-image safety
    /// caps. Runs in three passes:
    ///
    /// <list type="number">
    ///   <item><description>Byte-size check (cheap; bounds the rest of the work).</description></item>
    ///   <item><description>Magic-byte sniff (12-byte read; identifies the format
    ///   or rejects on unknown signature).</description></item>
    ///   <item><description>Dimension peek for the identified format (PNG IHDR,
    ///   JPEG SOFn, GIF logical-screen-descriptor, WebP VP8/VP8L/VP8X, BMP DIB
    ///   header). Reject when width/height exceed the cap or when their product
    ///   exceeds <see cref="MaxPixelArea"/>.</description></item>
    /// </list>
    ///
    /// Inexpensive on the success path: the entire validator does &lt; 100 byte
    /// reads on a typical input.</summary>
    public static ValidationResult Validate(ReadOnlySpan<byte> bytes)
    {
        // 1. Byte-size cap. Reject before any further work — an oversized
        // payload is malicious irrespective of format.
        if (bytes.Length > MaxBytes)
        {
            return new ValidationResult(
                ImageSafetyVerdict.Unsafe,
                $"image bytes ({bytes.Length}) exceed the {MaxBytes / (1024 * 1024)} MiB pre-decode cap",
                ImageFormat.Unknown);
        }

        // 2. Magic-byte sniff. Need at least 12 bytes for the deepest signature
        // (RIFF + WEBP for WebP).
        if (bytes.Length < 12)
        {
            return new ValidationResult(
                ImageSafetyVerdict.Unsafe,
                $"image bytes ({bytes.Length}) shorter than minimum magic-byte run (12)",
                ImageFormat.Unknown);
        }

        var format = SniffFormat(bytes);
        if (format == ImageFormat.Unknown)
        {
            return new ValidationResult(
                ImageSafetyVerdict.Unsafe,
                "image bytes did not match any recognized format signature (JPEG / PNG / GIF / WebP / BMP / AVIF)",
                ImageFormat.Unknown);
        }
        // Per PR #17 review user-recommendation #1 — AVIF is recognized so
        // SniffFormat can distinguish it from Unknown, but explicitly
        // rejected here. v1 does not decode AVIF (no libavif on the macOS
        // SkiaSharp build per project_dev_environment memory + the C-1
        // threat model considers AVIF in scope but support is post-v1).
        // Letting it through to Skia/libavif would defeat the gate.
        if (format == ImageFormat.Avif)
        {
            return new ValidationResult(
                ImageSafetyVerdict.Unsafe,
                "AVIF input rejected: NetPdf v1 does not decode AVIF (libavif unavailable on the SkiaSharp build)",
                ImageFormat.Avif);
        }

        // 3. Dimension peek. Format-specific; each branch only reads the
        // header bytes it needs.
        if (!TryPeekDimensions(bytes, format, out var width, out var height, out var dimReason))
        {
            return new ValidationResult(
                ImageSafetyVerdict.Unsafe,
                $"could not parse {format} header dimensions: {dimReason}",
                format);
        }

        // Per Phase D D-4 — per-path dimension caps. JPEG / PNG go
        // through the passthrough path (no full RGBA decode in process
        // memory) so they get the looser MaxDimension. GIF / WebP / BMP
        // go through the raster path (Skia → RGBA8888 → split planes)
        // which gets the tighter MaxRasterDimension. AVIF is rejected
        // earlier in this method.
        var (axisCap, areaCap) = format switch
        {
            ImageFormat.Jpeg or ImageFormat.Png => (MaxDimension, MaxPixelArea),
            ImageFormat.Gif or ImageFormat.WebP or ImageFormat.Bmp =>
                (MaxRasterDimension, MaxRasterPixelArea),
            _ => (MaxDimension, MaxPixelArea), // unreachable; keeps compiler happy
        };
        if (width > axisCap || height > axisCap)
        {
            return new ValidationResult(
                ImageSafetyVerdict.Unsafe,
                $"declared dimensions ({width} × {height}) exceed the {axisCap}-px per-axis cap for {format}",
                format);
        }
        var area = (long)width * height;
        if (area > areaCap)
        {
            return new ValidationResult(
                ImageSafetyVerdict.Unsafe,
                $"declared pixel area ({area}) exceeds the {areaCap}-pixel cap for {format}",
                format);
        }

        return new ValidationResult(ImageSafetyVerdict.Safe, null, format);
    }

    /// <summary>Identify the format from <paramref name="bytes"/>'s leading
    /// signature. Closed set; anything unrecognized returns
    /// <see cref="ImageFormat.Unknown"/>. Caller is expected to have already
    /// checked <c>bytes.Length &gt;= 12</c>.</summary>
    public static ImageFormat SniffFormat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12) return ImageFormat.Unknown;

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return ImageFormat.Png;
        }

        // JPEG SOI: FF D8 FF (followed by APP marker).
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ImageFormat.Jpeg;
        }

        // GIF: "GIF87a" or "GIF89a".
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46
            && bytes[3] == 0x38 && (bytes[4] == 0x37 || bytes[4] == 0x39) && bytes[5] == 0x61)
        {
            return ImageFormat.Gif;
        }

        // WebP: "RIFF" + 4 byte size + "WEBP".
        if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return ImageFormat.WebP;
        }

        // BMP: "BM".
        if (bytes[0] == 0x42 && bytes[1] == 0x4D)
        {
            return ImageFormat.Bmp;
        }

        // AVIF — ISOBMFF box at bytes 0..7: 4-byte size + "ftyp" + brand at
        // bytes 8..11 ("avif" / "avis" / "heic" — the latter two are
        // adjacent ISOBMFF brands that share the same box structure).
        // Per PR #17 review user-recommendation #1.
        if (bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
        {
            // Check the major brand at bytes 8..11.
            if (bytes[8] == 0x61 && bytes[9] == 0x76 && bytes[10] == 0x69 && bytes[11] == 0x66) // "avif"
                return ImageFormat.Avif;
            if (bytes[8] == 0x61 && bytes[9] == 0x76 && bytes[10] == 0x69 && bytes[11] == 0x73) // "avis"
                return ImageFormat.Avif;
            if (bytes[8] == 0x68 && bytes[9] == 0x65 && bytes[10] == 0x69 && bytes[11] == 0x63) // "heic"
                return ImageFormat.Avif;
        }

        return ImageFormat.Unknown;
    }

    /// <summary>Peek the format header for declared width / height. Returns
    /// <see langword="false"/> with a <paramref name="reason"/> when the header
    /// is too short, has malformed length fields, or otherwise can't yield
    /// dimensions. The format-specific branches read at most ~30 bytes each.</summary>
    private static bool TryPeekDimensions(
        ReadOnlySpan<byte> bytes, ImageFormat format,
        out int width, out int height, out string? reason)
    {
        width = 0;
        height = 0;
        reason = null;
        switch (format)
        {
            case ImageFormat.Png:
                // PNG IHDR is the first chunk: 8-byte signature + 4-byte length
                // + 4-byte type + 4-byte width + 4-byte height. Width is at
                // bytes 16..19, height at 20..23, big-endian.
                if (bytes.Length < 24) { reason = "header truncated"; return false; }
                width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                if (width <= 0 || height <= 0) { reason = "non-positive dimensions"; return false; }
                return true;

            case ImageFormat.Jpeg:
                return TryPeekJpegDimensions(bytes, out width, out height, out reason);

            case ImageFormat.Gif:
                // GIF logical-screen-descriptor: width at bytes 6..7,
                // height at 8..9, little-endian.
                if (bytes.Length < 10) { reason = "header truncated"; return false; }
                width = bytes[6] | (bytes[7] << 8);
                height = bytes[8] | (bytes[9] << 8);
                if (width <= 0 || height <= 0) { reason = "non-positive dimensions"; return false; }
                return true;

            case ImageFormat.WebP:
                return TryPeekWebPDimensions(bytes, out width, out height, out reason);

            case ImageFormat.Bmp:
                // BMP DIB header (BITMAPINFOHEADER): width at bytes 18..21,
                // height at 22..25, little-endian, signed (height can be
                // negative for top-down). Take absolute value via long so
                // 0x80000000 (int.MinValue) doesn't throw OverflowException
                // out of Math.Abs(int) — per PR #17 review user-recommendation #8.
                if (bytes.Length < 26) { reason = "header truncated"; return false; }
                var bmpW = (long)(bytes[18] | (bytes[19] << 8) | (bytes[20] << 16) | (bytes[21] << 24));
                var bmpH = (long)(bytes[22] | (bytes[23] << 8) | (bytes[24] << 16) | (bytes[25] << 24));
                var bmpAbsW = Math.Abs(bmpW);
                var bmpAbsH = Math.Abs(bmpH);
                if (bmpAbsW > int.MaxValue || bmpAbsH > int.MaxValue)
                {
                    reason = "BMP dimensions exceed Int32 range";
                    return false;
                }
                width = (int)bmpAbsW;
                height = (int)bmpAbsH;
                if (width <= 0 || height <= 0) { reason = "non-positive dimensions"; return false; }
                return true;

            default:
                reason = "unknown format";
                return false;
        }
    }

    /// <summary>Walk JPEG markers until SOF0/SOF1/SOF2/SOF3/etc. and read the
    /// frame's height + width. Skips APP / DQT / DHT / DAC / DRI / COM markers
    /// by their 16-bit-BE segment lengths. Bounded scan: refuses to walk past
    /// 16 KiB of header markers (legitimate JPEG headers are always &lt; 8 KiB).</summary>
    private static bool TryPeekJpegDimensions(
        ReadOnlySpan<byte> bytes, out int width, out int height, out string? reason)
    {
        width = 0;
        height = 0;
        reason = null;
        // Skip SOI (FF D8). Walk markers from position 2.
        var pos = 2;
        var maxScan = Math.Min(bytes.Length, 16 * 1024);
        while (pos + 4 <= maxScan)
        {
            if (bytes[pos] != 0xFF) { reason = "marker alignment lost"; return false; }
            // Multiple FFs are valid filler.
            while (pos < maxScan && bytes[pos] == 0xFF) pos++;
            if (pos >= maxScan) { reason = "header truncated"; return false; }
            var marker = bytes[pos];
            pos++;
            // SOFn frame headers: C0..C3, C5..C7, C9..CB, CD..CF.
            // C4 is DHT, C8 is JPG (reserved), CC is DAC.
            if (IsJpegSofMarker(marker))
            {
                // SOF segment: 2-byte length + 1-byte precision + 2-byte height + 2-byte width + ...
                if (pos + 7 > bytes.Length) { reason = "SOF segment truncated"; return false; }
                height = (bytes[pos + 3] << 8) | bytes[pos + 4];
                width = (bytes[pos + 5] << 8) | bytes[pos + 6];
                if (width <= 0 || height <= 0) { reason = "non-positive dimensions"; return false; }
                return true;
            }
            // RST0..RST7 (D0..D7), SOI (D8), EOI (D9), TEM (01) are length-less.
            if (marker is >= 0xD0 and <= 0xD9 || marker == 0x01)
            {
                continue;
            }
            // Length-bearing segment: read 16-bit BE length.
            if (pos + 2 > bytes.Length) { reason = "segment length truncated"; return false; }
            var segLen = (bytes[pos] << 8) | bytes[pos + 1];
            if (segLen < 2) { reason = "segment length < 2"; return false; }
            pos += segLen; // length includes the 2 length bytes
        }
        reason = "no SOF marker before max scan";
        return false;
    }

    private static bool IsJpegSofMarker(byte m) =>
        (m >= 0xC0 && m <= 0xC3)
        || (m >= 0xC5 && m <= 0xC7)
        || (m >= 0xC9 && m <= 0xCB)
        || (m >= 0xCD && m <= 0xCF);

    /// <summary>WebP dimensions live in the chunk after the RIFF + WEBP header.
    /// Three formats:
    /// <list type="bullet">
    ///   <item><c>VP8 </c> (lossy): 14 bytes after the chunk header carries the
    ///   key-frame; bit 0..14 of bytes 6..9 = width-1, height-1.</item>
    ///   <item><c>VP8L</c> (lossless): 14 bits of width-1 + 14 bits of height-1
    ///   packed at offset 5..</item>
    ///   <item><c>VP8X</c> (extended): explicit 24-bit canvas-width-1 +
    ///   canvas-height-1.</item>
    /// </list>
    /// We parse the simpler VP8 / VP8X paths; VP8L bit-packing is decoded too.</summary>
    private static bool TryPeekWebPDimensions(
        ReadOnlySpan<byte> bytes, out int width, out int height, out string? reason)
    {
        width = 0;
        height = 0;
        reason = null;
        // Bytes 12..15 are the chunk fourCC; 16..19 are chunk-size (LE);
        // 20.. are chunk payload.
        if (bytes.Length < 30) { reason = "header truncated"; return false; }
        var fourCc = (bytes[12], bytes[13], bytes[14], bytes[15]);

        // VP8 (lossy): "VP8 " (with trailing space, 0x20).
        if (fourCc == (0x56, 0x50, 0x38, 0x20))
        {
            // Frame payload starts at 20. Width at bytes 26..27 (LE, 14 bits + 2 scale bits);
            // Height at 28..29.
            if (bytes.Length < 30) { reason = "VP8 frame truncated"; return false; }
            var w14 = (bytes[26] | (bytes[27] << 8)) & 0x3FFF;
            var h14 = (bytes[28] | (bytes[29] << 8)) & 0x3FFF;
            width = w14;
            height = h14;
            if (width <= 0 || height <= 0) { reason = "non-positive dimensions"; return false; }
            return true;
        }

        // VP8L (lossless): "VP8L". Payload starts at 20. Byte 20 = signature
        // 0x2F. Bytes 21..24 carry packed (width-1, 14 bits) + (height-1, 14 bits).
        if (fourCc == (0x56, 0x50, 0x38, 0x4C))
        {
            if (bytes.Length < 25) { reason = "VP8L frame truncated"; return false; }
            if (bytes[20] != 0x2F) { reason = "VP8L signature mismatch"; return false; }
            var packed =
                (uint)bytes[21]
                | ((uint)bytes[22] << 8)
                | ((uint)bytes[23] << 16)
                | ((uint)bytes[24] << 24);
            width = (int)((packed & 0x3FFF) + 1);
            height = (int)(((packed >> 14) & 0x3FFF) + 1);
            return true;
        }

        // VP8X (extended). Payload at bytes 20.. : 1 byte flags + 3 bytes
        // reserved + 3 bytes (canvas width - 1, LE) + 3 bytes (canvas height - 1, LE).
        if (fourCc == (0x56, 0x50, 0x38, 0x58))
        {
            if (bytes.Length < 30) { reason = "VP8X chunk truncated"; return false; }
            var w24 = bytes[24] | (bytes[25] << 8) | (bytes[26] << 16);
            var h24 = bytes[27] | (bytes[28] << 8) | (bytes[29] << 16);
            width = w24 + 1;
            height = h24 + 1;
            return true;
        }

        reason = $"unrecognized WebP chunk fourCC '{(char)fourCc.Item1}{(char)fourCc.Item2}{(char)fourCc.Item3}{(char)fourCc.Item4}'";
        return false;
    }
}
