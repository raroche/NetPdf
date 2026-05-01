// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Objects;

namespace NetPdf.Pdf;

/// <summary>
/// Owns indirect-object allocation and the byte offsets needed for the xref table.
/// Object numbers are 1-based and assigned in allocation order, giving deterministic
/// numbering for byte-equal output. Object 0 is the implicit free-list head and is
/// emitted by <see cref="PdfDocumentWriter"/>; this store does not represent it.
/// </summary>
internal sealed class IndirectObjectStore
{
    private readonly List<Entry> _entries = new();

    /// <summary>Number of real (non-free-list-head) objects allocated.</summary>
    public int Count => _entries.Count;

    /// <summary>Total entries including object 0 (the free-list head emitted by the writer).</summary>
    public int TotalIncludingFreeListHead => _entries.Count + 1;

    /// <summary>
    /// Allocate an object number without binding an object. The returned reference can be
    /// used by other objects (forward reference); call <see cref="Assign"/> later to bind.
    /// </summary>
    public PdfIndirectRef Allocate()
    {
        _entries.Add(default);
        return new PdfIndirectRef(_entries.Count);
    }

    /// <summary>Allocate and bind in one step. Returns the new reference.</summary>
    public PdfIndirectRef Add(PdfObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        _entries.Add(new Entry(obj, 0));
        return new PdfIndirectRef(_entries.Count);
    }

    /// <summary>Bind an object to a previously-allocated reference.</summary>
    public void Assign(PdfIndirectRef reference, PdfObject obj)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(obj);

        int idx = reference.ObjectNumber - 1;
        if (idx < 0 || idx >= _entries.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reference),
                $"Object {reference.ObjectNumber} was not allocated in this store.");
        }
        if (_entries[idx].Object is not null)
        {
            throw new InvalidOperationException(
                $"Object {reference.ObjectNumber} is already assigned; cannot re-assign.");
        }
        _entries[idx] = _entries[idx] with { Object = obj };
    }

    public PdfObject? Get(PdfIndirectRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        int idx = reference.ObjectNumber - 1;
        return (idx >= 0 && idx < _entries.Count) ? _entries[idx].Object : null;
    }

    /// <summary>
    /// Throws if any allocated reference has not been assigned an object.
    /// Called by <see cref="PdfDocumentWriter.WriteTo"/> before emitting.
    /// </summary>
    public void ValidateAllAssigned()
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Object is null)
            {
                throw new InvalidOperationException(
                    $"Object {i + 1} was allocated via Allocate() but never Assigned. " +
                    "Either assign an object or remove the unused allocation.");
            }
        }
    }

    internal void RecordOffset(int objectNumber, long byteOffset)
    {
        int idx = objectNumber - 1;
        _entries[idx] = _entries[idx] with { ByteOffset = byteOffset };
    }

    internal long GetOffset(int objectNumber) => _entries[objectNumber - 1].ByteOffset;

    internal IReadOnlyList<Entry> AllEntries => _entries;

    internal record struct Entry(PdfObject? Object, long ByteOffset);
}
