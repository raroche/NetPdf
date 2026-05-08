// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Text.Fonts;

/// <summary>
/// Per Phase C C-2 — pre-decode font safety gate. Routes incoming font bytes
/// through a magic-byte sniffer + size + sfnt-header sanity check before they
/// reach <c>OpenTypeFont.Parse</c> / HarfBuzz. Defends against:
///
/// <list type="bullet">
///   <item><b>HarfBuzz CVE-class bugs.</b> CVE-2024-56732 type — malformed
///   <c>cmap</c> / <c>GSUB</c> / <c>GPOS</c> / <c>glyf</c> tables triggering
///   buffer overruns in the shaper. The validator can't fix the bug, but it
///   bounds the attack surface by rejecting obviously-malformed sfnt headers
///   before HarfBuzz sees the bytes.</item>
///   <item><b>DomPDF @font-face cache poisoning.</b> Once Phase 5 wires
///   <c>@font-face</c>, an attacker who can serve a font URL could pick the
///   filename + content. The validator's format-magic check + size cap stop
///   the cache from accepting non-fonts.</item>
/// </list>
///
/// <para>The validator does NOT replace the structural validation in
/// <c>OpenTypeFont.Parse</c> — it just bounds the attack surface before parse
/// runs. Parse is still authoritative for table-by-table integrity.</para>
///
/// <para><b>What this validator does NOT inspect:</b> sfnt table tags
/// (<c>cmap</c> / <c>glyf</c> / <c>SVG </c> / etc.) — only the
/// <c>numTables</c> count + directory bounds. SVG-in-OpenType fonts are
/// accepted by this gate; future hardening could walk the table tags to
/// reject SVG-only fonts (a real attack surface) but that work is not in
/// Phase C. Per PR #17 Copilot review #2 the docs were over-claiming;
/// they now state actual coverage.</para>
/// </summary>
public static class FontSafetyValidator
{
    /// <summary>Maximum encoded font bytes accepted by the pre-decode gate.
    /// Real-world fonts: Roboto Regular = 174 KiB, Noto Sans CJK Regular =
    /// 17 MiB. Cap at 32 MiB allows the largest legitimate single fonts +
    /// stops a 100 MiB attacker upload.</summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    /// <summary>Maximum number of sfnt table records in the directory. Real
    /// fonts have 10–25 tables; 64 is generous. A 65 535-table file (the
    /// uint16 max) would exhaust pre-Parse buffer allocations.</summary>
    public const int MaxTableCount = 64;

    /// <summary>Minimum file size to even be considered. The sfnt header is
    /// 12 bytes + at least 16 bytes per table record; reject anything that
    /// can't fit a single-table sfnt.</summary>
    public const int MinBytes = 12 + 16;

    /// <summary>Recognized font formats. Anything else routes to
    /// <see cref="FontFormat.Unknown"/> and is rejected.</summary>
    public enum FontFormat
    {
        Unknown = 0,
        /// <summary>TrueType — sfnt magic <c>00 01 00 00</c>.</summary>
        TrueType,
        /// <summary>OpenType / CFF — sfnt magic <c>"OTTO"</c>.</summary>
        OpenTypeCff,
        /// <summary>WOFF — wrapped sfnt, magic <c>"wOFF"</c>.</summary>
        Woff,
        /// <summary>WOFF2 — Brotli-wrapped sfnt, magic <c>"wOF2"</c>.</summary>
        Woff2,
    }

    /// <summary>Two-state verdict. Mirror of <c>ImageSafetyValidator</c> + the
    /// Phase B <c>UriSafetyValidator</c> for consistency.</summary>
    public enum FontSafetyVerdict { Safe = 0, Unsafe = 1 }

    public readonly record struct ValidationResult(
        FontSafetyVerdict Verdict, string? Reason, FontFormat DetectedFormat)
    {
        public bool IsSafe => Verdict == FontSafetyVerdict.Safe;
    }

