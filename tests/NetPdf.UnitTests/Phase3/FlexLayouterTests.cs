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
