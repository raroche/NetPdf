// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using NetPdf.Css.PagedMedia;
using NetPdf.Diagnostics;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
using NetPdf.Paginate;
using NetPdf.Pdf;
using NetPdf.Phase2;
using NetPdf.Shaping;

namespace NetPdf.Rendering;

/// <summary>
/// The internal HTML → PDF rendering engine the public <see cref="HtmlPdf"/>
/// facade drives. Composes the existing <see cref="Phase2Pipeline"/> (parse →
/// cascade → box tree) with fragmentainer-aware layout
/// (<see cref="BlockLayouter"/>), the <see cref="FragmentPainter"/> paint bridge,
/// and the <see cref="PdfDocument"/> byte writer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Single-page output painting <c>background-color</c> fills +
/// <c>border-*</c> edges (<see cref="FragmentPainter"/>) and shaped text glyphs
/// (<see cref="TextPainter"/>, cycle 5a-2-ii). Text subsets + embeds the exact font
/// program layout shaped — the shaper is kept alive past paint so glyph ids match.
/// Text-free content stays byte-deterministic; the default <see cref="SystemFontResolver"/>
/// text path is not deterministic-for-text until a bundled fallback font ships (TODO in
/// <c>deferrals.md#layout-to-pdf-pipeline</c>), but a fixed <c>IFontResolver</c> is fully
/// reproducible.
/// </para>
/// <para>
/// <b>Single page.</b> The multi-page driver (looping <see cref="BlockLayouter.AttemptLayout"/>
/// over continuations, a page per fragmentainer) is TODO 3. Until then, content
/// that overflows the first page is reported via
/// <c>PDF-CONTENT-OVERFLOW-TRUNCATED-001</c> rather than dropped silently.
/// </para>
/// </remarks>
internal static class PdfRenderPipeline
{
    /// <summary>The product of one conversion: the PDF bytes, the page count,
    /// and every diagnostic emitted across all stages.</summary>
    internal readonly record struct RenderOutcome(
        byte[] Pdf,
        int PageCount,
        IReadOnlyList<Diagnostic> Diagnostics);

