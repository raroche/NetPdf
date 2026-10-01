// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Properties;
using NetPdf.Layout.Inline;
using NetPdf.Shaping;
using NetPdf.Text.Bidi;
using NetPdf.Text.Shaping;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Shaping;

/// <summary>
/// Per-character font fallback (CSS Fonts 4 §5). Before it, every run used ONE font — the first family
/// of the <c>font-family</c> stack that resolved — and a character that font lacked was drawn as a
/// <c>.notdef</c> box ("tofu"), even when a later family in the stack or an installed symbol font had
/// it (for example the ✔ / ▲ / → marks in the tester corpus). Now the text the primary font can't draw
/// is split into its own runs, shaped and painted with the first font of the fallback chain that covers
/// it. Driven with two synthetic fonts: <c>Build('A')</c> covers only A/B, <c>Build('C')</c> only C/D.
/// </summary>
public sealed class FontFallbackTests
{
    private static readonly byte[] AbFont = SyntheticFont.Build('A');
    private static readonly byte[] CdFont = SyntheticFont.Build('C');

    // ── LineBuilder: splitting a run by font coverage ─────────────────────────────────────────

    /// <summary>Index 0 = A/B font, then the given fallback fonts (default: the C/D font). Style ignored.</summary>
    private sealed class TwoFontShaperResolver(params byte[][] fallbacks) : IShaperResolver
    {
        private readonly HbShaper[] _fonts =
            [new(AbFont, 12), .. (fallbacks.Length == 0 ? [CdFont] : fallbacks).Select(f => new HbShaper(f, 12))];
        public HbShaper Resolve(ComputedStyle style) => _fonts[0];
        public HbShaper Resolve(ComputedStyle style, int fontIndex) => _fonts[fontIndex];
        public int FindFallbackFont(ComputedStyle style, ReadOnlySpan<int> codepoints)
        {
            for (var i = 1; i < _fonts.Length; i++)
            {
                var all = true;
                foreach (var cp in codepoints) all &= _fonts[i].HasGlyph(cp);
                if (all) return i;
            }
            return 0;
        }
        public void Dispose()
        {
            foreach (var f in _fonts) f.Dispose();
        }
    }

    /// <summary>The same A/B font, but no fallback chain (the interface defaults).</summary>
    private sealed class PrimaryOnlyShaperResolver : IShaperResolver
    {
        private readonly HbShaper _primary = new(AbFont, 12);
        public HbShaper Resolve(ComputedStyle style) => _primary;
        public void Dispose() => _primary.Dispose();
    }

    private static ShapedRun[] Shape(string text, IShaperResolver resolver)
    {
        var runs = new[] { new TextRun(text, ComputedStyle.RentForExclusiveTesting()) };
        var itemized = LineBuilder.Itemize(runs, ParagraphDirection.LeftToRight);
        return LineBuilder.Shape(runs, itemized, resolver, "Latn", "en");
    }

    private static string Pieces(string text, ShapedRun[] shaped) =>
        string.Join("|", shaped.Select(r =>
            text.Substring(r.Source.Utf16Start, r.Source.Utf16Length) + ":" + r.Source.FontIndex));

    [Fact]
    public void Characters_the_primary_font_lacks_are_shaped_with_the_fallback_font()
    {
        using var resolver = new TwoFontShaperResolver();
        const string text = "ABCD AB";
        var shaped = Shape(text, resolver);

        // A space the primary can't map still stays on the primary (it paints nothing either way).
        Assert.Equal("AB:0|CD:1| AB:0", Pieces(text, shaped));
        Assert.All(shaped, r => Assert.False(r.HasVisibleMissingGlyph));
        // C / D got real glyphs (1, 2) from the fallback font instead of .notdef.
        Assert.Equal(new ushort[] { 1, 2 }, shaped[1].Glyphs.Select(g => g.GlyphId));
    }

    [Fact]
    public void A_combining_mark_stays_with_its_base_character()
    {
        using var resolver = new TwoFontShaperResolver();
        const string text = "AĆB";
        Assert.Equal("A:0|Ć:1|B:0", Pieces(text, Shape(text, resolver)));
    }

    [Fact]
    public void A_leading_combining_mark_is_its_own_cluster_and_stays_on_the_primary()
    {
        // A mark with no base before it is a grapheme cluster of its own (UAX #29); no font covers it here.
        using var resolver = new TwoFontShaperResolver();
        const string text = "\u0301CA";
        Assert.Equal("\u0301:0|C:1|A:0", Pieces(text, Shape(text, resolver)));
    }

