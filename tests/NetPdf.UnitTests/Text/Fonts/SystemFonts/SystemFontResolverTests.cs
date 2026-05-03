// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf;
using NetPdf.Text.Fonts.SystemFonts;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.SystemFonts;

/// <summary>
/// Tests for the public <see cref="SystemFontResolver"/> CSS-generic expansion +
/// fallback chain. Uses synthetic indexes so the resolver logic can be exercised
/// independently of any host platform's font set.
/// </summary>
public sealed class SystemFontResolverTests
{
    private static SystemFontEntry Entry(string family, int weight = 400, bool italic = false, int stretch = 5) =>
        new()
        {
            FilePath = $"/fake/{family}-w{weight}-s{stretch}.ttf",
            FaceIndex = 0,
            FamilyName = family,
            SubfamilyName = "Regular",
            PostScriptName = family.Replace(" ", string.Empty),
            WeightCss = weight,
            StretchCss = stretch,
            IsItalic = italic,
        };

    [Fact]
    public void Direct_family_hit_resolves_without_consulting_the_generic_chain()
    {
        var idx = SystemFontIndex.BuildFromEntries([Entry("Roboto"), Entry("Open Sans")]);
        var resolver = new SystemFontResolver(idx);
        var entry = resolver.ResolveEntry(new FontQuery
        {
            Family = "Roboto",
            WeightCss = 400,
            Style = FontStyle.Normal,
        });
        Assert.NotNull(entry);
        Assert.Equal("Roboto", entry.Value.FamilyName);
    }

    [Fact]
    public void CSS_generic_serif_falls_back_through_the_chain_in_order()
    {
        // Index has only DejaVu Serif (a Linux family later in the serif fallback chain).
        // Request "serif" should still resolve since some entry on the chain is present.
        var idx = SystemFontIndex.BuildFromEntries([Entry("DejaVu Serif")]);
        var resolver = new SystemFontResolver(idx);
        var entry = resolver.ResolveEntry(new FontQuery
        {
            Family = "serif",
            WeightCss = 400,
            Style = FontStyle.Normal,
        });
        Assert.NotNull(entry);
        Assert.Equal("DejaVu Serif", entry.Value.FamilyName);
    }

    [Fact]
    public void CSS_generic_sans_serif_resolves()
    {
        var idx = SystemFontIndex.BuildFromEntries([Entry("Helvetica")]);
        var resolver = new SystemFontResolver(idx);
        var entry = resolver.ResolveEntry(new FontQuery
        {
            Family = "sans-serif",
            WeightCss = 400,
            Style = FontStyle.Normal,
        });
        Assert.NotNull(entry);
        Assert.Equal("Helvetica", entry.Value.FamilyName);
    }

    [Fact]
    public void CSS_generic_monospace_resolves()
    {
        var idx = SystemFontIndex.BuildFromEntries([Entry("Menlo")]);
        var resolver = new SystemFontResolver(idx);
        var entry = resolver.ResolveEntry(new FontQuery
        {
            Family = "monospace",
            WeightCss = 400,
            Style = FontStyle.Normal,
        });
        Assert.NotNull(entry);
        Assert.Equal("Menlo", entry.Value.FamilyName);
    }

    [Fact]
    public void CSS_generic_chain_picks_first_present_family_not_first_in_chain()
    {
        // Chain order for monospace starts with "Courier New" then "Courier" then
        // Liberation/DejaVu/Menlo/Consolas/etc. Index has only Liberation Mono — must pick it.
        var idx = SystemFontIndex.BuildFromEntries([Entry("Liberation Mono")]);
        var resolver = new SystemFontResolver(idx);
        var entry = resolver.ResolveEntry(new FontQuery
        {
            Family = "monospace",
            WeightCss = 400,
            Style = FontStyle.Normal,
        });
        Assert.NotNull(entry);
        Assert.Equal("Liberation Mono", entry.Value.FamilyName);
    }

