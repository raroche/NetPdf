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
