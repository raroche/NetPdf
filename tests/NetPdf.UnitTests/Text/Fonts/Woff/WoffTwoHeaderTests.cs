// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.Woff;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.Woff;

/// <summary>
/// Header trust-boundary tests for <see cref="WoffTwoHeader"/>. Drives a full
/// <see cref="SyntheticWoffTwo"/> byte stream and tweaks individual header fields to
/// pin every validation branch.
/// </summary>
public sealed class WoffTwoHeaderTests
{
    [Fact]
    public void Parse_round_trips_a_well_formed_header()
    {
        var bytes = SyntheticWoffTwo.BuildNullTransform();
        var header = WoffTwoHeader.Parse(bytes);
        Assert.Equal(WoffTwoConstants.Signature, header.Signature);
        Assert.Equal(WoffTwoConstants.FlavorTrueType, header.Flavor);
        Assert.Equal(0, header.Reserved);
        Assert.True(header.NumTables > 0);
        Assert.False(header.IsCollection);
        Assert.Equal(0u, header.MetaOffset);
        Assert.Equal(0u, header.PrivOffset);
    }

    [Fact]
    public void Parse_rejects_too_short_buffer()
    {
        var bytes = new byte[WoffTwoConstants.HeaderSize - 1];
        Assert.Throws<InvalidDataException>(() => WoffTwoHeader.Parse(bytes));
    }

    [Fact]
    public void Parse_rejects_bad_signature()
    {
        var bytes = SyntheticWoffTwo.BuildNullTransform();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 0xDEADBEEFu);
        Assert.Throws<InvalidDataException>(() => WoffTwoHeader.Parse(bytes));
    }

    [Fact]
    public void Parse_rejects_unrecognized_flavor()
    {
        var bytes = SyntheticWoffTwo.BuildNullTransform();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), 0x12345678u);
        Assert.Throws<InvalidDataException>(() => WoffTwoHeader.Parse(bytes));
    }

    [Fact]
    public void Parse_rejects_zero_numTables()
    {
        var bytes = SyntheticWoffTwo.BuildNullTransform();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12, 2), 0);
        Assert.Throws<InvalidDataException>(() => WoffTwoHeader.Parse(bytes));
    }

    [Fact]
    public void Parse_accepts_non_zero_reserved_per_decoder_robustness()
    {
        // Per W3C WOFF 2.0 §3 the encoder is required to set reserved = 0, but the spec
        // does not mandate decoder rejection on non-zero values. Real-world WOFF2 files
        // emitted by older / nonstandard encoders sometimes carry non-zero reserved bytes;
        // the Google reference decoder (woff2) does not reject them. This test pins the
        // robustness contract — any future regression to strict-reject would fail here.
        var bytes = SyntheticWoffTwo.BuildNullTransform();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(14, 2), 0xABCD);
        var header = WoffTwoHeader.Parse(bytes);
        Assert.Equal(0xABCD, header.Reserved);
    }

    [Fact]
    public void Parse_rejects_inconsistent_metadata_block()
    {
        var bytes = SyntheticWoffTwo.BuildNullTransform();
        // Write metaOffset=0x1000 but leave length=0 → inconsistent.
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(28, 4), 0x1000u);
        Assert.Throws<InvalidDataException>(() => WoffTwoHeader.Parse(bytes));
    }

    [Fact]
    public void Parse_rejects_inconsistent_private_block()
    {
        var bytes = SyntheticWoffTwo.BuildNullTransform();
        // Write privOffset but leave length=0.
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(40, 4), 0x1000u);
        Assert.Throws<InvalidDataException>(() => WoffTwoHeader.Parse(bytes));
    }

    [Fact]
    public void Parse_rejects_metadata_block_extending_past_file_end()
    {
        var bytes = SyntheticWoffTwo.BuildNullTransform();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(28, 4), (uint)bytes.Length); // offset = file length
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(32, 4), 1u);                  // length 1 → past end
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(36, 4), 1u);                  // origLength 1
        Assert.Throws<InvalidDataException>(() => WoffTwoHeader.Parse(bytes));
    }

    [Fact]
    public void Parse_recognizes_TTC_flavor_as_collection()
    {
        var bytes = SyntheticWoffTwo.BuildNullTransform();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), WoffTwoConstants.FlavorTtc);
        var header = WoffTwoHeader.Parse(bytes);
        Assert.True(header.IsCollection);
    }
}
