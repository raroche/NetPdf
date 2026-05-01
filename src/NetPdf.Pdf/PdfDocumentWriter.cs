// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;
using System.Security.Cryptography;
using NetPdf.Pdf.Objects;

namespace NetPdf.Pdf;

/// <summary>
/// Orchestrates writing a complete, well-formed PDF byte stream:
/// header → indirect objects → xref table → trailer → <c>startxref</c> → <c>%%EOF</c>.
/// All numbering and byte offsets are deterministic given identical input — a property test
/// asserts byte-equal output for byte-equal input.
/// <para>
/// Before any bytes are written, <see cref="PdfPreflightValidator.Validate"/> runs structural
/// checks (version is supported, all refs are assigned, /Root present and points to an
/// allocated /Catalog, no dangling refs, all generations are 0). Convention violations
/// fail at the writer's API boundary instead of producing corrupt PDFs.
/// </para>
/// </summary>
internal sealed class PdfDocumentWriter
{
    public IndirectObjectStore Objects { get; } = new();

    /// <summary>
    /// The trailer dictionary. <c>/Size</c> and <c>/ID</c> are auto-managed on emit; the
    /// caller is responsible for at minimum setting <c>/Root</c>. If <c>/ID</c> is set
    /// explicitly before <see cref="WriteTo"/>, the explicit value is preserved; otherwise
    /// it is derived from a SHA-256 of the body bytes (header + indirect objects + xref).
    /// </summary>
    public PdfDictionary Trailer { get; } = new();

    /// <summary>
    /// PDF version string emitted in the header — must be one of <see cref="PdfFormat.SupportedVersions"/>.
    /// The facade translates the public <c>PdfVersion</c> enum to this string at the API boundary
    /// so <c>NetPdf.Pdf</c> stays decoupled from the public-API project.
    /// </summary>
    public string Version { get; init; } = "1.7";

    public void WriteTo(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        PdfPreflightValidator.Validate(this);

        // If the user already provided /ID, skip auto-derivation and don't pay the hash cost.
        bool autoDeriveId = !Trailer.ContainsKey(PdfNames.ID);

        using var hash = autoDeriveId ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        var w = new PdfWriter(output, hashSink: hash);

        WriteHeader(w);
        WriteIndirectObjects(w);
        long xrefStart = w.Position;
        EnsureXrefOffsetFits(xrefStart);
        WriteXrefTable(w);

        // Build a transient trailer view: user-provided entries + auto /Size + (when needed)
        // auto /ID. We never mutate the user's Trailer, so calling WriteTo twice with body
        // mutations between calls re-derives /ID fresh both times. This also keeps WriteTo
        // exception-safe: if anything throws after this point, no leaked state.
        PdfArray? autoId = autoDeriveId ? BuildContentDerivedId(hash!) : null;

        // Trailer bytes are NOT included in the hash — /ID lives inside the trailer, so
        // including the trailer would create a chicken-and-egg dependency.
        w.StopHashing();
        WriteTrailer(w, xrefStart, autoId);
    }

    /// <summary>
    /// Build the trailer <c>/ID</c> array from the body hash. Per ISO 32000-2 §14.4 the
    /// array contains two byte strings — original-doc id and current-revision id — both
    /// equal for fresh files. NetPdf uses the first 16 bytes of SHA-256 (the conventional
    /// 128-bit ID size), encoded as <see cref="PdfHexString"/>.
    /// </summary>
    private static PdfArray BuildContentDerivedId(IncrementalHash hash)
    {
        const int IdLength = 16;
        Span<byte> digest = stackalloc byte[32];
        if (!hash.TryGetCurrentHash(digest, out int written) || written != 32)
        {
            throw new InvalidOperationException("SHA-256 incremental hash did not produce a 32-byte digest.");
        }
        var idBytes = digest[..IdLength].ToArray();
        var hex = new PdfHexString(idBytes);
        return new PdfArray().Add(hex).Add(hex);
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
        w.Write(PdfFormat.BinaryMarkerBytes);
        w.WriteNewLine();
    }

