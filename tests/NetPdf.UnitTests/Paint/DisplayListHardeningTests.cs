// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Paint;
using Xunit;

namespace NetPdf.UnitTests.Paint;

/// <summary>
/// Post-Task-5 hardening: <see cref="DisplayList"/> rejects sentinel commands at the
/// boundary, validates side-table inputs, and isolates <see cref="TextRun"/> buffers
/// from caller mutation.
/// </summary>
public sealed class DisplayListHardeningTests
{
    // ───── Reject the None sentinel at Add ────────────────────────────────────

    [Fact]
    public void Add_rejects_default_command_with_kind_None()
    {
        using var list = new DisplayList();
        var sentinel = default(DisplayCommand);
        Assert.Equal(DisplayCommandKind.None, sentinel.Kind);
        Assert.Throws<ArgumentException>(() => list.Add(sentinel));
        Assert.Equal(0, list.Count);
    }

    // ───── TextRun boundary validation ────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-0.0001)]
    [InlineData(-12.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void AddTextRun_rejects_non_positive_or_non_finite_font_size(double bad)
    {
        using var list = new DisplayList();
        var run = new TextRun
        {
            FontId = 0,
            FontSize = bad,
            Color = RgbaColor.Black,
            Text = "x",
        };
        Assert.Throws<ArgumentException>(() => list.AddTextRun(run));
    }

    [Fact]
    public void AddTextRun_rejects_mismatched_glyph_and_advance_lengths()
    {
        using var list = new DisplayList();
        var run = new TextRun
        {
            FontId = 0,
            FontSize = 12,
            Color = RgbaColor.Black,
            Text = "abc",
            GlyphIds = new ushort[] { 1, 2, 3 },
            Advances = new float[] { 10, 20 }, // 2 advances vs 3 glyphs
        };
        Assert.Throws<ArgumentException>(() => list.AddTextRun(run));
    }

    [Fact]
    public void AddTextRun_rejects_glyphs_present_with_no_advances()
    {
        using var list = new DisplayList();
        var run = new TextRun
        {
            FontId = 0,
            FontSize = 12,
            Color = RgbaColor.Black,
            Text = "abc",
            GlyphIds = new ushort[] { 1, 2, 3 },
            // Advances left default empty
        };
        Assert.Throws<ArgumentException>(() => list.AddTextRun(run));
    }

    [Fact]
    public void AddTextRun_accepts_matching_glyphs_and_advances()
    {
        using var list = new DisplayList();
        var run = new TextRun
        {
            FontId = 0,
            FontSize = 12,
            Color = RgbaColor.Black,
            Text = "ab",
            GlyphIds = new ushort[] { 1, 2 },
            Advances = new float[] { 10, 20 },
        };
        Assert.Equal(0, list.AddTextRun(run));
    }

    [Fact]
    public void AddTextRun_accepts_unshaped_run_with_both_buffers_empty()
    {
        using var list = new DisplayList();
        var run = new TextRun
        {
            FontId = -1,
            FontSize = 14,
            Color = RgbaColor.Black,
            Text = "Hello",
        };
        Assert.Equal(0, list.AddTextRun(run));
    }

    // ───── RasterImage boundary validation ────────────────────────────────────

    [Fact]
    public void AddImage_rejects_empty_encoded_bytes()
    {
        using var list = new DisplayList();
        var img = new RasterImage
        {
            EncodedBytes = ReadOnlyMemory<byte>.Empty,
            Encoding = ImageEncoding.Png,
            PixelWidth = 1,
            PixelHeight = 1,
        };
        Assert.Throws<ArgumentException>(() => list.AddImage(img));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void AddImage_rejects_non_positive_pixel_dimensions(int w, int h)
    {
        using var list = new DisplayList();
        var img = new RasterImage
        {
            EncodedBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 },
            Encoding = ImageEncoding.Png,
            PixelWidth = w,
            PixelHeight = h,
        };
        Assert.Throws<ArgumentException>(() => list.AddImage(img));
    }

    // ───── TextRun buffer isolation (copy-on-init) ────────────────────────────

    [Fact]
    public void TextRun_isolates_glyph_buffer_from_caller_mutation()
    {
        var glyphs = new ushort[] { 1, 2, 3 };
        var advances = new float[] { 10, 20, 30 };
        var run = new TextRun
        {
            FontId = 0,
            FontSize = 12,
            Color = RgbaColor.Black,
            Text = "abc",
            GlyphIds = glyphs.AsMemory(),
            Advances = advances.AsMemory(),
        };

        // Mutate the source buffers post-construction.
        glyphs[0] = 999;
        advances[0] = -1;

        // The TextRun's view stays at the original values — it copied on init.
        Assert.Equal((ushort)1, run.GlyphIds.Span[0]);
        Assert.Equal(10f, run.Advances.Span[0]);
    }

    [Fact]
    public void TextRun_empty_buffers_remain_empty_and_allocate_no_storage()
    {
        var run = new TextRun
        {
            FontId = 0,
            FontSize = 12,
            Color = RgbaColor.Black,
            Text = "hi",
        };
        Assert.True(run.GlyphIds.IsEmpty);
        Assert.True(run.Advances.IsEmpty);
    }
}
