// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// Corpus-fidelity (11 certificate `.citation`). A block whose used width comes from <c>max-width</c>
/// (auto width clamped below the available range) with <c>margin: 0 auto</c> must be CENTERED
/// (CSS 2.2 §10.3.3 + §10.4) — not left-aligned. Before the fix, auto-margin distribution fired only
/// for an EXPLICIT width, so a max-width-clamped auto-width block stayed at the left. Covered for both
/// a text-bearing (inline-only) block and an empty one; both must center like an explicit-width block.
/// </summary>
public sealed class MaxWidthAutoMarginCenteringTests
{
    private static double BlockLeftX(string html)
    {
        var pdf = Encoding.Latin1.GetString(
            HtmlPdf.ConvertDetailed(html, new HtmlPdfOptions { PrintBackgrounds = true }).Pdf);
        // The block's background rect is ~300px (225pt) wide; return its left x.
        foreach (Match m in Regex.Matches(pdf, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re\s+f"))
        {
            var w = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (w > 180 && w < 260) return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return double.NaN;
    }

    [Theory]
    [InlineData("<style>.c{background:#e5e5e5;max-width:300px;margin:0 auto}</style>"
        + "<div class=\"c\">text long enough to reach the max width limit right here now yes indeed ok</div>")]
    // Block-children (general recursion path) — a child <p> gives the wrapper height + a paint rect.
    [InlineData("<style>.c{background:#e5e5e5;max-width:300px;margin:0 auto}.c p{margin:0}</style>"
        + "<div class=\"c\"><p>block child content reaching the max-width bound here now ok yes</p></div>")]
    public void Max_width_block_with_auto_margins_is_centered(string html)
    {
        // An A4 content area is ~595pt − 2×~45pt margins; a centered 225pt (300px) block starts at
        // ~(595 − 225)/2 ≈ 185pt. A left-aligned block starts at the content-left (~45–75pt).
        var x = BlockLeftX(html);
        Assert.False(double.IsNaN(x), "no block background rect found");
        Assert.True(x > 150,
            $"max-width block left x={x:0.#}pt — it was left-aligned, not centered by margin:0 auto.");
    }
}