    public static async Task<RenderOutcome> RenderAsync(
        string html,
        HtmlPdfOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(options);

        // One hub collects every stage's diagnostics for ConvertDetailed while
        // forwarding each live to the caller's sink (if any).
        var diagnostics = new CollectingDiagnosticsSink(options.Diagnostics);

        using var phase2 = await Phase2Pipeline.RunFromHtmlAsync(
            html, options, new PublicDiagnosticsSinkAdapter(diagnostics), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // Page geometry: layout works in CSS px on the content area (page minus
        // margins); the px → pt scale + y-flip happen in the painter / emit step.
        var pageSize = options.PageSize;
        var margins = options.Margins;

        // CSS Paged Media (Phase 3 Task 21). Applicability is filtered with the SAME media
        // context the cascade used (PDF output is print), so a `media="screen"` sheet or
        // `@media screen` block never affects the page. Selectors (`:first`/`:left`/`:right`)
        // + page-margin boxes are later cycles.
        var media = Phase2Pipeline.BuildMediaContext(options);

        // `@page { size }` (cycle 2) overrides the page size when PreferCssPageSize (default on).
        if (options.PreferCssPageSize
            && AtPageSizeResolver.Resolve(phase2.Sheets, media) is { } cssSize)
        {
            pageSize = new PageSize(cssSize.WidthPx, cssSize.HeightPx);
        }

        // `@page { margin… }` (cycle 1) overrides the option's page margins, per side. Percentage
        // margins resolve against the RESOLVED page box — `pageSize` already reflects any
        // `@page { size }` override above — so `@page { size: A5; margin: 10% }` computes its
        // percentages against A5, not the configured page size (CSS Page 3). Media applicability
        // still uses the original context.
        var pageMargins = AtPageMarginResolver.Resolve(
            phase2.Sheets, media, pageSize.WidthPx, pageSize.HeightPx);
        if (pageMargins.HasAny)
        {
            margins = new PageMargins(
                TopPx: pageMargins.TopPx ?? margins.TopPx,
                RightPx: pageMargins.RightPx ?? margins.RightPx,
                BottomPx: pageMargins.BottomPx ?? margins.BottomPx,
                LeftPx: pageMargins.LeftPx ?? margins.LeftPx);
        }

        var contentInlinePx = Math.Max(0, pageSize.WidthPx - margins.LeftPx - margins.RightPx);
        var contentBlockPx = Math.Max(0, pageSize.HeightPx - margins.TopPx - margins.BottomPx);

        var sink = new ListFragmentSink();
        var overflowReported = false;
        // Keep the shaper alive PAST paint (method-scoped using): the TextPainter subsets the
        // SAME font program layout shaped, so the shaped glyph ids index the same font. The
        // layouter + break resolver are layout-only and stay in their own scope below,
        // disposed as soon as layout finishes.
        using var shaper = new HarfBuzzShaperResolver(options.FontResolver ?? new SystemFontResolver());
        var fontResolutionFailed = false;
        try
        {
            using var layouter = new BlockLayouter(
                rootBox: phase2.BoxRoot,
                sink: sink,
                incomingContinuation: null,
                diagnostics: new PaginateToPublicDiagnosticsAdapter(diagnostics),
                shaperResolver: shaper);
            var fragmentainer = new FragmentainerContext(contentInlinePx, contentBlockPx);
            var layout = new LayoutContext(fragmentainer);
            using var breaks = new BreakResolver();

            var result = layouter.AttemptLayout(
                fragmentainer, ref layout, breaks, LayoutAttemptStrategy.LastResort, cancellationToken);

            if (result.Outcome == LayoutAttemptOutcome.PageComplete && result.Continuation is not null)
            {
                EmitOverflowTruncated(diagnostics);
                overflowReported = true;
            }
        }
        catch (FontResolutionException ex)
        {
            // Text shaping runs DURING layout, so a font that can't be resolved (no matching
            // face) or whose bytes are unsafe / WOFF-wrapped surfaces here — not at paint time
            // (the async-resolver case is a separate NotSupportedException already handled at the
            // inline-layout seam). Degrade to a valid PDF — painting whatever fragments laid out
            // before the failure — plus a diagnostic, rather than failing the whole conversion
            // (CLAUDE.md #7, post-PR-#127 review P1). Per-block continuation past the failing run
            // is a tracked follow-up. A bundled fallback font (cycle 5b) makes the default path
            // never reach this.
            EmitTextFontUnresolved(diagnostics, ex.Message);
            fontResolutionFailed = true;
        }

        // Overflow that didn't surface as a page break — a fragment wider/taller than the
        // content box, a forced-overflow AllDone, or a negative/absolute offset — would paint
        // outside the MediaBox and be clipped silently. Surface it with the same diagnostic
        // (PR #118 review P2).
        if (!fontResolutionFailed && !overflowReported
            && FragmentsOverflowContentRect(sink.Fragments, contentInlinePx, contentBlockPx))
            EmitOverflowTruncated(diagnostics);

        cancellationToken.ThrowIfCancellationRequested();

        // Emit: page size (CSS px) → /MediaBox (PDF pt). The painter reads the
        // box-tree styles, so it must run before phase2 is disposed.
        var document = new PdfDocument(PdfVersionString(options.EmittedPdfVersion));
        if (!string.IsNullOrEmpty(options.Title)) document.Title = options.Title;
        if (!string.IsNullOrEmpty(options.Author)) document.Author = options.Author;

        var mediaBox = new MediaBoxSize(
            PdfUnits.PxToPt(pageSize.WidthPx),
            PdfUnits.PxToPt(pageSize.HeightPx));
        var page = document.AddPage(mediaBox);

        FragmentPainter.PaintFragments(
            sink.Fragments, page, mediaBox.HeightPts, margins.LeftPx, margins.TopPx,
            paintBackgrounds: options.PrintBackgrounds, diagnostics);

        // Page margin boxes (running headers/footers, Task 21 cycle 3): resolved from the same
        // bare @page rules, laid out into fragments in the page margins. They paint through the
        // SAME TextPainter pass as the body (post-PR-#132 review P3) so a font shared by body +
        // header/footer is subset + embedded ONCE, not per-pass. Their fragments are offset
        // relative to the body's content origin (the margins), exactly like body fragments.
        // Literal + attr() content only this cycle; needs a host element (for attr()) —
        // element-free documents skip them.
        var textFragments = sink.Fragments;
        var marginBoxes = AtPageMarginBoxResolver.Resolve(phase2.Sheets, media);
        if (!marginBoxes.IsDefaultOrEmpty
            && FindRootElementBox(phase2.BoxRoot) is { SourceElement: { } host } rootEl)
        {
            // The root element box gives both the attr() host + the top of the inheritance chain;
            // the @page rules' own declarations form the page context the boxes inherit from.
            var pageDecls = AtPageMarginBoxResolver.PageContextDeclarations(phase2.Sheets, media);
            // Page counters for counter(page)/counter(pages) (cycle 9). Single page → (1, 1); the
            // blocked multi-page driver will supply the real per-page number + total once it lands.
            var pageCounters = new CssContentList.PageCounters(Page: 1, Pages: 1);
            var marginResult = PageMarginBoxPainter.Layout(
                marginBoxes, pageSize.WidthPx, pageSize.HeightPx,
                margins.TopPx, margins.RightPx, margins.BottomPx, margins.LeftPx,
                margins.LeftPx, margins.TopPx, host, rootEl.Style, pageDecls, pageCounters, shaper, diagnostics);
            // Margin-box background bands (cycle 8) paint BEHIND the header/footer text: after the
            // body backgrounds above, before the shared text pass — same bg → text content order.
            // Gated by PrintBackgrounds, exactly like body backgrounds (post-PR-#137 review P1).
            if (options.PrintBackgrounds && marginResult.Backgrounds.Count > 0)
                PageMarginBoxPainter.PaintBackgrounds(page, marginResult.Backgrounds, mediaBox.HeightPts);
            if (marginResult.Fragments.Count > 0)
            {
                var combined = new List<BoxFragment>(sink.Fragments.Count + marginResult.Fragments.Count);
                combined.AddRange(sink.Fragments);
                combined.AddRange(marginResult.Fragments);
                textFragments = combined;
            }
        }

        // Text paints OVER backgrounds + borders, ONE pass over body + margin-box fragments. The
        // shaper (kept alive above) lets the painter subset + embed the exact font program layout
        // shaped, then emit glyph runs at their baselines. With the default SystemFontResolver this
        // is not yet deterministic-for-text (a bundled fallback font is the next cycle); a fixed
        // IFontResolver makes it fully reproducible.
        TextPainter.PaintText(
            textFragments, page, document, shaper, mediaBox.HeightPts,
            margins.LeftPx, margins.TopPx, diagnostics);

        var bytes = document.Save();
        return new RenderOutcome(bytes, document.Pages.Count, diagnostics.Items);
    }

    /// <summary>The first DOM-element-backed box in the tree (depth-first) — the document root
    /// element's box. Its <see cref="Box.SourceElement"/> is the <c>attr()</c> host for page
    /// margin-box content, and its <see cref="Box.Style"/> is the top of the margin-box style
    /// inheritance chain (root → page context → margin box). <see langword="null"/> only for an
    /// element-free document (then margin boxes are skipped).</summary>
    private static Box? FindRootElementBox(Box? box)
    {
        if (box is null) return null;
        if (box.SourceElement is not null) return box;
        foreach (var child in box.Children)
            if (FindRootElementBox(child) is { } found) return found;
        return null;
    }

    private static void EmitTextFontUnresolved(IDiagnosticsSink diagnostics, string detail) =>
        diagnostics.Emit(new Diagnostic(
            DiagnosticCodes.PaintTextFontUnresolved001,
            "A font could not be resolved during layout, so the affected text was not painted: " +
            detail + " The rest of the document still renders. A bundled deterministic last-resort " +
            "font (so the default path always resolves) is a tracked follow-up " +
            "(deferrals.md#layout-to-pdf-pipeline).",
            DiagnosticSeverity.Warning));

    private static void EmitOverflowTruncated(IDiagnosticsSink diagnostics) =>
        diagnostics.Emit(new Diagnostic(
            DiagnosticCodes.PdfContentOverflowTruncated001,
            "Document content does not fit the single page the cycle-2 renderer emits — it " +
            "overflows the page content area (block and/or inline), and content beyond the " +
            "first page / content box is not rendered. Multi-page output + overflow handling " +
            "are tracked follow-ups (deferrals.md#layout-to-pdf-pipeline).",
            DiagnosticSeverity.Warning));

    // A fragment whose border box leaves the content rect [0,contentInline]×[0,contentBlock]
    // (CSS px, content-area-relative) paints into the margins / off-page and may be clipped.
    private static bool FragmentsOverflowContentRect(
        IReadOnlyList<BoxFragment> fragments, double contentInlinePx, double contentBlockPx)
    {
        const double eps = 0.5; // px — edge-touching (e.g. a full-width block) is not overflow.
        for (var i = 0; i < fragments.Count; i++)
        {
            var f = fragments[i];
            if (f.InlineSize <= 0 || f.BlockSize <= 0) continue;
            if (f.InlineOffset < -eps || f.BlockOffset < -eps
                || f.InlineOffset + f.InlineSize > contentInlinePx + eps
                || f.BlockOffset + f.BlockSize > contentBlockPx + eps)
                return true;
        }
        return false;
    }

    private static string PdfVersionString(PdfVersion version) => version switch
    {
        PdfVersion.V1_4 => "1.4",
        PdfVersion.V1_5 => "1.5",
        PdfVersion.V1_6 => "1.6",
        PdfVersion.V2_0 => "2.0",
        _ => "1.7",
    };
}
