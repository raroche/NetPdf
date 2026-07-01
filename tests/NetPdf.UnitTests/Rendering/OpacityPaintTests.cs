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

    /// <summary>Assert the graphics-state selection <paramref name="gsName"/> (e.g. <c>/GSop0_5 gs</c>) is in
    /// effect at <paramref name="marker"/> — it appears BEFORE the marker with no <c>Q</c> restore between them,
    /// so the marked paint is INSIDE that alpha scope. Stronger than mere containment (which an ancestor
    /// wrapper elsewhere in the stream could satisfy).</summary>
    private static void AssertInAlphaScope(string content, string gsName, string marker)
    {
        var markIdx = content.IndexOf(marker, System.StringComparison.Ordinal);
        Assert.True(markIdx >= 0, $"expected '{marker}' in the content stream");
        // The NEAREST selection preceding the marker is the enclosing one (a faded but empty ancestor emits its
        // own `q {gsName} Q` earlier — searching from the start would wrongly land on that closed scope).
        var gsIdx = content.LastIndexOf(gsName, markIdx, System.StringComparison.Ordinal);
        Assert.True(gsIdx >= 0, $"expected '{gsName}' selected before '{marker}'");
        var between = content.Substring(gsIdx + gsName.Length, markIdx - (gsIdx + gsName.Length));
        // No `Q` graphics-state RESTORE between them. Match `Q` as a whitespace-delimited operator TOKEN so a
        // literal 'Q' inside a PDF string/name can't false-positive (PR-257 Copilot).
        var tokens = between.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain("Q", tokens); // the alpha scope was NOT restored before the marked paint
    }

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
        // The child's #cc0000 fill must paint INSIDE the inherited 0.5 alpha scope (not merely somewhere in the
        // stream). The parent <div> has no background of its own, so the only fill is the child's.
        AssertInAlphaScope(text, "/GSop0_5 gs", "0.8 0 0 rg");
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
        // The image XObject invoke (` Do`) must sit inside the inherited 0.5 alpha scope.
        AssertInAlphaScope(text, "/GSop0_5 gs", " Do");
    }

    // --- CSS-wide opacity keywords resolve locally (PR-256 review [P2]) ---

    [Fact]
    public void Opacity_inherit_takes_the_parent_computed_value_then_multiplies()
    {
        // The child's own `opacity: inherit` = the parent's COMPUTED opacity (0.5); its effective/rendered alpha
        // is then that × the parent's subtree alpha (0.5) = 0.25.
        var html =
            "<!DOCTYPE html><html><body>" +
            "<div style=\"opacity:0.5\">" +
            "<p style=\"width:80px;height:40px;background:#3366cc;opacity:inherit\">x</p>" +
            "</div></body></html>";
        var text = Latin1(HtmlPdf.Convert(html));
        Assert.Contains("/ca 0.25", text);
        AssertInAlphaScope(text, "/GSop0_25 gs", "0.2 0.4 0.8 rg"); // #3366cc fill inside the 0.25 scope
    }

    [Fact]
    public void Opacity_initial_under_a_faded_parent_resets_own_alpha_but_the_subtree_still_fades()
    {
        // `opacity: initial` resets the child's OWN opacity to 1 (opaque for itself), but the parent's group
        // opacity (0.5) still fades the child's rendering → effective 0.5, NOT 0.25.
        var html =
            "<!DOCTYPE html><html><body>" +
            "<div style=\"opacity:0.5\">" +
            "<p style=\"width:80px;height:40px;background:#3366cc;opacity:initial\">x</p>" +
            "</div></body></html>";
        var text = Latin1(HtmlPdf.Convert(html));
        AssertInAlphaScope(text, "/GSop0_5 gs", "0.2 0.4 0.8 rg");
        Assert.DoesNotContain("/GSop0_25 gs", text); // initial is NOT the inherited 0.5, so no 0.25 multiply
    }

    [Fact]
    public void Opacity_unset_under_a_faded_parent_behaves_like_initial()
    {
        // opacity is not an inherited property → `unset` = `initial` = 1 for the child's own value; the subtree
        // fade (0.5) still applies → effective 0.5.
        var html =
            "<!DOCTYPE html><html><body>" +
            "<div style=\"opacity:0.5\">" +
            "<p style=\"width:80px;height:40px;background:#3366cc;opacity:unset\">x</p>" +
            "</div></body></html>";
        var text = Latin1(HtmlPdf.Convert(html));
        AssertInAlphaScope(text, "/GSop0_5 gs", "0.2 0.4 0.8 rg");
        Assert.DoesNotContain("/GSop0_25 gs", text);
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
