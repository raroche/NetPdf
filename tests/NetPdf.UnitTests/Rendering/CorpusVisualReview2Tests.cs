// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// End-to-end regressions for the second visual review of the NetPdf-tester corpus (after 1.1.0), through
/// the real facade (real fonts, real PDF bytes). Each case was checked against a browser at the A4 content
/// width:
/// <list type="number">
///   <item><b>05-payment-receipt</b> — an auto-width <c>position: absolute; right: 22px</c> "PAID" stamp
///   stretched across its whole containing block (no CSS 2.1 §10.3.7 shrink-to-fit).</item>
///   <item><b>05-payment-receipt</b> — an inline <c>&lt;svg&gt;</c> taller than the text was drawn ABOVE its
///   line (the check badge overlapped the card's top border).</item>
///   <item><b>01-cruise-booking-confirmation</b> — <c>flex: 1</c> cards ignored their automatic minimum
///   size, so the card holding a long e-mail stayed at an equal share and the address overflowed it.</item>
///   <item><b>01-cruise-booking-confirmation</b> — the source whitespace after a <c>&lt;br&gt;</c> became a
///   leading space on the next line (" Apt 7C").</item>
/// </list>
/// PDF coordinates are points (0.75 × CSS px) with y growing upward.
/// </summary>
public sealed class CorpusVisualReview2Tests
{
    private const double PxToPt = 0.75;

