// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Images;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Images;

/// <summary>
/// Header-parser unit tests for <see cref="JpegHeaderParser"/>. Drives every recognized
/// SOFn variant + the Adobe APP14 marker path + every reject branch using hand-built
/// JPEG byte streams from <see cref="SyntheticJpeg"/>.
/// </summary>
public sealed class JpegHeaderParserTests
{
    [Theory]
    [InlineData(100, 200, 3, 8, 0xC0)]   // SOF0 baseline DCT, RGB
    [InlineData(640, 480, 1, 8, 0xC0)]   // SOF0 grayscale
    [InlineData(800, 600, 4, 8, 0xC0)]   // SOF0 CMYK
    [InlineData(320, 240, 3, 8, 0xC1)]   // SOF1 extended sequential DCT
    [InlineData(320, 240, 3, 8, 0xC2)]   // SOF2 progressive DCT
    [InlineData(320, 240, 3, 8, 0xC3)]   // SOF3 lossless sequential
    [InlineData(320, 240, 3, 8, 0xC9)]   // SOF9 (extended sequential, arithmetic)
    [InlineData(320, 240, 3, 8, 0xCA)]   // SOF10 (progressive, arithmetic)
    [InlineData(320, 240, 3, 12, 0xC0)]  // 12-bit precision
    public void Parse_extracts_dimensions_and_components_from_recognized_SOFn_variants(
        ushort width, ushort height, byte components, byte precision, byte sofMarker)
    {
        var bytes = SyntheticJpeg.BuildBaseline(width, height, components, precision, sofMarker);
        var info = JpegHeaderParser.Parse(bytes);
        Assert.Equal(width, info.Width);
        Assert.Equal(height, info.Height);
        Assert.Equal(components, info.ComponentCount);
        Assert.Equal(precision, info.BitsPerComponent);
        Assert.False(info.IsAdobeInvertedCmyk);
    }

    [Fact]
    public void Parse_recognizes_Adobe_APP14_inverted_CMYK_marker()
    {
        var bytes = SyntheticJpeg.BuildBaseline(width: 100, height: 100, componentCount: 4,
            adobeColorTransform: 0);
        var info = JpegHeaderParser.Parse(bytes);
        Assert.True(info.IsAdobeInvertedCmyk);
    }

    [Fact]
    public void Parse_recognizes_Adobe_APP14_YCCK_color_transform_2_as_not_inverted()
    {
        var bytes = SyntheticJpeg.BuildBaseline(width: 100, height: 100, componentCount: 4,
            adobeColorTransform: 2);
        var info = JpegHeaderParser.Parse(bytes);
        Assert.False(info.IsAdobeInvertedCmyk);
    }

    [Fact]
    public void Parse_skips_through_DQT_COM_segments_before_SOFn()
    {
        var bytes = SyntheticJpeg.BuildWithPreSofnSegments(width: 50, height: 75, componentCount: 3);
        var info = JpegHeaderParser.Parse(bytes);
        Assert.Equal(50, info.Width);
        Assert.Equal(75, info.Height);
        Assert.Equal(3, info.ComponentCount);
    }

    // ───── Reject branches ───────────────────────────────────────────────────

    [Fact]
    public void Parse_rejects_too_short_input()
    {
        Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(new byte[3]));
    }

    [Fact]
    public void Parse_rejects_missing_SOI_marker()
    {
        var bytes = new byte[] { 0x00, 0x00, 0xFF, 0xC0 };
        Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(bytes));
    }

    [Fact]
    public void Parse_rejects_zero_dimension()
    {
        var bytes = SyntheticJpeg.BuildBaseline(width: 0, height: 100, componentCount: 3);
        var ex = Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(bytes));
        Assert.Contains("zero dimension", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_unsupported_component_count()
    {
        var bytes = SyntheticJpeg.BuildBaseline(width: 100, height: 100, componentCount: 2);
        var ex = Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(bytes));
        Assert.Contains("component count 2", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_non_supported_precision()
    {
        var bytes = SyntheticJpeg.BuildBaseline(width: 100, height: 100, componentCount: 3, precision: 16);
        var ex = Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(bytes));
        Assert.Contains("precision", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_DHT_and_DAC_pseudo_SOFn_markers()
    {
        // 0xC4 = DHT and 0xCC = DAC are NOT frame headers — parser must not treat them
        // as SOFn even though they fall in the 0xC0..0xCF range. We simulate by feeding
        // a JPEG that starts with SOI then DHT (which has its own length-prefixed
        // payload) and EOI — no SOFn — so the parser must scan to EOI and reject.
        using var ms = new MemoryStream();
        ms.WriteByte(0xFF); ms.WriteByte(0xD8); // SOI
        ms.WriteByte(0xFF); ms.WriteByte(0xC4); // DHT
        ms.WriteByte(0x00); ms.WriteByte(0x06); // length 6
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); // payload
        ms.WriteByte(0xFF); ms.WriteByte(0xD9); // EOI
        Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(ms.ToArray()));
    }

    [Fact]
    public void Parse_rejects_truncated_segment_length()
    {
        // SOI then a marker byte but no length follows.
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xC0 };
        Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(bytes));
    }

    [Fact]
    public void Parse_rejects_segment_length_smaller_than_2()
    {
        // Build a JPEG where the SOFn segment claims length 1 (invalid).
        using var ms = new MemoryStream();
        ms.WriteByte(0xFF); ms.WriteByte(0xD8);
        ms.WriteByte(0xFF); ms.WriteByte(0xC0);
        ms.WriteByte(0x00); ms.WriteByte(0x01); // length = 1 (invalid)
        Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(ms.ToArray()));
    }
}
