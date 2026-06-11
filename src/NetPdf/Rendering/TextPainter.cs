// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Diagnostics;
using NetPdf.Layout.Inline;
using NetPdf.Layout.Layouters;
using NetPdf.Pdf;
using NetPdf.Pdf.Fonts;
using NetPdf.Pdf.Objects;
using NetPdf.Shaping;
using NetPdf.Text.Fonts.OpenType;

namespace NetPdf.Rendering;

/// <summary>
/// Per Phase 5 layout→PDF cycle 5a-2-ii — the text paint bridge: turns the inline
/// layout (shaped glyph runs on wrapped lines) carried on each
/// <see cref="BoxFragment.InlineLayout"/> into real PDF glyph-show operators, in
/// concert with the same font subsetting / embedding machinery the PDF layer already
/// has. Runs AFTER <see cref="FragmentPainter"/> so text paints over backgrounds + borders.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two passes.</b> (1) <i>Collect</i> — walk every fragment's lines → slices →
/// shaped runs, gathering the ORIGINAL glyph ids used per RESOLVED font program (keyed by
/// the shaper's <see cref="HarfBuzzShaperResolver.ResolvedFontProgram.Identity"/> — a content
/// hash, so distinct family stacks that fall back to the same face share one subset) and
/// recording one draw command per slice with its baseline geometry + fill color. (2) <i>Build</i> —
/// per font, subset + embed exactly the used glyphs
/// (<see cref="OpenTypeFont.Parse"/> → <see cref="GlyphSubsetPlan.Build"/> →
/// <see cref="TtfSubsetter.Subset"/> → <see cref="ToUnicodeCMap.FromSubset"/> →
/// <see cref="EmbeddedTtfFont.Build"/> → <see cref="PdfDocument.RegisterFont"/> →
/// <see cref="PdfPage.AddFont"/>) and map original→subset glyph ids. Then <i>replay</i> the
/// draw commands via <see cref="PdfPage.ShowGlyphs"/>. The subset parses the SAME bytes
/// layout shaped (the resolver is kept alive past layout), so the shaped glyph ids index
/// the same program.
/// </para>
/// <para>
/// <b>Determinism.</b> Fonts are built in first-seen (deterministic walk) order via an
/// explicit ordered key list — never by dictionary iteration order — and the used-glyph
/// seed is sorted, so the emitted bytes are stable for stable input (CLAUDE.md #4). With a
/// fixed <c>IFontResolver</c> the whole path is reproducible; the default
/// <see cref="SystemFontResolver"/> path is not deterministic-for-text until a bundled
/// fallback font ships (deferrals.md#layout-to-pdf-pipeline).
/// </para>
/// <para>
/// <b>First-cut positioning (cycle 5a-2-ii).</b> Each slice emits one
/// <see cref="PdfPage.ShowGlyphs"/> call at its baseline origin; inter-glyph spacing comes
/// from the embedded font's <c>/W</c> advances ("simple Td + Tj" — Roland's pick).
/// Baselines are derived from the block's line-height (mirroring
/// <c>BlockLayouter</c>'s inline-only block model) + the run font's ascent; there is no
/// explicit per-line baseline in the IR. Partial text-fill alpha IS composited (via the same
/// <c>/ca</c> ExtGState the fills use). DEFERRED to a later cycle: GPOS-adjusted per-glyph
/// positioning, RTL visual-order line reversal, and per-run baseline alignment on
/// mixed-size lines.
/// </para>
/// </remarks>
internal static class TextPainter
{
    /// <summary>Fallback fill when <c>color</c> resolves to nothing — opaque black (the UA default).</summary>
    private const uint DefaultColorArgb = 0xFF000000;

    /// <summary>The de-facto <c>line-height: normal</c> multiple — mirrors
    /// <c>BlockLayouter</c>'s inline-only block metric so painted baselines line up with the
    /// box the layouter reserved.</summary>
    private const double NormalLineHeightFactor = 1.2;

