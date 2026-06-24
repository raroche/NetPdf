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
    public void Convert_paints_a_linear_gradient_background_as_a_pdf_axial_shading()
    {
        // Phase 4 gradients — `background-image: linear-gradient(...)` emits a PDF native
        // axial shading (ShadingType 2) with a color function, painted via the `sh` operator
        // clipped to the box. End-to-end through the cascade + collection + painter.
        const string html =
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;" +
            "background-image:linear-gradient(to right, #ff0000, #0000ff)\"></div>" +
            "</body></html>";

        var result = HtmlPdf.ConvertDetailed(html);
        var text = Latin1(result.Pdf);

        Assert.Contains("/ShadingType 2", text);   // axial shading object
        Assert.Contains("/FunctionType 2", text);  // the 2-stop color function
        Assert.Contains(" sh", text);              // the shading paint operator
        Assert.Contains("W n", text);              // clipped to the box
        // No "unsupported background-image" diagnostic — the gradient IS handled now.
        Assert.DoesNotContain(
            result.Warnings, d => d.Code == DiagnosticCodes.CssBackgroundImageUnsupported001);
    }

    [Fact]
    public void Convert_honors_linear_gradient_stop_positions_in_the_color_function()
    {
        // Phase 4 gradients (PR #209 Copilot fix) — authored stop positions must be honored:
        // `red 25%, blue 75%` holds red over [0, .25], transitions over [.25, .75], holds blue
        // over [.75, 1]. That is a FunctionType 3 stitch (with end holds) — NOT a single
        // FunctionType 2 spanning the whole axis — with /Bounds at the stop offsets.
        const string html =
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:20px;" +
            "background-image:linear-gradient(to right, red 25%, blue 75%)\"></div>" +
            "</body></html>";

        var text = Latin1(HtmlPdf.Convert(html));
        Assert.Contains("/FunctionType 3", text);  // stitched (end holds + interior transition)
        Assert.Contains("0.25", text);              // /Bounds carry the authored offsets
        Assert.Contains("0.75", text);
    }

    [Fact]
    public void Convert_handles_terminal_hard_stop_gradients_without_malformed_bounds()
    {
        // Phase 4 gradients (PR #209 review [P1]) — repeated TERMINAL stops at 100% used to
        // ceiling-clamp to duplicate /Bounds at 1.0 (a malformed FunctionType 3). The whole
        // value must still produce a well-formed, reader-safe PDF.
        const string html =
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:20px;" +
            "background-image:linear-gradient(red 100%, blue 100%, green 100%)\"></div>" +
            "</body></html>";

        var text = Latin1(HtmlPdf.Convert(html));
        Assert.StartsWith("%PDF-", text);
        Assert.Contains("%%EOF", text);
        Assert.Contains("/ShadingType 2", text);
        // Whatever interior /Bounds the stitch emits, they must be strictly inside (0, 1) and
        // strictly increasing — never the duplicate `1 1` the old clamp produced.
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(text, @"/Bounds \[([^\]]*)\]"))
        {
            var nums = m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double prev = 0.0;
            foreach (var s in nums)
            {
                var b = double.Parse(s, CultureInfo.InvariantCulture);
                Assert.True(b > prev && b < 1.0, $"bound {s} must be in (prev, 1): prev={prev}");
                prev = b;
            }
        }
    }

    [Fact]
    public void Convert_paints_a_radial_gradient_background_as_a_pdf_radial_shading()
    {
        // Phase 4 gradients — `background-image: radial-gradient(...)` emits a PDF native
        // radial shading (ShadingType 3) with a color function, painted via `sh`.
        const string html =
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:80px;height:80px;" +
            "background-image:radial-gradient(circle at center, #ffffff, #000000)\"></div>" +
            "</body></html>";

        var result = HtmlPdf.ConvertDetailed(html);
        var text = Latin1(result.Pdf);

        Assert.Contains("/ShadingType 3", text);   // radial shading object
        Assert.Contains("/FunctionType 2", text);  // the 2-stop color function
        Assert.Contains(" sh", text);
        Assert.DoesNotContain(
            result.Warnings, d => d.Code == DiagnosticCodes.CssBackgroundImageUnsupported001);
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
    public void Content_taller_than_one_page_paginates_across_multiple_pages()
    {
        // Ten 300px-tall blocks (3000px) far exceed an A4 content box (~931px). With the
        // multi-page driver (layout→PDF cycle 3) the block content now FLOWS across pages
        // instead of overflowing a single page — and since it splits cleanly at block
        // boundaries (3 blocks ≈ 900px fit per page), no overflow diagnostic fires.
        var sb = new StringBuilder("<!DOCTYPE html><html><body>");
        for (var i = 0; i < 10; i++)
            sb.Append("<div style=\"height:300px;background-color:#abcdef\"></div>");
        sb.Append("</body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString());

        Assert.True(result.PageCount > 1, $"expected multi-page output, got {result.PageCount}");
        Assert.True(result.PageCount < 20, $"sanity: expected a handful of pages, got {result.PageCount}");
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
    }

    [Fact]
    public void Prose_block_flow_taller_than_one_page_paginates_at_paragraph_boundaries()
    {
        // Prose pagination (block-granularity): a stack of text-bearing blocks (inline-only blocks) taller
        // than the page BREAKS across pages instead of overflowing page 1 (the pre-fix behavior was 120
        // paragraphs crammed onto a single page). Each paragraph is emitted EXACTLY ONCE — Td count ==
        // paragraph count — so the break/resume neither drops content (overflow) nor duplicates it. These
        // are ONE-LINE paragraphs, so each moves WHOLE to the next page (a single line can't be split);
        // splitting a SINGLE multi-line paragraph's lines across pages is covered by
        // Single_tall_paragraph_splits_its_lines_across_pages (`inline-only-block-line-splitting`).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var sb = new StringBuilder("<!DOCTYPE html><html><body>");
        for (var i = 0; i < 120; i++) sb.Append("<p>Line ").Append(i).Append("</p>");
        sb.Append("</body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString(), opts);

        Assert.True(result.PageCount >= 2, $"expected prose to paginate; got {result.PageCount} page(s).");
        Assert.True(result.PageCount < 20, $"sanity: a handful of pages, got {result.PageCount}.");
        Assert.Equal(120, TdCount(Latin1(result.Pdf)));   // one Td per paragraph — no content lost or duplicated
        // No CONTENT was truncated — the paragraphs paginated, none dropped (review P3, like the adjacent
        // block-pagination tests). (PAGINATION-FORCED-OVERFLOW-001 is the existing "subtree taller than a
        // page, committed + recursed" signal shared by ALL block-flow pagination — empty-height blocks emit
        // it too — so it is NOT asserted absent here; only data loss matters.)
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
    }

    [Fact]
    public void Prose_and_empty_height_blocks_paginate_together_without_content_loss()
    {
        // The inline-only break (prose) composes with the block-flow break (explicit-height blocks): a doc
        // mixing both paginates with ALL content present. 20 + 20 one-line paragraphs around a tall RED
        // spacer → both paths break, all 40 paragraph lines emit exactly once, AND the spacer still paints
        // its red fill (so dropping/misplacing the spacer fails the test, not just the surrounding prose —
        // review P2).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var sb = new StringBuilder("<!DOCTYPE html><html><body>");
        for (var i = 0; i < 20; i++) sb.Append("<p>Alpha ").Append(i).Append("</p>");
        sb.Append("<div style=\"height:500px;background-color:red\"></div>");
        for (var i = 0; i < 20; i++) sb.Append("<p>Beta ").Append(i).Append("</p>");
        sb.Append("</body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString(), opts);
        var pdf = Latin1(result.Pdf);

        Assert.True(result.PageCount >= 2, $"expected pagination; got {result.PageCount}.");
        Assert.Equal(40, TdCount(pdf));      // all 40 paragraphs present, once each
        Assert.Contains("1 0 0 rg", pdf);    // the red spacer's fill color
        Assert.Contains("re f", pdf);        // the spacer paints a filled rectangle (not dropped)
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
    }

    [Fact]
    public void Indented_multi_page_table_keeps_cells_at_their_inline_offset()
    {
        // `multi-page-allocation-churn` PR #211 review [P1] — the cross-page TableMeasurementCache
        // is populated by the SUBTREE-extent measure at a placeholder inline offset of 0; it must
        // NOT feed the EMIT path, whose buffered cell/caption fragments bake in the table's REAL
        // inline offset (FlushTo rebases only the block axis). An INDENTED (here padding-left:200px
        // → +150 pt) table that fragments across pages must paint its cell + caption text at the
        // indented x on EVERY page, not at x=0 (the dry-run position a wrongly-reused offset-0 cache
        // would produce).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var sb = new StringBuilder("<!DOCTYPE html><html><body>"
            + "<div style='padding-left:200px'><table><caption>CaptionText</caption>");
        for (var i = 0; i < 70; i++) sb.Append("<tr><td>Cell").Append(i).Append("</td></tr>");
        sb.Append("</table></div></body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString(), opts);
        var pdf = Latin1(result.Pdf);

        Assert.True(result.PageCount >= 2,
            $"the 70-row table must fragment so the resume path runs; got {result.PageCount}.");
        // Every text-positioning Td across all pages sits at the indented x (≈ 72 pt page margin +
        // 150 pt indent = 222), comfortably right of the un-indented ~72 pt the bug would yield.
        var xs = System.Text.RegularExpressions.Regex.Matches(pdf, @"(-?[0-9.]+) -?[0-9.]+ Td");
        Assert.True(xs.Count > 0, "expected cell text to paint.");
        foreach (System.Text.RegularExpressions.Match m in xs)
        {
            var x = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(x >= 200.0,
                $"a Td landed at x={x:F1} — table content is being painted at the dry-run offset, "
                + "not the indented inline position (cross-page measure cache leaked into the emit path).");
        }
    }

    [Fact]
    public void Single_tall_paragraph_splits_its_lines_across_pages()
    {
        // `inline-only-block-line-splitting` — ONE paragraph taller than a whole page now SLICES
        // its own wrapped lines across pages instead of force-overflowing a single page (the
        // residual the broad prose-pagination deferral left behind). 301 one-word lines (via
        // <br>) span several A4 pages; each line is emitted EXACTLY ONCE — no loss, no duplication.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var sb = new StringBuilder("<!DOCTYPE html><html><body><p>");
        for (var i = 0; i < 300; i++) sb.Append("L").Append(i).Append("<br>");
        sb.Append("L300</p></body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString(), opts);
        var pdf = Latin1(result.Pdf);
        var perPage = TdCountsPerPage(pdf);

        Assert.True(result.PageCount >= 2,
            $"a paragraph taller than a page must split its lines across pages; got {result.PageCount}.");
        Assert.Equal(301, TdCount(pdf));        // all 301 lines, once each — no loss / duplication
        Assert.Equal(301, perPage.Sum());       // distributed across the pages
        Assert.True(perPage.Count >= 2, $"lines must land on multiple pages; got [{string.Join(",", perPage)}].");
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
    }

    [Fact]
    public void A_tall_inline_block_bearing_paragraph_splits_its_lines_across_pages()
    {
        // inline-only-block-line-splitting (atomics) — a single inline-only block mixing text with
        // inline-block atomics, taller than a page, now SLICES its lines across pages (re-basing each
        // atomic to the page its line lands on) instead of force-overflowing the whole block. Every
        // inline-block background renders EXACTLY ONCE across the pages — no atomic lost or duplicated.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var sb = new StringBuilder("<!DOCTYPE html><html><body><div style=\"width:30px\">");
        for (var i = 0; i < 150; i++)
            sb.Append("x <span style=\"display:inline-block;width:8px;height:8px;background-color:#3366cc\"></span> ");
        sb.Append("</div></body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString(), opts);
        var fills = CountOccurrences(Latin1(result.Pdf), " re f");

        Assert.True(result.PageCount >= 2,
            $"the atomic-bearing block must split its lines across pages; got {result.PageCount} page(s).");
        Assert.Equal(150, fills);   // every inline-block background, once — no atomic lost / duplicated
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
    }

    [Fact]
    public void A_tall_padded_paragraph_splits_with_box_decoration_break_slice_padding()
    {
        // inline-only-block-line-splitting (box-decoration-break: slice) — a single inline-only block
        // with block-axis PADDING, taller than a page, now SLICES its lines across pages instead of
        // force-overflowing. The top padding sits above the content on the FIRST page only (so the first
        // line is pushed down by it) and no line is lost. (A block-axis BORDER still force-overflows —
        // the cut-edge border painting is the documented residual.)
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        string Html(string divStyle)
        {
            var b = new StringBuilder("<!DOCTYPE html><html><body><div style=\"" + divStyle + "\">");
            for (var i = 0; i < 300; i++) b.Append('L').Append(i).Append("<br>");
            b.Append("L300</div></body></html>");
            return b.ToString();
        }

        var padded = HtmlPdf.ConvertDetailed(Html("margin:0;padding-top:30px;padding-bottom:30px"), opts);
        var plain = HtmlPdf.ConvertDetailed(Html("margin:0"), opts);
        var paddedPdf = Latin1(padded.Pdf);

        Assert.True(padded.PageCount >= 2, $"the padded block must split its lines; got {padded.PageCount} page(s).");
        Assert.Equal(301, TdCount(paddedPdf));   // every line, once — no loss / duplication
        Assert.DoesNotContain(padded.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
        // box-decoration-break: slice — the first page's first line sits one top-padding lower than the
        // un-padded block's. 30px = 22.5pt; PDF y is bottom-up, so the padded baseline is 22.5pt smaller.
        Assert.Equal(FirstTd(Latin1(plain.Pdf)).Y - 22.5, FirstTd(paddedPdf).Y, precision: 1);
    }

    [Fact]
    public void Single_tall_paragraph_line_split_honors_widows()
    {
        // CSS Fragmentation L3 §4.2 — the LAST page of a split paragraph keeps at least `widows`
        // lines (read off the paragraph's OWN computed value — per-paragraph orphans/widows). A
        // 150-line paragraph with widows:20 pulls lines back from the penultimate page so the
        // final page carries >= 20, without losing any line.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var sb = new StringBuilder("<!DOCTYPE html><html><body><p style=\"widows:20\">");
        for (var i = 0; i < 149; i++) sb.Append("L").Append(i).Append("<br>");
        sb.Append("L149</p></body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString(), opts);
        var pdf = Latin1(result.Pdf);
        var perPage = TdCountsPerPage(pdf);

        Assert.True(result.PageCount >= 2, $"the paragraph must split; got {result.PageCount}.");
        Assert.Equal(150, TdCount(pdf));        // no line lost to the widows pull-back
        Assert.True(perPage[^1] >= 20,
            $"widows:20 must keep >= 20 lines on the last page; got [{string.Join(",", perPage)}].");
    }

    [Fact]
    public void Single_tall_paragraph_line_split_orphans_bounds_the_widows_pullback()
    {
        // CSS Fragmentation L3 §4.2 — when `widows` would pull so many lines onto the last page
        // that the PENULTIMATE page drops below `orphans`, orphans wins (the pull-back is bounded).
        // A 60-line paragraph (≈ 2 pages) with widows:40 + orphans:30: the naive widows pull-back
        // would leave only 60-40 = 20 lines on page 1 (< orphans 30), so it's declined — page 1
        // keeps its full ~48 lines. Without the orphans bound, page 1 would be 20.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var sb = new StringBuilder("<!DOCTYPE html><html><body><p style=\"widows:40;orphans:30\">");
        for (var i = 0; i < 59; i++) sb.Append("L").Append(i).Append("<br>");
        sb.Append("L59</p></body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString(), opts);
        var perPage = TdCountsPerPage(Latin1(result.Pdf));

        Assert.True(result.PageCount >= 2, $"the paragraph must split; got {result.PageCount}.");
        Assert.Equal(60, TdCount(Latin1(result.Pdf)));   // no line lost
        Assert.True(perPage[0] >= 30,
            $"orphans:30 must bound the widows pull-back — page 1 must keep >= 30 lines; got "
            + $"[{string.Join(",", perPage)}].");
    }

    [Fact]
    public void Nested_div_wrapped_tall_paragraph_splits_its_lines_across_pages()
    {
        // The PRIMARY line-split path is the recursive wrapper (EmitInlineOnlyBlockInRecursion-
        // Splitting). A paragraph nested inside a <div> (reached through the recursion, not the
        // top-level dispatch) taller than a page must still slice its lines across pages with no
        // loss — the nested-block continuation propagates up through the recursive wrapper.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var sb = new StringBuilder("<!DOCTYPE html><html><body><div><section><p>");
        for (var i = 0; i < 200; i++) sb.Append("N").Append(i).Append("<br>");
        sb.Append("N200</p></section></div></body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString(), opts);

        Assert.True(result.PageCount >= 2,
            $"the nested paragraph must split across pages; got {result.PageCount}.");
        Assert.Equal(201, TdCount(Latin1(result.Pdf)));   // all 201 lines, once each — no loss / dup
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
    }

    [Fact]
    public void Padded_tall_paragraph_now_slices_its_lines_across_pages()
    {
        // inline-only-block-line-splitting (box-decoration-break: slice) — a tall paragraph WITH
        // block-axis padding now SLICES its lines across pages (the top padding on the first slice, the
        // bottom on the last) instead of force-overflowing on the whole-block fallback. Every line still
        // emits, no overflow is truncated, and it paginates like the chrome-free version. (A block-axis
        // BORDER still force-overflows — the cut-edge border painting is the remaining residual.)
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        string Doc(string pStyle)
        {
            var sb = new StringBuilder($"<!DOCTYPE html><html><body><p style=\"{pStyle}\">");
            for (var i = 0; i < 99; i++) sb.Append("P").Append(i).Append("<br>");
            return sb.Append("P99</p></body></html>").ToString();
        }

        var padded = HtmlPdf.ConvertDetailed(Doc("padding:20px"), opts);
        var plain = HtmlPdf.ConvertDetailed(Doc(""), opts);

        Assert.Equal(100, TdCount(Latin1(padded.Pdf)));   // padded: all 100 lines present (no loss)
        Assert.Equal(100, TdCount(Latin1(plain.Pdf)));    // plain: all 100 lines present
        Assert.True(plain.PageCount >= 2,
            $"the chrome-free paragraph should slice across pages; got {plain.PageCount}.");
        Assert.True(padded.PageCount >= 2,
            $"the padded paragraph must now slice across pages too; got {padded.PageCount}.");
        Assert.DoesNotContain(padded.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
    }

    // Non-block pagination — regression lock-in (multi-page-driver.md, post-cycle-8 audit).
    // These three modes were ALREADY paginating through the driver loop (the table /
    // multicol layouters propagate their continuations through BlockLayouter's dispatch,
    // exactly like block content) but had no facade-level coverage — the cycle-8 probe's
    // "they don't paginate" reading was a false negative from auto-height cell/item collapse
    // (a tall-looking <td><div style="height:200px">…</div></td> renders ~one text line tall,
    // so the test table never exceeded one page). These tests pin the real behavior with
    // genuinely page-exceeding content so a regression can't slip through silently.

    [Fact]
    public void Table_with_many_rows_splits_across_pages_without_losing_rows()
    {
        // 80 natural-height rows (~19px each ⇒ ~1520px) exceed an A4 content box (~931px), so
        // the table splits at ROW boundaries across pages (TableLayouter → TableContinuation,
        // propagated by BlockLayouter). Each body cell is "BB" (2 glyphs) — counting the
        // 2-glyph runs across the whole PDF proves every row emitted exactly once (no row
        // dropped at the page boundary, none duplicated). A clean row split emits no overflow.
        var sb = new StringBuilder("<!DOCTYPE html><html><body><table>");
        for (var i = 0; i < 80; i++) sb.Append("<tr><td>BB</td></tr>");
        sb.Append("</table></body></html>");

        var result = HtmlPdf.ConvertDetailed(
            sb.ToString(), new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.True(result.PageCount >= 2, $"expected the table to paginate, got {result.PageCount} page(s)");
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
        Assert.Equal(80, GlyphRunCountOfLength(Latin1(result.Pdf), 2));   // 80 body cells, none lost
    }

    [Fact]
    public void Table_thead_tfoot_repeat_on_every_page()
    {
        // A split table re-emits its <thead> + <tfoot> on each page (CSS Tables; TableLayouter
        // EmitHeaderRows / EmitFooterRows gated by the continuation's RepeatHead / RepeatFoot).
        // Distinct glyph-run lengths make the three row kinds countable: header "HHH" (3),
        // footer "FFFF" (4), body "BB" (2). header-run-count == footer-run-count == PageCount
        // proves the header + footer appear once per page; body-run-count == 80 proves no body
        // row is lost to the repetition.
        var sb = new StringBuilder("<!DOCTYPE html><html><body><table>");
        sb.Append("<thead><tr><td>HHH</td></tr></thead>");
        sb.Append("<tfoot><tr><td>FFFF</td></tr></tfoot><tbody>");
        for (var i = 0; i < 80; i++) sb.Append("<tr><td>BB</td></tr>");
        sb.Append("</tbody></table></body></html>");

        var result = HtmlPdf.ConvertDetailed(
            sb.ToString(), new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);

        Assert.True(result.PageCount >= 2, $"expected the table to paginate, got {result.PageCount} page(s)");
        Assert.Equal(result.PageCount, GlyphRunCountOfLength(pdf, 3));   // <thead> once per page
        Assert.Equal(result.PageCount, GlyphRunCountOfLength(pdf, 4));   // <tfoot> once per page
        Assert.Equal(80, GlyphRunCountOfLength(pdf, 2));                 // every body row, once
    }

    [Fact]
    public void Multicol_content_paginates_across_pages_without_loss()
    {
        // A tall multicol container (column-count:2) flows its overflow onto the next page
        // (MulticolLayouter → MulticolContinuation, propagated by BlockLayouter). 120 "MM"
        // (2-glyph) paragraphs far exceed two A4 columns' worth of one page; counting the
        // 2-glyph runs proves every paragraph emitted exactly once across the pages.
        var sb = new StringBuilder("<!DOCTYPE html><html><body><div style=\"column-count:2\">");
        for (var i = 0; i < 120; i++) sb.Append("<div>MM</div>");
        sb.Append("</div></body></html>");

        var result = HtmlPdf.ConvertDetailed(
            sb.ToString(), new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.True(result.PageCount >= 2, $"expected multicol to paginate, got {result.PageCount} page(s)");
        Assert.Equal(120, GlyphRunCountOfLength(Latin1(result.Pdf), 2));   // every paragraph, once
    }

    [Fact]
    public void Flex_column_paginates_at_item_boundaries_without_loss()
    {
        // Non-block-pagination arc — a tall `flex-direction: column` container
        // splits at flex-ITEM boundaries across pages (FlexLayouter main-axis
        // item split → FlexContinuation.ItemIndex, propagated by BlockLayouter
        // with the natural-size / page-budget dual-input). 12 items × 200px
        // (2400px) far exceed an A4 content box (~931px). Each item paints a
        // background fill (one " re f" rect); counting the fills proves every
        // item emitted exactly once across the pages (FlexLayouter renders item
        // BOX geometry — laying out item inner CONTENT is a separate flex gap —
        // so the background-rect count is the robust per-item signal here). A
        // clean item split emits no overflow.
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><body><div style=\"display:flex;flex-direction:column\">");
        for (var i = 0; i < 12; i++)
            sb.Append("<div style=\"height:200px;background-color:#3366cc\"></div>");
        sb.Append("</div></body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString());
        var fills = CountOccurrences(Latin1(result.Pdf), " re f");

        Assert.True(result.PageCount >= 2, $"expected the column flex to paginate, got {result.PageCount} page(s)");
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
        Assert.Equal(12, fills);   // every item's background, once (no item lost / duplicated)
    }

    [Fact]
    public void Flex_column_reverse_paginates_at_item_boundaries_without_loss()
    {
        // Backlog #4 — a tall `flex-direction: column-reverse` now PAGINATES at item
        // boundaries (in VISUAL / reverse-DOM order): the emission reverses the item
        // sequence + reuses the forward column item-split. 12 × 200px items (2400px)
        // far exceed an A4 content box; each item's background fill renders exactly
        // once across the pages (no item lost / duplicated), no overflow.
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><body><div style=\"display:flex;flex-direction:column-reverse\">");
        for (var i = 0; i < 12; i++)
            sb.Append("<div style=\"height:200px;background-color:#3366cc\"></div>");
        sb.Append("</div></body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString());
        var fills = CountOccurrences(Latin1(result.Pdf), " re f");

        Assert.True(result.PageCount >= 2, $"expected the column-reverse flex to paginate, got {result.PageCount} page(s)");
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
        Assert.Equal(12, fills);   // every item's background, once
    }

    [Fact]
    public void Grid_with_explicit_template_rows_paginates_at_row_boundaries_without_loss()
    {
        // Non-block-pagination arc — a tall grid splits at GRID-ROW boundaries
        // across pages (GridLayouter → GridContinuation, already propagated by
        // BlockLayouter's dispatch + IsPaginatableGrid). Like tables / multicol,
        // grid pagination was ALREADY wired but untested at the facade. 12 rows
        // × 200px (2400px) exceed an A4 content box (~931px). Each cell paints a
        // background fill; counting the fills proves every row's cell emitted
        // exactly once across the pages.
        //
        // NOTE: this uses EXPLICIT grid-template-rows. Grids sized by IMPLICIT
        // (auto-placed / grid-auto-rows) rows ALSO paginate now — see
        // Grid_with_implicit_auto_rows_paginates_across_pages_without_loss + the
        // fr-column / root-child / auto-fill variants below (PreMeasureGridRowExtent
        // measures implicit tracks via GridSizing.Resolve).
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><body><div style=\"display:grid;grid-template-columns:100px;grid-template-rows:");
        for (var i = 0; i < 12; i++) sb.Append("200px ");
        sb.Append("\">");
        for (var i = 0; i < 12; i++) sb.Append("<div style=\"background-color:#3366cc\"></div>");
        sb.Append("</div></body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString());
        var fills = CountOccurrences(Latin1(result.Pdf), " re f");

        Assert.True(result.PageCount >= 2, $"expected the grid to paginate, got {result.PageCount} page(s)");
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
        Assert.Equal(12, fills);   // every grid cell, once
    }

    [Fact]
    public void Grid_with_implicit_auto_rows_paginates_across_pages_without_loss()
    {
        // Non-block-pagination completion — a grid whose rows come from IMPLICIT
        // tracks (grid-auto-rows + auto-placement, NOT grid-template-rows) now
        // paginates too. The wrapper pre-measure (PreMeasureGridRowExtent) used
        // to sum only template rows, so an auto-row grid stayed chrome-height +
        // never overflowed; now GridSizing.Resolve's implicit-track extent makes
        // the wrapper overflow + the (already-wired) grid pagination engages.
        // 12 implicit 200px rows (2400px) exceed an A4 content box (~931px).
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><body><div style=\"display:grid;grid-template-columns:100px;grid-auto-rows:200px\">");
        for (var i = 0; i < 12; i++) sb.Append("<div style=\"background-color:#3366cc\"></div>");
        sb.Append("</div></body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString());
        var fills = CountOccurrences(Latin1(result.Pdf), " re f");

        Assert.True(result.PageCount >= 2, $"expected the auto-row grid to paginate, got {result.PageCount} page(s)");
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
        Assert.Equal(12, fills);   // every implicit-row cell, once
    }

    [Fact]
    public void Grid_with_fr_column_and_implicit_rows_paginates()
    {
        // Non-block-pagination completion — an implicit-row grid whose COLUMN is
        // `1fr` paginates too. The fr column resolves against the (definite) page
        // width; the implicit-row extent (independent of the column track) drives
        // the wrapper overflow + pagination. 12 implicit 200px rows ⇒ ≥ 2 pages.
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><body><div style=\"display:grid;grid-template-columns:1fr;grid-auto-rows:200px\">");
        for (var i = 0; i < 12; i++) sb.Append("<div style=\"background-color:#3366cc\"></div>");
        sb.Append("</div></body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString());
        var fills = CountOccurrences(Latin1(result.Pdf), " re f");

        Assert.True(result.PageCount >= 2, $"expected the fr-column auto-row grid to paginate, got {result.PageCount} page(s)");
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
        Assert.Equal(12, fills);   // every cell, once
    }

    [Fact]
    public void Grid_auto_fill_columns_does_not_falsely_paginate()
    {
        // PR-#181 review P1 — the implicit-row pre-measure must resolve the SAME
        // column count the real dispatch will. With `repeat(auto-fill, 100px)` in
        // an ~602px A4 content box, 6 columns resolve, so 6 items occupy ONE 200px
        // row (fits one page). If the pre-measure used a fake width of 1 it would
        // resolve 1 column ⇒ 6 rows × 200 = 1200px ⇒ a FALSE page break. The fix
        // threads the real content inline size, so this stays a single page.
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><body><div style=\"display:grid;grid-template-columns:repeat(auto-fill,100px);grid-auto-rows:200px\">");
        for (var i = 0; i < 6; i++) sb.Append("<div style=\"background-color:#3366cc\"></div>");
        sb.Append("</div></body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString());

        Assert.Equal(1, result.PageCount);   // 6 cols × 1 row fits — NO false pagination
        Assert.Equal(6, CountOccurrences(Latin1(result.Pdf), " re f"));   // all 6 cells
    }

    [Fact]
    public void Grid_with_many_implicit_rows_paginates_at_scale_without_loss()
    {
        // PR-#181 review P2 — the implicit-row pre-measure now runs a full
        // GridSizing.Resolve (threaded with the cancellation token). A large
        // auto-row grid must still paginate correctly + non-lossily (scale sanity
        // for the dry-run). 60 implicit 50px rows (3000px) ⇒ several pages.
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><body><div style=\"display:grid;grid-template-columns:100px;grid-auto-rows:50px\">");
        for (var i = 0; i < 60; i++) sb.Append("<div style=\"background-color:#3366cc\"></div>");
        sb.Append("</div></body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString());

        Assert.True(result.PageCount >= 3, $"expected several pages, got {result.PageCount}");
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
        Assert.Equal(60, CountOccurrences(Latin1(result.Pdf), " re f"));   // every cell, once
    }

    [Fact]
    public void Grid_as_root_child_with_implicit_rows_paginates_via_outer_dispatch()
    {
        // Non-block-pagination completion — an implicit-row grid that is the
        // ROOT's direct child (here `body` itself is the grid) routes through
        // BlockLayouter's OUTER grid dispatch + pre-measure, not the recursive
        // one (mirrors the flex P1 outer-vs-recursion distinction). The Task-1
        // pre-measure fix covers both sites, so a body-level auto-row grid
        // taller than the page paginates too. 12 implicit 200px rows ⇒ ≥ 2 pages.
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><body style=\"display:grid;grid-template-columns:100px;grid-auto-rows:200px\">");
        for (var i = 0; i < 12; i++) sb.Append("<div style=\"background-color:#3366cc\"></div>");
        sb.Append("</body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString());
        var fills = CountOccurrences(Latin1(result.Pdf), " re f");

        Assert.True(result.PageCount >= 2, $"expected the root-child auto-row grid to paginate, got {result.PageCount} page(s)");
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
        Assert.Equal(12, fills);   // every cell, once
    }

    [Fact]
    public void Flex_column_on_the_root_child_paginates_via_the_outer_dispatch()
    {
        // PR-#180 review P1 — the ROOT's direct child being flex (here `body`
        // itself is the column flex container) routes through BlockLayouter's
        // OUTER flex dispatch, not the recursive one. Before the fix that path
        // clamped the column main-size to the page budget → flex-shrink shrank
        // the items → AllDone (one page). The dual-input fix makes it split.
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><body style=\"display:flex;flex-direction:column\">");
        for (var i = 0; i < 12; i++)
            sb.Append("<div style=\"height:200px;background-color:#3366cc\"></div>");
        sb.Append("</body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString());
        var fills = CountOccurrences(Latin1(result.Pdf), " re f");

        Assert.True(result.PageCount >= 2, $"expected the root-child column flex to paginate, got {result.PageCount} page(s)");
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
        Assert.Equal(12, fills);   // every item, once
    }

    [Fact]
    public void Flex_column_item_text_content_renders()
    {
        // Flex item CONTENT layout — a `flex-direction: column` with text items
        // now renders each item's inner TEXT (FlexLayouter previously emitted
        // only the item BOX geometry, dropping the content entirely). 10 items,
        // each "AA" (2 glyphs); all 10 glyph runs must appear in the PDF.
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><body><div style=\"display:flex;flex-direction:column\">");
        for (var i = 0; i < 10; i++) sb.Append("<div>AA</div>");
        sb.Append("</div></body></html>");

        var result = HtmlPdf.ConvertDetailed(
            sb.ToString(), new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.Equal(10, GlyphRunCountOfLength(Latin1(result.Pdf), 2));   // every item's text, once
    }

    [Fact]
    public void Flex_row_item_text_content_renders()
    {
        // Row-direction parity — a `flex-direction: row` lays out each item's
        // inner text too. 3 items, each "RR" (2 glyphs), with explicit widths
        // so they don't collapse; all 3 runs render.
        const string html =
            "<!DOCTYPE html><html><body><div style=\"display:flex\">"
            + "<div style=\"width:80px\">RR</div>"
            + "<div style=\"width:80px\">RR</div>"
            + "<div style=\"width:80px\">RR</div>"
            + "</div></body></html>";

        var result = HtmlPdf.ConvertDetailed(
            html, new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.Equal(3, GlyphRunCountOfLength(Latin1(result.Pdf), 2));
    }

    [Fact]
    public void Flex_column_text_items_paginate_and_render_all_content()
    {
        // Content rendering + the column item-split cooperate: 25 explicit-
        // height (50px ⇒ 1250px) column-flex items, each carrying text "CC"
        // (2 glyphs), paginate across pages AND render their text — every
        // item's text follows its item to the correct page and survives the
        // split exactly once (no loss / duplication). The content flush uses
        // each committed item's RE-ANCHORED offset, so resumed-page items paint
        // their text in the right place.
        //
        // Explicit heights here (the auto-height content-sized variant is the
        // separate Flex_column_auto_height_text_items_paginate test below).
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><body><div style=\"display:flex;flex-direction:column\">");
        for (var i = 0; i < 25; i++) sb.Append("<div style=\"height:50px\">CC</div>");
        sb.Append("</div></body></html>");

        var result = HtmlPdf.ConvertDetailed(
            sb.ToString(), new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.True(result.PageCount >= 2, $"expected the column flex to paginate, got {result.PageCount} page(s)");
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
        Assert.Equal(25, GlyphRunCountOfLength(Latin1(result.Pdf), 2));   // every item's text, once
    }

    [Fact]
    public void Flex_column_auto_height_text_items_paginate_via_content_aware_premeasure()
    {
        // Backlog #7 — an AUTO-height `flex-direction: column` whose items are
        // content-sized (each ~one text line, NO explicit height) now PAGINATES:
        // the content-aware PreMeasureFlexMainExtent measures the items' content
        // so the wrapper overflows the page + the (paginatable-flex) split
        // engages (PR #182 left this deferred — the pre-measure summed declared
        // heights only). 80 items, each "DD" (2 glyphs); every item's text
        // survives the split exactly once.
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><body><div style=\"display:flex;flex-direction:column\">");
        for (var i = 0; i < 80; i++) sb.Append("<div>DD</div>");
        sb.Append("</div></body></html>");

        var result = HtmlPdf.ConvertDetailed(
            sb.ToString(), new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.True(result.PageCount >= 2, $"expected the auto-height column flex to paginate, got {result.PageCount} page(s)");
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
        Assert.Equal(80, GlyphRunCountOfLength(Latin1(result.Pdf), 2));   // every item's text, once
    }

    [Fact]
    public void Grid_auto_rows_size_to_text_content_no_zero_sized_warning()
    {
        // Grid CONTENT-sized rows — `grid-auto-rows: auto` rows now size to
        // their cells' content. 4 text cells "HH" (2 glyphs) on one page: every
        // cell renders, and the rows are no longer zero-sized (no
        // LAYOUT-GRID-ZERO-SIZED-CELL-CONTENT-SKIPPED-001). Before the fix the
        // rows collapsed to 0 + emitted that warning.
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><body><div style=\"display:grid;grid-template-columns:100px;grid-auto-rows:auto\">");
        for (var i = 0; i < 4; i++) sb.Append("<div>HH</div>");
        sb.Append("</div></body></html>");

        var result = HtmlPdf.ConvertDetailed(
            sb.ToString(), new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.Equal(1, result.PageCount);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.LayoutGridZeroSizedCellContentSkipped001);
        Assert.Equal(4, GlyphRunCountOfLength(Latin1(result.Pdf), 2));   // every cell's text
    }

    [Fact]
    public void Grid_content_sized_auto_rows_paginate_and_render_all_content()
    {
        // Grid CONTENT-sized rows + pagination — `grid-auto-rows: auto` rows
        // size to content; with enough rows the grid overflows the page and
        // paginates (the content-aware pre-measure grows the wrapper so the
        // already-wired grid pagination engages). 80 text cells "GG" (2 glyphs);
        // every cell survives the page split exactly once, no zero-sized-cell
        // warning, no truncation.
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><body><div style=\"display:grid;grid-template-columns:100px;grid-auto-rows:auto\">");
        for (var i = 0; i < 80; i++) sb.Append("<div>GG</div>");
        sb.Append("</div></body></html>");

        var result = HtmlPdf.ConvertDetailed(
            sb.ToString(), new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.True(result.PageCount >= 2, $"expected the content-sized auto-row grid to paginate, got {result.PageCount} page(s)");
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.LayoutGridZeroSizedCellContentSkipped001);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
        Assert.Equal(80, GlyphRunCountOfLength(Latin1(result.Pdf), 2));   // every cell's text, once
    }

    [Fact]
    public void Flex_column_explicit_height_taller_than_page_paginates_without_spurious_overflow()
    {
        // A `flex-direction: column` with an EXPLICIT height taller than the
        // page paginates correctly (right page count, every item once) but used
        // to emit a SPURIOUS PAGINATION-FORCED-OVERFLOW-001 per page — the block
        // wrapping the rigid explicit-height flex hit BlockLayouter's top-level
        // forced-overflow path even though the flex itself splits cleanly. The
        // warning must NOT fire when no content is actually clipped.
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><body><div style=\"display:flex;flex-direction:column;height:2000px\">");
        for (var i = 0; i < 10; i++)
            sb.Append("<div style=\"height:200px;background-color:#3366cc\"></div>");
        sb.Append("</div></body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString());

        Assert.True(result.PageCount >= 2, $"expected the column flex to paginate, got {result.PageCount} page(s)");
        Assert.Equal(10, CountOccurrences(Latin1(result.Pdf), " re f"));   // every item, once
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PaginationForcedOverflow001);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
    }

    [Fact]
    public void Flex_item_with_translucent_background_and_text_paints_background_once()
    {
        // A flex item with BOTH a TRANSLUCENT background AND inline text renders
        // its text (the new content layout) WITHOUT double-painting the
        // background: the item's flex geometry fragment paints the background;
        // the inline-only content fragment (box == the item) paints text only.
        // A translucent fill makes the regression observable — before the
        // SuppressBoxDecoration fix the `/ca` alpha band painted twice (= a
        // visibly darker box), which a single fill-op count locks out.
        const string html = "<!DOCTYPE html><html><body><div style=\"display:flex;flex-direction:column\">"
            + "<div style=\"background-color:rgba(51,102,204,0.5)\">AA</div></div></body></html>";

        var result = HtmlPdf.ConvertDetailed(
            html, new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);

        Assert.Equal(1, CountOccurrences(pdf, " re f"));            // background fill painted ONCE
        Assert.Equal(1, CountOccurrences(pdf, "/ca 0.501961"));    // alpha band set ONCE (not doubled)
        Assert.Equal(1, GlyphRunCountOfLength(pdf, 2));            // text rendered ONCE
    }

    [Fact]
    public void Flex_item_with_tall_nested_column_flex_renders_all_content_atomically()
    {
        // PR-#182 review P1 — a flex ITEM whose content is a TALL nested
        // column-flex must render ALL of it: the nested measurement DISCARDS
        // the layout result, so an un-suppressed nested flex split would
        // PageComplete(FlexContinuation) and silently drop the deferred items.
        // disableFlexPagination makes the nested flex atomic. 5 × 400px items
        // (2000px) exceed the page; all 5 backgrounds must render exactly once.
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><body><div style=\"display:flex\">"
            + "<div style=\"width:200px\"><div style=\"display:flex;flex-direction:column\">");
        for (var i = 0; i < 5; i++)
            sb.Append("<div style=\"height:400px;background-color:#3366cc\"></div>");
        sb.Append("</div></div></div></body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString());

        Assert.Equal(5, CountOccurrences(Latin1(result.Pdf), " re f"));   // every nested item, once
    }

    [Fact]
    public void Grid_cell_with_tall_nested_column_flex_renders_all_content_atomically()
    {
        // PR-#182 review P1 — same for a GRID CELL whose content is a tall
        // nested column-flex (DispatchGridItemContents discards the result too).
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><body>"
            + "<div style=\"display:grid;grid-template-columns:200px;grid-template-rows:500px\">"
            + "<div><div style=\"display:flex;flex-direction:column\">");
        for (var i = 0; i < 5; i++)
            sb.Append("<div style=\"height:400px;background-color:#3366cc\"></div>");
        sb.Append("</div></div></div></body></html>");

        var result = HtmlPdf.ConvertDetailed(sb.ToString());

        Assert.Equal(5, CountOccurrences(Latin1(result.Pdf), " re f"));   // every nested item, once
    }

    [Fact]
    public void Flex_item_content_diagnostics_surface_once()
    {
        // PR-#182 review P2 — flex item content diagnostics are no longer
        // suppressed (they were measured with a null sink). A flex item with an
        // unsupported atomic inline inside it surfaces
        // LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001 once (buffered during item
        // measurement, flushed when the item commits). Uses `inline-flex` — an atomic
        // kind that STILL skips (inline-block now lays out, inline-atomic-boxes cycle).
        const string html =
            "<!DOCTYPE html><html><body><div style=\"display:flex;flex-direction:column\">"
            + "<div>text <span style=\"display:inline-flex\">X</span></div>"
            + "</div></body></html>";

        var result = HtmlPdf.ConvertDetailed(
            html, new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.LayoutInlineAtomicNotSupported001);
    }

    [Fact]
    public void Inline_block_lays_out_and_paints_its_decoration()
    {
        // Inline-atomic-boxes cycle (inline-block first cut) — a `display: inline-block`
        // span in a paragraph lays out end-to-end: it does NOT surface
        // LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001 (it's no longer skipped) and its
        // background decoration band paints a filled rectangle.
        const string html =
            "<!DOCTYPE html><html><body><p>before "
            + "<span style=\"display:inline-block;width:30px;height:20px;"
            + "background-color:#3366cc\">X</span> after</p></body></html>";

        var result = HtmlPdf.ConvertDetailed(
            html, new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.DoesNotContain(result.Warnings,
            d => d.Code == DiagnosticCodes.LayoutInlineAtomicNotSupported001);
        // The inline-block's background band paints one filled rectangle.
        Assert.Contains("re f", Latin1(result.Pdf));
    }

    [Fact]
    public void Text_align_center_and_right_shift_inline_content()
    {
        // Body text-align cycle — center / right shift the glyph line right of the default
        // (left/start), so the rendered PDF differs from a left-aligned one. Pre-cycle text-align
        // had no effect on inline content (the line-align factor was never set).
        static byte[] Render(string align) => HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><div style=\"text-align:" + align + "\">A</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        var left = Latin1(Render("left"));
        var center = Latin1(Render("center"));
        var right = Latin1(Render("right"));

        Assert.NotEqual(left, center);   // center shifts the line
        Assert.NotEqual(left, right);    // right shifts the line further
        Assert.NotEqual(center, right);
    }

    [Fact]
    public void Body_text_align_inherits_to_child_div_and_shifts_line_by_exact_geometry()
    {
        // Post-PR-#191 review P3 — text-align is an INHERITED property, so declaring it on <body> must
        // reach the child <div> that actually emits the line (prior coverage set TextAlign directly on
        // the block, proving nothing about inheritance). And the shift must be the EXACT line-align
        // geometry, not just "different bytes": center shifts the line by HALF the free space, right by
        // ALL of it — so off the same start (left) baseline, the right shift is EXACTLY twice the center
        // shift. That identity holds without hard-coding the page content width or the glyph advance.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double LineX(string bodyStyle) => FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body style=\"" + bodyStyle + "\"><div>A</div></body></html>", opts))).X;

        var start = LineX("");                       // no text-align anywhere → start (control)
        var center = LineX("text-align:center");     // body center, inherited by the div
        var right = LineX("text-align:right");        // body right, inherited by the div

        // Inheritance reached the div: both align values shift the line right of the start baseline.
        Assert.True(center > start + 1.0, $"inherited center should shift right of start: {start} → {center}");
        Assert.True(right > center + 1.0, $"inherited right should shift further than center: {center} → {right}");
        // Exact line-align geometry: center = start + free/2, right = start + free ⇒ right−start = 2·(center−start).
        Assert.Equal(2.0 * (center - start), right - start, precision: 1);
    }

    [Fact]
    public void Direction_rtl_inherits_and_right_aligns_default_start_text()
    {
        // Direction pipeline (PR 2 tasks 4+5) end-to-end through the REAL cascade: `direction` is an
        // INHERITED property (CSS Writing Modes 4 §2.1), so declaring `direction:rtl` on <body> reaches
        // the child <div> that emits the line, and the initial `text-align:start` then RIGHT-aligns it.
        // Off the LTR-start control (left edge), RTL-start shifts right — landing at the SAME right edge
        // as physical `text-align:right` (start resolves to the end/right edge in RTL; both factor 1.0).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double LineX(string bodyStyle) => FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body style=\"" + bodyStyle + "\"><div>A</div></body></html>", opts))).X;

        var ltrStart = LineX("");                  // LTR + start (control) → left edge
        var ltrRight = LineX("text-align:right");  // LTR + right → right edge (the geometry reference)
        var rtlStart = LineX("direction:rtl");     // RTL + INHERITED start → right edge

        // Inheritance reached the div AND start swapped: the RTL line shifts right of the LTR-start control.
        Assert.True(rtlStart > ltrStart + 1.0, $"rtl start should right-align: {ltrStart} → {rtlStart}");
        // start-in-RTL lands at the same right edge as physical right (both align factor 1.0).
        Assert.True(System.Math.Abs(rtlStart - ltrRight) < 1.0,
            $"rtl start should match physical right: rtlStart={rtlStart} ltrRight={ltrRight}");
    }

    [Fact]
    public void Percentage_line_height_inherits_the_declaring_elements_computed_length()
    {
        // CSS Inline 3 §4.2 — a % line-height computes to a length at the DECLARING element's font-size
        // and inherits AS that length. Both children are font-size:20px and wrap identically; the ONLY
        // variable is the PARENT's font-size, which sets the inherited line-height (200% × 20 = 40px vs
        // 200% × 40 = 80px). So the child's inter-line span differs — proving the % resolved at the
        // parent, not re-resolved against the child's own 20px (the old bug gave both 40px → equal spans).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double ChildSpan(string parentFontSize)
        {
            var pdf = Latin1(HtmlPdf.Convert(
                "<!DOCTYPE html><html><body>" +
                $"<div style=\"font-size:{parentFontSize};line-height:200%\">" +
                "<p style=\"font-size:20px;width:30px\">A A A A A A</p></div></body></html>", opts));
            double min = double.MaxValue, max = double.MinValue;
            var n = 0;
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(pdf, @"-?[0-9.]+ (-?[0-9.]+) Td"))
            {
                var y = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (y < min) min = y;
                if (y > max) max = y;
                n++;
            }
            Assert.True(n >= 2, $"expected the child to wrap to ≥2 lines, got {n}");
            return max - min;
        }

        var parent20 = ChildSpan("20px");   // inherited line-height 200% × 20 = 40px
        var parent40 = ChildSpan("40px");   // inherited line-height 200% × 40 = 80px
        Assert.True(parent40 > parent20 + 5.0,
            $"a larger parent font-size must give a taller inherited % line-height: " +
            $"parent20 span={parent20:F1}pt, parent40 span={parent40:F1}pt");
    }

    [Fact]
    public void White_space_break_spaces_breaks_after_every_space()
    {
        // white-space: break-spaces (CSS Text L3 §6.4) adds a soft-wrap opportunity AFTER every
        // preserved space (including between consecutive spaces), and trailing spaces take up width
        // (no hang). So a wide run of spaces in a narrow box wraps across MORE lines than pre-wrap,
        // which only breaks after the whole space sequence (and hangs trailing spaces). Same content +
        // width; isolates the per-space break opportunities the flat-build now synthesizes.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        int Lines(string ws) => TdCount(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><div style=\"width:30px;white-space:" + ws + "\">" +
            "A          A</div></body></html>", opts)));   // A + 10 spaces + A

        var preWrap = Lines("pre-wrap");
        var breakSpaces = Lines("break-spaces");
        Assert.True(breakSpaces > preWrap,
            $"break-spaces ({breakSpaces} lines) should wrap the space run across more lines than " +
            $"pre-wrap ({preWrap} lines)");
    }

    [Fact]
    public void Dir_attribute_rtl_right_aligns_start_text_end_to_end()
    {
        // The `dir="rtl"` HTML attribute maps to `direction: rtl` (HTML §3.2.6.4) through the real
        // cascade/box-builder, so the initial `text-align: start` resolves to the RIGHT edge — landing
        // at the same x as physical `text-align: right`, and well right of an LTR control.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double LineX(string body) => FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" + body + "</body></html>", opts))).X;

        var ltr = LineX("<div dir=\"ltr\">A</div>");           // ltr + start → left edge (control)
        var ltrRight = LineX("<div style=\"text-align:right\">A</div>");  // physical right (reference)
        var rtl = LineX("<div dir=\"rtl\">A</div>");           // rtl + INHERITED start → right edge

        Assert.True(rtl > ltr + 1.0, $"dir=rtl should right-align: ltr={ltr} → rtl={rtl}");
        Assert.True(System.Math.Abs(rtl - ltrRight) < 1.0,
            $"dir=rtl start should match physical right: rtl={rtl} ltrRight={ltrRight}");
    }

    [Fact]
    public void Text_align_match_parent_resolves_against_the_parent_used_value()
    {
        // text-align-match-parent (CSS Text 3 §7.1) end-to-end — the child takes the PARENT's used
        // text-align, resolving start/end against the PARENT's direction. Parent `direction:rtl;
        // text-align:start` → used RIGHT; the child declares its OWN `direction:ltr`, so `match-parent`
        // RIGHT-aligns (parent's start-in-rtl), whereas a plain inherited/declared `start` resolves
        // against the child's ltr → LEFT. Isolates the feature from inheritance AND from the prior
        // fixed-physical-left approximation (both of which would land LEFT here).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double InnerX(string childAlign) => FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"direction:rtl;text-align:start;width:300px\">" +
            $"<div style=\"direction:ltr;text-align:{childAlign}\">A</div></div></body></html>", opts))).X;

        var matchParent = InnerX("match-parent");   // parent start-in-rtl = RIGHT → large x
        var plainStart = InnerX("start");           // child start-in-ltr = LEFT → small x
        Assert.True(matchParent > plainStart + 50.0,
            $"match-parent should align to the parent's used RIGHT ({matchParent:F1}pt), " +
            $"well right of the child's own start/LEFT ({plainStart:F1}pt)");
    }

    [Fact]
    public void Text_align_justify_spreads_words_and_pushes_the_last_word_right()
    {
        // text-align: justify cycle (CSS Text 3 §7.3) — a wrapping paragraph distributes each NON-LAST
        // line's free space across its inter-word gaps. Observable two ways vs the left-aligned control:
        // (a) each justified line is SPLIT into per-word show ops, so there are more Td operators; and
        // (b) a justified line's last word is pushed toward the right edge, so the MAX Td x is larger.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        static string Doc(string align) =>
            "<!DOCTYPE html><html><body><div style=\"width:80px;text-align:" + align + "\">" +
            "A A A A A A A A A A A A</div></body></html>";

        var justify = Latin1(HtmlPdf.Convert(Doc("justify"), opts));
        var left = Latin1(HtmlPdf.Convert(Doc("left"), opts));

        // (a) justify splits non-last lines into per-word segments → strictly more Td operators.
        Assert.True(TdCount(justify) > TdCount(left),
            $"justify should split lines into per-word segments: justify={TdCount(justify)} left={TdCount(left)}");
        // (b) a justified line's last word is pushed right → the rightmost Td x grows past the control.
        Assert.True(MaxTdX(justify) > MaxTdX(left) + 5.0,
            $"justify should push words toward the right edge: justify={MaxTdX(justify)} left={MaxTdX(left)}");
    }

    [Fact]
    public void Text_align_justify_leaves_a_single_line_start_aligned()
    {
        // text-align: justify cycle — the §7.3 last-line exception: the LAST line of a block is NOT
        // justified (it stays start-aligned). A paragraph that fits on ONE line is all-last-line, so
        // even WITH inter-word gaps present it must render byte-identical to left-aligned (no spread).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        byte[] Render(string align) => HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><div style=\"width:600px;text-align:" + align + "\">A A A</div></body></html>",
            opts);

        Assert.Equal(Latin1(Render("left")), Latin1(Render("justify")));
    }

    [Fact]
    public void Text_align_justify_all_justifies_the_single_last_line()
    {
        // text-align: justify-all (CSS Text 3 §7.3, post-PR-#194 task 1) — unlike plain justify (which
        // leaves the LAST line start-aligned, the §7.3 exception), justify-all justifies EVERY line
        // including the last. A one-line paragraph therefore spreads its words to fill the width: more
        // per-word Td segments AND a larger max Td x than left — exactly where plain justify == left.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        static string Doc(string align) =>
            "<!DOCTYPE html><html><body><div style=\"width:600px;text-align:" + align + "\">A A A</div></body></html>";

        var left = Latin1(HtmlPdf.Convert(Doc("left"), opts));
        var justify = Latin1(HtmlPdf.Convert(Doc("justify"), opts));        // single line = last → start-aligned
        var justifyAll = Latin1(HtmlPdf.Convert(Doc("justify-all"), opts)); // last line justifies too

        Assert.Equal(left, justify);   // control: plain justify leaves the single (last) line alone.
        Assert.True(TdCount(justifyAll) > TdCount(left),
            $"justify-all should split the last line into per-word segments: all={TdCount(justifyAll)} left={TdCount(left)}");
        Assert.True(MaxTdX(justifyAll) > MaxTdX(left) + 5.0,
            $"justify-all should push the last line's words right: all={MaxTdX(justifyAll)} left={MaxTdX(left)}");
    }

    [Fact]
    public void Text_align_justify_all_distributes_on_a_line_carrying_an_inline_atomic()
    {
        // text-align: justify-all on an atomic-bearing line (post-PR-#194 task 1) — pre-fix a line carrying
        // an inline atomic stayed START-aligned (the painter bailed). Now the text justifies AROUND the
        // atomic: a one-line paragraph with an (empty) inline-block + inter-word gaps, under justify-all
        // (a single line is the last line, so plain justify wouldn't apply — see the non-last-line test
        // below for plain justify), pushes its last word toward the right edge (vs left).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        static string Doc(string align) =>
            "<!DOCTYPE html><html><body><div style=\"width:600px;text-align:" + align + "\">"
            + "A <span style=\"display:inline-block;width:12px;height:8px\"></span> B C</div></body></html>";

        var left = Latin1(HtmlPdf.Convert(Doc("left"), opts));
        var justifyAll = Latin1(HtmlPdf.Convert(Doc("justify-all"), opts));

        Assert.True(MaxTdX(justifyAll) > MaxTdX(left) + 5.0,
            $"justify-all should distribute on a line with an inline atomic: all={MaxTdX(justifyAll)} left={MaxTdX(left)}");
    }

    [Fact]
    public void Justify_all_justifies_an_internal_br_terminated_line()
    {
        // PR-3 task 9 (CSS Text 3 §7.3) — `justify-all` justifies EVERY line including a forced-break
        // (<br>)-terminated one; plain `justify` leaves a <br>-terminated line start-aligned (the §7.3
        // exception, like the last line). Here the first line "A A A" ends in a <br> (an INTERNAL forced
        // break, NOT the last line — "Z" is), so its words spread to the right edge only under justify-all.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        static string Doc(string align) =>
            "<!DOCTYPE html><html><body><div style=\"width:600px;text-align:" + align + "\">A A A<br>Z</div></body></html>";

        var justify = Latin1(HtmlPdf.Convert(Doc("justify"), opts));
        var justifyAll = Latin1(HtmlPdf.Convert(Doc("justify-all"), opts));

        // Under plain justify the <br> line stays start-aligned (§7.3 forced-break exception); under
        // justify-all it justifies — so its words split into per-word Td segments AND its last word is
        // pushed toward the right edge.
        Assert.True(TdCount(justifyAll) > TdCount(justify),
            $"justify-all should split the internal <br> line into per-word segments: all={TdCount(justifyAll)} justify={TdCount(justify)}");
        Assert.True(MaxTdX(justifyAll) > MaxTdX(justify) + 5.0,
            $"justify-all should push the <br> line's last word right: all={MaxTdX(justifyAll)} justify={MaxTdX(justify)}");
    }

    [Fact]
    public void Justify_keeps_text_on_the_layout_pinned_baseline()
    {
        // text-align: justify pinned-baseline (post-PR-#195 review P1) — when a justified line carries a
        // baseline-owning inline-block (a CONTENT-bearing one that pins the line's §10.8.1 baseline), the
        // justified TEXT must paint on that SAME pinned baseline as the atomic, NOT a recomputed centred
        // one. So under justify-all every glyph on the line (the body words + the inline-block's content
        // "I") shares ONE baseline Y → a near-zero Y spread. Pre-fix the body text dropped to a centred
        // baseline while the atomic stayed pinned, splitting the line into two distinct Ys.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var ys = AllTdY(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><div style=\"width:600px;text-align:justify-all\">L L " +
            "<span style=\"display:inline-block;font-size:40px\">I</span> R R</div></body></html>", opts)));

        Assert.True(ys.Length >= 3, $"expected the body words + the inline-block glyph; got {ys.Length}");
        double min = ys[0], max = ys[0];
        foreach (var y in ys) { if (y < min) min = y; if (y > max) max = y; }
        Assert.True(max - min < 3.0,
            $"justified text must share the inline-block's pinned baseline (spread≈0); got spread={max - min}");
    }

    [Fact]
    public void Text_align_justify_distributes_on_a_non_last_atomic_line()
    {
        // text-align: justify (NOT justify-all) on a NON-LAST atomic-bearing line (post-PR-#195 review P3) —
        // a wrapping paragraph whose first line carries an inline atomic justifies that line (the atomic +
        // following words shift right by the distributed gaps) while the LAST line stays start-aligned. The
        // wrapping non-last lines split into per-word segments + push their last word right vs left-aligned.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        static string Doc(string align) =>
            "<!DOCTYPE html><html><body><div style=\"width:90px;text-align:" + align + "\">"
            + "A A <span style=\"display:inline-block;width:10px;height:8px\"></span> A A A A A A A A A A</div></body></html>";

        var left = Latin1(HtmlPdf.Convert(Doc("left"), opts));
        var justify = Latin1(HtmlPdf.Convert(Doc("justify"), opts));

        Assert.True(TdCount(justify) > TdCount(left),
            $"plain justify should split the non-last (atomic-bearing) lines into per-word segments: justify={TdCount(justify)} left={TdCount(left)}");
        Assert.True(MaxTdX(justify) > MaxTdX(left) + 5.0,
            $"plain justify should push words on the atomic line toward the right edge: justify={MaxTdX(justify)} left={MaxTdX(left)}");
    }

    [Fact]
    public void Inline_block_box_line_height_does_not_shift_its_block_contents_baseline()
    {
        // inline-block last-line baseline real metrics (post-PR-#194 task 2), end-to-end — an inline-block's
        // baseline comes from its CONTENT's last line box, not the inline-block's OWN line-height. The inner
        // block here pins its line-height EXPLICITLY (14px), so its line is 14px regardless of what the
        // inline-block declares — and the surrounding "A" sits at the SAME y whether the inline-block's own
        // line-height is 14px or 40px. (The inner pin matters now that the line-height cycle makes the value
        // reach the computed style AND inherit — without it, a 40px inline-block line-height would inherit
        // to the inner block and legitimately grow its line, which is a different effect than the one tested.)
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double SurroundingTextY(string ibLineHeight) => FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><div>A<span style=\"display:inline-block;line-height:" + ibLineHeight + "\">"
            + "<div style=\"line-height:14px\">x</div></span></div></body></html>", opts))).Y;

        Assert.Equal(SurroundingTextY("14px"), SurroundingTextY("40px"), precision: 2);
    }

    [Fact]
    public void Inline_block_last_line_baseline_reflects_a_font_overriding_span()
    {
        // PR-3 task 9 — an inline-block's baseline comes from its LAST in-flow line box (CSS 2.2 §10.8.1),
        // so a SPAN that overrides the font (a larger font-size ≈ a deeper descender) ON the LAST line
        // deepens the descent and shifts the baseline — and the surrounding "A" aligned to it. Moving the
        // SAME 40px span from an earlier line to the LAST line therefore changes the result:
        // BufferingMeasureSink now scans the last line's DEEPEST run instead of using the box font only.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double SurroundingY(string ibContent) => FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><div>A<span style=\"display:inline-block\">" + ibContent
            + "</span></div></body></html>", opts))).Y;

        // Same glyphs; only WHICH line carries the 40px span differs (line 1 vs the LAST line).
        var deepFirst = SurroundingY("<span style=\"font-size:40px\">y</span><br>x");   // 40px on line 1, 16px last
        var deepLast = SurroundingY("x<br><span style=\"font-size:40px\">y</span>");    // 16px line 1, 40px last

        Assert.True(System.Math.Abs(deepFirst - deepLast) > 1.0,
            $"the LAST line's deepest font should drive the inline-block baseline: deepFirst={deepFirst} deepLast={deepLast}");
    }

    [Fact]
    public void Inline_block_last_line_baseline_reflects_a_line_height_overriding_span()
    {
        // PR #198 review P2 — the last-line deepest-run selection keys on USED DESCENT (line-height AND
        // font-size), not font-size alone: a SAME-font span with a much larger line-height on the LAST line
        // deepens the §10.8.1 descent (more half-leading below the baseline), shifting the inline-block's
        // baseline and the surrounding "A" aligned to it. Moving the same line-height:80px span from line 1
        // to the LAST line therefore changes the result — font-size-only selection (the pre-fix behavior)
        // missed a same-font, larger-line-height run.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double SurroundingY(string ibContent) => FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><div>A<span style=\"display:inline-block\">" + ibContent
            + "</span></div></body></html>", opts))).Y;

        // Same glyphs + same font-size; only WHICH line carries the line-height:80px span differs.
        var tallFirst = SurroundingY("<span style=\"line-height:80px\">y</span><br>x");  // tall lh on line 1
        var tallLast = SurroundingY("x<br><span style=\"line-height:80px\">y</span>");   // tall lh on the LAST line

        Assert.True(System.Math.Abs(tallFirst - tallLast) > 1.0,
            $"the LAST line's used line-height should drive the inline-block baseline: tallFirst={tallFirst} tallLast={tallLast}");
    }

    [Fact]
    public void Vertical_align_top_places_an_inline_block_differently_than_baseline()
    {
        // vertical-align cycle (CSS 2.2 §10.8.1) — end-to-end: a `vertical-align` keyword now resolves
        // through the cascade (was a silent raw passthrough) and the inline-atomic placement consumes it.
        // A short inline-block between two glyphs paints its background band at a DIFFERENT vertical
        // position under `top` (margin-box top at the line top) than under `baseline` → different bytes.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        static string Doc(string valign) =>
            "<!DOCTYPE html><html><body><div>A<span style=\"display:inline-block;width:20px;height:8px;" +
            "background-color:#3366cc;vertical-align:" + valign + "\"></span>A</div></body></html>";

        var baseline = Latin1(HtmlPdf.Convert(Doc("baseline"), opts));
        var top = Latin1(HtmlPdf.Convert(Doc("top"), opts));
        var bottom = Latin1(HtmlPdf.Convert(Doc("bottom"), opts));

        Assert.NotEqual(baseline, top);     // top raises the box to the line top
        Assert.NotEqual(baseline, bottom);  // bottom drops it to the line bottom
        Assert.NotEqual(top, bottom);
    }

    [Fact]
    public void Vertical_align_sub_super_and_length_shift_an_inline_block_end_to_end()
    {
        // vertical-align sub/super + length cycles — end-to-end through the cascade: `super` / `sub` and
        // a numeric `<length>` (a raise / lower off the baseline) each place the inline-block at a
        // DIFFERENT vertical position than `baseline`, so the painted background band differs.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        static string Doc(string valign) =>
            "<!DOCTYPE html><html><body><div>A<span style=\"display:inline-block;width:20px;height:8px;" +
            "background-color:#3366cc;vertical-align:" + valign + "\"></span>A</div></body></html>";

        var baseline = Latin1(HtmlPdf.Convert(Doc("baseline"), opts));
        var super = Latin1(HtmlPdf.Convert(Doc("super"), opts));
        var sub = Latin1(HtmlPdf.Convert(Doc("sub"), opts));
        var length = Latin1(HtmlPdf.Convert(Doc("6px"), opts));

        Assert.NotEqual(baseline, super);   // super raises
        Assert.NotEqual(baseline, sub);     // sub lowers
        Assert.NotEqual(baseline, length);  // a positive length raises
        Assert.NotEqual(super, sub);
    }

    [Fact]
    public void Text_vertical_align_super_sits_above_and_sub_below_same_line_text()
    {
        // text vertical-align cycle (CSS 2.2 §10.8.1) — an inline run's `super` sits ABOVE the normal
        // text ON ITS LINE, `sub` BELOW (the line box grows to contain the shift). Compare the shifted
        // run's glyph Td y to the baseline run's on the SAME line (PDF user space is y-up, so higher → a
        // LARGER y). A lone shifted run can't show the raise — the line grows around it — so compare to
        // same-line baseline text.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        (double Normal, double Shifted) Line(string valign)
        {
            var ys = AllTdY(Latin1(HtmlPdf.Convert(
                "<!DOCTYPE html><html><body><div><span>X</span>" +
                "<span style=\"vertical-align:" + valign + "\">A</span></div></body></html>", opts)));
            Assert.True(ys.Length >= 2,
                $"expected two Td glyph slices (X then A); got {ys.Length} for vertical-align:{valign}");
            return (ys[0], ys[1]);   // X (baseline) then A (shifted), in document order.
        }

        var sup = Line("super");
        var sub = Line("sub");

        Assert.True(sup.Shifted > sup.Normal + 1, $"super should sit above same-line text: {sup.Shifted} vs {sup.Normal}");
        Assert.True(sub.Shifted < sub.Normal - 1, $"sub should sit below same-line text: {sub.Shifted} vs {sub.Normal}");
    }

    [Fact]
    public void Text_vertical_align_top_and_bottom_position_at_the_line_box_edges()
    {
        // text vertical-align line-edge cycle (CSS 2.2 §10.8.1, post-PR-#194 task 3, bounded first cut) —
        // `top` / `bottom` position an inline TEXT run at the LINE BOX top / bottom (a NON-baseline offset,
        // where before they mapped to no shift). A tall inline-block grows the line to ~48px, so the
        // top-aligned "A" sits near the line TOP (high y) and the bottom-aligned one near the line BOTTOM
        // (low y) — far apart, and top well above a baseline-aligned run (PDF user space is y-up).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double GlyphY(string valign) => FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><div>" +
            "<span style=\"display:inline-block;width:4px;height:48px;vertical-align:baseline\"></span>" +
            "<span style=\"vertical-align:" + valign + "\">A</span></div></body></html>", opts))).Y;

        var top = GlyphY("top");
        var bottom = GlyphY("bottom");
        var baseline = GlyphY("baseline");

        Assert.True(top > bottom + 10, $"top should sit far above bottom on a tall line: top={top} bottom={bottom}");
        Assert.True(top > baseline + 5, $"top should sit above a baseline-aligned run: top={top} baseline={baseline}");
    }

    [Fact]
    public void Text_vertical_align_middle_text_top_text_bottom_position_relative_to_the_parent()
    {
        // text vertical-align line-edge (post-PR-#194 task 3 + PR 3 task 7 line-growth) — `middle` /
        // `text-top` / `text-bottom` position a run relative to the PARENT's baseline + text content-area.
        // Task 7 now GROWS the line to contain a tall such run, which MOVES the shared line baseline — so the
        // robust check compares the aligned 40px run against a SAME-LINE baseline 40px run (both share the
        // grown line + its baseline), rather than across separate docs with differing line heights. y-up,
        // the offset (aligned − same-line baseline) orders text-bottom > middle > text-top: text-bottom puts
        // the run's BOTTOM at the parent descent (run rides highest), text-top its TOP at the parent ascent
        // (run rides lowest), middle its midpoint at the parent baseline + half x-height.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double Offset(string valign)
        {
            var ys = AllTdY(Latin1(HtmlPdf.Convert(
                "<!DOCTYPE html><html><body><div style=\"font-size:16px\">" +
                "<span style=\"font-size:40px\">A</span>" +                                // reference: 40px baseline
                "<span style=\"vertical-align:" + valign + ";font-size:40px\">A</span>" +   // the aligned 40px run
                "</div></body></html>", opts)));
            Assert.True(ys.Length >= 2, $"expected two Td ops (reference + aligned); got {ys.Length}");
            return ys[1] - ys[0];   // aligned − same-line baseline reference (content-stream / doc order)
        }

        var middle = Offset("middle");
        var textTop = Offset("text-top");
        var textBottom = Offset("text-bottom");

        Assert.True(textBottom > middle + 2, $"text-bottom should ride above middle: textBottom={textBottom} middle={middle}");
        Assert.True(middle > textTop + 2, $"middle should ride above text-top: middle={middle} textTop={textTop}");
    }

    [Fact]
    public void Line_height_length_and_number_set_the_line_pitch_end_to_end()
    {
        // line-height cycle — a declared `line-height` now REACHES the computed style (pre-fix it was
        // unwired in the dispatch → every value silently fell back to font-size × 1.2). A 2-line block
        // (split by <br>) has a line PITCH (the Td-y delta between the two lines) equal to the used
        // line-height. The control `normal` = font-size × 1.2; an absolute `24px` = 24 regardless of font;
        // a unitless number = number × font-size. The synthetic font-size is derived from the control so
        // the assertions don't hard-code it. Proves the resolver → ReadLineHeightPx → layouter/painter path.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double Pitch(string style)
        {
            var ys = AllTdY(Latin1(HtmlPdf.Convert(
                "<!DOCTYPE html><html><body><div style=\"" + style + "\">A<br>A</div></body></html>", opts)));
            Assert.True(ys.Length >= 2, $"expected two lines (one Td each); got {ys.Length}");
            return ys[0] - ys[1];   // y-up: the first line sits above the second; their delta is the pitch
        }

        var normal = Pitch("");   // control → font-size(16px) × 1.2 = 19.2px = 14.4pt (the Td-y is in PDF pt)
        // A declared absolute length now REACHES the pitch (pre-fix: unwired → ignored). 40px = 30pt,
        // and it scales linearly — a 20px length is exactly half — proving the length, not a fallback, drives it.
        Assert.Equal(30.0, Pitch("line-height:40px"), precision: 1);
        Assert.Equal(0.5 * Pitch("line-height:40px"), Pitch("line-height:20px"), precision: 1);
        Assert.True(Pitch("line-height:40px") > normal + 5, $"a 40px line-height should exceed the normal pitch {normal}");
        // A unitless number scales with the element's font-size — line-height:4 is exactly 2× line-height:2.
        Assert.Equal(2.0 * Pitch("line-height:2"), Pitch("line-height:4"), precision: 1);
    }

    [Fact]
    public void Line_height_zero_collapses_the_line_pitch_end_to_end()
    {
        // Post-PR-#197 review P2 — a valid `line-height: 0` (and `0px`/`0%`) COLLAPSES the line box (the
        // two <br>-split lines overlap, pitch 0) instead of silently falling back to font-size × 1.2. A
        // plain numeric sentinel couldn't distinguish explicit 0 from `normal`; the nullable reader can.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double Pitch(string style)
        {
            var ys = AllTdY(Latin1(HtmlPdf.Convert(
                "<!DOCTYPE html><html><body><div style=\"" + style + "\">A<br>A</div></body></html>", opts)));
            Assert.True(ys.Length >= 2, $"expected two lines; got {ys.Length}");
            return ys[0] - ys[1];
        }

        Assert.Equal(0.0, Pitch("line-height:0"), precision: 1);    // explicit zero → collapsed
        Assert.Equal(0.0, Pitch("line-height:0px"), precision: 1);  // 0px → collapsed
        Assert.Equal(0.0, Pitch("line-height:0%"), precision: 1);   // 0% → collapsed
        Assert.True(Pitch("") > 5, "the normal control should keep a positive pitch (≠ collapsed)");
    }

    [Fact]
    public void Vertical_align_on_a_block_or_cell_does_not_shift_its_own_text()
    {
        // text vertical-align inline-level gate — vertical-align applies to INLINE-LEVEL boxes. A
        // BLOCK's own `vertical-align: super` (a CSS no-op) — and a TABLE CELL's — must NOT shift its
        // own text: `<div style="vertical-align:super">A</div>` renders identically to a plain div
        // (the run's style IS the block's, so the painter's inline-level gate skips the shift).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var plain = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><div>A</div></body></html>", opts));
        var blockValign = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><div style=\"vertical-align:super\">A</div></body></html>", opts));
        Assert.Equal(plain, blockValign);
    }

    [Fact]
    public void Vertical_align_on_a_table_cell_does_not_shift_its_direct_text_but_an_inline_span_does()
    {
        // text vertical-align inline-level gate, TABLE-CELL coverage (post-PR-#194 review P3). A cell's
        // inline content is wrapped in an anonymous block that REUSES the cell's style ref (BoxBuilder
        // FixupAnonymousBlocks), so the painter's ReferenceEquals(runStyle, blockStyle) gate fires for
        // the cell's DIRECT text — the cell's own `vertical-align: super` doesn't baseline-shift it. A
        // NESTED <span> carries a DISTINCT style, so its vertical-align STILL shifts. (TableLayouter
        // ignores cell vertical-align entirely, so the painter's gate is the ONLY thing that could
        // wrongly move the cell's text — making this the brittle path worth proving directly.)
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };

        // (1) The cell's OWN vertical-align:super must NOT leak into its direct text "Y": it stays on the
        //     baseline with the nested span's "X" (same line). A leak would raise "Y" above "X".
        var ownValign = AllTdY(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><table><tr>" +
            "<td style=\"vertical-align:super\"><span>X</span>Y</td></tr></table></body></html>", opts)));
        Assert.True(ownValign.Length >= 2, $"expected the span 'X' + the direct 'Y' Td; got {ownValign.Length}");
        Assert.Equal(ownValign[0], ownValign[1], precision: 2);   // X and Y share the baseline — no leak.

        // (2) A nested inline <span style="vertical-align:super"> in a PLAIN cell STILL shifts above its
        //     same-line baseline text — the gate is inline-level, not a blanket cell suppression.
        var nestedSpan = AllTdY(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><table><tr><td><span>X</span>" +
            "<span style=\"vertical-align:super\">A</span></td></tr></table></body></html>", opts)));
        Assert.True(nestedSpan.Length >= 2, $"expected the baseline 'X' + the super 'A' Td; got {nestedSpan.Length}");
        Assert.True(nestedSpan[1] > nestedSpan[0] + 1,
            $"a nested span's super should sit above same-line cell text: {nestedSpan[1]} vs {nestedSpan[0]}");
    }

    [Fact]
    public void Text_vertical_align_percentage_uses_the_runs_own_line_height()
    {
        // Post-PR-#193 review P2 — a text run's `vertical-align: %` resolves against the run's OWN
        // line-height (CSS 2.2 §10.8.1), not the parent / line box. The "A" span's OWN `font-size:32px`
        // gives a line-height of 32×1.2 = 38.4px, so `50%` is a 19.2px (= 14.4pt) raise above the
        // same-line normal "X"; the block's default 19.2px line-height would give only ~9.6px (= 7.2pt).
        // The relative A−X y isolates A's own raise (both runs share the line baseline).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var ys = AllTdY(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><div><span>X</span>" +
            "<span style=\"vertical-align:50%;font-size:32px\">A</span></div></body></html>", opts)));

        // ~14.4pt (50% of A's OWN 38.4px line-height), NOT ~7.2pt (50% of the block's default line-height).
        Assert.True(ys.Length >= 2, $"expected two Td glyph slices (X then A); got {ys.Length}");
        Assert.InRange(ys[1] - ys[0], 11.0, 18.0);
    }

    [Fact]
    public void Nonuniform_border_with_radius_rounds_corners_via_clip()
    {
        // Rounded NON-uniform borders cycle — a border-radius with per-side-differing border
        // widths/colours can't use the single uniform ring, so the four square edge rects are
        // CLIPPED to the rounded BORDER-box outline: the OUTER corners follow the radius (matching
        // the rounded background band). The clip surfaces as a `W n` path-clip operator. The
        // background band still rounds (a Bézier path fill).
        const string nonUniform =
            "<!DOCTYPE html><html><body>"
            + "<div style=\"width:50px;height:30px;border-radius:10px;"
            + "border-top:5px solid #ff0000;border-bottom:10px solid #0000ff;"
            + "border-left:5px solid #ff0000;border-right:5px solid #ff0000;"
            + "background-color:#33cc33\"></div></body></html>";
        var nonUniformText = Latin1(HtmlPdf.Convert(nonUniform));
        // A clip path is set (the rounded border-box outline) — the edges round, not poke out square.
        Assert.Contains("W n", nonUniformText);

        // Differential — the SAME box with a UNIFORM border (one width/colour on all sides) + radius
        // paints the single even-odd RING (`f*`) and needs NO per-edge clip.
        const string uniform =
            "<!DOCTYPE html><html><body>"
            + "<div style=\"width:50px;height:30px;border-radius:10px;"
            + "border:5px solid #ff0000;background-color:#33cc33\"></div></body></html>";
        var uniformText = Latin1(HtmlPdf.Convert(uniform));
        Assert.Contains("f*", uniformText);          // the uniform rounded ring
        Assert.DoesNotContain("W n", uniformText);   // no per-edge clip needed
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            n++;
            idx += needle.Length;
        }
        return n;
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
    public void Explicit_width_sizes_the_background_band()
    {
        // Body-explicit-width gap fix — an empty block with `width: 64px` paints a
        // 64px-wide (= 48pt) background band, not a full-content-width one (pre-fix the
        // no-inline-content block path ignored explicit width entirely; the band spanned
        // the whole content area, ~451.5pt under the default 96px page margins).
        var r = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:64px;height:20px;background-color:#3366cc\"></div>" +
            "</body></html>")));
        Assert.Equal(48.0, r.W, 1);   // 64px → 48pt
        Assert.Equal(15.0, r.H, 1);   // 20px → 15pt
    }

    [Fact]
    public void Explicit_width_background_band_is_the_border_box()
    {
        // The declared width is the CONTENT-box size — the painted band adds the
        // inline borders + padding: 64 + 2×2 + 2×8 = 84px → 63pt wide,
        // 20 + 2×2 + 2×8 = 40px → 30pt tall.
        var r = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:64px;height:20px;padding:8px;border:2px solid #000;" +
            "background-color:#3366cc\"></div>" +
            "</body></html>")));
        Assert.Equal(63.0, r.W, 1);
        Assert.Equal(30.0, r.H, 1);
    }

    [Fact]
    public void Body_em_width_sizes_the_background_band()
    {
        // Body context-dependent cycle — a font-relative body width resolves via the post-build
        // in-place pass: font-size 20px × 5em = 100px → a 75pt band (rides the explicit-width
        // border-box sizing from the PR #159 review).
        var r = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"font-size:20px;width:5em;height:20px;background-color:#3366cc\"></div>" +
            "</body></html>")));
        Assert.Equal(75.0, r.W, 1);
    }

    [Fact]
    public void Body_viewport_relative_width_resolves_against_the_page_box()
    {
        // 50vw on the default A4 page box (794px wide) = 397px → 297.75pt.
        var r = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:50vw;height:20px;background-color:#3366cc\"></div>" +
            "</body></html>")));
        Assert.Equal(297.75, r.W, 1);
    }

    [Fact]
    public void Body_font_relative_calc_padding_shifts_the_text()
    {
        // calc(1em + 8px) with the UA-default 16px font = 24px → an 18pt shift, with no
        // invalid-value diagnostic (pre-cycle the declaration was diagnosed + dropped).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>div { padding-left: calc(1em + 8px) }</style></head>" +
            "<body><div>AB</div></body></html>", opts);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
        var without = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head></head><body><div>AB</div></body></html>", opts)));
        Assert.Equal(without.X + 18.0, FirstTd(Latin1(result.Pdf)).X, 1);
    }

    [Fact]
    public void Body_trig_math_function_width_resolves()
    {
        // §10.8/§10.9 (trig/exp cycle) through the whole pipeline: an absolute-term math
        // function folds at cascade time — hypot(30px, 40px) = 50px → a 37.5pt band.
        var r = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:hypot(30px, 40px);height:20px;background-color:#3366cc\"></div>" +
            "</body></html>")));
        Assert.Equal(37.5, r.W, 1);
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

    /// <summary>The (x, y, width, height) operands of the first <c>… re f</c> rectangle-FILL op.
    /// Matches <c>" re f"</c>, not bare <c>" re"</c>, so a clip-path rectangle (<c>… re W n</c> —
    /// clip-path cycle) is never mistaken for a painted background/border rect.</summary>
    private static (double X, double Y, double W, double H) FirstRect(string pdf)
    {
        var idx = pdf.IndexOf(" re f", StringComparison.Ordinal);
        Assert.True(idx > 0, "expected a rectangle-fill operator in the content stream");
        var nums = pdf[..idx].TrimEnd().Split(' ');   // … <x> <y> <w> <h>
        return (
            double.Parse(nums[^4], CultureInfo.InvariantCulture),
            double.Parse(nums[^3], CultureInfo.InvariantCulture),
            double.Parse(nums[^2], CultureInfo.InvariantCulture),
            double.Parse(nums[^1], CultureInfo.InvariantCulture));
    }

    /// <summary>Every <c>… &lt;x&gt; &lt;y&gt; &lt;w&gt; &lt;h&gt; re f</c> rectangle-FILL in the stream,
    /// in emission order (pt). Each fill is <c>q … &lt;x&gt; &lt;y&gt; &lt;w&gt; &lt;h&gt; re f Q</c>, so
    /// the four operands are the last four space-separated tokens before each <c>" re f"</c>. A clip-path
    /// rect (<c>… re W n</c>) is deliberately excluded.</summary>
    private static List<(double X, double Y, double W, double H)> AllRects(string pdf)
    {
        var rects = new List<(double, double, double, double)>();
        for (var i = pdf.IndexOf(" re f", StringComparison.Ordinal); i > 0;
             i = pdf.IndexOf(" re f", i + 5, StringComparison.Ordinal))
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

    /// <summary>The fill colour (<c>"r g b"</c>) set immediately BEFORE each <c>" re f"</c> rectangle, in
    /// emission order — i.e. the actual colour of each painted rectangle (a background band or a border
    /// edge), NOT a text fill and NOT a (colourless) clip-path rect. Lets a test assert the RECT colour
    /// rather than just <c>Contains</c> on the whole stream (where a same-coloured text fill could mask a
    /// wrong rect colour — review P1).</summary>
    private static List<string> RectFillColors(string pdf)
    {
        var colors = new List<string>();
        for (var i = pdf.IndexOf(" re f", StringComparison.Ordinal); i > 0;
             i = pdf.IndexOf(" re f", i + 5, StringComparison.Ordinal))
        {
            var rg = pdf.LastIndexOf(" rg", i, StringComparison.Ordinal);   // the fill colour set for this rect
            if (rg < 0) continue;
            var nums = pdf[..rg].TrimEnd().Split(' ');   // … <r> <g> <b>
            if (nums.Length >= 3)
                colors.Add($"{nums[^3]} {nums[^2]} {nums[^1]}");
        }
        return colors;
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
    public void At_page_compound_name_first_selector_margin_box_paints_on_a_named_first_page()
    {
        // Backlog #5 — a COMPOUND `@page chapter:first` margin box paints on a page that is BOTH named
        // "chapter" (its first content box assigns `page: chapter`) AND first. The bare `@page`
        // @bottom-center "A" (1 glyph) is overridden by `chapter:first` "AB" (2 glyphs) — the compound
        // selector matched + won the cascade (it outranks the bare @page per CSS Page 3 §3.1). Body "x"
        // (1 glyph) + the compound box "AB" (2) = 3 total; a non-matching compound would leave the bare
        // "A" → 2 total.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content: \"A\" } } " +
            "@page chapter:first { @bottom-center { content: \"AB\" } }</style></head>" +
            "<body><div style=\"page:chapter\">x</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(3, TotalGlyphCount(pdf));   // body "x" (1) + compound box "AB" (2)
    }

    [Fact]
    public void At_page_pure_pseudo_compound_selector_margin_box_paints()
    {
        // Pure/multi-pseudo cycle — a PURE-pseudo compound `@page :first:right` margin box paints on a
        // page that is BOTH first AND right (page 0 in LTR), overriding the bare `@page` "A" (1 glyph)
        // with "AB" (2 glyphs). Pre-cycle a pure-pseudo compound was deferred (no match) → the bare "A".
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"A\" } } " +
            "@page :first:right { @top-center { content: \"AB\" } }</style></head>" +
            "<body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(2, TotalGlyphCount(pdf));   // the :first:right box "AB", not the bare "A"
    }

    [Fact]
    public void At_page_multi_pseudo_named_compound_selector_margin_box_paints()
    {
        // Pure/multi-pseudo cycle — a MULTI-pseudo named compound `@page chapter:first:right` margin box
        // paints on a page that is named "chapter" AND first AND right (the most specific, tier 111),
        // overriding the bare "A". Body "x" (1) + the compound box "AB" (2) = 3.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content: \"A\" } } " +
            "@page chapter:first:right { @bottom-center { content: \"AB\" } }</style></head>" +
            "<body><div style=\"page:chapter\">x</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(3, TotalGlyphCount(pdf));   // body "x" (1) + compound box "AB" (2)
    }

    // ---- Per-page @page GEOMETRY (per-page-geometry cycle) ----

    [Fact]
    public void At_page_left_right_duplex_margins_shift_the_body_per_page()
    {
        // Per-page-geometry cycle — duplex margins. `@page :right { margin-left: 24px }` /
        // `@page :left { margin-left: 96px }` give the right (page 0) and left (page 1) pages DIFFERENT
        // left insets, so the body paints at a different X on each page. Four 400px blocks (1600px) on a
        // margin-0 A4 (~1122px content) split 2 / 2 across two pages. The body is laid out against the
        // bare content area (documented approximation) but the per-page LEFT margin shifts the PAINT:
        // page 0 fills at 24px → 18pt, page 1 at 96px → 72pt.
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><head><style>" +
            "@page { margin: 0 } @page :right { margin-left: 24px } @page :left { margin-left: 96px }" +
            "</style></head><body>");
        for (var i = 0; i < 4; i++) sb.Append("<div style=\"height:400px;background-color:#3366cc\"></div>");
        sb.Append("</body></html>");
        var result = HtmlPdf.ConvertDetailed(sb.ToString());
        var rects = AllRects(Latin1(result.Pdf));

        Assert.Equal(2, result.PageCount);
        Assert.True(rects.Count >= 4, $"expected a fill per block, got {rects.Count}");
        Assert.Equal(18.0, rects[0].X, 1);    // page 0 (right) — margin-left 24px
        Assert.Equal(72.0, rects[^1].X, 1);   // page 1 (left)  — margin-left 96px
    }

    [Fact]
    public void At_page_named_page_size_changes_that_pages_media_box()
    {
        // Per-page-geometry cycle — a NAMED page's `size` changes its own MediaBox. `@page chapter { size:
        // A5 }` while the bare page stays A4; the second section (`page: chapter`) forces a break onto a
        // "chapter" page, so page 0's MediaBox is A4 and page 1's is A5 (the first cut applies per-page
        // SIZE to the page's MediaBox; the body still fragments against the bare A4 area).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>" +
            "@page { size: A4 } @page chapter { size: A5 }" +
            ".ch { page: chapter }" +
            "</style></head><body>" +
            "<div style=\"height:100px\"></div><section class=\"ch\" style=\"height:100px\"></section>" +
            "</body></html>");
        var boxes = AllMediaBoxes(Latin1(result.Pdf));

        Assert.Equal(2, result.PageCount);
        Assert.Equal(2, boxes.Count);
        Assert.Equal(595.3, boxes[0].W, 1);   // page 0 — A4 (bare)
        Assert.Equal(841.9, boxes[0].H, 1);
        Assert.Equal(419.5, boxes[1].W, 1);   // page 1 — A5 (@page chapter)
        Assert.Equal(595.3, boxes[1].H, 1);
    }

    [Fact]
    public void At_page_first_only_geometry_does_not_drive_the_whole_body_fragmentation()
    {
        // Post-PR-#184 review F2 — the multi-page LAYOUT baseline is the BARE `@page` geometry, NOT the
        // context-free bare + `:first`. `@page { size: A4; margin: 0 }` + `@page :first { size: A5 }`:
        // three 500px blocks fragment against the BARE A4 content box (~1122px → two blocks per page → TWO
        // pages). If the body had wrongly fragmented against the smaller `:first` A5 (~793px), only ONE
        // block would fit per page → THREE pages. Page 0 still PAINTS the `:first` A5 MediaBox; page 1
        // paints the bare A4.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>" +
            "@page { size: A4; margin: 0 } @page :first { size: A5 }" +
            "</style></head><body>" +
            "<div style=\"height:500px\"></div><div style=\"height:500px\"></div><div style=\"height:500px\"></div>" +
            "</body></html>");
        var boxes = AllMediaBoxes(Latin1(result.Pdf));

        Assert.Equal(2, result.PageCount);   // bare A4 baseline → 2 blocks/page (NOT 3 pages from :first A5)
        Assert.Equal(419.5, boxes[0].W, 1);  // page 0 PAINTS :first A5
        Assert.Equal(595.3, boxes[1].W, 1);  // page 1 PAINTS bare A4
    }

    [Fact]
    public void At_page_named_page_larger_margins_clip_body_and_emit_overflow_warning()
    {
        // Post-PR-#184 review F3 — per-page geometry can clip content the BARE baseline accepted, so the
        // overflow check runs per painted page against its OWN content rect. `@page chapter { margin: 300px }`
        // shrinks the chapter page's content box to ~522px (A4 1122px − 2×300px); a 700px `page: chapter`
        // block fits the bare A4 at layout but overflows the chapter page's content box at paint → the
        // overflow diagnostic fires.
        var clipping = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>" +
            "@page { size: A4; margin: 0 } @page chapter { margin: 300px }" +
            ".ch { page: chapter }" +
            "</style></head><body>" +
            "<div style=\"height:100px\"></div><div class=\"ch\" style=\"height:700px\"></div>" +
            "</body></html>");
        Assert.Contains(clipping.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);

        // Control — the SAME content with no shrinking chapter margin fits the bare A4 → NO overflow
        // (proves the warning above comes from the per-page geometry, not the body itself).
        var ok = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>" +
            "@page { size: A4; margin: 0 } @page chapter { margin: 0 }" +
            ".ch { page: chapter }" +
            "</style></head><body>" +
            "<div style=\"height:100px\"></div><div class=\"ch\" style=\"height:700px\"></div>" +
            "</body></html>");
        Assert.DoesNotContain(ok.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
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

    /// <summary>Every page's <c>/MediaBox [0 0 W H]</c> (W, H) in pt, in page order — for per-page
    /// geometry tests where pages differ in size.</summary>
    private static List<(double W, double H)> AllMediaBoxes(string pdf)
    {
        var boxes = new List<(double, double)>();
        for (var i = pdf.IndexOf("/MediaBox", StringComparison.Ordinal); i >= 0;
             i = pdf.IndexOf("/MediaBox", i + 9, StringComparison.Ordinal))
        {
            var open = pdf.IndexOf('[', i);
            var close = pdf.IndexOf(']', open);
            var nums = pdf[(open + 1)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries);  // 0 0 W H
            boxes.Add((double.Parse(nums[2], CultureInfo.InvariantCulture),
                double.Parse(nums[3], CultureInfo.InvariantCulture)));
        }
        return boxes;
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
    public void Page_margin_box_line_height_drives_the_line_pitch()
    {
        // margin-box-line-height cycle — a declared `line-height` now spaces a MULTI-LINE margin box's
        // lines: line-height joined MarginBoxStyle's inherited cascade, and PageMarginBoxPainter reads
        // it via ReadLineHeightPx. Same content + width → identical wrapping (the line COUNT is
        // line-height-independent), so a 3× line-height roughly triples the inter-line span;
        // `overflow: visible` keeps every line (no line-count clipping to confound the comparison).
        // BEFORE this cycle both renders used font-size × 1.2 regardless of `line-height`, so the spans
        // were identical — this asserts the new wiring bites.
        static (int Lines, double Span) MarginBox(string lineHeight)
        {
            var pdf = Latin1(HtmlPdf.Convert(
                "<!DOCTYPE html><html><head><style>@page { @top-center { " +
                "content: \"A A A A A A A A A A A A\"; width: 24px; overflow: visible; " +
                $"line-height: {lineHeight} }} }}</style></head><body></body></html>",
                new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
            double min = double.MaxValue, max = double.MinValue;
            var count = 0;
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(pdf, @"-?[0-9.]+ (-?[0-9.]+) Td"))
            {
                var y = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (y < min) min = y;
                if (y > max) max = y;
                count++;
            }
            return (count, count == 0 ? 0.0 : max - min);
        }

        var single = MarginBox("1");
        var triple = MarginBox("3");

        Assert.True(single.Lines >= 3, $"content should wrap to ≥3 lines, got {single.Lines}");
        Assert.Equal(single.Lines, triple.Lines);            // line count is line-height-independent
        Assert.True(triple.Span > single.Span * 2.0,         // ≈3× pitch — clearly more than a constant offset
            $"line-height:3 span ({triple.Span:F1}pt) should exceed 2× the line-height:1 span ({single.Span:F1}pt)");
    }

    [Theory]
    [InlineData("font-size:16px;line-height:2em", 32.0)]              // 2 × content font (16)
    [InlineData("font-size:24px;line-height:1.5rem", 24.0)]          // 1.5 × ROOT font (16), NOT 1.5 × box 24
    [InlineData("font-size:16px;line-height:calc(1em + 4px)", 20.0)] // 1×16 + 4
    public void Page_margin_box_resolves_relative_and_calc_line_height(string boxFontAndLh, double expectedPitchPx)
    {
        // margin-box-line-height cycle (PR #212 review P1) — a deferred font-/viewport-relative or
        // `calc()` line-height resolves at PAINT time against the content font / root / page box (the same
        // bases as size/padding), instead of falling back to font-size × 1.2. The `rem` case uses a box
        // font (24) ≠ the root (16) to prove it scales by the ROOT, not the box.
        var pitchPt = MarginBoxLinePitchPt(pageDecls: "",
            boxDecls: "content: \"A A A A A A A A A A\"; width: 24px; overflow: visible; " + boxFontAndLh);
        Assert.Equal(expectedPitchPx * 0.75, pitchPt, 1);   // 1px = 0.75pt
    }

    [Fact]
    public void Page_margin_box_percentage_line_height_inherits_the_page_context_length()
    {
        // CSS Inline 3 §4.2 (PR #212 review P1) — `@page { font-size:20px; line-height:200% }` computes a
        // 40px LENGTH on the page context; the `@top-center { font-size:10px }` child INHERITS that 40px
        // (not 200% × its own 10px = 20px). Proves the % is converted at the declaring @page context
        // BEFORE it inherits into the margin box.
        var pitchPt = MarginBoxLinePitchPt(
            pageDecls: "font-size:20px; line-height:200%",
            boxDecls: "content: \"A A A A A A A A A A\"; width: 24px; overflow: visible; font-size:10px");
        Assert.Equal(40.0 * 0.75, pitchPt, 1);   // 40px = 200% × the @page 20px, NOT 200% × the box 10px
    }

    // Per-line pitch (pt) of a wrapping @top-center margin box, rendered with optional @page-level decls
    // (font-size / line-height inherited into the box) + the box's own decls. SyntheticFont → deterministic.
    private static double MarginBoxLinePitchPt(string pageDecls, string boxDecls)
    {
        var pagePrefix = string.IsNullOrEmpty(pageDecls) ? "" : pageDecls + "; ";
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { " + pagePrefix +
            "@top-center { " + boxDecls + " } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        double min = double.MaxValue, max = double.MinValue;
        var n = 0;
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(pdf, @"-?[0-9.]+ (-?[0-9.]+) Td"))
        {
            var y = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (y < min) min = y;
            if (y > max) max = y;
            n++;
        }
        Assert.True(n >= 2, $"expected the margin box to wrap to ≥2 lines, got {n}");
        return (max - min) / (n - 1);   // inter-line pitch in pt
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
    public void Multi_page_footer_page_counter_increments_per_page()
    {
        // Cycle 4 — real per-page counter(page) (no longer the hard-coded (1, 1)). Two
        // 600px blocks split across two A4 pages; the @bottom-center footer is
        // counter(page, upper-alpha). Page 1's footer is "A", page 2's is "B" — DISTINCT
        // glyphs. The old single-page renderer printed "A" on every page (one distinct
        // glyph), so two distinct footer glyph runs prove the per-page counter.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>" +
            "@page { @bottom-center { content: \"A\" counter(page, upper-alpha) } }" +
            "</style></head><body>" +
            "<div style=\"height:600px\"></div>" +
            "<div style=\"height:600px\"></div>" +
            "</body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        // Each page subsets its own font, so a single-glyph footer would be glyph id 1 on
        // BOTH pages regardless of the counter. The literal "A" prefix forces page 2's
        // subset to carry BOTH 'A' and 'B' ("AB"), so page 2's glyph run differs from page
        // 1's ("AA") IFF counter(page) actually incremented to 2.
        Assert.Equal(2, result.PageCount);
        var runs = GlyphRuns(Latin1(result.Pdf));
        Assert.Equal(2, runs.Count);               // one footer per page
        Assert.NotEqual(runs[0], runs[1]);          // page 1 "AA" ≠ page 2 "AB"
    }

    [Fact]
    public void Multi_page_footer_total_counter_is_the_real_page_count()
    {
        // Cycle 4 — counter(pages) resolves to the REAL document total (layout-all-then-
        // paint knows it before painting). The footer "A" counter(pages, upper-alpha) is
        // "AA" when the total is 1 and "AB" when it is 2. The literal "A" prefix makes the
        // two-glyph footer comparable across the per-page font subsets (a single "A"/"B"
        // would both subset to glyph 1). A one-page doc's footer ("AA") must therefore
        // differ from a two-page doc's ("AB"); the old hard-coded total of 1 made both "AA".
        const string footer =
            "@page { @bottom-center { content: \"A\" counter(pages, upper-alpha) } }";
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };

        var onePage = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>" + footer + "</style></head><body>" +
            "<div style=\"height:100px\"></div></body></html>", opts);
        var twoPage = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>" + footer + "</style></head><body>" +
            "<div style=\"height:600px\"></div><div style=\"height:600px\"></div>" +
            "</body></html>", opts);

        Assert.Equal(1, onePage.PageCount);
        Assert.Equal(2, twoPage.PageCount);
        var oneRuns = GlyphRuns(Latin1(onePage.Pdf));   // counter(pages)=1 → "AA"
        var twoRuns = GlyphRuns(Latin1(twoPage.Pdf));   // counter(pages)=2 → "AB"
        Assert.NotEmpty(oneRuns);
        Assert.NotEmpty(twoRuns);
        Assert.DoesNotContain(twoRuns, r => oneRuns.Contains(r));  // "AB" ≠ "AA"
    }

    [Fact]
    public void Multi_page_running_header_shows_the_current_section_string_per_page()
    {
        // Cycle 5 — cross-page running content. Two sections each fill a page and set the
        // named string `title` (section 1 → "A", section 2 → "B"). The running header
        // content: "A" string(title) is therefore "AA" on page 1 and "AB" on page 2 —
        // DISTINCT. The whole-document collector returned the FIRST assignment ("A") on
        // EVERY page, so both headers would have read "AA"; per-page carry-forward makes
        // page 2 "AB". (The literal "A" prefix makes the value observable past the per-page
        // font subset — a lone "A"/"B" would both subset to glyph 1.)
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>" +
            "@page { @top-center { content: \"A\" string(title) } }" +
            ".s1 { string-set: title \"A\"; height: 600px }" +
            ".s2 { string-set: title \"B\"; height: 600px }" +
            "</style></head><body>" +
            "<div class=\"s1\"></div><div class=\"s2\"></div>" +
            "</body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.Equal(2, result.PageCount);
        var runs = GlyphRuns(Latin1(result.Pdf));
        Assert.Equal(2, runs.Count);            // one header per page
        Assert.NotEqual(runs[0], runs[1]);       // page 1 "AA" (title=A) ≠ page 2 "AB" (title=B)
    }

    [Fact]
    public void Multi_page_running_header_shows_the_current_section_element_per_page()
    {
        // Cycle 5b — cross-page element() running content (the element() analogue of the named-string
        // test above). Two sections, each fills a page and carries a `position: running(rh)` heading:
        // section 1 → "A", section 2 → "B". The @top-center running header content: "A" element(rh) is
        // therefore "AA" on page 1 and "AB" on page 2 — DISTINCT. The whole-document collector returned the
        // FIRST running element ("A") on EVERY page (both headers "AA"); per-page bucketing + carry-forward
        // makes page 2 "AB". (The literal "A" prefix makes the value observable past the per-page font
        // subset — a lone "A"/"B" would both subset to glyph 1.)
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>" +
            "@page { @top-center { content: \"A\" element(rh) } }" +
            ".rh { position: running(rh) }" +
            ".s1 { height: 600px } .s2 { height: 600px }" +
            "</style></head><body>" +
            "<div class=\"s1\"><div class=\"rh\">A</div></div>" +
            "<div class=\"s2\"><div class=\"rh\">B</div></div>" +
            "</body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.Equal(2, result.PageCount);
        var runs = GlyphRuns(Latin1(result.Pdf));
        Assert.Equal(2, runs.Count);            // one header per page
        Assert.NotEqual(runs[0], runs[1]);       // page 1 "AA" (rh=A) ≠ page 2 "AB" (rh=B)
    }

    [Fact]
    public void Multi_page_string_start_uses_the_page_start_setter_and_carries_forward()
    {
        // PR #198 review P1/P2 — string(name, start) end-to-end across pages. The .chap div sets title="A"
        // and is the FIRST element on page 1, so `start` there is "A" (the page STARTS with the setter — not
        // the empty entry value the pre-fix code returned); page 2 has no setter, so its `start` is the
        // carried "A". The header content "Z" string(title, start) is therefore "ZA" (2 glyphs) on BOTH
        // pages. (Pre-fix, page 1's start was the empty entry → "Z", so the two headers differed.)
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>" +
            "@page { @top-center { content: \"Z\" string(title, start) } }" +
            ".chap { string-set: title \"A\"; height: 50px }" +
            ".fill { height: 600px }" +
            "</style></head><body>" +
            "<div class=\"chap\"></div><div class=\"fill\"></div><div class=\"fill\"></div>" +
            "</body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.Equal(2, result.PageCount);
        var runs = GlyphRuns(Latin1(result.Pdf));
        Assert.Equal(2, runs.Count);              // one header per page
        Assert.Equal(8, runs[0].Length);          // page 1 "ZA" — start picks up the page-start setter (2 glyphs × 4 hex)
        Assert.Equal(runs[0], runs[1]);           // page 2 carries the same start value → identical header
    }

    [Fact]
    public void Multi_page_first_except_header_is_empty_on_the_chapter_start_page()
    {
        // PR #198 review P1/P2 — string(name, first-except) end-to-end. On a page that STARTS with the
        // assigning element, first == start, so first-except is the EMPTY string (the chapter title isn't
        // repeated in the header on the page whose top already shows the heading); a pure continuation page
        // suppresses it too (nothing changed). Plain string(title) DOES show "A" on both pages, proving the
        // value is resolvable and first-except specifically suppresses it. (Pre-fix `start` was the empty
        // entry on page 1, so first-except there wrongly showed "A".)
        const string doc =
            "<!DOCTYPE html><html><head><style>" +
            "@page {{ @top-center {{ content: \"Z\" string(title{0}) }} }}" +
            ".chap {{ string-set: title \"A\"; height: 50px }}" +
            ".fill {{ height: 600px }}" +
            "</style></head><body>" +
            "<div class=\"chap\"></div><div class=\"fill\"></div><div class=\"fill\"></div>" +
            "</body></html>";
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };

        var firstExcept = GlyphRuns(Latin1(HtmlPdf.Convert(string.Format(doc, ", first-except"), opts)));
        var plainFirst = GlyphRuns(Latin1(HtmlPdf.Convert(string.Format(doc, ""), opts)));

        Assert.Equal(2, firstExcept.Count);
        Assert.All(firstExcept, r => Assert.Equal(4, r.Length));   // each header just "Z" — title suppressed (1 glyph)
        Assert.Equal(2, plainFirst.Count);
        Assert.All(plainFirst, r => Assert.Equal(8, r.Length));    // plain string(title) shows "ZA" on both pages
    }

    [Fact]
    public void Multi_page_first_selector_margin_box_paints_only_on_the_first_page()
    {
        // Cycle 6 — @page :first margin boxes are now PER PAGE. The first page paints the :first header
        // ("AA"), later pages the bare @page header ("AB"). The pre-cycle driver resolved margin boxes
        // ONCE (:first won for the whole document), so every page read "AA" — both runs equal. (The "A"
        // prefix makes "AA" [one distinct glyph] vs "AB" [two] observable past the per-page font subset.)
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>" +
            "@page :first { @top-center { content: \"AA\" } }" +
            "@page { @top-center { content: \"AB\" } }" +
            "</style></head><body>" +
            "<div style=\"height:600px\"></div><div style=\"height:600px\"></div>" +
            "</body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.Equal(2, result.PageCount);
        var runs = GlyphRuns(Latin1(result.Pdf));
        Assert.Equal(2, runs.Count);            // one header per page
        Assert.NotEqual(runs[0], runs[1]);       // page 1 ":first AA" ≠ page 2 "bare AB"
    }

    [Fact]
    public void Multi_page_left_and_right_selector_margin_boxes_alternate_by_parity()
    {
        // Cycle 6 — @page :left / :right margin boxes (deferred pre-cycle) now paint by LTR parity:
        // page 1 is a RIGHT page → ":right AA", page 2 a LEFT page → ":left AB". Pre-cycle :left/:right
        // were classified Deferred and never applied (no bare box here → no header painted at all).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>" +
            "@page :right { @top-center { content: \"AA\" } }" +
            "@page :left { @top-center { content: \"AB\" } }" +
            "</style></head><body>" +
            "<div style=\"height:600px\"></div><div style=\"height:600px\"></div>" +
            "</body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.Equal(2, result.PageCount);
        var runs = GlyphRuns(Latin1(result.Pdf));
        Assert.Equal(2, runs.Count);            // a header on each page (proves :left/:right now apply)
        Assert.NotEqual(runs[0], runs[1]);       // page 1 ":right AA" ≠ page 2 ":left AB"
    }

    [Fact]
    public void Multi_page_selector_margin_box_is_not_suppressed_by_a_bare_content_none()
    {
        // Post-PR-#178 review P1: a bare `@page { @top-center { content: none } }` must NOT erase the
        // selector-scoped `@page :left { @top-center { content: "AB" } }` from the structural union — the
        // earlier ResolveAll cross-selector cascade let the bare `none` suppress top-center, so the
        // pipeline saw "no margin boxes" and the LEFT header never painted. Now the left (page 2) header
        // paints; the right (page 1) page is suppressed by the bare `none`.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>" +
            "@page :left { @top-center { content: \"AB\" } }" +
            "@page { @top-center { content: none } }" +
            "</style></head><body>" +
            "<div style=\"height:600px\"></div><div style=\"height:600px\"></div>" +
            "</body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.Equal(2, result.PageCount);
        var runs = GlyphRuns(Latin1(result.Pdf));
        Assert.Single(runs);                    // only the LEFT page (page 2) paints a header; right is suppressed
    }

    [Fact]
    public void Multi_page_named_page_selects_its_margin_box()
    {
        // Cycle 7 — named pages. The second block sets `page: chapter`, so the page it starts is a
        // "chapter" page and paints @page chapter's header ("AB"); the first (unnamed) page paints the bare
        // @page header ("AA"). Pre-cycle, @page <name> was deferred and never applied (both "AA").
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>" +
            "@page chapter { @top-center { content: \"AB\" } }" +
            "@page { @top-center { content: \"AA\" } }" +
            ".ch { page: chapter }" +
            "</style></head><body>" +
            "<div style=\"height:600px\"></div><div class=\"ch\" style=\"height:600px\"></div>" +
            "</body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.Equal(2, result.PageCount);
        var runs = GlyphRuns(Latin1(result.Pdf));
        Assert.Equal(2, runs.Count);            // one header per page
        Assert.NotEqual(runs[0], runs[1]);       // page 1 bare "AA" ≠ page 2 chapter "AB"
    }

    [Fact]
    public void Multi_page_dashed_named_page_selects_its_margin_box()
    {
        // Post-PR-#183 review P2 — a DASHED page name (`--chapter`) is a valid <custom-ident>, so the
        // whole named-page path works end-to-end: `@page --chapter` is matched, the section's
        // `page: --chapter` (recovered by the preprocessor) starts a "--chapter" page, and its header
        // ("AB") paints on page 2 while the bare page paints "AA" on page 1. Pre-fix the dashed name was
        // rejected by both validators so both pages painted the bare header.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>" +
            "@page --chapter { @top-center { content: \"AB\" } }" +
            "@page { @top-center { content: \"AA\" } }" +
            ".ch { page: --chapter }" +
            "</style></head><body>" +
            "<div style=\"height:600px\"></div><div class=\"ch\" style=\"height:600px\"></div>" +
            "</body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.Equal(2, result.PageCount);
        var runs = GlyphRuns(Latin1(result.Pdf));
        Assert.Equal(2, runs.Count);            // one header per page
        Assert.NotEqual(runs[0], runs[1]);       // page 1 bare "AA" ≠ page 2 --chapter "AB"
    }

    [Fact]
    public void Named_page_forces_a_break_even_when_the_content_fits()
    {
        // Post-PR-#179 review P1: CSS Page 3 §3.4 — a change in the used `page` value forces a page break
        // even when the content fits the current page. A short unnamed intro (100px) + a short named
        // section (100px) both fit one A4 page, but the section's `page: chapter` must START a new page —
        // so its @page chapter header ("AB") paints on page 2, distinct from page 1's bare header ("AA").
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>" +
            "@page chapter { @top-center { content: \"AB\" } }" +
            "@page { @top-center { content: \"AA\" } }" +
            ".ch { page: chapter }" +
            "</style></head><body>" +
            "<div style=\"height:100px\"></div><section class=\"ch\" style=\"height:100px\"></section>" +
            "</body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.Equal(2, result.PageCount);       // the named section forced a break (both fit one page)
        var runs = GlyphRuns(Latin1(result.Pdf));
        Assert.Equal(2, runs.Count);             // one header per page
        Assert.NotEqual(runs[0], runs[1]);        // page 1 bare "AA" ≠ page 2 chapter "AB"
    }

    [Fact]
    public void Multi_page_composition_paginates_with_running_header_footer_counter_and_named_page()
    {
        // Cycle 8 — end-to-end composition (golden): a three-section report paginates at block boundaries
        // (cycles 1–3); every page gets a running header from string-set carry-forward (cycle 5), a
        // "Page N of T" footer from counter(page)/counter(pages) (cycle 4), and the second section is a
        // named page (page: ref) that swaps in its own @page ref header (cycles 6/7). Verifies the whole
        // arc composes on one document, with deterministic bytes and no overflow truncation.
        const string html =
            "<!DOCTYPE html><html><head><style>" +
            "@page { @top-center { content: \"H\" string(sect) } " +
            "@bottom-center { content: counter(page) \"/\" counter(pages) } }" +
            "@page ref { @top-center { content: \"R\" string(sect) } " +
            "@bottom-center { content: counter(page) \"/\" counter(pages) } }" +
            ".s1 { string-set: sect \"A\"; height: 600px }" +
            ".s2 { string-set: sect \"B\"; height: 600px; page: ref }" +
            ".s3 { string-set: sect \"C\"; height: 600px }" +
            "</style></head><body>" +
            "<div class=\"s1\"></div><div class=\"s2\"></div><div class=\"s3\"></div>" +
            "</body></html>";
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var result = HtmlPdf.ConvertDetailed(html, opts);

        // Three 600px sections far exceed an A4 content box (~931px) and split at section boundaries.
        Assert.True(result.PageCount >= 2, $"expected multi-page, got {result.PageCount}");
        // Block content FLOWS across pages — no inline-overflow truncation.
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
        // Every page paints exactly one header + one footer (the named page swaps the header text, not its
        // presence), so the painted text-run count is 2 per page.
        var runs = GlyphRuns(Latin1(result.Pdf));
        Assert.Equal(result.PageCount * 2, runs.Count);
        // Determinism (CLAUDE.md §4): same input → identical bytes.
        Assert.Equal(Latin1(result.Pdf), Latin1(HtmlPdf.Convert(html, opts)));
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

    /// <summary>The glyph-hex operand of every <c>&lt;hex&gt; Tj</c> show-glyph operator,
    /// in order — one entry per painted text run. Used to compare per-page footer content
    /// (e.g. page 1's "A" run vs page 2's "B" run) without depending on the subsetter's
    /// renumbered glyph ids.</summary>
    private static List<string> GlyphRuns(string pdf)
    {
        var runs = new List<string>();
        var idx = 0;
        while ((idx = pdf.IndexOf(" Tj", idx, StringComparison.Ordinal)) >= 0)
        {
            var close = pdf.LastIndexOf('>', idx);
            var open = close > 0 ? pdf.LastIndexOf('<', close) : -1;
            if (open >= 0 && close > open) runs.Add(pdf.Substring(open + 1, close - open - 1));
            idx += 3;
        }
        return runs;
    }

    /// <summary>How many <c>&lt;hex&gt; Tj</c> show-glyph runs paint exactly
    /// <paramref name="glyphCount"/> glyphs (Identity-H 2-byte ids → 4 hex digits each).
    /// Lets a test count occurrences of a fixed-width text run (e.g. a 2-glyph "BB" body
    /// cell) across every page without depending on the subsetter's renumbered glyph ids —
    /// used to verify per-row / per-paragraph pagination is non-lossy + that repeated
    /// header / footer runs appear once per page.</summary>
    private static int GlyphRunCountOfLength(string pdf, int glyphCount)
    {
        var n = 0;
        foreach (var run in GlyphRuns(pdf))
            if (run.Length == glyphCount * 4) n++;
        return n;
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
    public void Multi_page_shares_one_embedded_font_across_pages()
    {
        // Font-dedup-across-pages: a font used on EVERY page is subset + embedded ONCE (from the cross-page
        // glyph UNION), not once per page. Two empty 600px sections split across two A4 pages, each with a
        // counter(page) footer: page 1's footer is "A", page 2's is "B" — DISTINCT glyphs on distinct pages.
        // Pre-dedup each page embedded its OWN subset ({A} then {B}) → two /FontFile2 programs; now the
        // union {A, B} is embedded once.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>" +
            "@page { @bottom-center { content: counter(page, upper-alpha) } }" +
            "</style></head><body>" +
            "<div style=\"height:600px\"></div><div style=\"height:600px\"></div>" +
            "</body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);

        Assert.Equal(2, result.PageCount);
        Assert.Equal(1, pdf.Split("/FontFile2").Length - 1);   // ONE embedded program shared across both pages
        // Both footers still paint (the union maps each page's glyph), one show per page.
        Assert.Equal(2, GlyphRuns(pdf).Count);
    }

    [Fact]
    public void Multi_page_no_text_document_paginates_without_text_runs()
    {
        // Post-PR-#179 review P3: a multi-page document with NO text (background-only) paginates correctly
        // — the text-paint session stores no empty page draw lists, and the page count + (text-free) output
        // are unchanged. Two 600px background blocks split across two A4 pages.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"height:600px;background-color:#abcdef\"></div>" +
            "<div style=\"height:600px;background-color:#abcdef\"></div>" +
            "</body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);

        Assert.Equal(2, result.PageCount);
        Assert.DoesNotContain("BT", pdf);            // no text-show operators at all
        Assert.Empty(GlyphRuns(pdf));
        Assert.Contains("re f", pdf);                // the backgrounds still paint
    }

    [Fact]
    public async Task Multi_page_render_honors_cancellation()
    {
        // Post-PR-#179 review P2: a cancelled token aborts the render, including the post-layout
        // text-finish pass (font subset/embed + per-page replay). A large multi-page text document with a
        // pre-cancelled token throws rather than running to completion.
        var sb = new StringBuilder("<!DOCTYPE html><html><body>");
        for (var i = 0; i < 40; i++) sb.Append("<p style=\"height:300px\">paragraph text</p>");
        sb.Append("</body></html>");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await HtmlPdf.ConvertAsync(
                sb.ToString(), new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }, cts.Token));
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
        // The `padding` 1-value box shorthand expands to all four longhands end-to-end — padding: 30px
        // sets padding-left = 30px, shifting the start-aligned @top-left line right by ~22.5pt. (30px,
        // not more: the vertical paddings count against the 96px band — the 19.2px line still fits the
        // 36px content box, so the overflow-clipping cycle doesn't truncate it.)
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var withPad = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content: \"AB\"; padding: 30px } }</style>" +
            "</head><body></body></html>", opts)));
        var without = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content: \"AB\" } }</style>" +
            "</head><body></body></html>", opts)));
        Assert.InRange(withPad.X - without.X, 21.0, 24.0);   // 30px → ~22.5pt shift
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
        // coords, and a valid PDF is still produced. The 0-height content box means the line no longer
        // fits, so the overflow-clipping cycle truncates the text (surfaced via the diagnostic) rather
        // than painting it at bogus coordinates.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"AB\"; padding: 9999px } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);
        Assert.StartsWith("%PDF-", pdf);          // valid output, no crash
        Assert.DoesNotContain("BT", pdf);         // the line doesn't fit the 0-height content box → clipped
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.PaintMarginBoxContentOverflow001
            && d.Message.Contains("0 of 1 line(s) painted"));   // …and the truncation is surfaced
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
    [InlineData("padding-left: 10%", false)]   // a percentage per-side longhand — resolves vs the band
    [InlineData("padding: 10%", false)]        // the shorthand (expands to four % longhands)
    [InlineData("padding-left: 1em", false)]   // a font-relative length — resolves vs the box font
    [InlineData("padding-left: 10cqw", true)]  // container units — still unsupported → surfaced
    public void Page_margin_box_unsupported_padding_is_surfaced_supported_is_not(string decls, bool surfaced)
    {
        // Relative-padding cycle: a percentage / font-/viewport-relative / calc() padding now RESOLVES
        // (no diagnostic — see the shift tests below); only what still can't resolve (container units)
        // keeps the diagnose-and-drop path (CLAUDE.md #7, review P2 + Copilot).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"AB\"; " + decls + " } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        if (surfaced)
            Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
        else
            Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
    }

    [Fact]
    public void Page_margin_box_percentage_padding_insets_by_the_band_fraction()
    {
        // Relative-padding cycle: padding-left: 10% resolves against the box's containing-block width
        // (the top edge band, CSS B&B §8.4) — the line shifts right by exactly 10% of the band.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"AB\"; padding-left: 10% } }</style>" +
            "</head><body></body></html>", opts));
        var without = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"AB\" } }</style>" +
            "</head><body></body></html>", opts)));
        var bandPt = MediaBox(pdf).W - 144.0;   // page − 2 × 1in margins (the top band width, pt)
        Assert.Equal(without.X + bandPt * 0.10, FirstTd(pdf).X, 1);   // shifted by 10% of the band
    }

    [Fact]
    public void Page_margin_box_em_and_calc_padding_inset_the_text()
    {
        // Relative-padding cycle: `1em` padding resolves against the BOX's font-size (20px → 15pt
        // shift), and a calc() padding evaluates with the same context (10px + 1em = 30px → 22.5pt).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double LineX(string decls) => FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"AB\"; font-size: 20px; " + decls +
            " } }</style></head><body></body></html>", opts))).X;
        var baseline = LineX("");
        Assert.Equal(baseline + 15.0, LineX("padding-left: 1em"), 1);                  // 20px × 0.75
        Assert.Equal(baseline + 22.5, LineX("padding-left: calc(10px + 1em)"), 1);     // 30px × 0.75
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
    public void Page_margin_box_rem_font_size_resolves_against_the_default_root()
    {
        // font-size cycle (flips the old "stays deferred → 16px" pin): rem now resolves against the
        // root font-size at paint time — with no html font-size declared, the 16px default root makes
        // 2rem = 32px → 24pt Tf.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @bottom-center { content:\"AB\"; font-size: 2rem } }</style>" +
            "</head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(24.0, FirstTf(pdf), 1);   // 2 × 16px root × 0.75 (resolved, no longer the fallback)
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
    public void Page_margin_box_border_box_sizing_makes_the_explicit_width_the_border_box()
    {
        // box-sizing cycle (CSS Basic UI 4 §10): under `box-sizing: border-box` the explicit `width`
        // IS the painted border-box — 200px → 150pt — the 20px paddings come out of the content area
        // instead of adding to it (content-box default: (200+40)px → 180pt, covered above).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; width: 200px; " +
            "box-sizing: border-box; padding-left: 20px; padding-right: 20px; background-color: red } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.InRange(FirstRect(pdf).W, 148.0, 152.0);   // 200px × 0.75 = 150pt — the border box itself
    }

    [Fact]
    public void Page_margin_box_explicit_content_box_sizing_matches_the_default()
    {
        // An explicit `box-sizing: content-box` (the initial) behaves exactly like the default:
        // width 200px + 20px paddings → a (200+40)px = 180pt border box.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; width: 200px; " +
            "box-sizing: content-box; padding-left: 20px; padding-right: 20px; background-color: red } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.InRange(FirstRect(pdf).W, 178.0, 182.0);   // (200 + 20 + 20)px × 0.75 = 180pt
    }

    [Fact]
    public void Page_margin_box_border_box_sizing_floors_at_the_insets()
    {
        // A border-box size SMALLER than the border+padding insets floors at the insets (the content
        // box floors at 0, it can't go negative): width 10px + 20px paddings → a 40px = 30pt box.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"\"; width: 10px; " +
            "box-sizing: border-box; padding-left: 20px; padding-right: 20px; background-color: red } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.InRange(FirstRect(pdf).W, 28.0, 32.0);     // max(10, 20+20)px × 0.75 = 30pt
    }

    [Fact]
    public void Page_margin_box_border_box_sizing_includes_the_border_width()
    {
        // The border widths are part of the border-box size too: width 200px + 10px borders +
        // box-sizing: border-box → the painted band stays 200px = 150pt (content box 180px).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; width: 200px; " +
            "box-sizing: border-box; border-left: 10px solid blue; border-right: 10px solid blue; " +
            "background-color: red } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.InRange(FirstRect(pdf).W, 148.0, 152.0);   // 200px × 0.75 = 150pt
    }

    [Fact]
    public void Page_margin_box_border_box_sizing_sizes_a_left_box_height()
    {
        // The vertical (left/right) variable axis honours box-sizing the same way: an explicit
        // `height: 200px` with `box-sizing: border-box` + 20px vertical paddings paints a 200px =
        // 150pt band (content-box would be (200+40)px = 180pt).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @left-middle { content:\"AB\"; height: 200px; " +
            "box-sizing: border-box; padding-top: 20px; padding-bottom: 20px; background-color: red } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.InRange(FirstRect(pdf).H, 148.0, 152.0);   // 200px × 0.75 = 150pt
    }

    [Fact]
    public void Page_margin_box_box_sizing_is_a_no_op_without_an_explicit_size()
    {
        // box-sizing only changes what an EXPLICIT size specifies — a shrink-to-fit (`width: auto`)
        // box is sized content + insets either way, so declaring border-box must not change the band.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double BandW(string sizing) => FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; " + sizing +
            "padding-left: 20px; padding-right: 20px; background-color: red } }</style></head><body></body></html>",
            opts))).W;
        Assert.Equal(BandW(""), BandW("box-sizing: border-box; "), 3);
    }

    // ---- vertical-edge content wrapping at the band width (Task 21, vertical-wrap cycle) ----

    [Fact]
    public void Page_margin_box_vertical_box_wraps_at_the_band_width()
    {
        // A left/right box's inline axis is FIXED (the 96px margin band) — wrappable content wider
        // than the band now WRAPS there (was: one NoWrap line spilling horizontally into the page),
        // stacking lines down the variable axis: multiple Td operators, and the shrink-to-fit band
        // grows ~one line-height per extra line vs a single-AB box.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        (int Lines, double BandH) Render(string content)
        {
            var pdf = Latin1(HtmlPdf.Convert(
                "<!DOCTYPE html><html><head><style>@page { @left-middle { content:\"" + content + "\"; " +
                "background-color: red } }</style></head><body></body></html>", opts));
            return (TdCount(pdf), FirstRect(pdf).H);
        }
        var single = Render("AB");
        var wrapped = Render("AB AB AB AB AB AB AB AB");   // wrappable: spaces are break points
        Assert.Equal(1, single.Lines);
        Assert.True(wrapped.Lines >= 2, $"wrappable content should wrap at the band width; got {wrapped.Lines} line(s)");
        Assert.True(wrapped.BandH > single.BandH + 10.0,
            $"the wrapped box should be taller (stacked lines): single={single.BandH}pt wrapped={wrapped.BandH}pt");
    }

    [Fact]
    public void Page_margin_box_corner_box_wraps_at_its_fixed_width()
    {
        // A corner box (both axes fixed to margin × margin) wraps at its own width too — wrappable
        // content wider than the 96px corner stacks into multiple lines instead of spilling.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left-corner { content:\"AB AB AB AB AB AB AB AB\" } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.True(TdCount(pdf) >= 2, $"corner content should wrap at the corner width; got {TdCount(pdf)} line(s)");
    }

    [Fact]
    public void Page_margin_box_vertical_wrapped_lines_clip_at_the_band_limit()
    {
        // Wrapping + clipping integration: wrappable content that wraps to more lines than the box's
        // explicit 60px height holds (floor(60.5 / 19.2) = 3) truncates at line granularity and
        // surfaces the height diagnostic — the wrap must not bypass the PR #155 vertical clip.
        var words = string.Join(" ", System.Linq.Enumerable.Repeat("AB", 16));   // wraps to > 3 band-width lines
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @left-middle { content:\"" + words + "\"; height: 60px } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.PaintMarginBoxContentOverflow001
            && d.Message.Contains("taller"));
        Assert.Equal(3, TdCount(Latin1(result.Pdf)));   // exactly the lines that fit the 60px box
    }

    // ---- margin-box overflow clip path + overflow: visible opt-out (Task 21, clip-path cycle) ----

    /// <summary>The (x, y, w, h) operands of the first <c>… re W n</c> clip-path rectangle.</summary>
    private static (double X, double Y, double W, double H) FirstClipRect(string pdf)
    {
        var idx = pdf.IndexOf(" re W n", StringComparison.Ordinal);
        Assert.True(idx > 0, "expected a clip-path rectangle (re W n) in the content stream");
        var nums = pdf[..idx].TrimEnd().Split(' ');
        return (
            double.Parse(nums[^4], CultureInfo.InvariantCulture),
            double.Parse(nums[^3], CultureInfo.InvariantCulture),
            double.Parse(nums[^2], CultureInfo.InvariantCulture),
            double.Parse(nums[^1], CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Page_margin_box_unbreakable_overflow_is_clipped_via_a_clip_path()
    {
        // An unbreakable run wider than the box (60 A's ≈ 576px in an explicit 100px box) now paints
        // CLIPPED: the fragment's glyph runs are wrapped in a `q <rect> re W n … Q` clip path at the
        // box edge, the glyphs still emit (clipping is visual, not operator-dropping), and the
        // width-phrased overflow diagnostic surfaces it.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"" + new string('A', 60) +
            "\"; width: 100px } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);
        Assert.Contains(" re W n", pdf);                       // the clip path
        Assert.Equal(60, TotalGlyphCount(pdf));                // glyphs still emitted under the clip
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.PaintMarginBoxContentOverflow001
            && d.Message.Contains("wider"));
    }

    [Fact]
    public void Page_margin_box_clip_rect_is_the_padding_box()
    {
        // CSS Overflow 3 §3: the clip edge is the PADDING box — the clip rect spans the border box
        // minus the border widths: width 100px + 10px left/right borders → border box 120px, clip
        // width (120 − 10 − 10)px = 100px → 75pt.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"" + new string('A', 60) +
            "\"; width: 100px; border-left: 10px solid blue; border-right: 10px solid blue } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.InRange(FirstClipRect(pdf).W, 74.0, 76.0);      // 100px × 0.75 = 75pt
    }

    [Fact]
    public void Page_margin_box_overflow_visible_opts_out_of_clipping()
    {
        // An EXPLICIT `overflow: visible` on the box restores the spill: no clip path, no overflow
        // diagnostic — authored overflow (the pre-clipping behavior).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"" + new string('A', 60) +
            "\"; width: 100px; overflow: visible } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);
        Assert.DoesNotContain(" re W n", pdf);
        Assert.Equal(60, TotalGlyphCount(pdf));                // the full run paints, spilling
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PaintMarginBoxContentOverflow001);
    }

    [Fact]
    public void Page_margin_box_overflow_visible_lets_tall_content_spill()
    {
        // The opt-out covers the VERTICAL truncation too: six stacked lines in the 96px band (5 fit)
        // all paint under `overflow: visible`, with no truncation and no diagnostic.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            "@page { @top-center { content: element(rh); overflow: visible } }</style></head>" +
            "<body><div class=\"rh\"><div>AB</div><div>AB</div><div>AB</div><div>AB</div><div>AB</div>" +
            "<div>AB</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.Equal(6, TdCount(Latin1(result.Pdf)));          // nothing truncated
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PaintMarginBoxContentOverflow001);
    }

    [Fact]
    public void Running_element_with_block_children_renders_stacked_nested_lines_not_flattened()
    {
        // PR-3 task 10 — a `position: running()` element with BLOCK-level children renders its children as
        // SEPARATE STACKED lines (real nested block layout via the segment-style + container-bands cycles),
        // NOT one flattened text line. Three block children → three Td lines, each laid out on its own
        // baseline; long text within a block also wraps (post-PR-#154). A nested container's OWN decoration
        // (a background band) paints as a rect over its descendant lines — the structure, not flat text.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            "@page { @top-center { content: element(rh) } }</style></head>" +
            "<body><div class=\"rh\"><div style=\"background-color:red\">P</div><div>Q</div><div>R</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);
        Assert.Equal(3, TdCount(pdf));               // 3 block children → 3 stacked lines (not 1 flattened)
        // The decorated child paints a FILLED red background band (nested decoration). Assert the red fill
        // color AND a rectangle FILL (`re f`), not a bare `re` — a clip-path emits `re W n`, so `re` alone
        // wouldn't prove the band actually painted (Copilot review).
        Assert.Contains("1 0 0 rg", pdf);            // red fill color (background-color:red)
        Assert.Contains("re f", pdf);                // a filled rectangle — the band, not a clip-path `re W n`
    }

    [Fact]
    public void Page_margin_box_fitting_content_emits_no_clip_path()
    {
        // A box whose content fits carries no clip rect — its stream must stay clip-free
        // (byte-identical to pre-cycle output).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; background-color: red } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.DoesNotContain(" re W n", pdf);
    }

    // ---- font-/viewport-relative explicit width/height (Task 21, relative-units cycle) ----

    [Fact]
    public void Page_margin_box_em_width_resolves_against_the_box_font_size()
    {
        // `width: 10em` scales by the BOX's resolved font-size (width is a box property): at
        // font-size 20px → 200px → 150pt; at the 16px default → 160px → 120pt.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double BandW(string extra) => FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; width: 10em; " + extra +
            "background-color: red } }</style></head><body></body></html>", opts))).W;
        Assert.InRange(BandW("font-size: 20px; "), 148.0, 152.0);   // 10 × 20px × 0.75
        Assert.InRange(BandW(""), 118.0, 122.0);                    // 10 × 16px × 0.75
    }

    [Fact]
    public void Page_margin_box_rem_width_resolves_against_the_root_font_size()
    {
        // `width: 10rem` scales by the ROOT element's font-size, not the box's own — a 10px box font
        // with a 20px root still yields 200px → 150pt (10em would give 100px → 75pt).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>html { font-size: 20px } " +
            "@page { @top-center { content:\"AB\"; width: 10rem; font-size: 10px; background-color: red } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.InRange(FirstRect(pdf).W, 148.0, 152.0);   // 10 × 20px × 0.75 = 150pt
    }

    [Fact]
    public void Page_margin_box_viewport_units_resolve_against_the_page_box()
    {
        // Viewport units scale by the PAGE box (paged media maps the viewport to the page):
        // width: 50vw paints a band of exactly half the page width, height: 25vh a quarter of the
        // page height — asserted against the document's own /MediaBox so the default page size
        // doesn't matter.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var vw = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; width: 50vw; background-color: red } }" +
            "</style></head><body></body></html>", opts));
        var halfPage = MediaBox(vw).W / 2.0;
        Assert.InRange(FirstRect(vw).W, halfPage - 2.0, halfPage + 2.0);
        var vh = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @left-middle { content:\"AB\"; height: 25vh; background-color: red } }" +
            "</style></head><body></body></html>", opts));
        var quarterPage = MediaBox(vh).H / 4.0;
        Assert.InRange(FirstRect(vh).H, quarterPage - 2.0, quarterPage + 2.0);
    }

    [Fact]
    public void Page_margin_box_overflow_into_padding_is_not_clipped()
    {
        // Post-PR-#156 review P2: the clip predicate uses the PADDING-BOX geometry — the same rect the
        // clip path uses. Content wider than the 100px CONTENT box but inside the (100+80)px padding box
        // (12 A's ≈ 115px ≤ 180px) extends into the right padding, which is INSIDE the clip edge: no
        // clip path and no overflow diagnostic. (The old content-box predicate wrongly tripped both.)
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"" + new string('A', 12) +
            "\"; width: 100px; padding-right: 80px } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.DoesNotContain(" re W n", Latin1(result.Pdf));
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PaintMarginBoxContentOverflow001);
    }

    [Fact]
    public void Page_margin_box_overflow_past_the_padding_box_still_clips()
    {
        // The padding-box predicate still catches a genuine spill: 30 A's ≈ 288px crosses the 180px
        // padding box → clip path + width diagnostic.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"" + new string('A', 30) +
            "\"; width: 100px; padding-right: 80px } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.Contains(" re W n", Latin1(result.Pdf));
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.PaintMarginBoxContentOverflow001
            && d.Message.Contains("wider"));
    }

    [Fact]
    public void Page_margin_box_extreme_relative_size_is_surfaced_not_silent()
    {
        // Post-PR-#156 review P2: a SYNTACTICALLY supported relative size whose contextual product
        // overflows to a non-finite value (1e308 × 16px font = ∞) must be SURFACED before the
        // shrink-to-fit fallback — not silently ignored (the keep gate in MarginBoxStyle is syntactic).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; width: 1e308em; background-color: red } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001
            && d.Message.Contains("could not be resolved against its context"));
        Assert.True(FirstRect(Latin1(result.Pdf)).W < 200.0,
            "an unresolvable relative width should shrink-to-fit, not paint the full band");
    }

    // ---- calc() explicit sizes (Task 21, calc cycle) ----

    [Fact]
    public void Page_margin_box_calc_width_resolves_percent_minus_length()
    {
        // calc cycle: `width: calc(100% - 100px)` sizes the band to the full edge band minus 100px
        // (= 75pt) — the % term resolves against the band, the px term is absolute.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; " +
            "width: calc(100% - 100px); background-color: red } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var bandPt = MediaBox(pdf).W - 144.0;
        Assert.InRange(FirstRect(pdf).W, bandPt - 75.0 - 2.0, bandPt - 75.0 + 2.0);   // band − 100px(75pt)
    }

    [Fact]
    public void Page_margin_box_calc_height_mixes_em_and_absolute_terms()
    {
        // A vertical box's calc() height with a font-relative term: font-size 20px →
        // calc(2em + 60px) = 100px = 75pt.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @left-middle { content:\"AB\"; font-size: 20px; " +
            "height: calc(2em + 60px); background-color: red } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.InRange(FirstRect(pdf).H, 73.0, 77.0);   // (40 + 60)px × 0.75 = 75pt
    }

    [Fact]
    public void Page_margin_box_calc_width_accepts_uppercase_units()
    {
        // CSS units are case-insensitive (CSS Syntax §4) — `calc(1IN - 24PT)` = 96 − 32 = 64px → 48pt
        // band, with no diagnostic (post-PR-#157 review P2: the absolute-unit lookup normalizes case).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; " +
            "width: calc(1IN - 24PT); background-color: red } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
        Assert.InRange(FirstRect(Latin1(result.Pdf)).W, 46.0, 50.0);   // 64px × 0.75 = 48pt
    }

    [Fact]
    public void Page_margin_box_min_max_clamp_size_functions_resolve()
    {
        // min/max/clamp cycle (CSS Values 4 §10.2): the comparison functions work STANDALONE as the
        // whole value — width: min(50%, 150px) picks the smaller 150px → 112.5pt band; a left box's
        // height: clamp(100px, 50px, 300px) raises VAL to MIN → 100px → 75pt.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var min = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; " +
            "width: min(50%, 150px); background-color: red } }</style></head><body></body></html>", opts);
        Assert.DoesNotContain(min.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
        Assert.InRange(FirstRect(Latin1(min.Pdf)).W, 110.5, 114.5);   // 150px × 0.75 = 112.5pt
        var clamp = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @left-middle { content:\"AB\"; " +
            "height: clamp(100px, 50px, 300px); background-color: red } }</style></head><body></body></html>", opts));
        Assert.InRange(FirstRect(clamp).H, 73.0, 77.0);               // 100px × 0.75 = 75pt
    }

    // ---- rem / viewport-relative margin-box font-size (Task 21, font-size cycle) ----

    [Fact]
    public void Page_margin_box_rem_and_viewport_font_size_resolve()
    {
        // font-size cycle: a root-/viewport-relative font-size no longer falls back to 16px —
        // `5vw` of an 800px page = 40px glyphs (30pt Tf), and `2rem` against a 20px root = 40px too.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var vw = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { size: 800px 600px; " +
            "@top-center { content:\"AB\"; font-size: 5vw } }</style></head><body></body></html>", opts));
        Assert.Contains(" 30 Tf", vw);   // 40px × 0.75 = 30pt (was 12pt from the 16px fallback)
        var rem = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>html { font-size: 20px } " +
            "@page { @top-center { content:\"AB\"; font-size: 2rem } }</style></head><body></body></html>", opts));
        Assert.Contains(" 30 Tf", rem);  // 2 × 20px × 0.75 = 30pt
    }

    [Fact]
    public void Body_absolute_calc_padding_resolves()
    {
        // Body-calc cycle: an ABSOLUTE-term calc() on a BODY property folds at cascade time —
        // padding-left: calc(10px + 14px) = 24px shifts the text right by 18pt, with no
        // CSS-PROPERTY-VALUE-INVALID-001 (pre-cycle, AngleSharp dropped the declaration and the
        // padding silently vanished; the math-function recovery + LengthResolver fold deliver it).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>div { padding-left: calc(10px + 14px) }</style></head>" +
            "<body><div>AB</div></body></html>", opts);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
        var without = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head></head><body><div>AB</div></body></html>", opts)));
        Assert.Equal(without.X + 18.0, FirstTd(Latin1(result.Pdf)).X, 1);   // 24px × 0.75 = 18pt shift
    }

    [Fact]
    public void Body_context_dependent_calc_is_still_surfaced()
    {
        // The body first cut covers ABSOLUTE terms only — a % term still needs layout-time bases and
        // keeps the diagnosed-invalid path (documented deferral).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>div { width: calc(50% - 10px) }</style></head>" +
            "<body><div>AB</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
    }

    [Fact]
    public void Page_margin_box_element_nested_blocks_recurse_into_stacked_lines()
    {
        // Deep-recursion cycle: a block child that ITSELF has block children contributes one line per
        // NESTED block — <div><div>AB</div><div>AB</div></div><div>AB</div> renders THREE lines
        // (was two: the outer pair flattened to "AB AB").
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            "@page { @top-center { content: element(rh) } }</style></head>" +
            "<body><div class=\"rh\"><div><div>AB</div><div>AB</div></div><div>AB</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(3, TdCount(pdf));
    }

    [Fact]
    public void Page_margin_box_element_segment_lines_render_in_their_own_font_and_color()
    {
        // Segment-style cycle ("real nested block layout" first cut): each stacked line of a
        // standalone element() shapes in the LEAF block's own font + colour — the h1 title line
        // at 32px (24pt Tf) red, the subtitle line at the 16px default (12pt Tf). Pre-cycle both
        // lines rendered in the running root's uniform style.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh h1 { font-size: 32px; color: #ff0000 } " +
            "@page { @top-center { content: element(rh) } }</style></head>" +
            "<body><div class=\"rh\"><h1>Title</h1><div>Sub</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        Assert.Equal(2, TdCount(pdf));          // two stacked lines
        Assert.Contains(" 24 Tf", pdf);         // the h1 segment: 32px × 0.75
        Assert.Contains(" 12 Tf", pdf);         // the plain segment: 16px default
        Assert.Contains("1 0 0 rg", pdf);       // the h1 segment's red fill
    }

    [Fact]
    public void Page_margin_box_and_element_decorations_nest_when_both_declare()
    {
        // Separately-decorated cycle (first cut): the BOX's band paints at the box rect AND the
        // running element's own background paints as a NESTED band at its content block — the
        // element's decoration no longer vanishes under the box's cascade win. #3366cc box,
        // #cc3366 element → both fills present.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); background-color: #cc3366 } " +
            "@page { @top-center { content: element(rh); background-color: #3366cc } }</style></head>" +
            "<body><div class=\"rh\">Head</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        Assert.Contains("0.2 0.4 0.8 rg", pdf);   // the box band
        Assert.Contains("0.8 0.2 0.4 rg", pdf);   // the element's nested band
    }

    [Fact]
    public void Page_margin_box_border_radius_rounds_the_background_band()
    {
        // Border-radius cycle (first cut): a uniform absolute radius fills the band as a rounded
        // path (m/l/c Béziers + f) instead of a square `re f`; an undeclared radius keeps `re f`.
        const string square =
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"H\"; " +
            "background-color: #3366cc } }</style></head><body></body></html>";
        const string rounded =
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"H\"; " +
            "background-color: #3366cc; border-radius: 8px } }</style></head><body></body></html>";
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };

        var squarePdf = Latin1(HtmlPdf.Convert(square, opts));
        var roundedPdf = Latin1(HtmlPdf.Convert(rounded, opts));

        Assert.DoesNotContain(" c ", BlueFillRegion(squarePdf));
        Assert.Contains(" c ", BlueFillRegion(roundedPdf));   // Bézier corners on the band fill

        static string BlueFillRegion(string pdf)
        {
            var i = pdf.IndexOf("0.2 0.4 0.8 rg", StringComparison.Ordinal);
            Assert.True(i >= 0, "expected the blue band fill");
            var end = pdf.IndexOf('Q', i);
            return pdf[i..(end < 0 ? pdf.Length : end)];
        }
    }

    [Fact]
    public void Page_margin_box_border_radius_e_notation_parses_like_every_other_length()
    {
        // Post-PR-#161 review P2 — the radius parses through the PRODUCTION tokenizer
        // (LengthResolver.TrySplitNumberAndUnit), so it accepts exactly the grammar every other
        // raw length does: e-notation is a valid CSS number (CSS Syntax §4.3.12; the body path
        // resolves `width: 1e2px` to 100px and the calc evaluator pins `calc(1e2px + 0px)`).
        // `border-radius: 1e1px` = 10px → a rounded (Bézier) band, with NO invalid diagnostic.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"H\"; " +
            "background-color: #3366cc; border-radius: 1e1px } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
        var pdf = Latin1(result.Pdf);
        var i = pdf.IndexOf("0.2 0.4 0.8 rg", StringComparison.Ordinal);
        Assert.True(i >= 0);
        Assert.Contains(" c ", pdf[i..pdf.IndexOf('Q', i)]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.0")]
    [InlineData("-0")]
    [InlineData("0e0")]
    public void Page_margin_box_border_radius_any_zero_spelling_is_a_valid_square(string zero)
    {
        // PR #161 Copilot — the unitless ZERO (CSS Values §6.2) is valid in any spelling the
        // number token admits, not just the literal "0": no diagnostic, square band.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"H\"; " +
            "background-color: #3366cc; border-radius: " + zero + " } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
        var pdf = Latin1(result.Pdf);
        var i = pdf.IndexOf("0.2 0.4 0.8 rg", StringComparison.Ordinal);
        Assert.True(i >= 0);
        Assert.DoesNotContain(" c ", pdf[i..pdf.IndexOf('Q', i)]);
    }

    [Fact]
    public void Page_margin_box_font_or_viewport_relative_border_radius_rounds_the_band()
    {
        // Margin-box relative-radius cycle — a font-/viewport-relative border-radius now RESOLVES
        // (was square): `0.5em` against `font-size: 20px` = 10px, and `2vw` against the page width,
        // each fill the band as a rounded (Bézier) path. No invalid diagnostic.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };

        var em = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"H\"; font-size: 20px; " +
            "background-color: #3366cc; border-radius: 0.5em } }</style></head><body></body></html>", opts);
        var vw = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"H\"; " +
            "background-color: #3366cc; border-radius: 2vw } }</style></head><body></body></html>", opts);

        Assert.DoesNotContain(em.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
        Assert.Contains(" c ", BlueFillRegion(Latin1(em.Pdf)));   // em resolved → rounded
        Assert.Contains(" c ", BlueFillRegion(Latin1(vw.Pdf)));   // vw resolved → rounded

        static string BlueFillRegion(string pdf)
        {
            var i = pdf.IndexOf("0.2 0.4 0.8 rg", StringComparison.Ordinal);
            Assert.True(i >= 0, "expected the blue band fill");
            var end = pdf.IndexOf('Q', i);
            return pdf[i..(end < 0 ? pdf.Length : end)];
        }
    }

    [Fact]
    public void Page_margin_box_relative_border_radius_overflowing_in_context_is_diagnosed_not_silently_squared()
    {
        // Post-PR-#191 review P2 — a SYNTACTICALLY supported relative radius that overflows IN CONTEXT
        // (`1e308em` × the 16px default font = non-finite) must be SURFACED, not silently squared: the
        // RelativeLengthResolver.IsSupported contract requires the caller to diagnose a kept value's
        // contextual failure (mirrors the explicit-size + padding paths). The `2em` control resolves
        // cleanly (no diagnostic, rounded band) so the test can't pass from an unrelated regression.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };

        var overflow = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"H\"; " +
            "background-color: #3366cc; border-radius: 1e308em } }</style></head><body></body></html>", opts);
        var control = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"H\"; " +
            "background-color: #3366cc; border-radius: 2em } }</style></head><body></body></html>", opts);

        // The overflowing radius is diagnosed AND falls back to a square band (no Bézier in the fill).
        Assert.Contains(overflow.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001
            && d.Message.Contains("border-radius"));
        Assert.DoesNotContain(" c ", BlueFill(Latin1(overflow.Pdf)));

        // The control: no diagnostic, rounded band — so the diagnostic above is the overflow's, not noise.
        Assert.DoesNotContain(control.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
        Assert.Contains(" c ", BlueFill(Latin1(control.Pdf)));

        static string BlueFill(string pdf)
        {
            var i = pdf.IndexOf("0.2 0.4 0.8 rg", StringComparison.Ordinal);
            Assert.True(i >= 0, "expected the blue band fill");
            var end = pdf.IndexOf('Q', i);
            return pdf[i..(end < 0 ? pdf.Length : end)];
        }
    }

    [Fact]
    public void Body_border_radius_slash_form_rounds_the_background_elliptically()
    {
        // border-radius-elliptical cycle — `border-radius: <h> / <v>` (dropped by AngleSharp) is
        // recovered + rounds the body background band with DISTINCT horizontal/vertical radii. The
        // elliptical band has Bézier corners (the slash was applied, not squared) AND differs from the
        // circular `<h>` band (the vertical radius is honored, not collapsed to circular).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var ellipse = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.box{width:120px;height:60px;" +
            "background-color:#3366cc;border-radius:40px / 8px}</style></head>" +
            "<body><div class=\"box\"></div></body></html>", opts));
        var circle = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.box{width:120px;height:60px;" +
            "background-color:#3366cc;border-radius:40px}</style></head>" +
            "<body><div class=\"box\"></div></body></html>", opts));
        Assert.True(CountOccurrences(ellipse, " c ") >= 4);   // Bézier corners → rounded (slash applied)
        Assert.NotEqual(circle, ellipse);                     // 8px vertical honored, not circular 40px
    }

    [Fact]
    public void Body_border_radius_circular_after_slash_resets_the_vertical_radii()
    {
        // post-PR-#186 review P1 — a later circular `border-radius` RESETS the internal `-y` slots a prior
        // elliptical `Rx / Ry` set, so `…10px / 30px; …5px` renders IDENTICAL to a plain circular 5px (NOT
        // 5px / 30px with a stale vertical radius). Same fix covers single-value corner longhands.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        string Box(string radius) =>
            "<!DOCTYPE html><html><head><style>.box{width:120px;height:60px;background-color:#3366cc;"
            + radius + "}</style></head><body><div class=\"box\"></div></body></html>";
        var reset = Latin1(HtmlPdf.Convert(Box("border-radius:10px / 30px;border-radius:5px"), opts));
        var circular = Latin1(HtmlPdf.Convert(Box("border-radius:5px"), opts));
        var stale = Latin1(HtmlPdf.Convert(Box("border-radius:10px / 30px"), opts));
        Assert.Equal(circular, reset);     // the second declaration fully resets the elliptical's vertical
        Assert.NotEqual(stale, reset);     // and is NOT the leftover 5px / 30px ellipse
    }

    [Fact]
    public void Body_corner_radius_longhands_after_slash_reset_their_vertical_radii()
    {
        // post-PR-#186 review P1 — single-value corner longhands also reset their corner's stale `-y`
        // after an elliptical shorthand: all four 5px corner longhands == a plain circular 5px.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        string Box(string radius) =>
            "<!DOCTYPE html><html><head><style>.box{width:120px;height:60px;background-color:#3366cc;"
            + radius + "}</style></head><body><div class=\"box\"></div></body></html>";
        var reset = Latin1(HtmlPdf.Convert(Box(
            "border-radius:10px / 30px;border-top-left-radius:5px;border-top-right-radius:5px;"
            + "border-bottom-right-radius:5px;border-bottom-left-radius:5px"), opts));
        var circular = Latin1(HtmlPdf.Convert(Box("border-radius:5px"), opts));
        Assert.Equal(circular, reset);
    }

    [Fact]
    public void Page_margin_box_border_radius_slash_form_rounds_the_band()
    {
        // border-radius-elliptical cycle — a margin box's `border-radius: <h> / <v>` rounds its band
        // (BorderRadiusShorthandExpander → the corner + internal `-y` longhands → MarginBoxStyle cascade
        // → ReadCornerRadii), with NO malformed diagnostic.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"H\"; " +
            "background-color: #3366cc; border-radius: 8px / 3px } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001
            && d.Message.Contains("border-radius"));
        var pdf = Latin1(result.Pdf);
        var i = pdf.IndexOf("0.2 0.4 0.8 rg", StringComparison.Ordinal);
        Assert.True(i >= 0);
        Assert.Contains(" c ", pdf[i..pdf.IndexOf('Q', i)]);   // rounded band (slash applied)
    }

    [Fact]
    public void Page_margin_box_border_radius_em_now_resolves_and_rounds()
    {
        // margin-box-relative-radius cycle — a font-relative `em` radius now RESOLVES (was deferred →
        // square): `2em` against the box's default 16px font = 32px (clamped to half the band) → a
        // rounded (Bézier) band, with NO diagnostic.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"H\"; " +
            "background-color: #3366cc; border-radius: 2em } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001
            && d.Message.Contains("border-radius"));
        var pdf = Latin1(result.Pdf);
        var i = pdf.IndexOf("0.2 0.4 0.8 rg", StringComparison.Ordinal);
        Assert.True(i >= 0);
        Assert.Contains(" c ", pdf[i..pdf.IndexOf('Q', i)]);   // em resolved → rounded band
    }

    [Fact]
    public void Page_margin_box_border_radius_percentage_rounds_the_band()
    {
        // margin-box-border-radius cycle — a `%` radius is now SUPPORTED (parity with the body): it
        // resolves against the box width/height (an ellipse), rounding the band, with NO diagnostic
        // (the first-cut "unsupported" surfacing is gone now per-corner/% cascade through).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"H\"; width: 80px; " +
            "background-color: #3366cc; border-radius: 50% } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001
            && d.Message.Contains("border-radius"));
        var pdf = Latin1(result.Pdf);
        var i = pdf.IndexOf("0.2 0.4 0.8 rg", StringComparison.Ordinal);
        Assert.True(i >= 0);
        Assert.Contains(" c ", pdf[i..pdf.IndexOf('Q', i)]);   // rounded (ellipse) band
    }

    [Fact]
    public void Page_margin_box_border_radius_per_corner_rounds_the_band()
    {
        // margin-box-border-radius cycle — a PER-CORNER shorthand (`8px 24px`) is now supported
        // (parity with the body): the band rounds with distinct corners (Bézier path, no diagnostic).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"H\"; width: 80px; " +
            "background-color: #3366cc; border-radius: 8px 24px } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001
            && d.Message.Contains("border-radius"));
        var pdf = Latin1(result.Pdf);
        var i = pdf.IndexOf("0.2 0.4 0.8 rg", StringComparison.Ordinal);
        Assert.True(i >= 0);
        Assert.Contains(" c ", pdf[i..pdf.IndexOf('Q', i)]);
    }

    [Fact]
    public void Page_margin_box_border_radius_calc_with_division_rounds_the_band()
    {
        // post-PR-#174 review P3 — a `/` inside `calc()` is a DIVISION the resolver evaluates, not the
        // elliptical separator: `calc(10px / 2)` = 5px rounds the band (parity with the body), with NO
        // diagnostic. Pre-fix the naive `Contains('/')` deferred it to square.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"H\"; width: 80px; " +
            "background-color: #3366cc; border-radius: calc(10px / 2) } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001
            && d.Message.Contains("border-radius"));
        var pdf = Latin1(result.Pdf);
        var i = pdf.IndexOf("0.2 0.4 0.8 rg", StringComparison.Ordinal);
        Assert.True(i >= 0);
        Assert.Contains(" c ", pdf[i..pdf.IndexOf('Q', i)]);   // rounded (calc evaluated to 5px)
    }

    [Theory]
    [InlineData("8px bogus")]   // a malformed token
    [InlineData("-5px")]        // negative (invalid per CSS B&B §6.1)
    [InlineData("calc(10px")]   // an unbalanced function
    public void Page_margin_box_border_radius_invalid_form_is_diagnosed(string value)
    {
        // post-PR-#174 review P2 — a MALFORMED / invalid margin-box border-radius is now SURFACED
        // (CSS-PROPERTY-VALUE-INVALID-001) instead of being silently dropped as an unknown raw
        // declaration; it still renders square (CLAUDE.md #7 — diagnostics, not silent corruption).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"H\"; width: 80px; " +
            "background-color: #3366cc; border-radius: " + value + " } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001
            && d.Message.Contains("border-radius"));
        var pdf = Latin1(result.Pdf);
        var i = pdf.IndexOf("0.2 0.4 0.8 rg", StringComparison.Ordinal);
        Assert.True(i >= 0);
        Assert.DoesNotContain(" c ", pdf[i..pdf.IndexOf('Q', i)]);   // square
    }

    // (The elliptical `Rx / Ry` slash form, formerly a documented square deferral here, now ROUNDS —
    //  border-radius-elliptical cycle — covered by Page_margin_box_border_radius_slash_form_rounds_the_band.)

    [Fact]
    public void Page_margin_box_border_radius_with_uniform_border_paints_a_ring()
    {
        // margin-box-border-radius cycle, Task 2 — a margin box with a border-radius AND a uniform
        // border paints a rounded RING (even-odd `f*`) for the border (free via FragmentPainter.
        // PaintBorders reading the now-cascaded corner longhands), instead of square edge rects.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"H\"; width: 80px; " +
            "border: 4px solid #000; border-radius: 8px } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains(" f* Q", pdf);               // the rounded border ring
    }

    [Fact]
    public void Page_margin_box_border_radius_rounds_the_image_clip()
    {
        // margin-box-border-radius cycle, Task 3 — a border-radius rounds the margin box's
        // background-image clip (a rounded clip path, not the rectangular `re W n`).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content: \"H\"; width: 80px; " +
            $"background-image: url({PngDataUri(16, 16)}); background-repeat: no-repeat; border-radius: 8px }} }}" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains(" W n", pdf);                // a clip is established
        Assert.DoesNotContain(" re W n", pdf);       // ... and it's the ROUNDED clip, not rectangular
    }

    [Fact]
    public void Page_margin_box_string_set_content_before_renders()
    {
        // content-pseudo cycle smoke — the string-set assignment composing content(before) +
        // content() resolves and the margin box paints its line.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>h1 { string-set: title content(before) content() } " +
            "h1::before { content: 'Ch. ' } " +
            "@page { @top-center { content: string(title) } }</style></head>" +
            "<body><h1>Intro</h1></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

        // The body renders the h1 + its ::before too, so the margin line ADDS one Td on top —
        // the meaningful assertions are the absence of the unsupported-content diagnostic (the
        // pre-cycle content(before) bail) and that text painted at all.
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssContentFunctionUnsupported001);
        Assert.True(TdCount(Latin1(result.Pdf)) >= 2);
    }

    [Fact]
    public void Page_margin_box_segment_decoration_paints_a_per_line_band()
    {
        // Segment-decor cycle — a leaf block's own background paints a band exactly ONE line tall
        // behind ITS line: the h1 (32px → 38.4px = 28.8pt line) gets a blue 28.8pt band; the
        // second (16px) line gets none.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh h1 { font-size: 32px; background-color: #3366cc } " +
            "@page { @top-center { content: element(rh) } }</style></head>" +
            "<body><div class=\"rh\"><h1>Title</h1><div>Sub</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        Assert.Contains("0.2 0.4 0.8 rg", pdf);                            // the h1's own blue
        Assert.Contains(AllRects(pdf), r => Math.Abs(r.H - 28.8) < 0.1);   // one 38.4px line band
    }

    [Fact]
    public void Body_percentage_width_sizes_the_background_band()
    {
        // Body-percent cycle — width: 50% of the 602px default content area = 301px → 225.75pt.
        var r = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:50%;height:20px;background-color:#3366cc\"></div>" +
            "</body></html>")));
        Assert.Equal(225.75, r.W, 1);
    }

    [Fact]
    public void Body_percentage_padding_shifts_the_text()
    {
        // padding-left: 10% of the 602px content area = 60.2px → a 45.15pt shift.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var without = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><div>AB</div></body></html>", opts)));
        var with_ = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>div { padding-left: 10% }</style></head>" +
            "<body><div>AB</div></body></html>", opts)));
        Assert.Equal(without.X + 45.15, with_.X, 1);
    }

    [Fact]
    public void Page_margin_box_segment_margin_inserts_a_collapsed_gap_between_lines()
    {
        // Segment-margins cycle — the second leaf block's margin-top: 16px inserts a gap, growing
        // the decorated element band: 19.2 + 16 + 19.2 = 54.4px → 40.8pt (no gap: 38.4px = 28.8pt).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); background-color: #cc3366 } " +
            ".rh .gap { margin-top: 16px } " +
            "@page { @top-center { content: element(rh); background-color: #3366cc } }</style></head>" +
            "<body><div class=\"rh\"><div>One</div><div class=\"gap\">Two</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        Assert.Contains(AllRects(pdf), r => Math.Abs(r.H - 40.8) < 0.1);
    }

    [Fact]
    public void Page_margin_box_segment_adjacent_margins_collapse_to_the_max()
    {
        // CSS 2.2 §8.3.1's adjoining case — margin-bottom: 20px meets margin-top: 16px → ONE
        // 20px gap (not 36): band = 19.2 + 20 + 19.2 = 58.4px → 43.8pt.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); background-color: #cc3366 } " +
            ".rh .a { margin-bottom: 20px } .rh .b { margin-top: 16px } " +
            "@page { @top-center { content: element(rh); background-color: #3366cc } }</style></head>" +
            "<body><div class=\"rh\"><div class=\"a\">One</div><div class=\"b\">Two</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        Assert.Contains(AllRects(pdf), r => Math.Abs(r.H - 43.8) < 0.1);
    }

    [Fact]
    public void Page_margin_box_segment_line_height_drives_its_own_pitch()
    {
        // Segment-line-height cycle — the h1's own line-height: 48px replaces its font × 1.2
        // pitch: band = 48 + 19.2 = 67.2px → 50.4pt (font-default pitch would be 38.4 + 19.2).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); background-color: #cc3366 } " +
            ".rh h1 { font-size: 32px; line-height: 48px } " +
            "@page { @top-center { content: element(rh); background-color: #3366cc } }</style></head>" +
            "<body><div class=\"rh\"><h1>Big</h1><div>Sub</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        Assert.Contains(AllRects(pdf), r => Math.Abs(r.H - 50.4) < 0.1);
    }

    [Fact]
    public void Page_margin_box_segment_unitless_line_height_multiplies_the_font()
    {
        // A unitless line-height multiplies the SEGMENT's font: 32px × 2 + 19.2 = 83.2px → 62.4pt.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); background-color: #cc3366 } " +
            ".rh h1 { font-size: 32px; line-height: 2 } " +
            "@page { @top-center { content: element(rh); background-color: #3366cc } }</style></head>" +
            "<body><div class=\"rh\"><h1>Big</h1><div>Sub</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        Assert.Contains(AllRects(pdf), r => Math.Abs(r.H - 62.4) < 0.1);
    }

    [Theory]
    [InlineData("0px")]
    [InlineData("0pt")]
    public void Page_margin_box_segment_zero_absolute_line_height_collapses_the_pitch(string zero)
    {
        // Post-PR-#163 review P2 — CSS line-height admits any NON-NEGATIVE value: an absolute
        // ZERO collapses the h1 line's pitch like the unitless 0 (band = 0 + 19.2px = 14.4pt),
        // instead of falling back to the 32px font default (would be 38.4 + 19.2 = 43.2pt).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); background-color: #cc3366 } " +
            ".rh h1 { font-size: 32px; line-height: " + zero + " } " +
            "@page { @top-center { content: element(rh); background-color: #3366cc } }</style></head>" +
            "<body><div class=\"rh\"><h1>Big</h1><div>Sub</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        Assert.Contains(AllRects(pdf), r => Math.Abs(r.H - 14.4) < 0.1);
    }

    [Fact]
    public void Page_margin_box_segment_uppercase_margin_unit_is_eaten_by_anglesharp()
    {
        // CANARY (post-PR-#163 review P3): CSS units are ASCII case-insensitive and the collector
        // normalizes them (CaptureSegmentMargins, matching SegmentLineHeightPx) — but
        // AngleSharp.Css 1.0.0-beta.144 DROPS `margin-top: 16PX` (uppercase unit on a known
        // property) before the cascade, so no gap can appear end-to-end: the band stays 38.4px
        // = 28.8pt. When an AngleSharp upgrade fixes the drop, THIS pin fails — flip it to the
        // 40.8pt gap expectation (the collector side is already correct).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); background-color: #cc3366 } " +
            ".rh .gap { margin-top: 16PX } " +
            "@page { @top-center { content: element(rh); background-color: #3366cc } }</style></head>" +
            "<body><div class=\"rh\"><div>One</div><div class=\"gap\">Two</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        Assert.Contains(AllRects(pdf), r => Math.Abs(r.H - 28.8) < 0.1);
        Assert.DoesNotContain(AllRects(pdf), r => Math.Abs(r.H - 40.8) < 0.1);
    }

    [Fact]
    public void Body_auto_margins_center_an_explicit_width_band()
    {
        // Auto-margins cycle — `width: 100px; margin: 0 auto` centres in the 602px content area:
        // x = 72 + (451.5 − 75) / 2 = 260.25pt; the band stays 75pt wide.
        var r = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:20px;margin:0 auto;background-color:#3366cc\"></div>" +
            "</body></html>")));
        Assert.Equal(75.0, r.W, 1);
        Assert.Equal(260.25, r.X, 1);
    }

    [Fact]
    public void Body_percentage_height_resolves_through_a_definite_chain()
    {
        // Percent-height cycle — a % height needs a DEFINITE ancestor chain (CSS 2.2 §10.5: a %
        // against an auto-height parent computes to auto, like browsers): with
        // `html, body { height: 100% }` the div's 25% resolves against the 931px page content
        // height = 232.75px → 174.56pt.
        var r = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>html, body { height: 100% }</style></head><body>" +
            "<div style=\"width:64px;height:25%;background-color:#3366cc\"></div>" +
            "</body></html>")));
        Assert.Equal(174.56, r.H, 1);
    }

    [Fact]
    public void Body_percentage_height_against_an_auto_parent_computes_to_auto()
    {
        // The counterpart pin: WITHOUT the definite chain the 25% computes to auto — the empty
        // div has no height, so no band paints (browser-equivalent).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:64px;height:25%;background-color:#3366cc\"></div>" +
            "</body></html>"));
        Assert.Empty(AllRects(pdf));   // post-PR-#164 review P3 — track filled rects, not raw spelling
    }

    [Fact]
    public void Page_margin_box_segment_vertical_padding_grows_its_line_band()
    {
        // Segment-padding cycle — the h1's own vertical padding grows ITS band/pitch:
        // the h1's one-line band is 38.4 + 8 + 8 = 54.4px → 40.8pt (background covers the
        // padding box), and the NESTED element band (the box co-declares a background, forcing
        // the nesting) totals (54.4 + 19.2)px = 73.6px → 55.2pt.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); background-color: #cc3366 } " +
            ".rh h1 { font-size: 32px; padding-top: 8px; padding-bottom: 8px; background-color: #3366cc } " +
            "@page { @top-center { content: element(rh); background-color: #00ff00 } }</style></head>" +
            "<body><div class=\"rh\"><h1>Big</h1><div>Sub</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        Assert.Contains(AllRects(pdf), r => Math.Abs(r.H - 55.2) < 0.1);   // the nested element band
        Assert.Contains(AllRects(pdf), r => Math.Abs(r.H - 40.8) < 0.1);   // the h1's padded line band
    }

    [Fact]
    public void Page_margin_box_wrapped_segment_pads_the_block_once_not_per_line()
    {
        // Post-PR-#164 review P2 — a leaf block WRAPPING under an explicit box width pads the
        // BLOCK once (pad-top above its first line, pad-bottom below its last), not every line.
        // Wrap-count-independent comparison: the padded render's element band exceeds the
        // unpadded one by EXACTLY 16px = 12pt (the per-line duplication bug added 12pt × lines).
        const string body =
            "<body><div class=\"rh\"><div>AB AB AB AB AB AB AB AB</div></div></body></html>";
        const string head =
            "<!DOCTYPE html><html><head><style>.rh {{ position: running(rh); background-color: #cc3366 }} " +
            "{0}@page {{ @top-center {{ content: element(rh); background-color: #00ff00; width: 60px }} }}" +
            "</style></head>";
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };

        double ElementBandH(string pdf)
        {
            // The pink element band is the tallest #cc3366 fill; identify rects by colour.
            var rects = AllRects(pdf);
            var colors = RectFillColors(pdf);
            var best = 0.0;
            for (var i = 0; i < rects.Count && i < colors.Count; i++)
                if (colors[i] == "0.8 0.2 0.4" && rects[i].H > best) best = rects[i].H;
            return best;
        }

        var unpadded = ElementBandH(Latin1(HtmlPdf.Convert(string.Format(head, "") + body, opts)));
        var padded = ElementBandH(Latin1(HtmlPdf.Convert(
            string.Format(head, ".rh div { padding-top: 8px; padding-bottom: 8px } ") + body, opts)));

        Assert.True(unpadded >= 28.8 - 0.1, $"expected a wrapped (≥2-line) band, got {unpadded}pt");
        Assert.Equal(unpadded + 12.0, padded, 1);   // ONE 16px padding, regardless of line count
    }

    [Fact]
    public void Body_percentage_height_overflowing_child_reserves_its_emitted_extent()
    {
        // Post-PR-#164 review P1 — the MEASURE pass resolves % heights like the EMIT pass: a
        // height:200% child of a definite 100px parent emits 200px tall, and the following
        // sibling must start BELOW it (pre-fix the measure read 0 → the cursor under-reserved
        // and the sibling overlapped).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>html, body { height: 100% }</style></head><body>" +
            "<div style=\"height:100px\"><div style=\"width:64px;height:200%;background-color:#3366cc\"></div></div>" +
            "<div style=\"width:64px;height:20px;background-color:#cc3366\"></div>" +
            "</body></html>"));

        var rects = AllRects(pdf);
        var blue = rects.Find(r => Math.Abs(r.H - 150.0) < 0.5);    // 200px → 150pt
        var pink = rects.Find(r => Math.Abs(r.H - 15.0) < 0.5);     // the 20px sibling
        Assert.True(blue.H > 0, "expected the 200% child band");
        Assert.True(pink.H > 0, "expected the sibling band");
        // PDF y-down flip: the sibling's TOP (Y + H) must be at/below the child's BOTTOM (Y).
        Assert.True(pink.Y + pink.H <= blue.Y + 0.5,
            $"sibling (top {pink.Y + pink.H}) must start below the % child (bottom {blue.Y})");
    }

    [Fact]
    public void Body_auto_margins_beside_a_float_keep_the_recursion_models_centering()
    {
        // Post-PR-#164 review P1 — the facade path lays the body out through the RECURSION,
        // which has NO float avoidance for in-flow siblings (a pre-existing cycle-1 gap, distinct
        // from the OUTER path's float-adjusted range — that consistency is pinned at the unit
        // level in BlockLayouterTests). Here the centred box ignores the float: x = 260.25pt
        // (the full-content-box centre).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"float:left;width:100px;height:40px\"></div>" +
            "<div style=\"width:100px;height:20px;margin:0 auto;background-color:#3366cc\"></div>" +
            "</body></html>"));

        var band = AllRects(pdf).Find(r => Math.Abs(r.W - 75.0) < 0.5);
        Assert.True(band.W > 0, "expected the centred band");
        Assert.Equal(260.25, band.X, 1);
    }

    [Fact]
    public void Body_zero_width_with_auto_margins_centers_the_border_box()
    {
        // Post-PR-#164 review P3 — `width: 0` is EXPLICIT (a zero-content border box), so
        // `border: 2px solid` + auto margins centre the 4px box: x = 72 + (451.5 − 3)/2
        // = 296.25pt; the border edges paint (the background is empty at width 0).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:0;height:20px;margin:0 auto;border:2px solid #3366cc\"></div>" +
            "</body></html>"));

        var rects = AllRects(pdf).FindAll(r => Math.Abs(r.W - 1.5) < 0.2);
        Assert.True(rects.Count >= 2, "expected the two vertical border edges");
        // The LEFT border edge (the minimum-X 1.5pt rect) sits at the centred border-box X.
        var leftX = double.MaxValue;
        foreach (var r in rects) leftX = Math.Min(leftX, r.X);
        Assert.Equal(296.25, leftX, 1);
    }

    [Fact]
    public void Page_margin_box_negative_segment_padding_is_ignored()
    {
        // Post-PR-#164 review P2 — a negative padding (invalid CSS) never shrinks the pitch:
        // the band stays the unpadded 2 × 19.2px = 28.8pt whether AngleSharp drops the
        // declaration upstream or the collector clamp catches it.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); background-color: #cc3366 } " +
            ".rh .neg { padding-top: -8px } " +
            "@page { @top-center { content: element(rh); background-color: #00ff00 } }</style></head>" +
            "<body><div class=\"rh\"><div class=\"neg\">One</div><div>Two</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        Assert.Contains(AllRects(pdf), r => Math.Abs(r.H - 28.8) < 0.1);
    }

    [Fact]
    public void Body_percent_width_with_auto_margins_centers()
    {
        // Post-PR-#164 review P3 coverage — the §10.3.3 gate composes with a PERCENT width:
        // width: 50% (301px = 225.75pt) centred in 602px → x = 72 + 112.875 = 184.875pt.
        var r = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:50%;height:20px;margin:0 auto;background-color:#3366cc\"></div>" +
            "</body></html>")));
        Assert.Equal(225.75, r.W, 1);
        Assert.Equal(184.88, r.X, 1);
    }

    [Fact]
    public void Body_text_bearing_auto_margin_block_centers_like_an_empty_one()
    {
        // Post-PR-#164 review P3 coverage — the INLINE-ONLY path's §10.3.3 wiring: a
        // text-bearing `width: 100px; margin: 0 auto` div's first glyph starts at the centred
        // content-box left (compare against the same div without auto margins, shifted by
        // (451.5 − 75)/2 = 188.25pt).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var plain = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><div style=\"width:100px\">AB</div></body></html>", opts)));
        var centred = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><div style=\"width:100px;margin:0 auto\">AB</div></body></html>", opts)));
        Assert.Equal(plain.X + 188.25, centred.X, 1);
    }

    [Fact]
    public void Page_margin_box_segment_horizontal_padding_insets_its_own_line()
    {
        // Hpadding cycle — the padded leaf's line starts 20px = 15pt right of its sibling
        // (both left-aligned in a 300px box); the unpadded line is unaffected.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh .pad { padding-left: 20px } " +
            "@page { @top-center { content: element(rh); width: 300px; text-align: left } }</style></head>" +
            "<body><div class=\"rh\"><div>AB</div><div class=\"pad\">AB</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        var tds = AllTdXs(pdf);
        Assert.True(tds.Count >= 2, "expected two margin lines");
        Assert.Equal(tds[0] + 15.0, tds[1], 1);
    }

    [Fact]
    public void Page_margin_box_segment_horizontal_padding_shrinks_the_aligned_extent()
    {
        // A right-aligned padded line ends padding-right SHORT of its unpadded sibling: the
        // second Td sits 15pt (20px) left of the first.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh .pad { padding-right: 20px } " +
            "@page { @top-center { content: element(rh); width: 300px; text-align: right } }</style></head>" +
            "<body><div class=\"rh\"><div>AB</div><div class=\"pad\">AB</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        var tds = AllTdXs(pdf);
        Assert.True(tds.Count >= 2, "expected two margin lines");
        Assert.Equal(tds[0] - 15.0, tds[1], 1);
    }

    [Fact]
    public void Page_margin_box_per_line_padding_overflow_is_clipped_and_diagnosed()
    {
        // PR #165 review P2 — the horizontal-overflow predicate measures each line's OCCUPIED
        // width (its left inset + advance), not the bare advance: an 80px segment padding-left
        // pushes the ≈57.6px line to ≈137.6px in the explicit 100px box → the promised clip
        // path + the width-phrased diagnostic fire (pre-fix the bare 57.6px advance passed).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh .pad { padding-left: 80px } " +
            "@page { @top-center { content: element(rh); width: 100px; text-align: left } }</style></head>" +
            "<body><div class=\"rh\"><div class=\"pad\">ABABAB</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);
        Assert.Contains(" re W n", pdf);                       // the clip path
        Assert.Equal(6, TotalGlyphCount(pdf));                 // glyphs still emitted under the clip
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.PaintMarginBoxContentOverflow001
            && d.Message.Contains("wider"));
    }

    [Fact]
    public void Page_margin_box_segment_horizontal_margin_insets_its_own_line()
    {
        // segment-hmargins cycle — the leaf's own margin-left shifts ITS line 20px = 15pt right
        // of its sibling (both left-aligned in the 300px box); the unmargined line is unaffected.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh .m { margin-left: 20px } " +
            "@page { @top-center { content: element(rh); width: 300px; text-align: left } }</style></head>" +
            "<body><div class=\"rh\"><div>AB</div><div class=\"m\">AB</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        var tds = AllTdXs(pdf);
        Assert.True(tds.Count >= 2, "expected two margin lines");
        Assert.Equal(tds[0] + 15.0, tds[1], 1);
    }

    [Fact]
    public void Page_margin_box_segment_margin_right_shrinks_the_aligned_extent()
    {
        // A right-aligned margined line ends margin-right SHORT of its unmargined sibling
        // (margins shrink the alignment extent like padding): the second Td sits 15pt left.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh .m { margin-right: 20px } " +
            "@page { @top-center { content: element(rh); width: 300px; text-align: right } }</style></head>" +
            "<body><div class=\"rh\"><div>AB</div><div class=\"m\">AB</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        var tds = AllTdXs(pdf);
        Assert.True(tds.Count >= 2, "expected two margin lines");
        Assert.Equal(tds[0] - 15.0, tds[1], 1);
    }

    [Fact]
    public void Page_margin_box_segment_horizontal_margin_insets_its_decoration_band()
    {
        // Margins sit OUTSIDE the leaf's border box — its per-line band starts after
        // margin-left and ends before margin-right: band X = box band X + 15pt, band width
        // (300 − 20 − 20)px = 195pt (padding, by contrast, stays INSIDE the band).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh .m { margin-left: 20px; margin-right: 20px; background-color: #ff0000 } " +
            "@page { @top-center { content: element(rh); width: 300px; text-align: left; " +
            "background-color: #eeeeee } }</style></head>" +
            "<body><div class=\"rh\"><div class=\"m\">AB</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        var rects = AllRects(pdf);
        Assert.True(rects.Count >= 2, "expected the box band + the segment band");
        Assert.Equal(rects[0].X + 15.0, rects[1].X, 1);   // after margin-left
        Assert.Equal(195.0, rects[1].W, 1);               // (300 − 40)px = 195pt
    }

    [Fact]
    public void Page_margin_box_segment_negative_horizontal_margin_clamps_to_zero()
    {
        // A negative horizontal margin would pull the line outside its box — clamped at 0
        // (capture-side, like the vertical gaps' overlap clamp): the line stays put.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh .m { margin-left: -10px } " +
            "@page { @top-center { content: element(rh); width: 300px; text-align: left } }</style></head>" +
            "<body><div class=\"rh\"><div>AB</div><div class=\"m\">AB</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        var tds = AllTdXs(pdf);
        Assert.True(tds.Count >= 2, "expected two margin lines");
        Assert.Equal(tds[0], tds[1], 1);
    }

    [Fact]
    public void Body_float_percentage_width_sizes_the_band()
    {
        // Float-percent cycle — float: left; width: 25% of the 602px content area = 150.5px
        // → 112.875pt.
        var r = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"float:left;width:25%;height:20px;background-color:#3366cc\"></div>" +
            "</body></html>")));
        Assert.Equal(112.88, r.W, 1);
    }

    [Fact]
    public void Body_border_box_sizing_keeps_the_declared_band_width()
    {
        // Body box-sizing cycle — width: 200px; padding: 20px; box-sizing: border-box → the band
        // IS 200px = 150pt (content-box would be 240px = 180pt).
        var r = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:200px;height:20px;padding:20px;box-sizing:border-box;" +
            "background-color:#3366cc\"></div>" +
            "</body></html>")));
        Assert.Equal(150.0, r.W, 1);
    }

    [Fact]
    public void Body_float_percentage_padding_insets_its_content_at_paint()
    {
        // PR #165 review P1 — the float's used % padding carries through to paint: the float's
        // child line starts padding-left = 10% of 602px = 60.2px = 45.15pt right of the float's
        // band edge (pre-fix the paint-side absolute-only read saw 0 → the text sat at the
        // border edge while the band was sized percent-aware).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"float:left;width:50%;padding-left:10%;height:40px;background-color:#3366cc\">" +
            "<div>AB</div></div>" +
            "</body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var r = FirstRect(pdf);
        var tds = AllTdXs(pdf);
        Assert.True(tds.Count >= 1, "expected the float's child line");
        Assert.Equal(r.X + 45.15, tds[0], 1);
    }

    [Fact]
    public void Body_border_box_auto_margins_centre_text_at_the_declared_width()
    {
        // PR #165 review P2 — the §10.3.3 distribution uses the SAME border-box width the
        // layout computes: width: 200px; padding: 20px; box-sizing: border-box; margin: 0 auto
        // in the 602px content area → leftover = 402 → margin-left = 201px; the line starts at
        // 201 + 20 (padding) = 221px = 165.75pt right of a plain left line. (The pre-fix
        // content-box mis-sizing distributed from 240px → 181 + 20 = 201px = 150.75pt.)
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div>AB</div>" +
            "<div style=\"width:200px;padding:20px;box-sizing:border-box;margin:0 auto\">AB</div>" +
            "</body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var tds = AllTdXs(pdf);
        Assert.True(tds.Count >= 2, "expected the plain + centred lines");
        Assert.Equal(tds[0] + 165.75, tds[1], 1);
    }

    [Fact]
    public void Body_explicit_zero_width_text_block_does_not_fill()
    {
        // PR #165 review P3 — `width: 0` is EXPLICIT (the legal zero), not auto: a text-bearing
        // zero-width box must NOT fall back to fill-width layout. Its zero content area fits no
        // line (the documented minimal route, same as the shipped border-box insets ≥ width
        // case) — no text paints.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:0;margin:0 auto;border:2px solid #000;padding:10px\">AB</div>" +
            "</body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Empty(AllTdXs(pdf));
    }

    // ---- the body image pipeline (img-pipeline cycle) ----

    /// <summary>A synthetic opaque-RGB PNG as a self-contained <c>data:</c> URI — the
    /// no-loader-needed default path the img pipeline ships with.</summary>
    private static string PngDataUri(int w, int h) =>
        "data:image/png;base64," + Convert.ToBase64String(
            NetPdf.UnitTests.Pdf.Images.SyntheticPng.BuildOpaqueRgb8(w, h));

    /// <summary>The (w, h, x, y) operands of every <c>q w 0 0 h x y cm /ImN Do Q</c> image
    /// placement, in content-stream order.</summary>
    private static List<(double W, double H, double X, double Y)> AllImagePlacements(string pdf)
    {
        var result = new List<(double, double, double, double)>();
        var idx = 0;
        while ((idx = pdf.IndexOf(" cm /Im", idx, StringComparison.Ordinal)) >= 0)
        {
            var opStart = pdf.LastIndexOf('q', idx);
            var nums = pdf[(opStart + 1)..idx].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            result.Add((
                double.Parse(nums[0], CultureInfo.InvariantCulture),
                double.Parse(nums[3], CultureInfo.InvariantCulture),
                double.Parse(nums[4], CultureInfo.InvariantCulture),
                double.Parse(nums[5], CultureInfo.InvariantCulture)));
            idx += 1;
        }
        return result;
    }

    [Fact]
    public void Img_data_uri_renders_at_its_intrinsic_size()
    {
        // img-pipeline cycle — a block-level <img> with a data: PNG renders with NO loader
        // configured: one XObject placement at the intrinsic 16×16 px = 12×12 pt.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(16, 16)}\" style=\"display:block\">" +
            "</body></html>"));
        var placements = AllImagePlacements(pdf);
        var p = Assert.Single(placements);
        Assert.Equal(12.0, p.W, 1);
        Assert.Equal(12.0, p.H, 1);
        Assert.Contains("/Subtype /Image", pdf);
    }

    [Fact]
    public void Inline_img_in_text_is_laid_out_and_painted()
    {
        // inline-atomic-boxes cycle — an INLINE <img> (default display) inside a line of text is no
        // longer skipped (LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001): it reserves its used size on the
        // line as an atomic and paints. 16×16 px = 12×12 pt placement, one XObject.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            $"<p>Hello <img src=\"{PngDataUri(16, 16)}\"> World</p>" +
            "</body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);
        var p = Assert.Single(AllImagePlacements(pdf));
        Assert.Equal(12.0, p.W, 1);
        Assert.Equal(12.0, p.H, 1);
        Assert.Contains("/Subtype /Image", pdf);
        // The handled inline <img> no longer raises the atomic-not-supported diagnostic.
        Assert.DoesNotContain(result.Warnings,
            d => d.Code == DiagnosticCodes.LayoutInlineAtomicNotSupported001);
    }

    [Fact]
    public void Inline_img_advances_with_preceding_text_on_the_line()
    {
        // inline flow — the img sits AFTER the leading text on the line, so more leading text shifts
        // its placement to the right (proves it participates in line layout, not anchored at x=0).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var near = AllImagePlacements(Latin1(HtmlPdf.Convert(
            $"<!DOCTYPE html><html><body><p>I<img src=\"{PngDataUri(16, 16)}\"></p></body></html>", opts)));
        var far = AllImagePlacements(Latin1(HtmlPdf.Convert(
            $"<!DOCTYPE html><html><body><p>IIIIIIIIII<img src=\"{PngDataUri(16, 16)}\"></p></body></html>", opts)));
        Assert.Single(near);
        Assert.Single(far);
        Assert.True(far[0].X > near[0].X + 5.0,
            $"img after more text should be further right: near.X={near[0].X}, far.X={far[0].X}");
    }

    [Fact]
    public void Inline_img_taller_than_text_grows_its_line_box()
    {
        // inline-atomic-boxes cycle — a tall inline <img> grows its line box (max(text line-height,
        // img height)), so a following BLOCK image is pushed further down the page (a smaller
        // bottom-up PDF Y). The block image is identified by its 12×12 pt size.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var tall = AllImagePlacements(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<p>x<img src=\"{PngDataUri(16, 16)}\" width=\"16\" height=\"64\"></p>" +
            $"<img src=\"{PngDataUri(16, 16)}\" style=\"display:block\">" +
            "</body></html>", opts)));
        var shortImg = AllImagePlacements(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<p>x<img src=\"{PngDataUri(16, 16)}\" width=\"16\" height=\"8\"></p>" +
            $"<img src=\"{PngDataUri(16, 16)}\" style=\"display:block\">" +
            "</body></html>", opts)));
        Assert.Equal(2, tall.Count);       // inline img + block img
        Assert.Equal(2, shortImg.Count);
        var tallBlock = tall.Find(q => Math.Abs(q.H - 12.0) < 1.0);
        var shortBlock = shortImg.Find(q => Math.Abs(q.H - 12.0) < 1.0);
        Assert.True(tallBlock.Y < shortBlock.Y - 20.0,
            $"tall inline img should push the block img down: tall.blockY={tallBlock.Y}, short.blockY={shortBlock.Y}");
    }

    [Fact]
    public void Inline_img_with_padding_reserves_advance_and_still_paints_content()
    {
        // post-PR-#186 review P1 — an inline <img> with padding reserves the MARGIN-box advance and emits
        // a BORDER-box fragment, so ImagePainter (which subtracts the img's own padding/border to get the
        // content box) still paints the content. Before the fix the fragment was content-sized →
        // ImagePainter subtracted the padding into a negative content box → no image painted.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var padded = AllImagePlacements(Latin1(HtmlPdf.Convert(
            $"<!DOCTYPE html><html><body><p>I<img src=\"{PngDataUri(16, 16)}\" style=\"padding:10px\">X</p></body></html>", opts)));
        var plain = AllImagePlacements(Latin1(HtmlPdf.Convert(
            $"<!DOCTYPE html><html><body><p>I<img src=\"{PngDataUri(16, 16)}\">X</p></body></html>", opts)));
        var p = Assert.Single(padded);      // content STILL painted (not subtracted into nothing)
        Assert.Single(plain);
        Assert.Equal(12.0, p.W, 1);          // the 16px content = 12pt, intact
        Assert.Equal(12.0, p.H, 1);
        // The padded img's content is inset by the 10px (7.5pt) left padding → further right than plain.
        Assert.True(p.X > plain[0].X + 5.0,
            $"padded img content should be inset by its padding: padded.X={p.X}, plain.X={plain[0].X}");
    }

    [Fact]
    public void Img_css_width_completes_height_from_the_intrinsic_ratio()
    {
        // §10.3.2 — width: 32px declared, height auto, intrinsic 16×16 → height completes via
        // the 1:1 ratio → a 24×24 pt placement.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(16, 16)}\" style=\"display:block;width:32px\">" +
            "</body></html>"));
        var p = Assert.Single(AllImagePlacements(pdf));
        Assert.Equal(24.0, p.W, 1);
        Assert.Equal(24.0, p.H, 1);
    }

    [Fact]
    public void Img_dimension_attributes_size_the_image()
    {
        // The HTML width/height attributes (CSS px integers) win over the intrinsic size when
        // no CSS width/height is declared: 48×24 px = 36×18 pt.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(16, 16)}\" width=\"48\" height=\"24\" style=\"display:block\">" +
            "</body></html>"));
        var p = Assert.Single(AllImagePlacements(pdf));
        Assert.Equal(36.0, p.W, 1);
        Assert.Equal(18.0, p.H, 1);
    }

    [Fact]
    public void Img_block_margin_auto_centres_the_image()
    {
        // The canonical centering idiom — display: block; margin: 0 auto. The centred image's
        // X sits (602 − 16) / 2 = 293 px = 219.75 pt right of the plain one's.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(16, 16)}\" style=\"display:block\">" +
            $"<img src=\"{PngDataUri(16, 16)}\" style=\"display:block;margin:0 auto\">" +
            "</body></html>"));
        var placements = AllImagePlacements(pdf);
        Assert.Equal(2, placements.Count);
        Assert.Equal(placements[0].X + 219.75, placements[1].X, 1);
    }

    [Fact]
    public void Img_fetch_failure_surfaces_res_load_failed()
    {
        // An http src under the SafeDefault policy (no http, no loader) fails the fetch — the
        // RES-LOAD-FAILED-001 diagnostic surfaces it and nothing paints (CLAUDE.md #7).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            "<img src=\"http://example.com/x.png\" style=\"display:block\">" +
            "</body></html>", new HtmlPdfOptions());
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.ResLoadFailed001);
        Assert.Empty(AllImagePlacements(Latin1(result.Pdf)));
    }

    [Fact]
    public void Img_undecodable_payload_surfaces_img_decode_failed()
    {
        // A data: payload that is no recognizable raster format → IMG-DECODE-FAILED-001 and no
        // placement (the element lays out at its declared size; nothing paints).
        var garbage = Convert.ToBase64String(Encoding.ASCII.GetBytes("not an image at all"));
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"data:image/png;base64,{garbage}\" style=\"display:block;width:20px;height:20px\">" +
            "</body></html>", new HtmlPdfOptions());
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.ImgDecodeFailed001);
        Assert.Empty(AllImagePlacements(Latin1(result.Pdf)));
    }

    // ---- object-fit (object-fit cycle) ----

    [Fact]
    public void Img_object_fit_contain_letterboxes_and_centres()
    {
        // A 16×16 (1:1) image in a 32×16 content box: contain → 16×16 px = 12pt square,
        // centred → X = the fill control's X + (32 − 16)/2 px = +6pt; no clip.
        var fill = AllImagePlacements(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(16, 16)}\" width=\"32\" height=\"16\" style=\"display:block\">" +
            "</body></html>")))[0];
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(16, 16)}\" width=\"32\" height=\"16\" " +
            "style=\"display:block;object-fit:contain\">" +
            "</body></html>"));
        var p = Assert.Single(AllImagePlacements(pdf));
        Assert.Equal(12.0, p.W, 1);
        Assert.Equal(12.0, p.H, 1);
        Assert.Equal(fill.X + 6.0, p.X, 1);
        Assert.DoesNotContain(" re W n", pdf);
    }

    [Fact]
    public void Img_object_fit_cover_fills_and_clips()
    {
        // cover in the 32×16 box scales the 1:1 image to 32×32 (24pt), centred — the overflow
        // clips at the content box.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(16, 16)}\" width=\"32\" height=\"16\" " +
            "style=\"display:block;object-fit:cover\">" +
            "</body></html>"));
        var p = Assert.Single(AllImagePlacements(pdf));
        Assert.Equal(24.0, p.W, 1);
        Assert.Equal(24.0, p.H, 1);
        Assert.Contains(" re W n", pdf);
    }

    [Fact]
    public void Img_object_fit_none_and_scale_down_use_the_intrinsic_size()
    {
        // none keeps the 8×8 intrinsic (6pt) centred in the 32×16 box; scale-down picks the
        // SMALLER of none/contain — 8×8 stays 6pt, a 64×64 image contains down to 12pt.
        var none = AllImagePlacements(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(8, 8)}\" width=\"32\" height=\"16\" " +
            "style=\"display:block;object-fit:none\">" +
            "</body></html>")))[0];
        Assert.Equal(6.0, none.W, 1);
        Assert.Equal(6.0, none.H, 1);

        var small = AllImagePlacements(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(8, 8)}\" width=\"32\" height=\"16\" " +
            "style=\"display:block;object-fit:scale-down\">" +
            "</body></html>")))[0];
        Assert.Equal(6.0, small.W, 1);

        var big = AllImagePlacements(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(64, 64)}\" width=\"32\" height=\"16\" " +
            "style=\"display:block;object-fit:scale-down\">" +
            "</body></html>")))[0];
        Assert.Equal(12.0, big.W, 1);
    }

    [Fact]
    public void Img_object_fit_unknown_value_falls_back_to_fill()
    {
        // AngleSharp validates object-fit's keyword grammar and DROPS `bogus` at parse (the
        // known beta drop boundary), so the cascade winner is null → the initial `fill`. The
        // painter's own unknown-value fallback (+ CSS-PROPERTY-VALUE-INVALID-001) stays as
        // defense-in-depth for raw values arriving via recovery paths.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(16, 16)}\" width=\"32\" height=\"16\" " +
            "style=\"display:block;object-fit:bogus\">" +
            "</body></html>", new HtmlPdfOptions());
        var p = Assert.Single(AllImagePlacements(Latin1(result.Pdf)));
        Assert.Equal(24.0, p.W, 1);   // fill — the full 32px content box
    }

    // ---- object-position (object-position cycle) ----

    [Fact]
    public void Img_object_position_left_top_anchors_to_the_content_box_corner()
    {
        // object-fit:none keeps the 16×16 (12pt) image in a 48×48 (36pt) content box → 24pt of
        // slack each axis. The centred default (the initial 50% 50%) sits +12pt in from the
        // top-left corner; `left top` pins it to the corner → 12pt left of + 12pt above (a LARGER
        // PDF y is higher) the centre.
        var centre = AllImagePlacements(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(16, 16)}\" width=\"48\" height=\"48\" " +
            "style=\"display:block;object-fit:none\">" +
            "</body></html>")))[0];
        var topLeft = AllImagePlacements(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(16, 16)}\" width=\"48\" height=\"48\" " +
            "style=\"display:block;object-fit:none;object-position:left top\">" +
            "</body></html>")))[0];
        Assert.Equal(centre.X - 12.0, topLeft.X, 1);
        Assert.Equal(centre.Y + 12.0, topLeft.Y, 1);
    }

    [Fact]
    public void Img_object_position_right_bottom_and_percent_pin_the_far_corner()
    {
        // `right bottom` and `100% 100%` both pin the object to the far corner: +12pt right of
        // and 12pt below (a SMALLER PDF y) the centre — and the two spellings agree.
        var centre = AllImagePlacements(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(16, 16)}\" width=\"48\" height=\"48\" " +
            "style=\"display:block;object-fit:none\">" +
            "</body></html>")))[0];
        var rightBottom = AllImagePlacements(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(16, 16)}\" width=\"48\" height=\"48\" " +
            "style=\"display:block;object-fit:none;object-position:right bottom\">" +
            "</body></html>")))[0];
        var percent = AllImagePlacements(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(16, 16)}\" width=\"48\" height=\"48\" " +
            "style=\"display:block;object-fit:none;object-position:100% 100%\">" +
            "</body></html>")))[0];
        Assert.Equal(centre.X + 12.0, rightBottom.X, 1);
        Assert.Equal(centre.Y - 12.0, rightBottom.Y, 1);
        Assert.Equal(rightBottom.X, percent.X, 1);
        Assert.Equal(rightBottom.Y, percent.Y, 1);
    }

    [Fact]
    public void Img_object_position_length_pair_offsets_from_the_origin()
    {
        // `10px 5px` places the object 10px right / 5px down from the content box's top-left
        // origin → +7.5pt X and 3.75pt below (a smaller PDF y) the corner-pinned placement.
        var topLeft = AllImagePlacements(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(16, 16)}\" width=\"48\" height=\"48\" " +
            "style=\"display:block;object-fit:none;object-position:left top\">" +
            "</body></html>")))[0];
        var offset = AllImagePlacements(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(16, 16)}\" width=\"48\" height=\"48\" " +
            "style=\"display:block;object-fit:none;object-position:10px 5px\">" +
            "</body></html>")))[0];
        Assert.Equal(topLeft.X + 7.5, offset.X, 1);
        Assert.Equal(topLeft.Y - 3.75, offset.Y, 1);
    }

    [Fact]
    public void Img_object_position_unsupported_form_falls_back_to_centre()
    {
        // `left 10px right 5px` is an INVALID <position> (two X-axis edges). AngleSharp drops it at
        // parse (so the cascade winner is null) and the painter's §3.6 parser would reject it too →
        // the object centres (the initial 50% 50%), matching the unset placement. (The 4-value
        // edge-offset form `left 10px top 5px` is now SUPPORTED — see the parser tests — but stays
        // unreachable via the facade, since AngleSharp drops it; the painter's fallback +
        // CSS-PROPERTY-VALUE-INVALID-001 stay defense-in-depth for raw-recovery paths.)
        var centre = AllImagePlacements(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(16, 16)}\" width=\"48\" height=\"48\" " +
            "style=\"display:block;object-fit:none\">" +
            "</body></html>")))[0];
        var p = Assert.Single(AllImagePlacements(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{PngDataUri(16, 16)}\" width=\"48\" height=\"48\" " +
            "style=\"display:block;object-fit:none;object-position:left 10px right 5px\">" +
            "</body></html>"))));
        Assert.Equal(centre.X, p.X, 1);
        Assert.Equal(centre.Y, p.Y, 1);
    }

    [Fact]
    public void Object_position_invalid_diagnostic_sanitizes_the_raw_value()
    {
        // PR #169 review P3 - the raw object-position flows through DiagnosticTextSanitizer
        // before it reaches a diagnostics sink: C0/C1 control chars are redacted (U+FFFD) and the
        // value is length-capped (120 chars + ellipsis). The painter's diagnostic path is reachable
        // only via raw-recovery (AngleSharp drops an invalid object-position upstream), so this pins
        // the sanitization at the message builder directly.
        var crafted = "\u001b[31m\u0000" + new string('x', 500);   // ANSI ESC + NUL + bloat
        var msg = NetPdf.Rendering.ImagePainter.BuildInvalidObjectPositionDiagnostic(crafted);
        // The message embeds EXACTLY the sanitizer's output.
        Assert.Contains(NetPdf.Css.Diagnostics.DiagnosticTextSanitizer.Sanitize(crafted), msg);
        Assert.Contains("object-position value", msg);
        // Ordinal char search - Assert.DoesNotContain on a control-char STRING is culture-sensitive
        // and treats control chars as ignorable (it would spuriously "find" them).
        Assert.False(msg.Contains('\u001b'), "ESC must be redacted");
        Assert.False(msg.Contains('\u0000'), "NUL must be redacted");
        Assert.Contains("\uFFFD", msg);                       // the redaction marker is present
        Assert.Contains("\u2026", msg);                       // the truncation ellipsis (input > 120)
    }

    // ---- background-image: url(...) (bg-image cycle) ----

    [Fact]
    public void Background_image_tiles_over_the_border_box()
    {
        // bg-image cycle — a 16×16 px tile repeats over the 64×32 px box (the initial
        // background-repeat) → 4 × 2 = 8 placements of one shared XObject, clipped to the
        // border box; the tiles sit over the background-color, under the text.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:64px;height:32px;background-image:url({PngDataUri(16, 16)});" +
            "background-color:#3366cc\"></div>" +
            "</body></html>"));
        var placements = AllImagePlacements(pdf);
        Assert.Equal(8, placements.Count);
        Assert.Equal(12.0, placements[0].W, 1);
        Assert.Contains(" re W n", pdf);   // partial-tile clip at the border box
        Assert.Contains(" re f", pdf);     // the color band still paints under the tiles
    }

    [Fact]
    public void Background_image_respects_print_backgrounds_off()
    {
        // Gated by PrintBackgrounds exactly like the color band.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:64px;height:32px;background-image:url({PngDataUri(16, 16)})\"></div>" +
            "</body></html>", new HtmlPdfOptions { PrintBackgrounds = false }));
        Assert.Empty(AllImagePlacements(pdf));
    }

    [Fact]
    public void Background_image_linear_gradient_paints_as_shading_over_the_color()
    {
        // Phase 4 gradients — a linear-gradient(...) now paints as a PDF native axial
        // shading (no longer surfaces CSS-BACKGROUND-IMAGE-UNSUPPORTED-001); the
        // background-color still paints UNDER it (CSS B&B §14.2 layer order).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:64px;height:32px;background-image:linear-gradient(red, blue);" +
            "background-color:#3366cc\"></div>" +
            "</body></html>", new HtmlPdfOptions());
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssBackgroundImageUnsupported001);
        var pdf = Latin1(result.Pdf);
        Assert.Contains("/ShadingType 2", pdf);          // the gradient shading
        Assert.Contains(" sh", pdf);                     // painted
        Assert.Contains("0.2 0.4 0.8 rg", pdf);          // #3366cc background-color underneath
        Assert.Contains(" re f", pdf);
    }

    [Fact]
    public void Background_image_large_tilings_emit_one_pattern_fill()
    {
        // tiling-patterns cycle — a 1×1 px tile over a 100×100 px box (10,000 tiles, formerly
        // the 4096-cap skip) now paints as ONE tiling-pattern fill: a /PatternType 1 object +
        // a /Pattern cs … scn … re f fill; no per-tile placements in the PAGE stream (the one
        // `cm /ImP Do` lives inside the pattern's own cell stream), and no diagnostic.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:100px;height:100px;background-image:url({PngDataUri(1, 1)});" +
            "background-color:#3366cc\"></div>" +
            "</body></html>", new HtmlPdfOptions());
        var pdf = Latin1(result.Pdf);
        Assert.Contains("/PatternType 1", pdf);
        Assert.Contains("/Pattern cs /P1 scn", pdf);
        Assert.DoesNotContain(result.Warnings, d => d.Code.StartsWith("PAINT-BG-IMAGE", StringComparison.Ordinal));
        Assert.Contains(" re f", pdf);   // the pattern fill + the color band
    }

    [Fact]
    public void Background_image_huge_boxes_pattern_fill_is_overflow_safe()
    {
        // PR #166 review P1 lineage — a 4e9 × 4e9 px box with a 1×1 tile (~1.6e19 tiles) must
        // not enter a placement loop. The overflow-safe threshold compare routes it to the
        // O(1) pattern fill.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:4000000000px;height:4000000000px;background-image:url({PngDataUri(1, 1)})\"></div>" +
            "</body></html>", new HtmlPdfOptions());
        var pdf = Latin1(result.Pdf);
        Assert.Contains("/PatternType 1", pdf);
        Assert.Contains(" scn", pdf);
    }

    [Fact]
    public void Background_image_pattern_fill_preserves_the_position_phase()
    {
        // The pattern's /Matrix anchors the grid at the phased first tile: position 8px with
        // repeat over a 400×32 box (25 × 2 = 50 tiles > the 16-tile loop threshold) → one
        // pattern whose cell is 12pt and whose anchor X sits at the −8px phase column
        // (firstX = 8 − 16 = −8px = −6pt from the box's left edge).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:400px;height:32px;background-image:url({PngDataUri(16, 16)});" +
            "background-position:8px 0;background-color:#3366cc\"></div>" +
            "</body></html>"));
        Assert.Contains("/PatternType 1", pdf);
        var band = FirstRect(pdf);
        // The pattern Matrix: [1 0 0 1 tx ty] — tx = band.X − 6pt.
        var idx = pdf.IndexOf("/Matrix [1 0 0 1 ", StringComparison.Ordinal);
        Assert.True(idx > 0, "expected the pattern Matrix");
        var tail = pdf[(idx + "/Matrix [1 0 0 1 ".Length)..];
        var tx = double.Parse(tail[..tail.IndexOf(' ')], CultureInfo.InvariantCulture);
        Assert.Equal(band.X - 6.0, tx, 1);
    }

    [Fact]
    public void Background_image_multi_layer_list_is_surfaced_not_misfetched()
    {
        // PR #166 review P2 — url(a),url(b) must take the unsupported-multi-layer path, not
        // parse as the single bogus URL "a),url(b" (which produced misleading fetch failures).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:32px;height:32px;background-image:url(a.png),url(b.png);" +
            "background-color:#3366cc\"></div>" +
            "</body></html>", new HtmlPdfOptions());
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssBackgroundImageUnsupported001);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.ResLoadFailed001);
        Assert.Empty(AllImagePlacements(Latin1(result.Pdf)));
    }

    // ---- background-position / -size / -repeat (bg-variants cycle) ----

    [Fact]
    public void Background_no_repeat_places_one_tile()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:64px;height:32px;background-image:url({PngDataUri(16, 16)});" +
            "background-repeat:no-repeat\"></div>" +
            "</body></html>"));
        Assert.Single(AllImagePlacements(pdf));
    }

    [Fact]
    public void Background_repeat_x_tiles_one_row()
    {
        // 64px / 16px = 4 columns, one row.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:64px;height:32px;background-image:url({PngDataUri(16, 16)});" +
            "background-repeat:repeat-x\"></div>" +
            "</body></html>"));
        Assert.Equal(4, AllImagePlacements(pdf).Count);
    }

    [Fact]
    public void Background_size_explicit_scales_the_tile()
    {
        // background-size: 32px 32px over 64×32 → 2 × 1 tiles, each 24pt wide.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:64px;height:32px;background-image:url({PngDataUri(16, 16)});" +
            "background-size:32px 32px\"></div>" +
            "</body></html>"));
        var placements = AllImagePlacements(pdf);
        Assert.Equal(2, placements.Count);
        Assert.Equal(24.0, placements[0].W, 1);
        Assert.Equal(24.0, placements[0].H, 1);
    }

    [Fact]
    public void Background_size_contain_and_cover_scale_aspect_preserving()
    {
        // contain in 64×32 with a 1:1 16×16 image → ×2 → 32×32 tile → 2 placements (it still
        // repeats); cover → ×4 → 64×64 tile → 1 placement.
        var contain = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:64px;height:32px;background-image:url({PngDataUri(16, 16)});" +
            "background-size:contain\"></div>" +
            "</body></html>"));
        var containPlacements = AllImagePlacements(contain);
        Assert.Equal(2, containPlacements.Count);
        Assert.Equal(24.0, containPlacements[0].W, 1);

        var cover = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:64px;height:32px;background-image:url({PngDataUri(16, 16)});" +
            "background-size:cover\"></div>" +
            "</body></html>"));
        var coverPlacements = AllImagePlacements(cover);
        Assert.Single(coverPlacements);
        Assert.Equal(48.0, coverPlacements[0].W, 1);
    }

    [Fact]
    public void Background_position_center_offsets_a_no_repeat_tile()
    {
        // §3.6: center → (64 − 16) / 2 = 24px = 18pt right of the band's left edge.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:64px;height:32px;background-image:url({PngDataUri(16, 16)});" +
            "background-repeat:no-repeat;background-position:center;background-color:#3366cc\"></div>" +
            "</body></html>"));
        var band = FirstRect(pdf);
        var p = Assert.Single(AllImagePlacements(pdf));
        Assert.Equal(band.X + 18.0, p.X, 1);
    }

    [Fact]
    public void Background_position_lengths_offset_the_tile()
    {
        // 8px 4px → +6pt X; the tile drops 4px = 3pt, so its PDF (bottom-left) y is 3pt lower.
        var zero = AllImagePlacements(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:64px;height:32px;background-image:url({PngDataUri(16, 16)});" +
            "background-repeat:no-repeat\"></div>" +
            "</body></html>")))[0];
        var offset = AllImagePlacements(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:64px;height:32px;background-image:url({PngDataUri(16, 16)});" +
            "background-repeat:no-repeat;background-position:8px 4px\"></div>" +
            "</body></html>")))[0];
        Assert.Equal(zero.X + 6.0, offset.X, 1);
        Assert.Equal(zero.Y - 3.0, offset.Y, 1);
    }

    [Fact]
    public void Page_margin_box_background_position_edge_offsets_place_the_tile()
    {
        // edge-offset cycle — a margin box's raw `background-position: left 10px top 5px` reaches
        // the §3.6 parser INTACT (margin-box bodies bypass AngleSharp, unlike a body block whose
        // 4-value position the beta splits/drops), placing the no-repeat tile 10px from the band's
        // left + 5px from its top: +7.5pt X, 3.75pt lower (smaller PDF y) than the top-left default.
        string Build(string posDecl) =>
            "<!DOCTYPE html><html><head><style>@page { margin: 32px; " +
            $"@top-center {{ content: \"AB\"; width: 64px; background-image: url({PngDataUri(16, 16)}); " +
            "background-repeat: no-repeat" + posDecl + " } }" +
            "</style></head><body></body></html>";
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var zero = AllImagePlacements(Latin1(HtmlPdf.Convert(Build(""), opts)))[0];
        var offset = AllImagePlacements(Latin1(HtmlPdf.Convert(
            Build("; background-position: left 10px top 5px"), opts)))[0];
        Assert.Equal(zero.X + 7.5, offset.X, 1);
        Assert.Equal(zero.Y - 3.75, offset.Y, 1);
    }

    [Fact]
    public void Background_position_phases_a_repeating_grid()
    {
        // position 8px with repeat: the first column starts at 8 − 16 = −8px, so
        // ceil((64 + 8) / 16) = 5 columns × 2 rows = 10 placements (edge tiles clipped).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:64px;height:32px;background-image:url({PngDataUri(16, 16)});" +
            "background-position:8px 0\"></div>" +
            "</body></html>"));
        Assert.Equal(10, AllImagePlacements(pdf).Count);
    }

    [Fact]
    public void Background_origin_shifts_the_positioning_area()
    {
        // bg-origin cycle — a 4px-border + 8px-padding box: the no-repeat tile anchors at the
        // background-origin box. border-box → the box edge; padding-box (the initial) → +4px (the
        // border) = +3pt; content-box → +12px (border+padding) = +9pt.
        string Build(string origin) =>
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:80px;height:80px;border:4px solid #000;padding:8px;" +
            $"background-image:url({PngDataUri(16, 16)});background-repeat:no-repeat;" +
            $"background-origin:{origin}\"></div>" +
            "</body></html>";
        var border = AllImagePlacements(Latin1(HtmlPdf.Convert(Build("border-box"))))[0];
        var padding = AllImagePlacements(Latin1(HtmlPdf.Convert(Build("padding-box"))))[0];
        var content = AllImagePlacements(Latin1(HtmlPdf.Convert(Build("content-box"))))[0];
        Assert.Equal(border.X + 3.0, padding.X, 1);    // +4px border
        Assert.Equal(border.X + 9.0, content.X, 1);    // +12px border + padding
    }

    [Fact]
    public void Background_clip_bounds_the_paint_area()
    {
        // bg-clip cycle — the SAME box (border box 80 + 16 padding + 8 border = 104px = 78pt). The
        // tile clip rect shrinks to the background-clip box: border-box (the initial) → 78pt;
        // padding-box → 72pt (−4px/side, the border); content-box → 60pt (−12px/side).
        string Build(string clip) =>
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:80px;height:80px;border:4px solid #000;padding:8px;" +
            $"background-image:url({PngDataUri(16, 16)});background-repeat:no-repeat;" +
            $"background-clip:{clip}\"></div>" +
            "</body></html>";
        Assert.Equal(78.0, FirstClipRect(Latin1(HtmlPdf.Convert(Build("border-box")))).W, 1);
        Assert.Equal(72.0, FirstClipRect(Latin1(HtmlPdf.Convert(Build("padding-box")))).W, 1);
        Assert.Equal(60.0, FirstClipRect(Latin1(HtmlPdf.Convert(Build("content-box")))).W, 1);
    }

    [Fact]
    public void Background_repeat_tiles_the_clip_border_strip_loop_path()
    {
        // PR #170 review P1 — with the default background-origin: padding-box + clip: border-box, a
        // repeated grid must tile the BORDER strip (the clip), not just the padding box. 16px content
        // + 8px padding + 4px border → padding box 32px (2 tiles), border box 40px. repeat-x: a tile
        // at −12px and +36px also span the 4px border strips → 4 columns cover the clip (was 2).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:16px;height:16px;border:4px solid #000;padding:8px;" +
            $"background-image:url({PngDataUri(16, 16)});background-repeat:repeat-x\"></div>" +
            "</body></html>"));
        Assert.Equal(4, AllImagePlacements(pdf).Count);   // the border box, not just the padding box
    }

    [Fact]
    public void Background_repeat_tiles_the_clip_border_strip_pattern_path()
    {
        // PR #170 review P1 — the same default-origin/clip border/padding case on the PATTERN path
        // (a 1×1 tile → a huge grid): the single pattern FILL must span the BORDER box (the clip),
        // not the padding box. 80px content + 8px padding + 4px border → border box 104px = 78pt.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:80px;height:80px;border:4px solid #000;padding:8px;" +
            $"background-image:url({PngDataUri(1, 1)});background-repeat:repeat\"></div>" +
            "</body></html>"));
        Assert.Contains("/PatternType 1", pdf);
        var fill = AllRects(pdf)[0];   // the pattern fill paints first, before the border edges
        Assert.Equal(78.0, fill.W, 1);
        Assert.Equal(78.0, fill.H, 1);
    }

    [Fact]
    public void Background_color_with_border_radius_rounds_the_band()
    {
        // body-radius cycle — a uniform border-radius rounds the background band: the fill becomes a
        // rounded-rect PATH (Bézier-curve corners), NOT a plain `re f` rectangle. (Confirms the
        // newly-registered border-*-radius longhands compute from the `border-radius` shorthand.)
        var square = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;background-color:#3366cc\"></div>" +
            "</body></html>"));
        var rounded = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;border-radius:8px;background-color:#3366cc\"></div>" +
            "</body></html>"));

        // Target the EXACT 100×60px → 75×45pt #3366cc (0.2 0.4 0.8) band rather than asserting
        // DoesNotContain(" re f") across the whole stream (which any unrelated rect fill would break —
        // post-PR-#171 review P3). The square band is a plain rectangle fill of that size + colour…
        static bool IsBand((double X, double Y, double W, double H) r) =>
            Math.Abs(r.W - 75.0) < 0.5 && Math.Abs(r.H - 45.0) < 0.5;
        var squareRects = AllRects(square);
        var squareColors = RectFillColors(square);
        var bandIdx = squareRects.FindIndex(IsBand);
        Assert.True(bandIdx >= 0, "expected a 75×45pt rectangle fill (the square band)");
        Assert.Equal("0.2 0.4 0.8", squareColors[bandIdx]);

        // …while the rounded band is NO LONGER that exact rectangle (it's a Bézier-curve PATH).
        Assert.DoesNotContain(AllRects(rounded), IsBand);
        Assert.Contains(" c ", rounded);             // ... with Bézier-curve corners
    }

    [Fact]
    public void Border_radius_per_corner_rounds_the_band_with_distinct_corners()
    {
        // border-radius-completion cycle, Task 1 — a per-corner shorthand (8px 24px → TL=BR=8, TR=BL=24)
        // rounds the 75×45pt #3366cc band as a Bézier PATH, not the uniform single-radius path and not
        // a plain rectangle. The path starts at the bottom edge right of the BL corner (x + 18pt).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;border-radius:8px 24px;background-color:#3366cc\"></div>" +
            "</body></html>"));
        Assert.DoesNotContain(AllRects(pdf), r => Math.Abs(r.W - 75.0) < 0.5 && Math.Abs(r.H - 45.0) < 0.5);
        Assert.Contains(" c ", pdf);                 // Bézier corners
        Assert.Contains("0.2 0.4 0.8 rg", pdf);      // the band colour
    }

    [Fact]
    public void Border_radius_percentage_rounds_the_band_as_an_ellipse()
    {
        // border-radius-completion cycle, Task 1 — `50%` on a non-square box resolves to rx = 50% of
        // width, ry = 50% of height (an ellipse, CSS B&B §4.1): the band paints as a curve path, never
        // a square rect (pre-cycle a percentage was deferred → square).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;border-radius:50%;background-color:#3366cc\"></div>" +
            "</body></html>"));
        Assert.DoesNotContain(AllRects(pdf), r => Math.Abs(r.W - 75.0) < 0.5 && Math.Abs(r.H - 45.0) < 0.5);
        Assert.Contains(" c ", pdf);
    }

    [Fact]
    public void Border_radius_with_uniform_border_paints_a_rounded_ring()
    {
        // border-radius-completion cycle, Task 2 — a uniform border + a border-radius paints ONE filled
        // ring (the annulus between the border box and the padding box, even-odd `f*`) in the border
        // colour, instead of four square edge rects. A FILL (rg), never a stroke (no S / RG) — so the
        // outer corner is exact and the alpha composites correctly (post-PR-#172 review P1+P2).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;border-radius:8px;border:4px solid #000;" +
            "background-color:#3366cc\"></div>" +
            "</body></html>"));
        Assert.Contains(" f* Q", pdf);               // the even-odd ring fill
        Assert.Contains("0 0 0 rg", pdf);            // the black border, FILLED
        Assert.DoesNotContain(" S Q", pdf);          // not a stroke (no /CA pitfall)
    }

    [Fact]
    public void Border_radius_small_radius_thick_border_keeps_outer_rounding()
    {
        // border-radius-completion cycle, Task 2 (post-PR-#172 review P2) — a SMALL radius under a THICK
        // border (radius 2px, border 4px) still rounds the OUTER corner (the ring's outer path is exact
        // for any width; a centerline stroke would have lost it). The ring paints (f*), not square edges.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;border-radius:2px;border:4px solid #000\"></div>" +
            "</body></html>"));
        Assert.Contains(" f* Q", pdf);               // the rounded ring still paints
        Assert.DoesNotContain(" re f Q", pdf);       // ... not square per-edge fills (the inner `re f*` is part of the ring)
    }

    [Fact]
    public void Border_radius_semi_transparent_border_composites_alpha()
    {
        // border-radius-completion cycle, Task 2 (post-PR-#172 review P1) — a semi-transparent rounded
        // border composites via the FILL constant-alpha /ca (the ring is a fill). rgba(0,0,0,.5) →
        // 8-bit-quantized 128/255 = 0.501961. Pre-fix the centerline stroke wrongly used /ca for a
        // stroke (which needs /CA) → fully opaque.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;border-radius:8px;border:4px solid rgba(0,0,0,0.5)\"></div>" +
            "</body></html>"));
        Assert.Contains("/ca 0.501961", pdf);        // fill alpha for the border ring
        Assert.Contains(" f* Q", pdf);
    }

    [Fact]
    public void Border_radius_uniform_border_mixed_units_still_rings()
    {
        // border-radius-completion cycle, Task 2 (post-PR-#172 review P2) — equivalent border widths
        // expressed in DIFFERENT units (1px ≡ 0.75pt) are treated as uniform via a tolerance (not exact
        // `==`), so the rounded ring still paints rather than silently falling back to square edges.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;border-radius:8px;border-style:solid;border-color:#000;" +
            "border-top-width:1px;border-right-width:0.75pt;border-bottom-width:1px;border-left-width:0.75pt\"></div>" +
            "</body></html>"));
        Assert.Contains(" f* Q", pdf);               // mixed-but-equal widths → still the rounded ring
        Assert.DoesNotContain(" re f", pdf);
    }

    [Fact]
    public void Non_uniform_border_with_radius_falls_back_to_square_edges()
    {
        // border-radius-completion cycle, Task 2 — a NON-uniform border (different per-edge widths)
        // can't be one ring, so it falls back to the per-edge square rects (no ring fill). The radius
        // still rounds the background band.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;border-radius:8px;border-style:solid;border-color:#f00;" +
            "border-top-width:6px;border-right-width:2px;border-bottom-width:6px;border-left-width:2px\"></div>" +
            "</body></html>"));
        Assert.DoesNotContain(" f* Q", pdf);         // no rounded ring
        Assert.Contains(" re f", pdf);               // per-edge square border rects
    }

    [Fact]
    public void Border_radius_rounds_the_background_image_clip()
    {
        // border-radius-completion cycle, Task 3 — a border-radius rounds the background-image CLIP: a
        // no-repeat tile is clipped by a rounded path (curve operators before `W n`), not the
        // rectangular `re W n`.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:100px;height:60px;border-radius:8px;background-image:url({PngDataUri(16, 16)});" +
            "background-repeat:no-repeat\"></div>" +
            "</body></html>"));
        Assert.Contains(" W n", pdf);                // a clip is established
        Assert.DoesNotContain(" re W n", pdf);       // ... and it is the ROUNDED clip, not rectangular
        Assert.Single(AllImagePlacements(pdf));      // the tile still paints
    }

    [Fact]
    public void Outline_paints_a_ring_outside_the_border_box()
    {
        // outline cycle, Task 1 — `outline` paints a ring OUTSIDE the border box (it doesn't affect
        // layout): a 4px outline on a 100×60px box → an even-odd ring whose OUTER rect is 108×68px =
        // 81×51pt (the box grown by the outline width on each side), filled in the outline colour.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;outline:4px solid #ff0000\"></div>" +
            "</body></html>"));
        Assert.Contains("1 0 0 rg", pdf);            // the red outline, FILLED
        Assert.Contains(" 81 51 re", pdf);           // the outer ring rect = box + 4px each side
        Assert.Contains(" f* Q", pdf);               // even-odd ring (outline = outer minus border box)
    }

    [Fact]
    public void Outline_none_and_zero_width_paint_nothing()
    {
        // outline cycle, Task 1 — `outline-style: none` (the initial) and a zero width paint no ring.
        var none = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><div style=\"width:100px;height:60px\"></div></body></html>"));
        Assert.DoesNotContain(" f* Q", none);

        var zero = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;outline-style:solid;outline-width:0\"></div></body></html>"));
        Assert.DoesNotContain(" f* Q", zero);
    }

    [Fact]
    public void Outline_color_defaults_to_currentcolor()
    {
        // outline cycle, Task 1 — outline-color's initial is currentcolor (CSS UI §5.5; `invert`
        // deferred): an outline with no colour uses the element's `color`.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;color:#0000ff;outline-style:solid;outline-width:3px\"></div>" +
            "</body></html>"));
        Assert.Contains("0 0 1 rg", pdf);            // the blue currentcolor outline
        Assert.Contains(" f* Q", pdf);
    }

    [Fact]
    public void Outline_offset_pushes_the_outline_outward()
    {
        // outline cycle, Task 2 — outline-offset (recovered from the AngleSharp drop via CssPreprocessor)
        // pushes the outline OUT from the border box: offset 4px + width 2px on a 100×60 box → the outer
        // ring rect = 100+2·(4+2)=112px=84pt × 60+2·(4+2)=72px=54pt (vs 78×48pt with no offset).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;outline:2px solid #ff0000;outline-offset:4px\"></div>" +
            "</body></html>"));
        Assert.Contains(" 84 54 re", pdf);           // outer rect reflects the +4px offset
        Assert.Contains(" f* Q", pdf);
    }

    [Fact]
    public void Outline_follows_border_radius_with_rounded_corners()
    {
        // outline cycle, Task 3 — a border-radius rounds the outline so it follows the box (CSS UI §5.3).
        // The box has NO background/border, so the only painted geometry is the outline ring — its curve
        // operators confirm the rounded corners (a square outline would have none).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;border-radius:8px;outline:2px solid #ff0000\"></div>" +
            "</body></html>"));
        Assert.Contains("1 0 0 rg", pdf);            // the outline ring
        Assert.Contains(" c ", pdf);                 // ... with rounded (Bézier) corners
        Assert.Contains(" f* Q", pdf);
    }

    [Fact]
    public void Outline_style_hidden_is_invalid_and_paints_no_outline()
    {
        // post-PR-#173 review P2 — `hidden` is invalid for outline-style (CSS UI 4 §5.2): the cascade
        // rejects it and the property falls back to its initial `none`, so NO outline paints (a `solid`
        // control does). `@supports (outline-style: hidden)` is also false (cascade test). (Note:
        // AngleSharp collapses inline `outline-style` duplicates to the last value at parse, so the
        // "solid then hidden preserves solid" form isn't reachable via an inline style here — the
        // standalone rejection + @supports cover the fix.)
        var hidden = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;outline-width:3px;outline-color:#ff0000;outline-style:hidden\"></div>" +
            "</body></html>"));
        Assert.DoesNotContain(" f* Q", hidden);      // hidden → invalid → initial none → no outline

        var solid = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;outline-width:3px;outline-color:#ff0000;outline-style:solid\"></div>" +
            "</body></html>"));
        Assert.Contains(" f* Q", solid);             // solid control → outline paints
    }

    [Fact]
    public void Outline_color_auto_resolves_to_currentcolor()
    {
        // post-PR-#173 review P2 — CSS UI 4 retired `invert` and makes `outline-color: auto` the initial,
        // computing to a UA colour we approximate as currentcolor: an explicit `auto` paints in the
        // element's `color` (pre-fix ColorResolver rejected `auto`).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;color:#0000ff;outline-style:solid;outline-width:3px;" +
            "outline-color:auto\"></div>" +
            "</body></html>"));
        Assert.Contains("0 0 1 rg", pdf);            // the blue currentcolor outline
        Assert.Contains(" f* Q", pdf);
    }

    [Fact]
    public void Outline_extreme_negative_offset_stays_centered_no_drift()
    {
        // post-PR-#173 review P2 — a negative outline-offset past half the box collapses the outline to a
        // zero-size box CENTERED on the box (the per-axis clamp constrains the ORIGIN, not just the size),
        // instead of drifting far to one side. With margin:0, the box is at (0,0); a -999px offset on a
        // 100×60 box → the outline's outer rect sits near the box centre (x well under 100pt), not ~750pt.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page{margin:0}body{margin:0}</style></head><body>" +
            "<div style=\"width:100px;height:60px;outline:4px solid #ff0000;outline-offset:-999px\"></div>" +
            "</body></html>"));
        // The inner box collapses to 0 → the ring fills the whole (tiny) outer rect (`f`, not `f*`).
        var i = pdf.IndexOf(" 6 6 re", StringComparison.Ordinal);   // the collapsed 8×8px = 6×6pt outer
        Assert.True(i > 0, "expected a collapsed 6×6pt outline outer rect");
        var nums = pdf[..i].TrimEnd().Split(' ');     // … <x> <y> (before "6 6 re")
        var x = double.Parse(nums[^2], CultureInfo.InvariantCulture);
        Assert.True(x is > 0 and < 100, $"outline should stay centred near the box, not drift; x={x}pt");
    }

    [Fact]
    public void Outline_rounded_with_negative_offset_does_not_crash()
    {
        // post-PR-#173 review P3 — a negative outline-offset larger than the border-radius would drive a
        // grown corner radius below 0; GrowRadii clamps it. The rounded outline still renders, no throw.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;border-radius:4px;outline:2px solid #ff0000;" +
            "outline-offset:-20px\"></div>" +
            "</body></html>"));
        Assert.Contains(" f* Q", pdf);               // renders (clamped radii, no negative-radius blow-up)
    }

    [Fact]
    public void Outline_width_negative_is_invalid_and_falls_back_to_medium()
    {
        // post-PR-#173 Copilot review — outline-width is a <line-width> (non-negative, CSS UI §5.1): a
        // negative value is INVALID (diagnosed) and falls back to the initial `medium` (3px), so a solid
        // outline still paints (pre-fix a negative LengthPx made the painter draw nothing).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;outline-style:solid;outline-color:#ff0000;" +
            "outline-width:-1px\"></div>" +
            "</body></html>", new HtmlPdfOptions());
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
        Assert.Contains(" f* Q", Latin1(result.Pdf));   // medium (3px) outline still paints
    }

    [Fact]
    public void Outline_and_border_style_approximation_diagnosed_once()
    {
        // post-PR-#173 Copilot review — PAINT-BORDER-STYLE-APPROXIMATED-001 is once-per-conversion, and
        // borders + outline SHARE the flag: a document with BOTH a non-solid border and a non-solid
        // outline surfaces the diagnostic exactly ONCE (not twice).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;border:2px dashed #000;outline:2px dotted #f00\"></div>" +
            "</body></html>", new HtmlPdfOptions());
        Assert.Equal(1, result.Warnings.Count(d => d.Code == DiagnosticCodes.PaintBorderStyleApproximated001));
    }

    [Fact]
    public void Background_repeat_space_distributes_equal_gaps()
    {
        // space-round cycle — `space` packs floor(area/tile) whole tiles flush to the edges with
        // equal gaps between: 88×32 box, 16×16 tile → floor(88/16)=5 cols × 2 rows = 10 tiles;
        // the 8px leftover spreads as 2px gaps → the column step is 16+2 = 18px = 13.5pt (vs
        // repeat's 6 cols at 16px). The first column is flush with the box's left edge.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:88px;height:32px;background-image:url({PngDataUri(16, 16)});" +
            "background-repeat:space;background-color:#3366cc\"></div>" +
            "</body></html>"));
        var placements = AllImagePlacements(pdf);
        Assert.Equal(10, placements.Count);
        var band = AllRects(pdf)[0];                      // the colour band = the border box
        Assert.Equal(band.X, placements[0].X, 1);         // first column flush left
        Assert.Equal(13.5, placements[1].X - placements[0].X, 1);   // 18px step = 16px tile + 2px gap
    }

    [Fact]
    public void Background_repeat_round_rescales_the_tile_to_fit()
    {
        // space-round cycle — `round` resizes the tile so a whole number fills the axis exactly:
        // 60×16 box, 16×16 tile → round(60/16)=4 cols, each rescaled to 60/4 = 15px = 11.25pt
        // (vs the 12pt intrinsic), edge-to-edge; round(16/16)=1 row → 4 placements.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:60px;height:16px;background-image:url({PngDataUri(16, 16)});" +
            "background-repeat:round\"></div>" +
            "</body></html>"));
        var placements = AllImagePlacements(pdf);
        Assert.Equal(4, placements.Count);
        Assert.Equal(11.25, placements[0].W, 2);                     // the rescaled tile width
        Assert.Equal(11.25, placements[1].X - placements[0].X, 2);   // tiles run edge-to-edge
    }

    [Fact]
    public void Background_repeat_space_pattern_path_rides_the_xstep()
    {
        // A `space` tiling above the 16-tile loop threshold emits ONE tiling pattern whose
        // /XStep carries the tile+gap origin step: 350×16 box, 16×16 tile → floor(350/16)=21
        // cols (>16) × 1 row; the 14px leftover spreads as 0.7px gaps → step 16.7px = 12.525pt
        // on /XStep, while the single-row Y keeps /YStep at the 12pt tile.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:350px;height:16px;background-image:url({PngDataUri(16, 16)});" +
            "background-repeat:space\"></div>" +
            "</body></html>"));
        Assert.Contains("/PatternType 1", pdf);
        Assert.Contains("/XStep 12.525", pdf);
        Assert.Contains("/YStep 12", pdf);
    }

    // ---- margin-box background images (margin-box-bg-image cycle) ----

    [Fact]
    public void Page_margin_box_background_image_tiles_over_the_band()
    {
        // A 64×32 @top-center band (margin: 32px → band height 32) with a 16×16 tile →
        // 4 × 2 = 8 placements, clipped at the band.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { margin: 32px; " +
            $"@top-center {{ content: \"AB\"; width: 64px; background-image: url({PngDataUri(16, 16)}) }} }}" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(8, AllImagePlacements(pdf).Count);
        Assert.Contains(" re W n", pdf);
    }

    [Fact]
    public void Page_margin_box_background_image_respects_print_backgrounds_off()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { margin: 32px; " +
            $"@top-center {{ content: \"AB\"; width: 64px; background-image: url({PngDataUri(16, 16)}) }} }}" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver(), PrintBackgrounds = false }));
        Assert.Empty(AllImagePlacements(pdf));
    }

    [Fact]
    public void Page_margin_box_background_image_gradient_is_surfaced()
    {
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { margin: 32px; " +
            "@top-center { content: \"AB\"; width: 64px; background-image: linear-gradient(red, blue) } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssBackgroundImageUnsupported001);
        Assert.Empty(AllImagePlacements(Latin1(result.Pdf)));
    }

    [Fact]
    public void Page_margin_box_background_variants_drive_the_tiler()
    {
        // PR #167 review P1 — a margin box's declared repeat/size/position raws reach the
        // shared tiler (margin-box bodies never pass through AngleSharp, so the authored
        // values arrive intact): no-repeat → ONE tile (was 8); size 32px 32px → 2 × 1 tiles
        // at 24pt.
        var noRepeat = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { margin: 32px; " +
            $"@top-center {{ content: \"AB\"; width: 64px; background-image: url({PngDataUri(16, 16)}); " +
            "background-repeat: no-repeat } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Single(AllImagePlacements(noRepeat));

        var sized = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { margin: 32px; " +
            $"@top-center {{ content: \"AB\"; width: 64px; background-image: url({PngDataUri(16, 16)}); " +
            "background-size: 32px 32px } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var placements = AllImagePlacements(sized);
        Assert.Equal(2, placements.Count);
        Assert.Equal(24.0, placements[0].W, 1);
    }

    [Fact]
    public void Page_margin_box_background_origin_and_clip_apply()
    {
        // bg-origin / bg-clip cycles (margin boxes) — a bordered/padded @top-center box honors
        // background-origin (content-box anchors the no-repeat tile +12px = +9pt right of
        // border-box) and background-clip (content-box shrinks the clip rect by 12px/side = 18pt
        // vs border-box). The body BackgroundAreaInset helper is reused on the margin box's style.
        string Build(string decls) =>
            "<!DOCTYPE html><html><head><style>@page { margin: 40px; " +
            "@top-center { content: \"AB\"; width: 80px; border: 4px solid #000; padding: 8px; " +
            $"background-image: url({PngDataUri(16, 16)}); background-repeat: no-repeat; {decls} }} }}" +
            "</style></head><body></body></html>";
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var oBorder = AllImagePlacements(Latin1(HtmlPdf.Convert(Build("background-origin: border-box"), opts)))[0];
        var oContent = AllImagePlacements(Latin1(HtmlPdf.Convert(Build("background-origin: content-box"), opts)))[0];
        Assert.Equal(oBorder.X + 9.0, oContent.X, 1);
        var cBorder = FirstClipRect(Latin1(HtmlPdf.Convert(Build("background-clip: border-box"), opts))).W;
        var cContent = FirstClipRect(Latin1(HtmlPdf.Convert(Build("background-clip: content-box"), opts))).W;
        Assert.Equal(cBorder - 18.0, cContent, 1);   // content-box clip is 12px/side narrower
    }

    [Fact]
    public void Page_margin_box_content_box_clip_collapse_paints_nothing_no_crash()
    {
        // post-PR-#171 review P1 — a small @top-center box clamped to a narrow band whose border +
        // padding EXCEED the box collapses a content-box clip to ≤ 0 (here negative width + zero
        // height): the inset sums are clamped to ≥ 0 and the tiler skips the empty clip, so NO image
        // is placed and conversion never throws (pre-fix a negative width/height reached the tiler).
        // The default border-box clip still places the tile — proving the box exists and it's the
        // content-box clip that collapses, not the box vanishing.
        string Build(string clip) =>
            "<!DOCTYPE html><html><head><style>@page { size: 100px 300px; margin: 40px; " +
            "@top-center { content: \"X\"; width: 80px; border: 20px solid #000; padding: 20px; " +
            $"background-image: url({PngDataUri(16, 16)}); background-repeat: no-repeat; {clip} }} }}" +
            "</style></head><body></body></html>";
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        Assert.NotEmpty(AllImagePlacements(Latin1(HtmlPdf.Convert(Build("background-clip: border-box"), opts))));
        Assert.Empty(AllImagePlacements(Latin1(HtmlPdf.Convert(Build("background-clip: content-box"), opts))));
    }

    [Fact]
    public void Body_content_box_clip_collapse_paints_nothing_no_crash()
    {
        // post-PR-#171 review P1 (body counterpart) — a box whose border+padding meet/exceed its size
        // under box-sizing: border-box collapses the content-box clip to ≤ 0; the same inset clamp +
        // empty-clip skip mean no tile is placed and conversion doesn't throw. border-box clip (the
        // default) still places it.
        string Build(string clip) =>
            "<!DOCTYPE html><html><body>" +
            "<div style=\"box-sizing:border-box;width:40px;height:40px;border:20px solid #000;padding:20px;" +
            $"background-image:url({PngDataUri(16, 16)});background-repeat:no-repeat;{clip}\"></div>" +
            "</body></html>";
        Assert.NotEmpty(AllImagePlacements(Latin1(HtmlPdf.Convert(Build("background-clip:border-box")))));
        Assert.Empty(AllImagePlacements(Latin1(HtmlPdf.Convert(Build("background-clip:content-box")))));
    }

    [Fact]
    public void Page_margin_box_background_origin_respects_importance()
    {
        // post-PR-#171 review P2 — background-origin now flows through the margin box's cascade, so
        // `content-box !important` beats a LATER `border-box` (pre-fix RawDeclarationWinner took the
        // last declaration → border-box). The winning content-box anchors the no-repeat tile +12px =
        // +9pt right of the border-box origin.
        string Build(string originDecls) =>
            "<!DOCTYPE html><html><head><style>@page { margin: 40px; " +
            "@top-center { content: \"AB\"; width: 80px; border: 4px solid #000; padding: 8px; " +
            $"background-image: url({PngDataUri(16, 16)}); background-repeat: no-repeat; {originDecls} }} }}" +
            "</style></head><body></body></html>";
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var borderBox = AllImagePlacements(Latin1(HtmlPdf.Convert(Build("background-origin: border-box"), opts)))[0];
        var importantContent = AllImagePlacements(Latin1(HtmlPdf.Convert(
            Build("background-origin: content-box !important; background-origin: border-box"), opts)))[0];
        Assert.Equal(borderBox.X + 9.0, importantContent.X, 1);   // !important content-box beat the later border-box
    }

    [Fact]
    public void Page_margin_box_background_clip_invalid_value_is_diagnosed_and_falls_back()
    {
        // post-PR-#171 review P2 — an invalid background-clip on a margin box now flows through the
        // cascade, so it's DIAGNOSED (CSS-PROPERTY-VALUE-INVALID-001) and falls back to the initial
        // border-box (pre-fix the silent RawDeclarationWinner passed the garbage straight to the
        // tiler, which silently treated it as border-box with no diagnostic). The clip rect matches
        // border-box, and the warning is surfaced.
        string Build(string clip) =>
            "<!DOCTYPE html><html><head><style>@page { margin: 40px; " +
            "@top-center { content: \"AB\"; width: 80px; border: 4px solid #000; padding: 8px; " +
            $"background-image: url({PngDataUri(16, 16)}); background-repeat: no-repeat; {clip} }} }}" +
            "</style></head><body></body></html>";
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var result = HtmlPdf.ConvertDetailed(Build("background-clip: bogus"), opts);
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);
        var bogusW = FirstClipRect(Latin1(result.Pdf)).W;
        var borderW = FirstClipRect(Latin1(HtmlPdf.Convert(Build("background-clip: border-box"), opts))).W;
        Assert.Equal(borderW, bogusW, 1);   // invalid → the initial border-box, not garbage
    }

    [Fact]
    public void Body_background_attachment_fixed_is_parse_only_no_diagnostic()
    {
        // post-PR-#171 review P2 — background-attachment is registered for VALIDATION only: a VALID
        // value (`fixed`) is silently approximated (element-relative; page-relative is a documented
        // deferral), so it must NOT surface a diagnostic, and the background image still paints. Pins
        // the "parse-only metadata" contract the corrected KeywordResolver comment + docs describe.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:64px;height:32px;background-image:url({PngDataUri(16, 16)});" +
            "background-attachment:fixed;background-repeat:no-repeat\"></div>" +
            "</body></html>", new HtmlPdfOptions());
        Assert.DoesNotContain(result.Warnings, d =>
            d.Code == DiagnosticCodes.CssPropertyValueInvalid001 &&
            d.Message.Contains("attachment", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(AllImagePlacements(Latin1(result.Pdf)));   // the image still paints
    }

    [Fact]
    public void Background_position_single_vertical_keyword_centers_x()
    {
        // PR #167 review P2 — `background-position: top` = `center top`: the tile sits
        // (64 − 16) / 2 = 24px = 18pt right of the band edge (pre-fix the keyword was
        // rejected on the X axis and fell back to 0% 0%).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:64px;height:32px;background-image:url({PngDataUri(16, 16)});" +
            "background-repeat:no-repeat;background-position:top;background-color:#3366cc\"></div>" +
            "</body></html>"));
        var band = FirstRect(pdf);
        var p = Assert.Single(AllImagePlacements(pdf));
        Assert.Equal(band.X + 18.0, p.X, 1);
    }

    [Fact]
    public void Background_size_negative_falls_back_with_diagnostic()
    {
        // PR #167 review P2 — a negative size is INVALID (not a zero tile): the longhand
        // falls back to auto (8 intrinsic tiles on the 64×32 box) and surfaces once.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:64px;height:32px;background-image:url({PngDataUri(16, 16)});" +
            "background-size:-10px\"></div>" +
            "</body></html>", new HtmlPdfOptions());
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssBackgroundImageUnsupported001);
        Assert.Equal(8, AllImagePlacements(Latin1(result.Pdf)).Count);
    }

    [Fact]
    public void Background_size_zero_paints_nothing_without_diagnostic()
    {
        // `background-size: 0` is VALID CSS — a zero tile paints nothing, with no
        // unsupported-form warning (PR #167 review P2).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:64px;height:32px;background-image:url({PngDataUri(16, 16)});" +
            "background-size:0\"></div>" +
            "</body></html>", new HtmlPdfOptions());
        Assert.Empty(AllImagePlacements(Latin1(result.Pdf)));
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssBackgroundImageUnsupported001);
    }

    // ---- element() nested CONTAINER bands (container-bands cycle) ----

    [Fact]
    public void Page_margin_box_container_band_spans_its_descendant_lines()
    {
        // A decorated intermediate div's band covers BOTH its leaf lines: H = 2 × 19.2px
        // (16px font × 1.2 pitch) = 38.4px = 28.8pt, the full 300px = 225pt content width.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh .c { background-color: #ff0000 } " +
            "@page { @top-center { content: element(rh); width: 300px } }</style></head>" +
            "<body><div class=\"rh\"><div class=\"c\"><div>AB</div><div>AB</div></div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var rects = AllRects(pdf);
        Assert.True(rects.Count >= 1, "expected the container band");
        Assert.Equal(28.8, rects[0].H, 1);
        Assert.Equal(225.0, rects[0].W, 1);
    }

    [Fact]
    public void Page_margin_box_container_vertical_margin_gaps_its_first_line()
    {
        // An UNDECORATED container's margin-top folds into its first leaf line's gap
        // (max-collapse): the second line drops line-height + 20px = 19.2 + 20 = 39.2px =
        // 29.4pt below the first (vs 14.4pt with no margin).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh .c { margin-top: 20px } " +
            "@page { @top-center { content: element(rh); width: 300px } }</style></head>" +
            "<body><div class=\"rh\"><div>AB</div><div class=\"c\"><div>AB</div></div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var ys = AllTdY(pdf);
        Assert.True(ys.Length >= 2, "expected two margin lines");
        Assert.Equal(29.4, ys[0] - ys[1], 1);
    }

    [Fact]
    public void Page_margin_box_container_horizontal_margins_inset_its_band()
    {
        // The container's own margin-left/right inset ITS band: X = box band X + 15pt,
        // width (300 − 40)px = 195pt — the leaf line geometry is untouched (first cut).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh .c { margin-left: 20px; margin-right: 20px; background-color: #ff0000 } " +
            "@page { @top-center { content: element(rh); width: 300px; " +
            "background-color: #eeeeee } }</style></head>" +
            "<body><div class=\"rh\"><div class=\"c\"><div>AB</div></div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var rects = AllRects(pdf);
        Assert.True(rects.Count >= 2, "expected the box band + the container band");
        Assert.Equal(rects[0].X + 15.0, rects[1].X, 1);
        Assert.Equal(195.0, rects[1].W, 1);
    }

    [Fact]
    public void Page_margin_box_container_band_paints_under_its_leaf_bands()
    {
        // Pre-order: the container's band is added BEFORE its leaf's per-line band, so the
        // leaf paints over it. rects[0] = the 2-line container, rects[1] = the 1-line leaf.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh .c { background-color: #eeeeee } .rh .leaf { background-color: #ff0000 } " +
            "@page { @top-center { content: element(rh); width: 300px } }</style></head>" +
            "<body><div class=\"rh\"><div class=\"c\"><div class=\"leaf\">AB</div><div>AB</div></div></div>" +
            "</body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var rects = AllRects(pdf);
        Assert.True(rects.Count >= 2, "expected the container + leaf bands");
        Assert.Equal(28.8, rects[0].H, 1);   // the container spans both lines
        Assert.Equal(14.4, rects[1].H, 1);   // the leaf's own line band paints over it
    }

    [Fact]
    public void Page_margin_box_container_padding_insets_its_descendant_lines()
    {
        // container-insets cycle — the container's padding-left propagates into its descendant
        // lines: the contained line starts 20px = 15pt right of its uncontained sibling.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh .c { padding-left: 20px } " +
            "@page { @top-center { content: element(rh); width: 300px; text-align: left } }</style></head>" +
            "<body><div class=\"rh\"><div>AB</div><div class=\"c\"><div>AB</div></div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var tds = AllTdXs(pdf);
        Assert.True(tds.Count >= 2, "expected two margin lines");
        Assert.Equal(tds[0] + 15.0, tds[1], 1);
    }

    [Fact]
    public void Page_margin_box_container_margin_and_padding_sum_into_descendants()
    {
        // margin-left 10px + padding-left 10px → the descendant line insets by the SUM (15pt);
        // the container's own decorated band insets by its MARGIN only (7.5pt — its padding is
        // inside the band).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh .c { margin-left: 10px; padding-left: 10px; background-color: #ff0000 } " +
            "@page { @top-center { content: element(rh); width: 300px; text-align: left; " +
            "background-color: #eeeeee } }</style></head>" +
            "<body><div class=\"rh\"><div>AB</div><div class=\"c\"><div>AB</div></div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var tds = AllTdXs(pdf);
        Assert.True(tds.Count >= 2, "expected two margin lines");
        Assert.Equal(tds[0] + 15.0, tds[1], 1);
        var rects = AllRects(pdf);
        Assert.True(rects.Count >= 2, "expected the box band + the container band");
        Assert.Equal(rects[0].X + 7.5, rects[1].X, 1);
    }

    [Fact]
    public void Page_margin_box_nested_container_band_insets_under_the_outer()
    {
        // The OUTER container's padding propagates into a NESTED container's band too: the
        // inner band starts 20px = 15pt right of the box band's edge.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh .outer { padding-left: 20px } .rh .inner { background-color: #ff0000 } " +
            "@page { @top-center { content: element(rh); width: 300px; text-align: left; " +
            "background-color: #eeeeee } }</style></head>" +
            "<body><div class=\"rh\"><div class=\"outer\"><div class=\"inner\"><div>AB</div></div></div></div>" +
            "</body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var rects = AllRects(pdf);
        Assert.True(rects.Count >= 2, "expected the box band + the inner container band");
        Assert.Equal(rects[0].X + 15.0, rects[1].X, 1);
    }

    // ---- container vertical padding / §4.3-gated borders (container-vpad cycle) ----

    [Fact]
    public void Page_margin_box_container_vertical_padding_extends_the_band()
    {
        // container-vpad cycle — vertical padding extends the container's band over its padding
        // strip: padding:10px adds 10px above the first line + 10px below the last → the band is
        // 20px = 15pt taller than the no-padding band (which spans the two leaf lines at 28.8pt).
        const string head = "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } ";
        const string page = "@page { @top-center { content: element(rh); width: 300px } }</style></head>";
        const string body = "<body><div class=\"rh\"><div class=\"c\"><div>AB</div><div>AB</div></div></div></body></html>";
        double BandHeight(string cRule)
        {
            var pdf = Latin1(HtmlPdf.Convert(
                head + ".rh .c { background-color: #ff0000; " + cRule + " } " + page + body,
                new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
            return AllRects(pdf)[0].H;   // backgrounds flush before borders → [0] = the band
        }
        var plain = BandHeight("");
        Assert.Equal(28.8, plain, 1);
        Assert.Equal(plain + 15.0, BandHeight("padding: 10px"), 1);
    }

    [Fact]
    public void Page_margin_box_container_border_extends_the_band_and_gates_on_style()
    {
        // §4.3-gated border-width — border:2px solid adds 2px above + 2px below the band → +3pt
        // taller; border-style:none gates the width to 0, so the band stays the no-border height.
        const string head = "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } ";
        const string page = "@page { @top-center { content: element(rh); width: 300px } }</style></head>";
        const string body = "<body><div class=\"rh\"><div class=\"c\"><div>AB</div><div>AB</div></div></div></body></html>";
        double BandHeight(string cRule)
        {
            var pdf = Latin1(HtmlPdf.Convert(
                head + ".rh .c { background-color: #ff0000; " + cRule + " } " + page + body,
                new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
            return AllRects(pdf)[0].H;
        }
        var plain = BandHeight("");
        Assert.Equal(plain + 3.0, BandHeight("border: 2px solid #00ff00"), 1);
        Assert.Equal(plain, BandHeight("border: 2px none #00ff00"), 1);   // none → 0, no extension
    }

    [Theory]
    [InlineData("border-left: 2px solid #00ff00", 1.5)]    // 2px = 1.5pt
    [InlineData("border-left: thin solid #00ff00", 0.75)]  // §4.3 thin = 1px
    [InlineData("border-left: thick solid #00ff00", 3.75)] // thick = 5px
    [InlineData("border-left-style: solid", 2.25)]         // painting edge, no width → medium 3px
    [InlineData("border-left: 0 solid #00ff00", 0.0)]      // EXPLICIT zero → 0, not medium (review P1)
    [InlineData("border-left: 0px solid #00ff00", 0.0)]    // explicit 0px → 0
    [InlineData("border-left: 10px none #00ff00", 0.0)]    // none → 0, no inset
    public void Page_margin_box_container_border_left_insets_its_descendant_line(string cRule, double insetPt)
    {
        // container-insets cycle — the §4.3-gated border-left propagates into the descendant line
        // like padding: the contained line starts `insetPt` right of its uncontained sibling (0 for
        // a non-painting style).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh .c { " + cRule + " } " +
            "@page { @top-center { content: element(rh); width: 300px; text-align: left } }</style></head>" +
            "<body><div class=\"rh\"><div>AB</div><div class=\"c\"><div>AB</div></div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var tds = AllTdXs(pdf);
        Assert.True(tds.Count >= 2, "expected two margin lines");
        Assert.Equal(tds[0] + insetPt, tds[1], 1);
    }

    [Fact]
    public void Page_margin_box_flat_element_decoration_paints_once_not_per_line_too()
    {
        // Post-PR-#162 review P1 — a FLAT running element's own background/border already rides
        // the standalone element() decoration path; the segment capture must NOT duplicate it as
        // a per-line band. Pre-fix: 2 blue background fills + 8 green border-edge rects (the box
        // path + a per-line copy). Post-fix: exactly 1 background fill + 4 border edges.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); " +
            "background-color: #0000ff; border: 4px solid #00ff00 } " +
            "@page { @top-center { content: element(rh) } }</style></head>" +
            "<body><div class=\"rh\">Head</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        var colors = RectFillColors(pdf);
        Assert.Equal(1, colors.Count(c => c == "0 0 1"));   // ONE background band
        Assert.Equal(4, colors.Count(c => c == "0 1 0"));   // FOUR border edges, not eight
    }

    [Fact]
    public void Page_margin_box_segment_band_y_origin_includes_the_box_insets()
    {
        // Post-PR-#162 review P1 — the per-line band starts at the CONTENT-box top (TextPainter
        // adds the box's border+padding before placing glyphs). A @top-center box is vertically
        // CENTERED, so `padding-top: 20px` shrinks the content box by 20px (the centred block
        // re-seats 10px higher within it) and then insets by the full 20px — a net 10px = 7.5pt
        // shift down the page, exactly matching the glyph shift (pre-fix the band ignored the
        // inset entirely and only moved by the recentre).
        const string css =
            "<!DOCTYPE html><html><head><style>.rh {{ position: running(rh) }} " +
            ".rh h1 {{ font-size: 32px; background-color: #3366cc }} " +
            "@page {{ @top-center {{ content: element(rh){0} }} }}</style></head>" +
            "<body><div class=\"rh\"><h1>Title</h1><div>Sub</div></div></body></html>";
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };

        var unpadded = BandY(Latin1(HtmlPdf.Convert(string.Format(css, ""), opts)));
        var padded = BandY(Latin1(HtmlPdf.Convert(string.Format(css, "; padding-top: 20px"), opts)));

        Assert.Equal(unpadded - 7.5, padded, 1);    // net 10px → 7.5pt further down (PDF y-down flip)

        static double BandY(string pdf)
        {
            var band = AllRects(pdf).Find(r => Math.Abs(r.H - 28.8) < 0.1);   // the one-line h1 band
            Assert.True(band.H > 0, "expected the 28.8pt segment band");
            return band.Y;
        }
    }

    [Fact]
    public void Page_margin_box_segment_pitch_follows_each_lines_own_font()
    {
        // Segment-pitch cycle — with a decorated element, the nested element band's height equals
        // the SUM of per-line heights: 32px h1 (38.4) + two 16px lines (19.2 × 2) = 76.8px = 57.6pt
        // (the old max-font approximation gave 3 × 38.4 = 115.2px = 86.4pt).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); background-color: #cc3366 } " +
            ".rh h1 { font-size: 32px } " +
            "@page { @top-center { content: element(rh); background-color: #3366cc } }</style></head>" +
            "<body><div class=\"rh\"><h1>Title</h1><div>Sub</div><div>More</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        Assert.Contains(AllRects(pdf), r => Math.Abs(r.H - 57.6) < 0.1);   // Σ per-line, not 86.4
    }

    [Fact]
    public void Page_margin_box_segment_text_align_aligns_its_own_line()
    {
        // Segment-align cycle — a leaf block's own text-align aligns ITS line within the content
        // box (the box declares an explicit width so there's room): the right-aligned second line
        // starts further right than the left-aligned first.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh .r { text-align: right } " +
            "@page { @top-center { content: element(rh); width: 300px } }</style></head>" +
            "<body><div class=\"rh\"><div>AB</div><div class=\"r\">AB</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        var tds = AllTdXs(pdf);
        Assert.True(tds.Count >= 2, "expected two margin lines");
        Assert.True(tds[1] > tds[0] + 10, $"right-aligned line at {tds[1]} should sit right of {tds[0]}");
    }

    [Fact]
    public void Page_margin_box_box_text_align_still_wins_over_segment_align()
    {
        // The box's own declared text-align beats per-segment alignment (the established
        // box-wins rule): both lines align right despite the first segment's left.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh .l { text-align: left } " +
            "@page { @top-center { content: element(rh); width: 300px; text-align: right } }</style></head>" +
            "<body><div class=\"rh\"><div class=\"l\">AB</div><div>AB</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        var tds = AllTdXs(pdf);
        Assert.True(tds.Count >= 2);
        Assert.True(Math.Abs(tds[0] - tds[1]) < 0.5, $"both right-aligned: {tds[0]} vs {tds[1]}");
    }

    /// <summary>Every text-positioning X (the <c>… &lt;x&gt; &lt;y&gt; Td</c> operands), in emission
    /// order — for comparing per-line alignment shifts.</summary>
    private static List<double> AllTdXs(string pdf)
    {
        var xs = new List<double>();
        for (var i = pdf.IndexOf(" Td", StringComparison.Ordinal); i > 0;
             i = pdf.IndexOf(" Td", i + 3, StringComparison.Ordinal))
        {
            var nums = pdf[..i].TrimEnd().Split(' ');
            xs.Add(double.Parse(nums[^2], CultureInfo.InvariantCulture));
        }
        return xs;
    }

    [Fact]
    public void Page_margin_box_element_single_styled_block_renders_in_its_own_font()
    {
        // Post-PR-#160 review P2 — a running element whose ONLY child is a styled block records
        // ONE leaf segment; its font/colour must drive the shaping (the root has no own style, so
        // the pre-fix single-run path painted the box default 12pt instead of the h1's 24pt).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            ".rh h1 { font-size: 32px; color: #ff0000 } " +
            "@page { @top-center { content: element(rh) } }</style></head>" +
            "<body><div class=\"rh\"><h1>Title</h1></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        Assert.Equal(1, TdCount(pdf));
        Assert.Contains(" 24 Tf", pdf);         // 32px × 0.75 — the h1 segment's own size
        Assert.DoesNotContain(" 12 Tf", pdf);   // not the 16px box default
        Assert.Contains("1 0 0 rg", pdf);
    }

    [Fact]
    public void Page_margin_box_element_uniform_segments_keep_the_single_style()
    {
        // Unstyled nested blocks (no per-leaf overrides) — both lines render at the default size,
        // and no spurious second font size appears (the multi-run path degenerates cleanly).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            "@page { @top-center { content: element(rh) } }</style></head>" +
            "<body><div class=\"rh\"><div>AB</div><div>AB</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));

        Assert.Equal(2, TdCount(pdf));
        Assert.Contains(" 12 Tf", pdf);
        Assert.DoesNotContain(" 24 Tf", pdf);
    }

    [Fact]
    public void Page_margin_box_element_deep_block_chain_renders_bounded()
    {
        // A 24-deep single-block chain exceeds MaxRunningBlockDepth (16) — the deeper nest flattens
        // (the pre-cycle behavior) instead of recursing unboundedly: still ONE line, no crash.
        var open = string.Concat(System.Linq.Enumerable.Repeat("<div>", 24));
        var close = string.Concat(System.Linq.Enumerable.Repeat("</div>", 24));
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            "@page { @top-center { content: element(rh) } }</style></head>" +
            "<body><div class=\"rh\">" + open + "AB" + close + "</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(1, TdCount(pdf));   // one leaf line, depth-capped recursion
    }

    [Fact]
    public void Page_margin_box_math_function_font_size_resolves()
    {
        // Post-PR-#158 review P2: font-size math functions evaluate — clamp(12px, 5vw, 24px) on an
        // 800px page clamps 40px down to 24px → 18pt Tf, and calc(50% + 10px)'s % term scales by the
        // PARENT (page-context) font-size per CSS Fonts (20px → 10 + 10 = 20px → 15pt Tf).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var clamp = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { size: 800px 600px; " +
            "@top-center { content:\"AB\"; font-size: clamp(12px, 5vw, 24px) } }</style></head><body></body></html>", opts));
        Assert.Contains(" 18 Tf", clamp);   // 24px × 0.75
        var pct = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { font-size: 20px; " +
            "@top-center { content:\"AB\"; font-size: calc(50% + 10px) } }</style></head><body></body></html>", opts));
        Assert.Contains(" 15 Tf", pct);     // (10 + 10)px × 0.75
    }

    [Fact]
    public void Page_margin_box_malformed_math_font_size_is_surfaced()
    {
        // A kept-but-unevaluable math-function font-size (length × length) is SURFACED before the
        // default fallback — not silently 16px (the admit gate is syntactic).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; " +
            "font-size: calc(10px * 5px) } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001
            && d.Message.Contains("font-size"));
        Assert.Contains(" 12 Tf", Latin1(result.Pdf));   // falls back to the 16px default → 12pt
    }

    [Fact]
    public void Page_margin_box_rem_font_size_feeds_the_em_width_base()
    {
        // Resolve ORDER: the font-size resolves BEFORE the size bases are read, so `width: 10em`
        // scales by the RESOLVED 2rem (= 40px) font → a 400px → 300pt band.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>html { font-size: 20px } " +
            "@page { @top-center { content:\"AB\"; font-size: 2rem; width: 10em; background-color: red } }" +
            "</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.InRange(FirstRect(pdf).W, 298.0, 302.0);   // 10 × 40px × 0.75 = 300pt
    }

    // ---- per-edge border currentcolor (Task 21, per-edge currentcolor cycle) ----

    [Fact]
    public void Page_margin_box_border_currentcolor_resolves_per_edge()
    {
        // CSS Color 4 §6.2 per edge: the BOX declares border-left (its blue currentcolor), the
        // running ELEMENT declares border-right (its red). Each edge falls back to ITS owner's
        // colour — blue left + red right. (The old whole-border rule painted BOTH edges blue
        // because the box declared "a border".)
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>" +
            ".rh { position: running(rh); color: red; border-right: 4px solid } " +
            "@page { @top-center { content: element(rh); color: blue; border-left: 4px solid } }" +
            "</style></head><body><div class=\"rh\">AB</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var colors = RectFillColors(pdf);
        Assert.Contains("0 0 1", colors);   // the box-owned left edge strokes the box's blue
        Assert.Contains("1 0 0", colors);   // the element-owned right edge strokes the element's red
    }

    [Fact]
    public void Page_margin_box_malformed_calc_width_is_surfaced_and_shrinks_to_fit()
    {
        // A calc() that fails evaluation (length × length has no CSS type, §10.4) is KEPT by the
        // syntactic gate but must SURFACE the contextual failure and shrink-to-fit — not vanish.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; " +
            "width: calc(10px * 5px); background-color: red } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001
            && d.Message.Contains("could not be resolved against its context"));
        Assert.True(FirstRect(Latin1(result.Pdf)).W < 200.0, "a failed calc() width should shrink-to-fit");
    }

    // ---- margin-box white-space (Task 21, white-space cycle) ----

    [Fact]
    public void Page_margin_box_nowrap_keeps_wrappable_content_on_one_line()
    {
        // white-space cycle: a DECLARED `white-space: nowrap` is now cascaded — wrappable content in a
        // vertical box stays ONE line (clipped at the band edge via the clip path + width diagnostic)
        // instead of wrapping at the band width.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        string Render(string ws) => Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @left-middle { content:\"AB AB AB AB AB AB AB AB\"; " +
            ws + " } }</style></head><body></body></html>", opts));
        var wrapped = Render("");
        var nowrap = Render("white-space: nowrap");
        Assert.True(TdCount(wrapped) >= 2, "the default (normal) should wrap at the band width");
        Assert.Equal(1, TdCount(nowrap));            // nowrap → a single rigid line
        Assert.Contains(" re W n", nowrap);          // …clipped at the band edge
    }

    [Fact]
    public void Page_margin_box_white_space_is_inherited_from_the_page_context()
    {
        // white-space is a CSS inherited property: `@page { white-space: nowrap }` flows into the
        // margin box (root → page context → box), keeping its wrappable content on one line.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { white-space: nowrap; " +
            "@left-middle { content:\"AB AB AB AB AB AB AB AB\" } }</style></head><body></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Equal(1, TdCount(pdf));
    }

    [Fact]
    public void Page_margin_box_nowrap_skips_the_min_content_flex()
    {
        // A nowrap box is RIGID (min == max), so two overlapping horizontal siblings take the
        // center-priority clamp instead of the wrap flex — the wide nowrap @top-left stays ONE line
        // (no re-wrap Tds) beside a centered sibling.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { @top-left { content:\"" + Words(40) +
            "\"; white-space: nowrap } @top-center { content:\"AB\" } }</style></head><body></body></html>", opts));
        Assert.Equal(2, TdCount(pdf));   // the @top-left line + the @top-center line — no wrapped extras
    }

    [Fact]
    public void Page_margin_box_deferred_explicit_width_is_surfaced_and_shrinks_to_fit()
    {
        // A container-relative width can't be resolved to a used size here (no container context) →
        // it's diagnosed (CSS-PROPERTY-VALUE-INVALID-001) and DROPPED, so the box EXPLICITLY
        // shrink-to-fits (a 2-glyph content box, not the full ~450pt band) rather than silently falling
        // back — review P2. (Font-/viewport-relative sizes and calc() used to take this path too; the
        // relative-units + calc cycles resolve those now — see their tests.)
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>@page { @top-center { content:\"AB\"; width: 10cqw; background-color: red } }" +
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
        // margin-top raises the band so EVERY wrapped line fits — the X variation comes from the short
        // LAST line, which the overflow-clipping cycle would otherwise truncate away.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double[] LineXs(string box) => AllTdX(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page { margin-top: 400px; @" + box + " { content:\"" + Words(200) + "\" } }</style>" +
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
        // Separately-decorated cycle — the box's band paints at the BOX rect AND the element's
        // own background now paints as a NESTED band at its content block (pre-cycle the box's
        // cascade win made the element's vanish entirely).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); background-color: #0000ff } " +
            "@page { @top-center { content: element(rh); background-color: #ff0000 } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("1 0 0 rg", pdf);        // the box's red band (the box rect)
        Assert.Contains("0 0 1 rg", pdf);        // the element's blue NESTED band (its content block)
    }

    [Theory]
    [InlineData("element(rh)", "1 0 0 rg", "0 0 1 rg")]        // default = first → r1's red; r2's blue must NOT paint
    [InlineData("element(rh, last)", "0 0 1 rg", "1 0 0 rg")]  // last → r2's blue; r1's red must NOT paint
    public void Page_margin_box_element_background_follows_the_selected_occurrence(string content, string colorOp, string otherColorOp)
    {
        // The element's DECORATION follows the same occurrence the text does (in lockstep) — element()
        // default/first → the first running element's red background, last → the last's blue. The NON-selected
        // occurrence's background must NOT paint (a negative assertion catches a double-paint regression —
        // Copilot review).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.r1 { position: running(rh); background-color: #ff0000 } " +
            ".r2 { position: running(rh); background-color: #0000ff } @page { @top-center { content: " + content + " } }</style>" +
            "</head><body><div class=\"r1\">A</div><div class=\"r2\">B</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains(colorOp, pdf);            // the selected occurrence's background paints
        Assert.DoesNotContain(otherColorOp, pdf); // the non-selected occurrence's does NOT
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
        // An element-owned border with no colour uses currentColor — for a standalone element() that's the
        // ELEMENT's own colour. `color: #00ff00; border: 2px solid` → green edges. Asserting the RECT colour
        // (not just Contains on the stream) so a same-coloured text fill can't mask a wrong border (review P1).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); color: #00ff00; border: 2px solid } " +
            "@page { @top-center { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        Assert.Contains("0 1 0", RectFillColors(pdf));   // the border RECTS are the element's own green
    }

    [Fact]
    public void Page_margin_box_box_border_currentcolor_resolves_against_the_box_color()
    {
        // Review P1: when the BOX declares the border, its currentcolor resolves against the BOX's `color`
        // (CSS Color 4), not the running element's. Box border + `color: blue`, element `color: red` → the
        // border RECTS are blue (the box's colour), NOT red (the element's). The text stays the element's red.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); color: #ff0000 } " +
            "@page { @top-center { content: element(rh); color: #0000ff; border: 2px solid } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var rects = RectFillColors(pdf);
        Assert.Contains("0 0 1", rects);        // border rects in the BOX's blue (currentcolor → box color)
        Assert.DoesNotContain("1 0 0", rects);  // NOT the running element's red
    }

    [Fact]
    public void Page_margin_box_box_background_currentcolor_resolves_against_the_box_color()
    {
        // Review P1: a box-declared `background-color: currentcolor` resolves against the BOX's `color`, not
        // the running element's. The box's background wins the cascade (overrides the element's), so the band
        // is the box's blue — NOT the element's red (currentcolor origin) and NOT the element's own green bg.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); color: #ff0000; background-color: #00ff00 } " +
            "@page { @top-center { content: element(rh); color: #0000ff; background-color: currentcolor } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() }));
        var rects = RectFillColors(pdf);
        Assert.Contains("0 0 1", rects);        // the BOX band is blue (currentcolor → box color)
        Assert.DoesNotContain("1 0 0", rects);  // NOT the element's red (the pre-fix currentcolor bug)
        // Separately-decorated cycle — the element's own green now paints as the NESTED band
        // (pre-cycle the box's cascade win suppressed it entirely).
        Assert.Contains("0 1 0", rects);
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

    // ---- element() own padding (task A) + own text-align (task B) ----

    [Fact]
    public void Page_margin_box_element_padding_insets_the_text()
    {
        // Task A: the running element's own `padding-left` insets its text (the existing border+padding
        // content-inset) — a start-aligned @top-left shifts the line right by ~15pt (20px), like the box's
        // own padding.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var withPad = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); padding-left: 20px } " +
            "@page { @top-left { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>", opts)));
        var without = FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            "@page { @top-left { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>", opts)));
        Assert.InRange(withPad.X - without.X, 13.0, 17.0);   // padding-left 20px → ~15pt inset
    }

    [Fact]
    public void Page_margin_box_element_padding_grows_the_background_band()
    {
        // Task A: the element's padding grows the shrink-to-fit box, so its background covers content +
        // padding. `padding: 10px` (→ 10px each side) widens the band by ~15pt (20px) vs no padding.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var padded = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); padding: 10px; background-color: red } " +
            "@page { @top-center { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>", opts))).W;
        var unpadded = FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); background-color: red } " +
            "@page { @top-center { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>", opts))).W;
        Assert.InRange(padded - unpadded, 13.0, 17.0);   // +20px padding (left+right) → +~15pt band width
    }

    [Fact]
    public void Page_margin_box_element_text_align_aligns_the_line_in_an_explicit_width_box()
    {
        // Task B: the running element's own `text-align` aligns its line within the content box (observable
        // when the box is wider than the line — here an explicit 300px @top-left). text-align:right pushes
        // the line far to the right vs text-align:left.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double LineX(string align) => FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); text-align: " + align + " } " +
            "@page { @top-left { content: element(rh); width: 300px } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>", opts))).X;
        Assert.True(LineX("right") > LineX("left") + 100,
            "the element's text-align:right should push the line far right of text-align:left in a wide box");
    }

    [Fact]
    public void Page_margin_box_box_text_align_overrides_the_running_elements()
    {
        // Task B: the box's OWN text-align wins over the element's (the box declarations override). Box
        // `text-align: left` + element `text-align: right` → the line is LEFT (box wins) — identical to the
        // box-left case with no element text-align.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double LineX(string rhAlign) => FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); text-align: " + rhAlign + " } " +
            "@page { @top-left { content: element(rh); width: 300px; text-align: left } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>", opts))).X;
        Assert.Equal(LineX("left"), LineX("right"), 1);   // box text-align:left wins regardless of the element's
    }

    [Fact]
    public void Page_margin_box_element_inherited_text_align_resolves_to_the_ancestor_value()
    {
        // Review P2 (+ P3 end-to-end): a running element's `text-align: inherit` resolves to its DOM
        // ancestor's value (CSS Cascade L5 §7 — the collector's inherited-property walk continues past
        // inherit/unset/revert). `.section { text-align: right }` → the inherited element aligns RIGHT,
        // exactly like a direct `text-align: right`, and far right of `text-align: left`. (Before the fix the
        // raw `inherit` mapped to no factor and the @top-left box fell back to its name-derived LEFT.)
        // Observable in a wide explicit-width box — proving PageMarginBoxPainter consumes the inherited value.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double LineX(string rhAlign) => FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.section { text-align: right } " +
            ".rh { position: running(rh); text-align: " + rhAlign + " } " +
            "@page { @top-left { content: element(rh); width: 300px } }</style>" +
            "</head><body><div class=\"section\"><div class=\"rh\">AB</div></div></body></html>", opts))).X;
        Assert.Equal(LineX("right"), LineX("inherit"), 1);   // inherit resolves to the ancestor's right
        Assert.True(LineX("inherit") > LineX("left") + 100,  // and far right of left (name-derived would be left)
            "inherited text-align:right should push the line far right of text-align:left in a wide box");
    }

    [Fact]
    public void Page_margin_box_box_text_align_inherit_keeps_the_name_derived_default()
    {
        // Copilot review: when the BOX declares `text-align` as a CSS-wide keyword (inherit/unset/revert), a
        // margin box keeps its NAME-DERIVED default — it must NOT fall through to the running element's own
        // text-align (margin-box alignment isn't inherited). @top-left's name-derived default is LEFT, so
        // `@top-left { text-align: inherit }` keeps the line LEFT (== an explicit box `text-align: left`)
        // even though the element is `text-align: right`; a box-declared `text-align: right` is far off.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double LineX(string boxAlign) => FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); text-align: right } " +
            "@page { @top-left { content: element(rh); width: 300px; text-align: " + boxAlign + " } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>", opts))).X;
        Assert.Equal(LineX("left"), LineX("inherit"), 1);   // box inherit → name-derived left, NOT the element's right
        Assert.True(LineX("right") > LineX("inherit") + 100,
            "a box-declared recognized text-align:right should still move the line (sanity)");
    }

    [Fact]
    public void Page_margin_box_box_padding_overrides_the_running_elements()
    {
        // Review P3: the box's OWN padding wins over the element's (box declarations cascade LAST in
        // BuildFromOwnStyle). Box `padding-left: 0` over element `padding-left: 40px` → NO inset: the line
        // sits where it would with no element padding at all, not ~30pt (40px) to the right.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double LineX(string rhDecls) => FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh)" + rhDecls + " } " +
            "@page { @top-left { content: element(rh); padding-left: 0 } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>", opts))).X;
        Assert.Equal(LineX(""), LineX("; padding-left: 40px"), 1);   // box padding-left:0 overrides the element's 40px
    }

    [Fact]
    public void Page_margin_box_element_padding_top_insets_the_text_and_grows_the_height()
    {
        // Review P3 (vertical axis): the running element's own `padding-top` (1) insets its line DOWNWARD (a
        // smaller y in PDF y-up), like the box's own padding-top, and (2) grows a vertical-axis box's
        // shrink-to-fit HEIGHT. (1) @top-left + vertical-align:top + element padding-top:40px → line ~30pt
        // lower. (2) @left-middle (variable axis = height) + element padding-top:20px → band ~15pt taller.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double TextY(string pad) => FirstTd(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh)" + pad + " } " +
            "@page { @top-left { content: element(rh); vertical-align: top } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>", opts))).Y;
        Assert.InRange(TextY("") - TextY("; padding-top: 40px"), 28.0, 32.0);   // 40px padding-top → ~30pt down

        double BandH(string pad) => FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); background-color: red" + pad + " } " +
            "@page { @left-middle { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>", opts))).H;
        Assert.InRange(BandH("; padding-top: 20px") - BandH(""), 13.0, 17.0);   // +20px padding-top → +~15pt height
    }

    [Theory]
    [InlineData("10%")]                // a percentage (resolves against the containing block)
    [InlineData("1em")]                // a font-relative length
    [InlineData("calc(10% + 5px)")]    // a calc() with a non-absolute term (a px-only calc IS absolute)
    public void Page_margin_box_element_non_absolute_padding_resolves_and_insets(string pad)
    {
        // Relative-padding cycle (flips the post-PR-#153 review P3 contract): a %/em/calc() element
        // padding now RESOLVES in the margin context — % against the band, em against the box font,
        // calc() with both — with NO diagnostic; the line shifts right and the shrink-to-fit box grows
        // (mirroring the box's own relative-padding policy).
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); padding-left: " + pad + "; background-color: red } " +
            "@page { @top-center { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>", opts);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssPropertyValueInvalid001);

        // Shift + grow vs the no-padding baseline (every theory value resolves to ≥ 16px ≈ 12pt).
        var baseline = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); background-color: red } " +
            "@page { @top-center { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>", opts));
        var padded = Latin1(result.Pdf);
        Assert.True(FirstTd(padded).X > FirstTd(baseline).X + 5.0,
            $"the resolved element padding should shift the line right: {FirstTd(baseline).X} → {FirstTd(padded).X}");
        Assert.True(FirstRect(padded).W > FirstRect(baseline).W + 5.0,
            $"the resolved element padding should grow the shrink-to-fit box: {FirstRect(baseline).W} → {FirstRect(padded).W}");
    }

    // ---- element() nested BLOCK children (stacked lines) + vertical-edge height overflow ----

    [Fact]
    public void Page_margin_box_element_stacks_block_children_as_separate_lines()
    {
        // Task 23 nested BLOCK children first cut: a running element with two BLOCK children renders as TWO
        // STACKED lines, not one concatenated line. On a vertical @left-middle box (variable axis = height)
        // the shrink-to-fit band is ~1 line-height TALLER for two block children than for one flat line.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double BandH(string inner) => FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); background-color: red } " +
            "@page { @left-middle { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">" + inner + "</div></body></html>", opts))).H;
        var twoBlocks = BandH("<div>AB</div><div>AB</div>");
        var oneLine = BandH("ABAB");
        Assert.True(twoBlocks > oneLine + 10,
            $"two block children should stack into a ~1-line-taller band; two-block {twoBlocks} vs one-line {oneLine}");
    }

    [Fact]
    public void Page_margin_box_element_with_inline_only_children_stays_one_line()
    {
        // No block-level child (text + an inline <span>) → a single flat line (byte-identical to the
        // pre-first-cut path): the @left-middle band is the SAME height as a plain single-line element.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        double BandH(string inner) => FirstRect(Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); background-color: red } " +
            "@page { @left-middle { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">" + inner + "</div></body></html>", opts))).H;
        Assert.Equal(BandH("AB AB"), BandH("A<span>B</span> A<span>B</span>"), 1);   // inline children → one line
    }

    [Fact]
    public void Page_margin_box_vertical_content_overflow_emits_a_diagnostic_and_is_clipped()
    {
        // Task 23 vertical-edge height-overflow + the overflow-clipping cycle: a left/right edge box whose
        // content is TALLER than its band surfaces PAINT-MARGIN-BOX-CONTENT-OVERFLOW-001 AND the overflow is
        // clipped at line granularity. A 2000px running header far exceeds the @left-middle band — not even
        // one 2400px line fits, so NO text paints (was: the line spilled over the page). A 16px one fits.
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        var overflow = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); font-size: 2000px } " +
            "@page { @left-middle { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>", opts);
        Assert.Contains(overflow.Warnings, d => d.Code == DiagnosticCodes.PaintMarginBoxContentOverflow001);
        Assert.Equal(0, TotalGlyphCount(Latin1(overflow.Pdf)));   // fully clipped — no glyphs painted

        var fits = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); font-size: 16px } " +
            "@page { @left-middle { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>", opts);
        Assert.DoesNotContain(fits.Warnings, d => d.Code == DiagnosticCodes.PaintMarginBoxContentOverflow001);
        Assert.Equal(2, TotalGlyphCount(Latin1(fits.Pdf)));       // a fitting header still paints in full
    }

    [Fact]
    public void Page_margin_box_element_long_block_child_wraps_in_a_narrow_box()
    {
        // Review P2: a LONG block child WRAPS within a narrow box (pre-line) while the authored block
        // boundary still forces a break — so a 2-block element produces MORE lines in a narrow @top-center
        // than a wide one (the long block re-wraps), not 2 rigid overflowing lines. (Before the fix,
        // forced-break content used `pre` + skipped re-wrap, so the long block never wrapped.)
        var opts = new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() };
        int Lines(string widthCss)
        {
            var pdf = Latin1(HtmlPdf.Convert(
                "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
                "@page { @top-center { content: element(rh)" + widthCss + " } }</style></head>" +
                "<body><div class=\"rh\"><div>AA AA AA AA AA AA AA AA</div><div>BB</div></div></body></html>", opts));
            var n = 0;
            for (var i = pdf.IndexOf(" Td", StringComparison.Ordinal); i >= 0; i = pdf.IndexOf(" Td", i + 3, StringComparison.Ordinal))
                n++;
            return n;
        }
        var wide = Lines("; width: 400px");
        var narrow = Lines("; width: 60px");
        Assert.True(wide >= 2, $"the two block children keep their authored break (≥ 2 lines); got {wide}");
        Assert.True(narrow > wide, $"a long block child should wrap MORE in a narrower box; narrow {narrow} vs wide {wide}");
    }

    [Fact]
    public void Page_margin_box_content_overflow_diagnostic_names_the_box_and_dimensions()
    {
        // Review P3 + the overflow-clipping cycle: the overflow diagnostic is ACTIONABLE — its message
        // names the box (@left-middle), the measured content vs available height, AND the kept/total
        // line count of the line-granularity truncation (here 0 of 1 — not even one line fits).
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); font-size: 2000px } " +
            "@page { @left-middle { content: element(rh) } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var found = false;
        foreach (var d in result.Warnings)
        {
            if (d.Code != DiagnosticCodes.PaintMarginBoxContentOverflow001) continue;
            found = true;
            Assert.Contains("left-middle", d.Message);              // names the box
            Assert.Contains("px content", d.Message);               // includes the measured height
            Assert.Contains("0 of 1 line(s) painted", d.Message);   // names the truncation
        }
        Assert.True(found, "expected a PAINT-MARGIN-BOX-CONTENT-OVERFLOW-001 diagnostic");
    }

    [Fact]
    public void Page_margin_box_overflowing_lines_are_clipped_to_the_content_box()
    {
        // Overflow-clipping cycle: SIX stacked lines (six block children at the default 19.2px pitch =
        // 115.2px) exceed the 96px top band — only the first FIVE whole lines fit (floor(96.5/19.2)),
        // so 5 lines paint (5 Td operators; was 6, the last spilling into the body area) and the
        // diagnostic reports the truncation.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            "@page { @top-center { content: element(rh) } }</style></head>" +
            "<body><div class=\"rh\"><div>AB</div><div>AB</div><div>AB</div><div>AB</div><div>AB</div>" +
            "<div>AB</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);
        var lines = 0;
        for (var i = pdf.IndexOf(" Td", StringComparison.Ordinal); i >= 0; i = pdf.IndexOf(" Td", i + 3, StringComparison.Ordinal))
            lines++;
        Assert.Equal(5, lines);                                    // the 6th line is clipped
        Assert.Equal(10, TotalGlyphCount(pdf));                    // 5 × "AB", not 6
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.PaintMarginBoxContentOverflow001
            && d.Message.Contains("top-center") && d.Message.Contains("5 of 6 line(s) painted"));
    }

    [Fact]
    public void Page_margin_box_exactly_fitting_lines_are_not_clipped()
    {
        // Boundary: FIVE stacked lines = 5 × 19.2 = 96px fill the 96px top band EXACTLY — the epsilon
        // absorbs the boundary, so nothing is clipped and no overflow diagnostic fires.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            "@page { @top-center { content: element(rh) } }</style></head>" +
            "<body><div class=\"rh\"><div>AB</div><div>AB</div><div>AB</div><div>AB</div><div>AB</div>" +
            "</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.Equal(10, TotalGlyphCount(Latin1(result.Pdf)));     // all 5 lines paint
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PaintMarginBoxContentOverflow001);
    }

    [Fact]
    public void Page_margin_box_explicit_height_clips_overflowing_lines()
    {
        // Clipping is driven by the CONTENT-BOX height, so an explicit `height` (smaller than the band)
        // clips too: two stacked lines (38.4px) in a 30px-tall @left-middle keep only the first
        // (floor(30.5/19.2) = 1) — and the second line's glyphs don't paint.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh) } " +
            "@page { @left-middle { content: element(rh); height: 30px } }</style></head>" +
            "<body><div class=\"rh\"><div>AB</div><div>AB</div></div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        Assert.Equal(2, TotalGlyphCount(Latin1(result.Pdf)));      // 1 × "AB" kept, the 2nd line clipped
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.PaintMarginBoxContentOverflow001
            && d.Message.Contains("1 of 2 line(s) painted"));
    }

    [Fact]
    public void Page_margin_box_fully_clipped_box_still_paints_its_decoration()
    {
        // When not even one line fits, the box paints decoration only (like an empty `content: ""`
        // box): the background band is still filled while the text is fully clipped.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>.rh { position: running(rh); font-size: 2000px } " +
            "@page { @left-middle { content: element(rh); background-color: red } }</style>" +
            "</head><body><div class=\"rh\">AB</div></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });
        var pdf = Latin1(result.Pdf);
        Assert.Equal(0, TotalGlyphCount(pdf));                     // text fully clipped…
        Assert.True(FirstRect(pdf).W > 0);                         // …but the background band still paints
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


    [Fact]
    public void Rtl_first_left_only_margin_box_still_paints_on_the_first_page()
    {
        // PR #219 review [P1] — a document whose ONLY @page margin box is `:first:left` must still paint.
        // The "does any margin box render anywhere" gate (the structural union) has to include a
        // first-LEFT representative context, else the WHOLE margin pass is skipped and the box never
        // renders. In an RTL document page 1 is the physical LEFT page, so `:first:left` matches it; in
        // LTR page 1 is the first-RIGHT page, so the SAME rule must NOT paint there.
        const string sheet = "<style>@page :first:left { @bottom-center { content: \"FOOT\" } }</style>";
        var rtlWith = TdCount(Latin1(HtmlPdf.Convert(
            "<!doctype html><html><head>" + sheet + "</head>"
            + "<body style='direction:rtl;margin:0'>x</body></html>")));
        var ltrWith = TdCount(Latin1(HtmlPdf.Convert(
            "<!doctype html><html><head>" + sheet + "</head>"
            + "<body style='margin:0'>x</body></html>")));
        var rtlBaseline = TdCount(Latin1(HtmlPdf.Convert(
            "<!doctype html><html><body style='direction:rtl;margin:0'>x</body></html>")));
        var ltrBaseline = TdCount(Latin1(HtmlPdf.Convert(
            "<!doctype html><html><body style='margin:0'>x</body></html>")));
        Assert.True(rtlWith > rtlBaseline, "RTL first page is a left page → :first:left footer must paint");
        Assert.Equal(ltrBaseline, ltrWith);   // LTR first page is a RIGHT page → :first:left must NOT paint
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

    // Per-page ` Td` counts (one per emitted text line), in page order — the facade emits one
    // uncompressed content stream per page, so each text-bearing stream is one page's lines.
    private static System.Collections.Generic.List<int> TdCountsPerPage(string pdf)
    {
        var counts = new System.Collections.Generic.List<int>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            pdf, "stream(.*?)endstream", System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            var c = TdCount(m.Groups[1].Value);
            if (c > 0) counts.Add(c);
        }
        return counts;
    }

    /// <summary>The maximum x operand across every <c>&lt;x&gt; &lt;y&gt; Td</c> text-position operator —
    /// the rightmost glyph-run origin in the content stream. Used by the justify tests: justifying a
    /// line pushes its last word toward the right edge, so the max Td x grows.</summary>
    private static double MaxTdX(string pdf)
    {
        var max = double.NegativeInfinity;
        for (var i = pdf.IndexOf(" Td", StringComparison.Ordinal); i >= 0;
             i = pdf.IndexOf(" Td", i + 3, StringComparison.Ordinal))
        {
            var nums = pdf[..i].TrimEnd().Split(' ');   // … <x> <y>
            var x = double.Parse(nums[^2], CultureInfo.InvariantCulture);
            if (x > max) max = x;
        }
        return max;
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
