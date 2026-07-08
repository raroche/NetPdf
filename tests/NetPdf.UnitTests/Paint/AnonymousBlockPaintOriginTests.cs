// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NetPdf;
using NetPdf.Text.Fonts;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Paint;

/// <summary>
/// Paint-origin regression tests for the "anonymous block re-applies its parent's chrome at
/// paint time" defect (corpus QA round 2, F1 + F5). An <c>AnonymousBlock</c> synthesized for
/// bare text inside a padded <c>&lt;td&gt;</c> or a block-in-inline (<c>text + display:block</c>)
/// shares its parent's <see cref="NetPdf.Css.ComputedValues.ComputedStyle"/> BY REFERENCE. The
/// layouter correctly zeroes an anonymous box's border/padding/margin (CSS 2.2 §9.2.1.1 — an
/// anonymous box's non-inherited properties take their initial value), so its fragment
/// border-box IS its content box. Before the fix, <c>TextPainter.CollectFragment</c> and the
/// <c>BlockLayouter</c> recursion path read that shared style unguarded and inset the glyphs by
/// the PARENT's chrome a SECOND time — painting cell / block-in-inline text low-and-right by the
/// cell padding (F1: the item main line crushed against its subline; F5: "Thank you…" painted on
/// top of the confirmation line).
///
/// <para>Method: the facade emits an UNCOMPRESSED content stream (a determinism guarantee), so
/// each glyph run's <c>&lt;x&gt; &lt;y&gt; Td</c> origin is string-searchable. The synthetic font
/// makes the geometry reproducible without a system font. Each case compares the anonymous-box
/// render against a CONTROL where the same text is wrapped in an explicit real block (zero chrome)
/// — post-fix the two must paint at the same origin; pre-fix the anonymous render was offset.</para>
/// </summary>
public sealed class AnonymousBlockPaintOriginTests
{
    private static HtmlPdfOptions Opts() => new() { FontResolver = new SynthResolver() };

    private static string Doc(string body, string css = "") =>
        "<!DOCTYPE html><html><head><style>@page{size:A4;margin:20mm}" + css + "</style></head>"
        + "<body style=\"margin:0;font-family:sans-serif;font-size:12px\">" + body + "</body></html>";

