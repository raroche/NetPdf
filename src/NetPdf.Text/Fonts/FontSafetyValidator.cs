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

        // Sfnt-format header sanity. WOFF/WOFF2 have their own headers (with
        // their own table counts) that the WOFF/WOFF2 decoder validates;
        // accepting them here is just the format-magic check. The sfnt sanity
        // check applies only to the unwrapped TrueType / OpenType-CFF cases.
        if (format is FontFormat.TrueType or FontFormat.OpenTypeCff)
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
        }

        return new ValidationResult(FontSafetyVerdict.Safe, null, format);
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
