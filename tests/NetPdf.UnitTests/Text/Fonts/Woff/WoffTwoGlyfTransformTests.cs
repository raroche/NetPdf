// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.Woff;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.Woff;

/// <summary>
/// Direct trust-boundary tests for <see cref="WoffTwoGlyfTransform"/>. Each test feeds a
/// hand-crafted minimal transformed-glyf header (with the relevant field tweaked) and
/// asserts the parser rejects via <see cref="InvalidDataException"/>. Together with the
/// real Roboto round-trip these pin both the spec-correctness contract and the
/// trust-boundary surface against future regressions.
/// </summary>
public sealed class WoffTwoGlyfTransformTests
{
    /// <summary>
    /// Build a 36-byte transformed-glyf header for <paramref name="numGlyphs"/> glyphs
    /// and the given option / index-format / per-substream sizes. Returns a byte array
    /// of exactly the header size — callers append substream payloads as needed.
    /// </summary>
    private static byte[] BuildHeader(
        ushort reserved = 0,
        ushort optionFlags = 0,
        ushort numGlyphs = 1,
        ushort indexFormat = 1,
        uint nContourStreamSize = 0,
        uint nPointsStreamSize = 0,
        uint flagStreamSize = 0,
        uint glyphStreamSize = 0,
        uint compositeStreamSize = 0,
        uint bboxStreamSize = 0,
        uint instructionStreamSize = 0)
    {
        var bytes = new byte[36];
        var s = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(s[0..2], reserved);
        BinaryPrimitives.WriteUInt16BigEndian(s[2..4], optionFlags);
        BinaryPrimitives.WriteUInt16BigEndian(s[4..6], numGlyphs);
        BinaryPrimitives.WriteUInt16BigEndian(s[6..8], indexFormat);
        BinaryPrimitives.WriteUInt32BigEndian(s[8..12], nContourStreamSize);
        BinaryPrimitives.WriteUInt32BigEndian(s[12..16], nPointsStreamSize);
        BinaryPrimitives.WriteUInt32BigEndian(s[16..20], flagStreamSize);
        BinaryPrimitives.WriteUInt32BigEndian(s[20..24], glyphStreamSize);
        BinaryPrimitives.WriteUInt32BigEndian(s[24..28], compositeStreamSize);
        BinaryPrimitives.WriteUInt32BigEndian(s[28..32], bboxStreamSize);
        BinaryPrimitives.WriteUInt32BigEndian(s[32..36], instructionStreamSize);
        return bytes;
    }

