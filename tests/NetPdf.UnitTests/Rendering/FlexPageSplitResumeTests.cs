// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;
using Xunit.Abstractions;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// Flex pagination resume — when a nested flex row is split across a page boundary, its continuation
/// must RESUME on the next page (emit only the remaining content) rather than re-emit the whole item.
/// Pre-fix, a fresh resume page's spare room kept the would-overflow pagination gate OFF, so the
/// resumed flex ignored its resume-cut, re-rendered the whole item, and left the sibling cursor
/// under-advanced — so a trailing block painted on top of the flex content. This asserts no text run
/// overlaps the footer note's line for that scenario.
/// <para>NOTE: this fixes the resume-cut overlap family. The travel-corpus <c>03-itinerary</c>
/// document exhibits a related but content-specific footer overlap that this does not fully resolve —
/// tracked as a residual in <c>docs/deferrals.md</c>.</para>
/// </summary>
public sealed class FlexPageSplitResumeTests
{
    private readonly ITestOutputHelper _out;
    public FlexPageSplitResumeTests(ITestOutputHelper o) => _out = o;

    // Split the (uncompressed facade) PDF into per-page text-run lists: (y, glyphCount).
    private static List<List<(double y, int glyphs)>> RunsPerPage(string pdf)
    {
        var pages = new List<List<(double, int)>>();
        foreach (Match sm in Regex.Matches(pdf, @"stream\r?\n(.*?)\r?\nendstream", RegexOptions.Singleline))
        {
            var s = sm.Groups[1].Value;
            if (!s.Contains(" Tf") && !s.Contains("\nBT")) continue;   // skip non-content streams
            var runs = new List<(double, int)>();
            foreach (Match m in Regex.Matches(s, @"BT(.*?)ET", RegexOptions.Singleline))
            {
                var blk = m.Groups[1].Value;
                var hex = Regex.Match(blk, @"<([0-9A-Fa-f]+)> *T[jJ]");
                if (!hex.Success) continue;
                double y = double.NaN;
                var tm = Regex.Match(blk, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) Tm");
                if (tm.Success) y = double.Parse(tm.Groups[6].Value, CultureInfo.InvariantCulture);
                else { var td = Regex.Match(blk, @"(-?[\d.]+) (-?[\d.]+) (?:Td|TD)"); if (td.Success) y = double.Parse(td.Groups[2].Value, CultureInfo.InvariantCulture); }
                if (!double.IsNaN(y)) runs.Add((y, hex.Groups[1].Value.Length / 4));
            }
            if (runs.Count > 0) pages.Add(runs);
        }
        return pages;
    }

    [Fact]
    public void Paginated_nested_flex_row_resumes_and_the_trailing_note_does_not_overlap()
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><style>@page{size:A4;margin:16mm}");
        sb.Append(".day{display:flex;gap:16px;padding:12px 0;border-left:3px solid #ccc;margin-left:18px;padding-left:22px}");
        sb.Append(".body{flex:1}.acts li{display:flex;gap:10px;padding:2px 0}");
        sb.Append(".note{margin-top:22px;border-top:1px solid #ccc;padding-top:10px}");
        sb.Append("</style></head><body><div class=\"timeline\">");
        for (var d = 0; d < 9; d++)
        {
            sb.Append("<div class=\"day\"><div class=\"body\"><div>Port ").Append(d).Append("</div><ul class=\"acts\">");
            for (var a = 0; a < 4; a++) sb.Append("<li><span>0").Append(a).Append(":00</span><span>Activity number ").Append(a).Append(" at port ").Append(d).Append("</span></li>");
            sb.Append("</ul></div></div>");
        }
        // A distinctive long note we can find by glyph count.
        sb.Append("</div><div class=\"note\">Times are local to each port and subject to change based on weather conditions</div></body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString(), new HtmlPdfOptions { PrintBackgrounds = true });
        var pdf = Encoding.Latin1.GetString(result.Pdf);
        var pages = RunsPerPage(pdf);
        _out.WriteLine($"pages={pages.Count}");
        Assert.True(pages.Count >= 2, "expected the timeline to paginate to >=2 pages");

        // The note is the longest run in the document. Find it + assert no other run on its page sits
        // within its line band (± the ~13pt line height) — i.e. nothing overlaps the note vertically.
        foreach (var page in pages)
        {
            (double y, int g) noteRun = (double.NaN, 0);
            foreach (var r in page) if (r.glyphs > noteRun.g) noteRun = r;
            if (noteRun.g < 60) continue;   // this page doesn't hold the (long) note
            foreach (var (y, g) in page)
            {
                if (g == noteRun.g && y == noteRun.y) continue;              // the note itself
                var overlap = System.Math.Abs(y - noteRun.y) < 11.0;        // within one line height
                Assert.False(overlap, $"a text run (glyphs={g}) at y={y:0.#} overlaps the footer note at y={noteRun.y:0.#}");
            }
        }
    }
}
