// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using NetPdf.Diagnostics;
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
        // Resolve the /Dest's page object number + top, and prove it points at the LAST page (page 2),
        // not merely "some page" — a link that wrongly targeted page 1 or a bad top would fail here.
        var dest = Regex.Match(pdf, @"/Dest \[(\d+) 0 R /XYZ null ([\d.]+) null\]");
        Assert.True(dest.Success, "no resolvable /GoTo /Dest array found");
        var destPageObj = int.Parse(dest.Groups[1].Value);
        var destTop = double.Parse(dest.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);

        var pageObjs = PageObjectNumbersInOrder(pdf);
        Assert.True(pageObjs.Count >= 2, "the document should paginate to multiple pages");
        Assert.Equal(pageObjs[^1], destPageObj);               // the destination is the LAST page's object
        Assert.NotEqual(pageObjs[0], destPageObj);             // and NOT the first page (proves cross-page)
        Assert.True(destTop > 0 && destTop < 900,              // a plausible /XYZ top (A4 ≈ 842pt tall)
            $"destination top {destTop} is not a plausible page coordinate");
    }

    /// <summary>The object numbers of every <c>/Type /Page</c> object, in file order (= page order).</summary>
    private static System.Collections.Generic.List<int> PageObjectNumbersInOrder(string pdf)
    {
        var nums = new System.Collections.Generic.List<int>();
        foreach (Match m in Regex.Matches(pdf, @"(\d+) 0 obj\s*<<[^>]*?/Type\s*/Page[ /\r\n>]"))
            nums.Add(int.Parse(m.Groups[1].Value));
        return nums;
    }

    // ── Review #274 [P2]: fragment hrefs follow browser percent-decoding ────────────────────

    [Theory]
    [InlineData("r%C3%A9sum%C3%A9", "résumé")]   // UTF-8 percent-encoded non-ASCII id
    [InlineData("sec%2F2", "sec/2")]              // reserved char (/) percent-encoded
    [InlineData("plain", "plain")]               // unencoded — unchanged
    public void Fragment_href_is_percent_decoded_before_matching_the_id(string hrefFrag, string id)
    {
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>"
            + $"<a href=\"#{hrefFrag}\" style=\"display:block;width:120px;height:16px\">go</a>"
            + $"<h2 id=\"{id}\">Target</h2></body></html>");
        var pdf = Latin1(result.Pdf);

        // The (decoded) href matches the raw id → a /GoTo annotation, and NO unresolved warning.
        Assert.Contains("/Subtype /Link", pdf);
        Assert.Contains("/Dest [", pdf);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.LinkFragmentUnresolved001);
    }

    // ── Review #274 [P3]: target-side element types (block / inline-block / inline-flow) ─────

    [Theory]
    [InlineData("display:block")]         // block target — its own fragment
    [InlineData("display:inline-block")]  // atomic-inline target — its own fragment
    [InlineData("")]                       // inline-flow target — no own fragment; anchors to its line/ancestor
    public void Inline_flow_and_block_targets_all_resolve(string targetStyle)
    {
        var style = targetStyle.Length == 0 ? "" : $" style=\"{targetStyle}\"";
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>"
            + "<a href=\"#t\" style=\"display:block;width:120px;height:16px\">go</a>"
            + $"<p>See the <span id=\"t\"{style}>summary</span> below.</p></body></html>");
        var pdf = Latin1(result.Pdf);

        Assert.Contains("/Subtype /Link", pdf);
        Assert.Contains("/Dest [", pdf);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.LinkFragmentUnresolved001);
    }

    [Fact]
    public void Unresolved_fragment_link_warns_and_emits_no_annotation()
    {
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            "<a href=\"#nope\" style=\"display:block;width:120px;height:16px\">Broken</a>" +
            "</body></html>");
        var pdf = Latin1(result.Pdf);

        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.LinkFragmentUnresolved001);
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

    [Fact]
    public void An_invalid_cast_enum_value_throws_rather_than_silently_dropping_the_request()
    {
        // A bogus (cast) enum value is programmer error — surface it instead of silently omitting /PageLayout.
        Assert.ThrowsAny<System.ArgumentException>(() => HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><p>x</p></body></html>",
            new HtmlPdfOptions { PageLayout = (PdfPageLayout)999 }));
    }
}
