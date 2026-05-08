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

    // ───── ImageXObjectResult / SMask orchestration ──────────────────────────

    [Fact]
    public void RegisterImage_opaque_PNG_round_trips_through_Save()
    {
        var doc = new PdfDocument();
        var png = PngImageXObject.Build(SyntheticPng.BuildOpaqueRgb8(16, 16));
        var imageRef = doc.RegisterImage(png);
        var page = doc.AddPage(MediaBoxSize.A4);
        page.PlaceImage(imageRef, 50, 50, 100, 100);
        var bytes = doc.Save();
        var content = Encoding.ASCII.GetString(bytes);

        // Image XObject + page resource reference both present.
        Assert.Contains("/Subtype /Image", content, StringComparison.Ordinal);
        Assert.Contains("/Im1", content, StringComparison.Ordinal);
        // Opaque PNG never gets an /SMask wired.
        Assert.DoesNotContain("/SMask ", content, StringComparison.Ordinal);
        Assert.Equal(1, doc.RegisteredImageCount);
    }

    [Fact]
    public void RegisterImage_RGBA_PNG_emits_indirect_SMask_reference()
    {
        var doc = new PdfDocument();
        var png = PngImageXObject.Build(SyntheticPng.BuildRgba8(8, 8));
        Assert.NotNull(png.SMask); // sanity
        var imageRef = doc.RegisterImage(png);
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 0, 0, 100, 100);
        var bytes = doc.Save();
        var content = Encoding.ASCII.GetString(bytes);

        // /SMask appears with an indirect reference suffix (e.g. "/SMask 7 0 R"),
        // not as an inline `<<...>>` direct stream.
        Assert.Matches(@"/SMask\s+\d+\s+0\s+R", content);
        // Both the primary Image XObject and the SMask Image XObject are present.
        var imageSubtypeCount = CountOccurrences(content, "/Subtype /Image");
        Assert.True(imageSubtypeCount >= 2, $"Expected at least two /Subtype /Image entries (Image + SMask); found {imageSubtypeCount}.");
    }

    [Fact]
    public void RegisterImage_indexed_PNG_with_binary_tRNS_emits_color_key_Mask_no_SMask()
    {
        // Binary tRNS (every alpha entry is 0 or 255) → /Mask color-key path, not SMask.
        var palette = new byte[] { 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF };
        var trns = new byte[] { 0x00, 0xFF, 0xFF }; // index 0 fully transparent, others opaque
        var doc = new PdfDocument();
        var png = PngImageXObject.Build(SyntheticPng.BuildIndexed8WithTrns(8, 8, palette, trns));
        Assert.Null(png.SMask); // sanity — binary tRNS uses /Mask, not SMask
        var imageRef = doc.RegisterImage(png);
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 0, 0, 100, 100);
        var bytes = doc.Save();
        var content = Encoding.ASCII.GetString(bytes);

        // /Mask array stays inline on the image dict (it's an array of integers, not a
        // stream — perfectly legal in a direct context).
        Assert.Contains("/Mask [", content, StringComparison.Ordinal);
        Assert.DoesNotContain("/SMask ", content, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterImage_dedupes_pair_when_Image_and_SMask_are_byte_identical()
    {
        var doc = new PdfDocument();
        var png1 = PngImageXObject.Build(SyntheticPng.BuildRgba8(8, 8));
        var png2 = PngImageXObject.Build(SyntheticPng.BuildRgba8(8, 8));

        var ref1 = doc.RegisterImage(png1);
        var ref2 = doc.RegisterImage(png2);

        Assert.Equal(ref1.ObjectNumber, ref2.ObjectNumber);
        Assert.Equal(1, doc.RegisteredImageCount);
    }

    [Fact]
    public void RegisterImage_dedup_distinguishes_streams_with_different_dictionary_metadata()
    {
        // Two streams with byte-identical payload but different /ColorSpace must NOT
        // dedupe — they render differently. This guards the "hash dictionary, not just
        // payload" invariant.
        var payload = new byte[256];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)i;

        var dictA = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.XObject)
            .Set(PdfNames.Subtype, PdfNames.Image)
            .Set(PdfNames.Width, new PdfInteger(16))
            .Set(PdfNames.Height, new PdfInteger(16))
            .Set(PdfNames.ColorSpace, PdfNames.DeviceGray)
            .Set(PdfNames.BitsPerComponent, new PdfInteger(8));
        var dictB = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.XObject)
            .Set(PdfNames.Subtype, PdfNames.Image)
            .Set(PdfNames.Width, new PdfInteger(16))
            .Set(PdfNames.Height, new PdfInteger(16))
            .Set(PdfNames.ColorSpace, PdfNames.DeviceRGB) // different ColorSpace
            .Set(PdfNames.BitsPerComponent, new PdfInteger(8));
        var streamA = new PdfStream(payload, dictA);
        var streamB = new PdfStream(payload, dictB);

        var doc = new PdfDocument();
        var refA = doc.RegisterImage(streamA);
        var refB = doc.RegisterImage(streamB);

        Assert.NotEqual(refA.ObjectNumber, refB.ObjectNumber);
        Assert.Equal(2, doc.RegisteredImageCount);
    }

    [Fact]
    public void RegisterImage_throws_when_input_is_not_an_Image_XObject()
    {
        var doc = new PdfDocument();
        // Random PdfStream with no /Subtype /Image — rejected.
        var notAnImage = new PdfStream([0xDE, 0xAD], new PdfDictionary());
        var ex = Assert.Throws<ArgumentException>(() => doc.RegisterImage(notAnImage));
        Assert.Contains("/Subtype must be /Image", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterImage_throws_when_image_dict_is_missing_required_keys()
    {
        var doc = new PdfDocument();
        // Has /Subtype /Image but missing /Width, /Height, /ColorSpace, /BitsPerComponent.
        var dict = new PdfDictionary()
            .Set(PdfNames.Subtype, PdfNames.Image);
        var stub = new PdfStream([0x00], dict);
        var ex = Assert.Throws<ArgumentException>(() => doc.RegisterImage(stub));
        Assert.Contains("/Width", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterImage_RGBA_save_passes_preflight_no_nested_direct_stream()
    {
        // Smoke test: the alpha-split path used to embed /SMask as a direct PdfStream,
        // which is malformed PDF. Now it's an indirect ref — Save() must succeed and
        // pass preflight.
        var doc = new PdfDocument();
        var png = PngImageXObject.Build(SyntheticPng.BuildRgba8(8, 8));
        var imageRef = doc.RegisterImage(png);
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 0, 0, 100, 100);
        // Should not throw.
        var bytes = doc.Save();
        Assert.True(bytes.Length > 100);
    }

    [Fact]
    public void RegisterImage_same_alpha_image_instance_dedupes_to_single_ref()
    {
        // Regression for the caller-mutation bug: registering the SAME ImageXObjectResult
        // instance twice used to mutate the primary image's dictionary on the first call
        // (setting /SMask to an indirect ref), so the second call computed a different
        // dedup key (against the now-mutated dict) and allocated a duplicate XObject
        // pair. Cloning the dict before wiring keeps the caller's instance pristine and
        // the dedup key stable across calls.
        var doc = new PdfDocument();
        var png = PngImageXObject.Build(SyntheticPng.BuildRgba8(8, 8));
        Assert.NotNull(png.SMask);

        var ref1 = doc.RegisterImage(png);
        var ref2 = doc.RegisterImage(png); // same instance

        Assert.Equal(ref1.ObjectNumber, ref2.ObjectNumber);
        Assert.Equal(1, doc.RegisteredImageCount);
    }

    [Fact]
    public void RegisterImage_does_not_mutate_caller_owned_image_dictionary()
    {
        // Builder output must remain pristine: the caller may register the same result
        // with a second PdfDocument or inspect/hash it after the first registration,
        // and the dictionary should look exactly like what the builder emitted.
        var png = PngImageXObject.Build(SyntheticPng.BuildRgba8(8, 8));
        Assert.Null(png.Image.Dictionary.Get(PdfNames.SMask)); // builder pre-condition

        var doc = new PdfDocument();
        doc.RegisterImage(png);

        // Caller's primary image dict still has no /SMask after registration.
        Assert.Null(png.Image.Dictionary.Get(PdfNames.SMask));
    }

    [Fact]
    public void RegisterImage_throws_on_zero_or_negative_dimensions()
    {
        var doc = new PdfDocument();
        var dict = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.XObject)
            .Set(PdfNames.Subtype, PdfNames.Image)
            .Set(PdfNames.Width, new PdfInteger(0)) // ← invalid
            .Set(PdfNames.Height, new PdfInteger(8))
            .Set(PdfNames.ColorSpace, PdfNames.DeviceGray)
            .Set(PdfNames.BitsPerComponent, new PdfInteger(8));
        var stream = new PdfStream([0x00], dict);
        var ex = Assert.Throws<ArgumentException>(() => doc.RegisterImage(stream));
        Assert.Contains("Width", ex.Message, StringComparison.Ordinal);
        Assert.Contains("> 0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterImage_throws_on_invalid_BitsPerComponent_value()
    {
        var doc = new PdfDocument();
        // BPC = 5 is invalid per ISO 32000-2 §8.9.5 Table 89 (allowed: 1, 2, 4, 8, 16).
        var dict = new PdfDictionary()
            .Set(PdfNames.Subtype, PdfNames.Image)
            .Set(PdfNames.Width, new PdfInteger(8))
            .Set(PdfNames.Height, new PdfInteger(8))
            .Set(PdfNames.ColorSpace, PdfNames.DeviceGray)
            .Set(PdfNames.BitsPerComponent, new PdfInteger(5));
        var stream = new PdfStream([0x00], dict);
        var ex = Assert.Throws<ArgumentException>(() => doc.RegisterImage(stream));
        Assert.Contains("BitsPerComponent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterImage_throws_when_SMask_uses_disallowed_BitsPerComponent()
    {
        // ISO 32000-2 §11.6: SMask BPC must be 8 or 16. A 4-bit SMask is invalid.
        var imageDict = new PdfDictionary()
            .Set(PdfNames.Subtype, PdfNames.Image)
            .Set(PdfNames.Width, new PdfInteger(8))
            .Set(PdfNames.Height, new PdfInteger(8))
            .Set(PdfNames.ColorSpace, PdfNames.DeviceRGB)
            .Set(PdfNames.BitsPerComponent, new PdfInteger(8));
        var smaskDict = new PdfDictionary()
            .Set(PdfNames.Subtype, PdfNames.Image)
            .Set(PdfNames.Width, new PdfInteger(8))
            .Set(PdfNames.Height, new PdfInteger(8))
            .Set(PdfNames.ColorSpace, PdfNames.DeviceGray)
            .Set(PdfNames.BitsPerComponent, new PdfInteger(4)); // ← invalid for SMask
        var result = new ImageXObjectResult
        {
            Image = new PdfStream([0x00], imageDict),
            SMask = new PdfStream([0x00], smaskDict),
        };

        var doc = new PdfDocument();
        var ex = Assert.Throws<ArgumentException>(() => doc.RegisterImage(result));
        Assert.Contains("SMask", ex.Message, StringComparison.Ordinal);
        Assert.Contains("8 or 16", ex.Message, StringComparison.Ordinal);
    }

    // ───── Document-level orchestration: raster fallback formats ────────────

    [Fact]
    public void RegisterImage_transparent_GIF_through_PdfDocument_emits_indirect_SMask()
    {
        var doc = new PdfDocument();
        var raster = RasterImageXObject.Build(SyntheticRasterImage.BuildTransparentGif());
        Assert.NotNull(raster.SMask); // sanity — GIF89a with transparent index → alpha-split
        var imageRef = doc.RegisterImage(raster);
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 0, 0, 100, 100);
        var bytes = doc.Save();
        var content = Encoding.ASCII.GetString(bytes);

        // /SMask is wired as an indirect ref — never an inline stream.
        Assert.Matches(@"/SMask\s+\d+\s+0\s+R", content);
    }

    [Fact]
    public void RegisterImage_opaque_WebP_through_PdfDocument_emits_no_SMask()
    {
        var doc = new PdfDocument();
        var raster = RasterImageXObject.Build(SyntheticRasterImage.BuildOpaqueWebp(8, 8));
        Assert.Null(raster.SMask); // sanity
        var imageRef = doc.RegisterImage(raster);
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 0, 0, 100, 100);
        var bytes = doc.Save();
        var content = Encoding.ASCII.GetString(bytes);

        Assert.DoesNotContain("/SMask ", content, StringComparison.Ordinal);
        // Image XObject is present.
        Assert.Contains("/Subtype /Image", content, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterImage_AVIF_now_rejected_by_phase_C_safety_gate()
    {
        // Per PR #17 Phase C C-1 follow-up — AVIF rejected by
        // ImageSafetyValidator before RasterImageXObject.Build returns.
        // The fixture stays for post-v1 wireup; the test now pins the
        // rejection contract.
        using var stream = typeof(PdfDocumentTests).Assembly
            .GetManifestResourceStream("NetPdf.UnitTests.Resources.Images.white_1x1.avif")
            ?? throw new InvalidOperationException("Test resource white_1x1.avif missing.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);

        var ex = Assert.Throws<InvalidOperationException>(
            () => RasterImageXObject.Build(ms.ToArray()));
        Assert.Contains("AVIF", ex.Message, StringComparison.Ordinal);
    }

    // ───── AppendContent contract ────────────────────────────────────────────

    [Fact]
    public void AppendContent_string_throws_on_non_ASCII_input()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);
        var ex = Assert.Throws<ArgumentException>(() => page.AppendContent("héllo"));
        Assert.Contains("ASCII", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendContent_byte_overload_writes_raw_bytes_into_content_stream()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);
        // 0x89 is non-ASCII; the byte overload accepts it without complaint.
        ReadOnlySpan<byte> raw = [(byte)'q', (byte)' ', 0x89, (byte)' ', (byte)'Q', (byte)'\n'];
        page.AppendContent(raw);
        var bytes = doc.Save();

        // The 0x89 byte must survive verbatim into the emitted content stream.
        Assert.Contains((byte)0x89, bytes);
    }

    private static int CountOccurrences(string source, string needle)
    {
        var count = 0;
        var pos = 0;
        while ((pos = source.IndexOf(needle, pos, StringComparison.Ordinal)) >= 0)
        {
            count++;
            pos += needle.Length;
        }
        return count;
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
