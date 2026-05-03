// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Security.Cryptography;
using NetPdf.Pdf;
using NetPdf.Pdf.Images;
using NetPdf.Pdf.Objects;
using NetPdf.UnitTests.Pdf.Images;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

/// <summary>
/// Determinism harness — Task 23. The contract that every individual feature test in
/// this codebase implicitly relies on is: <b>identical input produces byte-identical
/// output</b>. Each component already has a small "render twice, assert equal" check
/// (e.g. <c>PdfDocumentTests.Save_is_deterministic_for_byte_equal_input</c>); this
/// harness pulls those checks together at the orchestration layer and exercises the
/// determinism property across many document shapes simultaneously, so a regression
/// in any one of them — a silent <c>HashSet</c> iteration order leak, a thread-pool
/// PRNG, an ambient <c>DateTime.Now</c> sneaking in — will be caught here even when
/// the per-feature test still passes.
/// <para>
/// The harness has two layers:
/// </para>
/// <list type="bullet">
///   <item><b>Byte-equal-twice property</b>: build the same document twice in
///         independent <see cref="PdfDocument"/> instances and assert the byte arrays
///         are equal. Catches per-process non-determinism (caches, ordered/unordered
///         iteration, mutable static state).</item>
///   <item><b>Pinned-snapshot</b> for one canonical "everything-in" document: assert
///         the SHA-256 of the bytes equals a hash baked into the test. Catches drift
///         that no longer reproduces in the same process (e.g. an upgrade to a
///         dependency that quietly re-orders dict iteration). Updating the snapshot
///         requires manually verifying the new bytes round-trip through PDFium / qpdf
///         — never blindly re-pin.</item>
/// </list>
/// <para>
/// Image fixtures used here are restricted to <b>hand-crafted byte-stable</b>
/// generators (<see cref="SyntheticJpeg.BuildBaseline"/>, <see cref="SyntheticPng"/>,
/// <see cref="SyntheticRasterImage.BuildMinimalGif"/>,
/// <see cref="SyntheticRasterImage.BuildTransparentGif"/>). Skia-encoded WebP / AVIF
/// outputs are not byte-stable across SkiaSharp builds, so they live in the
/// byte-equal-twice property tests but are excluded from the pinned snapshot.
/// </para>
/// </summary>
public sealed class PdfDocumentDeterminismHarnessTests
{
    public static IEnumerable<object[]> AllShapes()
    {
        yield return new object[] { "blank-a4", (Func<byte[]>)BuildBlankA4 };
        yield return new object[] { "blank-letter-with-metadata", (Func<byte[]>)BuildBlankLetterWithMetadata };
        yield return new object[] { "multi-page-mixed-sizes", (Func<byte[]>)BuildMultiPageMixedSizes };
        yield return new object[] { "jpeg-embed-and-place", (Func<byte[]>)BuildJpegEmbedAndPlace };
        yield return new object[] { "png-opaque-embed", (Func<byte[]>)BuildPngOpaqueEmbed };
        yield return new object[] { "png-rgba-with-smask", (Func<byte[]>)BuildPngRgbaWithSMask };
        yield return new object[] { "png-indexed-binary-trns-color-key", (Func<byte[]>)BuildPngIndexedBinaryTrns };
        yield return new object[] { "png-indexed-non-binary-trns-smask", (Func<byte[]>)BuildPngIndexedNonBinaryTrns };
        yield return new object[] { "transparent-gif-via-raster", (Func<byte[]>)BuildTransparentGifViaRaster };
        yield return new object[] { "image-dedup-three-references", (Func<byte[]>)BuildImageDedupThreeReferences };
        yield return new object[] { "mixed-images-alpha-and-opaque", (Func<byte[]>)BuildMixedImagesAlphaAndOpaque };
        yield return new object[] { "raw-content-stream-byte-overload", (Func<byte[]>)BuildRawContentStreamByteOverload };
        yield return new object[] { "explicit-creation-date-utc", (Func<byte[]>)BuildExplicitCreationDateUtc };
    }

