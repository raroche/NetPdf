// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 borders (PR 3) — faithful dashed / dotted border styles: a non-rounded edge is
/// STROKED with a dash pattern (the <c>d</c> + <c>S</c> operators) instead of being filled as a solid
/// approximation. Page content is uncompressed, so the operators are string-inspectable.</summary>
public sealed class BorderStylePaintTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    // border color #cc3366 → (0.8, 0.2, 0.4).
    private static string Html(string borderStyle) =>
        "<!DOCTYPE html><html><body>" +
        $"<div style=\"width:100px;height:60px;border:4px {borderStyle} #cc3366\"></div>" +
        "</body></html>";

    [Fact]
    public void Dashed_border_strokes_with_a_dash_pattern()
    {
        var text = Latin1(HtmlPdf.Convert(Html("dashed")));
        Assert.Contains("0.8 0.2 0.4 RG", text);   // stroke color (RG, not the rg fill)
        Assert.Contains("] 0 d", text);             // a dash array + phase
        Assert.Contains(" S Q", text);              // the stroke operator
    }

    [Fact]
    public void Dotted_border_strokes_with_round_caps()
    {
        var text = Latin1(HtmlPdf.Convert(Html("dotted")));
        Assert.Contains("0.8 0.2 0.4 RG", text);
        Assert.Contains("1 J", text);               // round line cap → round dots
        Assert.Contains("] 0 d", text);
    }

    [Fact]
    public void Solid_border_still_fills_a_rectangle()
    {
        var text = Latin1(HtmlPdf.Convert(Html("solid")));
        Assert.Contains("0.8 0.2 0.4 rg", text);   // fill color (rg)
        Assert.DoesNotContain(" S Q", text);        // no stroke
        Assert.DoesNotContain("] 0 d", text);       // no dash
    }
}
