// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Images;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Images;

/// <summary>
/// Trust-boundary tests for <see cref="JpegHeaderParser"/>. The parser walks SOI →
/// SOFn → SOS+scan → EOI and rejects anything outside the documented PDF DCTDecode
/// passthrough envelope. These tests pin every accept and reject branch.
/// </summary>
public sealed class JpegHeaderParserTests
{
    // ───── Happy path: SOF0 (baseline) and SOF2 (progressive) only ───────────

    [Theory]
    [InlineData(100, 200, 3, 0xC0)]   // SOF0 baseline DCT, RGB
    [InlineData(640, 480, 1, 0xC0)]   // SOF0 grayscale
    [InlineData(800, 600, 4, 0xC0)]   // SOF0 CMYK
    [InlineData(320, 240, 3, 0xC2)]   // SOF2 progressive DCT
    [InlineData(320, 240, 1, 0xC2)]   // SOF2 grayscale
    [InlineData(320, 240, 4, 0xC2)]   // SOF2 CMYK
    public void Parse_accepts_SOF0_and_SOF2_with_valid_full_stream(
        ushort width, ushort height, byte components, byte sofMarker)
    {
        var bytes = SyntheticJpeg.BuildBaseline(width, height, components, sofMarker: sofMarker);
        var info = JpegHeaderParser.Parse(bytes);
        Assert.Equal(width, info.Width);
        Assert.Equal(height, info.Height);
        Assert.Equal(components, info.ComponentCount);
        Assert.Equal(8, info.BitsPerComponent);
        Assert.False(info.IsAdobeInvertedCmyk);
        Assert.False(info.HasIccProfile);
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

    // ───── Adobe APP14 detection ─────────────────────────────────────────────

    [Fact]
    public void Parse_recognizes_Adobe_APP14_inverted_CMYK_when_marker_appears_before_SOFn()
    {
        var bytes = SyntheticJpeg.BuildBaseline(100, 100, 4, adobeColorTransform: 0);
        var info = JpegHeaderParser.Parse(bytes);
        Assert.True(info.IsAdobeInvertedCmyk);
    }

    [Fact]
    public void Parse_recognizes_Adobe_APP14_when_marker_appears_AFTER_SOFn_but_before_SOS()
    {
        // Some encoders emit APP14 between SOF and SOS — parser must catch it.
        var bytes = SyntheticJpeg.BuildBaseline(100, 100, 4, adobeColorTransformAfterSofn: 0);
        var info = JpegHeaderParser.Parse(bytes);
        Assert.True(info.IsAdobeInvertedCmyk);
    }

    [Fact]
    public void Parse_treats_Adobe_APP14_YCCK_color_transform_2_as_not_inverted()
    {
        var bytes = SyntheticJpeg.BuildBaseline(100, 100, 4, adobeColorTransform: 2);
        var info = JpegHeaderParser.Parse(bytes);
        Assert.False(info.IsAdobeInvertedCmyk);
    }

    [Fact]
    public void Parse_detects_APP2_ICC_PROFILE_marker_via_HasIccProfile_flag()
    {
        var bytes = SyntheticJpeg.BuildBaseline(64, 64, 3, includeIccProfile: true);
        var info = JpegHeaderParser.Parse(bytes);
        Assert.True(info.HasIccProfile);
    }

    // ───── Reject: SOFn variants outside PDF DCTDecode envelope ──────────────

    [Theory]
    [InlineData(0xC1)] // SOF1 extended sequential
    [InlineData(0xC3)] // SOF3 lossless
    [InlineData(0xC5)] // SOF5 differential sequential
    [InlineData(0xC9)] // SOF9 arithmetic, extended sequential
    [InlineData(0xCA)] // SOF10 arithmetic, progressive
    [InlineData(0xCB)] // SOF11 arithmetic, lossless
    public void Parse_rejects_unsupported_SOFn_variants(byte sofMarker)
    {
        var bytes = SyntheticJpeg.BuildBaseline(100, 100, 3, sofMarker: sofMarker);
        var ex = Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(bytes));
        Assert.Contains("not supported", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_12_bit_precision()
    {
        var bytes = SyntheticJpeg.BuildBaseline(100, 100, 3, precision: 12);
        var ex = Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(bytes));
        Assert.Contains("precision", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ───── Reject: structural problems ───────────────────────────────────────

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
        var bytes = SyntheticJpeg.BuildBaseline(0, 100, 3);
        var ex = Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(bytes));
        Assert.Contains("zero dimension", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_unsupported_component_count()
    {
        var bytes = SyntheticJpeg.BuildBaseline(100, 100, 2);
        var ex = Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(bytes));
        Assert.Contains("component count 2", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_SOFn_with_segment_length_inconsistent_with_Nf()
    {
        var bytes = SyntheticJpeg.BuildSofnLengthMismatch(100, 100, 3);
        var ex = Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(bytes));
        Assert.Contains("inconsistent", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_header_only_JPEG_with_no_SOS()
    {
        var bytes = SyntheticJpeg.BuildHeaderOnly(100, 100, 3);
        Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(bytes));
    }

    [Fact]
    public void Parse_rejects_JPEG_with_SOFn_but_no_SOS_before_EOI()
    {
        var bytes = SyntheticJpeg.BuildWithoutSos(100, 100, 3);
        var ex = Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(bytes));
        Assert.Contains("SOS", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_JPEG_with_truncated_scan_data_no_EOI()
    {
        var bytes = SyntheticJpeg.BuildTruncatedScan(100, 100, 3);
        var ex = Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(bytes));
        Assert.Contains("EOI", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_truncated_segment_length()
    {
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xC0 };
        Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(bytes));
    }

    [Fact]
    public void Parse_rejects_segment_length_smaller_than_2()
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0xFF); ms.WriteByte(0xD8);
        ms.WriteByte(0xFF); ms.WriteByte(0xC0);
        ms.WriteByte(0x00); ms.WriteByte(0x01); // length = 1
        Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(ms.ToArray()));
    }

    [Fact]
    public void Parse_rejects_DHT_pseudo_SOFn_marker()
    {
        // 0xC4 = DHT, 0xCC = DAC — NOT frame headers even though in 0xC0..0xCF range.
        // A JPEG with only DHT and EOI (no SOFn) should fail.
        using var ms = new MemoryStream();
        ms.WriteByte(0xFF); ms.WriteByte(0xD8); // SOI
        ms.WriteByte(0xFF); ms.WriteByte(0xC4); // DHT
        ms.WriteByte(0x00); ms.WriteByte(0x06); // length 6
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); // payload
        ms.WriteByte(0xFF); ms.WriteByte(0xD9); // EOI
        Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(ms.ToArray()));
    }

    [Fact]
    public void Parse_rejects_multiple_SOFn_frame_headers()
    {
        // Construct a JPEG with two SOF0 segments — malformed.
        using var ms = new MemoryStream();
        ms.WriteByte(0xFF); ms.WriteByte(0xD8);
        // First SOF0
        ms.WriteByte(0xFF); ms.WriteByte(0xC0);
        ms.WriteByte(0x00); ms.WriteByte(0x11); // length 17 = 8 + 3*3
        ms.WriteByte(8);                          // precision
        ms.WriteByte(0); ms.WriteByte(0x64);      // height 100
        ms.WriteByte(0); ms.WriteByte(0x64);      // width 100
        ms.WriteByte(3);                          // components
        for (var i = 0; i < 3; i++) { ms.WriteByte((byte)(i + 1)); ms.WriteByte(0x11); ms.WriteByte(0); }
        // Second SOF0 — same layout — should be rejected.
        ms.WriteByte(0xFF); ms.WriteByte(0xC0);
        ms.WriteByte(0x00); ms.WriteByte(0x11);
        ms.WriteByte(8);
        ms.WriteByte(0); ms.WriteByte(0x64);
        ms.WriteByte(0); ms.WriteByte(0x64);
        ms.WriteByte(3);
        for (var i = 0; i < 3; i++) { ms.WriteByte((byte)(i + 1)); ms.WriteByte(0x11); ms.WriteByte(0); }
        ms.WriteByte(0xFF); ms.WriteByte(0xD9);
        var ex = Assert.Throws<InvalidDataException>(() => JpegHeaderParser.Parse(ms.ToArray()));
        Assert.Contains("multiple", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
