// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;
using System.Text;
using NetPdf.Pdf;
using NetPdf.Pdf.Objects;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

public sealed class PdfDocumentWriterTests
{
    [Fact]
    public void Header_starts_with_pdf_version_and_binary_marker()
    {
        var w = SeededWriter();
        var bytes = Render(w);

        Assert.Equal((byte)'%', bytes[0]);
        Assert.Equal("PDF-1.7\n", Encoding.ASCII.GetString(bytes, 1, 8));
        // §7.5.2 binary marker: % then 4 bytes >= 0x80 then \n.
        Assert.Equal((byte)'%', bytes[9]);
        Assert.True(bytes[10] >= 0x80);
        Assert.True(bytes[11] >= 0x80);
        Assert.True(bytes[12] >= 0x80);
        Assert.True(bytes[13] >= 0x80);
        Assert.Equal((byte)'\n', bytes[14]);
    }

    [Theory]
    [InlineData("1.4", "%PDF-1.4\n")]
    [InlineData("1.5", "%PDF-1.5\n")]
    [InlineData("1.6", "%PDF-1.6\n")]
    [InlineData("1.7", "%PDF-1.7\n")]
    [InlineData("2.0", "%PDF-2.0\n")]
    public void Each_pdf_version_emits_expected_header(string version, string expectedHeader)
    {
        var w = new PdfDocumentWriter { Version = version };
        var rootRef = w.Objects.Add(NewMinimalCatalog(w));
        w.Trailer.Set(PdfNames.Root, rootRef);

        var ascii = AsciiRender(w);
        Assert.StartsWith(expectedHeader, ascii);
    }

    [Fact]
    public void Indirect_object_uses_n_zero_obj_format()
    {
        var w = new PdfDocumentWriter();
        w.Objects.Add(new PdfInteger(42));
        w.Trailer.Set(PdfNames.Root, new PdfIndirectRef(1));

        var ascii = AsciiRender(w);
        Assert.Contains("1 0 obj\n42\nendobj\n", ascii);
    }

    [Fact]
    public void Multiple_objects_numbered_sequentially()
    {
        var w = new PdfDocumentWriter();
        w.Objects.Add(new PdfInteger(10));
        w.Objects.Add(new PdfInteger(20));
        w.Objects.Add(new PdfInteger(30));
        w.Trailer.Set(PdfNames.Root, new PdfIndirectRef(1));

        var ascii = AsciiRender(w);
        Assert.Contains("1 0 obj\n10\nendobj\n", ascii);
        Assert.Contains("2 0 obj\n20\nendobj\n", ascii);
        Assert.Contains("3 0 obj\n30\nendobj\n", ascii);
    }

    [Fact]
    public void Xref_section_header_is_zero_count_with_total_objects()
    {
        var w = new PdfDocumentWriter();
        w.Objects.Add(new PdfInteger(1));
        w.Objects.Add(new PdfInteger(2));
        w.Trailer.Set(PdfNames.Root, new PdfIndirectRef(1));

        var ascii = AsciiRender(w);
        Assert.Contains("xref\n0 3\n", ascii);
    }

    [Fact]
    public void Xref_first_entry_is_free_list_head()
    {
        var w = SeededWriter();
        var ascii = AsciiRender(w);
        Assert.Contains("0000000000 65535 f \n", ascii);
    }

    [Fact]
    public void Xref_real_entries_are_in_use_with_generation_zero()
    {
        var w = SeededWriter();
        var ascii = AsciiRender(w);
        Assert.Matches(@"\d{10} 00000 n \n", ascii);
    }

    [Fact]
    public void Xref_entries_are_exactly_twenty_bytes()
    {
        var w = new PdfDocumentWriter();
        w.Objects.Add(new PdfInteger(42));
        w.Trailer.Set(PdfNames.Root, new PdfIndirectRef(1));
        var bytes = Render(w);

        var marker = "xref\n0 2\n"u8;
        int idx = bytes.AsSpan().IndexOf(marker);
        Assert.True(idx >= 0, "xref section marker not found");

        int entryStart = idx + marker.Length;
        for (int i = 0; i < 2; i++)
        {
            ReadOnlySpan<byte> entry = bytes.AsSpan(entryStart + i * 20, 20);
            Assert.Equal((byte)' ', entry[10]);
            Assert.Equal((byte)' ', entry[16]);
            Assert.Equal((byte)' ', entry[18]);
            Assert.Equal((byte)'\n', entry[19]);
        }
    }

