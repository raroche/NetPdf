// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Shaping;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Shaping;

/// <summary>
/// Per-class unit tests for <see cref="HbShaper"/>. The integration test that runs an
/// actual shaping call through HarfBuzz against the synthetic font lives in
/// <see cref="HbShaperIntegrationTests"/>.
/// </summary>
public sealed class HbShaperTests
{
    private const string Latin = "Latn";
    private const string English = "en";

    [Fact]
    public void Constructor_throws_on_empty_font_bytes()
    {
        Assert.Throws<ArgumentException>(() => new HbShaper(ReadOnlyMemory<byte>.Empty, fontSizePx: 12));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Constructor_throws_on_invalid_font_size(double bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HbShaper(SyntheticFont.Build(), bad));
    }

    [Fact]
    public void Constructor_accepts_zero_font_size_and_shapes_to_zero_advances()
    {
        // CSS Fonts 4 §3.4 allows font-size in [0, ∞]; a 0-size shaper produces
        // zero-advance glyphs (invisible text) rather than throwing or snapping up.
        using var shaper = new HbShaper(SyntheticFont.Build(), fontSizePx: 0);

        var glyphs = shaper.Shape("Hi", ShapingDirection.LeftToRight, Latin, English);

        Assert.NotEmpty(glyphs);   // glyphs are still produced…
        Assert.All(glyphs, g =>    // …but every metric converts to zero.
        {
            Assert.Equal(0f, g.XAdvance);
            Assert.Equal(0f, g.YAdvance);
            Assert.Equal(0f, g.XOffset);
            Assert.Equal(0f, g.YOffset);
        });
    }

    [Fact]
    public void Shape_empty_text_returns_empty_array()
    {
        using var shaper = new HbShaper(SyntheticFont.Build(), fontSizePx: 12);
        var result = shaper.Shape(ReadOnlySpan<char>.Empty, ShapingDirection.LeftToRight, Latin, English);
        Assert.Empty(result);
    }

    [Fact]
    public void Shape_throws_after_dispose()
    {
        var shaper = new HbShaper(SyntheticFont.Build(), fontSizePx: 12);
        shaper.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
            shaper.Shape("A", ShapingDirection.LeftToRight, Latin, English));
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var shaper = new HbShaper(SyntheticFont.Build(), fontSizePx: 12);
        shaper.Dispose();
        shaper.Dispose(); // must not throw
    }
}
