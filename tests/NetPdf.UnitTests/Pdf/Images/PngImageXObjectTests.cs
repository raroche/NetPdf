// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Images;
using NetPdf.Pdf.Objects;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Images;

/// <summary>
/// Tests for <see cref="PngImageXObject"/> — the PDF Image XObject + optional SMask
/// builder. Verifies the produced <see cref="PdfStream"/> dictionary keys match ISO
/// 32000-2:2020 §8.9.5 for both the opaque-passthrough path and the alpha-split path.
/// </summary>
public sealed class PngImageXObjectTests
{
    private static PdfName? GetName(PdfDictionary dict, PdfName key) => dict.Get(key) as PdfName;
    private static long GetInt(PdfDictionary dict, PdfName key) => ((PdfInteger)dict.Get(key)!).Value;

    // ───── Opaque passthrough path ───────────────────────────────────────────

    [Fact]
    public void Opaque_grayscale_PNG_emits_FlateDecode_with_DeviceGray_and_predictor_15()
    {
        var bytes = SyntheticPng.BuildOpaqueGrayscale8(width: 32, height: 16);
        var result = PngImageXObject.Build(bytes);
        Assert.Null(result.SMask);
        var d = result.Image.Dictionary;
        Assert.Equal("XObject", GetName(d, PdfNames.Type)!.Value);
        Assert.Equal("Image", GetName(d, PdfNames.Subtype)!.Value);
        Assert.Equal(32, GetInt(d, PdfNames.Width));
        Assert.Equal(16, GetInt(d, PdfNames.Height));
        Assert.Equal("DeviceGray", GetName(d, PdfNames.ColorSpace)!.Value);
        Assert.Equal(8, GetInt(d, PdfNames.BitsPerComponent));
        Assert.Equal("FlateDecode", GetName(d, PdfNames.Filter)!.Value);

        // /DecodeParms with /Predictor 15.
        var dp = (PdfDictionary)d.Get(PdfNames.DecodeParms)!;
        Assert.Equal(15, ((PdfInteger)dp.Get(PdfNames.Predictor)!).Value);
        Assert.Equal(32, ((PdfInteger)dp.Get(PdfNames.Columns)!).Value);
        Assert.Equal(1, ((PdfInteger)dp.Get(PdfNames.Colors)!).Value);
        Assert.Equal(8, ((PdfInteger)dp.Get(PdfNames.BitsPerComponent)!).Value);
    }

    [Fact]
    public void Opaque_RGB_PNG_emits_DeviceRGB_and_three_colors()
    {
        var bytes = SyntheticPng.BuildOpaqueRgb8(width: 16, height: 8);
        var result = PngImageXObject.Build(bytes);
        Assert.Null(result.SMask);
        var d = result.Image.Dictionary;
        Assert.Equal("DeviceRGB", GetName(d, PdfNames.ColorSpace)!.Value);
        var dp = (PdfDictionary)d.Get(PdfNames.DecodeParms)!;
        Assert.Equal(3, ((PdfInteger)dp.Get(PdfNames.Colors)!).Value);
    }

    [Fact]
    public void Indexed_PNG_emits_Indexed_DeviceRGB_palette_color_space()
    {
        var palette = new byte[] { 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF };
        var bytes = SyntheticPng.BuildIndexed8(width: 8, height: 8, palette: palette);
        var result = PngImageXObject.Build(bytes);
        Assert.Null(result.SMask);
        var d = result.Image.Dictionary;
        var cs = (PdfArray)d.Get(PdfNames.ColorSpace)!;
        Assert.Equal(4, cs.Count);
        Assert.Equal("Indexed", ((PdfName)cs[0]).Value);
        Assert.Equal("DeviceRGB", ((PdfName)cs[1]).Value);
        Assert.Equal(3, ((PdfInteger)cs[2]).Value); // hival = entries - 1
        // Palette bytes are stored as a hex string.
        var paletteHex = (PdfHexString)cs[3];
        Assert.Equal(palette.Length, paletteHex.Bytes.Length);
        Assert.Equal(palette, paletteHex.Bytes.ToArray());
    }

    [Fact]
    public void Opaque_PNG_passes_IDAT_bytes_through_without_re_compression()
    {
        // The opaque-path stream data should equal the parser-extracted IDAT bytes
        // verbatim (no decode-recompress cycle).
        var bytes = SyntheticPng.BuildOpaqueRgb8(width: 16, height: 8);
        var info = PngHeaderParser.Parse(bytes);
        var result = PngImageXObject.Build(info);

        Assert.Equal(info.CompressedIdatBytes.Length, result.Image.Data.Length);
        for (var i = 0; i < info.CompressedIdatBytes.Length; i++)
        {
            Assert.Equal(info.CompressedIdatBytes.Span[i], result.Image.Data[i]);
        }
    }

    // ───── Alpha split path ──────────────────────────────────────────────────

    [Fact]
    public void RGBA_PNG_emits_Image_plus_SMask()
    {
        var bytes = SyntheticPng.BuildRgba8(width: 16, height: 8);
        var result = PngImageXObject.Build(bytes);

        Assert.NotNull(result.SMask);

        // Color image: DeviceRGB, 8-bit, FlateDecode (no predictor on alpha-split path).
        var imgDict = result.Image.Dictionary;
        Assert.Equal("DeviceRGB", GetName(imgDict, PdfNames.ColorSpace)!.Value);
        Assert.Equal(8, GetInt(imgDict, PdfNames.BitsPerComponent));
        Assert.Equal("FlateDecode", GetName(imgDict, PdfNames.Filter)!.Value);
        Assert.Null(imgDict.Get(PdfNames.DecodeParms));

        // SMask referenced from /SMask key.
        Assert.Same(result.SMask, imgDict.Get(PdfNames.SMask));

        // SMask: DeviceGray, 8-bit, FlateDecode.
        var sm = result.SMask!.Dictionary;
        Assert.Equal("DeviceGray", GetName(sm, PdfNames.ColorSpace)!.Value);
        Assert.Equal(8, GetInt(sm, PdfNames.BitsPerComponent));
        Assert.Equal("FlateDecode", GetName(sm, PdfNames.Filter)!.Value);
        Assert.Equal(GetInt(imgDict, PdfNames.Width), GetInt(sm, PdfNames.Width));
        Assert.Equal(GetInt(imgDict, PdfNames.Height), GetInt(sm, PdfNames.Height));
    }

