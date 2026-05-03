// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.OpenType;
using NetPdf.Text.Fonts.Woff;
using Xunit;
using Xunit.Abstractions;

namespace NetPdf.UnitTests.Text.Fonts.Woff;

/// <summary>
/// End-to-end integration tests against a real-world Google Fonts WOFF 2.0 file
/// (<c>Roboto-Regular.woff2</c>, the v32 Latin subset shipped via fonts.gstatic.com).
/// These exercise the full Phase B path: header → directory → Brotli → glyf transform
/// reversal (substream walk + 128-entry triplet decoding + simple/composite glyph
/// reconstruction) → loca rebuild → SFNT reassembly. The decoded SFNT must round-trip
/// through the project's own <c>OpenTypeFont.Parse</c>.
/// </summary>
public sealed class WoffTwoRealFontTests
{
    private const string RobotoResource = "NetPdf.UnitTests.Resources.Fonts.Roboto-Regular.woff2";

    private readonly ITestOutputHelper _output;

    public WoffTwoRealFontTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static byte[] LoadRobotoBytes()
    {
        using var stream = typeof(WoffTwoRealFontTests).Assembly.GetManifestResourceStream(RobotoResource)
            ?? throw new InvalidOperationException($"Test resource '{RobotoResource}' missing.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Resource_loads_and_has_woff2_signature()
    {
        var bytes = LoadRobotoBytes();
        Assert.True(bytes.Length > 1000, "real WOFF2 should be at least a few KB");
        // 'wOF2' = 0x77 0x4F 0x46 0x32
        Assert.Equal(0x77, bytes[0]);
        Assert.Equal(0x4F, bytes[1]);
        Assert.Equal(0x46, bytes[2]);
        Assert.Equal(0x32, bytes[3]);
    }

    [Fact]
    public void Decode_produces_a_well_formed_TrueType_SFNT()
    {
        var bytes = LoadRobotoBytes();
        var sfnt = WoffTwoDecoder.Decode(bytes);
        _output.WriteLine($"Decoded SFNT: {sfnt.Length} bytes (input WOFF2: {bytes.Length} bytes).");
        Assert.True(sfnt.Length > bytes.Length, "decompressed SFNT should be larger than the compressed WOFF2");
        // TTF magic at byte 0: 0x00010000.
        Assert.Equal(0x00, sfnt[0]);
        Assert.Equal(0x01, sfnt[1]);
        Assert.Equal(0x00, sfnt[2]);
        Assert.Equal(0x00, sfnt[3]);
    }

    [Fact]
    public void Decoded_SFNT_round_trips_through_OpenTypeFont_Parse()
    {
        var bytes = LoadRobotoBytes();
        var sfnt = WoffTwoDecoder.Decode(bytes);
        var font = OpenTypeFont.Parse(sfnt);

        Assert.True(font.HasTrueTypeOutlines, "Roboto Regular ships with TTF outlines");
        Assert.True(font.Maxp.NumGlyphs > 0, "must have glyphs");
        Assert.True(font.Head.UnitsPerEm > 0);
        _output.WriteLine($"Roboto: {font.Maxp.NumGlyphs} glyphs, unitsPerEm={font.Head.UnitsPerEm}, family={font.Name.FamilyName}");
        Assert.NotNull(font.Name.FamilyName);
        Assert.Contains("Roboto", font.Name.FamilyName!);
    }

    [Fact]
    public void Decoded_SFNT_contains_a_loca_consistent_with_glyf()
    {
        var bytes = LoadRobotoBytes();
        var sfnt = WoffTwoDecoder.Decode(bytes);
        var font = OpenTypeFont.Parse(sfnt);

        Assert.NotNull(font.Loca);
        Assert.NotNull(font.Glyf);
        // loca has numGlyphs + 1 offsets; the last must equal the glyf table size.
        Assert.Equal(font.Maxp.NumGlyphs + 1, font.Loca!.Offsets.Length);
        Assert.Equal((uint)font.Glyf!.RawBytes.Length, font.Loca.Offsets[^1]);
    }

    [Fact]
    public void Decode_is_deterministic_for_the_same_input()
    {
        var bytes = LoadRobotoBytes();
        var first = WoffTwoDecoder.Decode(bytes);
        var second = WoffTwoDecoder.Decode(bytes);
        Assert.Equal(first, second);
    }
}
