// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NetPdf;
using NetPdf.Text.Fonts;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Layout;

/// <summary>
/// Rendering-fidelity regressions found on the travel-document corpus:
/// <list type="bullet">
///   <item><b>RC1</b> — a flex item whose content is BLOCK-level children was measured at the huge
///   max-content available width (a <c>width:auto</c> block fills it), so its flex base size blew up
///   and the flex line falsely overflowed → the sibling collapsed toward min-content and its text
///   wrapped one word per line (the header "Sailing / with / Meridian / …" disaster).</item>
///   <item><b>RC3</b> — an inline <c>&lt;svg&gt;</c> element in HTML flow was not treated as a replaced
///   element: unhandled, it filled the available width (poisoning the flex measure above) and rendered
///   nothing. It is now a replaced element (intrinsic size from width/height/viewBox) painted via the
///   SVG pipeline.</item>
/// </list>
/// </summary>
public sealed class FlexIntrinsicAndInlineSvgTests
{
    private static HtmlPdfOptions Opts() => new() { FontResolver = new SynthResolver() };

    private static string Doc(string body) =>
        "<!DOCTYPE html><html><head><style>@page{size:A4;margin:16mm}*{box-sizing:border-box}"
        + "body{font-family:sans-serif;font-size:10.5pt;margin:0}"
        + ".bar{display:flex;justify-content:space-between;align-items:center}"
        + ".brand{display:flex;align-items:center;gap:12px}.name{font-size:15pt;font-weight:700}"
        + ".right{text-align:right;font-size:9pt}</style></head><body>" + body + "</body></html>";

    // ── RC1: a nested-block-in-flex item must not collapse its space-between sibling ─────────

    [Fact]
    public void Flex_item_with_block_children_does_not_collapse_the_space_between_sibling()
    {
        // The left flex item is a nested flex whose child is a BLOCK wrapper holding block lines — the
        // exact shape that used to measure as 1e6 and collapse `.right` to one word per line.
        var pdf = HtmlPdf.Convert(Doc(
            "<div class='bar'>"
            + "<div class='brand'><div><div class='name'>Azure Horizon Travel</div>"
            + "<div>Curated Ocean Voyages</div></div></div>"
            + "<div class='right'>Sailing with Meridian Cruise Line</div></div>"), Opts());

        // The right block is one logical line on EXACTLY one row: a collapse-to-min-content would wrap
        // it into many rows (one per word), and a `== 1` (not `<= 1`) also fails the degenerate case
        // where the block disappeared / moved off the right half (0 rows) — review #275 [P3].
        var rightRows = DistinctTextRowsInRightHalf(pdf);
        Assert.Equal(1, rightRows);
    }

    [Fact]
    public void Deeply_nested_auto_width_blocks_still_measure_to_content_not_available()
    {
        // A control with an even deeper auto-width block nest on the left — still must not collapse the right.
        var pdf = HtmlPdf.Convert(Doc(
            "<div class='bar'>"
            + "<div class='brand'><div><div><div class='name'>Azure Horizon Travel</div></div></div></div>"
            + "<div class='right'>Issued March 12 2026</div></div>"), Opts());
        Assert.Equal(1, DistinctTextRowsInRightHalf(pdf));
    }

    // ── RC3: inline <svg> renders as a replaced element ─────────────────────────────────────

    [Fact]
    public void Inline_svg_renders_as_a_replaced_element()
    {
        var pdf = Encoding.Latin1.GetString(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><p>logo:</p>"
            + "<svg width='40' height='40' viewBox='0 0 40 40'>"
            + "<circle cx='20' cy='20' r='18' fill='#e02020'/></svg></body></html>", Opts()));

