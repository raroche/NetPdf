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
    public void Dotted_border_strokes_round_dots_with_a_zero_length_dash()
    {
        // 4px border → 3pt line width. Dotted = a ZERO-length on-dash + round caps → a true round
        // dot of diameter w, spaced 2w (= [0 6]) — NOT [w w] (which would be capsule/pill marks under
        // round caps; PR #228 review P2).
        var text = Latin1(HtmlPdf.Convert(Html("dotted")));
        Assert.Contains("0.8 0.2 0.4 RG", text);
        Assert.Contains("1 J", text);               // round line cap → round dots
        Assert.Contains("[0 6] 0 d", text);         // 0-length dash, 2w gap (NOT [3 3] / [w w])
        Assert.DoesNotContain("[3 3]", text);       // not the pill/capsule pattern
    }

    [Fact]
    public void Semi_transparent_dashed_border_composites_via_stroke_alpha_CA()
    {
        // PR #228 review P1 — a dashed/dotted border is STROKED, so its partial alpha must select a
        // STROKE-alpha ExtGState (/CA), not the fill /ca (which a stroke ignores → opaque). rgba alpha
        // 0.5 → 8-bit-quantized 128/255 = 0.501961.
        var text = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;border:4px dashed rgba(204,51,102,0.5)\"></div>" +
            "</body></html>"));
        Assert.Contains("/CA 0.501961", text);       // STROKE constant-alpha
        Assert.DoesNotContain("/ca 0.501961", text);  // ... never the fill alpha for the stroke
        Assert.Contains(" S Q", text);
    }

    [Fact]
    public void Solid_border_still_fills_a_rectangle()
    {
        var text = Latin1(HtmlPdf.Convert(Html("solid")));
        Assert.Contains("0.8 0.2 0.4 rg", text);   // fill color (rg)
        Assert.DoesNotContain(" S Q", text);        // no stroke
        Assert.DoesNotContain("] 0 d", text);       // no dash
    }

    [Fact]
    public void Double_border_paints_two_bands_in_the_base_color()
    {
        var text = Latin1(HtmlPdf.Convert(Html("double")));
        Assert.Contains("0.8 0.2 0.4 rg", text);   // the base color (the two thirds)
        // NOT a 3D-shaded fill (no dark / light shade).
        Assert.DoesNotContain("0.4 0.1 0.2 rg", text);
    }

    [Fact]
    public void Inset_border_uses_a_dark_and_a_light_3d_shade()
    {
        // #cc3366 → (0.8, 0.2, 0.4); dark = ×0.5 = (0.4, 0.1, 0.2); light = (0.9, 0.6, 0.7).
        var text = Latin1(HtmlPdf.Convert(Html("inset")));
        Assert.Contains("0.4 0.1 0.2 rg", text);   // dark (top / left)
        Assert.Contains("0.9 0.6 0.7 rg", text);   // light (bottom / right)
    }

    [Fact]
    public void Groove_border_splits_each_edge_into_two_shaded_halves()
    {
        var text = Latin1(HtmlPdf.Convert(Html("groove")));
        Assert.Contains("0.4 0.1 0.2 rg", text);   // dark half
        Assert.Contains("0.9 0.6 0.7 rg", text);   // light half
    }
}
