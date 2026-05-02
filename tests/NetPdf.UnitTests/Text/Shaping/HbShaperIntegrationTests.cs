// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Shaping;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Shaping;

/// <summary>
/// Integration tests that run the HarfBuzzSharp native shaper end-to-end against the
/// synthetic font. Verifies the cmap-driven glyph resolution path, the font-units →
/// pixels scaling done in <see cref="HbShaper"/>, and per-glyph cluster preservation.
/// </summary>
public sealed class HbShaperIntegrationTests
{
    [Fact]
    public void Shape_resolves_each_codepoint_to_its_cmap_glyph()
    {
        // SyntheticFont cmap: 'A' (U+0041) → glyph 1, 'B' (U+0042) → glyph 2.
        using var shaper = new HbShaper(SyntheticFont.Build(), fontSizePx: 12);
        var result = shaper.Shape("AB");

        Assert.Equal(2, result.Length);
        Assert.Equal((ushort)1, result[0].GlyphId);
        Assert.Equal((ushort)2, result[1].GlyphId);
    }

    [Fact]
    public void Shape_returns_advances_scaled_to_pixels_at_requested_size()
    {
        // Synthetic font: unitsPerEm=1000, glyph 1 advance = 500 units.
        // At fontSizePx=12, expected advance = 500 * 12 / 1000 = 6.0 px.
        using var shaper = new HbShaper(SyntheticFont.Build(), fontSizePx: 12);
        var result = shaper.Shape("A");

        Assert.Single(result);
        Assert.Equal(6.0f, result[0].XAdvance, precision: 3);
    }

    [Fact]
    public void Shape_preserves_cluster_index_per_input_codepoint()
    {
        // Two ASCII chars → two clusters at indices 0 and 1.
        using var shaper = new HbShaper(SyntheticFont.Build(), fontSizePx: 12);
        var result = shaper.Shape("AB");

        Assert.Equal(0, result[0].Cluster);
        Assert.Equal(1, result[1].Cluster);
    }

    [Fact]
    public void Shape_is_deterministic_for_same_inputs()
    {
        using var shaper = new HbShaper(SyntheticFont.Build(), fontSizePx: 14);
        var a = shaper.Shape("AB");
        var b = shaper.Shape("AB");

        Assert.Equal(a.Length, b.Length);
        for (var i = 0; i < a.Length; i++)
        {
            Assert.Equal(a[i], b[i]);
        }
    }

    [Fact]
    public void Shape_at_larger_font_size_scales_advances_proportionally()
    {
        // Same glyph at 12 px vs 24 px should produce double the advance.
        using var smallShaper = new HbShaper(SyntheticFont.Build(), fontSizePx: 12);
        using var largeShaper = new HbShaper(SyntheticFont.Build(), fontSizePx: 24);
        var small = smallShaper.Shape("A");
        var large = largeShaper.Shape("A");

        Assert.Equal(small[0].XAdvance * 2, large[0].XAdvance, precision: 3);
    }

    [Fact]
    public void Shape_unmapped_codepoint_returns_notdef_glyph_zero()
    {
        // SyntheticFont cmap covers only 'A' and 'B'. 'Z' (U+005A) has no entry —
        // HarfBuzz emits glyph 0 (.notdef).
        using var shaper = new HbShaper(SyntheticFont.Build(), fontSizePx: 12);
        var result = shaper.Shape("Z");

        Assert.Single(result);
        Assert.Equal((ushort)0, result[0].GlyphId);
    }
}