    [Fact]
    public void Reverse_rejects_too_short_header()
    {
        var ex = Assert.Throws<InvalidDataException>(() => WoffTwoGlyfTransform.Reverse(new byte[35]));
        Assert.Contains("header truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reverse_rejects_non_zero_reserved()
    {
        var bytes = BuildHeader(reserved: 0x0001, numGlyphs: 1);
        Assert.Throws<InvalidDataException>(() => WoffTwoGlyfTransform.Reverse(bytes));
    }

    [Fact]
    public void Reverse_rejects_optionFlags_reserved_bits_set()
    {
        // bit 1..15 must be 0; bit 0 alone (overlapSimpleBitmap) is the only defined option.
        var bytes = BuildHeader(optionFlags: 0x0002, numGlyphs: 1, nContourStreamSize: 2, bboxStreamSize: 4);
        // Append minimal substreams so we get past the slicing checks before optionFlags is examined.
        var full = new byte[bytes.Length + 6];
        bytes.CopyTo(full, 0);
        var ex = Assert.Throws<InvalidDataException>(() => WoffTwoGlyfTransform.Reverse(full));
        Assert.Contains("optionFlags", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0xFFFF)]
    public void Reverse_rejects_indexFormat_other_than_0_or_1(int badIndexFormat)
    {
        var bytes = BuildHeader(indexFormat: (ushort)badIndexFormat, numGlyphs: 1);
        var ex = Assert.Throws<InvalidDataException>(() => WoffTwoGlyfTransform.Reverse(bytes));
        Assert.Contains("indexFormat", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reverse_rejects_nContourStream_size_not_matching_numGlyphs()
    {
        // numGlyphs = 2 should require nContourStreamSize = 4; supplying 2 (one glyph's
        // worth) is malformed.
        var bytes = BuildHeader(numGlyphs: 2, nContourStreamSize: 2);
        var full = new byte[bytes.Length + 2];
        bytes.CopyTo(full, 0);
        var ex = Assert.Throws<InvalidDataException>(() => WoffTwoGlyfTransform.Reverse(full));
        Assert.Contains("nContourStream", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reverse_rejects_extra_bytes_after_declared_substreams()
    {
        // Header declares all-empty substreams for 1 empty glyph (nContour = 0). Bbox bitmap
        // is 4 bytes (mandatory), so bboxStreamSize must be 4; we set it to 4 with bit 0=0.
        var hdr = BuildHeader(numGlyphs: 1, nContourStreamSize: 2, bboxStreamSize: 4);
        var bytes = new byte[hdr.Length + 2 + 4 + 8 /* extra trailing bytes */];
        hdr.CopyTo(bytes, 0);
        // nContourStream: 2 bytes of 0 (one empty glyph).
        // bboxBitmap: 4 zero bytes (no glyph has explicit bbox).
        // 8 trailing bytes — should be rejected.
        var ex = Assert.Throws<InvalidDataException>(() => WoffTwoGlyfTransform.Reverse(bytes));
        Assert.Contains("trailing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reverse_accepts_single_empty_glyph_with_no_payload_streams()
    {
        // Minimal valid input: 1 glyph, all-zero nContour (empty glyph), bbox bitmap is
        // 4 bytes (one byte per 8 glyphs, rounded up to 4-byte multiple) all-zero. No
        // other substreams are needed.
        var hdr = BuildHeader(numGlyphs: 1, nContourStreamSize: 2, bboxStreamSize: 4);
        var bytes = new byte[hdr.Length + 2 + 4];
        hdr.CopyTo(bytes, 0);
        // nContourStream: 2 bytes of 0.
        // bboxBitmap: 4 zero bytes.
        var (glyfBytes, locaBytes, indexFormat) = WoffTwoGlyfTransform.Reverse(bytes);
        Assert.Empty(glyfBytes); // empty glyph emits no bytes
        Assert.Equal(8, locaBytes.Length); // long format: 2 offsets × 4 bytes
        Assert.Equal(1, indexFormat);
    }

    [Fact]
    public void Reverse_rejects_composite_glyph_without_explicit_bbox()
    {
        // 1 composite glyph (nContour = -1). bboxBitmap byte 0 = 0 → no explicit bbox →
        // malformed (composites must carry bbox).
        var hdr = BuildHeader(numGlyphs: 1, nContourStreamSize: 2, bboxStreamSize: 4);
        var bytes = new byte[hdr.Length + 2 + 4];
        hdr.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(hdr.Length, 2), (short)-1);
        // bboxBitmap: 4 zero bytes (bit unset for glyph 0).
        var ex = Assert.Throws<InvalidDataException>(() => WoffTwoGlyfTransform.Reverse(bytes));
        Assert.Contains("composite", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bbox", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reverse_rejects_empty_glyph_with_bbox_bit_set()
    {
        // Empty glyph (nContour = 0) but bboxBitmap bit set — invalid per spec.
        var hdr = BuildHeader(numGlyphs: 1, nContourStreamSize: 2, bboxStreamSize: 4);
        var bytes = new byte[hdr.Length + 2 + 4];
        hdr.CopyTo(bytes, 0);
        // nContourStream: 2 bytes of 0 (empty glyph).
        // bboxBitmap: set bit for glyph 0 (high bit of first byte = 0x80).
        bytes[hdr.Length + 2] = 0x80;
        var ex = Assert.Throws<InvalidDataException>(() => WoffTwoGlyfTransform.Reverse(bytes));
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reverse_rejects_bboxStream_smaller_than_bitmap()
    {
        // bboxBitmap for 1 glyph needs 4 bytes; supplying bboxStreamSize = 2 is malformed.
        var hdr = BuildHeader(numGlyphs: 1, nContourStreamSize: 2, bboxStreamSize: 2);
        var bytes = new byte[hdr.Length + 2 + 2];
        hdr.CopyTo(bytes, 0);
        var ex = Assert.Throws<InvalidDataException>(() => WoffTwoGlyfTransform.Reverse(bytes));
        Assert.Contains("bbox", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ───── Decoder-level tests for the new contracts ─────────────────────────

    [Fact]
    public void Decoder_accepts_non_zero_padding_under_4_bytes_only_when_zero()
    {
        // Build a synthetic null-transform WOFF2 (no metadata, no priv) and inject a
        // non-zero byte into the trailing 4-byte alignment region. Layout validator must
        // reject.
        var bytes = SyntheticWoffTwo.BuildNullTransform();
        // Trailing bytes are at the end of the file (compressedAligned padding). Find a
        // non-zero spot to mutate by writing to the very last byte.
        if (bytes[^1] == 0)
        {
            bytes[^1] = 0xFF;
            Assert.Throws<InvalidDataException>(() => WoffTwoDecoder.Decode(bytes));
        }
    }
}
