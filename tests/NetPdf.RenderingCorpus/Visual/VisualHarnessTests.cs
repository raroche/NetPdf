// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.RenderingCorpus.Visual;
using Xunit;

namespace NetPdf.RenderingCorpus.Visual;

/// <summary>Phase 4 PR 8 (PR-242 review [P1]) — the remote-resource detector that keeps diffable invoices
/// self-contained: it flags FETCHED http(s) resources (img/script <c>src</c>, CSS <c>url()</c>,
/// <c>&lt;link&gt;</c> href) but NOT a navigation <c>&lt;a href&gt;</c>.</summary>
public sealed class VisualHarnessTests
{
    [Fact]
    public void Detects_remote_image_script_link_and_css_url_resources()
    {
        const string html = """
            <link rel="stylesheet" href="https://cdn.example.com/app.css">
            <img src="https://img.example.com/logo.png">
            <div style="background:url('https://img.example.com/bg.png')"></div>
            <script src="http://cdn.example.com/x.js"></script>
            """;
        var remote = VisualHarness.RemoteResourceUrls(html);
        Assert.Equal(4, remote.Count);
    }

    [Fact]
    public void Ignores_navigation_anchor_hrefs_and_local_or_data_resources()
    {
        const string html = """
            <a href="https://useanvil.com">a link is navigation, not a fetched resource</a>
            <img src="data:image/png;base64,iVBORw0KGgo=">
            <img src="logo.png">
            <link rel="stylesheet" href="local.css">
            """;
        Assert.Empty(VisualHarness.RemoteResourceUrls(html));
    }

    [Fact]
    public void The_self_contained_diffable_invoice_has_no_remote_resources()
    {
        foreach (var invoice in VisualHarness.DiffableInvoices)
            Assert.Empty(VisualHarness.RemoteResourceUrls(VisualHarness.ReadInvoiceHtml(invoice)));
    }

    [Fact]
    public void Reference_page_paths_are_zero_padded_and_page_ordered()
    {
        Assert.EndsWith("01-classic-pure-css-page-001.png", VisualHarness.ReferencePagePath("01-classic-pure-css.html", 1));
        Assert.EndsWith("01-classic-pure-css-page-012.png", VisualHarness.ReferencePagePath("01-classic-pure-css.html", 12));
    }
}
