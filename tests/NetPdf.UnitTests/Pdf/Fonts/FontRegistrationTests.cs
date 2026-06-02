// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf.Pdf;
using NetPdf.Pdf.Fonts;
using NetPdf.Pdf.Objects;
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

    // --- post-PR-#122 review ---------------------------------------------

    [Fact]
    public void AddFont_does_not_conflate_a_foreign_ref_with_the_same_object_number()
    {
        // (P2) Two documents number objects deterministically, so the first font in each
        // gets the same object number — but they're different objects in different stores.
        // AddFont must NOT dedup the foreign ref onto the local /F name (that would silently
        // swap the wrong font + skip preflight's foreign-ref rejection).
        var docA = new PdfDocument();
        var localRef = docA.RegisterFont(BuildEmbedded(1, 2));
        var docB = new PdfDocument();
        var foreignRef = docB.RegisterFont(BuildEmbedded(1, 2));

        Assert.Equal(localRef.ObjectNumber, foreignRef.ObjectNumber);   // same number…
        Assert.False(localRef.HasSameTarget(foreignRef));               // …different store.

        var page = docA.AddPage(new MediaBoxSize(100, 100));
        Assert.Equal("F1", page.AddFont(localRef).Value);
        Assert.Equal("F2", page.AddFont(foreignRef).Value);            // not conflated with F1
    }

    [Fact]
    public void RegisterFont_distinguishes_graphs_that_differ_only_in_a_dictionary()
    {
        // (P2) Same FontFile2 + ToUnicode streams, but a different /W (advance widths) → it
        // renders differently → it must NOT dedup onto the first graph.
        var original = BuildEmbedded(1, 2);
        var tweakedCid = CloneDict(original.CidFontDictionary);
        tweakedCid.Set(PdfNames.W, new PdfArray()
            .Add(new PdfInteger(0))
            .Add(new PdfArray().Add(new PdfInteger(123))));
        var tweaked = new EmbeddedFont
        {
            SubsetBaseFontName = original.SubsetBaseFontName,
            Type0FontDictionary = original.Type0FontDictionary,
            CidFontDictionary = tweakedCid,
            FontDescriptorDictionary = original.FontDescriptorDictionary,
            FontFile2Stream = original.FontFile2Stream,
            ToUnicodeStream = original.ToUnicodeStream,
        };

        var doc = new PdfDocument();
        doc.RegisterFont(original);
        doc.RegisterFont(tweaked);

        Assert.Equal(2, doc.RegisteredFontCount);
    }

    [Fact]
    public void RegisterFont_does_not_mutate_the_source_EmbeddedFont()
    {
        // (P3) RegisterFont clones each child dict before swapping in indirect refs, so the
        // caller's EmbeddedFont keeps its DIRECT child nesting (reusable across documents).
        var font = BuildEmbedded(1, 2);
        AssertDirectChildren(font);

        new PdfDocument().RegisterFont(font);

        AssertDirectChildren(font);

        static void AssertDirectChildren(EmbeddedFont f)
        {
            Assert.IsType<PdfStream>(f.FontDescriptorDictionary.Get(PdfNames.FontFile2));
            Assert.IsType<PdfDictionary>(f.CidFontDictionary.Get(PdfNames.FontDescriptor));
            Assert.IsType<PdfStream>(f.Type0FontDictionary.Get(PdfNames.ToUnicode));
            var descendants = Assert.IsType<PdfArray>(f.Type0FontDictionary.Get(PdfNames.DescendantFonts));
            PdfObject? first = null;
            foreach (var item in descendants) { first = item; break; }
            Assert.IsType<PdfDictionary>(first);   // the direct CID dict, not an indirect ref
        }
    }

    [Fact]
    public void Same_EmbeddedFont_registers_into_two_documents_without_cross_store_leakage()
    {
        // (P3) The same EmbeddedFont registered into two documents yields distinct refs from
        // distinct stores and never mutates the shared instance.
        var font = BuildEmbedded(1, 2);

        var refA = new PdfDocument().RegisterFont(font);
        var refB = new PdfDocument().RegisterFont(font);

        Assert.NotSame(refA, refB);
        Assert.False(refA.HasSameTarget(refB));   // different store id, despite same number
        Assert.IsType<PdfStream>(font.FontDescriptorDictionary.Get(PdfNames.FontFile2));   // still direct
    }

    [Fact]
    public void Saved_page_lists_the_font_in_its_Font_resource()
    {
        // (P3) The page's /Resources /Font maps the resource name to the font's indirect ref.
        var doc = new PdfDocument();
        var fontRef = doc.RegisterFont(BuildEmbedded(1, 2));
        var page = doc.AddPage(new MediaBoxSize(100, 100));
        var name = page.AddFont(fontRef);

        var pdf = SaveAsText(doc);

        Assert.Equal("F1", name.Value);
        Assert.Contains("/Resources", pdf);
        Assert.Contains("/Font", pdf);
        Assert.Matches(new Regex(@"/F1\s+\d+ 0 R"), pdf);   // /Font << /F1 N 0 R >>
    }

    [Fact]
    public void AddFont_after_save_throws()
    {
        // (P3) Once the document is saved the page is finalized — no further mutation.
        var doc = new PdfDocument();
        var fontRef = doc.RegisterFont(BuildEmbedded(1, 2));
        var page = doc.AddPage(new MediaBoxSize(100, 100));
        doc.Save();

        Assert.Throws<InvalidOperationException>(() => page.AddFont(fontRef));
    }

    private static PdfDictionary CloneDict(PdfDictionary source)
    {
        var clone = new PdfDictionary();
        foreach (var entry in source)
        {
            clone.Set(entry.Key, entry.Value);
        }
        return clone;
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