    /// <summary>
    /// Paint the shaped text of every fragment carrying an
    /// <see cref="BoxFragment.InlineLayout"/> onto <paramref name="page"/>.
    /// </summary>
    /// <param name="fragments">The page's fragments in paint order.</param>
    /// <param name="page">Destination page (text is painted over backgrounds + borders).</param>
    /// <param name="document">Owns the embedded-font indirect objects (<see cref="PdfDocument.RegisterFont"/>).</param>
    /// <param name="shaper">The SAME resolver layout shaped with, kept alive past layout so the
    /// painter subsets the exact font program (glyph ids match).</param>
    /// <param name="pageHeightPt">Full page height in PDF points — the y-flip pivot.</param>
    /// <param name="contentOriginLeftPx">Left page margin in CSS px (fragments are content-area-relative).</param>
    /// <param name="contentOriginTopPx">Top page margin in CSS px.</param>
    /// <param name="diagnostics">Sink for skipped-text diagnostics; <see langword="null"/> drops them.</param>
    public static void PaintText(
        IReadOnlyList<BoxFragment> fragments,
        PdfPage page,
        PdfDocument document,
        HarfBuzzShaperResolver shaper,
        double pageHeightPt,
        double contentOriginLeftPx,
        double contentOriginTopPx,
        IDiagnosticsSink? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(fragments);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(shaper);

        var collects = new Dictionary<string, FontCollect>(StringComparer.Ordinal);
        var fontOrder = new List<string>();          // first-seen order → deterministic build order.
        var failed = new HashSet<string>(StringComparer.Ordinal);   // font keys that couldn't resolve/parse/build.
        var diagnosed = new HashSet<string>(StringComparer.Ordinal); // dedup of emitted skip messages.
        var draws = new List<DrawCommand>();

        // ---- Pass 1: collect used glyphs per font + record draw commands ----
        for (var i = 0; i < fragments.Count; i++)
        {
            var fragment = fragments[i];
            if (fragment.InlineLayout is not { } inline) continue;
            if (inline.Lines.Length == 0) continue;
            var blockStyle = fragment.Box.Style;
            if (blockStyle is null) continue;

            CollectFragment(
                fragment, inline, blockStyle,
                contentOriginLeftPx, contentOriginTopPx, pageHeightPt,
                shaper, collects, fontOrder, failed, diagnosed, draws, diagnostics);
        }

        if (draws.Count == 0) return; // nothing painted (text-free, or all runs skipped).

        // ---- Pass 2a: build (subset + embed + register) each font, in deterministic order ----
        var emits = new Dictionary<string, FontEmit>(StringComparer.Ordinal);
        foreach (var key in fontOrder)
        {
            var fc = collects[key];
            try
            {
                var plan = GlyphSubsetPlan.Build(fc.Font, fc.SortedUsedGlyphIds());
                var subset = TtfSubsetter.Subset(fc.Font, plan);
                var toUnicode = ToUnicodeCMap.FromSubset(fc.Font, plan);
                var embedded = EmbeddedTtfFont.Build(fc.Font, subset, toUnicode);
                var name = page.AddFont(document.RegisterFont(embedded));
                emits[key] = new FontEmit(name, plan.OldToNew);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                          or ArgumentException or FormatException)
            {
                // A resolved+validated font we still can't subset/embed (e.g. a CFF/OTF
                // outline font TtfSubsetter doesn't handle yet). Skip its text, don't fail
                // the whole render (CLAUDE.md #7 — surface, don't drop silently).
                Diagnose(diagnostics, diagnosed,
                    $"font program '{key}' could not be subset/embedded: {ex.Message}");
            }
        }

        // ---- Pass 2b: replay the draw commands, mapping original → subset glyph ids ----
        // A fragment's clip rect (margin-box overflow clip-path cycle) wraps its glyph runs in ONE
        // q <rect> re W n … Q pair: draws are replayed in collect order, so a fragment's commands are
        // contiguous and the clip opens on the first drawn command carrying it and closes when the clip
        // changes (or at the end). The state is transitioned only for commands that actually draw —
        // a skipped (failed-font) command must not open a clip. ShowGlyphs wraps each run in its own
        // q/Q, so nesting inside the clip's q/Q is balanced.
        (double X, double Y, double W, double H)? openClipPt = null;
        foreach (var cmd in draws)
        {
            if (!emits.TryGetValue(cmd.FontKey, out var emit)) continue; // build failed → already diagnosed.
            if (cmd.ClipPt != openClipPt)
            {
                if (openClipPt is not null) page.RestoreGraphicsState();
                openClipPt = cmd.ClipPt;
                if (openClipPt is { } clip) page.BeginRectangleClip(clip.X, clip.Y, clip.W, clip.H);
            }
            var subsetIds = new ushort[cmd.OriginalGlyphIds.Length];
            for (var g = 0; g < subsetIds.Length; g++)
                subsetIds[g] = (ushort)emit.OldToNew[cmd.OriginalGlyphIds[g]];

            FragmentPainter.ColorChannels(cmd.ColorArgb, out var r, out var g2, out var b);
            // Partial text alpha composites via ExtGState /ca, like fills (review P2) — fully
            // transparent runs were already dropped at collect time.
            var alpha = FragmentPainter.Alpha(cmd.ColorArgb) / 255.0;
            page.ShowGlyphs(emit.Name, cmd.SizePt, cmd.XPt, cmd.YPt, subsetIds, r, g2, b, alpha);
        }
        if (openClipPt is not null) page.RestoreGraphicsState();
    }