    private void WriteIndirectObjects(PdfWriter w)
    {
        var entries = Objects.AllEntries;
        for (int i = 0; i < entries.Count; i++)
        {
            int objectNumber = i + 1;
            long offset = w.Position;
            EnsureXrefOffsetFits(offset);
            Objects.RecordOffset(objectNumber, offset);

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

        WriteXrefEntry(w, byteOffset: 0, generation: PdfFormat.FreeListHeadGeneration, isInUse: false);

        for (int i = 0; i < Objects.AllEntries.Count; i++)
        {
            int objectNumber = i + 1;
            WriteXrefEntry(w, Objects.GetOffset(objectNumber), generation: 0, isInUse: true);
        }
    }

    /// <summary>
    /// Writes one xref entry as exactly <see cref="PdfFormat.XrefEntrySize"/> (20) bytes per §7.5.4:
    /// <c>nnnnnnnnnn ggggg n[space][LF]</c> — 10-digit zero-padded byte offset, single space,
    /// 5-digit zero-padded generation, single space, 'n' or 'f', single space, single LF.
    /// The trailing "space + LF" is a 2-byte EOL that satisfies the 20-byte exactness rule
    /// portably across platforms (a single LF would be off-by-one).
    /// </summary>
    private static void WriteXrefEntry(PdfWriter w, long byteOffset, int generation, bool isInUse)
    {
        const int OffsetEnd = PdfFormat.XrefOffsetDigits;            // 10
        const int GenerationStart = OffsetEnd + 1;                   // 11
        const int GenerationEnd = GenerationStart + PdfFormat.XrefGenerationDigits; // 16
        const int InUseMarker = GenerationEnd + 1;                   // 17
        const int TrailingSpace = InUseMarker + 1;                   // 18
        const int TrailingLf = TrailingSpace + 1;                    // 19

        Span<byte> buffer = stackalloc byte[PdfFormat.XrefEntrySize];
        WriteFixedDecimal(buffer[..OffsetEnd], byteOffset);
        buffer[OffsetEnd] = (byte)' ';
        WriteFixedDecimal(buffer.Slice(GenerationStart, PdfFormat.XrefGenerationDigits), generation);
        buffer[GenerationEnd] = (byte)' ';
        buffer[InUseMarker] = isInUse ? (byte)'n' : (byte)'f';
        buffer[TrailingSpace] = (byte)' ';
        buffer[TrailingLf] = (byte)'\n';
        w.Write(buffer);
    }

    /// <summary>Right-aligned, zero-padded decimal into the destination span (width = span length).</summary>
    private static void WriteFixedDecimal(Span<byte> destination, long value)
    {
        for (int i = destination.Length - 1; i >= 0; i--)
        {
            destination[i] = (byte)('0' + (value % 10));
            value /= 10;
        }
    }

    private static void EnsureXrefOffsetFits(long offset)
    {
        if (offset > PdfFormat.MaxXrefByteOffset)
        {
            throw new InvalidOperationException(
                $"Byte offset {offset} exceeds the classic xref limit of {PdfFormat.MaxXrefByteOffset} " +
                $"(10 digits). Files larger than ~10 GB require xref streams (PDF 1.5+); " +
                $"opt in via EmittedPdfVersion = V2_0 or higher and the v2 emit path.");
        }
    }

    private void WriteTrailer(PdfWriter w, long xrefStart, PdfArray? autoDerivedId)
    {
        // Build a transient view of the trailer: user entries (in their insertion order),
        // followed by the writer-managed entries (/Size always, /ID only when auto-derived).
        // The user's Trailer is never mutated, so this method is reentrant and safe to call
        // again after the body is mutated.
        var emit = new PdfDictionary();
        foreach (var entry in Trailer)
        {
            emit.Set(entry.Key, entry.Value);
        }
        emit.Set(PdfNames.Size, new PdfInteger(Objects.TotalIncludingFreeListHead));
        if (autoDerivedId is not null)
        {
            emit.Set(PdfNames.ID, autoDerivedId);
        }

        w.WriteAscii("trailer\n");
        emit.WriteTo(w);
        w.WriteAscii("\nstartxref\n");
        w.WriteInteger(xrefStart);
        w.WriteAscii("\n%%EOF\n");
    }
}
