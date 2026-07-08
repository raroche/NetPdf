// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// RC-7 — a table pushed low on a page (by preceding content) whose repeated <c>&lt;thead&gt;</c> +
/// <c>&lt;tfoot&gt;</c> stack exceeded the space REMAINING on that page was misclassified as
/// "catastrophic": the layouter emitted the header + footer once, dropped ALL body rows, and let the
/// footer overprint the page edge (02-travel-quote lost its 5 rows + clipped "Estimated Total"). The
/// stack easily fits a FRESH page, so the fix breaks BEFORE the table — deferring the whole table to a
/// fresh page where it renders completely. A stack that exceeds even a fresh page stays truly
/// catastrophic (degrade + diagnostic), and the break-before can never loop.
/// </summary>
public sealed class TableBreakBeforeRc7Tests
{
    // Count text-position operators (coordinate-terminated `<x> <y> Td`) — a robust proxy for painted
    // text runs that can't match stray bytes in the compressed font stream.
    private static int TextRuns(byte[] pdf) =>
        Regex.Matches(Encoding.Latin1.GetString(pdf), @"-?\d+(?:\.\d+)? -?\d+(?:\.\d+)? Td").Count;

    private static PdfRenderResult Render(int cardHeightPx, int footerRows)
    {
        var foot = new StringBuilder();
        for (var i = 0; i < footerRows; i++)
            foot.Append("<tr><td>F").Append(i).Append("</td><td>T").Append(i).Append("</td></tr>");
        var body = new StringBuilder();
        for (var i = 0; i < 5; i++)
            body.Append("<tr><td>R").Append(i).Append("</td><td>v").Append(i).Append("</td></tr>");
        var cards = cardHeightPx > 0
            ? "<div class=\"options\"><div class=\"card\">A</div><div class=\"card\">B</div></div>"
            : string.Empty;
        var html =
            "<!doctype html><html><head><style>@page{size:A4;margin:20px}body{margin:0;font-size:12px}"
            + ".options{display:flex}.card{height:" + cardHeightPx + "px;width:50%;background:#eef}"
            + "table{width:100%;border-collapse:collapse}td,th{border:1px solid #333;padding:6px}"
            + "thead th{height:38px}</style></head><body>"
            + cards
            + "<table><thead><tr><th>H1</th><th>H2</th></tr></thead><tfoot>" + foot + "</tfoot>"
            + "<tbody>" + body + "</tbody></table></body></html>";
        return HtmlPdf.ConvertDetailed(html);
    }

    [Fact]
    public void Positionally_oversized_table_breaks_before_and_renders_every_row()
    {
        // thead + 9 tfoot rows exceed the ~180px remaining below 900px of flex cards, but fit a fresh
        // A4 page. The whole table must move to a fresh page and render completely — NOT drop the body.
        var pushed = Render(cardHeightPx: 900, footerRows: 9);
        var control = Render(cardHeightPx: 0, footerRows: 9);   // same table, room to render in place

        Assert.True(pushed.PageCount >= 2, "the pushed-down table should break onto a fresh page");
        Assert.DoesNotContain(pushed.Warnings, w => w.Code == "LAYOUT-TABLE-HEADER-FOOTER-OVERSIZED-001");
        Assert.DoesNotContain(pushed.Warnings, w => w.Code == "PDF-CONTENT-OVERFLOW-TRUNCATED-001");
        // The in-place control renders the full table; the pushed-down version renders it too (plus the
        // two flex-card labels), so it must have AT LEAST as many text runs — no rows dropped.
        Assert.True(TextRuns(pushed.Pdf) >= TextRuns(control.Pdf),
            $"pushed-down table dropped rows: {TextRuns(pushed.Pdf)} runs < in-place {TextRuns(control.Pdf)}");
    }

    [Fact]
    public void Table_that_fits_the_current_page_remainder_does_not_break()
    {
        // thead + 5 tfoot rows fit the ~380px below 700px of cards → no break, one page, no diagnostics.
        var res = Render(cardHeightPx: 700, footerRows: 5);
        Assert.Equal(1, res.PageCount);
        Assert.Empty(res.Warnings);
    }

    [Fact]
    public void Truly_oversized_header_footer_degrades_with_a_diagnostic_and_never_loops()
    {
        // 45 tfoot rows push the thead+tfoot stack past even a FRESH page's budget → genuinely can't
        // fit. It must DEGRADE (emit LAYOUT-TABLE-HEADER-FOOTER-OVERSIZED-001) and COMPLETE — the test
        // returning at all is the infinite-loop guard (a break-before loop would hang the run).
        var atTop = Render(cardHeightPx: 0, footerRows: 45);
        Assert.Contains(atTop.Warnings, w => w.Code == "LAYOUT-TABLE-HEADER-FOOTER-OVERSIZED-001");

        // Same stack, but pushed down: it fits neither the remainder NOR a fresh page, so it must still
        // degrade (NOT break-before, which would loop trying to find a page it fits on).
        var pushed = Render(cardHeightPx: 900, footerRows: 45);
        Assert.Contains(pushed.Warnings, w => w.Code == "LAYOUT-TABLE-HEADER-FOOTER-OVERSIZED-001");
    }
}
