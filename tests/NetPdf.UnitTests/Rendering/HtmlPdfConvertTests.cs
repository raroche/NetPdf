// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
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

    /// <summary>Every <c>… &lt;x&gt; &lt;y&gt; &lt;w&gt; &lt;h&gt; re</c> rectangle-fill in the stream, in
    /// emission order (pt). Each fill is <c>q … &lt;x&gt; &lt;y&gt; &lt;w&gt; &lt;h&gt; re f Q</c>, so the
    /// four operands are the last four space-separated tokens before each <c>" re"</c>.</summary>
    private static List<(double X, double Y, double W, double H)> AllRects(string pdf)
    {
        var rects = new List<(double, double, double, double)>();
        for (var i = pdf.IndexOf(" re", StringComparison.Ordinal); i > 0;
             i = pdf.IndexOf(" re", i + 3, StringComparison.Ordinal))
        {
            var nums = pdf[..i].TrimEnd().Split(' ');
            rects.Add((
                double.Parse(nums[^4], CultureInfo.InvariantCulture),
                double.Parse(nums[^3], CultureInfo.InvariantCulture),
                double.Parse(nums[^2], CultureInfo.InvariantCulture),
                double.Parse(nums[^1], CultureInfo.InvariantCulture)));
        }
        return rects;
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
    public void At_page_first_selector_size_overrides_the_bare_page()
    {
        // Task 21 selectors: @page :first overrides the bare @page on the single (first) page.
        // size A4 (bare) → A5 (:first) → MediaBox = A5 (148 × 210mm → 419.5 × 595.3pt).
        var mb = MediaBox(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { size: A4 } @page :first { size: A5 }</style>" +
            "</head><body></body></html>")));
        Assert.Equal(419.5, mb.W, 1);
        Assert.Equal(595.3, mb.H, 1);
    }

    [Fact]
    public void At_page_first_selector_margin_overrides_the_bare_page()
    {
        // @page { margin: 0 } @page :first { margin: 1in } → the single page gets the :first 1in
        // margin (96px → 72pt inset on each side), not the bare 0.
        var body = "<head></head><body><div style=\"width:50px;height:50px;background:#000\"></div></body>";
        var r = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { margin: 0 } @page :first { margin: 1in }</style></head>" + body + "</html>")));
        Assert.Equal(72.0, r.X, 1);   // left content edge inset by the :first 1in margin (not 0)
    }

    [Fact]
    public void At_page_first_selector_margin_box_content_paints_end_to_end()
    {
        // The :first margin box wins through to the PDF: bare @bottom-center "A" (1 glyph) is
        // overridden by :first "AB" (2 glyphs). The glyph count proves the :first box painted.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content: \"A\" } } " +
            "@page :first { @bottom-center { content: \"AB\" } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(2, TotalGlyphCount(pdf));   // "AB" (the :first box), not "A" (the bare box)
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
        // A non-page counter (counter(chapter)) is still a later cycle — it must emit a diagnostic
        // and paint nothing (not crash, not silently drop). The body is empty, so no text is painted.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content: counter(chapter) } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);

        Assert.StartsWith("%PDF-", pdf);
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssContentFunctionUnsupported001);
        Assert.DoesNotContain("BT", pdf);   // nothing painted
    }

    [Theory]
    [InlineData("counter(page)")]
    [InlineData("counter(pages)")]
    [InlineData("counter(page, decimal)")]
    [InlineData("counter(page, lower-roman)")]    // page 1 → "i" (Task 21 — counter styles)
    [InlineData("counter(page, upper-roman)")]    // → "I"
    [InlineData("counter(page, lower-alpha)")]    // → "a"
    [InlineData("counter(page, upper-latin)")]    // → "A"
    [InlineData("counter(page, decimal-leading-zero)")] // → "01"
    [InlineData("counter(pages, lower-roman)")]
    public void Page_margin_box_page_counter_content_is_painted(string content)
    {
        // counter(page)/counter(pages) now resolve (Task 21 cycle 9) with an optional <counter-style>
        // (Task 21 — roman/alpha/leading-zero, shared with list markers via CounterStyleFormatter) — the
        // page number is laid out + painted (a text run), with NO unsupported-content diagnostic.
        var result = HtmlPdf.ConvertDetailed(
            $"<!DOCTYPE html><html><head><style>@page {{ @bottom-center {{ content: {content} }} }}</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);

        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssContentFunctionUnsupported001);
        Assert.Contains("BT", pdf);   // the page number was painted
    }

    [Fact]
    public void Page_margin_box_upper_alpha_page_counter_paints_the_letter()
    {
        // counter(page, upper-alpha) on the single (first) page → "A" — the one numeral the synthetic
        // font has a glyph for, so the painted output is observable (1 glyph), proving the style resolved.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content: counter(page, upper-alpha) } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(1, TotalGlyphCount(pdf));   // page 1 → "A" → one glyph
    }

    [Theory]
    [InlineData("hebrew")]            // a predefined style we don't format
    [InlineData("cjk-ideographic")]
    [InlineData("not-a-style")]       // an undefined name
    public void Page_margin_box_unknown_counter_style_falls_back_to_decimal(string style)
    {
        // CSS Counter Styles §7.1.4: an unknown / unimplemented counter style falls back to `decimal` —
        // the page number must NEVER silently vanish (review P2). So counter(page, <style>) still paints
        // (the decimal page number), with NO unsupported-content diagnostic.
        var result = HtmlPdf.ConvertDetailed(
            $"<!DOCTYPE html><html><head><style>@page {{ @bottom-center {{ content: counter(page, {style}) }} }}</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssContentFunctionUnsupported001);
        Assert.Contains("BT", Latin1(result.Pdf));   // the decimal page number was painted, not dropped
    }

    [Fact]
    public void Page_margin_box_mixed_string_and_counters_paints_the_full_value()
    {
        // "Page " counter(page) " of " counter(pages) → "Page 1 of 1" (11 chars). The SyntheticFont is
        // A/B-only so the rendered glyphs aren't readable, but the glyph COUNT pins that the counters
        // resolved + concatenated to the right LENGTH end-to-end (the exact value is asserted at the
        // unit layer in CssContentListTests). No unsupported-content diagnostic.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center " +
            "{ content: \"Page \" counter(page) \" of \" counter(pages) } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);

        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssContentFunctionUnsupported001);
        Assert.Equal("Page 1 of 1".Length, TotalGlyphCount(pdf));   // 11 glyphs laid out + painted
    }

    /// <summary>Total glyph count across all <c>&lt;hex&gt; Tj</c> show operators (Identity-H 2-byte
    /// glyph ids → 4 hex digits each).</summary>
    private static int TotalGlyphCount(string pdf)
    {
        var total = 0;
        var idx = 0;
        while ((idx = pdf.IndexOf(" Tj", idx, StringComparison.Ordinal)) >= 0)
        {
            var close = pdf.LastIndexOf('>', idx);
            var open = close > 0 ? pdf.LastIndexOf('<', close) : -1;
            if (open >= 0 && close > open) total += (close - open - 1) / 4;
            idx += 3;
        }
        return total;
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
    public void Page_margin_box_declared_text_align_does_not_move_the_box_placement()
    {
        // §5.3.2.4: a margin box is placed by its NAME-DERIVED role (@top-center → centered),
        // independent of a declared text-align. `text-align: left` must NOT pull the centered box's
        // line to the band's ~72pt left edge (the pre-fix behavior) — the line stays centered, in the
        // same place as the box with no declared text-align (a shrink-to-fit box is content-sized, so
        // text-align has no room to act). [Was: the old full-band model where text-align positioned the
        // line within the whole band, so `left` pinned it to the 72pt edge.]
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var withLeftPdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"AB\"; text-align: left } }</style>" +
            "</head><body></body></html>", opts));
        var withoutAlignX = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"AB\" } }</style>" +
            "</head><body></body></html>", opts))).X;
        var withLeftX = FirstTd(withLeftPdf).X;
        var pageCenterX = MediaBox(withLeftPdf).W / 2.0;
        Assert.Equal(withoutAlignX, withLeftX, 1);                  // text-align: left didn't move the centered box
        Assert.True(withLeftX > 150, $"line must not be pinned to the ~72pt left edge: {withLeftX}pt");
        Assert.InRange(withLeftX, pageCenterX - 30, pageCenterX);   // centered (line starts just left of center)
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
    public void Page_margin_box_padding_left_insets_the_text()
    {
        // padding-left now shifts the text inward (padding cycle) — @top-left is start-aligned, so a
        // declared padding-left moves the line right by the padding amount (50px → 37.5pt).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var withPad = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content: \"AB\"; padding-left: 50px } }</style>" +
            "</head><body></body></html>", opts)));
        var without = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content: \"AB\" } }</style>" +
            "</head><body></body></html>", opts)));
        Assert.InRange(withPad.X - without.X, 36.0, 39.0);   // 50px padding-left → ~37.5pt shift
    }

    [Fact]
    public void Page_margin_box_padding_shorthand_insets_the_text()
    {
        // The `padding` 1-value box shorthand expands to all four longhands end-to-end — padding: 40px
        // sets padding-left = 40px, shifting the start-aligned @top-left line right by ~30pt.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var withPad = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content: \"AB\"; padding: 40px } }</style>" +
            "</head><body></body></html>", opts)));
        var without = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content: \"AB\" } }</style>" +
            "</head><body></body></html>", opts)));
        Assert.InRange(withPad.X - without.X, 28.0, 32.0);   // 40px → ~30pt shift
    }

    [Fact]
    public void Page_margin_box_border_left_width_insets_the_text()
    {
        // The cycle-11-deferred border content-inset now works: a border-left pushes the text right by
        // its used width (20px → 15pt), independently of (and in addition to) padding.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var withBorder = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content: \"AB\"; border-left: 20px solid red } }</style>" +
            "</head><body></body></html>", opts)));
        var without = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content: \"AB\" } }</style>" +
            "</head><body></body></html>", opts)));
        Assert.InRange(withBorder.X - without.X, 13.0, 17.0);   // 20px border-left → ~15pt shift
    }

    [Fact]
    public void Page_margin_box_padding_and_border_inset_add_up()
    {
        // The content-origin inset is border-width + padding per side (CSS box model): a 10px border-left
        // + 20px padding-left → a 30px (→ ~22.5pt) total shift.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var withBoth = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content: \"AB\"; border-left: 10px solid red; padding-left: 20px } }</style>" +
            "</head><body></body></html>", opts)));
        var without = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content: \"AB\" } }</style>" +
            "</head><body></body></html>", opts)));
        Assert.InRange(withBoth.X - without.X, 21.0, 24.0);   // (10 + 20)px → ~22.5pt shift
    }

    [Fact]
    public void Page_margin_box_oversized_padding_clamps_without_crashing()
    {
        // padding larger than the band clamps the content box to >= 0 — no negative-size / non-finite
        // coords; the box still emits valid text output.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"AB\"; padding: 9999px } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("BT", pdf);   // still produced a text run, no crash
    }

    [Fact]
    public void Page_margin_box_malformed_padding_is_surfaced()
    {
        // An un-expandable padding value (`10xyz` isn't a length) is kept as a raw marker and surfaced
        // via the CSS diagnostic path — not silently dropped.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content:\"AB\"; padding: 10xyz } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
    }

    [Theory]
    [InlineData("padding-left: 10%")]   // a percentage per-side longhand
    [InlineData("padding-top: 10%")]
    [InlineData("padding: 10%")]        // the shorthand (expands to four % longhands)
    [InlineData("padding-left: 1em")]   // a font-relative length (also can't resolve to px here)
    public void Page_margin_box_non_absolute_padding_is_surfaced(string decls)
    {
        // A percentage (resolves against the containing block) or a font-/viewport-relative padding
        // can't be resolved to used px in margin boxes yet (the §5.3 box sizing / font context is
        // deferred). It must be DIAGNOSED, not silently rendered as 0 (review P2 + Copilot).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"AB\"; " + decls + " } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
    }

    [Fact]
    public void Page_margin_box_percentage_padding_does_not_shift_the_text()
    {
        // The deferred % padding resolves to no inset (it's dropped, not mis-rendered) — the line stays
        // at the same X as a box with no padding.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var withPct = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"AB\"; padding-left: 10% } }</style>" +
            "</head><body></body></html>", opts)));
        var without = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"AB\" } }</style>" +
            "</head><body></body></html>", opts)));
        Assert.Equal(without.X, withPct.X, 1);   // % padding dropped → no horizontal shift
    }

    [Fact]
    public void Page_margin_box_padding_top_insets_the_text_downward()
    {
        // The VERTICAL inset path (riskier — content-height shrink + TextPainter top inset + PDF y-flip):
        // a top-aligned line is pushed DOWN by padding-top, which in PDF (y-up) is a SMALLER y. Guards
        // against a double-applied or missing top inset (review P2).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var withPad = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"AB\"; vertical-align: top; padding-top: 40px } }</style>" +
            "</head><body></body></html>", opts)));
        var without = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"AB\"; vertical-align: top } }</style>" +
            "</head><body></body></html>", opts)));
        Assert.InRange(without.Y - withPad.Y, 28.0, 32.0);   // 40px padding-top → ~30pt downward
    }

    [Fact]
    public void Page_margin_box_border_top_width_insets_the_text_downward()
    {
        // The cycle-12 border content-inset, VERTICAL axis: a top-aligned line is pushed down by the
        // border-top width (20px → ~15pt smaller y).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var withBorder = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"AB\"; vertical-align: top; border-top: 20px solid red } }</style>" +
            "</head><body></body></html>", opts)));
        var without = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"AB\"; vertical-align: top } }</style>" +
            "</head><body></body></html>", opts)));
        Assert.InRange(without.Y - withBorder.Y, 13.0, 17.0);   // 20px border-top → ~15pt downward
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
        // text-align: initial resolves to `start` (the property's initial value), NOT ignored. Proven
        // on a CORNER box, whose content area isn't shrink-to-fit (§5.3 varies only the edge boxes), so
        // its content alignment is observable: `initial`→start pins "AB" to the corner's left edge
        // (x≈0), distinct from the name-derived centering it would keep if `initial` were ignored.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var initialX = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left-corner { content: \"AB\"; text-align: initial } }</style>" +
            "</head><body></body></html>", opts))).X;
        var defaultX = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left-corner { content: \"AB\" } }</style>" +
            "</head><body></body></html>", opts))).X;
        Assert.True(initialX < defaultX - 10,
            $"initial→start should left-align in the corner (got {initialX}pt vs name-centered {defaultX}pt)");
        Assert.InRange(initialX, 0.0, 3.0);   // flush to the corner's left edge (x≈0)
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
    public void Page_margin_box_background_color_fills_the_band_height()
    {
        // @bottom-center { background-color: red } → a red rectangle spanning the bottom-margin band's
        // full HEIGHT (the §5.3 FIXED axis for a bottom box) behind the footer text. The WIDTH is the
        // §5.3 VARIABLE axis — content-sized (see the shrink-to-fit tests below), not asserted here.
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

    // ---- border (Task 21 border cycle) ----

    [Fact]
    public void Page_margin_box_border_strokes_the_region()
    {
        // @bottom-center { border: 2px solid red } → the box's border edges stroke around its region
        // (filled rects in red), reusing the body border painter.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content:\"AB\"; border: 2px solid red } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("1 0 0 rg", pdf);   // red border edges
        Assert.Contains(" re", pdf);        // the edges are filled rectangles
    }

    [Fact]
    public void Page_margin_box_border_top_rule_paints_for_a_footer()
    {
        // The common footer "rule line" — @bottom-center { border-top: 1px solid #333 }.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content:\"AB\"; border-top: 1px solid #333333 } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains(" re", pdf);        // a stroked top edge
    }

    [Fact]
    public void Page_margin_box_border_paints_even_when_print_backgrounds_disabled()
    {
        // Borders are NOT background graphics — they paint regardless of PrintBackgrounds (like body
        // borders), unlike the background band.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content:\"AB\"; border: 2px solid red } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver(), PrintBackgrounds = false }));
        Assert.Contains("1 0 0 rg", pdf);   // the border still paints
    }

    [Fact]
    public void Page_margin_box_border_is_not_painted_for_a_zero_height_band()
    {
        // @page { margin-top: 0 } collapses the @top-center band to zero height → no box → the border
        // must NOT paint (geometry guard, review P2). Without the guard the top/bottom edges would
        // stroke a full-width red rectangle around the zero-height band.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { margin-top: 0; @top-center { content:\"AB\"; border: 10px solid red } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.DoesNotContain("1 0 0 rg", pdf);   // no red border around the zero-height band
    }

    [Fact]
    public void Page_margin_box_malformed_border_is_surfaced()
    {
        // An un-expandable border value (`1bananas` isn't a width) is kept as a raw marker by the
        // parser and surfaced via the CSS diagnostic path (review P2) — not silently dropped — while
        // the box still paints its text (default, no border).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content:\"AB\"; border: 1bananas solid red } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
        Assert.DoesNotContain("1 0 0 rg", Latin1(result.Pdf));   // the malformed border paints nothing
    }

    [Fact]
    public void Page_margin_box_border_inherit_is_not_diagnosed()
    {
        // `border: inherit` is a valid CSS-wide keyword (now expanded, Copilot review) — it must NOT
        // surface the invalid-border diagnostic; it resolves to no border (the parent declares none).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content:\"AB\"; border: inherit } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
    }

    // ---- border box shorthands (Task 21 border-box cycle) ----

    [Fact]
    public void Page_margin_box_border_box_shorthands_paint_all_edges()
    {
        // The separate border-style / border-width / border-color box shorthands compose to a painted
        // border, distributed across all four edges.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content:\"AB\"; border-style: solid; border-width: 2px; border-color: red } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("1 0 0 rg", pdf);   // red edges
        Assert.Contains(" re", pdf);        // filled rectangles
    }

    [Fact]
    public void Page_margin_box_border_width_box_shorthand_insets_the_text()
    {
        // A border-width box shorthand sets border-left-width; with a style it paints AND insets the
        // text (the cycle-12 content-inset) — @top-left is start-aligned, so the line shifts right by
        // the left width (20px → ~15pt).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var withBorder = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"AB\"; border-style: solid; border-width: 0 0 0 20px } }</style>" +
            "</head><body></body></html>", opts)));
        var without = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"AB\" } }</style>" +
            "</head><body></body></html>", opts)));
        Assert.InRange(withBorder.X - without.X, 13.0, 17.0);   // border-width:0 0 0 20px → left 20px → ~15pt
    }

    [Fact]
    public void Page_margin_box_malformed_border_box_shorthand_is_surfaced()
    {
        // An un-expandable border box value (`1bananas` isn't a width) is kept as a raw marker and
        // surfaced via the CSS diagnostic path — not silently dropped.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content:\"AB\"; border-width: 1bananas } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
    }

    [Fact]
    public void Page_margin_box_border_width_box_shorthand_cascades_with_a_longhand()
    {
        // The box shorthand expands to per-edge longhands, so it cascades against an explicit
        // border-left-width by importance then source order. Observed via the start-aligned text inset
        // (border-left-width drives the @top-left content-origin shift): 4px → ~3pt, 40px → ~30pt.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double LeftX(string decls) => FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"AB\"; border-style: solid; " + decls +
            " } }</style></head><body></body></html>", opts))).X;

        var shorthandLast = LeftX("border-left-width: 40px; border-width: 4px");      // later shorthand wins → 4px
        var longhandLast = LeftX("border-width: 4px; border-left-width: 40px");        // later longhand wins → 40px
        var importantShorthand = LeftX("border-width: 4px !important; border-left-width: 40px"); // !important wins → 4px

        Assert.True(longhandLast > shorthandLast + 20,
            $"later longhand (40px) should beat the earlier shorthand (4px): short={shorthandLast} long={longhandLast}");
        Assert.True(importantShorthand < longhandLast - 20,
            $"an !important shorthand should beat a later normal longhand: imp={importantShorthand} long={longhandLast}");
        Assert.InRange(importantShorthand - shorthandLast, -2.0, 2.0);   // both resolve left=4px
    }

    [Fact]
    public void Page_margin_box_border_color_box_shorthand_cascades_with_a_longhand()
    {
        // border-color: red expands to all four edges, then a later border-left-color: blue overrides
        // the left edge — so both colors paint (top/right/bottom red, left blue).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; border-style: solid; border-width: 2px; border-color: red; border-left-color: blue } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("1 0 0 rg", pdf);   // red (top/right/bottom)
        Assert.Contains("0 0 1 rg", pdf);   // blue (the left-edge longhand override)
    }

    // ---- §5.3 three-box-per-edge sizing: shrink-to-fit (Task 21) ----

    [Fact]
    public void Page_margin_box_background_shrinks_to_fit_content_width()
    {
        // §5.3 (first cut): a top/bottom edge box's background covers its CONTENT width along the
        // variable axis, not the whole band — so a wider content gives a wider band (was full-band in
        // the cycle-8 model). The A/B-only SyntheticFont makes "ABABABABAB" ~5× the width of "AB".
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double BgWidth(string content) => FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"" + content + "\"; background-color: red } }</style>" +
            "</head><body></body></html>", opts))).W;
        var narrow = BgWidth("AB");
        var wide = BgWidth("ABABABABAB");
        Assert.True(wide > narrow + 20, $"wider content → wider background: narrow={narrow} wide={wide}");
        Assert.True(narrow < 200, $"a 2-glyph background should be content-sized, not the ~468pt full band: {narrow}pt");
    }

    [Fact]
    public void Page_margin_box_empty_content_keeps_the_full_band_background()
    {
        // An empty content:"" box has no content size → it keeps the FULL band (the cycle-8 decorative
        // band is preserved; explicit width is a deferred follow-up).
        var emptyWidth = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"\"; background-color: red } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }))).W;
        Assert.True(emptyWidth > 300, $"an empty box should span the full ~468pt band, got {emptyWidth}pt");
    }

    [Fact]
    public void Page_margin_box_left_edge_background_shrinks_to_fit_height()
    {
        // A left/right edge box's VARIABLE axis is HEIGHT — its background shrinks to the line height,
        // not the full margin column (~648pt for Letter minus 1in margins).
        var h = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @left-middle { content:\"AB\"; background-color: red } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }))).H;
        Assert.True(h < 60, $"a single-line left box should shrink to ~line height, not the full column: {h}pt");
    }

    [Fact]
    public void Page_margin_box_top_center_background_stays_centered_under_text_align_left()
    {
        // §5.3.2.4: the shrunk box's background rect is placed by the box's NAME-DERIVED role, NOT the
        // declared text-align — so @top-center { text-align: left } keeps the band horizontally
        // CENTERED (pre-fix the box slid to the band's left edge). The rect is identical to the same
        // centered box with no declared text-align, and its center sits at the page's horizontal center.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        (double X, double Y, double W, double H) Band(string decls) => FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"" + decls + "; background-color: red } }" +
            "</style></head><body></body></html>", opts)));
        var withLeftPdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; text-align: left; background-color: red } }" +
            "</style></head><body></body></html>", opts));
        var withLeft = FirstRect(withLeftPdf);
        var centered = Band("");
        Assert.Equal(centered.X, withLeft.X, 1);    // text-align: left did NOT move the box's background rect
        var pageCenterX = MediaBox(withLeftPdf).W / 2.0;
        Assert.InRange(withLeft.X + withLeft.W / 2.0, pageCenterX - 1, pageCenterX + 1);   // band centered, not at the left edge
    }

    [Fact]
    public void Page_margin_box_left_middle_background_stays_vertically_centered_under_vertical_align_top()
    {
        // §5.3.2.4: a left/right box is placed by its NAME-DERIVED role too — @left-middle stays
        // vertically CENTERED in its column regardless of a declared vertical-align: top (pre-fix the
        // box slid to the top of the column). The rect's vertical center must sit at the page's
        // vertical center (margins are symmetric, so the column is centered on the page).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @left-middle { content:\"AB\"; vertical-align: top; background-color: red } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var r = FirstRect(pdf);
        var pageCenterY = MediaBox(pdf).H / 2.0;
        Assert.InRange(r.Y + r.H / 2.0, pageCenterY - 1, pageCenterY + 1);   // column-centered despite vertical-align: top
    }

    // ---- §5.3 explicit width / height (Task 21) ----

    [Fact]
    public void Page_margin_box_explicit_width_sizes_the_background()
    {
        // An explicit `width` overrides shrink-to-fit on a top/bottom box's VARIABLE axis: the
        // background rect is the declared content-box width (300px → 225pt), NOT shrink-to-content
        // (~tens of pt) and NOT the full ~468pt band.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; width: 300px; background-color: red } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.InRange(FirstRect(pdf).W, 223.0, 227.0);   // 300px × 0.75 = 225pt
    }

    [Fact]
    public void Page_margin_box_explicit_height_sizes_a_left_box()
    {
        // A left/right box's VARIABLE axis is HEIGHT — an explicit `height` sizes the band there
        // (200px → 150pt), instead of shrinking to the single line height (~tens of pt).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @left-middle { content:\"AB\"; height: 200px; background-color: red } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.InRange(FirstRect(pdf).H, 148.0, 152.0);   // 200px × 0.75 = 150pt
    }

    [Fact]
    public void Page_margin_box_explicit_width_clamps_to_the_band()
    {
        // An over-large explicit width is clamped to the edge band (overflow clipping is deferred).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; width: 10000px; background-color: red } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var bandWidth = MediaBox(pdf).W - 144.0;   // content width = page − 2×1in margins (72pt each)
        Assert.InRange(FirstRect(pdf).W, bandWidth - 1.0, bandWidth + 1.0);
    }

    [Fact]
    public void Page_margin_box_explicit_percent_width_resolves_against_the_band()
    {
        // A percentage width resolves against the box's containing block on that axis — the edge band.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; width: 50%; background-color: red } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var halfBand = (MediaBox(pdf).W - 144.0) / 2.0;
        Assert.InRange(FirstRect(pdf).W, halfBand - 2.0, halfBand + 2.0);
    }

    [Fact]
    public void Page_margin_box_explicit_width_makes_content_text_align_observable()
    {
        // With shrink-to-fit, text-align is a no-op (the box equals the line). An explicit width makes
        // the content box WIDER than the line, so text-align positions the line within it: `right`
        // pushes the line well to the right of `left`. (The box itself stays centered — §5.3.2.4.)
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double LineX(string align) => FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; width: 300px; text-align: " + align + " } }" +
            "</style></head><body></body></html>", opts))).X;
        var leftX = LineX("left");
        var rightX = LineX("right");
        Assert.True(rightX > leftX + 100.0,
            $"text-align should position the line within the 300px box: left={leftX} right={rightX}");
    }

    [Fact]
    public void Page_margin_box_empty_box_with_explicit_width_is_sized()
    {
        // An explicit width sizes even an empty `content:""` box (a sized decorative band), overriding
        // the cycle-14 "empty boxes keep the full band" fallback — 200px → 150pt, with no text.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"\"; width: 200px; background-color: red } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.InRange(FirstRect(pdf).W, 148.0, 152.0);   // 200px × 0.75 = 150pt, not the full band
        Assert.DoesNotContain(" Tj", pdf);                // …and no text painted
    }

    [Fact]
    public void Page_margin_box_explicit_width_is_content_box_padding_adds_to_the_border_box()
    {
        // The explicit `width` is the CONTENT-box; the painted background is the BORDER-box = content +
        // padding (+ border). width:200px + padding-left/right:20px → (200+40)px → 180pt.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; width: 200px; " +
            "padding-left: 20px; padding-right: 20px; background-color: red } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.InRange(FirstRect(pdf).W, 178.0, 182.0);   // (200 + 20 + 20)px × 0.75 = 180pt
    }

    [Fact]
    public void Page_margin_box_explicit_width_border_box_adds_border_width()
    {
        // The border-box also includes the border width: width:200px + border-left/right:10px →
        // (200+20)px → 165pt. (The background band, painted first, is the border-box.)
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; width: 200px; " +
            "border-left: 10px solid blue; border-right: 10px solid blue; background-color: red } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.InRange(FirstRect(pdf).W, 163.0, 167.0);   // (200 + 10 + 10)px × 0.75 = 165pt
    }

    [Fact]
    public void Page_margin_box_explicit_width_plus_insets_clamps_to_the_band()
    {
        // The border-box (content width + padding + border) is clamped to the band — even when the
        // explicit content width is reduced, the insets must not push it past the band edge.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; width: 10000px; " +
            "padding-left: 50px; background-color: red } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var bandWidth = MediaBox(pdf).W - 144.0;
        Assert.InRange(FirstRect(pdf).W, bandWidth - 1.0, bandWidth + 1.0);   // clamped to the band
    }

    [Fact]
    public void Page_margin_box_explicit_percent_height_resolves_against_the_column()
    {
        // A left/right box's VARIABLE axis is HEIGHT — a percentage height resolves against the column
        // extent (the box's containing block on that axis): 50% of (page height − 2×1in margins).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @left-middle { content:\"AB\"; height: 50%; background-color: red } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var halfColumn = (MediaBox(pdf).H - 144.0) / 2.0;
        Assert.InRange(FirstRect(pdf).H, halfColumn - 2.0, halfColumn + 2.0);
    }

    [Fact]
    public void Page_margin_box_deferred_explicit_width_is_surfaced_and_shrinks_to_fit()
    {
        // A font-relative `width: 10em` can't be resolved to a used size here yet → it's diagnosed
        // (CSS-PROPERTY-VALUE-INVALID-001) and DROPPED, so the box EXPLICITLY shrink-to-fits (a 2-glyph
        // content box, not the full ~450pt band) rather than silently falling back — review P2.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; width: 10em; background-color: red } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
        Assert.True(FirstRect(Latin1(result.Pdf)).W < 200.0,
            "a dropped deferred width should shrink-to-fit, not paint the full band");
    }

    [Fact]
    public void Page_margin_box_supported_explicit_width_emits_no_diagnostic()
    {
        // No false positive: an absolute / percentage width must NOT trip the deferred-size guard.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; width: 300px; background-color: red } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
    }

    // ---- §5.3 sibling-box overlap resolution (Task 21) ----

    [Fact]
    public void Page_margin_box_wide_side_box_is_clamped_by_a_center_sibling()
    {
        // §5.3 distribution: a very wide @top-left would overlap a centered @top-center; the center box
        // gets priority, so @top-left's box (background) is CLAMPED to the left gap (~half the band) —
        // much narrower than when @top-left is alone on the edge (clamped only to the full band).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var longText = new string('A', 100);   // far wider than the band → forces overlap with the center
        double LeftBgWidth(string extra) => FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"" + longText + "\"; background-color: red }" +
            extra + " }</style></head><body></body></html>", opts))).W;
        var alone = LeftBgWidth("");
        var withCenter = LeftBgWidth(" @top-center { content:\"AB\" }");
        Assert.True(withCenter < alone - 100,
            $"a center sibling must clamp the wide @top-left box: alone={alone}pt clamped={withCenter}pt");
        Assert.True(withCenter > 50, $"the clamped box should be the left gap, not zero: {withCenter}pt");
    }

    [Fact]
    public void Page_margin_box_center_sibling_stays_centered_when_a_side_box_is_wide()
    {
        // Center-priority: the @top-center box keeps its centered position regardless of a very wide
        // @top-left sibling — its background center stays at the page's horizontal center.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"" + new string('A', 100) + "\" } " +
            "@top-center { content:\"AB\"; background-color: red } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var r = FirstRect(pdf);   // only @top-center has a background
        var pageCenterX = MediaBox(pdf).W / 2.0;
        Assert.InRange(r.X + r.W / 2.0, pageCenterX - 1, pageCenterX + 1);
    }

    [Fact]
    public void Page_margin_box_short_siblings_are_not_repositioned()
    {
        // Short content that doesn't overlap → the distribution is a NO-OP: @top-center's background
        // center is identical with or without a short @top-left sibling (the per-box model is preserved).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double CenterX(string extra)
        {
            var r = FirstRect(Latin1(HtmlPdf.Convert(
                "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; background-color: red }" +
                extra + " }</style></head><body></body></html>", opts)));
            return r.X + r.W / 2.0;
        }
        Assert.Equal(CenterX(""), CenterX(" @top-left { content:\"AB\" }"), 3);
    }

    [Fact]
    public void Page_margin_box_three_wide_siblings_do_not_overlap_and_stay_ordered()
    {
        // §5.3 distribution end-to-end: three wide top boxes (left/center/right) that would all overlap
        // are clamped apart — collecting every background rect, sorted left→right, each box ends at or
        // before the next begins, and the center box stays centered.
        var w = new string('A', 30);
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { " +
            "@top-left { content:\"" + w + "\"; background-color: red } " +
            "@top-center { content:\"AB\"; background-color: lime } " +
            "@top-right { content:\"" + w + "\"; background-color: blue } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var rects = AllRects(pdf);
        Assert.Equal(3, rects.Count);
        rects.Sort((p, q) => p.X.CompareTo(q.X));
        Assert.True(rects[0].X + rects[0].W <= rects[1].X + 0.5, $"left overlaps center: {rects[0]} / {rects[1]}");
        Assert.True(rects[1].X + rects[1].W <= rects[2].X + 0.5, $"center overlaps right: {rects[1]} / {rects[2]}");
        var pageCenterX = MediaBox(pdf).W / 2.0;
        Assert.InRange(rects[1].X + rects[1].W / 2.0, pageCenterX - 1, pageCenterX + 1);   // center stays centered
    }

    [Fact]
    public void Page_margin_box_two_wide_siblings_without_a_center_share_the_band()
    {
        // No center box: a wide @top-left + wide @top-right shrink proportionally to share the band — their
        // backgrounds tile it without overlap, and equal content → equal widths.
        var w = new string('A', 40);
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { " +
            "@top-left { content:\"" + w + "\"; background-color: red } " +
            "@top-right { content:\"" + w + "\"; background-color: blue } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var rects = AllRects(pdf);
        Assert.Equal(2, rects.Count);
        rects.Sort((p, q) => p.X.CompareTo(q.X));
        Assert.True(rects[0].X + rects[0].W <= rects[1].X + 0.5, $"left overlaps right: {rects[0]} / {rects[1]}");
        Assert.Equal(rects[0].W, rects[1].W, 1);   // equal content → equal proportional share
    }

    [Fact]
    public void Page_margin_box_vertical_edge_siblings_do_not_overlap()
    {
        // The distribution runs on the VERTICAL axis too (left/right columns): three tall left-edge boxes
        // that would overlap are clamped apart by HEIGHT — sorted by Y, each ends at or before the next
        // begins, and @left-middle stays vertically centered. (Exercises the vertical grouping/writeback.)
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { " +
            "@left-top { content:\"AB\"; height: 500px; background-color: red } " +
            "@left-middle { content:\"AB\"; background-color: lime } " +
            "@left-bottom { content:\"AB\"; height: 500px; background-color: blue } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var rects = AllRects(pdf);
        Assert.Equal(3, rects.Count);
        rects.Sort((p, q) => p.Y.CompareTo(q.Y));   // PDF y is bottom-origin; ascending = bottom→top
        Assert.True(rects[0].Y + rects[0].H <= rects[1].Y + 0.5, $"boxes overlap: {rects[0]} / {rects[1]}");
        Assert.True(rects[1].Y + rects[1].H <= rects[2].Y + 0.5, $"boxes overlap: {rects[1]} / {rects[2]}");
        var pageCenterY = MediaBox(pdf).H / 2.0;
        Assert.InRange(rects[1].Y + rects[1].H / 2.0, pageCenterY - 1, pageCenterY + 1);   // middle stays centered
    }

    [Fact]
    public void Page_margin_box_flexed_box_re_wraps_its_content_to_multiple_lines()
    {
        // §5.3 min/max-content flex + overflow wrapping: a wide WRAPPABLE @top-left overlapping a centered
        // @top-center is flexed narrower than its single-line width, so its content RE-WRAPS to multiple
        // lines (more Td operators) instead of overflowing — vs the same @top-left alone (one line).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        const string wrappable = "A A A A A A A A A A A A";   // 12 wrappable words (spaces = break points)
        int Lines(string extra) => TdCount(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"" + wrappable + "\" }" + extra +
            " }</style></head><body></body></html>", opts)));
        var alone = Lines("");
        var withCenter = Lines(" @top-center { content:\"AB\" }");
        Assert.True(withCenter > alone,
            $"a flexed wrappable box should wrap to more lines than when alone: alone={alone} withCenter={withCenter}");
    }

    [Fact]
    public void Page_margin_box_center_box_stays_centered_beside_a_wide_wrappable_side()
    {
        // §5.3.2 / review P1 — the FLEX path must keep @top-center CENTERED. A wide WRAPPABLE @top-left
        // (spaces = break points) overlaps and flexes narrower; @top-center's background centre must stay
        // at the page centre. (The old flex tiled B right after A, sliding the page number off-centre.)
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"" + Words(40) + "\" } " +
            "@top-center { content:\"AB\"; background-color: red } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var r = FirstRect(pdf);   // only @top-center has a background
        var pageCenterX = MediaBox(pdf).W / 2.0;
        Assert.InRange(r.X + r.W / 2.0, pageCenterX - 1, pageCenterX + 1);
    }

    [Fact]
    public void Page_margin_box_wrappable_side_does_not_overlap_the_center()
    {
        // §5.3.2 / review P1 — no side/centre overlap on the FLEX path: a wide wrappable @top-left + a
        // centred @top-center, both with backgrounds → the left box ends at or before the centre begins,
        // and the centre stays centred.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"" + Words(40) + "\"; background-color: red } " +
            "@top-center { content:\"AB\"; background-color: lime } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var rects = AllRects(pdf);
        Assert.Equal(2, rects.Count);
        rects.Sort((p, q) => p.X.CompareTo(q.X));
        Assert.True(rects[0].X + rects[0].W <= rects[1].X + 0.5, $"side overlaps centre: {rects[0]} / {rects[1]}");
        var pageCenterX = MediaBox(pdf).W / 2.0;
        Assert.InRange(rects[1].X + rects[1].W / 2.0, pageCenterX - 1, pageCenterX + 1);
    }

    [Fact]
    public void Page_margin_box_min_overflow_widths_are_proportional_to_content()
    {
        // §5.3.2 / review P2 end-to-end: two rigid side boxes (no centre) whose min-contents overflow the
        // band share it PROPORTIONALLY to content — @top-left has twice @top-right's content, so its
        // background is ~2× as wide, and they tile the band without overlap (not clamped or max-proportional).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { " +
            "@top-left { content:\"" + new string('A', 60) + "\"; background-color: red } " +
            "@top-right { content:\"" + new string('A', 30) + "\"; background-color: blue } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var rects = AllRects(pdf);
        Assert.Equal(2, rects.Count);
        rects.Sort((p, q) => p.X.CompareTo(q.X));
        Assert.True(rects[0].X + rects[0].W <= rects[1].X + 0.5, $"left overlaps right: {rects[0]} / {rects[1]}");
        Assert.True(rects[0].W < MediaBox(pdf).W, "the wider box must not be clamped to the full band");
        Assert.Equal(2.0, rects[0].W / rects[1].W, 1);   // left content 2× right → ~2× width (min-proportional)
    }

    [Fact]
    public void Page_margin_box_wrapped_lines_stay_block_centered_in_the_band()
    {
        // review P2 — a re-wrapped multi-line header is positioned by its FULL block height, not as one
        // line. So the wrapped block's vertical CENTRE coincides with where a single centred line sits
        // (proving block, not single-line, centring — which would slide the block down out of the band).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double[] TdYs(string content) => AllTdY(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"" + content + "\" } }</style>" +
            "</head><body></body></html>", opts)));
        var single = TdYs("AB");          // one centred line
        var wrapped = TdYs(Words(200));   // long → wraps to several lines, filling the band width
        Assert.True(wrapped.Length >= 2, $"expected the content to wrap to multiple lines: {wrapped.Length}");
        double Max(double[] a) { var m = a[0]; foreach (var v in a) if (v > m) m = v; return m; }
        double Min(double[] a) { var m = a[0]; foreach (var v in a) if (v < m) m = v; return m; }
        var blockMid = (Max(wrapped) + Min(wrapped)) / 2.0;
        Assert.InRange(blockMid, single[0] - 1.5, single[0] + 1.5);   // block centre == single-line centre
    }

    [Fact]
    public void Page_margin_box_wrapped_lines_are_individually_aligned()
    {
        // Task 21 (wrapped-line content-alignment): a wrapped @top-center header centers EACH line within
        // the content box (so narrower lines are indented more) — not just the block. A @top-left header
        // left-aligns every line at one X. So the centered block's per-line start Xs VARY, while the
        // left-aligned block's are constant. (Before the fix, the centered block also shared one X.)
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double[] LineXs(string box) => AllTdX(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @" + box + " { content:\"" + Words(200) + "\" } }</style>" +
            "</head><body></body></html>", opts)));
        double Spread(double[] a)
        {
            double lo = a[0], hi = a[0];
            foreach (var v in a) { if (v < lo) lo = v; if (v > hi) hi = v; }
            return hi - lo;
        }
        var left = LineXs("top-left");
        var center = LineXs("top-center");
        Assert.True(left.Length >= 2 && center.Length >= 2, "content should wrap to multiple lines");
        Assert.True(Spread(left) < 1.0, $"left-aligned lines should share one X: spread={Spread(left)}");
        Assert.True(Spread(center) > 5.0, $"centered lines are individually centered (vary): spread={Spread(center)}");
    }

    /// <summary>n space-separated "A" words ("A A A …") — wrappable synthetic content (the spaces are the
    /// break opportunities; the synthetic font has no space glyph but spaces still break a line).</summary>
    private static string Words(int n) => new string('A', n).Replace("A", "A ").TrimEnd();

    /// <summary>The Y operand (pt) of every <c>Td</c> text-position operator, in content-stream order.</summary>
    private static double[] AllTdY(string pdf) => AllTdOperand(pdf, yAxis: true);

    /// <summary>The X operand (pt) of every <c>Td</c> text-position operator, in content-stream order.</summary>
    private static double[] AllTdX(string pdf) => AllTdOperand(pdf, yAxis: false);

    private static double[] AllTdOperand(string pdf, bool yAxis)
    {
        var vals = new List<double>();
        for (var i = pdf.IndexOf(" Td", StringComparison.Ordinal); i >= 0;
             i = pdf.IndexOf(" Td", i + 3, StringComparison.Ordinal))
        {
            var nums = pdf[..i].TrimEnd().Split(' ');   // … <x> <y>
            vals.Add(double.Parse(nums[yAxis ? ^1 : ^2], CultureInfo.InvariantCulture));
        }
        return vals.ToArray();
    }

    // ---- string-set / string() (Task 22) + position: running() / element() (Task 23) ----

    [Fact]
    public void Page_margin_box_string_resolves_a_string_set_value()
    {
        // Task 22: `h1 { string-set: t attr(data-t) }` sets the named string `t`; the header's
        // `content: string(t)` pulls it. Body h1 "AB" (2 glyphs) + header string(t)="AB" (2) = 4;
        // an undefined name resolves to the empty string → header empty → body's 2 glyphs only.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        int Glyphs(string headerContent) => TotalGlyphCount(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>h1 { string-set: t attr(data-t) } " +
            "@page { @top-center { content: " + headerContent + " } }</style></head>" +
            "<body><h1 data-t=\"AB\">AB</h1></body></html>", opts)));
        Assert.Equal(2, Glyphs("string(missing)"));   // undefined name → empty header; body h1 only
        Assert.Equal(4, Glyphs("string(t)"));         // + header string(t) = "AB" (2 more glyphs)
    }

    [Fact]
    public void Page_margin_box_string_resolves_a_literal_string_set()
    {
        // string-set can take a literal content-list: `string-set: t "AB"` → string(t) renders "AB".
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>body { string-set: t \"AB\" } @page { @top-center { content: string(t) } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(2, TotalGlyphCount(pdf));   // header "AB" only (empty body)
    }

    [Fact]
    public void Page_margin_box_string_set_content_pulls_the_element_text()
    {
        // Task 22 (content() form) — the canonical running header: `h1 { string-set: title content() }`
        // sets `title` to the h1's own text; the header's `content: string(title)` pulls it. AngleSharp.Css
        // DROPS the content() declaration, so this exercises the raw-CSS recovery (CssPreprocessor) → the
        // cascade → the collector resolving content() to the element's text. Body h1 "AB" (2) + header (2) = 4.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>h1 { string-set: title content() } @page { @top-center { content: string(title) } }</style>" +
            "</head><body><h1>AB</h1></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(4, TotalGlyphCount(pdf));   // body h1 "AB" (2) + header string(title) = "AB" (2)
    }

    [Fact]
    public void Page_margin_box_string_set_content_last_keyword_takes_the_last_element()
    {
        // content() on a selector matching several elements, with `string(title, last)` (the EXIT value):
        // the named string is the LAST match's text. Body h1 "A"(1) + h1 "AB"(2) = 3; header
        // string(title, last) = last h1 = "AB"(2) → total 5. (The default `first` would give "A"(1) → 4.)
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>h1 { string-set: title content() } @page { @top-center { content: string(title, last) } }</style>" +
            "</head><body><h1>A</h1><h1>AB</h1></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(5, TotalGlyphCount(pdf));   // body 3 + header last "AB"(2)
    }

    [Fact]
    public void Page_margin_box_string_set_content_mixes_with_a_literal()
    {
        // A string-set content-list can mix a literal with content(): `string-set: t "A" content()` →
        // string(t) = "A" + the element text. Body h1 "B"(1) + header "A"+"B" = "AB"(2) → total 3.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>h1 { string-set: t \"A\" content() } @page { @top-center { content: string(t) } }</style>" +
            "</head><body><h1>B</h1></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(3, TotalGlyphCount(pdf));   // body h1 "B"(1) + header "A"+content()="B" → "AB"(2)
    }

    [Fact]
    public void Page_margin_box_string_set_content_includes_nested_indented_text()
    {
        // content() pulls the element's full (NESTED) text end-to-end and the source INDENTATION does not
        // leak in: an indented <h1> wrapping two <span>s resolves to "AB" in the header (the leading
        // "\n  " + trailing "\n" are stripped, so no stray .notdef whitespace glyphs). Body h1 "AB" (2) +
        // header content()="AB" (2) = 4. (The exact GCPM white-space:normal collapse is unit-tested in
        // CssContentListTests; here the margin box's own NoWrap layout would also collapse it, so this
        // asserts the nested-element + indentation PATH renders cleanly end-to-end.)
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>h1 { string-set: title content() } @page { @top-center { content: string(title) } }</style>" +
            "</head><body><h1>\n  <span>A</span><span>B</span>\n</h1></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(4, TotalGlyphCount(pdf));   // body "AB"→A,B (2) + header content()="AB"→A,B (2)
    }

    [Fact]
    public void Page_margin_box_element_renders_a_running_element()
    {
        // Task 23: a div with `position: running(rh)` is REMOVED from the body flow; the header's
        // `content: element(rh)` pulls its text "AB" into the margin box. Only the header's 2 glyphs
        // paint (the running div is out of flow) — 4 would mean it rendered in both places.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } @page { @top-center { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(2, TotalGlyphCount(pdf));   // ONLY the header element(rh)="AB"
    }

    [Theory]
    [InlineData("element(rh)", 1)]          // GCPM default = first → "A"
    [InlineData("element(rh, first)", 1)]   // explicit first → "A"
    [InlineData("element(rh, last)", 2)]    // last → "AB"
    public void Page_margin_box_element_position_keyword_picks_the_occurrence(string content, int glyphs)
    {
        // Two running elements share the name `rh`. Per CSS GCPM §7.4 element() defaults to `first` (the
        // first occurrence on the page), like string(); `last` is the exit value. Both divs are removed
        // from flow, so ONLY the header renders. first → "A"(1); last → "AB"(2).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } @page { @top-center { content: " + content + " } }</style>" +
            "</head><body><div class=\"rh\">A</div><div class=\"rh\">AB</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(glyphs, TotalGlyphCount(pdf));
    }

    [Fact]
    public void Page_margin_box_element_normalizes_indented_nested_text()
    {
        // element() GCPM-normalizes the running element's text (white-space: normal) — an INDENTED div
        // with NESTED spans resolves to "AB" in the header without leaking the source indentation. The div
        // is removed from flow → only the header's 2 glyphs paint.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } @page { @top-center { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">\n  <span>A</span><span>B</span>\n</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(2, TotalGlyphCount(pdf));   // "AB" (nested spans, indentation stripped)
    }

    [Fact]
    public void Page_margin_box_sizable_running_element_renders_in_full_under_the_cap()
    {
        // A sizable (but under the 64 KiB cap) running element renders end-to-end in full — the bounded
        // read (review P2) only truncates above the cap, so normal running content is unaffected.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } @page { @top-center { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">" + new string('A', 500) + "</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(500, TotalGlyphCount(pdf));   // all 500 'A' glyphs (not truncated under the cap)
    }

    // ---- element() first-cut OWN-STYLE rendering (Task 23) ----

    [Fact]
    public void Page_margin_box_element_uses_the_running_elements_own_color()
    {
        // First cut of full block rendering: a STANDALONE element(rh) paints the running element's text in
        // the ELEMENT's own color, not the box's default. A red .rh → the header glyphs are red.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); color: #ff0000 } @page { @top-center { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("1 0 0 rg", pdf);   // rgb(255,0,0) — the element's own red text fill
    }

    [Fact]
    public void Page_margin_box_element_uses_the_running_elements_own_font_size()
    {
        // A STANDALONE element(rh) renders in the running element's own font-size: a 24px .rh → 18pt Tf
        // (24 × 0.75), vs the box's default 16px → 12pt.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); font-size: 24px } @page { @top-center { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(18.0, FirstTf(pdf), 1);   // the element's 24px → 18pt
    }

    [Fact]
    public void Page_margin_box_mixed_element_content_keeps_the_box_style()
    {
        // Own-style is STANDALONE element() only (GCPM). A MIXED list (`"A" element(rh)`) keeps the box's
        // own style — so the red .rh does NOT colour the (box-default black) header.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); color: #ff0000 } @page { @top-center { content: \"A\" element(rh) } }</style>" +
            "</head><body><div class=\"rh\">B</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.DoesNotContain("1 0 0 rg", pdf);   // mixed content → box style (black), not the element's red
    }

    [Theory]
    [InlineData("element(rh)", "1 0 0 rg")]       // default = first → red (r1)
    [InlineData("element(rh, first)", "1 0 0 rg")]
    [InlineData("element(rh, last)", "0 0 1 rg")] // last → blue (r2)
    public void Page_margin_box_element_own_style_follows_the_selected_occurrence(string content, string colorOp)
    {
        // Two running elements share `rh` with different colours; the OWN-STYLE follows the SAME occurrence
        // the text does — element() default + first → the first's red, last → the last's blue.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.r1 { position: running(rh); color: #ff0000 } " +
            ".r2 { position: running(rh); color: #0000ff } @page { @top-center { content: " + content + " } }</style>" +
            "</head><body><div class=\"r1\">A</div><div class=\"r2\">B</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains(colorOp, pdf);
    }

    [Fact]
    public void Page_margin_box_element_last_unstyled_does_not_inherit_the_first_occurrences_style()
    {
        // Review P1: a STYLED first + UNSTYLED last running element. element(rh, last) must render the LAST
        // text in the box's own (default black) style — NOT the first occurrence's red. Without lockstep
        // style capture, the stale first-occurrence red would leak onto the last text.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.r1 { position: running(rh); color: #ff0000 } " +
            ".r2 { position: running(rh) } @page { @top-center { content: element(rh, last) } }</style>" +
            "</head><body><div class=\"r1\">A</div><div class=\"r2\">B</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.DoesNotContain("1 0 0 rg", pdf);   // last (r2) unstyled → box style (black), not the first's red
    }

    [Fact]
    public void Page_margin_box_element_first_unstyled_does_not_inherit_a_later_occurrences_style()
    {
        // Review P1, converse: an UNSTYLED first + STYLED second. element(rh) (default = first) must render
        // the FIRST text in the box's own style — NOT the later occurrence's blue. Without lockstep capture,
        // the second's blue would be recorded as the "first" style and leak onto the first text.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.r1 { position: running(rh) } " +
            ".r2 { position: running(rh); color: #0000ff } @page { @top-center { content: element(rh) } }</style>" +
            "</head><body><div class=\"r1\">A</div><div class=\"r2\">B</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.DoesNotContain("0 0 1 rg", pdf);   // first (r1) unstyled → box style (black), not the later blue
    }

    [Fact]
    public void Page_margin_box_element_wrapped_lines_use_the_running_element_font_pitch()
    {
        // Review P1 (#2): a standalone element() shapes glyphs at the running element's font-size, so the
        // painter must STACK wrapped lines at THAT pitch — not the box's default 16px. A 32px element forced
        // to wrap (narrow box width) → consecutive baselines are 32 × 1.2 × 0.75 = 28.8pt apart, not the
        // box-default 16 × 1.2 × 0.75 = 14.4pt (which would overlap the 32px glyphs).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); font-size: 32px } " +
            "@page { @top-center { content: element(rh); width: 20px } }</style>" +
            "</head><body><div class=\"rh\">A A A A</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var ys = AllTdY(pdf);
        Array.Sort(ys);
        Array.Reverse(ys);   // descending: the top line (largest PDF-y) first
        var pitch = 0.0;
        var found = false;
        for (var i = 1; i < ys.Length; i++)
            if (ys[i - 1] - ys[i] > 0.5) { pitch = ys[i - 1] - ys[i]; found = true; break; }
        Assert.True(found, "expected the 32px content to wrap to >= 2 lines at distinct baselines");
        Assert.Equal(28.8, pitch, 1);   // line pitch = element 32px × 1.2 × 0.75, not the 16px box default
    }

    [Fact]
    public void Page_margin_box_element_box_height_uses_the_running_element_font_size()
    {
        // Review P1 (#2): the box reserves its block height from the running element's font-size too. A
        // VERTICAL edge box (@left-middle, height shrinks to content) holding a single-line 32px element →
        // its background band is 32 × 1.2 × 0.75 = 28.8pt tall, not the box default 16 × 1.2 × 0.75 = 14.4pt.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); font-size: 32px } " +
            "@page { @left-middle { content: element(rh); background-color: #ff0000 } }</style>" +
            "</head><body><div class=\"rh\">A</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(28.8, FirstRect(pdf).H, 1);   // band height = element 32px × 1.2 × 0.75
    }

    [Fact]
    public void Page_margin_box_element_inherits_an_ancestor_color()
    {
        // Review P2: color is CSS-inherited, so an ANCESTOR's `color` is the running element's own colour —
        // a standalone element() renders the header in the inherited red even though the running element
        // itself declares no colour (the collector walks ancestors for the nearest declared winner).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.section { color: #ff0000 } .rh { position: running(rh) } " +
            "@page { @top-center { content: element(rh) } }</style>" +
            "</head><body><div class=\"section\"><div class=\"rh\">AB</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("1 0 0 rg", pdf);   // inherited red from the .section ancestor
    }

    [Fact]
    public void Page_margin_box_element_inherits_an_ancestor_font_size()
    {
        // Review P2: font-size is inherited too — an ancestor's 24px reaches the running element's own-style
        // → 18pt Tf (24 × 0.75), vs the box default 16px → 12pt.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.section { font-size: 24px } .rh { position: running(rh) } " +
            "@page { @top-center { content: element(rh) } }</style>" +
            "</head><body><div class=\"section\"><div class=\"rh\">AB</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(18.0, FirstTf(pdf), 1);   // inherited 24px → 18pt
    }

    // ---- element() full-block decoration: own background (task A) + border (task B) ----

    [Fact]
    public void Page_margin_box_element_paints_the_running_elements_own_background()
    {
        // Task 23 full-block first cut (task A): a standalone element() adopts the running element's OWN
        // background-color as the box decoration — `.rh { background-color: #3366cc }` paints a band behind
        // the header (rgb 0.2 0.4 0.8), even though the @page box declares no background of its own.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); background-color: #3366cc } " +
            "@page { @top-center { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("0.2 0.4 0.8 rg", pdf);   // the element's own #3366cc band (text is black)
    }

    [Fact]
    public void Page_margin_box_box_background_overrides_the_running_elements_own()
    {
        // The element's decoration cascades UNDER the box's own declarations — a box `background-color`
        // overrides the element's. Box red wins; the element's blue band does NOT paint.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); background-color: #0000ff } " +
            "@page { @top-center { content: element(rh); background-color: #ff0000 } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("1 0 0 rg", pdf);        // box's red band (it overrides the element's)
        Assert.DoesNotContain("0 0 1 rg", pdf);  // the element's blue background did NOT win
    }

    [Theory]
    [InlineData("element(rh)", "1 0 0 rg")]        // default = first → r1's red band
    [InlineData("element(rh, last)", "0 0 1 rg")]  // last → r2's blue band
    public void Page_margin_box_element_background_follows_the_selected_occurrence(string content, string colorOp)
    {
        // The element's DECORATION follows the same occurrence the text does (in lockstep) — element()
        // default/first → the first running element's red background, last → the last's blue.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.r1 { position: running(rh); background-color: #ff0000 } " +
            ".r2 { position: running(rh); background-color: #0000ff } @page { @top-center { content: " + content + " } }</style>" +
            "</head><body><div class=\"r1\">A</div><div class=\"r2\">B</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains(colorOp, pdf);
    }

    [Fact]
    public void Page_margin_box_element_does_not_inherit_an_ancestor_background()
    {
        // background-color is NON-inherited, so an ancestor's background must NOT bleed onto the running
        // element (unlike color/font, which DO inherit). No red band paints.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.section { background-color: #ff0000 } .rh { position: running(rh) } " +
            "@page { @top-center { content: element(rh) } }</style>" +
            "</head><body><div class=\"section\"><div class=\"rh\">AB</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.DoesNotContain("1 0 0 rg", pdf);   // ancestor red background NOT applied (non-inherited)
    }

    [Fact]
    public void Page_margin_box_element_strokes_the_running_elements_own_border()
    {
        // Task 23 full-block first cut (task B): a standalone element() adopts the running element's OWN
        // border. `.rh { border: 2px solid #00ff00 }` → green edges (the element's `border` shorthand is
        // expanded to longhands by the normal cascade, since the element is a real DOM node). Borders paint
        // as filled rects in the edge colour.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); border: 2px solid #00ff00 } " +
            "@page { @top-center { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("0 1 0 rg", pdf);   // green border edges (text is black)
    }

    [Fact]
    public void Page_margin_box_element_border_uses_the_running_elements_own_color_as_currentcolor()
    {
        // A border with no colour uses currentColor — for a standalone element() that's the ELEMENT's own
        // colour (read from the content style), not the box's default black. `color: #00ff00; border: 2px
        // solid` → green edges. (The text is green too; the green edges prove the border took currentColor.)
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); color: #00ff00; border: 2px solid } " +
            "@page { @top-center { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("0 1 0 rg", pdf);   // border edges in the element's own green (currentColor)
    }

    [Fact]
    public void Page_margin_box_element_border_width_insets_the_text()
    {
        // The element's border-width insets its text (the existing border content-inset) — a start-aligned
        // @top-left with the element's `border-left: 20px solid` shifts the line right by ~15pt (20px),
        // exactly like a box-declared border.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var withBorder = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); border-left: 20px solid red } " +
            "@page { @top-left { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>", opts)));
        var without = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            "@page { @top-left { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>", opts)));
        Assert.InRange(withBorder.X - without.X, 13.0, 17.0);   // border-left 20px → ~15pt inset
    }

    [Fact]
    public void Page_margin_box_running_element_is_removed_from_the_body_flow()
    {
        // A normal div renders in the body; with `position: running()` (and no element() reference) the
        // div is removed from flow and pulled nowhere → nothing renders.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var normal = TotalGlyphCount(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head></head><body><div>AB</div></body></html>", opts)));
        var running = TotalGlyphCount(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) }</style></head>" +
            "<body><div class=\"rh\">AB</div></body></html>", opts)));
        Assert.Equal(2, normal);    // normal div renders "AB" in the body
        Assert.Equal(0, running);   // running div removed from flow, referenced nowhere → nothing paints
    }

    [Fact]
    public void Page_margin_box_position_running_emits_no_invalid_value_diagnostic()
    {
        // `position: running(name)` is a valid GCPM value — it must NOT emit CSS-PROPERTY-VALUE-INVALID-001
        // (BoxBuilder detects it from the raw value before the keyword resolver, which would reject it).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) }</style></head>" +
            "<body><div class=\"rh\">AB</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
    }

    [Fact]
    public void Page_margin_box_string_set_supports_multiple_comma_separated_pairs()
    {
        // GCPM §2: string-set takes one-or-more comma-separated name/value pairs. Both names resolve —
        // string(a)="AB" (2 glyphs) + string(b)="BA" (2) = 4 across the two headers (empty body). Only 2
        // would mean a single pair parsed; 0 would mean the whole declaration failed (review P2).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>body { string-set: a attr(data-a), b attr(data-b) } " +
            "@page { @top-left { content: string(a) } @top-right { content: string(b) } }</style></head>" +
            "<body data-a=\"AB\" data-b=\"BA\"></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(4, TotalGlyphCount(pdf));   // string(a)=2 + string(b)=2 → both pairs set
    }

    [Fact]
    public void Page_margin_box_string_last_keyword_takes_the_exit_value()
    {
        // Two elements set the same name; `string(t, last)` is the EXIT value (the LAST in document order).
        // Body h1 "A"(1) + h1 "AB"(2) = 3 glyphs; header string(t, last) = last data-t = "AB"(2) → total 5.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>h1 { string-set: t attr(data-t) } @page { @top-center { content: string(t, last) } }</style>" +
            "</head><body><h1 data-t=\"A\">A</h1><h1 data-t=\"AB\">AB</h1></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(5, TotalGlyphCount(pdf));   // body 3 + header last "AB"(2)
    }

    [Theory]
    [InlineData("string(t)")]          // GCPM DEFAULT is `first` (review P1)
    [InlineData("string(t, first)")]   // explicit `first`
    public void Page_margin_box_string_default_and_first_take_the_first_assignment(string headerContent)
    {
        // Per CSS GCPM §7.3 the DEFAULT position keyword is `first` (the first assignment on the page) —
        // NOT the exit value. Same document as the exit-value test: body h1 "A"(1) + "AB"(2) = 3; header
        // first = first h1 = "A"(1) → total 4. (The exit value → 5; this 4 proves the default is `first`.)
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>h1 { string-set: t attr(data-t) } @page { @top-center { content: " + headerContent + " } }</style>" +
            "</head><body><h1 data-t=\"A\">A</h1><h1 data-t=\"AB\">AB</h1></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(4, TotalGlyphCount(pdf));   // body 3 + header first "A"(1)
    }

    [Fact]
    public void Page_margin_box_invalid_running_name_does_not_remove_the_element()
    {
        // `position: running(123)` is an INVALID custom-ident (leading digit) → NOT treated as running →
        // the element is NOT removed from flow; it renders normally in the body (review P2). If the
        // invalid name were accepted, the div would be removed (0 glyphs).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.x { position: running(123) }</style></head>" +
            "<body><div class=\"x\">AB</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(2, TotalGlyphCount(pdf));   // the div renders "AB" in the body (not removed)
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

    /// <summary>Count of <c>Td</c> text-position operators. <c>TextPainter</c> emits one <c>Td</c> per
    /// painted SLICE (per line, per run-slice), so a multi-run line can yield several — but for the
    /// single-run synthetic-font content here it's effectively one per line, so more lines (e.g. wrapped
    /// content) → a higher count (Copilot review).</summary>
    private static int TdCount(string pdf)
    {
        var n = 0;
        for (var i = pdf.IndexOf(" Td", StringComparison.Ordinal); i >= 0;
             i = pdf.IndexOf(" Td", i + 3, StringComparison.Ordinal))
            n++;
        return n;
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