    /// <summary>Validate <paramref name="fontBytes"/> against the per-font
    /// safety caps. Two passes: byte-size check, then magic-byte sniff. For
    /// TrueType / OpenType-CFF formats, additionally validate the sfnt
    /// directory header (numTables ≤ <see cref="MaxTableCount"/>, header
    /// length consistent with <paramref name="fontBytes"/>.Length).</summary>
    public static ValidationResult Validate(ReadOnlySpan<byte> fontBytes)
    {
        if (fontBytes.Length < MinBytes)
        {
            return new ValidationResult(
                FontSafetyVerdict.Unsafe,
                $"font bytes ({fontBytes.Length}) below minimum {MinBytes}-byte sfnt header",
                FontFormat.Unknown);
        }
        if (fontBytes.Length > MaxBytes)
        {
            return new ValidationResult(
                FontSafetyVerdict.Unsafe,
                $"font bytes ({fontBytes.Length}) exceed the {MaxBytes / (1024 * 1024)} MiB pre-decode cap",
                FontFormat.Unknown);
        }

        var format = SniffFormat(fontBytes);
        if (format == FontFormat.Unknown)
        {
            return new ValidationResult(
                FontSafetyVerdict.Unsafe,
                "font bytes did not match any recognized format magic (TTF / OTF / WOFF / WOFF2)",
                FontFormat.Unknown);
        }

        // Sfnt-format header sanity. WOFF/WOFF2 have their own headers
        // (with their own table counts) that decompression unwraps; the
        // wrapped formats get their own header checks below.
        if (format is FontFormat.TrueType or FontFormat.OpenTypeCff)
        {
            var sfntResult = ValidateSfntHeader(fontBytes, format);
            if (!sfntResult.IsSafe) return sfntResult;
        }
        else if (format is FontFormat.Woff)
        {
            // Per Phase D D-5 — WOFF header sanity (W3C WOFF File Format
            // 1.0 §3): wOFF magic + flavor (sfnt version of the wrapped
            // font) + length + numTables + reserved + totalSfntSize.
            // 44-byte fixed header, validated before future Brotli/zlib
            // decode reaches OpenTypeFont.Parse.
            var woffResult = ValidateWoffHeader(fontBytes);
            if (!woffResult.IsSafe) return woffResult;
        }
        else if (format is FontFormat.Woff2)
        {
            // Per Phase D D-5 — WOFF2 header sanity (W3C WOFF2 §4):
            // wOF2 magic + flavor + length + numTables + reserved +
            // totalSfntSize + totalCompressedSize. 48-byte fixed header.
            var woff2Result = ValidateWoff2Header(fontBytes);
            if (!woff2Result.IsSafe) return woff2Result;
        }

        return new ValidationResult(FontSafetyVerdict.Safe, null, format);
    }

