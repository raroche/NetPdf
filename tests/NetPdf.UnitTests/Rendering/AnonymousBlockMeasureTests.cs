// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// Corpus-fidelity — a padded auto-height block container whose block children are separated by
/// pretty-print whitespace was over-inflated: the whitespace becomes <c>AnonymousBlock</c> wrappers
/// that REUSE the parent's style, and the subtree-visual-extent MEASURE pass charged the parent's
/// padding/border to each wrapper (the painter draws none). Result: 05's "Payment Received" box and
/// 06's code-band ballooned by ~padding per whitespace gap. The box height must not depend on
/// inter-element whitespace.
/// </summary>
public sealed class AnonymousBlockMeasureTests
{
    private static double? FirstRedFillHeight(string html)
    {
        var result = HtmlPdf.ConvertDetailed(html, new HtmlPdfOptions { PrintBackgrounds = true });
        var pdf = Encoding.Latin1.GetString(result.Pdf);
        // The container's background paints as `1 0 0 rg` then `x y w h re f`. Grab the first such h.
        var m = Regex.Match(pdf, @"1 0 0 rg\s+(?:[^A-Za-z]*?\s)?(-?[\d.]+) (-?[\d.]+) ([\d.]+) ([\d.]+) re\s+f",
            RegexOptions.Singleline);
        return m.Success ? double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : (double?)null;
    }

    [Fact]
    public void Padded_box_height_is_independent_of_inter_element_whitespace()
    {
        const string css = "<style>.box{padding:26px 0 22px;background:#ff0000;width:200px}"
            + ".line{height:20px}</style>";
        // Pretty-printed: newlines + indentation between .box's block children → AnonymousBlock wrappers.
        var pretty = "<!DOCTYPE html><html><head>" + css + "</head><body>\n  <div class=\"box\">\n"
            + "    <div class=\"line\">A</div>\n    <div class=\"line\">B</div>\n  </div>\n</body></html>";
        // Minified: no whitespace between children.
        var minified = "<!DOCTYPE html><html><head>" + css + "</head><body><div class=\"box\">"
            + "<div class=\"line\">A</div><div class=\"line\">B</div></div></body></html>";

        var hPretty = FirstRedFillHeight(pretty);
        var hMinified = FirstRedFillHeight(minified);
        Assert.NotNull(hPretty);
        Assert.NotNull(hMinified);
        // Same box, same content → identical painted height regardless of source whitespace.
        Assert.Equal(hMinified!.Value, hPretty!.Value, precision: 1);
        // And the height is the content-driven value (~2×20px + 48px padding = 88px ≈ 66pt), not a
        // whitespace-inflated one (pre-fix ≳ +36pt per gap).
        Assert.True(hPretty.Value < 80, $"box painted {hPretty:0.#}pt tall — inflated by whitespace anonymous-block padding.");
    }
}
