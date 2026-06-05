// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;
using NetPdf.Layout.Boxes;
using Xunit;

namespace NetPdf.UnitTests.Layout.Boxes;

/// <summary>
/// Tests for <see cref="MarginContentCollector"/>'s bounded running-element text read — the
/// allocation guard (post-PR-#150 review P2) for <c>content: element(name)</c> running content.
/// </summary>
public sealed class MarginContentCollectorTests
{
    private static async Task<IElement> MakeHost(string html, string id)
    {
        var ctx = BrowsingContext.New(Configuration.Default);
        var doc = await ctx.OpenAsync(req => req.Content(html));
        return doc.QuerySelector("#" + id)!;
    }

    [Fact]
    public async Task ReadBoundedDescendantText_caps_a_huge_element_to_maxChars()
    {
        // Review P2: a megabyte running element must NOT materialize its whole textContent — the read
        // stops at the cap, so the allocation is bounded regardless of the subtree size.
        var host = await MakeHost("<div id='h'>" + new string('A', 200_000) + "</div>", "h");
        Assert.Equal(64 * 1024, MarginContentCollector.ReadBoundedDescendantText(host, 64 * 1024).Length);
    }

    [Fact]
    public async Task ReadBoundedDescendantText_concatenates_nested_descendant_text_in_document_order()
    {
        // textContent is ALL descendant text in document order, not just direct children.
        var host = await MakeHost("<div id='h'>a<span>b<em>c</em>d</span>e</div>", "h");
        Assert.Equal("abcde", MarginContentCollector.ReadBoundedDescendantText(host, 64 * 1024));
    }

    [Fact]
    public async Task ReadBoundedDescendantText_under_the_cap_returns_the_full_text()
    {
        var host = await MakeHost("<div id='h'>Chapter One</div>", "h");
        Assert.Equal("Chapter One", MarginContentCollector.ReadBoundedDescendantText(host, 64 * 1024));
    }

    [Fact]
    public async Task ReadBoundedDescendantText_truncates_mid_text_node_at_the_cap()
    {
        // The cap can fall in the middle of a single text node — only `maxChars` are taken.
        var host = await MakeHost("<div id='h'>" + new string('A', 100) + "</div>", "h");
        Assert.Equal(new string('A', 10), MarginContentCollector.ReadBoundedDescendantText(host, 10));
    }
}
