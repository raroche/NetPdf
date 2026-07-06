// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf;
using NetPdf.Diagnostics;
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
    public void In_document_fragment_link_is_a_goto_destination_not_a_uri_action()
    {
        // A resolved #fragment link is emitted as an internal /GoTo (a direct /Dest), NOT a /URI action.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<a href=\"#section\" style=\"display:block;width:100px;height:20px\">jump</a>" +
            "<h2 id=\"section\">Section</h2></body></html>"));
        Assert.Contains("/Subtype /Link", pdf);
        Assert.Contains("/Dest [", pdf);
        Assert.DoesNotContain("/S /URI", pdf);
    }

    [Fact]
    public void No_links_emits_no_annots()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><p>plain text</p></body></html>"));
        Assert.DoesNotContain("/Annots", pdf);
    }

    [Fact]
    public void Mailto_scheme_is_allowed()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<a href=\"mailto:hi@example.com\" style=\"display:block;width:100px;height:20px\">mail</a>" +
            "</body></html>"));
        Assert.Contains("/Subtype /Link", pdf);
        Assert.Contains("(mailto:hi@example.com)", pdf);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/x")]
    [InlineData("myapp://do-thing")]
    [InlineData("../relative/page.html")] // relative, no safe BaseUri
    public void Unsafe_or_unresolved_scheme_never_becomes_a_link(string href)
    {
        // The security guarantee: a non-allowlisted / unresolved href NEVER produces a clickable
        // /Link annotation (some — data: / javascript: — are also stripped upstream by the HTML sanitizer).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<a href=\"{href}\" style=\"display:block;width:100px;height:20px\">x</a>" +
            "</body></html>"));
        Assert.DoesNotContain("/Subtype /Link", pdf);
    }

    [Theory]
    [InlineData("ftp://example.com/x")]
    [InlineData("myapp://do-thing")]
    [InlineData("../relative/page.html")]
    public void Blocked_scheme_that_reaches_the_collector_is_diagnosed(string href)
    {
        // For hrefs the sanitizer keeps (ftp / custom / relative), the link policy rejects them at emission
        // and surfaces LINK-URI-UNSUPPORTED-001 (data: / javascript: are stripped earlier, so no anchor).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            $"<a href=\"{href}\" style=\"display:block;width:100px;height:20px\">x</a>" +
            "</body></html>");
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.LinkUriUnsupported001);
    }

    [Fact]
    public void Annotations_are_indirect_objects()
    {
        // PR-229 review [P2]: /Annots must reference indirect objects, not inline direct dicts. So the
        // annotation's /Subtype /Link sits in its own `N 0 obj … endobj`, and /Annots holds `N 0 R` refs.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<a href=\"https://example.com/\" style=\"display:block;width:100px;height:20px\">x</a>" +
            "</body></html>"));
        Assert.Contains("/Annots [", pdf);
        Assert.Contains("0 R]", pdf.Replace(" ]", "]")); // /Annots holds an indirect ref array
        Assert.Contains("/Subtype /Link", pdf);          // the Link lives in its own object
    }
}
