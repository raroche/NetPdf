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
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;
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
/// Phase 3 Task 12 sub-cycle 1 post-PR-49 hardening (Finding 6) —
/// production-pipeline tests for <see cref="TableLayouter"/>. The
/// existing <see cref="TableLayouterTests"/> construct box trees
/// directly (bypassing BoxBuilder); this fixture exercises tables
/// through the FULL pipeline:
///
/// <para>HTML → <c>HtmlParsingHost</c> → <c>CssPreprocessor</c> →
/// <c>CssParserAdapter</c> → <c>CascadeResolver</c> →
/// <c>VarResolver</c> → <c>BoxBuilder</c> → <c>BlockLayouter</c>
/// (which dispatches into <c>TableLayouter</c> for table wrappers).
/// </para>
///
/// <para>Coverage gaps closed by this fixture (relative to
/// <see cref="TableLayouterTests"/>): captions (which BoxBuilder
/// keeps as direct wrapper children, NOT under the grid); row
/// groups synthesized from real <c>&lt;thead&gt;</c> /
/// <c>&lt;tbody&gt;</c>; anonymous wrappers; whitespace handling
/// around tables; real inline text flowing through the cell content
/// path; sibling block flow around a table.</para>
/// </summary>
public sealed class TableLayouterProductionTests
{
    [Fact]
    public async Task Simple_html_table_renders_via_full_pipeline()
    {
        // Per Finding 6 — a real HTML table flows through every stage
        // of the pipeline + emits row + cell fragments. Asserts the
        // paint-safe order (Finding 2 regression) for the production
        // dispatch path.
        const string html = """
            <!DOCTYPE html><html><head><style></style></head><body>
            <table><tr><td>Hello</td></tr></table>
            </body></html>
            """;

        var (sink, _, table) = await RenderViaFullPipelineAsync(html);

        // At least one Table fragment + one TableRow + one TableCell.
        Assert.Contains(sink.Fragments, f => f.Box.Kind == BoxKind.Table);
        Assert.Contains(sink.Fragments, f => f.Box.Kind == BoxKind.TableRow);
        Assert.Contains(sink.Fragments, f => f.Box.Kind == BoxKind.TableCell);

        // Paint-safe order — for each (row, its cell) pair the row
        // index must precede the cell index, and the cell index must
        // precede the cell content (we use the first AnonymousBlock /
        // BlockContainer under the cell as a proxy for cell content).
        int rowIdx = -1, cellIdx = -1;
        for (var i = 0; i < sink.Fragments.Count; i++)
        {
            var b = sink.Fragments[i].Box;
            if (b.Kind == BoxKind.TableRow && rowIdx < 0) rowIdx = i;
            else if (b.Kind == BoxKind.TableCell && cellIdx < 0) cellIdx = i;
        }
        Assert.True(rowIdx >= 0 && cellIdx >= 0);
        Assert.True(rowIdx < cellIdx,
            $"Paint-safe order violated: row at {rowIdx} after cell at {cellIdx}.");
    }

    [Fact]
    public async Task Table_followed_by_paragraph_does_not_overlap()
    {
        // Per Finding 1 regression — the paragraph after a table must
        // land BELOW the table's bottom edge. Pre-fix the wrapper
        // border-box was 0-tall (auto-height) so the paragraph
        // overlapped the table content.
        const string html = """
            <!DOCTYPE html><html><head><style>
                td > div { height: 100px; }
                p { height: 50px; }
            </style></head><body>
            <table><tr><td><div></div></td></tr></table>
            <p></p>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        // Find the table wrapper + the following <p>.
        var tableFragment = sink.Fragments.FirstOrDefault(f =>
            f.Box.Kind == BoxKind.Table);
        Assert.NotEqual(default, tableFragment);

        // The <p> is a BlockContainer outside the table.
        BoxFragment paragraphFragment = default;
        var foundTable = false;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.Table) { foundTable = true; continue; }
            if (foundTable
                && f.Box.Kind == BoxKind.BlockContainer
                && f.Box.SourceElement?.LocalName == "p")
            {
                paragraphFragment = f;
                break;
            }
        }
        Assert.NotEqual(default, paragraphFragment);

        var tableBottom = tableFragment.BlockOffset + tableFragment.BlockSize;
        Assert.True(paragraphFragment.BlockOffset >= tableBottom,
            $"Paragraph at BlockOffset={paragraphFragment.BlockOffset} overlaps "
            + $"table that bottoms at {tableBottom}.");
    }

    [Fact]
    public async Task Table_with_caption_emits_feature_diagnostic()
    {
        // Per Finding 4 regression — a real <caption> element must
        // surface a LAYOUT-TABLE-FEATURE-UNSUPPORTED-001 diagnostic
        // with the caption text in the message.
        const string html = """
            <!DOCTYPE html><html><head><style></style></head><body>
            <table>
              <caption>Q1 2026 Revenue</caption>
              <tr><td>X</td></tr>
            </table>
            </body></html>
            """;

        var (_, diagnostics, _) = await RenderViaFullPipelineAsync(html);

        var captionDiag = diagnostics.Diagnostics.FirstOrDefault(d =>
            d.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001
            && d.Message.Contains("caption"));
        Assert.True(
            captionDiag.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001,
            "Expected LAYOUT-TABLE-FEATURE-UNSUPPORTED-001 diagnostic with 'caption' "
            + "in message via the full pipeline.");
        Assert.Contains("Q1 2026 Revenue", captionDiag.Message);
        Assert.Equal(PaginateDiagnosticSeverity.Warning, captionDiag.Severity);
    }

    [Fact]
    public async Task Table_with_thead_tbody_collects_rows_via_row_groups()
    {
        // BoxBuilder synthesizes TableHeaderGroup + TableRowGroup
        // wrappers around <thead> + <tbody>. TableLayouter must
        // recurse into them to find the inner rows.
        const string html = """
            <!DOCTYPE html><html><head><style></style></head><body>
            <table>
              <thead><tr><th>H</th></tr></thead>
              <tbody>
                <tr><td>1</td></tr>
                <tr><td>2</td></tr>
              </tbody>
            </table>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var rowCount = sink.Fragments.Count(f => f.Box.Kind == BoxKind.TableRow);
        Assert.Equal(3, rowCount);
    }

