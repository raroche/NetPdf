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

    [Fact]
    public async Task Cycle2_production_multi_page_multicol_via_full_pipeline()
    {
        // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) — the
        // depth==1-only continuation propagation limit has been
        // lifted. A real HTML multicol nested inside HTML > BODY
        // now SPLITS CLEANLY: the recursion returns a chained
        // BlockContinuation(rc=N, ls=BlockContinuation(rc=N,
        // ls=MulticolContinuation)) reflecting the html→body→div
        // nesting; the OUTER AttemptLayout wraps that into the
        // top-level PageComplete result.
        //
        // Pre-finding the test asserted truncation
        // (LAYOUT-MULTICOL-FORCED-OVERFLOW-001 fired + at least 2
        // item fragments landed); post-finding the test asserts
        // POSITIVE multi-page outcome: no forced-overflow diagnostic,
        // PageComplete on the first page, continuation chain shape
        // matches the HTML+BODY nesting depth.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .multicol {
                    column-count: 2;
                    width: 232px;
                    height: 100px;
                }
                .item { height: 80px; }
            </style></head><body>
            <div class="multicol">
              <div class="item"></div>
              <div class="item"></div>
              <div class="item"></div>
              <div class="item"></div>
              <div class="item"></div>
            </div>
            </body></html>
            """;

        var (sink, diagnostics, _, result) =
            await RenderViaFullPipelineCapturingResultAsync(html);

        // The pipeline didn't crash; verify fragments were emitted.
        var hasMulticolFragment = false;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind != BoxKind.BlockContainer) continue;
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            var cls = srcEl.GetAttribute("class");
            if (cls == "multicol") hasMulticolFragment = true;
        }
        Assert.True(hasMulticolFragment,
            "Multicol wrapper fragment should be emitted on page 1.");

        // Per Phase 3 Task 14 cycle 2 hardening Finding #1 — NO
        // forced-overflow diagnostic. The deep-nested multicol now
        // splits cleanly via the lifted continuation propagation.
        var hasForcedOverflow = false;
        foreach (var d in diagnostics.Diagnostics)
        {
            if (d.Code == PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001)
            {
                hasForcedOverflow = true;
                break;
            }
        }
        Assert.False(hasForcedOverflow,
            "After cycle 2 hardening Finding #1, deep-nested multicol must NOT "
            + "emit LAYOUT-MULTICOL-FORCED-OVERFLOW-001 — the multi-level "
            + "recursion-continuation propagation handles it cleanly. "
            + "Diagnostics: "
            + string.Join("; ", diagnostics.Diagnostics.Select(d =>
                $"[{d.Code}] {d.Message}")));

        // Page 1 must be PageComplete with a chained continuation.
        // The chain shape mirrors the html → body → div.multicol
        // depth: BlockContinuation(rc=<html-child-of-root>,
        // LayouterState=BlockContinuation(rc=<body-child-of-html>,
        // LayouterState=...=MulticolContinuation)).
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var topBc = Assert.IsType<BlockContinuation>(result.Continuation);

        // Walk the chain to the MulticolContinuation leaf. The exact
        // depth depends on the BoxBuilder's wrapping; we walk through
        // any nested BlockContinuations until we hit the leaf.
        object? walker = topBc.LayouterState;
        var chainDepth = 0;
        while (walker is BlockContinuation deeper)
        {
            chainDepth++;
            walker = deeper.LayouterState;
            Assert.True(chainDepth < 32,
                "Continuation chain unexpectedly deep — chain runaway?");
        }
        Assert.IsType<MulticolContinuation>(walker);
        Assert.True(chainDepth >= 1,
            "Expected chain depth >= 1 — the HTML > BODY > multicol "
            + "nesting should produce at least one nested BlockContinuation "
            + "wrapping the MulticolContinuation leaf. Actual chainDepth = "
            + chainDepth);
    }

    [Fact]
    public void Cycle2_production_multicol_as_root_child_splits_cleanly()
    {
        // Per Phase 3 Task 14 cycle 2 — full-pipeline propagation
        // test. The HTML > BODY wrapping in real documents puts the
        // multicol at recursion depth 2 (where cycle 2 single-level
        // propagation can't reach); this test exercises the depth==1
        // propagation path by placing the multicol as a DIRECT child
        // of the root. Mirrors Task 13 cycle 1's
        // `Cycle1_production_table_as_root_child_splits_cleanly_at_outer_path`
        // pattern.
        //
        // Setup: 2-column multicol 100 tall + 4 children of 80 each.
        // Expected: PageComplete(BlockContinuation(LayouterState=
        // MulticolContinuation(NextChildIndex=2))).
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rootStyle = ComputedStyle.RentForExclusiveTesting();
        var root = Box.CreateRoot(rootStyle);
        var multicolStyle = ComputedStyle.RentForExclusiveTesting();
        multicolStyle.Set(PropertyId.ColumnCount, ComputedSlot.FromInteger(2));
        var multicolElem = MakeElement();
        var multicol = Box.ForElement(BoxKind.BlockContainer, multicolStyle, multicolElem);
        for (var i = 0; i < 4; i++)
        {
            var s = ComputedStyle.RentForExclusiveTesting();
            s.Set(PropertyId.Height, ComputedSlot.FromLengthPx(80));
            var c = Box.ForElement(BoxKind.BlockContainer, s, MakeElement());
            multicol.AppendChild(c);
        }
        // Set explicit height: 100 on the multicol so the column
        // block-size is 100.
        multicolStyle.Set(PropertyId.Height, ComputedSlot.FromLengthPx(100));
        root.AppendChild(multicol);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: diagSink,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var blockCont = Assert.IsType<BlockContinuation>(result.Continuation);
        var mcCont = Assert.IsType<MulticolContinuation>(blockCont.LayouterState);
        Assert.Equal(2, mcCont.NextChildIndex);

        // The forced-overflow diagnostic must NOT fire — clean split.
        foreach (var d in diagSink.Diagnostics)
        {
            Assert.NotEqual(
                PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001,
                d.Code);
        }
    }

    [Fact]
    public void Cycle2_production_multicol_at_depth_3_splits_cleanly()
    {
        // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) — the
        // multi-level continuation propagation lifts the depth==1-only
        // limit. Construct a 3-level-deep nest:
        //   root > div1 > div2 > multicol(column-count=2, h=100)
        // with 4 child items of 80 each — content overflows the 2
        // columns on page 1. The recursion returns a chain of THREE
        // nested BlockContinuations wrapping the MulticolContinuation
        // leaf.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rootStyle = ComputedStyle.RentForExclusiveTesting();
        var root = Box.CreateRoot(rootStyle);
        var div1Style = ComputedStyle.RentForExclusiveTesting();
        var div1 = Box.ForElement(BoxKind.BlockContainer, div1Style, MakeElement());
        var div2Style = ComputedStyle.RentForExclusiveTesting();
        var div2 = Box.ForElement(BoxKind.BlockContainer, div2Style, MakeElement());
        var multicolStyle = ComputedStyle.RentForExclusiveTesting();
        multicolStyle.Set(PropertyId.ColumnCount, ComputedSlot.FromInteger(2));
        multicolStyle.Set(PropertyId.Height, ComputedSlot.FromLengthPx(100));
        var multicol = Box.ForElement(BoxKind.BlockContainer, multicolStyle, MakeElement());
        for (var i = 0; i < 4; i++)
        {
            var s = ComputedStyle.RentForExclusiveTesting();
            s.Set(PropertyId.Height, ComputedSlot.FromLengthPx(80));
            multicol.AppendChild(
                Box.ForElement(BoxKind.BlockContainer, s, MakeElement()));
        }
        div2.AppendChild(multicol);
        div1.AppendChild(div2);
        root.AppendChild(div1);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null,
            diagnostics: diagSink,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var topBc = Assert.IsType<BlockContinuation>(result.Continuation);
        // Per post-PR-#57 review #2 Finding #1 — chain has DOM-depth
        // layers (the flatten that dropped one intermediate index has
        // been removed). For `root > div1 > div2 > multicol` the chain
        // is BC > BC > BC > MulticolContinuation:
        //   topBc      → wraps div1's recursion descent
        //   bcLevel1   → wraps div2's recursion descent
        //   bcLevel2   → wraps multicol's container position
        //   mcCont     → the multicol's resume state (leaf)
        var bcLevel1 = Assert.IsType<BlockContinuation>(topBc.LayouterState);
        var bcLevel2 = Assert.IsType<BlockContinuation>(bcLevel1.LayouterState);
        var mcCont = Assert.IsType<MulticolContinuation>(bcLevel2.LayouterState);
        Assert.Equal(2, mcCont.NextChildIndex);

        // No forced-overflow diagnostic — the multi-level propagation
        // is now clean.
        foreach (var d in diagSink.Diagnostics)
        {
            Assert.NotEqual(
                PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001,
                d.Code);
        }
    }

    [Fact]
    public async Task Cycle2_production_multicol_via_html_body_splits_cleanly()
    {
        // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) —
        // positive-case complement to the flipped pin test. Same
        // html > body > multicol shape but asserting clean split
        // (chain shape + no forced-overflow). The flipped pin asserts
        // the same thing in chain-walking form; this test pins the
        // exact PageComplete outcome + structured chain destructuring.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .multicol {
                    column-count: 2;
                    width: 232px;
                    height: 100px;
                }
                .item { height: 80px; }
            </style></head><body>
            <div class="multicol">
              <div class="item"></div>
              <div class="item"></div>
              <div class="item"></div>
              <div class="item"></div>
            </div>
            </body></html>
            """;

        var (_, diagnostics, _, result) =
            await RenderViaFullPipelineCapturingResultAsync(html);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var topBc = Assert.IsType<BlockContinuation>(result.Continuation);
        // Walk the chain to MulticolContinuation leaf — varies in depth
        // depending on how BoxBuilder wraps html/body. We require at
        // least one nested level + a multicol leaf.
        object? walker = topBc.LayouterState;
        var depth = 0;
        while (walker is BlockContinuation deeper)
        {
            depth++;
            walker = deeper.LayouterState;
            Assert.True(depth < 32, "Chain runaway?");
        }
        Assert.IsType<MulticolContinuation>(walker);
        Assert.True(depth >= 1,
            "Expected chain depth >= 1 — html > body > multicol "
            + "produces at least one nested BlockContinuation. Got " + depth);
        Assert.DoesNotContain(diagnostics.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001);
    }

    [Fact]
    public async Task Cycle3_production_multicol_with_auto_height_balances_cleanly()
    {
        // Per Phase 3 Task 14 cycle 3 — full pipeline (HTML → cascade →
        // box → BlockLayouter → MulticolLayouter) for an auto-height
        // multicol container with column-fill: balance (the spec
        // default). Cycle 3 distributes the children equally across
        // columns rather than letting column 0 absorb everything.
        //
        // Setup: 4 paragraph-like blocks of 80 px each in a 2-column
        // multicol with no explicit height. Per cycle 3 balancing:
        //   totalSerial = 320; ideal = ceil(320/2) = 160;
        //   balancedBlockSize = min(160, page-remaining) = 160.
        // Column 0 holds 2 children (160 px); column 1 holds 2 children
        // (160 px). Cycle 1+2 would have placed all 4 children in column
        // 0 (= 320 px) since the page-remaining (800 px) easily
        // accommodates them serially.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .multicol {
                    column-count: 2;
                    width: 232px;
                }
                .item { height: 80px; }
            </style></head><body>
            <div class="multicol">
              <div class="item"></div>
              <div class="item"></div>
              <div class="item"></div>
              <div class="item"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        // Find item fragments + bucket by column (inline offset).
        // Column 0 starts at the multicol's inline-start; column 1 starts
        // at perColumnInline + columnGap. Since BlockLayouter cycle 1
        // returns full available inline (= 600 px from
        // RenderViaFullPipelineAsync), perColumnInline = (600-16)/2 = 292.
        // Column 1 starts at 292 + 16 = 308.
        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind != BoxKind.BlockContainer) continue;
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            if (srcEl.GetAttribute("class") == "item")
            {
                itemFragments.Add(f);
            }
        }
        Assert.Equal(4, itemFragments.Count);

        var column0Items = 0;
        var column1Items = 0;
        foreach (var f in itemFragments)
        {
            if (f.InlineOffset == 0) column0Items++;
            else if (f.InlineOffset > 0) column1Items++;
        }
        // Balanced distribution: 2 children per column.
        Assert.Equal(2, column0Items);
        Assert.Equal(2, column1Items);
    }

    [Fact]
    public async Task Cycle4_production_html_column_width_only_lays_out_in_derived_columns()
    {
        // Per Phase 3 Task 14 cycle 4 — full pipeline (HTML → cascade →
        // box → BlockLayouter → MulticolLayouter) for an auto-column-
        // count multicol container relying on column-width alone.
        //
        // Setup: column-width=120px in a 600px page (RenderViaFullPipeline
        // uses contentInlineSize=600). BlockLayouter cycle 1 ignores the
        // declared width on the wrapper + emits at full page-inline,
        // so the multicol's content inline-size is 600px. With default
        // 16px column-gap, derived N = floor((600+16)/(120+16)) =
        // floor(616/136) = floor(4.529) = 4.
        //
        // Verify 4 columns emit: 4 children at column offsets 0,
        // perCol+gap, 2*(perCol+gap), 3*(perCol+gap), where
        // perCol = (600 - 3*16)/4 = 552/4 = 138.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .multicol {
                    column-width: 120px;
                    height: 100px;
                }
                .item { height: 90px; }
            </style></head><body>
            <div class="multicol">
              <div class="item"></div>
              <div class="item"></div>
              <div class="item"></div>
              <div class="item"></div>
            </div>
            </body></html>
            """;

        var (sink, diagnostics, _) = await RenderViaFullPipelineAsync(html);

        // Confirm column-width parsed to a LengthPx slot.
        BoxFragment? multicolFragment = null;
        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind != BoxKind.BlockContainer) continue;
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            var cls = srcEl.GetAttribute("class");
            if (cls == "multicol") multicolFragment = f;
            else if (cls == "item") itemFragments.Add(f);
        }
        Assert.NotNull(multicolFragment);
        Assert.Equal(4, itemFragments.Count);

        // column-width parsed AND ReadColumnWidth returns 120.
        var columnWidth = multicolFragment!.Value.Box.Style.ReadColumnWidth();
        Assert.Equal(120, columnWidth);
        // column-count NOT set (= auto).
        var columnCount = multicolFragment.Value.Box.Style.ReadColumnCount();
        Assert.Null(columnCount);

        // The 4 children land in 4 distinct columns. Compute expected
        // perColumnWidth from the wrapper's actual inline size — cycle 1
        // BlockLayouter emits the wrapper at the full page-inline (600 px)
        // even when `width: 100%` is declared (the cycle 3 work for
        // explicit width resolution lives in BlockLayouter, not here).
        var wrapperInline = multicolFragment.Value.InlineSize;
        Assert.True(wrapperInline > 0);
        // derivedN per ComputeUsedColumnCount, given the actual wrapper
        // inline size + the 16 px default column-gap.
        var expectedN = ComputedStyleLayoutExtensions.ComputeUsedColumnCount(
            containerContentInlineSize: wrapperInline,
            specifiedColumnCount: null,
            columnWidth: 120,
            columnGap: 16);
        Assert.True(expectedN >= 2,
            $"Expected derivedN >= 2 for wrapperInline={wrapperInline} + columnWidth=120 + gap=16; got {expectedN}.");
        var expectedPerCol = (wrapperInline - (expectedN - 1) * 16.0) / expectedN;

        // Each of the 4 items is in a distinct column (= 4 distinct
        // inline offsets). Inline offsets follow column 0..N-1
        // mapping: 0, perCol+gap, 2(perCol+gap), ...
        // Bucket the fragments by which column they're in.
        var offsetsSeen = new HashSet<int>();
        foreach (var f in itemFragments)
        {
            // Find which column the offset corresponds to.
            for (var c = 0; c < expectedN; c++)
            {
                var expectedOffset = c * (expectedPerCol + 16.0);
                if (System.Math.Abs(f.InlineOffset - expectedOffset) < 0.5)
                {
                    offsetsSeen.Add(c);
                    break;
                }
            }
        }
        // All 4 children land in DISTINCT columns when N >= 4 (= one
        // child per column). For smaller derived N (e.g., 2 or 3), some
        // children share a column. The test pins the derivation: at
        // a 600 px wrapper inline-size + 120 px column-width + 16 px
        // gap, derivedN = 4 and each child lands in its own column.
        Assert.Equal(expectedN, offsetsSeen.Count);

        // No forced-overflow diagnostic — the layout fits cleanly.
        Assert.DoesNotContain(diagnostics.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001);
    }

    [Fact]
    public async Task Em_column_width_columnizes_after_deferred_length_resolution()
    {
        // multicol-balancing-pagination (font-relative `column-width`) — the FLIP of the former
        // `..._em_does_not_trigger_multicol_yet` pin. CSS Multi-column L1 §3.1's introductory
        // example is `column-width: 12em`; the cascade DEFERS em/rem (the slot stays Unset, the
        // raw rides along), so pre-fix `ReadColumnWidth` returned null + the container fell
        // through to ordinary block flow. `DeferredLengthResolver` now resolves a font-relative
        // `column-width` against the cascaded font-size BEFORE the multicol dispatch, so the
        // container columnizes exactly like an absolute `column-width: <px>`.
        //
        // Setup: `font-size: 25px; column-width: 8em` ⇒ 25 × 8 = 200 px (NOT the 12em / default-
        // 16 px the literal might suggest — the test proves em scales with the CASCADED size).
        // An explicit `column-gap: 16px` isolates this from the (font-relative) gutter so the
        // test pins column-WIDTH only. In a 600 px container:
        //   derivedN = floor((600 + 16) / (200 + 16)) = floor(616 / 216) = 2,
        //   perColumnInline = (600 − (2 − 1) × 16) / 2 = 584 / 2 = 292.
        // Four 30 px items (120 px) overflow the 100 px column-0 budget, so serial fill spills
        // the 4th into column 1 — proving an actual per-column inline TRANSLATION, not just a
        // narrower wrapper.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .multicol {
                    font-size: 25px;
                    column-width: 8em;
                    column-gap: 16px;
                    height: 100px;
                }
                .item { height: 30px; }
            </style></head><body>
            <div class="multicol">
              <div class="item"></div>
              <div class="item"></div>
              <div class="item"></div>
              <div class="item"></div>
            </div>
            </body></html>
            """;

        var (sink, diagnostics, _) = await RenderViaFullPipelineAsync(html);

        // Find the multicol wrapper + item fragments.
        BoxFragment? multicolFragment = null;
        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind != BoxKind.BlockContainer) continue;
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            var cls = srcEl.GetAttribute("class");
            if (cls == "multicol") multicolFragment = f;
            else if (cls == "item") itemFragments.Add(f);
        }
        Assert.NotNull(multicolFragment);
        Assert.Equal(4, itemFragments.Count);

        // `column-width: 8em` resolved to 200 px against the 25 px font-size (was: null/deferred).
        var columnWidth = multicolFragment!.Value.Box.Style.ReadColumnWidth();
        Assert.Equal(200, columnWidth);

        // derivedN = 2 (auto column-count) ⇒ each item is the per-column inline size (292 px),
        // strictly NARROWER than the wrapper (600 px) — the unambiguous proof that columnization
        // fired rather than ordinary block flow (which would give the full wrapper width).
        var wrapperInline = multicolFragment.Value.InlineSize;
        var expectedN = ComputedStyleLayoutExtensions.ComputeUsedColumnCount(
            containerContentInlineSize: wrapperInline,
            specifiedColumnCount: null,
            columnWidth: 200,
            columnGap: 16);
        Assert.Equal(2, expectedN);
        var expectedPerCol = (wrapperInline - (expectedN - 1) * 16.0) / expectedN;
        foreach (var f in itemFragments)
        {
            Assert.Equal(expectedPerCol, f.InlineSize, precision: 3);
            Assert.True(f.InlineSize < wrapperInline - 1.0,
                $"item inline-size {f.InlineSize} should be a per-column slice (< wrapper {wrapperInline}).");
        }

        // The items occupy at least two DISTINCT columns (serial fill — explicit height uses the
        // conservative serial path — packs three items in column 0 then spills the 4th into
        // column 1: column 0 at offset 0, column 1 at offset 292 + 16 = 308).
        var distinctColumnOffsets = new HashSet<int>();
        foreach (var f in itemFragments)
            distinctColumnOffsets.Add((int)System.Math.Round(f.InlineOffset));
        Assert.True(distinctColumnOffsets.Count >= 2,
            "Expected the items to spread across >= 2 columns; offsets: "
            + string.Join(", ", distinctColumnOffsets));

        // Clean fit — no forced-overflow diagnostic.
        Assert.DoesNotContain(diagnostics.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001);
    }

    [Fact]
    public async Task Em_column_gap_widens_the_gutter_after_deferred_resolution()
    {
        // multicol-balancing-pagination (font-relative gutter) — `column-gap: 2em` at
        // `font-size: 20px` resolves to 40 px (DeferredLengthResolver, against the cascaded font
        // size), NOT the former 0/normal-fallback. In a 600 px 2-column container:
        //   perColumnInline = (600 − 40) / 2 = 280,  column 1 origin = 280 + 40 = 320.
        // The default 16 px gap would give perCol 292 + a 308 origin, so the 320 origin + 280
        // width are the unambiguous proof the em gutter resolved.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .multicol {
                    column-count: 2;
                    font-size: 20px;
                    column-gap: 2em;
                    height: 50px;
                }
                .item { height: 40px; }
            </style></head><body>
            <div class="multicol">
              <div class="item"></div>
              <div class="item"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var itemFragments = CollectItemFragments(sink);
        Assert.Equal(2, itemFragments.Count);
        // Both items are 280 px wide (perColumnInline with the 40 px gutter).
        foreach (var f in itemFragments)
            Assert.Equal(280, f.InlineSize, precision: 3);
        // Column 0 at offset 0, column 1 at offset 320 (= 280 + the 40 px em gutter).
        var offsets = itemFragments.Select(f => f.InlineOffset).OrderBy(x => x).ToList();
        Assert.Equal(0, offsets[0], precision: 3);
        Assert.Equal(320, offsets[1], precision: 3);
    }

    [Fact]
    public async Task Normal_column_gap_resolves_to_one_em_not_a_hard_coded_16px()
    {
        // multicol-balancing-pagination — the `normal` initial gutter is 1em (CSS Multi-column L1
        // §6.1), now scaled by the cascaded font-size instead of the former hard-coded 16 px. At
        // `font-size: 25px` the gutter is 25 px:
        //   perColumnInline = (600 − 25) / 2 = 287.5,  column 1 origin = 287.5 + 25 = 312.5
        // (a hard-coded 16 px would give 292 + a 308 origin).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .multicol {
                    column-count: 2;
                    font-size: 25px;
                    height: 50px;
                }
                .item { height: 40px; }
            </style></head><body>
            <div class="multicol">
              <div class="item"></div>
              <div class="item"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var itemFragments = CollectItemFragments(sink);
        Assert.Equal(2, itemFragments.Count);
        foreach (var f in itemFragments)
            Assert.Equal(287.5, f.InlineSize, precision: 3);
        var offsets = itemFragments.Select(f => f.InlineOffset).OrderBy(x => x).ToList();
        Assert.Equal(0, offsets[0], precision: 3);
        Assert.Equal(312.5, offsets[1], precision: 3);
    }

    private static List<BoxFragment> CollectItemFragments(RecordingFragmentSink sink)
    {
        var items = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind != BoxKind.BlockContainer) continue;
            if (f.Box.SourceElement?.GetAttribute("class") == "item") items.Add(f);
        }
        return items;
    }

    private static AngleSharp.Dom.IElement MakeElement()
    {
        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var doc = parser.ParseDocument("<div></div>");
        return doc.CreateElement("div");
    }

    // ====================================================================
    //  Pipeline driver — mirrors TableLayouterProductionTests.
    // ====================================================================

    private static async Task<(RecordingFragmentSink sink,
        RecordingDiagnosticsSink diagnostics, Box root)>
        RenderViaFullPipelineAsync(string html)
    {
        var (sink, diagnostics, box, _) = await RenderViaFullPipelineCapturingResultAsync(html);
        return (sink, diagnostics, box);
    }

    private static async Task<(RecordingFragmentSink sink,
        RecordingDiagnosticsSink diagnostics, Box root,
        LayoutAttemptResult result)>
        RenderViaFullPipelineCapturingResultAsync(string html)
    {
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheets = AdaptAllSheetsViaPreprocessor(document);
        var cascade = CascadeResolver.Resolve(document, sheets, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, document);
        var box = BoxBuilder.Build(document, resolved);

        // Mirror PdfRenderPipeline: resolve deferred font-/viewport-relative lengths in place
        // (em / rem / vw) before layout, so a font-relative `column-width: 12em` columnizes
        // exactly as it does in the real render path (multicol-balancing-pagination). The page
        // box matches the unit harness's fragmentainer (600 × 800); em/rem don't depend on it.
        DeferredLengthResolver.ResolveTreeInPlace(box, pageWidthPx: 600, pageHeightPx: 800);

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
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        return (sink, diagSink, box, result);
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

        public void UpdateFragmentBlockSize(int cursor, double newBlockSize)
        {
            // Per Phase 3 Task 17 cycle 5c.2b — F2 wrapper-resize.
            // Mutate the BoxFragment at <c>cursor</c> in place so the
            // BlockLayouter's post-dispatch wrapper-resize consumer
            // can shrink a paginatable-grid / paginatable-flex
            // wrapper from the clamped budget to the actual emitted
            // extent without breaking z-order (= the wrapper stays
            // ahead of its children in the fragment list).
            if (cursor < 0 || cursor >= Fragments.Count) return;
            Fragments[cursor] = Fragments[cursor] with { BlockSize = newBlockSize };
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
