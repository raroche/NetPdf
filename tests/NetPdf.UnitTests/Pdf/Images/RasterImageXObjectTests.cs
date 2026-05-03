// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Images;
using NetPdf.Pdf.Objects;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Images;

/// <summary>
/// Tests for <see cref="RasterImageXObject"/> — the Skia-decoded image → PDF Image
/// XObject builder. Verifies the produced <see cref="PdfStream"/> dictionary keys
/// match ISO 32000-2:2020 §8.9.5 for both the opaque and alpha-split paths.
/// </summary>
public sealed class RasterImageXObjectTests
{
    private static PdfName? GetName(PdfDictionary dict, PdfName key) => dict.Get(key) as PdfName;
    private static long GetInt(PdfDictionary dict, PdfName key) => ((PdfInteger)dict.Get(key)!).Value;

    [Fact]
    public void Opaque_WebP_emits_RGB_image_without_SMask()
    {
        var bytes = SyntheticRasterImage.BuildOpaqueWebp(width: 16, height: 8);
        var result = RasterImageXObject.Build(bytes);
        Assert.Null(result.SMask);
        var d = result.Image.Dictionary;
        Assert.Equal("XObject", GetName(d, PdfNames.Type)!.Value);
        Assert.Equal("Image", GetName(d, PdfNames.Subtype)!.Value);
        Assert.Equal(16, GetInt(d, PdfNames.Width));
        Assert.Equal(8, GetInt(d, PdfNames.Height));
        Assert.Equal("DeviceRGB", GetName(d, PdfNames.ColorSpace)!.Value);
        Assert.Equal(8, GetInt(d, PdfNames.BitsPerComponent));
        Assert.Equal("FlateDecode", GetName(d, PdfNames.Filter)!.Value);
    }

    [Fact]
    public void RGBA_WebP_emits_RGB_image_plus_DeviceGray_SMask()
    {
        var bytes = SyntheticRasterImage.BuildRgbaWebp(width: 12, height: 12, a: 0x80);
        var result = RasterImageXObject.Build(bytes);
        Assert.NotNull(result.SMask);
        var imgDict = result.Image.Dictionary;
        Assert.Equal("DeviceRGB", GetName(imgDict, PdfNames.ColorSpace)!.Value);
        Assert.Same(result.SMask, imgDict.Get(PdfNames.SMask));

        var sm = result.SMask!.Dictionary;
        Assert.Equal("DeviceGray", GetName(sm, PdfNames.ColorSpace)!.Value);
        Assert.Equal(8, GetInt(sm, PdfNames.BitsPerComponent));
        Assert.Equal(GetInt(imgDict, PdfNames.Width), GetInt(sm, PdfNames.Width));
        Assert.Equal(GetInt(imgDict, PdfNames.Height), GetInt(sm, PdfNames.Height));
    }

    [Fact]
    public void GIF_decode_round_trips_through_RasterImageXObject()
    {
        var bytes = SyntheticRasterImage.BuildMinimalGif();
        var result = RasterImageXObject.Build(bytes);
        Assert.Equal(1, GetInt(result.Image.Dictionary, PdfNames.Width));
        Assert.Equal(1, GetInt(result.Image.Dictionary, PdfNames.Height));
    }

    [Fact]
    public void Build_passes_decoded_pixel_count_into_color_stream()
    {
        // Compressed FlateDecode size is bounded by 3 × width × height (RGB) plus zlib
        // overhead. A 16×16 opaque WebP decodes to 768 raw bytes; the compressed stream
        // should be substantially smaller (solid color compresses very well).
        var bytes = SyntheticRasterImage.BuildOpaqueWebp(width: 16, height: 16);
        var result = RasterImageXObject.Build(bytes);
        Assert.True(result.Image.Data.Length > 0);
        Assert.True(result.Image.Data.Length < 16 * 16 * 3,
            "solid-color RGB should compress smaller than raw byte count");
    }

    [Fact]
    public void Build_throws_on_null_args()
    {
        Assert.Throws<ArgumentNullException>(() => RasterImageXObject.Build((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => RasterImageXObject.Build((RasterImageInfo)null!));
    }
}
