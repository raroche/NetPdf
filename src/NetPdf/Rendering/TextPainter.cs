// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
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
        // Single-page convenience over the multi-page session (byte-identical to the pre-dedup path: a
        // session with one page subsets that page's glyphs exactly as before). The multi-page driver uses
        // the session directly to dedup a shared font across pages.
        ArgumentNullException.ThrowIfNull(page);
        var session = new TextPaintSession(shaper, pageHeightPt, contentOriginLeftPx, contentOriginTopPx, diagnostics);
        session.CollectPage(page, fragments);
        session.Finish(document);
    }

    /// <summary>The font built ONCE for the whole document (font-dedup-across-pages cycle): the registered
    /// indirect ref (shared by every page that uses the font — <see cref="PdfDocument.RegisterFont"/> already
    /// dedups identical embedded subsets, so subsetting the UNION of glyphs once yields ONE font object) plus
    /// the original→subset glyph map.</summary>
    private readonly record struct BuiltFont(PdfIndirectRef FontRef, IReadOnlyDictionary<int, int> OldToNew);

    /// <summary>
    /// A document-scoped text-paint pass (font-dedup-across-pages cycle). Collecting EVERY page's glyphs
    /// before building lets each font be subset + embedded ONCE from the UNION of glyphs used across all
    /// pages — so a font shared by N pages is embedded once (via <see cref="PdfDocument.RegisterFont"/>'s
    /// content dedup) instead of N times, and HarfBuzz shapes each run once (not once per page). Per page:
    /// <see cref="CollectPage(PdfPage, IReadOnlyList{BoxFragment})"/> records its draw commands +
    /// accumulates its glyphs; then <see cref="Finish"/> builds the fonts and replays every page's draws.
    /// </summary>
    internal sealed class TextPaintSession(
        HarfBuzzShaperResolver shaper,
        double pageHeightPt,
        double contentOriginLeftPx,
        double contentOriginTopPx,
        IDiagnosticsSink? diagnostics)
    {
        private readonly Dictionary<string, FontCollect> _collects = new(StringComparer.Ordinal);
        private readonly List<string> _fontOrder = [];               // first-seen (across pages) → deterministic build order.
        private readonly HashSet<string> _failed = new(StringComparer.Ordinal);    // font keys that couldn't resolve/parse/build.
        private readonly HashSet<string> _diagnosed = new(StringComparer.Ordinal); // dedup of emitted skip messages.
        private readonly List<(PdfPage Page, List<DrawCommand> Draws)> _pages = [];

        /// <summary>Pass 1 for one page: collect its used glyphs into the SHARED per-font sets (so the
        /// subset is built from the cross-page union) and record its draw commands for replay in
        /// <see cref="Finish"/>. The page is stored so its text can be emitted after all fonts are built.</summary>
        public void CollectPage(PdfPage page, IReadOnlyList<BoxFragment> fragments)
            => CollectPage(page, fragments, pageHeightPt, contentOriginLeftPx, contentOriginTopPx);

        /// <summary>As <see cref="CollectPage(PdfPage, IReadOnlyList{BoxFragment})"/> but with PER-PAGE
        /// geometry (per-page-geometry cycle): a page whose <c>@page :left</c>/<c>:right</c>/named size or
        /// margins differ from the document default uses its own page height (for the y-flip) + content
        /// origin so its text lands correctly on its own MediaBox. Passing the session defaults reproduces
        /// the base overload exactly (byte-identical when no per-page geometry varies).</summary>
        public void CollectPage(
            PdfPage page, IReadOnlyList<BoxFragment> fragments,
            double pageHeightPtForPage, double originLeftPx, double originTopPx)
        {
            ArgumentNullException.ThrowIfNull(page);
            ArgumentNullException.ThrowIfNull(fragments);
            ArgumentNullException.ThrowIfNull(shaper);
            var draws = new List<DrawCommand>();
            for (var i = 0; i < fragments.Count; i++)
            {
                var fragment = fragments[i];
                if (fragment.InlineLayout is not { } inline) continue;
                if (inline.Lines.Length == 0) continue;
                var blockStyle = fragment.Box.Style;
                if (blockStyle is null) continue;

                CollectFragment(
                    fragment, inline, blockStyle,
                    originLeftPx, originTopPx, pageHeightPtForPage,
                    shaper, _collects, _fontOrder, _failed, _diagnosed, draws, diagnostics);
            }
            // Skip a page with no draws (a background/image-only, transparent-text, or font-size:0 page) —
            // it has no text to replay, so storing it would just no-op in Finish (Copilot — avoid retaining
            // the page reference + the empty list). The page itself is unaffected (its non-text content was
            // already painted), so the page count + output are unchanged.
            if (draws.Count > 0) _pages.Add((page, draws));
        }

        /// <summary>Pass 2: build (subset + embed + register) each font ONCE from the union of glyphs
        /// collected across all pages, then replay each page's draw commands mapping original → subset
        /// glyph ids. Honors <paramref name="cancellationToken"/> before each font build + each page replay
        /// (post-PR-#179 review P2 — this pass parses/subsets/embeds fonts + replays all pages' text after
        /// layout, so a caller timeout / cancellation must still abort it).</summary>
        public void Finish(PdfDocument document, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(document);

            // ---- Pass 2a: build each font ONCE, in deterministic (first-seen-across-pages) order ----
            var built = new Dictionary<string, BuiltFont>(StringComparer.Ordinal);
            foreach (var key in _fontOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();   // before each (potentially large) font build
                var fc = _collects[key];
                try
                {
                    var plan = GlyphSubsetPlan.Build(fc.Font, fc.SortedUsedGlyphIds());
                    var subset = TtfSubsetter.Subset(fc.Font, plan);
                    var toUnicode = ToUnicodeCMap.FromSubset(fc.Font, plan);
                    var embedded = EmbeddedTtfFont.Build(fc.Font, subset, toUnicode);
                    built[key] = new BuiltFont(document.RegisterFont(embedded), plan.OldToNew);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                              or ArgumentException or FormatException)
                {
                    // A resolved+validated font we still can't subset/embed (e.g. a CFF/OTF
                    // outline font TtfSubsetter doesn't handle yet). Skip its text, don't fail
                    // the whole render (CLAUDE.md #7 — surface, don't drop silently).
                    Diagnose(diagnostics, _diagnosed,
                        $"font program '{key}' could not be subset/embedded: {ex.Message}");
                }
            }

            // ---- Pass 2b: replay each page's draws ----
            foreach (var (page, draws) in _pages)
            {
                cancellationToken.ThrowIfCancellationRequested();   // before each page's text replay
                EmitPage(page, draws, built, cancellationToken);
            }
        }

        /// <summary>Replay one page's draw commands. Each font is added to THIS page's resource dictionary
        /// on first use (cached), referencing the one shared embedded-font object. A fragment's clip rect
        /// (margin-box overflow clip-path cycle) wraps its glyph runs in ONE <c>q … re W n … Q</c> pair:
        /// draws are in collect order, so a fragment's commands are contiguous and the clip opens on the
        /// first drawn command carrying it and closes when the clip changes (or at the end). The state is
        /// transitioned only for commands that actually draw — a skipped (failed-font) command must not open
        /// a clip. <see cref="PdfPage.ShowGlyphs"/> wraps each run in its own <c>q/Q</c>, so nesting inside
        /// the clip's <c>q/Q</c> is balanced.</summary>
        private static void EmitPage(
            PdfPage page, List<DrawCommand> draws, Dictionary<string, BuiltFont> built,
            CancellationToken cancellationToken)
        {
            if (draws.Count == 0) return;
            var pageNames = new Dictionary<string, PdfName>(StringComparer.Ordinal);   // AddFont once per font per page.
            (double X, double Y, double W, double H)? openClipPt = null;
            var drawn = 0;
            foreach (var cmd in draws)
            {
                if ((drawn++ & 0xFF) == 0) cancellationToken.ThrowIfCancellationRequested();   // every 256 runs
                if (!built.TryGetValue(cmd.FontKey, out var bf)) continue; // build failed → already diagnosed.
                if (!pageNames.TryGetValue(cmd.FontKey, out var name))
                    pageNames[cmd.FontKey] = name = page.AddFont(bf.FontRef);
                if (cmd.ClipPt != openClipPt)
                {
                    if (openClipPt is not null) page.RestoreGraphicsState();
                    openClipPt = cmd.ClipPt;
                    if (openClipPt is { } clip) page.BeginRectangleClip(clip.X, clip.Y, clip.W, clip.H);
                }
                var subsetIds = new ushort[cmd.OriginalGlyphIds.Length];
                for (var g = 0; g < subsetIds.Length; g++)
                    subsetIds[g] = (ushort)bf.OldToNew[cmd.OriginalGlyphIds[g]];

                FragmentPainter.ColorChannels(cmd.ColorArgb, out var r, out var g2, out var b);
                // Partial text alpha composites via ExtGState /ca, like fills (review P2) — fully
                // transparent runs were already dropped at collect time.
                var alpha = FragmentPainter.Alpha(cmd.ColorArgb) / 255.0;
                page.ShowGlyphs(name, cmd.SizePt, cmd.XPt, cmd.YPt, subsetIds, r, g2, b, alpha);
            }
            if (openClipPt is not null) page.RestoreGraphicsState();
        }
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

        // text-align / justify is distributed within the CONTENT box, not the border box: the glyph
        // origin already starts at contentLeftPx (border + padding added), so the available inline size
        // for alignment must subtract the SAME inline border + padding from the border-box InlineSize.
        // Otherwise center / right / justify treat the box's border+padding as extra free space AND
        // desync from the inline-atomic placement, which aligns against the CONTENT inline size
        // (post-PR-#192 Copilot review). With no border/padding (the common body block) it equals
        // InlineSize, byte-identical.
        var contentInlineSizePx = fragment.InlineSize
            - blockStyle.ReadLengthPxOrZero(PropertyId.BorderLeftWidth)
            - blockStyle.ReadLengthPxOrZero(PropertyId.PaddingLeft)
            - blockStyle.ReadLengthPxOrZero(PropertyId.BorderRightWidth)
            - blockStyle.ReadLengthPxOrZero(PropertyId.PaddingRight);

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

        // text-align: justify cycle — when the fragment justifies, reconstruct the shaping
        // CONCATENATED text (the source runs' preprocessed text in document order — the same
        // concatenation the line builder shaped, so a glyph's Cluster indexes straight into it).
        // The justify pass below reads it to identify word-separator spaces. Built once per fragment,
        // only when justifying (null otherwise → the byte-identical non-justify path).
        string? concatText = null;
        if (fragment.JustifyLines)
        {
            var ct = new System.Text.StringBuilder();
            foreach (var pr in preRuns) ct.Append(pr.Text);
            concatText = ct.ToString();
        }

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
            // The cumulative path keys on EITHER per-line array (post-PR-#163 review P3): the
            // margin-box producer sends both together, but the fragment contract exposes them
            // independently — an offsets-only fragment must not silently drop its gaps.
            var lineTopPx = contentTopPx
                + (fragment.PerLineHeightsPx is null && fragment.PerLineTopOffsetsPx is null
                    ? li * lineHeightPx : cumulativeTopPx);
            cumulativeTopPx += thisLineHeightPx;
            // Per-line inline alignment (wrapped-line content-alignment, Task 21; per-line factors —
            // segment-align cycle): shift each line by its own leftover × the line's align factor
            // (the fragment-wide factor unless per-line factors are present; 0 = start; default, so
            // non-margin fragments are unchanged). Clamped to ≥ 0 so a line wider than the content
            // box still starts at the left edge.
            var lineAlignFactor = fragment.PerLineAlignFactors is { } factors && li < factors.Count
                ? factors[li] : fragment.LineAlignFactor;
            // Per-line HORIZONTAL INSETS (hpadding cycle): the line starts at its left inset and
            // aligns within the inset-shrunk extent (a leaf block's own horizontal padding); null
            // arrays keep the pre-cycle arithmetic byte-identical.
            var insetLeftPx = fragment.PerLineInsetLeftPx is { } iLs && li < iLs.Count ? iLs[li] : 0.0;
            var insetRightPx = fragment.PerLineInsetRightPx is { } iRs && li < iRs.Count ? iRs[li] : 0.0;

            // Inline-block last-line-baseline cycle (CSS 2.2 §10.8.1): when a line carries a baseline-
            // aligned inline-block the layout supplies its baseline (§10.8.1 max-ascent line box) so the
            // line's TEXT sits on the SAME baseline as the box. A per-line NaN — and the null default —
            // leaves the per-slice real-metric baseline below, byte-identical.
            double? explicitBaselineTopPx =
                fragment.PerLineBaselineTopPx is { } bls && li < bls.Count && !double.IsNaN(bls[li])
                    ? lineTopPx + bls[li]
                    : null;

            // text-align: justify (CSS Text 3 §7.3) — the LAST line justifies only under justify-all
            // (JustifyLastLine); a non-last line justifies unless it's forced-break-terminated (an
            // internal <br> stays start-aligned even under justify-all — a documented approximation). The
            // LAST line of a block carries EndsWithMandatoryBreak (content end), so it's gated on
            // JustifyLastLine, NOT the break flag. `gapCount` is the interior word-separator-space count
            // (trailing spaces sort last → excluded; an inline ATOMIC is advance-only, not an opportunity —
            // EmitJustifiedLine just advances past it, and the layout shifts the atomic by the same gaps);
            // an overflowing line (free ≤ 0) isn't squeezed.
            var isLastLine = li == lines.Length - 1;
            var justifyExtraPerGapPx = 0.0;
            var justifyGapCount = 0;
            if (fragment.JustifyLines && concatText is not null
                && (isLastLine ? fragment.JustifyLastLine : !line.EndsWithMandatoryBreak)
                && (justifyGapCount = InlineJustify.InteriorGapCount(line, shapedRuns, concatText)) > 0)
            {
                var freePx = contentInlineSizePx - insetLeftPx - insetRightPx - line.TotalAdvance;
                if (freePx > 0) justifyExtraPerGapPx = freePx / justifyGapCount;
            }

            if (justifyExtraPerGapPx > 0.0)
            {
                EmitJustifiedLine(
                    line, shapedRuns, preRuns, blockStyle, concatText!, justifyGapCount, justifyExtraPerGapPx,
                    insetLeftPx, contentLeftPx, lineTopPx, thisLineHeightPx, explicitBaselineTopPx, pageHeightPt, clipPt,
                    shaper, collects, fontOrder, failed, diagnosed, diagnostics, draws);
                continue;
            }

            var xCursorPx = insetLeftPx + (lineAlignFactor != 0.0
                ? Math.Max(0.0, (contentInlineSizePx - insetLeftPx - insetRightPx - line.TotalAdvance) * lineAlignFactor)
                : 0.0);

            foreach (var slice in line.Slices)
            {
                // Advance the line cursor for every slice — even skipped ones — so the
                // remaining slices on the line stay horizontally positioned.
                var sliceStartXPx = xCursorPx;
                xCursorPx += slice.SliceAdvance;

                if (slice.GlyphLength <= 0) continue;
                var run = shapedRuns[slice.ShapedRunIndex];
                // Inline-atomic-boxes cycle — an inline `<img>` atomic's synthetic glyph only reserves
                // advance (already applied above); the box itself paints from its own emitted fragment
                // (ImagePainter), so emit no glyph here.
                if (run.Atomic is not null) continue;
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
                // drop to the baseline by the ascent. Metrics from the run's parsed font. When the line
                // carries a baseline-aligned inline-block, the layout pins the baseline instead (so text
                // and box share it — CSS 2.2 §10.8.1); otherwise the real-metric centred baseline holds.
                var unitsPerEm = fc.Font.Head.UnitsPerEm;
                var ascentPx = fc.Font.Hhea.Ascender * fontSizePx / unitsPerEm;
                var descentPx = fc.Font.Hhea.Descender * fontSizePx / unitsPerEm; // negative for Latin.
                var halfLeadingPx = (thisLineHeightPx - (ascentPx - descentPx)) / 2.0;
                var lineBaselineTopPx = explicitBaselineTopPx ?? lineTopPx + halfLeadingPx + ascentPx;
                // text vertical-align cycle (CSS 2.2 §10.8.1) — a run's own vertical-align positions its
                // glyph baseline. LINE-EDGE keywords (top / bottom / middle / text-top / text-bottom) align
                // the run to the line box / parent content-area (a non-baseline offset); the others
                // (baseline / sub / super / a <length> / <percentage>) RAISE off the line baseline. A
                // sub/super/numeric shift GROWS the line in layout (ComputeInlineAtomicLayout pins the
                // per-line baseline); a line-edge run ALSO grows the line now (PR 3 task 7 —
                // TextLineEdgeGrowth), so a tall top/bottom/middle/text-* run is positioned WITHIN the
                // grown line rather than overflowing it. baseline / plain text → byte-identical.
                var blockFontSizePx = blockStyle.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16);
                var baselineTopPx = InlineVerticalAlign.TextLineEdgeBaselineTopPx(
                        runStyle, blockStyle, lineTopPx, thisLineHeightPx, lineBaselineTopPx,
                        ascentPx, descentPx, blockFontSizePx)
                    ?? lineBaselineTopPx - InlineVerticalAlign.TextRaisePx(runStyle, blockStyle, fontSizePx);

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

    // text-align: justify — the line's interior word-separator-space count (InteriorGapCount) and the
    // space test (IsJustifySpace) live on the shared NetPdf.Layout.Inline.InlineJustify helper, so the
    // gaps the painter distributes here and the offset the layout gives an inline atomic on the SAME
    // line are computed from one source and can't disagree.

    // Text vertical-align raise is computed by the shared NetPdf.Layout.Inline.InlineVerticalAlign
    // helper (the layout line-box sizing uses the SAME helper, so the line it reserves and the baseline
    // the painter draws on can't disagree).

    /// <summary>text-align: justify cycle — paint one JUSTIFIED line. Walks the line's glyphs at an
    /// EXPANDING pen (advancing by each glyph's own <c>XAdvance</c> — the same /W the Tj uses — plus
    /// <paramref name="extraPerGapPx"/> after each of the first <paramref name="gapCount"/> word-
    /// separator spaces), splitting the run into per-word <see cref="DrawCommand"/> segments at those
    /// spaces. A font-fail / transparent / size-0 run still advances the pen + consumes its
    /// opportunities (so visible later words stay positioned) but paints nothing.</summary>
    private static void EmitJustifiedLine(
        LineFragment line, IReadOnlyList<ShapedRun> shapedRuns, IReadOnlyList<TextRun> preRuns,
        ComputedStyle blockStyle, string concatText, int gapCount, double extraPerGapPx,
        double insetLeftPx, double contentLeftPx, double lineTopPx, double thisLineHeightPx,
        double? explicitBaselineTopPx,
        double pageHeightPt, (double X, double Y, double W, double H)? clipPt,
        HarfBuzzShaperResolver shaper, Dictionary<string, FontCollect> collects, List<string> fontOrder,
        HashSet<string> failed, HashSet<string> diagnosed, IDiagnosticsSink? diagnostics,
        List<DrawCommand> draws)
    {
        var penXPx = insetLeftPx;         // painted x cursor (natural advances + gaps added so far).
        var opportunitiesUsed = 0;
        foreach (var slice in line.Slices)
        {
            if (slice.GlyphLength <= 0) continue;
            var run = shapedRuns[slice.ShapedRunIndex];
            if (run.Atomic is not null)
            {
                // An inline atomic paints from its OWN fragment (layout placed it, shifted by the same
                // justify gaps via InlineJustify); here just advance the pen so following words stay put.
                for (var g = 0; g < slice.GlyphLength; g++)
                    penXPx += run.Glyphs[slice.GlyphStart + g].XAdvance;
                continue;
            }
            var runStyle = preRuns[run.Source.SourceTextRunIndex].Style;
            var fontSizePx = runStyle.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16);
            if (!FragmentPainter.TryResolveColor(runStyle.Get(PropertyId.Color), DefaultColorArgb, out var argb))
                argb = DefaultColorArgb;

            // Resolve the run's font + baseline once; a failure leaves fc null (pen still advances).
            string? key = null;
            FontCollect? fc = null;
            var baselineTopPx = 0.0;
            if (fontSizePx > 0 && FragmentPainter.Alpha(argb) != 0
                && TryGetFontCollect(shaper, runStyle, collects, fontOrder, failed, diagnosed, diagnostics,
                    out var k, out var f))
            {
                key = k;
                fc = f;
                var unitsPerEm = f.Font.Head.UnitsPerEm;
                var ascentPx = f.Font.Hhea.Ascender * fontSizePx / unitsPerEm;
                var descentPx = f.Font.Hhea.Descender * fontSizePx / unitsPerEm;
                // Use the layout-PINNED per-line baseline when present (a baseline-aligned inline-block or a
                // max-ascent text-shift grew + pinned the line) so justified text sits on the SAME baseline
                // as the atomic placement — else the real-metric centred baseline (post-PR-#195 review P1).
                var lineBaselineTopPx = explicitBaselineTopPx
                    ?? lineTopPx + (thisLineHeightPx - (ascentPx - descentPx)) / 2.0 + ascentPx;
                // text vertical-align (CSS 2.2 §10.8.1) — a run on a justified line positions its glyph
                // baseline the same way: a line-edge keyword (top/bottom/middle/text-top/text-bottom) aligns
                // to the line box / parent content-area, the rest RAISE off the baseline (baseline → 0,
                // byte-identical).
                var blockFontSizePx = blockStyle.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16);
                baselineTopPx = InlineVerticalAlign.TextLineEdgeBaselineTopPx(
                        runStyle, blockStyle, lineTopPx, thisLineHeightPx, lineBaselineTopPx,
                        ascentPx, descentPx, blockFontSizePx)
                    ?? lineBaselineTopPx - InlineVerticalAlign.TextRaisePx(runStyle, blockStyle, fontSizePx);
            }

            var segStartG = 0;            // segment start glyph, relative to slice.GlyphStart.
            var segPenXPx = penXPx;       // painted x of the current segment's first glyph.
            for (var g = 0; g < slice.GlyphLength; g++)
            {
                var glyph = run.Glyphs[slice.GlyphStart + g];
                penXPx += glyph.XAdvance;
                if (opportunitiesUsed >= gapCount
                    || !InlineJustify.IsJustifySpace(concatText, glyph.Cluster))
                    continue;
                // Word boundary — flush [segStartG, g] (incl. the space) then open a gap after it.
                opportunitiesUsed++;
                if (fc is not null)
                    EmitGlyphSegment(run, slice.GlyphStart + segStartG, g - segStartG + 1, fc, key!,
                        fontSizePx, contentLeftPx + segPenXPx + run.Glyphs[slice.GlyphStart + segStartG].XOffset,
                        baselineTopPx, argb, pageHeightPt, clipPt, draws);
                penXPx += extraPerGapPx;
                segStartG = g + 1;
                segPenXPx = penXPx;
            }
            // Trailing segment after the slice's last opportunity (or the whole slice if none).
            if (fc is not null && segStartG < slice.GlyphLength)
                EmitGlyphSegment(run, slice.GlyphStart + segStartG, slice.GlyphLength - segStartG, fc, key!,
                    fontSizePx, contentLeftPx + segPenXPx + run.Glyphs[slice.GlyphStart + segStartG].XOffset,
                    baselineTopPx, argb, pageHeightPt, clipPt, draws);
        }
    }

    /// <summary>Emit one <see cref="DrawCommand"/> for a contiguous glyph segment
    /// <c>[glyphStart, glyphStart+glyphCount)</c> of <paramref name="run"/> at the (already
    /// content-relative) baseline origin <paramref name="xStartPx"/> / <paramref name="baselineTopPx"/>,
    /// seeding the font's used-glyph set. Shared by the justify pass's per-word segments.</summary>
    private static void EmitGlyphSegment(
        ShapedRun run, int glyphStart, int glyphCount, FontCollect fc, string fontKey,
        double fontSizePx, double xStartPx, double baselineTopPx, uint argb, double pageHeightPt,
        (double X, double Y, double W, double H)? clipPt, List<DrawCommand> draws)
    {
        var ids = new ushort[glyphCount];
        for (var i = 0; i < glyphCount; i++)
        {
            var gid = run.Glyphs[glyphStart + i].GlyphId;
            ids[i] = gid;
            fc.Used.Add(gid);
        }
        draws.Add(new DrawCommand(
            FontKey: fontKey,
            OriginalGlyphIds: ids,
            XPt: PdfUnits.PxToPt(xStartPx),
            YPt: pageHeightPt - PdfUnits.PxToPt(baselineTopPx),
            SizePt: PdfUnits.PxToPt(fontSizePx),
            ColorArgb: argb,
            ClipPt: clipPt));
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
