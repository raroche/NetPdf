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
/// Corpus-fidelity — a flex item with a percentage size + the default <c>flex-basis: auto</c> must
/// resolve that percentage against the container's definite size, not collapse to 0. Main axis
/// (<c>width: 48%</c>) fixes 07 "Bill To" off-page + 09 "Accepted by" clipped; cross axis
/// (<c>height: 100%</c>) fixes 10's zero-height barcodes.
/// </summary>
public sealed class FlexPercentSizeTests
{
    private readonly ITestOutputHelper _out;
    public FlexPercentSizeTests(ITestOutputHelper o) => _out = o;

    private static string Render(string body, string css)
    {
        var html = "<!DOCTYPE html><html><head><style>@page{size:A4;margin:16mm}" + css
            + "</style></head><body>" + body + "</body></html>";
        var result = HtmlPdf.ConvertDetailed(html, new HtmlPdfOptions { PrintBackgrounds = true });
        return Encoding.Latin1.GetString(result.Pdf);
    }

    /// <summary>All shown-text x-origins (handles the <c>Tm</c> and <c>Td</c> forms).</summary>
    private static List<double> TextXs(string pdf)
    {
        var xs = new List<double>();
        foreach (Match m in Regex.Matches(pdf, @"BT(.*?)ET", RegexOptions.Singleline))
        {
            var blk = m.Groups[1].Value;
            if (!Regex.IsMatch(blk, @"<[0-9A-Fa-f]+> *T[jJ]")) continue;
            var tm = Regex.Match(blk, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) Tm");
            if (tm.Success) { xs.Add(double.Parse(tm.Groups[5].Value, CultureInfo.InvariantCulture)); continue; }
            var td = Regex.Match(blk, @"(-?[\d.]+) (-?[\d.]+) (?:Td|TD)");
            if (td.Success) xs.Add(double.Parse(td.Groups[1].Value, CultureInfo.InvariantCulture));
        }
        return xs;
    }

    [Fact]
    public void Percent_width_flex_items_do_not_collapse_and_push_siblings_off_page()
    {
        // A4 content width = 210mm − 2×16mm = 178mm ≈ 504pt. Two 48% columns with space-between fit.
        // Pre-fix the width:48% resolved to 0, space-between shoved the 2nd column to the far right,
        // and its "RIGHTCOL" text overflowed the page edge (07/09).
        var pdf = Render(
            "<div class=\"parties\"><div class=\"p\">LEFTCOL</div><div class=\"p\">RIGHTCOL</div></div>",
            ".parties{display:flex;justify-content:space-between}.p{width:48%}");
        var xs = TextXs(pdf);
        _out.WriteLine("text xs: " + string.Join(",", xs));
        Assert.NotEmpty(xs);
        var maxX = 0.0;
        foreach (var x in xs) if (x > maxX) maxX = x;
        // No text starts past the ~504pt content edge (the page box is 595pt wide; the right margin
        // starts at ~549pt). Pre-fix the RIGHTCOL text started well beyond that.
        Assert.True(maxX < 520, $"a flex column's text starts at x={maxX:0.#}pt — the percentage width collapsed and pushed it off-page.");
    }
}