    /// <summary>Per Phase D D-5 — sfnt directory walk. Beyond the header
    /// sanity (numTables + directory bounds), this validates EVERY table
    /// record's offset+length lies within the file + rejects any font
    /// that uses one of the danger-class tables NetPdf v1 doesn't
    /// render. The exact denylist enforced by <see cref="IsDangerousTableTag"/>
    /// is: <c>SVG </c> (SVG-in-OpenType), <c>sbix</c> (Apple bitmap),
    /// <c>CBDT</c> + <c>CBLC</c> (Google color bitmap data + location),
    /// <c>EBDT</c> + <c>EBLC</c> (embedded bitmap data + location). SVG-
    /// in-OpenType has been a real attack surface (parsers process SVG
    /// payloads as graphics); bitmap glyph tables route through different
    /// rendering code we don't own. Rejecting them upfront keeps the
    /// attack surface bounded; a font with these tables alongside
    /// <c>glyf</c>/<c>CFF</c> will still render via the supported tables
    /// in a future relaxation.
    ///
    /// <para><b>Per PR #18 Copilot review #7 — table list aligned.</b>
    /// An earlier docstring revision listed <c>COLR</c>/<c>CPAL</c> as
    /// part of the denylist; those are color-glyph palette tables that
    /// NetPdf v1 ALSO doesn't render but the actual enforcement only
    /// covers the bitmap + SVG surfaces above. Decision: keep COLR/CPAL
    /// off the denylist for now (they're palette metadata, not embedded
    /// payloads — much smaller attack surface than SVG / bitmap glyph
    /// tables), and clarify the doc to match what's enforced. If a
    /// future review wants COLR/CPAL added too, both the doc + the
    /// <see cref="IsDangerousTableTag"/> body should be updated together.</para>
    ///
    /// <para><b>Per PR #18 review #9 — public for post-decompression
    /// re-validation.</b> WOFF / WOFF2 wrap an sfnt + apply zlib /
    /// Brotli compression to the table data. The pre-decompression
    /// validators (<see cref="ValidateWoffHeader"/> /
    /// <see cref="ValidateWoff2Header"/>) only see the wrapper, not the
    /// decoded sfnt directory. Phase 5's WOFF/WOFF2 decoder MUST call
    /// this method on the reconstructed sfnt bytes BEFORE handing them
    /// to <c>OpenTypeFont.Parse</c> / HarfBuzz — without that, a
    /// wrapped font with a <c>SVG </c> table sneaks past the table-tag
    /// denylist. Made <c>public</c> (was internal) so Phase 5 can
    /// invoke it from the decompressor without a back-channel.</para>
    /// </summary>
    public static ValidationResult ValidateSfntHeader(ReadOnlySpan<byte> fontBytes, FontFormat format)
    {
        // sfnt header: uint32 sfntVersion, uint16 numTables, uint16 searchRange,
        // uint16 entrySelector, uint16 rangeShift. All BE.
        var numTables = (fontBytes[4] << 8) | fontBytes[5];
        if (numTables == 0)
        {
            return new ValidationResult(
                FontSafetyVerdict.Unsafe,
                "sfnt header declares 0 tables",
                format);
        }
        if (numTables > MaxTableCount)
        {
            return new ValidationResult(
                FontSafetyVerdict.Unsafe,
                $"sfnt header declares {numTables} tables; cap is {MaxTableCount}",
                format);
        }
        // 12-byte header + numTables × 16-byte records must fit.
        var directoryEnd = 12 + numTables * 16;
        if (directoryEnd > fontBytes.Length)
        {
            return new ValidationResult(
                FontSafetyVerdict.Unsafe,
                $"sfnt directory ({directoryEnd} bytes) extends past file length ({fontBytes.Length})",
                format);
        }

        // Per Phase D D-5 — walk every table record + verify its
        // offset + length are within the file + the table tag isn't
        // on the danger denylist. Each record is 16 bytes:
        // 4-byte tag, 4-byte checksum, 4-byte offset, 4-byte length.
        for (var i = 0; i < numTables; i++)
        {
            var recordOffset = 12 + i * 16;
            // Tag is 4 ASCII bytes, e.g., "glyf" (left-padded with spaces).
            var t0 = (char)fontBytes[recordOffset + 0];
            var t1 = (char)fontBytes[recordOffset + 1];
            var t2 = (char)fontBytes[recordOffset + 2];
            var t3 = (char)fontBytes[recordOffset + 3];
            if (IsDangerousTableTag(t0, t1, t2, t3))
            {
                return new ValidationResult(
                    FontSafetyVerdict.Unsafe,
                    $"font references the {t0}{t1}{t2}{t3} table; bitmap/SVG glyph surfaces are not rendered by NetPdf v1 and the table is rejected to bound the attack surface",
                    format);
            }

            var tableOffset =
                  ((uint)fontBytes[recordOffset + 8] << 24)
                | ((uint)fontBytes[recordOffset + 9] << 16)
                | ((uint)fontBytes[recordOffset + 10] << 8)
                |  (uint)fontBytes[recordOffset + 11];
            var tableLength =
                  ((uint)fontBytes[recordOffset + 12] << 24)
                | ((uint)fontBytes[recordOffset + 13] << 16)
                | ((uint)fontBytes[recordOffset + 14] << 8)
                |  (uint)fontBytes[recordOffset + 15];

            // offset + length must fit within the file (use uint math
            // to catch overflow attempts where offset + length wraps).
            var endOffset = (long)tableOffset + tableLength;
            if (endOffset > fontBytes.Length)
            {
                return new ValidationResult(
                    FontSafetyVerdict.Unsafe,
                    $"table {t0}{t1}{t2}{t3} record (offset {tableOffset} + length {tableLength}) extends past file length {fontBytes.Length}",
                    format);
            }
        }

        return new ValidationResult(FontSafetyVerdict.Safe, null, format);
    }

