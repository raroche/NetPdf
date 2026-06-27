// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using NetPdf;
using NetPdf.Diagnostics;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 — faithful NON-SOLID styles on the uniform ROUNDED border + outline ring (previously
/// approximated as a solid ring). <c>dashed</c>/<c>dotted</c> stroke the rounded centreline (a curved path
/// stroke with a dash); <c>double</c> draws two concentric rounded rings; the 3D styles stay a solid-ring
/// approximation (diagnosed). Page content is uncompressed, so the operators are string-inspectable. Border
/// colour #cc3366 → (0.8, 0.2, 0.4).</summary>
public sealed class RoundedBorderStylePaintTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static int Count(string h, string n)
    {
        var c = 0;
        for (var i = h.IndexOf(n, StringComparison.Ordinal); i >= 0; i = h.IndexOf(n, i + n.Length, StringComparison.Ordinal)) c++;
        return c;
    }

    private static string Border(string borderStyle) =>
        "<!DOCTYPE html><html><body>" +
        $"<div style=\"width:100px;height:60px;border:9px {borderStyle} #cc3366;border-radius:15px\"></div>" +
        "</body></html>";

    private static string Outline(string outlineStyle) =>
        "<!DOCTYPE html><html><body>" +
        $"<div style=\"width:100px;height:60px;border-radius:15px;outline:9px {outlineStyle} #cc3366\"></div>" +
        "</body></html>";

    [Fact]
    public void Rounded_dashed_border_strokes_a_curved_path_with_a_dash()
    {
        var t = Latin1(HtmlPdf.Convert(Border("dashed")));
        Assert.Contains("0.8 0.2 0.4 RG", t);   // STROKE colour (RG), not the rg fill of a solid ring
        Assert.Contains("] 0 d", t);            // a dash array + phase
        Assert.Contains(" c ", t);              // the rounded centreline has Bézier corners
        Assert.Contains(" S Q", t);             // stroked, not filled
    }

    [Fact]
    public void Rounded_dotted_border_uses_round_caps()
    {
        var t = Latin1(HtmlPdf.Convert(Border("dotted")));
        Assert.Contains("0.8 0.2 0.4 RG", t);
        Assert.Contains("1 J", t);              // round line cap → true dots
        Assert.Contains("[0 ", t);              // zero-length on-dash
        Assert.Contains(" S Q", t);
    }

    [Fact]
    public void Rounded_double_border_draws_two_concentric_rings()
    {
        var t = Latin1(HtmlPdf.Convert(Border("double")));
        Assert.Equal(2, Count(t, "0.8 0.2 0.4 rg"));   // two ring FILLS (outer + inner thirds)
        Assert.DoesNotContain(" S Q", t);              // filled rings, not a stroke
    }

    [Fact]
    public void Rounded_solid_border_still_fills_one_ring_and_is_not_diagnosed()
    {
        var result = HtmlPdf.ConvertDetailed(Border("solid"));
        var t = Latin1(result.Pdf);
        Assert.Equal(1, Count(t, "0.8 0.2 0.4 rg"));   // single ring fill (unchanged)
        Assert.DoesNotContain(" S Q", t);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PaintBorderStyleApproximated001);
    }

    [Fact]
    public void Rounded_3d_border_stays_a_solid_ring_with_a_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(Border("groove"));
        Assert.Contains("0.8 0.2 0.4 rg", Latin1(result.Pdf));   // solid-ring approximation
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.PaintBorderStyleApproximated001);
    }

    [Fact]
    public void Rounded_dashed_outline_strokes_with_a_dash()
    {
        var t = Latin1(HtmlPdf.Convert(Outline("dashed")));
        Assert.Contains("0.8 0.2 0.4 RG", t);
        Assert.Contains("] 0 d", t);
        Assert.Contains(" S Q", t);
    }

    [Fact]
    public void Rounded_double_outline_draws_two_rings()
    {
        var t = Latin1(HtmlPdf.Convert(Outline("double")));
        Assert.Equal(2, Count(t, "0.8 0.2 0.4 rg"));
    }

    [Fact]
    public void Dashed_outline_is_no_longer_diagnosed_as_approximated()
    {
        var result = HtmlPdf.ConvertDetailed(Outline("dashed"));
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PaintBorderStyleApproximated001);
    }

    [Fact]
    public void Sharp_dashed_outline_also_strokes_with_a_dash()
    {
        // No border-radius → a SHARP outline; non-solid styles were also approximated as solid before.
        var t = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;outline:9px dashed #cc3366\"></div></body></html>"));
        Assert.Contains("0.8 0.2 0.4 RG", t);
        Assert.Contains("] 0 d", t);
        Assert.Contains(" S Q", t);
    }
}