    [Theory]
    [MemberData(nameof(AllShapes))]
    public void Document_shape_renders_byte_equal_when_built_twice(string name, Func<byte[]> build)
    {
        // Two independent PdfDocument instances built from identical inputs must produce
        // identical bytes. Mutable static state, hash-randomization, ordered/unordered
        // dictionary iteration, etc., would surface as a difference here.
        _ = name; // captured for xUnit test name only
        var first = build();
        var second = build();
        Assert.Equal(first, second);
    }

    [Theory]
    [MemberData(nameof(AllShapes))]
    public void Document_shape_renders_byte_equal_across_three_iterations(string name, Func<byte[]> build)
    {
        // Stronger property: any internal cache that warms up on first build but
        // produces a different output on subsequent builds (e.g. a HashSet's iteration
        // order changing after rehash) will manifest at iteration 3 even if iterations
        // 1 and 2 happened to coincide.
        _ = name;
        var a = build();
        var b = build();
        var c = build();
        Assert.Equal(a, b);
        Assert.Equal(b, c);
    }

    /// <summary>
    /// Pinned-snapshot test for a canonical "everything-in" document. Update the
    /// constant <see cref="CanonicalSnapshotSha256"/> only after confirming the new
    /// bytes are still well-formed PDF (qpdf <c>--check</c>, PDFium open). Drift
    /// without verification is the principal failure mode this test is designed to
    /// catch.
    /// </summary>
    [Fact]
    public void Canonical_everything_document_matches_pinned_SHA256()
    {
        var bytes = BuildCanonicalEverythingDocument();
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        Assert.True(
            string.Equals(CanonicalSnapshotSha256, actual, StringComparison.Ordinal),
            $"Canonical document hash drifted.\n  expected: {CanonicalSnapshotSha256}\n  actual:   {actual}\n  byte length: {bytes.Length}");
    }

    [Fact]
    public void Canonical_everything_document_renders_byte_equal_when_built_twice()
    {
        // The pinned-snapshot test asserts a specific hash; this test asserts the
        // weaker but more portable property (same input → same output) so the harness
        // still has signal even if a future runtime/dep update legitimately shifts
        // the canonical hash and the snapshot is in the process of being re-pinned.
        var first = BuildCanonicalEverythingDocument();
        var second = BuildCanonicalEverythingDocument();
        Assert.Equal(first, second);
    }

    /// <summary>
    /// SHA-256 hex of the canonical document built by <see cref="BuildCanonicalEverythingDocument"/>.
    /// Captured 2026-05-03 on .NET 10 (macOS arm64). To re-pin: run the test, copy the
    /// "actual" value from the failure message into here after verifying the bytes are
    /// still a valid PDF (qpdf --check / PDFium open).
    /// </summary>
    private const string CanonicalSnapshotSha256 =
        "75E83E423805B8881FD14A6CD62E0BCE82E8CB62F424D3413CD8CD8A9D8AEABC";

    // ───── Document builders (each one is its own determinism unit) ─────────

    private static byte[] BuildBlankA4()
    {
        var doc = new PdfDocument();
        doc.AddPage(MediaBoxSize.A4);
        return doc.Save();
    }

    private static byte[] BuildBlankLetterWithMetadata()
    {
        var doc = new PdfDocument
        {
            // Note: PdfLiteralString currently rejects non-ASCII; metadata strings used
            // here are ASCII-only. Hex-string metadata path is exercised elsewhere.
            Title = "Determinism - Letter",
            Author = "Roland",
            Subject = "Phase 1 Task 23",
            Keywords = "pdf, deterministic, hash",
            Creator = "NetPdf.UnitTests",
        };
        doc.AddPage(MediaBoxSize.Letter);
        return doc.Save();
    }

    private static byte[] BuildMultiPageMixedSizes()
    {
        var doc = new PdfDocument();
        doc.AddPage(MediaBoxSize.A4);
        doc.AddPage(MediaBoxSize.Letter);
        doc.AddPage(MediaBoxSize.A5);
        doc.AddPage(MediaBoxSize.Legal);
        return doc.Save();
    }

