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
/// Corpus-fidelity (11 certificate `.citation`). A block whose used width comes from <c>max-width</c>
/// (auto width clamped below the available range) with <c>margin: 0 auto</c> must be CENTERED
/// (CSS 2.2 §10.3.3 + §10.4) — not left-aligned. Before the fix, auto-margin distribution fired only
/// for an EXPLICIT width, so a max-width-clamped auto-width block stayed at the left.
///
/// The geometry is asserted against the ACTUAL content box (discovered from a full-width baseline
/// block), so the used width and centered left-x are pinned exactly — not merely "it moved right".
/// </summary>
public sealed class MaxWidthAutoMarginCenteringTests
{
    // (leftX, width) of the single solid `.c` background rect, in PDF user units (pt).
    private static (double X, double W) BlockRect(string html)
    {
        var pdf = Encoding.Latin1.GetString(
            HtmlPdf.ConvertDetailed(html, new HtmlPdfOptions { PrintBackgrounds = true }).Pdf);
        (double X, double W) best = (double.NaN, -1);
        foreach (Match m in Regex.Matches(pdf,
            @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re\s+f"))
        {
            var w = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            var h = double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            // Skip hairlines; the `.c` fill is the widest real rect (only one bg block per doc).
            if (h > 5 && w > 40 && w > best.W)
                best = (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), w);
        }
        return best;
    }

    // The content box [L, L+CW] discovered from an auto-width block that fills the whole content area.
    private static (double L, double CW) ContentBox()
    {
        var (x, w) = BlockRect(
            "<style>.c{background:#e5e5e5}</style><div class=\"c\">full width baseline block</div>");
        return (x, w);
    }

    [Theory]
    // Text-bearing (inline-only) block — the corpus `.citation` shape.
    [InlineData("<style>.c{background:#e5e5e5;max-width:300px;margin:0 auto}</style>"
        + "<div class=\"c\">text long enough to reach the max width limit right here now yes indeed ok</div>",
        225.0)]
    // Block-children (general recursion path) — a child <p> gives the wrapper height + a paint rect.
    [InlineData("<style>.c{background:#e5e5e5;max-width:300px;margin:0 auto}.c p{margin:0}</style>"
        + "<div class=\"c\"><p>block child content reaching the max-width bound here now ok yes</p></div>",
        225.0)]
    public void Max_width_block_with_auto_margins_is_centered(string html, double expectedWidthPt)
    {
        var (l, cw) = ContentBox();
        var (x, w) = BlockRect(html);
        Assert.False(double.IsNaN(x), "no block background rect found");
        // 300px == 225pt; the auto-width block clamps to the max-width.
        Assert.True(System.Math.Abs(w - expectedWidthPt) < 2,
            $"used width={w:0.#}pt, expected ~{expectedWidthPt:0.#}pt (max-width clamp).");
        var expectedX = l + (cw - w) / 2.0;
        Assert.True(System.Math.Abs(x - expectedX) < 2,
            $"left x={x:0.#}pt, expected centered ~{expectedX:0.#}pt (content box L={l:0.#}, CW={cw:0.#}).");
    }

    [Fact]
    public void Max_width_percent_block_with_auto_margins_is_centered()
    {
        var (l, cw) = ContentBox();
        var (x, w) = BlockRect(
            "<style>.c{background:#e5e5e5;max-width:50%;margin:0 auto}</style>"
            + "<div class=\"c\">a fifty percent max-width block centered by auto margins ok yes indeed</div>");
        Assert.True(System.Math.Abs(w - cw * 0.5) < 2,
            $"used width={w:0.#}pt, expected ~{cw * 0.5:0.#}pt (50% of content box CW={cw:0.#}).");
        var expectedX = l + (cw - w) / 2.0;
        Assert.True(System.Math.Abs(x - expectedX) < 2,
            $"left x={x:0.#}pt, expected centered ~{expectedX:0.#}pt.");
    }

    [Fact]
    public void Max_width_block_with_one_sided_auto_margin_is_right_aligned()
    {
        var (l, cw) = ContentBox();
        var (x, w) = BlockRect(
            "<style>.c{background:#e5e5e5;max-width:300px;margin-left:auto;margin-right:0}</style>"
            + "<div class=\"c\">a max-width block pushed to the right by a single auto left margin ok</div>");
        Assert.True(System.Math.Abs(w - 225.0) < 2, $"used width={w:0.#}pt, expected ~225pt.");
        // margin-left:auto absorbs ALL the free space → block sits flush at the content-right edge.
        var expectedX = l + (cw - w);
        Assert.True(System.Math.Abs(x - expectedX) < 2,
            $"left x={x:0.#}pt, expected right-aligned ~{expectedX:0.#}pt (R−W).");
    }

    [Fact]
    public void Non_clamping_max_width_100pct_with_auto_margins_fills_and_does_not_shift()
    {
        var (l, cw) = ContentBox();
        var (x, w) = BlockRect(
            "<style>.c{background:#e5e5e5;max-width:100%;margin:0 auto}</style>"
            + "<div class=\"c\">a max-width 100 percent block that does not clamp and keeps full width</div>");
        // max-width:100% doesn't clamp auto width → used width == content width, no free space to
        // distribute → left x stays at the content-left (auto margins resolve to 0).
        Assert.True(System.Math.Abs(w - cw) < 2, $"used width={w:0.#}pt, expected full CW={cw:0.#}pt.");
        Assert.True(System.Math.Abs(x - l) < 2, $"left x={x:0.#}pt, expected content-left ~{l:0.#}pt.");
    }
}
