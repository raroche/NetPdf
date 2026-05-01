// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections;

namespace NetPdf.Pdf.Objects;

/// <summary>
/// ISO 32000-2 §7.3.6 — array object. Emits <c>[ a b c ]</c>. Insertion order is preserved.
/// </summary>
internal sealed class PdfArray : PdfObject, IEnumerable<PdfObject>
{
    private readonly List<PdfObject> _items = new();

    public PdfArray() { }

    public PdfArray(IEnumerable<PdfObject> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.AddRange(items);
    }

    public int Count => _items.Count;

    public PdfObject this[int index] => _items[index];

    public PdfArray Add(PdfObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
        return this;
    }

    public IEnumerator<PdfObject> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override void WriteTo(PdfWriter writer)
    {
        writer.WriteByte((byte)'[');
        for (int i = 0; i < _items.Count; i++)
        {
            if (i > 0) writer.WriteSpace();
            _items[i].WriteTo(writer);
        }
        writer.WriteByte((byte)']');
    }
}
