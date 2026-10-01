// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace NetPdf.Text.Fonts.OpenType;

/// <summary>
/// Reader for OpenType font collections (<c>.ttc</c> / <c>.otc</c>, OpenType §"Font Collections"):
/// a <c>ttcf</c> header, then one offset per face to that face's own table directory. Faces share
/// table data inside the file (directory offsets are relative to the start of the FILE, not the face).
/// </summary>
/// <remarks>
/// <para>
/// <b>Extraction model.</b> The rest of NetPdf (the safety validator, <see cref="OpenTypeFont.Parse"/>,
/// HarfBuzz, the PDF subsetter) works on one standalone sfnt. <see cref="ExtractFace(string, int)"/>
/// copies the chosen face's tables into a new single-font sfnt (sorted directory, 4-byte-aligned tables,
/// recomputed <c>head.checkSumAdjustment</c>), so those paths need no collection awareness.
/// </para>
/// <para>
/// <b>Bounded reads.</b> System collections can be large (CJK and emoji collections pass 50 MB), so the
/// file path reads with random access: only the header, the face directories, and the tables that a
/// caller asks for. Every offset and length is bounds-checked against the file length, the face count is
/// capped at <see cref="MaxFaces"/>, the table count at <see cref="FontSafetyValidator.MaxTableCount"/>,
/// and an extracted face at <see cref="FontSafetyValidator.MaxBytes"/>.
/// </para>
/// </remarks>
internal static class FontCollection
{
    /// <summary>The <c>ttcf</c> collection tag.</summary>
    public const uint CollectionTag = 0x74746366u;

    /// <summary>Upper bound on faces per collection (real system collections hold well under 100).</summary>
    public const int MaxFaces = 256;

    /// <summary>True when <paramref name="bytes"/> starts with the <c>ttcf</c> collection tag.</summary>
    public static bool IsCollection(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(bytes) == CollectionTag;

    /// <summary>One face's table directory inside a collection.</summary>
    internal sealed record FaceDirectory(uint SfntVersion, TableRecord[] Tables)
    {
        /// <summary>Find a table record by tag.</summary>
        public bool TryGet(uint tag, out TableRecord record)
        {
            foreach (var t in Tables)
            {
                if (t.Tag == tag)
                {
                    record = t;
                    return true;
                }
            }
            record = default;
            return false;
        }
    }

    /// <summary>Random-access view of a collection — an in-memory buffer or an open file.</summary>
    internal readonly struct Source
    {
        private readonly ReadOnlyMemory<byte> _memory;
        private readonly SafeFileHandle? _file;

        private Source(ReadOnlyMemory<byte> memory, SafeFileHandle? file, long length)
        {
            _memory = memory;
            _file = file;
            Length = length;
        }

        /// <summary>Total byte length of the collection.</summary>
        public long Length { get; }

        /// <summary>A source over an in-memory collection.</summary>
        public static Source FromMemory(ReadOnlyMemory<byte> bytes) => new(bytes, null, bytes.Length);

        /// <summary>A source over an open file (the caller owns the handle).</summary>
        public static Source FromFile(SafeFileHandle file) => new(default, file, RandomAccess.GetLength(file));

        /// <summary>Fill <paramref name="destination"/> from <paramref name="offset"/>; throws when the
        /// range is outside the collection.</summary>
        public void ReadExactly(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset + destination.Length > Length)
            {
                throw new InvalidDataException(
                    $"Font collection: read of {destination.Length} byte(s) at offset {offset} is outside the file ({Length} bytes).");
            }
            if (_file is null)
            {
                _memory.Span.Slice((int)offset, destination.Length).CopyTo(destination);
                return;
            }
            var done = 0;
            while (done < destination.Length)
            {
                var n = RandomAccess.Read(_file, destination[done..], offset + done);
                if (n <= 0) throw new InvalidDataException("Font collection: unexpected end of file.");
                done += n;
            }
        }

        /// <summary>Read one table's bytes.</summary>
        public byte[] ReadTable(TableRecord record)
        {
            var bytes = new byte[record.Length];
            ReadExactly(record.Offset, bytes);
            return bytes;
        }
    }

    /// <summary>Read the collection header and return the offset of each face's table directory.</summary>
    public static uint[] ReadFaceOffsets(in Source source)
    {
        Span<byte> header = stackalloc byte[12];
        source.ReadExactly(0, header);
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != CollectionTag)
            throw new InvalidDataException("Font collection: missing 'ttcf' tag.");
        var major = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        if (major is not (1 or 2))
            throw new InvalidDataException($"Font collection: unsupported header version {major}.");
        var numFonts = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        if (numFonts is 0 or > MaxFaces)
            throw new InvalidDataException($"Font collection: face count {numFonts} is outside 1..{MaxFaces}.");

