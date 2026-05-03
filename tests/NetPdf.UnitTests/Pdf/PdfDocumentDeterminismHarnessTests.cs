// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf.Pdf;
using NetPdf.Pdf.Images;
using NetPdf.Pdf.Objects;
using NetPdf.UnitTests.Pdf.Images;
using Xunit;
using Xunit.Abstractions;

namespace NetPdf.UnitTests.Pdf;

/// <summary>
/// Determinism harness — Task 23 + Task 23 follow-up review.
/// <para>
/// The contract every individual feature test in this codebase implicitly relies on
/// is: <b>identical input produces byte-identical output</b>. The per-feature tests
/// (e.g. <c>PdfDocumentTests.Save_is_deterministic_for_byte_equal_input</c>) prove
/// determinism for one shape; this harness pulls those checks together at the
/// orchestration layer and exercises the determinism property across many document
/// shapes simultaneously, so a regression in any one of them — a silent
/// <c>HashSet</c> iteration order leak, a thread-pool PRNG, an ambient
/// <c>DateTime.Now</c> sneaking in — gets caught here even when the per-feature
/// test still passes.
/// </para>
/// <para>
/// The harness has three layers:
/// </para>
/// <list type="bullet">
///   <item><b>Byte-equal-twice / byte-equal-thrice property tests</b>: build the same
///         document multiple times in independent <see cref="PdfDocument"/>
///         instances and assert the byte arrays are equal. Catches per-process
///         non-determinism (caches, ordered/unordered iteration, mutable static
///         state). The thrice variant catches caches that warm up wrong only on the
///         third+ build.</item>
///   <item><b>Per-shape pinned snapshots</b>: each shape carries its own SHA-256
///         pin captured per platform key. Catches drift that no longer reproduces in
///         the same process (e.g. a runtime upgrade that quietly re-orders dict
///         iteration, a shift in zlib's tie-breaking choices). Per-shape pinning
///         localizes a drift report to the affected subsystem instead of just
///         saying "canonical doc drifted, somewhere."</item>
///   <item><b>Structural sanity</b>: every emitted document is checked for the
///         well-formed-PDF shape (<c>%PDF-</c> header, <c>xref</c>, <c>startxref</c>,
///         trailing <c>%%EOF</c>). Guards the failure mode where a regression
///         produces stable-but-corrupt bytes — the SHA pin alone would still pass.
///         </item>
/// </list>
/// <para>
/// Pin scope: pins are captured per OS family + CPU architecture key
/// (<c>osx-arm64</c>, <c>linux-x64</c>, <c>win-x64</c>, etc — see
/// <see cref="DeterminismDiagnostics.CurrentPlatformKey"/>) because zlib output is
/// stable across point releases of the OS but may shift across CPU architectures or
/// major .NET runtime versions. When the harness runs on a platform key that has
/// no pin, the snapshot test logs and skips (the byte-equal-twice property test
/// still runs).
/// </para>
/// <para>
/// Image fixtures are restricted to <b>hand-crafted byte-stable</b> generators
/// (<see cref="SyntheticJpeg.BuildBaseline"/>, <see cref="SyntheticPng"/>,
/// <see cref="SyntheticRasterImage.BuildMinimalGif"/>,
/// <see cref="SyntheticRasterImage.BuildTransparentGif"/>). Skia-encoded WebP / AVIF
/// outputs are not byte-stable across SkiaSharp builds and are intentionally
/// excluded — the byte-equal-twice property would still pass for them within a
/// process, but the per-shape pin would fail across machines, which is more noise
/// than signal.
/// </para>
/// </summary>
public sealed class PdfDocumentDeterminismHarnessTests
{
    private readonly ITestOutputHelper _output;

    public PdfDocumentDeterminismHarnessTests(ITestOutputHelper output) => _output = output;

    // ───── Shape registry ────────────────────────────────────────────────────

    public static IEnumerable<object[]> AllShapes()
    {
        foreach (var key in s_shapes.Keys) yield return [key];
    }

