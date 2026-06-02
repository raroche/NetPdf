// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;

namespace NetPdf.Pdf;

/// <summary>
/// Byte-level writer over an <see cref="IBufferWriter{T}"/> with cumulative position tracking.
/// Position is the number of bytes written so far; xref entry generation depends on it.
/// <para>
/// Optionally tees every emitted byte into an <see cref="IncrementalHash"/> so the document
/// writer can derive a content-addressed <c>/ID</c> in a single pass without buffering the
/// body. <see cref="StopHashing"/> detaches the sink before the trailer is written so the
/// hash covers only header + indirect objects + xref (the <c>/ID</c> goes inside the trailer
/// and so cannot include itself).
/// </para>
/// </summary>
internal sealed class PdfWriter
{
    /// <summary>
    /// Canonical PDF real serialization format: fixed-point, up to 6 fraction digits, no
    /// exponent, trailing zeros trimmed (ISO 32000-2 §7.3.3). Centralized so any code that
    /// must reproduce a real's exact emitted bytes (e.g. an ExtGState dedup key keyed on the
    /// serialized alpha) stays in lock-step with <see cref="WriteReal(double)"/> — the two
    /// drifting apart was the root of a /ca de-dupe collision (PR-#125 review P2).
    /// </summary>
    internal const string CanonicalRealFormat = "0.######";

    private readonly IBufferWriter<byte> _output;
    private IncrementalHash? _hashSink;

    public PdfWriter(IBufferWriter<byte> output, IncrementalHash? hashSink = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _hashSink = hashSink;
    }

    public long Position { get; private set; }

    /// <summary>Detach the hash sink. Subsequent writes are not fed into the hash.</summary>
    public void StopHashing() => _hashSink = null;

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        var dest = _output.GetSpan(bytes.Length);
        bytes.CopyTo(dest);
        _output.Advance(bytes.Length);
        Position += bytes.Length;
        _hashSink?.AppendData(bytes);
    }

    public void WriteByte(byte b)
    {
        var dest = _output.GetSpan(1);
        dest[0] = b;
        _output.Advance(1);
        Position++;
        if (_hashSink is not null)
        {
            Span<byte> single = stackalloc byte[1];
            single[0] = b;
            _hashSink.AppendData(single);
        }
    }

    /// <summary>
    /// Write the ASCII byte sequence of a string. Throws <see cref="ArgumentException"/> if
    /// any character is outside the range 0x00..0x7F — the writer fails fast rather than
    /// silently truncating. For arbitrary byte sequences (Latin-1, encoded text, image data)
    /// use <see cref="Write(ReadOnlySpan{byte})"/> instead.
    /// </summary>
    public void WriteAscii(ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty) return;
        var dest = _output.GetSpan(chars.Length);
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c > 0x7F)
            {
                throw new ArgumentException(
                    $"WriteAscii requires every character in [0x00, 0x7F]; got 0x{(int)c:X4} at index {i}. " +
                    $"Use Write(ReadOnlySpan<byte>) for non-ASCII byte sequences.",
                    nameof(chars));
            }
            dest[i] = (byte)c;
        }
        _output.Advance(chars.Length);
        Position += chars.Length;
        if (_hashSink is not null)
        {
            _hashSink.AppendData(dest[..chars.Length]);
        }
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
        if (!value.TryFormat(chars, out int written, CanonicalRealFormat, CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("Real formatting failed; this should never happen.");
        }
        WriteAscii(chars[..written]);
    }
}