    [Fact]
    public void A_cluster_goes_to_the_font_that_covers_all_of_it()
    {
        // PR #387 review — the primary covers 'A' but not U+0301; a fallback covers both. The whole cluster
        // (base + mark) moves to that fallback instead of leaving the mark as tofu on the primary.
        using var resolver = new TwoFontShaperResolver(CdFont, SyntheticFont.Build('A', '\u0301'));
        const string text = "A\u0301B";
        var shaped = Shape(text, resolver);
        Assert.Equal("A\u0301:2|B:0", Pieces(text, shaped));
        Assert.All(shaped, r => Assert.False(r.HasVisibleMissingGlyph));
    }

    [Fact]
    public void A_lone_surrogate_stays_on_the_primary_instead_of_throwing()
    {
        // PR #387 review — malformed UTF-16 must keep HarfBuzz's replacement behaviour, not throw.
        using var resolver = new TwoFontShaperResolver();
        const string text = "A\uD800C";
        Assert.Equal("A\uD800:0|C:1", Pieces(text, Shape(text, resolver)));
    }

    [Fact]
    public void A_character_no_font_covers_stays_tofu_and_is_still_reported()
    {
        using var resolver = new TwoFontShaperResolver();
        const string text = "ABE";
        var shaped = Shape(text, resolver);

        var run = Assert.Single(shaped);
        Assert.Equal(0, run.Source.FontIndex);
        Assert.True(run.HasVisibleMissingGlyph);
    }

    [Fact]
    public void A_resolver_without_a_fallback_chain_shapes_exactly_as_before()
    {
        using var resolver = new PrimaryOnlyShaperResolver();
        const string text = "ABCD";
        var run = Assert.Single(Shape(text, resolver));
        Assert.Equal(0, run.Source.FontIndex);
        Assert.True(run.HasVisibleMissingGlyph);
    }

    [Fact]
    public void Text_the_primary_font_fully_covers_is_not_split()
    {
        using var resolver = new TwoFontShaperResolver();
        var run = Assert.Single(Shape("ABBA", resolver));
        Assert.Equal(0, run.Source.FontIndex);
    }

    // ── HarfBuzzShaperResolver: the fallback chain ────────────────────────────────────────────

    /// <summary>Maps family names to font bytes; anything else does not resolve.</summary>
    private sealed class MapFontResolver(Dictionary<string, byte[]> fonts) : IFontResolver
    {
        public List<string> Queried { get; } = new();

        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
        {
            Queried.Add(query.Family);
            return new(fonts.TryGetValue(query.Family, out var bytes)
                ? new FontFaceData { Bytes = bytes, Family = query.Family }
                : null);
        }
    }

    private static ComputedStyle StyleWithFamilies(string families)
    {
        var style = ComputedStyle.RentForExclusiveTesting();
        PropertyResolverDispatch.Resolve(PropertyId.FontFamily, families).MaterializeInto(style, PropertyId.FontFamily);
        return style;
    }

    [Fact]
    public void A_later_family_in_the_stack_is_the_fallback_for_characters_the_first_lacks()
    {
        using var resolver = new HarfBuzzShaperResolver(new MapFontResolver(new()
        {
            ["Primary"] = AbFont,
            ["Second"] = CdFont,
        }));
        var style = StyleWithFamilies("Primary, Second");

        Assert.Equal(1, resolver.FindFallbackFont(style, ['C']));
        Assert.Equal(0, resolver.FindFallbackFont(style, ['E']));   // nothing covers it
        Assert.True(resolver.Resolve(style, 1).HasGlyph('C'));
        Assert.Equal(CdFont, resolver.ResolveFontProgram(style, 1).Bytes.ToArray());
        Assert.Equal(AbFont, resolver.ResolveFontProgram(style, 0).Bytes.ToArray());
    }

    [Fact]
    public void System_fallback_families_are_tried_after_the_author_stack()
    {
        // The author stack names only "Primary"; a platform fallback family covers C.
        var fallbackFamily = HarfBuzzShaperResolver.SystemFallbackFamilies[^1];
        using var resolver = new HarfBuzzShaperResolver(new MapFontResolver(new()
        {
            ["Primary"] = AbFont,
            [fallbackFamily] = CdFont,
        }));
        var style = StyleWithFamilies("Primary");

        var index = resolver.FindFallbackFont(style, ['C']);
        Assert.True(index > 0);
        Assert.Equal(CdFont, resolver.ResolveFontProgram(style, index).Bytes.ToArray());
    }

    [Fact]
    public void Families_that_resolve_to_the_same_font_are_one_chain_entry()
    {
        // Every family resolves to the A/B font: there is no distinct fallback, so C stays uncovered.
        var fonts = new Dictionary<string, byte[]> { ["Primary"] = AbFont, ["Second"] = AbFont, ["sans-serif"] = AbFont };
        using var resolver = new HarfBuzzShaperResolver(new MapFontResolver(fonts));
        Assert.Equal(0, resolver.FindFallbackFont(StyleWithFamilies("Primary, Second"), ['C']));
    }

