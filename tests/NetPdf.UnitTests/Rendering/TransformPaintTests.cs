// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetPdf;
using NetPdf.Diagnostics;
using NetPdf.UnitTests.Pdf.Images;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 transforms — end-to-end <c>transform</c> painting: a transformed box wraps its
/// decoration (and its text, in the separate text pass) in a PDF <c>cm</c> about the
/// transform-origin; 3D functions flatten with a diagnostic; unparseable values paint untransformed
/// with a diagnostic.</summary>
public sealed class TransformPaintTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);
    private static int Count(string h, string n) => h.Split(n).Length - 1;
    private static HtmlPdfOptions Opts() => new() { FontResolver = new SynthFontResolver() };

    // translate(20px, 30px) ⇒ a position-independent cm of [1 0 0 1 15 -22.5] (px→pt + y-flip).
    private const string TranslateCm = "1 0 0 1 15 -22.5 cm";

    [Fact]
    public void Transformed_box_decoration_is_wrapped_in_a_cm()
    {
        var text = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;background-color:#3366cc;transform:translate(20px,30px)\"></div>" +
            "</body></html>"));

        Assert.Contains(TranslateCm, text);          // the q…cm wrapping the decoration
        Assert.Contains("0.2 0.4 0.8 rg", text);     // the background still paints (inside the transform)
    }

    [Fact]
    public void Transformed_box_with_text_wraps_both_decoration_and_glyphs()
    {
        var text = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<p style=\"background-color:#3366cc;transform:translate(20px,30px)\">A</p>" +
            "</body></html>", Opts()));

        // The SAME cm wraps the decoration pass AND the text pass → it appears at least twice.
        Assert.True(Count(text, TranslateCm) >= 2, "the transform must wrap both the box and its text");
        Assert.Contains("BT", text);                 // text was painted
    }

    [Fact]
    public void Three_d_transform_flattens_with_a_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:50px;height:50px;background-color:red;transform:rotateX(45deg)\"></div>" +
            "</body></html>");

        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssTransform3DUnsupported001);
    }

    [Fact]
    public void Unparseable_transform_paints_untransformed_with_a_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:50px;height:50px;background-color:red;transform:wobble(3)\"></div>" +
            "</body></html>");

        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssTransformUnsupported001);
        Assert.DoesNotContain(" cm", Latin1(result.Pdf)); // no transform was applied
    }

    [Fact]
    public void None_and_identity_emit_no_transform_and_no_diagnostic()
    {
        foreach (var value in new[] { "none", "scale(1)", "translate(0)" })
        {
            var result = HtmlPdf.ConvertDetailed(
                "<!DOCTYPE html><html><body>" +
                $"<div style=\"width:50px;height:50px;background-color:red;transform:{value}\"></div>" +
                "</body></html>");

            Assert.DoesNotContain(" cm", Latin1(result.Pdf));
            Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssTransform3DUnsupported001);
            Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssTransformUnsupported001);
        }
    }

    [Fact]
    public void Transformed_parent_also_transforms_child_text()
    {
        // PR #210 review [P1]: <div transform><p>A</p></div> — the CHILD paragraph's text must wrap
        // in the div's matrix too (transforms are subtree-local, not box-local).
        var text = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"transform:translate(20px,30px)\"><p>A</p></div>" +
            "</body></html>", Opts()));

        Assert.Contains(TranslateCm, text);
        var lastCm = text.LastIndexOf(TranslateCm, StringComparison.Ordinal);
        var btAfter = text.IndexOf("BT", lastCm, StringComparison.Ordinal);
        Assert.True(lastCm >= 0 && btAfter > lastCm, "child text must wrap in the parent transform");
    }

    [Fact]
    public void Transformed_image_wraps_its_xobject_placement()
    {
        // PR #210 review [P1]: a transformed <img> must transform the image XObject, not just decoration.
        var img = "data:image/png;base64," + Convert.ToBase64String(SyntheticPng.BuildOpaqueRgb8(8, 8));
        var text = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{img}\" style=\"display:block;width:40px;height:40px;transform:translate(20px,30px)\">" +
            "</body></html>"));

        Assert.Contains(TranslateCm, text);
        Assert.Contains(" Do", text); // the image XObject was placed
        var cmIdx = text.IndexOf(TranslateCm, StringComparison.Ordinal);
        var doIdx = text.IndexOf(" Do", StringComparison.Ordinal);
        Assert.True(cmIdx >= 0 && doIdx > cmIdx, "the image placement must wrap in the transform");
    }

    [Fact]
    public void Nested_transforms_compose()
    {
        // outer translate(10px, 0) ∘ inner translate(0, 20px) ⇒ the deepest text wraps in
        // translate(10, 20): cm = [1 0 0 1 7.5 -15] (px→pt + y-flip).
        var text = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"transform:translate(10px,0)\"><div style=\"transform:translate(0,20px)\">" +
            "<p>A</p></div></div>" +
            "</body></html>", Opts()));

        Assert.Contains("1 0 0 1 7.5 -15 cm", text);
    }

    private sealed class SynthFontResolver : IFontResolver
    {
        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = SyntheticFont.Build(), Family = query.Family });
    }
}
