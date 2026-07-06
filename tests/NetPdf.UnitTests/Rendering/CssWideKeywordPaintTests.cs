// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Linq;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// A CSS-wide keyword (<c>initial</c> / <c>inherit</c> / <c>unset</c> / <c>revert</c>) on a paint
/// property is the cascade's value, resolving to the property's initial (= <c>none</c> / no effect) —
/// NOT an authoring error. The paint properties are read as RAW winners (bypassing the typed resolver),
/// so a CSS-wide keyword reached the parser verbatim and was rejected as "unsupported", firing a spurious
/// diagnostic on nearly every document (the <c>background: #color</c> shorthand expands to
/// <c>background-image: initial</c>). This asserts those keywords are now treated as the reset with NO
/// unsupported diagnostic. Extends the central CSS-wide-keyword handling (PR #271) to the raw-read
/// paint path.
/// </summary>
public sealed class CssWideKeywordPaintTests
{
    private static void AssertNoUnsupportedFor(string inlineStyle)
    {
        var result = HtmlPdf.ConvertDetailed(
            $"<!DOCTYPE html><html><body><div style=\"{inlineStyle};width:80px;height:24px\">x</div></body></html>");
        var offenders = result.Warnings
            .Where(w => w.Code.Contains("UNSUPPORTED", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(offenders.Count == 0,
            $"style '{inlineStyle}' emitted a spurious unsupported diagnostic: "
            + string.Join(", ", offenders.Select(o => o.Code)));
    }

    [Theory]
    [InlineData("background:#3366cc")]              // shorthand → background-image: initial
    [InlineData("background-image:initial")]
    [InlineData("background-image:inherit")]
    [InlineData("background-image:unset")]
    [InlineData("box-shadow:initial")]
    [InlineData("text-shadow:initial")]
    [InlineData("transform:initial")]
    [InlineData("filter:initial")]
    [InlineData("clip-path:initial")]
    public void Css_wide_keyword_on_a_paint_property_is_not_unsupported(string inlineStyle)
        => AssertNoUnsupportedFor(inlineStyle);

    [Fact]
    public void A_real_gradient_still_renders_after_the_css_wide_guard()
    {
        // Guard the fix against over-broadly swallowing real values.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>"
            + "<div style=\"background:linear-gradient(90deg,#f00,#00f);width:80px;height:24px\">x</div>"
            + "</body></html>", new HtmlPdfOptions { PrintBackgrounds = true });
        var pdf = System.Text.Encoding.Latin1.GetString(result.Pdf);
        // A native axial shading paints as `/Sh<n> sh` (PdfPage.PaintShadingInRect) with a matching
        // `/Shading` resource dictionary — assert BOTH so the guard didn't over-broadly swallow the
        // gradient. (A bare `Contains("sh")` would false-pass on any unrelated "sh" substring.)
        Assert.Matches(@"/Sh\d+ sh\b", pdf);
        Assert.Contains("/Shading", pdf);
    }
}
