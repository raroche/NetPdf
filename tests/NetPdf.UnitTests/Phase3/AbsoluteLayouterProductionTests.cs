// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using NetPdf;
using NetPdf.Css.Cascade;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Inline;
using NetPdf.Layout.Layouters;
using NetPdf.Paginate;
using NetPdf.Paginate.Diagnostics;
using NetPdf.Text.Shaping;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>
/// Phase 3 Task 19 cycle 1 — production-pipeline tests for
/// <c>position: absolute</c>. Drives real HTML through
/// HTML → CssPreprocessor → CssParserAdapter → CascadeResolver →
/// BoxBuilder → BlockLayouter and asserts the emitted fragment
/// geometry. Cycle 1 anchors abspos boxes to the establishing block's
/// content box (= the fragmentainer content area / initial containing
/// block for the top-level case), explicit top/left/width/height only.
/// </summary>
public sealed class AbsoluteLayouterProductionTests
{
    [Fact]
    public async Task Cycle1_production_abspos_anchors_to_initial_containing_block()
    {
        // A position:absolute box as a direct child of <body> with no
        // positioned ancestor → anchored to the initial containing
        // block (= the fragmentainer content area). top:10 left:20
        // width:50 height:30 → fragment at (20, 10, 50, 30).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .abs {
                    position: absolute;
                    top: 10px; left: 20px;
                    width: 50px; height: 30px;
                }
            </style></head><body>
            <div class="abs"></div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var abs = FindByClass(sink, "abs");
        Assert.NotNull(abs);
        Assert.Equal(20.0, abs!.Value.InlineOffset, precision: 3);
        Assert.Equal(10.0, abs.Value.BlockOffset, precision: 3);
        Assert.Equal(50.0, abs.Value.InlineSize, precision: 3);
        Assert.Equal(30.0, abs.Value.BlockSize, precision: 3);
    }

    [Fact]
    public async Task Cycle1_production_abspos_removed_from_flow()
    {
        // An abspos box between two in-flow blocks must not displace
        // them. flow1 (height 100) at block 0; flow2 (height 100) at
        // block 100 — the abspos box in between is out-of-flow.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flow1 { height: 100px; }
                .flow2 { height: 100px; }
                .abs {
                    position: absolute;
                    top: 300px; left: 0px;
                    width: 40px; height: 40px;
                }
            </style></head><body>
            <div class="flow1"></div>
            <div class="abs"></div>
            <div class="flow2"></div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var flow1 = FindByClass(sink, "flow1");
        var flow2 = FindByClass(sink, "flow2");
        var abs = FindByClass(sink, "abs");
        Assert.NotNull(flow1);
        Assert.NotNull(flow2);
        Assert.NotNull(abs);
        // In-flow blocks stack contiguously; the abspos box doesn't
        // push flow2 down.
        Assert.Equal(0.0, flow1!.Value.BlockOffset, precision: 3);
        Assert.Equal(100.0, flow2!.Value.BlockOffset, precision: 3);
        // abspos at its anchored block offset.
        Assert.Equal(300.0, abs!.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public async Task Cycle1_production_abspos_auto_offset_dropped_with_diagnostic()
    {
        // No top/left set (= auto) → cycle-1 deferral → box dropped +
        // LAYOUT-ABSOLUTE-FEATURE-UNSUPPORTED-001.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .abs {
                    position: absolute;
                    width: 50px; height: 30px;
                }
            </style></head><body>
            <div class="abs"></div>
            </body></html>
            """;

        var (sink, diag) = await RenderAsync(html);
        Assert.Null(FindByClass(sink, "abs"));
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutAbsoluteFeatureUnsupported001);
    }

    // ================================================================
    //  Pipeline driver — mirrors GridLayouterProductionTests.
    // ================================================================

    private static async Task<(RecordingFragmentSink sink, RecordingDiagnosticsSink diag)>
        RenderAsync(string html)
    {
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());
        var sheets = AdaptAllSheetsViaPreprocessor(document);
        var cascade = CascadeResolver.Resolve(document, sheets, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, document);
        var box = BoxBuilder.Build(document, resolved);

        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();
        using var layouter = new BlockLayouter(
            rootBox: box,
            sink: sink,
            incomingContinuation: null,
            diagnostics: diagSink,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);
        return (sink, diagSink);
    }

    private static BoxFragment? FindByClass(RecordingFragmentSink sink, string className)
    {
        foreach (var f in sink.Fragments)
        {
            var el = f.Box.SourceElement;
            if (el is null) continue;
            var classAttr = el.GetAttribute("class");
            if (classAttr is null) continue;
            if (classAttr.Split(' ').Any(t => t == className)) return f;
        }
        return null;
    }

    private static ImmutableArray<CssStylesheet> AdaptAllSheetsViaPreprocessor(IDocument document)
    {
        var output = ImmutableArray.CreateBuilder<CssStylesheet>();
        var order = 0;
        var styleElements = document.QuerySelectorAll("style");
        var styleIdx = 0;
        foreach (var rawSheet in document.StyleSheets.OfType<ICssStyleSheet>())
        {
            string rawText;
            if (styleIdx < styleElements.Length)
            {
                rawText = styleElements[styleIdx].TextContent ?? string.Empty;
                styleIdx++;
            }
            else
            {
                rawText = string.Empty;
            }
            var preprocess = string.IsNullOrEmpty(rawText)
                ? CssPreprocessResult.Empty
                : CssPreprocessor.Process(rawText);
            output.Add(CssParserAdapter.Adapt(
                rawSheet, preprocess,
                href: null,
                origin: CssStylesheetOrigin.Author,
                ownerKind: CssStylesheetOwnerKind.StyleElement,
                mediaQuery: null,
                isDisabled: false,
                order: order++));
        }
        return output.ToImmutable();
    }

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

    private sealed class RecordingDiagnosticsSink : IPaginateDiagnosticsSink
    {
        public List<PaginateDiagnostic> Diagnostics { get; } = new();
        public void Emit(PaginateDiagnostic diagnostic) => Diagnostics.Add(diagnostic);
    }

    private sealed class SyntheticShaperResolver : IShaperResolver, System.IDisposable
    {
        private readonly HbShaper _shaper = new(SyntheticFont.Build(), fontSizePx: 12);
        public HbShaper Resolve(ComputedStyle style) => _shaper;
        public void Dispose() => _shaper.Dispose();
    }
}