    private static readonly IReadOnlyDictionary<string, Func<byte[]>> s_shapes =
        new Dictionary<string, Func<byte[]>>(StringComparer.Ordinal)
        {
            ["blank-a4"] = BuildBlankA4,
            ["blank-letter-with-metadata"] = BuildBlankLetterWithMetadata,
            ["multi-page-mixed-sizes"] = BuildMultiPageMixedSizes,
            ["jpeg-embed-and-place"] = BuildJpegEmbedAndPlace,
            ["png-opaque-embed"] = BuildPngOpaqueEmbed,
            ["png-rgba-with-smask"] = BuildPngRgbaWithSMask,
            ["png-indexed-binary-trns-color-key"] = BuildPngIndexedBinaryTrns,
            ["png-indexed-non-binary-trns-smask"] = BuildPngIndexedNonBinaryTrns,
            ["transparent-gif-via-raster"] = BuildTransparentGifViaRaster,
            ["image-dedup-three-references"] = BuildImageDedupThreeReferences,
            ["mixed-images-alpha-and-opaque"] = BuildMixedImagesAlphaAndOpaque,
            ["raw-content-stream-byte-overload"] = BuildRawContentStreamByteOverload,
            ["explicit-creation-date-utc"] = BuildExplicitCreationDateUtc,
            // New (review follow-up M5 / M6 shapes):
            ["explicit-creation-date-positive-half-hour-offset"] = BuildExplicitCreationDatePositiveHalfHour,
            ["explicit-creation-date-negative-offset"] = BuildExplicitCreationDateNegativeOffset,
            ["long-metadata-strings"] = BuildLongMetadataStrings,
            ["many-append-content-calls"] = BuildManyAppendContentCalls,
            ["canonical-everything"] = BuildCanonicalEverythingDocument,
        };

    // ───── Platform pin map (per OS+arch, per shape) ─────────────────────────

