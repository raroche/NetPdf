// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// Corpus-fidelity — AngleSharp.Css 1.0.0-beta.144 drops a <c>background</c> / <c>border[-side]</c>
/// shorthand whose value contains <c>var()</c> (the reference can't be validated pre-substitution, so
/// the whole shorthand is rejected), even though the same <c>var()</c> in the equivalent longhand
/// resolves fine. <c>CssPreprocessor</c> now re-expands such a shorthand to its longhands with the
/// <c>var()</c> intact. These were the invisible-visual bugs: 08 bar fills, 09/12 logos, 12 signature
/// lines — every one of them declared a fill/border via a <c>var()</c>-carrying shorthand.
/// </summary>
public sealed class VarShorthandRecoveryTests
{
    private static string Render(string boxCss)
    {
        var html = "<!DOCTYPE html><html><head><style>:root{--accent:#ff0000;--ink:#008000}" + boxCss
            + "</style></head><body><div class=\"box\">x</div></body></html>";
        var result = HtmlPdf.ConvertDetailed(html, new HtmlPdfOptions { PrintBackgrounds = true });
        return Encoding.Latin1.GetString(result.Pdf);
    }

    [Fact]
    public void Background_shorthand_with_var_solid_color_paints()
    {
        // `.brand-mark { background: var(--accent) }` (12 logos) → background-color.
        Assert.Contains("1 0 0 rg", Render(".box{background:var(--accent);width:100px;height:20px}"));
    }

    [Fact]
    public void Background_shorthand_with_var_gradient_paints_a_shading()
    {
        // `.bar-fill { background: linear-gradient(90deg, var(--accent), var(--accent-2)) }` (08 bars,
        // 09 logo) → background-image → native shading.
        var pdf = Render(".box{background:linear-gradient(90deg,var(--accent),#0000ff);width:100px;height:20px}");
        Assert.Matches(@"/Sh\d+ sh\b", pdf);
    }

    [Fact]
    public void Border_side_shorthand_with_var_color_paints()
    {
        // `.sig-lines .line { border-top: 1px solid var(--ink) }` (12), `.doc-header { border-bottom:
        // 3px solid var(--accent) }` → border-*-width/style/color longhands.
        Assert.Contains("1 0 0", Render(".box{border-bottom:3px solid var(--accent);width:100px;height:20px}"));
    }

    [Fact]
    public void Non_var_background_shorthand_is_unaffected()
    {
        // Guard: a literal-color shorthand still renders (AngleSharp handles it; the recovery is gated to
        // var() so it isn't double-recovered — keeps existing output byte-stable).
        Assert.Contains("1 0 0 rg", Render(".box{background:#ff0000;width:100px;height:20px}"));
    }
}
