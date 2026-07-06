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
    public void Border_side_shorthand_with_var_component_color_paints()
    {
        // `.sig-lines .line { border-top: 1px solid var(--ink) }` (12), `.doc-header { border-bottom:
        // 3px solid var(--accent) }` → the var() is a COMPONENT (mixed with 3px + solid) → expands to
        // border-*-width/style/color. The border paints as a red fill rectangle (`1 0 0 rg` + `re f`).
        Assert.Contains("1 0 0 rg", Render(".box{border-bottom:3px solid var(--accent);width:100px;height:20px}"));
    }

    [Fact]
    public void Background_shorthand_var_color_clears_a_prior_background_image()
    {
        // CSS Backgrounds 3 §3.10 reset semantics: `background: var(--accent)` resets background-image
        // to `none`, so a PRIOR `background-image: <gradient>` must be cleared — only the solid color
        // paints, and no native shading (`/Sh … sh`) is emitted for the dropped gradient.
        var pdf = Render(".box{background-image:linear-gradient(90deg,#0000ff,#00ff00);background:var(--accent);width:100px;height:20px}");
        Assert.Contains("1 0 0 rg", pdf);          // the reset color paints
        Assert.DoesNotMatch(@"/Sh\d+ sh\b", pdf);  // the prior gradient image was cleared
    }

    [Fact]
    public void Whole_value_var_border_shorthand_renders_after_substitution()
    {
        // `border: var(--rule)` (the ENTIRE value is a var()) can't be classified into width/style/color
        // BEFORE substitution, so `CssPreprocessor` recovers the SHORTHAND itself (not longhands) and
        // `VarResolver` expands it AFTER substituting the `var()`. The green border now paints correctly
        // (was dropped). This also guards against the earlier misexpansion: a var-misclassified-as-color
        // would have emitted style:none → NO border, so a painted green border proves width/style survived.
        var pdf = Render(".box{--rule:3px solid #00ff00;border:var(--rule);width:100px;height:20px}");
        Assert.Contains("0 1 0 rg", pdf);   // the resolved green border paints
    }

    [Fact]
    public void Whole_value_var_border_shorthand_yields_a_side_to_a_later_explicit_longhand()
    {
        // Cascade order (review) — `border: var(--rule)` [red] then an explicit `border-top-color: blue`
        // that comes LATER + wins the cascade: the top edge is blue, the other three edges stay the
        // shorthand's red. The whole-value-var expansion must not clobber the winning explicit longhand.
        var pdf = Render(".box{--rule:3px solid #ff0000;border:var(--rule);border-top-color:#0000ff;width:100px;height:20px}");
        Assert.Contains("0 0 1 rg", pdf);   // the later explicit top color (blue) survives
        Assert.Contains("1 0 0 rg", pdf);   // the shorthand still supplies the other three edges (red)
    }

    [Fact]
    public void Whole_value_var_border_side_shorthand_renders_after_substitution()
    {
        // The per-side form (`border-bottom: var(--edge)`) takes the same whole-value path.
        var pdf = Render(".box{--edge:3px solid #ff0000;border-bottom:var(--edge);width:100px;height:20px}");
        Assert.Contains("1 0 0 rg", pdf);   // the resolved red bottom border paints
    }

    [Fact]
    public void Non_var_background_shorthand_is_unaffected()
    {
        // Guard: a literal-color shorthand still renders (AngleSharp handles it; the recovery is gated to
        // var() so it isn't double-recovered — keeps existing output byte-stable).
        Assert.Contains("1 0 0 rg", Render(".box{background:#ff0000;width:100px;height:20px}"));
    }
}