    /// <summary>
    /// Per-platform per-shape SHA-256 pins. Outer key:
    /// <see cref="DeterminismDiagnostics.CurrentPlatformKey"/>. Inner key: the shape
    /// name from <see cref="s_shapes"/>. To re-pin: run the harness, copy the
    /// "actual" hash from each failing shape's diagnostic into the appropriate inner
    /// dictionary, then verify well-formed-PDF and a sample byte-diff with qpdf
    /// before committing. Updating without verification is the principal failure
    /// mode this layer is designed to catch.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> s_pinsByPlatform =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            // Captured 2026-05-03 on .NET 10 / macOS arm64.
            // Re-pin protocol on legitimate drift:
            //   1. Run the Capture_all_shape_hashes_for_pinning helper (un-Skip first).
            //   2. Verify a sample of the new bytes round-trip through qpdf --check
            //      and PDFium open before re-pinning — never blindly accept drift.
            //   3. Paste the captured "<name>=<hash>" lines into this dictionary.
            //   4. Re-Skip the capture helper.
            ["osx-arm64"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["blank-a4"] = "30B65E8992D1E2A40CDEBC83B019AC78EFCFA90B3D709817AAB01FC79CEEE521",
                ["blank-letter-with-metadata"] = "52144D000FDD5AE7B3C4EBEF4B0954502AC57B8238CB07A5994F58CD38066129",
                ["multi-page-mixed-sizes"] = "349E718C987BF3AE65126CC2F2BA30F70700EA530FE9F82BD4DB20E6934EAFAE",
                ["jpeg-embed-and-place"] = "053D1E223711D27DE16445443844A984967CA39268CE4FE9CBD6C43AFA95522B",
                ["png-opaque-embed"] = "2B08A6A94CE8C3F7938D1C0330AC12D2EE9FB8258D3B8B93958A13362F15533E",
                ["png-rgba-with-smask"] = "65AE66182F57A53C4F4EEE22E319344127CE9FF897E451EF0250F5315038935A",
                ["png-indexed-binary-trns-color-key"] = "1D30BD96BA18C673F35828B276A03AD396C256AD024C2EBEAEECE6E9BAD36F79",
                ["png-indexed-non-binary-trns-smask"] = "DC603FC6E09F28CED63F52B4E004900A6737789AB0E73E1E3CFF6ACE801C2BDB",
                ["transparent-gif-via-raster"] = "6F8AE646BD068A382A4CDC252C82047C8EE0A09D8F9AE6C293B30A0432C90BB1",
                ["image-dedup-three-references"] = "9C3AD0D366F3A060EB1352D79B848166F04C130EA5B77542862A488BA359E7FE",
                ["mixed-images-alpha-and-opaque"] = "D583D3A3D758AF824804EEC37DE90564839AE651B07F3C917CA45CF79D9F3FD3",
                ["raw-content-stream-byte-overload"] = "C4FD09D25493A45B24E179F360E863450657BFF6E4D60A38974E177C83C9D611",
                ["explicit-creation-date-utc"] = "16B5A5C9E50E071691A04B498A9BB5DD040B6EFC373B63943340FF7C1DAA6883",
                ["explicit-creation-date-positive-half-hour-offset"] = "C6742A1C67C12D3DDB8FD58CD86DECCAABA357B38DB34B315D5E79318FC185E1",
                ["explicit-creation-date-negative-offset"] = "0CD67242DDD425FDCB3E8D51F3ED74DF36DA6CD3F3FDFC7FE4100BA27665FA5A",
                ["long-metadata-strings"] = "7CA255FD51DE6F0C801D03D92A21FE3AD747BF6A9E7780812CBABE956CC1E6E0",
                ["many-append-content-calls"] = "8748DD7639984FC90D18DA5C4AC7284243B8E54C28000B24F9E104673AC81D8A",
                ["canonical-everything"] = "9253363C6608706665C81580533BA2D1DF07129C660CDE8234FEFC5AE5C987CA",
            },
            // Other platforms (linux-x64, win-x64, linux-arm64) intentionally absent
            // until pins captured on those environments. The snapshot test will skip
            // with an informative log line on platforms not in this map.
        };

    // ───── Property tests: byte-equal-twice and byte-equal-thrice ────────────

    [Theory]
    [MemberData(nameof(AllShapes))]
    public void Document_shape_renders_byte_equal_when_built_twice(string shape)
    {
        var build = ResolveBuilder(shape);
        var first = build();
        var second = build();
        DeterminismDiagnostics.AssertByteEqualsWithDiagnostics(first, second);
    }

    [Theory]
    [MemberData(nameof(AllShapes))]
    public void Document_shape_renders_byte_equal_across_three_iterations(string shape)
    {
        // Stronger property: any internal cache that warms up on first build but
        // produces a different output on subsequent builds (e.g. a HashSet's iteration
        // order changing after rehash) will manifest at iteration 3 even if iterations
        // 1 and 2 happened to coincide.
        var build = ResolveBuilder(shape);
        var a = build();
        var b = build();
        var c = build();
        DeterminismDiagnostics.AssertByteEqualsWithDiagnostics(a, b);
        DeterminismDiagnostics.AssertByteEqualsWithDiagnostics(b, c);
    }

    [Theory]
    [MemberData(nameof(AllShapes))]
    public void Document_shape_emits_well_formed_PDF_bytes(string shape)
    {
        // Structural sanity: header + xref + startxref + trailing %%EOF. Cheap and
        // catches the failure mode where bytes are stable but corrupt — the SHA pin
        // alone would not surface that.
        var build = ResolveBuilder(shape);
        DeterminismDiagnostics.AssertWellFormedPdfShape(build());
    }

    [Theory]
    [MemberData(nameof(AllShapes))]
    public void Document_shape_matches_pinned_SHA256_for_current_platform(string shape)
    {
        if (!TryGetPin(shape, out var expected))
        {
            _output.WriteLine(
                $"No pinned hash for shape '{shape}' on platform key '{DeterminismDiagnostics.CurrentPlatformKey}' — snapshot check skipped. " +
                "Run the harness on this platform, capture hashes, and add to s_pinsByPlatform.");
            return;
        }
        var build = ResolveBuilder(shape);
        var bytes = build();
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            // Surface diagnostics through the test runner so a developer hitting drift
            // can paste the actual hash back into s_pinsByPlatform with one copy.
            _output.WriteLine($"Shape '{shape}' SHA-256 drifted on platform '{DeterminismDiagnostics.CurrentPlatformKey}'.");
            _output.WriteLine($"  expected:    {expected}");
            _output.WriteLine($"  actual:      {actual}");
            _output.WriteLine($"  byte length: {bytes.Length}");
            Assert.Equal(expected, actual);
        }
    }

    // ───── Pin-capture helper (TEMP — used to populate s_pinsByPlatform) ────

    [Fact(Skip = "Pin-capture utility — remove Skip and run to print all hashes for the current platform; re-add Skip after pinning.")]
    public void Capture_all_shape_hashes_for_pinning()
    {
        _output.WriteLine($"Platform key: {DeterminismDiagnostics.CurrentPlatformKey}");
        foreach (var (name, build) in s_shapes)
        {
            var bytes = build();
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            _output.WriteLine($"[\"{name}\"] = \"{hash}\",");
        }
    }

    // ───── Cross-cutting determinism: explicit /ID extraction (M4) ──────────

    [Fact]
    public void Canonical_document_has_deterministic_trailer_ID()
    {
        // /ID is auto-derived from SHA-256 of the body. Two builds of the canonical doc
        // must surface the same /ID hex pair. This test names the property explicitly
        // (covered indirectly by byte-equal-twice but easier to diagnose if /ID drifts
        // for a reason unrelated to body bytes).
        var first = BuildCanonicalEverythingDocument();
        var second = BuildCanonicalEverythingDocument();

        var idFirst = ExtractTrailerId(first);
        var idSecond = ExtractTrailerId(second);

        Assert.NotEmpty(idFirst);
        Assert.Equal(idFirst, idSecond);
    }

    // ───── Error-path determinism (M3) ────────────────────────────────────

    [Fact]
    public void RegisterImage_invalid_input_throws_identical_message_across_runs()
    {
        // If exception messages contain non-deterministic content (e.g. interpolated
        // GUIDs, time stamps, hash codes) consumers that log them get noisy output.
        // Two registrations of the same invalid input must throw the same Type and
        // the same Message.
        var notAnImage = new PdfStream([0xDE, 0xAD], new PdfDictionary());

        var (typeA, msgA) = CaptureThrow(() =>
            new PdfDocument().RegisterImage(notAnImage));
        var (typeB, msgB) = CaptureThrow(() =>
            new PdfDocument().RegisterImage(notAnImage));

        Assert.Equal(typeA, typeB);
        Assert.Equal(msgA, msgB);
    }

    [Fact]
    public void Save_after_save_throws_identical_message_across_runs()
    {
        var (typeA, msgA) = CaptureThrow(() =>
        {
            var d = new PdfDocument();
            d.AddPage(MediaBoxSize.A4);
            d.Save();
            d.Save();
        });
        var (typeB, msgB) = CaptureThrow(() =>
        {
            var d = new PdfDocument();
            d.AddPage(MediaBoxSize.A4);
            d.Save();
            d.Save();
        });

        Assert.Equal(typeA, typeB);
        Assert.Equal(msgA, msgB);
    }

    // ───── Helpers ──────────────────────────────────────────────────────────

    private static Func<byte[]> ResolveBuilder(string shape)
    {
        if (!s_shapes.TryGetValue(shape, out var build))
        {
            throw new InvalidOperationException(
                $"Unknown shape '{shape}'. The MemberData provider must only emit keys present in s_shapes.");
        }
        return build;
    }

    private static bool TryGetPin(string shape, out string pin)
    {
        if (s_pinsByPlatform.TryGetValue(DeterminismDiagnostics.CurrentPlatformKey, out var shapeMap)
            && shapeMap.TryGetValue(shape, out var hex)
            && !hex.StartsWith("PLACEHOLDER", StringComparison.Ordinal))
        {
            pin = hex;
            return true;
        }
        pin = "";
        return false;
    }

    private static (Type Type, string Message) CaptureThrow(Action action)
    {
        try { action(); }
        catch (Exception ex) { return (ex.GetType(), ex.Message); }
        Assert.Fail("Action did not throw.");
        return default; // unreachable
    }

    private static string ExtractTrailerId(byte[] bytes)
    {
        // /ID emits as `/ID [<HEX1> <HEX2>]` per ISO 32000-2 §14.4. Both hex strings
        // are 32 characters (16 bytes) when produced by NetPdf's auto-derivation.
        var ascii = Encoding.Latin1.GetString(bytes);
        var match = Regex.Match(ascii, @"/ID\s*\[\s*<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*\]");
        return match.Success ? $"{match.Groups[1].Value}|{match.Groups[2].Value}" : "";
    }

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
            // PdfLiteralString currently rejects non-ASCII metadata; the Phase 1 public
            // API forces ASCII-only Title/Author/etc. Non-ASCII metadata is a known gap
            // tracked in docs/compatibility-matrix.md.
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
        var trns = new byte[] { 0x00, 0xFF, 0xFF };
        var doc = new PdfDocument();
        var png = PngImageXObject.Build(SyntheticPng.BuildIndexed8WithTrns(8, 8, palette, trns));
        var imageRef = doc.RegisterImage(png);
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 0, 0, 100, 100);
        return doc.Save();
    }

    private static byte[] BuildPngIndexedNonBinaryTrns()
    {
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
        var doc = new PdfDocument();
        var raster = RasterImageXObject.Build(SyntheticRasterImage.BuildTransparentGif());
        var imageRef = doc.RegisterImage(raster);
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 0, 0, 100, 100);
        return doc.Save();
    }

    private static byte[] BuildImageDedupThreeReferences()
    {
        var doc = new PdfDocument();
        var png = PngImageXObject.Build(SyntheticPng.BuildOpaqueRgb8(8, 8));
        var refA = doc.RegisterImage(png);
        var refB = doc.RegisterImage(png);
        var refC = doc.RegisterImage(png);
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
        var doc = new PdfDocument
        {
            CreationDate = new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero),
            ModDate = new DateTimeOffset(2026, 5, 3, 12, 30, 0, TimeSpan.Zero),
        };
        doc.AddPage(MediaBoxSize.A4);
        return doc.Save();
    }

    private static byte[] BuildExplicitCreationDatePositiveHalfHour()
    {
        // Positive offset, with non-zero MINUTES — exercises the "+HH'mm'" path of
        // FormatPdfDate (vs. the Z-shortcut for UTC). India Standard Time is +05:30,
        // a real-world half-hour offset.
        var doc = new PdfDocument
        {
            CreationDate = new DateTimeOffset(2026, 5, 3, 12, 0, 0, new TimeSpan(5, 30, 0)),
        };
        doc.AddPage(MediaBoxSize.A4);
        return doc.Save();
    }

    private static byte[] BuildExplicitCreationDateNegativeOffset()
    {
        // Negative offset — exercises the "-HH'mm'" path. PST is -08:00.
        var doc = new PdfDocument
        {
            CreationDate = new DateTimeOffset(2026, 5, 3, 12, 0, 0, new TimeSpan(-8, 0, 0)),
        };
        doc.AddPage(MediaBoxSize.A4);
        return doc.Save();
    }

    private static byte[] BuildLongMetadataStrings()
    {
        // Boundary-test the literal-string escape path with longer ASCII metadata
        // including PDF-syntax-special characters (parens, backslashes) that must be
        // escaped per ISO 32000-2 §7.3.4.2.
        var doc = new PdfDocument
        {
            Title = "A long title with (parens) and \\backslashes\\ and many ASCII characters " +
                    "to exercise the literal-string escape path under volume.",
            Author = string.Concat(Enumerable.Repeat("Author ", 16)).TrimEnd(),
            Subject = "(nested (parens (in subject)))",
            Keywords = string.Join(", ", Enumerable.Range(0, 32).Select(i => $"keyword{i}")),
            Creator = "NetPdf",
        };
        doc.AddPage(MediaBoxSize.A4);
        return doc.Save();
    }

    private static byte[] BuildManyAppendContentCalls()
    {
        // Mix string + byte AppendContent calls many times. Catches any subtle
        // accumulator non-determinism (e.g. ArrayBufferWriter growth thresholds
        // shifting hash boundaries).
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);
        for (var i = 0; i < 64; i++)
        {
            page.AppendContent($"q 1 0 0 1 {i * 5} {i * 5} cm 0.{i % 10} 0.{(i + 3) % 10} 0.{(i + 7) % 10} rg 10 10 20 20 re f Q\n");
            ReadOnlySpan<byte> nl = [(byte)'\n'];
            page.AppendContent(nl);
        }
        return doc.Save();
    }

    /// <summary>
    /// Canonical "everything-in" document: multiple pages, mixed metadata, all the
    /// hand-crafted image embed paths (JPEG passthrough, opaque PNG, RGBA PNG with
    /// SMask, indexed PNG with binary tRNS color-key, transparent GIF through
    /// raster), a deduped image, raw content-stream operators on each page.
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
        p2.PlaceImage(jpegRefDedup, 100, 400, 100, 100);

        return doc.Save();
    }
}