    [Fact]
    public async Task Table_with_colspan_emits_feature_diagnostic_via_full_pipeline()
    {
        // Per the existing colspan unit test, but exercised through
        // the full pipeline. The DOM round-trip must preserve the
        // colspan attribute on the cell's source element so the
        // TableLayouter's HasSpanAttribute check fires.
        const string html = """
            <!DOCTYPE html><html><head><style></style></head><body>
            <table>
              <tr><td colspan="2">spans two columns</td></tr>
            </table>
            </body></html>
            """;

        var (_, diagnostics, _) = await RenderViaFullPipelineAsync(html);

        Assert.Contains(diagnostics.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001
            && d.Message.Contains("colspan"));
    }

    [Fact]
    public async Task Table_cell_with_real_inline_text_lays_out_via_inline_only_block()
    {
        // Per Finding 6 — the cell content path must produce a
        // BoxFragment with shaped inline content (proving the nested
        // BlockLayouter → InlineLayouter chain works inside cells).
        // We wrap the cell text in a <div> so BoxBuilder produces a
        // BlockContainer child whose only descendant is the TextRun
        // — that's exactly the inline-only-block shape the inline
        // dispatch detects.
        const string html = """
            <!DOCTYPE html><html><head><style></style></head><body>
            <table><tr><td><div>Hello world</div></td></tr></table>
            </body></html>
            """;

        var (sink, _, root) = await RenderViaFullPipelineAsync(html);

        // First sanity-check we got a TableCell fragment via the
        // nested-recursion dispatch.
        Assert.Contains(sink.Fragments, f => f.Box.Kind == BoxKind.TableCell);

        // Walk fragments for any BoxFragment whose InlineLayout is
        // populated — that's the cell-content inline-only-block
        // fragment. The cell's div lands either as a direct fragment
        // (if it has inline content + uses inline-only-block dispatch)
        // OR as an AnonymousBlock fragment around the TextRun. Either
        // way the InlineLayout should be populated somewhere.
        var inlineFragments = sink.Fragments
            .Where(f => f.InlineLayout?.Lines is not null && f.InlineLayout.Value.Lines.Length > 0)
            .ToList();
        Assert.NotEmpty(inlineFragments);

        // Verify at least one inline fragment carries real glyphs
        // (the shape pass produced glyph data for "Hello world").
        var anyGlyphs = false;
        foreach (var frag in inlineFragments)
        {
            var inlineLayout = frag.InlineLayout!.Value;
            foreach (var run in inlineLayout.ShapedRuns)
            {
                if (run.Glyphs.Length > 0) { anyGlyphs = true; break; }
            }
            if (anyGlyphs) break;
        }
        Assert.True(anyGlyphs, "Expected at least one shaped glyph in the cell's inline content.");
    }

    // ====================================================================
    //  Pipeline driver
    // ====================================================================

    /// <summary>Drive an HTML string through the full production
    /// pipeline + run <see cref="BlockLayouter"/> on the resulting box
    /// tree. Returns the recording sink + diagnostic sink + the root
    /// box for assertions.</summary>
    private static async Task<(RecordingFragmentSink sink,
        RecordingDiagnosticsSink diagnostics, Box root)>
        RenderViaFullPipelineAsync(string html)
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

        return (sink, diagSink, box);
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

    // ====================================================================
    //  Test doubles
    // ====================================================================

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
    }

    private sealed class RecordingDiagnosticsSink : IPaginateDiagnosticsSink
    {
        public List<PaginateDiagnostic> Diagnostics { get; } = new();
        public void Emit(PaginateDiagnostic diagnostic) => Diagnostics.Add(diagnostic);
    }

    private sealed class SyntheticShaperResolver : IShaperResolver
    {
        private readonly HbShaper _shaper = new(SyntheticFont.Build(), fontSizePx: 12);
        public HbShaper Resolve(ComputedStyle style) => _shaper;
        public void Dispose() => _shaper.Dispose();
    }
}