    private static byte[] Render(string body, string css) =>
        HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><meta charset='utf-8'><style>@page{size:A4;margin:20mm}"
            + "body{margin:0;font:12px/1.5 sans-serif}" + css + "</style></head><body>" + body + "</body></html>",
            new HtmlPdfOptions { PrintBackgrounds = true }).Pdf;

    private static string Page(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        foreach (Match sm in Regex.Matches(text, @"stream\r?\n(.*?)\r?\nendstream", RegexOptions.Singleline))
        {
            var s = sm.Groups[1].Value;
            if (s.Contains(" re") || s.Contains(" Do")) return s;
        }
        throw new Xunit.Sdk.XunitException("no page content stream");
    }

    /// <summary>Filled rectangles in the exact device-RGB fill, as (x, y, w, h) — y is the BOTTOM edge.</summary>
    private static List<(double X, double Y, double W, double H)> Rects(string page, double r, double g, double b)
    {
        var rects = new List<(double, double, double, double)>();
        foreach (Match m in Regex.Matches(page,
            @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) rg\s+(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re"))
        {
            if (Math.Abs(Num(m.Groups[1].Value) - r) > 0.002 || Math.Abs(Num(m.Groups[2].Value) - g) > 0.002
                || Math.Abs(Num(m.Groups[3].Value) - b) > 0.002) continue;
            rects.Add((Num(m.Groups[4].Value), Num(m.Groups[5].Value), Num(m.Groups[6].Value), Num(m.Groups[7].Value)));
        }
        return rects;
    }

    /// <summary>Image placements (<c>w 0 0 h x y cm /ImN Do</c>) as (x, y, w, h) — y is the BOTTOM edge.</summary>
    private static List<(double X, double Y, double W, double H)> Images(string page)
    {
        var images = new List<(double, double, double, double)>();
        foreach (Match m in Regex.Matches(page,
            @"(-?[\d.]+) 0 0 (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) cm /Im\d+ Do"))
            images.Add((Num(m.Groups[3].Value), Num(m.Groups[4].Value), Num(m.Groups[1].Value), Num(m.Groups[2].Value)));
        return images;
    }

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    // ── 05: the PAID stamp is shrink-to-fit ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("right:22px")]
    [InlineData("left:22px")]
    public void Absolute_auto_width_box_is_shrink_to_fit(string inset)
    {
        var page = Page(Render(
            "<div class='top'><div class='stamp'>PAID</div></div>",
            ".top{position:relative;height:120px;background:#ff0000}"
            + ".stamp{position:absolute;top:18px;" + inset + ";padding:4px 12px;border:3px solid #10b981;"
            + "background:#0000ff;color:#fff;font-weight:800;font-size:16px;letter-spacing:3px}"));

        var top = Assert.Single(Rects(page, 1, 0, 0));
        var stamp = Assert.Single(Rects(page, 0, 0, 1));
        // Pre-fix the stamp took the whole available width (the containing block minus 22px).
        Assert.True(stamp.W < top.W / 3, $"stamp should hug 'PAID': {stamp.W:0.#}pt of {top.W:0.#}pt");
        if (inset.StartsWith("right", StringComparison.Ordinal))
            Assert.Equal(top.X + top.W - 22 * PxToPt, stamp.X + stamp.W, precision: 0);
        else
            Assert.Equal(top.X + 22 * PxToPt, stamp.X, precision: 0);
    }

    // ── 05: an inline svg taller than the text stays inside its line ───────────────────────────

    [Fact]
    public void Tall_inline_svg_is_drawn_inside_the_padding_box_not_above_it()
    {
        var page = Page(Render(
            "<div class='top'><svg width='76' height='76' viewBox='0 0 76 76' xmlns='http://www.w3.org/2000/svg'>"
            + "<rect width='76' height='76' fill='#0000ff'/></svg></div>",
            ".top{padding-top:26px;text-align:center;background:#ff0000}"));

        var top = Assert.Single(Rects(page, 1, 0, 0));
        var svg = Assert.Single(Images(page));
        var contentTop = top.Y + top.H - 26 * PxToPt;
        // Its top is the content-box top (the line's top). Pre-fix it sat ~33px higher, over the padding
        // and the border — the check badge overlapping the receipt card's edge.
        Assert.Equal(contentTop, svg.Y + svg.H, precision: 0);
        Assert.True(svg.Y >= top.Y, "the svg must stay inside its container");
    }

    // ── 01: flex:1 cards honor their automatic minimum size ────────────────────────────────────

    [Fact]
    public void Flex_one_item_with_wide_content_widens_instead_of_overflowing()
    {
        var page = Page(Render(
            "<div class='cards'><div class='a'>d.whitfield@example.com.verylongaddress</div>"
            + "<div class='b'>x</div><div class='c'>x</div></div>",
            ".cards{display:flex;width:420px}.cards>div{flex:1}"
            + ".a{background:#ff0000}.b{background:#00ff00}.c{background:#0000ff}"));

        var a = Assert.Single(Rects(page, 1, 0, 0));
        var b = Assert.Single(Rects(page, 0, 1, 0));
        var c = Assert.Single(Rects(page, 0, 0, 1));
        // The long address is wider than an equal third (140px): its card takes the address width and the
        // other two share the rest equally (a browser does the same). Pre-fix all three were 140px.
        Assert.True(a.W > 140 * PxToPt + 1, $"card a should widen past an equal third: {a.W / PxToPt:0.#}px");
        Assert.Equal(b.W, c.W, precision: 1);
        Assert.Equal(420 * PxToPt, a.W + b.W + c.W, precision: 0);
    }

    // ── 01: no leading space after <br> ─────────────────────────────────────────────────────────

    [Fact]
    public void Line_after_a_br_has_no_leading_space()
    {
        // The source newline + indentation after each <br> must collapse away at the line start (CSS Text
        // §4.1.2), so each line holds exactly its own glyphs: "1420 Marina Boulevard" (21), "Apt 7C" (6),
        // "San Francisco" (13). Pre-fix lines 2 and 3 carried a leading space glyph (7 and 14), which also
        // widened them enough to wrap early in a narrow column.
        var pdf = Encoding.Latin1.GetString(Render(
            "<div class='v'>\n  1420 Marina Boulevard<br>\n        Apt 7C<br>\n        San Francisco\n</div>",
            ".v{font-weight:600}"));
        var glyphs = new List<int>();
        foreach (Match m in Regex.Matches(pdf, @"Td <([0-9A-Fa-f]+)> *Tj"))
            glyphs.Add(m.Groups[1].Value.Length / 4);
        Assert.Equal(new[] { 21, 6, 13 }, glyphs);
    }
}
