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

    [Fact]
    public void Percent_cross_size_flex_item_fills_a_definite_height_container()
    {
        // 10's barcodes: `.barcode{display:flex;align-items:flex-end;height:58px}` with bars
        // `.bar{width:6px;height:100%;background:#000}`. Pre-fix the height:100% resolved to 0 →
        // zero-height bars → no rect emitted. Now it resolves against the container's definite 58px.
        // 58px ≈ 43.5pt; assert a black fill whose rect height is ≳ 40pt is emitted.
        var pdf = Render(
            "<div class=\"barcode\"><span class=\"bar\"></span><span class=\"bar\"></span></div>",
            ".barcode{display:flex;align-items:flex-end;height:58px}.bar{width:6px;height:100%;background:#000000}");
        // A filled rect is `x y w h re f`. Find a black fill (`0 0 0 rg`) followed by a re with h≳40pt.
        Assert.Contains("0 0 0 rg", pdf);
        var tallRect = Regex.IsMatch(pdf, @"-?[\d.]+ -?[\d.]+ [\d.]+ (4[0-9]|[5-9][0-9])(\.\d+)? re",
            RegexOptions.None);
        Assert.True(tallRect, "no tall (≳40pt) filled rect — the height:100% bar collapsed to 0.");
    }

    [Fact]
    public void Percent_cross_size_flex_followed_by_a_block_does_not_overlap()
    {
        // Regression for the pre-measure/emission consistency of the cross-size fix (review P3-4): a
        // definite-height flex container with a percentage-cross-size child must reserve its full
        // height so a FOLLOWING block sits below it (no under-advance / overlap). Pre-fix the bar
        // collapsed to 0, but the container had an explicit height so this specific case advanced;
        // this guards that the resolved cross size + cursor stay consistent.
        var pdf = Render(
            "<div class=\"row\"><span class=\"bar\">B</span></div><div class=\"after\">AFTERBLOCK</div>",
            ".row{display:flex;align-items:flex-end;height:60px}.bar{width:8px;height:100%;background:#000}"
            + ".after{}");
        var xs = TextXs(pdf);   // both "B" and "AFTERBLOCK" render
        Assert.NotEmpty(xs);
        // Extract the y of every text run; the AFTER block's text must be well below the flex row top.
        var ys = new List<double>();
        foreach (Match m in Regex.Matches(pdf, @"BT(.*?)ET", RegexOptions.Singleline))
        {
            var blk = m.Groups[1].Value;
            if (!Regex.IsMatch(blk, @"<[0-9A-Fa-f]+> *T[jJ]")) continue;
            var tm = Regex.Match(blk, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) Tm");
            if (tm.Success) { ys.Add(double.Parse(tm.Groups[6].Value, CultureInfo.InvariantCulture)); continue; }
            var td = Regex.Match(blk, @"(-?[\d.]+) (-?[\d.]+) (?:Td|TD)");
            if (td.Success) ys.Add(double.Parse(td.Groups[2].Value, CultureInfo.InvariantCulture));
        }
        Assert.True(ys.Count >= 2, "expected both the bar label and the after-block text");
        var top = 0.0; var bottom = double.MaxValue;
        foreach (var y in ys) { if (y > top) top = y; if (y < bottom) bottom = y; }
        // The two runs are on different lines separated by ≳ the 60px (≈45pt) row height — i.e. the
        // after-block didn't collapse onto / overlap the flex row.
        Assert.True(top - bottom > 30, $"the after-block (Δy={top - bottom:0.#}pt) overlaps the flex row — the container height wasn't reserved.");
    }
}
