// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;

namespace NetPdf.Pdf.Objects;

/// <summary>
/// ISO 32000-2 §7.3.4.3 — hex string. Emits <c>&lt;DEADBEEF&gt;</c>. Each input byte becomes
/// two upper-case hex digits. Use this for any byte sequence; preferred over
/// <see cref="PdfLiteralString"/> for non-ASCII content (no escaping needed).
/// </summary>
internal sealed class PdfHexString : PdfObject
{
    private readonly byte[] _bytes;

    public PdfHexString(ReadOnlySpan<byte> bytes) => _bytes = bytes.ToArray();

    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>UTF-16BE encoding of <paramref name="text"/> with BOM (FEFF) per PDF
    /// convention for textual metadata strings (Title, Author, etc.).</summary>
    public static PdfHexString FromUtf16BeWithBom(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var encoded = Encoding.BigEndianUnicode.GetBytes(text);
        var buf = new byte[encoded.Length + 2];
        buf[0] = 0xFE;
        buf[1] = 0xFF;
        encoded.CopyTo(buf, 2);
        return new PdfHexString(buf);
    }

    public override void WriteTo(PdfWriter writer)
    {
        writer.WriteByte((byte)'<');
        foreach (byte b in _bytes)
        {
            writer.WriteByte(Hex.HighNibble(b));
            writer.WriteByte(Hex.LowNibble(b));
        }
        writer.WriteByte((byte)'>');
    }
}