    /// <summary>Per Phase D D-5 — danger-class table tags. NetPdf v1
    /// renders only outline-based glyphs from <c>glyf</c> (TrueType) /
    /// <c>CFF </c> / <c>CFF2</c>. Bitmap + SVG glyph surfaces are
    /// rejected up front: the Phase 5 shaper pipeline doesn't own them,
    /// and accepting them would invite the SVG-in-OpenType + colored-
    /// bitmap attack-surface classes.</summary>
    private static bool IsDangerousTableTag(char a, char b, char c, char d)
    {
        // SVG  — embedded SVG glyphs.
        if (a == 'S' && b == 'V' && c == 'G' && d == ' ') return true;
        // sbix — Apple's bitmap (PNG / JPEG) glyph table.
        if (a == 's' && b == 'b' && c == 'i' && d == 'x') return true;
        // CBDT / CBLC — color bitmap data + location table (Google).
        if (a == 'C' && b == 'B' && c == 'D' && d == 'T') return true;
        if (a == 'C' && b == 'B' && c == 'L' && d == 'C') return true;
        // EBDT / EBLC — embedded bitmap data + location.
        if (a == 'E' && b == 'B' && c == 'D' && d == 'T') return true;
        if (a == 'E' && b == 'B' && c == 'L' && d == 'C') return true;
        return false;
    }

    /// <summary>Per Phase D D-5 — WOFF (1.0) header validation per
    /// W3C WOFF File Format 1.0 §3. Accepts the font when the
    /// fixed-size header is internally consistent + fits within the
    /// file; the actual zlib-compressed table decode is Phase 5's
    /// problem. The wrapped flavor is also checked so a WOFF claiming
    /// to wrap "ttf2" or some other made-up sfnt magic is rejected.</summary>
    internal static ValidationResult ValidateWoffHeader(ReadOnlySpan<byte> bytes)
    {
        // WOFF header: signature(4) + flavor(4) + length(4) + numTables(2)
        // + reserved(2) + totalSfntSize(4) + majorVersion(2) +
        // minorVersion(2) + metaOffset(4) + metaLength(4) +
        // metaOrigLength(4) + privOffset(4) + privLength(4) = 44 bytes.
        if (bytes.Length < 44)
        {
            return new ValidationResult(
                FontSafetyVerdict.Unsafe,
                $"WOFF header truncated ({bytes.Length} bytes); minimum 44",
                FontFormat.Woff);
        }
        var flavor =
            ((uint)bytes[4] << 24) | ((uint)bytes[5] << 16)
            | ((uint)bytes[6] << 8) | (uint)bytes[7];
        // Flavor must be a valid sfnt magic (TTF 0x00010000 or OTF "OTTO").
        if (flavor != 0x00010000u && flavor != 0x4F54544Fu)
        {
            return new ValidationResult(
                FontSafetyVerdict.Unsafe,
                $"WOFF wraps unknown sfnt flavor 0x{flavor:X8}; expected 0x00010000 (TTF) or 0x4F54544F (OTF)",
                FontFormat.Woff);
        }
        var length =
            ((uint)bytes[8] << 24) | ((uint)bytes[9] << 16)
            | ((uint)bytes[10] << 8) | (uint)bytes[11];
        if (length != bytes.Length)
        {
            return new ValidationResult(
                FontSafetyVerdict.Unsafe,
                $"WOFF declared length {length} != actual length {bytes.Length}",
                FontFormat.Woff);
        }
        var numTables = ((int)bytes[12] << 8) | bytes[13];
        if (numTables == 0 || numTables > MaxTableCount)
        {
            return new ValidationResult(
                FontSafetyVerdict.Unsafe,
                $"WOFF declares {numTables} tables; expected 1..{MaxTableCount}",
                FontFormat.Woff);
        }
        // reserved must be 0 per spec.
        if (bytes[14] != 0 || bytes[15] != 0)
        {
            return new ValidationResult(
                FontSafetyVerdict.Unsafe,
                "WOFF reserved field is non-zero",
                FontFormat.Woff);
        }
        return new ValidationResult(FontSafetyVerdict.Safe, null, FontFormat.Woff);
    }

