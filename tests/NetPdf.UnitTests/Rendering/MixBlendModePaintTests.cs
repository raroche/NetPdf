// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 mix-blend-mode (PR 4) — a <c>mix-blend-mode</c> wraps the box's decoration in a PDF
/// blend-mode graphics state (an <c>/ExtGState</c> with <c>/BM</c>, selected with <c>gs</c>). Page content
/// is uncompressed, so the operators + the resource are string-inspectable.</summary>
public sealed class MixBlendModePaintTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static string Html(string blendMode) =>
        "<!DOCTYPE html><html><body>" +
        $"<div style=\"width:100px;height:60px;background:#3366cc;mix-blend-mode:{blendMode}\"></div>" +
        "</body></html>";

    [Fact]
    public void Multiply_selects_a_BM_extgstate()
    {
        var text = Latin1(HtmlPdf.Convert(Html("multiply")));
        Assert.Contains("/BM /Multiply", text);        // the ExtGState resource
        Assert.Contains("/GSbmMultiply gs", text);     // selected in the content stream
        Assert.Contains("0.2 0.4 0.8 rg", text);       // the background still fills, blended
    }

    [Fact]
    public void Screen_maps_to_the_pdf_screen_mode()
    {
        var text = Latin1(HtmlPdf.Convert(Html("screen")));
        Assert.Contains("/BM /Screen", text);
    }

    [Fact]
    public void Normal_blend_mode_emits_no_extgstate()
    {
        var text = Latin1(HtmlPdf.Convert(Html("normal")));
        Assert.DoesNotContain("/BM", text);            // normal is the initial → no blend wrap
    }

    [Fact]
    public void Unknown_blend_mode_emits_no_extgstate()
    {
        var text = Latin1(HtmlPdf.Convert(Html("plus-lighter"))); // no PDF equivalent
        Assert.DoesNotContain("/BM", text);
    }
}
