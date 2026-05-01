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
    public void Assign_unallocated_reference_throws()
    {
        var s = new IndirectObjectStore();
        var stranger = new PdfIndirectRef(99);

        Assert.Throws<ArgumentOutOfRangeException>(() => s.Assign(stranger, PdfBoolean.True));
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
