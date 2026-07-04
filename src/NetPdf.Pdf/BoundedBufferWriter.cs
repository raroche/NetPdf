// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Buffers;

namespace NetPdf.Pdf;

/// <summary>
/// SEC-5 — an <see cref="IBufferWriter{T}"/> that forwards to an inner writer but aborts once more than
/// <c>maxBytes</c> have been advanced, so an oversized PDF is stopped <b>during</b> serialization (before
/// the full buffer is grown + the final <c>ToArray()</c> copy) rather than measured after
/// <see cref="PdfDocument.Save()"/> has already materialized it. Throws
/// <see cref="PdfOutputSizeExceededException"/> on the write that crosses the cap.
/// </summary>
internal sealed class BoundedBufferWriter(IBufferWriter<byte> inner, long maxBytes) : IBufferWriter<byte>
{
    private long _written;

    /// <summary>Total bytes advanced so far (never exceeds <c>maxBytes</c> — the throw happens on the
    /// crossing write, before the inner writer is advanced).</summary>
    public long Written => _written;

    public void Advance(int count)
    {
        if (_written + count > maxBytes)
        {
            throw new PdfOutputSizeExceededException(maxBytes);
        }

        _written += count;
        inner.Advance(count);
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

    public Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
}

/// <summary>SEC-5 — thrown by <see cref="BoundedBufferWriter"/> when serialization exceeds the configured
/// output-size cap; the render pipeline maps it to <c>HtmlPdfException(PDF-OUTPUT-SIZE-EXCEEDED-001)</c>.</summary>
internal sealed class PdfOutputSizeExceededException(long maxBytes)
    : Exception($"PDF output exceeded the configured cap of {maxBytes} bytes during serialization.")
{
    public long MaxBytes { get; } = maxBytes;
}
