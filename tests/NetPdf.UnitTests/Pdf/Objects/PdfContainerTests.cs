// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Objects;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Objects;

public sealed class PdfArrayTests
{
    [Fact]
    public void Empty_array() =>
        Assert.Equal("[]", PdfBytes.Ascii(new PdfArray()));

    [Fact]
    public void Single_element() =>
        Assert.Equal("[42]", PdfBytes.Ascii(new PdfArray().Add(new PdfInteger(42))));

    [Fact]
    public void Multiple_elements_are_space_separated()
    {
        var arr = new PdfArray()
            .Add(new PdfInteger(1))
            .Add(new PdfInteger(2))
            .Add(new PdfInteger(3));
        Assert.Equal("[1 2 3]", PdfBytes.Ascii(arr));
    }

    [Fact]
    public void Mixed_types()
    {
        var arr = new PdfArray()
            .Add(PdfBoolean.True)
            .Add(new PdfInteger(42))
            .Add(PdfNames.Type)
            .Add(new PdfLiteralString("hello"));
        Assert.Equal("[true 42 /Type (hello)]", PdfBytes.Ascii(arr));
    }

    [Fact]
    public void Nested_array()
    {
        var inner = new PdfArray().Add(new PdfInteger(1)).Add(new PdfInteger(2));
        var outer = new PdfArray().Add(inner).Add(new PdfInteger(3));
        Assert.Equal("[[1 2] 3]", PdfBytes.Ascii(outer));
    }

    [Fact]
    public void Count_and_indexer()
    {
        var arr = new PdfArray().Add(new PdfInteger(10)).Add(new PdfInteger(20));
        Assert.Equal(2, arr.Count);
        Assert.Equal(10, ((PdfInteger)arr[0]).Value);
        Assert.Equal(20, ((PdfInteger)arr[1]).Value);
    }

    [Fact]
    public void Add_null_throws() =>
        Assert.Throws<ArgumentNullException>(() => new PdfArray().Add(null!));
}

public sealed class PdfDictionaryTests
{
    [Fact]
    public void Empty_dictionary() =>
        Assert.Equal("<< >>", PdfBytes.Ascii(new PdfDictionary()));

    [Fact]
    public void Single_entry()
    {
        var d = new PdfDictionary().Set(PdfNames.Type, PdfNames.Catalog);
        Assert.Equal("<< /Type /Catalog >>", PdfBytes.Ascii(d));
    }

    [Fact]
    public void Multiple_entries_preserve_insertion_order()
    {
        var d = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Page)
            .Set(PdfNames.Parent, new PdfIndirectRef(2))
            .Set(PdfNames.Count, new PdfInteger(3));
        Assert.Equal("<< /Type /Page /Parent 2 0 R /Count 3 >>", PdfBytes.Ascii(d));
    }

    [Fact]
    public void Get_returns_value_or_null()
    {
        var d = new PdfDictionary().Set(PdfNames.Type, PdfNames.Catalog);
        Assert.Same(PdfNames.Catalog, d.Get(PdfNames.Type));
        Assert.Null(d.Get(PdfNames.Pages));
    }

    [Fact]
    public void Set_with_existing_key_replaces_value()
    {
        var d = new PdfDictionary()
            .Set(PdfNames.Length, new PdfInteger(10))
            .Set(PdfNames.Length, new PdfInteger(20));
        Assert.Equal("<< /Length 20 >>", PdfBytes.Ascii(d));
    }

    [Fact]
    public void ContainsKey_works()
    {
        var d = new PdfDictionary().Set(PdfNames.Type, PdfNames.Catalog);
        Assert.True(d.ContainsKey(PdfNames.Type));
        Assert.False(d.ContainsKey(PdfNames.Pages));
    }

    [Fact]
    public void Remove_returns_true_when_removed()
    {
        var d = new PdfDictionary().Set(PdfNames.Type, PdfNames.Catalog);
        Assert.True(d.Remove(PdfNames.Type));
        Assert.False(d.Remove(PdfNames.Type));
    }

    [Fact]
    public void Determinism_byte_equal_for_byte_equal_input()
    {
        var d1 = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Page)
            .Set(PdfNames.Parent, new PdfIndirectRef(2))
            .Set(PdfNames.MediaBox, new PdfArray()
                .Add(new PdfInteger(0))
                .Add(new PdfInteger(0))
                .Add(new PdfInteger(612))
                .Add(new PdfInteger(792)));
        var d2 = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Page)
            .Set(PdfNames.Parent, new PdfIndirectRef(2))
            .Set(PdfNames.MediaBox, new PdfArray()
                .Add(new PdfInteger(0))
                .Add(new PdfInteger(0))
                .Add(new PdfInteger(612))
                .Add(new PdfInteger(792)));
        Assert.Equal(PdfBytes.Render(d1), PdfBytes.Render(d2));
    }
}

public sealed class PdfStreamTests
{
    [Fact]
    public void Empty_stream_writes_dictionary_and_keywords()
    {
        var s = new PdfStream(Array.Empty<byte>());
        var ascii = PdfBytes.Ascii(s);
        Assert.Equal("<< /Length 0 >>\nstream\n\nendstream", ascii);
    }

    [Fact]
    public void Stream_sets_length_automatically()
    {
        var data = new byte[] { (byte)'B', (byte)'T', (byte)' ' };
        var s = new PdfStream(data);
        Assert.NotNull(s.Dictionary.Get(PdfNames.Length));
        var length = (PdfInteger)s.Dictionary.Get(PdfNames.Length)!;
        Assert.Equal(3, length.Value);
    }

    [Fact]
    public void Stream_data_is_emitted_verbatim()
    {
        var data = new byte[] { 0x01, 0x02, 0x03 };
        var s = new PdfStream(data);
        var rendered = PdfBytes.Render(s);
        // Find "stream\n" then check next 3 bytes.
        var streamMarker = "stream\n"u8;
        int idx = rendered.AsSpan().IndexOf(streamMarker);
        Assert.True(idx >= 0);
        int payloadStart = idx + streamMarker.Length;
        Assert.Equal(0x01, rendered[payloadStart]);
        Assert.Equal(0x02, rendered[payloadStart + 1]);
        Assert.Equal(0x03, rendered[payloadStart + 2]);
    }

    [Fact]
    public void Custom_dictionary_is_preserved()
    {
        var dict = new PdfDictionary().Set(PdfNames.Filter, PdfNames.FlateDecode);
        var s = new PdfStream(new byte[] { 1, 2 }, dict);
        var ascii = PdfBytes.Ascii(s);
        Assert.StartsWith("<< /Filter /FlateDecode /Length 2 >>", ascii);
    }
}

public sealed class PdfWriterTests
{
    [Fact]
    public void Position_tracks_bytes_written()
    {
        var pos = PdfBytes.PositionAfter(new PdfLiteralString("hello"));
        Assert.Equal(7, pos); // ( h e l l o ) = 7 bytes
    }

    [Fact]
    public void Position_tracks_through_complex_object()
    {
        var arr = new PdfArray()
            .Add(new PdfInteger(0))
            .Add(new PdfInteger(0))
            .Add(new PdfInteger(612))
            .Add(new PdfInteger(792));
        var pos = PdfBytes.PositionAfter(arr);
        Assert.Equal(13, pos); // [0 0 612 792] = 13 bytes
    }
}
