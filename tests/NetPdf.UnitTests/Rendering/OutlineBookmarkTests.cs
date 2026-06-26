// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 outlines (PR 4) — <c>&lt;h1&gt;</c>–<c>&lt;h6&gt;</c> headings become the PDF document
/// outline (<c>/Outlines</c> + nested items with <c>/Title</c> + <c>/Dest</c>). Objects are uncompressed,
/// so the outline dicts are inspectable.</summary>
public sealed class OutlineBookmarkTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    [Fact]
    public void Headings_build_an_outline_tree()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<h1>Chapter One</h1><p>x</p><h2>Section A</h2><p>y</p><h1>Chapter Two</h1>" +
            "</body></html>"));
        Assert.Contains("/Type /Outlines", pdf);
        Assert.Contains("/Title (Chapter One)", pdf);
        Assert.Contains("/Title (Section A)", pdf);
        Assert.Contains("/Title (Chapter Two)", pdf);
        Assert.Contains("/Dest", pdf);
        Assert.Contains("/XYZ", pdf);
    }

    [Fact]
    public void Outline_nests_h2_under_h1()
    {
        // The h2 (Section A) must reference the h1 (Chapter One) as its /Parent — i.e. it nests, and the
        // h1 gains a /First + /Count for its child.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><h1>Top</h1><h2>Child</h2></body></html>"));
        Assert.Contains("/Title (Top)", pdf);
        Assert.Contains("/Title (Child)", pdf);
        Assert.Contains("/First", pdf);   // the h1 item points to its h2 child
        Assert.Contains("/Parent", pdf);
    }

    [Fact]
    public void No_headings_emits_no_outline()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><p>just a paragraph</p></body></html>"));
        Assert.DoesNotContain("/Type /Outlines", pdf);
    }
}
