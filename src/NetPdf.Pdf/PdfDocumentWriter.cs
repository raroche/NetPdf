// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;
using NetPdf.Pdf.Objects;

namespace NetPdf.Pdf;

/// <summary>
/// Orchestrates writing a complete, well-formed PDF byte stream:
/// header → indirect objects → xref table → trailer → <c>startxref</c> → <c>%%EOF</c>.
/// All numbering and byte offsets are deterministic given identical input — a property test
/// asserts byte-equal output for byte-equal input.
/// </summary>
internal sealed class PdfDocumentWriter
{
    public IndirectObjectStore Objects { get; } = new();

    /// <summary>The trailer dictionary. <c>/Size</c> is auto-set on emit; the caller is
    /// responsible for at minimum setting <c>/Root</c> (and typically <c>/Info</c>, <c>/ID</c>).</summary>
    public PdfDictionary Trailer { get; } = new();

    /// <summary>
    /// PDF version string emitted in the header (e.g., <c>"1.7"</c>, <c>"2.0"</c>). The facade
    /// translates the public <c>PdfVersion</c> enum to this string at the API boundary so the
    /// writer stays decoupled from the public-API project.
    /// </summary>
    public string Version { get; init; } = "1.7";

    public void WriteTo(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        Objects.ValidateAllAssigned();

        var w = new PdfWriter(output);
        WriteHeader(w);
        WriteIndirectObjects(w);
        long xrefStart = w.Position;
        WriteXrefTable(w);
        WriteTrailer(w, xrefStart);
    }

    private void WriteHeader(PdfWriter w)
    {
        // %PDF-X.Y\n
        w.WriteAscii("%PDF-");
        w.WriteAscii(Version);
        w.WriteNewLine();

        // §7.5.2 binary marker comment: a comment containing 4 bytes >= 0x80 marks the file
        // as binary. Without it, transports that handle ASCII (e.g., FTP ASCII mode) might
        // mangle the file. Bytes chosen to match Adobe's canonical example.
        w.WriteByte((byte)'%');
        w.WriteByte(0xE2);
        w.WriteByte(0xE3);
        w.WriteByte(0xCF);
        w.WriteByte(0xD3);
        w.WriteNewLine();
    }

    private void WriteIndirectObjects(PdfWriter w)
    {
        var entries = Objects.AllEntries;
        for (int i = 0; i < entries.Count; i++)
        {
            int objectNumber = i + 1;
            Objects.RecordOffset(objectNumber, w.Position);

            // "<n> 0 obj\n<body>\nendobj\n"
            w.WriteInteger(objectNumber);
            w.WriteAscii(" 0 obj\n");
            entries[i].Object!.WriteTo(w);
            w.WriteAscii("\nendobj\n");
        }
    }

    private void WriteXrefTable(PdfWriter w)
    {
        // §7.5.4 — xref keyword, subsection header "0 N", N entries each exactly 20 bytes.
        w.WriteAscii("xref\n");
        w.WriteAscii("0 ");
        w.WriteInteger(Objects.TotalIncludingFreeListHead);
        w.WriteNewLine();

        // Object 0: free-list head, generation 65535, marker 'f'.
        WriteXrefEntry(w, byteOffset: 0, generation: 65535, isInUse: false);

        // Real objects, in numeric order (= allocation order).
        for (int i = 0; i < Objects.AllEntries.Count; i++)
        {
            int objectNumber = i + 1;
            WriteXrefEntry(w, Objects.GetOffset(objectNumber), generation: 0, isInUse: true);
        }
    }

    /// <summary>
    /// Writes one xref entry as exactly 20 bytes per §7.5.4:
    /// <c>nnnnnnnnnn ggggg n[space][LF]</c> — 10-digit zero-padded byte offset, single space,
    /// 5-digit zero-padded generation, single space, 'n' or 'f', single space, single LF.
    /// The trailing "space + LF" is a 2-byte EOL that satisfies the 20-byte exactness rule
    /// portably across platforms (a CRLF or single LF would be off-by-one).
    /// </summary>
    private static void WriteXrefEntry(PdfWriter w, long byteOffset, int generation, bool isInUse)
    {
        Span<byte> buffer = stackalloc byte[20];
        WriteFixedDecimal(buffer[..10], byteOffset, 10);
        buffer[10] = (byte)' ';
        WriteFixedDecimal(buffer.Slice(11, 5), generation, 5);
        buffer[16] = (byte)' ';
        buffer[17] = isInUse ? (byte)'n' : (byte)'f';
        buffer[18] = (byte)' ';
        buffer[19] = (byte)'\n';
        w.Write(buffer);
    }

    /// <summary>Right-aligned, zero-padded decimal into a fixed-width span.</summary>
    private static void WriteFixedDecimal(Span<byte> destination, long value, int digits)
    {
        for (int i = digits - 1; i >= 0; i--)
        {
            destination[i] = (byte)('0' + (value % 10));
            value /= 10;
        }
    }

    private void WriteTrailer(PdfWriter w, long xrefStart)
    {
        // /Size is auto-managed: total objects including the free-list head.
        Trailer.Set(PdfNames.Size, new PdfInteger(Objects.TotalIncludingFreeListHead));

        w.WriteAscii("trailer\n");
        Trailer.WriteTo(w);
        w.WriteAscii("\nstartxref\n");
        w.WriteInteger(xrefStart);
        w.WriteAscii("\n%%EOF\n");
    }

}
