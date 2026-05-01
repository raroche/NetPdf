// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;
using System.IO.Compression;
using NetPdf.Pdf.Objects;

namespace NetPdf.Pdf.Content;

/// <summary>
/// Convenience facade: build a <see cref="PdfStream"/> from a callback that emits content
/// stream operators via <see cref="ContentStreamWriter"/>. The builder owns the buffer,
/// drives <see cref="ContentStreamWriter.Finish"/>, and optionally applies FlateDecode
/// (zlib) compression.
/// </summary>
internal static class ContentStreamBuilder
{
    /// <summary>
    /// Run <paramref name="body"/> against an <see cref="IContentStream"/> and wrap the
    /// resulting bytes in a <see cref="PdfStream"/>. The callback sees only the operator
    /// surface — lifecycle (<c>Finish</c>) is owned by the builder, so callbacks cannot
    /// double-finalize and create a downstream throw. When <paramref name="compress"/> is
    /// true the bytes are zlib-deflated and <c>/Filter /FlateDecode</c> is set on the dictionary.
    /// </summary>
    public static PdfStream Build(Action<IContentStream> body, bool compress = false)
    {
        ArgumentNullException.ThrowIfNull(body);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PdfWriter(buffer);
        var content = new ContentStreamWriter(writer);

        body(content);
        content.Finish();

        var raw = buffer.WrittenSpan.ToArray();

        if (!compress)
        {
            return new PdfStream(raw);
        }

        var compressed = Deflate(raw);
        var dictionary = new PdfDictionary().Set(PdfNames.Filter, PdfNames.FlateDecode);
        return new PdfStream(compressed, dictionary);
    }

    /// <summary>
    /// zlib-deflate <paramref name="data"/> per the <c>FlateDecode</c> filter (§7.4.4).
    /// Uses <see cref="ZLibStream"/> so the output includes the zlib header and Adler-32
    /// trailer that PDF readers expect — raw <see cref="DeflateStream"/> output would not
    /// be decodable.
    /// </summary>
    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }
}
