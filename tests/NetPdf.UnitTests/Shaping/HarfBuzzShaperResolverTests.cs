// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Threading;
using System.Threading.Tasks;
using NetPdf;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Shaping;
using NetPdf.Text.Shaping;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Shaping;

/// <summary>
/// Phase 5 layout→PDF cycle 1 — unit tests for the production
/// <see cref="HarfBuzzShaperResolver"/>. A test <see cref="IFontResolver"/>
/// returns a fixed synthetic font so the tests are deterministic +
/// independent of any system-font registry.
/// </summary>
public sealed class HarfBuzzShaperResolverTests
{
    private static ComputedStyle MakeStyle() => ComputedStyle.RentForExclusiveTesting();

    [Fact]
    public void Resolve_returns_a_shaper_that_shapes_text_into_glyphs()
    {
        using var resolver = new HarfBuzzShaperResolver(new SyntheticFontResolver());
        var shaper = resolver.Resolve(MakeStyle());

        var glyphs = shaper.Shape(
            "Hi".AsSpan(), ShapingDirection.LeftToRight, "Latn", "en");

        Assert.NotNull(shaper);
        Assert.NotEmpty(glyphs);   // real HarfBuzz shaping produced glyph(s)
    }

    [Fact]
    public void Resolve_caches_one_shaper_per_resolved_font_and_size()
    {
        using var resolver = new HarfBuzzShaperResolver(new SyntheticFontResolver());
        var first = resolver.Resolve(MakeStyle());
        var second = resolver.Resolve(MakeStyle());

        // Same style → same shaper instance (the IShaperResolver contract).
        Assert.Same(first, second);
    }

    [Fact]
    public void Resolve_returns_distinct_shapers_for_distinct_font_sizes()
    {
        using var resolver = new HarfBuzzShaperResolver(new SyntheticFontResolver());

        // The resolver honors a resolved font-size slot (forward-compatible:
        // the CSS FontSizeResolver isn't wired yet, but the reader picks up
        // a LengthPx slot — which a resolved cascade will produce).
        var small = MakeStyle();
        small.Set(PropertyId.FontSize, ComputedSlot.FromLengthPx(12));
        var large = MakeStyle();
        large.Set(PropertyId.FontSize, ComputedSlot.FromLengthPx(24));

        Assert.NotSame(resolver.Resolve(small), resolver.Resolve(large));
    }

    [Fact]
    public void Resolve_maps_css_font_style_to_the_font_query()
    {
        var recording = new RecordingFontResolver();
        using var resolver = new HarfBuzzShaperResolver(recording);

        var italic = MakeStyle();
        // font-style keyword table: 0 normal / 1 italic / 2 oblique.
        italic.Set(PropertyId.FontStyle, ComputedSlot.FromKeyword(1));
        resolver.Resolve(italic);

        Assert.Equal(FontStyle.Italic, recording.LastQuery.Style);
    }

    [Fact]
    public void Resolve_throws_a_clear_error_when_no_font_resolves()
    {
        using var resolver = new HarfBuzzShaperResolver(new NullFontResolver());

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve(MakeStyle()));
        Assert.Contains("no font resolved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_after_dispose_throws_object_disposed()
    {
        var resolver = new HarfBuzzShaperResolver(new SyntheticFontResolver());
        resolver.Dispose();

        Assert.Throws<ObjectDisposedException>(() => resolver.Resolve(MakeStyle()));
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var resolver = new HarfBuzzShaperResolver(new SyntheticFontResolver());
        _ = resolver.Resolve(MakeStyle());   // create a cached shaper
        resolver.Dispose();
        resolver.Dispose();                  // second dispose must not throw
    }

    // --- test font resolvers --------------------------------------------

    /// <summary>Returns a fixed synthetic font for any query.</summary>
    private sealed class SyntheticFontResolver : IFontResolver
    {
        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = SyntheticFont.Build(), Family = query.Family });
    }

    /// <summary>Records the last query (to assert style mapping) + returns
    /// a synthetic font.</summary>
    private sealed class RecordingFontResolver : IFontResolver
    {
        public FontQuery LastQuery { get; private set; }

        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
        {
            LastQuery = query;
            return new(new FontFaceData { Bytes = SyntheticFont.Build(), Family = query.Family });
        }
    }

    /// <summary>Resolves nothing (exercises the no-font error path).</summary>
    private sealed class NullFontResolver : IFontResolver
    {
        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new((FontFaceData?)null);
    }
}
