// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Images;
using NetPdf.Pdf.Objects;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Images;

/// <summary>
/// Tests for <see cref="JpegImageXObject"/> — the PDF Image XObject wrapper around raw
/// JPEG bytes (the DCTDecode passthrough). Verifies the produced <see cref="PdfStream"/>
/// dictionary keys match ISO 32000-2:2020 §8.9.5.
/// </summary>
public sealed class JpegImageXObjectTests
{
    private static PdfName? GetName(PdfDictionary dict, PdfName key) =>
        dict.Get(key) as PdfName;

    private static long GetInt(PdfDictionary dict, PdfName key) =>
        ((PdfInteger)dict.Get(key)!).Value;

    [Fact]
    public void Build_emits_the_required_Image_XObject_dictionary_keys()
    {
        var bytes = SyntheticJpeg.BuildBaseline(width: 200, height: 100, componentCount: 3);
        var stream = JpegImageXObject.Build(bytes);
        var d = stream.Dictionary;

        Assert.Equal("XObject", GetName(d, PdfNames.Type)!.Value);
        Assert.Equal("Image", GetName(d, PdfNames.Subtype)!.Value);
        Assert.Equal(200, GetInt(d, PdfNames.Width));
        Assert.Equal(100, GetInt(d, PdfNames.Height));
        Assert.Equal("DeviceRGB", GetName(d, PdfNames.ColorSpace)!.Value);
        Assert.Equal(8, GetInt(d, PdfNames.BitsPerComponent));
        Assert.Equal("DCTDecode", GetName(d, PdfNames.Filter)!.Value);
    }

    [Fact]
    public void Build_uses_DeviceGray_for_single_component_JPEG()
    {
        var bytes = SyntheticJpeg.BuildBaseline(width: 32, height: 32, componentCount: 1);
        var stream = JpegImageXObject.Build(bytes);
        Assert.Equal("DeviceGray", GetName(stream.Dictionary, PdfNames.ColorSpace)!.Value);
    }

    [Fact]
    public void Build_uses_DeviceCMYK_for_four_component_JPEG()
    {
        var bytes = SyntheticJpeg.BuildBaseline(width: 32, height: 32, componentCount: 4);
        var stream = JpegImageXObject.Build(bytes);
        Assert.Equal("DeviceCMYK", GetName(stream.Dictionary, PdfNames.ColorSpace)!.Value);
    }

    [Fact]
    public void Build_emits_Decode_inversion_array_for_Adobe_inverted_CMYK()
    {
        // Photoshop-saved CMYK with APP14 ColorTransform=0 → must emit /Decode [1 0 1 0 1 0 1 0]
        var bytes = SyntheticJpeg.BuildBaseline(width: 32, height: 32, componentCount: 4,
            adobeColorTransform: 0);
        var stream = JpegImageXObject.Build(bytes);
        var decode = (PdfArray)stream.Dictionary.Get(PdfNames.Decode)!;
        Assert.Equal(8, decode.Count);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(1, ((PdfInteger)decode[i * 2]).Value);
            Assert.Equal(0, ((PdfInteger)decode[i * 2 + 1]).Value);
        }
    }

    [Fact]
    public void Build_omits_Decode_for_standard_CMYK_with_APP14_ColorTransform_2()
    {
        var bytes = SyntheticJpeg.BuildBaseline(width: 32, height: 32, componentCount: 4,
            adobeColorTransform: 2);
        var stream = JpegImageXObject.Build(bytes);
        Assert.Null(stream.Dictionary.Get(PdfNames.Decode));
    }

    [Fact]
    public void Build_omits_Decode_for_standard_RGB()
    {
        var bytes = SyntheticJpeg.BuildBaseline(width: 32, height: 32, componentCount: 3);
        var stream = JpegImageXObject.Build(bytes);
        Assert.Null(stream.Dictionary.Get(PdfNames.Decode));
    }

    [Fact]
    public void Build_passes_JPEG_bytes_through_unchanged_to_the_stream_data()
    {
        var bytes = SyntheticJpeg.BuildBaseline(width: 16, height: 16, componentCount: 3);
        var stream = JpegImageXObject.Build(bytes);
        // PdfStream.Data is a ReadOnlySpan view over the same bytes — the passthrough win.
        Assert.Equal(bytes.Length, stream.Data.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            Assert.Equal(bytes[i], stream.Data[i]);
        }
    }

    [Fact]
    public void Build_throws_on_null_args()
    {
        Assert.Throws<ArgumentNullException>(() => JpegImageXObject.Build((byte[])null!));
        var bytes = SyntheticJpeg.BuildBaseline(width: 16, height: 16, componentCount: 3);
        var info = JpegHeaderParser.Parse(bytes);
        Assert.Throws<ArgumentNullException>(() => JpegImageXObject.Build(null!, info));
        Assert.Throws<ArgumentNullException>(() => JpegImageXObject.Build(bytes, null!));
    }
}