    // A distinct text line, keyed by its glyph-show origin. Grouping by (rounded) baseline y
    // folds a line that shaped into several runs (e.g. per word) into ONE line whose start is the
    // minimum x — so the assertions hold regardless of run segmentation.
    private static List<(double Y, double MinX)> Lines(byte[] pdf) =>
        Regex.Matches(Encoding.Latin1.GetString(pdf), @"(-?[\d.]+) (-?[\d.]+) Td <[0-9A-Fa-f]*> Tj")
            .Select(m => (
                X: double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                Y: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .GroupBy(r => Math.Round(r.Y, 1))
            .Select(g => (Y: g.Key, MinX: g.Min(r => r.X)))
            .OrderByDescending(t => t.Y)   // PDF space is y-up: the first line is the topmost.
            .ToList();

    private static void AssertClose(double expected, double actual, double tol = 0.5) =>
        Assert.True(Math.Abs(expected - actual) <= tol,
            $"expected {expected:0.###} ± {tol}, got {actual:0.###} (Δ {actual - expected:0.###})");

    // ── F1: a cell's direct inline text (anonymous wrapper) and a block sibling must share the
    //         cell's content-left edge and stack vertically. Pre-fix the main line was inset right
    //         by the cell padding-left and sunk down by the padding-top, hugging the subline. ────
    [Fact]
    public void Cell_main_line_and_block_subline_share_the_content_left_edge()
    {
        var pdf = HtmlPdf.Convert(Doc(
            "<table><tr><td>Main line text<small>WBS 2070</small></td></tr></table>",
            "table{border-collapse:collapse}td{padding:8px 10px;vertical-align:top}"
            + "small{display:block;font-size:12px}"), Opts());

        var lines = Lines(pdf);
        Assert.True(lines.Count >= 2, $"expected 2 stacked lines, got {lines.Count}");
        // Main line (anonymous wrapper) and subline (real <small> block) start at the SAME x.
        AssertClose(lines[1].MinX, lines[0].MinX);
        // …and the main line sits ABOVE the subline (larger y in PDF bottom-left space).
        Assert.True(lines[0].Y > lines[1].Y);
    }

    // ── F1/F5: bare inline text in a padded cell must paint at the cell's content-box origin, not
    //           inset by the padding a second time. Asymmetric padding proves BOTH axes. ─────────
    [Fact]
    public void Anonymous_cell_text_is_not_double_inset_by_padding()
    {
        // top 40px, right 0, bottom 0, left 100px — a swapped or doubled axis is unmissable.
        const string css = "table{border-collapse:collapse}td{padding:40px 0 0 100px}";
        var repro = Lines(HtmlPdf.Convert(Doc("<table><tr><td>Cell</td></tr></table>", css), Opts()));
        var control = Lines(HtmlPdf.Convert(
            Doc("<table><tr><td><div>Cell</div></td></tr></table>", css), Opts()));

        Assert.Single(repro);
        Assert.Single(control);
        AssertClose(control[0].MinX, repro[0].MinX);   // no extra padding-left
        AssertClose(control[0].Y, repro[0].Y);         // no extra padding-top
    }

    // ── Regression guard: the fix must NOT over-zero — a REAL block's own padding still insets its
    //           text. (The bug was the anonymous wrapper reading the PARENT's chrome, not real
    //           chrome being wrongly applied.) ──────────────────────────────────────────────────
    [Fact]
    public void Real_block_padding_still_insets_its_own_text()
    {
        var padded = Lines(HtmlPdf.Convert(
            Doc("<div style='padding-left:60px'>Text</div>"), Opts()));
        var plain = Lines(HtmlPdf.Convert(Doc("<div>Text</div>"), Opts()));

        Assert.Single(padded);
        Assert.Single(plain);
        // 60px = 45pt to the right of the un-padded control — the real inset is preserved.
        AssertClose(plain[0].MinX + 45.0, padded[0].MinX);
    }

    // ── F5 alignment half: right-aligned cell text aligns to the cell's CONTENT right edge. Pre-fix
    //           the anonymous wrapper's alignment width was short by the parent's inline chrome, so
    //           the text landed padding-right short of the edge. ─────────────────────────────────
    [Fact]
    public void Right_aligned_cell_text_reaches_the_content_right_edge()
    {
        const string css = "table{border-collapse:collapse}td{text-align:right;padding:0 20px}";
        var repro = Lines(HtmlPdf.Convert(Doc("<table><tr><td>Total</td></tr></table>", css), Opts()));
        var control = Lines(HtmlPdf.Convert(
            Doc("<table><tr><td><div>Total</div></td></tr></table>", css), Opts()));

        Assert.Single(repro);
        Assert.Single(control);
        // text-align is inherited, so the control's inner div right-aligns to the same content edge.
        AssertClose(control[0].MinX, repro[0].MinX);
    }

    // ── F5 site 2 (BlockLayouter recursion): a nested block's margin-top must NOT be charged a
    //           second time to the anonymous wrapper of its leading inline text. ─────────────────
    [Fact]
    public void Nested_margined_block_does_not_double_apply_margin_to_anon_text()
    {
        // The inner block is a NESTED container (child of the outer div) with mixed content
        // (text + a display:block sibling) → the anonymous "Main" wrapper is emitted on the
        // recursion path. Its y must equal the control (explicit-div-wrapped) render's y.
        const string css = ".inner{margin-top:30px}.sub{display:block;font-size:12px}";
        var repro = Lines(HtmlPdf.Convert(Doc(
            "<div><div class='inner'>Main<span class='sub'>Sub</span></div></div>", css), Opts()));
        var control = Lines(HtmlPdf.Convert(Doc(
            "<div><div class='inner'><div>Main</div><span class='sub'>Sub</span></div></div>", css), Opts()));

        Assert.True(repro.Count >= 2 && control.Count >= 2);
        // The topmost line ("Main") sits at the same y in both — the parent margin is applied once.
        AssertClose(control[0].Y, repro[0].Y);
        // …and "Main" stays ABOVE "Sub" (document order preserved, no sink-onto-sibling).
        Assert.True(repro[0].Y > repro[1].Y);
    }

    // ── F5 integration: the real 05-payment-receipt block-in-inline pattern. Pre-fix "Thank you for
    //           your payment!" sank by the .thanks block-start chrome onto the confirmation line. ─
    [Fact]
    public void Payment_receipt_block_in_inline_text_sits_above_the_sub_line()
    {
        var pdf = HtmlPdf.Convert(Doc(
            "<div class='thanks'>Thank you for your payment!"
            + "<span class='sub'>A confirmation has been emailed to you.</span></div>",
            ".thanks{text-align:center;font-size:14px;padding:14px;border-top:1px dashed #a7f3d0;margin-top:8px}"
            + ".thanks .sub{display:block;font-size:10px;margin-top:4px}"), Opts());

        var lines = Lines(pdf);
        Assert.True(lines.Count >= 2, $"expected 2 stacked lines, got {lines.Count}");
        // The bold line is clearly ABOVE the sub line — a full line-height apart, no overlap.
        // Pre-fix the two shared a y-band (the bold line painted on top of the sub line).
        Assert.True(lines[0].Y - lines[1].Y > 6.0,
            $"'Thank you…' and the sub line overlap: Δy = {lines[0].Y - lines[1].Y:0.###}pt");
    }

    private sealed class SynthResolver : IFontResolver
    {
        private static readonly byte[] FontBytes = SyntheticFont.Build();

        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = FontBytes, Family = query.Family });
    }
}
