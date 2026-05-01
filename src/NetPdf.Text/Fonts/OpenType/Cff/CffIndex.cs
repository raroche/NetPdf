// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.OpenType.Cff;

/// <summary>
/// Parsed CFF INDEX structure (Adobe Technical Note #5176 §"5 INDEX Data"). The CFF format
/// uses INDEXes to wrap variable-length collections — Name, Top DICT, String, Global/Local
/// Subr, CharStrings, FDArray.
/// </summary>
/// <remarks>
/// Wire format:
/// <list type="bullet">
/// <item>Card16 <c>count</c> — number of objects.</item>
/// <item>OffSize <c>offSize</c> — width of the offset elements (1..4 bytes), present only when <c>count != 0</c>.</item>
/// <item>Offset[count + 1] — 1-based offsets, where offsets[0] = 1 and offsets[count] points one past the end of the data.</item>
/// <item>Card8[…] — the object bytes themselves.</item>
/// </list>
/// Special case: <c>count == 0</c> serializes as just two bytes (no offSize, no offsets, no data).
/// </remarks>
internal sealed class CffIndex
{
    /// <summary>Number of objects in the INDEX.</summary>
    public required int Count { get; init; }

    /// <summary>Width of each offset element in bytes (1..4); zero for empty INDEXes.</summary>
    public required int OffSize { get; init; }

    /// <summary>1-based offset array of length <c>Count + 1</c>. Empty for empty INDEXes.</summary>
    public required uint[] Offsets { get; init; }

    /// <summary>The object data block — <c>Offsets[i] - 1</c> indexes into this span (0-based).</summary>
    public required ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>Total bytes consumed by this INDEX in the parent stream — used by the orchestrator to advance past it.</summary>
    public required int TotalSize { get; init; }

    /// <summary>True if the INDEX has zero objects and therefore no data block.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>Span over object <paramref name="index"/>'s bytes inside <see cref="Data"/>.</summary>
    public ReadOnlySpan<byte> GetObjectBytes(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index,
                $"Object index {index} out of range (Count = {Count}).");
        }
        // Offsets are 1-based per spec — subtract 1 to make them 0-based into Data.
        var start = (int)(Offsets[index] - 1);
        var end = (int)(Offsets[index + 1] - 1);
        if (start < 0 || end < start || end > Data.Length)
        {
            throw new InvalidDataException(
                $"CFF INDEX: object {index} has invalid byte range [{start}, {end}) into a data block of length {Data.Length}.");
        }
        return Data.Span[start..end];
    }

    /// <summary>
    /// Parse a CFF INDEX starting at <paramref name="bytes"/>[0]. Returns the parsed INDEX
    /// and reports the total bytes consumed (= position to resume parsing the next CFF
    /// structure from).
    /// </summary>
    public static CffIndex Parse(ReadOnlySpan<byte> bytes, ReadOnlyMemory<byte> backingMemory, int absoluteOffset)
    {
        if (bytes.Length < 2)
        {
            throw new InvalidDataException(
                $"CFF INDEX: expected at least 2 bytes for count; got {bytes.Length}.");
        }
        var count = (bytes[0] << 8) | bytes[1];
        if (count == 0)
        {
            return new CffIndex
            {
                Count = 0,
                OffSize = 0,
                Offsets = [],
                Data = ReadOnlyMemory<byte>.Empty,
                TotalSize = 2,
            };
        }
        if (bytes.Length < 3)
        {
            throw new InvalidDataException("CFF INDEX: missing offSize byte after count.");
        }
        var offSize = bytes[2];
        if (offSize is < 1 or > 4)
        {
            throw new InvalidDataException(
                $"CFF INDEX: offSize {offSize} outside spec range [1, 4].");
        }

        var offsetArrayLength = (count + 1) * offSize;
        const int headerLen = 3;
        if (bytes.Length < headerLen + offsetArrayLength)
        {
            throw new InvalidDataException(
                $"CFF INDEX: truncated. Need {headerLen + offsetArrayLength} bytes for {count + 1} offset(s) of {offSize} byte(s); got {bytes.Length}.");
        }

        var offsets = new uint[count + 1];
        for (var i = 0; i <= count; i++)
        {
            offsets[i] = ReadOffset(bytes.Slice(headerLen + (i * offSize), offSize));
        }
        if (offsets[0] != 1)
        {
            throw new InvalidDataException(
                $"CFF INDEX: spec requires offsets[0] == 1; got {offsets[0]}.");
        }
        for (var i = 1; i <= count; i++)
        {
            if (offsets[i] < offsets[i - 1])
            {
                throw new InvalidDataException(
                    $"CFF INDEX: offsets must be non-decreasing; offsets[{i}] = {offsets[i]} < offsets[{i - 1}] = {offsets[i - 1]}.");
            }
        }

        var dataStart = headerLen + offsetArrayLength;
        var dataLength = (int)(offsets[count] - 1);
        if (bytes.Length < dataStart + dataLength)
        {
            throw new InvalidDataException(
                $"CFF INDEX: data block truncated. Need {dataLength} bytes; got {bytes.Length - dataStart}.");
        }

        var totalSize = dataStart + dataLength;
        var dataMemory = backingMemory.Slice(absoluteOffset + dataStart, dataLength);

        return new CffIndex
        {
            Count = count,
            OffSize = offSize,
            Offsets = offsets,
            Data = dataMemory,
            TotalSize = totalSize,
        };
    }

    private static uint ReadOffset(ReadOnlySpan<byte> slice) => slice.Length switch
    {
        1 => slice[0],
        2 => (uint)((slice[0] << 8) | slice[1]),
        3 => (uint)((slice[0] << 16) | (slice[1] << 8) | slice[2]),
        4 => ((uint)slice[0] << 24) | ((uint)slice[1] << 16) | ((uint)slice[2] << 8) | slice[3],
        _ => throw new InvalidOperationException($"Unreachable: offset width {slice.Length}."),
    };
}
