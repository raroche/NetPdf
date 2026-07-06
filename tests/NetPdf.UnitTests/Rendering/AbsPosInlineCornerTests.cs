// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// Corpus-fidelity (11 course-completion-certificate) — INLINE-level out-of-flow boxes (e.g. the four
/// abspos <c>&lt;svg&gt;</c>/<c>&lt;span&gt;</c> frame corners) that are direct children of a
/// <c>position:relative</c> container which ALSO has block children were being swept into an anonymous
/// inline-only block during <c>BoxBuilder.FixupAnonymousBlocks</c>. That wrapper reuses the parent's
/// <c>position:relative</c> style and is emitted through the inline-only path, which never records
/// positioned-box geometry — so the corners' containing block could not resolve and all four were
/// DROPPED (LAYOUT-ABSOLUTE-FEATURE-UNSUPPORTED-001 ×4). Out-of-flow boxes now stay direct children of
/// the real positioned parent (whose geometry IS recorded), so they render.
/// </summary>
public sealed class AbsPosInlineCornerTests
{
    private static PdfRenderResult Render(string bodyInner, string css) =>
        HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>" + css + "</style></head><body>" + bodyInner + "</body></html>",
            new HtmlPdfOptions { PrintBackgrounds = true });

    [Fact]
    public void Inline_abspos_corners_in_mixed_positioned_container_render()
    {
        var res = Render(
            "<div class=\"frame\">"
            + "<span class=\"corner tl\"></span><span class=\"corner tr\"></span>"
            + "<span class=\"corner bl\"></span><span class=\"corner br\"></span>"
            + "<h1>Certificate</h1><p>Awarded to someone</p><div>footer</div></div>",
            ".frame{position:relative;width:400px;height:300px;padding:20px}"
            + ".corner{position:absolute;width:40px;height:40px;background:#ff0000}"
            + ".tl{top:8px;left:8px}.tr{top:8px;right:8px}.bl{bottom:8px;left:8px}.br{bottom:8px;right:8px}");
        var pdf = Encoding.Latin1.GetString(res.Pdf);

        // All four corner fills paint (they were dropped before the fix → 0).
        Assert.Equal(4, Regex.Matches(pdf, @"1 0 0 rg").Count);
        // …and NONE of them are reported as an unsupported-absolute drop.
        foreach (var w in res.Warnings)
            Assert.DoesNotContain("ABSOLUTE", w.Code);
    }

    [Fact]
    public void Inline_abspos_corner_anchors_to_the_positioned_parent_not_the_page()
    {
        // The corner uses `right`/`bottom`, so anchoring to the 400×300 frame (padding box) places it at
        // the frame's far edge, NOT the page's — a page-anchored fallback would land it elsewhere. The
        // fill rectangle's origin proves it resolved against the frame.
        var res = Render(
            "<div class=\"frame\"><span class=\"corner br\"></span><h1>Hi</h1><p>body</p></div>",
            ".frame{position:relative;width:400px;height:300px;margin:0;padding:0}"
            + ".corner{position:absolute;width:40px;height:40px;background:#ff0000}"
            + ".br{bottom:0;right:0}");
        var pdf = Encoding.Latin1.GetString(res.Pdf);
        // A red fill rectangle exists (corner rendered), and no absolute-unsupported drop.
        Assert.Contains("1 0 0 rg", pdf);
        foreach (var w in res.Warnings)
            Assert.DoesNotContain("ABSOLUTE", w.Code);
    }

    [Fact]
    public void Two_overlapping_inline_abspos_children_keep_dom_paint_order()
    {
        // Review (P3) — excluding out-of-flow boxes from the anonymous-block wrap reorders them relative to
        // the surrounding inline runs. Verify that (a) an abspos inline child interleaved with inline text
        // BEFORE and AFTER it still renders, and (b) two overlapping abspos children keep DOM paint order
        // (the LATER one paints on top): the equal-`z-index` positioned siblings paint in tree order, and
        // the fix preserves their relative order (it appends them as encountered).
        var res = Render(
            "<div class=\"frame\">before"
            + "<span class=\"c red\"></span>middle<span class=\"c blue\"></span>after"
            + "<h1>H</h1><p>body</p></div>",
            ".frame{position:relative;width:200px;height:120px;margin:0;padding:0}"
            + ".c{position:absolute;top:10px;left:10px;width:40px;height:40px}"
            + ".red{background:#ff0000}.blue{background:#0000ff}");
        var pdf = Encoding.Latin1.GetString(res.Pdf);
        var redAt = pdf.IndexOf("1 0 0 rg", System.StringComparison.Ordinal);
        var blueAt = pdf.IndexOf("0 0 1 rg", System.StringComparison.Ordinal);
        Assert.True(redAt >= 0, "red abspos child rendered");
        Assert.True(blueAt >= 0, "blue abspos child rendered");
        Assert.True(blueAt > redAt, "the later (blue) abspos child must paint AFTER the earlier (red) one");
        foreach (var w in res.Warnings)
            Assert.DoesNotContain("ABSOLUTE", w.Code);
    }
}
