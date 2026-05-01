// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf.Objects;

/// <summary>
/// ISO 32000-2 §7.3.8 — stream object. The stream's dictionary precedes the keyword
/// <c>stream</c>, then the (already-encoded) byte payload, then <c>endstream</c>.
/// <c>/Length</c> is set automatically from the payload byte count.
/// </summary>
internal sealed class PdfStream : PdfObject
{
    private readonly byte[] _data;

    public PdfDictionary Dictionary { get; }

    /// <summary>The encoded byte payload (post-filter). Read-only access.</summary>
    public ReadOnlySpan<byte> Data => _data;

    public PdfStream(byte[] data, PdfDictionary? dictionary = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        Dictionary = dictionary ?? new PdfDictionary();
        Dictionary.Set(PdfNames.Length, new PdfInteger(_data.Length));
    }

    public override void WriteTo(PdfWriter writer)
    {
        // Re-establish the /Length invariant. Stream length is intrinsic to the payload
        // bytes; if the user mutated /Length between construction and emit (intentionally
        // or by accident — e.g., dictionary cleared, /Filter swapped, /Length set wrong)
        // this restores correctness silently. There is no legitimate reason a caller
        // should override /Length, so we always trust the payload size.
        Dictionary.Set(PdfNames.Length, new PdfInteger(_data.Length));

        Dictionary.WriteTo(writer);
        writer.WriteNewLine();
        writer.WriteAscii("stream\n");
        writer.Write(_data);
        // Spec recommends a newline before endstream when not already present.
        if (_data.Length == 0 || _data[^1] != (byte)'\n')
        {
            writer.WriteNewLine();
        }
        writer.WriteAscii("endstream");
    }

    public override IEnumerable<PdfObject> EnumerateChildren()
    {
        yield return Dictionary;
    }
}
