// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf.Objects;

/// <summary>
/// ISO 32000-2 §7.3.5 — name object. Emits <c>/Name</c> with hex-escaping (<c>#XX</c>) for any
/// byte outside 0x21..0x7E or that is a delimiter. Names are case-sensitive and value-equal.
/// </summary>
internal sealed class PdfName : PdfObject, IEquatable<PdfName>
{
    public string Value { get; }

    public PdfName(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("PDF name cannot be null or empty.", nameof(value));
        }
        Value = value;
    }

    public override void WriteTo(PdfWriter writer)
    {
        writer.WriteByte((byte)'/');
        foreach (char c in Value)
        {
            if (c > 0xFF)
            {
                throw new InvalidOperationException(
                    "PDF name characters must be in the byte range; UTF-8 encoding for names is not yet supported.");
            }
            byte b = (byte)c;
            if (NeedsEscape(b))
            {
                writer.WriteByte((byte)'#');
                writer.WriteByte(Hex.HighNibble(b));
                writer.WriteByte(Hex.LowNibble(b));
            }
            else
            {
                writer.WriteByte(b);
            }
        }
    }

    private static bool NeedsEscape(byte b) =>
        // Outside printable ASCII
        b < 0x21 || b > 0x7E ||
        // Delimiters per §7.2.3
        b == (byte)'(' || b == (byte)')' || b == (byte)'<' || b == (byte)'>' ||
        b == (byte)'[' || b == (byte)']' || b == (byte)'{' || b == (byte)'}' ||
        b == (byte)'/' || b == (byte)'%' ||
        // # itself must be escaped because it's the escape introducer
        b == (byte)'#';

    public bool Equals(PdfName? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => Equals(obj as PdfName);
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);
    public override string ToString() => "/" + Value;
}
