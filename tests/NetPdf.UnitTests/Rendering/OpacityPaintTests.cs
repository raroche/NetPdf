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
        // No `Q` graphics-state RESTORE operator between them. Scanned as a real content-stream token — literal
        // `( … )` strings, `< … >` hex strings, `% …` comments, and `/names` are skipped, so a `Q` inside a PDF
        // string (e.g. `( Q ) Tj`) can't false-positive (PR-258 review [P3] / Copilot).
        Assert.False(ContainsRestoreOperator(between),
            $"a `Q` graphics-state restore appears between '{gsName}' and '{marker}' — the marked paint is not in scope");
    }

    /// <summary>True if the content-stream fragment contains a bare <c>Q</c> operator token, skipping over PDF
    /// literal strings (<c>( … )</c>, honoring <c>\</c> escapes + nested parens), hex strings (<c>&lt; … &gt;</c>),
    /// comments (<c>% …</c>), and names (<c>/…</c>) — the tokens that can carry a stray 'Q' that is not an
    /// operator.</summary>
    private static bool ContainsRestoreOperator(string s)
    {
        for (var i = 0; i < s.Length;)
        {
            var c = s[i];
            if (c == '%') { while (i < s.Length && s[i] != '\n' && s[i] != '\r') i++; continue; }
            if (c == '(')
            {
                var depth = 1; i++;
                while (i < s.Length && depth > 0)
                {
                    var d = s[i++];
                    if (d == '\\') i++;            // escape — skip the next char
                    else if (d == '(') depth++;
                    else if (d == ')') depth--;
                }
                continue;
            }
            if (c == '<')                          // hex string < … > (or a `<<` dict open — just step past it)
            {
                i++;
                if (i < s.Length && s[i] == '<') { i++; continue; }
                while (i < s.Length && s[i] != '>') i++;
                if (i < s.Length) i++;
                continue;
            }
            if (c == '/') { i++; while (i < s.Length && !IsPdfDelimiterOrSpace(s[i])) i++; continue; } // name
            if (IsPdfDelimiterOrSpace(c)) { i++; continue; }
            var start = i;                         // a regular token: an operator / number / keyword
            while (i < s.Length && !IsPdfDelimiterOrSpace(s[i])) i++;
            if (string.CompareOrdinal(s, start, "Q", 0, i - start) == 0 && i - start == 1) return true;
        }
        return false;
    }

    private static bool IsPdfDelimiterOrSpace(char c) =>
        char.IsWhiteSpace(c) || c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

    // --- AssertInAlphaScope robustness (PR-258 review [P3] / Copilot) ---

    [Fact]
    public void AssertInAlphaScope_ignores_a_Q_inside_a_pdf_string_literal()
    {
        // A `Q` inside a `( … )` text-show operand is NOT a graphics-state restore — the helper must not treat
        // it as one, so the marked paint still counts as inside the alpha scope.
        const string content = "q\n/GSop0_5 gs\n( Q ) Tj\n0.2 0.4 0.8 rg\nf\nQ\n";
        AssertInAlphaScope(content, "/GSop0_5 gs", "0.2 0.4 0.8 rg"); // must NOT throw
    }

    [Fact]
    public void AssertInAlphaScope_detects_a_real_Q_restore_operator()
    {
        // A genuine `Q` restore between the selection and the marker breaks the scope → the helper must fail.
        const string content = "q\n/GSop0_5 gs\nQ\n0.2 0.4 0.8 rg\nf\n";
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertInAlphaScope(content, "/GSop0_5 gs", "0.2 0.4 0.8 rg"));
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
