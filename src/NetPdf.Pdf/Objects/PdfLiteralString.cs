// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;

namespace NetPdf.Pdf.Objects;

/// <summary>
/// ISO 32000-2 §7.3.4.2 — literal string. Emits <c>(...)</c> with backslash-escaped
/// parens, backslashes, and bytes &lt; 0x20 / &gt; 0x7E rendered as 3-digit octal escapes.
/// PDF literal strings are byte sequences, not Unicode strings; non-ASCII text should be
/// passed in pre-encoded (UTF-16BE for metadata, etc.) or use <see cref="PdfHexString"/>.
/// </summary>
internal sealed class PdfLiteralString : PdfObject
{
    private readonly byte[] _bytes;

    public PdfLiteralString(ReadOnlySpan<byte> bytes) => _bytes = bytes.ToArray();

    /// <summary>Construct from an ASCII string. Throws if any character is &gt; 0x7E.</summary>
    public PdfLiteralString(string ascii)
    {
        ArgumentNullException.ThrowIfNull(ascii);
        foreach (char c in ascii)
        {
            if (c > 0x7E)
            {
                throw new ArgumentException(
                    "Non-ASCII character; pass pre-encoded bytes or use PdfHexString.", nameof(ascii));
            }
        }
        _bytes = Encoding.ASCII.GetBytes(ascii);
    }

    public ReadOnlySpan<byte> Bytes => _bytes;

    public override void WriteTo(PdfWriter writer)
    {
        writer.WriteByte((byte)'(');
        foreach (byte b in _bytes)
        {
            switch (b)
            {
                case (byte)'(':  writer.WriteAscii(@"\("); break;
                case (byte)')':  writer.WriteAscii(@"\)"); break;
                case (byte)'\\': writer.WriteAscii(@"\\"); break;
                case (byte)'\n': writer.WriteAscii(@"\n"); break;
                case (byte)'\r': writer.WriteAscii(@"\r"); break;
                case (byte)'\t': writer.WriteAscii(@"\t"); break;
                case (byte)'\b': writer.WriteAscii(@"\b"); break;
                case (byte)'\f': writer.WriteAscii(@"\f"); break;
                default:
                    if (b < 0x20 || b > 0x7E)
                    {
                        // 3-digit octal escape \ddd
                        writer.WriteByte((byte)'\\');
                        writer.WriteByte((byte)('0' + ((b >> 6) & 0x07)));
                        writer.WriteByte((byte)('0' + ((b >> 3) & 0x07)));
                        writer.WriteByte((byte)('0' + (b & 0x07)));
                    }
                    else
                    {
                        writer.WriteByte(b);
                    }
                    break;
            }
        }
        writer.WriteByte((byte)')');
    }
}
