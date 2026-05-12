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
/// Phase 3 Task 14 cycle 1 — production-pipeline tests for
/// <see cref="MulticolLayouter"/>. The existing
/// <see cref="MulticolLayouterTests"/> construct box trees directly
/// (bypassing BoxBuilder); this fixture exercises multicol through
/// the FULL pipeline:
///
/// <para>HTML → <c>HtmlParsingHost</c> → <c>CssPreprocessor</c> →
/// <c>CssParserAdapter</c> → <c>CascadeResolver</c> →
/// <c>VarResolver</c> → <c>BoxBuilder</c> → <c>BlockLayouter</c>
/// (which dispatches into <c>MulticolLayouter</c> for any block
/// container declaring <c>column-count: N</c> with N &gt;= 2).</para>
///
/// <para>Coverage delivered: the production cascade resolves the
/// <c>column-count</c> property into the
/// <see cref="ComputedSlotTag.Integer"/> slot the
/// <see cref="ComputedStyleLayoutExtensions.ReadColumnCount"/>
/// extension method consumes; the BoxBuilder produces a regular
/// <see cref="BoxKind.BlockContainer"/> for the multicol element
/// (no dedicated kind); the BlockLayouter dispatch fires + the
/// MulticolLayouter emits per-column fragments at the correct
/// inline offsets.</para>
/// </summary>
public sealed class MulticolLayouterProductionTests
{
    [Fact]
    public async Task Production_html_div_with_column_count_lays_out_in_columns()
    {
        // Per Phase 3 Task 14 cycle 1 — a real HTML <div> with
        // `column-count: 2; width: 200px` flows through every stage
        // of the pipeline + emits per-column content fragments at
        // the expected inline offsets.
        //
        // The two inner blocks are each 90 px tall + the column
        // block-size is 100 px, so block 0 fills column 0 + block 1
        // is forced into column 1.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .multicol {
                    column-count: 2;
                    width: 200px;
                    height: 100px;
                }
                .item { height: 90px; }
            </style></head><body>
            <div class="multicol">
              <div class="item"></div>
              <div class="item"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        // Find the multicol container (= the <div class="multicol">,
        // a BlockContainer with column-count declared).
        BoxFragment? multicolFragment = null;
        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind != BoxKind.BlockContainer) continue;
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            var classAttr = srcEl.GetAttribute("class");
            if (classAttr == "multicol")
            {
                multicolFragment = f;
            }
            else if (classAttr == "item")
            {
                itemFragments.Add(f);
            }
        }
        Assert.NotNull(multicolFragment);
        Assert.Equal(2, itemFragments.Count);

        // Confirm the cascade resolved column-count: 2 to an Integer
        // slot on the multicol DIV's computed style. If the cascade
        // didn't honor the property the dispatch never fires + the
        // items would emit at the full wrapper width.
        var columnCount = multicolFragment!.Value.Box.Style.ReadColumnCount();
        Assert.Equal(2, columnCount);

        // Per cycle 1 — BlockLayouter cycle 1's width handling
        // returns the FULL available inline-axis space when
        // `width: auto` (the cycle 3 work for explicit width
        // resolution is deferred). The container's wrapper inline
        // size is therefore the page content-inline-size (600 px)
        // rather than the declared 200 px. The per-column math
        // still derives from the wrapper's actual inline-size +
        // the 16 px default gap. Sub-cycle 2 will tighten this
        // when BlockLayouter honors declared widths.
        var wrapperInline = multicolFragment.Value.InlineSize;
        Assert.True(wrapperInline > 0,
            $"Multicol wrapper has non-positive inline size: {wrapperInline}.");

        // Per-column inline size = (wrapperInline - 16) / 2.
        // Column 0 offset = 0; column 1 offset =
        // perColumnInlineSize + 16.
        var expectedColumnWidth = (wrapperInline - 16.0) / 2.0;
        Assert.Equal(expectedColumnWidth, itemFragments[0].InlineSize, precision: 3);
        Assert.Equal(0, itemFragments[0].InlineOffset, precision: 3);
        Assert.Equal(expectedColumnWidth, itemFragments[1].InlineSize, precision: 3);
        Assert.Equal(expectedColumnWidth + 16.0, itemFragments[1].InlineOffset, precision: 3);
    }

    [Fact]
    public async Task Production_multicol_followed_by_paragraph_no_overlap()
    {
        // Per Phase 3 Task 14 cycle 1 hardening (Finding 1) — when a
        // multicol container is followed by a sibling block, the
        // sibling must NOT overlap the multicol's columnized content.
        // Pre-fix the outer cursor advance summed the serial subtree
        // extent (~180 px from two 90-px children); the trailing
        // paragraph then jumped to 180 px leaving false blank space.
        // This test pins the corrected behavior end-to-end through
        // the production pipeline.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .multicol {
                    column-count: 2;
                    width: 200px;
                    height: 100px;
                }
                .item { height: 90px; }
                .after { height: 50px; }
            </style></head><body>
            <div class="multicol">
              <div class="item"></div>
              <div class="item"></div>
            </div>
            <div class="after"></div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        BoxFragment? multicolFragment = null;
        BoxFragment? afterFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind != BoxKind.BlockContainer) continue;
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            var classAttr = srcEl.GetAttribute("class");
            if (classAttr == "multicol") multicolFragment = f;
            else if (classAttr == "after") afterFragment = f;
        }
        Assert.NotNull(multicolFragment);
        Assert.NotNull(afterFragment);

        // The .after block must land at the multicol's bottom or
        // below (= no overlap). The multicol declares height: 100,
        // so the after block should land at ~100 px. Pre-fix it
        // landed at ~180 px (false blank space from serial sum).
        var multicolBottom = multicolFragment!.Value.BlockOffset
            + multicolFragment.Value.BlockSize;
        Assert.True(afterFragment!.Value.BlockOffset >= multicolBottom - 1.0,
            $".after at {afterFragment.Value.BlockOffset} overlaps multicol "
            + $"(ending at {multicolBottom}).");
        // And the offset shouldn't be wildly past the multicol bottom
        // (within ~10 px slack for any rounding).
        Assert.True(afterFragment.Value.BlockOffset < multicolBottom + 10.0,
            $".after at {afterFragment.Value.BlockOffset} leaves false blank "
            + $"space below multicol (ending at {multicolBottom}) — "
            + "cursor advance is using serial sum of children "
            + "(Finding 1 regression).");
    }

    [Fact]
    public async Task Production_multicol_without_explicit_height_renders_full_column_space()
    {
        // Per Phase 3 Task 14 cycle 1 hardening (Finding 2) — when
        // a multicol container has no explicit height (= height: auto),
        // the per-column block-size derives from the fragmentainer's
        // REMAINING block-space so columns fill the available page.
        // Pre-fix the per-column block-size was ~0 → 1 px (post-clamp)
        // → multicol force-overflowed immediately + truncated all
        // content.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .multicol {
                    column-count: 2;
                    width: 400px;
                    /* No height — height: auto. */
                }
                .item { height: 200px; }
            </style></head><body>
            <div class="multicol">
              <div class="item"></div>
            </div>
            </body></html>
            """;

        var (sink, diagnostics, _) = await RenderViaFullPipelineAsync(html);

        // The .item should be emitted (in column 0). Pre-fix the
        // multicol force-overflowed because the per-column block-size
        // was 1 px; no .item fragment would emit.
        var hasItem = false;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.SourceElement is null) continue;
            if (f.Box.SourceElement.GetAttribute("class") == "item")
            {
                hasItem = true;
                break;
            }
        }
        Assert.True(hasItem,
            "Expected the .item child to be emitted in the auto-height "
            + "multicol's column 0. Pre-fix it was truncated by the "
            + "forced-overflow path. Diagnostics: "
            + string.Join("; ", diagnostics.Diagnostics.Select(d =>
                $"[{d.Code}] {d.Message}")));

        // And NO forced-overflow diagnostic (content fits in column 0).
        foreach (var d in diagnostics.Diagnostics)
        {
            Assert.NotEqual(
                PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001,
                d.Code);
        }
    }

    // ====================================================================
    //  Pipeline driver — mirrors TableLayouterProductionTests.
    // ====================================================================

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
    //  Test doubles — same shape as TableLayouterProductionTests'.
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
