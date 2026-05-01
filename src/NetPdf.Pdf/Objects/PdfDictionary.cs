// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections;

namespace NetPdf.Pdf.Objects;

/// <summary>
/// ISO 32000-2 §7.3.7 — dictionary object. Emits <c>&lt;&lt; /Key value /Key2 value2 &gt;&gt;</c>.
/// Insertion order is preserved (via <see cref="OrderedDictionary{TKey,TValue}"/>) so byte
/// output is deterministic given identical insertion sequence.
/// </summary>
internal sealed class PdfDictionary : PdfObject, IEnumerable<KeyValuePair<PdfName, PdfObject>>
{
    private readonly OrderedDictionary<PdfName, PdfObject> _entries = new();

    public PdfDictionary() { }

    public int Count => _entries.Count;

    public bool ContainsKey(PdfName key) => _entries.ContainsKey(key);

    public PdfObject? Get(PdfName key) => _entries.TryGetValue(key, out var v) ? v : null;

    public PdfDictionary Set(PdfName key, PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        _entries[key] = value;
        return this;
    }

    public bool Remove(PdfName key) => _entries.Remove(key);

    public IEnumerator<KeyValuePair<PdfName, PdfObject>> GetEnumerator() => _entries.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override void WriteTo(PdfWriter writer)
    {
        writer.WriteAscii("<<");
        foreach (var entry in _entries)
        {
            writer.WriteSpace();
            entry.Key.WriteTo(writer);
            writer.WriteSpace();
            entry.Value.WriteTo(writer);
        }
        writer.WriteAscii(" >>");
    }
}
