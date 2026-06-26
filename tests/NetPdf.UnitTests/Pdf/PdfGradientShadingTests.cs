// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf.Pdf;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

/// <summary>
/// Phase 4 gradients (PR #209 review) — the PDF-layer shading primitives
/// <see cref="PdfDocument.RegisterAxialShading"/> / <see cref="PdfDocument.RegisterRadialShading"/>
/// over the shared <c>BuildGradientFunction</c>. Covers the FunctionType 3 strictly-increasing
/// <c>/Bounds</c> contract for hard-stop gradients ([P1]) and the function / shading reuse
/// cache ([P3]). Page content is emitted uncompressed, so the objects are string-inspectable.
/// </summary>
public sealed class PdfGradientShadingTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static PdfGradientStop Stop(double offset, double r, double g, double b) =>
        new(offset, r, g, b);

    private static int Count(string haystack, string needle) =>
        haystack.Split(needle).Length - 1;

    /// <summary>Every <c>/Bounds [...]</c> array in the saved document, parsed to doubles.</summary>
    private static List<double[]> AllBounds(string pdf) =>
        Regex.Matches(pdf, @"/Bounds \[([^\]]*)\]")
            .Select(m => m.Groups[1].Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.Parse(s, CultureInfo.InvariantCulture))
                .ToArray())
            .ToList();

    [Theory]
    // Duplicate TERMINAL stops at 100% — the [P1] bug: the old ceiling clamp produced duplicate
    // /Bounds at 1.0, violating the FunctionType 3 strictly-increasing contract.
    [InlineData(new[] { 1.0, 1.0, 1.0 })]
    // Duplicate LEADING stops at 0%.
    [InlineData(new[] { 0.0, 0.0, 0.5 })]
    // A duplicate MIDDLE hard stop (a legitimate `red 50%, blue 50%` shape).
    [InlineData(new[] { 0.0, 0.5, 0.5, 1.0 })]
    // Every stop coincident in the middle.
    [InlineData(new[] { 0.5, 0.5, 0.5 })]
    public void Axial_bounds_are_strictly_increasing_inside_the_open_unit_interval(double[] offsets)
    {
        var doc = new PdfDocument();
        var stops = offsets.Select((o, i) => Stop(o, i % 2, 0, 1 - (i % 2))).ToList();
        var shadingRef = doc.RegisterAxialShading(0, 0, 100, 0, stops);
        var page = doc.AddPage(MediaBoxSize.A4);
        page.PaintShadingInRect(shadingRef, 0, 0, 100, 100);
        var pdf = Latin1(doc.Save());

        Assert.Contains("/ShadingType 2", pdf);
        var allBounds = AllBounds(pdf);
        Assert.NotEmpty(allBounds); // these all stitch (≥ 3 control points)
        foreach (var bounds in allBounds)
        {
            for (var i = 0; i < bounds.Length; i++)
            {
                Assert.True(bounds[i] > 0.0 && bounds[i] < 1.0,
                    $"bound {bounds[i].ToString(CultureInfo.InvariantCulture)} must lie strictly inside (0, 1)");
                if (i > 0)
                    Assert.True(bounds[i] > bounds[i - 1],
                        $"/Bounds must strictly increase: {bounds[i - 1]} is not < {bounds[i]}");
            }
        }
    }

    [Fact]
    public void Radial_hard_stops_also_produce_valid_strictly_increasing_bounds()
    {
        var doc = new PdfDocument();
        var stops = new List<PdfGradientStop>
        {
            Stop(0, 1, 1, 1), Stop(1, 0, 0, 0), Stop(1, 1, 0, 0), // terminal hard stops
        };
        var shadingRef = doc.RegisterRadialShading(50, 50, 0, 50, stops);
        var page = doc.AddPage(MediaBoxSize.A4);
        page.PaintShadingInRect(shadingRef, 0, 0, 100, 100);
        var pdf = Latin1(doc.Save());

        Assert.Contains("/ShadingType 3", pdf);
        foreach (var bounds in AllBounds(pdf))
            for (var i = 1; i < bounds.Length; i++)
                Assert.True(bounds[i] > bounds[i - 1] && bounds[i] < 1.0);
    }

    [Fact]
    public void Identical_gradients_share_one_color_function()
    {
        // Three stacked boxes with the SAME gradient → three shadings (distinct axes) but ONE
        // shared color function: no per-fragment function-graph bloat (PR #209 review [P3]).
        var doc = new PdfDocument();
        var stops = new List<PdfGradientStop> { Stop(0, 1, 0, 0), Stop(1, 0, 0, 1) };
        var page = doc.AddPage(MediaBoxSize.A4);
        for (var i = 0; i < 3; i++)
        {
            var sh = doc.RegisterAxialShading(0, i * 50, 100, i * 50, stops);
            page.PaintShadingInRect(sh, 0, i * 50, 100, 40);
        }
        var pdf = Latin1(doc.Save());

        Assert.Equal(3, Count(pdf, "/ShadingType 2"));  // three distinct shadings (different axes)
        Assert.Equal(1, Count(pdf, "/FunctionType 2")); // ONE shared color function
    }

    [Fact]
    public void Coincident_axial_shadings_are_reused()
    {
        var doc = new PdfDocument();
        var stops = new List<PdfGradientStop> { Stop(0, 1, 0, 0), Stop(1, 0, 0, 1) };
        var a = doc.RegisterAxialShading(0, 0, 100, 0, stops);
        var b = doc.RegisterAxialShading(0, 0, 100, 0, stops);  // same axis + stops → same object
        var c = doc.RegisterAxialShading(0, 0, 100, 50, stops); // different axis → a new object
        Assert.Equal(a.ObjectNumber, b.ObjectNumber);
        Assert.NotEqual(a.ObjectNumber, c.ObjectNumber);
    }

    [Fact]
    public void Shading_ctm_is_concatenated_between_the_clip_and_the_sh()
    {
        // Phase 4 PR 1 — the optional shadingCtm (used to render a radial ellipse) emits a `cm`
        // AFTER the clip (`W n`) and BEFORE the `sh`, so the clip stays in page space.
        var doc = new PdfDocument();
        var stops = new List<PdfGradientStop> { Stop(0, 1, 0, 0), Stop(1, 0, 0, 1) };
        var sh = doc.RegisterRadialShading(50, 50, 0, 50, stops);
        var page = doc.AddPage(MediaBoxSize.A4);
        page.PaintShadingInRect(sh, 0, 0, 100, 100, radii: null, alpha: 1.0,
            shadingCtm: (2.0, 0.0, 0.0, 1.0, -50.0, 0.0));
        var pdf = Latin1(doc.Save());

        Assert.Matches(@"W n\s+2 0 0 1 -50 0 cm\s+/Sh1 sh", pdf);
    }

    [Fact]
    public void No_shading_ctm_emits_no_cm_before_the_sh()
    {
        var doc = new PdfDocument();
        var stops = new List<PdfGradientStop> { Stop(0, 1, 0, 0), Stop(1, 0, 0, 1) };
        var sh = doc.RegisterRadialShading(50, 50, 0, 50, stops);
        var page = doc.AddPage(MediaBoxSize.A4);
        page.PaintShadingInRect(sh, 0, 0, 100, 100);
        var pdf = Latin1(doc.Save());

        Assert.Matches(@"W n /Sh1 sh", pdf);   // no cm — byte-identical with the pre-ellipse path
        Assert.DoesNotContain("cm /Sh1 sh", pdf);
    }
}
