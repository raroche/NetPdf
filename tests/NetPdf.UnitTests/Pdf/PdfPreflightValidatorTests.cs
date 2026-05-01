// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;
using NetPdf.Pdf;
using NetPdf.Pdf.Objects;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

public sealed class PdfPreflightValidatorTests
{
    [Fact]
    public void Missing_root_throws()
    {
        var w = new PdfDocumentWriter();
        w.Objects.Add(new PdfInteger(42));
        // No Trailer.Set(Root) — validator should reject.

        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(new ArrayBufferWriter<byte>()));
        Assert.Contains("/Root", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Root_that_is_not_an_indirect_reference_throws()
    {
        var w = new PdfDocumentWriter();
        w.Objects.Add(new PdfInteger(42));
        w.Trailer.Set(PdfNames.Root, new PdfInteger(1)); // wrong type — must be PdfIndirectRef

        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(new ArrayBufferWriter<byte>()));
        Assert.Contains("indirect reference", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Root_pointing_to_unallocated_object_throws()
    {
        var w = new PdfDocumentWriter();
        w.Objects.Add(new PdfInteger(42)); // object 1 only
        w.Trailer.Set(PdfNames.Root, new PdfIndirectRef(99)); // dangling

        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(new ArrayBufferWriter<byte>()));
        Assert.Contains("not allocated", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Root_pointing_to_non_catalog_dictionary_throws()
    {
        var w = new PdfDocumentWriter();
        var pages = new PdfDictionary().Set(PdfNames.Type, PdfNames.Pages);
        var pagesRef = w.Objects.Add(pages);
        w.Trailer.Set(PdfNames.Root, pagesRef);

        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(new ArrayBufferWriter<byte>()));
        Assert.Contains("/Catalog", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dangling_ref_inside_dictionary_throws()
    {
        var w = new PdfDocumentWriter();
        var catalog = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Catalog)
            .Set(PdfNames.Pages, new PdfIndirectRef(99)); // dangling
        w.Trailer.Set(PdfNames.Root, w.Objects.Add(catalog));

        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(new ArrayBufferWriter<byte>()));
        Assert.Contains("dangling", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dangling_ref_inside_array_throws()
    {
        var w = new PdfDocumentWriter();
        var catalog = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Catalog)
            .Set(PdfNames.Kids, new PdfArray().Add(new PdfIndirectRef(42))); // 42 is dangling
        w.Trailer.Set(PdfNames.Root, w.Objects.Add(catalog));

        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(new ArrayBufferWriter<byte>()));
        Assert.Contains("dangling", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dangling_ref_inside_nested_dictionary_throws()
    {
        var w = new PdfDocumentWriter();
        var nestedDict = new PdfDictionary().Set(PdfNames.Parent, new PdfIndirectRef(99));
        var catalog = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Catalog)
            .Set(PdfNames.Resources, nestedDict);
        w.Trailer.Set(PdfNames.Root, w.Objects.Add(catalog));

        Assert.Throws<InvalidOperationException>(() => w.WriteTo(new ArrayBufferWriter<byte>()));
    }

    [Fact]
    public void Dangling_ref_inside_stream_dictionary_throws()
    {
        var w = new PdfDocumentWriter();
        var streamDict = new PdfDictionary().Set(PdfNames.Filter, new PdfIndirectRef(99));
        var stream = new PdfStream(new byte[] { 1, 2, 3 }, streamDict);
        var streamRef = w.Objects.Add(stream);

        var catalog = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Catalog)
            .Set(PdfNames.Contents, streamRef);
        w.Trailer.Set(PdfNames.Root, w.Objects.Add(catalog));

        Assert.Throws<InvalidOperationException>(() => w.WriteTo(new ArrayBufferWriter<byte>()));
    }

    [Fact]
    public void Non_zero_generation_throws()
    {
        var w = new PdfDocumentWriter();
        var catalog = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Catalog)
            .Set(PdfNames.Pages, new PdfIndirectRef(1, generation: 1)); // gen != 0
        w.Trailer.Set(PdfNames.Root, w.Objects.Add(catalog));

        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(new ArrayBufferWriter<byte>()));
        Assert.Contains("generation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]      // unsupported (too old)
    [InlineData("3.0")]      // unsupported (future)
    [InlineData("1.7beta")]  // not on the allow-list
    [InlineData(" 1.7")]     // whitespace
    [InlineData("abc")]
    public void Invalid_version_throws(string version)
    {
        var w = new PdfDocumentWriter { Version = version };
        var catalog = new PdfDictionary().Set(PdfNames.Type, PdfNames.Catalog);
        w.Trailer.Set(PdfNames.Root, w.Objects.Add(catalog));

        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(new ArrayBufferWriter<byte>()));
        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1.4")]
    [InlineData("1.5")]
    [InlineData("1.6")]
    [InlineData("1.7")]
    [InlineData("2.0")]
    public void Each_supported_version_passes_validation(string version)
    {
        var w = new PdfDocumentWriter { Version = version };
        var catalog = new PdfDictionary().Set(PdfNames.Type, PdfNames.Catalog);
        w.Trailer.Set(PdfNames.Root, w.Objects.Add(catalog));

        var buf = new ArrayBufferWriter<byte>();
        w.WriteTo(buf);
        Assert.True(buf.WrittenCount > 0);
    }

    [Fact]
    public void Minimal_valid_pdf_passes_validation()
    {
        // A correctly-constructed minimal PDF goes through validation cleanly.
        var w = new PdfDocumentWriter();
        var catalogRef = w.Objects.Allocate();
        var pagesRef = w.Objects.Allocate();

        var catalog = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Catalog)
            .Set(PdfNames.Pages, pagesRef);
        var pages = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Pages)
            .Set(PdfNames.Kids, new PdfArray())
            .Set(PdfNames.Count, new PdfInteger(0));

        w.Objects.Assign(catalogRef, catalog);
        w.Objects.Assign(pagesRef, pages);
        w.Trailer.Set(PdfNames.Root, catalogRef);

        var buf = new ArrayBufferWriter<byte>();
        w.WriteTo(buf);
        Assert.True(buf.WrittenCount > 0);
    }
}