    /// <summary>Per Phase D D-5 — WOFF2 header validation per W3C WOFF2
    /// §4. Same shape as WOFF but with totalCompressedSize replacing
    /// reserved. Brotli decompression of the table data is Phase 5's
    /// problem; this just bounds the wrapper.</summary>
    internal static ValidationResult ValidateWoff2Header(ReadOnlySpan<byte> bytes)
    {
        // WOFF2 header: signature(4) + flavor(4) + length(4) + numTables(2)
        // + reserved(2) + totalSfntSize(4) + totalCompressedSize(4) +
        // majorVersion(2) + minorVersion(2) + metaOffset(4) + metaLength(4) +
        // metaOrigLength(4) + privOffset(4) + privLength(4) = 48 bytes.
        if (bytes.Length < 48)
        {
            return new ValidationResult(
                FontSafetyVerdict.Unsafe,
                $"WOFF2 header truncated ({bytes.Length} bytes); minimum 48",
                FontFormat.Woff2);
        }
        var flavor =
            ((uint)bytes[4] << 24) | ((uint)bytes[5] << 16)
            | ((uint)bytes[6] << 8) | (uint)bytes[7];
        if (flavor != 0x00010000u && flavor != 0x4F54544Fu)
        {
            return new ValidationResult(
                FontSafetyVerdict.Unsafe,
                $"WOFF2 wraps unknown sfnt flavor 0x{flavor:X8}; expected 0x00010000 (TTF) or 0x4F54544F (OTF)",
                FontFormat.Woff2);
        }
        var length =
            ((uint)bytes[8] << 24) | ((uint)bytes[9] << 16)
            | ((uint)bytes[10] << 8) | (uint)bytes[11];
        if (length != bytes.Length)
        {
            return new ValidationResult(
                FontSafetyVerdict.Unsafe,
                $"WOFF2 declared length {length} != actual length {bytes.Length}",
                FontFormat.Woff2);
        }
        var numTables = ((int)bytes[12] << 8) | bytes[13];
        if (numTables == 0 || numTables > MaxTableCount)
        {
            return new ValidationResult(
                FontSafetyVerdict.Unsafe,
                $"WOFF2 declares {numTables} tables; expected 1..{MaxTableCount}",
                FontFormat.Woff2);
        }
        // Per PR #18 Copilot review #8 — WOFF2 reserved field (bytes
        // 14..15) must be zero per W3C WOFF2 §4. ValidateWoffHeader
        // already enforces this for WOFF; the WOFF2 path skipped it.
        // Non-zero reserved bytes are a malformed-wrapper signal and
        // a possible amplifier for downstream parser confusion;
        // reject up-front to match WOFF's behavior + the spec.
        if (bytes[14] != 0 || bytes[15] != 0)
        {
            return new ValidationResult(
                FontSafetyVerdict.Unsafe,
                "WOFF2 reserved field is non-zero",
                FontFormat.Woff2);
        }
        return new ValidationResult(FontSafetyVerdict.Safe, null, FontFormat.Woff2);
    }

    /// <summary>Identify the font format from <paramref name="bytes"/>'s
    /// leading 4 bytes. Caller is expected to have already checked
    /// <c>bytes.Length &gt;= 4</c>.</summary>
    public static FontFormat SniffFormat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) return FontFormat.Unknown;

        // TrueType: 00 01 00 00.
        if (bytes[0] == 0x00 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
            return FontFormat.TrueType;

        // OpenType-CFF: ASCII "OTTO".
        if (bytes[0] == 0x4F && bytes[1] == 0x54 && bytes[2] == 0x54 && bytes[3] == 0x4F)
            return FontFormat.OpenTypeCff;

        // WOFF: ASCII "wOFF".
        if (bytes[0] == 0x77 && bytes[1] == 0x4F && bytes[2] == 0x46 && bytes[3] == 0x46)
            return FontFormat.Woff;

        // WOFF2: ASCII "wOF2".
        if (bytes[0] == 0x77 && bytes[1] == 0x4F && bytes[2] == 0x46 && bytes[3] == 0x32)
            return FontFormat.Woff2;

        return FontFormat.Unknown;
    }
}
