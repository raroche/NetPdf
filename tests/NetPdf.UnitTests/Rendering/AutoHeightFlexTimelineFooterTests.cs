// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// Corpus-fidelity (03 itinerary footer overlap) — an AUTO-height ROW flex container's used cross
/// (block) size is its content cross extent, but <c>FlexLinePacker</c> sizes each line from the items'
/// DECLARED cross only (0 for a content-determined item — it doesn't content-measure). So a block that
/// STACKS such flex rows (a <c>.timeline</c> of flex <c>.day</c> rows) reported a chrome-only block box
/// and a trailing sibling (a <c>.note</c>) was placed inside the flex content it overlapped. The flex
/// emission now resizes the wrapper to its real content extent (via the dispatch's
/// <c>LastEmittedBlockExtent</c> consumer) and the pagination measure folds in the content extent, so the
/// note lands BELOW the timeline. (Page COUNT is not asserted — a large auto-height-flex stack still
/// paginates conservatively pending the deferred mid-split work; this pins the no-OVERLAP invariant.)
/// </summary>
public sealed class AutoHeightFlexTimelineFooterTests
{
    private static string Doc(int days)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><style>@page{size:A4;margin:16mm}*{margin:0}");
        sb.Append(".day{display:flex;gap:16px;padding:12px 0;border-left:3px solid #ccc;margin-left:18px;padding-left:22px;position:relative}");
        sb.Append(".badge{position:absolute;left:-20px;top:12px;width:34px;height:34px}");
        sb.Append(".body{flex:1}.acts{margin:6px 0 0;padding:0;list-style:none}.acts li{display:flex;gap:10px;padding:2px 0}");
        sb.Append(".note{margin-top:22px;border-top:1px solid #ccc;padding-top:10px}");
        sb.Append("</style></head><body><div class=\"timeline\">");
        for (var d = 0; d < days; d++)
        {
            sb.Append("<div class=\"day\"><div class=\"badge\">D").Append(d).Append("</div><div class=\"body\">");
            sb.Append("<div class=\"toprow\">Port number ").Append(d).Append("</div><ul class=\"acts\">");
            for (var a = 0; a < 4; a++)
                sb.Append("<li><span>0").Append(a).Append(":00</span><span>Activity ").Append(a).Append(" at port ").Append(d).Append("</span></li>");
            sb.Append("</ul></div></div>");
        }
        // A UNIQUELY long single-line note → the max-glyph run, easy to find; must be the LAST content.
        sb.Append("</div><div class=\"note\">NOTEMARKER times local subject change weather captain discretion back board departure zulu</div></body></html>");
        return sb.ToString();
    }

    private static List<List<(double y, int g)>> Pages(string html)
    {
        var pdf = Encoding.Latin1.GetString(
            HtmlPdf.ConvertDetailed(html, new HtmlPdfOptions { PrintBackgrounds = true }).Pdf);
        var pages = new List<List<(double, int)>>();
        foreach (Match sm in Regex.Matches(pdf, @"stream\r?\n(.*?)\r?\nendstream", RegexOptions.Singleline))
        {
            var s = sm.Groups[1].Value;
            if (!s.Contains(" Tf")) continue;
            var runs = new List<(double, int)>();
            foreach (Match m in Regex.Matches(s, @"BT(.*?)ET", RegexOptions.Singleline))
            {
                var b = m.Groups[1].Value;
                var h = Regex.Match(b, @"<([0-9A-Fa-f]+)> *T[jJ]");
                if (!h.Success) continue;
                double y = double.NaN;
                var tm = Regex.Match(b, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) Tm");
                if (tm.Success) y = double.Parse(tm.Groups[6].Value, CultureInfo.InvariantCulture);
                else { var td = Regex.Match(b, @"(-?[\d.]+) (-?[\d.]+) (?:Td|TD)"); if (td.Success) y = double.Parse(td.Groups[2].Value, CultureInfo.InvariantCulture); }
                if (!double.IsNaN(y)) runs.Add((y, h.Groups[1].Value.Length / 4));
            }
            if (runs.Count > 0) pages.Add(runs);
        }
        return pages;
    }

    [Theory]
    [InlineData(4)]
    [InlineData(9)]
    public void Note_after_auto_height_flex_timeline_is_last_not_overlapping(int days)
    {
        var pages = Pages(Doc(days));
        var noteG = 0;
        foreach (var p in pages) foreach (var (_, g) in p) if (g > noteG) noteG = g;

        // The note (single longest run) appears exactly once.
        var noteHits = 0; var notePage = -1; var noteY = double.NaN;
        for (var pi = 0; pi < pages.Count; pi++)
            foreach (var (y, g) in pages[pi]) if (g == noteG) { noteHits++; notePage = pi; noteY = y; }
        Assert.Equal(1, noteHits);

        // On the note's page NOTHING (bar the running header ABOVE it) sits BELOW the note's baseline —
        // i.e. no `.day` content overlaps or follows the footer. (A page footer, if any, is the note here.)
        foreach (var (y, g) in pages[notePage])
            Assert.False(y < noteY - 0.5 && g != noteG,
                $"a run (glyphs={g}) at y={y:0.#} sits below the footer note (y={noteY:0.#}) — the note is not last.");
    }
}