        var raw = new byte[numFonts * 4];
        source.ReadExactly(12, raw);
        var offsets = new uint[numFonts];
        for (var i = 0; i < offsets.Length; i++)
            offsets[i] = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(i * 4));
        return offsets;
    }

    /// <summary>Read and bounds-check the table directory that starts at <paramref name="offset"/>.</summary>
    public static FaceDirectory ReadFaceDirectory(in Source source, uint offset)
    {
        Span<byte> header = stackalloc byte[12];
        source.ReadExactly(offset, header);
        var sfntVersion = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (sfntVersion is not (OpenTypeTags.SfntVersionTtf or OpenTypeTags.SfntVersionOtf or OpenTypeTags.SfntVersionAppleTrue))
            throw new InvalidDataException($"Font collection: face at {offset} has unrecognized sfnt version 0x{sfntVersion:X8}.");
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        if (numTables is 0 or > FontSafetyValidator.MaxTableCount)
            throw new InvalidDataException(
                $"Font collection: face at {offset} declares {numTables} tables (allowed 1..{FontSafetyValidator.MaxTableCount}).");

        var raw = new byte[numTables * 16];
        source.ReadExactly((long)offset + 12, raw);
        var tables = new TableRecord[numTables];
        for (var i = 0; i < numTables; i++)
        {
            var r = raw.AsSpan(i * 16, 16);
            var record = new TableRecord
            {
                Tag = BinaryPrimitives.ReadUInt32BigEndian(r),
                Checksum = BinaryPrimitives.ReadUInt32BigEndian(r[4..]),
                Offset = BinaryPrimitives.ReadUInt32BigEndian(r[8..]),
                Length = BinaryPrimitives.ReadUInt32BigEndian(r[12..]),
            };
            if ((long)record.Offset + record.Length > source.Length)
                throw new InvalidDataException(
                    $"Font collection: table '{OpenTypeTags.ToAsciiString(record.Tag)}' (offset {record.Offset}, length {record.Length}) extends past the file ({source.Length} bytes).");
            for (var j = 0; j < i; j++)
            {
                if (tables[j].Tag == record.Tag)
                    throw new InvalidDataException(
                        $"Font collection: duplicate table tag '{OpenTypeTags.ToAsciiString(record.Tag)}'.");
            }
            tables[i] = record;
        }
        // The sfnt directory must be sorted by tag (OpenType §"Table directory").
        Array.Sort(tables, static (a, b) => a.Tag.CompareTo(b.Tag));
        return new FaceDirectory(sfntVersion, tables);
    }

    /// <summary>Byte size of the standalone sfnt that <see cref="ExtractFace(in Source, int)"/> would build.</summary>
    public static long StandaloneSize(FaceDirectory face)
    {
        long size = 12 + 16L * face.Tables.Length;
        foreach (var t in face.Tables) size += Align4(t.Length);
        return size;
    }

    /// <summary>Copy face <paramref name="faceIndex"/> of the collection file at <paramref name="path"/>
    /// into a standalone single-font sfnt.</summary>
    public static byte[] ExtractFace(string path, int faceIndex)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        return ExtractFace(Source.FromFile(handle), faceIndex);
    }

    /// <summary>Copy face <paramref name="faceIndex"/> of an in-memory collection into a standalone sfnt.</summary>
    public static byte[] ExtractFace(ReadOnlyMemory<byte> collection, int faceIndex) =>
        ExtractFace(Source.FromMemory(collection), faceIndex);

    /// <summary>Copy face <paramref name="faceIndex"/> into a standalone single-font sfnt: the same
    /// sfnt version and tables, a fresh tag-sorted directory, each table 4-byte aligned, and
    /// <c>head.checkSumAdjustment</c> recomputed for the new file.</summary>
    public static byte[] ExtractFace(in Source source, int faceIndex)
    {
        var offsets = ReadFaceOffsets(source);
        if ((uint)faceIndex >= (uint)offsets.Length)
            throw new ArgumentOutOfRangeException(nameof(faceIndex), faceIndex,
                $"Font collection has {offsets.Length} face(s).");
        var face = ReadFaceDirectory(source, offsets[faceIndex]);
        var size = StandaloneSize(face);
        if (size > FontSafetyValidator.MaxBytes)
            throw new InvalidDataException(
                $"Font collection: face {faceIndex} is {size} bytes, over the {FontSafetyValidator.MaxBytes / (1024 * 1024)} MiB cap.");

        var output = new byte[size];
        var span = output.AsSpan();
        var numTables = (ushort)face.Tables.Length;
        // searchRange / entrySelector / rangeShift (OpenType §"Table directory").
        var entrySelector = (ushort)Math.Floor(Math.Log2(numTables));
        var searchRange = (ushort)((1 << entrySelector) * 16);
        BinaryPrimitives.WriteUInt32BigEndian(span, face.SfntVersion);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], numTables);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)(numTables * 16 - searchRange));

        var dataOffset = 12 + 16 * numTables;
        var headOffset = -1;
        for (var i = 0; i < numTables; i++)
        {
            var t = face.Tables[i];
            var rec = span.Slice(12 + i * 16, 16);
            BinaryPrimitives.WriteUInt32BigEndian(rec, t.Tag);
            BinaryPrimitives.WriteUInt32BigEndian(rec[4..], t.Checksum);
            BinaryPrimitives.WriteUInt32BigEndian(rec[8..], (uint)dataOffset);
            BinaryPrimitives.WriteUInt32BigEndian(rec[12..], t.Length);
            source.ReadExactly(t.Offset, span.Slice(dataOffset, (int)t.Length));
            if (t.Tag == OpenTypeTags.Head && t.Length >= 12) headOffset = dataOffset;
            dataOffset += (int)Align4(t.Length);
        }

        // head.checkSumAdjustment (offset 8): 0xB1B0AFBA minus the sum of the whole file computed
        // with the field set to 0. The collection's value was for the collection, not this face.
        if (headOffset >= 0)
        {
            var adjustment = span.Slice(headOffset + 8, 4);
            adjustment.Clear();
            BinaryPrimitives.WriteUInt32BigEndian(adjustment, unchecked(0xB1B0AFBAu - Checksum(span)));
        }
        return output;
    }

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        // Every table is 4-byte aligned and zero padded, so the file length is a multiple of 4.
        uint sum = 0;
        for (var i = 0; i + 4 <= data.Length; i += 4)
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data[i..]));
        return sum;
    }

    private static long Align4(uint length) => (length + 3L) & ~3L;
}
