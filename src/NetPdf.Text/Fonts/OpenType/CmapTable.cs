// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.OpenType;

/// <summary>
/// Parsed <c>cmap</c> table (OpenType §"cmap"). Maps Unicode code points to glyph IDs.
/// Phase 1 supports formats 4 (BMP segmented) and 12 (full Unicode), which between them
/// cover virtually every modern font.
/// </summary>
/// <remarks>
/// Wire format: a 4-byte header (<c>version</c> / <c>numTables</c>) followed by
/// <c>numTables</c> 8-byte encoding records pointing into per-subtable data. The font may
/// carry several subtables for different platforms/encodings; <see cref="Parse"/> picks
/// the best Unicode-capable subtable per the OpenType §"Use of the cmap table" priority,
/// preferring (3, 10) Microsoft-Unicode-UCS-4 → (0, 6) Unicode-Full → (3, 1)
/// Microsoft-Unicode-BMP → (0, 3) Unicode-BMP.
/// </remarks>
internal sealed class CmapTable
{
    public required ushort SelectedPlatformId { get; init; }
    public required ushort SelectedEncodingId { get; init; }
    public required ushort SelectedFormat { get; init; }

    /// <summary>Sorted, non-overlapping Unicode → glyphID groups parsed from the chosen subtable.</summary>
    public required IReadOnlyList<CmapGroup> Groups { get; init; }

    /// <summary>Look up the glyph id for <paramref name="codePoint"/>; returns 0 (.notdef) when unmapped.</summary>
    public ushort GetGlyphId(uint codePoint)
    {
        // Linear scan is fine for Phase 1; format-4 fonts produce ~80–200 groups, format-12
        // tables are slightly larger. Binary search is a Phase 4+ optimization if measured.
        foreach (var g in Groups)
        {
            if (codePoint < g.StartCodePoint)
            {
                return 0;
            }
            if (codePoint <= g.EndCodePoint)
            {
                return (ushort)(g.StartGlyphId + (codePoint - g.StartCodePoint));
            }
        }
        return 0;
    }

    public static CmapTable Parse(ReadOnlySpan<byte> tableBytes)
    {
        if (tableBytes.Length < 4)
        {
            throw new InvalidDataException(
                $"cmap: expected at least 4 bytes for header; got {tableBytes.Length}.");
        }
        var reader = new BigEndianReader(tableBytes);
        _ = reader.ReadUInt16(); // version (must be 0; not enforced)
        var numTables = reader.ReadUInt16();
        if (numTables == 0)
        {
            throw new InvalidDataException("cmap: numTables is 0.");
        }
        if (4 + (numTables * 8) > tableBytes.Length)
        {
            throw new InvalidDataException(
                $"cmap: table truncated for {numTables} encoding record(s).");
        }

        var encodings = new (ushort PlatformId, ushort EncodingId, uint SubtableOffset)[numTables];
        for (var i = 0; i < numTables; i++)
        {
            var platformId = reader.ReadUInt16();
            var encodingId = reader.ReadUInt16();
            var subtableOffset = reader.ReadUInt32();
            encodings[i] = (platformId, encodingId, subtableOffset);
        }

        var (chosenIndex, chosenScore) = (-1, -1);
        for (var i = 0; i < numTables; i++)
        {
            var score = ScoreEncoding(encodings[i].PlatformId, encodings[i].EncodingId);
            if (score > chosenScore)
            {
                (chosenIndex, chosenScore) = (i, score);
            }
        }
        if (chosenIndex < 0)
        {
            throw new InvalidDataException("cmap: no usable Unicode subtable found.");
        }

        var (selPlatform, selEncoding, selOffset) = encodings[chosenIndex];
        if (selOffset >= (uint)tableBytes.Length)
        {
            throw new InvalidDataException(
                $"cmap: subtable offset {selOffset} exceeds table length {tableBytes.Length}.");
        }
        var subtable = tableBytes[(int)selOffset..];
        if (subtable.Length < 2)
        {
            throw new InvalidDataException("cmap: subtable too small to read format.");
        }
        var format = BigEndianFormat(subtable);

        IReadOnlyList<CmapGroup> groups = format switch
        {
            4 => ParseFormat4(subtable),
            12 => ParseFormat12(subtable),
            _ => throw new InvalidDataException(
                $"cmap: unsupported subtable format {format}. Phase 1 supports 4 and 12 only."),
        };

        return new CmapTable
        {
            SelectedPlatformId = selPlatform,
            SelectedEncodingId = selEncoding,
            SelectedFormat = format,
            Groups = groups,
        };
    }

