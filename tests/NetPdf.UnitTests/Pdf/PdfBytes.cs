// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;
using System.Text;
using NetPdf.Pdf;
using NetPdf.Pdf.Objects;

namespace NetPdf.UnitTests.Pdf;

/// <summary>Test helper: render a <see cref="PdfObject"/> to bytes / ASCII.</summary>
internal static class PdfBytes
{
    public static byte[] Render(PdfObject obj)
    {
        var buf = new ArrayBufferWriter<byte>();
        var writer = new PdfWriter(buf);
        obj.WriteTo(writer);
        return buf.WrittenSpan.ToArray();
    }

    public static string Ascii(PdfObject obj) => Encoding.ASCII.GetString(Render(obj));

    public static long PositionAfter(PdfObject obj)
    {
        var buf = new ArrayBufferWriter<byte>();
        var writer = new PdfWriter(buf);
        obj.WriteTo(writer);
        return writer.Position;
    }
}
