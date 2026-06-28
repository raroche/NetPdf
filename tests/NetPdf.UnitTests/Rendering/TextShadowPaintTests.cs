// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetPdf;
using NetPdf.Diagnostics;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 shadows — end-to-end <c>text-shadow</c> painting. A sharp shadow draws the
/// glyph run offset in the shadow color UNDER the main text; a blurred shadow paints a sharp
/// offset (a documented approximation) + a diagnostic; unsupported forms surface a diagnostic.
/// Deterministic text via a synthetic font.</summary>
public sealed class TextShadowPaintTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static HtmlPdfOptions Opts() => new() { FontResolver = new SynthFontResolver() };

    private sealed class SynthFontResolver : IFontResolver
    {
        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = SyntheticFont.Build(), Family = query.Family });
    }

    // The synthetic font's glyphs have EMPTY outlines (Skia reads no contours), so a blurred shadow
    // rasterizes nothing and falls back to sharp. The blur tests below therefore use the DEFAULT system
    // font resolver — a real single-face font (the system font index only indexes single-face TTF/OTF,
    // which both HarfBuzz and the Skia raster parse). They assert only structural presence (an image
    // XObject + its /SMask), so the chosen system font is immaterial.

    // Black text (0 0 0) + a red (1 0 0) text-shadow → unambiguous, distinct glyph-run colors.
    private static string Html(string textShadow) =>
        "<!DOCTYPE html><html><body>" +
        $"<p style=\"color:#000000;text-shadow:{textShadow}\">A</p>" +
        "</body></html>";

    [Fact]
    public void Sharp_text_shadow_draws_the_glyph_run_in_the_shadow_color_under_the_text()
    {
        var text = Latin1(HtmlPdf.Convert(Html("3px 3px #ff0000"), Opts()));

        Assert.Contains("1 0 0 rg BT", text);   // the red shadow glyph run
        Assert.Contains("0 0 0 rg BT", text);   // the black main text glyph run
        var shadowIdx = text.IndexOf("1 0 0 rg BT", StringComparison.Ordinal);
        var textIdx = text.IndexOf("0 0 0 rg BT", StringComparison.Ordinal);
        Assert.True(shadowIdx >= 0 && textIdx >= 0 && shadowIdx < textIdx,
            "the text-shadow must paint UNDER (before) the main text");
        // One shadow draw + one text draw for the single glyph run.
        Assert.Equal(2, text.Split(" Tj").Length - 1);
    }

    [Fact]
    public void Blurred_text_shadow_emits_the_blur_raster_info_not_the_unsupported_warning()
    {
        // The synthetic font's glyphs have empty outlines, so the raster yields nothing and falls back
        // to a sharp offset — but the collect-time diagnostic is the Info (blur is a supported,
        // rastered feature now), NOT the unsupported Warning.
        var result = HtmlPdf.ConvertDetailed(Html("3px 3px 4px #ff0000"), Opts());
        var text = Latin1(result.Pdf);

        Assert.Contains("1 0 0 rg BT", text);    // sharp fallback (synthetic empty glyphs)
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssTextShadowBlurRaster001);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssTextShadowUnsupported001);
    }

    [Fact]
    public void Blurred_text_shadow_with_a_real_font_emits_the_blur_raster_info()
    {
        var result = HtmlPdf.ConvertDetailed(Html("3px 3px 4px #ff0000"));
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssTextShadowBlurRaster001);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssTextShadowUnsupported001);
    }

    [Fact]
    public void Unsupported_unit_drops_the_shadow_with_a_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(Html("2em 2em #ff0000"), Opts());

        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssTextShadowUnsupported001);
        Assert.DoesNotContain("1 0 0 rg BT", Latin1(result.Pdf)); // value rejected → no red shadow
    }

    [Fact]
    public void Blurred_text_shadow_with_a_real_font_paints_a_rasterized_image_under_the_text()
    {
        var text = Latin1(HtmlPdf.Convert(Html("3px 3px 4px #ff0000")));

        Assert.Contains(" Do", text);            // the blurred shadow placed as an image XObject
        Assert.Contains("/SMask", text);         // ... with an alpha soft-mask
        Assert.Contains("BT", text);             // the main text still shows glyphs
        Assert.DoesNotContain("1 0 0 rg BT", text); // NOT a sharp red glyph run — the shadow is the image
    }

    [Fact]
    public void Sharp_text_shadow_with_a_real_font_uses_glyphs_not_an_image()
    {
        var text = Latin1(HtmlPdf.Convert(Html("3px 3px #ff0000")));

        Assert.Contains("1 0 0 rg", text);       // the sharp shadow is a red glyph run
        Assert.DoesNotContain(" Do", text);      // ... no rasterized shadow image
    }

    [Fact]
    public void Multiple_text_shadows_mix_a_blurred_image_and_a_sharp_run()
    {
        // First layer sharp green (on top), second layer blurred blue (bottom).
        var text = Latin1(HtmlPdf.Convert(Html("2px 2px #00ff00, 5px 5px 5px #0000ff")));

        Assert.Contains("0 1 0 rg", text);       // the sharp green layer (glyph run)
        Assert.Contains(" Do", text);            // the blurred blue layer (image)
        Assert.Contains("/SMask", text);
    }

    [Fact]
    public void Text_shadow_inherits_to_descendant_text()
    {
        // The <div> declares a red shadow; the <p> has none of its own → its text inherits it.
        var text = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>"
            + "<div style=\"color:#000000;text-shadow:3px 3px #ff0000\"><p>A</p></div>"
            + "</body></html>", Opts()));

        Assert.Contains("1 0 0 rg BT", text);   // the inherited red shadow on the <p>'s text
        Assert.Contains("0 0 0 rg BT", text);   // the black main text
    }

    [Fact]
    public void A_childs_own_text_shadow_overrides_the_inherited_one()
    {
        var text = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>"
            + "<div style=\"text-shadow:3px 3px #ff0000\">"
            + "<p style=\"color:#000000;text-shadow:1px 1px #00ff00\">A</p></div>"
            + "</body></html>", Opts()));

        Assert.Contains("0 1 0 rg BT", text);       // the child's own green shadow
        Assert.DoesNotContain("1 0 0 rg BT", text); // NOT the inherited red one
    }

    [Fact]
    public void A_child_text_shadow_none_resets_the_inherited_shadow()
    {
        var text = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>"
            + "<div style=\"text-shadow:3px 3px #ff0000\">"
            + "<p style=\"color:#000000;text-shadow:none\">A</p></div>"
            + "</body></html>", Opts()));

        Assert.Contains("0 0 0 rg BT", text);       // the text still paints
        Assert.DoesNotContain("1 0 0 rg BT", text); // `none` reset the inherited shadow
    }

    [Fact]
    public void None_paints_only_the_text_with_no_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(Html("none"), Opts());
        var text = Latin1(result.Pdf);

        Assert.Contains("0 0 0 rg BT", text);    // the text still paints
        Assert.DoesNotContain("1 0 0 rg BT", text);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssTextShadowUnsupported001);
    }
}