    private static ushort BigEndianFormat(ReadOnlySpan<byte> subtable)
        => (ushort)((subtable[0] << 8) | subtable[1]);

    /// <summary>Higher score = preferred subtable. Mirrors the priority list in the type-level remarks.</summary>
    private static int ScoreEncoding(ushort platformId, ushort encodingId) => (platformId, encodingId) switch
    {
        (3, 10) => 100,            // Microsoft Unicode UCS-4 (full Unicode)
        (0, 6) => 95,              // Unicode platform, full
        (0, 4) => 90,              // Unicode platform, full (older variant)
        (3, 1) => 80,              // Microsoft Unicode BMP
        (0, 3) => 70,              // Unicode BMP
        (0, _) => 60,              // any Unicode platform
        (3, 0) => 30,              // Microsoft Symbol — usable but punted to last resort
        _ => -1,
    };

    private static List<CmapGroup> ParseFormat4(ReadOnlySpan<byte> subtable)
    {
        var reader = new BigEndianReader(subtable);
        _ = reader.ReadUInt16();        // format = 4
        var length = reader.ReadUInt16();
        if (length > subtable.Length)
        {
            throw new InvalidDataException(
                $"cmap format 4: declared length {length} exceeds subtable length {subtable.Length}.");
        }
        _ = reader.ReadUInt16();        // language
        var segCountX2 = reader.ReadUInt16();
        if ((segCountX2 & 1) != 0)
        {
            throw new InvalidDataException(
                $"cmap format 4: segCountX2 ({segCountX2}) must be even.");
        }
        var segCount = (ushort)(segCountX2 / 2);
        _ = reader.ReadUInt16();        // searchRange
        _ = reader.ReadUInt16();        // entrySelector
        _ = reader.ReadUInt16();        // rangeShift

        var endCodes = new ushort[segCount];
        for (var i = 0; i < segCount; i++)
        {
            endCodes[i] = reader.ReadUInt16();
        }
        _ = reader.ReadUInt16();        // reservedPad (must be 0)
        var startCodes = new ushort[segCount];
        for (var i = 0; i < segCount; i++)
        {
            startCodes[i] = reader.ReadUInt16();
        }
        var idDeltas = new short[segCount];
        for (var i = 0; i < segCount; i++)
        {
            idDeltas[i] = reader.ReadInt16();
        }

        var idRangeOffsetStart = reader.Position;
        var idRangeOffsets = new ushort[segCount];
        for (var i = 0; i < segCount; i++)
        {
            idRangeOffsets[i] = reader.ReadUInt16();
        }

        var groups = new List<CmapGroup>();
        for (var i = 0; i < segCount; i++)
        {
            var start = startCodes[i];
            var end = endCodes[i];
            if (start > end)
            {
                continue; // malformed segment — skip
            }
            // Final segment in format 4 is always [0xFFFF, 0xFFFF] mapping to glyph 0; skip emitting it.
            if (start == 0xFFFF && end == 0xFFFF)
            {
                continue;
            }

            if (idRangeOffsets[i] == 0)
            {
                // glyphId = (codePoint + idDelta) mod 65536 — same delta for the whole segment
                var startGlyph = (ushort)((start + idDeltas[i]) & 0xFFFF);
                groups.Add(new CmapGroup
                {
                    StartCodePoint = start,
                    EndCodePoint = end,
                    StartGlyphId = startGlyph,
                });
            }
            else
            {
                // Per-character lookup into glyphIdArray. Expand each character into its own
                // single-character group; format 4 uses this only for non-contiguous ranges, so
                // keeping them as single-char groups stays sorted and correct.
                var rangeOffsetByteStart = idRangeOffsetStart + (i * 2);
                for (uint cp = start; cp <= end; cp++)
                {
                    var glyphIdAddress = rangeOffsetByteStart
                                         + idRangeOffsets[i]
                                         + (int)((cp - start) * 2);
                    if (glyphIdAddress + 2 > subtable.Length)
                    {
                        throw new InvalidDataException(
                            $"cmap format 4: glyphIdArray address {glyphIdAddress} exceeds subtable length.");
                    }
                    var rawGlyph = (ushort)((subtable[glyphIdAddress] << 8) | subtable[glyphIdAddress + 1]);
                    var glyphId = rawGlyph == 0
                        ? (ushort)0
                        : (ushort)((rawGlyph + idDeltas[i]) & 0xFFFF);
                    groups.Add(new CmapGroup
                    {
                        StartCodePoint = cp,
                        EndCodePoint = cp,
                        StartGlyphId = glyphId,
                    });
                }
            }
        }
        return MergeAdjacentGroups(groups);
    }

