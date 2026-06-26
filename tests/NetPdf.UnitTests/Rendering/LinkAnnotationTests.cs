// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 links (PR 4) — <c>&lt;a href&gt;</c> elements become PDF <c>/Link</c> annotations with a
/// <c>/URI</c> action. Page content + objects are uncompressed, so the annotation dict is inspectable.</summary>
public sealed class LinkAnnotationTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    [Fact]
    public void Block_anchor_emits_a_link_annotation()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<a href=\"https://example.com/\" style=\"display:block;width:100px;height:20px\">site</a>" +
            "</body></html>"));
        Assert.Contains("/Subtype /Link", pdf);
        Assert.Contains("/S /URI", pdf);
        Assert.Contains("(https://example.com/)", pdf);
        Assert.Contains("/Annots", pdf);
    }

    [Fact]
    public void Inline_block_anchor_emits_a_link_annotation()
    {
        // An <a> that produces its own box fragment (block / inline-block / flex / grid item) gets a link
        // annotation. A precise rect for an <a> whose text flows inside a line box (inline-flow) needs
        // per-slice inline geometry — a documented follow-up.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<a href=\"https://netpdf.example/docs\" style=\"display:inline-block\">the docs</a>" +
            "</body></html>"));
        Assert.Contains("/Subtype /Link", pdf);
        Assert.Contains("(https://netpdf.example/docs)", pdf);
    }

    [Fact]
    public void In_document_fragment_link_is_not_emitted_as_a_uri_annotation()
    {
        // #fragment links need a name→destination map (a follow-up) — they are NOT URI actions.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<a href=\"#section\" style=\"display:block;width:100px;height:20px\">jump</a>" +
            "</body></html>"));
        Assert.DoesNotContain("/Subtype /Link", pdf);
    }

    [Fact]
    public void No_links_emits_no_annots()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><p>plain text</p></body></html>"));
        Assert.DoesNotContain("/Annots", pdf);
    }
}
