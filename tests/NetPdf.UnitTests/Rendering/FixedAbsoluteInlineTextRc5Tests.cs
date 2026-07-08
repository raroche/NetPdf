// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// RC-5 — a <c>position: fixed</c> / <c>position: absolute</c> box with INLINE-ONLY content (the
/// ubiquitous <c>&lt;div class="footer"&gt;Total: $384.00&lt;/div&gt;</c>) had its own text SILENTLY
/// DROPPED: the content dispatch built the inner layouter without <c>layoutRootInlineContent</c>, so
/// the block-only child loop skipped the box's direct text runs (index.pdf's footer text was missing
/// on every page). Fixed by opting the abspos/fixed content dispatch into root-inline layout — and
/// suppressing the resulting root fragment's decoration (already painted by the box's own fragment) so
/// the text isn't accompanied by a duplicate background.
/// </summary>
public sealed class FixedAbsoluteInlineTextRc5Tests
{
    // Count only text-SHOWING operators (Tj / TJ) — NOT Td, which merely moves the text cursor and can
    // be emitted even when no glyphs are painted. Counting Td would let the test pass even if the
    // positioned footer's text were still dropped.
    private static int TextOps(byte[] pdf) =>
        Regex.Matches(Encoding.Latin1.GetString(pdf), @"\b(Tj|TJ)\b").Count;

    private static string Doc(string position) =>
        "<!doctype html><html><head><style>@page{size:A4;margin:0} body{margin:0;font-size:12px}"
        + ".content{height:200px}"
        + $".footer{{position:{position};left:0;bottom:0;width:320px;height:30px;background:#eef;padding:5px}}"
        + "</style></head><body><div class=\"content\">Body content</div>"
        + "<div class=\"footer\">Footer total here</div></body></html>";

    [Theory]
    [InlineData("fixed")]
    [InlineData("absolute")]
    public void Positioned_box_with_inline_text_renders_that_text(string position)
    {
        var res = HtmlPdf.ConvertDetailed(Doc(position), new HtmlPdfOptions { PrintBackgrounds = true });
        // Body + footer = two text-showing runs. Before the fix only the body's text emitted (1).
        Assert.True(TextOps(res.Pdf) >= 2,
            $"expected the {position} footer's text to render (>=2 text ops); got {TextOps(res.Pdf)}");
    }

    [Theory]
    [InlineData("fixed")]
    [InlineData("absolute")]
    public void Positioned_inline_only_root_text_is_isolated_and_rendered(string position)
    {
        // Isolate the footer: NO body text, so the ONLY text-showing operator can come from the
        // positioned box's own inline-only-root content. Its TextRun shares the box's out-of-flow
        // ComputedStyle; before the fix CollectInlineTextRuns treated that shared style as out-of-flow
        // and dropped the text, so this rendered ZERO text-show ops.
        var doc =
            "<!doctype html><html><head><style>@page{size:A4;margin:0} body{margin:0;font-size:12px}"
            + $".footer{{position:{position};left:0;bottom:0;width:320px;height:30px;background:#eef;padding:5px}}"
            + "</style></head><body><div class=\"footer\">Footer total here</div></body></html>";
        var res = HtmlPdf.ConvertDetailed(doc, new HtmlPdfOptions { PrintBackgrounds = true });
        Assert.True(TextOps(res.Pdf) >= 1,
            $"the {position} inline-only-root footer's own text must render (>=1 text-show op); got {TextOps(res.Pdf)}");
    }

    [Fact]
    public void Content_dispatch_does_not_add_a_duplicate_of_the_box_decoration()
    {
        // The content dispatch now lays out the root-inline text (RC-5); the decorationOwner
        // suppression must keep it from ALSO re-painting the box's own background. A plain absolute
        // box (no separate fixed-emission phantom) paints its background exactly once.
        var res = HtmlPdf.ConvertDetailed(Doc("absolute"), new HtmlPdfOptions { PrintBackgrounds = true });
        var s = Encoding.Latin1.GetString(res.Pdf);
        // #eef ≈ 0.933 0.933 1.0 — the footer background fill.
        var footerFills = Regex.Matches(s, @"0\.93\d* 0\.93\d* 1(?:\.0+)? rg").Count;
        Assert.Equal(1, footerFills);
    }
}