    /// <summary>Collect one fragment's inline text: walk its lines → slices → shaped runs,
    /// resolving each run's font + accumulating its used glyph ids and a draw command.</summary>
    private static void CollectFragment(
        BoxFragment fragment, InlineLayoutResult inline, ComputedStyle blockStyle,
        double contentOriginLeftPx, double contentOriginTopPx, double pageHeightPt,
        HarfBuzzShaperResolver shaper,
        Dictionary<string, FontCollect> collects, List<string> fontOrder,
        HashSet<string> failed, HashSet<string> diagnosed, List<DrawCommand> draws,
        IDiagnosticsSink? diagnostics)
    {
        // Content-box origin (CSS px, page-top-relative): the border box minus the
        // border + padding the layouter inset the inline content by.
        var contentLeftPx = contentOriginLeftPx + fragment.InlineOffset
            + blockStyle.ReadLengthPxOrZero(PropertyId.BorderLeftWidth)
            + blockStyle.ReadLengthPxOrZero(PropertyId.PaddingLeft);
        var contentTopPx = contentOriginTopPx + fragment.BlockOffset
            + blockStyle.ReadLengthPxOrZero(PropertyId.BorderTopWidth)
            + blockStyle.ReadLengthPxOrZero(PropertyId.PaddingTop);

        // line-height: mirror BlockLayouter's inline-only block rule EXACTLY (declared
        // line-height when > 0, else 1.2 × the block's font-size) so painted lines stack at
        // the same pitch the layouter reserved. The line METRICS follow the fragment's
        // TextMetricsStyle when set (post-PR-#151 review P1 — a page-margin box rendering a
        // standalone element() in the running element's own font shapes glyphs at THAT
        // font-size, so the pitch/baseline must match it, not the box's default), falling back
        // to the box style so every other fragment is byte-identical.
        var metricsStyle = fragment.TextMetricsStyle ?? blockStyle;
        var lineHeightOverridePx = metricsStyle.ReadLengthPxOrZero(PropertyId.LineHeight);
        var lineHeightPx = lineHeightOverridePx > 0
            ? lineHeightOverridePx
            : metricsStyle.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16) * NormalLineHeightFactor;

        var lines = inline.Lines;
        var shapedRuns = inline.ShapedRuns;
        var preRuns = inline.PreprocessedRuns;

        // The fragment's opt-in clip rect (margin-box overflow clip-path cycle) → page-pt once per
        // fragment; every draw command this fragment queues carries it (replay groups them under one
        // q … re W n / Q pair). Content-area-relative px, like the fragment offsets.
        (double X, double Y, double W, double H)? clipPt = null;
        if (fragment.ClipRect is { } clipRect)
        {
            FragmentPainter.ToPdfRect(
                contentOriginLeftPx + clipRect.LeftPx, contentOriginTopPx + clipRect.TopPx,
                clipRect.WidthPx, clipRect.HeightPx, pageHeightPt,
                out var cx, out var cy, out var cw, out var ch);
            clipPt = (cx, cy, cw, ch);
        }

