// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf.Objects;

/// <summary>
/// ISO 32000-2 §7.3.10 — indirect reference. Emits <c>N G R</c> where N is the object
/// number and G is the generation. NetPdf uses generation 0 throughout v1 (no incremental
/// updates); non-zero generations are rejected by the preflight validator.
/// <para>
/// References carry an opaque <see cref="StoreId"/> that ties them to the
/// <see cref="IndirectObjectStore"/> that allocated them. <see cref="IndirectObjectStore.Assign"/>
/// rejects refs from a different store. Externally-constructed refs (e.g.,
/// <c>new PdfIndirectRef(N)</c> for a known target number when setting <c>/Root</c> in tests)
/// have <see cref="StoreId"/> of 0 and are accepted as opaque pointers — they emit correctly
/// but cannot be used to <see cref="IndirectObjectStore.Assign"/> against any store.
/// </para>
/// </summary>
internal sealed class PdfIndirectRef : PdfObject
{
    public int ObjectNumber { get; }
    public int Generation { get; }

    /// <summary>
    /// Identifier of the <see cref="IndirectObjectStore"/> that allocated this reference,
    /// or 0 for refs constructed via the public constructor (treated as synthetic / opaque).
    /// </summary>
    internal int StoreId { get; }

    public PdfIndirectRef(int objectNumber, int generation = 0)
        : this(objectNumber, generation, storeId: 0)
    {
    }

    internal PdfIndirectRef(int objectNumber, int generation, int storeId)
    {
        if (objectNumber < 1) throw new ArgumentOutOfRangeException(nameof(objectNumber), "Object numbers start at 1.");
        if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));
        ObjectNumber = objectNumber;
        Generation = generation;
        StoreId = storeId;
    }

    /// <summary>
    /// Identity comparison for two indirect references: same object number, generation,
    /// AND originating store. The <see cref="StoreId"/> is essential — two
    /// <see cref="IndirectObjectStore"/>s (e.g. two <see cref="PdfDocument"/>s) number
    /// objects deterministically, so a FOREIGN ref can share this ref's object number
    /// while pointing at an entirely different object. Used by resource dedup
    /// (<see cref="PdfPage.AddFont"/>) where conflating a foreign ref with a local one
    /// would silently substitute the wrong object and skip preflight's foreign-ref rejection.
    /// </summary>
    internal bool HasSameTarget(PdfIndirectRef other) =>
        other is not null
        && ObjectNumber == other.ObjectNumber
        && Generation == other.Generation
        && StoreId == other.StoreId;

    public override void WriteTo(PdfWriter writer)
    {
        writer.WriteInteger(ObjectNumber);
        writer.WriteSpace();
        writer.WriteInteger(Generation);
        writer.WriteAscii(" R");
    }
}