    [Fact]
    public void Fallback_candidates_are_resolved_lazily_until_one_covers_the_character()
    {
        // PR #387 review — a check mark must not load every installed fallback font: resolution stops at
        // the first candidate that covers it.
        var fonts = new MapFontResolver(new() { ["Primary"] = AbFont, ["Second"] = CdFont, ["Third"] = AbFont });
        using var resolver = new HarfBuzzShaperResolver(fonts);
        var style = StyleWithFamilies("Primary, Second, Third");

        Assert.Equal(1, resolver.FindFallbackFont(style, ['C']));
        Assert.DoesNotContain("Third", fonts.Queried);
        Assert.DoesNotContain(HarfBuzzShaperResolver.SystemFallbackFamilies[0], fonts.Queried);

        // An uncovered character walks the rest of the candidates once; asking again costs nothing.
        Assert.Equal(0, resolver.FindFallbackFont(style, ['E']));
        var afterExhausted = fonts.Queried.Count;
        Assert.Equal(0, resolver.FindFallbackFont(style, ['E']));
        Assert.Equal(1, resolver.FindFallbackFont(style, ['D']));
        Assert.Equal(afterExhausted, fonts.Queried.Count);
    }

    [Fact]
    public void A_fallback_font_that_does_not_parse_is_skipped()
    {
        // PR #387 review — the safety validator only bounds the sfnt directory; a candidate whose tables are
        // corrupt (here an unknown maxp version) must be skipped, not abort the render.
        var broken = (byte[])CdFont.Clone();
        var maxp = NetPdf.Text.Fonts.OpenType.TableDirectory.Parse(broken).Tables[NetPdf.Text.Fonts.OpenType.OpenTypeTags.Maxp];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(broken.AsSpan((int)maxp.Offset), 0x7FFF0000u);
        using var resolver = new HarfBuzzShaperResolver(new MapFontResolver(new()
        {
            ["Primary"] = AbFont,
            ["Broken"] = broken,
            ["Second"] = CdFont,
        }));
        var style = StyleWithFamilies("Primary, Broken, Second");

        var index = resolver.FindFallbackFont(style, ['C']);
        Assert.Equal(CdFont, resolver.ResolveFontProgram(style, index).Bytes.ToArray());
    }

    [Fact]
    public void Unsafe_fallback_bytes_are_skipped_not_fatal()
    {
        using var resolver = new HarfBuzzShaperResolver(new MapFontResolver(new()
        {
            ["Primary"] = AbFont,
            ["Garbage"] = new byte[64],
            ["Second"] = CdFont,
        }));
        var style = StyleWithFamilies("Primary, Garbage, Second");
        Assert.Equal(CdFont, resolver.ResolveFontProgram(style, resolver.FindFallbackFont(style, ['C'])).Bytes.ToArray());
    }

    // ── End to end: shaped, embedded, and on the same baseline ────────────────────────────────

    private static PdfRenderResult Render(string families, string text) =>
        HtmlPdf.ConvertDetailed(
            $"<!doctype html><html><body><p style=\"font-family:{families};font-size:20px\">{text}</p></body></html>",
            new HtmlPdfOptions
            {
                FontResolver = new MapFontResolver(new() { ["Primary"] = AbFont, ["Second"] = CdFont }),
            });

    [Fact]
    public void Fallback_glyphs_are_embedded_from_their_own_font_and_share_the_baseline()
    {
        var result = Render("Primary, Second", "ABCDAB");
        var pdf = Encoding.Latin1.GetString(result.Pdf);

        Assert.DoesNotContain(result.Warnings, w => w.Code == "FONT-MISSING-GLYPH-001");
        // Two distinct embedded font programs: the A/B font and the C/D font.
        Assert.Equal(2, Regex.Matches(pdf, @"/FontFile2 ").Count);
        var fontsUsed = Regex.Matches(pdf, @"/(\w+) [\d.]+ Tf").Select(m => m.Groups[1].Value).Distinct().Count();
        Assert.Equal(2, fontsUsed);

        // Three text pieces (AB | CD | AB), each 2 glyphs, all on one baseline.
        var pieces = Regex.Matches(pdf, @"(-?[\d.]+) (-?[\d.]+) Td <([0-9A-Fa-f]+)> *Tj");
        Assert.Equal(3, pieces.Count);
        Assert.All(pieces, m => Assert.Equal(2, m.Groups[3].Value.Length / 4));
        var baselines = pieces.Select(m => double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)).Distinct();
        Assert.Single(baselines);
    }

    [Fact]
    public void Without_a_covering_font_the_missing_glyph_diagnostic_is_still_emitted()
    {
        var result = Render("Primary", "ABEF");
        Assert.Contains(result.Warnings, w => w.Code == "FONT-MISSING-GLYPH-001");
    }
}
