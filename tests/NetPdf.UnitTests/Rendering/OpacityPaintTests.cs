// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf;
using NetPdf.Diagnostics;
using NetPdf.UnitTests.Pdf.Images;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 <c>opacity</c> — an element with <c>opacity &lt; 1</c> wraps its decoration in a constant
/// alpha graphics state (an <c>/ExtGState</c> carrying <c>/ca</c> AND <c>/CA</c>, selected with <c>gs</c>),
/// and the same alpha folds into the box's glyph fill. Page content is uncompressed, so the operators + the
/// resource are string-inspectable. <c>opacity: 1</c> (the initial) is a no-op.</summary>
public sealed class OpacityPaintTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static string Div(string opacity) =>
        "<!DOCTYPE html><html><body>" +
        $"<div style=\"width:100px;height:60px;background:#3366cc;opacity:{opacity}\">hi</div>" +
        "</body></html>";

    [Fact]
    public void Half_opacity_selects_a_constant_alpha_extgstate_with_both_ca_and_CA()
    {
        var text = Latin1(HtmlPdf.Convert(Div("0.5")));
        // The ExtGState resource carries both fill (/ca) and stroke (/CA) alpha at the exact value.
        Assert.Contains("/ca 0.5", text);
        Assert.Contains("/CA 0.5", text);
        // Selected in the content stream via the deduped GSop… name.
        Assert.Contains("/GSop0_5 gs", text);
        // The background still fills, now faded.
        Assert.Contains("0.2 0.4 0.8 rg", text);
    }

    [Fact]
    public void Percentage_opacity_is_accepted()
    {
        var text = Latin1(HtmlPdf.Convert(Div("50%")));
        Assert.Contains("/GSop0_5 gs", text);
    }

    [Fact]
    public void Opacity_one_emits_no_alpha_wrap()
    {
        var text = Latin1(HtmlPdf.Convert(Div("1")));
        Assert.DoesNotContain("/GSop", text);
        Assert.DoesNotContain("/ca 1 ", text);
    }

    [Fact]
    public void Opacity_zero_is_collected_and_wraps_at_alpha_zero()
    {
        var text = Latin1(HtmlPdf.Convert(Div("0")));
        Assert.Contains("/ca 0", text);
        Assert.Contains("/GSop0 gs", text);
    }

    [Fact]
    public void Out_of_range_opacity_clamps_to_one_and_emits_no_wrap()
    {
        // 1.5 clamps to 1 → opaque → no wrap (per CSS Color L4 the value is clamped for compositing).
        var text = Latin1(HtmlPdf.Convert(Div("1.5")));
        Assert.DoesNotContain("/GSop", text);
    }

    [Fact]
    public void Faded_element_folds_opacity_into_its_glyph_fill_alpha()
    {
        // The div's default-black text glyphs (alpha 1) fade to 0.5 → the glyph fill selects a /ca ExtGState.
        var text = Latin1(HtmlPdf.Convert(Div("0.5")));
        Assert.Contains("/GSca0_5 gs", text); // the fill-alpha ExtGState the glyph run selects
    }

    [Fact]
    public void Faded_element_emits_the_group_approximation_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(Div("0.4"));
        Assert.Contains(result.Warnings,
            d => d.Code == DiagnosticCodes.CssOpacityGroupApproximated001);
    }

    [Fact]
    public void Opaque_element_emits_no_opacity_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(Div("1"));
        Assert.DoesNotContain(result.Warnings,
            d => d.Code == DiagnosticCodes.CssOpacityGroupApproximated001);
    }

    // --- opacity applies to the whole descendant subtree (PR-255 review [P1]) ---

    [Fact]
    public void Parent_opacity_fades_a_child_that_declares_no_opacity_of_its_own()
    {
        // The child <p> declares NO opacity, but its faded parent (0.5) fades its rendered subtree — the
        // child's own background must paint under the same 0.5 constant alpha.
        var html =
            "<!DOCTYPE html><html><body>" +
            "<div style=\"opacity:0.5\">" +
            "<p style=\"width:80px;height:40px;background:#cc0000\">child</p>" +
            "</div></body></html>";
        var text = Latin1(HtmlPdf.Convert(html));
        Assert.Contains("/GSop0_5 gs", text); // the child's decoration is wrapped at the inherited alpha
        Assert.Contains("0.8 0 0 rg", text);  // #cc0000 still fills, now faded
    }

    [Fact]
    public void Parent_opacity_fades_a_child_paragraph_text()
    {
        // The child's text glyphs (own alpha 1) fade to the parent's 0.5 → the glyph fill selects the /ca
        // ExtGState, even though the <p> has no opacity of its own.
        var html =
            "<!DOCTYPE html><html><body>" +
            "<div style=\"opacity:0.5\"><p>child text</p></div>" +
            "</body></html>";
        var text = Latin1(HtmlPdf.Convert(html));
        Assert.Contains("/GSca0_5 gs", text);
    }

    [Fact]
    public void Nested_opacity_multiplies_down_the_subtree()
    {
        // div 0.5 over p 0.5 → the child's EFFECTIVE alpha is 0.25 (the product), not 0.5.
        var html =
            "<!DOCTYPE html><html><body>" +
            "<div style=\"opacity:0.5\">" +
            "<p style=\"width:80px;height:40px;background:#008000;opacity:0.5\">x</p>" +
            "</div></body></html>";
        var text = Latin1(HtmlPdf.Convert(html));
        Assert.Contains("/ca 0.25", text);
        Assert.Contains("/GSop0_25 gs", text);
    }

    [Fact]
    public void Parent_opacity_fades_a_nested_image()
    {
        // A faded parent fades a nested <img> (ImagePainter path) even though the <img> has no own opacity.
        var img = "data:image/png;base64," + System.Convert.ToBase64String(SyntheticPng.BuildOpaqueRgb8(8, 8));
        var html =
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"opacity:0.5\"><img src=\"{img}\" style=\"width:32px;height:32px\"></div>" +
            "</body></html>";
        var text = Latin1(HtmlPdf.Convert(html));
        Assert.Contains("/GSop0_5 gs", text); // the image draw is wrapped at the inherited alpha
    }

    [Fact]
    public void Opaque_parent_leaves_an_opaque_child_byte_identical()
    {
        // opacity:1 on the parent multiplies to 1 down the tree → no wrap anywhere (byte-identical).
        var html =
            "<!DOCTYPE html><html><body>" +
            "<div style=\"opacity:1\"><p style=\"background:#008000\">x</p></div>" +
            "</body></html>";
        var text = Latin1(HtmlPdf.Convert(html));
        Assert.DoesNotContain("/GSop", text);
    }
}
