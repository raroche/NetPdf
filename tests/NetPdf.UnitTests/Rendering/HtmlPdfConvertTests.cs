// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetPdf;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// End-to-end tests for the Phase 5 layout→PDF "Hello World" wiring: drive
/// <see cref="HtmlPdf.Convert(string, HtmlPdfOptions?)"/> through the real
/// HTML → cascade → box tree → layout → paint → PDF-bytes pipeline and assert
/// the produced PDF is valid, deterministic, and actually paints the box's
/// <c>background-color</c> fill. (Page content is emitted uncompressed, so the
/// operator bytes are directly inspectable.)
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
    public void Convert_paints_a_solid_border_edge()
    {
        const string html =
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:80px;height:40px;" +
            "border-top-width:5px;border-top-style:solid;border-top-color:#ff0000\"></div>" +
            "</body></html>";

        var text = Latin1(HtmlPdf.Convert(html));

        // #ff0000 → rgb(1, 0, 0) + a filled rectangle for the top border edge.
        Assert.Contains("1 0 0 rg", text);
        Assert.Contains("re f", text);
    }

    [Fact]
    public void Non_solid_border_style_emits_the_approximation_diagnostic()
    {
        const string html =
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:80px;height:40px;" +
            "border-top-width:3px;border-top-style:dashed;border-top-color:#000000\"></div>" +
            "</body></html>";

        var result = HtmlPdf.ConvertDetailed(html);

        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.PaintBorderStyleApproximated001);
    }

    [Fact]
    public void Convert_paints_border_from_the_border_shorthand()
    {
        const string html =
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:80px;height:40px;border:5px solid red\"></div>" +
            "</body></html>";

        var text = Latin1(HtmlPdf.Convert(html));

        Assert.Contains("1 0 0 rg", text);   // red, expanded from the `border` shorthand
        Assert.Contains("re f", text);
    }

    [Fact]
    public void Convert_paints_border_from_the_per_side_shorthand()
    {
        const string html =
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:80px;height:40px;border-top:5px solid red\"></div>" +
            "</body></html>";

        var text = Latin1(HtmlPdf.Convert(html));

        Assert.Contains("1 0 0 rg", text);
        Assert.Contains("re f", text);
    }

    [Fact]
    public void Convert_paints_the_initial_medium_width_when_only_style_and_color_are_set()
    {
        // No width declared → border-top-width is its initial `medium` (3px). Proves the
        // medium-default path paints — invoices commonly rely on it (PR #119 review P2).
        const string html =
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:80px;height:40px;border-top-style:solid;border-top-color:red\"></div>" +
            "</body></html>";

        var text = Latin1(HtmlPdf.Convert(html));

        Assert.Contains("1 0 0 rg", text);
        Assert.Contains("re f", text);
    }

    [Fact]
    public void Partial_alpha_border_color_is_composited_via_constant_alpha()
    {
        const string html =
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:80px;height:40px;border-top-width:5px;border-top-style:solid;" +
            "border-top-color:rgba(255,0,0,0.5)\"></div>" +
            "</body></html>";

        var result = HtmlPdf.ConvertDetailed(html);
        var pdf = Latin1(result.Pdf);

        // No longer an approximation — the alpha is carried by an ExtGState /ca.
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PaintBorderAlphaApproximated001);
        Assert.Contains("1 0 0 rg", pdf);          // the color (rgb is unchanged; alpha is separate)
        // Exact /ca value with a trailing delimiter: rgba(...,0.5) quantizes to the 8-bit color
        // model as round(0.5*255)=128, so /ca is 128/255 = 0.501961, NOT 0.5. A bare "/ca 0.5"
        // is a prefix of "/ca 0.501961" and would mask the real value (review P3).
        Assert.Contains("/ca 0.501961 ", pdf);     // the constant-alpha ExtGState (exact value)
        Assert.Contains(" gs", pdf);               // selected via the gs operator
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
    public void PrintBackgrounds_false_paints_no_background()
    {
        var options = new HtmlPdfOptions { PrintBackgrounds = false };
        var text = Latin1(HtmlPdf.Convert(BackgroundHtml, options));

        // The background fill is suppressed — no filled-rectangle operator at all.
        Assert.DoesNotContain("re f", text);
        // Sanity: with backgrounds on (the default), the same document DOES paint one.
        Assert.Contains("re f", Latin1(HtmlPdf.Convert(BackgroundHtml)));
    }

    [Fact]
    public void At_page_margin_overrides_the_content_area_width()
    {
        // A full-width (auto) block paints a background rect spanning the content width. With
        // @page { margin: 0 } the content area is the full page; with the default 96px margins
        // it is narrower. The rect-WIDTH difference equals the removed horizontal margins
        // (2 × 96px = 144pt) — independent of any UA body margin (constant in both renders), so
        // this proves @page { margin } reaches the page geometry without depending on exact coords.
        const string body = "<body><div style=\"height:50px;background-color:#3366cc\"></div></body>";
        var zero = FirstRectWidthPt(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { margin: 0 }</style></head>" + body + "</html>")));
        var dflt = FirstRectWidthPt(Latin1(HtmlPdf.Convert("<!DOCTYPE html><html>" + body + "</html>")));

        Assert.True(zero > dflt, $"@page margin:0 content ({zero}pt) should be wider than default ({dflt}pt)");
        Assert.Equal(144.0, zero - dflt, 1);   // 2 × 96px default margins removed = 144pt
    }

    /// <summary>Width operand (3rd of x/y/w/h) of the first <c>… re f</c> rectangle-fill op.</summary>
    private static double FirstRectWidthPt(string pdf)
    {
        var idx = pdf.IndexOf(" re", StringComparison.Ordinal);
        Assert.True(idx > 0, "expected a rectangle-fill operator in the content stream");
        var nums = pdf[..idx].TrimEnd().Split(' ');   // … <x> <y> <w> <h>
        return double.Parse(nums[^2], CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Fragment_outside_the_content_box_emits_the_overflow_diagnostic()
    {
        // An absolutely-positioned box with a negative offset lays out to completion
        // (AllDone — no page break) but sits outside the content box, so it paints into
        // the margin / off-page and would be clipped. The post-layout fragment-bounds
        // check must surface it even though there's no continuation (PR #118 review P2).
        const string html =
            "<!DOCTYPE html><html><body>" +
            "<div style=\"position:absolute;left:-40px;top:10px;width:30px;height:30px;" +
            "background-color:#3366cc\"></div>" +
            "</body></html>";

        var result = HtmlPdf.ConvertDetailed(html);

        Assert.Equal(1, result.PageCount);
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
    }

    [Fact]
    public void Partial_alpha_background_is_composited_via_constant_alpha()
    {
        const string html =
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:60px;height:40px;background-color:rgba(255,0,0,0.5)\"></div>" +
            "</body></html>";

        var result = HtmlPdf.ConvertDetailed(html);
        var pdf = Latin1(result.Pdf);

        // No longer an approximation — the alpha is carried by an ExtGState /ca.
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PaintBackgroundAlphaApproximated001);
        Assert.Contains("1 0 0 rg", pdf);          // the color (rgb is unchanged; alpha is separate)
        // Exact /ca value with a trailing delimiter: rgba(...,0.5) quantizes to the 8-bit color
        // model as round(0.5*255)=128, so /ca is 128/255 = 0.501961, NOT 0.5. A bare "/ca 0.5"
        // is a prefix of "/ca 0.501961" and would mask the real value (review P3).
        Assert.Contains("/ca 0.501961 ", pdf);     // the constant-alpha ExtGState (exact value)
        Assert.Contains(" gs", pdf);               // selected via the gs operator
    }

    [Fact]
    public void Plain_text_only_document_still_produces_a_valid_pdf()
    {
        // Text now paints via the default SystemFontResolver (cycle 5a-2-ii). The default
        // path is robust: whether or not a system font resolves + subsets, the pipeline must
        // emit a valid PDF and never throw. (Determinism-for-text on the default path waits on
        // a bundled fallback font; the fixed-font tests below cover the deterministic path.)
        var bytes = HtmlPdf.Convert("<!DOCTYPE html><html><body><p>Hello world</p></body></html>");

        Assert.StartsWith("%PDF-", Latin1(bytes));
        Assert.Contains("%%EOF", Latin1(bytes));
    }

    [Fact]
    public void Text_with_a_fixed_font_emits_real_glyph_operators_and_embeds_the_font()
    {
        var options = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        // SyntheticFont only carries glyphs for 'A' (U+0041) and 'B' (U+0042).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body><p>AB</p></body></html>", options);
        var pdf = Latin1(result.Pdf);

        // Real text-show operators (the content stream is uncompressed, so these are
        // directly inspectable): open a text object, select the font + size, position the
        // line origin, show the glyphs, close.
        Assert.Contains("BT", pdf);
        Assert.Contains(" Tf", pdf);
        Assert.Contains(" Td", pdf);
        Assert.Contains(" Tj", pdf);
        Assert.Contains("ET", pdf);

        // The font was subset + embedded as a composite Type0 / CIDFontType2 program.
        Assert.Contains("/Type0", pdf);
        Assert.Contains("/CIDFontType2", pdf);
        Assert.Contains("/FontFile2", pdf);

        // Every run's font resolved — no skipped-text diagnostic on the fixed-font path.
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PaintTextFontUnresolved001);
    }

    [Fact]
    public void Text_with_a_fixed_font_is_deterministic_across_runs()
    {
        var options = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        const string html = "<!DOCTYPE html><html><body><p>AB</p></body></html>";

        // The whole text path — shape → collect → subset → embed → emit — must be byte-stable
        // for stable input (CLAUDE.md #4); a fixed font removes the system-font dependency.
        var first = HtmlPdf.Convert(html, options);
        var second = HtmlPdf.Convert(html, options);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Unresolvable_font_skips_text_with_a_diagnostic_and_a_valid_pdf()
    {
        // The resolver resolves NOTHING. Text shaping happens during layout, so this surfaces
        // there — the pipeline must degrade to a valid PDF + PAINT-TEXT-FONT-UNRESOLVED-001 and
        // NEVER throw (post-PR-#127 review P1).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body><p>AB</p></body></html>",
            new HtmlPdfOptions { FontResolver = new NullResolver() });
        var pdf = Latin1(result.Pdf);

        Assert.StartsWith("%PDF-", pdf);
        Assert.Contains("%%EOF", pdf);
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.PaintTextFontUnresolved001);
        Assert.DoesNotContain("BT", pdf);   // no glyphs were painted
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })]              // garbage — not an sfnt
    [InlineData(new byte[] { 0x77, 0x4F, 0x46, 0x46, 0, 0, 0, 0 })]  // "wOFF…" — a WOFF wrapper
    public void Unsafe_or_wrapped_font_bytes_skip_text_with_a_diagnostic_and_a_valid_pdf(byte[] fontBytes)
    {
        // Resolved-but-rejected bytes (garbage / WOFF) throw the same recoverable
        // FontResolutionException as no-font, caught as the pipeline backstop (review P1).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body><p>AB</p></body></html>",
            new HtmlPdfOptions { FontResolver = new FixedBytesResolver(fontBytes) });

        Assert.StartsWith("%PDF-", Latin1(result.Pdf));
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.PaintTextFontUnresolved001);
    }

    [Fact]
    public void Async_font_resolver_does_not_throw_and_produces_a_valid_pdf()
    {
        // A non-synchronous resolver trips the synchronous-shaping guard (NotSupportedException),
        // already degraded at the inline-layout seam — the conversion must still produce a valid
        // PDF and not throw (review P1).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body><p>AB</p></body></html>",
            new HtmlPdfOptions { FontResolver = new NeverCompletesResolver() });

        Assert.StartsWith("%PDF-", Latin1(result.Pdf));
        Assert.Contains("%%EOF", Latin1(result.Pdf));
    }

    [Fact]
    public void Partial_alpha_text_is_composited_via_constant_alpha()
    {
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body><p style=\"color:rgba(255,0,0,0.5)\">AB</p></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);

        Assert.Contains("BT", pdf);                 // text IS painted (not silently dropped)
        Assert.Contains(" gs", pdf);                // ... behind a constant-alpha ExtGState
        Assert.Contains("/ca 0.501961 ", pdf);      // rgba(...,0.5) → 128/255 = 0.501961 (exact)
        Assert.Contains("1 0 0 rg", pdf);           // opaque fill color; the alpha is separate
    }

    [Fact]
    public void Transparent_text_paints_no_glyphs()
    {
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body><p style=\"color:transparent\">AB</p></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.StartsWith("%PDF-", Latin1(result.Pdf));
        Assert.DoesNotContain("BT", Latin1(result.Pdf));   // fully transparent → no text object
    }

    [Fact]
    public void Distinct_font_family_stacks_resolving_to_the_same_face_embed_one_font()
    {
        // Two different font-family stacks both fall back to the synthetic face. Because the
        // program identity is the resolved CONTENT (not the requested query), they share ONE
        // subset + embedded font — not one per stack (post-PR-#127 review P3).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            "<p style=\"font-family:Foo\">A</p><p style=\"font-family:Bar\">B</p>" +
            "</body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.Equal(1, Latin1(result.Pdf).Split("/FontFile2").Length - 1);   // ONE embedded program
    }

    /// <summary>A deterministic <see cref="IFontResolver"/> that resolves every query to the
    /// in-repo <see cref="SyntheticFont"/> (a minimal valid TTF with glyphs for 'A'/'B').
    /// Completes synchronously, as the synchronous layout shaping path requires.</summary>
    private sealed class SyntheticFontResolver : IFontResolver
    {
        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = SyntheticFont.Build(), Family = query.Family });
    }

    /// <summary>Resolves nothing — exercises the no-font-resolved degradation path.</summary>
    private sealed class NullResolver : IFontResolver
    {
        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new((FontFaceData?)null);
    }

    /// <summary>Returns fixed bytes for any query — for garbage / WOFF rejection paths.</summary>
    private sealed class FixedBytesResolver(byte[] bytes) : IFontResolver
    {
        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = bytes, Family = query.Family });
    }

    /// <summary>Never completes synchronously — trips the synchronous-shaping guard.</summary>
    private sealed class NeverCompletesResolver : IFontResolver
    {
        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new TaskCompletionSource<FontFaceData?>().Task);   // never set
    }
}
