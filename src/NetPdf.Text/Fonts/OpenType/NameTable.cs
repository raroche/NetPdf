// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;

namespace NetPdf.Text.Fonts.OpenType;

/// <summary>
/// Parsed <c>name</c> table (OpenType §"name"). Carries the human-readable identifying
/// strings (family name, subfamily, full name, postscript name, …). Phase 1 needs the
/// PostScript name for the embedded-font BaseFont and the family/subfamily for diagnostics.
/// </summary>
/// <remarks>
/// Wire format: a 6-byte header (format / count / storageOffset) followed by
/// <c>count</c> 12-byte name records and a string-data heap. Strings can be encoded in
/// UTF-16BE (Microsoft / Unicode platforms) or MacRoman (Macintosh platform); we decode
/// both. Name format 1 also has language-tag records — Phase 1 parses but does not
/// expose them.
/// </remarks>
internal sealed class NameTable
{
    public const ushort PlatformIdUnicode = 0;
    public const ushort PlatformIdMacintosh = 1;
    public const ushort PlatformIdWindows = 3;

    public const ushort NameIdCopyright = 0;
    public const ushort NameIdFamilyName = 1;
    public const ushort NameIdSubfamilyName = 2;
    public const ushort NameIdUniqueId = 3;
    public const ushort NameIdFullName = 4;
    public const ushort NameIdVersionString = 5;
    public const ushort NameIdPostScriptName = 6;

    public required ushort Format { get; init; }
    public required IReadOnlyList<NameRecord> Records { get; init; }

    /// <summary>
    /// Pick the first record matching <paramref name="nameId"/>, preferring
    /// Windows-Unicode → Unicode-platform → Macintosh. Records whose <see cref="NameRecord.Text"/>
    /// failed to decode are skipped so a partially-readable font still surfaces names from
    /// usable records. Returns <c>null</c> if no record matches.
    /// </summary>
    public string? GetName(ushort nameId)
    {
        NameRecord? windows = null;
        NameRecord? unicode = null;
        NameRecord? mac = null;
        foreach (var r in Records)
        {
            if (r.NameId != nameId || r.Text is null)
            {
                continue;
            }
            switch (r.PlatformId)
            {
                case PlatformIdWindows when windows is null:
                    windows = r;
                    break;
                case PlatformIdUnicode when unicode is null:
                    unicode = r;
                    break;
                case PlatformIdMacintosh when mac is null:
                    mac = r;
                    break;
            }
        }
        return windows?.Text ?? unicode?.Text ?? mac?.Text;
    }

    /// <summary>Convenience — returns the family name (nameID = 1).</summary>
    public string? FamilyName => GetName(NameIdFamilyName);

    /// <summary>Convenience — returns the PostScript name (nameID = 6).</summary>
    public string? PostScriptName => GetName(NameIdPostScriptName);

    public static NameTable Parse(ReadOnlySpan<byte> tableBytes)
    {
        if (tableBytes.Length < 6)
        {
            throw new InvalidDataException(
                $"name: expected at least 6 bytes for header; got {tableBytes.Length}.");
        }
        var reader = new BigEndianReader(tableBytes);
        var format = reader.ReadUInt16();
        if (format is not (0 or 1))
        {
            throw new InvalidDataException(
                $"name: unknown format {format}. Spec defines 0 and 1.");
        }
        var count = reader.ReadUInt16();
        var storageOffset = reader.ReadUInt16();
        if (storageOffset > tableBytes.Length)
        {
            throw new InvalidDataException(
                $"name: storageOffset {storageOffset} exceeds table length {tableBytes.Length}.");
        }

        var headerSize = 6 + (count * 12);
        if (tableBytes.Length < headerSize)
        {
            throw new InvalidDataException(
                $"name: table truncated. Header for {count} record(s) needs {headerSize} bytes; got {tableBytes.Length}.");
        }

        var records = new List<NameRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var platformId = reader.ReadUInt16();
            var encodingId = reader.ReadUInt16();
            var languageId = reader.ReadUInt16();
            var nameId = reader.ReadUInt16();
            var length = reader.ReadUInt16();
            var offset = reader.ReadUInt16();

            var stringStart = storageOffset + offset;
            if (stringStart + length > tableBytes.Length)
            {
                // Spec allows truncated records in the wild — skip rather than fail the whole parse.
                records.Add(new NameRecord
                {
                    PlatformId = platformId,
                    EncodingId = encodingId,
                    LanguageId = languageId,
                    NameId = nameId,
                    Text = null,
                });
                continue;
            }

            var raw = tableBytes.Slice(stringStart, length);
            var text = DecodeNameString(raw, platformId, encodingId);
            records.Add(new NameRecord
            {
                PlatformId = platformId,
                EncodingId = encodingId,
                LanguageId = languageId,
                NameId = nameId,
                Text = text,
            });
        }

        return new NameTable
        {
            Format = format,
            Records = records,
        };
    }

    private static string? DecodeNameString(ReadOnlySpan<byte> raw, ushort platformId, ushort encodingId)
    {
        return platformId switch
        {
            PlatformIdWindows => Encoding.BigEndianUnicode.GetString(raw),
            PlatformIdUnicode => Encoding.BigEndianUnicode.GetString(raw),
            PlatformIdMacintosh when encodingId == 0 => DecodeMacRoman(raw),
            _ => null, // unknown platform/encoding — surface as null rather than guess
        };
    }

    private static string DecodeMacRoman(ReadOnlySpan<byte> raw)
    {
        // MacRoman matches ASCII for 0x00..0x7F. Values 0x80..0xFF map to a fixed table; for the
        // identifying strings we care about (font family / postscript / full name) Latin/ASCII is
        // overwhelmingly the case. A tiny fallback table covers the most common high-byte glyphs;
        // the rest decode as the replacement char so unknown bytes never crash downstream.
        var sb = new StringBuilder(raw.Length);
        foreach (var b in raw)
        {
            sb.Append(b < 0x80 ? (char)b : MacRomanHighByteFallback(b));
        }
        return sb.ToString();
    }

    private static char MacRomanHighByteFallback(byte b) => b switch
    {
        0xCA => ' ', // non-breaking space
        0xAE => '®', // ®
        0xA8 => '©', // ©
        0xAA => '™', // ™
        0xB7 => '•', // bullet
        0xD2 => '“', // left double quote
        0xD3 => '”', // right double quote
        0xD4 => '‘', // left single quote
        0xD5 => '’', // right single quote
        _ => '�',    // replacement char
    };
}

/// <summary>One <c>name</c> table record. <see cref="Text"/> is null when the bytes were truncated or the encoding wasn't recognized.</summary>
internal sealed class NameRecord
{
    public required ushort PlatformId { get; init; }
    public required ushort EncodingId { get; init; }
    public required ushort LanguageId { get; init; }
    public required ushort NameId { get; init; }
    public required string? Text { get; init; }
}
