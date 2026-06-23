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
    public void Blurred_text_shadow_paints_a_sharp_offset_and_emits_the_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(Html("3px 3px 4px #ff0000"), Opts());
        var text = Latin1(result.Pdf);

        Assert.Contains("1 0 0 rg BT", text);   // still painted (blur approximated as a sharp offset)
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssTextShadowUnsupported001);
    }

    [Fact]
    public void Unsupported_unit_drops_the_shadow_with_a_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(Html("2em 2em #ff0000"), Opts());

        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssTextShadowUnsupported001);
        Assert.DoesNotContain("1 0 0 rg BT", Latin1(result.Pdf)); // value rejected → no red shadow
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
