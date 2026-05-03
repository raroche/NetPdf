// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.OpenType;

/// <summary>
/// Parsed <c>post</c> table header (OpenType §"post"). Phase 1 parses the 32-byte common
/// header only; per-glyph PostScript names (v2 / v2.5) are not consumed by the PDF
/// emitter (we use the <c>cmap</c>-derived glyph IDs directly), so deep parsing is
/// deferred until / unless a downstream task needs it.
/// </summary>
internal sealed class PostTable
{
    public const uint Version10 = 0x00010000u;
    public const uint Version20 = 0x00020000u;
    public const uint Version25 = 0x00025000u;
    public const uint Version30 = 0x00030000u;
    public const uint Version40 = 0x00040000u;

    public required uint Version { get; init; }

    /// <summary>Italic angle in counter-clockwise degrees from vertical (Fixed 16.16, raw uint32).</summary>
    public required uint ItalicAngle { get; init; }

    public required short UnderlinePosition { get; init; }
    public required short UnderlineThickness { get; init; }

    /// <summary>Non-zero when the font is monospaced.</summary>
    public required uint IsFixedPitch { get; init; }

    public required uint MinMemType42 { get; init; }
    public required uint MaxMemType42 { get; init; }
    public required uint MinMemType1 { get; init; }
    public required uint MaxMemType1 { get; init; }

    public bool IsMonospaced => IsFixedPitch != 0;

    public static PostTable Parse(ReadOnlySpan<byte> tableBytes)
    {
        if (tableBytes.Length < 32)
        {
            throw new InvalidDataException(
                $"post: expected at least 32 bytes; got {tableBytes.Length}.");
        }
        var reader = new BigEndianReader(tableBytes);
        var version = reader.ReadUInt32();
        if (version is not (Version10 or Version20 or Version25 or Version30 or Version40))
        {
            throw new InvalidDataException(
                $"post: unrecognized version 0x{version:X8}. Expected 1.0 / 2.0 / 2.5 / 3.0 / 4.0.");
        }
        var italicAngle = reader.ReadUInt32();
        var underlinePosition = reader.ReadInt16();
        var underlineThickness = reader.ReadInt16();
        var isFixedPitch = reader.ReadUInt32();
        var minMemType42 = reader.ReadUInt32();
        var maxMemType42 = reader.ReadUInt32();
        var minMemType1 = reader.ReadUInt32();
        var maxMemType1 = reader.ReadUInt32();

        return new PostTable
        {
            Version = version,
            ItalicAngle = italicAngle,
            UnderlinePosition = underlinePosition,
            UnderlineThickness = underlineThickness,
            IsFixedPitch = isFixedPitch,
            MinMemType42 = minMemType42,
            MaxMemType42 = maxMemType42,
            MinMemType1 = minMemType1,
            MaxMemType1 = maxMemType1,
        };
    }
}
