// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// Same-document navigation — <c>&lt;a href="#id"&gt;</c> anchors become internal <c>/GoTo</c> link
/// annotations (a <c>/Dest</c> to the target element's page + top), and the catalog initial-view options
/// (<c>/PageLayout</c> / <c>/PageMode</c>) control how the document opens. Objects are uncompressed, so
/// the annotation + catalog dictionaries are inspectable. Like external links, an internal link needs an
/// anchor that produces its OWN box fragment (block / inline-block); inline-flow anchors are a documented
/// follow-up shared with the external-link path.
/// </summary>
public sealed class DocumentNavigationTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    [Fact]
    public void Internal_fragment_link_emits_a_goto_annotation_to_the_target()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<a href=\"#section\" style=\"display:block;width:120px;height:16px\">Jump</a>" +
            "<h2 id=\"section\">Section</h2></body></html>"));

        Assert.Contains("/Subtype /Link", pdf);
        Assert.Contains("/Dest [", pdf);
        Assert.Contains("/XYZ", pdf);
        Assert.Contains("/Annots", pdf);
    }

    [Fact]
    public void Internal_link_targets_an_element_on_a_later_page()
    {
        // The target sits on page 2 (a tall spacer pushes it there); the link's /Dest must reference a
        // page object, proving cross-page resolution works (the target isn't painted when the link is).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page{size:A4;margin:20mm}</style></head><body>" +
            "<a href=\"#end\" style=\"display:block;width:120px;height:16px\">To end</a>" +
            "<div style=\"height:1400px\"></div>" +
            "<h2 id=\"end\">The End</h2></body></html>"));

        Assert.Contains("/Subtype /Link", pdf);
        // A /GoTo destination array: [<page> /XYZ left top zoom].
        Assert.Matches(@"/Dest \[\d+ 0 R /XYZ", pdf);
    }

    [Fact]
    public void Unresolved_fragment_link_warns_and_emits_no_annotation()
    {
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            "<a href=\"#nope\" style=\"display:block;width:120px;height:16px\">Broken</a>" +
            "</body></html>");
        var pdf = Latin1(result.Pdf);

        Assert.Contains(result.Warnings, d => d.Code == "LINK-FRAGMENT-UNRESOLVED-001");
        Assert.DoesNotContain("/Subtype /Link", pdf);   // no clickable jump for a dangling target
    }

    [Fact]
    public void Page_layout_and_page_mode_are_emitted_when_requested()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><h1>Doc</h1></body></html>",
            new HtmlPdfOptions
            {
                PageLayout = PdfPageLayout.TwoColumnLeft,
                PageMode = PdfPageMode.UseOutlines,
            }));

        Assert.Contains("/PageLayout /TwoColumnLeft", pdf);
        Assert.Contains("/PageMode /UseOutlines", pdf);
    }

    [Fact]
    public void A_document_without_navigation_options_emits_no_layout_or_mode()
    {
        var pdf = Latin1(HtmlPdf.Convert("<!DOCTYPE html><html><body><p>plain</p></body></html>"));
        Assert.DoesNotContain("/PageLayout", pdf);
        Assert.DoesNotContain("/PageMode", pdf);
    }
}