    private static byte[] BuildJpegEmbedAndPlace()
    {
        var doc = new PdfDocument();
        var jpeg = JpegImageXObject.Build(SyntheticJpeg.BuildBaseline(width: 16, height: 16, componentCount: 3));
        var imageRef = doc.RegisterImage(jpeg);
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, x: 50, y: 50, width: 200, height: 200);
        return doc.Save();
    }

    private static byte[] BuildPngOpaqueEmbed()
    {
        var doc = new PdfDocument();
        var png = PngImageXObject.Build(SyntheticPng.BuildOpaqueRgb8(16, 16));
        var imageRef = doc.RegisterImage(png);
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 0, 0, 100, 100);
        return doc.Save();
    }

    private static byte[] BuildPngRgbaWithSMask()
    {
        var doc = new PdfDocument();
        var png = PngImageXObject.Build(SyntheticPng.BuildRgba8(8, 8));
        var imageRef = doc.RegisterImage(png);
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 0, 0, 100, 100);
        return doc.Save();
    }

    private static byte[] BuildPngIndexedBinaryTrns()
    {
        var palette = new byte[] { 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF };
        var trns = new byte[] { 0x00, 0xFF, 0xFF }; // index 0 transparent, 1+2 opaque (binary)
        var doc = new PdfDocument();
        var png = PngImageXObject.Build(SyntheticPng.BuildIndexed8WithTrns(8, 8, palette, trns));
        var imageRef = doc.RegisterImage(png);
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 0, 0, 100, 100);
        return doc.Save();
    }

    private static byte[] BuildPngIndexedNonBinaryTrns()
    {
        // Non-binary tRNS forces the SMask split path through PdfDocument.
        var palette = new byte[] { 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF };
        var trns = new byte[] { 0x00, 0x80, 0xFF, 0xFF };
        var doc = new PdfDocument();
        var png = PngImageXObject.Build(SyntheticPng.BuildIndexed8WithTrns(8, 8, palette, trns));
        var imageRef = doc.RegisterImage(png);
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 0, 0, 100, 100);
        return doc.Save();
    }

    private static byte[] BuildTransparentGifViaRaster()
    {
        // GIF89a fixture is hand-crafted bytes (not Skia-encoded), so its decode + raster
        // re-encode pathway is byte-stable as long as zlib output is stable.
        var doc = new PdfDocument();
        var raster = RasterImageXObject.Build(SyntheticRasterImage.BuildTransparentGif());
        var imageRef = doc.RegisterImage(raster);
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 0, 0, 100, 100);
        return doc.Save();
    }

    private static byte[] BuildImageDedupThreeReferences()
    {
        // Same image registered three times → single XObject, three distinct page
        // placements. Every reference must serialize identically across runs.
        var doc = new PdfDocument();
        var png = PngImageXObject.Build(SyntheticPng.BuildOpaqueRgb8(8, 8));
        var refA = doc.RegisterImage(png);
        var refB = doc.RegisterImage(png); // dedup hit
        var refC = doc.RegisterImage(png); // dedup hit
        var page = doc.AddPage(MediaBoxSize.A4);
        page.PlaceImage(refA, 50, 50, 80, 80);
        page.PlaceImage(refB, 150, 150, 80, 80);
        page.PlaceImage(refC, 250, 250, 80, 80);
        return doc.Save();
    }

    private static byte[] BuildMixedImagesAlphaAndOpaque()
    {
        var doc = new PdfDocument
        {
            Title = "Mixed Images",
        };
        var jpeg = JpegImageXObject.Build(SyntheticJpeg.BuildBaseline(16, 16, 3));
        var pngOpaque = PngImageXObject.Build(SyntheticPng.BuildOpaqueRgb8(8, 8));
        var pngAlpha = PngImageXObject.Build(SyntheticPng.BuildRgba8(8, 8));

        var jpegRef = doc.RegisterImage(jpeg);
        var pngOpaqueRef = doc.RegisterImage(pngOpaque);
        var pngAlphaRef = doc.RegisterImage(pngAlpha);

        var page = doc.AddPage(MediaBoxSize.A4);
        page.PlaceImage(jpegRef, 50, 50, 100, 100);
        page.PlaceImage(pngOpaqueRef, 200, 50, 100, 100);
        page.PlaceImage(pngAlphaRef, 50, 200, 100, 100);
        return doc.Save();
    }

    private static byte[] BuildRawContentStreamByteOverload()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);
        // Mix the string and byte AppendContent overloads so both code paths are
        // exercised from a single document. The byte overload accepts non-ASCII;
        // we use ASCII operators here because the PDF reader will execute them, but
        // the determinism guarantee covers any byte payload.
        page.AppendContent("0.5 0.5 0.5 rg 10 10 50 50 re f\n");
        ReadOnlySpan<byte> raw = [(byte)'1', (byte)' ', (byte)'0', (byte)' ', (byte)'0', (byte)' ',
                                  (byte)'1', (byte)' ', (byte)'1', (byte)'0', (byte)'0', (byte)' ',
                                  (byte)'2', (byte)'0', (byte)'0', (byte)' ', (byte)'c', (byte)'m',
                                  (byte)'\n'];
        page.AppendContent(raw);
        return doc.Save();
    }

    private static byte[] BuildExplicitCreationDateUtc()
    {
        // CreationDate is the only metadata field that depends on a clock by default;
        // when set explicitly it must round-trip into bytes deterministically.
        var doc = new PdfDocument
        {
            CreationDate = new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero),
            ModDate = new DateTimeOffset(2026, 5, 3, 12, 30, 0, TimeSpan.Zero),
        };
        doc.AddPage(MediaBoxSize.A4);
        return doc.Save();
    }

    /// <summary>
    /// Canonical "everything-in" document: multiple pages, mixed metadata, all the
    /// hand-crafted image embed paths (JPEG passthrough, opaque PNG, RGBA PNG with
    /// SMask, indexed PNG with binary tRNS color-key, transparent GIF through
    /// raster), a deduped image, raw content-stream operators on each page. Pinned
    /// hash is captured once and serves as the regression net for cross-process or
    /// cross-version drift.
    /// </summary>
    private static byte[] BuildCanonicalEverythingDocument()
    {
        var doc = new PdfDocument
        {
            Title = "NetPdf Canonical",
            Author = "Roland",
            Subject = "Phase 1 Task 23 - determinism harness",
            Keywords = "pdf, deterministic",
            Creator = "NetPdf.UnitTests.PdfDocumentDeterminismHarnessTests",
            CreationDate = new DateTimeOffset(2026, 5, 3, 0, 0, 0, TimeSpan.Zero),
        };

        var jpeg = JpegImageXObject.Build(SyntheticJpeg.BuildBaseline(16, 16, 3));
        var pngOpaque = PngImageXObject.Build(SyntheticPng.BuildOpaqueRgb8(8, 8));
        var pngAlpha = PngImageXObject.Build(SyntheticPng.BuildRgba8(8, 8));
        var pngIndexedBin = PngImageXObject.Build(
            SyntheticPng.BuildIndexed8WithTrns(8, 8,
                palette: [0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF],
                trns:    [0x00, 0xFF, 0xFF]));
        var transparentGif = RasterImageXObject.Build(SyntheticRasterImage.BuildTransparentGif());

        var jpegRef = doc.RegisterImage(jpeg);
        var pngOpaqueRef = doc.RegisterImage(pngOpaque);
        var pngAlphaRef = doc.RegisterImage(pngAlpha);
        var pngIndexedBinRef = doc.RegisterImage(pngIndexedBin);
        var gifRef = doc.RegisterImage(transparentGif);
        // Dedup test: register the JPEG a second time; must reuse jpegRef.
        var jpegRefDedup = doc.RegisterImage(jpeg);
        Assert.Equal(jpegRef.ObjectNumber, jpegRefDedup.ObjectNumber);

        var p1 = doc.AddPage(MediaBoxSize.A4);
        p1.AppendContent("0.9 0.9 0.9 rg 0 0 595 842 re f\n");
        p1.PlaceImage(jpegRef, 50, 50, 100, 100);
        p1.PlaceImage(pngOpaqueRef, 200, 50, 100, 100);
        p1.PlaceImage(pngAlphaRef, 350, 50, 100, 100);

        var p2 = doc.AddPage(MediaBoxSize.Letter);
        p2.AppendContent("0 0 0 rg 0 0 612 792 re S\n");
        p2.PlaceImage(pngIndexedBinRef, 100, 100, 200, 200);
        p2.PlaceImage(gifRef, 350, 100, 200, 200);
        // Reference the JPEG again on page 2 — proves dedup carries across pages.
        p2.PlaceImage(jpegRefDedup, 100, 400, 100, 100);

        return doc.Save();
    }
}
