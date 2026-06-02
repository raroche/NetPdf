// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf.Pdf;
using NetPdf.Pdf.Fonts;
using NetPdf.Text.Fonts.OpenType;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Fonts;

/// <summary>
/// Phase 5 layout→PDF cycle 5a-1 — the deferred Phase 1 Task 22:
/// <see cref="PdfDocument.RegisterFont"/> (indirect-object allocation + cross-reference
/// rewiring + content-hash dedup) and <see cref="PdfPage.AddFont"/> (per-page <c>/Font</c>
/// resource naming). The synthetic test font keeps output deterministic + independent of
/// any system-font registry.
/// </summary>
public sealed class FontRegistrationTests
{
    private static EmbeddedFont BuildEmbedded(params int[] glyphIds)
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int>(glyphIds));
        var subset = TtfSubsetter.Subset(font, plan);
        var toUnicode = ToUnicodeCMap.FromSubset(font, plan);
        return EmbeddedTtfFont.Build(font, subset, toUnicode);
    }

    private static string SaveAsText(PdfDocument doc) => Encoding.Latin1.GetString(doc.Save());

    [Fact]
    public void RegisterFont_returns_a_ref_and_counts_one_subset()
    {
        var doc = new PdfDocument();
        var fontRef = doc.RegisterFont(BuildEmbedded(1, 2));

        Assert.NotNull(fontRef);
        Assert.Equal(1, doc.RegisteredFontCount);
    }

    [Fact]
    public void RegisterFont_is_idempotent_for_the_same_subset()
    {
        var doc = new PdfDocument();
        var a = doc.RegisterFont(BuildEmbedded(1, 2));
        var b = doc.RegisterFont(BuildEmbedded(1, 2));   // a byte-identical subset

        Assert.Same(a, b);
        Assert.Equal(1, doc.RegisteredFontCount);
    }

    [Fact]
    public void RegisterFont_returns_distinct_refs_for_distinct_subsets()
    {
        var doc = new PdfDocument();
        var a = doc.RegisterFont(BuildEmbedded(1, 2));
        var b = doc.RegisterFont(BuildEmbedded(1));      // a different glyph set → different subset

        Assert.NotSame(a, b);
        Assert.Equal(2, doc.RegisteredFontCount);
    }

    [Fact]
    public void RegisterFont_after_save_throws()
    {
        var doc = new PdfDocument();
        doc.AddPage(new MediaBoxSize(100, 100));
        doc.Save();

        Assert.Throws<InvalidOperationException>(() => doc.RegisterFont(BuildEmbedded(1, 2)));
    }

    [Fact]
    public void Saved_pdf_embeds_the_font_graph_with_indirect_cross_references()
    {
        var doc = new PdfDocument();
        var fontRef = doc.RegisterFont(BuildEmbedded(1, 2));
        var page = doc.AddPage(new MediaBoxSize(100, 100));
        page.AddFont(fontRef);

        var pdf = SaveAsText(doc);

        // The five-object graph is present…
        Assert.Contains("/Type0", pdf);
        Assert.Contains("/CIDFontType2", pdf);
        Assert.Contains("/FontDescriptor", pdf);
        Assert.Contains("/FontFile2", pdf);
        Assert.Contains("/ToUnicode", pdf);
        // …and the structural cross-references are INDIRECT (separate objects), not the
        // EmbeddedFont's direct nesting — this is the whole point of RegisterFont.
        Assert.Matches(new Regex(@"/FontFile2\s+\d+ 0 R"), pdf);
        Assert.Matches(new Regex(@"/FontDescriptor\s+\d+ 0 R"), pdf);
        Assert.Matches(new Regex(@"/ToUnicode\s+\d+ 0 R"), pdf);
        Assert.Matches(new Regex(@"/DescendantFonts\s*\[\s*\d+ 0 R\s*\]"), pdf);
    }

    [Fact]
    public void One_subset_referenced_twice_emits_a_single_font_graph()
    {
        var doc = new PdfDocument();
        var fontRef = doc.RegisterFont(BuildEmbedded(1, 2));
        doc.RegisterFont(BuildEmbedded(1, 2));   // dedup — same subset, no second graph
        var p1 = doc.AddPage(new MediaBoxSize(100, 100));
        var p2 = doc.AddPage(new MediaBoxSize(100, 100));
        p1.AddFont(fontRef);
        p2.AddFont(fontRef);

        var pdf = SaveAsText(doc);

        Assert.Equal(1, CountOccurrences(pdf, "/CIDFontType2"));
        Assert.Equal(1, CountOccurrences(pdf, "/FontFile2"));
    }

    [Fact]
    public void Font_embedding_is_deterministic()
    {
        Assert.Equal(BuildOneFontDocument(), BuildOneFontDocument());

        static byte[] BuildOneFontDocument()
        {
            var doc = new PdfDocument();
            var fontRef = doc.RegisterFont(BuildEmbedded(1, 2));
            var page = doc.AddPage(new MediaBoxSize(100, 100));
            page.AddFont(fontRef);
            return doc.Save();
        }
    }

    [Fact]
    public void AddFont_allocates_F1_and_is_idempotent_per_ref()
    {
        var doc = new PdfDocument();
        var fontRef = doc.RegisterFont(BuildEmbedded(1, 2));
        var page = doc.AddPage(new MediaBoxSize(100, 100));

        var name1 = page.AddFont(fontRef);
        var name2 = page.AddFont(fontRef);   // same ref → same name, one /Font entry

        Assert.Equal("F1", name1.Value);
        Assert.Equal(name1.Value, name2.Value);
    }

    [Fact]
    public void AddFont_names_distinct_fonts_F1_then_F2()
    {
        var doc = new PdfDocument();
        var a = doc.RegisterFont(BuildEmbedded(1, 2));
        var b = doc.RegisterFont(BuildEmbedded(1));
        var page = doc.AddPage(new MediaBoxSize(100, 100));

        Assert.Equal("F1", page.AddFont(a).Value);
        Assert.Equal("F2", page.AddFont(b).Value);
        Assert.Equal("F1", page.AddFont(a).Value);   // the first font still dedups to F1
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }
}
