// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using System.Threading.Tasks;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// End-to-end tests for the Phase 5 layout→PDF "Hello World" wiring: drive
/// <see cref="HtmlPdf.Convert(string, HtmlPdfOptions?)"/> through the real
/// HTML → cascade → box tree → layout → paint → PDF-bytes pipeline and assert
/// the produced PDF is valid, deterministic, and actually paints the box's
/// <c>background-color</c> + <c>border-*</c> edges. (Page content is emitted
/// uncompressed, so the operator bytes are directly inspectable.)
/// </summary>
public sealed class HtmlPdfConvertTests
{
    // #3366cc → rgb(51, 102, 204) → exactly (0.2, 0.4, 0.8) in PDF unit RGB.
    private const string BackgroundHtml =
        "<!DOCTYPE html><html><body>" +
        "<div style=\"width:100px;height:60px;background-color:#3366cc\"></div>" +
        "</body></html>";

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    [Fact]
    public void Convert_produces_a_well_formed_pdf()
    {
        var bytes = HtmlPdf.Convert(BackgroundHtml);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        var text = Latin1(bytes);
        Assert.StartsWith("%PDF-", text);
        Assert.Contains("%%EOF", text);
    }

    [Fact]
    public void Convert_paints_the_background_color()
    {
        var text = Latin1(HtmlPdf.Convert(BackgroundHtml));

        // The fill color + a filled rectangle for the div's background.
        Assert.Contains("0.2 0.4 0.8 rg", text);
        Assert.Contains("re f", text);
    }

    [Fact]
    public void Convert_is_deterministic_across_runs()
    {
        var first = HtmlPdf.Convert(BackgroundHtml);
        var second = HtmlPdf.Convert(BackgroundHtml);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Convert_maps_page_size_to_the_media_box_in_points()
    {
        var text = Latin1(HtmlPdf.Convert(BackgroundHtml));

        // A4 default = 794 × 1123 CSS px → × 0.75 = 595.5 × 842.25 pt.
        Assert.Contains("/MediaBox", text);
        Assert.Contains("595.5", text);
        Assert.Contains("842.25", text);
    }

    [Fact]
    public async Task ConvertAsync_matches_the_sync_overload()
    {
        var sync = HtmlPdf.Convert(BackgroundHtml);
        var async = await HtmlPdf.ConvertAsync(BackgroundHtml);

        Assert.Equal(sync, async);
    }

    [Fact]
    public void ConvertDetailed_reports_a_single_page_for_short_content()
    {
        var result = HtmlPdf.ConvertDetailed(BackgroundHtml);

        Assert.Equal(1, result.PageCount);
        Assert.True(result.Pdf.Length > 0);
    }

    [Fact]
    public void Content_taller_than_one_page_emits_the_overflow_diagnostic()
    {
        // Ten 300px-tall blocks (3000px) far exceed an A4 content box (~931px),
        // so layout returns a continuation the single-page cycle can't yet emit.
        var sb = new StringBuilder("<!DOCTYPE html><html><body>");
        for (var i = 0; i < 10; i++)
            sb.Append("<div style=\"height:300px;background-color:#abcdef\"></div>");
        sb.Append("</body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString());

        Assert.Equal(1, result.PageCount);
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
    }

    [Fact]
    public void Plain_text_only_document_still_produces_a_valid_pdf()
    {
        // No background / border to paint, and text painting is not yet wired —
        // the pipeline must still emit a valid (effectively blank) page, not throw.
        var bytes = HtmlPdf.Convert("<!DOCTYPE html><html><body><p>Hello world</p></body></html>");

        Assert.StartsWith("%PDF-", Latin1(bytes));
        Assert.Contains("%%EOF", Latin1(bytes));
    }
}
