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
        var zero = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { margin: 0 }</style></head>" + body + "</html>")));
        var dflt = FirstRect(Latin1(HtmlPdf.Convert("<!DOCTYPE html><html>" + body + "</html>")));

        Assert.True(zero.W > dflt.W, $"@page margin:0 content ({zero.W}pt) should be wider than default ({dflt.W}pt)");
        Assert.Equal(144.0, zero.W - dflt.W, 1);   // 2 × 96px default margins removed = 144pt
    }

    // A4 is 794 × 1123 px → 595.5 × 842.25 pt; NetPdf applies no UA body margin, so a full-width
    // 50px (= 37.5pt) block's painted rect has exact, body-margin-free coordinates. These pin the
    // x/y offsets + height (not just the width delta) for @page margins (review P3).

    [Fact]
    public void At_page_margin_zero_positions_the_full_width_rect_at_the_page_origin()
    {
        var r = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { margin: 0 }</style></head>" +
            "<body><div style=\"height:50px;background-color:#3366cc\"></div></body></html>")));
        Assert.Equal(0.0, r.X, 1);        // left margin 0
        Assert.Equal(595.5, r.W, 1);      // full A4 content width (794px)
        Assert.Equal(37.5, r.H, 1);       // 50px block height — independent of @page margins
        Assert.Equal(804.75, r.Y, 1);     // 842.25 (page top) − 37.5 (height); top margin 0
    }

    [Fact]
    public void At_page_mixed_longhand_margins_offset_the_rect()
    {
        // top 40px, right 0, bottom 0, left 80px.
        var r = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { margin: 40px 0 0 80px }</style></head>" +
            "<body><div style=\"height:50px;background-color:#3366cc\"></div></body></html>")));
        Assert.Equal(60.0, r.X, 1);       // left 80px → 60pt
        Assert.Equal(535.5, r.W, 1);      // (794 − 80 − 0)px → 535.5pt
        Assert.Equal(37.5, r.H, 1);
        Assert.Equal(774.75, r.Y, 1);     // 842.25 − 30 (top 40px) − 37.5
    }

    [Fact]
    public void At_page_partial_margin_merges_per_side_with_option_margins()
    {
        // Only margin-left is set by @page → it overrides the left; top/right/bottom keep the
        // default 96px option margins.
        var r = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { margin-left: 0 }</style></head>" +
            "<body><div style=\"height:50px;background-color:#3366cc\"></div></body></html>")));
        Assert.Equal(0.0, r.X, 1);        // left overridden to 0
        Assert.Equal(523.5, r.W, 1);      // (794 − 0 left − 96 right)px → 523.5pt
        Assert.Equal(732.75, r.Y, 1);     // 842.25 − 72 (default top 96px) − 37.5
    }

    /// <summary>The (x, y, width, height) operands of the first <c>… re f</c> rectangle-fill op.</summary>
    private static (double X, double Y, double W, double H) FirstRect(string pdf)
    {
        var idx = pdf.IndexOf(" re", StringComparison.Ordinal);
        Assert.True(idx > 0, "expected a rectangle-fill operator in the content stream");
        var nums = pdf[..idx].TrimEnd().Split(' ');   // … <x> <y> <w> <h>
        return (
            double.Parse(nums[^4], CultureInfo.InvariantCulture),
            double.Parse(nums[^3], CultureInfo.InvariantCulture),
            double.Parse(nums[^2], CultureInfo.InvariantCulture),
            double.Parse(nums[^1], CultureInfo.InvariantCulture));
    }

    [Fact]
    public void At_page_size_keyword_sets_the_media_box()
    {
        // @page { size: A5 } → MediaBox = A5 (148 × 210mm → 419.5 × 595.3pt).
        var mb = MediaBox(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { size: A5 }</style></head><body></body></html>")));
        Assert.Equal(419.5, mb.W, 1);
        Assert.Equal(595.3, mb.H, 1);
    }

    [Fact]
    public void At_page_size_landscape_swaps_the_media_box_dimensions()
    {
        var mb = MediaBox(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { size: A4 landscape }</style></head><body></body></html>")));
        Assert.True(mb.W > mb.H, $"expected landscape (W > H); got {mb.W} × {mb.H}");
        Assert.Equal(841.9, mb.W, 1);   // A4's 297mm dimension becomes the width
    }

    [Fact]
    public void At_page_size_is_ignored_when_PreferCssPageSize_is_false()
    {
        var mb = MediaBox(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { size: A5 }</style></head><body></body></html>",
            new HtmlPdfOptions { PreferCssPageSize = false })));
        Assert.Equal(595.5, mb.W, 1);   // the A4 option default wins; A5 (419.5) is ignored
    }

    [Fact]
    public void At_page_size_with_percentage_margins_resolves_them_against_the_css_page_size()
    {
        // @page { size: A5; margin: 10% } — the page becomes A5 AND the 10% margins resolve
        // against A5 (the resolved page box), not the configured A4 default (review P2).
        //   A5 = 559.4 × 793.7px → MediaBox 419.5 × 595.3pt.
        //   left margin = 10% × 559.4px = 55.9px → 41.95pt; content width = 559.4 − 2×55.9
        //   = 447.5px → 335.6pt. Resolving % against A4's 793.7px width would instead give a
        //   59.5pt left / 300.5pt width — the bug this guards against.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { size: A5; margin: 10% }</style></head>" +
            "<body><div style=\"height:50px;background-color:#3366cc\"></div></body></html>"));

        var mb = MediaBox(pdf);
        Assert.Equal(419.5, mb.W, 1);   // A5 width
        Assert.Equal(595.3, mb.H, 1);   // A5 height

        var r = FirstRect(pdf);
        Assert.Equal(335.6, r.W, 1);    // content width = A5 − 10% margins (NOT 300.5 from A4)
        Assert.Equal(42.0, r.X, 1);     // left margin = 10% × A5 width = 41.95pt (NOT 59.5)
    }

    /// <summary>The (width, height) of the page's <c>/MediaBox [0 0 W H]</c>, in pt.</summary>
    private static (double W, double H) MediaBox(string pdf)
    {
        var i = pdf.IndexOf("/MediaBox", StringComparison.Ordinal);
        Assert.True(i >= 0, "MediaBox not found");
        var open = pdf.IndexOf('[', i);
        var close = pdf.IndexOf(']', open);
        var nums = pdf[(open + 1)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries);  // 0 0 W H
        return (double.Parse(nums[2], CultureInfo.InvariantCulture),
            double.Parse(nums[3], CultureInfo.InvariantCulture));
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

    // ---- Page margin boxes (Task 21 cycle 3) ----

    [Fact]
    public void Page_margin_box_paints_its_literal_content_in_the_bottom_margin()
    {
        // @bottom-center { content: "AB" } → a footer painted (BT…Tj…ET) in the bottom page
        // margin. SyntheticFont carries 'A'/'B', so the glyphs actually render. The body is empty,
        // so the ONLY text object is the footer — its Td y lands in the bottom-margin band.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content: \"AB\" } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);

        Assert.Contains("BT", pdf);           // a text object was painted
        Assert.Contains(" Tj", pdf);          // glyphs shown
        Assert.Contains("/FontFile2", pdf);   // the font was subset + embedded
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PaintTextFontUnresolved001);

        // The footer sits in the bottom margin: with the default 96px (= 72pt) margins on A4
        // (842.25pt tall), its baseline is within the bottom band (small PDF-y), NOT up in the
        // content area (y > 700pt) where body text would be.
        var td = FirstTd(pdf);
        Assert.InRange(td.Y, 0.0, 72.0);
    }

    [Fact]
    public void Page_margin_box_with_unsupported_content_function_is_skipped_with_a_diagnostic()
    {
        // counter(page) generated content is a later cycle — it must emit a diagnostic and paint
        // nothing (not crash, not silently drop). The body is empty, so no text at all is painted.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content: counter(page) } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);

        Assert.StartsWith("%PDF-", pdf);
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssContentFunctionUnsupported001);
        Assert.DoesNotContain("BT", pdf);   // nothing painted
    }

    [Fact]
    public void Body_text_and_a_margin_box_share_one_embedded_font()
    {
        // Body "A" + a footer "B" both resolve to SyntheticFont. They now paint through ONE
        // TextPainter pass, so the program is subset + embedded ONCE — not once per pass
        // (post-PR-#132 review P3). Both glyphs present → neither run was dropped.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content: \"B\" } }</style></head>" +
            "<body><p>A</p></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);

        Assert.Equal(1, pdf.Split("/FontFile2").Length - 1);   // exactly ONE embedded font program
        Assert.Contains("BT", pdf);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PaintTextFontUnresolved001);
    }

    [Fact]
    public void Page_margin_box_inside_at_media_print_is_painted()
    {
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@media print { @page { @top-center { content: \"AB\" } } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.Contains("BT", Latin1(result.Pdf));   // the print-media branch matches → painted
    }

    [Fact]
    public void Page_margin_box_in_a_screen_media_sheet_is_ignored_in_print()
    {
        // A media="screen" sheet never contributes to the print render → no footer text.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style media=\"screen\">@page { @bottom-center { content: \"AB\" } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.DoesNotContain("BT", Latin1(result.Pdf));
    }

    [Fact]
    public void Page_margin_box_content_none_is_suppressed_without_a_diagnostic()
    {
        // content: none → "no box", NOT unsupported content: no warning, no text (review P2).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content: none } }</style></head>" +
            "<body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.DoesNotContain("BT", Latin1(result.Pdf));
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssContentFunctionUnsupported001);
    }

    [Fact]
    public void Page_margin_box_attr_content_resolves_against_the_host_element()
    {
        // attr() reads the box tree's host element (the document root); SyntheticFont covers 'A'/'B',
        // so attr(data-title)="AB" actually renders glyphs.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html data-title=\"AB\"><head>" +
            "<style>@page { @top-center { content: attr(data-title) } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.Contains("BT", Latin1(result.Pdf));
    }

    [Fact]
    public void Page_margin_box_unsupported_content_diagnostic_is_length_capped()
    {
        // The raw author value is sanitized (control chars stripped, length capped at 120 + a
        // U+2026 marker) before landing in a host-visible diagnostic (review P2 —
        // DiagnosticTextSanitizer). A 300-char unsupported value must not leak verbatim.
        var longArg = new string('A', 300);
        var result = HtmlPdf.ConvertDetailed(
            $"<!DOCTYPE html><html><head><style>@page {{ @bottom-center {{ content: counter({longArg}) }} }}</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        var warning = Assert.Single(
            result.Warnings, d => d.Code == DiagnosticCodes.CssContentFunctionUnsupported001);
        Assert.Contains("…", warning.Message);        // truncation marker → the value was capped
        Assert.DoesNotContain(longArg, warning.Message);   // the full 300-char value did not leak
    }

    // ---- Per-box style (Task 21 cycle 4) ----

    [Fact]
    public void Page_margin_box_honors_declared_color()
    {
        // @bottom-center { color: #ff0000 } → the footer glyphs paint with a red fill.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content: \"AB\"; color: #ff0000 } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("1 0 0 rg", pdf);   // rgb(255,0,0) text fill
    }

    [Fact]
    public void Page_margin_box_honors_declared_font_size()
    {
        // font-size: 24px → 18pt in the Tf operator (× 0.75), vs the default 16px → 12pt.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content: \"AB\"; font-size: 24px } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(18.0, FirstTf(pdf), 1);
    }

    [Fact]
    public void Page_margin_box_declared_text_align_overrides_the_name_default()
    {
        // @top-center defaults to centered; text-align: left pins the line to the band's left edge
        // (the left margin = 96px → 72pt on A4), distinctly left of the centered position.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"AB\"; text-align: left } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.InRange(FirstTd(pdf).X, 71.0, 73.0);   // ≈ 72pt left content edge, not centered
    }

    [Fact]
    public void Page_margin_box_color_important_wins_across_page_rules()
    {
        // color: #ff0000 !important in one @page beats a later normal color: #0000ff in another
        // (per-property cascade across @page occurrences — post-PR-#133 review P2).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>" +
            "@page { @bottom-center { content: \"AB\"; color: #ff0000 !important } }" +
            "@page { @bottom-center { color: #0000ff } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("1 0 0 rg", pdf);        // red (the !important) won
        Assert.DoesNotContain("0 0 1 rg", pdf);  // not the later normal blue
    }

    [Fact]
    public void Page_margin_box_ignores_unsupported_padding_declaration()
    {
        // padding-left would shift the text if materialized (the painter reads padding from the
        // style); the cycle-4 whitelist excludes it, so the line stays at the same X (review P2).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var withPad = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"AB\"; padding-left: 50px } }</style>" +
            "</head><body></body></html>", opts)));
        var without = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"AB\" } }</style>" +
            "</head><body></body></html>", opts)));
        Assert.Equal(without.X, withPad.X, 3);   // padding-left was ignored → no horizontal shift
    }

    [Fact]
    public void Page_margin_box_inherits_color_from_the_page_context()
    {
        // Cycle 5: a margin box inherits from the @page context — @page { color: red } tints the
        // footer even though the box declares no color of its own.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { color: red; @bottom-center { content: \"AB\" } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("1 0 0 rg", pdf);   // inherited red from @page
    }

    [Fact]
    public void Page_margin_box_own_color_overrides_inherited_page_context()
    {
        // The box's own declaration wins over the inherited @page value.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { color: red; @bottom-center { content: \"AB\"; color: #0000ff } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("0 0 1 rg", pdf);        // the box's own blue
        Assert.DoesNotContain("1 0 0 rg", pdf);  // not the inherited red
    }

    [Fact]
    public void Page_margin_box_inherits_color_from_the_document_root()
    {
        // The chain reaches the document root: html { color } flows root → page context → margin box.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>html { color: #00ff00 } @page { @bottom-center { content: \"AB\" } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("0 1 0 rg", pdf);   // inherited lime from the root element
    }

    [Fact]
    public void Page_margin_box_invalid_style_value_is_surfaced()
    {
        // color: bogus is invalid → the box still paints (default color), but the invalid value is
        // surfaced via the CSS diagnostic path rather than silently swallowed (review P3).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content: \"AB\"; color: bogus } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
    }

    // ---- post-PR-#134 review ----

    [Fact]
    public void Page_margin_box_without_declared_text_align_keeps_name_derived_centering()
    {
        // REGRESSION (review thread 1): @top-center with no text-align must stay CENTERED — the
        // page/root's UA-default text-align:start must NOT be inherited as an override. Body empty
        // → the footer is the only text. Centered in the top band (centre ≈ 290pt) sits well right
        // of the 72pt left content edge that a spuriously-inherited start alignment would produce.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"AB\" } }</style></head>" +
            "<body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.True(FirstTd(pdf).X > 150, $"expected centered (~290pt), got {FirstTd(pdf).X}pt — start was wrongly inherited");
    }

    [Fact]
    public void Page_margin_box_page_text_align_does_not_override_name_derived()
    {
        // @page text-align:left does NOT override @top-center's name-derived centering — only a
        // text-align declared ON THE BOX does (review P3). The footer stays centered.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { text-align: left; @top-center { content: \"AB\" } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.True(FirstTd(pdf).X > 150);   // still centered, not left-aligned by the @page rule
    }

    [Fact]
    public void Page_margin_box_text_align_initial_is_start()
    {
        // text-align: initial → start (the property's initial value), overriding @top-center's
        // name-derived centering → the line sits at the left content edge.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"AB\"; text-align: initial } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.InRange(FirstTd(pdf).X, 71.0, 73.0);   // start → 72pt left edge
    }

    [Fact]
    public void Page_margin_box_color_initial_resets_inherited_color_without_a_diagnostic()
    {
        // @page color red + box color:initial → resets to the initial (black), NOT the inherited
        // red, and emits NO invalid-value diagnostic (initial is a valid CSS-wide keyword, review P2).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { color: red; @bottom-center { content:\"AB\"; color: initial } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);
        Assert.Contains("BT", pdf);
        Assert.DoesNotContain("1 0 0 rg", pdf);   // reset to initial, not inherited red
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
    }

    [Fact]
    public void Page_margin_box_relative_font_size_resolves_against_the_inherited_font()
    {
        // Cycle 7 (was the cycle-5/6 deferral pin): font-size: 2em now resolves against the
        // inherited page-context font (16px default) → 32px → 24pt Tf, not the old 12pt fallback.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content:\"AB\"; font-size: 2em } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(24.0, FirstTf(pdf), 1);   // 2em × 16px = 32px → 24pt
    }

    [Fact]
    public void Page_margin_box_em_resolves_against_the_page_context_font_size()
    {
        // The CSS Page 3 chain: @page { font-size: 20px } sets the page-context font, and the box's
        // 1.5em resolves against THAT (not the 16px root) → 30px → 22.5pt Tf.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { font-size: 20px; @bottom-center " +
            "{ content:\"AB\"; font-size: 1.5em } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(22.5, FirstTf(pdf), 1);   // 1.5em × 20px = 30px → 22.5pt
    }

    [Fact]
    public void Page_margin_box_larger_keyword_resolves_against_the_inherited_font()
    {
        // larger = parent font-size × 1.2 (16px → 19.2px → 14.4pt).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; font-size: larger } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(14.4, FirstTf(pdf), 1);   // 16px × 1.2 = 19.2px → 14.4pt
    }

    [Fact]
    public void Page_margin_box_rem_font_size_stays_deferred_and_falls_back()
    {
        // PIN: rem isn't parent-relative (it needs the root font-size threaded through) → it stays
        // deferred and falls back to the 16px reader default → 12pt Tf. Documents the remaining gap.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content:\"AB\"; font-size: 2rem } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(12.0, FirstTf(pdf), 1);   // 16px default × 0.75 (2rem not resolved)
    }

    [Fact]
    public void Page_margin_box_em_resolves_through_the_root_to_page_context_chain()
    {
        // Full CSS Page 3 chain: html { font-size:20px } → the page context inherits 20px (no @page
        // font-size) → the box's 1.5em resolves against THAT → 30px → 22.5pt.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>html { font-size: 20px } " +
            "@page { @bottom-center { content:\"AB\"; font-size: 1.5em } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(22.5, FirstTf(pdf), 1);   // 1.5em × (inherited 20px) = 30px → 22.5pt
    }

    [Fact]
    public void Page_margin_box_em_compounds_root_then_page_context_then_box()
    {
        // Each link resolves against the one above it: html 20px → @page 1.5em = 30px (root-relative)
        // → @bottom-center 2em = 60px (page-context-relative) → 45pt.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>html { font-size: 20px } " +
            "@page { font-size: 1.5em; @bottom-center { content:\"AB\"; font-size: 2em } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(45.0, FirstTf(pdf), 1);   // 2em × (1.5em × 20px = 30px) = 60px → 45pt
    }

    [Fact]
    public void Page_margin_box_font_shorthand_with_relative_size_resolves()
    {
        // PR135 shorthand expansion + PR136 deferred resolution compose: font: bold italic 1.5em
        // serif → font-size 1.5em → resolved against the 16px default → 24px → 18pt.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center " +
            "{ content:\"AB\"; font: bold italic 1.5em serif } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(18.0, FirstTf(pdf), 1);   // 1.5em × 16px = 24px → 18pt
    }

    // ---- background-color (Task 21 cycle 8) ----

    [Fact]
    public void Page_margin_box_background_color_fills_the_region_band()
    {
        // @bottom-center { background-color: red } → a red rectangle filling the FULL bottom-margin
        // band (behind the footer text), not just the text's bounding box.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content:\"AB\"; background-color: red } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("1 0 0 rg", pdf);     // red fill
        var r = FirstRect(pdf);
        Assert.Equal(0.0, r.Y, 0);            // the bottom band sits at the page-bottom (PDF y origin)
        Assert.Equal(72.0, r.H, 0);           // full bottom-margin height (96px → 72pt), not the ~12pt text line
    }

    [Fact]
    public void Page_margin_box_without_background_color_paints_no_band()
    {
        // No background-color → only the text paints; no rectangle-fill at all (empty body).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content:\"AB\" } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.DoesNotContain(" re", pdf);
    }

    [Fact]
    public void Page_margin_box_transparent_background_paints_no_band()
    {
        // background-color: transparent (alpha 0) paints nothing.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content:\"AB\"; background-color: transparent } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.DoesNotContain(" re", pdf);
    }

    [Fact]
    public void Page_margin_box_background_color_rgba_composites_partial_alpha()
    {
        // rgba(0,0,255,0.5) → a blue band composited via constant-alpha (/ca through an ExtGState),
        // mirroring body backgrounds — not painted fully opaque.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content:\"AB\"; background-color: rgba(0,0,255,0.5) } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("0 0 1 rg", pdf);     // blue fill
        Assert.Contains(" gs", pdf);          // an ExtGState (/ca) → partial-alpha compositing
    }

    [Fact]
    public void Page_margin_box_background_suppressed_when_print_backgrounds_disabled()
    {
        // PrintBackgrounds=false suppresses the margin-box band, exactly like body backgrounds
        // (post-PR-#137 review P1).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content:\"AB\"; background-color: red } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver(), PrintBackgrounds = false }));
        Assert.DoesNotContain("1 0 0 rg", pdf);   // no red band
        Assert.DoesNotContain(" re", pdf);
    }

    [Fact]
    public void Page_margin_box_empty_content_still_paints_the_background_band()
    {
        // content:"" generates the box (CSS Page 3 §6.1 — content is not none/normal) → the band
        // paints even with no text to lay out (post-PR-#137 review P2).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content:\"\"; background-color: red } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("1 0 0 rg", pdf);   // red band painted
        Assert.Contains(" re", pdf);
        Assert.DoesNotContain(" Tj", pdf);  // …with no text glyphs shown
    }

    [Fact]
    public void Page_margin_box_background_color_inherit_takes_the_page_context()
    {
        // @page { background-color: red } + box `background-color: inherit` → the box's band inherits
        // red, even though background-color is non-inherited by default (post-PR-#137 review P2).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { background-color: red; @bottom-center " +
            "{ content:\"AB\"; background-color: inherit } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("1 0 0 rg", pdf);   // inherited red band
    }

    [Fact]
    public void Page_margin_box_background_color_currentcolor_uses_the_box_color()
    {
        // background-color: currentcolor resolves against the box's own color (post-PR-#137 review P3).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center " +
            "{ content:\"AB\"; color: #0000ff; background-color: currentcolor } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        // The band (the first `re`, painted before the text) is filled with the box's blue currentColor.
        var beforeRect = pdf[..pdf.IndexOf(" re", StringComparison.Ordinal)];
        Assert.Contains("0 0 1 rg", beforeRect);
    }

    [Fact]
    public void Page_margin_box_font_shorthand_sets_the_size(/* Task 21 cycle 6 */)
    {
        // The `font` shorthand is expanded into longhands for margin-box bodies (AngleSharp never
        // sees them). `font: italic 24px serif` → font-size 24px → 18pt Tf.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content: \"AB\"; font: italic 24px serif } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("BT", pdf);
        Assert.Equal(18.0, FirstTf(pdf), 1);   // 24px × 0.75 — the shorthand's size was applied
    }

    [Fact]
    public void Page_margin_box_font_shorthand_leading_tokens_still_apply_the_size()
    {
        // Leading <font-style> + <font-weight> tokens are parsed; the size is still applied (proves
        // the leading-token scan doesn't swallow the size). font: bold italic 24px serif → 18pt Tf.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content: \"AB\"; font: bold italic 24px serif } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(18.0, FirstTf(pdf), 1);
    }

    [Fact]
    public void Page_margin_box_font_shorthand_important_beats_a_later_normal_longhand()
    {
        // The shorthand's importance propagates to each expanded longhand: font: 24px serif
        // !important sets font-size: 24px !important, which a later normal font-size: 10px can't
        // override (review #5 — !important interaction). → 18pt Tf, not 7.5pt.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center " +
            "{ content: \"AB\"; font: 24px serif !important; font-size: 10px } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(18.0, FirstTf(pdf), 1);
    }

    [Fact]
    public void Page_margin_box_font_shorthand_css_wide_initial_resets_the_size()
    {
        // font: initial maps every longhand to `initial` → font-size resets to medium (16px → 12pt),
        // not the 24px of any inherited/declared value (review #5 — CSS-wide keyword).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content: \"AB\"; font: initial } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(12.0, FirstTf(pdf), 1);   // medium = 16px → 12pt
    }

    [Theory]
    [InlineData("caption")]                  // a valid-but-unsupported system-font keyword
    [InlineData("italic 12bananas serif")]   // a malformed shorthand
    public void Page_margin_box_unsupported_font_shorthand_is_surfaced(string fontValue)
    {
        // A `font` shorthand we can't apply is reported (review #3) rather than silently dropped: the
        // box still paints (default font), and the value is surfaced via the CSS diagnostic path.
        var result = HtmlPdf.ConvertDetailed(
            $"<!DOCTYPE html><html><head><style>@page {{ @bottom-center {{ content: \"AB\"; font: {fontValue} }} }}</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
    }

    [Fact]
    public void Page_margin_box_unsupported_font_shorthand_diagnostic_is_length_capped()
    {
        // The raw `font` value is sanitized (length-capped at 120 + a U+2026 marker) before landing
        // in a host-visible diagnostic — a 300-char value must not leak verbatim (review #3).
        var longValue = new string('A', 300);
        var result = HtmlPdf.ConvertDetailed(
            $"<!DOCTYPE html><html><head><style>@page {{ @bottom-center {{ content: \"AB\"; font: {longValue} }} }}</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        var warning = Assert.Single(
            result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
        Assert.Contains("…", warning.Message);            // truncation marker → the value was capped
        Assert.DoesNotContain(longValue, warning.Message); // the full 300-char value did not leak
    }

    /// <summary>The font size (pt) of the first <c>… &lt;size&gt; Tf</c> operator.</summary>
    private static double FirstTf(string pdf)
    {
        var idx = pdf.IndexOf(" Tf", StringComparison.Ordinal);
        Assert.True(idx > 0, "expected a font-select (Tf) operator in the content stream");
        var nums = pdf[..idx].TrimEnd().Split(' ');   // … /Fn <size>
        return double.Parse(nums[^1], CultureInfo.InvariantCulture);
    }

    /// <summary>The (x, y) operands of the first <c>… Td</c> text-position operator, in pt.</summary>
    private static (double X, double Y) FirstTd(string pdf)
    {
        var idx = pdf.IndexOf(" Td", StringComparison.Ordinal);
        Assert.True(idx > 0, "expected a text-position (Td) operator in the content stream");
        var nums = pdf[..idx].TrimEnd().Split(' ');   // … <x> <y>
        return (double.Parse(nums[^2], CultureInfo.InvariantCulture),
            double.Parse(nums[^1], CultureInfo.InvariantCulture));
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
