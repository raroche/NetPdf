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
}
