// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
using NetPdf.Paginate;
using NetPdf.Paginate.Diagnostics;
using NetPdf.Phase2;

namespace NetPdf.W3cConformance;

/// <summary>Per Phase 3 PR 1 — the curated W3C conformance suite drives the
/// INTERNAL layout pipeline (HTML → <see cref="Phase2Pipeline"/> box tree →
/// <see cref="BlockLayouter"/>) and asserts <see cref="BoxFragment"/> GEOMETRY
/// directly. This is the (A) approach from the harness investigation — the
/// public facade exposes only PDF bytes (no structured geometry) and the repo
/// has no PDF content-stream reader, so fragment-level assertions are both the
/// cleaner and the only viable path.
///
/// <para>Cases are authored against BLOCK-BOX geometry (sized elements, no text
/// metrics) so they're deterministic without a font dependency — the layouter
/// runs with <c>shaperResolver: null</c>. A text-bearing block would be SKIPPED
/// (it emits the no-shaper diagnostic), which is why the harness now CAPTURES
/// diagnostics and the runner fails any case that emits an unexpected one — a
/// silent layout omission (e.g. accidental text content) becomes a hard failure
/// instead of a misleading pass (PR 1 review [P2]).</para></summary>
internal static class ConformanceHarness
{
    /// <summary>A laid-out element's border-box rectangle in content-area-
    /// relative CSS px (horizontal-tb: <c>InlineOffset</c>→X, <c>BlockOffset</c>
    /// →Y, <c>InlineSize</c>→Width, <c>BlockSize</c>→Height), tagged with the
    /// 0-based page it landed on.</summary>
    public readonly record struct LaidOutBox(
        string Id, int Page, double X, double Y, double Width, double Height);

    /// <summary>The outcome of rendering a case: every laid-out <c>id</c>'d box,
    /// the diagnostics the pipeline emitted (so the runner can fail on an
    /// unexpected one — silent content omission), and whether the page loop hit
    /// its <c>maxPages</c> ceiling with layout still incomplete (an unconsumed
    /// continuation — a runaway / under-asserted case, never a real pass).</summary>
    internal readonly record struct RenderResult(
        IReadOnlyList<LaidOutBox> Boxes,
        IReadOnlyList<PaginateDiagnostic> Diagnostics,
        bool MaxPagesExhausted);

    /// <summary>Render <paramref name="html"/> across however many pages it
    /// needs (single page for most cases; multi-page for fragmentation) into a
    /// content area of <paramref name="contentWidthPx"/> ×
    /// <paramref name="contentHeightPx"/>, and return every laid-out box that
    /// carries an <c>id</c> attribute (tagged with its page + border-box
    /// geometry), plus the emitted diagnostics and the exhaustion flag.</summary>
    internal static RenderResult Render(
        string html, double contentWidthPx, double contentHeightPx, int maxPages = 32)
    {
        var options = new HtmlPdfOptions();
        using var phase2 = Phase2Pipeline.RunFromHtmlAsync(html, options)
            .GetAwaiter().GetResult();
        // Resolve em / vw / vh against the page box (mirrors the production
        // PdfRenderPipeline pre-layout pass); px-only cases are unaffected.
        DeferredLengthResolver.ResolveTreeInPlace(
            phase2.BoxRoot, contentWidthPx, contentHeightPx);

        var laidOut = new List<LaidOutBox>();
        var diagnostics = new RecordingDiagnosticsSink();
        LayoutContinuation? continuation = null;
        var completed = false;
        for (var page = 0; page < maxPages; page++)
        {
            var sink = new RecordingFragmentSink();
            using (var layouter = new BlockLayouter(
                rootBox: phase2.BoxRoot,
                sink: sink,
                incomingContinuation: continuation,
                diagnostics: diagnostics,
                shaperResolver: null))
            {
                var fragmentainer = new FragmentainerContext(contentWidthPx, contentHeightPx)
                {
                    PageIndex = page,
                };
                var layout = new LayoutContext(fragmentainer) { Diagnostics = diagnostics };
                using var breaks = new BreakResolver();
                // Drive each page through the production retry coordinator
                // (Strict → DropAvoidInside → LastResort) so fragmentation cases
                // see the same break behavior the real pipeline produces.
                var coordinator = new LayoutRetryCoordinator(
                    diagnostics: diagnostics, fragmentSink: sink);
                var result = coordinator.Run(layouter, fragmentainer, ref layout, breaks);

                foreach (var f in sink.Fragments)
                {
                    var id = f.Box.SourceElement?.Id;
                    if (string.IsNullOrEmpty(id)) continue;
                    laidOut.Add(new LaidOutBox(
                        id!, page, f.InlineOffset, f.BlockOffset, f.InlineSize, f.BlockSize));
                }

                if (result.Outcome != LayoutAttemptOutcome.PageComplete
                    || result.Continuation is null)
                {
                    // AllDone, or a final page with nothing left to continue.
                    completed = true;
                    break;
                }
                continuation = result.Continuation;
            }
        }
        return new RenderResult(laidOut, diagnostics.Diagnostics, MaxPagesExhausted: !completed);
    }

    /// <summary>Recording fragment sink — captures every emitted
    /// <see cref="BoxFragment"/> + supports the rollback / wrapper-resize
    /// contract the layouter expects. Mirrors the canonical unit-test sink.</summary>
    private sealed class RecordingFragmentSink : IBlockFragmentSink
    {
        public List<BoxFragment> Fragments { get; } = new();
        public int Cursor => Fragments.Count;
        public void Emit(BoxFragment fragment) => Fragments.Add(fragment);

        public void RollbackTo(int cursor)
        {
            if (cursor < Fragments.Count)
            {
                Fragments.RemoveRange(cursor, Fragments.Count - cursor);
            }
        }

        public void UpdateFragmentBlockSize(int cursor, double newBlockSize)
        {
            if (cursor < 0 || cursor >= Fragments.Count) return;
            Fragments[cursor] = Fragments[cursor] with { BlockSize = newBlockSize };
        }
    }

    /// <summary>Recording diagnostics sink — captures every
    /// <see cref="PaginateDiagnostic"/> the layouter / retry coordinator emit so
    /// the runner can fail a case on an unexpected one. Mirrors the canonical
    /// unit-test sink.</summary>
    private sealed class RecordingDiagnosticsSink : IPaginateDiagnosticsSink
    {
        public List<PaginateDiagnostic> Diagnostics { get; } = new();
        public void Emit(PaginateDiagnostic diagnostic) => Diagnostics.Add(diagnostic);
    }
}
