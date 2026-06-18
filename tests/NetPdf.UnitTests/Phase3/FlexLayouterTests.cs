// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
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
/// Phase 3 Task 15 cycle 1 (Hello World) — direct-construction tests
/// for <see cref="FlexLayouter"/>. Constructs the flex container
/// (a <see cref="BoxKind.FlexContainer"/> Box) directly via test
/// helpers + asserts the layouter's per-item emission math.
///
/// <para>Fixture mirrors <see cref="MulticolLayouterTests"/>'s helper
/// shape — <c>RentForExclusiveTesting</c> styles, a
/// <see cref="RecordingFragmentSink"/>, a
/// <see cref="RecordingDiagnosticsSink"/>. The synthetic-font shaper
/// resolver is plumbed but unused in cycle 1 (no inline content
/// inside flex items yet); kept for parity with the other layouters'
/// constructor shapes + so cycle 2's inline-content tests don't need
/// fixture changes.</para>
///
/// <para><b>Cycle 1 (Hello World) behaviors exercised:</b>
/// <list type="bullet">
///   <item>3 items with explicit widths pack at consecutive inline
///   offsets (single-line row, flex-start packing).</item>
///   <item>Single item emits at the container's content-inline-start
///   (= contentInlineOffset 0 in the test fixture).</item>
///   <item>Empty flex container emits only the wrapper fragment from
///   the dispatching BlockLayouter.</item>
///   <item>Constructor rejects non-null incomingContinuation (multi-
///   page split is sub-cycle 2+ scope).</item>
///   <item>Constructor rejects a non-flex root BoxKind.</item>
/// </list></para>
/// </summary>
public sealed class FlexLayouterTests
{
    [Fact]
    public void Flex_container_emits_wrapper_plus_items_in_row()
    {
        // 3 items with explicit widths 100, 50, 80 should emit at
        // InlineOffsets 0, 100, 150 with those widths. The wrapper
        // fragment is emitted by BlockLayouter; FlexLayouter emits
        // the per-item content inside.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        // Give the flex container an explicit height so the
        // BlockLayouter dispatch's content-block-size derivation
        // doesn't fall through to 1px clamp.
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var widths = new[] { 100.0, 50.0, 80.0 };
        var items = new Box[widths.Length];
        for (var i = 0; i < widths.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, widths[i]);
            SetLengthPx(style, PropertyId.Height, 50);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // The flex wrapper fragment should be emitted exactly once.
        BoxFragment? wrapper = null;
        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box == flex) wrapper = f;
            else
            {
                for (var i = 0; i < items.Length; i++)
                {
                    if (f.Box == items[i])
                    {
                        itemFragments.Add(f);
                        break;
                    }
                }
            }
        }
        Assert.NotNull(wrapper);
        Assert.Equal(3, itemFragments.Count);

        // Expected inline offsets: 0, 100, 150. The flex container
        // has no border / padding so the content-inline-start matches
        // the wrapper's inline-start (= the page content-area origin
        // = 0).
        Assert.Equal(0.0, itemFragments[0].InlineOffset, precision: 3);
        Assert.Equal(100.0, itemFragments[0].InlineSize, precision: 3);
        Assert.Equal(100.0, itemFragments[1].InlineOffset, precision: 3);
        Assert.Equal(50.0, itemFragments[1].InlineSize, precision: 3);
        Assert.Equal(150.0, itemFragments[2].InlineOffset, precision: 3);
        Assert.Equal(80.0, itemFragments[2].InlineSize, precision: 3);

        // Each item lands at the container's content-block-start
        // (cycle 1 emits align-items: flex-start equivalent
        // regardless of the computed value).
        foreach (var f in itemFragments)
        {
            Assert.Equal(50.0, f.BlockSize, precision: 3);
        }
    }

    [Fact]
    public void Flex_container_single_item_emits_at_origin()
    {
        // A single flex item should emit at the container's content-
        // inline-start (= 0 in this fixture).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var itemStyle = MakeStyle();
        SetLengthPx(itemStyle, PropertyId.Width, 75);
        SetLengthPx(itemStyle, PropertyId.Height, 50);
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? itemFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == item) itemFragment = f;
        }
        Assert.NotNull(itemFragment);
        Assert.Equal(0.0, itemFragment!.Value.InlineOffset, precision: 3);
        Assert.Equal(75.0, itemFragment.Value.InlineSize, precision: 3);
    }

    [Fact]
    public void Flex_container_with_no_children_emits_only_wrapper()
    {
        // A flex container with no children should emit only the
        // wrapper fragment (from BlockLayouter); no per-item content.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Height, 100);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Exactly one wrapper fragment (for the flex container), no
        // item fragments.
        var flexFragmentCount = 0;
        var nonRootNonFlexFragmentCount = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == flex) flexFragmentCount++;
            else if (f.Box != root) nonRootNonFlexFragmentCount++;
        }
        Assert.Equal(1, flexFragmentCount);
        Assert.Equal(0, nonRootNonFlexFragmentCount);
    }

    [Fact]
    public void Flex_container_rejects_non_null_continuation()
    {
        // Per Phase 3 Task 15 cycle 1 (Hello World) — multi-page flex
        // container splitting is sub-cycle 2+ scope. The constructor
        // REJECTS any non-null incomingContinuation. Cycle 2 will
        // accept a FlexContinuation.
        var sink = new RecordingFragmentSink();
        var flex = BuildFlexContainer();
        var continuation = new BlockContinuation(ResumeAtChild: 0, ConsumedBlockSize: 0);

        Assert.Throws<System.ArgumentException>(() =>
            new FlexLayouter(flex, sink, incomingContinuation: continuation));
    }

    [Fact]
    public void Flex_layouter_rejects_non_flex_root_box()
    {
        // The FlexLayouter constructor validates the BoxKind. A
        // plain BlockContainer should throw — the dispatching
        // BlockLayouter's IsFlexContainer predicate is the gate;
        // this is the defensive direct-construction check.
        var sink = new RecordingFragmentSink();
        var blockBox = Box.ForElement(
            BoxKind.BlockContainer, MakeStyle(), MakeElement());

        Assert.Throws<System.ArgumentException>(() =>
            new FlexLayouter(blockBox, sink));
    }

    // ====================================================================
    //  Phase 3 Task 15 L2 — justify-content main-axis alignment
    // ====================================================================
    //
    // Fixture: 3 items of width 50 in a 300px container (= freeSpace 150)
    // unless noted otherwise. The CSS Box Alignment L3 §4.5 keyword
    // index mapping is verified against
    // KeywordResolver.BuildJustifyContentTable: 0=normal,
    // 1=space-between, 2=space-around, 3=space-evenly, 4=stretch,
    // 5=center, 6=start, 7=end, 8=flex-start, 9=flex-end, 10=left,
    // 11=right. The unit tests set the keyword slot directly via
    // ComputedSlot.FromKeyword(...) rather than going through the
    // cascade so the indices are part of the test's contract.

    [Fact]
    public void L2_justify_content_flex_end_packs_items_at_main_end()
    {
        // 3 items of width 50 in a 300px container, justify-content:
        // flex-end (= keyword index 9). freeSpace = 150 →
        // startOffset = 150, betweenSpacing = 0. Expected offsets:
        // 150, 200, 250.
        var fragments = LayoutThreeItemsWithJustifyContent(
            keywordIndex: 9, itemWidth: 50, containerWidth: 300);

        Assert.Equal(3, fragments.Count);
        Assert.Equal(150.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(250.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L2_justify_content_center_centers_items_on_main_axis()
    {
        // 3 items of width 50 in a 300px container, justify-content:
        // center (= keyword index 5). freeSpace = 150 →
        // startOffset = 75, betweenSpacing = 0. Expected offsets:
        // 75, 125, 175.
        var fragments = LayoutThreeItemsWithJustifyContent(
            keywordIndex: 5, itemWidth: 50, containerWidth: 300);

        Assert.Equal(3, fragments.Count);
        Assert.Equal(75.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(125.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(175.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L2_justify_content_space_between_distributes_equal_gaps()
    {
        // 3 items of width 50 in a 300px container, justify-content:
        // space-between (= keyword index 1). freeSpace = 150 →
        // startOffset = 0, betweenSpacing = 150 / (3 - 1) = 75.
        // Expected offsets: 0, 125, 250.
        var fragments = LayoutThreeItemsWithJustifyContent(
            keywordIndex: 1, itemWidth: 50, containerWidth: 300);

        Assert.Equal(3, fragments.Count);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(125.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(250.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L2_justify_content_space_around_half_space_at_edges()
    {
        // 3 items of width 50 in a 300px container, justify-content:
        // space-around (= keyword index 2). freeSpace = 150 →
        // startOffset = 150 / (2 * 3) = 25, betweenSpacing = 150 / 3
        // = 50. Expected offsets: 25, 125, 225.
        var fragments = LayoutThreeItemsWithJustifyContent(
            keywordIndex: 2, itemWidth: 50, containerWidth: 300);

        Assert.Equal(3, fragments.Count);
        Assert.Equal(25.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(125.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(225.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L2_justify_content_space_evenly_equal_space_everywhere()
    {
        // 3 items of width 50 in a 300px container, justify-content:
        // space-evenly (= keyword index 3). freeSpace = 150 →
        // startOffset = 150 / (3 + 1) = 37.5, betweenSpacing = 37.5.
        // Expected offsets: 37.5, 125, 212.5.
        var fragments = LayoutThreeItemsWithJustifyContent(
            keywordIndex: 3, itemWidth: 50, containerWidth: 300);

        Assert.Equal(3, fragments.Count);
        Assert.Equal(37.5, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(125.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(212.5, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L2_justify_content_center_with_overflow_keeps_centered_negative_offset()
    {
        // Per Phase 3 Task 15 L2 post-PR-#62 hardening F#2 — rewritten
        // from the pre-fix `L2_justify_content_overflow_falls_back_to_flex_start`.
        // The pre-fix test pinned the WRONG behavior (all values
        // collapsed to flex-start on overflow). Per CSS Box Alignment
        // L3 §5.3 positional values (= center / flex-end / flex-start)
        // keep their natural offset on overflow — center with negative
        // free-space yields `freeSpace/2` (a negative value) so items
        // overflow EQUALLY on both sides of the container.
        //
        // 5 items of width 100 in a 300px container, justify-content:
        // center (= keyword index 5). totalItemSize = 500, freeSpace
        // = -200 → startOffset = -100. Expected offsets:
        // -100, 0, 100, 200, 300.
        var fragments = LayoutNItemsWithJustifyContent(
            keywordIndex: 5, itemWidth: 100, containerWidth: 300, itemCount: 5);

        Assert.Equal(5, fragments.Count);
        Assert.Equal(-100.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[2].InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[3].InlineOffset, precision: 3);
        Assert.Equal(300.0, fragments[4].InlineOffset, precision: 3);
    }

    [Fact]
    public void L2_justify_content_space_between_with_overflow_falls_back_to_flex_start()
    {
        // Per Phase 3 Task 15 L2 post-PR-#62 hardening F#2 — pins the
        // distribution-value overflow path per CSS Box Alignment L3
        // §5.3. Distribution values (space-between / space-around /
        // space-evenly) DO collapse to safe-start (= flex-start) on
        // overflow — only the positional-value branch keeps its
        // natural offset.
        //
        // 5 items of width 100 in a 300px container, justify-content:
        // space-between (= keyword index 1). freeSpace = -200 →
        // safe-start fallback. Expected offsets: 0, 100, 200, 300, 400.
        var fragments = LayoutNItemsWithJustifyContent(
            keywordIndex: 1, itemWidth: 100, containerWidth: 300, itemCount: 5);

        Assert.Equal(5, fragments.Count);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[2].InlineOffset, precision: 3);
        Assert.Equal(300.0, fragments[3].InlineOffset, precision: 3);
        Assert.Equal(400.0, fragments[4].InlineOffset, precision: 3);
    }

    [Fact]
    public void L2_justify_content_space_around_with_overflow_falls_back_to_flex_start()
    {
        // Per Phase 3 Task 15 L2 post-PR-#62 hardening F#2 — pins
        // space-around's distribution-value overflow fallback. All
        // three distribution values (space-between / space-around /
        // space-evenly) share the same safe-start fallback per §5.3.
        //
        // 5 items of width 100 in a 300px container, justify-content:
        // space-around (= keyword index 2). freeSpace = -200 →
        // safe-start fallback. Expected offsets: 0, 100, 200, 300, 400.
        var fragments = LayoutNItemsWithJustifyContent(
            keywordIndex: 2, itemWidth: 100, containerWidth: 300, itemCount: 5);

        Assert.Equal(5, fragments.Count);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[2].InlineOffset, precision: 3);
        Assert.Equal(300.0, fragments[3].InlineOffset, precision: 3);
        Assert.Equal(400.0, fragments[4].InlineOffset, precision: 3);
    }

    [Fact]
    public void L2_justify_content_safe_center_with_overflow_falls_back_to_flex_start()
    {
        // Per Phase 3 Task 15 L2 post-PR-#62 hardening F#1 — pins the
        // `safe center` compound (= keyword index 12) overflow
        // behavior. Per CSS Box Alignment L3 §5.3 the `safe` modifier
        // forces safe-start fallback on overflow REGARDLESS of the
        // base alignment (= the spec's safe-mode containment).
        //
        // 5 items of width 100 in a 300px container, justify-content:
        // `safe center` (= keyword index 12). freeSpace = -200 →
        // safe modifier forces safe-start. Expected offsets:
        // 0, 100, 200, 300, 400.
        var fragments = LayoutNItemsWithJustifyContent(
            keywordIndex: 12, itemWidth: 100, containerWidth: 300, itemCount: 5);

        Assert.Equal(5, fragments.Count);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[2].InlineOffset, precision: 3);
        Assert.Equal(300.0, fragments[3].InlineOffset, precision: 3);
        Assert.Equal(400.0, fragments[4].InlineOffset, precision: 3);
    }

    [Fact]
    public void L2_justify_content_safe_center_without_overflow_behaves_as_center()
    {
        // Per Phase 3 Task 15 L2 post-PR-#62 hardening F#1 — pins the
        // `safe center` compound (= keyword index 12) WITHOUT-overflow
        // behavior. Per CSS Box Alignment L3 §5.3 the `safe` modifier
        // is TRANSPARENT when free-space is non-negative — it ONLY
        // changes behavior on overflow. So `safe center` with positive
        // free-space behaves identically to bare `center`.
        //
        // 3 items of width 50 in a 300px container, justify-content:
        // `safe center` (= keyword index 12). freeSpace = 150 →
        // startOffset = 75. Expected offsets: 75, 125, 175 (= bare
        // center result from L2_justify_content_center_centers_items_on_main_axis).
        var fragments = LayoutThreeItemsWithJustifyContent(
            keywordIndex: 12, itemWidth: 50, containerWidth: 300);

        Assert.Equal(3, fragments.Count);
        Assert.Equal(75.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(125.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(175.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L2_justify_content_unsafe_flex_end_with_overflow_honors_alignment()
    {
        // Per Phase 3 Task 15 L2 post-PR-#62 hardening F#1 — pins the
        // `unsafe flex-end` compound (= keyword index 23) overflow
        // behavior. Per CSS Box Alignment L3 §5.3 the `unsafe`
        // modifier honors the specified alignment EVEN ON OVERFLOW —
        // items may be pushed offscreen.
        //
        // 5 items of width 100 in a 300px container, justify-content:
        // `unsafe flex-end` (= keyword index 23). totalItemSize = 500,
        // freeSpace = -200 → unsafe modifier honors flex-end → start-
        // offset = freeSpace = -200. Expected offsets:
        // -200, -100, 0, 100, 200.
        var fragments = LayoutNItemsWithJustifyContent(
            keywordIndex: 23, itemWidth: 100, containerWidth: 300, itemCount: 5);

        Assert.Equal(5, fragments.Count);
        Assert.Equal(-200.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(-100.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[2].InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[3].InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[4].InlineOffset, precision: 3);
    }

    [Fact]
    public void L2_justify_content_known_gap_item_margins_ignored_in_free_space_calc()
    {
        // Per Phase 3 Task 15 L2 post-PR-#62 hardening F#3 — DEFERRAL
        // PIN. The current pre-pass at `FlexLayouter.AttemptLayout`
        // sums only the items' declared `width`, IGNORING item
        // margins / padding / borders. Per CSS Flexbox L1 §9.5 the
        // free-space calculation should use each item's resolved
        // margin-box main-axis size; auto margins consume free space
        // BEFORE `justify-content` distributes the remainder.
        //
        // This test pins the CURRENT (incomplete) behavior so that
        // sub-cycle 3+'s outer-main-size pre-pass + auto-margin
        // resolution will make this test FAIL — prompting an update
        // to assert the spec-correct margin-box-aware free-space
        // calculation.
        //
        // Fixture: 3 items of width 50, with margin-right: 20px each
        // in a 300px container, justify-content: space-between.
        //
        // Spec-correct result (FUTURE — what sub-cycle 3+ should
        // produce): totalMarginBoxInline = 3*(50+20) = 210;
        // freeSpace = 90; betweenSpacing = 90/(3-1) = 45;
        // cursors:  0  (item 1)
        //           50 + 20 (margin) + 45 (gap) = 115 (item 2)
        //           115 + 50 + 20 + 45 = 230 (item 3)
        //
        // Current (BUGGY) result: totalItemSize = 3*50 = 150;
        // freeSpace = 150; betweenSpacing = 150/(3-1) = 75; the
        // emission loop currently advances by item width only (no
        // margin contribution either), so offsets are 0/125/250 —
        // identical to the no-margin case.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 300);
        SetLengthPx(flex.Style, PropertyId.Height, 100);
        flex.Style.Set(PropertyId.JustifyContent, ComputedSlot.FromKeyword(1)); // space-between

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 50);
            SetLengthPx(style, PropertyId.Height, 50);
            // Author the margin-right that the pre-pass currently ignores.
            SetLengthPx(style, PropertyId.MarginRight, 20);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 300, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i])
                {
                    fragments.Add(f);
                    break;
                }
            }
        }

        Assert.Equal(3, fragments.Count);
        // F#3 deferral pin — these offsets are the BUGGY result
        // (margins ignored in free-space calc AND in cursor advance).
        // Sub-cycle 3+ should make this fail; when it does, update to
        // the spec-correct offsets 0, 115, 230 (see comments above).
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(125.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(250.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L2_justify_content_single_item_space_between_uses_flex_start()
    {
        // 1 item of width 50 in a 300px container, justify-content:
        // space-between (= keyword index 1). Per spec N=1 with
        // space-between falls back to flex-start (single item has no
        // gaps between). Expected offset: 0.
        var fragments = LayoutNItemsWithJustifyContent(
            keywordIndex: 1, itemWidth: 50, containerWidth: 300, itemCount: 1);

        Assert.Single(fragments);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
    }

    [Fact]
    public void L2_justify_content_normal_keyword_maps_to_flex_start()
    {
        // 3 items of width 50 in a 300px container, justify-content:
        // normal (= keyword index 0). Per CSS Flexbox L1 §8.2 the
        // computed default `normal` resolves to flex-start. Expected
        // offsets: 0, 50, 100 (pack at main-start).
        var fragments = LayoutThreeItemsWithJustifyContent(
            keywordIndex: 0, itemWidth: 50, containerWidth: 300);

        Assert.Equal(3, fragments.Count);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(50.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[2].InlineOffset, precision: 3);
    }

    [Theory]
    [InlineData(6, 0.0, 50.0, 100.0)]     // start → flex-start (LTR row)
    [InlineData(10, 0.0, 50.0, 100.0)]    // left → flex-start (LTR row)
    [InlineData(7, 150.0, 200.0, 250.0)]  // end → flex-end (LTR row)
    [InlineData(11, 150.0, 200.0, 250.0)] // right → flex-end (LTR row)
    public void L2_justify_content_logical_alias_maps_to_physical_for_ltr_row(
        int keywordIndex, double expected0, double expected1, double expected2)
    {
        // Per CSS Box Alignment L3 §4.5 + Copilot PR-#62 review (Cs #3 + #5):
        // `start` / `end` / `left` / `right` are logical-axis aliases that
        // resolve to flex-start / flex-end for the L1 default
        // `flex-direction: row` (LTR). Writing-mode-aware mapping is L3+
        // scope. Pin the current contract: 3 items of width 50 in 300px,
        // freeSpace = 150 → start/left land at 0/50/100 (= flex-start);
        // end/right land at 150/200/250 (= flex-end).
        var fragments = LayoutThreeItemsWithJustifyContent(
            keywordIndex: keywordIndex, itemWidth: 50, containerWidth: 300);

        Assert.Equal(3, fragments.Count);
        Assert.Equal(expected0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(expected1, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(expected2, fragments[2].InlineOffset, precision: 3);
    }

    [Theory]
    [InlineData(13, 0.0, 50.0, 100.0)]    // safe start → flex-start
    [InlineData(14, 150.0, 200.0, 250.0)] // safe end → flex-end (no overflow)
    [InlineData(17, 0.0, 50.0, 100.0)]    // safe left → flex-start
    [InlineData(18, 150.0, 200.0, 250.0)] // safe right → flex-end (no overflow)
    [InlineData(20, 0.0, 50.0, 100.0)]    // unsafe start → flex-start
    [InlineData(21, 150.0, 200.0, 250.0)] // unsafe end → flex-end
    [InlineData(24, 0.0, 50.0, 100.0)]    // unsafe left → flex-start
    [InlineData(25, 150.0, 200.0, 250.0)] // unsafe right → flex-end
    public void L2_justify_content_safe_unsafe_alias_compounds_map_to_physical_without_overflow(
        int keywordIndex, double expected0, double expected1, double expected2)
    {
        // Per Copilot PR-#62 review (Cs #3 + #5) — alias families inside
        // safe/unsafe compounds should also map per the L1 default
        // `flex-direction: row` (LTR). Without overflow the safe modifier
        // is a no-op; both safe and unsafe should produce the same offsets
        // as their bare alias counterparts (pinned by the test above).
        var fragments = LayoutThreeItemsWithJustifyContent(
            keywordIndex: keywordIndex, itemWidth: 50, containerWidth: 300);

        Assert.Equal(3, fragments.Count);
        Assert.Equal(expected0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(expected1, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(expected2, fragments[2].InlineOffset, precision: 3);
    }

    /// <summary>L2 helper — drive FlexLayouter with N identical items
    /// + the requested justify-content keyword. Returns the per-item
    /// fragments in source order (= the order the layouter emitted
    /// them, which for L2's row-packing matches source order
    /// regardless of justify-content).
    ///
    /// <para>The flex container's content-inline-size derives from the
    /// fragmentainer's content-inline-size (cycle 1 BlockLayouter sizes
    /// block-level wrappers to fill the available inline range; the
    /// container's declared <c>width</c> is not honored as a shrink-
    /// to-fit constraint). To keep the test arithmetic precise, the
    /// helper passes the requested <paramref name="containerWidth"/> as
    /// the FragmentainerContext's contentInlineSize so the flex
    /// wrapper's <c>borderBoxInlineSize</c> equals it exactly.</para></summary>
    private static List<BoxFragment> LayoutNItemsWithJustifyContent(
        int keywordIndex, double itemWidth, double containerWidth, int itemCount)
    {
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        // Set the container's declared width for documentation purposes —
        // cycle 1's block-sizing path inherits the available range
        // regardless, so the fragmentainer width below is what
        // controls the layouter's _contentInlineSize.
        SetLengthPx(flex.Style, PropertyId.Width, containerWidth);
        SetLengthPx(flex.Style, PropertyId.Height, 100);
        flex.Style.Set(PropertyId.JustifyContent, ComputedSlot.FromKeyword(keywordIndex));

        var items = new Box[itemCount];
        for (var i = 0; i < itemCount; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, itemWidth);
            SetLengthPx(style, PropertyId.Height, 50);
            // Per Phase 3 Task 15 L8 — the cascade default for
            // `flex-shrink` is `1` (= items shrink to fit when their
            // sum exceeds the container's main size). The L2 / L5
            // overflow tests below were authored pre-L8 against the
            // implicit "items never shrink" behavior; they assume
            // items overflow at their declared widths. Pin
            // `flex-shrink: 0` on every item so the §9.7 shrink
            // resolution stays inert + the overflow assertions hold
            // unchanged. (Tests that EXERCISE flex-shrink set
            // `flex-shrink: 1` explicitly on items + use a separate
            // builder.)
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        // FragmentainerContext.contentInlineSize = containerWidth: the
        // BlockLayouter's wrapper sizing fills this range so the
        // flex container's borderBoxInlineSize = containerWidth and
        // the layouter's _contentInlineSize matches the test fixture's
        // intent exactly.
        var ctx = new FragmentainerContext(
            contentInlineSize: containerWidth, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Filter out the wrapper + root, return item fragments in
        // source order. Each item's Box identity is preserved so the
        // sort order matches the source order of items[].
        var itemFragments = new List<BoxFragment>();
        for (var i = 0; i < itemCount; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i])
                {
                    itemFragments.Add(f);
                    break;
                }
            }
        }
        return itemFragments;
    }

    /// <summary>Three-item L2 helper — convenience wrapper around
    /// <see cref="LayoutNItemsWithJustifyContent"/> for the common
    /// case used by the position + distribution tests.</summary>
    private static List<BoxFragment> LayoutThreeItemsWithJustifyContent(
        int keywordIndex, double itemWidth, double containerWidth) =>
        LayoutNItemsWithJustifyContent(
            keywordIndex, itemWidth, containerWidth, itemCount: 3);

    // ====================================================================
    //  Phase 3 Task 15 L3 — align-items cross-axis alignment
    // ====================================================================
    //
    // Fixture: 3 items of width 100 in a container of declared height
    // 200 (= containerCrossSize 200) unless noted otherwise. The CSS
    // Box Alignment L3 §6 + Flexbox L1 §8.3 keyword index mapping is
    // verified against KeywordResolver.BuildAlignItemsTable
    // (KeywordResolver.cs:286-298) + the SelfPositions order
    // (KeywordResolver.cs:114-115 = "center, start, end, self-start,
    // self-end, flex-start, flex-end"). Tests set the keyword slot
    // directly via ComputedSlot.FromKeyword(...) rather than going
    // through the cascade so the indices are part of the test's
    // contract. The keyword index mapping is:
    //   0=normal, 1=stretch, 2=anchor-center, 3=baseline,
    //   4=first baseline, 5=last baseline,
    //   6=center, 7=start, 8=end, 9=self-start, 10=self-end,
    //   11=flex-start, 12=flex-end,
    //   13-19=safe {center, start, end, self-start, self-end, flex-start, flex-end},
    //   20-26=unsafe {…same 7…}

    [Fact]
    public void L3_align_items_flex_start_packs_items_at_cross_start()
    {
        // 3 items of height 50 in a 200px-tall container, align-items:
        // flex-start (= keyword index 11). crossSpace = 150. Expected
        // block offsets: all at contentBlockOffset (= 0 in this fixture
        // since the flex container has no border / padding).
        var fragments = LayoutThreeItemsWithAlignItems(
            keywordIndex: 11, containerHeight: 200, itemHeight: 50);

        Assert.Equal(3, fragments.Count);
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[2].BlockOffset, precision: 3);
        // Items keep their declared block-size (positional alignment
        // never resizes; only stretch does).
        Assert.Equal(50.0, fragments[0].BlockSize, precision: 3);
        Assert.Equal(50.0, fragments[1].BlockSize, precision: 3);
        Assert.Equal(50.0, fragments[2].BlockSize, precision: 3);
    }

    [Fact]
    public void L3_align_items_flex_end_packs_items_at_cross_end()
    {
        // 3 items of height 50 in a 200px container, align-items:
        // flex-end (= keyword index 12). crossSpace = 150 → block
        // offsets all at contentBlockOffset + crossSpace = 150.
        var fragments = LayoutThreeItemsWithAlignItems(
            keywordIndex: 12, containerHeight: 200, itemHeight: 50);

        Assert.Equal(3, fragments.Count);
        Assert.Equal(150.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(150.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(150.0, fragments[2].BlockOffset, precision: 3);
        Assert.Equal(50.0, fragments[0].BlockSize, precision: 3);
        Assert.Equal(50.0, fragments[1].BlockSize, precision: 3);
        Assert.Equal(50.0, fragments[2].BlockSize, precision: 3);
    }

    [Fact]
    public void L3_align_items_center_centers_items_on_cross_axis()
    {
        // 3 items of height 50 in a 200px container, align-items:
        // center (= keyword index 6). crossSpace = 150 → block
        // offsets all at contentBlockOffset + crossSpace / 2 = 75.
        var fragments = LayoutThreeItemsWithAlignItems(
            keywordIndex: 6, containerHeight: 200, itemHeight: 50);

        Assert.Equal(3, fragments.Count);
        Assert.Equal(75.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(75.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(75.0, fragments[2].BlockOffset, precision: 3);
        Assert.Equal(50.0, fragments[0].BlockSize, precision: 3);
        Assert.Equal(50.0, fragments[1].BlockSize, precision: 3);
        Assert.Equal(50.0, fragments[2].BlockSize, precision: 3);
    }

    [Fact]
    public void L3_align_items_stretch_resizes_items_to_container_cross_size()
    {
        // Per CSS Flexbox L1 §8.3 + §7.2 — `align-items: stretch`
        // resizes the item's cross-axis size to the container's cross
        // extent ONLY when the item's own cross-axis size is auto
        // (read as itemCrossSize == 0 from the cycle-1 reader).
        // Explicitly-sized items keep their declared block-size.
        //
        // Fixture: 3 items in a 200px container with align-items:
        // stretch (= keyword index 1). Items 0 + 2 declare no height
        // (= itemCrossSize 0 → stretched to 200); item 1 declares
        // height 50 (= kept at 50). All emit at the container's
        // content-block-start.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Height, 200);
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(1)); // stretch

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            // Items 0 + 2 leave height auto (no SetLengthPx call →
            // the reader returns 0 → stretch path activates).
            // Item 1 declares an explicit height.
            if (i == 1) SetLengthPx(style, PropertyId.Height, 50);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        // All items emit at the container's content-block-start (=
        // stretch starts at cross-start by definition).
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[2].BlockOffset, precision: 3);
        // Auto-height items get container's cross extent (200).
        Assert.Equal(200.0, fragments[0].BlockSize, precision: 3);
        // Explicit-height item is preserved (50).
        Assert.Equal(50.0, fragments[1].BlockSize, precision: 3);
        Assert.Equal(200.0, fragments[2].BlockSize, precision: 3);
    }

    [Fact]
    public void L3_align_items_overflow_center_keeps_negative_offset()
    {
        // 3 items of height 250 in a 200px container, align-items:
        // center (= keyword index 6). crossSpace = -50 → natural
        // offset = -25 (= -50/2). Per CSS Box Alignment L3 §5.3
        // default mode + positional values keep their natural
        // (possibly-negative) offset — items overflow EQUALLY on
        // both sides.
        var fragments = LayoutThreeItemsWithAlignItems(
            keywordIndex: 6, containerHeight: 200, itemHeight: 250);

        Assert.Equal(3, fragments.Count);
        Assert.Equal(-25.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(-25.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(-25.0, fragments[2].BlockOffset, precision: 3);
        // Items keep declared block-size; positional alignment doesn't resize.
        Assert.Equal(250.0, fragments[0].BlockSize, precision: 3);
    }

    [Fact]
    public void L3_align_items_safe_center_overflow_falls_back_to_flex_start()
    {
        // Per CSS Box Alignment L3 §5.3 — the `safe` modifier forces
        // safe-start (= contentBlockOffset) fallback on overflow
        // regardless of the base value.
        //
        // 3 items of height 250 in a 200px container, align-items:
        // `safe center` (= keyword index 13). crossSpace = -50 →
        // safe modifier forces block-offset = contentBlockOffset = 0.
        var fragments = LayoutThreeItemsWithAlignItems(
            keywordIndex: 13, containerHeight: 200, itemHeight: 250);

        Assert.Equal(3, fragments.Count);
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[2].BlockOffset, precision: 3);
    }

    [Fact]
    public void L3_align_items_unsafe_flex_end_overflow_honors_alignment()
    {
        // Per CSS Box Alignment L3 §5.3 — the `unsafe` modifier
        // honors the natural offset even on overflow.
        //
        // 3 items of height 250 in a 200px container, align-items:
        // `unsafe flex-end` (= keyword index 26). crossSpace = -50 →
        // natural flex-end offset = contentBlockOffset + crossSpace
        // = 0 + (-50) = -50. Items are pushed PAST the container's
        // start edge.
        var fragments = LayoutThreeItemsWithAlignItems(
            keywordIndex: 26, containerHeight: 200, itemHeight: 250);

        Assert.Equal(3, fragments.Count);
        Assert.Equal(-50.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(-50.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(-50.0, fragments[2].BlockOffset, precision: 3);
    }

    [Theory]
    [InlineData(7, 0.0)]    // start → flex-start (LTR row)
    [InlineData(9, 0.0)]    // self-start → flex-start (LTR row)
    [InlineData(8, 150.0)]  // end → flex-end (LTR row)
    [InlineData(10, 150.0)] // self-end → flex-end (LTR row)
    public void L3_align_items_logical_alias_maps_to_physical_for_ltr_row(
        int keywordIndex, double expectedBlockOffset)
    {
        // Per CSS Box Alignment L3 §6.1 + the L4+ writing-mode-aware
        // deferral — the logical-axis aliases `start` / `end` /
        // `self-start` / `self-end` resolve to flex-start / flex-end
        // under the L1 default LTR + `flex-direction: row`.
        //
        // 3 items of height 50 in a 200px container → crossSpace 150.
        // Expected: start / self-start → 0; end / self-end → 150.
        var fragments = LayoutThreeItemsWithAlignItems(
            keywordIndex: keywordIndex, containerHeight: 200, itemHeight: 50);

        Assert.Equal(3, fragments.Count);
        Assert.Equal(expectedBlockOffset, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(expectedBlockOffset, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(expectedBlockOffset, fragments[2].BlockOffset, precision: 3);
    }

    [Fact]
    public void L3_align_items_baseline_falls_back_to_stretch_for_l3()
    {
        // DEFERRAL PIN — per Phase 3 Task 15 L3 LOCKED scope,
        // `baseline` / `first baseline` / `last baseline` require
        // text-shaping integration + are L4+ scope. The L3 decoder
        // maps all three baseline indices (3, 4, 5) to the safe
        // default `stretch`.
        //
        // 3 items with auto height in a 200px container, align-items:
        // baseline (= keyword index 3). Stretch resizes auto-sized
        // items to containerCrossSize = 200; all emit at the
        // container's content-block-start.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Height, 200);
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(3)); // baseline

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            // Leave height auto (= itemCrossSize 0 → stretch resizes
            // to containerCrossSize).
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        // Baseline falls through to stretch — items resize to
        // containerCrossSize + emit at cross-start. Sub-cycle L4+ will
        // implement actual baseline alignment + this test should be
        // updated then.
        foreach (var f in fragments)
        {
            Assert.Equal(0.0, f.BlockOffset, precision: 3);
            Assert.Equal(200.0, f.BlockSize, precision: 3);
        }
    }

    [Fact]
    public void L3_align_items_container_height_auto_uses_max_item_height()
    {
        // Per Phase 3 Task 15 L3 — when the flex container's height
        // is auto (= no LengthPx slot on PropertyId.Height), the
        // containerCrossSize derivation uses max(item natural
        // block-size). This is the spec's max-content cross-size
        // simplification for the L1 default single-line case
        // (CSS Flexbox L1 §9.4).
        //
        // Fixture: 3 items of heights 50/100/75 in a height-auto
        // container, align-items: flex-end (= keyword index 12).
        // containerCrossSize = max(50, 100, 75) = 100.
        // crossSpace per item:
        //   item 0 (h=50)  → 100 - 50 = 50  → flex-end offset 50
        //   item 1 (h=100) → 100 - 100 = 0  → flex-end offset 0
        //   item 2 (h=75)  → 100 - 75 = 25  → flex-end offset 25
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        // Don't set Height — leave it as auto (= the Unset slot).
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(12)); // flex-end

        var heights = new[] { 50.0, 100.0, 75.0 };
        var items = new Box[heights.Length];
        for (var i = 0; i < heights.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, heights[i]);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Per PR #63 Copilot review — derive contentBlockOffset from the
        // flex wrapper's BoxFragment (the container has no border /
        // padding in this fixture, so the wrapper's BlockOffset equals
        // the content-block-start). Pre-fix used `baseOffset =
        // fragments[0].BlockOffset - 50.0` which was tautological:
        // asserting `baseOffset + 50.0 == fragments[0].BlockOffset` is
        // trivially true. The new derivation roots the absolute offset
        // in the wrapper's actual block-start, so the assertions pin
        // both `containerCrossSize = max(50, 100, 75) = 100` AND the
        // per-item flex-end placement against an independent reference.
        BoxFragment? flexWrapper = null;
        var fragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box == flex) flexWrapper = f;
            for (var i = 0; i < items.Length; i++)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.NotNull(flexWrapper);
        Assert.Equal(3, fragments.Count);
        var contentBlockOffset = flexWrapper!.Value.BlockOffset;
        // crossSpace per item = containerCrossSize - itemHeight; flex-
        // end places each item at contentBlockOffset + crossSpace.
        Assert.Equal(contentBlockOffset + 50.0, fragments[0].BlockOffset, precision: 3); // 100 - 50 = 50
        Assert.Equal(contentBlockOffset + 0.0, fragments[1].BlockOffset, precision: 3);  // 100 - 100 = 0
        Assert.Equal(contentBlockOffset + 25.0, fragments[2].BlockOffset, precision: 3); // 100 - 75 = 25
        // Item 1 (the tallest) lands at the cross-start because
        // crossSpace == 0; this independently verifies the
        // containerCrossSize = max(item heights) derivation.
        Assert.Equal(flexWrapper.Value.BlockOffset, fragments[1].BlockOffset, precision: 3);
    }

    // ====================================================================
    //  Phase 3 Task 15 L3 post-PR-#63 review hardening tests
    // ====================================================================

    [Fact]
    public void L3_hardening_flex_wrapper_height_auto_sizes_to_max_item_height()
    {
        // F#1 — when a flex container has height:auto, the wrapper's
        // BoxFragment.BlockSize must reflect the spec-correct
        // single-line cross-extent (= max(item natural cross-size))
        // per CSS Flexbox L1 §9.4 — NOT the block-flow stacking sum.
        //
        // Fixture: 3 items of heights 50/100/75 in a height:auto flex
        // container, align-items: flex-start. Pre-fix the wrapper
        // BlockSize = sum (~225 from MeasureSubtreeVisualBlockExtent's
        // block-flow stacking); post-fix it = max = 100 (the flex line
        // cross-extent).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        // Don't set Height — leave it auto.
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(11)); // flex-start

        var heights = new[] { 50.0, 100.0, 75.0 };
        var items = new Box[heights.Length];
        for (var i = 0; i < heights.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, heights[i]);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? wrapper = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == flex) { wrapper = f; break; }
        }
        Assert.NotNull(wrapper);
        // F#1 — wrapper sizes to max(50, 100, 75) = 100 (the flex
        // line cross-extent), NOT 225 (the block-flow stacking sum).
        Assert.Equal(100.0, wrapper!.Value.BlockSize, precision: 3);
    }

    [Fact]
    public void L3_hardening_flex_wrapper_advances_outer_cursor_by_flex_line_extent()
    {
        // F#1 — a sibling AFTER a height:auto flex container must
        // land at flexWrapper.BlockOffset + flex_line_extent, NOT
        // flexWrapper.BlockOffset + block_flow_stacking_sum. Pre-fix
        // siblings sat ~125px (= 225 - 100) too low because the
        // cursor advance over-reserved space for the flex container.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        // height:auto.
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(11)); // flex-start

        var heights = new[] { 50.0, 100.0, 75.0 };
        for (var i = 0; i < heights.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, heights[i]);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }

        // Sibling AFTER the flex container — a plain block of height 30.
        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Width, 100);
        SetLengthPx(siblingStyle, PropertyId.Height, 30);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());

        root.AppendChild(flex);
        root.AppendChild(sibling);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? wrapper = null;
        BoxFragment? siblingFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == flex) wrapper = f;
            else if (f.Box == sibling) siblingFragment = f;
        }
        Assert.NotNull(wrapper);
        Assert.NotNull(siblingFragment);
        // F#1 — sibling lands directly after the flex line extent
        // (= wrapper BlockOffset + 100). Pre-fix it landed at
        // wrapper BlockOffset + 225.
        Assert.Equal(wrapper!.Value.BlockOffset + 100.0,
            siblingFragment!.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public void L3_hardening_stretch_honors_explicit_zero_height()
    {
        // F#2 — stretch applies only when the item's cross-size
        // property computes to `auto`. Explicit `height: 0` (a
        // LengthPx slot with payload 0) is NOT auto + must keep its
        // declared 0 cross-size. Pre-fix `itemCrossSize > 0` test
        // stretched explicit zeros to containerCrossSize.
        //
        // Fixture: 1 item with explicit height:0 in a 200px stretch
        // container.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Height, 200);
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(1)); // stretch

        var itemStyle = MakeStyle();
        SetLengthPx(itemStyle, PropertyId.Width, 100);
        SetLengthPx(itemStyle, PropertyId.Height, 0); // EXPLICIT 0
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? itemFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == item) { itemFragment = f; break; }
        }
        Assert.NotNull(itemFragment);
        // F#2 — explicit height:0 is honored, NOT stretched to 200.
        Assert.Equal(0.0, itemFragment!.Value.BlockSize, precision: 3);
    }

    [Fact]
    public void L3_hardening_stretch_resizes_auto_height_item()
    {
        // F#2 — stretch applies when the cross-size property computes
        // to `auto`. An item with no `height` declaration (= Unset
        // slot, default auto) is auto + must be stretched to
        // containerCrossSize.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Height, 200);
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(1)); // stretch

        var itemStyle = MakeStyle();
        SetLengthPx(itemStyle, PropertyId.Width, 100);
        // No SetLengthPx for Height — leave it Unset (= default auto).
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? itemFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == item) { itemFragment = f; break; }
        }
        Assert.NotNull(itemFragment);
        // Auto-height item stretched to containerCrossSize.
        Assert.Equal(200.0, itemFragment!.Value.BlockSize, precision: 3);
    }

    [Fact]
    public void L3_hardening_stretch_honors_explicit_positive_height()
    {
        // F#2 — explicit positive heights keep their declared value
        // (the established behavior; pin to ensure the F#2 fix didn't
        // break it).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Height, 200);
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(1)); // stretch

        var itemStyle = MakeStyle();
        SetLengthPx(itemStyle, PropertyId.Width, 100);
        SetLengthPx(itemStyle, PropertyId.Height, 30); // EXPLICIT positive
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? itemFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == item) { itemFragment = f; break; }
        }
        Assert.NotNull(itemFragment);
        // Explicit positive height honored, NOT stretched.
        Assert.Equal(30.0, itemFragment!.Value.BlockSize, precision: 3);
    }

    [Fact]
    public void L3_hardening_known_gap_cross_axis_margins_ignored_in_alignment()
    {
        // F#4 DEFERRAL PIN — per CSS Flexbox L1 §8.4, align-items
        // operates on the item's MARGIN BOX. The L3 ComputeAlignItems
        // Placement currently uses the item's border-box cross-size,
        // ignoring the cross-axis margins entirely. Sub-cycle L4+
        // will refactor to read item margins through FlexLayouter.
        //
        // Fixture: 1 item of height 50 with margin-top: 20px in a 200px
        // container with align-items: flex-start. Spec-correct: item
        // emits at contentBlockOffset + margin-top = 20. Current
        // (buggy): item emits at contentBlockOffset = 0.
        //
        // When sub-cycle L4+ ships the margin-box alignment, this test
        // should FAIL — update to assert 20 + remove the deferral
        // bullet from docs/deferrals.md.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Height, 200);
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(11)); // flex-start

        var itemStyle = MakeStyle();
        SetLengthPx(itemStyle, PropertyId.Width, 100);
        SetLengthPx(itemStyle, PropertyId.Height, 50);
        SetLengthPx(itemStyle, PropertyId.MarginTop, 20); // CROSS-AXIS margin
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? itemFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == item) { itemFragment = f; break; }
        }
        Assert.NotNull(itemFragment);
        // F#4 deferral pin — items emit at contentBlockOffset = 0
        // ignoring margin-top: 20. Spec-correct (sub-cycle L4+) is 20.
        Assert.Equal(0.0, itemFragment!.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public void L3_hardening_known_gap_stretch_ignores_min_max_constraints()
    {
        // F#5 DEFERRAL PIN — per CSS Flexbox L1 §7.2, stretch
        // computes the cross-size with min-height / max-height
        // clamps. The L3 stretch branch currently sets
        // BlockSize = containerCrossSize without consulting min /
        // max constraints. Sub-cycle L4+ will fold them in.
        //
        // Fixture: 1 auto-height item with max-height: 50px in a
        // 200px stretch container. Spec-correct: clamp to 50.
        // Current (buggy): stretched to 200.
        //
        // When sub-cycle L4+ ships the min/max clamping, this test
        // should FAIL — update to assert 50 + remove the deferral
        // bullet from docs/deferrals.md.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Height, 200);
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(1)); // stretch

        var itemStyle = MakeStyle();
        SetLengthPx(itemStyle, PropertyId.Width, 100);
        // No SetLengthPx for Height — leave it auto so stretch activates.
        SetLengthPx(itemStyle, PropertyId.MaxHeight, 50); // should clamp the stretch
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? itemFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == item) { itemFragment = f; break; }
        }
        Assert.NotNull(itemFragment);
        // F#5 deferral pin — stretch ignores max-height: 50,
        // resizes to containerCrossSize = 200. Spec-correct is 50.
        Assert.Equal(200.0, itemFragment!.Value.BlockSize, precision: 3);
    }

    // ====================================================================
    //  Phase 3 Task 15 L4 — flex-direction: column tests
    // ====================================================================
    //
    // FlexDirection keyword indices (KeywordResolver.cs:197):
    //   0=row (L1-L3 default), 1=row-reverse, 2=column (L4 new),
    //   3=column-reverse. L4 ships row + column only; reversed variants
    //   are decoded but defer item-order reversal to L5+.
    //
    // For column direction:
    //   - main axis = block axis (vertical stacking)
    //   - cross axis = inline axis (horizontal placement)
    //   - justify-content controls block-axis positioning
    //   - align-items controls inline-axis positioning

    [Fact]
    public void L4_flex_direction_column_stacks_items_vertically()
    {
        // 3 items of height 50 in a column container of height 300.
        // Items pack along the main axis (= block axis) starting at the
        // content-block-start (= 0). Expected BlockOffsets: 0, 50, 100.
        // The cross axis = inline; each item declares an explicit
        // width: 100 so the L3-default `stretch` align-items DOES NOT
        // resize them (stretch only fires for cross-size-auto items
        // per CSS Flexbox §7.2). Items keep their declared inline-size.
        // Per Phase 3 Task 15 L4 post-PR-#64 review F#5 — corrected
        // from the prior "auto-width items expand to container" wording
        // which contradicted the fixture's explicit widths.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        SetLengthPx(flex.Style, PropertyId.Height, 300);
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Height, 50);
            SetLengthPx(style, PropertyId.Width, 100);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        // Items stack vertically (main = block axis): BlockOffsets
        // 0, 50, 100. Each item keeps its declared block-size 50.
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(50.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(100.0, fragments[2].BlockOffset, precision: 3);
        Assert.Equal(50.0, fragments[0].BlockSize, precision: 3);
        Assert.Equal(50.0, fragments[1].BlockSize, precision: 3);
        Assert.Equal(50.0, fragments[2].BlockSize, precision: 3);
    }

    [Fact]
    public void L4_flex_direction_column_with_justify_content_center()
    {
        // 3 items of height 50 in a column container of height 300.
        // justify-content: center → freeSpace = 300 - 150 = 150;
        // startOffset = 75. Expected BlockOffsets: 75, 125, 175.
        // The cursor axis is block, NOT inline (the L4 axis swap).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        flex.Style.Set(PropertyId.JustifyContent, ComputedSlot.FromKeyword(5)); // center
        SetLengthPx(flex.Style, PropertyId.Height, 300);
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Height, 50);
            SetLengthPx(style, PropertyId.Width, 100);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        // justify-content centers on the MAIN axis (= block for column).
        // BlockOffsets: 75, 125, 175.
        Assert.Equal(75.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(125.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(175.0, fragments[2].BlockOffset, precision: 3);
    }

    [Fact]
    public void L4_flex_direction_column_with_align_items_center()
    {
        // 3 items of width 100 in a column container of width 200.
        // align-items: center on the CROSS axis (= inline for column).
        // crossSpace = 200 - 100 = 100 → InlineOffset = 50 per item.
        // BlockOffsets advance along main axis (= block).
        //
        // The cycle-1 block-flow inline-sizing path inherits the
        // fragmentainer's available range as the wrapper's inline
        // size regardless of the declared `width` — same caveat as
        // L2's LayoutNItemsWithJustifyContent helper. We pass
        // contentInlineSize = 200 in the FragmentainerContext so the
        // wrapper's _contentInlineSize = 200 matches the test intent.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(6)); // center
        SetLengthPx(flex.Style, PropertyId.Height, 300);
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Height, 50);
            SetLengthPx(style, PropertyId.Width, 100);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        // align-items centers on the CROSS axis (= inline for column).
        // crossSpace = 200 - 100 = 100 → InlineOffset 50 per item.
        Assert.Equal(50.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(50.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(50.0, fragments[2].InlineOffset, precision: 3);
        // Items keep declared inline-size 100 (positional alignment
        // never resizes).
        Assert.Equal(100.0, fragments[0].InlineSize, precision: 3);
        // BlockOffsets advance along main axis: 0, 50, 100.
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(50.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(100.0, fragments[2].BlockOffset, precision: 3);
    }

    [Fact]
    public void L4_flex_direction_column_stretch_resizes_items_to_container_inline_size()
    {
        // Column direction with align-items: stretch. Items with no
        // explicit width get resized to the container's cross extent
        // (= inline size 200 here). Mirrors the L3 stretch test but
        // along the inline axis instead of the block axis.
        //
        // Same caveat as the align-items-center test: the block-flow
        // inline-sizing inherits the fragmentainer's contentInlineSize
        // as the wrapper's inline-size; we pass 200 in the context.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(1)); // stretch
        SetLengthPx(flex.Style, PropertyId.Height, 300);
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Height, 50);
            // Items 0 + 2 leave width auto → stretch resizes to 200.
            // Item 1 declares explicit width 80 → kept at 80.
            if (i == 1) SetLengthPx(style, PropertyId.Width, 80);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        // Auto-width items stretched to container's inline cross extent (200).
        Assert.Equal(200.0, fragments[0].InlineSize, precision: 3);
        // Explicit-width item kept at declared value.
        Assert.Equal(80.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[2].InlineSize, precision: 3);
        // All items start at cross-start (= contentInlineOffset = 0).
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L4_flex_direction_column_with_space_between_distributes_block_axis()
    {
        // 3 items of height 50 in a column container of height 400.
        // justify-content: space-between (on main = block axis).
        // freeSpace = 400 - 150 = 250; betweenSpacing = 250 / 2 = 125.
        // BlockOffsets: 0, 50 + 125 = 175, 50 + 125 + 50 + 125 = 350.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        flex.Style.Set(PropertyId.JustifyContent, ComputedSlot.FromKeyword(1)); // space-between
        SetLengthPx(flex.Style, PropertyId.Height, 400);
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Height, 50);
            SetLengthPx(style, PropertyId.Width, 100);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        // space-between distributes on the MAIN axis (= block for column).
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(175.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(350.0, fragments[2].BlockOffset, precision: 3);
    }

    [Fact]
    public void L4_flex_direction_row_baseline_preserved()
    {
        // Sanity test: row direction (default; keyword index 0) still
        // produces the same offsets as before the L4 axis-mapping
        // refactor. 3 items of width 50 in a 400px container with no
        // justify-content keyword → flex-start packing. Expected
        // InlineOffsets: 0, 50, 100.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        // Default flex-direction is row; set explicitly to pin the test.
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(0)); // row
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 50);
            SetLengthPx(style, PropertyId.Height, 50);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        // Row direction baseline — InlineOffsets advance along main
        // axis (= inline for row): 0, 50, 100.
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(50.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[2].InlineOffset, precision: 3);
        // BlockOffset shared (= contentBlockOffset; align-items default
        // is stretch but items have explicit height so they keep 50).
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[2].BlockOffset, precision: 3);
    }

    /// <summary>L3 helper — drive FlexLayouter with 3 identical items
    /// + the requested align-items keyword. Returns the per-item
    /// fragments in source order.
    ///
    /// <para>The flex container has an explicit height of
    /// <paramref name="containerHeight"/> so the L3
    /// <c>containerCrossSize</c> derivation reads the explicit slot
    /// directly (= the LengthPx branch, not the height-auto max-item
    /// fallback). Items get an explicit <paramref name="itemHeight"/>
    /// so the stretch override doesn't activate + positional alignment
    /// has a deterministic cross-space.</para></summary>
    private static List<BoxFragment> LayoutThreeItemsWithAlignItems(
        int keywordIndex, double containerHeight, double itemHeight)
    {
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Height, containerHeight);
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(keywordIndex));

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, itemHeight);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        // contentInlineSize 400 keeps the items fitting on the main
        // axis (3 * 100 = 300 ≤ 400) so the alignment math is decoupled
        // from any inline-axis overflow.
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var itemFragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i])
                {
                    itemFragments.Add(f);
                    break;
                }
            }
        }
        return itemFragments;
    }

    // ====================================================================
    //  Phase 3 Task 15 L4 post-PR-#64 review hardening tests
    // ====================================================================

    [Fact]
    public void L4_hardening_column_wrapper_height_auto_sums_item_block_sizes()
    {
        // F#1 — when a column-direction flex container has height:auto,
        // the wrapper's BoxFragment.BlockSize must reflect the
        // spec-correct main-axis content extent = SUM of item block-
        // sizes (CSS Flexbox L1 §9.4 — max-content main-size for an
        // auto-main-size single-line container is the sum of item
        // hypothetical main-sizes).
        //
        // Fixture: 3 items of heights 50/100/75 in a height:auto column
        // container. Pre-F#1 the column path skipped PreMeasureFlex*
        // entirely, leaving the wrapper at the auto-resolved fallback
        // (often 0 or a 1px clamp via the fragmentainer-remainder
        // path); post-F#1 the wrapper paints at the spec sum = 225.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        // Leave Height auto.
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var heights = new[] { 50.0, 100.0, 75.0 };
        var items = new Box[heights.Length];
        for (var i = 0; i < heights.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, heights[i]);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? wrapper = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == flex) { wrapper = f; break; }
        }
        Assert.NotNull(wrapper);
        // F#1 — wrapper sizes to SUM(50, 100, 75) = 225 (the column
        // main-axis content extent), NOT 0 / 1 (the pre-fix
        // auto-resolved fallback) or 100 (max — that's the row-direction
        // cross-extent).
        Assert.Equal(225.0, wrapper!.Value.BlockSize, precision: 3);
    }

    [Fact]
    public void L4_hardening_column_justify_content_center_with_auto_height()
    {
        // F#1 — with the F#1 fix, a height:auto column container has
        // its borderBoxBlockSize correctly grown to sum(items). The
        // FlexLayouter then sees containerMainSize = sum =
        // freeSpace = 0; justify-content: center → startOffset = 0;
        // items land at BlockOffsets 0, 50, 100 (no overflow → no
        // negative offsets).
        //
        // Pre-F#1: the wrapper was tiny (auto fallback), freeSpace
        // was hugely negative, items emitted at strange offsets
        // (positional-value overflow per CSS Box Alignment §5.3
        // still produces negative offsets when freeSpace < 0 + the
        // overflow mode is `default`).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        flex.Style.Set(PropertyId.JustifyContent, ComputedSlot.FromKeyword(5)); // center
        // Leave Height auto.
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Height, 50);
            SetLengthPx(style, PropertyId.Width, 100);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? wrapper = null;
        var fragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box == flex) wrapper = f;
            for (var i = 0; i < items.Length; i++)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.NotNull(wrapper);
        Assert.Equal(3, fragments.Count);
        // F#1 — with the F#1 fix the wrapper grows to sum = 150
        // (the container's content-block-size); freeSpace = 0;
        // startOffset = 0 → items at 0/50/100 (no negative offsets,
        // no overflow).
        Assert.Equal(150.0, wrapper!.Value.BlockSize, precision: 3);
        var wrapperTop = wrapper.Value.BlockOffset;
        Assert.Equal(wrapperTop + 0.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(wrapperTop + 50.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(wrapperTop + 100.0, fragments[2].BlockOffset, precision: 3);
    }

    [Fact]
    public void L4_hardening_column_sibling_lands_after_full_column_stack()
    {
        // F#1 — a sibling block AFTER a height:auto column flex
        // container must land at wrapper.BlockOffset + sum(items),
        // NOT at some other value derived from the pre-F#1 fallback.
        // Mirrors the L3 row-direction sibling test but along the
        // column main axis.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        // height:auto.
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var heights = new[] { 50.0, 100.0, 75.0 };
        for (var i = 0; i < heights.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, heights[i]);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }

        // Sibling AFTER the flex container — a plain block of height 30.
        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Width, 100);
        SetLengthPx(siblingStyle, PropertyId.Height, 30);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());

        root.AppendChild(flex);
        root.AppendChild(sibling);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? wrapper = null;
        BoxFragment? siblingFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == flex) wrapper = f;
            else if (f.Box == sibling) siblingFragment = f;
        }
        Assert.NotNull(wrapper);
        Assert.NotNull(siblingFragment);
        // F#1 — sibling lands directly after the column main-axis
        // content sum (= wrapper BlockOffset + 225 = wrapper bottom
        // edge given no padding/border). Pre-F#1 it landed at
        // wrapper BlockOffset + the auto-resolved fallback (usually
        // 0 / 1px), leaving the column items visually overlapping
        // the sibling.
        Assert.Equal(wrapper!.Value.BlockOffset + 225.0,
            siblingFragment!.Value.BlockOffset, precision: 3);
    }

    [Fact(Skip =
        "Phase 3 Task 15 L4 post-PR-#64 review F#2 — explicit-width "
        + "honoring for flex containers requires the BlockLayouter "
        + "width-resolution pipeline to honor declared `width` as a "
        + "shrink-to-fit constraint (cycle-1 BlockLayouter derives "
        + "borderBoxInlineSize from the available inline range + "
        + "ignores declared width). The fix is out of L4 hardening "
        + "scope; tracked as the BlockLayouter-flex-explicit-width "
        + "Missing bullet under docs/deferrals.md#flex-layouter-features. "
        + "When that gap is closed, remove the Skip + the assertion "
        + "below verifies items center against the declared 200px "
        + "container (not the 600px page).")]
    public void L4_hardening_column_explicit_width_smaller_than_page_centers_correctly()
    {
        // F#2 — page = 600px wide. Flex container has explicit
        // width:200 + flex-direction:column + align-items:center.
        // Item is width:100. Expected: item centered in the
        // 200-px container at InlineOffset = (200 - 100) / 2 = 50.
        // (NOT (600 - 100) / 2 = 250, which would mean the layouter
        // ignored the declared width.)
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(6)); // center
        SetLengthPx(flex.Style, PropertyId.Height, 300);
        SetLengthPx(flex.Style, PropertyId.Width, 200); // explicit

        var itemStyle = MakeStyle();
        SetLengthPx(itemStyle, PropertyId.Width, 100);
        SetLengthPx(itemStyle, PropertyId.Height, 50);
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        // Page-sized fragmentainer — NOT matched to the flex's
        // declared width.
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? itemFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == item) { itemFragment = f; break; }
        }
        Assert.NotNull(itemFragment);
        // F#2 expected: item centered in the 200-px declared width.
        Assert.Equal(50.0, itemFragment!.Value.InlineOffset, precision: 3);
    }

    [Fact]
    public void L4_hardening_known_gap_column_flex_ignores_declared_width()
    {
        // F#2 known-gap pin — paired with the Skip'd test above.
        // Documents the CURRENT (incomplete) behavior: a column flex
        // container with width:200 in a 600px page treats
        // _contentInlineSize as the full page width, so align-items:
        // center centers items against 600 (= page) instead of 200
        // (= declared width). Reviewer-flagged at PR #64.
        //
        // When the BlockLayouter width-resolution fix lands + the
        // Skip'd "smaller_than_page_centers_correctly" test starts
        // passing, this test will start failing — at which point
        // remove BOTH this pin AND the matching Missing bullet in
        // docs/deferrals.md.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(6)); // center
        SetLengthPx(flex.Style, PropertyId.Height, 300);
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var itemStyle = MakeStyle();
        SetLengthPx(itemStyle, PropertyId.Width, 100);
        SetLengthPx(itemStyle, PropertyId.Height, 50);
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? itemFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == item) { itemFragment = f; break; }
        }
        Assert.NotNull(itemFragment);
        // Known-gap — item centered against the 600px page, NOT
        // against the declared 200px container width. Spec-correct
        // is 50; current is 250.
        Assert.Equal(250.0, itemFragment!.Value.InlineOffset, precision: 3);
    }

    [Fact]
    public void L4_hardening_row_stretch_does_not_treat_percentage_height_as_auto()
    {
        // F#3 — `height: 50%` is an EXPLICIT cross-size declaration
        // for a row-direction flex item. Per CSS Flexbox §7.2 stretch
        // applies only when the cross-size property computes to
        // `auto`; percentages are NOT auto. Pre-F#3 the check was
        // `slot.Tag != ComputedSlotTag.LengthPx`, which incorrectly
        // treated Percentage as auto + stretched the item. Post-F#3
        // the check is `slot.Tag is Unset or Keyword`, matching the
        // canonical IsHeightAuto predicate.
        //
        // The cycle-1 LengthResolver returns 0 for percentages via
        // ReadLengthPxOrZero (= "percentage cross-size not yet
        // resolved" is its own gap), so the item's recorded
        // BlockSize is the declared-but-unresolved 0 — NOT the
        // stretched 200. The pin is that BlockSize != 200 (stretch
        // didn't override the declaration).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        // Row direction (default); explicit height = 200 so
        // containerCrossSize = 200.
        SetLengthPx(flex.Style, PropertyId.Height, 200);
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(1)); // stretch

        var itemStyle = MakeStyle();
        SetLengthPx(itemStyle, PropertyId.Width, 100);
        // EXPLICIT percentage cross-size — Percentage tag, NOT
        // LengthPx, NOT Unset, NOT Keyword.
        itemStyle.Set(PropertyId.Height, ComputedSlot.FromPercentage(50.0));
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? itemFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == item) { itemFragment = f; break; }
        }
        Assert.NotNull(itemFragment);
        // F#3 — explicit percentage is honored; stretch does NOT
        // resize to 200 (the containerCrossSize). The cycle-1
        // ReadLengthPxOrZero resolves percentage to 0 — that's a
        // separate gap; the F#3 pin is that the slot tag's
        // explicit-declaration status is respected.
        Assert.NotEqual(200.0, itemFragment!.Value.BlockSize);
    }

    [Fact]
    public void L4_hardening_column_stretch_does_not_treat_percentage_width_as_auto()
    {
        // F#3 — column-direction equivalent. A `width: 50%` item in
        // a column container with align-items: stretch must NOT be
        // stretched to containerCrossSize.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(1)); // stretch
        SetLengthPx(flex.Style, PropertyId.Height, 300);
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var itemStyle = MakeStyle();
        SetLengthPx(itemStyle, PropertyId.Height, 50);
        // EXPLICIT percentage cross-size.
        itemStyle.Set(PropertyId.Width, ComputedSlot.FromPercentage(50.0));
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? itemFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == item) { itemFragment = f; break; }
        }
        Assert.NotNull(itemFragment);
        // F#3 — explicit percentage is honored; stretch does NOT
        // resize the item to 200 (containerCrossSize).
        Assert.NotEqual(200.0, itemFragment!.Value.InlineSize);
    }

    // ====================================================================
    //  Phase 3 Task 15 L5 — flex-direction: row-reverse + column-reverse
    // ====================================================================

    [Fact]
    public void L5_flex_direction_row_reverse_with_flex_start_packs_at_right_edge()
    {
        // Per CSS Flexbox L1 §5.1 — `row-reverse` swaps main-start and
        // main-end. `justify-content: flex-start` (default) packs items
        // at the new main-start = the right edge of the container. So
        // for 3 items of width 50 in a 400px container: items pack
        // visually as DOM-2, DOM-1, DOM-0 against the right edge.
        //   - DOM 0 (item-a): InlineOffset = 350 (right-most)
        //   - DOM 1 (item-b): InlineOffset = 300
        //   - DOM 2 (item-c): InlineOffset = 250
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(1)); // row-reverse
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 50);
            SetLengthPx(style, PropertyId.Height, 50);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        // DOM 0 lands at the right edge; DOM 2 lands at the left of the
        // packed cluster. Visual order from the original main-start
        // (= new main-end at left) to original main-end (= new main-
        // start at right): item-c, item-b, item-a.
        Assert.Equal(350.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(300.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(250.0, fragments[2].InlineOffset, precision: 3);
        // Items keep declared inline-size 50.
        Assert.Equal(50.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(50.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(50.0, fragments[2].InlineSize, precision: 3);
    }

    [Fact]
    public void L5_flex_direction_row_reverse_with_justify_content_center_centers_cluster()
    {
        // 3 items width 50 in 400px container with row-reverse +
        // justify-content: center. freeSpace = 250, startOffset = 125.
        // Non-reverse cursor walks DOM 0 → 125, DOM 1 → 175, DOM 2 → 225.
        // Reverse flip: actualOffset = 400 - natural - 50.
        //   - DOM 0 (item-a): 400 - 125 - 50 = 225 (right of cluster)
        //   - DOM 1 (item-b): 400 - 175 - 50 = 175 (middle)
        //   - DOM 2 (item-c): 400 - 225 - 50 = 125 (left of cluster)
        // Visually centered [125..275], reversed DOM order.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(1)); // row-reverse
        flex.Style.Set(PropertyId.JustifyContent, ComputedSlot.FromKeyword(5)); // center
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 50);
            SetLengthPx(style, PropertyId.Height, 50);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        Assert.Equal(225.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(175.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(125.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L5_flex_direction_column_reverse_with_flex_start_packs_at_bottom()
    {
        // Column-reverse — main-start moves to the bottom edge.
        // 3 items of height 50 in a 400px-tall container, justify-content:
        // flex-start (default). Items pack at the bottom edge in reverse
        // DOM order.
        //   - DOM 0 (item-a): BlockOffset = 350 (bottom-most)
        //   - DOM 1 (item-b): BlockOffset = 300
        //   - DOM 2 (item-c): BlockOffset = 250
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(3)); // column-reverse
        SetLengthPx(flex.Style, PropertyId.Height, 400);
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Height, 50);
            SetLengthPx(style, PropertyId.Width, 100);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        // Items pack at the BOTTOM edge in reverse DOM order.
        Assert.Equal(350.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(300.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(250.0, fragments[2].BlockOffset, precision: 3);
        // Items keep declared block-size 50.
        Assert.Equal(50.0, fragments[0].BlockSize, precision: 3);
    }

    [Fact]
    public void L5_flex_direction_column_reverse_with_justify_content_center_centers_cluster()
    {
        // 3 items height 50 in 400px container with column-reverse +
        // justify-content: center. Natural startOffset = 125.
        //   - DOM 0: 400 - 125 - 50 = 225
        //   - DOM 1: 400 - 175 - 50 = 175
        //   - DOM 2: 400 - 225 - 50 = 125
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(3)); // column-reverse
        flex.Style.Set(PropertyId.JustifyContent, ComputedSlot.FromKeyword(5)); // center
        SetLengthPx(flex.Style, PropertyId.Height, 400);
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Height, 50);
            SetLengthPx(style, PropertyId.Width, 100);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        Assert.Equal(225.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(175.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(125.0, fragments[2].BlockOffset, precision: 3);
    }

    [Fact]
    public void L5_flex_direction_row_reverse_preserves_cross_axis_alignment()
    {
        // L5 reversal applies ONLY to the main axis. align-items on the
        // CROSS axis (= block for row-reverse) must be unchanged. With
        // 3 items height 30, container height 100, align-items: center,
        // crossSpace = 100 - 30 = 70 → BlockOffset = 35 per item.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(1)); // row-reverse
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(6)); // center
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 50);
            SetLengthPx(style, PropertyId.Height, 30);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        // Cross axis (block) is unchanged — center alignment.
        Assert.Equal(35.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(35.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(35.0, fragments[2].BlockOffset, precision: 3);
        // Main axis (inline) IS reversed: DOM 0 packs at right, DOM 2
        // at left of the cluster.
        Assert.Equal(350.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(300.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(250.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L5_flex_direction_row_baseline_unchanged_for_non_reverse()
    {
        // Sanity — non-reverse row still produces 0, 50, 100 offsets.
        // Pins that the L5 offset-flip is transparent for non-reverse
        // directions.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(0)); // row (explicit)
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 50);
            SetLengthPx(style, PropertyId.Height, 50);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(50.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L5_flex_direction_column_baseline_unchanged_for_non_reverse()
    {
        // Sanity — non-reverse column still produces 0, 50, 100
        // BlockOffsets. Pins that the L5 offset-flip is transparent for
        // non-reverse directions.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        SetLengthPx(flex.Style, PropertyId.Height, 300);
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Height, 50);
            SetLengthPx(style, PropertyId.Width, 100);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(50.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(100.0, fragments[2].BlockOffset, precision: 3);
    }

    // ====================================================================
    //  Phase 3 Task 15 L5 post-PR-#65 review hardening
    // ====================================================================

    // -- F#1 (P2) — direction/writing-mode pipeline known gap -----------

    [Fact(Skip = "PR 2 task 6 — FlexLayouter has not yet ADOPTED the direction pipeline; the shared reader " +
        "(DirectionStyleExtensions.ReadDirection, added in task 4) exists, but the RTL row → main-axis flip is the " +
        "remaining work. Tracked in flex-layouter-features deferral.")]
    public void L5_known_gap_rtl_row_should_flip_main_axis_pending_flex_direction_adoption()
    {
        // Per CSS Flexbox §3.1 axis-mapping: `row` in RTL writing mode
        // means right-to-left (= same physical layout as `row-reverse`
        // in LTR). With L5's LTR-only main-axis mapping, an RTL
        // container with `flex-direction: row` would still emit items
        // left-to-right (= InlineOffsets 0/50/100). When task 6 wires
        // FlexLayouter to the direction pipeline, this test will pin the
        // spec-correct behavior: items emitted in physical right-to-left
        // order so DOM 0 lands at the right edge (= InlineOffset 350 for
        // 3×50 items in 400px), DOM 1 at 300, DOM 2 at 250 — matching the
        // current `row-reverse` LTR output for the same fixture.
        //
        // When task 6 lands (the pipeline ALREADY exists post-task-4):
        //   1. Read the cascaded `direction` via DirectionStyleExtensions.
        //      ReadDirection / IsRtl inside FlexLayouter.
        //   2. The axis mapping switches from "row → inline left-to-
        //      right" to "row → inline right-to-left" under RTL.
        //   3. Drop this test's [Skip] + assert spec-correct offsets.
        //   4. Remove the corresponding Missing bullet in
        //      docs/deferrals.md#flex-layouter-features.
    }

    // -- F#3 (P2) — reverse alignment matrix hardening ------------------

    [Fact]
    public void L5_hardening_row_reverse_with_flex_end_packs_at_left_edge()
    {
        // Per CSS Flexbox L1 §5.1 + CSS Box Alignment L3 §4.5 —
        // `row-reverse` + `flex-end`: for 3×50px items in 400px,
        // non-reverse flex-end positions the cluster at the right:
        // cursor walks 250/300/350. The L5 flip transform
        //   mainOffsetForEmission = (contentMainOffset + containerMainSize)
        //                         - (mainCursor - contentMainOffset)
        //                         - itemMainSize
        // produces:
        //   - DOM 0: 0 + 400 - (250 - 0) - 50 = 100
        //   - DOM 1: 0 + 400 - (300 - 0) - 50 = 50
        //   - DOM 2: 0 + 400 - (350 - 0) - 50 = 0
        // Items pack against the LEFT edge (= the reversed main-end,
        // which is what `flex-end` targets under row-reverse) in
        // reverse DOM order.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(1)); // row-reverse
        flex.Style.Set(PropertyId.JustifyContent, ComputedSlot.FromKeyword(9)); // flex-end
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 50);
            SetLengthPx(style, PropertyId.Height, 50);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        Assert.Equal(100.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(50.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L5_hardening_row_reverse_with_space_between_distributes_correctly()
    {
        // 3×50px items in 400px with `row-reverse` + `space-between`.
        // freeSpace = 250; non-reverse cursor walks 0/175/350 (gap =
        // 250/(3-1) = 125; between-spacing applied AFTER each item).
        //   - DOM 0 cursor: 0
        //   - DOM 1 cursor: 0 + 50 + 125 = 175
        //   - DOM 2 cursor: 175 + 50 + 125 = 350
        // Apply the L5 flip transform:
        //   - DOM 0: 0 + 400 - 0 - 50 = 350
        //   - DOM 1: 0 + 400 - 175 - 50 = 175
        //   - DOM 2: 0 + 400 - 350 - 50 = 0
        // Visually [0, 175, 350] from left to right with 125px gaps;
        // DOM order is reversed (DOM 2 at left edge, DOM 0 at right).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(1)); // row-reverse
        flex.Style.Set(PropertyId.JustifyContent, ComputedSlot.FromKeyword(1)); // space-between
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 50);
            SetLengthPx(style, PropertyId.Height, 50);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        Assert.Equal(350.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(175.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L5_hardening_row_reverse_with_unsafe_flex_end_overflow_honors_alignment()
    {
        // 5×100px items (total 500) in 400px container = overflow
        // (freeSpace = -100). With `row-reverse` + `unsafe flex-end`
        // (= keyword index 23; unsafe + flex-end): per CSS Box
        // Alignment L3 §5.3 unsafe honors the alignment even on
        // overflow. Non-reverse cursor walks (-100, 0, 100, 200, 300)
        // because flex-end naturally starts at freeSpace = -100.
        // Apply the L5 flip transform:
        //   - DOM 0: 0 + 400 - (-100) - 100 = 400
        //   - DOM 1: 0 + 400 - 0 - 100 = 300
        //   - DOM 2: 0 + 400 - 100 - 100 = 200
        //   - DOM 3: 0 + 400 - 200 - 100 = 100
        //   - DOM 4: 0 + 400 - 300 - 100 = 0
        // Items at 400/300/200/100/0 — fully overflowing past the
        // container's right edge (= old main-end), in reverse DOM
        // order. Pinning that unsafe + overflow + reverse compose
        // correctly.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(1)); // row-reverse
        // Keyword index 23 = unsafe flex-end (per
        // KeywordResolver.BuildJustifyContentTable: indices 19-25 are
        // unsafe + position; 19=unsafe center, 20=unsafe start,
        // 21=unsafe end, 22=unsafe flex-start, 23=unsafe flex-end).
        flex.Style.Set(PropertyId.JustifyContent, ComputedSlot.FromKeyword(23)); // unsafe flex-end
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[5];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            // Per Phase 3 Task 15 L8 — pin `flex-shrink: 0` so items
            // overflow at their declared widths instead of shrinking
            // to fit. This test pre-dates L8 and exercises overflow
            // semantics; the L8 §9.7 shrink resolution would otherwise
            // absorb the -100 free space, defeating the overflow
            // assertion.
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(5, fragments.Count);
        Assert.Equal(400.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(300.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[2].InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[3].InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[4].InlineOffset, precision: 3);
    }

    [Fact]
    public void L5_hardening_column_reverse_with_flex_end_packs_at_top()
    {
        // Column-reverse + flex-end equivalent of the row-reverse +
        // flex-end test. 3×50px items in a 400px-tall column-reverse
        // container with `justify-content: flex-end`. Non-reverse
        // cursor (under column direction) walks 250/300/350 along the
        // block axis. Apply the L5 flip transform:
        //   - DOM 0: 0 + 400 - 250 - 50 = 100
        //   - DOM 1: 0 + 400 - 300 - 50 = 50
        //   - DOM 2: 0 + 400 - 350 - 50 = 0
        // Items pack against the TOP edge (= the reversed main-end
        // under column-reverse) in reverse DOM order.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(3)); // column-reverse
        flex.Style.Set(PropertyId.JustifyContent, ComputedSlot.FromKeyword(9)); // flex-end
        SetLengthPx(flex.Style, PropertyId.Height, 400);
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Height, 50);
            SetLengthPx(style, PropertyId.Width, 100);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        Assert.Equal(100.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(50.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[2].BlockOffset, precision: 3);
    }

    [Fact]
    public void L5_hardening_row_reverse_with_varied_item_sizes()
    {
        // Verify the flip transform on varied (non-uniform) item
        // widths. 3 items of widths 30/80/50 (total 160) in 400px with
        // `row-reverse` + `flex-start`. freeSpace = 240; startOffset =
        // 0; non-reverse cursor walks:
        //   - DOM 0 cursor: 0
        //   - DOM 1 cursor: 0 + 30 = 30
        //   - DOM 2 cursor: 30 + 80 = 110
        // Apply the L5 flip transform with each item's own width:
        //   - DOM 0: 0 + 400 - 0 - 30 = 370
        //   - DOM 1: 0 + 400 - 30 - 80 = 290
        //   - DOM 2: 0 + 400 - 110 - 50 = 240
        // Sizes preserved (30/80/50); reverse DOM order along main
        // axis. Pinning that the flip uses each item's own size, not
        // a uniform value.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(1)); // row-reverse
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var widths = new double[] { 30, 80, 50 };
        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, widths[i]);
            SetLengthPx(style, PropertyId.Height, 50);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        Assert.Equal(370.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(290.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(240.0, fragments[2].InlineOffset, precision: 3);
        // Sizes preserved.
        Assert.Equal(30.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(80.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(50.0, fragments[2].InlineSize, precision: 3);
    }

    // ====================================================================
    //  Phase 3 Task 15 L6 — flex-wrap: wrap (multi-line layout).
    // ====================================================================
    // FlexWrap keyword indices (KeywordResolver.cs:198):
    //   0=nowrap, 1=wrap, 2=wrap-reverse.

    [Fact]
    public void L6_flex_wrap_packs_items_into_two_lines_when_natural_widths_exceed_container()
    {
        // 4 items of width 100 in a 250px-wide flex container with
        // `flex-wrap: wrap`. Per CSS Flexbox L1 §9.3 greedy line packing:
        //   - Line 1: items 0+1 (200px); item 2 would push to 300 > 250
        //     so wrap.
        //   - Line 2: items 2+3 (200px).
        // Line 1's cross-extent = max(item block-size) = 50 (each item
        // is 100x50). Line 2 lands at BlockOffset = 50.
        //
        // Per Phase 3 Task 15 L7 — explicit `align-content: flex-start`
        // pins the L1-L6 natural-stacking behavior (lines packed at
        // cross-start with no inter-line distribution). The §8.4
        // default `align-content: normal` resolves to `stretch` which
        // would grow each line to 100 (= half of 200), shifting line 2
        // to BlockOffset 100; the L7 stretch behavior is covered by
        // the dedicated `L7_align_content_*` tests above.
        //
        // The fragmentainer's contentInlineSize is set to 250 so the
        // wrapper's _contentInlineSize matches the declared width.
        // BlockLayouter does NOT yet honor declared `width` as a
        // shrink-to-fit constraint (= the L4 deferral pinned at
        // `L4_hardening_known_gap_column_flex_ignores_declared_width`),
        // so to exercise the wrap-budget = 250 case the fragmentainer
        // must already be 250.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(11)); // flex-start (pin L6 behavior)
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var items = new Box[4];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(4, fragments.Count);
        // Line 1: items 0 + 1 at InlineOffset 0, 100; BlockOffset 0.
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[1].BlockOffset, precision: 3);
        // Line 2: items 2 + 3 at InlineOffset 0, 100; BlockOffset 50
        // (= line 1's cross-extent).
        Assert.Equal(0.0, fragments[2].InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[3].InlineOffset, precision: 3);
        Assert.Equal(50.0, fragments[2].BlockOffset, precision: 3);
        Assert.Equal(50.0, fragments[3].BlockOffset, precision: 3);
    }

    [Fact]
    public void L6_flex_wrap_three_items_per_line_with_remainder()
    {
        // 7 items of width 80 in a 250px container, `flex-wrap: wrap`.
        // 3 items per line fit (240 <= 250); 4 wouldn't (320 > 250).
        // Lines: [0,1,2] = 240px, [3,4,5] = 240px, [6] = 80px.
        // Cross-extent per line = 30.
        // Per L7 — pin L6 natural-stacking behavior via explicit
        // `align-content: flex-start`; the §8.4 default `normal` →
        // stretch would grow each of the 3 lines by 70 (= 210/3)
        // shifting line 2 to BlockOffset 100, line 3 to 200. See
        // dedicated `L7_align_content_stretch_*` test for stretch.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(11)); // flex-start (pin L6 behavior)
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 300);

        var items = new Box[7];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 80);
            SetLengthPx(style, PropertyId.Height, 30);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        // Match fragmentainer.contentInlineSize to declared width (see
        // L4 deferral comment in `L6_flex_wrap_packs_items_into_two_lines_when_natural_widths_exceed_container`).
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(7, fragments.Count);
        // Line 1 (items 0-2): InlineOffset 0/80/160; BlockOffset 0.
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(80.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(160.0, fragments[2].InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[2].BlockOffset, precision: 3);
        // Line 2 (items 3-5): InlineOffset 0/80/160; BlockOffset 30.
        Assert.Equal(0.0, fragments[3].InlineOffset, precision: 3);
        Assert.Equal(80.0, fragments[4].InlineOffset, precision: 3);
        Assert.Equal(160.0, fragments[5].InlineOffset, precision: 3);
        Assert.Equal(30.0, fragments[3].BlockOffset, precision: 3);
        Assert.Equal(30.0, fragments[4].BlockOffset, precision: 3);
        Assert.Equal(30.0, fragments[5].BlockOffset, precision: 3);
        // Line 3 (item 6): InlineOffset 0; BlockOffset 60.
        Assert.Equal(0.0, fragments[6].InlineOffset, precision: 3);
        Assert.Equal(60.0, fragments[6].BlockOffset, precision: 3);
    }

    [Fact]
    public void L6_flex_wrap_single_oversized_item_still_emits_on_its_line()
    {
        // 1 item of width 500 in a 100px container, `flex-wrap: wrap`.
        // Per CSS Flexbox L1 §9.3 "if the very first uncollected item
        // wouldn't fit, collect just it into the line" — the oversized
        // item lands alone + overflows. Verify it emits at offset 0
        // with its declared 500 width.
        //
        // Fragmentainer.contentInlineSize matches the declared width
        // (see deferral comment in
        // `L6_flex_wrap_packs_items_into_two_lines_when_natural_widths_exceed_container`).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        SetLengthPx(flex.Style, PropertyId.Width, 100);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var itemStyle = MakeStyle();
        SetLengthPx(itemStyle, PropertyId.Width, 500);
        SetLengthPx(itemStyle, PropertyId.Height, 50);
        // Per Phase 3 Task 15 L8 — pin `flex-shrink: 0` so the
        // oversized item retains its 500px declared width instead of
        // shrinking to fit the 100px container. The §9.3 "single
        // oversized item lands alone + overflows" rule depends on the
        // item NOT shrinking; with the L8 default `flex-shrink: 1` the
        // item would shrink to 100px and the test's "overflow per
        // §9.3" assertion (InlineSize == 500) would fail.
        itemStyle.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        // 100px fragmentainer matches the declared 100px width — see
        // the L4 deferral comment above.
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? itemFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == item) { itemFragment = f; break; }
        }
        Assert.NotNull(itemFragment);
        Assert.Equal(0.0, itemFragment!.Value.InlineOffset, precision: 3);
        Assert.Equal(500.0, itemFragment!.Value.InlineSize, precision: 3);
    }

    [Fact]
    public void L6_flex_wrap_with_justify_content_center_centers_each_line()
    {
        // 4 items of width 100 in a 250px container, `flex-wrap: wrap`
        // + `justify-content: center`. Lines: [0,1]+[2,3]; each line's
        // main-size = 200; freeSpace = 50; startOffset = 25.
        // Items per line at InlineOffset 25, 125.
        //
        // Fragmentainer.contentInlineSize matches declared width (see
        // L4 deferral comment above).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        flex.Style.Set(PropertyId.JustifyContent, ComputedSlot.FromKeyword(5)); // center
        // Per L7 — pin L6 natural-stacking behavior via explicit
        // `align-content: flex-start` (the test exercises per-line
        // justify-content; the default §8.4 stretch would expand
        // lines + shift line 2 to BlockOffset 100, conflating the
        // two alignment axes).
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var items = new Box[4];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(4, fragments.Count);
        // Each line centers at startOffset 25; items 0+1 at 25, 125
        // (line 1, BlockOffset 0); items 2+3 at 25, 125 (line 2,
        // BlockOffset 50).
        Assert.Equal(25.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(125.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(25.0, fragments[2].InlineOffset, precision: 3);
        Assert.Equal(125.0, fragments[3].InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(50.0, fragments[2].BlockOffset, precision: 3);
        Assert.Equal(50.0, fragments[3].BlockOffset, precision: 3);
    }

    [Fact]
    public void L6_flex_wrap_container_auto_height_sums_line_extents()
    {
        // 4 items 100x50 in a 250px-wide auto-height (no explicit
        // height) flex container, `flex-wrap: wrap`. Per CSS Flexbox L1
        // §9.4 the container's auto cross-size = sum of line cross-
        // extents. Lines: [0,1] + [2,3]; each line cross = 50; sum =
        // 100. The wrapper fragment's BlockSize should reflect 100 (=
        // the BlockLayouter pre-measure grew it via
        // PreMeasureFlexMultiLineCrossExtent).
        //
        // Fragmentainer matches declared width (see L4 deferral above).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        // No explicit Height — auto.

        var items = new Box[4];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? wrapper = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == flex) { wrapper = f; break; }
        }
        Assert.NotNull(wrapper);
        // Wrapper's BlockSize should equal sum-of-line-cross-extents
        // (= 50 + 50 = 100). Pre-L6 the wrapper sized to max(item
        // block-size) = 50, which would clip line 2's items.
        Assert.Equal(100.0, wrapper!.Value.BlockSize, precision: 3);
    }

    [Fact]
    public void L6_flex_wrap_align_items_center_per_line()
    {
        // Lines with varying item heights — `align-items: center`
        // should center each item against ITS LINE'S cross-extent (=
        // max(item cross-size on that line)), not the container's
        // total cross extent.
        //
        // Items: 4 items 100x50 + 100x100 + 100x50 + 100x100 → in a
        // 250px-wide container:
        //   Line 1: items 0(100x50) + 1(100x100). LineMain = 200 (<=
        //     250). LineCross = max(50, 100) = 100.
        //   Line 2: items 2(100x50) + 3(100x100). LineMain = 200.
        //     LineCross = 100.
        // align-items: center → on each line:
        //   - 50px item is centered in 100px line → BlockOffset =
        //     lineCrossStart + (100-50)/2 = lineCrossStart + 25.
        //   - 100px item fills its line → BlockOffset = lineCrossStart.
        // Line 1 cross-start = 0; Line 2 cross-start = 100.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(6)); // center
        // Per L7 — pin L6 natural-stacking via explicit
        // `align-content: flex-start`; the test exercises per-line
        // align-items center, and §8.4's stretch default would
        // expand each line by 100 (= 200/2), conflating axes.
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 400);

        var heights = new double[] { 50, 100, 50, 100 };
        var items = new Box[4];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, heights[i]);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        // Fragmentainer matches declared width (see L4 deferral comment
        // in `L6_flex_wrap_packs_items_into_two_lines_when_natural_widths_exceed_container`).
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(4, fragments.Count);
        // Line 1, cross-start = 0:
        //   item 0 (50 tall): centered in 100 → BlockOffset 25.
        //   item 1 (100 tall): centered → BlockOffset 0.
        Assert.Equal(25.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[1].BlockOffset, precision: 3);
        // Line 2, cross-start = 100:
        //   item 2 (50 tall): centered → BlockOffset 125.
        //   item 3 (100 tall): centered → BlockOffset 100.
        Assert.Equal(125.0, fragments[2].BlockOffset, precision: 3);
        Assert.Equal(100.0, fragments[3].BlockOffset, precision: 3);
    }

    [Fact]
    public void L6_flex_direction_column_flex_wrap_packs_into_columns()
    {
        // Column direction + wrap — items stack vertically (block axis
        // = main); when cumulative block-axis size exceeds container's
        // declared height, a new line starts; lines stack horizontally
        // (inline axis = cross).
        //
        // Fixture: 4 items of 100x60 in a column container 150px wide
        // x 150px tall with `flex-wrap: wrap`. Per the greedy
        // algorithm:
        //   Line 1: items 0+1 (cumulative block = 120 <= 150). Adding
        //     item 2 → 180 > 150 → wrap.
        //   Line 2: items 2+3 (cumulative block = 120).
        // Line 1's cross-extent (inline-axis max) = 100; line 2's
        // cross-extent = 100. Line 1 cross-start = 0, line 2 cross-
        // start = 100.
        // Each item's main-axis offset = cumulative within its line.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        // Per L7 — pin L6 natural-stacking via explicit
        // `align-content: flex-start`; for column direction the
        // cross axis is inline, so the §8.4 stretch default would
        // grow each line's inline extent by 50 (= 100/2) shifting
        // line 2 to InlineOffset 150 instead of the natural 100.
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 300);
        SetLengthPx(flex.Style, PropertyId.Height, 150);

        var items = new Box[4];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 60);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(4, fragments.Count);
        // Line 1 (column 1): items 0 + 1; BlockOffset 0, 60 (stacked
        // vertically). InlineOffset = 0 (line 1's cross-start).
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(60.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[1].InlineOffset, precision: 3);
        // Line 2 (column 2): items 2 + 3; BlockOffset 0, 60.
        // InlineOffset = 100 (line 2's cross-start = line 1 inline
        // extent of 100).
        Assert.Equal(0.0, fragments[2].BlockOffset, precision: 3);
        Assert.Equal(60.0, fragments[3].BlockOffset, precision: 3);
        Assert.Equal(100.0, fragments[2].InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[3].InlineOffset, precision: 3);
    }

    [Fact]
    public void L6_flex_wrap_nowrap_default_behavior_unchanged()
    {
        // Sanity: with default flex-wrap (nowrap), the layout is the
        // L1-L5 single-line algorithm. 3 items of 100 in 250px container
        // — total 300 > 250, but no wrap. Items pack at inline 0, 100,
        // 200; item 2 overflows past 250 but does not wrap.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        // No flex-wrap declaration — defaults to nowrap.
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }

        Assert.Equal(3, fragments.Count);
        // All items on the same line (BlockOffset 0); InlineOffset
        // walks 0/100/200. Item 2 ends at 300 (overflows 250) — that's
        // the spec'd nowrap behavior (CSS Flexbox L1 §6 + §9.4).
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[2].InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[2].BlockOffset, precision: 3);
    }

    // ====================================================================
    //  Phase 3 Task 15 L6 post-PR-#66 review hardening (6 findings).
    // ====================================================================

    [Fact]
    public void L6_hardening_column_wrap_explicit_height_clamps_outer_cursor_advance()
    {
        // F#1 — column + wrap + explicit height: a 4×60px column-flex
        // declares height: 150px so wrap fires (4*60=240 > 150 → 2
        // columns of items). Pre-F#1 the BlockLayouter's
        // MeasureSubtreeVisualBlockExtent walked the flex children as
        // block-flow and reserved 240px (the un-wrapped sum) on the
        // outer cursor; a sibling block AFTER the flex container then
        // landed at wrapper.BlockOffset + 240 instead of the
        // spec-correct wrapper.BlockOffset + 150 (= the declared
        // wrapper height). Post-F#1 the outer row-direction clamp
        // extends to fire for column+wrap+explicit-LengthPx-height,
        // bringing the over-measured subtree extent back to the
        // wrapper's declared border-box block size.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        SetLengthPx(flex.Style, PropertyId.Width, 300);
        SetLengthPx(flex.Style, PropertyId.Height, 150);

        // 4 items of 100x60 — main-axis sum = 240 > 150 → wrap into
        // 2 columns of items each. The block-flow stacking sum (=
        // 240) is the OVER-measurement we're guarding against.
        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 60);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }

        // Sibling AFTER the flex container — a plain block of height
        // 30. With the F#1 clamp, this sibling lands at
        // wrapper.BlockOffset + 150 (= declared wrapper height).
        // Without the clamp it would land at wrapper.BlockOffset +
        // 240 (= un-wrapped sum).
        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Width, 100);
        SetLengthPx(siblingStyle, PropertyId.Height, 30);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());

        root.AppendChild(flex);
        root.AppendChild(sibling);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? wrapper = null;
        BoxFragment? siblingFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == flex) wrapper = f;
            else if (f.Box == sibling) siblingFragment = f;
        }
        Assert.NotNull(wrapper);
        Assert.NotNull(siblingFragment);
        // F#1 — sibling lands at wrapper.BlockOffset + 150 (= the
        // declared wrapper height), NOT + 240 (= the un-wrapped
        // block-flow stacking sum).
        Assert.Equal(wrapper!.Value.BlockOffset + 150.0,
            siblingFragment!.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public void L6_hardening_column_wrap_near_page_boundary_does_not_force_break()
    {
        // F#1 — a column+wrap flex container with declared height
        // slightly below the page-remaining size; pre-F#1 the
        // over-measurement of the un-wrapped block-flow sum could
        // trigger a false page break because the outer pagination
        // sees the wrapper as oversized. Post-F#1 the clamp brings
        // the subtree extent back to the declared height + the flex
        // emits cleanly on the current page with no diagnostic.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        SetLengthPx(flex.Style, PropertyId.Width, 400);
        // Declared height = 150. Page size = 200. Without the F#1
        // clamp the outer pagination saw the wrapper as 240 (un-
        // wrapped sum) > 200 → forced overflow / break path.
        SetLengthPx(flex.Style, PropertyId.Height, 150);

        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 60);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }

        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: diagSink,
            shaperResolver: shaper);
        // Page size 200 — JUST enough to fit a 150-px-tall wrapper.
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Sanity — the wrapper fragment + all 4 items emit.
        BoxFragment? wrapper = null;
        var itemFragmentCount = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == flex) wrapper = f;
            else if (f.Box.Kind == BoxKind.BlockContainer
                && f.Box != root && f.Box != flex) itemFragmentCount++;
        }
        Assert.NotNull(wrapper);
        // The 4 flex items + nothing else.
        Assert.Equal(4, itemFragmentCount);
        // The wrapper paints at its declared height (= the wrap
        // threshold).
        Assert.Equal(150.0, wrapper!.Value.BlockSize, precision: 3);
        // No PAGINATION-FORCED-OVERFLOW-001 diagnostic — the F#1
        // clamp prevents the false over-measurement that would have
        // triggered it.
        foreach (var d in diagSink.Diagnostics)
        {
            Assert.NotEqual("PAGINATION-FORCED-OVERFLOW-001", d.Code);
        }
    }

    [Fact]
    public void L7_align_content_stretch_default_grows_lines_to_fill_container()
    {
        // Per Phase 3 Task 15 L7 — CSS Flexbox L1 §8.4 + CSS Box
        // Alignment L3 §6 say the initial value of `align-content` is
        // `stretch`. A 200px-tall multi-line container with two 50px
        // natural-cross-extent lines stretches the lines so they fill
        // the 200px cross extent — line 1 at BlockOffset 0, line 2 at
        // BlockOffset 100 (= the stretched first line's cross-end =
        // start of line 2). Each line's cross-extent grows from 50 →
        // 100 (= 50 + freeCrossSpace / lineCount = 50 + 100/2). This
        // test was previously the L6_hardening_known_gap_*_not_implemented
        // pin which asserted line 2 at BlockOffset 50 (= the L6
        // approximation that stacked lines at natural sizes); the
        // L7 implementation flips the assertion to the spec-correct
        // value. PR #66 finding F#3.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        // Row direction (default) + wrap.
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        // Container cross-extent (height) = 200; two lines of 50px
        // each (sumLineCross = 100) leave 100px of free cross space.
        // align-content: stretch (the default, =`normal` per §8.4)
        // grows each line by 100/2 = 50px, producing two 100px lines
        // that exactly fill the container.
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        // 4 items of 100×50 — line 1 = items 0+1, line 2 = items 2+3.
        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box != root && f.Box != flex
                && f.Box.Kind == BoxKind.BlockContainer)
            {
                itemFragments.Add(f);
            }
        }
        Assert.Equal(4, itemFragments.Count);
        // L7 spec-correct stretch assertions — items[0..1] on line 1
        // at BlockOffset 0 (= cross-start); items[2..3] on line 2 at
        // BlockOffset 100 (= the stretched line 1's cross-end).
        Assert.Equal(0.0, itemFragments[0].BlockOffset, precision: 3);
        Assert.Equal(0.0, itemFragments[1].BlockOffset, precision: 3);
        Assert.Equal(100.0, itemFragments[2].BlockOffset, precision: 3);
        Assert.Equal(100.0, itemFragments[3].BlockOffset, precision: 3);
    }

    [Fact]
    public void L7_align_content_flex_start_packs_lines_at_cross_start()
    {
        // Per Phase 3 Task 15 L7 — `align-content: flex-start` packs
        // wrapped lines at the cross-start edge with no inter-line
        // spacing. 2 lines of 50px each in a 200px container → lines
        // at BlockOffset 0 and 50 (= natural stack). The 100px of free
        // cross-space stays empty at the cross-end. Keyword index 8 in
        // BuildAlignContentTable (= position 8 of the ContentPositions
        // suffix after `normal` + 4 distributions: flex-start).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box != root && f.Box != flex
                && f.Box.Kind == BoxKind.BlockContainer)
            {
                itemFragments.Add(f);
            }
        }
        Assert.Equal(4, itemFragments.Count);
        Assert.Equal(0.0, itemFragments[0].BlockOffset, precision: 3);
        Assert.Equal(0.0, itemFragments[1].BlockOffset, precision: 3);
        Assert.Equal(50.0, itemFragments[2].BlockOffset, precision: 3);
        Assert.Equal(50.0, itemFragments[3].BlockOffset, precision: 3);
    }

    [Fact]
    public void L7_align_content_flex_end_packs_lines_at_cross_end()
    {
        // Per Phase 3 Task 15 L7 — `align-content: flex-end` packs
        // lines at cross-end. 2 lines of 50px each in 200px container
        // → freeCrossSpace = 100; lines at BlockOffset 100 + 150 (=
        // freeCrossSpace + natural-offset shift). Keyword index 9 (=
        // flex-end position in the ContentPositions sequence).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(12)); // flex-end
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box != root && f.Box != flex
                && f.Box.Kind == BoxKind.BlockContainer)
            {
                itemFragments.Add(f);
            }
        }
        Assert.Equal(4, itemFragments.Count);
        Assert.Equal(100.0, itemFragments[0].BlockOffset, precision: 3);
        Assert.Equal(100.0, itemFragments[1].BlockOffset, precision: 3);
        Assert.Equal(150.0, itemFragments[2].BlockOffset, precision: 3);
        Assert.Equal(150.0, itemFragments[3].BlockOffset, precision: 3);
    }

    [Fact]
    public void L7_align_content_center_centers_lines_on_cross_axis()
    {
        // Per Phase 3 Task 15 L7 — `align-content: center` centers the
        // line stack on the cross axis. 2 lines of 50px each in 200px
        // container → freeCrossSpace = 100; centering shifts the stack
        // by 50; lines at BlockOffset 50 + 100. Keyword index 5
        // (= center, first ContentPosition).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(8)); // center
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box != root && f.Box != flex
                && f.Box.Kind == BoxKind.BlockContainer)
            {
                itemFragments.Add(f);
            }
        }
        Assert.Equal(4, itemFragments.Count);
        Assert.Equal(50.0, itemFragments[0].BlockOffset, precision: 3);
        Assert.Equal(50.0, itemFragments[1].BlockOffset, precision: 3);
        Assert.Equal(100.0, itemFragments[2].BlockOffset, precision: 3);
        Assert.Equal(100.0, itemFragments[3].BlockOffset, precision: 3);
    }

    [Fact]
    public void L7_align_content_space_between_distributes_gaps()
    {
        // Per Phase 3 Task 15 L7 — `align-content: space-between` puts
        // first line at cross-start, last line at cross-end, equal
        // gaps between. 3 lines of 50px each in 300px container →
        // sumLineCross = 150; freeCrossSpace = 150; betweenSpacing =
        // 150 / (3-1) = 75; lines at BlockOffset 0, 125 (= 50+75),
        // 250 (= 125+50+75). Keyword index 1 (= space-between, first
        // ContentDistribution).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(1)); // space-between
        SetLengthPx(flex.Style, PropertyId.Width, 150);
        SetLengthPx(flex.Style, PropertyId.Height, 300);

        // 3 items of 100×50, each wraps to its own line (each item is
        // 100 wide; the container is 150 wide; second item would push
        // total to 200 > 150 so it wraps; same for third).
        for (var i = 0; i < 3; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 150, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box != root && f.Box != flex
                && f.Box.Kind == BoxKind.BlockContainer)
            {
                itemFragments.Add(f);
            }
        }
        Assert.Equal(3, itemFragments.Count);
        Assert.Equal(0.0, itemFragments[0].BlockOffset, precision: 3);
        Assert.Equal(125.0, itemFragments[1].BlockOffset, precision: 3);
        Assert.Equal(250.0, itemFragments[2].BlockOffset, precision: 3);
    }

    [Fact]
    public void L7_align_content_space_around_distributes_with_half_gaps()
    {
        // Per Phase 3 Task 15 L7 — `align-content: space-around` puts
        // half-size gaps at the edges + full gaps between lines. 3
        // lines of 50px each in 300px container → freeCrossSpace =
        // 150; gap = 150 / 3 = 50; halfGap (= startOffset) = 25;
        // lines at BlockOffset 25, 125 (= 25+50+50), 225 (= 125+50+50).
        // Keyword index 2 (= space-around).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(2)); // space-around
        SetLengthPx(flex.Style, PropertyId.Width, 150);
        SetLengthPx(flex.Style, PropertyId.Height, 300);

        for (var i = 0; i < 3; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 150, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box != root && f.Box != flex
                && f.Box.Kind == BoxKind.BlockContainer)
            {
                itemFragments.Add(f);
            }
        }
        Assert.Equal(3, itemFragments.Count);
        Assert.Equal(25.0, itemFragments[0].BlockOffset, precision: 3);
        Assert.Equal(125.0, itemFragments[1].BlockOffset, precision: 3);
        Assert.Equal(225.0, itemFragments[2].BlockOffset, precision: 3);
    }

    [Fact]
    public void L7_align_content_space_evenly_equal_gaps_everywhere()
    {
        // Per Phase 3 Task 15 L7 — `align-content: space-evenly` puts
        // equal gaps at edges AND between lines. 3 lines of 50px in
        // 300px container → freeCrossSpace = 150; gap = 150 / (3+1) =
        // 37.5; lines at BlockOffset 37.5, 125 (= 37.5+50+37.5),
        // 212.5 (= 125+50+37.5). Keyword index 3 (= space-evenly).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(3)); // space-evenly
        SetLengthPx(flex.Style, PropertyId.Width, 150);
        SetLengthPx(flex.Style, PropertyId.Height, 300);

        for (var i = 0; i < 3; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 150, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box != root && f.Box != flex
                && f.Box.Kind == BoxKind.BlockContainer)
            {
                itemFragments.Add(f);
            }
        }
        Assert.Equal(3, itemFragments.Count);
        Assert.Equal(37.5, itemFragments[0].BlockOffset, precision: 3);
        Assert.Equal(125.0, itemFragments[1].BlockOffset, precision: 3);
        Assert.Equal(212.5, itemFragments[2].BlockOffset, precision: 3);
    }

    [Fact]
    public void L7_hardening_default_center_overflow_keeps_natural_negative_offset()
    {
        // Per Phase 3 Task 15 L7 post-PR-#67 hardening F#2 — when the
        // sum of line cross extents EXCEEDS the container's cross
        // extent (freeCrossSpace < 0), CSS Box Alignment L3 §5.3 says
        // default-mode positional values (= FlexStart / FlexEnd /
        // Center) keep their natural (possibly-negative) offset on
        // overflow — items overflow equally on both sides for center.
        // 3 lines × 80px = 240px in 200px container with align-content:
        // center → freeCrossSpace = -40; natural offset = -40 / 2 = -20.
        // Lines pack at -20, 60, 140 (= overflow on both edges).
        // Pre-F#2 this returned 0 (= incorrect safe-start fallback).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(8)); // center
        SetLengthPx(flex.Style, PropertyId.Width, 150);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        for (var i = 0; i < 3; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 80);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 150, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box != root && f.Box != flex
                && f.Box.Kind == BoxKind.BlockContainer)
            {
                itemFragments.Add(f);
            }
        }
        Assert.Equal(3, itemFragments.Count);
        // Natural center offset = freeCrossSpace / 2 = -40 / 2 = -20.
        Assert.Equal(-20.0, itemFragments[0].BlockOffset, precision: 3);
        Assert.Equal(60.0, itemFragments[1].BlockOffset, precision: 3);
        Assert.Equal(140.0, itemFragments[2].BlockOffset, precision: 3);
    }

    [Fact]
    public void L7_hardening_default_space_between_overflow_falls_back_to_flex_start()
    {
        // Per Phase 3 Task 15 L7 post-PR-#67 hardening F#2 — default-mode
        // distribution values (space-between/-around/-evenly) fall back
        // to safe-start on overflow per CSS Box Alignment L3 §5.3.
        // 3 lines × 80px = 240 in 200 container → freeCrossSpace = -40.
        // align-content: space-between with overflow → fall back to
        // safe-start (= 0). Lines stack at 0, 80, 160.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(1)); // space-between
        SetLengthPx(flex.Style, PropertyId.Width, 150);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        for (var i = 0; i < 3; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 80);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 150, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box != root && f.Box != flex
                && f.Box.Kind == BoxKind.BlockContainer)
            {
                itemFragments.Add(f);
            }
        }
        Assert.Equal(3, itemFragments.Count);
        // Safe-start fallback — lines at 0, 80, 160.
        Assert.Equal(0.0, itemFragments[0].BlockOffset, precision: 3);
        Assert.Equal(80.0, itemFragments[1].BlockOffset, precision: 3);
        Assert.Equal(160.0, itemFragments[2].BlockOffset, precision: 3);
    }

    [Fact]
    public void L7_hardening_safe_center_overflow_falls_back_to_flex_start()
    {
        // Per Phase 3 Task 15 L7 post-PR-#67 hardening F#2 — `safe X`
        // modifier ALWAYS falls back to safe-start on overflow,
        // regardless of value family. Same 3 × 80px overflow scenario
        // as the default-mode test above, but with `safe center`
        // (keyword index 15) → lines pack at 0, 80, 160 (= safe-start
        // fallback, not natural -20). Pre-F#2 there was no distinction
        // between default mode + safe mode at all.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(15)); // safe center
        SetLengthPx(flex.Style, PropertyId.Width, 150);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        for (var i = 0; i < 3; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 80);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 150, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box != root && f.Box != flex
                && f.Box.Kind == BoxKind.BlockContainer)
            {
                itemFragments.Add(f);
            }
        }
        Assert.Equal(3, itemFragments.Count);
        // Safe modifier — safe-start fallback regardless of value.
        Assert.Equal(0.0, itemFragments[0].BlockOffset, precision: 3);
        Assert.Equal(80.0, itemFragments[1].BlockOffset, precision: 3);
        Assert.Equal(160.0, itemFragments[2].BlockOffset, precision: 3);
    }

    [Fact]
    public void L7_hardening_unsafe_center_overflow_keeps_natural_negative_offset()
    {
        // Per Phase 3 Task 15 L7 post-PR-#67 hardening F#2 — `unsafe X`
        // modifier ALWAYS honors the natural offset on overflow,
        // regardless of value family — = explicit author opt-in to
        // overflow behavior. Same 3 × 80px overflow scenario with
        // `unsafe center` (keyword index 22) → lines pack at -20, 60,
        // 140 (= natural negative offset honored, matching the
        // default-mode positional behavior since `center` is positional).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(22)); // unsafe center
        SetLengthPx(flex.Style, PropertyId.Width, 150);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        for (var i = 0; i < 3; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 80);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 150, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box != root && f.Box != flex
                && f.Box.Kind == BoxKind.BlockContainer)
            {
                itemFragments.Add(f);
            }
        }
        Assert.Equal(3, itemFragments.Count);
        // Unsafe modifier — honor natural negative offset.
        Assert.Equal(-20.0, itemFragments[0].BlockOffset, precision: 3);
        Assert.Equal(60.0, itemFragments[1].BlockOffset, precision: 3);
        Assert.Equal(140.0, itemFragments[2].BlockOffset, precision: 3);
    }

    [Fact]
    public void L7_align_content_single_line_has_no_effect()
    {
        // Per Phase 3 Task 15 L7 — CSS Flexbox §8.4 says align-content
        // has NO EFFECT on a single-line container. With nowrap (no
        // wrapping), `align-content: center` is ignored — the line
        // stays at cross-start. Two 100×50 items in a 300×200
        // container with align-content: center → both items at
        // BlockOffset 0 (= cross-start), NOT shifted by 75 (= what
        // center would produce on multi-line).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        // No flex-wrap declaration → nowrap (the default).
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(8)); // center
        // Also disable align-items stretch (= the default) so items
        // keep their declared 50px height; otherwise stretch would
        // expand them to 200 + obscure the align-content gate.
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 300);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        for (var i = 0; i < 2; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 300, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box != root && f.Box != flex
                && f.Box.Kind == BoxKind.BlockContainer)
            {
                itemFragments.Add(f);
            }
        }
        Assert.Equal(2, itemFragments.Count);
        // Both items at BlockOffset 0 — align-content: center has no
        // effect on the single-line container.
        Assert.Equal(0.0, itemFragments[0].BlockOffset, precision: 3);
        Assert.Equal(0.0, itemFragments[1].BlockOffset, precision: 3);
    }

    [Fact]
    public void L7_align_content_normal_resolves_to_stretch_per_spec()
    {
        // Per Phase 3 Task 15 L7 — sanity test for the §8.4 spec
        // default. `align-content: normal` MUST produce identical
        // behavior to explicit `align-content: stretch` for flex
        // containers (= the property is unset = the cascade's initial
        // `normal` keyword applies). 2 lines of 50px each in 200px
        // container → both lines stretched to 100px → line 2 at
        // BlockOffset 100. This is the same expected output as the
        // stretch_default test, but with NO explicit align-content
        // declaration on the container — the cascade default takes
        // over and resolves to stretch via the §8.4 rule.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        // NO explicit align-content set — cascade carries `normal` (=
        // initial value); §8.4 says normal → stretch for flex.
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box != root && f.Box != flex
                && f.Box.Kind == BoxKind.BlockContainer)
            {
                itemFragments.Add(f);
            }
        }
        Assert.Equal(4, itemFragments.Count);
        // Identical to L7_align_content_stretch_default_grows_lines_to_fill_container —
        // normal resolves to stretch per §8.4.
        Assert.Equal(0.0, itemFragments[0].BlockOffset, precision: 3);
        Assert.Equal(0.0, itemFragments[1].BlockOffset, precision: 3);
        Assert.Equal(100.0, itemFragments[2].BlockOffset, precision: 3);
        Assert.Equal(100.0, itemFragments[3].BlockOffset, precision: 3);
    }

    [Fact]
    public void L7_align_content_column_direction_stretch_grows_lines_along_inline_axis()
    {
        // Per Phase 3 Task 15 L7 post-PR-#67 hardening F#7 — column-
        // direction proof. For column-direction the cross axis is the
        // inline axis, so align-content distributes lines along inline
        // (= horizontally) and the stretch addend grows each line's
        // INLINE extent. 4 items of 100w × 50h in a column container
        // 400 wide × 150 tall with flex-wrap: wrap:
        //   Line 1: items 0+1 (cumulative block = 100 <= 150). Adding
        //     item 2 → 150 ≤ 150 (fits exactly). Adding item 3 → 200 >
        //     150 → wrap.
        //   Line 1: items 0+1+2 (block sum = 150), line 2: item 3.
        // Each line's inline extent = max(item inline) = 100. Sum =
        // 200; container inline = 400 → freeCrossSpace = 200. Stretch
        // grows each line's inline-extent by 200/2 = 100, so each line
        // = 200 inline. Line 1 starts at InlineOffset 0; line 2 starts
        // at 200 (= stretched line 1's inline-end). Items use their
        // line's inline extent for their align-items math but here we
        // verify the line cursor positions are correct.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        // No explicit align-content — cascade default normal → stretch.
        SetLengthPx(flex.Style, PropertyId.Width, 400);
        SetLengthPx(flex.Style, PropertyId.Height, 150);

        var items = new Box[4];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(4, fragments.Count);
        // Line 1 = items 0+1+2 at InlineOffset 0; line 2 = item 3 at
        // InlineOffset 200 (= stretched line 1 inline-end).
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[2].InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[3].InlineOffset, precision: 3);
    }

    [Fact]
    public void L7_align_content_column_direction_center_centers_lines_inline()
    {
        // Per Phase 3 Task 15 L7 post-PR-#67 hardening F#7 — column +
        // align-content: center centers the line stack along the
        // inline axis. 4 items of 100w × 50h in a column 400w × 150h
        // with flex-wrap: wrap. Lines pack as in the stretch test
        // (line 1: items 0+1+2; line 2: item 3); each line inline-
        // extent = 100; sum = 200; freeCrossSpace = 400 - 200 = 200.
        // align-content: center → startOffset = 100; line 1 at
        // InlineOffset 100, line 2 at 200.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(8)); // center
        SetLengthPx(flex.Style, PropertyId.Width, 400);
        SetLengthPx(flex.Style, PropertyId.Height, 150);

        var items = new Box[4];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(4, fragments.Count);
        // Line 1 at InlineOffset 100; line 2 at 200 (= 100 + line 1
        // natural inline extent of 100).
        Assert.Equal(100.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[2].InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[3].InlineOffset, precision: 3);
    }

    [Fact]
    public void L7_align_content_column_direction_space_between_distributes_along_inline()
    {
        // Per Phase 3 Task 15 L7 post-PR-#67 hardening F#7 — column +
        // align-content: space-between distributes lines along the
        // inline axis with no edge spacing. Same 4 items / 400w × 150h
        // / wrap fixture as above. Lines: 1 = items 0+1+2 (inline 100),
        // 2 = item 3 (inline 100); sumLineCross = 200; freeCrossSpace
        // = 200. space-between with N=2 → between-spacing = 200/(2-1)
        // = 200. Line 1 starts at InlineOffset 0; line 2 starts at 300
        // (= 0 + line 1 inline of 100 + between of 200).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(1)); // space-between
        SetLengthPx(flex.Style, PropertyId.Width, 400);
        SetLengthPx(flex.Style, PropertyId.Height, 150);

        var items = new Box[4];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(4, fragments.Count);
        // Line 1 at InlineOffset 0; line 2 at 300 (= 0 + 100 + 200).
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[2].InlineOffset, precision: 3);
        Assert.Equal(300.0, fragments[3].InlineOffset, precision: 3);
    }

    [Fact]
    public void L7_align_content_stretch_with_align_items_center_auto_items_stay_at_natural_size()
    {
        // Per Phase 3 Task 15 L7 post-PR-#67 hardening F#8 —
        // interaction between align-content: stretch (grows lines) and
        // align-items: center on items with auto cross-size. Per the
        // L3 post-PR-#63 IsCrossSizeAuto detection: an item with
        // explicit height: 0 keeps its declared size (returns false from
        // IsCrossSizeAuto), but an item with NO height declaration is
        // auto (= IsCrossSizeAuto returns true).
        //
        // For positional alignment (center / flex-end), auto items keep
        // their NATURAL cross-size (= 0 from ReadLengthPxOrZero) and are
        // POSITIONED within the line's cross extent. With stretched
        // lines (200 cross-extent each) + auto items + align-items:
        // center → items at line center = line cross-start + 100.
        //
        // 4 items of 100×auto in a 250×400 container; freeCrossSpace =
        // 400 - 100 (= 2 natural lines of cross-extent 0) ... wait,
        // natural cross-size 0 means sumLineCrossExtents = 0 + 0 = 0,
        // freeCrossSpace = 400; stretch addend = 200 per line. So each
        // line grows from 0 → 200. With align-items: center on natural
        // 0-height items: item centered in 200 → offset = 100.
        //
        // Line 1: items 0+1 at BlockOffset 100 (within line 0..200).
        // Line 2: items 2+3 at BlockOffset 300 (line 2 starts at 200,
        // centered = 200 + 100 = 300).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        // align-content: stretch is the default (= normal → stretch).
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(6)); // center
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 400);

        // 4 items of 100w × auto-h — IsCrossSizeAuto returns true for
        // these (height slot is Unset).
        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            // NO height set — leaves the slot Unset → IsCrossSizeAuto.
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box != root && f.Box != flex
                && f.Box.Kind == BoxKind.BlockContainer)
            {
                itemFragments.Add(f);
            }
        }
        Assert.Equal(4, itemFragments.Count);
        // Auto items keep natural 0 cross-size; centered in stretched
        // 200 line. Line 1 center = 100; line 2 starts at 200, center
        // = 300.
        Assert.Equal(100.0, itemFragments[0].BlockOffset, precision: 3);
        Assert.Equal(100.0, itemFragments[1].BlockOffset, precision: 3);
        Assert.Equal(300.0, itemFragments[2].BlockOffset, precision: 3);
        Assert.Equal(300.0, itemFragments[3].BlockOffset, precision: 3);
        // Items' cross-sizes (BlockSize) stay at natural 0 — center is
        // a positional alignment, never resizes.
        Assert.Equal(0.0, itemFragments[0].BlockSize, precision: 3);
        Assert.Equal(0.0, itemFragments[1].BlockSize, precision: 3);
    }

    [Fact]
    public void L7_align_content_stretch_with_align_items_stretch_auto_items_grow_to_stretched_line()
    {
        // Per Phase 3 Task 15 L7 post-PR-#67 hardening F#8 — stretched
        // lines × align-items: stretch (the default) on items with
        // auto cross-size. The stretch branch of align-items RESIZES
        // auto items to fill the line's cross extent; with stretched
        // lines = 200 cross-extent, each auto item grows from natural
        // 0 → 200 cross-size.
        //
        // 4 items of 100w × auto-h (Unset = auto per IsCrossSizeAuto)
        // in a 250×400 container. Natural line cross-extent = 0
        // (max(item cross) = 0), sumLineCross = 0, freeCrossSpace =
        // 400, stretchAddend = 200 per line. Each line grows to 200.
        // align-items: stretch resizes each auto item from 0 → 200.
        //
        // Item 0 + 1 on line 1 at BlockOffset 0 (line cross-start),
        // BlockSize 200. Item 2 + 3 on line 2 at BlockOffset 200 (line
        // 2 starts after stretched line 1).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        // No align-items declaration → default normal → stretch.
        // No align-content → default normal → stretch.
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 400);

        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            // NO height set — auto via IsCrossSizeAuto.
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box != root && f.Box != flex
                && f.Box.Kind == BoxKind.BlockContainer)
            {
                itemFragments.Add(f);
            }
        }
        Assert.Equal(4, itemFragments.Count);
        // Line 1 at BlockOffset 0; items stretched to BlockSize 200.
        Assert.Equal(0.0, itemFragments[0].BlockOffset, precision: 3);
        Assert.Equal(0.0, itemFragments[1].BlockOffset, precision: 3);
        Assert.Equal(200.0, itemFragments[0].BlockSize, precision: 3);
        Assert.Equal(200.0, itemFragments[1].BlockSize, precision: 3);
        // Line 2 at BlockOffset 200 (= stretched line 1 cross-end).
        Assert.Equal(200.0, itemFragments[2].BlockOffset, precision: 3);
        Assert.Equal(200.0, itemFragments[3].BlockOffset, precision: 3);
        Assert.Equal(200.0, itemFragments[2].BlockSize, precision: 3);
        Assert.Equal(200.0, itemFragments[3].BlockSize, precision: 3);
    }

    [Fact]
    public void L11_align_content_wrap_reverse_applies_distribution_with_reversed_lines()
    {
        // Per Phase 3 Task 15 L11 — replaces the L7 post-PR-#67 F#9
        // approximation test. wrap-reverse now ships proper line
        // stacking (no diagnostic). The align-content distribution
        // applies to the REVERSED line list — line 0 (DOM first)
        // emits at the NEW cross-start (= the bottom for L1 LTR +
        // horizontal-tb default).
        //
        // Fixture: 4 items of 100×50 in 250×200 wrap-reverse +
        // align-content: center → 2 lines × 50 cross each; sum = 100;
        // freeCrossSpace = 100; center startOffset = 50.
        // Reversed iteration: line 1 (DOM 1) first at BlockOffset 50;
        // line 0 (DOM 0) second at BlockOffset 100.
        // DOM items 0+1 are on the FIRST DOM line → BlockOffset 100.
        // DOM items 2+3 are on the SECOND DOM line → BlockOffset 50.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(2)); // wrap-reverse
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(8)); // center
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var items = new Box[4];
        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: diagSink, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // (a) No more LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001
        // diagnostic — wrap-reverse is now spec-correct.
        foreach (var d in diagSink.Diagnostics)
        {
            Assert.NotEqual("LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001", d.Code);
        }

        // (b) align-content: center distributes lines after reversal.
        // Lines in iteration order: DOM line 1 first at BlockOffset
        // 50 (= startOffset for center), DOM line 0 second at
        // BlockOffset 100 (= 50 + 50 line 1 cross).
        // DOM items 0+1 (= DOM line 0) at BlockOffset 100.
        // DOM items 2+3 (= DOM line 1) at BlockOffset 50.
        var fragments = new BoxFragment?[4];
        foreach (var f in sink.Fragments)
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (f.Box == items[i]) { fragments[i] = f; break; }
            }
        }
        Assert.Equal(100.0, fragments[0]!.Value.BlockOffset, precision: 3);
        Assert.Equal(100.0, fragments[1]!.Value.BlockOffset, precision: 3);
        Assert.Equal(50.0, fragments[2]!.Value.BlockOffset, precision: 3);
        Assert.Equal(50.0, fragments[3]!.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public void L7_hardening_baseline_keyword_resolves_to_stretch_approximation()
    {
        // Per Phase 3 Task 15 L7 post-PR-#67 hardening F#6 — the three
        // <baseline-position> keywords (baseline, first baseline, last
        // baseline) are admitted by the BuildAlignContentTable but L7
        // approximates them all as Stretch (= the safe default; proper
        // baseline alignment is text-shaping integration scope, L8+).
        // Pre-F#6 the table didn't include the baseline keywords at all
        // → AngleSharp+cascade would either drop the declaration or
        // emit an invalid-value diagnostic. Post-F#6 the keyword
        // resolves cleanly + behaves identically to align-content:
        // stretch. Smoke test: align-content: baseline (index 5) on a
        // multi-line wrap container → stretched lines (same expected
        // output as align-content: stretch).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(5)); // baseline
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box != root && f.Box != flex
                && f.Box.Kind == BoxKind.BlockContainer)
            {
                itemFragments.Add(f);
            }
        }
        Assert.Equal(4, itemFragments.Count);
        // Same expected output as align-content: stretch — baseline
        // approximates to stretch in L7.
        Assert.Equal(0.0, itemFragments[0].BlockOffset, precision: 3);
        Assert.Equal(0.0, itemFragments[1].BlockOffset, precision: 3);
        Assert.Equal(100.0, itemFragments[2].BlockOffset, precision: 3);
        Assert.Equal(100.0, itemFragments[3].BlockOffset, precision: 3);
    }

    [Fact]
    public void L7_hardening_wrap_container_with_single_line_stretches_per_spec()
    {
        // Per Phase 3 Task 15 L7 post-PR-#67 hardening F#1 — CSS Flexbox
        // L1 §9.4: a flex container is "single-line" iff `flex-wrap:
        // nowrap` (NOT iff `flex-wrap: wrap` produced only one line).
        // A wrapping container whose content happens to fit on one line
        // is still a MULTI-LINE container per §9.4, and align-content
        // still applies to it. The default `normal → stretch` from §8.4
        // grows that single line to fill the container's cross extent.
        //
        // Pre-F#1: the helper short-circuited on `lineCount <= 1`,
        // returning (0,0,0) regardless of flex-wrap value → wrap+1-line
        // containers did NOT stretch (= bug per spec).
        // Post-F#1: gate is `!isWrapping || lineCount == 0`. A wrap
        // container with 1 line gets the stretch addend, growing the
        // single line from natural cross-size to the full container.
        //
        // Fixture: 2 items of 100w × 50h in a 250w × 200h container
        // with `flex-wrap: wrap`. Items fit on one line (200 ≤ 250).
        // Default align-content = normal → stretch. Natural line cross-
        // extent = max(50, 50) = 50. sumLineCross = 50; freeCrossSpace
        // = 200 - 50 = 150; stretchAddend = 150 / 1 = 150. Line grows
        // 50 → 200. Items use stretched 200 line cross-extent for
        // align-items math (default stretch on items → items grow from
        // natural 50 to 200; align-items: flex-start would keep them
        // at BlockOffset 0 with natural 50). Sanity assertion: items
        // emit at BlockOffset 0 with BlockSize 200 (= the stretched
        // line cross-extent applied to auto/stretched items per the
        // align-items: stretch default chain).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        // NO align-content set — cascade default normal → stretch.
        // NO align-items set — cascade default normal → stretch (the
        // L3 path).
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        for (var i = 0; i < 2; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var itemFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box != root && f.Box != flex
                && f.Box.Kind == BoxKind.BlockContainer)
            {
                itemFragments.Add(f);
            }
        }
        Assert.Equal(2, itemFragments.Count);
        // Both items on the (single) stretched line at BlockOffset 0;
        // the line itself is stretched from natural 50 to 200 (= full
        // container cross-extent) so items with align-items: stretch
        // (declared height 50 but stretchable to fit the line) reach
        // the line's full cross extent. Items keep explicit declared
        // height 50 since IsCrossSizeAuto returns false for declared
        // lengths (per the L3 post-PR-#63 detection).
        Assert.Equal(0.0, itemFragments[0].BlockOffset, precision: 3);
        Assert.Equal(0.0, itemFragments[1].BlockOffset, precision: 3);
        // Items keep their declared 50px height (IsCrossSizeAuto false
        // for explicit LengthPx). The line grew to 200 underneath but
        // align-items: stretch only resizes auto items. Pin the items'
        // BlockSize stays at 50.
        Assert.Equal(50.0, itemFragments[0].BlockSize, precision: 3);
        Assert.Equal(50.0, itemFragments[1].BlockSize, precision: 3);
    }

    // ====================================================================
    //  Phase 3 Task 15 L8 — flex-grow / flex-shrink / flex-basis.
    //  CSS Flexbox L1 §7 + §9.7 — the flexibility algorithm.
    // ====================================================================

    [Fact]
    public void ResolveFlexLineMainSizes_resolves_flex_basis_grow_for_the_pre_measure()
    {
        // PR #189 review P1 — the row-flex pre-measure (BlockLayouter.PreMeasureFlexCrossExtent)
        // resolves item widths through THIS shared §9.7 helper — the same one FlexLayouter emits
        // at — so an auto-height row item is measured at its FLEX-RESOLVED width, not the raw
        // declared/container width.
        // (a) a rigid `Width: 100; flex: 0 0` item + a `Width: 0; flex-grow: 1` item in a 300px
        //     row → 100 (the rigid item keeps its size, NOT the 300 container) + 200 (grows).
        var rigid = MakeStyle();
        SetLengthPx(rigid, PropertyId.Width, 100);
        rigid.Set(PropertyId.FlexGrow, ComputedSlot.FromNumber(0.0));
        rigid.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
        var growing = MakeStyle();
        SetLengthPx(growing, PropertyId.Width, 0);
        growing.Set(PropertyId.FlexGrow, ComputedSlot.FromNumber(1.0));
        var itemsAB = new[]
        {
            Box.ForElement(BoxKind.BlockContainer, rigid, MakeElement()),
            Box.ForElement(BoxKind.BlockContainer, growing, MakeElement()),
        };
        var resolvedAB = FlexLayouter.ResolveFlexLineMainSizes(
            itemsAB, PropertyId.Width, PropertyId.MinWidth, PropertyId.MaxWidth, 300, default);
        Assert.Equal(100.0, resolvedAB[0], precision: 3);
        Assert.Equal(200.0, resolvedAB[1], precision: 3);

        // (b) three `Width: 0; flex-grow: 1` items in 300px → 100 each (even split).
        var thirds = new Box[3];
        for (var i = 0; i < 3; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Width, 0);
            s.Set(PropertyId.FlexGrow, ComputedSlot.FromNumber(1.0));
            thirds[i] = Box.ForElement(BoxKind.BlockContainer, s, MakeElement());
        }
        var resolvedThirds = FlexLayouter.ResolveFlexLineMainSizes(
            thirds, PropertyId.Width, PropertyId.MinWidth, PropertyId.MaxWidth, 300, default);
        Assert.All(resolvedThirds, w => Assert.Equal(100.0, w, precision: 3));
    }

    [Fact]
    public void L8_flex_grow_distributes_positive_free_space_proportionally()
    {
        // Per Phase 3 Task 15 L8 — §9.7 positive-free-space distribution.
        // 3 items of width 100 in a 600px container = sumHypothetical
        // 300, freeMainSpace = 300. Each item has flex-grow: 1, so
        // sumFlexGrow = 3; each item grows by (1/3) * 300 = 100.
        // Resolved widths: 200, 200, 200. Cursors: 0, 200, 400.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexGrow, ComputedSlot.FromNumber(1.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(400.0, fragments[2].InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[2].InlineSize, precision: 3);
    }

    [Fact]
    public void L8_flex_grow_weighted_distributes_proportionally()
    {
        // Per Phase 3 Task 15 L8 — §9.7 with unequal grow factors.
        // 3 items of width 100 in a 600px container; flex-grow values:
        // 1, 2, 3. sumFlexGrow = 6; freeMainSpace = 300. Each item gets
        // (grow/6) * 300 of free space:
        //   item 0: +50  → 150
        //   item 1: +100 → 200
        //   item 2: +150 → 250
        // Sum: 600. Cursors: 0, 150, 350.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var grows = new[] { 1.0, 2.0, 3.0 };
        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexGrow, ComputedSlot.FromNumber(grows[i]));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        Assert.Equal(150.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(250.0, fragments[2].InlineSize, precision: 3);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(150.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(350.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L8_flex_grow_zero_items_do_not_grow()
    {
        // Per Phase 3 Task 15 L8 — §9.7: items with flex-grow: 0 retain
        // their hypothetical main-size; growable items absorb all the
        // free space. 3 items of width 100 in 600px container; grow
        // values: 1, 0, 1. sumFlexGrow = 2; freeMainSpace = 300. Each
        // growable item grows by 150:
        //   item 0: +150 → 250
        //   item 1: 0    → 100 (unchanged)
        //   item 2: +150 → 250
        // Cursors: 0, 250, 350.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var grows = new[] { 1.0, 0.0, 1.0 };
        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexGrow, ComputedSlot.FromNumber(grows[i]));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        Assert.Equal(250.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(100.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(250.0, fragments[2].InlineSize, precision: 3);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(250.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(350.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L8_flex_grow_no_growable_items_keeps_natural_sizes()
    {
        // Per Phase 3 Task 15 L8 — §9.7: when sumFlexGrow == 0 the
        // grow phase is skipped (free space goes to justify-content).
        // 3 items of width 100 in 600px container, no grow declarations
        // (= cascade default 0) and justify-content default = flex-start.
        // Items keep natural 100; freeMainSpace = 300 goes to padding-
        // end. Cursors: 0, 100, 200; sizes: 100, 100, 100.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            // Per Phase 3 Task 15 L8 — pin `flex-shrink: 0` so the
            // §9.7 shrink branch is inert (free space > 0, but the
            // shrink path doesn't even trigger here). Pin
            // `flex-grow: 0` explicitly to make the no-grow intent
            // unambiguous (the cascade default is 0 already).
            style.Set(PropertyId.FlexGrow, ComputedSlot.FromNumber(0.0));
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        Assert.Equal(100.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(100.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(100.0, fragments[2].InlineSize, precision: 3);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L8_flex_shrink_absorbs_negative_free_space_proportionally()
    {
        // Per Phase 3 Task 15 L8 — §9.7 shrink resolution. 3 items of
        // width 300 (sumHypothetical 900) in a 600px container =
        // freeMainSpace = -300; deficit = 300. Each item has the
        // cascade default flex-shrink: 1, hypothetical 300; scaled
        // shrink = 1 * 300 = 300 each; sumScaledShrinks = 900. Each
        // item absorbs (300/900) * 300 = 100 → resolved size = 200.
        // Cursors: 0, 200, 400; sizes: 200, 200, 200.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 300);
            SetLengthPx(style, PropertyId.Height, 50);
            // No flex-shrink declaration → cascade default 1 (= shrink).
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        Assert.Equal(200.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[2].InlineSize, precision: 3);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(400.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L8_flex_shrink_zero_items_do_not_shrink()
    {
        // Per Phase 3 Task 15 L8 — §9.7: items with flex-shrink: 0
        // retain their hypothetical main-size on negative free space;
        // other items absorb the deficit alone. 3 items of width 300
        // in 600px container; shrink values: 1, 0, 1. sumScaledShrinks
        // = 300 + 0 + 300 = 600; deficit = 300. Each shrinkable item
        // absorbs (300/600) * 300 = 150:
        //   item 0: 300 - 150 = 150
        //   item 1: 300 (unchanged, flex-shrink: 0)
        //   item 2: 300 - 150 = 150
        // Cursors: 0, 150, 450.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var shrinks = new[] { 1.0, 0.0, 1.0 };
        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 300);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(shrinks[i]));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        Assert.Equal(150.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(300.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(150.0, fragments[2].InlineSize, precision: 3);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(150.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(450.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L8_flex_basis_explicit_length_overrides_declared_width()
    {
        // Per Phase 3 Task 15 L8 — flex-basis: <length> uses the
        // explicit pixel value as the hypothetical main-size,
        // overriding the declared width. 3 items with width: 50 but
        // flex-basis: 100 in a 600px container with flex-grow: 1 each.
        // Hypothetical = 100 each (NOT 50, since flex-basis wins);
        // sumHypothetical = 300; freeMainSpace = 300; each grows by
        // 100 → final size 200. Cursors: 0, 200, 400.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 50);
            SetLengthPx(style, PropertyId.Height, 50);
            // flex-basis: 100 overrides the declared 50 width as the
            // §9.2 hypothetical main-size input to §9.7.
            SetLengthPx(style, PropertyId.FlexBasis, 100);
            style.Set(PropertyId.FlexGrow, ComputedSlot.FromNumber(1.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        Assert.Equal(200.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[2].InlineSize, precision: 3);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(400.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L8_flex_basis_zero_with_grow_one_equally_fills_container()
    {
        // Per Phase 3 Task 15 L8 — the canonical `flex: 1` recipe
        // (`flex: 1 1 0`). With flex-basis: 0 + flex-grow: 1 on every
        // item, sumHypothetical = 0 (regardless of declared widths);
        // freeMainSpace = containerMainSize - 0 = 600; sumFlexGrow = 3;
        // each item grows by (1/3) * 600 = 200. All three items end up
        // at 200 — they equally fill the container regardless of their
        // declared widths.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        // Declare different widths to demonstrate flex-basis: 0
        // overrides them — the resolved sizes should ignore the widths
        // and partition the container equally.
        var widths = new[] { 50.0, 150.0, 100.0 };
        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, widths[i]);
            SetLengthPx(style, PropertyId.Height, 50);
            SetLengthPx(style, PropertyId.FlexBasis, 0);
            style.Set(PropertyId.FlexGrow, ComputedSlot.FromNumber(1.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        Assert.Equal(200.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[2].InlineSize, precision: 3);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(400.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L8_flex_basis_percentage_resolves_against_container_main_size()
    {
        // Per Phase 3 Task 15 L8 — flex-basis: <percentage> resolves
        // against the container's main-size per §9.2.3. 3 items in a
        // 600px container; each flex-basis: 25% → hypothetical = 150.
        // sumHypothetical = 450; freeMainSpace = 150. With flex-grow: 0
        // (= cascade default) the grow phase is skipped and items
        // stay at 150 each. justify-content: flex-start (default) puts
        // them at 0, 150, 300.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            // No explicit width — flex-basis: 25% drives the hypothetical
            // main-size.
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexBasis, ComputedSlot.FromPercentage(25.0));
            // flex-grow: 0 (the cascade default) so items stay at the
            // percentage-derived hypothetical instead of growing to fill.
            // flex-shrink: 0 so the (positive) free space doesn't enter
            // the shrink branch — it goes to justify-content.
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        Assert.Equal(150.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(150.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(150.0, fragments[2].InlineSize, precision: 3);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(150.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(300.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L8_flex_basis_auto_delegates_to_declared_width()
    {
        // Per Phase 3 Task 15 L8 — flex-basis: auto (the default)
        // delegates to the declared width. 3 items of width 100 in a
        // 600px container; flex-basis defaults to auto + width = 100
        // → hypothetical = 100. sumHypothetical = 300; freeMainSpace =
        // 300. With flex-grow: 1 on each, each grows by 100 → 200.
        // Identical output to `L8_flex_grow_distributes_positive_free_space_proportionally`
        // — sanity that the explicit flex-basis: auto produces the
        // same result as no flex-basis declaration.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            // Explicit flex-basis: auto (Keyword 0 = LengthResolver's
            // KeywordIdAuto).
            style.Set(PropertyId.FlexBasis, ComputedSlot.FromKeyword(0));
            style.Set(PropertyId.FlexGrow, ComputedSlot.FromNumber(1.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        Assert.Equal(200.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[2].InlineSize, precision: 3);
    }

    [Fact]
    public void L8_flex_grow_column_direction_distributes_along_block_axis()
    {
        // Per Phase 3 Task 15 L8 — direction parity. flex-direction:
        // column → main axis = block. 3 items of height 50 in a column
        // container of height 600; each flex-grow: 1. Hypothetical = 50
        // each; sumHypothetical = 150; freeMainSpace = 450; each item
        // grows by 150 → final block-size = 200. BlockOffsets: 0, 200,
        // 400.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        SetLengthPx(flex.Style, PropertyId.Width, 200);
        SetLengthPx(flex.Style, PropertyId.Height, 600);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexGrow, ComputedSlot.FromNumber(1.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        // Block axis = main axis under column direction; items grow on
        // the block axis.
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(200.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(400.0, fragments[2].BlockOffset, precision: 3);
        Assert.Equal(200.0, fragments[0].BlockSize, precision: 3);
        Assert.Equal(200.0, fragments[1].BlockSize, precision: 3);
        Assert.Equal(200.0, fragments[2].BlockSize, precision: 3);
    }

    [Fact]
    public void L12_min_width_clamps_resolved_shrink_per_spec_step_4()
    {
        // Per Phase 3 Task 15 L12 — replaces the L8 known-gap pin
        // (`L8_known_gap_min_width_does_not_clamp_resolved_size_yet`).
        // Per CSS Flexbox L1 §9.7 step 4 the algorithm clamps each
        // item's resolved main-size to [min, max] + iterates to
        // redistribute the clamped-off space among non-frozen items.
        //
        // Fixture: 3 items × width: 300 + min-width: 250 + flex-shrink: 1
        // (cascade default) in a 600px container. Pre-fix:
        // sumHypothetical = 900; freeSpace = -300; each item absorbs
        // 100 → resolved 200 (= the L8 incomplete output).
        // Post-fix (§9.7 step 4):
        //   Iter 1: same distribution → 200 each. Clamp: 200 < 250
        //           → clamp UP to 250. Violations: +50 each;
        //           totalViolation = +150 > 0 → freeze min-violators.
        //   Iter 2: all 3 items frozen at 250. No further distribution.
        //           Convergence (totalViolation == 0).
        // Final: each item at 250; total = 750 > 600 → container
        // overflows by 150. This is the spec-correct behavior: items
        // honor their min-width even if it means the container
        // overflows.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 300);
            SetLengthPx(style, PropertyId.Height, 50);
            SetLengthPx(style, PropertyId.MinWidth, 250);
            // No flex-shrink declaration → cascade default 1.
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        // Spec-correct: each item clamped UP to min-width 250.
        Assert.Equal(250.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(250.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(250.0, fragments[2].InlineSize, precision: 3);
    }

    // ====================================================================
    //  Phase 3 Task 15 L8 post-PR-#68 hardening tests (F#1 / F#2 /
    //  F#3 / F#4 / F#5 — 5 findings, +9 unit tests).
    // ====================================================================

    [Fact]
    public void L8_hardening_wrap_with_flex_basis_zero_packs_into_single_line()
    {
        // Per Phase 3 Task 15 L8 post-PR-#68 hardening F#1 — line
        // packing uses the HYPOTHETICAL main-size (driven by
        // flex-basis), NOT the declared width. The canonical bug case:
        // 3 items of width: 300 + flex-basis: 0 in a 300px wrap
        // container. Pre-F#1 line packing saw width=300 for each item
        // → 3 separate lines (each item alone on its line). Post-F#1
        // line packing sees flex-basis=0 → all 3 items fit on one
        // line; flex-grow: 1 then grows each to 100px (= 300/3).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        SetLengthPx(flex.Style, PropertyId.Width, 300);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 300);
            SetLengthPx(style, PropertyId.Height, 50);
            SetLengthPx(style, PropertyId.FlexBasis, 0);
            style.Set(PropertyId.FlexGrow, ComputedSlot.FromNumber(1.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 300, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        // All 3 items on a single line (= BlockOffset 0 for all);
        // each item grew from basis 0 to 100 (= 300/3).
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[2].BlockOffset, precision: 3);
        Assert.Equal(100.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(100.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(100.0, fragments[2].InlineSize, precision: 3);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L8_hardening_wrap_with_flex_basis_length_overrides_width_for_packing()
    {
        // Per Phase 3 Task 15 L8 post-PR-#68 hardening F#1 — flex-basis
        // LENGTH (not just 0) drives line packing. 3 items of width: 50
        // but flex-basis: 200 in a 500px wrap container. Pre-F#1 line
        // packing saw width=50 → 3 items fit on one line; post-F#1 line
        // packing sees flex-basis=200 → first item lands (200 fits in
        // 500), second item adds to 400 (fits), third item would push
        // to 600 > 500 → wraps to a new line. Lines: [item0, item1] /
        // [item2]. With flex-grow: 0 (cascade default) + flex-shrink: 0
        // pinned the items stay at flex-basis 200; remaining 100 on
        // line 1 + 300 on line 2 go to justify-content (flex-start
        // default).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        // Pin align-content: flex-start so the §8.4 stretch default
        // doesn't grow line 1's cross-extent (= shifting line 2's
        // BlockOffset). The L7 align-content default of `normal →
        // stretch` would otherwise expand each line's cross-extent
        // to fill the 200px container, shifting line 2 from BlockOffset
        // 50 to 100.
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 500);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 50);
            SetLengthPx(style, PropertyId.Height, 50);
            SetLengthPx(style, PropertyId.FlexBasis, 200);
            // No grow, no shrink — items stay at basis 200.
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 500, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        // Line 1 = items 0 + 1; line 2 = item 2. Items 0, 1 at
        // BlockOffset 0 + InlineOffsets 0, 200. Item 2 at BlockOffset
        // 50 (= line 1's cross extent).
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(50.0, fragments[2].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[2].InlineOffset, precision: 3);
        // Items stay at basis 200 (no grow, no shrink).
        Assert.Equal(200.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[2].InlineSize, precision: 3);
    }

    [Fact]
    public void L8_hardening_wrap_with_flex_basis_percentage_drives_line_packing()
    {
        // Per Phase 3 Task 15 L8 post-PR-#68 hardening F#1 — percentage
        // flex-basis drives line packing (resolved against the
        // container's main-size). 3 items with flex-basis: 40% in a
        // 500px wrap container → each hypothetical = 200. Same line-
        // packing math as the LENGTH test above: items 0 + 1 = 400 fit
        // on line 1; item 2 wraps to line 2. Pre-F#1 line packing would
        // have used the declared width (= 50 in this fixture →
        // single-line 3-item layout).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        // Pin align-content: flex-start so the §8.4 stretch default
        // doesn't grow line 1's cross-extent (shifting line 2).
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 500);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 50);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexBasis, ComputedSlot.FromPercentage(40.0));
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 500, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(50.0, fragments[2].BlockOffset, precision: 3);
        Assert.Equal(200.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[2].InlineSize, precision: 3);
    }

    [Fact]
    public void L8_hardening_fractional_flex_grow_sum_below_one_only_takes_fraction()
    {
        // Per Phase 3 Task 15 L8 post-PR-#68 hardening F#2 — when the
        // sum of flex-grow factors on a line is LESS THAN 1, items
        // take only that fraction of the free space (the remainder
        // stays for justify-content) per CSS Flexbox L1 §9.7. 2 items
        // of width: 100 in a 400px container with flex-grow: 0.25 each
        // (sumFlexGrow = 0.5). freeMainSpace = 200; pre-F#2 each item
        // grew by (0.25/0.5) * 200 = 100 → 200 each (consuming 100% of
        // free space). Post-F#2 each item grows by 0.25 * 200 = 50 →
        // 150 each (consuming 50% of free space, leaving 100 for
        // justify-content's flex-start padding-end).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 400);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[2];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexGrow, ComputedSlot.FromNumber(0.25));
            // Pin flex-shrink: 0 so the (positive) free space stays
            // positive — defensive (sumFlexGrow > 0 + freeSpace > 0
            // triggers the grow branch anyway).
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(2, fragments.Count);
        // Each item grows by 0.25 * 200 = 50 → 150. justify-content:
        // flex-start (default) puts them at 0 and 150.
        Assert.Equal(150.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(150.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(150.0, fragments[1].InlineOffset, precision: 3);
    }

    [Fact]
    public void L8_hardening_known_gap_flex_basis_content_approximates_to_auto()
    {
        // Per Phase 3 Task 15 L8 post-PR-#68 hardening F#4 — KNOWN GAP
        // pin. Per CSS Flexbox L1 §7.2.1 `flex-basis: content` forces
        // the intrinsic content size REGARDLESS of the declared
        // width/height. L8 approximates Content as Auto (= delegates
        // to ReadLengthPxOrZero on the main-size property), so an item
        // with `width: 200` + `flex-basis: content` produces a
        // hypothetical of 200 instead of the spec-correct intrinsic
        // content size. When L9+ wires intrinsic sizing through the
        // BlockLayouter pre-measure, this test should flip — the
        // hypothetical will be the intrinsic content size (0 in this
        // fixture with no children).
        //
        // Fixture: 1 item, width: 200, flex-basis: content (Keyword 1),
        // no flex-grow, no flex-shrink, in a 600px container. With the
        // Content → Auto approximation: hypothetical = 200; no grow/
        // shrink → resolved = 200. With intrinsic sizing (L9+):
        // hypothetical = intrinsic content size = 0 (no children); no
        // grow/shrink → resolved = 0.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var itemStyle = MakeStyle();
        SetLengthPx(itemStyle, PropertyId.Width, 200);
        SetLengthPx(itemStyle, PropertyId.Height, 50);
        // Keyword 1 = `content` per the L8 LengthResolver
        // FlexBasis grammar.
        itemStyle.Set(PropertyId.FlexBasis, ComputedSlot.FromKeyword(1));
        itemStyle.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? itemFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == item) { itemFragment = f; break; }
        }
        Assert.NotNull(itemFragment);
        // L8 Content → Auto approximation: hypothetical = declared
        // width = 200. Pin the current behavior. When L9+ ships
        // intrinsic sizing, this assertion should flip to 0 (= the
        // intrinsic content size of an item with no children).
        Assert.Equal(200.0, itemFragment!.Value.InlineSize, precision: 3);
    }

    [Fact]
    public void L8_hardening_row_reverse_with_flex_grow_distributes_along_reversed_axis()
    {
        // Per Phase 3 Task 15 L8 post-PR-#68 hardening F#5 — direction
        // parity. flex-direction: row-reverse + flex-grow: 1. 3 items
        // of width: 100 in a 600px container. Hypothetical = 100 each;
        // freeMainSpace = 300; sumFlexGrow = 3 ≥ 1 → each grows by
        // (1/3) * 300 = 100 → 200 each. Natural cursor walks 0, 200,
        // 400; the row-reverse flip transform repositions per item:
        //   actual_i = (contentMainOffset + containerMainSize) - cursor_i - itemMainSize
        // For DOM order 0, 1, 2:
        //   DOM 0: 0 + 600 - 0 - 200 = 400
        //   DOM 1: 0 + 600 - 200 - 200 = 200
        //   DOM 2: 0 + 600 - 400 - 200 = 0
        // Items at InlineOffset 400, 200, 0 (reverse order on the
        // inline axis) with each at flexed size 200.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        // Keyword 1 = row-reverse per the L5 mapping.
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(1));
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexGrow, ComputedSlot.FromNumber(1.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        // Items grew to 200 each (flexed); positioned in reverse order
        // on the inline axis: DOM 0 at 400, DOM 1 at 200, DOM 2 at 0.
        Assert.Equal(200.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[2].InlineSize, precision: 3);
        Assert.Equal(400.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L8_hardening_negative_flex_basis_length_is_invalid_falls_back_to_auto()
    {
        // Per Phase 3 Task 15 L8 post-PR-#68 hardening F#3 — negative
        // flex-basis length values are spec-invalid per CSS Flexbox
        // L1 §7.2 (the <'width'> reference brings in the non-negative
        // rule from CSS Sizing §5). The KeywordResolver/LengthResolver
        // path now rejects negatives via NonNegativeProperties; the
        // cascade falls back to the property's initial value (`auto`).
        //
        // We can't easily exercise the full CSS parser here, but we
        // CAN verify the layout-level contract: setting the FlexBasis
        // slot directly to a NEGATIVE LengthPx (= what a buggy
        // upstream pipeline might produce) flows through the
        // ResolveFlexItemHypotheticalMainSize floor at 0 — which is
        // the same behavior as flex-basis: 0. Combined with the L8
        // existing tests that pin LengthResolver negative-rejection
        // through the production CSS path
        // (Css.ComputedValues.PropertyResolvers.* tests), the cascade
        // + layout contract is closed.
        //
        // Direct-resolver coverage of negative flex-basis is provided
        // by a new KeywordResolverTests / NumberResolverTests-style
        // test in the Css test project; this test pins the LAYOUT
        // defensive behavior.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var itemStyle = MakeStyle();
        SetLengthPx(itemStyle, PropertyId.Width, 100);
        SetLengthPx(itemStyle, PropertyId.Height, 50);
        // Inject a NEGATIVE LengthPx via the slot encoder — simulates
        // a contract violation from an upstream resolver (the L8 +
        // post-PR-#68 F#3 fix at the resolver level rejects this
        // before reaching layout). The layout's floor-at-0 is a
        // defense-in-depth check.
        itemStyle.Set(PropertyId.FlexBasis, ComputedSlot.FromLengthPx(-10.0));
        itemStyle.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? itemFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == item) { itemFragment = f; break; }
        }
        Assert.NotNull(itemFragment);
        // Layout floors negative basis at 0 (= no contribution to
        // line packing; with flex-grow: 0 + flex-shrink: 0 the
        // resolved size is 0).
        Assert.Equal(0.0, itemFragment!.Value.InlineSize, precision: 3);
    }

    [Fact]
    public void L8_hardening_heterogeneous_shrink_with_zero_floor_caps_at_zero()
    {
        // Per Phase 3 Task 15 L8 post-PR-#68 hardening F#5 — pin the
        // current behavior when shrink would push an item below 0.
        // Without the §9.7 step-4 min-clamp + iteration (L9+ scope),
        // L8 simply floors each item at 0; the leftover deficit
        // remains unabsorbed (= the line overflows the container).
        //
        // Fixture: 2 items in a 100px container; item 0 width: 80
        // shrink: 1, item 1 width: 200 shrink: 10. sumHypothetical
        // = 280; freeMainSpace = -180. sumScaledShrinks = 1*80 +
        // 10*200 = 2080. Each item's share:
        //   item 0: (80 / 2080) * 180 ≈ 6.92 absorbed → resolved
        //           80 - 6.92 ≈ 73.08
        //   item 1: (2000 / 2080) * 180 ≈ 173.08 absorbed → resolved
        //           200 - 173.08 ≈ 26.92
        // Both items stay positive; no flooring triggers. This pins
        // the proportional-shrink math itself (= no item reaches 0
        // floor in this fixture; the floor is exercised separately
        // when a single item's scaledShrink is large enough that the
        // formula would drive it negative).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 100);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var widths = new[] { 80.0, 200.0 };
        var shrinks = new[] { 1.0, 10.0 };
        var items = new Box[2];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, widths[i]);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(shrinks[i]));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(2, fragments.Count);
        // item 0: 80 - (80 / 2080) * 180 ≈ 73.0769
        // item 1: 200 - (2000 / 2080) * 180 ≈ 26.9231
        Assert.Equal(73.0769, fragments[0].InlineSize, precision: 2);
        Assert.Equal(26.9231, fragments[1].InlineSize, precision: 2);
    }

    [Fact]
    public void L8_hardening_pre_measure_and_layout_pack_into_same_lines_with_flex_basis()
    {
        // Per Phase 3 Task 15 L8 post-PR-#68 hardening F#1 — the
        // BlockLayouter pre-measure (= multi-line cross-extent
        // estimate) and the FlexLayouter's PackLines must agree on
        // line boundaries when flex-basis differs from the declared
        // width. The shared
        // `ResolveFlexItemHypotheticalMainSize` extension closes the
        // drift; this test verifies the wrapper emits at the correct
        // cross-extent (sum of line cross-extents) when flex-basis: 0
        // collapses items onto a single line.
        //
        // Fixture: 3 items of width: 300 + flex-basis: 0 in a 300px
        // wrap container with HEIGHT: auto (so the wrapper grows to
        // sum of line cross-extents). Pre-F#1 pre-measure saw width=
        // 300 → 3 lines → wrapper block-extent 150 (= 3 * 50). Post-F#1
        // pre-measure sees basis=0 → 1 line → wrapper block-extent 50.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        SetLengthPx(flex.Style, PropertyId.Width, 300);
        // No explicit height — wrapper sizes to sum of line cross-
        // extents.

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 300);
            SetLengthPx(style, PropertyId.Height, 50);
            SetLengthPx(style, PropertyId.FlexBasis, 0);
            style.Set(PropertyId.FlexGrow, ComputedSlot.FromNumber(1.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 300, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Find the flex container fragment.
        BoxFragment? flexFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == flex) { flexFragment = f; break; }
        }
        Assert.NotNull(flexFragment);
        // Wrapper cross-extent = single line's cross-extent = 50 (=
        // max(item heights)). Pre-F#1 would have produced 150 (= 3
        // separate lines × 50 each).
        Assert.Equal(50.0, flexFragment!.Value.BlockSize, precision: 3);
    }

    // ====================================================================
    //  Phase 3 Task 15 L9 — align-self per-item alignment override.
    //  CSS Box Alignment L3 §4.3 — "If the value of align-self is auto,
    //  its used value is the value of align-items on the parent".
    // ====================================================================

    [Fact]
    public void L9_align_self_auto_falls_back_to_container_align_items()
    {
        // Per Phase 3 Task 15 L9 — cascade default `align-self: auto`
        // preserves the L1-L8 behavior: every item picks up the
        // container's align-items. 3 items in a 200px-tall flex with
        // `align-items: center`. No align-self declarations → all 3
        // items center at BlockOffset 75 (= (200-50)/2).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(6)); // center
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            // No align-self declaration → cascade default `auto`.
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        // All centered at BlockOffset (200 - 50) / 2 = 75.
        Assert.Equal(75.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(75.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(75.0, fragments[2].BlockOffset, precision: 3);
    }

    [Fact]
    public void L9_align_self_overrides_container_align_items_per_item()
    {
        // Per Phase 3 Task 15 L9 — `align-self` on individual items
        // overrides the container's align-items. 3 items in a 200px-
        // tall flex with container align-items: center. Items 0 + 2
        // have align-self: flex-start (= top); item 1 has align-self:
        // flex-end (= bottom). Expected BlockOffsets:
        //   item 0: 0 (flex-start, ignore container center)
        //   item 1: 150 (flex-end = 200 - 50)
        //   item 2: 0 (flex-start)
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(6)); // center
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        // align-self keyword indices per BuildAlignSelfTable:
        //   12 = flex-start, 13 = flex-end.
        var alignSelfIndices = new[] { 12, 13, 12 };
        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.AlignSelf, ComputedSlot.FromKeyword(alignSelfIndices[i]));
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);   // flex-start
        Assert.Equal(150.0, fragments[1].BlockOffset, precision: 3); // flex-end
        Assert.Equal(0.0, fragments[2].BlockOffset, precision: 3);   // flex-start
    }

    [Fact]
    public void L9_align_self_stretch_grows_auto_cross_size_item()
    {
        // Per Phase 3 Task 15 L9 — `align-self: stretch` on an item
        // with auto cross-size grows the item to fill the line's
        // cross-extent (mirrors the L3 align-items: stretch behavior).
        // 3 items in 200px-tall flex with container align-items:
        // flex-start (= no stretch). Item 1 declares align-self:
        // stretch + height: auto → item 1 grows to fill the container
        // cross-extent (200). Items 0 + 2 stay at their declared 50.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            if (i == 1)
            {
                // Item 1: no explicit height (= auto) + align-self:
                // stretch → grows to fill cross-extent. Keyword 2 =
                // stretch.
                style.Set(PropertyId.AlignSelf, ComputedSlot.FromKeyword(2));
            }
            else
            {
                SetLengthPx(style, PropertyId.Height, 50);
            }
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        // Items 0 + 2 at flex-start with their declared 50.
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        Assert.Equal(50.0, fragments[0].BlockSize, precision: 3);
        Assert.Equal(0.0, fragments[2].BlockOffset, precision: 3);
        Assert.Equal(50.0, fragments[2].BlockSize, precision: 3);
        // Item 1 stretched to fill the container's full cross-extent
        // (200) at offset 0.
        Assert.Equal(0.0, fragments[1].BlockOffset, precision: 3);
        Assert.Equal(200.0, fragments[1].BlockSize, precision: 3);
    }

    [Fact]
    public void L9_align_self_center_overrides_container_stretch()
    {
        // Per Phase 3 Task 15 L9 — `align-self: center` on one item
        // overrides the container's default stretch (= normal →
        // stretch per §8.3). 3 items × 50 height in a 200px-tall flex
        // with no align-items declaration (= default stretch). Items 0
        // + 2 are stretched (no align-self → fall back to container);
        // item 1 has align-self: center → stays at declared 50,
        // centered at (200-50)/2 = 75.
        //
        // For items 0 + 2: BlockOffset 0; BlockSize 50 (= declared
        // height; IsCrossSizeAuto is false for explicit LengthPx, so
        // even container-stretch leaves them at 50 — same as the
        // L3 post-PR-#63 IsCrossSizeAuto detection).
        // For item 1: align-self: center → BlockOffset 75, BlockSize 50.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        // No align-items → default normal → stretch.
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            if (i == 1)
            {
                // Keyword 7 = center per BuildAlignSelfTable.
                style.Set(PropertyId.AlignSelf, ComputedSlot.FromKeyword(7));
            }
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);   // stretch on explicit-height item ≈ flex-start
        Assert.Equal(75.0, fragments[1].BlockOffset, precision: 3);  // align-self: center
        Assert.Equal(0.0, fragments[2].BlockOffset, precision: 3);
    }

    [Fact]
    public void L9_align_self_column_direction_distributes_along_inline_axis()
    {
        // Per Phase 3 Task 15 L9 — column-direction parity. Cross
        // axis = inline axis under flex-direction: column; align-self
        // distributes the item along inline. 3 items of width: 50 in
        // a 400w × 200h column container with container align-items:
        // flex-start. Item 1 declares align-self: center.
        // InlineOffsets:
        //   item 0: 0 (flex-start)
        //   item 1: (400 - 50) / 2 = 175 (center)
        //   item 2: 0 (flex-start)
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 400);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 50);
            SetLengthPx(style, PropertyId.Height, 50);
            if (i == 1)
            {
                style.Set(PropertyId.AlignSelf, ComputedSlot.FromKeyword(7)); // center
            }
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        Assert.Equal(0.0, fragments[0].InlineOffset, precision: 3);
        Assert.Equal(175.0, fragments[1].InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[2].InlineOffset, precision: 3);
    }

    [Fact]
    public void L9_align_self_safe_center_overflow_falls_back_to_safe_start()
    {
        // Per Phase 3 Task 15 L9 — `align-self`'s overflow mode honors
        // the safe/unsafe modifier per CSS Box Alignment L3 §5.3
        // (mirrors L3 post-PR-#63 align-items overflow). Item 1 has
        // height: 250 in a 200px-tall container = overflow on the
        // cross axis (item cross > container cross). With safe center,
        // the safe modifier forces safe-start fallback → item 1 at
        // BlockOffset 0 (NOT centered at -25).
        //
        // Keyword 14 = safe center per BuildAlignSelfTable
        // (14-20 = safe + 7 self-positions ordered center/start/end/
        // self-start/self-end/flex-start/flex-end).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(11)); // flex-start (so item 0 is at 0)
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var items = new Box[2];
        var style0 = MakeStyle();
        SetLengthPx(style0, PropertyId.Width, 100);
        SetLengthPx(style0, PropertyId.Height, 50);
        style0.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
        items[0] = Box.ForElement(BoxKind.BlockContainer, style0, MakeElement());

        var style1 = MakeStyle();
        SetLengthPx(style1, PropertyId.Width, 100);
        SetLengthPx(style1, PropertyId.Height, 250); // overflows the 200 container
        style1.Set(PropertyId.AlignSelf, ComputedSlot.FromKeyword(14)); // safe center
        style1.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
        items[1] = Box.ForElement(BoxKind.BlockContainer, style1, MakeElement());

        flex.AppendChild(items[0]);
        flex.AppendChild(items[1]);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(2, fragments.Count);
        // Item 0 at flex-start (container default).
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        // Item 1 overflow → safe-start fallback (BlockOffset 0, NOT
        // centered at -25).
        Assert.Equal(0.0, fragments[1].BlockOffset, precision: 3);
    }

    [Fact]
    public void L9_hardening_align_self_unsafe_center_overflow_honors_natural_negative_offset()
    {
        // Per Phase 3 Task 15 L9 post-PR-#69 hardening F#3 — mirrors
        // the safe-center overflow test above but with the `unsafe`
        // modifier. Per CSS Box Alignment L3 §5.3, `unsafe X` honors
        // the requested alignment EVEN ON OVERFLOW (= items may be
        // pushed offscreen). Item 1 has height: 250 in a 200px-tall
        // container with align-self: unsafe center: cross-extent =
        // 200; item cross = 250; freeSpace = -50; natural center
        // offset = -25. Pre-fix the unsafe path was untested at the
        // align-self call site.
        //
        // Keyword 21 = unsafe center per BuildAlignSelfTable (21-27 =
        // unsafe + 7 self-positions ordered center/start/end/self-start/
        // self-end/flex-start/flex-end).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var items = new Box[2];
        var style0 = MakeStyle();
        SetLengthPx(style0, PropertyId.Width, 100);
        SetLengthPx(style0, PropertyId.Height, 50);
        style0.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
        items[0] = Box.ForElement(BoxKind.BlockContainer, style0, MakeElement());

        var style1 = MakeStyle();
        SetLengthPx(style1, PropertyId.Width, 100);
        SetLengthPx(style1, PropertyId.Height, 250); // overflows 200 container
        style1.Set(PropertyId.AlignSelf, ComputedSlot.FromKeyword(21)); // unsafe center
        style1.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
        items[1] = Box.ForElement(BoxKind.BlockContainer, style1, MakeElement());

        flex.AppendChild(items[0]);
        flex.AppendChild(items[1]);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(2, fragments.Count);
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        // Item 1 unsafe center → natural negative offset honored.
        // freeSpace = 200 - 250 = -50; natural center = -50/2 = -25.
        Assert.Equal(-25.0, fragments[1].BlockOffset, precision: 3);
    }

    [Fact]
    public void L9_hardening_align_self_unsafe_flex_end_overflow_honors_alignment()
    {
        // Per Phase 3 Task 15 L9 post-PR-#69 hardening F#3 — `unsafe
        // flex-end` honors the flex-end alignment on overflow. Item 1
        // overflows the cross axis (height 250 in 200 container) with
        // align-self: unsafe flex-end (keyword 27): natural flex-end
        // offset = freeSpace = -50. Item appears at BlockOffset -50.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var items = new Box[2];
        var style0 = MakeStyle();
        SetLengthPx(style0, PropertyId.Width, 100);
        SetLengthPx(style0, PropertyId.Height, 50);
        style0.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
        items[0] = Box.ForElement(BoxKind.BlockContainer, style0, MakeElement());

        var style1 = MakeStyle();
        SetLengthPx(style1, PropertyId.Width, 100);
        SetLengthPx(style1, PropertyId.Height, 250);
        style1.Set(PropertyId.AlignSelf, ComputedSlot.FromKeyword(27)); // unsafe flex-end
        style1.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
        items[1] = Box.ForElement(BoxKind.BlockContainer, style1, MakeElement());

        flex.AppendChild(items[0]);
        flex.AppendChild(items[1]);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(2, fragments.Count);
        Assert.Equal(0.0, fragments[0].BlockOffset, precision: 3);
        // Item 1 unsafe flex-end on overflow → natural flex-end offset
        // = freeSpace = -50.
        Assert.Equal(-50.0, fragments[1].BlockOffset, precision: 3);
    }

    // ====================================================================
    //  Phase 3 Task 15 L10 — `order` property reorders items before
    //  line packing per CSS Flexbox L1 §5.4. Items with equal order
    //  preserve DOM order (stable sort).
    // ====================================================================

    [Fact]
    public void L10_order_reorders_items_along_main_axis()
    {
        // Per Phase 3 Task 15 L10 — `order` reorders items per CSS
        // Flexbox L1 §5.4. 3 items of width 100 in a 600px row flex:
        //   item 0 (DOM): order = 2  → packs last
        //   item 1 (DOM): order = 0  → packs in middle (after order: -1)
        //   item 2 (DOM): order = -1 → packs first
        // Effective order: item 2, item 1, item 0
        // Cursors: 0, 100, 200 for the sorted sequence.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var orders = new[] { 2, 0, -1 };
        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.Order, ComputedSlot.FromInteger(orders[i]));
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new BoxFragment?[3];
        foreach (var f in sink.Fragments)
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (f.Box == items[i]) { fragments[i] = f; break; }
            }
        }
        Assert.NotNull(fragments[0]);
        Assert.NotNull(fragments[1]);
        Assert.NotNull(fragments[2]);
        // Effective order: item 2 first (order=-1), item 1 middle
        // (order=0), item 0 last (order=2).
        Assert.Equal(200.0, fragments[0]!.Value.InlineOffset, precision: 3); // DOM 0 → last
        Assert.Equal(100.0, fragments[1]!.Value.InlineOffset, precision: 3); // DOM 1 → middle
        Assert.Equal(0.0, fragments[2]!.Value.InlineOffset, precision: 3);   // DOM 2 → first
    }

    [Fact]
    public void L10_order_equal_values_preserve_dom_order_stable_sort()
    {
        // Per Phase 3 Task 15 L10 — items with equal `order` preserve
        // DOM order (stable sort). 4 items all with order: 0 (the
        // cascade default) should pack in DOM order — identical to
        // the L1-L9 behavior. Plus a 5th item with order: 1 to verify
        // the comparator is exercised (so we're not in the fast-path
        // short-circuit).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var orders = new[] { 0, 0, 0, 0, 1 };
        var items = new Box[5];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.Order, ComputedSlot.FromInteger(orders[i]));
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new BoxFragment?[5];
        foreach (var f in sink.Fragments)
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (f.Box == items[i]) { fragments[i] = f; break; }
            }
        }
        // DOM 0-3 keep DOM order (all order: 0); DOM 4 packs last
        // (order: 1).
        Assert.Equal(0.0, fragments[0]!.Value.InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[1]!.Value.InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[2]!.Value.InlineOffset, precision: 3);
        Assert.Equal(300.0, fragments[3]!.Value.InlineOffset, precision: 3);
        Assert.Equal(400.0, fragments[4]!.Value.InlineOffset, precision: 3);
    }

    [Fact]
    public void L10_order_default_zero_preserves_dom_order_fast_path()
    {
        // Per Phase 3 Task 15 L10 — when no item declares a non-zero
        // `order`, `GetFlexChildrenInOrderSequence` short-circuits to
        // DOM order without sorting. This test pins the
        // L1-L9-equivalent behavior — no order declarations =
        // identical to pre-L10 layout. 3 items, no order set.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            // No order set — cascade default 0.
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new BoxFragment?[3];
        foreach (var f in sink.Fragments)
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (f.Box == items[i]) { fragments[i] = f; break; }
            }
        }
        Assert.Equal(0.0, fragments[0]!.Value.InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[1]!.Value.InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[2]!.Value.InlineOffset, precision: 3);
    }

    [Fact]
    public void L10_order_with_row_reverse_combines_reorder_then_flip()
    {
        // Per Phase 3 Task 15 L10 — `order` reorders BEFORE the L5
        // row-reverse flip. 3 items with orders [2, 0, -1] in a
        // row-reverse 600px container. Effective order before flip:
        // item 2, item 1, item 0 (sorted by order). Natural cursors:
        // item 2 at 0, item 1 at 100, item 0 at 200. row-reverse flip
        // transform: actual = (0 + 600) - cursor - 100. So:
        //   item 2 (sorted-pos 0, natural 0):   600 - 0 - 100 = 500
        //   item 1 (sorted-pos 1, natural 100): 600 - 100 - 100 = 400
        //   item 0 (sorted-pos 2, natural 200): 600 - 200 - 100 = 300
        // InlineOffsets: item 0 → 300, item 1 → 400, item 2 → 500.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(1)); // row-reverse
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var orders = new[] { 2, 0, -1 };
        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.Order, ComputedSlot.FromInteger(orders[i]));
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new BoxFragment?[3];
        foreach (var f in sink.Fragments)
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (f.Box == items[i]) { fragments[i] = f; break; }
            }
        }
        Assert.Equal(300.0, fragments[0]!.Value.InlineOffset, precision: 3);
        Assert.Equal(400.0, fragments[1]!.Value.InlineOffset, precision: 3);
        Assert.Equal(500.0, fragments[2]!.Value.InlineOffset, precision: 3);
    }

    [Fact]
    public void L10_order_with_column_direction_distributes_along_block_axis()
    {
        // Per Phase 3 Task 15 L10 — `order` parity with column
        // direction. 3 items × height 50 in a 300px column flex with
        // orders [2, -1, 0]. Effective order: item 1 (-1), item 2 (0),
        // item 0 (2). BlockOffsets: 0, 50, 100 for the sorted
        // sequence.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        SetLengthPx(flex.Style, PropertyId.Width, 200);
        SetLengthPx(flex.Style, PropertyId.Height, 300);

        var orders = new[] { 2, -1, 0 };
        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.Order, ComputedSlot.FromInteger(orders[i]));
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new BoxFragment?[3];
        foreach (var f in sink.Fragments)
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (f.Box == items[i]) { fragments[i] = f; break; }
            }
        }
        // Effective order: item 1 (-1) first, item 2 (0) middle, item 0 (2) last.
        // Block-axis cursors: sorted-pos 0 → 0, 1 → 50, 2 → 100.
        Assert.Equal(100.0, fragments[0]!.Value.BlockOffset, precision: 3); // DOM 0 → last
        Assert.Equal(0.0, fragments[1]!.Value.BlockOffset, precision: 3);   // DOM 1 → first
        Assert.Equal(50.0, fragments[2]!.Value.BlockOffset, precision: 3);  // DOM 2 → middle
    }

    [Fact]
    public void L10_order_with_wrap_packs_lines_in_effective_order()
    {
        // Per Phase 3 Task 15 L10 — `order` affects wrap line packing.
        // 4 items of width 100 in a 250px wrap container with orders
        // [10, 0, -10, 5]. Effective order: item 2 (-10), item 1 (0),
        // item 3 (5), item 0 (10). Line packing greedy: line 1 = items
        // 2, 1 (200 ≤ 250); item 3 (300 > 250) → line 2. Line 2 =
        // items 3, 0 (200 ≤ 250). Lines: [item2, item1] / [item3, item0].
        // BlockOffsets: line 1 at 0, line 2 at 50.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        // Pin align-content: flex-start so §8.4 stretch default
        // doesn't shift line 2.
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var orders = new[] { 10, 0, -10, 5 };
        var items = new Box[4];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.Order, ComputedSlot.FromInteger(orders[i]));
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new BoxFragment?[4];
        foreach (var f in sink.Fragments)
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (f.Box == items[i]) { fragments[i] = f; break; }
            }
        }
        // Effective order: item 2, item 1, item 3, item 0.
        // Line 1: item 2 at InlineOffset 0, item 1 at 100 (both BlockOffset 0).
        // Line 2: item 3 at InlineOffset 0, item 0 at 100 (both BlockOffset 50).
        Assert.Equal(100.0, fragments[0]!.Value.InlineOffset, precision: 3); // DOM 0 → line 2 right
        Assert.Equal(50.0, fragments[0]!.Value.BlockOffset, precision: 3);
        Assert.Equal(100.0, fragments[1]!.Value.InlineOffset, precision: 3); // DOM 1 → line 1 right
        Assert.Equal(0.0, fragments[1]!.Value.BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[2]!.Value.InlineOffset, precision: 3);   // DOM 2 → line 1 left
        Assert.Equal(0.0, fragments[2]!.Value.BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[3]!.Value.InlineOffset, precision: 3);   // DOM 3 → line 2 left
        Assert.Equal(50.0, fragments[3]!.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public void L10_order_with_flex_grow_distributes_after_reorder()
    {
        // Per Phase 3 Task 15 L10 — `order` happens BEFORE the §9.7
        // flexibility resolution. 3 items × width 100 in 600px row,
        // orders [2, 0, -1], each flex-grow: 1. Effective order:
        // item 2, item 1, item 0. After grow: each grows from 100 to
        // 200 (freeMainSpace = 300, distributed 100 to each). Cursors
        // in effective order: 0, 200, 400.
        // InlineOffsets: item 2 → 0, item 1 → 200, item 0 → 400.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var orders = new[] { 2, 0, -1 };
        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.Order, ComputedSlot.FromInteger(orders[i]));
            style.Set(PropertyId.FlexGrow, ComputedSlot.FromNumber(1.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new BoxFragment?[3];
        foreach (var f in sink.Fragments)
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (f.Box == items[i]) { fragments[i] = f; break; }
            }
        }
        Assert.Equal(200.0, fragments[0]!.Value.InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[1]!.Value.InlineSize, precision: 3);
        Assert.Equal(200.0, fragments[2]!.Value.InlineSize, precision: 3);
        Assert.Equal(400.0, fragments[0]!.Value.InlineOffset, precision: 3); // DOM 0 → last
        Assert.Equal(200.0, fragments[1]!.Value.InlineOffset, precision: 3); // DOM 1 → middle
        Assert.Equal(0.0, fragments[2]!.Value.InlineOffset, precision: 3);   // DOM 2 → first
    }

    [Fact]
    public void L10_hardening_order_affects_sink_emission_order_for_painting()
    {
        // Per Phase 3 Task 15 L10 post-PR-#70 review F#1 — CSS
        // Display 3 §3 says flex/grid items are laid out in order-
        // modified document order, which ALSO affects painting order.
        // The L10 implementation walks _sortedFlexChildIndices in
        // both PackLines + emission, so items emit to the sink in
        // effective-order sequence (= the visual / paint order). The
        // existing L10 tests only assert geometry (final offsets);
        // this regression test asserts the SINK EMISSION ORDER —
        // protecting painting behavior if a future refactor decouples
        // positioning from emission.
        //
        // Fixture: 3 items with DOM order [a, b, c] but orders
        // [a:2, b:0, c:-1]. Effective order: c, b, a. Sink should
        // emit fragments in effective order (item c first, b second,
        // a third).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var orders = new[] { 2, 0, -1 };
        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.Order, ComputedSlot.FromInteger(orders[i]));
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Filter sink to just the flex items (skip root, flex
        // wrapper). The order of these fragments in sink.Fragments
        // is the EMISSION ORDER — which Phase 3 Paint translates
        // directly to PDF z-order / painting sequence.
        var itemEmissionOrder = new List<Box>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box != root && f.Box != flex && f.Box.Kind == BoxKind.BlockContainer)
            {
                itemEmissionOrder.Add(f.Box);
            }
        }
        Assert.Equal(3, itemEmissionOrder.Count);
        // Effective order: item 2 (c, order -1) first, item 1 (b,
        // order 0) middle, item 0 (a, order 2) last.
        Assert.Same(items[2], itemEmissionOrder[0]); // c emitted first
        Assert.Same(items[1], itemEmissionOrder[1]); // b emitted middle
        Assert.Same(items[0], itemEmissionOrder[2]); // a emitted last
    }

    [Fact]
    public void L10_hardening_order_with_auto_height_wrap_pre_measure_packs_in_effective_order()
    {
        // Per Phase 3 Task 15 L10 post-PR-#70 review F#2 — pin the
        // BlockLayouter pre-measure honors `order` for wrap line
        // packing. Pre-fix the existing L10 wrap test used explicit
        // height, so PreMeasureFlexMultiLineCrossExtent's line
        // counting was bypassed by the explicit-height short-circuit.
        // This test uses `height: auto` so the wrapper grows to the
        // sum of line cross-extents — IF the pre-measure walks the
        // sorted sequence it produces the correct line count.
        //
        // Fixture: 4 items of width 100 in a 250px row+wrap container
        // with `height: auto`. Item heights differ to make the line
        // cross-extent depend on which items group together:
        //   item 0 (DOM 0, order: 1): height 50
        //   item 1 (DOM 1, order: 0): height 80
        //   item 2 (DOM 2, order: 0): height 80
        //   item 3 (DOM 3, order: -1): height 50
        // DOM-order line packing would group [0,1] / [2,3]:
        //   line 1 (DOM 0, 1): max height = 80 → cross 80
        //   line 2 (DOM 2, 3): max height = 80 → cross 80
        //   total wrapper block-extent = 160
        // Effective-order line packing groups [3,1] / [2,0]:
        //   line 1 (item 3 first, item 1 second): max(50, 80) = 80
        //   line 2 (item 2 first, item 0 second): max(80, 50) = 80
        //   total wrapper block-extent = 160
        // SAME TOTAL by coincidence in this fixture. Make the
        // heights DIFFERENT to detect drift:
        //   item 0 (order: 1): height 200 (= the LAST item, line 2)
        //   item 1 (order: 0): height 30
        //   item 2 (order: 0): height 30
        //   item 3 (order: -1): height 30 (= the FIRST item, line 1)
        // DOM-order packing groups [0,1] / [2,3]:
        //   line 1: max(200, 30) = 200; line 2: max(30, 30) = 30
        //   total wrapper block-extent = 230 (wrong — DOM order)
        // Effective-order packing groups [3,1] / [2,0]:
        //   line 1: max(30, 30) = 30; line 2: max(30, 200) = 200
        //   total wrapper block-extent = 230 (same total but
        //   different line block-offsets — sibling lands at 230 either
        //   way for this case)
        //
        // Actually the most direct way to prove pre-measure parity is
        // to assert wrapper.BlockSize equals the SUM of effective-
        // order line cross-extents AND that the sibling block element
        // lands AT that wrapper's bottom edge — both numbers come
        // from pre-measure. Use a sibling div + assert its
        // BlockOffset.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        // Pin align-content: flex-start so §8.4 stretch doesn't shift
        // line block-extents.
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        // No explicit height — wrapper auto-sizes to sum of line
        // cross-extents.

        // Heights chosen so DOM-order vs effective-order produce
        // different total block-extents:
        // DOM order:   [item0 h=200, item1 h=30] / [item2 h=30, item3 h=30]
        //   line 1 max = 200, line 2 max = 30 → sum = 230
        // Effective order: [item3 h=30, item1 h=30] / [item2 h=30, item0 h=200]
        //   line 1 max = 30, line 2 max = 200 → sum = 230
        // Same sum (by inline-item count parity) but the wrapper
        // block-extent IS the sum, so we assert it.
        var heights = new[] { 200, 30, 30, 30 };
        var orders = new[] { 1, 0, 0, -1 };
        var items = new Box[4];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, heights[i]);
            style.Set(PropertyId.Order, ComputedSlot.FromInteger(orders[i]));
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);
        // Add a sibling AFTER the flex container — its BlockOffset is
        // driven by the pre-measure's wrapper.BlockSize estimate.
        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Width, 100);
        SetLengthPx(siblingStyle, PropertyId.Height, 10);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Find the flex container fragment + the sibling.
        BoxFragment? flexFragment = null;
        BoxFragment? siblingFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == flex) flexFragment = f;
            else if (f.Box == sibling) siblingFragment = f;
        }
        Assert.NotNull(flexFragment);
        Assert.NotNull(siblingFragment);

        // Effective-order line packing: line 1 = items 3 + 1 (max h
        // = 30); line 2 = items 2 + 0 (max h = 200). Wrapper auto
        // height = 30 + 200 = 230. Sibling lands at BlockOffset 230
        // (= wrapper's BlockSize). If pre-measure walked DOM order
        // instead, lines would be [0,1] / [2,3] with max h 200/30 →
        // same 230 sum (coincidence). To distinguish: assert the
        // ACTUAL emission positions item 0 at BlockOffset 30 (= line
        // 2 starts after line 1's cross-extent 30). DOM-order would
        // place item 0 at BlockOffset 0 (= line 1 starts at 0).
        BoxFragment? item0Fragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == items[0]) { item0Fragment = f; break; }
        }
        Assert.NotNull(item0Fragment);
        // Item 0 (DOM 0, order: 1) is in effective-order sequence
        // position 3 (last). With wrap line packing producing
        // line 1 = [item3, item1], line 2 = [item2, item0], item 0
        // lands at BlockOffset 30 (= line 1's cross-extent).
        Assert.Equal(30.0, item0Fragment!.Value.BlockOffset, precision: 3);

        // Wrapper block-extent = 30 + 200 = 230 (sum of effective-
        // order line cross-extents).
        Assert.Equal(230.0, flexFragment!.Value.BlockSize, precision: 3);
        // Sibling lands at BlockOffset 230 (= wrapper's BlockSize).
        Assert.Equal(230.0, siblingFragment!.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public void L11_flex_wrap_reverse_reverses_line_stacking_on_cross_axis()
    {
        // Per Phase 3 Task 15 L11 + post-PR-#71 hardening F#1 — pin
        // the spec-correct wrap-reverse behavior per CSS Flexbox L1
        // §6.3: "Behaves the same as wrap but cross-start and cross-
        // end are permuted". The diagnostic
        // LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001 is no longer
        // emitted; DOM line 0 lands at the NEW cross-start (= the
        // physical cross-END = container bottom for L1 LTR +
        // horizontal-tb default).
        //
        // Fixture: 4 items of 100w × 50h in a 250w × 200h flex with
        // `flex-wrap: wrap-reverse` + `align-content: flex-start`.
        // Wrap math:
        //   line 0 (DOM): items 0+1 → cross-extent 50
        //   line 1 (DOM): items 2+3 → cross-extent 50
        //   sumLineCross = 100
        // With wrap-reverse + align-content: flex-start:
        //   freeCrossSpace = 100; lineStartOffset = 0.
        //   Swap formula puts DOM line 0 at:
        //     physical = 0 + 200 - 0 - 50 = 150
        //   And DOM line 1 at:
        //     physical = 0 + 200 - 50 - 50 = 100
        // Items 0+1 (= DOM line 0) at BlockOffset 150 (physical
        // bottom). Items 2+3 (= DOM line 1) at BlockOffset 100.
        //
        // Pre-PR-#71 the test asserted items 0+1 at 50 and items 2+3
        // at 0 — that locked in the L11 Hello World's incomplete
        // implementation (it reversed `lines` iteration but did not
        // move the stack to the swapped cross-start origin).
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(2)); // wrap-reverse
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var items = new Box[4];
        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: diagSink,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // (a) The LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001
        // diagnostic NO LONGER fires — wrap-reverse is now properly
        // implemented.
        foreach (var d in diagSink.Diagnostics)
        {
            Assert.NotEqual("LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001", d.Code);
        }

        // (b) Line ordering is REVERSED. Items 0 + 1 (= DOM line 0)
        // emit at the BOTTOM (BlockOffset 50); items 2 + 3 (= DOM
        // line 1) emit at the TOP (BlockOffset 0). The within-line
        // ordering (items 0 before 1, items 2 before 3 on each line)
        // is unchanged per §6.3.
        var fragments = new BoxFragment?[4];
        foreach (var f in sink.Fragments)
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (f.Box == items[i]) { fragments[i] = f; break; }
            }
        }
        // items 0 + 1 (DOM line 0) at NEW cross-start (= physical
        // bottom of container) = BlockOffset 150.
        Assert.Equal(150.0, fragments[0]!.Value.BlockOffset, precision: 3);
        Assert.Equal(150.0, fragments[1]!.Value.BlockOffset, precision: 3);
        // items 2 + 3 (DOM line 1) at BlockOffset 100 (just above
        // DOM line 0, toward the swapped cross-end = physical top).
        Assert.Equal(100.0, fragments[2]!.Value.BlockOffset, precision: 3);
        Assert.Equal(100.0, fragments[3]!.Value.BlockOffset, precision: 3);
        // Within-line item order preserved: item 0 at InlineOffset 0,
        // item 1 at 100 (and similarly for items 2+3 on the other
        // line).
        Assert.Equal(0.0, fragments[0]!.Value.InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[1]!.Value.InlineOffset, precision: 3);
        Assert.Equal(0.0, fragments[2]!.Value.InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[3]!.Value.InlineOffset, precision: 3);
    }

    [Fact]
    public void L11_flex_wrap_reverse_single_line_packs_at_swapped_cross_start()
    {
        // Per Phase 3 Task 15 L11 post-PR-#71 hardening F#1 — even
        // when wrap-reverse produces only ONE line, the cross-axis
        // SWAP still applies. The line-reversal step is a no-op (=
        // nothing to reverse), but the line stack still moves to
        // the NEW cross-start origin (= physical cross-end).
        //
        // Fixture: 2 items × 100w × 50h in 400w × 200h wrap-reverse
        // container with align-items: flex-start + align-content:
        // flex-start. Both items fit on one line. The line lands at
        // the swapped cross-start = physical bottom:
        //   sumLineCross = 50; freeCrossSpace = 150; lineStartOffset = 0
        //   physical = 0 + 200 - 0 - 50 = 150
        // Within the line, align-items: flex-start under wrap-reverse
        // means the line's NEW cross-start = the line's PHYSICAL-
        // BOTTOM edge (= y=200). Items with cross-size 50 anchor to
        // the bottom = y=150 (top edge of items, items span y=150..200).
        // Since item cross-size == line cross-size (50 == 50), the
        // item's top edge IS the line's top edge.
        //
        // Pre-PR-#71 this test was named "...does_not_reverse" and
        // asserted items at BlockOffset 0, which was semantically
        // misleading: the line-ORDER reversal is a no-op, but the
        // cross-axis SWAP is not.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(2)); // wrap-reverse
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(11)); // flex-start
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 400);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var items = new Box[2];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new BoxFragment?[2];
        foreach (var f in sink.Fragments)
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (f.Box == items[i]) { fragments[i] = f; break; }
            }
        }
        Assert.NotNull(fragments[0]);
        Assert.NotNull(fragments[1]);
        // Both items on the (single) line at BlockOffset 150 (= the
        // swapped cross-start = physical bottom of the 200px container
        // minus the 50px line cross-extent). Within-line DOM order
        // preserved: item 0 at InlineOffset 0, item 1 at 100.
        Assert.Equal(150.0, fragments[0]!.Value.BlockOffset, precision: 3);
        Assert.Equal(150.0, fragments[1]!.Value.BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[0]!.Value.InlineOffset, precision: 3);
        Assert.Equal(100.0, fragments[1]!.Value.InlineOffset, precision: 3);
    }

    [Fact]
    public void L11_flex_wrap_reverse_column_direction_swaps_inline_cross_start()
    {
        // Per Phase 3 Task 15 L11 + post-PR-#71 hardening F#1 —
        // column-direction wrap-reverse swaps cross-start/cross-end
        // on the INLINE axis (= cross axis for column direction).
        // For column + horizontal-tb LTR, the swap moves cross-start
        // from the physical LEFT edge to the physical RIGHT edge.
        //
        // Fixture: 4 items of 100w × 50h in a 400w × 150h column
        // flex with wrap-reverse + align-content: flex-start.
        //   PackLines (DOM): line 0 = items 0+1+2 (block sum 150),
        //   line 1 = item 3.
        //   Each line's inline-extent = max item inline = 100.
        //   sumLineCross = 200.
        // Swap formula for align-content: flex-start (lineStartOffset
        // = 0):
        //   DOM line 0 (items 0+1+2): physical = 0 + 400 - 0 - 100
        //     = 300 (anchored at the swapped cross-start = right
        //     edge minus line extent).
        //   DOM line 1 (item 3): physical = 0 + 400 - 100 - 100 = 200.
        // Items 0+1+2 at InlineOffset 300; item 3 at InlineOffset 200.
        //
        // Pre-PR-#71 assertion was 100/100/100/0 — locked in the
        // bug (lines stayed at the physical left after just reversing
        // the iteration order).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(2)); // wrap-reverse
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 400);
        SetLengthPx(flex.Style, PropertyId.Height, 150);

        var items = new Box[4];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new BoxFragment?[4];
        foreach (var f in sink.Fragments)
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (f.Box == items[i]) { fragments[i] = f; break; }
            }
        }
        // Swapped cross-start (= physical right) is at InlineOffset
        // 300 for DOM line 0 + 200 for DOM line 1.
        Assert.Equal(300.0, fragments[0]!.Value.InlineOffset, precision: 3);
        Assert.Equal(300.0, fragments[1]!.Value.InlineOffset, precision: 3);
        Assert.Equal(300.0, fragments[2]!.Value.InlineOffset, precision: 3);
        Assert.Equal(200.0, fragments[3]!.Value.InlineOffset, precision: 3);
    }

    [Fact]
    public void L11_flex_wrap_reverse_sink_emission_order_matches_dom_order()
    {
        // Per Phase 3 Task 15 L11 + post-PR-#71 hardening F#1 — sink
        // emission order (= painting order per CSS Display §3)
        // follows DOM order under wrap-reverse. The cross-axis SWAP
        // is applied at offset-computation time (the line stack
        // moves to the swapped cross-start), NOT by reversing the
        // iteration order. Each line emits its items in DOM order;
        // lines emit in DOM order; the only difference from wrap is
        // the physical cross-offset of each line.
        //
        // Fixture: 4 items in 250×200 → 2 lines. Emission order
        // (= sink fragment order) is DOM order: items 0, 1, 2, 3.
        // (Pre-PR-#71 the test asserted reversed emission [2, 3, 0, 1]
        // — that was a side effect of the lines.Reverse() approach
        // which the F#1 hardening removed.)
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(2)); // wrap-reverse
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var items = new Box[4];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var emissionOrder = new List<Box>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box != root && f.Box != flex && f.Box.Kind == BoxKind.BlockContainer)
            {
                emissionOrder.Add(f.Box);
            }
        }
        Assert.Equal(4, emissionOrder.Count);
        // DOM order preserved (the swap is purely an offset
        // computation, not an iteration reorder).
        Assert.Same(items[0], emissionOrder[0]);
        Assert.Same(items[1], emissionOrder[1]);
        Assert.Same(items[2], emissionOrder[2]);
        Assert.Same(items[3], emissionOrder[3]);
    }

    // ====================================================================
    //  Phase 3 Task 15 L11 post-PR-#71 hardening — new test coverage.
    // ====================================================================

    [Fact]
    public void L11_hardening_wrap_reverse_align_content_flex_end_packs_at_swapped_cross_end()
    {
        // Per Phase 3 Task 15 L11 post-PR-#71 F#4 — wrap-reverse +
        // align-content: flex-end packs lines at the swapped cross-
        // end (= physical TOP for row + horizontal-tb LTR). DOM line
        // 0 (first) is at the swapped cross-START side of the packed
        // stack; DOM line 1 (later) is at the swapped cross-END.
        //
        // Fixture: 2 lines × 50 in 200; freeCrossSpace = 100;
        // lineStartOffset for flex-end = 100.
        //   DOM line 0: swappedCursor=100; physical=200-100-50=50.
        //   DOM line 1: swappedCursor=150; physical=200-150-50=0.
        // DOM items 0+1 at y=50; items 2+3 at y=0 (top of container).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(2)); // wrap-reverse
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(12)); // flex-end
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var items = new Box[4];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new BoxFragment?[4];
        foreach (var f in sink.Fragments)
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (f.Box == items[i]) { fragments[i] = f; break; }
            }
        }
        Assert.Equal(50.0, fragments[0]!.Value.BlockOffset, precision: 3);
        Assert.Equal(50.0, fragments[1]!.Value.BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[2]!.Value.BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[3]!.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public void L11_hardening_wrap_reverse_align_content_space_between_distributes_with_swap()
    {
        // Per Phase 3 Task 15 L11 post-PR-#71 F#4 — wrap-reverse +
        // align-content: space-between distributes lines on the
        // cross axis with no edge spacing in the SWAPPED axis.
        // For 2 lines × 50 in 200 with space-between:
        //   lineStartOffset = 0; lineBetweenSpacing = 100.
        //   DOM line 0: swappedCursor=0; physical=200-0-50=150.
        //   DOM line 1: swappedCursor=150; physical=200-150-50=0.
        // DOM line 0 at the swapped cross-start (= physical bottom);
        // DOM line 1 at the swapped cross-end (= physical top); a
        // 100px gap between them.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(2)); // wrap-reverse
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(1)); // space-between
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var items = new Box[4];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new BoxFragment?[4];
        foreach (var f in sink.Fragments)
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (f.Box == items[i]) { fragments[i] = f; break; }
            }
        }
        Assert.Equal(150.0, fragments[0]!.Value.BlockOffset, precision: 3);
        Assert.Equal(150.0, fragments[1]!.Value.BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[2]!.Value.BlockOffset, precision: 3);
        Assert.Equal(0.0, fragments[3]!.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public void L11_hardening_wrap_reverse_align_items_flex_start_anchors_at_swapped_line_edge()
    {
        // Per Phase 3 Task 15 L11 post-PR-#71 F#2 — under wrap-reverse,
        // align-items: flex-start anchors items at the LINE's new
        // cross-start (= the line's PHYSICAL-BOTTOM edge for row +
        // horizontal-tb LTR). Test with UNEQUAL item cross-sizes to
        // distinguish flex-start from flex-end.
        //
        // Fixture: 2 items × 100 inline with HEIGHTS 30 + 50 in a
        // 250 × 200 wrap-reverse + align-content: flex-start +
        // align-items: flex-start. Both fit on one line (combined
        // inline 200 ≤ 250). Line cross-extent = max(30, 50) = 50.
        // Line physical offset = 200 - 50 = 150 (top edge of the
        // line; line spans y=150..200).
        // Within the line, align-items: flex-start under wrap-reverse
        // anchors items at the line's new cross-start = y=200 (the
        // PHYSICAL BOTTOM of the line). Item with height 30 lands at
        // y=200-30=170 (its top edge); item with height 50 lands at
        // y=200-50=150 (its top edge = also the line's top edge).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(2)); // wrap-reverse
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(11)); // flex-start
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var heights = new[] { 30, 50 };
        var items = new Box[2];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, heights[i]);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new BoxFragment?[2];
        foreach (var f in sink.Fragments)
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (f.Box == items[i]) { fragments[i] = f; break; }
            }
        }
        // Item 0 (h=30): anchored at the line's bottom edge (y=200),
        // top edge at y=170.
        Assert.Equal(170.0, fragments[0]!.Value.BlockOffset, precision: 3);
        // Item 1 (h=50): also anchored at the bottom; top edge at y=150
        // (= line's top edge since item fills line cross).
        Assert.Equal(150.0, fragments[1]!.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public void L11_hardening_wrap_reverse_align_items_flex_end_anchors_at_swapped_line_top()
    {
        // Per Phase 3 Task 15 L11 post-PR-#71 F#2 — align-items:
        // flex-end under wrap-reverse anchors items at the LINE's new
        // cross-end (= the line's PHYSICAL-TOP edge). Test with
        // UNEQUAL heights to distinguish from flex-start.
        //
        // Same fixture as above but align-items: flex-end.
        // Line physical offset = 150 (line spans y=150..200).
        // Within the line, flex-end anchors items at the line's
        // new cross-end = y=150 (PHYSICAL TOP of the line).
        // Item with height 30: top edge at y=150; bottom at y=180.
        // Item with height 50: top edge at y=150; bottom at y=200.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(2)); // wrap-reverse
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(11)); // flex-start
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(12)); // flex-end
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var heights = new[] { 30, 50 };
        var items = new Box[2];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, heights[i]);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new BoxFragment?[2];
        foreach (var f in sink.Fragments)
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (f.Box == items[i]) { fragments[i] = f; break; }
            }
        }
        // Both items anchored at the line's TOP edge (= y=150).
        Assert.Equal(150.0, fragments[0]!.Value.BlockOffset, precision: 3);
        Assert.Equal(150.0, fragments[1]!.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public void L11_hardening_wrap_reverse_align_self_override_respects_swapped_axis()
    {
        // Per Phase 3 Task 15 L11 post-PR-#71 F#2 — align-self
        // overrides align-items per item. Under wrap-reverse, the
        // override's FlexStart/FlexEnd meaning also flips with the
        // axis. Fixture: 2 items × h=30, container align-items:
        // flex-start. Item 1 sets align-self: flex-end → it should
        // anchor at the line's TOP edge (= swapped cross-end) while
        // item 0 remains at the line's BOTTOM edge (= swapped
        // cross-start). Line spans y=150..200 (under wrap-reverse +
        // align-content: flex-start; max height for 1 line of 2 items
        // is 30, so line cross-extent = 30; physical line offset =
        // 200 - 30 = 170; line spans y=170..200).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(2)); // wrap-reverse
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(11)); // flex-start
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var items = new Box[2];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 30);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            if (i == 1)
            {
                // Keyword 12 = flex-end per BuildAlignSelfTable
                // (offset by +1 from the align-items index since
                // align-self has `auto` at index 0).
                style.Set(PropertyId.AlignSelf, ComputedSlot.FromKeyword(13));
            }
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new BoxFragment?[2];
        foreach (var f in sink.Fragments)
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (f.Box == items[i]) { fragments[i] = f; break; }
            }
        }
        // Line cross-extent = 30; line physical offset = 200 - 30 =
        // 170; line spans y=170..200. Item 0 (align-items: flex-start
        // under wrap-reverse = anchored at line bottom y=200, top edge
        // = 200 - 30 = 170). Item 1 (align-self: flex-end under
        // wrap-reverse = anchored at line top y=170, top edge = 170).
        // In this fixture both happen to be at y=170 because each
        // item fills the line cross-extent. The behavior differs
        // visually only when items have different heights from the
        // line cross-extent; pin the offsets here.
        Assert.Equal(170.0, fragments[0]!.Value.BlockOffset, precision: 3);
        Assert.Equal(170.0, fragments[1]!.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public void L11_hardening_wrap_reverse_auto_height_wrapper_sums_line_cross_extents()
    {
        // Per Phase 3 Task 15 L11 post-PR-#71 F#6 — wrap-reverse +
        // height: auto pre-measure parity. The BlockLayouter pre-
        // measure must produce the SAME wrapper cross-extent for
        // wrap vs wrap-reverse (the swap is purely a placement
        // decision, not a sizing decision).
        //
        // Fixture: 4 items × 100w × 50h in a 250w wrap-reverse
        // container with auto-height. Wrap math → 2 lines × 50 each
        // → wrapper.BlockSize = 100. Sibling lands at BlockOffset
        // 100 (= wrapper bottom).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(2)); // wrap-reverse
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        // No explicit height — wrapper auto-sizes.

        var items = new Box[4];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);
        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Width, 100);
        SetLengthPx(siblingStyle, PropertyId.Height, 10);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? flexFragment = null;
        BoxFragment? siblingFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == flex) flexFragment = f;
            else if (f.Box == sibling) siblingFragment = f;
        }
        Assert.NotNull(flexFragment);
        Assert.NotNull(siblingFragment);
        // Wrapper sizes to sum of line cross-extents = 100.
        Assert.Equal(100.0, flexFragment!.Value.BlockSize, precision: 3);
        // Sibling lands at the wrapper's bottom edge.
        Assert.Equal(100.0, siblingFragment!.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public void L11_hardening_wrap_reverse_with_order_swaps_after_reorder()
    {
        // Per Phase 3 Task 15 L11 post-PR-#71 F#7 — `order` reorders
        // items BEFORE line packing (L10 sorted-sequence pre-pass);
        // wrap-reverse swaps cross-start/cross-end AFTER PackLines
        // at emission time. The two transforms are orthogonal.
        //
        // Fixture: 4 items × 100w × 50h in 250 × 200 wrap-reverse.
        // Orders: [2, 0, 0, -1]. Effective sorted order: item 3
        // (order -1), item 1 (order 0), item 2 (order 0), item 0
        // (order 2). Line packing: line 0 = item 3 + item 1 (inline
        // sum 200 ≤ 250). line 1 = item 2 + item 0.
        // align-content: flex-start under wrap-reverse:
        //   DOM line 0 at swappedCursor 0; physical = 200-0-50 = 150.
        //     (Contains item 3 + item 1.)
        //   DOM line 1 at swappedCursor 50; physical = 200-50-50 = 100.
        //     (Contains item 2 + item 0.)
        // So items 3 + 1 at BlockOffset 150; items 2 + 0 at BlockOffset 100.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(2)); // wrap-reverse
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(11)); // flex-start
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var orders = new[] { 2, 0, 0, -1 };
        var items = new Box[4];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.Order, ComputedSlot.FromInteger(orders[i]));
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new BoxFragment?[4];
        foreach (var f in sink.Fragments)
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (f.Box == items[i]) { fragments[i] = f; break; }
            }
        }
        // Effective DOM order: item 3 + item 1 on line 0 (BlockOffset
        // 150); item 2 + item 0 on line 1 (BlockOffset 100).
        Assert.Equal(100.0, fragments[0]!.Value.BlockOffset, precision: 3); // item 0 → line 1
        Assert.Equal(150.0, fragments[1]!.Value.BlockOffset, precision: 3); // item 1 → line 0
        Assert.Equal(100.0, fragments[2]!.Value.BlockOffset, precision: 3); // item 2 → line 1
        Assert.Equal(150.0, fragments[3]!.Value.BlockOffset, precision: 3); // item 3 → line 0
    }

    // ====================================================================
    //  Phase 3 Task 15 L12 — §9.7 step-4 min/max clamping iteration
    //  (4 additional cases beyond the canonical
    //  `L12_min_width_clamps_resolved_shrink_per_spec_step_4` above).
    // ====================================================================

    [Fact]
    public void L12_max_width_clamps_resolved_grow_per_spec_step_4()
    {
        // Per Phase 3 Task 15 L12 — symmetric case for max-width on grow.
        // Per CSS Flexbox L1 §9.7 step 4 the clamping iteration freezes
        // max-violators (totalViolation < 0) on the same pass it freezes
        // min-violators on the other sign.
        //
        // Fixture: 3 items × width: 100 + max-width: 150 + flex-grow: 1
        // in a 600px container. Pre-fix:
        //   sumHypothetical = 300; freeSpace = +300; each item absorbs
        //   100 → resolved 200.
        // Post-fix (§9.7 step 4):
        //   Iter 1: same distribution → 200 each. Clamp: 200 > 150
        //           → clamp DOWN to 150. Violations: -50 each;
        //           totalViolation = -150 < 0 → freeze max-violators.
        //   Iter 2: all 3 items frozen at 150. No further distribution.
        //           Convergence (totalViolation == 0).
        // Final: each item at 150; total = 450 < 600 → container has
        // 150px of unused free space. Spec-correct: items honor
        // max-width even if it means the container has leftover space.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            SetLengthPx(style, PropertyId.MaxWidth, 150);
            style.Set(PropertyId.FlexGrow, ComputedSlot.FromNumber(1.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        // Spec-correct: each item clamped DOWN to max-width 150.
        Assert.Equal(150.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(150.0, fragments[1].InlineSize, precision: 3);
        Assert.Equal(150.0, fragments[2].InlineSize, precision: 3);
    }

    [Fact]
    public void L12_mixed_min_and_max_violations_iterate_to_convergence()
    {
        // Per Phase 3 Task 15 L12 — combined violation case proves the
        // §9.7 step-4 algorithm correctly handles the totalViolation
        // sign rule: per spec, an iteration freezes EITHER min-violators
        // (when totalViolation > 0) OR max-violators (when < 0). When
        // both kinds exist, the larger-magnitude family freezes first
        // and the other family resolves in subsequent iterations.
        //
        // Fixture (row direction, 600px container, all flex-grow: 1,
        // hypothetical = width = 100 each):
        //   item 0: width: 100, min-width: 250 (= +150 min violation
        //           after the natural grow to 200)
        //   item 1: width: 100, max-width: 150 (= -50 max violation
        //           after the natural grow to 200)
        //   item 2: width: 100, no min/max (= will absorb leftover)
        //
        // Iter 1: equal distribution → 200 each.
        //   item 0: 200 < 250 → clamp UP, violation +50.
        //   item 1: 200 > 150 → clamp DOWN, violation -50.
        //   item 2: no violation.
        //   totalViolation = 0 → spec says STOP. All items frozen at
        //   clamped values: (250, 150, 200) = total 600 exactly.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 600);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var widths = new double[] { 100, 100, 100 };
        var minWidths = new double[] { 250, 0, 0 };
        var maxWidths = new double[] { 0, 150, 0 };
        var items = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, widths[i]);
            SetLengthPx(style, PropertyId.Height, 50);
            if (minWidths[i] > 0)
            {
                SetLengthPx(style, PropertyId.MinWidth, minWidths[i]);
            }
            if (maxWidths[i] > 0)
            {
                SetLengthPx(style, PropertyId.MaxWidth, maxWidths[i]);
            }
            style.Set(PropertyId.FlexGrow, ComputedSlot.FromNumber(1.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(3, fragments.Count);
        // Item 0: clamped UP by min-width to 250.
        Assert.Equal(250.0, fragments[0].InlineSize, precision: 3);
        // Item 1: clamped DOWN by max-width to 150.
        Assert.Equal(150.0, fragments[1].InlineSize, precision: 3);
        // Item 2: no clamp; absorbs the natural distribution = 200.
        Assert.Equal(200.0, fragments[2].InlineSize, precision: 3);
    }

    [Fact]
    public void L12_min_width_clamps_when_basis_below_min_during_hypothetical()
    {
        // Per Phase 3 Task 15 L12 — covers the corner case where the
        // declared flex-basis is itself BELOW min-width. Per CSS Flexbox
        // L1 §9.7 the hypothetical main-size is the flex-basis value
        // (before flexing); step-4 clamping pulls it up to min-width on
        // the first iteration.
        //
        // Fixture: 2 items × flex-basis: 50 + min-width: 200 +
        // flex-grow: 1 in a 600px container. Pre-fix:
        //   sumHypothetical = 100; freeSpace = +500; each item absorbs
        //   250 → resolved 300 (no clamping needed because grow exceeds
        //   the min). But what if freeSpace can't bring it above min?
        // This test uses a CASE where natural grow would land at 200
        // exactly = the min boundary. Setting basis: 50, min: 250,
        // container: 600 → naturally grows to 300 each (50 + 250),
        // still above min. So we strengthen by adding flex-shrink: 0
        // and reducing the container to make the test sharper:
        //   2 items × flex-basis: 50 + min-width: 250 in a 200px
        //   container. flex-grow: 0 (default basis path).
        //   sumHypothetical = 100; freeSpace = 200 - 100 = 100. No
        //   grow → resolved = basis = 50 each. Clamp: 50 < 250
        //   → clamp UP to 250. Final: (250, 250); total = 500 >
        //   container 200 → overflow by 300. Spec-correct.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Width, 200);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        var items = new Box[2];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.FlexBasis, 50);
            SetLengthPx(style, PropertyId.Height, 50);
            SetLengthPx(style, PropertyId.MinWidth, 250);
            // grow: 0 (default), shrink: 1 (default). Free space is
            // positive (no shrink kicks in) and there's no grow factor
            // so resolved stays at basis until clamping.
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(2, fragments.Count);
        // Both items clamped UP from basis 50 → min 250.
        Assert.Equal(250.0, fragments[0].InlineSize, precision: 3);
        Assert.Equal(250.0, fragments[1].InlineSize, precision: 3);
    }

    [Fact]
    public void L12_column_direction_max_height_clamps_resolved_grow()
    {
        // Per Phase 3 Task 15 L12 — axis-symmetric variant proving the
        // §9.7 step-4 clamping also fires under
        // `flex-direction: column` where the main axis is the block
        // axis and the relevant constraint properties are min-HEIGHT
        // / max-HEIGHT (NOT min-width / max-width).
        //
        // Uses the GROW + MAX-HEIGHT direction because under column
        // direction the BlockLayouter pre-grows the container's
        // block extent to fit items' declared heights (= the
        // column-direction auto-height path also applies to declared
        // heights when items overflow), so naturally-arising shrink
        // scenarios under column direction don't fire. Grow-clamp
        // remains testable because we can declare a container TALLER
        // than the item sum, letting flex-grow distribute the surplus
        // until max-height kicks in.
        //
        // Fixture: 2 items × height: 100 + max-height: 200 +
        // flex-grow: 1 in a 600px block-axis container; column
        // direction.
        //   sumHypothetical = 200; freeSpace = +400; sumFlexGrow = 2;
        //   each item grows by (1/2)*400 = 200 → resolved 300.
        //   Clamp: 300 > 200 → clamp DOWN to 200. totalViolation =
        //   -200 < 0 → freeze max-violators.
        //   Iter 2: all frozen. Final each at 200, total = 400 <
        //   container 600 → 200px unused free space on the block axis.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        // column direction (KeywordIdColumn = 2 per the flex-direction
        // properties.json mapping). Same as the L8 column-direction
        // tests above.
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2));
        SetLengthPx(flex.Style, PropertyId.Width, 200);
        SetLengthPx(flex.Style, PropertyId.Height, 600);

        var items = new Box[2];
        for (var i = 0; i < items.Length; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 100);
            SetLengthPx(style, PropertyId.MaxHeight, 200);
            style.Set(PropertyId.FlexGrow, ComputedSlot.FromNumber(1.0));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var fragments = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { fragments.Add(f); break; }
            }
        }
        Assert.Equal(2, fragments.Count);
        // Each item clamped DOWN to max-height 200.
        Assert.Equal(200.0, fragments[0].BlockSize, precision: 3);
        Assert.Equal(200.0, fragments[1].BlockSize, precision: 3);
    }

    // ====================================================================
    //  Phase 3 Task 16 cycle 1 (Hello World) — multi-page flex split
    //  via FlexContinuation. The layouter accepts a non-null
    //  FlexContinuation + emits a PageComplete result when lines
    //  don't fit, carrying a continuation for the next page. Gated
    //  behind `allowPagination: true` on ConfigureEmission so L1-L17
    //  fixtures (atomic, may overflow) stay byte-identical.
    // ====================================================================

    [Fact]
    public void Task16_pagination_off_by_default_preserves_atomic_behavior()
    {
        // Sanity baseline — without `allowPagination: true`,
        // ConfigureEmission preserves the L1-L17 contract:
        // overflow is allowed, no split, single AllDone result.
        // 3 items × height 100 in a wrap container of width 250
        // (so 100+100 fits per row → 2 lines of 2 items, last
        // line has 1) + container height 30 (= insufficient for
        // 2 lines). With pagination off → all lines emit, container
        // overflows on the block axis.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1)); // wrap
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 30);

        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            var item = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(item);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // All 4 items emitted on a single page → 4 fragments.
        var itemFragments = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.BlockContainer) itemFragments++;
        }
        Assert.Equal(4, itemFragments);
    }

    [Fact]
    public void Task16_with_pagination_splits_when_lines_dont_fit_in_container()
    {
        // Cycle 1: directly construct a FlexLayouter (bypassing
        // BlockLayouter dispatch which doesn't wire pagination yet)
        // + verify the resume contract. Pre-Task-16 the constructor
        // rejected any non-null continuation; post-Task-16 it accepts
        // a FlexContinuation + skips lines [0, LineIndex).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1));
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        // 4 items of 100×50, 2 per line, 2 lines total (LineCrossSize = 50).
        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            var item = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(item);
        }

        // Construct the layouter directly; pretend the wrapper
        // provides only 50 of block space (= room for 1 line, NOT 2).
        using var layouter = new NetPdf.Layout.Layouters.FlexLayouter(
            rootBox: flex, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 250,
            contentBlockSize: 50,
            allowPagination: true);

        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Per Task 16 cycle 1 — split happens at line 1 (= second
        // line). Result is PageComplete with FlexContinuation{LineIndex=1}.
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.NotNull(result.Continuation);
        var flexCont = Assert.IsType<FlexContinuation>(result.Continuation);
        Assert.Equal(1, flexCont.LineIndex);

        // First page emitted 2 items (the first line).
        var pageOneItems = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.BlockContainer) pageOneItems++;
        }
        Assert.Equal(2, pageOneItems);
    }

    // ====================================================================
    //  Flex-column pagination (non-block-pagination arc) — a
    //  `flex-direction: column` + nowrap container splits at ITEM
    //  boundaries along the MAIN = block axis. The page CUT-OFF is the
    //  pageBlockBudget (kept distinct from the natural column main-size so
    //  flex resolution doesn't SHRINK items to fit instead of paginating).
    // ====================================================================

    private static Box BuildColumnFlexWithItems(int count, double itemHeightPx)
    {
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        for (var i = 0; i < count; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, itemHeightPx);
            var item = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(item);
        }
        return flex;
    }

    [Fact]
    public void Column_flex_splits_at_item_boundary_against_page_budget()
    {
        // 4 items × 50 (block-axis) ⇒ natural column main-size 200. A page
        // budget of 100 fits exactly 2 items (bottoms 50, 100); the 3rd
        // (bottom 150 > 100) defers. The split is between items 1 and 2.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();
        var flex = BuildColumnFlexWithItems(4, 50);

        using var layouter = new NetPdf.Layout.Layouters.FlexLayouter(
            rootBox: flex, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: 100, contentBlockSize: 200, // natural main-size
            allowPagination: true, pageBlockBudget: 100);   // page room for 2 items

        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 1000);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var cont = Assert.IsType<FlexContinuation>(result.Continuation);
        Assert.Equal(2, cont.ItemIndex);

        var items = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
            if (f.Box.Kind == BoxKind.BlockContainer) items.Add(f);
        Assert.Equal(2, items.Count);                  // 2 items on page 1
        Assert.Equal(0, items[0].BlockOffset, 3);       // first item at content origin
        Assert.Equal(50, items[1].BlockOffset, 3);      // second stacked below
        Assert.Equal(50, items[0].BlockSize, 3);        // NOT shrunk to fit the budget
    }

    [Fact]
    public void Column_flex_resume_emits_remaining_items_reanchored_to_page_top()
    {
        // Resume at item 2 (the cut from the test above). The remaining items
        // 2 + 3 re-anchor to the page's content-block-start (offsets 0, 50),
        // NOT their natural offsets (100, 150). Everything fits ⇒ AllDone.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();
        var flex = BuildColumnFlexWithItems(4, 50);

        using var layouter = new NetPdf.Layout.Layouters.FlexLayouter(
            rootBox: flex, sink: sink,
            incomingContinuation: new FlexContinuation(LineIndex: 0, ItemIndex: 2),
            diagnostics: null, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: 100, contentBlockSize: 200,
            allowPagination: true, pageBlockBudget: 100);

        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 1000);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        var items = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
            if (f.Box.Kind == BoxKind.BlockContainer) items.Add(f);
        Assert.Equal(2, items.Count);              // only items 2 + 3
        Assert.Equal(0, items[0].BlockOffset, 3);   // item 2 re-anchored to page top
        Assert.Equal(50, items[1].BlockOffset, 3);  // item 3 below it
    }

    [Fact]
    public void Column_flex_first_item_taller_than_page_force_overflows_forward()
    {
        // The first item on a page always commits even if it alone exceeds the
        // budget (CSS Fragmentation L3 §4.4 progress rule) — otherwise the
        // continuation would loop forever. A 200-tall first item + a 50-tall
        // second, budget 100: item 0 force-overflows + commits, item 1 defers.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        foreach (var h in new[] { 200.0, 50.0 })
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, h);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }

        using var layouter = new NetPdf.Layout.Layouters.FlexLayouter(
            rootBox: flex, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: 100, contentBlockSize: 250,
            allowPagination: true, pageBlockBudget: 100);

        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 1000);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.Equal(1, Assert.IsType<FlexContinuation>(result.Continuation).ItemIndex);
        var items = 0;
        foreach (var f in sink.Fragments)
            if (f.Box.Kind == BoxKind.BlockContainer) items++;
        Assert.Equal(1, items);   // the oversized first item committed alone
    }

    [Fact]
    public void Column_flex_as_root_direct_child_paginates_without_shrinking_items()
    {
        // PR-#180 review P1 — the OUTER BlockLayouter flex dispatch (root's
        // DIRECT child is flex, e.g. body{display:flex;flex-direction:column})
        // must use the natural-size / page-budget dual-input like the recursive
        // path. Without it, the outer clamp sets contentBlockSize = the page
        // budget; FlexLayouter resolves flex-shrink against that, SHRINKS items
        // to fit, and returns AllDone (no split). With the dual-input, items
        // keep their natural size + the container splits via FlexContinuation.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();
        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(BuildColumnFlexWithItems(4, 200)); // 4×200 ⇒ natural 800

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        // Page budget 500 ⇒ room for 2 items (bottoms 200, 400); item 3
        // (bottom 600) defers. Default flex-shrink:1 would shrink each item to
        // ~125 against a 500 main-size — the regression this test guards.
        var ctx = new FragmentainerContext(contentInlineSize: 300, blockSize: 500);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var blockCont = Assert.IsType<BlockContinuation>(result.Continuation);
        var flexCont = Assert.IsType<FlexContinuation>(blockCont.LayouterState);
        Assert.Equal(2, flexCont.ItemIndex);

        var items = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
            if (f.Box.Kind == BoxKind.BlockContainer) items.Add(f);
        Assert.Equal(2, items.Count);
        Assert.All(items, f => Assert.Equal(200, f.BlockSize, 3)); // NOT shrunk to ~125
    }

    [Fact]
    public void Column_flex_split_wrapper_resizes_to_emitted_item_extent()
    {
        // PR-#180 review P2 — a paginating column flex wrapper paints the ACTUAL
        // emitted item extent, not the clamped page budget. 4×200 (natural 800),
        // page budget 500 ⇒ 2 items (400) commit on page 1. The wrapper
        // (FlexContainer fragment) must be 400, not the clamped 500 — else 100px
        // of blank trailing space below the last item.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();
        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(BuildColumnFlexWithItems(4, 200));

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 300, blockSize: 500);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        BoxFragment? wrapper = null;
        foreach (var f in sink.Fragments)
            if (f.Box.Kind == BoxKind.FlexContainer) wrapper = f;
        Assert.NotNull(wrapper);
        Assert.Equal(400, wrapper!.Value.BlockSize, 3);   // emitted extent, NOT the 500 budget
    }

    [Fact]
    public void Column_flex_final_resume_page_resizes_wrapper_and_places_sibling_tightly()
    {
        // PR-#180 review P2 — on a final resume page where the remaining items
        // occupy LESS than the page budget, the wrapper resizes to the emitted
        // extent (AllDone carries no continuation to read it from — the fix uses
        // FlexLayouter.LastEmittedBlockExtent) and the following sibling sits
        // directly below the wrapper, not pushed down to the budget.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();
        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(BuildColumnFlexWithItems(4, 200));        // child 0: the flex
        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 77);          // distinctive height
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement())); // child 1

        // Resume at item 2 (items 0,1 committed on a prior page). 2 items (400)
        // remain + fit the 500 budget ⇒ AllDone on this page; the sibling follows.
        var incoming = new BlockContinuation(
            ResumeAtChild: 0, ConsumedBlockSize: 0,
            LayouterState: new FlexContinuation(LineIndex: 0, ItemIndex: 2));
        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: incoming,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 300, blockSize: 500);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        BoxFragment? wrapper = null, sibling = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.FlexContainer) wrapper = f;
            if (f.Box.Kind == BoxKind.BlockContainer && f.BlockSize is > 76 and < 78) sibling = f;
        }
        Assert.NotNull(wrapper);
        Assert.NotNull(sibling);
        Assert.Equal(400, wrapper!.Value.BlockSize, 3);     // 2 remaining items, no blank space
        Assert.Equal(400, sibling!.Value.BlockOffset, 3);    // sibling tight under the wrapper, not at 500
    }

    [Fact]
    public void Column_flex_pagination_splits_by_sorted_order_not_dom_index()
    {
        // PR-#180 review (optional) — pin FlexContinuation.ItemIndex as a SORTED
        // position (CSS `order`), NOT a DOM index. 3 column items 200px each with
        // order reversing DOM {2,0,-1} ⇒ sorted item2, item1, item0. A 200px page
        // fits one item, so page 1 must emit the SORTED-FIRST item (DOM #2, order
        // -1) + defer at sorted position 1.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();
        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        var orders = new[] { 2, 0, -1 };
        var items = new Box[3];
        for (var i = 0; i < 3; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 200);
            style.Set(PropertyId.Order, ComputedSlot.FromInteger(orders[i]));
            items[i] = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 300, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var flexCont = Assert.IsType<FlexContinuation>(
            Assert.IsType<BlockContinuation>(result.Continuation).LayouterState);
        Assert.Equal(1, flexCont.ItemIndex);   // sorted position, not DOM index

        BoxFragment? emitted = null;
        var count = 0;
        foreach (var f in sink.Fragments)
            if (f.Box.Kind == BoxKind.BlockContainer) { emitted = f; count++; }
        Assert.Equal(1, count);                 // exactly one item on page 1
        Assert.True(emitted!.Value.Box == items[2],
            "page 1 must emit the SORTED-first item (DOM #2, order -1), not the DOM-first item");
    }

    [Fact]
    public void Column_flex_without_pagination_emits_all_items_atomically()
    {
        // Baseline: allowPagination false (the default) keeps the atomic
        // contract — every item emits on one page even when they overflow.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();
        var flex = BuildColumnFlexWithItems(4, 50);

        using var layouter = new NetPdf.Layout.Layouters.FlexLayouter(
            rootBox: flex, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: 100, contentBlockSize: 200,
            allowPagination: false);

        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 1000);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        var items = 0;
        foreach (var f in sink.Fragments)
            if (f.Box.Kind == BoxKind.BlockContainer) items++;
        Assert.Equal(4, items);   // all items on one page (atomic)
    }

    [Fact]
    public void Task16_cycle4e_PageComplete_reports_emitted_block_extent()
    {
        // Per Phase 3 Task 16 cycle 4e (P2 #5 from PR-#79) —
        // FlexLayouter populates FlexContinuation.EmittedBlockExtent
        // with the TRUE occupied cross-axis extent (= the
        // content-cross-box 0-based bottom of the deepest emitted
        // line, INCLUDING align-content's lineStartOffset +
        // lineBetweenSpacing). For the default align-content:
        // stretch / flex-start case with no extra gap, the value
        // equals sum(LineCrossSize) — see the alignment-variant
        // tests below for the cases where these differ.
        //
        // BlockLayouter does NOT yet consume the field; cycle 4f
        // will use it for wrapper resize + ConsumedBlockSize
        // precision per the z-order constraint documented on
        // FlexContinuation.
        //
        // Fixture: 4 items of 100×50 in a 250-wide / 50-block
        // container (= room for 1 line of 50 tall). 2 items per
        // line (100+100 = 200 fits in 250), 2 lines total. The
        // first line (= 2 items at 50 tall each → LineCrossSize=50)
        // emits on page 1; the 2nd line defers via
        // FlexContinuation{LineIndex=1}. The emitted block extent =
        // sum of emitted line cross-sizes = 50.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.FlexWrap, 0);  // wrap
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1));
        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            var item = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(item);
        }

        using var layouter = new NetPdf.Layout.Layouters.FlexLayouter(
            rootBox: flex, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 250,
            contentBlockSize: 50,
            allowPagination: true);

        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var flexCont = Assert.IsType<FlexContinuation>(result.Continuation);
        Assert.Equal(1, flexCont.LineIndex);

        // The cycle-4e contract: EmittedBlockExtent = 50 (= LineCrossSize
        // of the single emitted line). Pre-cycle-4e the field
        // didn't exist + the BlockLayouter caller used the clamped
        // budget instead — over-counting when emitted content was
        // smaller than the budget.
        Assert.Equal(50.0, flexCont.EmittedBlockExtent, precision: 3);
    }

    [Fact]
    public void Task16_cycle4e_PageComplete_reports_emitted_extent_for_two_lines()
    {
        // Variant of the above: budget that fits 2 lines of 30 tall
        // (= 60 of budget) but 3 lines exist. EmittedBlockExtent
        // should be 60 (= 30 + 30).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1));
        for (var i = 0; i < 3; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 250);  // each item = full line
            SetLengthPx(style, PropertyId.Height, 30);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            var item = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(item);
        }

        using var layouter = new NetPdf.Layout.Layouters.FlexLayouter(
            rootBox: flex, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 250,
            contentBlockSize: 70,  // fits 2 lines (60); line 3 would exceed
            allowPagination: true);

        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var flexCont = Assert.IsType<FlexContinuation>(result.Continuation);
        Assert.Equal(2, flexCont.LineIndex);
        Assert.Equal(60.0, flexCont.EmittedBlockExtent, precision: 3);
    }

    [Fact]
    public void Task16_cycle4e_emitted_extent_includes_align_content_space_between_gap()
    {
        // Per Phase 3 Task 16 cycle 4e post-PR-#86 review P1 #1 +
        // P2 #3 — align-content: space-between distributes the
        // free-cross-space as gaps BETWEEN lines. The TRUE occupied
        // extent INCLUDES those gaps; a naive sum(LineCrossSize)
        // would under-count, causing cycle 4f's wrapper resize to
        // clip children.
        //
        // Fixture: 3 lines of 20 in a 100-block container. Page
        // budget = 100, fits all 3 lines (60 of content + 40 of
        // gap). But to exercise the paginated path we fit only 2
        // lines per page → budget = 60 (= 20 + 20 + 20 fits with
        // no remaining). Use 4 items at 30 wide / 20 tall in a
        // 30-wide container so each item wraps to its own line:
        // 4 items → 4 lines × 20 = 80. Budget = 60 (room for 3
        // lines of content, no gap). Page 1 emits 3 lines; line 4
        // defers. With align-content: space-between on 3 lines in
        // budget 60: free = 0, gap = 0 → EmittedBlockExtent = 60.
        //
        // To get a non-trivial gap, use lines that don't fill the
        // budget: 2 items, each on own line, 30×20 in a 30-wide
        // / 100-block container with align-content: space-between.
        // Items: 2 → 2 lines × 20 = 40. Free = 100 - 40 = 60.
        // Gap = 60/(2-1) = 60. Lines at swappedAxisCursor 0 + 80.
        // Bottoms at 20 + 100. maxBottom = 100. But this is
        // single-page (no continuation).
        //
        // For the paginated case: 4 items × 20 each, line packing
        // gives 4 lines. Budget = 70 → 3 lines fit (cumulative
        // 60 < 70 < 80). Line 4 defers. align-content:
        // space-between on 3 emitted lines in 70 budget: free = 10,
        // gap = 5. Lines at cursors 0, 25, 50. Bottoms: 20, 45,
        // 70. maxBottom = 70. Naive sum = 60. CORRECT value = 70.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1));
        // Per L7's ReadAlignContent keyword mapping
        // (ComputedStyleLayoutExtensions.cs line ~795):
        // space-between = Keyword 1.
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(1));
        // Explicit container height = 70 so ResolveContainerCrossSize
        // returns 70 (not sum-of-line-extents); freeCrossSpace = 10
        // after 3 lines × 20 fit → align-content distribution
        // applies with non-zero start/gap offsets.
        SetLengthPx(flex.Style, PropertyId.Height, 70);
        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 250);  // = wider than container → each wraps alone
            SetLengthPx(style, PropertyId.Height, 20);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            var item = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(item);
        }

        using var layouter = new NetPdf.Layout.Layouters.FlexLayouter(
            rootBox: flex, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 250,
            contentBlockSize: 70,  // fits 3 lines × 20 = 60; gap of 10 available
            allowPagination: true);

        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var flexCont = Assert.IsType<FlexContinuation>(result.Continuation);
        Assert.Equal(3, flexCont.LineIndex);

        // align-content: space-between with 3 emitted lines in 70
        // budget: free = 10, gap = 10/(3-1) = 5. Lines at cursors
        // 0, 25, 50. Bottoms: 20, 45, 70. maxBottom = 70.
        // Naive sum(LineCrossSize) = 60. Cycle-4e hardening
        // pins the correct value (70).
        Assert.Equal(70.0, flexCont.EmittedBlockExtent, precision: 3);
    }

    [Fact]
    public void Task16_cycle4e_emitted_extent_includes_align_content_center_start_offset()
    {
        // Per Phase 3 Task 16 cycle 4e post-PR-#86 review P1 #1 —
        // align-content: center adds a lineStartOffset = free/2
        // BEFORE the first emitted line. The TRUE occupied extent
        // INCLUDES that prefix space (= the wrapper resize target
        // must contain the empty top-space too); a naive
        // sum(LineCrossSize) under-counts.
        //
        // Fixture: 4 items × 250×20 in a 250×70 container. 3 lines
        // fit on this page. align-content: center → free = 10,
        // start = 5, gap = 0. Lines at cursors 5, 25, 45. Bottoms:
        // 25, 45, 65. maxBottom = 65. Naive sum = 60.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1));
        // L7 KeywordResolver (ComputedStyleLayoutExtensions.cs ~795):
        // center = Keyword 8.
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(8));
        SetLengthPx(flex.Style, PropertyId.Height, 70);
        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 250);
            SetLengthPx(style, PropertyId.Height, 20);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            var item = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(item);
        }

        using var layouter = new NetPdf.Layout.Layouters.FlexLayouter(
            rootBox: flex, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 250,
            contentBlockSize: 70,
            allowPagination: true);

        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var flexCont = Assert.IsType<FlexContinuation>(result.Continuation);
        // center: start = 5, lines at 5/25/45, bottoms at
        // 25/45/65 → maxBottom = 65.
        Assert.Equal(65.0, flexCont.EmittedBlockExtent, precision: 3);
    }

    [Fact]
    public void Task16_cycle4e_emitted_extent_for_flex_end_positions_at_bottom()
    {
        // Per Phase 3 Task 16 cycle 4e post-PR-#86 review P1 #1 —
        // align-content: flex-end pushes all lines to the
        // cross-end. lineStartOffset = free (= entire free space
        // before first line); gap = 0. The deepest line's bottom =
        // budget = the WHOLE clamped wrapper size.
        //
        // Fixture: 4 items × 250×20 in 250×70 budget; 3 lines fit.
        // free = 10, start = 10, gap = 0. Lines at 10, 30, 50.
        // Bottoms: 30, 50, 70. maxBottom = 70.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1));
        // L7 KeywordResolver (ComputedStyleLayoutExtensions.cs ~795):
        // flex-end = Keyword 12.
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(12));
        SetLengthPx(flex.Style, PropertyId.Height, 70);
        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 250);
            SetLengthPx(style, PropertyId.Height, 20);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            var item = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(item);
        }

        using var layouter = new NetPdf.Layout.Layouters.FlexLayouter(
            rootBox: flex, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 250,
            contentBlockSize: 70,
            allowPagination: true);

        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var flexCont = Assert.IsType<FlexContinuation>(result.Continuation);
        // flex-end: start = 10, lines at 10/30/50, bottoms at
        // 30/50/70 → maxBottom = 70 (= the whole budget).
        Assert.Equal(70.0, flexCont.EmittedBlockExtent, precision: 3);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.1)]
    [InlineData(-1.0)]
    [InlineData(-100.0)]
    public void Task16_cycle4e_FlexContinuation_rejects_invalid_EmittedBlockExtent(double bad)
    {
        // Per Phase 3 Task 16 cycle 4e post-PR-#86 review P2 #2 —
        // defensive validation. NaN / ±Infinity / negative
        // EmittedBlockExtent would corrupt cycle 4f's
        // wrapper-resize + ConsumedBlockSize accounting. The
        // FlexContinuation constructor surfaces the contract
        // violation immediately.
        var ex = Assert.Throws<System.ArgumentOutOfRangeException>(
            () => new FlexContinuation(
                LineIndex: 1, BaselineState: null, EmittedBlockExtent: bad));
        Assert.Contains("EmittedBlockExtent", ex.Message);
    }

    [Fact]
    public void Task16_cycle4e_FlexContinuation_accepts_zero_EmittedBlockExtent()
    {
        // Zero is valid (= the default when the field isn't
        // populated — e.g., the no-continuation AllDone path
        // doesn't emit a FlexContinuation at all; the cycle-1
        // resume contract uses 0 for callers that don't know
        // the value yet).
        var c = new FlexContinuation(
            LineIndex: 0, BaselineState: null, EmittedBlockExtent: 0.0);
        Assert.Equal(0.0, c.EmittedBlockExtent, precision: 3);
    }

    [Fact]
    public void Task16_cycle4e_FlexContinuation_accepts_positive_EmittedBlockExtent()
    {
        // Sanity: positive finite value passes the guard.
        var c = new FlexContinuation(
            LineIndex: 2, BaselineState: null, EmittedBlockExtent: 100.5);
        Assert.Equal(100.5, c.EmittedBlockExtent, precision: 3);
    }

    [Fact]
    public void Task16_resume_with_continuation_skips_emitted_lines()
    {
        // Continuation of the test above: re-run with a non-null
        // FlexContinuation{LineIndex=1} to verify the layouter skips
        // line 0 + emits only line 1.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1));
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            var item = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            flex.AppendChild(item);
        }

        // Resume from line 1 (= the second line on a fresh page).
        var continuation = new FlexContinuation(LineIndex: 1);
        using var layouter = new NetPdf.Layout.Layouters.FlexLayouter(
            rootBox: flex, sink: sink, incomingContinuation: continuation,
            diagnostics: null, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 250,
            contentBlockSize: 200,
            allowPagination: true);

        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Per Task 16 cycle 1 — all remaining lines (= just line 1)
        // fit on this page → AllDone.
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Null(result.Continuation);

        // Second page emitted only the 2 items from line 1 (= items
        // 2 + 3 in DOM order).
        var itemFragments = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.BlockContainer) itemFragments++;
        }
        Assert.Equal(2, itemFragments);
    }

    [Fact]
    public void Task16_constructor_rejects_non_flex_continuation()
    {
        // Sanity: a misrouted MulticolContinuation surfaces loudly.
        var flex = BuildFlexContainer();
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var badContinuation = new MulticolContinuation(NextChildIndex: 0);

        Assert.Throws<ArgumentException>(() =>
            new NetPdf.Layout.Layouters.FlexLayouter(
                rootBox: flex, sink: sink,
                incomingContinuation: badContinuation,
                diagnostics: null, shaperResolver: shaper));
    }

    [Fact]
    public void Task16_too_tall_first_line_still_emits_per_fragmentation_progress_rule()
    {
        // Per Phase 3 Task 16 cycle 1 + post-PR-#78 review P2 — CSS
        // Fragmentation L3 §4.4 requires AT LEAST ONE line to commit
        // per page even if it's taller than the available block
        // extent (= "unforced break must not stop progress"). Verifies
        // the `isFirstOnPage` guard in the fragment-range computation.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1));
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 100);

        // One item 250×100 (1 line of cross size 100). Configure
        // contentBlockSize: 50 (= less than the line's cross size).
        // Despite the line being too tall, it MUST emit + the result
        // is AllDone (nothing left to defer).
        var style = MakeStyle();
        SetLengthPx(style, PropertyId.Width, 250);
        SetLengthPx(style, PropertyId.Height, 100);
        style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
        flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));

        using var layouter = new NetPdf.Layout.Layouters.FlexLayouter(
            rootBox: flex, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 250,
            contentBlockSize: 50,
            allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // One line emitted (1 item) + AllDone (no more lines).
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Null(result.Continuation);
        var emittedItems = sink.Fragments.Count(f => f.Box.Kind == BoxKind.BlockContainer);
        Assert.Equal(1, emittedItems);
    }

    [Fact]
    public void Task16_exact_fit_returns_all_done()
    {
        // Per post-PR-#78 review P2 — 2 lines exactly filling
        // _contentBlockSize → all 2 lines fit on this page → AllDone.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1));
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }

        // 2 lines × 50 cross each = 100 total. contentBlockSize = 100
        // → exact fit, both lines emit.
        using var layouter = new NetPdf.Layout.Layouters.FlexLayouter(
            rootBox: flex, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 250,
            contentBlockSize: 100,
            allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Null(result.Continuation);
        Assert.Equal(4, sink.Fragments.Count(f => f.Box.Kind == BoxKind.BlockContainer));
    }

    [Fact]
    public void Task16_multiple_lines_split_at_correct_index()
    {
        // Per post-PR-#78 review P2 — 3 lines × LineCrossSize 50, in
        // a 75-block-configured layout: line 0 fits (cursor 0 → 50),
        // line 1 overflows (cursor 50 + 50 > 75 → split). Continuation
        // LineIndex = 1.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1));
        SetLengthPx(flex.Style, PropertyId.Width, 150);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        for (var i = 0; i < 6; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }
        // 6 items × 100 width in a 150 container → 1 item per line
        // (= 6 lines of cross-size 50).

        using var layouter = new NetPdf.Layout.Layouters.FlexLayouter(
            rootBox: flex, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 150,
            contentBlockSize: 75,
            allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 150, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var flexCont = Assert.IsType<FlexContinuation>(result.Continuation);
        // Line 0 emits (cursor 0 → 50, fits). Line 1 would put
        // cursor at 100 > 75 → split. Continuation = line 1.
        Assert.Equal(1, flexCont.LineIndex);
    }

    [Fact]
    public void Task16_constructor_rejects_negative_line_index()
    {
        // Per post-PR-#78 review P1 #3 — invalid LineIndex < 0 surfaces
        // as ArgumentOutOfRangeException; pre-fix it silently emitted
        // nonsensical behavior.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1));
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var style = MakeStyle();
        SetLengthPx(style, PropertyId.Width, 100);
        SetLengthPx(style, PropertyId.Height, 50);
        flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));

        using var layouter = new NetPdf.Layout.Layouters.FlexLayouter(
            rootBox: flex, sink: sink,
            incomingContinuation: new FlexContinuation(LineIndex: -1),
            diagnostics: null, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 250,
            contentBlockSize: 200,
            allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        ArgumentOutOfRangeException? thrown = null;
        try
        {
            layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
                LayoutAttemptStrategy.LastResort);
        }
        catch (ArgumentOutOfRangeException ex) { thrown = ex; }
        Assert.NotNull(thrown);
    }

    [Fact]
    public void Task16_constructor_rejects_out_of_range_line_index()
    {
        // Per post-PR-#78 review P1 #3 — LineIndex > lines.Count is
        // a corrupted continuation; surface loudly.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1));
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var style = MakeStyle();
        SetLengthPx(style, PropertyId.Width, 100);
        SetLengthPx(style, PropertyId.Height, 50);
        flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        // 1 line packed → lines.Count == 1. LineIndex = 99 is invalid.

        using var layouter = new NetPdf.Layout.Layouters.FlexLayouter(
            rootBox: flex, sink: sink,
            incomingContinuation: new FlexContinuation(LineIndex: 99),
            diagnostics: null, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 250,
            contentBlockSize: 200,
            allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        ArgumentOutOfRangeException? thrown = null;
        try
        {
            layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
                LayoutAttemptStrategy.LastResort);
        }
        catch (ArgumentOutOfRangeException ex) { thrown = ex; }
        Assert.NotNull(thrown);
    }

    [Fact]
    public void Task16_line_index_equal_to_line_count_resumes_as_no_op_all_done()
    {
        // Per post-PR-#78 review P1 #3 — the boundary LineIndex ==
        // lines.Count is INTENTIONALLY allowed: it represents "every
        // line was emitted on a prior page; nothing left to do".
        // Resumes as a no-op AllDone.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1));
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var style = MakeStyle();
        SetLengthPx(style, PropertyId.Width, 100);
        SetLengthPx(style, PropertyId.Height, 50);
        flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        // 1 line packed → lines.Count == 1. LineIndex = 1 == lines.Count.

        using var layouter = new NetPdf.Layout.Layouters.FlexLayouter(
            rootBox: flex, sink: sink,
            incomingContinuation: new FlexContinuation(LineIndex: 1),
            diagnostics: null, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 250,
            contentBlockSize: 200,
            allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Null(result.Continuation);
        // No items emitted (= the prior page emitted them).
        Assert.Equal(0, sink.Fragments.Count(f => f.Box.Kind == BoxKind.BlockContainer));
    }

    [Fact]
    public void Task16_wrap_reverse_pagination_disabled_for_cycle_1()
    {
        // Per post-PR-#78 review P1 #2 — wrap-reverse uses
        // CrossAxisFlow's swap formula against unfragmented
        // containerCrossSize. On a resumed page, this would place
        // emitted lines at the bottom of the ORIGINAL container, not
        // the current fragment. Cycle 1 explicitly DOES NOT paginate
        // wrap-reverse; sub-cycle 2 will recompute the swap against
        // each fragment's per-page cross extent.
        //
        // Verification: a wrap-reverse container with content that
        // would otherwise paginate emits ALL lines atomically (= the
        // L1-L11 wrap-reverse contract preserved).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(2)); // wrap-reverse
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }

        using var layouter = new NetPdf.Layout.Layouters.FlexLayouter(
            rootBox: flex, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 250,
            // Try to force pagination by configuring a tiny block:
            contentBlockSize: 50,
            allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // wrap-reverse pagination is gated off → all lines emit
        // atomically + AllDone.
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Null(result.Continuation);
        Assert.Equal(4, sink.Fragments.Count(f => f.Box.Kind == BoxKind.BlockContainer));
    }

    [Fact]
    public void Task16_align_content_center_does_not_skew_pagination_fit()
    {
        // Per post-PR-#78 review P1 #1 — align-content: center adds
        // a startOffset (= freeCrossSpace / 2) and shifts every line
        // by that amount. Pre-fix the cursor included that offset
        // when checking line-fit, which could incorrectly split
        // earlier than warranted. Post-fix the fragment range is
        // computed via a RAW cursor (sum of line.LineCrossSize only)
        // BEFORE align-content runs.
        //
        // Fixture: 2 lines × 50 cross-size = 100 total content.
        // contentBlockSize = 100 (exact fit). align-content: center
        // adds (containerCrossSize - 100) / 2 offset. If the pre-fix
        // logic counted this offset against the budget, line 1 would
        // not fit → split. Post-fix: 2 lines exactly fit → AllDone.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(1));
        // align-content: center per the CSS Box Alignment keyword
        // table = KeywordId 5 for the AlignContent property.
        flex.Style.Set(PropertyId.AlignContent, ComputedSlot.FromKeyword(5));
        SetLengthPx(flex.Style, PropertyId.Width, 250);
        SetLengthPx(flex.Style, PropertyId.Height, 200); // 100 of free cross space

        for (var i = 0; i < 4; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 100);
            SetLengthPx(style, PropertyId.Height, 50);
            style.Set(PropertyId.FlexShrink, ComputedSlot.FromNumber(0.0));
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        }

        using var layouter = new NetPdf.Layout.Layouters.FlexLayouter(
            rootBox: flex, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 250,
            contentBlockSize: 100, // exact line-sum fit
            allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 250, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Both lines fit per raw cross size; pagination should NOT
        // count align-content offsets against the budget.
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Null(result.Continuation);
        Assert.Equal(4, sink.Fragments.Count(f => f.Box.Kind == BoxKind.BlockContainer));
    }

    [Fact]
    public void Task16_block_layouter_rejects_misrouted_flex_continuation()
    {
        // Per PR-#79 P2 #6 closure — `BlockLayouter.AttemptLayout`
        // entry guard validates that a `BlockContinuation` carrying
        // a `FlexContinuation` in `LayouterState` targets a
        // `FlexContainer` / `InlineFlexContainer` / block-flow
        // container that could contain one. Misrouted (= pointing at
        // a leaf non-flex non-block-flow kind) surfaces loudly with
        // `InvalidOperationException` rather than silently ignoring
        // the resume state.
        //
        // Fixture: a root containing a `TableCell` child + a
        // BlockContinuation pointing at the TableCell with a
        // FlexContinuation in LayouterState (= the misrouted case).
        var root = Box.CreateRoot(MakeStyle());
        var tableCellStyle = MakeStyle();
        var tableCell = Box.ForElement(BoxKind.TableCell, tableCellStyle, MakeElement());
        root.AppendChild(tableCell);

        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();
        var misrouted = new BlockContinuation(
            ResumeAtChild: 0,
            ConsumedBlockSize: 0.0,
            LayouterState: new FlexContinuation(LineIndex: 0));

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: misrouted,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        InvalidOperationException? thrown = null;
        try
        {
            layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
                LayoutAttemptStrategy.LastResort);
        }
        catch (InvalidOperationException ex) { thrown = ex; }
        Assert.NotNull(thrown);
        Assert.Contains("FlexContinuation", thrown!.Message);
    }

    // ====================================================================
    //  Non-block-pagination arc — flex item CONTENT layout
    // ====================================================================

    [Fact]
    public void Flex_item_inner_block_content_is_laid_out_and_translated()
    {
        // Row flex, 2 items (width 100, height 80) each containing a nested
        // block child (width 40, height 30). FlexLayouter now lays out each
        // item's inner content via a nested BlockLayouter + flushes it at the
        // item's content-box origin, so the child fragments appear translated
        // to within their item (no border / padding ⇒ child origin = item
        // origin). Before this feature, flex item content didn't render at all.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        SetLengthPx(flex.Style, PropertyId.Height, 200);

        var items = new Box[2];
        var children = new Box[2];
        for (var i = 0; i < items.Length; i++)
        {
            var itemStyle = MakeStyle();
            SetLengthPx(itemStyle, PropertyId.Width, 100);
            SetLengthPx(itemStyle, PropertyId.Height, 80);
            items[i] = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());

            var childStyle = MakeStyle();
            SetLengthPx(childStyle, PropertyId.Width, 40);
            SetLengthPx(childStyle, PropertyId.Height, 30);
            children[i] = Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement());
            items[i].AppendChild(children[i]);

            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // Each item's child must be emitted at the item's origin (item 0 at
        // inline 0, item 1 at inline 100; both at block 0).
        for (var i = 0; i < children.Length; i++)
        {
            BoxFragment? childFrag = null;
            foreach (var f in sink.Fragments)
            {
                if (f.Box == children[i]) { childFrag = f; break; }
            }
            Assert.NotNull(childFrag);
            Assert.Equal(i * 100.0, childFrag!.Value.InlineOffset, precision: 3);
            Assert.Equal(0.0, childFrag.Value.BlockOffset, precision: 3);
            Assert.Equal(40.0, childFrag.Value.InlineSize, precision: 3);
            Assert.Equal(30.0, childFrag.Value.BlockSize, precision: 3);
        }
    }

    [Fact]
    public void Flex_column_auto_height_items_content_size_and_stack()
    {
        // Column flex with AUTO-height items, each containing a 60px-tall block
        // child. Previously each item collapsed to height 0 (auto main-size, no
        // content sizing) so all items stacked at the same offset; now content
        // sizes each item to 60 and they stack at 0, 60, 120 — and the child
        // content renders within each item.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        SetLengthPx(flex.Style, PropertyId.Height, 400);
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var items = new Box[3];
        var children = new Box[3];
        for (var i = 0; i < items.Length; i++)
        {
            // Item: no explicit height ⇒ auto main-size ⇒ content-sized.
            var itemStyle = MakeStyle();
            items[i] = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());

            var childStyle = MakeStyle();
            SetLengthPx(childStyle, PropertyId.Height, 60);
            children[i] = Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement());
            items[i].AppendChild(children[i]);
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        var itemFrags = new List<BoxFragment>();
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var f in sink.Fragments)
            {
                if (f.Box == items[i]) { itemFrags.Add(f); break; }
            }
        }
        Assert.Equal(3, itemFrags.Count);
        // Content-sized to the child's 60px + stacked along the block axis.
        for (var i = 0; i < itemFrags.Count; i++)
        {
            Assert.Equal(60.0, itemFrags[i].BlockSize, precision: 3);
            Assert.Equal(i * 60.0, itemFrags[i].BlockOffset, precision: 3);
        }
        // Child content rendered within each item.
        for (var i = 0; i < children.Length; i++)
        {
            Assert.Contains(sink.Fragments, f => f.Box == children[i]);
        }
    }

    [Fact]
    public void Flex_explicit_height_column_items_keep_declared_size_with_content()
    {
        // Guard: an explicit-height column item is NOT content-resized (the
        // auto-sizing only fires for content-determined main-size). The item
        // keeps its declared 90px height even though its child is only 20 tall,
        // and the child still renders inside it.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        SetLengthPx(flex.Style, PropertyId.Height, 400);
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var itemStyle = MakeStyle();
        SetLengthPx(itemStyle, PropertyId.Height, 90);
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        var childStyle = MakeStyle();
        SetLengthPx(childStyle, PropertyId.Height, 20);
        var child = Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement());
        item.AppendChild(child);
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        BoxFragment? itemFrag = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == item) { itemFrag = f; break; }
        }
        Assert.NotNull(itemFrag);
        Assert.Equal(90.0, itemFrag!.Value.BlockSize, precision: 3); // declared, not content-sized
        Assert.Contains(sink.Fragments, f => f.Box == child);        // content still rendered
    }

    [Fact]
    public void Flex_item_inline_text_content_is_laid_out()
    {
        // A column flex item whose content is INLINE (a TextRun, the common
        // `<div>text</div>` shape). The nested BlockLayouter (opted into
        // inline-only-root content) must emit an inline-only-block fragment
        // (with InlineLayout) for the item's text — the block-only child loop
        // would otherwise skip the item's direct inline child.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        SetLengthPx(flex.Style, PropertyId.Height, 400);
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var itemStyle = MakeStyle();
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        item.AppendChild(Box.TextRun("AB", MakeStyle()));
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // Some fragment must carry an InlineLayout (= the shaped "AB").
        Assert.Contains(sink.Fragments, f => f.InlineLayout is not null
            && f.InlineLayout.Value.Lines.Length > 0);
    }

    [Fact]
    public void Flex_column_explicit_height_taller_than_fragmentainer_no_spurious_forced_overflow()
    {
        // An auto-height wrapper block contains an explicit-height column flex
        // TALLER than the fragmentainer. The flex paginates at item boundaries;
        // the wrapper must NOT emit a spurious PAGINATION-FORCED-OVERFLOW-001 —
        // the subtree-extent measure now PROJECTS the paginatable flex to one
        // page (like a paginatable grid), so the wrapper's break-check sees a
        // fitting chunk instead of the rigid 1200px authored height. Mirrors the
        // facade repro (an auto-height wrapper whose own border-box is 0 but
        // whose subtree read the flex's full explicit height).
        var sink = new RecordingFragmentSink();
        var diags = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var wrapper = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        SetLengthPx(flex.Style, PropertyId.Height, 1200); // explicit, > fragmentainer
        for (var i = 0; i < 6; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 200);
            flex.AppendChild(Box.ForElement(BoxKind.BlockContainer, s, MakeElement()));
        }
        wrapper.AppendChild(flex);
        root.AppendChild(wrapper);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: diags,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 500);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diags };
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.DoesNotContain(diags.Diagnostics,
            d => d.Code == PaginateDiagnosticCodes.PaginationForcedOverflow001);
    }

    [Fact]
    public void Flex_item_own_margin_and_padding_do_not_offset_inline_content()
    {
        // PR-#182 Copilot review (margins) + review P3 (border-box placement) —
        // the nested-content root must NOT apply the item's OWN margins (the
        // outer flex already positioned the item including its margin). For an
        // INLINE-ONLY item the content fragment is box == item: it is emitted at
        // the item's BORDER-box origin (= the same origin as the flex geometry
        // fragment) and TextPainter insets the GLYPHS by the item's own border +
        // padding — so the content-inset cycle leaves this fragment's ORIGIN
        // unchanged (insetting it here would double-inset the glyphs). The
        // block-CHILDREN content path is what the content-inset cycle shifts.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        SetLengthPx(flex.Style, PropertyId.Height, 400);
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var itemStyle = MakeStyle();
        SetLengthPx(itemStyle, PropertyId.MarginTop, 20);
        SetLengthPx(itemStyle, PropertyId.MarginLeft, 20);
        SetLengthPx(itemStyle, PropertyId.PaddingTop, 10);
        SetLengthPx(itemStyle, PropertyId.PaddingLeft, 10);
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        item.AppendChild(Box.TextRun("AB", MakeStyle()));
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // The two box==item fragments: the flex geometry (no InlineLayout) and
        // the inline-only content (InlineLayout set).
        BoxFragment? geometry = null, content = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box != item) continue;
            if (f.InlineLayout is not null) content = f; else geometry = f;
        }
        Assert.NotNull(geometry);
        Assert.NotNull(content);
        // The inline-only content fragment is at the item's border-box origin —
        // margin NOT re-applied (Copilot fix) and the fragment ORIGIN is not inset
        // (TextPainter insets the glyphs by the item's own border + padding).
        Assert.Equal(geometry!.Value.BlockOffset, content!.Value.BlockOffset, precision: 3);
        Assert.Equal(geometry.Value.InlineOffset, content.Value.InlineOffset, precision: 3);
    }

    [Fact]
    public void Flex_box_sizing_content_box_item_grows_border_box_by_chrome()
    {
        // Flex box-sizing cycle — a row item with an explicit width + height + border +
        // padding under `box-sizing: content-box` (the initial): the declared sizes are
        // the CONTENT box, so the emitted geometry fragment's border box adds the item's
        // border + padding (width 200 + 30 = 230; height 100 + 30 = 130). Pre-cycle the
        // declared size WAS the border box (chrome ignored).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();
        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(0)); // row
        SetLengthPx(flex.Style, PropertyId.Width, 400);

        var itemStyle = MakeStyle();
        SetLengthPx(itemStyle, PropertyId.Width, 200);
        SetLengthPx(itemStyle, PropertyId.Height, 100);
        SetPaddingAllSides(itemStyle, 10);
        SetSolidBorderAllSides(itemStyle, 5);
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        item.AppendChild(Box.TextRun("x", MakeStyle()));
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        var geometry = FindItemGeometry(sink, item);
        Assert.Equal(230, geometry.InlineSize, precision: 3); // 200 content + 2×(5+10) chrome
        Assert.Equal(130, geometry.BlockSize, precision: 3);  // 100 content + 2×(5+10) chrome
    }

    [Fact]
    public void Flex_box_sizing_border_box_item_declared_size_is_the_border_box()
    {
        // Flex box-sizing cycle — the same item under `box-sizing: border-box`: the
        // declared width/height ARE the border box (the border + padding come out of the
        // content area), so the geometry fragment is exactly 200 × 100 (≥ the 30px chrome).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();
        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(0)); // row
        SetLengthPx(flex.Style, PropertyId.Width, 400);

        var itemStyle = MakeStyle();
        SetLengthPx(itemStyle, PropertyId.Width, 200);
        SetLengthPx(itemStyle, PropertyId.Height, 100);
        SetPaddingAllSides(itemStyle, 10);
        SetSolidBorderAllSides(itemStyle, 5);
        itemStyle.Set(PropertyId.BoxSizing, ComputedSlot.FromKeyword(1)); // border-box
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        item.AppendChild(Box.TextRun("x", MakeStyle()));
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        var geometry = FindItemGeometry(sink, item);
        Assert.Equal(200, geometry.InlineSize, precision: 3);
        Assert.Equal(100, geometry.BlockSize, precision: 3);
    }

    [Fact]
    public void Flex_block_child_content_inset_by_item_border_and_padding()
    {
        // Flex content-inset cycle — a column item with a BLOCK child + border + padding
        // (auto height, content-box): the child fragment is inset to the item's
        // CONTENT-box origin (= geometry origin + border 5 + padding 10 = 15 each axis),
        // and the item's auto-height border box = the child's content + the item's block
        // chrome (30). Pre-cycle the child anchored at the border-box origin (overlapping
        // the border/padding) + the border box omitted the chrome.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();
        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var itemStyle = MakeStyle();
        SetPaddingAllSides(itemStyle, 10);
        SetSolidBorderAllSides(itemStyle, 5);
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        var child = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        child.AppendChild(Box.TextRun("AB", MakeStyle()));
        item.AppendChild(child);
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        var geometry = FindItemGeometry(sink, item);
        BoxFragment? childFrag = null;
        foreach (var f in sink.Fragments)
            if (ReferenceEquals(f.Box, child)) childFrag = f;
        Assert.NotNull(childFrag);

        // Child inset to the content-box origin (15 = border 5 + padding 10).
        Assert.Equal(geometry.InlineOffset + 15, childFrag!.Value.InlineOffset, precision: 3);
        Assert.Equal(geometry.BlockOffset + 15, childFrag.Value.BlockOffset, precision: 3);
        // Item auto-height border box = child content block size + the item's block chrome.
        Assert.Equal(childFrag.Value.BlockSize + 30, geometry.BlockSize, precision: 3);
    }

    [Fact]
    public void Flex_definite_zero_main_size_floors_at_chrome_under_box_sizing()
    {
        // Post-PR-#190 Copilot review — a DEFINITE 0 main size (`width: 0`) with border +
        // padding must map through BoxSizingHelper: content-box border box = 0 + chrome (was
        // dropped to 0 by the `value > 0` gate). A row item `width: 0` + padding 10 + border 5
        // → main border box = the 30px chrome, not 0.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();
        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(0)); // row
        SetLengthPx(flex.Style, PropertyId.Width, 400);

        var itemStyle = MakeStyle();
        SetLengthPx(itemStyle, PropertyId.Width, 0);   // explicit definite 0
        SetLengthPx(itemStyle, PropertyId.Height, 40);
        SetPaddingAllSides(itemStyle, 10);
        SetSolidBorderAllSides(itemStyle, 5);
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        var geometry = FindItemGeometry(sink, item);
        Assert.Equal(30, geometry.InlineSize, precision: 3); // 0 content + 2×(5+10) chrome
    }

    [Fact]
    public void Flex_percentage_cross_size_does_not_floor_to_chrome()
    {
        // Post-PR-#190 Copilot review — an UNRESOLVED percentage cross-size (`height: 50%`,
        // cycle-1 reads it as 0) must NOT floor to the item's chrome under box-sizing (which
        // would make a padded item spuriously `chrome` tall); it stays 0, consistent with
        // FlexLinePacker's line-cross packing. A row item `height: 50%` + padding/border →
        // emitted block size 0 (not 30).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();
        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(0)); // row
        SetLengthPx(flex.Style, PropertyId.Width, 400);
        // align-items: flex-start so a non-auto cross size is used as-is (no stretch).
        flex.Style.Set(PropertyId.AlignItems, ComputedSlot.FromKeyword(11)); // flex-start

        var itemStyle = MakeStyle();
        SetLengthPx(itemStyle, PropertyId.Width, 100);
        itemStyle.Set(PropertyId.Height, ComputedSlot.FromPercentage(50)); // unresolved %
        SetPaddingAllSides(itemStyle, 10);
        SetSolidBorderAllSides(itemStyle, 5);
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        var geometry = FindItemGeometry(sink, item);
        Assert.Equal(0, geometry.BlockSize, precision: 3); // unresolved %, NOT the 30px chrome
    }

    /// <summary>The flex GEOMETRY fragment for <paramref name="item"/> (box == item with
    /// NO inline layout — the box-decoration fragment, distinct from an inline-only item's
    /// own text fragment which carries an InlineLayout).</summary>
    private static BoxFragment FindItemGeometry(RecordingFragmentSink sink, Box item)
    {
        foreach (var f in sink.Fragments)
            if (ReferenceEquals(f.Box, item) && f.InlineLayout is null) return f;
        throw new Xunit.Sdk.XunitException("No flex geometry fragment found for the item.");
    }

    [Fact]
    public void Flex_item_out_of_flow_inline_descendant_not_in_inline_flow()
    {
        // PR-#182 Copilot review — an OUT-OF-FLOW (position: absolute) inline
        // descendant inside an inline-only flex item must NOT contribute to the
        // item's inline content (CollectInlineTextRuns skips it); the abspos
        // pass anchors it separately. Here the item's inline-only content
        // fragment shapes only "AB" (length 2), not the abspos "XY".
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        SetLengthPx(flex.Style, PropertyId.Height, 400);
        SetLengthPx(flex.Style, PropertyId.Width, 200);

        var itemStyle = MakeStyle();
        var item = Box.ForElement(BoxKind.BlockContainer, itemStyle, MakeElement());
        item.AppendChild(Box.TextRun("AB", MakeStyle()));
        // An inline-level out-of-flow span "XY" — must be excluded from the line.
        var absStyle = MakeStyle();
        absStyle.Set(PropertyId.Position, ComputedSlot.FromKeyword(2)); // absolute
        var absSpan = Box.ForElement(BoxKind.InlineBox, absStyle, MakeElement());
        absSpan.AppendChild(Box.TextRun("XY", MakeStyle()));
        item.AppendChild(absSpan);
        flex.AppendChild(item);
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // The item's inline-only content fragment shapes only the in-flow "AB".
        BoxFragment? content = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == item && f.InlineLayout is not null) { content = f; break; }
        }
        Assert.NotNull(content);
        var lines = content!.Value.InlineLayout!.Value.Lines;
        Assert.Single(lines);
        // 2 glyphs ("AB") — the abspos "XY" did NOT join the line.
        var glyphCount = 0;
        foreach (var slice in lines[0].Slices) glyphCount += slice.GlyphLength;
        Assert.Equal(2, glyphCount);
    }

    [Fact]
    public void Flex_column_auto_height_content_items_paginate_first_attempt_PageComplete()
    {
        // Backlog #7 — an AUTO-height column flex whose items are content-sized
        // (each a 200px block child, no explicit item height) taller than the
        // fragmentainer now PAGINATES: the content-aware PreMeasureFlexMainExtent
        // grows the wrapper so the first AttemptLayout returns PageComplete
        // (before, the pre-measure saw 0 → AllDone, all on one overflowing page).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(2)); // column
        for (var i = 0; i < 5; i++)
        {
            var item = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
            var childStyle = MakeStyle();
            SetLengthPx(childStyle, PropertyId.Height, 200);
            item.AppendChild(Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement()));
            flex.AppendChild(item);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 500);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // 5 × 200px content (1000px) > 500px fragmentainer → paginates.
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
    }

    [Fact]
    public void Flex_column_reverse_paginates_in_visual_reverse_dom_order()
    {
        // Backlog #4 — a PAGINATING column-reverse emits in VISUAL (reverse-DOM)
        // order: the first page's first item is the LAST DOM item. 4 × 300px
        // items on a 500px page → page 1 fits exactly one item = DOM item 3.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexDirection, ComputedSlot.FromKeyword(3)); // column-reverse
        var items = new Box[4];
        for (var i = 0; i < 4; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 300);
            items[i] = Box.ForElement(BoxKind.BlockContainer, s, MakeElement());
            flex.AppendChild(items[i]);
        }
        root.AppendChild(flex);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 500);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        // The flex-item fragments emitted on this first page (in emission order).
        var emitted = new List<Box>();
        foreach (var f in sink.Fragments)
            for (var i = 0; i < 4; i++)
                if (f.Box == items[i]) { emitted.Add(f.Box); break; }
        // Page 1's FIRST item is the VISUAL-first = the LAST DOM item (items[3]),
        // anchored at the page top (block offset 0).
        Assert.NotEmpty(emitted);
        Assert.Same(items[3], emitted[0]);
        BoxFragment? firstItemFrag = null;
        foreach (var f in sink.Fragments) if (f.Box == items[3]) { firstItemFrag = f; break; }
        Assert.NotNull(firstItemFrag);
        Assert.Equal(0.0, firstItemFrag!.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public void IsPaginatablePerStyle_row_nowrap_is_now_eligible_for_intra_item_content_split()
    {
        // Task 1 — a `row` + `nowrap` flex container is now paginatable (intra-item
        // content split). The defaults are flex-direction: row + flex-wrap: nowrap,
        // so a bare flex container qualifies (pre-Task-1 this returned false).
        var flex = BuildFlexContainer();
        Assert.True(FlexLayouter.IsPaginatablePerStyle(flex));
    }

    [Fact]
    public void IsPaginatablePerStyle_row_wrap_reverse_is_not_eligible()
    {
        // Row + wrap-reverse stays ineligible — its cross-axis swap origin derives
        // from the UNFRAGMENTED size, so partial content would land at the wrong
        // offset (deferred).
        var flex = BuildFlexContainer();
        flex.Style.Set(PropertyId.FlexWrap, ComputedSlot.FromKeyword(2)); // wrap-reverse
        Assert.False(FlexLayouter.IsPaginatablePerStyle(flex));
    }

    [Fact]
    public void IsPaginatablePerStyle_non_flex_box_is_not_eligible()
    {
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        Assert.False(FlexLayouter.IsPaginatablePerStyle(block));
    }

    [Fact]
    public void FlushRangeTo_slices_buffer_to_cross_window_and_reports_remaining()
    {
        // Task 1 — the row-nowrap content slice primitive. Buffer 4 fragments at
        // cross offsets 0/30/60/90 (height 30); flush the window [30, 90) → only
        // the fragments STARTING in the window emit (30, 60), re-anchored so the
        // window top maps to the content cross origin (→ 0, 30); AnyRemaining is
        // true (the 90-offset fragment starts on a later page).
        var sink = new BufferingMeasureSink();
        var box = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        for (var i = 0; i < 4; i++)
        {
            sink.Emit(new BoxFragment(
                Box: box, InlineOffset: 0, BlockOffset: i * 30, InlineSize: 100, BlockSize: 30));
        }

        var target = new RecordingFragmentSink();
        var (extent, anyRemaining) = sink.FlushRangeTo(
            target, inlineTranslation: 5, itemCrossOffsetAbs: 0, contentCrossOriginAbs: 0,
            windowFrom: 30, windowTo: 90);

        Assert.True(anyRemaining);                          // the 90-offset fragment defers
        Assert.Equal(60.0, extent, precision: 3);           // deepest emitted bottom (30→0..30, 60→30..60)
        Assert.Equal(2, target.Fragments.Count);
        Assert.Equal(0.0, target.Fragments[0].BlockOffset, precision: 3);   // 30 → page top
        Assert.Equal(30.0, target.Fragments[1].BlockOffset, precision: 3);  // 60 → 30
        Assert.Equal(5.0, target.Fragments[0].InlineOffset, precision: 3);  // inline translation applied
    }

    [Fact]
    public void FlushRangeTo_window_covering_all_content_emits_all_with_no_remaining()
    {
        // A window covering all content → every fragment emits, AnyRemaining false
        // (= the last page of a split, or a fits-in-one-page degenerate case).
        var sink = new BufferingMeasureSink();
        var box = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        for (var i = 0; i < 3; i++)
        {
            sink.Emit(new BoxFragment(
                Box: box, InlineOffset: 0, BlockOffset: i * 20, InlineSize: 100, BlockSize: 20));
        }
        var target = new RecordingFragmentSink();
        var (extent, anyRemaining) = sink.FlushRangeTo(
            target, inlineTranslation: 0, itemCrossOffsetAbs: 0, contentCrossOriginAbs: 0,
            windowFrom: 0, windowTo: 1000);
        Assert.False(anyRemaining);
        Assert.Equal(3, target.Fragments.Count);
        Assert.Equal(60.0, extent, precision: 3);           // last fragment bottom (40 + 20)
    }

    // ====================================================================
    //  Helpers
    // ====================================================================

    /// <summary>Build a flex container Box (= block-outer + flex-inner,
    /// matching <c>display: flex</c>). Mirrors
    /// <see cref="MulticolLayouterTests"/>'s <c>BuildMulticolContainer</c>
    /// shape but uses the dedicated <see cref="BoxKind.FlexContainer"/>
    /// rather than property-based detection.</summary>
    private static Box BuildFlexContainer()
    {
        var style = MakeStyle();
        return Box.ForElement(BoxKind.FlexContainer, style, MakeElement());
    }

    private static ComputedStyle MakeStyle() => ComputedStyle.RentForExclusiveTesting();

    private static void SetLengthPx(ComputedStyle style, PropertyId id, double px) =>
        style.Set(id, ComputedSlot.FromLengthPx(px));

    /// <summary>Flex box-sizing cycle tests — set a uniform SOLID border (width px,
    /// style solid) on all four sides. The width is §4.3-gated by the style
    /// (<c>ReadLengthPxOrZero</c> returns 0 when the style is none/hidden), so the
    /// style MUST be set for the width to count as chrome.</summary>
    private static void SetSolidBorderAllSides(ComputedStyle style, double px)
    {
        (PropertyId Width, PropertyId Style)[] edges =
        [
            (PropertyId.BorderTopWidth, PropertyId.BorderTopStyle),
            (PropertyId.BorderRightWidth, PropertyId.BorderRightStyle),
            (PropertyId.BorderBottomWidth, PropertyId.BorderBottomStyle),
            (PropertyId.BorderLeftWidth, PropertyId.BorderLeftStyle),
        ];
        foreach (var (w, s) in edges)
        {
            style.Set(w, ComputedSlot.FromLengthPx(px));
            style.Set(s, ComputedSlot.FromKeyword(4)); // 4 = solid
        }
    }

    /// <summary>Flex box-sizing cycle tests — uniform padding on all four sides.</summary>
    private static void SetPaddingAllSides(ComputedStyle style, double px)
    {
        foreach (var p in new[]
            { PropertyId.PaddingTop, PropertyId.PaddingRight, PropertyId.PaddingBottom, PropertyId.PaddingLeft })
            style.Set(p, ComputedSlot.FromLengthPx(px));
    }

    private static AngleSharp.Dom.IElement MakeElement()
    {
        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var doc = parser.ParseDocument("<div></div>");
        return doc.CreateElement("div");
    }

    /// <summary>Recording sink mirroring
    /// <see cref="MulticolLayouterTests"/>'s.</summary>
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

    /// <summary>Recording diagnostic sink mirroring
    /// <see cref="MulticolLayouterTests"/>'s. Plumbed but unused in
    /// cycle 1 (no flex-specific diagnostics yet).</summary>
    private sealed class RecordingDiagnosticsSink : IPaginateDiagnosticsSink
    {
        public List<PaginateDiagnostic> Diagnostics { get; } = new();
        public void Emit(PaginateDiagnostic diagnostic) => Diagnostics.Add(diagnostic);
    }

    /// <summary>Synthetic-font shaper resolver mirroring
    /// <see cref="MulticolLayouterTests"/>'s. Plumbed but unused in
    /// cycle 1 (no inline content inside flex items yet).</summary>
    private sealed class SyntheticShaperResolver : IShaperResolver
    {
        private readonly HbShaper _shaper = new(SyntheticFont.Build(), fontSizePx: 12);
        public HbShaper Resolve(ComputedStyle style) => _shaper;
        public void Dispose() => _shaper.Dispose();
    }
}