    [Fact]
    public void GrayscaleAlpha_PNG_emits_DeviceGray_Image_plus_SMask()
    {
        var bytes = SyntheticPng.BuildGrayscaleAlpha8(width: 16, height: 8);
        var result = PngImageXObject.Build(bytes);

        Assert.NotNull(result.SMask);
        Assert.Equal("DeviceGray", GetName(result.Image.Dictionary, PdfNames.ColorSpace)!.Value);
        Assert.Equal("DeviceGray", GetName(result.SMask!.Dictionary, PdfNames.ColorSpace)!.Value);
    }

    // ───── Reject paths ──────────────────────────────────────────────────────

    [Fact]
    public void Build_rejects_interlaced_PNG()
    {
        var bytes = SyntheticPng.BuildInterlaced(width: 16, height: 8);
        Assert.Throws<NotSupportedException>(() => PngImageXObject.Build(bytes));
    }

    [Fact]
    public void Build_throws_on_null_args()
    {
        Assert.Throws<ArgumentNullException>(() => PngImageXObject.Build((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => PngImageXObject.Build((PngImageInfo)null!));
    }

    // ───── tRNS color-key /Mask paths ────────────────────────────────────────

    [Fact]
    public void Grayscale_with_tRNS_emits_color_key_Mask_array()
    {
        var bytes = SyntheticPng.BuildOpaqueGrayscale8WithTrns(16, 8, transparentGray: 0x80);
        var result = PngImageXObject.Build(bytes);
        Assert.Null(result.SMask);
        var d = result.Image.Dictionary;
        // Color is still passthrough — DeviceGray + Predictor 15.
        Assert.Equal("DeviceGray", GetName(d, PdfNames.ColorSpace)!.Value);
        // /Mask [v v] for the transparent gray value.
        var mask = (PdfArray)d.Get(PdfNames.Mask)!;
        Assert.Equal(2, mask.Count);
        Assert.Equal(0x80, ((PdfInteger)mask[0]).Value);
        Assert.Equal(0x80, ((PdfInteger)mask[1]).Value);
    }

    [Fact]
    public void RGB_with_tRNS_emits_color_key_Mask_array_with_six_entries()
    {
        var bytes = SyntheticPng.BuildOpaqueRgb8WithTrns(16, 8, tr: 0xFF, tg: 0x00, tb: 0x80);
        var result = PngImageXObject.Build(bytes);
        Assert.Null(result.SMask);
        var d = result.Image.Dictionary;
        Assert.Equal("DeviceRGB", GetName(d, PdfNames.ColorSpace)!.Value);
        var mask = (PdfArray)d.Get(PdfNames.Mask)!;
        Assert.Equal(6, mask.Count);
        // [r r g g b b]
        Assert.Equal(0xFF, ((PdfInteger)mask[0]).Value);
        Assert.Equal(0xFF, ((PdfInteger)mask[1]).Value);
        Assert.Equal(0x00, ((PdfInteger)mask[2]).Value);
        Assert.Equal(0x00, ((PdfInteger)mask[3]).Value);
        Assert.Equal(0x80, ((PdfInteger)mask[4]).Value);
        Assert.Equal(0x80, ((PdfInteger)mask[5]).Value);
    }

    [Fact]
    public void Indexed_with_binary_tRNS_emits_color_key_Mask_for_transparent_indices()
    {
        // Palette has 4 entries; tRNS marks index 0 transparent (0) + others opaque (255).
        var palette = new byte[] { 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF };
        var trns = new byte[] { 0x00, 0xFF, 0xFF, 0xFF };
        var bytes = SyntheticPng.BuildIndexed8WithTrns(8, 8, palette, trns);
        var result = PngImageXObject.Build(bytes);
        Assert.Null(result.SMask); // binary alpha → /Mask path, not SMask
        var d = result.Image.Dictionary;
        var mask = (PdfArray)d.Get(PdfNames.Mask)!;
        Assert.Equal(2, mask.Count); // only index 0 is transparent
        Assert.Equal(0, ((PdfInteger)mask[0]).Value);
        Assert.Equal(0, ((PdfInteger)mask[1]).Value);
    }

    [Fact]
    public void Indexed_with_non_binary_tRNS_emits_SMask()
    {
        var palette = new byte[] { 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF };
        var trns = new byte[] { 0x00, 0x80, 0xFF, 0xFF }; // index 1 has intermediate alpha (128)
        var bytes = SyntheticPng.BuildIndexed8WithTrns(8, 8, palette, trns);
        var result = PngImageXObject.Build(bytes);
        Assert.NotNull(result.SMask);
        // Image still uses indexed passthrough; SMask provides per-pixel alpha.
        var d = result.Image.Dictionary;
        Assert.IsType<PdfArray>(d.Get(PdfNames.ColorSpace)); // /Indexed array
        Assert.Same(result.SMask, d.Get(PdfNames.SMask));
        var sm = result.SMask!.Dictionary;
        Assert.Equal("DeviceGray", GetName(sm, PdfNames.ColorSpace)!.Value);
        Assert.Equal(8, GetInt(sm, PdfNames.BitsPerComponent));
    }
}
