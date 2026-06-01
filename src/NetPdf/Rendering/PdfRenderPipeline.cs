// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
/// <b>Cycle-2 scope.</b> Single-page output painting backgrounds + borders (no
/// text — that needs the CSS font-property resolvers, TODO 1 in
/// <c>deferrals.md#layout-to-pdf-pipeline</c>). The shaper is constructed
/// forward-compatibly so text lights up automatically once those land; for
/// text-free content it is never invoked, which also keeps output deterministic
/// (no system-font dependency until a bundled fallback font ships).
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
        var contentInlinePx = Math.Max(0, pageSize.WidthPx - margins.LeftPx - margins.RightPx);
        var contentBlockPx = Math.Max(0, pageSize.HeightPx - margins.TopPx - margins.BottomPx);

        var sink = new ListFragmentSink();
        using (var shaper = new HarfBuzzShaperResolver(options.FontResolver ?? new SystemFontResolver()))
        using (var layouter = new BlockLayouter(
            rootBox: phase2.BoxRoot,
            sink: sink,
            incomingContinuation: null,
            diagnostics: new PaginateToPublicDiagnosticsAdapter(diagnostics),
            shaperResolver: shaper))
        {
            var fragmentainer = new FragmentainerContext(contentInlinePx, contentBlockPx);
            var layout = new LayoutContext(fragmentainer);
            using var breaks = new BreakResolver();

            var result = layouter.AttemptLayout(
                fragmentainer, ref layout, breaks, LayoutAttemptStrategy.LastResort, cancellationToken);

            if (result.Outcome == LayoutAttemptOutcome.PageComplete && result.Continuation is not null)
            {
                diagnostics.Emit(new Diagnostic(
                    DiagnosticCodes.PdfContentOverflowTruncated001,
                    "Document content exceeds a single page; only the first page is emitted in this " +
                    "build. Multi-page output is a tracked follow-up (deferrals.md#layout-to-pdf-pipeline).",
                    DiagnosticSeverity.Warning));
            }
        }

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
            sink.Fragments, page, mediaBox.HeightPts, margins.LeftPx, margins.TopPx);

        var bytes = document.Save();
        return new RenderOutcome(bytes, document.Pages.Count, diagnostics.Items);
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
