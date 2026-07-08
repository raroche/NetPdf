// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// RC-9 — phantom anonymous blocks from inter-tag whitespace. Pretty-printed HTML puts a
/// newline+indent between every pair of block siblings; before the fix each such collapsible-
/// whitespace run became an <c>AnonymousBlock</c> that REUSED the parent's ComputedStyle, charging
/// the parent's own margin once per run (the measure pass zeroed border/padding/height but not the
/// margin) and, for inline-only table cells, double-counting the cell's padding. Both inflated
/// vertical layout and pushed content onto extra pages. These render-level tests assert the
/// pretty-printed geometry now matches the equivalent tight markup.
/// </summary>
public sealed class PhantomAnonBlockRc9Tests
{
    // Bottom-most text baseline Y in the (uncompressed facade) content stream.
    private static double BottomTextY(byte[] pdf)
    {
        var s = Encoding.Latin1.GetString(pdf);
        double min = double.MaxValue;
        foreach (Match m in Regex.Matches(s, @"(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+Td"))
        {
            var y = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (y < min) min = y;
        }
        return min;
    }

    // Tallest content rectangle ("x y w h re") — the cell border/background box.
    private static double TallestRectHeight(byte[] pdf)
    {
        var s = Encoding.Latin1.GetString(pdf);
        double max = 0;
        foreach (Match m in Regex.Matches(
            s, @"(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+re"))
        {
            var h = Math.Abs(double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture));
            if (h > max && h < 400) max = h; // exclude full-page fills
        }
        return max;
    }

    private const string MarginPretty =
        "<!doctype html><html><head><style>body{margin:0;font-size:14px}"
        + ".c{margin-bottom:60px}</style></head><body>"
        + "<div class=\"c\">\n  <div>A</div>\n  <div>B</div>\n  <div>C</div>\n</div>"
        + "<div>after</div></body></html>";

    private const string MarginTight =
        "<!doctype html><html><head><style>body{margin:0;font-size:14px}"
        + ".c{margin-bottom:60px}</style></head><body>"
        + "<div class=\"c\"><div>A</div><div>B</div><div>C</div></div>"
        + "<div>after</div></body></html>";

    [Fact]
    public void Pretty_printed_blocks_do_not_inflate_layout_via_container_margin()
    {
        var pretty = BottomTextY(HtmlPdf.ConvertDetailed(MarginPretty).Pdf);
        var tight = BottomTextY(HtmlPdf.ConvertDetailed(MarginTight).Pdf);
        // Before the fix the pretty-printed "after" text sat lower on the page by
        // (whitespace-run count) x 60px. Now the two are identical.
        Assert.Equal(tight, pretty, precision: 1);
    }

    [Fact]
    public void Inline_only_table_cell_does_not_double_count_its_padding()
    {
        const string inlineOnly =
            "<!doctype html><html><head><style>body{margin:0;font-size:12px}"
            + "table{border-collapse:collapse}td{border:1px solid #000;padding:10px}"
            + "</style></head><body><table><tr><td>Xy</td></tr></table></body></html>";
        const string blockChild =
            "<!doctype html><html><head><style>body{margin:0;font-size:12px}"
            + "table{border-collapse:collapse}td{border:1px solid #000;padding:10px}.b{margin:0}"
            + "</style></head><body><table><tr><td><div class=\"b\">Xy</div></td></tr></table></body></html>";

        var inlineOnlyH = TallestRectHeight(HtmlPdf.ConvertDetailed(inlineOnly).Pdf);
        var blockChildH = TallestRectHeight(HtmlPdf.ConvertDetailed(blockChild).Pdf);
        // The inline-only cell (whose text is wrapped in an anonymous block sharing the
        // cell's style) must be the SAME height as the explicit-block-child cell; before
        // the fix it was taller by ~one padding box (the wrapper re-applied the padding).
        Assert.True(inlineOnlyH > 0 && blockChildH > 0, "expected cell rectangles in both renders");
        Assert.Equal(blockChildH, inlineOnlyH, precision: 1);
    }
}
