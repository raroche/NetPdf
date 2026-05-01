// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Frozen;

namespace NetPdf.Text.Fonts.OpenType;

/// <summary>
/// Parsed SFNT header + table directory. The SFNT header (12 bytes) names the outline
/// flavor and table count; the directory that follows is one <see cref="TableRecord"/>
/// per table sorted by tag (OpenType §"Table directory").
/// </summary>
internal sealed class TableDirectory
{
    /// <summary>Magic identifying the outline format. See <see cref="OpenTypeTags.SfntVersionTtf"/> etc.</summary>
    public required uint SfntVersion { get; init; }

    /// <summary>Number of <see cref="TableRecord"/> entries.</summary>
    public required ushort NumTables { get; init; }

    /// <summary>All table records, indexed by <see cref="TableRecord.Tag"/>. Frozen for fast lookup.</summary>
    public required FrozenDictionary<uint, TableRecord> Tables { get; init; }

    /// <summary>True if this is a TrueType-outline font (TTF).</summary>
    public bool IsTrueType => SfntVersion is OpenTypeTags.SfntVersionTtf or OpenTypeTags.SfntVersionAppleTrue;

    /// <summary>True if this is a PostScript-outline font (OTF/CFF).</summary>
    public bool IsCff => SfntVersion == OpenTypeTags.SfntVersionOtf;

    /// <summary>Look up a table record by tag.</summary>
    public bool TryGetRecord(uint tag, out TableRecord record) => Tables.TryGetValue(tag, out record);

    /// <summary>
    /// Slice the original font bytes for a specific table. Throws if the table is missing or
    /// extends past the end of the font.
    /// </summary>
    public ReadOnlySpan<byte> GetTableBytes(uint tag, ReadOnlySpan<byte> fontBytes)
    {
        if (!Tables.TryGetValue(tag, out var record))
        {
            throw new InvalidDataException($"OpenType: required table '{OpenTypeTags.ToAsciiString(tag)}' is missing.");
        }
        if ((long)record.Offset + record.Length > fontBytes.Length)
        {
            throw new InvalidDataException(
                $"OpenType: table '{OpenTypeTags.ToAsciiString(tag)}' at offset {record.Offset} length {record.Length} " +
                $"extends past font end ({fontBytes.Length}).");
        }
        return fontBytes.Slice((int)record.Offset, (int)record.Length);
    }

    /// <summary>
    /// Parse the SFNT header + table directory from the head of <paramref name="fontBytes"/>.
    /// </summary>
    public static TableDirectory Parse(ReadOnlySpan<byte> fontBytes)
    {
        var reader = new BigEndianReader(fontBytes);

        var sfntVersion = reader.ReadUInt32();
        if (sfntVersion is not (OpenTypeTags.SfntVersionTtf
                                or OpenTypeTags.SfntVersionOtf
                                or OpenTypeTags.SfntVersionAppleTrue))
        {
            throw new InvalidDataException(
                $"OpenType: unrecognized SFNT version 0x{sfntVersion:X8}. Expected TTF (0x00010000), " +
                $"OTF/CFF ('OTTO'), or Apple TrueType ('true').");
        }

        var numTables = reader.ReadUInt16();
        if (numTables == 0)
        {
            throw new InvalidDataException("OpenType: numTables is 0; font has no tables.");
        }

        // searchRange / entrySelector / rangeShift — historically used by old binary-search parsers.
        // We don't rely on them, but we still consume them to advance the reader.
        _ = reader.ReadUInt16(); // searchRange
        _ = reader.ReadUInt16(); // entrySelector
        _ = reader.ReadUInt16(); // rangeShift

        var dict = new Dictionary<uint, TableRecord>(numTables);
        for (var i = 0; i < numTables; i++)
        {
            var tag = reader.ReadUInt32();
            var checksum = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            var length = reader.ReadUInt32();

            if (!dict.TryAdd(tag, new TableRecord
            {
                Tag = tag,
                Checksum = checksum,
                Offset = offset,
                Length = length,
            }))
            {
                throw new InvalidDataException(
                    $"OpenType: duplicate table tag '{OpenTypeTags.ToAsciiString(tag)}' in directory.");
            }
        }

        return new TableDirectory
        {
            SfntVersion = sfntVersion,
            NumTables = numTables,
            Tables = dict.ToFrozenDictionary(),
        };
    }
}
