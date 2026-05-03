// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Pdf;
using NetPdf.Pdf.Images;
using NetPdf.Pdf.Objects;
using NetPdf.UnitTests.Pdf.Images;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

/// <summary>
/// End-to-end tests for the high-level <see cref="PdfDocument"/> builder. Exercises
/// page allocation, image registration with content-hash dedup, metadata emission,
/// and deterministic byte-equal output.
/// </summary>
public sealed class PdfDocumentTests
{
    [Fact]
    public void Single_blank_page_produces_a_well_formed_PDF_byte_stream()
    {
        var doc = new PdfDocument();
        doc.AddPage(MediaBoxSize.A4);
        var bytes = doc.Save();

        // PDF header
        Assert.StartsWith("%PDF-1.7", Encoding.ASCII.GetString(bytes, 0, 8));
        // Ends with %%EOF
        var tail = Encoding.ASCII.GetString(bytes, bytes.Length - 8, 8);
        Assert.Contains("%%EOF", tail, StringComparison.Ordinal);
        // Has at least one /Page object
        Assert.Contains("/Type /Page", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Multiple_pages_emit_correct_Pages_Count_and_Kids()
    {
        var doc = new PdfDocument();
        doc.AddPage(MediaBoxSize.Letter);
        doc.AddPage(MediaBoxSize.Letter);
        doc.AddPage(MediaBoxSize.A4);
        var bytes = doc.Save();
        var content = Encoding.ASCII.GetString(bytes);

        Assert.Contains("/Type /Pages", content, StringComparison.Ordinal);
        Assert.Contains("/Count 3", content, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaBox_reflects_the_PageSize_passed_to_AddPage()
    {
        var doc = new PdfDocument();
        doc.AddPage(MediaBoxSize.A4);
        var bytes = doc.Save();
        var content = Encoding.ASCII.GetString(bytes);

        // A4 is 595 × 842 pts.
        Assert.Contains("MediaBox", content, StringComparison.Ordinal);
        Assert.Contains("595", content, StringComparison.Ordinal);
        Assert.Contains("842", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Metadata_appears_in_the_Info_dictionary()
    {
        var doc = new PdfDocument
        {
            Title = "My Doc",
            Author = "Roland",
            Subject = "Testing",
            Keywords = "pdf, test",
            Creator = "NetPdf.UnitTests",
        };
        doc.AddPage(MediaBoxSize.A4);
        var bytes = doc.Save();
        var content = Encoding.ASCII.GetString(bytes);

        Assert.Contains("/Producer", content, StringComparison.Ordinal);
        Assert.Contains("(NetPdf)", content, StringComparison.Ordinal);
        Assert.Contains("(My Doc)", content, StringComparison.Ordinal);
        Assert.Contains("(Roland)", content, StringComparison.Ordinal);
        Assert.Contains("(Testing)", content, StringComparison.Ordinal);
        Assert.Contains("(pdf, test)", content, StringComparison.Ordinal);
    }

    [Fact]
    public void CreationDate_is_formatted_per_ISO_32000_PDF_date_convention()
    {
        var doc = new PdfDocument
        {
            CreationDate = new DateTimeOffset(2026, 5, 2, 14, 30, 45, TimeSpan.Zero),
        };
        doc.AddPage(MediaBoxSize.A4);
        var bytes = doc.Save();
        var content = Encoding.ASCII.GetString(bytes);

        // D:YYYYMMDDHHmmSSZ
        Assert.Contains("(D:20260502143045Z)", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_is_deterministic_for_byte_equal_input()
    {
        // Two builds with identical inputs (and no timestamp) must produce identical bytes.
        var first = BuildSampleDocument();
        var second = BuildSampleDocument();
        Assert.Equal(first, second);
    }

    [Fact]
    public void Save_throws_when_called_twice()
    {
        var doc = new PdfDocument();
        doc.AddPage(MediaBoxSize.A4);
        doc.Save();
        Assert.Throws<InvalidOperationException>(() => doc.Save());
    }

    [Fact]
    public void AddPage_throws_after_Save()
    {
        var doc = new PdfDocument();
        doc.AddPage(MediaBoxSize.A4);
        doc.Save();
        Assert.Throws<InvalidOperationException>(() => doc.AddPage(MediaBoxSize.A4));
    }

    // ───── Image registration + dedup ────────────────────────────────────────

    [Fact]
    public void RegisterImage_returns_distinct_refs_for_distinct_content()
    {
        var doc = new PdfDocument();
        var jpegA = JpegImageXObject.Build(SyntheticJpeg.BuildBaseline(width: 16, height: 16, componentCount: 3));
        var jpegB = JpegImageXObject.Build(SyntheticJpeg.BuildBaseline(width: 32, height: 32, componentCount: 3));

        var refA = doc.RegisterImage(jpegA);
        var refB = doc.RegisterImage(jpegB);

        Assert.NotEqual(refA.ObjectNumber, refB.ObjectNumber);
        Assert.Equal(2, doc.RegisteredImageCount);
    }

    [Fact]
    public void RegisterImage_dedupes_byte_identical_content()
    {
        var doc = new PdfDocument();
        // Build the same JPEG bytes twice — wrap each in a separate PdfStream so we know
        // the dedup is by content, not by object identity.
        var jpegBytes = SyntheticJpeg.BuildBaseline(width: 16, height: 16, componentCount: 3);
        var stream1 = JpegImageXObject.Build(jpegBytes);
        var stream2 = JpegImageXObject.Build(jpegBytes);

        var ref1 = doc.RegisterImage(stream1);
        var ref2 = doc.RegisterImage(stream2);

        Assert.Equal(ref1.ObjectNumber, ref2.ObjectNumber);
        Assert.Equal(1, doc.RegisteredImageCount);
    }

    [Fact]
    public void Page_PlaceImage_emits_content_operators_referencing_the_resource_name()
    {
        var doc = new PdfDocument();
        var jpeg = JpegImageXObject.Build(SyntheticJpeg.BuildBaseline(width: 16, height: 16, componentCount: 3));
        var imageRef = doc.RegisterImage(jpeg);
        var page = doc.AddPage(MediaBoxSize.A4);
        var resourceName = page.PlaceImage(imageRef, x: 100, y: 200, width: 300, height: 400);
        var bytes = doc.Save();
        var content = Encoding.ASCII.GetString(bytes);

        Assert.Equal("Im1", resourceName);
        // Expect the content-stream operator: q 300 0 0 400 100 200 cm /Im1 Do Q
        Assert.Contains("300 0 0 400 100 200 cm /Im1 Do Q", content, StringComparison.Ordinal);
        // And the page resources reference the image.
        Assert.Contains("/Im1", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_AppendContent_writes_raw_operators_into_the_content_stream()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);
        page.AppendContent("0.5 0.5 0.5 rg 100 100 50 50 re f\n");
        var bytes = doc.Save();
        var content = Encoding.ASCII.GetString(bytes);

        Assert.Contains("0.5 0.5 0.5 rg 100 100 50 50 re f", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_throws_when_appending_content_after_Save()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);
        doc.Save();
        Assert.Throws<InvalidOperationException>(() => page.AppendContent("x"));
    }

    [Fact]
    public void Pages_property_lists_pages_in_insertion_order()
    {
        var doc = new PdfDocument();
        var p1 = doc.AddPage(MediaBoxSize.A4);
        var p2 = doc.AddPage(MediaBoxSize.Letter);
        var p3 = doc.AddPage(MediaBoxSize.A5);
        Assert.Equal(3, doc.Pages.Count);
        Assert.Same(p1, doc.Pages[0]);
        Assert.Same(p2, doc.Pages[1]);
        Assert.Same(p3, doc.Pages[2]);
    }

    private static byte[] BuildSampleDocument()
    {
        var doc = new PdfDocument
        {
            Title = "Determinism",
            Author = "Test",
        };
        var jpeg = JpegImageXObject.Build(SyntheticJpeg.BuildBaseline(16, 16, 3));
        var imageRef = doc.RegisterImage(jpeg);
        var p = doc.AddPage(MediaBoxSize.A4);
        p.PlaceImage(imageRef, x: 50, y: 50, width: 100, height: 100);
        return doc.Save();
    }
}
