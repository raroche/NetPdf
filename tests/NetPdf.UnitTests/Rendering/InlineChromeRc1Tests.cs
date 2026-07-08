// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// RC-1 — horizontal margin/border/padding on a non-replaced inline element (`&lt;span&gt;`) was
/// silently ignored, so badges/pills and label↔value gaps collapsed to zero spacing (invoice-05 p8
/// "Amount due by &lt;date&gt;$&lt;amount&gt;" ran together). CollectInlineTextRuns dropped the
/// wrapper's own box-model chrome. The fix folds the open/close-edge advance (margin+border+padding
/// -left/right) into the wrapper's first/last leaf run at shaping time (no new break opportunity).
/// <para>Measured via the x-position of the LAST text-position (Td) operator — the text AFTER the
/// span. Comparing chrome-vs-control cancels the font specifics, so the delta is exactly the chrome
/// (40px = 30pt). All content stays on one line.</para>
/// </summary>
public sealed class InlineChromeRc1Tests
{
    // x of the last `<x> <y> Td` (the slice furthest along the inline axis = text after the span).
    private static double LastTdX(string html)
    {
        var doc = "<!doctype html><html><head><style>body{margin:0;font-size:20px}</style></head><body>"
            + html + "</body></html>";
        var s = Encoding.Latin1.GetString(HtmlPdf.Convert(doc));
        var xs = new List<double>();
        foreach (Match m in Regex.Matches(s, @"(-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?) Td"))
            xs.Add(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.True(xs.Count > 0, "expected at least one text-position operator");
        return xs.Max();
    }

    [Theory]
    [InlineData("margin-right:40px")]
    [InlineData("border-right:40px solid #000")]
    [InlineData("padding-right:40px")]
    public void End_edge_chrome_pushes_following_text_right_by_the_chrome(string css)
    {
        var control = LastTdX("<div>Xa<span>Yb</span>Zc</div>");
        var withChrome = LastTdX($"<div>Xa<span style=\"{css}\">Yb</span>Zc</div>");
        // 40px = 30pt. Before the fix the two were byte-identical (chrome ignored).
        Assert.Equal(control + 30.0, withChrome, precision: 0);
    }

    [Theory]
    [InlineData("padding-left:40px")]
    [InlineData("border-left:40px solid #000")]
    [InlineData("margin-left:40px")]
    public void Start_edge_chrome_pushes_the_span_content_and_everything_after(string css)
    {
        // The start-edge advance lands before the span's own content, so BOTH the span text and the
        // text after it shift right by the chrome.
        var control = LastTdX("<div>Xa<span>Yb</span>Zc</div>");
        var withChrome = LastTdX($"<div>Xa<span style=\"{css}\">Yb</span>Zc</div>");
        Assert.Equal(control + 30.0, withChrome, precision: 0);
    }

    [Fact]
    public void Chrome_accumulates_across_nested_inline_wrappers()
    {
        // margin-right:20px (15pt) on the outer + margin-right:16px (12pt) on the inner both trail the
        // same last glyph → the following text shifts right by 27pt.
        var control = LastTdX("<div>Xa<span><span>Yb</span></span>Zc</div>");
        var nested = LastTdX(
            "<div>Xa<span style=\"margin-right:20px\"><span style=\"margin-right:16px\">Yb</span></span>Zc</div>");
        Assert.Equal(control + 27.0, nested, precision: 0);
    }

    [Fact]
    public void Explicit_zero_chrome_adds_no_advance()
    {
        // A span whose margin/border/padding are all explicitly 0 must not shift anything — the advance
        // addition is gated on non-zero chrome, so it matches a plain span exactly.
        var plain = LastTdX("<div>Xa<span>Yb</span>Zc</div>");
        var zeroed = LastTdX("<div>Xa<span style=\"margin:0;border:0;padding:0\">Yb</span>Zc</div>");
        Assert.Equal(plain, zeroed, precision: 2);
    }

    [Fact]
    public void Visible_inline_border_surfaces_a_rule7_diagnostic_exactly_once()
    {
        // Inline decoration PAINTING is a documented residual, but it must not be SILENT (rule 7). A
        // bordered inline emits LAYOUT-INLINE-UNSUPPORTED-001 — once, even with several bordered spans.
        var one = HtmlPdf.ConvertDetailed(
            "<!doctype html><html><body><div>A<span style=\"border:2px solid red\">BB</span>C</div></body></html>");
        Assert.Equal(1, one.Warnings.Count(w => w.Code == "LAYOUT-INLINE-UNSUPPORTED-001"));

        var many = HtmlPdf.ConvertDetailed(
            "<!doctype html><html><body><div>"
            + "<span style=\"border:1px solid\">a</span><span style=\"border:1px solid\">b</span></div></body></html>");
        Assert.Equal(1, many.Warnings.Count(w => w.Code == "LAYOUT-INLINE-UNSUPPORTED-001"));

        // A spacing-only (margin/padding) inline is fully honored → no diagnostic.
        var spacingOnly = HtmlPdf.ConvertDetailed(
            "<!doctype html><html><body><div>A<span style=\"margin-right:20px\">BB</span>C</div></body></html>");
        Assert.DoesNotContain(spacingOnly.Warnings, w => w.Code == "LAYOUT-INLINE-UNSUPPORTED-001");
    }
}
