// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;
using System.Globalization;

namespace NetPdf.Pdf;

/// <summary>
/// Byte-level writer over an <see cref="IBufferWriter{T}"/> with cumulative position tracking.
/// Position is the number of bytes written so far; xref entry generation depends on it.
/// </summary>
internal sealed class PdfWriter
{
    private readonly IBufferWriter<byte> _output;

    public PdfWriter(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    public long Position { get; private set; }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        var dest = _output.GetSpan(bytes.Length);
        bytes.CopyTo(dest);
        _output.Advance(bytes.Length);
        Position += bytes.Length;
    }

    public void WriteByte(byte b)
    {
        var dest = _output.GetSpan(1);
        dest[0] = b;
        _output.Advance(1);
        Position++;
    }

    /// <summary>
    /// Write the ASCII byte sequence of a string. The caller MUST ensure every char is in
    /// the range 0x00..0x7F; non-ASCII chars are encoded as the low byte of their UTF-16
    /// code unit, which is incorrect for PDF if not pure ASCII.
    /// </summary>
    public void WriteAscii(ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty) return;
        var dest = _output.GetSpan(chars.Length);
        for (int i = 0; i < chars.Length; i++)
        {
            dest[i] = (byte)chars[i];
        }
        _output.Advance(chars.Length);
        Position += chars.Length;
    }

    public void WriteAscii(string s) => WriteAscii(s.AsSpan());

    public void WriteSpace() => WriteByte((byte)' ');
    public void WriteNewLine() => WriteByte((byte)'\n');

    public void WriteInteger(long value)
    {
        Span<char> chars = stackalloc char[20];
        if (!value.TryFormat(chars, out int written, default, CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("Integer formatting failed; this should never happen.");
        }
        WriteAscii(chars[..written]);
    }

    /// <summary>
    /// Write a real number in canonical PDF form (no exponential notation, up to 6 fraction
    /// digits, trailing zeros trimmed). Throws on NaN/Infinity which PDF does not permit.
    /// </summary>
    public void WriteReal(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentException("PDF reals must be finite (NaN and Infinity are not permitted).", nameof(value));
        }

        Span<char> chars = stackalloc char[32];
        if (!value.TryFormat(chars, out int written, "0.######", CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("Real formatting failed; this should never happen.");
        }
        WriteAscii(chars[..written]);
    }
}