        var cumulativeTopPx = 0.0;   // per-line pitch (segment-pitch cycle): cumulative line tops.
        for (var li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            // Per-line pitch (segment-pitch cycle): when the fragment carries per-line heights,
            // line i sits below the SUM of its predecessors' heights and uses ITS OWN height for
            // the half-leading below; the null default keeps the uniform li × pitch, byte-identical.
            var thisLineHeightPx = fragment.PerLineHeightsPx is { } heights && li < heights.Count
                ? heights[li] : lineHeightPx;
            // Per-line TOP GAP (segment-margins cycle): a leaf block's collapsed vertical margin
            // pushes its line down BEFORE placement (the gap is transparent — the painter's band
            // starts after it). Null = no gaps, byte-identical.
            if (fragment.PerLineTopOffsetsPx is { } gaps && li < gaps.Count)
                cumulativeTopPx += gaps[li];
            var lineTopPx = contentTopPx + (fragment.PerLineHeightsPx is null
                ? li * lineHeightPx : cumulativeTopPx);
            cumulativeTopPx += thisLineHeightPx;
            // Per-line inline alignment (wrapped-line content-alignment, Task 21; per-line factors —
            // segment-align cycle): shift each line by its own leftover × the line's align factor
            // (the fragment-wide factor unless per-line factors are present; 0 = start; default, so
            // non-margin fragments are unchanged). Clamped to ≥ 0 so a line wider than the content
            // box still starts at the left edge.
            var lineAlignFactor = fragment.PerLineAlignFactors is { } factors && li < factors.Count
                ? factors[li] : fragment.LineAlignFactor;
            var xCursorPx = lineAlignFactor != 0.0
                ? Math.Max(0.0, (fragment.InlineSize - line.TotalAdvance) * lineAlignFactor)
                : 0.0;

            foreach (var slice in line.Slices)
            {
                // Advance the line cursor for every slice — even skipped ones — so the
                // remaining slices on the line stay horizontally positioned.
                var sliceStartXPx = xCursorPx;
                xCursorPx += slice.SliceAdvance;

                if (slice.GlyphLength <= 0) continue;
                var run = shapedRuns[slice.ShapedRunIndex];
                var runStyle = preRuns[run.Source.SourceTextRunIndex].Style;

                var fontSizePx = runStyle.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16);
                if (fontSizePx <= 0) continue; // font-size:0 paint guard — nothing visible to emit.

                if (!FragmentPainter.TryResolveColor(runStyle.Get(PropertyId.Color), DefaultColorArgb, out var argb))
                    argb = DefaultColorArgb;
                if (FragmentPainter.Alpha(argb) == 0) continue; // fully transparent text paints nothing.

                if (!TryGetFontCollect(shaper, runStyle, collects, fontOrder, failed, diagnosed, diagnostics,
                        out var key, out var fc))
                    continue;

                // Gather this slice's ORIGINAL glyph ids; seed the font's used set.
                var originalIds = new ushort[slice.GlyphLength];
                for (var g = 0; g < slice.GlyphLength; g++)
                {
                    var gid = run.Glyphs[slice.GlyphStart + g].GlyphId;
                    originalIds[g] = gid;
                    fc.Used.Add(gid);
                }

                // Baseline: centre the run font's em box in the line box (half-leading), then
                // drop to the baseline by the ascent. Metrics from the run's parsed font.
                var unitsPerEm = fc.Font.Head.UnitsPerEm;
                var ascentPx = fc.Font.Hhea.Ascender * fontSizePx / unitsPerEm;
                var descentPx = fc.Font.Hhea.Descender * fontSizePx / unitsPerEm; // negative for Latin.
                var halfLeadingPx = (thisLineHeightPx - (ascentPx - descentPx)) / 2.0;
                var baselineTopPx = lineTopPx + halfLeadingPx + ascentPx;

                // The first glyph's shaped x-offset shifts the run origin; subsequent glyphs
                // are spaced by the font /W advances (first-cut Td + Tj).
                var xStartPx = contentLeftPx + sliceStartXPx + run.Glyphs[slice.GlyphStart].XOffset;

                draws.Add(new DrawCommand(
                    FontKey: key,
                    OriginalGlyphIds: originalIds,
                    XPt: PdfUnits.PxToPt(xStartPx),
                    YPt: pageHeightPt - PdfUnits.PxToPt(baselineTopPx),
                    SizePt: PdfUnits.PxToPt(fontSizePx),
                    ColorArgb: argb,
                    ClipPt: clipPt));
            }
        }
    }

    /// <summary>Resolve a run's font program to a parsed-font collector, caching by program
    /// key. Returns <see langword="false"/> (and diagnoses once) when the font can't be
    /// resolved or parsed — the caller skips that run.</summary>
    private static bool TryGetFontCollect(
        HarfBuzzShaperResolver shaper, ComputedStyle runStyle,
        Dictionary<string, FontCollect> collects, List<string> fontOrder,
        HashSet<string> failed, HashSet<string> diagnosed, IDiagnosticsSink? diagnostics,
        out string key, out FontCollect collect)
    {
        collect = null!;
        key = string.Empty;
        HarfBuzzShaperResolver.ResolvedFontProgram program;
        try
        {
            program = shaper.ResolveFontProgram(runStyle);
        }
        catch (InvalidOperationException ex)
        {
            // No face for the family stack, or unsafe / WOFF-wrapped bytes. Skip + surface.
            Diagnose(diagnostics, diagnosed, $"text run font could not be resolved: {ex.Message}");
            return false;
        }

        key = program.Identity;
        if (failed.Contains(key)) return false;
        if (collects.TryGetValue(key, out var existing)) { collect = existing; return true; }

        try
        {
            var font = OpenTypeFont.Parse(program.Bytes);
            collect = new FontCollect(font);
            collects[key] = collect;
            fontOrder.Add(key);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                      or ArgumentException or FormatException)
        {
            failed.Add(key);
            Diagnose(diagnostics, diagnosed, $"font program '{key}' could not be parsed: {ex.Message}");
            return false;
        }
    }

    private static void Diagnose(IDiagnosticsSink? diagnostics, HashSet<string> diagnosed, string detail)
    {
        if (!diagnosed.Add(detail)) return; // one diagnostic per distinct failure.
        diagnostics?.Emit(new Diagnostic(
            DiagnosticCodes.PaintTextFontUnresolved001,
            "A text run was not painted because its font could not be used: " + detail +
            " The rest of the page still renders. A bundled deterministic last-resort font is a " +
            "tracked follow-up (deferrals.md#layout-to-pdf-pipeline).",
            DiagnosticSeverity.Warning));
    }

    /// <summary>Per-font collection state: the parsed font + the set of original glyph ids
    /// used across all runs that reference it.</summary>
    private sealed class FontCollect(OpenTypeFont font)
    {
        public OpenTypeFont Font { get; } = font;
        public HashSet<int> Used { get; } = [];

        /// <summary>The used glyph ids as a SORTED array — a stable seed for
        /// <see cref="GlyphSubsetPlan.Build"/> independent of hash-set iteration order
        /// (determinism, CLAUDE.md #4).</summary>
        public int[] SortedUsedGlyphIds()
        {
            var ids = new int[Used.Count];
            Used.CopyTo(ids);
            Array.Sort(ids);
            return ids;
        }
    }

    /// <summary>Per-font emit state: the page resource name + the original→subset glyph map.</summary>
    private readonly record struct FontEmit(PdfName Name, IReadOnlyDictionary<int, int> OldToNew);

    /// <summary>One queued glyph-show: the font, the original glyph ids (mapped to subset ids
    /// at replay), the baseline origin + size in PDF points, the fill color, and the fragment's
    /// optional clip rect (page-pt, bottom-origin — replay groups consecutive same-clip commands
    /// under one <c>q … re W n</c> / <c>Q</c> pair).</summary>
    private readonly record struct DrawCommand(
        string FontKey,
        ushort[] OriginalGlyphIds,
        double XPt,
        double YPt,
        double SizePt,
        uint ColorArgb,
        (double X, double Y, double W, double H)? ClipPt = null);
}