        // The inline SVG is decoded into an image XObject and painted (a `/Name Do`).
        Assert.Contains("/Subtype /Image", pdf);
        Assert.Matches(@"/[A-Za-z0-9]+ Do", pdf);
    }

    [Fact]
    public void Inline_svg_logo_in_a_flex_header_does_not_collapse_the_sibling()
    {
        // The real header shape: a logo <svg> + text block on the left, info block on the right. The svg
        // used to measure as 1e6 and collapse the right block.
        var pdf = HtmlPdf.Convert(Doc(
            "<div class='bar'>"
            + "<div class='brand'><svg width='46' height='46' viewBox='0 0 46 46'>"
            + "<circle cx='23' cy='23' r='20' fill='#0d5c7a'/></svg>"
            + "<div><div class='name'>Azure Horizon Travel</div></div></div>"
            + "<div class='right'>Sailing with Meridian Cruise Line</div></div>"), Opts());
        Assert.Equal(1, DistinctTextRowsInRightHalf(pdf));
    }

    [Fact]
    public void Min_width_only_block_child_keeps_its_floor_in_flex_intrinsic_measurement()
    {
        // Review #275 [P2] — a nowrap flex row (flex-start) whose FIRST item's only content is an empty
        // block with `min-width:200px` (≈150pt) and no line descendants / explicit width. The intrinsic
        // measure must floor the item at 200px, so the SECOND item's text ("Y") starts ~150pt in. If the
        // floor were lost (the fix's else-branch only counting explicit widths), item 1 would measure 0 and
        // "Y" would sit at the left edge.
        var pdf = Encoding.Latin1.GetString(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><style>@page{margin:16mm}*{box-sizing:border-box}"
            + "body{font-family:sans-serif;margin:0}.row{display:flex;justify-content:flex-start}</style></head><body>"
            + "<div class='row'><div><div style='min-width:200px;height:6px'></div></div><div>Y</div></div>"
            + "</body></html>", Opts()));

        var yx = FirstTextX(pdf);
        Assert.True(yx > 120, $"'Y' at x={yx:0.#}pt — the min-width floor was lost (expected ≳150pt).");
    }

    [Fact]
    public void Inline_svg_current_color_resolves_to_the_html_context_color()
    {
        // Review #275 [P2] — fill="currentColor" must resolve to the surrounding HTML color (seeded onto
        // the SVG root), so the same SVG under color:red vs color:blue rasterizes to DIFFERENT images.
        byte[] Render(string color) => HtmlPdf.Convert(
            $"<!DOCTYPE html><html><body><span style=\"color:{color}\">"
            + "<svg width='30' height='30' viewBox='0 0 30 30'><rect x='3' y='3' width='24' height='24' "
            + "fill='currentColor'/></svg></span></body></html>", Opts());

        var red = Render("red");
        var blue = Render("blue");
        Assert.Contains("/Subtype /Image", Encoding.Latin1.GetString(red));
        Assert.NotEqual(red, blue);   // currentColor was applied (else both would be identical)
    }

    [Theory]
    [InlineData("filter:grayscale(1)")]
    [InlineData("filter:drop-shadow(1px 1px 1px #000)")]
    public void Inline_svg_filter_is_supported_not_diagnosed_unsupported(string style)
    {
        // Review #275 [P2] — inline <svg> reuses the <img> image-spec path, so a valid CSS filter parses
        // and applies rather than being reported unsupported.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>"
            + $"<svg style=\"{style}\" width='30' height='30' viewBox='0 0 30 30'>"
            + "<rect x='3' y='3' width='24' height='24' fill='#333'/></svg></body></html>");
        Assert.DoesNotContain(result.Warnings, w => w.Code == "CSS-FILTER-UNSUPPORTED-001");
        Assert.Contains("/Subtype /Image", Encoding.Latin1.GetString(result.Pdf));
    }

    [Fact]
    public void Inline_svg_with_only_a_viewbox_is_intrinsically_sized_and_renders()
    {
        // Review #275 [P3] — no width/height attributes: intrinsic size comes from the viewBox.
        var pdf = Encoding.Latin1.GetString(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>"
            + "<svg viewBox='0 0 80 40'><rect x='2' y='2' width='76' height='36' fill='#28a'/></svg>"
            + "</body></html>", Opts()));
        Assert.Contains("/Subtype /Image", pdf);
        Assert.Matches(@"/[A-Za-z0-9]+ Do", pdf);
    }

    [Fact]
    public void Repeated_identical_inline_svgs_share_the_same_image_xobjects()
    {
        // Review #275 [P3] — content-keyed dedup: a SECOND identical inline SVG adds NO new image XObjects
        // (the count is the same as with a single SVG). (An RGBA SVG contributes an image + its /SMask, so
        // the absolute count isn't 1 — the point is that the duplicate is free.)
        const string svg = "<svg width='20' height='20' viewBox='0 0 20 20'>"
            + "<circle cx='10' cy='10' r='8' fill='#c33'/></svg>";
        int Images(string body) => Regex.Matches(
            Encoding.Latin1.GetString(HtmlPdf.Convert($"<!DOCTYPE html><html><body>{body}</body></html>", Opts())),
            "/Subtype /Image").Count;

        Assert.Equal(Images(svg), Images(svg + svg));
    }

    [Fact]
    public void Native_svg_opt_in_renders_inline_svg_without_error()
    {
        // Review #275 [P3] — the native-vector opt-in path also applies to inline <svg>.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>"
            + "<svg width='30' height='30' viewBox='0 0 30 30'><rect x='3' y='3' width='24' height='24' fill='#333'/></svg>"
            + "</body></html>", new HtmlPdfOptions { NativeSvgRendering = true, FontResolver = new SynthResolver() });
        var pdf = Encoding.Latin1.GetString(result.Pdf);
        // Painted either as native vector ops or the raster fallback — either way, content is emitted.
        Assert.True(pdf.Contains("/Subtype /Image") || Regex.IsMatch(pdf, @"\d+\.?\d* \d+\.?\d* \d+\.?\d* rg"),
            "inline SVG under NativeSvgRendering rendered nothing");
    }

    /// <summary>Number of distinct text baseline rows whose x-origin is on the RIGHT half of an A4 page
    /// (&gt; 297pt). A collapsed right block wraps one word per line → many rows; a correctly-sized one → 1.</summary>
    private static int DistinctTextRowsInRightHalf(byte[] pdf)
    {
        var s = Encoding.Latin1.GetString(pdf);
        var ys = new HashSet<int>();
        foreach (Match m in Regex.Matches(s, @"BT(.*?)ET", RegexOptions.Singleline))
        {
            var blk = m.Groups[1].Value;
            double x, y;
            var tm = Regex.Match(blk, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) Tm");
            var td = Regex.Match(blk, @"(-?[\d.]+) (-?[\d.]+) (?:Td|TD)");
            if (tm.Success) { x = D(tm.Groups[5].Value); y = D(tm.Groups[6].Value); }
            else if (td.Success) { x = D(td.Groups[1].Value); y = D(td.Groups[2].Value); }
            else continue;
            if (!Regex.IsMatch(blk, @"<[0-9A-Fa-f]+>")) continue;   // has shown text
            if (x > 297) ys.Add((int)(y + 0.5));
        }

        return ys.Count;
    }

    private static double D(string v) => double.Parse(v, CultureInfo.InvariantCulture);

    /// <summary>The x-origin of the first shown text run (handles the <c>Tm</c> and <c>Td</c> forms).</summary>
    private static double FirstTextX(string pdf)
    {
        foreach (Match m in Regex.Matches(pdf, @"BT(.*?)ET", RegexOptions.Singleline))
        {
            var blk = m.Groups[1].Value;
            if (!Regex.IsMatch(blk, @"<[0-9A-Fa-f]+>")) continue;
            var tm = Regex.Match(blk, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) Tm");
            if (tm.Success) return D(tm.Groups[5].Value);
            var td = Regex.Match(blk, @"(-?[\d.]+) (-?[\d.]+) (?:Td|TD)");
            if (td.Success) return D(td.Groups[1].Value);
        }

        return -1;
    }

    private sealed class SynthResolver : IFontResolver
    {
        private static readonly byte[] FontBytes = SyntheticFont.Build();

        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = FontBytes, Family = query.Family });
    }
}
