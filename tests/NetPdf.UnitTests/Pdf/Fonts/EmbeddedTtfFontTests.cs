// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Fonts;
using NetPdf.Pdf.Objects;
using NetPdf.Text.Fonts.OpenType;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Fonts;

/// <summary>
/// Per-class unit tests for <see cref="EmbeddedTtfFont"/>. Integration tests that re-parse
/// the embedded SFNT envelope through <see cref="OpenTypeFont.Parse"/> end-to-end live in
/// <see cref="EmbeddedTtfFontIntegrationTests"/>.
/// </summary>
public sealed class EmbeddedTtfFontTests
{
    private static EmbeddedFont BuildEmbeddedFromSyntheticFont()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { 1, 2 });
        var subset = TtfSubsetter.Subset(font, plan);
        var toUnicode = ToUnicodeCMap.FromSubset(font, plan);
        return EmbeddedTtfFont.Build(font, subset, toUnicode);
    }

    [Fact]
    public void Build_throws_when_source_font_is_not_truetype()
    {
        var font = OpenTypeFont.Parse(NetPdf.UnitTests.Text.Fonts.OpenType.Cff.SyntheticOtf.Build());
        var fakePlan = new GlyphSubsetPlan
        {
            OrderedOldGlyphIds = new[] { 0 },
            OldToNew = new Dictionary<int, int> { { 0, 0 } },
        };
        var fakeSubset = new TtfSubsetResult
        {
            Plan = fakePlan,
            SubsetBaseFontName = "AAAAAA+Stub",
            HeadBytes = ReadOnlyMemory<byte>.Empty,
            HheaBytes = ReadOnlyMemory<byte>.Empty,
            MaxpBytes = ReadOnlyMemory<byte>.Empty,
            HmtxBytes = ReadOnlyMemory<byte>.Empty,
            LocaBytes = ReadOnlyMemory<byte>.Empty,
            GlyfBytes = ReadOnlyMemory<byte>.Empty,
        };
        var fakeCmap = new ToUnicodeCMap { SubsetGlyphIdToText = new Dictionary<int, string>() };
        Assert.Throws<InvalidOperationException>(() => EmbeddedTtfFont.Build(font, fakeSubset, fakeCmap));
    }

    [Fact]
    public void Type0_dictionary_has_required_keys()
    {
        var embedded = BuildEmbeddedFromSyntheticFont();
        var type0 = embedded.Type0FontDictionary;

        Assert.Equal(PdfNames.Font, type0.Get(PdfNames.Type));
        Assert.Equal(PdfNames.Type0, type0.Get(PdfNames.Subtype));

        var baseFont = (PdfName)type0.Get(PdfNames.BaseFont)!;
        Assert.Equal(embedded.SubsetBaseFontName, baseFont.Value);

        Assert.Equal(PdfNames.IdentityH, type0.Get(PdfNames.Encoding));
        Assert.IsType<PdfArray>(type0.Get(PdfNames.DescendantFonts));
        Assert.NotNull(type0.Get(PdfNames.ToUnicode));
    }

    [Fact]
    public void CidFont_dictionary_has_CIDFontType2_subtype_and_identity_mapping()
    {
        var embedded = BuildEmbeddedFromSyntheticFont();
        var cid = embedded.CidFontDictionary;

        Assert.Equal(PdfNames.Font, cid.Get(PdfNames.Type));
        Assert.Equal(PdfNames.CIDFontType2, cid.Get(PdfNames.Subtype));
        Assert.Equal(PdfNames.Identity, cid.Get(PdfNames.CIDToGIDMap));
        Assert.IsType<PdfArray>(cid.Get(PdfNames.W));
    }

    [Fact]
    public void CidSystemInfo_holds_Adobe_Identity_zero()
    {
        var embedded = BuildEmbeddedFromSyntheticFont();
        var systemInfo = (PdfDictionary)embedded.CidFontDictionary.Get(PdfNames.CIDSystemInfo)!;

        Assert.Equal("Adobe", System.Text.Encoding.ASCII.GetString(((PdfLiteralString)systemInfo.Get(PdfNames.Registry)!).Bytes));
        Assert.Equal("Identity", System.Text.Encoding.ASCII.GetString(((PdfLiteralString)systemInfo.Get(PdfNames.Ordering)!).Bytes));
        Assert.Equal(0L, ((PdfInteger)systemInfo.Get(PdfNames.Supplement)!).Value);
    }

    [Fact]
    public void FontDescriptor_dictionary_has_font_metrics_and_FontFile2()
    {
        var embedded = BuildEmbeddedFromSyntheticFont();
        var desc = embedded.FontDescriptorDictionary;

        Assert.Equal(PdfNames.FontDescriptor, desc.Get(PdfNames.Type));
        Assert.NotNull(desc.Get(PdfNames.FontName));
        Assert.NotNull(desc.Get(PdfNames.Flags));
        Assert.NotNull(desc.Get(PdfNames.FontBBox));
        Assert.NotNull(desc.Get(PdfNames.ItalicAngle));
        Assert.NotNull(desc.Get(PdfNames.Ascent));
        Assert.NotNull(desc.Get(PdfNames.Descent));
        Assert.NotNull(desc.Get(PdfNames.CapHeight));
        Assert.NotNull(desc.Get(PdfNames.XHeight));
        Assert.NotNull(desc.Get(PdfNames.StemV));
        Assert.IsType<PdfStream>(desc.Get(PdfNames.FontFile2));
    }

    [Fact]
    public void FontFile2_stream_carries_Length1_metadata()
    {
        var embedded = BuildEmbeddedFromSyntheticFont();
        var length1 = (PdfInteger)embedded.FontFile2Stream.Dictionary.Get(PdfNames.Length1)!;
        Assert.Equal(embedded.FontFile2Stream.Data.Length, length1.Value);
    }

    [Fact]
    public void Widths_array_has_one_entry_per_subset_glyph()
    {
        var embedded = BuildEmbeddedFromSyntheticFont();
        var w = (PdfArray)embedded.CidFontDictionary.Get(PdfNames.W)!;

        // Format "[0 [w0 w1 … wN]]" — exactly two top-level entries.
        Assert.Equal(2, w.Count);
        Assert.Equal(0L, ((PdfInteger)w[0]).Value);

        var perGlyph = (PdfArray)w[1];
        // Plan included {1, 2} so subset has 3 glyphs: .notdef + 1 + 2.
        Assert.Equal(3, perGlyph.Count);
    }

    [Fact]
    public void Build_is_byte_deterministic_across_repeated_calls()
    {
        var a = BuildEmbeddedFromSyntheticFont();
        var b = BuildEmbeddedFromSyntheticFont();

        Assert.Equal(a.FontFile2Stream.Data.ToArray(), b.FontFile2Stream.Data.ToArray());
        Assert.Equal(a.SubsetBaseFontName, b.SubsetBaseFontName);
        Assert.Equal(a.ToUnicodeStream.Data.ToArray(), b.ToUnicodeStream.Data.ToArray());
    }
}