    [Fact]
    public void Italic_query_propagates_into_index_lookup()
    {
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", 400, italic: false),
            Entry("Roboto", 400, italic: true),
        ]);
        var resolver = new SystemFontResolver(idx);
        var entry = resolver.ResolveEntry(new FontQuery
        {
            Family = "Roboto",
            WeightCss = 400,
            Style = FontStyle.Italic,
        });
        Assert.NotNull(entry);
        Assert.True(entry.Value.IsItalic);
    }

    [Fact]
    public void Oblique_style_is_treated_as_italic_for_lookup_purposes()
    {
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", 400, italic: false),
            Entry("Roboto", 400, italic: true),
        ]);
        var resolver = new SystemFontResolver(idx);
        var entry = resolver.ResolveEntry(new FontQuery
        {
            Family = "Roboto",
            WeightCss = 400,
            Style = FontStyle.Oblique,
        });
        Assert.NotNull(entry);
        Assert.True(entry.Value.IsItalic);
    }

    [Fact]
    public void Unknown_family_with_no_generic_match_returns_null()
    {
        var idx = SystemFontIndex.BuildFromEntries([Entry("Roboto")]);
        var resolver = new SystemFontResolver(idx);
        var entry = resolver.ResolveEntry(new FontQuery
        {
            Family = "TotallyMadeUpFamilyThatDoesNotExist",
            WeightCss = 400,
            Style = FontStyle.Normal,
        });
        Assert.Null(entry);
    }

    [Fact]
    public void CSS_generic_with_empty_index_returns_null()
    {
        var idx = SystemFontIndex.BuildFromEntries([]);
        var resolver = new SystemFontResolver(idx);
        var entry = resolver.ResolveEntry(new FontQuery
        {
            Family = "serif",
            WeightCss = 400,
            Style = FontStyle.Normal,
        });
        Assert.Null(entry);
    }

    [Fact]
    public async Task ResolveAsync_returns_null_for_unresolvable_query_without_throwing()
    {
        var idx = SystemFontIndex.BuildFromEntries([]);
        var resolver = new SystemFontResolver(idx);
        var data = await resolver.ResolveAsync(new FontQuery
        {
            Family = "nope",
            WeightCss = 400,
            Style = FontStyle.Normal,
        }, CancellationToken.None);
        Assert.Null(data);
    }

    [Fact]
    public async Task ResolveAsync_propagates_cancellation()
    {
        var idx = SystemFontIndex.BuildFromEntries([Entry("Roboto")]);
        var resolver = new SystemFontResolver(idx);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await resolver.ResolveAsync(new FontQuery
            {
                Family = "Roboto",
                WeightCss = 400,
                Style = FontStyle.Normal,
            }, cts.Token));
    }

    // ───── Stretch propagation through the resolver (review #1, #2, #3) ──────

    [Fact]
    public void Direct_family_query_propagates_stretch_to_the_matcher()
    {
        // Index has condensed and normal-width Roboto. A condensed query must pick the
        // condensed face — proves stretch is reaching FindBest.
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", weight: 400, italic: false, stretch: 3),
            Entry("Roboto", weight: 400, italic: false, stretch: 5),
        ]);
        var resolver = new SystemFontResolver(idx);
        var entry = resolver.ResolveEntry(new FontQuery
        {
            Family = "Roboto",
            WeightCss = 400,
            Style = FontStyle.Normal,
            StretchCss = 3,
        });
        Assert.NotNull(entry);
        Assert.Equal(3, entry.Value.StretchCss);
    }

    [Fact]
    public void CSS_generic_family_query_propagates_stretch_through_the_fallback_chain()
    {
        // sans-serif chain hits Liberation Sans. Index has Liberation Sans in two widths;
        // an expanded query should pick the expanded face.
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Liberation Sans", weight: 400, italic: false, stretch: 5),
            Entry("Liberation Sans", weight: 400, italic: false, stretch: 7),
        ]);
        var resolver = new SystemFontResolver(idx);
        var entry = resolver.ResolveEntry(new FontQuery
        {
            Family = "sans-serif",
            WeightCss = 400,
            Style = FontStyle.Normal,
            StretchCss = 7,
        });
        Assert.NotNull(entry);
        Assert.Equal(7, entry.Value.StretchCss);
    }

    [Fact]
    public void Null_StretchCss_in_FontQuery_is_treated_as_normal_width_5()
    {
        // FontQuery.StretchCss is int? — most callers omit it. The resolver must default
        // to 5 so an omitted query matches normal-width entries cleanly.
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", weight: 400, italic: false, stretch: 5),
            Entry("Roboto", weight: 400, italic: false, stretch: 9),
        ]);
        var resolver = new SystemFontResolver(idx);
        var entry = resolver.ResolveEntry(new FontQuery
        {
            Family = "Roboto",
            WeightCss = 400,
            Style = FontStyle.Normal,
            // StretchCss not set — should default to 5 (normal).
        });
        Assert.NotNull(entry);
        Assert.Equal(5, entry.Value.StretchCss);
    }

    // ───── Public surface improvements (review follow-up #3, #6) ─────────────

    [Fact]
    public async Task ResolveAsync_populates_FontFaceData_StretchCss_from_the_chosen_face()
    {
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", weight: 400, italic: false, stretch: 3),
            Entry("Roboto", weight: 400, italic: false, stretch: 7),
        ]);
        // Use a temp file so cache-loader doesn't fail on a fake path.
        var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ttf");
        File.WriteAllBytes(tmp, [0xDE, 0xAD]);
        try
        {
            // Override the entry to point at the real temp file so File.ReadAllBytes works.
            var idxReal = SystemFontIndex.BuildFromEntries([
                new SystemFontEntry
                {
                    FilePath = tmp,
                    FaceIndex = 0,
                    FamilyName = "Roboto",
                    SubfamilyName = "Expanded",
                    PostScriptName = "Roboto-Expanded",
                    WeightCss = 400,
                    StretchCss = 7,
                    IsItalic = false,
                },
            ]);
            var resolver = new SystemFontResolver(idxReal);
            var data = await resolver.ResolveAsync(new FontQuery
            {
                Family = "Roboto",
                WeightCss = 400,
                Style = FontStyle.Normal,
                StretchCss = 7,
            }, CancellationToken.None);
            Assert.NotNull(data);
            Assert.Equal(7, data!.StretchCss);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
        }
    }

    [Theory]
    [InlineData(-1, 1)]   // Below range clamps to 1.
    [InlineData(0, 1)]    // Below range clamps to 1.
    [InlineData(10, 9)]   // Above range clamps to 9.
    [InlineData(100, 9)]  // Far above range still clamps to 9.
    public void ResolveEntry_clamps_explicit_out_of_range_StretchCss_to_1_to_9(int requested, int expectedEffectiveStretch)
    {
        // Index has a face at the expected clamp endpoint plus a normal-width face. The
        // resolver should pick the clamp-endpoint face — proving the clamp happens at the
        // resolver boundary (not silently passed through and matched as out-of-range).
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", weight: 400, italic: false, stretch: 5),
            Entry("Roboto", weight: 400, italic: false, stretch: expectedEffectiveStretch),
        ]);
        var resolver = new SystemFontResolver(idx);
        var entry = resolver.ResolveEntry(new FontQuery
        {
            Family = "Roboto",
            WeightCss = 400,
            Style = FontStyle.Normal,
            StretchCss = requested,
        });
        Assert.NotNull(entry);
        Assert.Equal(expectedEffectiveStretch, entry.Value.StretchCss);
    }
}
