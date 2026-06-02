// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Threading;
using System.Threading.Tasks;
using NetPdf;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
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

        // The resolver honors the resolved font-size slot (FontSizeResolver writes a
        // LengthPx slot the reader picks up); distinct sizes → distinct shapers.
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

        var ex = Assert.Throws<FontResolutionException>(() => resolver.Resolve(MakeStyle()));
        Assert.Contains("no font resolved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveFontProgram_returns_the_validated_font_bytes()
    {
        using var resolver = new HarfBuzzShaperResolver(new SyntheticFontResolver());

        // The text painter subsets THESE bytes after layout — they must be the exact program
        // the resolver hands HarfBuzz (the validated face bytes, here the synthetic font).
        var program = resolver.ResolveFontProgram(MakeStyle());

        Assert.Equal(SyntheticFont.Build(), program.Bytes.ToArray());
        Assert.False(string.IsNullOrEmpty(program.Identity));
    }

    [Fact]
    public void ResolveFontProgram_identity_is_font_size_independent()
    {
        using var resolver = new HarfBuzzShaperResolver(new SyntheticFontResolver());

        // A font program is identical at every size (the size is applied at emit via Tf), so
        // runs of the same face at different sizes share ONE subset/embedded font — the program
        // identity must NOT vary with font-size.
        var small = MakeStyle();
        small.Set(PropertyId.FontSize, ComputedSlot.FromLengthPx(12));
        var large = MakeStyle();
        large.Set(PropertyId.FontSize, ComputedSlot.FromLengthPx(48));

        Assert.Equal(
            resolver.ResolveFontProgram(small).Identity,
            resolver.ResolveFontProgram(large).Identity);
    }

    [Fact]
    public void ResolveFontProgram_identity_is_content_based_not_query_based()
    {
        using var resolver = new HarfBuzzShaperResolver(new SyntheticFontResolver());

        // Identity hashes the RESOLVED bytes, not the requested query (post-PR-#127 review P3).
        // The synthetic resolver returns the same face regardless of style, so normal + italic
        // (DISTINCT queries) share ONE identity ⇒ one subset/embedded font — the dedup that
        // collapses different font-family stacks that fall back to the same face.
        var normal = MakeStyle();
        var italic = MakeStyle();
        italic.Set(PropertyId.FontStyle, ComputedSlot.FromKeyword(1)); // 1 = italic.

        Assert.Equal(
            resolver.ResolveFontProgram(normal).Identity,
            resolver.ResolveFontProgram(italic).Identity);
    }

    [Fact]
    public void ResolveFontProgram_reuses_the_program_resolved_by_Resolve()
    {
        var counting = new CountingFontResolver();
        using var resolver = new HarfBuzzShaperResolver(counting);
        var style = MakeStyle();

        // Layout shapes via Resolve (one resolution); paint then calls ResolveFontProgram. It
        // MUST reuse the cached program, not re-query — otherwise a stateful / CDN resolver that
        // returns different bytes on a later call would make the painter subset a different
        // program than layout shaped (post-PR-#127 review P1).
        resolver.Resolve(style);
        var program = resolver.ResolveFontProgram(style);

        Assert.Equal(1, counting.ResolveCount);
        Assert.Equal(SyntheticFont.Build(), program.Bytes.ToArray());
    }

    [Fact]
    public void ResolveFontProgram_throws_when_no_font_resolves()
    {
        using var resolver = new HarfBuzzShaperResolver(new NullFontResolver());

        // Mirrors the shaping path: the painter / pipeline catches this + skips/diagnoses.
        Assert.Throws<FontResolutionException>(() => resolver.ResolveFontProgram(MakeStyle()));
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

    // --- post-PR-#120 review follow-ups ---------------------------------

    [Fact]
    public void Resolve_honors_font_size_zero_as_zero_advance_text()
    {
        // P1 — a resolved font-size of 0 must NOT snap back to the 16px default; it
        // shapes to zero-advance (invisible) glyphs (CSS Fonts 4 §3.4 allows [0, ∞]).
        using var resolver = new HarfBuzzShaperResolver(new SyntheticFontResolver());
        var style = MakeStyle();
        style.Set(PropertyId.FontSize, ComputedSlot.FromLengthPx(0));

        var glyphs = resolver.Resolve(style)
            .Shape("Hi".AsSpan(), ShapingDirection.LeftToRight, "Latn", "en");

        Assert.NotEmpty(glyphs);                       // glyphs are produced…
        Assert.All(glyphs, g => Assert.Equal(0f, g.XAdvance));   // …with zero advance.
    }

    [Fact]
    public void Resolve_walks_the_font_family_stack_past_missing_families()
    {
        // P2 — the shaper tries each author family in order, not just the primary:
        // `MissingFont, Arial, sans-serif` resolves via Arial instead of throwing.
        var fonts = new SelectiveFontResolver("Arial");
        using var resolver = new HarfBuzzShaperResolver(fonts);
        var style = MakeStyle();
        PropertyResolverDispatch.Resolve(PropertyId.FontFamily, "MissingFont, Arial, sans-serif")
            .MaterializeInto(style, PropertyId.FontFamily);

        var shaper = resolver.Resolve(style);   // must NOT throw — falls through to Arial.

        Assert.NotNull(shaper);
        Assert.Equal("Arial", fonts.LastResolvedFamily);
    }

    // --- post-PR-#117 review hardening ----------------------------------

    [Fact]
    public void Resolve_rejects_garbage_font_bytes_before_harfbuzz()
    {
        // P1 — resolved bytes are gated through FontSafetyValidator (as
        // FontFace.Load does) BEFORE reaching native HarfBuzz: a custom
        // resolver returning non-font garbage must be rejected, not shaped.
        using var resolver = new HarfBuzzShaperResolver(
            new FixedBytesFontResolver(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));

        var ex = Assert.Throws<FontResolutionException>(() => resolver.Resolve(MakeStyle()));
        Assert.Contains("safety validator", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_rejects_woff_wrapped_font_bytes()
    {
        // P1 — WOFF/WOFF2 wrap an sfnt that HarfBuzz can't read directly;
        // reject (mirroring FontFace.Load) rather than feed wrapped bytes
        // to the shaper.
        using var resolver = new HarfBuzzShaperResolver(
            new FixedBytesFontResolver(MinimalValidWoffHeader()));

        Assert.Throws<FontResolutionException>(() => resolver.Resolve(MakeStyle()));
    }

    [Fact]
    public void Resolve_fails_fast_on_a_non_synchronous_resolver()
    {
        // P1 — layout shaping is synchronous; an async resolver that doesn't
        // complete synchronously (e.g. a CDN fetch) must FAIL FAST, never
        // block/hang. A never-completing resolver must throw promptly (this
        // test returning at all is the no-hang proof).
        using var resolver = new HarfBuzzShaperResolver(new NeverCompletesFontResolver());

        Assert.Throws<NotSupportedException>(() => resolver.Resolve(MakeStyle()));
    }

    [Fact]
    public void Resolve_is_safe_under_concurrent_access()
    {
        // P2 — a single resolver shared across parallel layout work: the
        // lock-guarded cache must not corrupt, and the same style must yield
        // the same instance from every thread (created exactly once).
        using var resolver = new HarfBuzzShaperResolver(new SyntheticFontResolver());
        var style = MakeStyle();
        var results = new HbShaper[64];

        System.Threading.Tasks.Parallel.For(0, results.Length, i => results[i] = resolver.Resolve(style));

        Assert.All(results, r => Assert.Same(results[0], r));
    }

    /// <summary>A minimal 44-byte WOFF 1.0 header (signature <c>wOFF</c>,
    /// TTF flavor, length 44, 1 table) — enough for FontSafetyValidator to
    /// recognize the wrapped format.</summary>
    private static byte[] MinimalValidWoffHeader()
    {
        var b = new byte[44];
        b[0] = 0x77; b[1] = 0x4F; b[2] = 0x46; b[3] = 0x46;   // "wOFF"
        b[4] = 0x00; b[5] = 0x01; b[6] = 0x00; b[7] = 0x00;   // flavor 0x00010000 (TTF)
        b[8] = 0x00; b[9] = 0x00; b[10] = 0x00; b[11] = 0x2C; // length = 44 (BE)
        b[12] = 0x00; b[13] = 0x01;                           // numTables = 1 (BE)
        // reserved (b[14..16]) + the rest stay zero.
        return b;
    }

    // --- test font resolvers --------------------------------------------

    /// <summary>Returns a fixed synthetic font for any query.</summary>
    private sealed class SyntheticFontResolver : IFontResolver
    {
        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = SyntheticFont.Build(), Family = query.Family });
    }

    /// <summary>Counts ResolveAsync calls (to prove the program cache prevents re-resolution).</summary>
    private sealed class CountingFontResolver : IFontResolver
    {
        public int ResolveCount { get; private set; }

        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
        {
            ResolveCount++;
            return new(new FontFaceData { Bytes = SyntheticFont.Build(), Family = query.Family });
        }
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

    /// <summary>Resolves a synthetic font ONLY for one whitelisted family
    /// (case-insensitive) + records it; returns null for any other family — exercises
    /// the font-family fallback-stack walk.</summary>
    private sealed class SelectiveFontResolver(string resolvable) : IFontResolver
    {
        public string? LastResolvedFamily { get; private set; }

        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
        {
            if (string.Equals(query.Family, resolvable, StringComparison.OrdinalIgnoreCase))
            {
                LastResolvedFamily = query.Family;
                return new(new FontFaceData { Bytes = SyntheticFont.Build(), Family = query.Family });
            }
            return new((FontFaceData?)null);
        }
    }

    /// <summary>Returns caller-supplied bytes verbatim (exercises the
    /// FontSafetyValidator gate with garbage / wrapped bytes).</summary>
    private sealed class FixedBytesFontResolver(byte[] bytes) : IFontResolver
    {
        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = bytes, Family = query.Family });
    }

    /// <summary>Returns a ValueTask that never completes (exercises the
    /// fail-fast-on-async path — must throw, not block).</summary>
    private sealed class NeverCompletesFontResolver : IFontResolver
    {
        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new TaskCompletionSource<FontFaceData?>().Task);   // never set
    }
}