    private static List<CmapGroup> ParseFormat12(ReadOnlySpan<byte> subtable)
    {
        if (subtable.Length < 16)
        {
            throw new InvalidDataException(
                $"cmap format 12: expected at least 16 bytes for header; got {subtable.Length}.");
        }
        var reader = new BigEndianReader(subtable);
        _ = reader.ReadUInt16();        // format = 12
        _ = reader.ReadUInt16();        // reserved
        _ = reader.ReadUInt32();        // length
        _ = reader.ReadUInt32();        // language
        var numGroups = reader.ReadUInt32();
        if (16 + (long)numGroups * 12 > subtable.Length)
        {
            throw new InvalidDataException(
                $"cmap format 12: declared {numGroups} group(s) exceed subtable length {subtable.Length}.");
        }
        var groups = new List<CmapGroup>((int)numGroups);
        for (uint i = 0; i < numGroups; i++)
        {
            var startCharCode = reader.ReadUInt32();
            var endCharCode = reader.ReadUInt32();
            var startGlyphId = reader.ReadUInt32();
            if (endCharCode < startCharCode)
            {
                continue;
            }
            // Glyph IDs are 16-bit in PDF / our consumers; sanity-check overflow.
            if (startGlyphId > 0xFFFF)
            {
                continue;
            }
            groups.Add(new CmapGroup
            {
                StartCodePoint = startCharCode,
                EndCodePoint = endCharCode,
                StartGlyphId = (ushort)startGlyphId,
            });
        }
        return groups;
    }

    private static List<CmapGroup> MergeAdjacentGroups(List<CmapGroup> raw)
    {
        if (raw.Count <= 1)
        {
            return raw;
        }
        raw.Sort((a, b) => a.StartCodePoint.CompareTo(b.StartCodePoint));
        var merged = new List<CmapGroup>(raw.Count);
        var current = raw[0];
        for (var i = 1; i < raw.Count; i++)
        {
            var next = raw[i];
            var expectedNextStart = current.EndCodePoint + 1;
            var expectedGlyph = (ushort)(current.StartGlyphId + (expectedNextStart - current.StartCodePoint));
            if (next.StartCodePoint == expectedNextStart && next.StartGlyphId == expectedGlyph)
            {
                current = new CmapGroup
                {
                    StartCodePoint = current.StartCodePoint,
                    EndCodePoint = next.EndCodePoint,
                    StartGlyphId = current.StartGlyphId,
                };
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }
        merged.Add(current);
        return merged;
    }
}

/// <summary>Contiguous Unicode → glyph-id range used by both format 4 and format 12.</summary>
internal readonly struct CmapGroup
{
    public required uint StartCodePoint { get; init; }
    public required uint EndCodePoint { get; init; }
    public required ushort StartGlyphId { get; init; }
}
