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
/// Phase 3 cycle-2d — the absolute-positioning pass runs on EVERY page, not only page 1. A
/// <c>position:absolute</c> box whose nearest positioned ancestor (its containing block) is laid out
/// on a RESUME page now paints on THAT page instead of being dropped with
/// <c>LAYOUT-ABSOLUTE-FEATURE-UNSUPPORTED-001</c>. This is the root cause of the 03-itinerary
/// <c>.day .badge</c> circles dropping (the timeline paginates, so most <c>.day</c>s — and their
/// abspos badges — land on page 2+).
/// </summary>
public sealed class AbsPosAcrossPaginationTests
{
    // A filler block that consumes ~a full page, then a position:relative container on page 2 holding
    // an abspos child (mirrors .day / .badge). `flexContainer` toggles the CB between a plain block
    // and a flex container (the exact 03 shape).
    private static string Doc(bool flexContainer)
    {
        var disp = flexContainer ? "display:flex;" : "";
        return "<!DOCTYPE html><html><head><style>@page{size:A4;margin:16mm}*{margin:0;box-sizing:border-box}"
            + ".filler{height:950px;background:#eef}"
            + ".card{" + disp + "position:relative;margin-top:20px;padding:12px 0 12px 30px;border:1px solid #ccc}"
            + ".badge{position:absolute;left:6px;top:12px;width:20px;height:20px;background:#0f7a5a;color:#fff}"
            + ".body{flex:1}</style></head><body>"
            + "<div class=\"filler\">page one filler</div>"
            + "<div class=\"card\"><div class=\"badge\">B</div><div class=\"body\">Card body on page two</div></div>"
            + "</body></html>";
    }

    private static List<string> PageStreams(byte[] pdf)
    {
        var s = Encoding.Latin1.GetString(pdf);
        return Regex.Matches(s, @"stream\r?\n(.*?)\r?\nendstream", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value).Where(x => x.Contains(" Tf") || x.Contains(" re")).ToList();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Abspos_child_of_positioned_container_on_page_2_is_not_dropped(bool flexContainer)
    {
        var res = HtmlPdf.ConvertDetailed(Doc(flexContainer), new HtmlPdfOptions { PrintBackgrounds = true });

        // The badge's containing block (.card) lands on page 2 → pre-cycle-2d it dropped. Now it paints.
        Assert.DoesNotContain(res.Warnings, w => w.Code == "LAYOUT-ABSOLUTE-FEATURE-UNSUPPORTED-001");

        // The badge's green fill (0.0588 0.478 0.352 rg, 8-bit quantized) is painted exactly once.
        var pdf = Encoding.Latin1.GetString(res.Pdf);
        var fillMatches = Regex.Matches(pdf, @"0\.058\d* 0\.47\d* 0\.35\d* rg").Count;
        Assert.Equal(1, fillMatches);
    }

    [Fact]
    public void Abspos_child_shares_exactly_one_content_stream_with_its_container()
    {
        var res = HtmlPdf.ConvertDetailed(Doc(flexContainer: true), new HtmlPdfOptions { PrintBackgrounds = true });
        var streams = PageStreams(res.Pdf);
        Assert.True(streams.Count >= 2, "expected a paginated (2-page) render");

        // The badge (green fill) appears in exactly ONE page-content stream, and that same stream
        // carries text (the card body) — i.e. it is emitted on the card's page, not smeared across
        // pages or orphaned onto the text-free filler page.
        var badgeFill = new Regex(@"0\.058\d* 0\.47\d* 0\.35\d* rg");
        var streamsWithBadge = streams.Where(st => badgeFill.IsMatch(st)).ToList();
        Assert.Single(streamsWithBadge);
        Assert.Contains(" Tf", streamsWithBadge[0]);
    }
}
