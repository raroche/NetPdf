// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf;
using NetPdf.Pdf.Objects;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

public sealed class IndirectObjectStoreTests
{
    [Fact]
    public void Newly_created_store_has_no_real_entries()
    {
        var s = new IndirectObjectStore();
        Assert.Equal(0, s.Count);
        Assert.Equal(1, s.TotalIncludingFreeListHead);
    }

    [Fact]
    public void Add_assigns_sequential_one_based_object_numbers()
    {
        var s = new IndirectObjectStore();
        var first = s.Add(PdfBoolean.True);
        var second = s.Add(PdfNull.Instance);

        Assert.Equal(1, first.ObjectNumber);
        Assert.Equal(2, second.ObjectNumber);
        Assert.Equal(0, first.Generation);
        Assert.Equal(0, second.Generation);
        Assert.Equal(2, s.Count);
        Assert.Equal(3, s.TotalIncludingFreeListHead);
    }

    [Fact]
    public void Allocate_then_Assign_works_for_forward_references()
    {
        var s = new IndirectObjectStore();
        var reference = s.Allocate();
        Assert.Null(s.Get(reference));

        s.Assign(reference, PdfBoolean.True);
        Assert.Same(PdfBoolean.True, s.Get(reference));
    }

    [Fact]
    public void Assign_synthetic_reference_throws()
    {
        // Synthetic refs (constructed via the public ctor, StoreId = 0) are emit-only —
        // they can appear in trailer /Root or as values in dictionaries, but they cannot
        // be used to bind an object to any store.
        var s = new IndirectObjectStore();
        var synthetic = new PdfIndirectRef(99);

        var ex = Assert.Throws<InvalidOperationException>(() => s.Assign(synthetic, PdfBoolean.True));
        Assert.Contains("synthetic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assign_reference_from_a_different_store_throws()
    {
        var storeA = new IndirectObjectStore();
        var storeB = new IndirectObjectStore();

        // Allocate in store A; try to bind in store B.
        var refFromA = storeA.Allocate();

        var ex = Assert.Throws<InvalidOperationException>(() => storeB.Assign(refFromA, PdfBoolean.True));
        Assert.Contains("different IndirectObjectStore", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_store_has_a_unique_id()
    {
        var a = new IndirectObjectStore();
        var b = new IndirectObjectStore();
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void Allocate_and_Add_tag_refs_with_store_id()
    {
        var s = new IndirectObjectStore();
        var allocated = s.Allocate();
        var added = s.Add(PdfBoolean.True);

        Assert.Equal(s.Id, allocated.StoreId);
        Assert.Equal(s.Id, added.StoreId);
    }

    [Fact]
    public void Synthetic_refs_have_store_id_zero()
    {
        var synthetic = new PdfIndirectRef(1);
        Assert.Equal(0, synthetic.StoreId);
    }

    [Fact]
    public void Assign_already_assigned_reference_throws()
    {
        var s = new IndirectObjectStore();
        var reference = s.Add(PdfBoolean.True);

        Assert.Throws<InvalidOperationException>(() => s.Assign(reference, PdfBoolean.False));
    }

    [Fact]
    public void Add_null_object_throws()
    {
        var s = new IndirectObjectStore();
        Assert.Throws<ArgumentNullException>(() => s.Add(null!));
    }

    [Fact]
    public void ValidateAllAssigned_throws_when_any_unassigned()
    {
        var s = new IndirectObjectStore();
        s.Add(PdfBoolean.True);
        s.Allocate(); // hole

        Assert.Throws<InvalidOperationException>(s.ValidateAllAssigned);
    }

    [Fact]
    public void ValidateAllAssigned_passes_when_all_filled()
    {
        var s = new IndirectObjectStore();
        s.Add(PdfBoolean.True);
        s.Add(PdfNull.Instance);
        var pending = s.Allocate();
        s.Assign(pending, new PdfInteger(42));

        s.ValidateAllAssigned();
    }

    [Fact]
    public void Forward_reference_round_trips()
    {
        // Catalog references the page tree; page tree is allocated first to get its number,
        // then both are filled in. This is the standard PDF assembly pattern.
        var s = new IndirectObjectStore();
        var catalogRef = s.Allocate();
        var pagesRef = s.Allocate();

        var catalog = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Catalog)
            .Set(PdfNames.Pages, pagesRef);
        var pages = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Pages)
            .Set(PdfNames.Kids, new PdfArray())
            .Set(PdfNames.Count, new PdfInteger(0));

        s.Assign(catalogRef, catalog);
        s.Assign(pagesRef, pages);

        Assert.Same(catalog, s.Get(catalogRef));
        Assert.Same(pages, s.Get(pagesRef));
        s.ValidateAllAssigned();
    }

    [Fact]
    public void Get_with_unallocated_reference_returns_null()
    {
        var s = new IndirectObjectStore();
        Assert.Null(s.Get(new PdfIndirectRef(42)));
    }
}