    [Fact]
    public void Xref_offsets_match_actual_indirect_object_byte_positions()
    {
        var w = new PdfDocumentWriter();
        w.Objects.Add(new PdfInteger(10));
        w.Objects.Add(new PdfInteger(20));
        w.Trailer.Set(PdfNames.Root, new PdfIndirectRef(1));
        var bytes = Render(w);
        var ascii = Encoding.Latin1.GetString(bytes);

        int obj1Pos = ascii.IndexOf("1 0 obj");
        int obj2Pos = ascii.IndexOf("2 0 obj");

        Assert.Contains($"{obj1Pos:D10} 00000 n \n", ascii);
        Assert.Contains($"{obj2Pos:D10} 00000 n \n", ascii);
    }

    [Fact]
    public void Trailer_size_includes_free_list_head()
    {
        var w = new PdfDocumentWriter();
        w.Objects.Add(new PdfInteger(1));
        w.Objects.Add(new PdfInteger(2));
        w.Trailer.Set(PdfNames.Root, new PdfIndirectRef(1));

        var ascii = AsciiRender(w);
        Assert.Contains("/Size 3", ascii); // 2 real objects + 1 free-list head
    }

    [Fact]
    public void Startxref_points_to_xref_keyword_byte_offset()
    {
        var w = SeededWriter();
        var bytes = Render(w);
        var ascii = Encoding.Latin1.GetString(bytes);

        int xrefIdx = ascii.IndexOf("xref\n");
        int startxrefIdx = ascii.IndexOf("startxref\n");
        Assert.True(xrefIdx >= 0 && startxrefIdx >= 0);

        int valueStart = startxrefIdx + "startxref\n".Length;
        int valueEnd = ascii.IndexOf('\n', valueStart);
        int parsed = int.Parse(ascii.AsSpan(valueStart, valueEnd - valueStart));

        Assert.Equal(xrefIdx, parsed);
    }

    [Fact]
    public void Output_ends_with_eof_marker()
    {
        var w = SeededWriter();
        var ascii = AsciiRender(w);
        Assert.EndsWith("%%EOF\n", ascii);
    }

    [Fact]
    public void Unassigned_object_throws_at_write_time()
    {
        var w = new PdfDocumentWriter();
        w.Objects.Allocate(); // never assigned
        w.Trailer.Set(PdfNames.Root, new PdfIndirectRef(1));

        var buf = new ArrayBufferWriter<byte>();
        Assert.Throws<InvalidOperationException>(() => w.WriteTo(buf));
    }

    [Fact]
    public void Writing_to_null_output_throws()
    {
        var w = SeededWriter();
        Assert.Throws<ArgumentNullException>(() => w.WriteTo(null!));
    }

    [Fact]
    public void Determinism_byte_equal_for_byte_equal_input()
    {
        // Same construction sequence → same bytes. This is the foundational property
        // of the byte writer; everything downstream (deterministic font subsetter prefix,
        // /ID derivation, regression tests) relies on it.
        Assert.Equal(BuildMinimalPdf(), BuildMinimalPdf());
    }

    [Fact]
    public void Minimal_pdf_has_well_formed_structure()
    {
        var bytes = BuildMinimalPdf();
        var ascii = Encoding.Latin1.GetString(bytes);

        Assert.StartsWith("%PDF-1.7\n", ascii);
        Assert.Contains("1 0 obj\n", ascii);
        Assert.Contains("/Type /Catalog", ascii);
        Assert.Contains("2 0 obj\n", ascii);
        Assert.Contains("/Type /Pages", ascii);
        Assert.Contains("xref\n0 3\n", ascii);
        Assert.Contains("0000000000 65535 f \n", ascii);
        Assert.Contains("trailer\n", ascii);
        Assert.Contains("/Size 3", ascii);
        Assert.Contains("/Root 1 0 R", ascii);
        Assert.Contains("startxref\n", ascii);
        Assert.EndsWith("%%EOF\n", ascii);
    }

    // ------------------------------------------------------------------------------------

    private static PdfDocumentWriter SeededWriter()
    {
        var w = new PdfDocumentWriter();
        var rootRef = w.Objects.Add(NewMinimalCatalog(w));
        w.Trailer.Set(PdfNames.Root, rootRef);
        return w;
    }

    private static PdfDictionary NewMinimalCatalog(PdfDocumentWriter w)
        => new PdfDictionary().Set(PdfNames.Type, PdfNames.Catalog);

    private static byte[] BuildMinimalPdf()
    {
        // Catalog → Pages tree (empty). The simplest valid PDF document body.
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

        return Render(w);
    }

    private static byte[] Render(PdfDocumentWriter w)
    {
        var buf = new ArrayBufferWriter<byte>();
        w.WriteTo(buf);
        return buf.WrittenSpan.ToArray();
    }

    private static string AsciiRender(PdfDocumentWriter w) =>
        Encoding.Latin1.GetString(Render(w));
}
