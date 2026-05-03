// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;

namespace NetPdf.Text.Fonts.OpenType;

/// <summary>
/// Big-endian reader over a <see cref="ReadOnlySpan{Byte}"/>. OpenType / TrueType data is
/// big-endian throughout (OpenType spec §"Data Types"), and most parser hot paths fit a
/// few hundred reads — a <c>ref struct</c> keeps everything stack-resident.
/// </summary>
/// <remarks>
/// Each read advances <see cref="Position"/> by the read width. Out-of-bounds reads throw
/// <see cref="InvalidDataException"/>; OpenType parsers should never silently truncate.
/// </remarks>
internal ref struct BigEndianReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public BigEndianReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public readonly int Position => _position;
    public readonly int Length => _buffer.Length;
    public readonly int Remaining => _buffer.Length - _position;
    public readonly bool IsAtEnd => _position >= _buffer.Length;

    public byte ReadUInt8()
    {
        EnsureRemaining(1);
        return _buffer[_position++];
    }

    public sbyte ReadInt8() => unchecked((sbyte)ReadUInt8());

    public ushort ReadUInt16()
    {
        EnsureRemaining(2);
        var v = BinaryPrimitives.ReadUInt16BigEndian(_buffer[_position..]);
        _position += 2;
        return v;
    }

    public short ReadInt16()
    {
        EnsureRemaining(2);
        var v = BinaryPrimitives.ReadInt16BigEndian(_buffer[_position..]);
        _position += 2;
        return v;
    }

    public uint ReadUInt32()
    {
        EnsureRemaining(4);
        var v = BinaryPrimitives.ReadUInt32BigEndian(_buffer[_position..]);
        _position += 4;
        return v;
    }

    public int ReadInt32()
    {
        EnsureRemaining(4);
        var v = BinaryPrimitives.ReadInt32BigEndian(_buffer[_position..]);
        _position += 4;
        return v;
    }

    public long ReadInt64()
    {
        EnsureRemaining(8);
        var v = BinaryPrimitives.ReadInt64BigEndian(_buffer[_position..]);
        _position += 8;
        return v;
    }

    /// <summary>Reads <paramref name="count"/> bytes and returns a sub-span over the source buffer.</summary>
    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        EnsureRemaining(count);
        var slice = _buffer.Slice(_position, count);
        _position += count;
        return slice;
    }

    public void Skip(int count)
    {
        EnsureRemaining(count);
        _position += count;
    }

    /// <summary>Set the absolute read position. Bounds-checked.</summary>
    public void Seek(int absolute)
    {
        if ((uint)absolute > (uint)_buffer.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(absolute), absolute,
                $"Seek out of bounds (buffer length = {_buffer.Length}).");
        }
        _position = absolute;
    }

    /// <summary>Random-access slice over the source buffer; does not move the position.</summary>
    public readonly ReadOnlySpan<byte> Slice(int start, int length)
    {
        if ((uint)start > (uint)_buffer.Length || (uint)length > (uint)(_buffer.Length - start))
        {
            throw new ArgumentOutOfRangeException(
                nameof(start),
                $"Slice [{start}, {start + length}) out of bounds (buffer length = {_buffer.Length}).");
        }
        return _buffer.Slice(start, length);
    }

    private readonly void EnsureRemaining(int required)
    {
        if (_position + required > _buffer.Length)
        {
            throw new InvalidDataException(
                $"OpenType parse: needed {required} byte(s) at position {_position}, " +
                $"but buffer has {_buffer.Length} (remaining {_buffer.Length - _position}).");
        }
    }
}
