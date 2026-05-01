// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Objects;

namespace NetPdf.Pdf;

/// <summary>
/// Owns indirect-object allocation and the byte offsets needed for the xref table.
/// Object numbers are 1-based and assigned in allocation order, giving deterministic
/// numbering for byte-equal output. Object 0 is the implicit free-list head and is
/// emitted by <see cref="PdfDocumentWriter"/>; this store does not represent it.
/// <para>
/// Each store has a unique <see cref="Id"/>. References returned by <see cref="Allocate"/>
/// and <see cref="Add"/> are tagged with that id so cross-store binding (using a ref from
/// store A to <see cref="Assign"/> in store B) is rejected as a programming error.
/// Externally-constructed refs (from the public <see cref="PdfIndirectRef"/> constructor)
/// have id 0 and remain rejected from <see cref="Assign"/> — they're for emit-only use
/// (e.g., constructing a <c>/Root</c> placeholder when the catalog object number is known).
/// </para>
/// </summary>
internal sealed class IndirectObjectStore
{
    private static int s_nextStoreId;

    private readonly List<Entry> _entries = new();

    /// <summary>Opaque id distinguishing this store from any other in the process.</summary>
    public int Id { get; } = Interlocked.Increment(ref s_nextStoreId);

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
        return new PdfIndirectRef(_entries.Count, generation: 0, storeId: Id);
    }

    /// <summary>Allocate and bind in one step. Returns the new reference.</summary>
    public PdfIndirectRef Add(PdfObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        _entries.Add(new Entry(obj, 0));
        return new PdfIndirectRef(_entries.Count, generation: 0, storeId: Id);
    }

    /// <summary>Bind an object to a previously-allocated reference.</summary>
    public void Assign(PdfIndirectRef reference, PdfObject obj)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(obj);

        if (reference.StoreId != Id)
        {
            throw new InvalidOperationException(
                reference.StoreId == 0
                    ? "Reference is synthetic (StoreId = 0) and cannot be Assigned. " +
                      "Use Allocate() or Add() to obtain a store-bound reference."
                    : $"Reference belongs to a different IndirectObjectStore " +
                      $"(StoreId {reference.StoreId}, this store is {Id}); cross-store binding is rejected.");
        }

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
    /// Returns true when <paramref name="reference"/>'s object number is in this store's
    /// allocated range (regardless of <see cref="PdfIndirectRef.StoreId"/>). Used by the
    /// preflight validator to detect dangling references before emit.
    /// </summary>
    internal bool HasAllocatedNumber(PdfIndirectRef reference) =>
        reference.ObjectNumber >= 1 && reference.ObjectNumber <= _entries.Count;

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
