// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts;
using NetPdf.Text.Fonts.OpenType;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts;

/// <summary>
/// Weight-clamping trust-boundary tests for <see cref="FontMetadata.Extract"/>. Builds a
/// minimal valid TTF via <see cref="SyntheticFont.Build"/> and rewrites the
/// <c>OS/2.usWeightClass</c> bytes in place to exercise malformed values without needing
/// a real font on disk.
/// </summary>
public sealed class FontMetadataWeightClampTests
{
    [Fact]
    public void Extract_normalizes_zero_weight_to_400()
    {
        var bytes = BuildSyntheticFontWithWeight(0);
        var meta = FontMetadata.Extract(OpenTypeFont.Parse(bytes));
        Assert.Equal(400, meta.WeightCss);
    }

    [Fact]
    public void Extract_clamps_weight_greater_than_1000_to_400()
    {
        // The reviewer's spec: malformed OS/2.usWeightClass values (out of CSS 1..1000) are
        // treated as "missing" and normalized to 400 (CSS normal). Pin both sides of the bound.
        var bytes = BuildSyntheticFontWithWeight(1500);
        var meta = FontMetadata.Extract(OpenTypeFont.Parse(bytes));
        Assert.Equal(400, meta.WeightCss);
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(2, 200)]
    [InlineData(4, 400)]
    [InlineData(7, 700)]
    [InlineData(9, 900)]
    public void Extract_scales_legacy_1_to_9_weight_into_CSS_100_to_900(int legacyWeight, int expectedCssWeight)
    {
        var bytes = BuildSyntheticFontWithWeight((ushort)legacyWeight);
        var meta = FontMetadata.Extract(OpenTypeFont.Parse(bytes));
        Assert.Equal(expectedCssWeight, meta.WeightCss);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(400)]
    [InlineData(700)]
    [InlineData(900)]
    [InlineData(1000)]
    public void Extract_passes_through_in_range_CSS_weight_unchanged(int weight)
    {
        var bytes = BuildSyntheticFontWithWeight((ushort)weight);
        var meta = FontMetadata.Extract(OpenTypeFont.Parse(bytes));
        Assert.Equal(weight, meta.WeightCss);
    }

    [Fact]
    public void Extract_emitted_weight_is_always_within_CSS_1_to_1000_range()
    {
        // Cover edge values to pin the contract.
        foreach (var input in new ushort[] { 0, 1, 9, 10, 400, 1000, 1001, 65535 })
        {
            var bytes = BuildSyntheticFontWithWeight(input);
            var meta = FontMetadata.Extract(OpenTypeFont.Parse(bytes));
            Assert.InRange(meta.WeightCss, 1, 1000);
        }
    }

    /// <summary>
    /// Build a SyntheticFont byte stream and overwrite the OS/2.usWeightClass field (16-bit
    /// big-endian at OS/2-relative offset 4) so the parsed font carries
    /// <paramref name="weight"/> as its raw OS/2 weight value.
    /// </summary>
    private static byte[] BuildSyntheticFontWithWeight(ushort weight)
    {
        var bytes = SyntheticFont.Build();
        // Walk the SFNT directory to find the OS/2 table.
        var span = bytes.AsSpan();
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(span[4..6]);
        const int sfntHeaderSize = 12;
        const int recordSize = 16;
        for (var i = 0; i < numTables; i++)
        {
            var recOffset = sfntHeaderSize + i * recordSize;
            var tag = BinaryPrimitives.ReadUInt32BigEndian(span[recOffset..(recOffset + 4)]);
            if (tag != 0x4F532F32u) continue; // 'OS/2'
            var tableOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(recOffset + 8)..(recOffset + 12)]);
            // OS/2 weight is at offset 4..6 inside the table.
            BinaryPrimitives.WriteUInt16BigEndian(span[(tableOffset + 4)..(tableOffset + 6)], weight);
            return bytes;
        }
        throw new InvalidOperationException("OS/2 table not found in SyntheticFont byte stream.");
    }
}
