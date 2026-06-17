// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
using NetPdf.Paginate;
using NetPdf.Paginate.Diagnostics;
using NetPdf.Layout.Inline;
using NetPdf.Text.Shaping;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>
/// Phase 3 Task 7 cycle 1 + PR #22 review pass — BlockLayouter tests.
/// Drives the layouter through synthesized box trees + verifies
/// position math, page-break dispatch via the IBreakResolver,
/// continuation token shape, and the strategy / cancellation hooks
/// inherited from ILayouter. Cycle 2 will add margin-collapse +
/// recursive-layout coverage; cycle 3 will add vertical writing-mode
/// + auto/percentage-resolution coverage. The cycle-2/3 deferrals
/// are pinned by failing-skip tests at the bottom of this file.
/// </summary>
public sealed class BlockLayouterTests
{
    // --- Border-box geometry contract (PR #22 fix #3 + Copilot #2) ---

    [Fact]
    public void Fragment_block_size_is_border_box_excluding_margin()
    {
        // PR #22 fix #3 + Copilot #2 — BoxFragment.BlockSize is the
        // border box (border + padding + content), NOT the margin box.
        // Pagination accounting uses the margin-box extent for the
        // cursor advance, but the fragment stores the border-box for
        // the painter.
        var sink = new RecordingFragmentSink();
        var style = MakeStyle();
        SetLengthPx(style, PropertyId.MarginTop, 10);
        SolidBorders(style);
        SetLengthPx(style, PropertyId.BorderTopWidth, 5);
        SetLengthPx(style, PropertyId.PaddingTop, 8);
        SetLengthPx(style, PropertyId.Height, 100);
        SetLengthPx(style, PropertyId.PaddingBottom, 8);
        SetLengthPx(style, PropertyId.BorderBottomWidth, 5);
        SetLengthPx(style, PropertyId.MarginBottom, 10);

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Single(sink.Fragments);
        // Border-box block size = 5 + 8 + 100 + 8 + 5 = 126
        Assert.Equal(126, sink.Fragments[0].BlockSize);
        // BlockOffset = UsedBlockSize (0) + marginTop (10) = 10
        Assert.Equal(10, sink.Fragments[0].BlockOffset);
        // After this block: UsedBlockSize advanced by margin-box extent
        // (10 + 126 + 10 = 146).
        Assert.Equal(146, ctx.UsedBlockSize);
    }

    [Fact]
    public void Fragment_inline_size_is_border_box_excluding_margin()
    {
        // PR #22 fix #3 + Copilot #2 — InlineSize is border-box
        // (ContentInlineSize - margin-left - margin-right), and
        // InlineOffset is the border-box left edge (= margin-left).
        var sink = new RecordingFragmentSink();
        var style = MakeStyle();
        SetLengthPx(style, PropertyId.MarginLeft, 30);
        SetLengthPx(style, PropertyId.MarginRight, 20);
        SetLengthPx(style, PropertyId.Height, 100);

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // InlineOffset = margin-left = 30
        Assert.Equal(30, sink.Fragments[0].InlineOffset);
        // InlineSize = ContentInlineSize - marginLeft - marginRight = 600 - 30 - 20 = 550
        Assert.Equal(550, sink.Fragments[0].InlineSize);
    }

    [Fact]
    public void Explicit_width_sizes_the_fragment_border_box_inline_size()
    {
        // Body-explicit-width gap fix — a plain block container with an
        // explicit `width` (the CONTENT-box size) gets a border-box
        // fragment of width + inline borders + padding, mirroring the
        // inline-only block path (CSS 2.2 §10.3.3 cycle-1 subset). The
        // pre-fix behavior filled the available range, so an empty
        // `width: 64px` div painted a full-content-width background band.
        var sink = new RecordingFragmentSink();
        var style = MakeStyle();
        SetLengthPx(style, PropertyId.Width, 64);
        SetLengthPx(style, PropertyId.MarginLeft, 30);
        SolidBorders(style);
        SetLengthPx(style, PropertyId.BorderLeftWidth, 2);
        SetLengthPx(style, PropertyId.BorderRightWidth, 2);
        SetLengthPx(style, PropertyId.PaddingLeft, 8);
        SetLengthPx(style, PropertyId.PaddingRight, 8);
        SetLengthPx(style, PropertyId.Height, 100);

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Single(sink.Fragments);
        // Border box = 64 (content) + 2 + 2 (borders) + 8 + 8 (padding) = 84,
        // NOT 600 - 30 = 570 (the auto fill). Margin distribution stays
        // deferred — the box keeps its inline-start edge (margin-left).
        Assert.Equal(84, sink.Fragments[0].InlineSize);
        Assert.Equal(30, sink.Fragments[0].InlineOffset);
    }

    [Fact]
    public void Explicit_width_in_nested_recursion_matches_the_outer_path()
    {
        // Body-explicit-width gap fix — the recursive subtree path sizes an
        // explicit-width nested block exactly like the outer dispatch path,
        // and an auto sibling still fills the parent's content box.
        var sink = new RecordingFragmentSink();
        var parentStyle = MakeStyle();
        SetLengthPx(parentStyle, PropertyId.Width, 200);
        var fixedChildStyle = MakeStyle();
        SetLengthPx(fixedChildStyle, PropertyId.Width, 64);
        SetLengthPx(fixedChildStyle, PropertyId.Height, 40);
        var autoChildStyle = MakeStyle();
        SetLengthPx(autoChildStyle, PropertyId.Height, 40);

        var root = Box.CreateRoot(MakeStyle());
        var parent = Box.ForElement(BoxKind.BlockContainer, parentStyle, MakeElement());
        parent.AppendChild(Box.ForElement(BoxKind.BlockContainer, fixedChildStyle, MakeElement()));
        parent.AppendChild(Box.ForElement(BoxKind.BlockContainer, autoChildStyle, MakeElement()));
        root.AppendChild(parent);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Parent border box = its explicit 200 (no borders/padding);
        // nested explicit child = 64; nested auto child fills the
        // parent's content box (= 200, parent has no borders/padding).
        // Emit order: parent wrapper first, then the children in
        // document order via the recursive subtree walk.
        Assert.Equal(3, sink.Fragments.Count);
        Assert.Equal(200, sink.Fragments[0].InlineSize);
        Assert.Equal(64, sink.Fragments[1].InlineSize);
        Assert.Equal(200, sink.Fragments[2].InlineSize);
    }

    [Fact]
    public void Percentage_width_and_margins_resolve_against_the_containing_block()
    {
        // Body-percent cycle — CSS 2.2 §10.2/§8.3: width: 50% of the 600px containing block =
        // 300; margin-left: 10% = 60 (the inline-axis base on every side).
        var sink = new RecordingFragmentSink();
        var style = MakeStyle();
        style.Set(PropertyId.Width, ComputedSlot.FromPercentage(50));
        style.Set(PropertyId.MarginLeft, ComputedSlot.FromPercentage(10));
        SetLengthPx(style, PropertyId.Height, 40);

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Single(sink.Fragments);
        Assert.Equal(300, sink.Fragments[0].InlineSize);
        Assert.Equal(60, sink.Fragments[0].InlineOffset);
    }

    [Fact]
    public void Percentage_padding_resolves_against_the_inline_axis_even_vertically()
    {
        // §8.4 — padding-top: 10% resolves against the containing block's WIDTH (600 → 60),
        // growing the border-box block size: 60 + 40 + 60 = 160.
        var sink = new RecordingFragmentSink();
        var style = MakeStyle();
        style.Set(PropertyId.PaddingTop, ComputedSlot.FromPercentage(10));
        style.Set(PropertyId.PaddingBottom, ComputedSlot.FromPercentage(10));
        SetLengthPx(style, PropertyId.Height, 40);

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(160, sink.Fragments[0].BlockSize);
    }

    [Fact]
    public void Percentage_width_in_the_nested_recursion_uses_the_parent_content_box()
    {
        // The recursion resolves % against the PARENT's content box: a 50% child of a 400px
        // parent is 200.
        var sink = new RecordingFragmentSink();
        var parentStyle = MakeStyle();
        SetLengthPx(parentStyle, PropertyId.Width, 400);
        var childStyle = MakeStyle();
        childStyle.Set(PropertyId.Width, ComputedSlot.FromPercentage(50));
        SetLengthPx(childStyle, PropertyId.Height, 40);

        var root = Box.CreateRoot(MakeStyle());
        var parent = Box.ForElement(BoxKind.BlockContainer, parentStyle, MakeElement());
        parent.AppendChild(Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement()));
        root.AppendChild(parent);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(2, sink.Fragments.Count);
        Assert.Equal(400, sink.Fragments[0].InlineSize);
        Assert.Equal(200, sink.Fragments[1].InlineSize);
    }

    [Fact]
    public void Percentage_margin_top_positions_a_nested_inline_only_child()
    {
        // Post-PR-#163 review P1 — the nested inline-only path read margin-top with the
        // percent-blind helper for the fragment POSITION while the cursor advance was
        // percent-aware: `margin-top: 10%` of the 400px parent must push the child 40px DOWN
        // (not reserve 40px after it).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();
        var parentStyle = MakeStyle();
        SetLengthPx(parentStyle, PropertyId.Width, 400);
        var childStyle = MakeStyle();
        childStyle.Set(PropertyId.MarginTop, ComputedSlot.FromPercentage(10));
        SetLengthPx(childStyle, PropertyId.FontSize, 16);

        var root = Box.CreateRoot(MakeStyle());
        var parent = Box.ForElement(BoxKind.BlockContainer, parentStyle, MakeElement());
        var blockChild = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        SetLengthPx(blockChild.Style, PropertyId.Height, 10);
        var inlineOnly = Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement());
        inlineOnly.AppendChild(Box.TextRun("AB", childStyle));
        parent.AppendChild(blockChild);    // forces the RECURSIVE (mixed-children) path
        parent.AppendChild(inlineOnly);
        root.AppendChild(parent);

        using var layouter = new BlockLayouter(root, sink, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Fragments: parent, blockChild (10 tall at parent top), the inline-only line.
        // The inline-only border box must start at blockChild bottom + 40 (10% of 400).
        var inlineFrag = sink.Fragments[^1];
        Assert.Equal(10 + 40, inlineFrag.BlockOffset, 3);
    }

    [Fact]
    public void Auto_inline_margins_center_an_explicit_width_block()
    {
        // §10.3.3 (auto-margins cycle) — `width: 200px; margin: 0 auto` in a 600px containing
        // block centres the border box: offset (600 − 200) / 2 = 200.
        var sink = new RecordingFragmentSink();
        var style = MakeStyle();
        SetLengthPx(style, PropertyId.Width, 200);
        SetKeyword(style, PropertyId.MarginLeft, 0);    // the authored `auto` keyword
        SetKeyword(style, PropertyId.MarginRight, 0);
        SetLengthPx(style, PropertyId.Height, 40);

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(200, sink.Fragments[0].InlineOffset);
        Assert.Equal(200, sink.Fragments[0].InlineSize);
    }

    [Fact]
    public void Block_replaced_element_with_explicit_width_does_not_fill()
    {
        // img-pipeline cycle — a BlockReplacedElement with an explicit width (the sizing
        // pre-pass writes the §10.3.2 used size into the slots) takes the explicit-width
        // mapping, not the fill: a 100×50 image emits a 100-wide fragment in the 600 page.
        var sink = new RecordingFragmentSink();
        var style = MakeStyle();
        SetLengthPx(style, PropertyId.Width, 100);
        SetLengthPx(style, PropertyId.Height, 50);

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockReplacedElement, style, MakeElement()));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(100, sink.Fragments[0].InlineSize);
        Assert.Equal(50, sink.Fragments[0].BlockSize);
    }

    [Fact]
    public void Block_replaced_element_auto_margins_centre_it()
    {
        // img-pipeline cycle — `display: block; margin: 0 auto` is the canonical image
        // centering: width 100 in the 600 page → offset (600 − 100) / 2 = 250.
        var sink = new RecordingFragmentSink();
        var style = MakeStyle();
        SetLengthPx(style, PropertyId.Width, 100);
        SetLengthPx(style, PropertyId.Height, 50);
        SetKeyword(style, PropertyId.MarginLeft, 0);    // the authored `auto` keyword
        SetKeyword(style, PropertyId.MarginRight, 0);

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockReplacedElement, style, MakeElement()));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(250, sink.Fragments[0].InlineOffset);
        Assert.Equal(100, sink.Fragments[0].InlineSize);
    }

    [Fact]
    public void Single_auto_margin_absorbs_the_leftover()
    {
        // One auto side takes the whole remainder: width 200, margin-right 50 → left auto
        // absorbs 600 − 200 − 50 = 350.
        var sink = new RecordingFragmentSink();
        var style = MakeStyle();
        SetLengthPx(style, PropertyId.Width, 200);
        SetKeyword(style, PropertyId.MarginLeft, 0);
        SetLengthPx(style, PropertyId.MarginRight, 50);
        SetLengthPx(style, PropertyId.Height, 40);

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(350, sink.Fragments[0].InlineOffset);
    }

    [Fact]
    public void Auto_margins_without_an_explicit_width_keep_the_fill()
    {
        // Auto width + auto margins = the pre-cycle fill (auto margins read 0).
        var sink = new RecordingFragmentSink();
        var style = MakeStyle();
        SetKeyword(style, PropertyId.MarginLeft, 0);
        SetKeyword(style, PropertyId.MarginRight, 0);
        SetLengthPx(style, PropertyId.Height, 40);

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(0, sink.Fragments[0].InlineOffset);
        Assert.Equal(600, sink.Fragments[0].InlineSize);
    }

    [Fact]
    public void Overconstrained_auto_margins_clamp_at_zero()
    {
        // Width 700 exceeds the 600px containing block — the auto margins clamp at 0 (§10.3.3).
        var sink = new RecordingFragmentSink();
        var style = MakeStyle();
        SetLengthPx(style, PropertyId.Width, 700);
        SetKeyword(style, PropertyId.MarginLeft, 0);
        SetKeyword(style, PropertyId.MarginRight, 0);
        SetLengthPx(style, PropertyId.Height, 40);

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(0, sink.Fragments[0].InlineOffset);
        Assert.Equal(700, sink.Fragments[0].InlineSize);
    }

    [Fact]
    public void Percentage_height_resolves_against_the_fragmentainer_and_a_definite_parent()
    {
        // Percent-height cycle — outer: 50% of the 800px content height = 400. Nested: 25% of
        // the parent's DEFINITE 400px content height = 100; with an AUTO parent it computes to
        // auto (0-height border box).
        var sink = new RecordingFragmentSink();
        var parentStyle = MakeStyle();
        parentStyle.Set(PropertyId.Height, ComputedSlot.FromPercentage(50));
        var childStyle = MakeStyle();
        childStyle.Set(PropertyId.Height, ComputedSlot.FromPercentage(25));

        var root = Box.CreateRoot(MakeStyle());
        var parent = Box.ForElement(BoxKind.BlockContainer, parentStyle, MakeElement());
        parent.AppendChild(Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement()));
        root.AppendChild(parent);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(400, sink.Fragments[0].BlockSize);   // 50% × 800
        Assert.Equal(100, sink.Fragments[1].BlockSize);   // 25% × the parent's definite 400
    }

    [Fact]
    public void Auto_margins_distribute_within_the_float_adjusted_range()
    {
        // Post-PR-#164 review P1 — the OUTER path distributes auto margins across the SAME
        // float-adjusted range placement uses: a 100px left float leaves [100, 600] (500 wide);
        // the centred 200px box sits at 100 + (500 − 200)/2 = 250 (distributing across the full
        // 600px containing block then adding the float offset would drift it to 300).
        var sink = new RecordingFragmentSink();
        var floatStyle = MakeStyle();
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 40);
        SetKeyword(floatStyle, PropertyId.Float, 1);   // float: left
        var centredStyle = MakeStyle();
        SetLengthPx(centredStyle, PropertyId.Width, 200);
        SetKeyword(centredStyle, PropertyId.MarginLeft, 0);
        SetKeyword(centredStyle, PropertyId.MarginRight, 0);
        SetLengthPx(centredStyle, PropertyId.Height, 20);

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement()));
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, centredStyle, MakeElement()));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        var centred = sink.Fragments[^1];
        Assert.Equal(200, centred.InlineSize);
        Assert.Equal(250, centred.InlineOffset, 3);
    }

    [Fact]
    public void Border_box_sizing_makes_the_declared_width_the_border_box()
    {
        // Body box-sizing cycle (CSS Basic UI 4 §10) — width: 200 + 20px padding each side:
        // border-box → the fragment IS 200 (content 160); content-box default → 240.
        foreach (var (borderBox, expected) in new[] { (true, 200.0), (false, 240.0) })
        {
            var sink = new RecordingFragmentSink();
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 200);
            SetLengthPx(style, PropertyId.PaddingLeft, 20);
            SetLengthPx(style, PropertyId.PaddingRight, 20);
            SetLengthPx(style, PropertyId.Height, 40);
            if (borderBox) SetKeyword(style, PropertyId.BoxSizing, 1);   // border-box

            var root = Box.CreateRoot(MakeStyle());
            root.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
            using var layouter = new BlockLayouter(root, sink);
            var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
            var layoutCtx = new LayoutContext(ctx);
            using var resolver = new BreakResolver();
            layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

            Assert.Equal(expected, sink.Fragments[0].InlineSize);
        }
    }

    [Fact]
    public void Border_box_sizing_floors_at_the_insets()
    {
        // A border-box width SMALLER than the insets floors there (content box at 0) —
        // the PR #155 margin-box rule, mirrored for the body.
        var sink = new RecordingFragmentSink();
        var style = MakeStyle();
        SetLengthPx(style, PropertyId.Width, 10);
        SetLengthPx(style, PropertyId.PaddingLeft, 20);
        SetLengthPx(style, PropertyId.PaddingRight, 20);
        SetLengthPx(style, PropertyId.Height, 40);
        SetKeyword(style, PropertyId.BoxSizing, 1);

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(40, sink.Fragments[0].InlineSize);
    }

    [Fact]
    public void Border_box_sizing_makes_the_declared_height_the_border_box()
    {
        // Box-sizing audit (block HEIGHT — the symmetric gap to the done width work):
        // height: 100 + 15px padding top/bottom → border-box → the fragment block size
        // IS 100 (content 70); content-box (initial) → 130. Byte-identical for content-box.
        foreach (var (borderBox, expected) in new[] { (true, 100.0), (false, 130.0) })
        {
            var sink = new RecordingFragmentSink();
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Width, 200);
            SetLengthPx(style, PropertyId.Height, 100);
            SetLengthPx(style, PropertyId.PaddingTop, 15);
            SetLengthPx(style, PropertyId.PaddingBottom, 15);
            if (borderBox) SetKeyword(style, PropertyId.BoxSizing, 1);   // border-box

            var root = Box.CreateRoot(MakeStyle());
            root.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));
            using var layouter = new BlockLayouter(root, sink);
            var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
            var layoutCtx = new LayoutContext(ctx);
            using var resolver = new BreakResolver();
            layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

            Assert.Equal(expected, sink.Fragments[0].BlockSize);
        }
    }

    [Fact]
    public void Float_percentage_width_resolves_against_the_bfc()
    {
        // Float-percent cycle — a float's width: 50% resolves against the 600px BFC content box.
        var sink = new RecordingFragmentSink();
        var floatStyle = MakeStyle();
        floatStyle.Set(PropertyId.Width, ComputedSlot.FromPercentage(50));
        SetLengthPx(floatStyle, PropertyId.Height, 40);
        SetKeyword(floatStyle, PropertyId.Float, 1);   // left

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement()));
        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(300, sink.Fragments[0].InlineSize);
    }

    [Fact]
    public void Float_percentage_padding_defers_at_the_page_boundary()
    {
        // PR #165 review P1 — the WouldFloatOverflow pre-check resolves % lengths like
        // EmitFloat (the shared ReadFloatBoxModel): a float with padding-top: 10% (= 60px of
        // the 600px BFC) after a 700-tall block totals 700 + 60 + 50 = 810 > 800 → DEFER to
        // the next page. Pre-fix the check read the % as 0 → 750 ≤ 800 → emitted + overflowed
        // (a PAGINATION-FORCED-OVERFLOW-001 instead of a clean deferral).
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        var root = Box.CreateRoot(MakeStyle());

        var blockStyle = MakeStyle();
        SetLengthPx(blockStyle, PropertyId.Height, 700);
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement()));

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);   // left
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 50);
        floatStyle.Set(PropertyId.PaddingTop, ComputedSlot.FromPercentage(10));
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement()));

        using var layouter = new BlockLayouter(
            root, sink, incomingContinuation: null, diagnostics: diagSink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var cont = Assert.IsType<BlockContinuation>(result.Continuation);
        Assert.Equal(1, cont.ResumeAtChild);
        Assert.Single(sink.Fragments);   // only the 700 block; the float deferred cleanly.
        Assert.Empty(diagSink.Diagnostics);
    }

    [Fact]
    public void Float_percentage_padding_insets_its_children_after_the_used_value_rewrite()
    {
        // PR #165 review P1 — EmitFloat rewrites the used % padding IN PLACE before the child
        // recursion, so the float's child block is placed at the float's CONTENT box:
        // padding-left: 10% of 600 = 60px → child InlineOffset 60, fill width 360 − 60 = 300.
        // Pre-fix the recursion's absolute-only reads saw 0 → the child painted at the border
        // edge at the full border-box width.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);   // left
        floatStyle.Set(PropertyId.Width, ComputedSlot.FromPercentage(50));       // 300
        floatStyle.Set(PropertyId.PaddingLeft, ComputedSlot.FromPercentage(10)); // 60
        SetLengthPx(floatStyle, PropertyId.Height, 100);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        var childStyle = MakeStyle();
        SetLengthPx(childStyle, PropertyId.Height, 30);
        floatBox.AppendChild(Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement()));
        root.AppendChild(floatBox);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Fragment 0 = the float (border box 60 + 300 = 360 wide); fragment 1 = its child at
        // the content-box left (60) and the content-box fill width (300).
        Assert.Equal(2, sink.Fragments.Count);
        Assert.Equal(360, sink.Fragments[0].InlineSize);
        Assert.Equal(60, sink.Fragments[1].InlineOffset);
        Assert.Equal(300, sink.Fragments[1].InlineSize);
    }

    // --- Multi-block stacking ----------------------------------------

    [Fact]
    public void Layouter_stacks_multiple_blocks_with_collapse()
    {
        // Two block children with explicit margins. Per Phase 3 Task 7
        // cycle 2 + CSS 2.1 §8.3.1 — adjacent vertical margins collapse:
        //   Block 0: marginBottom=10
        //   Block 1: marginTop=5
        //   Collapsed gap between them = max(10, 5) = 10 (NOT 15)
        //
        // Block 0: BlockOffset=10 (marginTop), BlockSize=200 (border-box),
        //          cursor advances 10+200+10 = 220
        // Block 1: collapsedGap = max(10, 5) = 10; topShift = 10 - 10 = 0
        //          (the 10 was already in the cursor as block 0's marginBottom).
        //          BlockOffset = 220 + 0 = 220, BlockSize = 150.
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 200, marginTop: 10, marginBottom: 10),
            (height: 150, marginTop: 5, marginBottom: 5));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Equal(2, sink.Fragments.Count);

        // Block 0: BlockOffset=10 (marginTop), BlockSize=200 (border-box).
        Assert.Equal(10, sink.Fragments[0].BlockOffset);
        Assert.Equal(200, sink.Fragments[0].BlockSize);

        // Block 1: cursor after block 0 = 220 (= 10+200+10, including
        // bottom margin). Collapse: max(10, 5) = 10; topShift = 0.
        // BlockOffset = 220, BlockSize = 150.
        Assert.Equal(220, sink.Fragments[1].BlockOffset);
        Assert.Equal(150, sink.Fragments[1].BlockSize);
    }

    // --- Page break: third box doesn't fit ---------------------------

    [Fact]
    public void Layouter_returns_PageComplete_when_next_block_overflows()
    {
        var sink = new RecordingFragmentSink();
        // Page = 800. Margin-box advance: 250, 250, 350.
        // After 2 blocks: cursor = 500. Block 3 would push to 850 > 800.
        var (root, _) = BuildTree(
            (height: 230, marginTop: 10, marginBottom: 10),  // 250
            (height: 230, marginTop: 10, marginBottom: 10),  // 250
            (height: 330, marginTop: 10, marginBottom: 10)); // 350

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.Equal(2, sink.Fragments.Count);

        var blockCont = Assert.IsType<BlockContinuation>(result.Continuation);
        Assert.Equal(2, blockCont.ResumeAtChild);
        // ConsumedBlockSize = cumulative across pages (Copilot #1).
        // Prior pages: 0 (this is page 1). Current attempt with cycle-2
        // margin collapse: block 0 cursor=250 (10+230+10); block 1
        // collapses (10, 10)→10, topShift=0, advance=240 (0+230+10).
        // Total = 250+240 = 490 (NOT 500 — saved 10 from collapse).
        Assert.Equal(490, blockCont.ConsumedBlockSize);
    }

    [Fact]
    public void Continuation_consumed_block_size_accumulates_across_pages()
    {
        // PR #22 Copilot #1 — ConsumedBlockSize is documented as
        // cumulative across prior pages. When called with a
        // BlockContinuation that already has a non-zero
        // ConsumedBlockSize, the next continuation must include it.
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 230, marginTop: 10, marginBottom: 10),  // already on page 1
            (height: 230, marginTop: 10, marginBottom: 10),  // page 2 — fits
            (height: 700, marginTop: 10, marginBottom: 10)); // page 2 — doesn't fit

        // Resume on page 2 with prior pages = 500 px consumed.
        var continuation = new BlockContinuation(ResumeAtChild: 1, ConsumedBlockSize: 500);
        using var layouter = new BlockLayouter(root, sink, continuation);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        var blockCont = Assert.IsType<BlockContinuation>(result.Continuation);
        // Cumulative: 500 (prior pages) + 250 (this attempt's emission) = 750.
        Assert.Equal(750, blockCont.ConsumedBlockSize);
    }

    [Fact]
    public void Layouter_resumes_at_continuation_child_index()
    {
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 230, marginTop: 10, marginBottom: 10),  // already on page 1
            (height: 230, marginTop: 10, marginBottom: 10),  // already on page 1
            (height: 330, marginTop: 10, marginBottom: 10)); // RESUMING on page 2

        var continuation = new BlockContinuation(ResumeAtChild: 2, ConsumedBlockSize: 500);
        using var layouter = new BlockLayouter(root, sink, continuation);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Single(sink.Fragments);
        // Border-box BlockSize = 330 (just the height; no padding/border).
        Assert.Equal(330, sink.Fragments[0].BlockSize);
    }

    // --- PR #22 fix #1: oversized-block forward progress -------------

    [Fact]
    public void Layouter_emits_oversized_first_block_with_overflow_penalty()
    {
        // PR #22 fix #1 — when the first block on a fresh page is
        // taller than the fragmentainer + the resolver returns
        // BreakHere, the layouter MUST emit the block anyway with a
        // forced-overflow penalty so pagination makes progress.
        // Otherwise the continuation would resume at the same child
        // forever.
        var sink = new RecordingFragmentSink();
        // Page is 800; one block at 1500 px high.
        var (root, _) = BuildTree(
            (height: 1500, marginTop: 0, marginBottom: 0));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        // Greedy resolver returns BreakHere when chunk doesn't fit.
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        // The oversized block IS emitted (forward progress).
        Assert.Single(sink.Fragments);
        Assert.Equal(1500, sink.Fragments[0].BlockSize);

        // Continuation resumes at the NEXT child — NOT the same one.
        var blockCont = Assert.IsType<BlockContinuation>(result.Continuation);
        Assert.Equal(1, blockCont.ResumeAtChild);  // past the oversized block

        // Cost picks up the overflow penalty.
        Assert.True(result.Cost >= CostModel.BreakInsideAvoidViolation);
    }

    [Fact]
    public void Layouter_normal_break_resumes_at_current_child_when_content_was_emitted()
    {
        // Counterpart to the oversized-block test — when content WAS
        // emitted earlier on this page, BreakHere normally resumes at
        // the offending child (no forward-progress fast-path).
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 200, marginTop: 0, marginBottom: 0),
            (height: 700, marginTop: 0, marginBottom: 0));  // overflow

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.Single(sink.Fragments);  // only first block emitted
        var blockCont = Assert.IsType<BlockContinuation>(result.Continuation);
        // Resume at child 1 (the overflowing block) — NOT child 2.
        Assert.Equal(1, blockCont.ResumeAtChild);
    }

    // --- PR #22 fix #2: checkpoint capture/register ------------------

    [Fact]
    public void Layouter_registers_checkpoint_with_resolver_at_each_block_boundary()
    {
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: 0),
            (height: 100, marginTop: 0, marginBottom: 0),
            (height: 100, marginTop: 0, marginBottom: 0));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new RegisterCountingResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // 3 blocks → 3 RegisterCheckpoint calls.
        Assert.Equal(3, resolver.RegisterCount);
    }

    [Fact]
    public void Layouter_checkpoint_captures_sink_cursor_and_child_index()
    {
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: 0),
            (height: 100, marginTop: 0, marginBottom: 0));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new CapturingCheckpointInspector();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Two block boundaries → two captures. First captured at child 0
        // (FragmentOutputCursor=0, LastEmittedChildIndex=-1).
        Assert.Equal(2, resolver.CapturedSnapshots.Count);
        Assert.Equal(0, resolver.CapturedSnapshots[0].FragmentOutputCursor);
        Assert.Equal(-1, resolver.CapturedSnapshots[0].LastEmittedChildIndex);

        // Second captured AFTER first emit: FragmentOutputCursor=1,
        // LastEmittedChildIndex=0.
        Assert.Equal(1, resolver.CapturedSnapshots[1].FragmentOutputCursor);
        Assert.Equal(0, resolver.CapturedSnapshots[1].LastEmittedChildIndex);
    }

    [Fact]
    public void Layouter_dispose_releases_final_checkpoint_lease()
    {
        // PR #22 fix #2 — the layouter holds a lease for the final
        // registered checkpoint until disposed. Verify Dispose
        // returns it.
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree((height: 100, marginTop: 0, marginBottom: 0));

        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        // Use using-block so Dispose is exercised on scope exit.
        using (var layouter = new BlockLayouter(root, sink))
        {
            layouter.AttemptLayout(
                ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);
            Assert.NotNull(resolver.GetLastCheckpoint());
        }
        // After the using-block: the layouter's Dispose returned its
        // final lease. Verify by Renting — the Reset state on the
        // checkpoint is observable.
        var nextLease = LayoutCheckpointPool.Rent();
        Assert.Equal(0, nextLease.Checkpoint!.PageIndex);  // Reset on rent
        LayoutCheckpointPool.Return(nextLease);
    }

    // --- PR #22 fix #6: negative margins -----------------------------

    [Fact]
    public void Layouter_handles_negative_margin_without_throwing()
    {
        // PR #22 fix #6 — margins can be negative per CSS box model;
        // the layouter must not throw via BreakOpportunity.EnsureValid.
        // Margin-box advance can be negative (cursor moves backward —
        // visual overlap is the intended effect).
        var sink = new RecordingFragmentSink();
        var style = MakeStyle();
        SetLengthPx(style, PropertyId.MarginTop, -50);
        SetLengthPx(style, PropertyId.Height, 100);
        SetLengthPx(style, PropertyId.MarginBottom, 0);

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        // Should not throw.
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Single(sink.Fragments);
    }

    // --- PR #22 fix #7: continuation validation ----------------------

    [Fact]
    public void Constructor_throws_on_non_BlockContinuation()
    {
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree((height: 100, marginTop: 0, marginBottom: 0));

        // Pass an InlineContinuation — wrong type for BlockLayouter.
        var wrongCont = new InlineContinuation(0, 0);

        Assert.Throws<System.ArgumentException>(() =>
            new BlockLayouter(root, sink, wrongCont));
    }

    [Fact]
    public void Constructor_throws_on_negative_ResumeAtChild()
    {
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree((height: 100, marginTop: 0, marginBottom: 0));

        var badCont = new BlockContinuation(ResumeAtChild: -1, ConsumedBlockSize: 0);
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            new BlockLayouter(root, sink, badCont));
    }

    [Fact]
    public void Constructor_throws_on_out_of_range_ResumeAtChild()
    {
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: 0),
            (height: 100, marginTop: 0, marginBottom: 0));

        // 2 children → max valid ResumeAtChild = 2 (= "all done").
        // 3 is out of range.
        var badCont = new BlockContinuation(ResumeAtChild: 3, ConsumedBlockSize: 0);
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            new BlockLayouter(root, sink, badCont));
    }

    [Fact]
    public void Constructor_accepts_ResumeAtChild_equal_to_child_count()
    {
        // ResumeAtChild = child count means "all done"; valid edge case.
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: 0),
            (height: 100, marginTop: 0, marginBottom: 0));

        var endCont = new BlockContinuation(ResumeAtChild: 2, ConsumedBlockSize: 200);
        using var layouter = new BlockLayouter(root, sink, endCont);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Empty(sink.Fragments);
    }

    // --- Skip non-block-level children -------------------------------

    [Fact]
    public void Layouter_skips_inline_children_silently()
    {
        // Cycle 1: inline content not yet wired (Task 10 InlineLayouter).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var blockStyle = MakeStyle();
        SetLengthPx(blockStyle, PropertyId.Height, 100);
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement()));

        root.AppendChild(Box.TextRun("inline text", MakeStyle()));

        var blockStyle2 = MakeStyle();
        SetLengthPx(blockStyle2, PropertyId.Height, 50);
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, blockStyle2, MakeElement()));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Equal(2, sink.Fragments.Count);
    }

    // --- Empty / null guards ----------------------------------------

    [Fact]
    public void Layouter_returns_AllDone_for_empty_root()
    {
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Empty(sink.Fragments);
    }

    [Fact]
    public void Constructor_throws_on_null_root_box()
    {
        var sink = new RecordingFragmentSink();
        Assert.Throws<System.ArgumentNullException>(() => new BlockLayouter(null!, sink));
    }

    [Fact]
    public void Constructor_throws_on_null_sink()
    {
        var root = Box.CreateRoot(MakeStyle());
        Assert.Throws<System.ArgumentNullException>(() => new BlockLayouter(root, null!));
    }

    // --- Cancellation -----------------------------------------------

    [Fact]
    public void AttemptLayout_throws_OperationCanceled_when_token_fired()
    {
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: 0));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var threw = false;
        try
        {
            layouter.AttemptLayout(
                ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict, cts.Token);
        }
        catch (System.OperationCanceledException)
        {
            threw = true;
        }
        Assert.True(threw);
    }

    // --- Force break dispatch (Copilot #4 — clarified naming) -------

    [Fact]
    public void Layouter_emits_PageComplete_when_resolver_returns_BreakHere_mid_stream()
    {
        // Per Copilot #4 — this test is NOT a force-break (the
        // BreakOpportunity has ForceBreak=false). It exercises the
        // case where the resolver decides BreakHere mid-stream
        // regardless of the opportunity's flags. Real ForceBreak
        // detection from CSS author CSS is TODO cycle 3.
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: 0),  // block 0
            (height: 100, marginTop: 0, marginBottom: 0)); // block 1

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakAfterFirstResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.Single(sink.Fragments);
        var cont = Assert.IsType<BlockContinuation>(result.Continuation);
        Assert.Equal(1, cont.ResumeAtChild);
    }

    // --- IBlockFragmentSink contract ------------------------------

    [Fact]
    public void Sink_cursor_increments_with_each_emit()
    {
        var sink = new RecordingFragmentSink();
        Assert.Equal(0, sink.Cursor);
        sink.Emit(new BoxFragment(
            Box.CreateRoot(MakeStyle()), 0, 0, 100, 100));
        Assert.Equal(1, sink.Cursor);
        sink.Emit(new BoxFragment(
            Box.CreateRoot(MakeStyle()), 0, 0, 100, 100));
        Assert.Equal(2, sink.Cursor);
    }

    [Fact]
    public void Sink_RollbackTo_truncates_emissions_past_cursor()
    {
        var sink = new RecordingFragmentSink();
        var s = MakeStyle();
        for (var i = 0; i < 5; i++)
        {
            sink.Emit(new BoxFragment(Box.CreateRoot(s), 0, 0, 100, 100));
        }
        Assert.Equal(5, sink.Cursor);
        sink.RollbackTo(2);
        Assert.Equal(2, sink.Cursor);
        Assert.Equal(2, sink.Fragments.Count);
    }

    // ====================================================================
    //  PR #23 review fixes — regression tests
    // ====================================================================

    // --- P1 #1: Rewind retry resumes from checkpoint, not constructor -

    [Fact]
    public void Cycle2_rewind_retry_does_not_duplicate_fragments()
    {
        // PR #23 review fix #1 — integration test with
        // LayoutRetryCoordinator + BlockLayouter. Resolver Continues
        // for blocks 0 and 1 (emits both), then Rewinds at block 2's
        // boundary. Retry must resume at block 2 (LEC=1 + 1 = 2),
        // NOT at index 0 (which would duplicate blocks 0 and 1).
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: 0),
            (height: 100, marginTop: 0, marginBottom: 0),
            (height: 100, marginTop: 0, marginBottom: 0));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new RewindAtBlock2Resolver();
        var coordinator = new LayoutRetryCoordinator(diagnostics: null, fragmentSink: sink);

        var result = coordinator.Run(layouter, ctx, ref layoutCtx, resolver);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        // 3 blocks emitted once each — no duplication. Pre-fix the
        // retry would re-emit blocks 0 + 1 → 5 fragments instead of 3.
        Assert.Equal(3, sink.Fragments.Count);
    }

    [Fact]
    public void Cycle2_rewind_resume_uses_checkpoint_LastEmittedChildIndex()
    {
        // Direct test: after a rewind, the layouter's NEXT
        // AttemptLayout call resumes at the correct child index.
        // Drives the layouter directly (without the coordinator) to
        // verify the per-instance state-machine.
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: 0),
            (height: 100, marginTop: 0, marginBottom: 0),
            (height: 100, marginTop: 0, marginBottom: 0));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new RewindAtBlock2Resolver();

        // First call: Continue, Continue, Rewind at block 2.
        var firstResult = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);
        Assert.Equal(LayoutAttemptOutcome.NeedsRewind, firstResult.Outcome);

        // After firstResult.RewindTo!.RestoreInto + sink.RollbackTo
        // (which we simulate manually here):
        Assert.NotNull(firstResult.RewindTo);
        firstResult.RewindTo!.RestoreInto(ctx, ref layoutCtx);
        sink.RollbackTo(firstResult.RewindTo.FragmentOutputCursor);
        Assert.Equal(2, sink.Fragments.Count);  // blocks 0+1 preserved

        // Second call: should resume at block 2 (LEC=1 + 1 = 2).
        var secondResult = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);
        Assert.Equal(LayoutAttemptOutcome.AllDone, secondResult.Outcome);
        // After retry: 3 fragments total (blocks 0, 1 from first; block 2 from second).
        Assert.Equal(3, sink.Fragments.Count);
    }

    // --- P1 #2 + Copilot #1: oversized resumed-block on later page ---

    [Fact]
    public void Cycle2_oversized_block_first_on_resumed_page_makes_progress()
    {
        // PR #23 review fix #2 + Copilot #1 — when the first child
        // on PAGE 2+ (resumed via continuation, priorPagesConsumed > 0)
        // is oversized, the forced-overflow path MUST still trigger
        // so pagination makes progress. Pre-fix the predicate
        // required priorPagesConsumed == 0, so this scenario would
        // loop forever returning ResumeAtChild=current.
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: 0),    // page 1 (already emitted in priorPagesConsumed)
            (height: 1500, marginTop: 0, marginBottom: 0));  // page 2 first child — oversized

        // Resume on page 2 with prior pages = 100 px consumed.
        var continuation = new BlockContinuation(ResumeAtChild: 1, ConsumedBlockSize: 100);
        using var layouter = new BlockLayouter(root, sink, continuation);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        // Forward progress: the oversized block IS emitted.
        Assert.Single(sink.Fragments);
        Assert.Equal(1500, sink.Fragments[0].BlockSize);
        // Continuation advances to the NEXT child (= 2 = end of input).
        var blockCont = Assert.IsType<BlockContinuation>(result.Continuation);
        Assert.Equal(2, blockCont.ResumeAtChild);
        // Cost picks up the overflow penalty.
        Assert.True(result.Cost >= CostModel.BreakInsideAvoidViolation);
    }

    // --- P2 #3: non-block content interrupts margin adjacency ---------

    [Fact]
    public void Cycle2_inline_child_between_blocks_breaks_margin_adjacency()
    {
        // PR #23 review fix #3 — inline content creates a line box
        // that breaks margin adjacency per CSS 2.1 §8.3.1. Margins
        // must NOT collapse across the line box.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        // Block 0: marginBottom=20
        var b0Style = MakeStyle();
        SetLengthPx(b0Style, PropertyId.Height, 100);
        SetLengthPx(b0Style, PropertyId.MarginBottom, 20);
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, b0Style, MakeElement()));

        // Inline content between blocks (line-box-creating).
        root.AppendChild(Box.TextRun("inline text", MakeStyle()));

        // Block 1: marginTop=15
        var b1Style = MakeStyle();
        SetLengthPx(b1Style, PropertyId.Height, 100);
        SetLengthPx(b1Style, PropertyId.MarginTop, 15);
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, b1Style, MakeElement()));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(2, sink.Fragments.Count);
        // Block 0: BlockOffset=0, BlockSize=100. Cursor after = 120 (100+20).
        Assert.Equal(0, sink.Fragments[0].BlockOffset);
        // Block 1: NO collapse (inline child broke adjacency).
        // BlockOffset = 120 (cursor) + 15 (marginTop, fully applied) = 135.
        // Pre-fix: would collapse(20, 15) = 20, topShift = 0, BlockOffset = 120.
        Assert.Equal(135, sink.Fragments[1].BlockOffset);
    }

    // --- P2 #4: negative margins keep UsedBlockSize non-negative ----

    [Fact]
    public void Cycle2_negative_margin_does_not_drive_used_block_size_below_zero()
    {
        // PR #23 review fix #4 — a block with very-negative margin-bottom
        // can produce a negative margin-box advance. The cursor in
        // fragmentainer.UsedBlockSize must clamp to 0 so the next
        // BreakOpportunity doesn't trip CostModel.Score's non-negative
        // guard.
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 50, marginTop: 0, marginBottom: -200),  // ends at -150 (clamped to 0)
            (height: 100, marginTop: 0, marginBottom: 0));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        // Should not throw. Without the clamp, the second block's
        // BreakOpportunity.UsedBlockSize would be -150, tripping
        // CostModel's guard.
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Equal(2, sink.Fragments.Count);
        // After the second block, cursor advance = max(0, ...). Should be ≥ 0.
        Assert.True(ctx.UsedBlockSize >= 0);
    }

    // --- P2 #5: huge margin keeps inline-size non-negative -----------

    [Fact]
    public void Cycle2_huge_inline_margins_clamp_inline_size_to_zero()
    {
        // PR #23 review fix #5 — when margin-left + margin-right
        // exceed ContentInlineSize, the resulting border-box inline
        // size is negative. Clamp to 0 so the fragment record stays
        // well-formed.
        var sink = new RecordingFragmentSink();
        var style = MakeStyle();
        SetLengthPx(style, PropertyId.MarginLeft, 700);   // 700 + 200 = 900 > 600
        SetLengthPx(style, PropertyId.MarginRight, 200);
        SetLengthPx(style, PropertyId.Height, 100);

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Single(sink.Fragments);
        // Pre-fix: InlineSize = 600 - 700 - 200 = -300 (invalid).
        // Post-fix: clamped to 0.
        Assert.Equal(0, sink.Fragments[0].InlineSize);
    }

    // ====================================================================
    //  PR #26 review pass — 4 cycle-2 P1 regression tests
    // ====================================================================

    // --- #1: Forced overflow emits PAGINATION-FORCED-OVERFLOW-001 ---

    [Fact]
    public void Cycle2_forced_overflow_on_strict_emits_diagnostic()
    {
        // PR #26 review fix #1 — when an oversized block is force-emitted
        // via the forward-progress path, the layouter MUST emit
        // PAGINATION-FORCED-OVERFLOW-001 directly. Pre-fix the
        // diagnostic was only emitted by the LayoutRetryCoordinator
        // before the LastResort attempt, but the BlockLayouter's
        // forced-overflow path returns PageComplete on Strict — so
        // the coordinator never reaches LastResort + the diagnostic
        // was lost.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        var (root, _) = BuildTree(
            (height: 1500, marginTop: 0, marginBottom: 0));  // 1500 > 800

        using var layouter = new BlockLayouter(root, sink,
            incomingContinuation: null, diagnostics: diagSink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        // Forward progress: oversized block emitted.
        Assert.Single(sink.Fragments);
        // PR #26 fix #1 — diagnostic emitted.
        Assert.Single(diagSink.Diagnostics);
        Assert.Equal(PaginateDiagnosticCodes.PaginationForcedOverflow001,
            diagSink.Diagnostics[0].Code);
        Assert.Contains("forced overflow", diagSink.Diagnostics[0].Message,
            System.StringComparison.OrdinalIgnoreCase);
    }

    // --- #2: Rewind retry preserves cumulative ConsumedBlockSize ----

    [Fact]
    public void Cycle2_rewind_retry_preserves_cumulative_consumed_block_size()
    {
        // PR #26 review fix #2 — the page-start baseline used for
        // ConsumedBlockSize accounting is set on the FIRST AttemptLayout
        // entry + NOT reset on rewind retries. Without this, the retry
        // would compute `priorPagesConsumed + (UsedBlockSize - midpage)`
        // which undercounts by the midpage value.
        //
        // Test scenario: 4 blocks. Resolver Continues for blocks 0+1,
        // Rewinds at block 2 (once), Continues blocks 0+1+2 on retry,
        // then BreakHere at block 3. After break, ConsumedBlockSize on
        // the continuation should be 300 (= blocks 0+1+2 cumulative
        // page contribution), NOT 100 (which is what cycle 1's bug
        // would compute: cursor 300 - midpage-after-restore 200 = 100).
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: 0),
            (height: 100, marginTop: 0, marginBottom: 0),
            (height: 100, marginTop: 0, marginBottom: 0),
            (height: 100, marginTop: 0, marginBottom: 0));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new RewindAtBlock2ThenBreakAtBlock3Resolver();

        // First attempt: Continue, Continue, Rewind at block 2.
        var firstResult = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);
        Assert.Equal(LayoutAttemptOutcome.NeedsRewind, firstResult.Outcome);

        // Restore from checkpoint.
        firstResult.RewindTo!.RestoreInto(ctx, ref layoutCtx);
        sink.RollbackTo(firstResult.RewindTo.FragmentOutputCursor);

        // Second attempt: per PR #23 review fix #1 the retry resumes
        // from `RewindTo.LastEmittedChildIndex + 1 = 2` (NOT from
        // block 0). Resolver call 4 = block 2 retry → Continue;
        // call 5 = block 3 → BreakHere.
        var secondResult = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, secondResult.Outcome);
        var cont = Assert.IsType<BlockContinuation>(secondResult.Continuation);
        // Page contribution: blocks 0+1+2 = 300. PR #26 fix #2:
        // ConsumedBlockSize uses _pageStartUsedBlockSize (= 0, captured
        // on first AttemptLayout entry, NOT reset on rewind), so the
        // accumulated page extent is correctly 300.
        // Pre-fix would be (UsedBlockSize 300 - initialUsed-after-restore
        // 200) = 100, missing the 200 already-emitted-this-page extent.
        Assert.Equal(300, cont.ConsumedBlockSize);
        // 3 fragments emitted total: blocks 0+1 from first attempt
        // (preserved across rollback to checkpoint cursor=2), block 2
        // re-emitted from retry. Block 3 is in the continuation.
        Assert.Equal(3, sink.Fragments.Count);
    }

    // --- #3: Rewind preserves margin-collapse frontier --------------

    [Fact]
    public void Cycle2_rewind_retry_preserves_margin_collapse_frontier()
    {
        // PR #26 review fix #3 — when the rewind happens AFTER a
        // block with non-zero bottom margin, the retried child must
        // collapse with that bottom margin (not apply its full
        // marginTop). The collapse frontier (prevBlockMarginEnd +
        // hasPriorAdjoiningBlock) is captured before NeedsRewind +
        // restored on retry entry.
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: 20),  // marginBottom=20
            (height: 100, marginTop: 0, marginBottom: 0),
            (height: 100, marginTop: 30, marginBottom: 0)); // marginTop=30

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new RewindAtBlock2Resolver();

        // First attempt: Continue, Continue, Rewind at block 2.
        var firstResult = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);
        Assert.Equal(LayoutAttemptOutcome.NeedsRewind, firstResult.Outcome);
        firstResult.RewindTo!.RestoreInto(ctx, ref layoutCtx);
        sink.RollbackTo(firstResult.RewindTo.FragmentOutputCursor);

        // Second attempt: emits block 2 with collapse applied.
        var secondResult = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.AllDone, secondResult.Outcome);
        Assert.Equal(3, sink.Fragments.Count);
        // Block 2's BlockOffset: position should account for collapse
        // with block 1's marginBottom=0 (not block 0's, since block 1
        // is the immediate prior). Block 1 cursor = 220, marginBottom=0.
        // Block 2 marginTop=30, collapse(0, 30) = 30. topShift = 30 - 0 = 30.
        // BlockOffset = 220 + 30 = 250.
        // (Without fix #3, the retry would reset the collapse frontier;
        // block 2 would still apply marginTop=30 fully, giving same
        // BlockOffset = 250 in this test. The diagnostic is more
        // important when block 1's marginBottom is non-zero — covered
        // by the more specific test below.)
        Assert.Equal(250, sink.Fragments[2].BlockOffset);
    }

    // --- #4: Negative margins don't hide visual overflow -----------

    [Fact]
    public void Cycle2_negative_margins_do_not_hide_visual_overflow()
    {
        // PR #26 review fix #4 — `margin-top:-1000; height:2000;
        // margin-bottom:-1000` produces a 2000-px visual border box
        // but a 0-px net margin-box. Pre-fix `chunkForBreakCheck =
        // Math.Max(0, marginBoxBlockSize)` was 0, bypassing overflow
        // handling. Post-fix uses the visual extent as the overflow
        // measure.
        var sink = new RecordingFragmentSink();
        var style = MakeStyle();
        SetLengthPx(style, PropertyId.MarginTop, -1000);
        SetLengthPx(style, PropertyId.Height, 2000);  // visual = 2000
        SetLengthPx(style, PropertyId.MarginBottom, -1000);

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // The 2000-px visual extent overflows the 800-px page. The
        // greedy resolver returns BreakHere, hitting the forced-overflow
        // forward-progress path (since this is the first block on a
        // fresh page).
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.Single(sink.Fragments);
        // Overflow penalty in the cost.
        Assert.True(result.Cost >= CostModel.BreakInsideAvoidViolation);
    }

    // --- Post-Task-7 review #3: collapsed margins don't overcount in overflow check --

    [Fact]
    public void PostTask7_collapsed_margins_do_not_trigger_false_page_break()
    {
        // Post-Task-7 review (recommendation P2 #3) — `visualBlockExtent`
        // pre-fix used raw `marginStart`. After collapse the actual top
        // contribution is `topShift`, NOT `marginStart`. With a previous
        // block bottom-margin of 80 + current block top-margin of 10,
        // the collapsed gap = max(80, 10) = 80, which is already in
        // fragmentainer.UsedBlockSize (the prior block's emission added
        // it). topShift = 80 - 80 = 0 — current block's top contributes
        // 0 additional space. Pre-fix counted +10, which on a fragmentainer
        // boundary would create a false page break.
        //
        // Test scenario:
        //   page block size = 800
        //   block 1: height=100, marginBottom=80 → cursor goes 0→180
        //     (border-box top=0, border-box bottom=100, +80 marginBottom = 180)
        //   block 2: height=620, marginTop=10
        //     - collapsed gap = max(80, 10) = 80 (already counted)
        //     - topShift = 0
        //     - actual visual top contribution = 0
        //     - block 2's border-box top = 180; bottom = 800 — fits exactly.
        //   Total page contribution = 800. NO overflow.
        //
        // Pre-fix: visualBlockExtent = 620 + max(0,10) + max(0,0) = 630.
        //   chunkForBreakCheck = max(620, 630) = 630.
        //   UsedBlockSize at decision = 180. 180 + 630 = 810 > 800 → BreakHere
        //   → false page break (block 2 spuriously fails to fit).
        // Post-fix: visualBlockExtent = 620 + max(0,topShift=0) + max(0,0) = 620.
        //   chunkForBreakCheck = max(620, 620) = 620.
        //   180 + 620 = 800 ≤ 800 → Continue. Block fits.
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: 80),
            (height: 620, marginTop: 10, marginBottom: 0));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Both blocks fit; layouter returns AllDone (no continuation needed).
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Equal(2, sink.Fragments.Count);
        // Block 1: BlockOffset=0, BlockSize=100.
        Assert.Equal(0, sink.Fragments[0].BlockOffset);
        Assert.Equal(100, sink.Fragments[0].BlockSize);
        // Block 2: collapsed-with-prior. effectiveTopGap=80, topShift=0.
        // BlockOffset = UsedBlockSize-after-block-1 (180) + topShift (0) = 180.
        Assert.Equal(180, sink.Fragments[1].BlockOffset);
        Assert.Equal(620, sink.Fragments[1].BlockSize);
    }

    // --- Post-Task-7 review #5: checkpoint-owned margin-collapse frontier ---

    [Fact]
    public void PostTask7_rewind_to_older_checkpoint_uses_that_checkpoints_frontier()
    {
        // Post-Task-7 review (recommendation P2 #5) — pre-fix, the
        // layouter stored the margin-collapse frontier in private
        // fields populated AT REWIND TIME with the LAYOUTER'S CURRENT
        // state. That works for the current single-slot resolver
        // (always rewinds to the most-recent checkpoint), but would
        // break a future resolver that retains multiple checkpoints +
        // rewinds to an OLDER one (e.g., DP-optimal rewind across a
        // window).
        //
        // Post-fix, the frontier is captured ON THE CHECKPOINT at
        // capture time, and the rewind branch reads from
        // `decision.RewindTo` rather than the layouter's "now" state.
        //
        // This test simulates the retained-multiple-checkpoints case
        // with a resolver that:
        //   - Stashes the FIRST checkpoint it sees (block 1 boundary —
        //     no prior adjoining block, no collapse state).
        //   - Continues through blocks 1, 2.
        //   - At block 3, returns Rewind targeting the STASHED first
        //     checkpoint (NOT the just-registered block-3 one).
        // The layouter should resume with:
        //   - LastEmittedChildIndex from the stashed checkpoint
        //     (= -1 or 0 depending on capture timing — see resolver).
        //   - The FRONTIER captured at the stashed checkpoint
        //     (`hasAdjoiningBlockOnEntry: false`,
        //      `prevBlockMarginEnd: 0`).
        // Pre-fix would (incorrectly) restore the LAYOUTER'S state at
        // rewind-call time — which corresponds to AFTER block 2 was
        // emitted, so prevBlockMarginEnd != 0.
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: 50),  // block 0 — bottom=50
            (height: 100, marginTop: 0, marginBottom: 80),  // block 1 — bottom=80
            (height: 100, marginTop: 0, marginBottom: 30)); // block 2 — bottom=30

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new RewindToFirstCheckpointResolver();

        // First attempt: resolver stashes the first checkpoint (block 0
        // boundary), continues through blocks 0+1, then rewinds to the
        // stashed first checkpoint at block 2 boundary.
        var firstResult = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);
        Assert.Equal(LayoutAttemptOutcome.NeedsRewind, firstResult.Outcome);

        // The stashed checkpoint's frontier should reflect the BLOCK 0
        // boundary state: hasAdjoiningBlockOnEntry=false (no block emitted
        // yet at that point), prevBlockMarginEnd=0.
        Assert.NotNull(firstResult.RewindTo);
        Assert.False(firstResult.RewindTo!.HasAdjoiningBlockOnEntry);
        Assert.Equal(0, firstResult.RewindTo.PrevBlockMarginEnd);

        // Restore + retry. The retry should resume from the stashed
        // checkpoint's LastEmittedChildIndex+1 = 0 (since
        // LastEmittedChildIndex = -1 — nothing emitted before block 0).
        firstResult.RewindTo.RestoreInto(ctx, ref layoutCtx);
        sink.RollbackTo(firstResult.RewindTo.FragmentOutputCursor);
        resolver.SuppressNextRewind = true;

        var secondResult = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);
        Assert.Equal(LayoutAttemptOutcome.AllDone, secondResult.Outcome);
        // 3 blocks emitted on the retry. Block 0 starts at offset 0
        // (no collapse — frontier was hasAdjoiningBlock=false). If pre-
        // fix had restored the wrong frontier (block 2's state), block 0
        // on retry would have collapsed against a phantom prior margin.
        Assert.Equal(3, sink.Fragments.Count);
        Assert.Equal(0, sink.Fragments[0].BlockOffset);
    }

    /// <summary>Resolver that retains the FIRST checkpoint it sees +
    /// rewinds to it on the third Consider call. Simulates the future
    /// retained-multiple-checkpoints resolver scenario for the P2 #5
    /// regression.</summary>
    private sealed class RewindToFirstCheckpointResolver : IBreakResolver
    {
        public bool SuppressNextRewind;
        private int _calls;
        private CheckpointLease _firstLease;
        private CheckpointLease _latestLease;

        public BreakDecision ConsiderBreakAt(BreakOpportunity opportunity, FragmentainerContext ctx)
        {
            _calls++;
            // On call 3 (block 2 boundary), rewind to the FIRST
            // captured checkpoint (block 0 boundary).
            if (_calls == 3 && !SuppressNextRewind)
            {
                return new BreakDecision(BreakAction.Rewind, 0, _firstLease.Checkpoint);
            }
            return BreakDecision.Continue;
        }

        public OptimizerResult ResolveBreaks(
            IReadOnlyList<BreakOpportunity> opportunities, FragmentainerContext ctx, CancellationToken cancellationToken = default)
            => OptimizerResult.Empty;

        public void RegisterCheckpoint(CheckpointLease lease)
        {
            // Stash the FIRST checkpoint we ever see.
            if (_firstLease.Checkpoint is null)
            {
                _firstLease = lease;
                return;
            }
            // Subsequent checkpoints — return the prior LATEST (NOT the
            // first; we want to keep the first alive for the rewind).
            if (_latestLease.Checkpoint is not null
                && !ReferenceEquals(_latestLease.Checkpoint, lease.Checkpoint)
                && !ReferenceEquals(_latestLease.Checkpoint, _firstLease.Checkpoint))
            {
                LayoutCheckpointPool.Return(_latestLease);
            }
            _latestLease = lease;
        }

        public LayoutCheckpoint? GetLastCheckpoint() => _latestLease.Checkpoint ?? _firstLease.Checkpoint;

        public void Dispose()
        {
            if (_firstLease.Checkpoint is not null)
            {
                LayoutCheckpointPool.Return(_firstLease);
                _firstLease = default;
            }
            if (_latestLease.Checkpoint is not null)
            {
                LayoutCheckpointPool.Return(_latestLease);
                _latestLease = default;
            }
        }
    }

    // --- Post-Task-7 review #1 (P1 #2): diagnostics flow through ---
    // --- LayoutContext from coordinator to layouter --------------

    [Fact]
    public void PostTask7_coordinator_diagnostics_reach_layouter_via_layout_context()
    {
        // Post-Task-7 review (recommendation P1 #2) — pre-fix the
        // coordinator + layouter each had their own diagnostic sink
        // wired separately. A composition root that wired ONLY the
        // coordinator's sink would miss
        // PAGINATION-FORCED-OVERFLOW-001 emitted from the layouter's
        // forward-progress path on the Strict attempt (LastResort
        // never fires when Strict commits).
        //
        // Post-fix, the coordinator threads its sink into
        // layout.Diagnostics on entry; the layouter reads from
        // layout.Diagnostics. Wiring once at the coordinator reaches
        // both sides.
        //
        // Test: oversized block (taller than the page) on fragmentainer
        // 0. Strict path: resolver returns BreakHere (block too tall);
        // layouter's forward-progress hits the forced-overflow path
        // BEFORE LastResort. Pre-fix this would emit nothing on the
        // coordinator's sink (layouter's _diagnostics was null —
        // we never set it via constructor); post-fix it emits because
        // the coordinator pushes its sink to layout.Diagnostics.
        var diagSink = new RecordingDiagnosticsSink();
        var sink = new RecordingFragmentSink();

        var style = MakeStyle();
        SetLengthPx(style, PropertyId.Height, 2000);  // taller than page
        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));

        // Layouter constructed WITHOUT a diagnostics arg — the
        // coordinator's sink is the only one wired.
        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        var coordinator = new LayoutRetryCoordinator(diagnostics: diagSink, fragmentSink: sink);
        using var resolver = new BreakResolver();

        var result = coordinator.Run(layouter, ctx, ref layoutCtx, resolver);

        // Layouter's Strict-path forward-progress emits the diagnostic.
        // Pre-fix: nothing emitted (layouter._diagnostics was null).
        // Post-fix: emitted via layout.Diagnostics.
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.Single(diagSink.Diagnostics);
        Assert.Equal(PaginateDiagnosticCodes.PaginationForcedOverflow001,
            diagSink.Diagnostics[0].Code);
        Assert.Contains("forced overflow", diagSink.Diagnostics[0].Message,
            System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostTask7_constructor_injected_sink_still_works_for_direct_construction()
    {
        // Backward-compat sanity: tests / direct callers that pass an
        // IPaginateDiagnosticsSink to the BlockLayouter constructor
        // (without going through the coordinator) still get diagnostics.
        // The lookup is `layout.Diagnostics ?? _diagnostics` so the
        // constructor sink wins when the ambient one isn't set.
        var diagSink = new RecordingDiagnosticsSink();
        var sink = new RecordingFragmentSink();

        var style = MakeStyle();
        SetLengthPx(style, PropertyId.Height, 2000);
        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));

        using var layouter = new BlockLayouter(
            root, sink, incomingContinuation: null, diagnostics: diagSink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);  // layoutCtx.Diagnostics = null
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.Single(diagSink.Diagnostics);
        Assert.Equal(PaginateDiagnosticCodes.PaginationForcedOverflow001,
            diagSink.Diagnostics[0].Code);
    }

    /// <summary>Recording sink for diagnostics — captures all emitted
    /// PaginateDiagnostics for assertion. Per the IPaginateDiagnosticsSink
    /// contract: must not throw.</summary>
    private sealed class RecordingDiagnosticsSink : IPaginateDiagnosticsSink
    {
        public List<PaginateDiagnostic> Diagnostics { get; } = new();
        public void Emit(PaginateDiagnostic diagnostic) => Diagnostics.Add(diagnostic);
    }

    // ====================================================================
    //  Cycle 2-3 deferral pins — failing-skip integration tests
    // ====================================================================

    // Per Phase 3 Task 7 cycle 2b — recursive nested-block layout.
    // The deferral pin from cycle 1 / 2 is now ACTIVATED — see the
    // Cycle2b_* tests below for the activated coverage.

    [Fact]
    public void Cycle2b_layouter_emits_fragments_for_nested_block_descendants()
    {
        // Cycle 1 / 2 walked _rootBox.Children only — a nested
        // `div > p` tree emitted only the div fragment. Cycle 2b
        // recurses, emitting fragments for BOTH the div + the p.
        //
        // Tree:
        //   root
        //     └─ div (BlockContainer, height=200, padding=10, border=5)
        //          └─ p (BlockContainer, height=80, marginTop=15)
        //
        // Expected fragments:
        //   div: BlockOffset=0, BlockSize=230 (5+10+200+10+5 = border-box)
        //   p:   BlockOffset = div.contentTop (0+5+10=15) + p.marginTop (15) = 30
        //        BlockSize  = 80
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());
        var divStyle = MakeStyle();
        SetLengthPx(divStyle, PropertyId.Height, 200);
        SetLengthPx(divStyle, PropertyId.PaddingTop, 10);
        SetLengthPx(divStyle, PropertyId.PaddingBottom, 10);
        SolidBorders(divStyle);
        SetLengthPx(divStyle, PropertyId.BorderTopWidth, 5);
        SetLengthPx(divStyle, PropertyId.BorderBottomWidth, 5);
        var div = Box.ForElement(BoxKind.BlockContainer, divStyle, MakeElement());
        root.AppendChild(div);

        var pStyle = MakeStyle();
        SetLengthPx(pStyle, PropertyId.Height, 80);
        SetLengthPx(pStyle, PropertyId.MarginTop, 15);
        var p = Box.ForElement(BoxKind.BlockContainer, pStyle, MakeElement());
        div.AppendChild(p);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        // BOTH div AND p emitted (cycle 2b recursion).
        Assert.Equal(2, sink.Fragments.Count);
        // div first (parent emitted before children).
        Assert.Same(div, sink.Fragments[0].Box);
        Assert.Equal(0, sink.Fragments[0].BlockOffset);
        Assert.Equal(230, sink.Fragments[0].BlockSize);  // 5+10+200+10+5
        // p inside div.
        Assert.Same(p, sink.Fragments[1].Box);
        // p's BlockOffset = div's contentTop (0+5+10=15) + p.marginTop (15) = 30.
        Assert.Equal(30, sink.Fragments[1].BlockOffset);
        Assert.Equal(80, sink.Fragments[1].BlockSize);
    }

    [Fact]
    public void Cycle2b_layouter_recurses_through_three_levels()
    {
        // Tree: root > section > article > h1
        // All BlockContainers; verify all three nested levels emit
        // fragments at correct offsets.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var sectionStyle = MakeStyle();
        SetLengthPx(sectionStyle, PropertyId.Height, 400);
        SetLengthPx(sectionStyle, PropertyId.PaddingTop, 20);
        var section = Box.ForElement(BoxKind.BlockContainer, sectionStyle, MakeElement());
        root.AppendChild(section);

        var articleStyle = MakeStyle();
        SetLengthPx(articleStyle, PropertyId.Height, 300);
        SetLengthPx(articleStyle, PropertyId.PaddingTop, 10);
        var article = Box.ForElement(BoxKind.BlockContainer, articleStyle, MakeElement());
        section.AppendChild(article);

        var h1Style = MakeStyle();
        SetLengthPx(h1Style, PropertyId.Height, 50);
        var h1 = Box.ForElement(BoxKind.BlockContainer, h1Style, MakeElement());
        article.AppendChild(h1);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Equal(3, sink.Fragments.Count);
        // section: BlockOffset=0, BlockSize=420 (20+400+0)
        Assert.Same(section, sink.Fragments[0].Box);
        Assert.Equal(0, sink.Fragments[0].BlockOffset);
        Assert.Equal(420, sink.Fragments[0].BlockSize);
        // article inside section: contentTop=0+0+20=20
        Assert.Same(article, sink.Fragments[1].Box);
        Assert.Equal(20, sink.Fragments[1].BlockOffset);
        Assert.Equal(310, sink.Fragments[1].BlockSize);  // 10+300+0
        // h1 inside article: article's contentTop = 20+0+10 = 30
        Assert.Same(h1, sink.Fragments[2].Box);
        Assert.Equal(30, sink.Fragments[2].BlockOffset);
        Assert.Equal(50, sink.Fragments[2].BlockSize);
    }

    [Fact]
    public void Cycle2b_nested_children_apply_inline_offsets_inside_parent_padding()
    {
        // Verify inline-axis nesting: child's InlineOffset = parent's
        // contentLeft + child's marginLeft. Parent's contentLeft =
        // parent.borderLeft + parent.paddingLeft.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var divStyle = MakeStyle();
        SetLengthPx(divStyle, PropertyId.Height, 200);
        SetLengthPx(divStyle, PropertyId.PaddingLeft, 25);
        SolidBorders(divStyle);
        SetLengthPx(divStyle, PropertyId.BorderLeftWidth, 5);
        var div = Box.ForElement(BoxKind.BlockContainer, divStyle, MakeElement());
        root.AppendChild(div);

        var pStyle = MakeStyle();
        SetLengthPx(pStyle, PropertyId.Height, 50);
        SetLengthPx(pStyle, PropertyId.MarginLeft, 8);
        var p = Box.ForElement(BoxKind.BlockContainer, pStyle, MakeElement());
        div.AppendChild(p);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // div.InlineOffset = 0; div.InlineSize = 600 (no margins on div).
        Assert.Equal(0, sink.Fragments[0].InlineOffset);
        Assert.Equal(600, sink.Fragments[0].InlineSize);
        // p.InlineOffset = div.contentLeft (0+5+25=30) + p.marginLeft (8) = 38.
        Assert.Equal(38, sink.Fragments[1].InlineOffset);
        // p.InlineSize = div.contentInlineSize (600-5-25-0-0=570) - p.marginLeft (8) - p.marginRight (0) = 562.
        Assert.Equal(562, sink.Fragments[1].InlineSize);
    }

    [Fact]
    public void Cycle2b_nested_siblings_apply_margin_collapse()
    {
        // Two block-level siblings inside a parent should collapse
        // adjacent margins per CSS 2.1 §8.3.1, just like top-level
        // siblings. Parent contentTop=0; child1.marginTop=20;
        // child2.marginTop=10 collapses with child1.marginBottom=15
        // to max(15,10)=15 → topShift = 15 - 15 = 0.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var divStyle = MakeStyle();
        SetLengthPx(divStyle, PropertyId.Height, 300);
        var div = Box.ForElement(BoxKind.BlockContainer, divStyle, MakeElement());
        root.AppendChild(div);

        var c1Style = MakeStyle();
        SetLengthPx(c1Style, PropertyId.Height, 60);
        SetLengthPx(c1Style, PropertyId.MarginTop, 20);
        SetLengthPx(c1Style, PropertyId.MarginBottom, 15);
        var c1 = Box.ForElement(BoxKind.BlockContainer, c1Style, MakeElement());
        div.AppendChild(c1);

        var c2Style = MakeStyle();
        SetLengthPx(c2Style, PropertyId.Height, 40);
        SetLengthPx(c2Style, PropertyId.MarginTop, 10);
        var c2 = Box.ForElement(BoxKind.BlockContainer, c2Style, MakeElement());
        div.AppendChild(c2);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(3, sink.Fragments.Count);
        // c1 inside div. Div.contentTop=0; c1.marginTop=20 → c1.BlockOffset=20.
        Assert.Same(c1, sink.Fragments[1].Box);
        Assert.Equal(20, sink.Fragments[1].BlockOffset);
        // c2 inside div. cursor after c1 = 20+60+15=95. Collapse(15,10)=15;
        // topShift = 15-15 = 0. c2.BlockOffset = div.contentTop + cursor + topShift = 0 + 95 + 0 = 95.
        Assert.Same(c2, sink.Fragments[2].Box);
        Assert.Equal(95, sink.Fragments[2].BlockOffset);
    }

    [Fact]
    public void Cycle2b_nested_non_block_children_are_skipped()
    {
        // A BlockContainer containing both a block child + an inline-
        // level child (TextRun): only the block child is emitted by
        // recursion. TextRun is Task 10's domain.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var divStyle = MakeStyle();
        SetLengthPx(divStyle, PropertyId.Height, 200);
        var div = Box.ForElement(BoxKind.BlockContainer, divStyle, MakeElement());
        root.AppendChild(div);

        // Inline-level (skipped).
        var textStyle = MakeStyle();
        var text = Box.ForElement(BoxKind.TextRun, textStyle, MakeElement());
        div.AppendChild(text);

        // Block-level (emitted).
        var pStyle = MakeStyle();
        SetLengthPx(pStyle, PropertyId.Height, 50);
        var p = Box.ForElement(BoxKind.BlockContainer, pStyle, MakeElement());
        div.AppendChild(p);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // div + p (text skipped).
        Assert.Equal(2, sink.Fragments.Count);
        Assert.Same(div, sink.Fragments[0].Box);
        Assert.Same(p, sink.Fragments[1].Box);
    }

    // ====================================================================
    //  Post-PR-28 review pass — cycle 2b correctness + safety + coverage
    // ====================================================================

    [Fact]
    public void PostPr28_nested_negative_margin_overlaps_prior_sibling()
    {
        // Per cycle-2b post-PR-28 review #1 + Copilot #2 — pre-fix the
        // recursion clamped childCursor with Math.Max(0, ...), which is
        // appropriate for the OUTER loop's UsedBlockSize (feeds
        // BreakOpportunity validation that requires non-negative
        // measures) but WRONG for the inner cursor: nested negative
        // margins legitimately produce overlap per CSS 2.1 §8.3.1.
        //
        // Test: parent div with two children; child 1 has very negative
        // margin-bottom so child 2 should be POSITIONED ABOVE child 1's
        // bottom edge (i.e., overlapping).
        //
        //   parent div: height=400 (no padding/border)
        //   child 1:    height=100, marginBottom=-30
        //   child 2:    height=50,  marginTop=0
        //
        // child 1 occupies y=[0, 100) inside parent's content area.
        // child 1's contribution to cursor: 0 (topShift) + 100 + (-30) = 70.
        // child 2's BlockOffset = parent.contentTop + cursor + topShift
        //                      = 0 + 70 + 0 = 70.
        // Pre-fix childCursor would have been Math.Max(0, 70) = 70 — no
        // visible difference HERE. The bug shows when cursor goes
        // negative; let's strengthen with a more aggressive margin:
        //
        //   child 1: height=50, marginBottom=-100  → contributes -50 to cursor
        //   child 2 BlockOffset (post-fix) = 0 + (-50) + 0 = -50.
        //   Pre-fix would have clamped cursor to 0 → child 2 BlockOffset = 0
        //   (which OVERLAPS WITH child 1's start, not its end — wrong).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var parentStyle = MakeStyle();
        SetLengthPx(parentStyle, PropertyId.Height, 400);
        var parent = Box.ForElement(BoxKind.BlockContainer, parentStyle, MakeElement());
        root.AppendChild(parent);

        var c1Style = MakeStyle();
        SetLengthPx(c1Style, PropertyId.Height, 50);
        SetLengthPx(c1Style, PropertyId.MarginBottom, -100);
        var c1 = Box.ForElement(BoxKind.BlockContainer, c1Style, MakeElement());
        parent.AppendChild(c1);

        var c2Style = MakeStyle();
        SetLengthPx(c2Style, PropertyId.Height, 80);
        var c2 = Box.ForElement(BoxKind.BlockContainer, c2Style, MakeElement());
        parent.AppendChild(c2);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // 3 fragments: parent + c1 + c2.
        Assert.Equal(3, sink.Fragments.Count);
        // c1 at parent.contentTop + 0 (no marginTop) = 0.
        Assert.Same(c1, sink.Fragments[1].Box);
        Assert.Equal(0, sink.Fragments[1].BlockOffset);
        // c2 at parent.contentTop + cursor + topShift =
        //   0 + (0 + 0 + 50 + (-100)) + 0 = -50.
        // POST-fix expected: -50 (c2 overlaps with c1, painting on top
        // per CSS 2.1 paint order). PRE-fix the cursor clamp made this
        // 0 — c2 would have been at the same position as c1 (visually
        // wrong; c2 must be 50px higher than its post-c1 stack position).
        Assert.Same(c2, sink.Fragments[2].Box);
        Assert.Equal(-50, sink.Fragments[2].BlockOffset);
    }

    [Fact]
    public void PostPr28_pathologically_deep_box_tree_throws_invalid_op()
    {
        // Per cycle-2b post-PR-28 review #2 + Copilot #1 — DoS guard.
        // A deeply nested tree (e.g., adversarial HTML with 10k nested
        // divs) must NOT trigger StackOverflowException. Build a tree
        // deeper than MaxRecursionDepth (256) + verify the layouter
        // throws InvalidOperationException with a clear message.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());
        var current = root;
        // Build a chain of 300 nested BlockContainers — exceeds the
        // 256 cap.
        for (var i = 0; i < 300; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Height, 1);
            var box = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            current.AppendChild(box);
            current = box;
        }

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        // ref parameters can't be captured in lambdas; use try/catch.
        InvalidOperationException? caught = null;
        try
        {
            layouter.AttemptLayout(
                ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);
        }
        catch (InvalidOperationException ex)
        {
            caught = ex;
        }
        Assert.NotNull(caught);
        Assert.Contains("recursion depth", caught!.Message,
            System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostPr28_recursion_observes_cancellation_token()
    {
        // Per cycle-2b post-PR-28 review #2 + Copilot #1 — CT is now
        // threaded through the recursion. Build a moderately-deep tree
        // (50 nested divs — under the depth cap), pre-cancel the
        // token, verify OperationCanceledException is thrown PROMPTLY
        // (before all 50 levels are walked).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());
        var current = root;
        for (var i = 0; i < 50; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Height, 1);
            var box = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            current.AppendChild(box);
            current = box;
        }

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();

        // ref parameters can't be captured in lambdas; use try/catch.
        OperationCanceledException? caught = null;
        try
        {
            layouter.AttemptLayout(
                ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict, cts.Token);
        }
        catch (OperationCanceledException ex)
        {
            caught = ex;
        }
        Assert.NotNull(caught);
    }

    [Fact]
    public void PostPr28_recursion_does_not_walk_into_table_children()
    {
        // Per cycle-2b post-PR-28 review #3 — Table is block-level for
        // outer display but its inner geometry (rows, cells) belongs
        // to TableLayouter (Phase 3 Task ~9). The recursion's
        // IsBlockFlowContainerOwnedByBlockLayouter predicate must
        // skip walking INTO a Table's children.
        //
        // Tree:
        //   root > table (BoxKind.Table) > row (BoxKind.TableRow)
        //
        // Expected: only the table fragment emitted (table is
        // block-level, so the OUTER loop emits it as a placeholder).
        // The row is NOT emitted (cycle 2b's recursion gates on
        // flow-container kinds; Table is not flow).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var tableStyle = MakeStyle();
        SetLengthPx(tableStyle, PropertyId.Height, 200);
        var table = Box.ForElement(BoxKind.Table, tableStyle, MakeElement());
        root.AppendChild(table);

        var rowStyle = MakeStyle();
        SetLengthPx(rowStyle, PropertyId.Height, 50);
        var row = Box.ForElement(BoxKind.TableRow, rowStyle, MakeElement());
        table.AppendChild(row);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Only the table — row's geometry is TableLayouter's domain.
        Assert.Single(sink.Fragments);
        Assert.Same(table, sink.Fragments[0].Box);
    }

    [Fact]
    public void PostPr28_recursion_does_not_walk_into_flex_grid_or_replaced_children()
    {
        // Per cycle-2b post-PR-28 review #3 — BlockLayouter recursion
        // skips flex/grid/replaced subtrees; their dedicated layouters
        // emit those items instead.
        //
        // Phase 3 Task 15 cycle 1: FlexLayouter shipped + emits flex
        // items. Phase 3 Task 17 cycle 1: GridLayouter shipped + emits
        // grid items when the grid has an explicit template. PR-#103
        // review F1: cycle 6a's implicit-only grid path means a grid
        // with NO template still emits its items via implicit tracks.
        // BlockReplacedElement remains atomic (no inner-content
        // dispatch yet).
        //
        // Net fragment count: 5 (flex wrapper, flex item, grid
        // wrapper, grid item via GridLayouter implicit-only path, img
        // wrapper).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        // Flex container with a flex-item child.
        var flexStyle = MakeStyle();
        SetLengthPx(flexStyle, PropertyId.Height, 100);
        var flex = Box.ForElement(BoxKind.FlexContainer, flexStyle, MakeElement());
        root.AppendChild(flex);
        var flexItemStyle = MakeStyle();
        SetLengthPx(flexItemStyle, PropertyId.Height, 30);
        var flexItem = Box.ForElement(BoxKind.BlockContainer, flexItemStyle, MakeElement());
        flex.AppendChild(flexItem);

        // Grid container with a grid-item child.
        var gridStyle = MakeStyle();
        SetLengthPx(gridStyle, PropertyId.Height, 100);
        var grid = Box.ForElement(BoxKind.GridContainer, gridStyle, MakeElement());
        root.AppendChild(grid);
        var gridItemStyle = MakeStyle();
        SetLengthPx(gridItemStyle, PropertyId.Height, 40);
        var gridItem = Box.ForElement(BoxKind.BlockContainer, gridItemStyle, MakeElement());
        grid.AppendChild(gridItem);

        // Block-replaced (e.g., img with display:block) — atomic; no inner.
        var imgStyle = MakeStyle();
        SetLengthPx(imgStyle, PropertyId.Height, 80);
        var img = Box.ForElement(BoxKind.BlockReplacedElement, imgStyle, MakeElement());
        root.AppendChild(img);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // 5 fragments per PR-#103 review F1: flex wrapper, flex item,
        // grid wrapper, grid item via GridLayouter implicit-only
        // path, img wrapper.
        Assert.Equal(5, sink.Fragments.Count);
        Assert.Same(flex, sink.Fragments[0].Box);
        Assert.Same(flexItem, sink.Fragments[1].Box);
        Assert.Same(grid, sink.Fragments[2].Box);
        Assert.Same(gridItem, sink.Fragments[3].Box);
        Assert.Same(img, sink.Fragments[4].Box);
    }

    [Fact]
    public void PostPr28_forced_overflow_emits_nested_descendants_too()
    {
        // Per cycle-2b post-PR-28 review #4 — the existing Cycle2b
        // tests cover the normal Continue path, but the recursion ALSO
        // runs from the forced-overflow path. Verify nested descendants
        // emit AND the diagnostic fires when an oversized parent goes
        // through forward-progress.
        //
        // Tree:
        //   root > parent (oversized: height=2000 > page=800)
        //          └─ child (height=100)
        //
        // The parent overflows the 800px page; the layouter takes the
        // forced-overflow path on Strict, emits parent + diagnostic,
        // and (per cycle 2b) also emits the nested child.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        var root = Box.CreateRoot(MakeStyle());

        var parentStyle = MakeStyle();
        SetLengthPx(parentStyle, PropertyId.Height, 2000);  // > page
        var parent = Box.ForElement(BoxKind.BlockContainer, parentStyle, MakeElement());
        root.AppendChild(parent);

        var childStyle = MakeStyle();
        SetLengthPx(childStyle, PropertyId.Height, 100);
        var child = Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement());
        parent.AppendChild(child);

        using var layouter = new BlockLayouter(
            root, sink, incomingContinuation: null, diagnostics: diagSink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Forced-overflow committed parent + child both.
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.Equal(2, sink.Fragments.Count);
        Assert.Same(parent, sink.Fragments[0].Box);
        Assert.Same(child, sink.Fragments[1].Box);
        // Diagnostic fired.
        Assert.Single(diagSink.Diagnostics);
        Assert.Equal(PaginateDiagnosticCodes.PaginationForcedOverflow001,
            diagSink.Diagnostics[0].Code);
        // Child positioned inside parent (parent.BlockOffset=0;
        // child.BlockOffset = parent.contentTop = 0).
        Assert.Equal(0, sink.Fragments[1].BlockOffset);
    }

    [Fact]
    public void PostPr28_rewind_rolls_back_nested_descendants()
    {
        // Per cycle-2b post-PR-28 review #5 — rewind tests previously
        // covered top-level fragments only. With recursion, a single
        // top-level child can produce multiple sink entries (parent +
        // descendants). On rewind to a checkpoint BEFORE the parent
        // was emitted, sink.RollbackTo must discard ALL of them.
        //
        // Tree: 3 top-level divs, each with a nested p.
        //   div0 > p0   div1 > p1 (← rewind target)   div2 > p2
        // After emitting div0+p0+div1+p1+div2+p2, rewind to a checkpoint
        // captured at div2's boundary (cursor = 4: 4 fragments for
        // [div0, p0, div1, p1] already emitted before div2). Rollback
        // truncates the sink to cursor 4 → only [div0, p0, div1, p1]
        // remain.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());
        for (var i = 0; i < 3; i++)
        {
            var divStyle = MakeStyle();
            SetLengthPx(divStyle, PropertyId.Height, 200);
            var div = Box.ForElement(BoxKind.BlockContainer, divStyle, MakeElement());
            root.AppendChild(div);
            var pStyle = MakeStyle();
            SetLengthPx(pStyle, PropertyId.Height, 50);
            var p = Box.ForElement(BoxKind.BlockContainer, pStyle, MakeElement());
            div.AppendChild(p);
        }

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new RewindAtThirdTopLevelResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Resolver returned Rewind at third top-level boundary.
        Assert.Equal(LayoutAttemptOutcome.NeedsRewind, result.Outcome);
        // Sink had 4 fragments emitted before the rewind point
        // (div0+p0+div1+p1). Rollback to that cursor.
        Assert.Equal(4, result.RewindTo!.FragmentOutputCursor);
        sink.RollbackTo(result.RewindTo.FragmentOutputCursor);
        Assert.Equal(4, sink.Fragments.Count);
    }

    /// <summary>Resolver that returns Rewind on the third
    /// ConsiderBreakAt call (third top-level boundary), targeting that
    /// checkpoint. Used by
    /// <c>PostPr28_rewind_rolls_back_nested_descendants</c>.</summary>
    private sealed class RewindAtThirdTopLevelResolver : IBreakResolver
    {
        private int _calls;
        private CheckpointLease _lastLease;

        public BreakDecision ConsiderBreakAt(BreakOpportunity opportunity, FragmentainerContext ctx)
        {
            _calls++;
            if (_calls == 3)
            {
                return new BreakDecision(BreakAction.Rewind, 0, _lastLease.Checkpoint);
            }
            return BreakDecision.Continue;
        }

        public OptimizerResult ResolveBreaks(
            IReadOnlyList<BreakOpportunity> opportunities, FragmentainerContext ctx, CancellationToken cancellationToken = default)
            => OptimizerResult.Empty;

        public void RegisterCheckpoint(CheckpointLease lease)
        {
            if (_lastLease.Checkpoint is not null
                && !ReferenceEquals(_lastLease.Checkpoint, lease.Checkpoint))
            {
                LayoutCheckpointPool.Return(_lastLease);
            }
            _lastLease = lease;
        }

        public LayoutCheckpoint? GetLastCheckpoint() => _lastLease.Checkpoint;

        public void Dispose()
        {
            if (_lastLease.Checkpoint is not null)
            {
                LayoutCheckpointPool.Return(_lastLease);
                _lastLease = default;
            }
        }
    }

    [Fact]
    public void PostPr28_non_block_child_between_blocks_breaks_collapse_inside_nested_parent()
    {
        // Per cycle-2b post-PR-28 review #9 — strengthen the existing
        // Cycle2b_nested_non_block_children_are_skipped test which
        // only verified the fragment count. Per PR #23 fix #3 + the
        // recursion's faithful copy of that logic, a non-block child
        // between two block siblings INSIDE a nested parent must
        // break the margin-collapse chain — the second block must
        // apply its FULL marginTop without collapsing with the first
        // block's marginBottom.
        //
        // Tree:
        //   root > div > [c1 (margin-bottom:30), text-run, c2 (margin-top:20)]
        //
        // If text-run breaks adjacency: c2's BlockOffset = c1's bottom
        //   + c1.marginBottom + c2.marginTop = 50 + 30 + 20 = 100.
        // If text-run did NOT break adjacency (regression): c2 would
        //   collapse with c1: BlockOffset = 50 + max(30, 20) = 80.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var divStyle = MakeStyle();
        SetLengthPx(divStyle, PropertyId.Height, 300);
        var div = Box.ForElement(BoxKind.BlockContainer, divStyle, MakeElement());
        root.AppendChild(div);

        var c1Style = MakeStyle();
        SetLengthPx(c1Style, PropertyId.Height, 50);
        SetLengthPx(c1Style, PropertyId.MarginBottom, 30);
        var c1 = Box.ForElement(BoxKind.BlockContainer, c1Style, MakeElement());
        div.AppendChild(c1);

        // Inline text run between the two block siblings — breaks
        // margin adjacency.
        var textStyle = MakeStyle();
        var text = Box.ForElement(BoxKind.TextRun, textStyle, MakeElement());
        div.AppendChild(text);

        var c2Style = MakeStyle();
        SetLengthPx(c2Style, PropertyId.Height, 40);
        SetLengthPx(c2Style, PropertyId.MarginTop, 20);
        var c2 = Box.ForElement(BoxKind.BlockContainer, c2Style, MakeElement());
        div.AppendChild(c2);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // div + c1 + c2 (text skipped).
        Assert.Equal(3, sink.Fragments.Count);
        // c1 at div.contentTop + marginTop=0 = 0; height=50.
        Assert.Same(c1, sink.Fragments[1].Box);
        Assert.Equal(0, sink.Fragments[1].BlockOffset);
        // c2 at: cursor after c1 = 0 (topShift) + 50 (size) + 30 (marginBottom) = 80.
        // text reset hasPriorAdjoiningBlock to false → c2 uses topShift = marginTop = 20.
        // c2.BlockOffset = div.contentTop + 80 + 20 = 100.
        Assert.Same(c2, sink.Fragments[2].Box);
        Assert.Equal(100, sink.Fragments[2].BlockOffset);
    }

    // ====================================================================
    //  Phase 3 Task 7 cycle 2c — subtree-aware pagination (MVP: atomic,
    //  break-before for oversized subtrees). Mid-subtree splits remain a
    //  cycle 2d deferral pin below.
    // ====================================================================

    [Fact]
    public void Cycle2c_nested_overflow_pushes_subtree_to_next_page_when_prior_content_exists()
    {
        // Cycle 2c MVP — pre-cycle-2c the outer break decision used
        // the parent's OWN borderBoxBlockSize, ignoring any descendants
        // that visually overflow the parent. A scenario like:
        //
        //   root > [div_a (height=200), parent (height=600) > child (height=900)]
        //
        // pre-cycle-2c emitted div_a (200) + parent (600 fits in remaining
        // 600) + child (overflowing — painter would clip on page 2).
        //
        // Cycle 2c MVP measures parent's full subtree extent =
        // max(parent's own=600, child's bottom in parent coords = 0+900
        // = 900) = 900. The outer break check sees 200 + 900 = 1100 >
        // 800 page → BreakHere → break-before parent. The continuation
        // resumes at parent on page 2; on page 2, parent is the first
        // child + its 900-extent still exceeds the 800 page → forced-
        // overflow forward progress (per cycle 2b's existing path) →
        // commits parent + child + diagnostic.
        //
        // Result: page 1 = div_a only (200px); page 2 = parent + child
        // (with overflow diagnostic). This is the "child crosses page
        // boundary cleanly" outcome from the original deferral pin.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        var root = Box.CreateRoot(MakeStyle());

        var divAStyle = MakeStyle();
        SetLengthPx(divAStyle, PropertyId.Height, 200);
        var divA = Box.ForElement(BoxKind.BlockContainer, divAStyle, MakeElement());
        root.AppendChild(divA);

        var parentStyle = MakeStyle();
        SetLengthPx(parentStyle, PropertyId.Height, 600);
        var parent = Box.ForElement(BoxKind.BlockContainer, parentStyle, MakeElement());
        root.AppendChild(parent);

        var childStyle = MakeStyle();
        SetLengthPx(childStyle, PropertyId.Height, 900);
        var child = Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement());
        parent.AppendChild(child);

        using var layouter = new BlockLayouter(
            root, sink, incomingContinuation: null, diagnostics: diagSink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        // Page 1 — should emit div_a + break-before parent.
        var page1 = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);
        Assert.Equal(LayoutAttemptOutcome.PageComplete, page1.Outcome);
        var cont1 = Assert.IsType<BlockContinuation>(page1.Continuation);
        // Resume at parent's index (1 — div_a was index 0, parent is 1).
        Assert.Equal(1, cont1.ResumeAtChild);
        // Page 1 emits only div_a. Pre-cycle-2c would have emitted
        // div_a + parent + child here.
        Assert.Single(sink.Fragments);
        Assert.Same(divA, sink.Fragments[0].Box);
    }

    [Fact]
    public void Cycle2c_nested_overflow_on_fresh_page_emits_with_diagnostic()
    {
        // When an oversized subtree is the ONLY content (or first child
        // on a fresh page), it can't be pushed further — the layouter
        // commits via forced-overflow forward progress + emits
        // PAGINATION-FORCED-OVERFLOW-001. Pre-cycle-2c this case ALSO
        // emitted, but silently (the outer measure missed the
        // overflow). Cycle 2c makes the diagnostic fire correctly.
        //
        //   root > parent (height=200) > child (height=1500 — overflows)
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        var root = Box.CreateRoot(MakeStyle());

        var parentStyle = MakeStyle();
        SetLengthPx(parentStyle, PropertyId.Height, 200);
        var parent = Box.ForElement(BoxKind.BlockContainer, parentStyle, MakeElement());
        root.AppendChild(parent);

        var childStyle = MakeStyle();
        SetLengthPx(childStyle, PropertyId.Height, 1500);
        var child = Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement());
        parent.AppendChild(child);

        using var layouter = new BlockLayouter(
            root, sink, incomingContinuation: null, diagnostics: diagSink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Forced overflow committed parent + child both.
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.Equal(2, sink.Fragments.Count);
        // Diagnostic fired (cycle 2c improvement — pre-fix this was silent).
        Assert.Single(diagSink.Diagnostics);
        Assert.Equal(PaginateDiagnosticCodes.PaginationForcedOverflow001,
            diagSink.Diagnostics[0].Code);
    }

    [Fact]
    public void Cycle2c_subsequent_sibling_does_not_overlap_overflowing_subtree()
    {
        // Cycle 2c — cursor advance is also subtree-aware. Pre-cycle-2c
        // the cursor advanced by parent's OWN border-box size; a sibling
        // AFTER an overflowing subtree would visually overlap the
        // overflow.
        //
        //   root > [
        //     parent_a (height=200) > overflowing_child (height=350),
        //     sibling (height=100)
        //   ]
        //
        // Parent_a's subtree extent: max(200, 0+350) = 350.
        // Pre-cycle-2c: cursor after parent_a = 200; sibling.BlockOffset = 200
        //   (overlaps with overflowing_child which extends to 350).
        // Cycle 2c: cursor after parent_a = 350; sibling.BlockOffset = 350
        //   (no overlap).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var parentAStyle = MakeStyle();
        SetLengthPx(parentAStyle, PropertyId.Height, 200);
        var parentA = Box.ForElement(BoxKind.BlockContainer, parentAStyle, MakeElement());
        root.AppendChild(parentA);

        var overflowingStyle = MakeStyle();
        SetLengthPx(overflowingStyle, PropertyId.Height, 350);
        var overflowing = Box.ForElement(BoxKind.BlockContainer, overflowingStyle, MakeElement());
        parentA.AppendChild(overflowing);

        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 100);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // 3 fragments: parent_a, overflowing, sibling.
        Assert.Equal(3, sink.Fragments.Count);
        // parent_a at 0, overflowing at 0 (inside parent_a's content area),
        // sibling at 350 (post-cycle-2c — was 200 pre-fix).
        Assert.Same(parentA, sink.Fragments[0].Box);
        Assert.Equal(0, sink.Fragments[0].BlockOffset);
        Assert.Same(overflowing, sink.Fragments[1].Box);
        Assert.Equal(0, sink.Fragments[1].BlockOffset);
        Assert.Same(sibling, sink.Fragments[2].Box);
        Assert.Equal(350, sink.Fragments[2].BlockOffset);
    }

    [Fact]
    public void Cycle2c_leaf_box_subtree_extent_equals_own_border_box()
    {
        // Sanity: for a leaf box (no block-level children), the
        // subtree extent equals the box's own border-box size. Cycle
        // 2c shouldn't change behavior for cycle-1-style leaf-only
        // trees (the entire pre-cycle-2b test corpus stays green).
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: 0),
            (height: 200, marginTop: 0, marginBottom: 0));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Equal(2, sink.Fragments.Count);
        Assert.Equal(0, sink.Fragments[0].BlockOffset);
        Assert.Equal(100, sink.Fragments[0].BlockSize);
        Assert.Equal(100, sink.Fragments[1].BlockOffset);
        Assert.Equal(200, sink.Fragments[1].BlockSize);
    }

    [Fact]
    public void Cycle2c_subtree_extent_respects_margin_collapse_between_nested_siblings()
    {
        // Subtree-extent measure should use the same margin-collapse
        // logic as EmitBlockSubtreeRecursive. Tree:
        //
        //   root > parent (no padding/border, height=auto-as-0) >
        //     [c1 (height=100, marginBottom=20),
        //      c2 (height=80, marginTop=10)]
        //
        // Without collapse: stack = 100 + 20 + 10 + 80 = 210.
        // With collapse: c1.bottom=100; collapse(20,10)=20; topShift=20-20=0;
        //   c2.bottom = 100 + 20 + 0 + 80 = 200.
        //
        // Subtree extent (including parent's auto height resolved to 0):
        //   max(parent's own=0, c2.bottom=200) = 200.
        //
        // Subsequent sibling at root level should be at offset 200 (not
        // 210), confirming collapse was applied during the measure.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var parentStyle = MakeStyle();
        // height=auto (= 0 in cycle 2c since no auto-resolution yet)
        var parent = Box.ForElement(BoxKind.BlockContainer, parentStyle, MakeElement());
        root.AppendChild(parent);

        var c1Style = MakeStyle();
        SetLengthPx(c1Style, PropertyId.Height, 100);
        SetLengthPx(c1Style, PropertyId.MarginBottom, 20);
        var c1 = Box.ForElement(BoxKind.BlockContainer, c1Style, MakeElement());
        parent.AppendChild(c1);

        var c2Style = MakeStyle();
        SetLengthPx(c2Style, PropertyId.Height, 80);
        SetLengthPx(c2Style, PropertyId.MarginTop, 10);
        var c2 = Box.ForElement(BoxKind.BlockContainer, c2Style, MakeElement());
        parent.AppendChild(c2);

        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 50);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // 4 fragments: parent, c1, c2, sibling.
        Assert.Equal(4, sink.Fragments.Count);
        // sibling at offset 200 — confirms collapse was applied during
        // pre-measure (210 would be no-collapse / sum).
        Assert.Same(sibling, sink.Fragments[3].Box);
        Assert.Equal(200, sink.Fragments[3].BlockOffset);
    }

    [Fact]
    public void Cycle2c_subtree_extent_treats_table_as_atomic_using_own_height()
    {
        // Per cycle-2b post-PR-28 review #3 — Table/Flex/Grid/Replaced
        // are atomic to the BlockLayouter. The subtree-extent measure
        // also gates on IsBlockFlowContainerOwnedByBlockLayouter; for
        // a Table, its own borderBoxBlockSize is used, NOT a recursive
        // walk into table rows.
        //
        //   root > [
        //     table (height=300) > row (height=200),  // atomic; extent = 300
        //     sibling (height=100)
        //   ]
        //
        // Sibling should be at offset 300 (table's own height), not at
        // some other position based on incorrectly recursing into row.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var tableStyle = MakeStyle();
        SetLengthPx(tableStyle, PropertyId.Height, 300);
        var table = Box.ForElement(BoxKind.Table, tableStyle, MakeElement());
        root.AppendChild(table);

        var rowStyle = MakeStyle();
        SetLengthPx(rowStyle, PropertyId.Height, 200);
        var row = Box.ForElement(BoxKind.TableRow, rowStyle, MakeElement());
        table.AppendChild(row);

        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 100);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // 2 fragments: table (placeholder, atomic) + sibling. Row is
        // NOT emitted (TableLayouter's domain) per cycle 2b review #3.
        Assert.Equal(2, sink.Fragments.Count);
        Assert.Same(table, sink.Fragments[0].Box);
        Assert.Equal(300, sink.Fragments[0].BlockSize);
        Assert.Same(sibling, sink.Fragments[1].Box);
        // sibling at offset 300 (table's own height) — confirms the
        // measure pass treats Table as atomic.
        Assert.Equal(300, sink.Fragments[1].BlockOffset);
    }

    // ====================================================================
    //  Phase 3 Task 8 cycle 1 — FloatManager integration
    // ====================================================================

    [Fact]
    public void Task8_left_float_emits_at_inline_start_of_containing_block()
    {
        // CSS: `<div style="float:left; width:100; height:80"></div>`
        // → fragment at InlineOffset=0, BlockOffset=0, InlineSize=100,
        //   BlockSize=80.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, keywordIndex: 1);  // left
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 80);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Single(sink.Fragments);
        Assert.Equal(0, sink.Fragments[0].InlineOffset);
        Assert.Equal(0, sink.Fragments[0].BlockOffset);
        Assert.Equal(100, sink.Fragments[0].InlineSize);
        Assert.Equal(80, sink.Fragments[0].BlockSize);
    }

    [Fact]
    public void Task8_right_float_emits_at_inline_end_minus_size()
    {
        // CSS: `<div style="float:right; width:100; height:50"></div>`
        // → fragment at InlineOffset=500 (= 600 - 100).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, keywordIndex: 2);  // right
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 50);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Single(sink.Fragments);
        Assert.Equal(500, sink.Fragments[0].InlineOffset);
    }

    [Fact]
    public void Task8_float_does_not_advance_in_flow_cursor()
    {
        // Tree: float (h=80), block (h=100).
        // Cycle 1 MVP: float is out-of-flow, so the in-flow block
        // emits at offset 0 (NOT 80). Cycle 2 will reduce inline-size
        // to flow around the float.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);  // left
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 80);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        var blockStyle = MakeStyle();
        SetLengthPx(blockStyle, PropertyId.Height, 100);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(2, sink.Fragments.Count);
        Assert.Same(floatBox, sink.Fragments[0].Box);
        Assert.Same(block, sink.Fragments[1].Box);
        // Float at y=0 (out-of-flow); block ALSO at y=0 (cycle 1 MVP —
        // floats overlap in-flow content visually).
        Assert.Equal(0, sink.Fragments[0].BlockOffset);
        Assert.Equal(0, sink.Fragments[1].BlockOffset);
    }

    [Fact]
    public void Task8_clear_left_advances_block_past_left_float()
    {
        // Tree: float (h=80, float:left), block (h=100, clear:left).
        // Cycle 1: clear:left advances block past float's bottom (80).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);  // left
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 80);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        var blockStyle = MakeStyle();
        SetKeyword(blockStyle, PropertyId.Clear, 1);  // left
        SetLengthPx(blockStyle, PropertyId.Height, 100);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(2, sink.Fragments.Count);
        // Float at y=0.
        Assert.Equal(0, sink.Fragments[0].BlockOffset);
        // Block at y=80 (cleared past left float's bottom).
        Assert.Equal(80, sink.Fragments[1].BlockOffset);
    }

    [Fact]
    public void Task8_clear_right_ignores_left_float()
    {
        // Block has clear:right but only a left float exists → no
        // clearance, block emits at y=0.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);  // left
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 100);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        var blockStyle = MakeStyle();
        SetKeyword(blockStyle, PropertyId.Clear, 2);  // right
        SetLengthPx(blockStyle, PropertyId.Height, 50);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Block at y=0 (clear:right finds no right floats).
        Assert.Equal(0, sink.Fragments[1].BlockOffset);
    }

    [Fact]
    public void Task8_clear_both_advances_past_both_sides()
    {
        // Two floats (left + right), block has clear:both → past max.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var leftStyle = MakeStyle();
        SetKeyword(leftStyle, PropertyId.Float, 1);  // left
        SetLengthPx(leftStyle, PropertyId.Width, 80);
        SetLengthPx(leftStyle, PropertyId.Height, 60);
        var leftFloat = Box.ForElement(BoxKind.BlockContainer, leftStyle, MakeElement());
        root.AppendChild(leftFloat);

        var rightStyle = MakeStyle();
        SetKeyword(rightStyle, PropertyId.Float, 2);  // right
        SetLengthPx(rightStyle, PropertyId.Width, 80);
        SetLengthPx(rightStyle, PropertyId.Height, 90);
        var rightFloat = Box.ForElement(BoxKind.BlockContainer, rightStyle, MakeElement());
        root.AppendChild(rightFloat);

        var blockStyle = MakeStyle();
        SetKeyword(blockStyle, PropertyId.Clear, 3);  // both
        SetLengthPx(blockStyle, PropertyId.Height, 50);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Block clears past max(left=60, right=90) = 90.
        Assert.Equal(90, sink.Fragments[2].BlockOffset);
    }

    [Fact]
    public void Task8_two_left_floats_stack_vertically()
    {
        // Two left floats → second stacks below first per cycle-1 MVP.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var float1Style = MakeStyle();
        SetKeyword(float1Style, PropertyId.Float, 1);
        SetLengthPx(float1Style, PropertyId.Width, 100);
        SetLengthPx(float1Style, PropertyId.Height, 60);
        var float1 = Box.ForElement(BoxKind.BlockContainer, float1Style, MakeElement());
        root.AppendChild(float1);

        var float2Style = MakeStyle();
        SetKeyword(float2Style, PropertyId.Float, 1);
        SetLengthPx(float2Style, PropertyId.Width, 100);
        SetLengthPx(float2Style, PropertyId.Height, 40);
        var float2 = Box.ForElement(BoxKind.BlockContainer, float2Style, MakeElement());
        root.AppendChild(float2);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(2, sink.Fragments.Count);
        Assert.Equal(0, sink.Fragments[0].BlockOffset);   // first at y=0
        Assert.Equal(60, sink.Fragments[1].BlockOffset);  // second at y=60
    }

    [Fact]
    public void Task8_float_does_not_break_margin_collapse_chain_between_in_flow_blocks()
    {
        // Tree: blockA (marginBottom=20), float, blockB (marginTop=10).
        // Floats are out-of-flow per CSS 2.2 §9.5 — the in-flow chain
        // continues across the float, so blockA's marginBottom + blockB's
        // marginTop collapse normally.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var aStyle = MakeStyle();
        SetLengthPx(aStyle, PropertyId.Height, 50);
        SetLengthPx(aStyle, PropertyId.MarginBottom, 20);
        var blockA = Box.ForElement(BoxKind.BlockContainer, aStyle, MakeElement());
        root.AppendChild(blockA);

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);
        SetLengthPx(floatStyle, PropertyId.Width, 80);
        SetLengthPx(floatStyle, PropertyId.Height, 30);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        var bStyle = MakeStyle();
        SetLengthPx(bStyle, PropertyId.Height, 40);
        SetLengthPx(bStyle, PropertyId.MarginTop, 10);
        var blockB = Box.ForElement(BoxKind.BlockContainer, bStyle, MakeElement());
        root.AppendChild(blockB);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // 3 fragments: blockA, float, blockB.
        Assert.Equal(3, sink.Fragments.Count);
        // blockA at 0, height 50.
        Assert.Same(blockA, sink.Fragments[0].Box);
        Assert.Equal(0, sink.Fragments[0].BlockOffset);
        // blockB after blockA + collapsed margin: cursor=70 (=50 +
        // marginBottom=20). Collapse(20, 10) = 20, topShift = 0.
        // blockB at 70.
        Assert.Same(blockB, sink.Fragments[2].Box);
        Assert.Equal(70, sink.Fragments[2].BlockOffset);
    }

    [Fact]
    public void Task8_inline_start_float_resolves_as_left_in_cycle_1_LTR()
    {
        // float: inline-start (keyword 3) → left under cycle 1's
        // horizontal-tb LTR assumption.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 3);  // inline-start
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 80);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Aligned left (cycle 1: inline-start → left).
        Assert.Equal(0, sink.Fragments[0].InlineOffset);
    }

    [Fact]
    public void Task8_clear_resets_collapse_chain()
    {
        // Tree: blockA (marginBottom=30), float (h=200), blockB
        //   (clear:left, marginTop=10).
        // Trace:
        //   - blockA at y=0, h=50, marginBottom=30 → cursor=80 after.
        //   - float placed at currentBlockY=80 (float is processed
        //     AFTER blockA in source order; cursor reflects blockA's
        //     contribution). Float height=200 → float bottom = 280.
        //   - blockB clear:left, marginTop=10. Per CSS 2.1 clearance
        //     is space ABOVE marginTop chosen so border-box top =
        //     max(hypothetical, floatBottom). Hypothetical without
        //     clearance = 80 + marginTop=10 = 90. Float bottom = 280.
        //     border-box top = max(90, 280) = 280. Per cycle 1
        //     post-PR-30 review (P1 #2) — pre-fix would have been 290
        //     (= 280 + 10, marginTop applied ON TOP of clearance);
        //     post-fix snaps to float bottom = 280.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var aStyle = MakeStyle();
        SetLengthPx(aStyle, PropertyId.Height, 50);
        SetLengthPx(aStyle, PropertyId.MarginBottom, 30);
        var blockA = Box.ForElement(BoxKind.BlockContainer, aStyle, MakeElement());
        root.AppendChild(blockA);

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);  // left
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 200);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        var bStyle = MakeStyle();
        SetKeyword(bStyle, PropertyId.Clear, 1);  // left
        SetLengthPx(bStyle, PropertyId.Height, 40);
        SetLengthPx(bStyle, PropertyId.MarginTop, 10);
        var blockB = Box.ForElement(BoxKind.BlockContainer, bStyle, MakeElement());
        root.AppendChild(blockB);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(3, sink.Fragments.Count);
        Assert.Same(blockB, sink.Fragments[2].Box);
        // Per CSS 2.1 clearance — border-box top = max(hypothetical=90,
        // floatBottom=280) = 280. Cycle 1 post-PR-30 fix.
        Assert.Equal(280, sink.Fragments[2].BlockOffset);
    }

    // ====================================================================
    //  Phase 3 Task 8 cycle 1 post-PR-30 review tests
    //  (the 6 required regression tests + supporting coverage)
    // ====================================================================

    [Fact]
    public void PostPr30_rewind_after_emitted_float_does_not_duplicate_fragments()
    {
        // Per cycle 1 post-PR-30 review (P0 #1) — pre-fix the float
        // accounting (lastEmittedIdx, FloatManager snapshot/restore)
        // was missing, so a rewind after a float emit could:
        //   (a) leave the float fragment in the sink (correct — sink
        //       rolls back to the captured cursor), but
        //   (b) re-emit the float on retry from a stale lastEmittedIdx,
        //   (c) AND keep the float record in _floatManager (no
        //       FloatManager rewind support).
        // Post-fix: lastEmittedIdx advances past the float; the
        // checkpoint captures the FloatManager snapshot; restore
        // pops the snapshot back into _floatManager.
        //
        // Tree: float (h=80), block (h=100). Resolver rewinds at
        // block 1 (the in-flow block AFTER the float). Sink should
        // contain ONLY the float fragment after rollback (cursor=1).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);  // left
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 80);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        var blockStyle = MakeStyle();
        SetLengthPx(blockStyle, PropertyId.Height, 100);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new RewindAtSecondTopLevelResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.NeedsRewind, result.Outcome);
        // 1 fragment emitted before rewind: the float (childIdx=0).
        // Block (childIdx=1) hit the resolver's Rewind so its fragment
        // wasn't emitted.
        Assert.Equal(1, result.RewindTo!.FragmentOutputCursor);
        sink.RollbackTo(result.RewindTo.FragmentOutputCursor);
        Assert.Single(sink.Fragments);
        Assert.Same(floatBox, sink.Fragments[0].Box);
    }

    /// <summary>Resolver that rewinds on the second ConsiderBreakAt
    /// call (top-level block after a float).</summary>
    private sealed class RewindAtSecondTopLevelResolver : IBreakResolver
    {
        private int _calls;
        private CheckpointLease _lastLease;

        public BreakDecision ConsiderBreakAt(BreakOpportunity opportunity, FragmentainerContext ctx)
        {
            _calls++;
            if (_calls == 1)  // second call (1-indexed via _calls++ first)
            {
                return new BreakDecision(BreakAction.Rewind, 0, _lastLease.Checkpoint);
            }
            return BreakDecision.Continue;
        }

        public OptimizerResult ResolveBreaks(
            IReadOnlyList<BreakOpportunity> opportunities, FragmentainerContext ctx, CancellationToken cancellationToken = default)
            => OptimizerResult.Empty;

        public void RegisterCheckpoint(CheckpointLease lease)
        {
            if (_lastLease.Checkpoint is not null
                && !ReferenceEquals(_lastLease.Checkpoint, lease.Checkpoint))
            {
                LayoutCheckpointPool.Return(_lastLease);
            }
            _lastLease = lease;
        }

        public LayoutCheckpoint? GetLastCheckpoint() => _lastLease.Checkpoint;

        public void Dispose()
        {
            if (_lastLease.Checkpoint is not null)
            {
                LayoutCheckpointPool.Return(_lastLease);
                _lastLease = default;
            }
        }
    }

    [Fact]
    public void PostPr30_float_counts_as_emitted_for_forced_overflow_check()
    {
        // Per cycle 1 post-PR-30 review (P0 #1) — pre-fix
        // emittedThisAttempt didn't increment after a float, so a page
        // containing only a float + an oversized block would trip the
        // forced-overflow path (treating the oversized block as the
        // "first thing on a fresh page"). Post-fix: float counts as
        // emitted; the oversized block triggers a NORMAL break-before
        // (PageComplete with ResumeAtChild=blockIdx).
        //
        // Tree: float (h=100), block (h=2000 — taller than page).
        // Page = 800.
        // Pre-fix: float emit (emittedThisAttempt stays 0); block hits
        //   BreakHere; emittedThisAttempt==0 + atTopOfPage → forced-
        //   overflow path → emit block + diagnostic → PageComplete
        //   with ResumeAtChild=blockIdx+1 (= past block, no further
        //   resumption).
        // Post-fix: float emit increments emittedThisAttempt;
        //   block hits BreakHere; emittedThisAttempt>0 → NORMAL break-
        //   before path → PageComplete with ResumeAtChild=blockIdx
        //   (the block is not emitted on this page; resumes on next).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 100);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        var blockStyle = MakeStyle();
        SetLengthPx(blockStyle, PropertyId.Height, 2000);  // > page=800
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var cont = Assert.IsType<BlockContinuation>(result.Continuation);
        // Post-fix: ResumeAtChild = block's index (1) — block didn't
        // emit on this page, will resume on the next.
        Assert.Equal(1, cont.ResumeAtChild);
        // Only the float emitted on this page.
        Assert.Single(sink.Fragments);
        Assert.Same(floatBox, sink.Fragments[0].Box);
    }

    [Fact]
    public void PostPr30_clear_with_marginTop_positions_border_at_float_bottom_not_below()
    {
        // Per cycle 1 post-PR-30 review (P1 #2) — clearance is space
        // ABOVE marginTop, not below. Pre-fix: cursor advances to
        // floatBottom, then marginTop is added → border-box top at
        // floatBottom + marginTop (one marginTop too low). Post-fix:
        // border-box top = max(hypothetical-without-clear, floatBottom).
        //
        // Tree: float (h=100), block (clear:left, marginTop=20,
        //   h=50). Page = 800.
        // Trace:
        //   - float at y=0, h=100 → bottom=100.
        //   - block: hypothetical without clear = 0 + marginTop=20 = 20.
        //     With clear:left, border-box top = max(20, 100) = 100.
        //   Pre-fix: 100 + 20 = 120. Post-fix: 100.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 100);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        var blockStyle = MakeStyle();
        SetKeyword(blockStyle, PropertyId.Clear, 1);  // left
        SetLengthPx(blockStyle, PropertyId.MarginTop, 20);
        SetLengthPx(blockStyle, PropertyId.Height, 50);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Block at y=100 (= float bottom), NOT 120 (= float + marginTop).
        Assert.Equal(100, sink.Fragments[1].BlockOffset);
    }

    [Fact]
    public void PostPr30_nested_float_inside_div_uses_float_manager()
    {
        // Per cycle 1 post-PR-30 review (P1 #4) — nested floats are
        // dispatched in EmitBlockSubtreeRecursive too (cycle 1
        // assumes single BFC; cycle 3 will detect nested BFCs).
        //
        // Tree: outer-div > [float-child (h=80, float:left), block-
        //   sibling (h=50, clear:left)].
        // The float and clear-block are NESTED inside outer-div.
        // Float aligns to BFC-wide left edge (= 0); clear block
        // advances past float bottom (= 80).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var outerStyle = MakeStyle();
        SetLengthPx(outerStyle, PropertyId.Height, 200);
        var outer = Box.ForElement(BoxKind.BlockContainer, outerStyle, MakeElement());
        root.AppendChild(outer);

        var floatChildStyle = MakeStyle();
        SetKeyword(floatChildStyle, PropertyId.Float, 1);
        SetLengthPx(floatChildStyle, PropertyId.Width, 100);
        SetLengthPx(floatChildStyle, PropertyId.Height, 80);
        var floatChild = Box.ForElement(BoxKind.BlockContainer, floatChildStyle, MakeElement());
        outer.AppendChild(floatChild);

        var clearSiblingStyle = MakeStyle();
        SetKeyword(clearSiblingStyle, PropertyId.Clear, 1);  // left
        SetLengthPx(clearSiblingStyle, PropertyId.Height, 50);
        var clearSibling = Box.ForElement(BoxKind.BlockContainer, clearSiblingStyle, MakeElement());
        outer.AppendChild(clearSibling);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // 3 fragments: outer, floatChild, clearSibling.
        Assert.Equal(3, sink.Fragments.Count);
        Assert.Same(outer, sink.Fragments[0].Box);
        Assert.Same(floatChild, sink.Fragments[1].Box);
        // Float at BFC y=0, aligned to BFC-left (cycle 1 simplification).
        Assert.Equal(0, sink.Fragments[1].BlockOffset);
        Assert.Equal(0, sink.Fragments[1].InlineOffset);
        // clearSibling at BFC y=80 (past float bottom).
        Assert.Same(clearSibling, sink.Fragments[2].Box);
        Assert.Equal(80, sink.Fragments[2].BlockOffset);
    }

    [Fact]
    public void PostPr30_float_wider_than_containing_block_is_emitted_with_authored_size()
    {
        // Per cycle 1 post-PR-30 review (test #5) — oversized floats
        // are allowed (overflow the containing block; painter handles
        // z-order). Per the FloatManager docstring, end < start or
        // oversized floats are legal.
        //
        // Tree: float (width=800, height=50, float:left) on a 600px
        //   containing block. Float emits at InlineOffset=0,
        //   InlineSize=800 (overflows by 200).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);
        SetLengthPx(floatStyle, PropertyId.Width, 800);  // > 600 page
        SetLengthPx(floatStyle, PropertyId.Height, 50);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Single(sink.Fragments);
        Assert.Equal(0, sink.Fragments[0].InlineOffset);
        Assert.Equal(800, sink.Fragments[0].InlineSize);
    }

    [Fact]
    public void PostPr30_right_float_with_asymmetric_margins_emits_at_correct_inline_offset()
    {
        // Right float with marginLeft=15, marginRight=5, width=100 on
        // a 600px CB:
        //   marginBoxInlineSize = 15 + 100 + 5 = 120
        //   placedInline (margin-box origin) = 600 - 120 = 480
        //   border-box origin = 480 + marginLeft=15 = 495
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 2);  // right
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 50);
        SetLengthPx(floatStyle, PropertyId.MarginLeft, 15);
        SetLengthPx(floatStyle, PropertyId.MarginRight, 5);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(495, sink.Fragments[0].InlineOffset);
        Assert.Equal(100, sink.Fragments[0].InlineSize);  // border-box, not margin-box
    }

    [Fact]
    public void PostPr30_trailing_float_overflow_when_no_prior_emission_still_diagnoses()
    {
        // Per cycle 1 post-PR-30 review (P1 #3 + test #6) — a float
        // near the bottom of the page that extends past the
        // fragmentainer emits PAGINATION-FORCED-OVERFLOW-001.
        // Per Phase 3 Task 8 cycle 2 — when the page already has
        // emitted content, a too-tall float DEFERS to the next page
        // (cross-fragmentainer float per Fragmentation L3 §5) instead
        // of overflowing+diagnostic. This test pins the FALLBACK
        // case: a float that's TALLER THAN ANY PAGE (still emits +
        // diagnostic; cycle 3 will fragment such floats).
        //
        // Tree: float (h=2000, left) on a page=800. The float can't
        //   fit any page; it emits at y=0 with diagnostic.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 2000);  // > page=800
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        using var layouter = new BlockLayouter(
            root, sink, incomingContinuation: null, diagnostics: diagSink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Float emitted (page has no prior content; deferral falls
        // back to cycle 1 emit + diagnostic).
        Assert.Single(sink.Fragments);
        Assert.Same(floatBox, sink.Fragments[0].Box);
        // Diagnostic fired.
        Assert.NotEmpty(diagSink.Diagnostics);
        Assert.Contains(diagSink.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.PaginationForcedOverflow001
            && d.Message.Contains("float overflow", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PostPr30_floatmanager_clear_uses_O1_cache()
    {
        // Per cycle 1 post-PR-30 review (P2 #7) — sanity check that
        // GetClearedBlockY uses the per-side max-bottom cache rather
        // than scanning all records. This is a lightweight test
        // verifying the cache is correctly maintained — many floats
        // + many clears succeed without timing out.
        var fm = new FloatManager();
        // Place 1000 left floats stacked vertically.
        for (var i = 0; i < 1000; i++)
        {
            fm.PlaceFloat(FloatSide.Left, 10, 10, 0, 600, 0);
        }
        // Last float bottom = 10000.
        Assert.Equal(10000, fm.GetClearedBlockY(0, ClearKind.Left));
        // Right has no floats → returns input.
        Assert.Equal(50, fm.GetClearedBlockY(50, ClearKind.Right));
        // Both = max of left + right = 10000.
        Assert.Equal(10000, fm.GetClearedBlockY(0, ClearKind.Both));
    }

    // ====================================================================
    //  Phase 3 Task 8 cycle 2 — flow around floats + cross-page deferral
    // ====================================================================

    [Fact]
    public void Task8c2_in_flow_block_after_left_float_starts_past_float_right_edge()
    {
        // CSS: a left float (w=100, h=200) followed by a block. The
        // block flows AROUND the float — its inline-axis extent
        // shrinks + its inline-offset starts at the float's right
        // edge.
        //
        // Tree: float (float:left, w=100, h=200), block (h=50).
        // Page = 600 wide.
        // Cycle 1 (pre-fix): block at InlineOffset=0, InlineSize=600
        //   (overlaps float visually).
        // Cycle 2 (post-fix): block at InlineOffset=100,
        //   InlineSize=500.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);  // left
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 200);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        var blockStyle = MakeStyle();
        SetLengthPx(blockStyle, PropertyId.Height, 50);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(2, sink.Fragments.Count);
        Assert.Same(floatBox, sink.Fragments[0].Box);
        // Block flows around the float — starts at x=100, width=500.
        Assert.Same(block, sink.Fragments[1].Box);
        Assert.Equal(100, sink.Fragments[1].InlineOffset);
        Assert.Equal(500, sink.Fragments[1].InlineSize);
    }

    [Fact]
    public void Task8c2_in_flow_block_after_right_float_keeps_left_edge_but_shrinks_width()
    {
        // Right float (w=100, h=200), block (h=50). Block flows around
        // the right float: InlineOffset=0, InlineSize=500.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 2);  // right
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 200);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        var blockStyle = MakeStyle();
        SetLengthPx(blockStyle, PropertyId.Height, 50);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Same(block, sink.Fragments[1].Box);
        Assert.Equal(0, sink.Fragments[1].InlineOffset);
        Assert.Equal(500, sink.Fragments[1].InlineSize);
    }

    [Fact]
    public void Task8c2_block_past_float_bottom_uses_full_width()
    {
        // Float (h=50), tall block (h=300). Block at y=0 — float at
        // y=[0,50). At block's hypothetical y=0, float is active →
        // block shrinks. (This is cycle 2 MVP — uses the block's
        // hypothetical-Y for the available-range query; the block
        // doesn't dynamically widen below the float's bottom. Cycle 3
        // will refine for blocks that span past the float bottom.)
        //
        // For a block AFTER another block that's past the float
        // bottom, the second block uses full width.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 50);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        // First block at y=0 — float still active.
        var blockAStyle = MakeStyle();
        SetLengthPx(blockAStyle, PropertyId.Height, 80);
        var blockA = Box.ForElement(BoxKind.BlockContainer, blockAStyle, MakeElement());
        root.AppendChild(blockA);

        // Second block at y=80 — float bottom is 50, so y=80 > float
        // bottom. Should use full width.
        var blockBStyle = MakeStyle();
        SetLengthPx(blockBStyle, PropertyId.Height, 50);
        var blockB = Box.ForElement(BoxKind.BlockContainer, blockBStyle, MakeElement());
        root.AppendChild(blockB);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // blockA flows around float (InlineOffset=100, InlineSize=500).
        Assert.Equal(100, sink.Fragments[1].InlineOffset);
        Assert.Equal(500, sink.Fragments[1].InlineSize);
        // blockB at y=80 (past float bottom=50) — full width.
        Assert.Equal(0, sink.Fragments[2].InlineOffset);
        Assert.Equal(600, sink.Fragments[2].InlineSize);
    }

    [Fact]
    public void Task8c2_block_with_left_and_right_floats_shrinks_from_both_sides()
    {
        // Left float (w=80, h=200), right float (w=60, h=200), block
        // → block at InlineOffset=80, InlineSize=460 (= 600 - 80 - 60).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var leftStyle = MakeStyle();
        SetKeyword(leftStyle, PropertyId.Float, 1);
        SetLengthPx(leftStyle, PropertyId.Width, 80);
        SetLengthPx(leftStyle, PropertyId.Height, 200);
        var leftFloat = Box.ForElement(BoxKind.BlockContainer, leftStyle, MakeElement());
        root.AppendChild(leftFloat);

        var rightStyle = MakeStyle();
        SetKeyword(rightStyle, PropertyId.Float, 2);
        SetLengthPx(rightStyle, PropertyId.Width, 60);
        SetLengthPx(rightStyle, PropertyId.Height, 200);
        var rightFloat = Box.ForElement(BoxKind.BlockContainer, rightStyle, MakeElement());
        root.AppendChild(rightFloat);

        var blockStyle = MakeStyle();
        SetLengthPx(blockStyle, PropertyId.Height, 50);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Same(block, sink.Fragments[2].Box);
        Assert.Equal(80, sink.Fragments[2].InlineOffset);
        Assert.Equal(460, sink.Fragments[2].InlineSize);
    }

    [Fact]
    public void Task8c2_trailing_float_defers_to_next_page_when_page_has_prior_content()
    {
        // Per CSS Fragmentation L3 §5 — a float that doesn't fit the
        // current fragmentainer moves to the top of the next one
        // (when the page already has prior content; if the float is
        // first, it forced-overflows with diagnostic per cycle 1).
        //
        // Tree: block (h=700), float (h=200, left). Page = 800.
        // Block fills 0-700; float would extend 700-900 > 800.
        // Page has emitted content → PageComplete with float as
        // resume target.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        var root = Box.CreateRoot(MakeStyle());

        var blockStyle = MakeStyle();
        SetLengthPx(blockStyle, PropertyId.Height, 700);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        root.AppendChild(block);

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 200);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        using var layouter = new BlockLayouter(
            root, sink, incomingContinuation: null, diagnostics: diagSink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Page complete with float as resume target.
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var cont = Assert.IsType<BlockContinuation>(result.Continuation);
        Assert.Equal(1, cont.ResumeAtChild);  // float index
        // Only the block emitted on this page; float deferred.
        Assert.Single(sink.Fragments);
        Assert.Same(block, sink.Fragments[0].Box);
        // No overflow diagnostic — the deferral handled it cleanly.
        Assert.Empty(diagSink.Diagnostics);
    }

    [Fact]
    public void Task8c2_float_on_empty_page_that_exceeds_page_size_still_emits_with_diagnostic()
    {
        // Float taller than ANY page (h=2000 on page=800), no prior
        // content. Cycle 2 deferral doesn't help (next page won't fit
        // it either); falls back to cycle 1 emit + diagnostic.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 2000);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        using var layouter = new BlockLayouter(
            root, sink, incomingContinuation: null, diagnostics: diagSink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Single(sink.Fragments);
        Assert.NotEmpty(diagSink.Diagnostics);
    }

    [Fact]
    public void Task8c2_float_smaller_than_remaining_space_does_not_defer()
    {
        // Block (h=500), float (h=200). Float at y=500, bottom=700 ≤
        // page=800 → fits, doesn't defer.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var blockStyle = MakeStyle();
        SetLengthPx(blockStyle, PropertyId.Height, 500);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        root.AppendChild(block);

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 200);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Both block + float emitted on the same page.
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Equal(2, sink.Fragments.Count);
    }

    [Fact]
    public void Task8c2_float_with_margins_correctly_uses_margin_box_for_overflow_check()
    {
        // Float h=600, marginTop=100, marginBottom=150 → margin-box
        // height = 850 > page=800 → would overflow even on fresh page.
        // With prior content, defers; without, emits + diagnostic.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        var root = Box.CreateRoot(MakeStyle());

        // Prior block to make page non-empty.
        var blockStyle = MakeStyle();
        SetLengthPx(blockStyle, PropertyId.Height, 50);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        root.AppendChild(block);

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 600);
        SetLengthPx(floatStyle, PropertyId.MarginTop, 100);
        SetLengthPx(floatStyle, PropertyId.MarginBottom, 150);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        using var layouter = new BlockLayouter(
            root, sink, incomingContinuation: null, diagnostics: diagSink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Float would overflow when added: 50 (block) + 850 (float
        // margin-box) = 900 > 800. Page has prior content → defer.
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.Single(sink.Fragments);  // block only; float deferred
    }

    // ====================================================================
    //  Phase 3 Task 8 cycle 2 post-PR-31 review tests (7 required)
    // ====================================================================

    [Fact]
    public void PostPr31_clear_left_block_after_left_float_uses_full_width()
    {
        // Per cycle 2 post-PR-31 review (P1 #1) — pre-fix the
        // GetAvailableInlineRange query happened at the cursor
        // position BEFORE clearance/topShift was applied. A
        // clear:left block after a left float was correctly moved
        // BELOW the float vertically but the range query saw the
        // float as active at the cursor → block got reduced
        // inline-size.
        //
        // Tree: float (h=100, float:left), block (clear:left, h=50).
        // Page = 600.
        // Expected: block at y=100 (cleared past float), x=0 (FULL
        // width past float bottom), width=600.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 100);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        var blockStyle = MakeStyle();
        SetKeyword(blockStyle, PropertyId.Clear, 1);  // left
        SetLengthPx(blockStyle, PropertyId.Height, 50);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(2, sink.Fragments.Count);
        Assert.Same(block, sink.Fragments[1].Box);
        // Block clears past float → y=100.
        Assert.Equal(100, sink.Fragments[1].BlockOffset);
        // Block uses FULL width (NOT 500) — past float bottom.
        Assert.Equal(0, sink.Fragments[1].InlineOffset);
        Assert.Equal(600, sink.Fragments[1].InlineSize);
    }

    [Fact]
    public void PostPr31_marginTop_pushes_block_below_float_so_block_uses_full_width()
    {
        // Per cycle 2 post-PR-31 review (P1 #2 + Copilot #2) — pre-fix
        // queried GetAvailableInlineRange at the cursor (= 0) instead
        // of the post-margin border-box top, so a block whose
        // marginTop pushed it past the float bottom still got reduced
        // width as if it were beside the float.
        //
        // Tree: float (h=80, float:left), block (h=50, marginTop=120).
        // Page = 600. Block's hypothetical-top = cursor + marginTop =
        // 0 + 120 = 120 > float bottom = 80. Block should use full
        // width (NOT shrink past the float).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 80);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        var blockStyle = MakeStyle();
        SetLengthPx(blockStyle, PropertyId.MarginTop, 120);
        SetLengthPx(blockStyle, PropertyId.Height, 50);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Same(block, sink.Fragments[1].Box);
        // Block at y=120 (= 0 + marginTop), full width.
        Assert.Equal(120, sink.Fragments[1].BlockOffset);
        Assert.Equal(0, sink.Fragments[1].InlineOffset);
        Assert.Equal(600, sink.Fragments[1].InlineSize);
    }

    [Fact]
    public void PostPr31_same_side_stacked_floats_overflow_defers_to_next_page()
    {
        // Per cycle 2 post-PR-31 review (P1 #3 + Copilot #1) — pre-fix
        // WouldFloatOverflow used currentBlockY directly instead of
        // PlaceFloat's stacking rule. A second same-side float that
        // would stack BELOW the cursor would emit + diagnose instead
        // of deferring.
        //
        // Tree: float1 (h=500, float:left), float2 (h=400, float:left).
        // Page = 800. Float1 at y=0, bottom=500. Float2 should stack
        // at y=500, bottom=900 > 800 → defer.
        // Pre-fix: float2's WouldFloatOverflow saw 0 + 400 = 400 < 800 →
        //   no defer → emit + diagnose.
        // Post-fix: peek sees stacked y=500, 500 + 400 = 900 > 800 →
        //   defer (page has prior content from float1).
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        var root = Box.CreateRoot(MakeStyle());

        var float1Style = MakeStyle();
        SetKeyword(float1Style, PropertyId.Float, 1);
        SetLengthPx(float1Style, PropertyId.Width, 100);
        SetLengthPx(float1Style, PropertyId.Height, 500);
        var float1 = Box.ForElement(BoxKind.BlockContainer, float1Style, MakeElement());
        root.AppendChild(float1);

        var float2Style = MakeStyle();
        SetKeyword(float2Style, PropertyId.Float, 1);
        SetLengthPx(float2Style, PropertyId.Width, 100);
        SetLengthPx(float2Style, PropertyId.Height, 400);
        var float2 = Box.ForElement(BoxKind.BlockContainer, float2Style, MakeElement());
        root.AppendChild(float2);

        using var layouter = new BlockLayouter(
            root, sink, incomingContinuation: null, diagnostics: diagSink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Page complete, float2 deferred (resume at index 1).
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var cont = Assert.IsType<BlockContinuation>(result.Continuation);
        Assert.Equal(1, cont.ResumeAtChild);
        // Only float1 emitted; float2 deferred.
        Assert.Single(sink.Fragments);
        Assert.Same(float1, sink.Fragments[0].Box);
        // No diagnostic — deferral handled it cleanly.
        Assert.Empty(diagSink.Diagnostics);
    }

    [Fact]
    public void PostPr31_initial_used_block_size_without_layouter_emissions_still_defers()
    {
        // Per cycle 2 post-PR-31 review (P1 #4) — pre-fix used
        // emittedThisAttempt > 0 to gate deferral. If the
        // fragmentainer had prior content from a header reservation
        // (UsedBlockSize > 0) but THIS layouter hadn't emitted yet,
        // a too-tall float would force-overflow instead of deferring.
        // Post-fix uses fragmentainer.UsedBlockSize > 0.
        //
        // Tree: float (h=750). Page = 800. Layouter starts with
        // UsedBlockSize=100 (simulated header reservation).
        // Pre-fix: emittedThisAttempt=0 → no defer → emit + diagnose.
        // Post-fix: UsedBlockSize=100 > 0 + would overflow (100+750=
        //   850 > 800) → defer.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 750);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        using var layouter = new BlockLayouter(
            root, sink, incomingContinuation: null, diagnostics: diagSink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        ctx.UsedBlockSize = 100;  // simulate header reservation
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Float deferred; PageComplete with float as resume.
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.Empty(sink.Fragments);  // float not emitted on this page
        // No diagnostic — deferral handled it.
        Assert.Empty(diagSink.Diagnostics);
    }

    [Fact]
    public void PostPr31_nested_float_overflow_emits_diagnostic()
    {
        // Per cycle 2 post-PR-31 review (P1 #5) — nested floats now
        // emit PAGINATION-FORCED-OVERFLOW-001 when their margin-box
        // bottom exceeds the fragmentainer (cycle 2 MVP: diagnostic
        // only — recursive deferral requires a future cycle's
        // continuation-token refactor).
        //
        // Tree: outer-div > [block (h=700), nested-float (h=200,
        //   float:left)]. Page = 800. Nested float at y=700, bottom=
        //   900 > 800 → diagnostic fires.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        var root = Box.CreateRoot(MakeStyle());

        var outerStyle = MakeStyle();
        SetLengthPx(outerStyle, PropertyId.Height, 1000);
        var outer = Box.ForElement(BoxKind.BlockContainer, outerStyle, MakeElement());
        root.AppendChild(outer);

        var blockStyle = MakeStyle();
        SetLengthPx(blockStyle, PropertyId.Height, 700);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        outer.AppendChild(block);

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 200);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        outer.AppendChild(floatBox);

        using var layouter = new BlockLayouter(
            root, sink, incomingContinuation: null, diagnostics: diagSink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Nested-float overflow diagnostic emitted.
        Assert.NotEmpty(diagSink.Diagnostics);
        Assert.Contains(diagSink.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.PaginationForcedOverflow001
            && d.Message.Contains("nested float overflow", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PostPr31_nested_clear_with_marginTop_does_not_add_marginTop_below_float_bottom()
    {
        // Per cycle 2 post-PR-31 review (P1 #6) — nested clear had
        // the same over-applied-marginTop bug as outer clear (fixed
        // in PR #30). Pre-fix: childCursor advanced to clearedBfcY
        // immediately, then marginTop was applied → border-box top
        // at floatBottom + marginTop. Post-fix: clearance folds into
        // topShift; border-box top = max(hypothetical, floatBottom).
        //
        // Tree: outer-div > [float (h=80, float:left), block
        //   (clear:left, marginTop=20, h=50)].
        // Pre-fix: block at y=80 + 20 = 100. Post-fix: block at y=80
        //   (= float bottom; marginTop=20 < 80, so floatBottom wins).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var outerStyle = MakeStyle();
        SetLengthPx(outerStyle, PropertyId.Height, 200);
        var outer = Box.ForElement(BoxKind.BlockContainer, outerStyle, MakeElement());
        root.AppendChild(outer);

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetLengthPx(floatStyle, PropertyId.Height, 80);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        outer.AppendChild(floatBox);

        var blockStyle = MakeStyle();
        SetKeyword(blockStyle, PropertyId.Clear, 1);  // left
        SetLengthPx(blockStyle, PropertyId.MarginTop, 20);
        SetLengthPx(blockStyle, PropertyId.Height, 50);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        outer.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // 3 fragments: outer, float, block.
        Assert.Equal(3, sink.Fragments.Count);
        Assert.Same(block, sink.Fragments[2].Box);
        // Block at y=80 (floatBottom dominates marginTop=20).
        Assert.Equal(80, sink.Fragments[2].BlockOffset);
    }

    [Fact]
    public void PostPr31_flow_around_with_float_border_padding_margins_uses_margin_box_footprint()
    {
        // Per cycle 2 post-PR-31 review (test #7) — verify the
        // float's MARGIN-BOX (not just content-box) is used for
        // GetAvailableInlineRange's footprint reporting. Float with
        // marginLeft=10, borderLeft=5, paddingLeft=15, content-width=80
        // → margin-box width = 10+5+15+80+15+5+10 = 140 (assuming
        // symmetric padding/border).
        //
        // Block flowing around it should start at x=140 (past float's
        // margin-box right edge), width = 600 - 140 = 460.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var floatStyle = MakeStyle();
        SetKeyword(floatStyle, PropertyId.Float, 1);
        SetLengthPx(floatStyle, PropertyId.Width, 80);
        SetLengthPx(floatStyle, PropertyId.Height, 200);
        SetLengthPx(floatStyle, PropertyId.MarginLeft, 10);
        SetLengthPx(floatStyle, PropertyId.MarginRight, 10);
        SolidBorders(floatStyle);
        SetLengthPx(floatStyle, PropertyId.BorderLeftWidth, 5);
        SetLengthPx(floatStyle, PropertyId.BorderRightWidth, 5);
        SetLengthPx(floatStyle, PropertyId.PaddingLeft, 15);
        SetLengthPx(floatStyle, PropertyId.PaddingRight, 15);
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        root.AppendChild(floatBox);

        var blockStyle = MakeStyle();
        SetLengthPx(blockStyle, PropertyId.Height, 50);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Same(block, sink.Fragments[1].Box);
        // Block starts at x=140 (= 10 + 5 + 15 + 80 + 15 + 5 + 10 =
        // float's margin-box width).
        Assert.Equal(140, sink.Fragments[1].InlineOffset);
        // Block width = 600 - 140 = 460.
        Assert.Equal(460, sink.Fragments[1].InlineSize);
    }

    // ====================================================================
    //  Phase 3 Task 7 cycle 2c post-PR-29 review tests
    // ====================================================================

    [Fact]
    public void PostPr29_emit_recursion_advances_cursor_by_subtree_extent_for_nested_grandchildren()
    {
        // Per cycle 2c post-PR-29 review #2 (P1) — pre-fix the
        // EmitBlockSubtreeRecursive cursor advanced by
        // childBorderBoxBlockSize while the OUTER loop (cycle 2c)
        // advanced by subtree-aware extent. So nested siblings AFTER
        // an overflowing nested grandchild would visually overlap the
        // overflow even though OUTER pagination correctly reserved space.
        //
        // Scenario from review #2:
        //   parent > [
        //     childA (h=200) > grandchild (h=350),
        //     childB (h=100)
        //   ]
        //
        // Pre-fix: childB inside parent's recursion at cursor 200 (=
        //   childA's borderBoxBlockSize), overlapping grandchild
        //   (which extends to 350 in childA's content area).
        // Post-fix: childB at cursor 350 (= max(200, grandchild's bottom
        //   in childA's coords)).
        //
        // Note: childA's BlockOffset = 0 (in parent's content area),
        // grandchild.BlockOffset = 0 (in childA's content area, which
        // is at parent.contentTop + 0 = 0). So grandchild's absolute
        // bottom = 350. childB's expected absolute BlockOffset = 350
        // (no longer 200).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var parentStyle = MakeStyle();
        SetLengthPx(parentStyle, PropertyId.Height, 500);  // big enough
        var parent = Box.ForElement(BoxKind.BlockContainer, parentStyle, MakeElement());
        root.AppendChild(parent);

        var childAStyle = MakeStyle();
        SetLengthPx(childAStyle, PropertyId.Height, 200);
        var childA = Box.ForElement(BoxKind.BlockContainer, childAStyle, MakeElement());
        parent.AppendChild(childA);

        var grandchildStyle = MakeStyle();
        SetLengthPx(grandchildStyle, PropertyId.Height, 350);
        var grandchild = Box.ForElement(BoxKind.BlockContainer, grandchildStyle, MakeElement());
        childA.AppendChild(grandchild);

        var childBStyle = MakeStyle();
        SetLengthPx(childBStyle, PropertyId.Height, 100);
        var childB = Box.ForElement(BoxKind.BlockContainer, childBStyle, MakeElement());
        parent.AppendChild(childB);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // 4 fragments: parent, childA, grandchild, childB.
        Assert.Equal(4, sink.Fragments.Count);
        Assert.Same(parent, sink.Fragments[0].Box);
        Assert.Same(childA, sink.Fragments[1].Box);
        Assert.Equal(0, sink.Fragments[1].BlockOffset);
        Assert.Same(grandchild, sink.Fragments[2].Box);
        Assert.Equal(0, sink.Fragments[2].BlockOffset);  // inside childA at offset 0
        Assert.Same(childB, sink.Fragments[3].Box);
        // childB's BlockOffset = parent.contentTop (0) + cursor +
        //   topShift. Pre-fix cursor = 200, post-fix = 350.
        Assert.Equal(350, sink.Fragments[3].BlockOffset);
    }

    [Fact]
    public void PostPr29_two_page_follow_through_for_break_before_then_forced_overflow()
    {
        // Per cycle 2c post-PR-29 review #6 (P2) — the original cycle
        // 2c test only verified page 1 of the break-before scenario.
        // This test follows through page 2: assert parent + child
        // both emit on page 2 + diagnostic fires + continuation
        // advances past the parent.
        //
        // Tree (same as Cycle2c_nested_overflow_pushes_subtree_to_next_page_when_prior_content_exists):
        //   root > [div_a (h=200), parent (h=600) > child (h=900)]
        // Page = 800.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        var root = Box.CreateRoot(MakeStyle());

        var divAStyle = MakeStyle();
        SetLengthPx(divAStyle, PropertyId.Height, 200);
        var divA = Box.ForElement(BoxKind.BlockContainer, divAStyle, MakeElement());
        root.AppendChild(divA);

        var parentStyle = MakeStyle();
        SetLengthPx(parentStyle, PropertyId.Height, 600);
        var parent = Box.ForElement(BoxKind.BlockContainer, parentStyle, MakeElement());
        root.AppendChild(parent);

        var childStyle = MakeStyle();
        SetLengthPx(childStyle, PropertyId.Height, 900);
        var child = Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement());
        parent.AppendChild(child);

        // Page 1.
        using var page1Layouter = new BlockLayouter(
            root, sink, incomingContinuation: null, diagnostics: diagSink);
        var ctx1 = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx1 = new LayoutContext(ctx1);
        using var resolver1 = new BreakResolver();

        var page1 = page1Layouter.AttemptLayout(
            ctx1, ref layoutCtx1, resolver1, LayoutAttemptStrategy.Strict);
        Assert.Equal(LayoutAttemptOutcome.PageComplete, page1.Outcome);
        var cont1 = Assert.IsType<BlockContinuation>(page1.Continuation);
        Assert.Equal(1, cont1.ResumeAtChild);  // resume at parent
        var sinkBeforePage2 = sink.Fragments.Count;
        Assert.Equal(1, sinkBeforePage2);  // div_a only on page 1

        // Page 2 — same root, new layouter with continuation.
        using var page2Layouter = new BlockLayouter(
            root, sink, incomingContinuation: cont1, diagnostics: diagSink);
        var ctx2 = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        ctx2.PageIndex = 1;
        var layoutCtx2 = new LayoutContext(ctx2);
        using var resolver2 = new BreakResolver();

        var page2 = page2Layouter.AttemptLayout(
            ctx2, ref layoutCtx2, resolver2, LayoutAttemptStrategy.Strict);

        // Page 2: parent + child emitted via forced-overflow path
        // (subtree extent 900 > page 800).
        Assert.Equal(LayoutAttemptOutcome.PageComplete, page2.Outcome);
        var cont2 = Assert.IsType<BlockContinuation>(page2.Continuation);
        // Continuation advances PAST parent (no more children).
        Assert.Equal(2, cont2.ResumeAtChild);
        // 3 fragments total: div_a (page 1), parent + child (page 2).
        Assert.Equal(3, sink.Fragments.Count);
        Assert.Same(parent, sink.Fragments[1].Box);
        Assert.Same(child, sink.Fragments[2].Box);
        // Diagnostic fired on page 2 (was silent on page 1's clean break).
        Assert.Single(diagSink.Diagnostics);
        Assert.Equal(PaginateDiagnosticCodes.PaginationForcedOverflow001,
            diagSink.Diagnostics[0].Code);
        // Per cycle 2c post-PR-29 review #9 — the diagnostic mentions
        // BOTH own border-box + subtree extent.
        Assert.Contains("subtree extent", diagSink.Diagnostics[0].Message,
            System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("own border-box", diagSink.Diagnostics[0].Message,
            System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostPr29_measure_includes_parent_padding_bottom_when_descendants_dominate()
    {
        // Per cycle 2c post-PR-29 review #5 (P2) — when descendants
        // overflow the parent's own height, the measured extent must
        // include the parent's padding-bottom + border-bottom (which
        // sit BELOW the deepest descendant in CSS box model). Pre-fix
        // the measure tracked only descendant border-box bottoms.
        //
        // Tree:
        //   root > parent (h=auto-as-0, padding-bottom=20, border-bottom=5)
        //          > child (h=300)
        //   sibling (h=100)
        //
        // Pre-fix subtree extent: max(parent.borderBox=25, descendantBottom
        //   = 0+300=300) = 300. Sibling at offset 300.
        // Post-fix: descendant dominates → append parent's tail (20+5=25)
        //   → subtree extent = 325. Sibling at offset 325.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var parentStyle = MakeStyle();
        // height=auto (= 0); add padding-bottom + border-bottom.
        SetLengthPx(parentStyle, PropertyId.PaddingBottom, 20);
        SolidBorders(parentStyle);
        SetLengthPx(parentStyle, PropertyId.BorderBottomWidth, 5);
        var parent = Box.ForElement(BoxKind.BlockContainer, parentStyle, MakeElement());
        root.AppendChild(parent);

        var childStyle = MakeStyle();
        SetLengthPx(childStyle, PropertyId.Height, 300);
        var child = Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement());
        parent.AppendChild(child);

        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 100);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(3, sink.Fragments.Count);
        // sibling at offset 325 (post-fix), not 300 (pre-fix).
        Assert.Same(sibling, sink.Fragments[2].Box);
        Assert.Equal(325, sink.Fragments[2].BlockOffset);
    }

    [Fact]
    public void PostPr29_measure_includes_final_child_margin_bottom_when_descendants_dominate()
    {
        // Per cycle 2c post-PR-29 review #5 (P2) — sibling extension:
        // the LAST child's margin-bottom also sits below its border-box
        // bottom in the parent's box model. When descendants dominate,
        // the measured extent must include it.
        //
        // Tree:
        //   root > parent (h=auto-as-0, no padding/border) >
        //          [c1 (h=200, marginBottom=30)]
        //   sibling (h=100)
        //
        // Pre-fix subtree extent: max(0, 0+200) = 200. Sibling at 200.
        // Post-fix: descendant dominates (200 > 0) → append last-child
        //   marginEnd (30) + parent tail (0+0=0) = 230. Sibling at 230.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var parentStyle = MakeStyle();
        var parent = Box.ForElement(BoxKind.BlockContainer, parentStyle, MakeElement());
        root.AppendChild(parent);

        var c1Style = MakeStyle();
        SetLengthPx(c1Style, PropertyId.Height, 200);
        SetLengthPx(c1Style, PropertyId.MarginBottom, 30);
        var c1 = Box.ForElement(BoxKind.BlockContainer, c1Style, MakeElement());
        parent.AppendChild(c1);

        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 100);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(3, sink.Fragments.Count);
        Assert.Same(sibling, sink.Fragments[2].Box);
        Assert.Equal(230, sink.Fragments[2].BlockOffset);
    }

    [Fact]
    public void PostPr29_negative_margin_subtree_overlap_measured_consistently_with_emit()
    {
        // Per cycle 2c post-PR-29 review #11 (P3) — measure + emit
        // must agree on subtree extent under negative margins.
        //
        // Tree:
        //   root > parent (h=400) > [
        //     c1 (h=80, marginBottom=-30),
        //     c2 (h=60)
        //   ]
        //   sibling (h=50)
        //
        // Inside parent (cycle 2b post-PR-28 — signed cursor):
        //   c1 at offset 0, height 80; cursor after c1 = 0+0+80+(-30) = 50
        //   c2 at offset 50 (in parent.contentArea), height 60; cursor = 50+60 = 110
        // Subtree extent in parent: max(80, 110) = 110.
        //
        // Parent's own borderBox = 400. Subtree extent in parent =
        //   max(parent.borderBox=400, deepestBottom-from-children=110) =
        //   400 (parent's own height dominates).
        //
        // So sibling at offset = parent.borderBox = 400 (parent's own
        // size dominates, no overflow).
        //
        // The KEY invariant is: emit places c1 at 0, c2 at 50 (overlap
        // visible), and the measure agrees that the subtree extent is
        // bounded by parent's own height (no overflow past parent).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var parentStyle = MakeStyle();
        SetLengthPx(parentStyle, PropertyId.Height, 400);
        var parent = Box.ForElement(BoxKind.BlockContainer, parentStyle, MakeElement());
        root.AppendChild(parent);

        var c1Style = MakeStyle();
        SetLengthPx(c1Style, PropertyId.Height, 80);
        SetLengthPx(c1Style, PropertyId.MarginBottom, -30);
        var c1 = Box.ForElement(BoxKind.BlockContainer, c1Style, MakeElement());
        parent.AppendChild(c1);

        var c2Style = MakeStyle();
        SetLengthPx(c2Style, PropertyId.Height, 60);
        var c2 = Box.ForElement(BoxKind.BlockContainer, c2Style, MakeElement());
        parent.AppendChild(c2);

        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 50);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(4, sink.Fragments.Count);
        // c1 inside parent at 0.
        Assert.Same(c1, sink.Fragments[1].Box);
        Assert.Equal(0, sink.Fragments[1].BlockOffset);
        // c2 at 50 (signed cursor allowed negative-margin overlap with c1).
        Assert.Same(c2, sink.Fragments[2].Box);
        Assert.Equal(50, sink.Fragments[2].BlockOffset);
        // Sibling at 400 (parent's own border-box dominates the subtree
        // extent — c1+c2 stack ends at 110 < parent.height=400, so no
        // overflow past parent).
        Assert.Same(sibling, sink.Fragments[3].Box);
        Assert.Equal(400, sink.Fragments[3].BlockOffset);
    }

    // ====================================================================
    //  Phase 3 Task 7 cycle 2d deferral pin — true mid-subtree splits
    // ====================================================================

    [Fact]
    public void Cycle2d_oversized_subtree_splits_across_two_pages_at_inner_break()
    {
        // Phase 3 multi-page driver, cycle 1 — nested-container fragmentation
        // (docs/design/multi-page-driver.md §4.1). This was the failing-skip
        // pin for "true mid-subtree pagination splits"; cycle 1 implements it
        // via break consultation INSIDE EmitBlockSubtreeRecursive returning a
        // chained BlockContinuation (carried in the existing LayouterState
        // slot — the proposed dedicated NestedContinuation field was
        // unnecessary; LayouterState is already the nested-resume slot).
        //
        // Tree: root > parent(height=auto) > [child1 (h=400), child2 (h=500)]
        // Page = 800. Subtree extent = 0 + 400 + 500 = 900 > 800.
        //   child1 fits (bottom 400 <= 800) → page 1; child2 (bottom 900)
        //   overflows → break BEFORE it → resume INSIDE the parent at child2
        //   on page 2. The parent fragment repeats on each page it spans
        //   (full-size; per-page "partial" sizing is a later refinement).
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        var root = Box.CreateRoot(MakeStyle());
        var parent = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        root.AppendChild(parent);
        var child1Style = MakeStyle();
        SetLengthPx(child1Style, PropertyId.Height, 400);
        var child1 = Box.ForElement(BoxKind.BlockContainer, child1Style, MakeElement());
        parent.AppendChild(child1);
        var child2Style = MakeStyle();
        SetLengthPx(child2Style, PropertyId.Height, 500);
        var child2 = Box.ForElement(BoxKind.BlockContainer, child2Style, MakeElement());
        parent.AppendChild(child2);

        // Page 1.
        using var page1Layouter = new BlockLayouter(
            root, sink, incomingContinuation: null, diagnostics: diagSink);
        var ctx1 = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx1 = new LayoutContext(ctx1);
        using var resolver1 = new BreakResolver();
        var page1 = page1Layouter.AttemptLayout(
            ctx1, ref layoutCtx1, resolver1, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, page1.Outcome);
        // The continuation is a CHAIN: resume at the parent (root child 0),
        // and INSIDE the parent at child2 (parent child 1).
        var outer = Assert.IsType<BlockContinuation>(page1.Continuation);
        Assert.Equal(0, outer.ResumeAtChild);   // the parent
        var inner = Assert.IsType<BlockContinuation>(outer.LayouterState);
        Assert.Equal(1, inner.ResumeAtChild);    // child2, INSIDE the parent
        // Page 1 emitted the parent + child1 only; child2 broke to page 2.
        Assert.Contains(sink.Fragments, f => ReferenceEquals(f.Box, child1));
        Assert.DoesNotContain(sink.Fragments, f => ReferenceEquals(f.Box, child2));

        // Page 2 — resume with the chained continuation.
        var sink2 = new RecordingFragmentSink();
        using var page2Layouter = new BlockLayouter(
            root, sink2, incomingContinuation: outer, diagnostics: diagSink);
        var ctx2 = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        ctx2.PageIndex = 1;
        var layoutCtx2 = new LayoutContext(ctx2);
        using var resolver2 = new BreakResolver();
        page2Layouter.AttemptLayout(ctx2, ref layoutCtx2, resolver2, LayoutAttemptStrategy.Strict);

        // child2 emitted on page 2; child1 NOT re-emitted (no duplication).
        Assert.Contains(sink2.Fragments, f => ReferenceEquals(f.Box, child2));
        Assert.DoesNotContain(sink2.Fragments, f => ReferenceEquals(f.Box, child1));
    }

    [Fact]
    public void MultiPageDriver_cycle1_nested_wrapper_children_split_across_pages_at_child_boundaries()
    {
        // Phase 3 multi-page driver, cycle 1 — nested-container fragmentation
        // (docs/design/multi-page-driver.md §4.1). This WAS the blocker:
        // `EmitBlockSubtreeRecursive` never consulted the resolver, so a
        // nested container laid out ALL its children on the current page —
        // content nested under `html > body` never paginated. Cycle 1 breaks
        // at child boundaries INSIDE the subtree.
        //
        //   root > wrapper(auto height) > [6 × (h=200)]   on a 500px page.
        //   Two children fit per page (400 <= 500; the 3rd's bottom 600
        //   overflows), so the wrapper's children split 2 / 2 / 2 across
        //   three pages. The wrapper fragment repeats on each page it spans
        //   (per-page wrapper sizing is a later refinement).
        var root = Box.CreateRoot(MakeStyle());
        var wrapper = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        root.AppendChild(wrapper);
        var children = new List<Box>();
        for (var i = 0; i < 6; i++)
        {
            var childStyle = MakeStyle();
            SetLengthPx(childStyle, PropertyId.Height, 200);
            var c = Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement());
            children.Add(c);
            wrapper.AppendChild(c);
        }

        var pages = DriveMultiPageLoop(root, children, blockSize: 500, pageCap: 8);

        // Core pagination invariant: every child appears exactly once, in
        // document order — no child lost or duplicated across the split.
        Assert.Equal(children, pages.SelectMany(p => p.Kids).ToList());

        // The expected 2 / 2 / 2 split across three CONTENT pages.
        var contentPages = pages.Where(p => p.Kids.Count > 0).ToList();
        Assert.Equal(3, contentPages.Count);
        Assert.Equal(new[] { children[0], children[1] }, contentPages[0].Kids);
        Assert.Equal(new[] { children[2], children[3] }, contentPages[1].Kids);
        Assert.Equal(new[] { children[4], children[5] }, contentPages[2].Kids);

        // PR #175 review P2 — prove TERMINATION, not just content uniqueness.
        // The run ends cleanly (final AllDone, no continuation) and emits AT
        // MOST one trailing empty resume page (the documented forced-overflow
        // artifact the cycle-3 driver skips). A regression that kept returning
        // PageComplete with empty fragments would fail here, not slip through.
        Assert.True(pages.Count <= contentPages.Count + 1,
            $"expected at most one trailing empty page; got {pages.Count} pages "
            + $"for {contentPages.Count} content pages");
        Assert.Equal(LayoutAttemptOutcome.AllDone, pages[^1].Outcome);
        Assert.True(pages[^1].ContinuationIsNull);
    }

    /// <summary>Drives the manual multi-page loop (the pipeline driver is
    /// cycle 3): one fresh sink + fragmentainer per page, resuming via the
    /// returned continuation, until the layouter stops. Returns, per page, the
    /// subset of <paramref name="tracked"/> boxes emitted + the attempt
    /// outcome + whether the continuation was null — so tests can assert the
    /// exact page sequence AND termination (PR #175 review P2).</summary>
    private static List<(List<Box> Kids, LayoutAttemptOutcome Outcome, bool ContinuationIsNull)>
        DriveMultiPageLoop(Box root, IReadOnlyList<Box> tracked, int blockSize, int pageCap)
    {
        LayoutContinuation? continuation = null;
        var pages = new List<(List<Box>, LayoutAttemptOutcome, bool)>();
        for (var pageIndex = 0; pageIndex < pageCap; pageIndex++)
        {
            var sink = new RecordingFragmentSink();
            using var layouter = new BlockLayouter(root, sink, continuation);
            var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: blockSize);
            ctx.PageIndex = pageIndex;
            var layoutCtx = new LayoutContext(ctx);
            using var resolver = new BreakResolver();

            var result = layouter.AttemptLayout(
                ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);
            pages.Add((
                sink.Fragments.Select(f => f.Box).Where(tracked.Contains).ToList(),
                result.Outcome,
                result.Continuation is null));

            if (result.Outcome != LayoutAttemptOutcome.PageComplete || result.Continuation is null)
            {
                break;
            }
            continuation = result.Continuation;
        }

        return pages;
    }

    /// <summary>Drives the manual multi-page loop for a paginating flex container, capturing per page
    /// the tracked items emitted (in EMISSION order) WITH their page-relative <c>BlockOffset</c> — so a
    /// column-reverse test can assert visual reverse order, top re-anchoring, no loss/duplication, and
    /// clean termination across pages (post-PR-#183 review P3).</summary>
    private static List<(List<(Box Box, double BlockOffset)> Items, LayoutAttemptOutcome Outcome, bool ContinuationIsNull)>
        DriveFlexColumnPages(Box root, IReadOnlyList<Box> tracked, int blockSize, int pageCap)
    {
        LayoutContinuation? continuation = null;
        var pages = new List<(List<(Box, double)>, LayoutAttemptOutcome, bool)>();
        for (var pageIndex = 0; pageIndex < pageCap; pageIndex++)
        {
            var sink = new RecordingFragmentSink();
            using var layouter = new BlockLayouter(root, sink, continuation);
            var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: blockSize);
            ctx.PageIndex = pageIndex;
            var layoutCtx = new LayoutContext(ctx);
            using var resolver = new BreakResolver();

            var result = layouter.AttemptLayout(
                ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);
            var items = sink.Fragments
                .Where(f => tracked.Contains(f.Box))
                .Select(f => (f.Box, f.BlockOffset))
                .ToList();
            pages.Add((items, result.Outcome, result.Continuation is null));

            if (result.Outcome != LayoutAttemptOutcome.PageComplete || result.Continuation is null)
            {
                break;
            }
            continuation = result.Continuation;
        }

        return pages;
    }

    [Fact]
    public void Backlog4_column_reverse_flex_paginates_in_visual_reverse_order_without_loss()
    {
        // Post-PR-#183 review P3 — column-reverse flex pagination (backlog #4) emits items in VISUAL
        // (reverse-DOM) order, RE-ANCHORED at each page's top, with no loss / duplication and correct
        // continuation indexes across THREE pages. Six items with VARIED heights (all in [110, 140]) on
        // a 300px page: ANY two fit (sum ≤ 280 ≤ 300), ANY three overflow (sum ≥ 330 > 300), so exactly
        // two items emit per page — [item5,item4] / [item3,item2] / [item1,item0] (reverse DOM). The
        // strict reverse-DOM concatenation is the proof the per-page continuation index re-anchors
        // correctly (a wrong index would lose, duplicate, or reorder an item).
        var root = Box.CreateRoot(MakeStyle());
        var flex = Box.ForElement(BoxKind.FlexContainer, MakeStyle(), MakeElement());
        SetKeyword(flex.Style, PropertyId.FlexDirection, 3);   // column-reverse
        root.AppendChild(flex);
        var heights = new[] { 120.0, 140.0, 110.0, 130.0, 115.0, 135.0 };
        var items = new List<Box>();
        for (var i = 0; i < heights.Length; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, heights[i]);
            var c = Box.ForElement(BoxKind.BlockContainer, s, MakeElement());
            items.Add(c);
            flex.AppendChild(c);
        }

        var pages = DriveFlexColumnPages(root, items, blockSize: 300, pageCap: 10);
        var contentPages = pages.Where(p => p.Items.Count > 0).ToList();

        // Visual reverse-DOM order across ALL pages — no item lost, none duplicated, none reordered.
        var emitted = contentPages.SelectMany(p => p.Items.Select(it => it.Box)).ToList();
        Assert.Equal(
            new[] { items[5], items[4], items[3], items[2], items[1], items[0] }, emitted);

        // The heights force a clean 2 / 2 / 2 split over three content pages.
        Assert.Equal(3, contentPages.Count);
        Assert.All(contentPages, p => Assert.Equal(2, p.Items.Count));

        // Top re-anchoring — each page's FIRST item sits at the page content origin (no margin/border/
        // padding here) and items stack DOWNWARD, NOT bottom-packed (the non-paginating flip).
        foreach (var p in contentPages)
        {
            Assert.InRange(p.Items[0].BlockOffset, 0.0, 1.0);
            Assert.True(p.Items[1].BlockOffset > p.Items[0].BlockOffset,
                "items must stack top-to-bottom on each page (re-anchored, not bottom-packed)");
        }

        // Clean termination — final AllDone, no dangling continuation.
        Assert.Equal(LayoutAttemptOutcome.AllDone, pages[^1].Outcome);
        Assert.True(pages[^1].ContinuationIsNull);
    }

    [Fact]
    public void Backlog4_column_reverse_flex_pagination_honors_order_then_reverses()
    {
        // Post-PR-#183 review P3 — the `order` variant. `order` reorders the flex items FIRST (CSS
        // Flexbox L1 §5.4, stable by DOM for ties), THEN column-reverse reverses THAT sequence for
        // VISUAL order. DOM [A,B,C,D] with orders [2,1,2,0] → flex order [D,B,A,C]; column-reverse
        // visual order = [C,A,B,D]. Four 140px items on a 300px page split 2 / 2 across two pages
        // ([C,A] then [B,D]) with no loss / duplication.
        var root = Box.CreateRoot(MakeStyle());
        var flex = Box.ForElement(BoxKind.FlexContainer, MakeStyle(), MakeElement());
        SetKeyword(flex.Style, PropertyId.FlexDirection, 3);   // column-reverse
        root.AppendChild(flex);
        var orders = new[] { 2, 1, 2, 0 };   // A, B, C, D
        var items = new List<Box>();
        for (var i = 0; i < orders.Length; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 140);
            s.Set(PropertyId.Order, ComputedSlot.FromInteger(orders[i]));
            var c = Box.ForElement(BoxKind.BlockContainer, s, MakeElement());
            items.Add(c);
            flex.AppendChild(c);
        }
        var (a, b, c2, d) = (items[0], items[1], items[2], items[3]);

        var pages = DriveFlexColumnPages(root, items, blockSize: 300, pageCap: 10);
        var contentPages = pages.Where(p => p.Items.Count > 0).ToList();

        // flex order [D,B,A,C] reversed for column-reverse VISUAL order = [C,A,B,D].
        var emitted = contentPages.SelectMany(p => p.Items.Select(it => it.Box)).ToList();
        Assert.Equal(new[] { c2, a, b, d }, emitted);
        Assert.Equal(2, contentPages.Count);                 // 2 / 2 across two pages
        Assert.All(contentPages, p => Assert.Equal(2, p.Items.Count));
        Assert.Equal(LayoutAttemptOutcome.AllDone, pages[^1].Outcome);
        Assert.True(pages[^1].ContinuationIsNull);
    }

    [Fact]
    public void MultiPageDriver_cycle1_nested_fragmentation_works_at_arbitrary_depth()
    {
        // Phase 3 multi-page driver, cycle 1 — the break consultation lives
        // INSIDE EmitBlockSubtreeRecursive, whose self-call threads the
        // resolver + fragmentainer down and whose caller chains the returned
        // BlockContinuation. So a TWO-level nest paginates with no extra
        // work (the planned "arbitrary depth" follow-up comes for free).
        //   root > outer > inner > [4 × (h=200)]   on a 500px page.
        //   Two children fit per page → split 2 / 2 across two content pages.
        var root = Box.CreateRoot(MakeStyle());
        var outer = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        root.AppendChild(outer);
        var inner = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        outer.AppendChild(inner);
        var children = new List<Box>();
        for (var i = 0; i < 4; i++)
        {
            var cs = MakeStyle();
            SetLengthPx(cs, PropertyId.Height, 200);
            var c = Box.ForElement(BoxKind.BlockContainer, cs, MakeElement());
            children.Add(c);
            inner.AppendChild(c);
        }

        var pages = DriveMultiPageLoop(root, children, blockSize: 500, pageCap: 8);

        Assert.Equal(children, pages.SelectMany(p => p.Kids).ToList());
        var contentPages = pages.Where(p => p.Kids.Count > 0).ToList();
        Assert.Equal(2, contentPages.Count);
        Assert.Equal(new[] { children[0], children[1] }, contentPages[0].Kids);
        Assert.Equal(new[] { children[2], children[3] }, contentPages[1].Kids);
        // PR #175 review P2 — terminates cleanly, at most one trailing empty page.
        Assert.True(pages.Count <= contentPages.Count + 1);
        Assert.Equal(LayoutAttemptOutcome.AllDone, pages[^1].Outcome);
        Assert.True(pages[^1].ContinuationIsNull);
    }

    [Fact]
    public void MultiPageDriver_cycle1_forward_progress_counts_a_prior_float_not_childCursor()
    {
        // PR #175 review P1 — the forward-progress guard must be "a child's
        // fragment was actually emitted at this level", NOT the signed
        // `childCursor`. A nested FLOAT emits a fragment but does NOT advance
        // childCursor, so a `childCursor > 0` guard would wrongly suppress the
        // break before an overflowing block child that follows the float,
        // collapsing nested pagination back to forced overflow.
        //
        //   root > wrapper > [ float(h=120), block(h=480) ]   on a 300px page.
        //   The float emits (sink advances, childCursor stays 0); the 480px
        //   block can't share the page (a clean fit needs the float's 120 +
        //   480 = 600 > 300), so it must BREAK to page 2 — not force-overflow
        //   on page 1.
        var root = Box.CreateRoot(MakeStyle());
        var wrapper = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        root.AppendChild(wrapper);

        var floatStyle = MakeStyle();
        SetLengthPx(floatStyle, PropertyId.Height, 120);
        SetLengthPx(floatStyle, PropertyId.Width, 100);
        SetKeyword(floatStyle, PropertyId.Float, 1);   // float: left
        var floatBox = Box.ForElement(BoxKind.BlockContainer, floatStyle, MakeElement());
        wrapper.AppendChild(floatBox);

        var blockStyle = MakeStyle();
        SetLengthPx(blockStyle, PropertyId.Height, 480);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        wrapper.AppendChild(block);

        var pages = DriveMultiPageLoop(root, new[] { block }, blockSize: 300, pageCap: 8);

        // The block paginated to a later page (it did not force-overflow onto
        // page 0 alongside the float) — i.e. there IS more than one page and
        // page 0 did not contain the block.
        Assert.True(pages.Count >= 2, "the float-then-tall-block subtree must paginate");
        Assert.DoesNotContain(block, pages[0].Kids);
        Assert.Contains(pages.SelectMany(p => p.Kids), b => ReferenceEquals(b, block));
    }

    [Fact]
    public void MultiPageDriver_cycle1_forward_progress_survives_a_negative_margin_prior_child()
    {
        // PR #175 review P1 — a prior block with NEGATIVE margins can leave
        // the signed `childCursor` <= 0 even though real content was emitted;
        // a `childCursor > 0` guard would then suppress the break before the
        // next overflowing child. The fragment-count guard is immune.
        //
        //   root > wrapper > [ a(h=100, margin-bottom:-120), b(h=350) ]
        //   on a 300px page. After `a` the signed cursor = 100 - 120 = -20,
        //   yet `a` emitted a fragment; `b` (350 > the 300px page) must BREAK
        //   to page 2 — a `childCursor > 0` guard would suppress it.
        var root = Box.CreateRoot(MakeStyle());
        var wrapper = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        root.AppendChild(wrapper);

        var aStyle = MakeStyle();
        SetLengthPx(aStyle, PropertyId.Height, 100);
        SetLengthPx(aStyle, PropertyId.MarginBottom, -120);
        var a = Box.ForElement(BoxKind.BlockContainer, aStyle, MakeElement());
        wrapper.AppendChild(a);

        var bStyle = MakeStyle();
        SetLengthPx(bStyle, PropertyId.Height, 350);
        var b = Box.ForElement(BoxKind.BlockContainer, bStyle, MakeElement());
        wrapper.AppendChild(b);

        var pages = DriveMultiPageLoop(root, new[] { a, b }, blockSize: 300, pageCap: 8);

        // Both emitted, in order, each exactly once — and it paginated (b did
        // not get forced onto page 0 because the negative-margin cursor read
        // <= 0). `a` is on page 0; `b` on a later page.
        Assert.Equal(new[] { a, b }, pages.SelectMany(p => p.Kids).ToList());
        Assert.True(pages.Count >= 2, "the negative-margin subtree must paginate");
        Assert.Contains(a, pages[0].Kids);
        Assert.DoesNotContain(b, pages[0].Kids);
    }

    [Fact]
    public void MultiPageDriver_cycle1_suppress_block_pagination_does_not_break_nested()
    {
        // PR #175 review P3 — when the fragmentainer disables pagination
        // (position: fixed content, Task 20), the nested break must NOT fire;
        // the content overflows at its natural position instead.
        //   root > wrapper > [3 × (h=200)] = 600px on a 500px page, BUT the
        //   fragmentainer suppresses pagination → all three emit on one page.
        var root = Box.CreateRoot(MakeStyle());
        var wrapper = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        root.AppendChild(wrapper);
        var children = new List<Box>();
        for (var i = 0; i < 3; i++)
        {
            var cs = MakeStyle();
            SetLengthPx(cs, PropertyId.Height, 200);
            var c = Box.ForElement(BoxKind.BlockContainer, cs, MakeElement());
            children.Add(c);
            wrapper.AppendChild(c);
        }

        var sink = new RecordingFragmentSink();
        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 500)
        {
            SuppressBlockPagination = true,
        };
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // No break: all three children emit on this page (overflowing), and
        // there is no continuation to a second page.
        foreach (var c in children)
        {
            Assert.Contains(sink.Fragments, f => ReferenceEquals(f.Box, c));
        }
        Assert.Null(result.Continuation);
    }

    [Fact]
    public void MultiPageDriver_cycle1_first_oversized_nested_child_force_overflows_not_loops()
    {
        // PR #175 review P3 — a nested child that is the FIRST at its level
        // AND taller than the whole page must force-overflow (emit), not
        // suppress + loop forever. The fragment-count guard is 0 at the first
        // child, so the break does not fire; the child emits.
        //   root > wrapper > [ huge(h=900) ]   on a 500px page.
        var root = Box.CreateRoot(MakeStyle());
        var wrapper = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        root.AppendChild(wrapper);
        var hugeStyle = MakeStyle();
        SetLengthPx(hugeStyle, PropertyId.Height, 900);
        var huge = Box.ForElement(BoxKind.BlockContainer, hugeStyle, MakeElement());
        wrapper.AppendChild(huge);

        var pages = DriveMultiPageLoop(root, new[] { huge }, blockSize: 500, pageCap: 8);

        // The huge child is force-emitted on page 0 (overflowing) and the run
        // terminates — it does not spin returning empty PageComplete pages.
        Assert.Contains(huge, pages[0].Kids);
        Assert.True(pages.Count <= 2, $"must not loop; got {pages.Count} pages");
        Assert.Equal(LayoutAttemptOutcome.AllDone, pages[^1].Outcome);
        Assert.True(pages[^1].ContinuationIsNull);
    }

    // ====================================================================
    //  Cycle 2 — adjacent-sibling margin collapse (CSS 2.1 §8.3.1)
    // ====================================================================

    [Fact]
    public void Cycle2_collapses_adjacent_positive_margins_to_max()
    {
        // CSS 2.1 §8.3.1 — when both margins are positive, the
        // collapsed value = max(m1, m2). Pre-cycle-2 the layouter
        // summed them.
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: 20),  // bottom=20
            (height: 100, marginTop: 10, marginBottom: 0)); // top=10 → collapse to max(20,10)=20

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Block 0: BlockOffset=0 (marginTop=0), BlockSize=100. Cursor=120.
        Assert.Equal(0, sink.Fragments[0].BlockOffset);
        // Block 1: collapse(20, 10) = 20; topShift = 20 - 20 = 0.
        // BlockOffset = 120 + 0 = 120 (NOT 130 like pre-cycle-2 sum).
        Assert.Equal(120, sink.Fragments[1].BlockOffset);
    }

    [Fact]
    public void Cycle2_collapses_mixed_sign_margins_to_difference()
    {
        // CSS 2.1 §8.3.1 — mixed positive + negative: result = positive
        // - |negative|. Example: marginBottom=20 + marginTop=-5 →
        // collapsed = 20 - 5 = 15.
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: 20),
            (height: 100, marginTop: -5, marginBottom: 0));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Block 0: BlockOffset=0, BlockSize=100. Cursor=120.
        // Block 1: collapse(20, -5) = 20 - 5 = 15. topShift = 15 - 20 = -5.
        // BlockOffset = 120 + (-5) = 115.
        Assert.Equal(115, sink.Fragments[1].BlockOffset);
    }

    [Fact]
    public void Cycle2_collapses_both_negative_margins_to_most_negative()
    {
        // CSS 2.1 §8.3.1 — both negative: result = -max(|m1|, |m2|).
        // Example: marginBottom=-10 + marginTop=-20 → collapsed = -20.
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: -10),
            (height: 100, marginTop: -20, marginBottom: 0));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Block 0: BlockOffset=0, BlockSize=100, marginBottom=-10. Cursor=90 (0+100-10).
        Assert.Equal(0, sink.Fragments[0].BlockOffset);
        // Block 1: collapse(-10, -20) = -20. topShift = -20 - (-10) = -10.
        // BlockOffset = 90 + (-10) = 80.
        Assert.Equal(80, sink.Fragments[1].BlockOffset);
    }

    [Fact]
    public void Cycle2_collapse_chain_across_three_blocks()
    {
        // 3 blocks; verify collapse applies between EACH adjacent pair.
        // m1.bottom=10, m2.top=20 → max(10,20)=20
        // m2.bottom=15, m3.top=5 → max(15,5)=15
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: 10),
            (height: 100, marginTop: 20, marginBottom: 15),
            (height: 100, marginTop: 5, marginBottom: 0));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(3, sink.Fragments.Count);
        // Block 0: BlockOffset=0. Cursor=110.
        Assert.Equal(0, sink.Fragments[0].BlockOffset);
        // Block 1: collapse(10, 20) = 20. topShift = 20-10 = 10. BlockOffset = 110+10 = 120.
        Assert.Equal(120, sink.Fragments[1].BlockOffset);
        // After block 1: cursor = 120+100+15 = 235.
        // Block 2: collapse(15, 5) = 15. topShift = 15-15 = 0. BlockOffset = 235+0 = 235.
        Assert.Equal(235, sink.Fragments[2].BlockOffset);
    }

    [Fact]
    public void Cycle2_first_block_on_page_has_full_margin_top_no_collapse_across_page()
    {
        // CSS Fragmentation L3 §6.1 — margins meeting at a fragmentainer
        // boundary do NOT collapse. The layouter resets the collapse
        // chain on each page entry: the first block on a fresh page
        // applies its FULL marginTop, regardless of the prior page's
        // last-block marginBottom.
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: 50),  // page 1 last block (hypothetical)
            (height: 100, marginTop: 30, marginBottom: 0)); // page 2 first block

        // Resume on page 2: first-block-on-page semantics apply.
        var continuation = new BlockContinuation(ResumeAtChild: 1, ConsumedBlockSize: 150);
        using var layouter = new BlockLayouter(root, sink, continuation);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Page 2's first block: full marginTop=30 applied. BlockOffset = 30.
        Assert.Single(sink.Fragments);
        Assert.Equal(30, sink.Fragments[0].BlockOffset);
    }

    [Fact]
    public void Cycle2_collapse_does_not_apply_to_first_block_on_page()
    {
        // Single-block input: first-block-on-page → full marginTop.
        // (Sanity check that the cycle-2 path doesn't accidentally
        // collapse with a phantom prior block.)
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 25, marginBottom: 0));

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // First block: marginTop=25 fully applied. BlockOffset=25.
        Assert.Equal(25, sink.Fragments[0].BlockOffset);
    }

    [Fact(Skip = "Phase 3 Task 7 cycle 3 — vertical writing-mode support. "
        + "Cycle 1 reads physical top/bottom/left/right; cycle 3 wires logical "
        + "block-start/end + inline-start/end via writing-mode-aware helpers.")]
    public void Cycle3_Layouter_honors_vertical_rl_writing_mode()
    {
        // In vertical-rl, the block axis is X (right-to-left).
        // Layouter should read MarginRight as block-start margin.
    }

    [Fact(Skip = "Phase 3 Task 7 cycle 3 — width/height auto resolution per CSS 2.1 §10.3.3. "
        + "Cycle 1 returns 0 for non-LengthPx slots; cycle 3 resolves auto + percentages "
        + "against the containing-block extent.")]
    public void Cycle3_Layouter_resolves_auto_width_against_containing_block()
    {
        // Width=auto + ContentInlineSize=600 → resolved width = 600 - margins.
    }

    // ====================================================================
    //  Cycle 5c.2a F1 — pre-dispatch row-fit decision for paginatable grids
    //
    //  Verifies that, before the grid wrapper is emitted at the outer-site
    //  Continue path, the F1 check inspects the resolved first remaining
    //  row's extent + routes PageComplete WITHOUT emitting the wrapper when
    //  the row wouldn't fit the page-remaining content area but would fit
    //  on a fresh page. Resolves the grid-wrapper-rollback-for-pre-dispatch
    //  deferral (= cycle 5b PR-#97 review F1). The fixtures pin the gate
    //  semantics (IsPaginatableGrid + strategy != LastResort) + the
    //  progress guard (no defer on a fresh page or on rows oversized
    //  beyond even a fresh page) so cycle 5c.2b's clamp reactivation has
    //  byte-stable foundation under F1.
    // ====================================================================

    [Fact]
    public void Cycle5c2a_F1_defers_paginatable_grid_when_first_row_exceeds_page_remaining()
    {
        // Page 300; sibling 200 consumes the first 200 of the
        // page. The grid is AUTO-HEIGHT with rows totaling 600
        // (= 4 × 150) so its natural extent exceeds the
        // pageRemaining 100. PreMeasureGridRowExtent grows the
        // wrapper to natural extent 600. The outer-site clamp
        // (gated by IsHeightAuto per cycle-5c.2c post-PR-#101
        // P1#1) fires + shrinks borderBoxBlockSize → 100 + flips
        // paginateGridForOuterChild=true. F1 then probes the
        // first row (= 150) > pageRemainingForGridContent (= 100)
        // + ≤ nextPageRemaining (= 300) → routes PageComplete
        // WITHOUT emitting the wrapper.
        //
        // Per PR-#101 review P1#1: this test was updated to use
        // an AUTO-HEIGHT grid; the F1 + clamp pair only activates
        // for auto-height grids until the
        // <c>grid-fragment-plan-shared-sizing-deferral</c> lands
        // (= separates resolved geometry from fragment budget so
        // explicit-height grids can paginate too).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 200);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        // Auto-height grid with rows totaling 600 (= much greater
        // than pageRemaining 100). No explicit Height → IsHeightAuto
        // gate passes → clamp fires.
        var grid = BuildGridContainerWithRowTracks(
            rowsPx: new[] { 150.0, 150.0, 150.0, 150.0 });
        var gridItem = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        grid.AppendChild(gridItem);
        root.AppendChild(grid);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 300);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        // Only the sibling was emitted — grid wrapper is NOT in the sink.
        Assert.Single(sink.Fragments);
        Assert.Same(sibling, sink.Fragments[0].Box);
        // The returned BlockContinuation resumes at the grid (= child
        // index 1) + carries a GridContinuation with RowIndex=0 (=
        // fresh start on the next page) + EmittedBlockExtent=0 (= no
        // rows committed on this page).
        var blockCont = (BlockContinuation)result.Continuation!;
        Assert.Equal(1, blockCont.ResumeAtChild);
        var gridCont = (GridContinuation)blockCont.LayouterState!;
        Assert.Equal(0, gridCont.RowIndex);
        Assert.Null(gridCont.Cache);
        Assert.Equal(0.0, gridCont.EmittedBlockExtent);
    }

    [Fact]
    public void Cycle5c2a_F1_does_not_defer_under_LastResort_strategy()
    {
        // Same shape as the defer test but strategy = LastResort. Per
        // CSS Fragmentation L3 §4.4 + cycle 5's strategy gating, the
        // LastResort attempt must force-emit the grid (= F1's defer
        // pre-empt is bypassed under LastResort). The grid wrapper +
        // its (oversized) row item DO appear in the sink even though
        // the row exceeds the page-remaining content area.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 200);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 150.0 });
        SetLengthPx(grid.Style, PropertyId.Height, 50);
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        root.AppendChild(grid);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 300);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // F1 skipped under LastResort → the wrapper emitted +
        // GridLayouter dispatched atomically. The sink contains the
        // sibling, the grid wrapper, AND the grid item (cycle 1
        // atomic emission). The key F1-specific assertion is that
        // the grid wrapper IS present (= no defer).
        Assert.Same(sibling, sink.Fragments[0].Box);
        Assert.Contains(sink.Fragments, f => ReferenceEquals(f.Box, grid));
    }

    [Fact]
    public void Cycle5c2a_F1_does_not_defer_when_first_row_fits_page_remaining()
    {
        // Sibling consumes 100 of a 300 page → remaining 200. The
        // grid's first row is 80px (≤ 200 remaining → fits). F1 must
        // NOT defer; the wrapper emits + the grid dispatches atomically.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 100);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 80.0 });
        SetLengthPx(grid.Style, PropertyId.Height, 80);
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        root.AppendChild(grid);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 300);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // F1 didn't defer → wrapper emit ran. Sink contains the
        // sibling + the grid wrapper (= F1-relevant assertion);
        // GridLayouter's atomic emit of the row item is an
        // orthogonal cycle-1 behavior.
        Assert.Same(sibling, sink.Fragments[0].Box);
        Assert.Contains(sink.Fragments, f => ReferenceEquals(f.Box, grid));
    }

    [Fact]
    public void Cycle5c2a_F1_does_not_defer_when_row_also_exceeds_fresh_page()
    {
        // Progress guard: F1 must only defer when the row would fit a
        // fresh page. Here the row is 500px on a 300px page → no
        // deferral can help. F1 falls through to the normal dispatch
        // (which under Strict + atomic-grid runs the upstream
        // break-check / forced-overflow path).
        //
        // Setup: sibling 200 + grid wrapper 50 (fits 100 remaining) +
        // first row 500 (exceeds 300 fresh page too). F1's
        // <c>firstRowExtent &lt;= fullPageRemainingForGridContent</c>
        // predicate evaluates false → no defer.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 200);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 500.0 });
        SetLengthPx(grid.Style, PropertyId.Height, 50);
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        root.AppendChild(grid);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 300);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // F1 didn't defer → wrapper emit ran. The grid wrapper IS in
        // the sink (= F1 not the source of any deferral); the
        // downstream path takes over.
        Assert.Contains(sink.Fragments, f => ReferenceEquals(f.Box, grid));
    }

    [Fact]
    public void Cycle5c2a_F1_progress_guard_skips_defer_on_fresh_page()
    {
        // Progress guard: when nothing has been emitted on this page
        // (= fresh page; UsedBlockSize == 0 + topShift == 0), F1's
        // <c>pageRemainingForGridContent &lt;
        // fullPageRemainingForGridContent</c> predicate evaluates
        // false → no defer. Without this guard, an oversized grid on a
        // fresh page would defer infinitely (each next page is also
        // fresh).
        //
        // Setup: NO sibling; grid wrapper 50 + first row 150 + 300
        // page. pageRemaining == fullPage on a fresh page → guard
        // fires.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 150.0 });
        SetLengthPx(grid.Style, PropertyId.Height, 50);
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        root.AppendChild(grid);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 300);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // F1 didn't defer on the fresh page → grid wrapper was emitted.
        Assert.Contains(sink.Fragments, f => ReferenceEquals(f.Box, grid));
    }

    [Fact]
    public void Cycle5c2a_F1_does_not_apply_to_non_grid_children()
    {
        // F1 is gated by IsPaginatableGrid. A regular block-flow
        // container with similar shape (wrapper fits remaining + a
        // descendant that visually overflows) does NOT invoke the F1
        // probe + does NOT route PageComplete via the F1 path. The
        // block emits normally.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 200);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        // A block container (NOT a grid) with the same wrapper shape.
        var blockStyle = MakeStyle();
        SetLengthPx(blockStyle, PropertyId.Height, 50);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 300);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Both fragments emit; no F1 routing happens for non-grid
        // children.
        Assert.Equal(2, sink.Fragments.Count);
        Assert.Same(sibling, sink.Fragments[0].Box);
        Assert.Same(block, sink.Fragments[1].Box);
    }

    [Fact]
    public void Cycle5c2a_F1_progress_guard_first_child_with_margin_top_does_not_defer()
    {
        // Per PR-#99 review P1#1 — direct regression for the zero-
        // progress page loop. With the OLD progress guard
        // (<c>pageRemaining &lt; fullPage</c>), a first-on-page grid
        // with positive <c>margin-top</c> had
        // <c>pageRemaining = BlockSize - 0 - marginStart - chrome</c>
        // less than <c>fullPage = BlockSize - chrome</c>, so the
        // guard fired even though the page was fresh; F1 would defer
        // a row > pageRemaining + ≤ fullPage, and the next page
        // (also fresh, same first-child grid with same marginTop)
        // would repeat the same defer → unbounded blank-page loop
        // since <see cref="LayoutRetryCoordinator"/> returns
        // PageComplete without escalating to LastResort.
        //
        // POST-P1#1 fix: progress guard is
        // <c>emittedThisAttempt &gt; 0</c>. When the grid is the
        // first emittable child on this attempt, F1 does NOT defer
        // regardless of the marginTop / pageRemaining geometry.
        //
        // Repro fixture: BlockSize=300, grid is first child,
        // marginTop=50, wrapper height=50, first row track=280.
        // With OLD code: pageRemaining=250 < fullPage=300 → guard
        // fires + 280 > 250 + 280 ≤ 300 → defer (zero-progress loop).
        // With NEW code: emittedThisAttempt=0 → guard fails → no
        // defer + grid wrapper emits.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 280.0 });
        SetLengthPx(grid.Style, PropertyId.MarginTop, 50);
        SetLengthPx(grid.Style, PropertyId.Height, 50);
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        root.AppendChild(grid);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 300);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Progress invariant: F1 did NOT defer → grid wrapper IS in
        // sink + layouter does NOT return PageComplete with zero
        // progress (= empty sink + BlockContinuation for the same
        // child).
        Assert.Contains(sink.Fragments, f => ReferenceEquals(f.Box, grid));
        // Sink has at least one fragment from this attempt — any
        // PageComplete result therefore commits real progress.
        Assert.NotEmpty(sink.Fragments);
    }

    [Fact]
    public void Cycle5c2a_F1_P1_first_child_terminates_via_coordinator()
    {
        // Per PR-#99 review P1#1 required test #2 — same scenario as
        // the direct test above but driven through the multi-page
        // continuation loop, not a single <c>AttemptLayout</c> call.
        // Pre-fix this would not terminate; post-fix it commits the
        // grid on the first attempt + returns AllDone.
        //
        // We page-drive manually rather than using
        // <see cref="LayoutRetryCoordinator"/> because the
        // coordinator's <c>Run</c> covers a SINGLE page's retry loop;
        // the page-driving loop (= continue until AllDone) is the
        // pipeline's responsibility. A bounded iteration count
        // catches any regression that reintroduces the unbounded
        // PageComplete loop.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 280.0 });
        SetLengthPx(grid.Style, PropertyId.MarginTop, 50);
        SetLengthPx(grid.Style, PropertyId.Height, 50);
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        root.AppendChild(grid);

        // Drive the page-loop manually with a strict iteration cap
        // so a regression that reintroduces the unbounded zero-
        // progress loop fails the test instead of hanging CI.
        LayoutContinuation? incoming = null;
        var pageCount = 0;
        const int maxPages = 8;
        LayoutAttemptOutcome lastOutcome;
        do
        {
            using var pageLayouter = new BlockLayouter(root, sink, incoming);
            var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 300);
            var layoutCtx = new LayoutContext(ctx);
            using var resolver = new BreakResolver();
            using var coordinator = new RecordingCoordinator();
            var result = coordinator.RunAndAssertNotNeedsRewind(
                pageLayouter, ctx, ref layoutCtx, resolver);
            lastOutcome = result.Outcome;
            incoming = result.Continuation;
            pageCount++;
        }
        while (lastOutcome != LayoutAttemptOutcome.AllDone
            && incoming is not null
            && pageCount < maxPages);

        Assert.Equal(LayoutAttemptOutcome.AllDone, lastOutcome);
        Assert.InRange(pageCount, 1, 2);
        Assert.Contains(sink.Fragments, f => ReferenceEquals(f.Box, grid));
    }

    [Fact]
    public void Cycle5c2a_F1_helper_cache_hit_reads_from_cache_when_identity_and_inline_size_match()
    {
        // Per PR-#99 review P1#2 + required test #3 — direct unit
        // test of <c>PreMeasureGridRowExtentAt</c>'s cache-
        // consultation behavior. When an incoming
        // <see cref="GridResumeCache"/> has matching identity +
        // inline-size, the probe MUST read
        // <c>cache.RowBaseSizes[rowIndex]</c> directly rather than
        // running a fresh <c>GridSizing.Resolve</c>. Without this,
        // the resume page's F1 decision could diverge from
        // GridLayouter's actual emission (= probe row-size != emit
        // row-size).
        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 100.0, 100.0, 100.0 });

        // Fabricate a cache with row sizes DIFFERENT from the
        // declared track list (= 50/50/50 instead of 100/100/100)
        // so we can detect that the probe reads from the cache
        // rather than re-resolving the style.
        var cache = new GridResumeCache(
            GridIdentity: grid,
            OriginalContentInlineSize: 400.0,
            RowBaseSizes: ImmutableArray.Create(50.0, 50.0, 50.0),
            ColumnBaseSizes: ImmutableArray.Create(100.0),
            RowPositions: ImmutableArray.Create(0.0, 50.0, 100.0),
            ColumnPositions: ImmutableArray.Create(0.0),
            ItemPlacements: ImmutableArray<GridItemPlacement>.Empty);

        var extent = BlockLayouter.PreMeasureGridRowExtentAt(
            gridBox: grid,
            rowIndex: 1,
            contentInlineSize: 400.0,
            contentBlockSize: 300.0,
            incomingCache: cache,
            cancellationToken: default);

        Assert.Equal(50.0, extent);
    }

    [Fact]
    public void Cycle5c2a_F1_helper_cache_miss_when_inline_size_differs_falls_back_to_fresh_resolve()
    {
        // Per PR-#99 review P1#2 — when cache.OriginalContentInlineSize
        // doesn't match the requested contentInlineSize (= same grid
        // but a different page width), the cache is stale for fr /
        // maximize-distributed columns; the probe MUST run a fresh
        // sizing pass against the new inline-size (mirroring
        // GridLayouter's cycle-5b PR-#97 F4 behavior).
        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 100.0, 100.0 });

        // Cache built at inline-size 400 with FABRICATED row sizes
        // (50). At inline-size 600 (= different page width), the
        // probe should fall through to a fresh GridSizing.Resolve
        // which returns 100 (= the declared length tracks).
        var cache = new GridResumeCache(
            GridIdentity: grid,
            OriginalContentInlineSize: 400.0,
            RowBaseSizes: ImmutableArray.Create(50.0, 50.0),
            ColumnBaseSizes: ImmutableArray.Create(100.0),
            RowPositions: ImmutableArray.Create(0.0, 50.0),
            ColumnPositions: ImmutableArray.Create(0.0),
            ItemPlacements: ImmutableArray<GridItemPlacement>.Empty);

        var extent = BlockLayouter.PreMeasureGridRowExtentAt(
            gridBox: grid,
            rowIndex: 0,
            contentInlineSize: 600.0,
            contentBlockSize: 300.0,
            incomingCache: cache,
            cancellationToken: default);

        Assert.Equal(100.0, extent);
    }

    [Fact]
    public void Cycle5c2a_F1_helper_cache_miss_when_identity_differs_falls_back_to_fresh_resolve()
    {
        // Per PR-#99 review P1#2 — when cache.GridIdentity doesn't
        // ReferenceEqual the gridBox parameter, the cache was built
        // for a different grid container (= routing bug); the probe
        // MUST reject the cache + run a fresh resolve. Mirrors
        // GridLayouter's PR-#96 review F5 validation.
        var gridForCache = BuildGridContainerWithRowTracks(rowsPx: new[] { 50.0 });
        var gridForProbe = BuildGridContainerWithRowTracks(rowsPx: new[] { 100.0 });

        var cache = new GridResumeCache(
            GridIdentity: gridForCache,
            OriginalContentInlineSize: 400.0,
            RowBaseSizes: ImmutableArray.Create(50.0),
            ColumnBaseSizes: ImmutableArray.Create(100.0),
            RowPositions: ImmutableArray.Create(0.0),
            ColumnPositions: ImmutableArray.Create(0.0),
            ItemPlacements: ImmutableArray<GridItemPlacement>.Empty);

        var extent = BlockLayouter.PreMeasureGridRowExtentAt(
            gridBox: gridForProbe,
            rowIndex: 0,
            contentInlineSize: 400.0,
            contentBlockSize: 300.0,
            incomingCache: cache,
            cancellationToken: default);

        // Cache rejected by identity → fresh resolve returns the
        // declared track size of gridForProbe (= 100), NOT the
        // cache's 50.
        Assert.Equal(100.0, extent);
    }

    [Fact]
    public void Cycle5c2a_F1_helper_handles_out_of_range_and_negative_row_indices()
    {
        // Per PR-#99 review P1#2 — defensive return for bad rowIndex
        // values. Out-of-range + negative both return 0 so the
        // caller's row-fit comparison naturally fails (= 0 always
        // fits any positive remaining + defer never triggers from
        // a degenerate probe).
        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 100.0 });

        Assert.Equal(0.0, BlockLayouter.PreMeasureGridRowExtentAt(
            gridBox: grid, rowIndex: -1,
            contentInlineSize: 400, contentBlockSize: 300,
            incomingCache: null, cancellationToken: default));
        Assert.Equal(0.0, BlockLayouter.PreMeasureGridRowExtentAt(
            gridBox: grid, rowIndex: 5,
            contentInlineSize: 400, contentBlockSize: 300,
            incomingCache: null, cancellationToken: default));
    }

    /// <summary>Test double mirroring
    /// <see cref="LayoutRetryCoordinator"/>'s contract for the
    /// page-driving termination test. Wraps the coordinator's
    /// <c>Run</c> + asserts the result is never
    /// <c>NeedsRewind</c> (= the contract guarantee).</summary>
    private sealed class RecordingCoordinator : System.IDisposable
    {
        private readonly LayoutRetryCoordinator _inner = new();

        public LayoutAttemptResult RunAndAssertNotNeedsRewind(
            BlockLayouter layouter,
            FragmentainerContext fragmentainer,
            ref LayoutContext layout,
            IBreakResolver resolver)
        {
            var result = _inner.Run(
                layouter, fragmentainer, ref layout, resolver);
            Assert.NotEqual(LayoutAttemptOutcome.NeedsRewind, result.Outcome);
            return result;
        }

        public void Dispose() { /* nothing to dispose */ }
    }

    /// <summary>Per Phase 3 Task 17 cycle 5c.2a — fixture helper to
    /// build a grid container with explicit <c>grid-template-rows</c>
    /// Length tracks. Mirrors <see cref="GridLayouterTests"/>'s
    /// builder shape so F1's probe sees the same row sizes as
    /// GridLayouter would.</summary>
    private static Box BuildGridContainerWithRowTracks(double[] rowsPx)
    {
        var rowsBuilder = ImmutableArray.CreateBuilder<TrackListItem>(rowsPx.Length);
        foreach (var size in rowsPx)
        {
            rowsBuilder.Add(new TrackListEntry(TrackEntry.ForLength(size)));
        }
        var rows = new TrackList(rowsBuilder.ToImmutable());
        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForLength(100))));

        var style = MakeStyle();
        style.SetSideTablePayload(PropertyId.GridTemplateRows, rows);
        style.Set(PropertyId.GridTemplateRows, ComputedSlot.FromSideTableIndex(0));
        style.SetSideTablePayload(PropertyId.GridTemplateColumns, cols);
        style.Set(PropertyId.GridTemplateColumns, ComputedSlot.FromSideTableIndex(0));
        return Box.ForElement(BoxKind.GridContainer, style, MakeElement());
    }

    // ====================================================================
    //  Cycle 5c.2b F2 — outer-site clamp + gate-flip + wrapper-resize
    //
    //  Tests covering the cycle-5b PR-#97 reverted outer-site activation:
    //  cycle 5c.2a F1 (above) + cycle 5c.2b F2 (here) make the gate-flip
    //  safe by routing Strict-defer pre-empt without empty wrappers + by
    //  resizing the wrapper to the actual emitted-rows extent (not the
    //  clamped budget). The IBlockFragmentSink.UpdateFragmentBlockSize
    //  API mutation preserves z-order (= wrapper stays ahead of children
    //  in the fragment list) without re-emitting.
    // ====================================================================

    [Fact]
    public void Cycle5c2b_F2_wrapper_resizes_to_emitted_extent_on_partial_emit()
    {
        // Page=300, sibling 200, grid has 3 rows of 100px each + auto
        // height (= natural extent 300). Page remaining after sibling
        // = 100; clamp shrinks borderBoxBlockSize to 100 + flips
        // paginateGridForOuterChild=true. GridLayouter dispatches with
        // allowPagination=true → emits row 0 only (100 fits 100
        // budget; row 1 doesn't) → returns PageComplete with
        // GridContinuation(RowIndex=1, EmittedBlockExtent=100). F2
        // consumer resizes the wrapper from 100 (clamped budget) to
        // 100 (chrome=0 + emittedExtent=100) — identical today
        // because the budget equals the emitted-extent in this
        // single-fit case. The test pins the WRAPPER RESIZE PATH
        // executes (= UpdateFragmentBlockSize was called) by
        // checking the recorded wrapper has the expected BlockSize.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 200);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 100.0, 100.0, 100.0 });
        // No explicit height → auto-grid; PreMeasureGridRowExtent
        // grows borderBoxBlockSize to natural 300. The clamp then
        // shrinks to remaining = 100.
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        root.AppendChild(grid);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 300);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Expectations:
        //   - PageComplete with BlockContinuation routing to the grid.
        //   - Sink contains sibling + wrapper (= 2 wrappers minimum;
        //     the grid item is emitted INSIDE the wrapper after it).
        //   - Wrapper BlockSize equals emittedExtent + chrome = 100
        //     (= F2 resize hit; pre-F2 it would be 100 too because
        //     budget == emitted in this case; the stronger test is
        //     the multi-row scenario below).
        //   - Outgoing GridContinuation has RowIndex=1 (= one row
        //     emitted, row 1 next).
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var blockCont = (BlockContinuation)result.Continuation!;
        Assert.Equal(1, blockCont.ResumeAtChild);
        var gridCont = (GridContinuation)blockCont.LayouterState!;
        Assert.Equal(1, gridCont.RowIndex);
        Assert.Equal(100.0, gridCont.EmittedBlockExtent);
        // Wrapper fragment is in the sink (= 2nd fragment after
        // sibling); BlockSize matches emitted-extent + chrome (0
        // here since no border/padding).
        var wrapperFragment = sink.Fragments
            .First(f => ReferenceEquals(f.Box, grid));
        Assert.Equal(100.0, wrapperFragment.BlockSize);
    }

    [Fact]
    public void Cycle5c2b_F2_wrapper_resize_eliminates_empty_trailing_space()
    {
        // The CRITICAL F2 test — wrapper budget DIVERGES from emitted
        // extent. Page=300, sibling 50, grid has rows [80, 80, 80] +
        // auto height. Sibling consumes 50; page remaining = 250.
        // Clamp shrinks borderBoxBlockSize to 250 + flips gate ON.
        // GridLayouter with allowPagination=true emits rows 0+1+2
        // (= 240 fits 250) → PageComplete(GridContinuation(...,
        // EmittedBlockExtent=240)). PRE-F2: wrapper paints at 250
        // (= empty 10px trailing space). POST-F2: wrapper paints at
        // 240 (= matches emitted content exactly).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 50);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 80.0, 80.0, 80.0 });
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        root.AppendChild(grid);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 300);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Critical assertion: wrapper BlockSize == 240 (= sum of
        // emitted row tracks), NOT 250 (= clamped budget). Pre-F2
        // this would have been 250 → 10px empty trailing space.
        var wrapperFragment = sink.Fragments
            .First(f => ReferenceEquals(f.Box, grid));
        Assert.Equal(240.0, wrapperFragment.BlockSize, precision: 3);
    }

    [Fact]
    public void Cycle5c2b_F2_cursor_advance_uses_emitted_extent_not_budget()
    {
        // Twin to the trailing-space test — verifies the cursor
        // advance (= fragmentainer.UsedBlockSize after the grid) uses
        // emittedExtent + chrome, not the clamped budget. Page=300,
        // sibling 50, grid rows [80, 80, 80]. After dispatch:
        //   - sibling consumed 50
        //   - grid wrapper would have advanced by clamped budget 250
        //     (pre-F2) → UsedBlockSize = 300 (= full page)
        //   - F2 advances by emittedExtent 240 → UsedBlockSize = 290
        // The 10px difference matters when other siblings follow the
        // grid: pre-F2 they'd be displaced 10px past their correct
        // position.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 50);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 80.0, 80.0, 80.0 });
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        root.AppendChild(grid);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 300);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // UsedBlockSize after the run: sibling(50) + grid(240
        // emitted, NOT 250 budget) = 290. Pre-F2 this would be 300
        // (full page) due to wrapper-budget over-advance.
        Assert.Equal(290.0, ctx.UsedBlockSize, precision: 3);
    }

    [Fact]
    public void Cycle5c2b_F2_gate_off_path_uses_unmodified_marginbox_cursor_advance()
    {
        // When the clamp doesn't fire (= wrapper fits remaining +
        // grid is atomic-dispatched), the gate stays OFF + F2
        // wrapper-resize does NOT run. Cursor advance uses
        // marginBoxBlockSizeForCursor (= subtree-aware natural
        // extent) as in cycle 2c. Pins the GATE-OFF code path.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 80.0 });
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        root.AppendChild(grid);

        using var layouter = new BlockLayouter(root, sink);
        // Grid wrapper 80 fits the 600 page comfortably → clamp
        // doesn't fire → gate stays OFF → atomic dispatch + no F2
        // resize.
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 600);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Wrapper at natural size (80) since gate is OFF + no
        // F2 resize triggered.
        var wrapperFragment = sink.Fragments
            .First(f => ReferenceEquals(f.Box, grid));
        Assert.Equal(80.0, wrapperFragment.BlockSize, precision: 3);
    }

    [Fact]
    public void Cycle5c2b_F2_AllDone_final_resume_page_resizes_wrapper()
    {
        // The critical F1+F2 round-trip: page 1 emits row 0, defers
        // remaining via PageComplete; page 2 (resume page) emits the
        // remaining rows in AllDone. On page 2, the wrapper must
        // size to the emitted-rows extent (= LastEmittedBlockExtent
        // on AllDone, per cycle 5c.1 PR-#98 review F1), NOT the
        // clamped budget. Mirrors GridLayouterTests'
        // <c>Cycle5c1_F1_AllDone_final_fragment_of_split_grid_exposes_remaining_extent</c>
        // but exercised through the full BlockLayouter dispatch +
        // F2 wrapper-resize consumer.
        //
        // Fixture: 3-row grid 100/100/100. Page block 300; sibling
        // 200 on page 1 → grid emits row 0 (100 of 100 remaining).
        // Page 2 (resume): empty page, grid is first emittable,
        // remaining 300; grid emits rows 1+2 (= 200 total). Wrapper
        // BlockSize on page 2 must be 200, NOT 300 (= natural) or
        // 300 (= clamped budget).
        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 100.0, 100.0, 100.0 });
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));

        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 200);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(sibling);
        root.AppendChild(grid);

        // Page 1: emits sibling + row 0 of grid (= clamp to 100
        // remaining → 1 row of 100 fits).
        var sink1 = new RecordingFragmentSink();
        using var layouter1 = new BlockLayouter(root, sink1);
        var ctx1 = new FragmentainerContext(contentInlineSize: 600, blockSize: 300);
        var layoutCtx1 = new LayoutContext(ctx1);
        using var resolver1 = new BreakResolver();
        var result1 = layouter1.AttemptLayout(
            ctx1, ref layoutCtx1, resolver1, LayoutAttemptStrategy.Strict);
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result1.Outcome);
        var continuation = (BlockContinuation)result1.Continuation!;

        // Page 2: resume page with the continuation, fresh
        // fragmentainer. Should emit rows 1+2.
        var sink2 = new RecordingFragmentSink();
        using var layouter2 = new BlockLayouter(root, sink2, continuation);
        var ctx2 = new FragmentainerContext(contentInlineSize: 600, blockSize: 300);
        var layoutCtx2 = new LayoutContext(ctx2);
        using var resolver2 = new BreakResolver();
        var result2 = layouter2.AttemptLayout(
            ctx2, ref layoutCtx2, resolver2, LayoutAttemptStrategy.Strict);

        // Page 2 emits the grid wrapper. With F2 + the cycle-5c.1
        // AllDone-extent contract, the wrapper resizes to 200 (=
        // emittedExtent for rows 1+2). Pre-F2 it would be either 300
        // (= natural) or unclamped.
        var wrapperFragment2 = sink2.Fragments
            .First(f => ReferenceEquals(f.Box, grid));
        Assert.Equal(200.0, wrapperFragment2.BlockSize, precision: 3);
    }

    [Fact]
    public void Cycle5c2b_F2_sink_UpdateFragmentBlockSize_replaces_blocksize_preserves_other_fields()
    {
        // Direct unit test of the new IBlockFragmentSink API. Verifies
        // that UpdateFragmentBlockSize mutates ONLY the BlockSize +
        // preserves Box, InlineOffset, BlockOffset, InlineSize,
        // InlineLayout. Catches regressions in the test sink's `with`
        // expression / mutation pattern.
        var sink = new RecordingFragmentSink();
        var box = Box.CreateRoot(MakeStyle());
        sink.Emit(new BoxFragment(
            Box: box,
            InlineOffset: 10,
            BlockOffset: 20,
            InlineSize: 100,
            BlockSize: 200));
        Assert.Equal(1, sink.Cursor);

        sink.UpdateFragmentBlockSize(cursor: 0, newBlockSize: 50);

        var fragment = sink.Fragments[0];
        Assert.Same(box, fragment.Box);
        Assert.Equal(10, fragment.InlineOffset);
        Assert.Equal(20, fragment.BlockOffset);
        Assert.Equal(100, fragment.InlineSize);
        Assert.Equal(50, fragment.BlockSize);
    }

    [Fact]
    public void Cycle5c2b_F2_sink_UpdateFragmentBlockSize_out_of_range_is_safe_noop()
    {
        // The sink contract permits invalid indices as defensive
        // no-ops (= rather than throw). Test sinks mirror the
        // pattern; this verifies neither out-of-range index nor
        // empty sink crash.
        var sink = new RecordingFragmentSink();
        // Empty sink:
        sink.UpdateFragmentBlockSize(cursor: 0, newBlockSize: 100);
        sink.UpdateFragmentBlockSize(cursor: -1, newBlockSize: 100);
        Assert.Equal(0, sink.Cursor);

        // Populate one fragment + try out-of-range:
        var box = Box.CreateRoot(MakeStyle());
        sink.Emit(new BoxFragment(box, 0, 0, 100, 200));
        sink.UpdateFragmentBlockSize(cursor: 5, newBlockSize: 999);
        sink.UpdateFragmentBlockSize(cursor: -1, newBlockSize: 999);
        Assert.Equal(200, sink.Fragments[0].BlockSize);
    }

    // ====================================================================
    //  Cycle 5c.2b post-PR-#100 review hardening
    //
    //  P1#1: nested-context BlockLayouters (table cell / grid item /
    //  table caption) suppress grid pagination so a paginatable direct-
    //  child grid can't return PageComplete(GridContinuation) into a
    //  parent that discards it (= silent row loss). The
    //  <c>disableGridPagination</c> constructor flag is the seam.
    //
    //  P1#2: F2 cursor arithmetic uses <c>topShift</c>, not
    //  <c>marginStart</c>, so adjacent-sibling margin collapse is not
    //  double-counted (per CSS 2.1 §8.3.1).
    //
    //  P1#3: the outer-site extent clamp is also gated by
    //  <c>IsHeightAuto(child)</c> so explicit-height grids don't get
    //  their authored wrapper geometry corrupted before cycle 5c.2c
    //  ships F3 (= grid-aware MeasureSubtreeVisualBlockExtent).
    // ====================================================================

    [Fact]
    public void Cycle5c2b_P1_1_nested_block_layouter_disables_grid_pagination()
    {
        // Direct test of the disableGridPagination flag. When a
        // nested BlockLayouter is constructed with the flag set,
        // even a scenario that WOULD trigger F1's defer at the
        // root falls through to atomic dispatch + emits the
        // wrapper. Critical for the table-cell / grid-item context
        // where the parent discards the inner result.
        //
        // Fixture: identical to the F1-defers test (= prior
        // sibling + paginatable grid first row exceeds remaining
        // + Strict). With disableGridPagination=true, F1 is
        // bypassed → wrapper emits.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 200);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 150.0 });
        SetLengthPx(grid.Style, PropertyId.Height, 50);
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        root.AppendChild(grid);

        using var layouter = new BlockLayouter(
            rootBox: root,
            sink: sink,
            incomingContinuation: null,
            diagnostics: null,
            shaperResolver: null,
            disableGridPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 300);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // With pagination suppressed, the wrapper IS emitted on
        // this page (F1 doesn't defer + the clamp doesn't fire +
        // F2 doesn't resize).
        Assert.Contains(sink.Fragments, f => ReferenceEquals(f.Box, grid));
    }

    [Fact]
    public void Cycle5c2b_P1_1_table_cell_with_paginatable_grid_renders_all_rows()
    {
        // Per PR-#100 review P1#1 required test #1: a table cell
        // containing a direct-child paginatable grid must render
        // EVERY grid row exactly once — no silent loss from a
        // discarded continuation. The cell's nested BlockLayouter
        // is now constructed with disableGridPagination=true (=
        // see TableLayouter.cs cell-content dispatch), so the
        // grid dispatches atomically + emits all 4 rows under
        // visual-overflow semantics (= cycle-1 contract).
        //
        // Direct GridLayouter assertion: when a BlockLayouter is
        // constructed with disableGridPagination=true + given a
        // root containing only a paginatable grid in a tight
        // budget that WOULD trigger F1 / the clamp, all grid
        // rows must emit (= no defer + no clamp + atomic).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var grid = BuildGridContainerWithRowTracks(
            rowsPx: new[] { 100.0, 100.0, 100.0, 100.0 });
        var item1 = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        var item2 = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        var item3 = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        var item4 = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        grid.AppendChild(item1);
        grid.AppendChild(item2);
        grid.AppendChild(item3);
        grid.AppendChild(item4);
        root.AppendChild(grid);

        // Inner fragmentainer modeling the cell's available
        // block-extent. Without disableGridPagination, the F1
        // clamp would defer / split this — losing rows.
        using var layouter = new BlockLayouter(
            rootBox: root,
            sink: sink,
            incomingContinuation: null,
            diagnostics: null,
            shaperResolver: null,
            disableGridPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 300);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // CRITICAL — no GridContinuation in the result (= F1 didn't
        // route + the result has no nested grid state for a parent
        // to discard). Outcome may be AllDone (= grid fit) or
        // PageComplete (= outer forced-overflow fired because the
        // grid's natural extent 400 exceeds the page 300; that's
        // outer-block-layouter behavior, NOT grid pagination).
        if (result.Continuation is BlockContinuation bc)
        {
            Assert.Null(bc.LayouterState);
        }
        // All 4 grid items emitted (no row loss) — the dispatch
        // was atomic per the disableGridPagination contract.
        Assert.Contains(sink.Fragments, f => ReferenceEquals(f.Box, item1));
        Assert.Contains(sink.Fragments, f => ReferenceEquals(f.Box, item2));
        Assert.Contains(sink.Fragments, f => ReferenceEquals(f.Box, item3));
        Assert.Contains(sink.Fragments, f => ReferenceEquals(f.Box, item4));
    }

    [Fact]
    public void Cycle5c2b_P1_2_F2_cursor_advance_honors_collapsed_margins()
    {
        // Per PR-#100 review P1#2: F2 must use topShift, not
        // marginStart. Fixture: sibling marginBottom=80, grid
        // marginTop=10. Per CSS 2.1 §8.3.1, the collapsed gap is
        // max(80,10)=80 — the 80 was already added by the prior
        // block's emit, so the grid's topShift contribution is 0
        // (no additional). With marginStart-based arithmetic
        // (pre-P1#2), F2 charges 10 again → cursor over-advances
        // by 10px. With topShift-based arithmetic (post-P1#2),
        // cursor advance is correct.
        //
        // Scenario: sibling 100 (+ marginBottom 80), then
        // paginatable grid (marginTop 10, height auto, single
        // row 50). Page 300.
        //   Cursor after sibling: BlockOffset 0 → emit at 0..100;
        //     UsedBlockSize advances by marginBox = 0+100+80=180.
        //   At grid: prevBlockMarginEnd=80, marginStart=10
        //     → effectiveTopGap = max(80,10) = 80;
        //     topShift = 80 - 80 = 0;
        //     blockOffset = 180 + 0 = 180.
        //   Grid wrapper natural extent 50 → fits remaining 120.
        //   Clamp doesn't fire → atomic dispatch → wrapper at 50.
        //   Standard cursor advance:
        //     marginBoxBlockSizeForCursor = topShift + 50 +
        //     marginEnd(0) = 50.
        //
        // But to exercise F2's cursor recomputation, we need
        // F2 to fire. Construct a scenario where the clamp
        // fires (auto-height grid wrapper > remaining + remaining
        // > chrome).
        //
        // Fixture v2: page 300, sibling 100 + marginBottom 80,
        // grid marginTop 10 + auto-height with 3 rows of 100
        // (natural extent 300). PreMeasure grows wrapper to 300.
        //   After sibling: UsedBlockSize=180.
        //   At grid: topShift = 0 (collapsed); pageRemaining =
        //     300 - 180 - 0 = 120; clamp fires
        //     (120 < 300 + 120 > chrome 0) → borderBoxBlockSize=120,
        //     paginateGridForOuterChild=true.
        //   GridLayouter with allowPagination=true emits row 0
        //     (= 100 fits 120) → PageComplete(GridContinuation,
        //     EmittedBlockExtent=100).
        //   F2 wrapper-resize: BlockSize = chrome(0) + 100 = 100.
        //   F2 cursor advance (post-P1#2 topShift-based):
        //     topShift(0) + 100 + marginEnd(0) = 100.
        //   UsedBlockSize after grid = 180 + 100 = 280.
        //
        // Pre-P1#2 (marginStart-based) would have computed:
        //   marginStart(10) + 100 + 0 = 110;
        //   UsedBlockSize after grid = 180 + 110 = 290 (= 10
        //   over-advance from double-counted margin).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 100);
        SetLengthPx(siblingStyle, PropertyId.MarginBottom, 80);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 100.0, 100.0, 100.0 });
        SetLengthPx(grid.Style, PropertyId.MarginTop, 10);
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        root.AppendChild(grid);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 300);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Correct cursor advance: 100 (sibling border-box) + 80
        // (sibling marginBottom, collapsed) + 0 (no additional
        // topShift) + 100 (emitted grid row) + 0 (grid marginEnd)
        // = 280. Pre-P1#2 was 290 (= 10 over-advance).
        Assert.Equal(280.0, ctx.UsedBlockSize, precision: 3);
        // Wrapper still at the resized extent (100).
        var wrapperFragment = sink.Fragments
            .First(f => ReferenceEquals(f.Box, grid));
        Assert.Equal(100.0, wrapperFragment.BlockSize, precision: 3);
    }

    [Fact]
    public void Cycle5c3_explicit_height_grid_paginates_via_outer_site()
    {
        // Per Phase 3 Task 17 cycle 5c.3 — F3 SHIPPED. Explicit-
        // height grids now paginate via the outer-site clamp +
        // dual-input dispatch:
        //   1. PreMeasure leaves borderBoxBlockSize at the
        //      authored 300.
        //   2. <c>authoredBorderBoxBlockSize</c> captures 300
        //      pre-clamp.
        //   3. Outer-site clamp (no longer gated on IsHeightAuto)
        //      shrinks borderBoxBlockSize to pageRemaining 250 +
        //      flips <c>paginateGridForOuterChild</c>.
        //   4. Subtree-extent clamp brings <c>subtreeBlockExtent</c>
        //      to 250 so the break-check stays in the Continue
        //      path.
        //   5. Dispatch passes AUTHORED contentBlockSize (300 -
        //      0 chrome = 300) to <c>GridSizing.Resolve</c> +
        //      clamped contentBlockSize (250) as
        //      <c>pageBlockBudget</c>. Rows resolve as
        //      [100, 100, 100] against authored 300 (= preserves
        //      authored geometry).
        //   6. <c>ComputePaginatedRowRange</c> with budget 250
        //      emits rows 0..1 (sum 200), defers row 2 via
        //      GridContinuation.
        //   7. F2 wrapper-resize shrinks wrapper to chrome (0) +
        //      lastEmittedBlockExtent (200) = 200.
        //
        // Pre-F3 (cycle 5c.2c-post-PR-#101 state): wrapper at
        // authored 300, atomic dispatch emits all 3 rows past
        // the page edge.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 100.0, 100.0, 100.0 });
        SetLengthPx(grid.Style, PropertyId.Height, 300);
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        root.AppendChild(grid);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 250);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // F2 resized wrapper to chrome + emittedRowsExtent =
        // 0 + 200 = 200 (= 2 emitted rows × 100).
        var wrapperFragment = sink.Fragments
            .First(f => ReferenceEquals(f.Box, grid));
        Assert.Equal(200.0, wrapperFragment.BlockSize, precision: 3);
        // PageComplete carries a chained BlockContinuation with
        // a GridContinuation leaf pointing at row 2 (= the
        // deferred row).
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var bc = (BlockContinuation)result.Continuation!;
        var gridCont = (GridContinuation)bc.LayouterState!;
        Assert.Equal(2, gridCont.RowIndex);
    }

    [Fact]
    public void Cycle5c3_explicit_height_grid_after_sibling_paginates_first_row()
    {
        // Per Phase 3 Task 17 cycle 5c.3 — sibling + explicit-
        // height grid scenario WITH F3. The outer-site clamp
        // now fires for explicit-height grids; the
        // dual-input dispatch emits row 0 + defers row 1 via
        // GridContinuation.
        //
        // Pre-F3 (cycle 5c.2c-post-PR-#101 state): the clamp
        // didn't fire (IsHeightAuto gate), the break-check
        // would fire BreakHere because the grid's natural
        // borderBox (200) exceeded pageRemaining (150), and the
        // grid was deferred whole via break-before.
        //
        // Post-F3:
        //   1. After sibling (150) emits, UsedBlockSize=150.
        //   2. pageRemaining for grid = 300 - 150 = 150.
        //   3. authored borderBox = 200, clamp shrinks to 150.
        //   4. Subtree-extent clamp brings subtreeBlockExtent
        //      from 200 → 150 → break-check Continues.
        //   5. F1 row-fit probe (Strict + emittedThisAttempt=1):
        //      row 0 = 100 fits pageRemaining 150 (content) →
        //      F1 does NOT defer.
        //   6. Dispatch with authored 200 + budget 150. Rows
        //      [100, 100]. Budget fits row 0 only → emit row 0,
        //      defer row 1 via GridContinuation.
        //   7. F2 resizes wrapper to 0 + 100 = 100.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 150);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 100.0, 100.0 });
        SetLengthPx(grid.Style, PropertyId.Height, 200);
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        root.AppendChild(grid);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 300);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Sibling + grid wrapper (F2-resized to 100) both
        // emitted. PageComplete carries a chained
        // BlockContinuation → GridContinuation(RowIndex=1).
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.Contains(sink.Fragments, f => ReferenceEquals(f.Box, sibling));
        var wrapperFragment = sink.Fragments
            .First(f => ReferenceEquals(f.Box, grid));
        Assert.Equal(100.0, wrapperFragment.BlockSize, precision: 3);
        var blockCont = (BlockContinuation)result.Continuation!;
        Assert.Equal(1, blockCont.ResumeAtChild);
        var gridCont = (GridContinuation)blockCont.LayouterState!;
        Assert.Equal(1, gridCont.RowIndex);
    }

    [Fact]
    public void Cycle5c3_oversized_first_row_LastResort_emits_diagnostic_and_makes_progress()
    {
        // Per Phase 3 Task 17 cycle 5c.3 — F3 reroutes the
        // oversized-first-row case through the Continue path
        // with pagination enabled. The grid emits the oversized
        // row via its OWN §4.4 force-emit path; the outer
        // block-level forced-overflow path no longer fires.
        //
        // Pre-F3: clamp didn't fire (IsHeightAuto gate) →
        // break-check BreakHere → outer forced-overflow path →
        // PAGINATION-FORCED-OVERFLOW-001 + atomic dispatch.
        //
        // Post-F3:
        //   1. authored borderBox = 500, clamp shrinks to 250.
        //   2. Subtree-extent clamp brings 500 → 250.
        //   3. Break-check Continue.
        //   4. Dispatch with authored 500 contentBlockSize +
        //      budget 250.
        //   5. GridSizing.Resolve against 500: row = 500.
        //   6. ComputePaginatedRowRange(budget=250) sees row 0
        //      = 500 doesn't fit. LastResort force-emits row +
        //      emits LAYOUT-GRID-FORCED-OVERFLOW-001.
        //   7. LastEmittedBlockExtent = 500. F2 resizes
        //      wrapper to 0 + 500 = 500.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        var root = Box.CreateRoot(MakeStyle());

        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 500.0 });
        SetLengthPx(grid.Style, PropertyId.Height, 500);
        var item = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        grid.AppendChild(item);
        root.AppendChild(grid);

        using var layouter = new BlockLayouter(root, sink, diagnostics: diag);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 250);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diag };
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // Result is committed (not NeedsRewind per LastResort
        // contract): either AllDone or PageComplete.
        Assert.NotEqual(LayoutAttemptOutcome.NeedsRewind, result.Outcome);
        // Wrapper in sink at F2-resized geometry (= chrome 0 +
        // emittedExtent 500 = 500). Authored row geometry
        // preserved (= GridSizing.Resolve ran against authored
        // 500, NOT clamped 250).
        var wrapperFragment = sink.Fragments
            .First(f => ReferenceEquals(f.Box, grid));
        Assert.Equal(500.0, wrapperFragment.BlockSize, precision: 3);
        // Grid item dispatched + emitted (= the §4.4 force-emit
        // path inside GridLayouter committed it).
        Assert.Contains(sink.Fragments, f => ReferenceEquals(f.Box, item));
        // GridLayouter emits LAYOUT-GRID-FORCED-OVERFLOW-001
        // because the oversized row was force-emitted in
        // LastResort + paginatable mode.
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridForcedOverflow001);
    }

    [Fact]
    public void Cycle5c3_explicit_height_with_fr_rows_preserves_authored_geometry()
    {
        // Per PR-#101 review P1#1 → Phase 3 Task 17 cycle 5c.3
        // — THE CRITICAL F3 REGRESSION TEST:
        // <c>height: 400px; grid-template-rows: 100px 1fr</c>.
        //
        // F3 minimum-viable correctness requirement: the fr row
        // MUST resolve against AUTHORED 400 (not the clamped
        // page budget), giving rows [100, 300]. The cycle 5c.2c
        // initial F3 attempt fed the clamped 250 into
        // <c>GridSizing.Resolve</c> as <c>contentBlockSize</c>,
        // causing fr to redistribute against 250 → row 1 =
        // 250 - 100 = 150 (= 150px silently lost). The fix:
        // <c>ConfigureEmission</c>'s new <c>pageBlockBudget</c>
        // parameter separates row-sizing input (authored 400)
        // from pagination budget (clamped 250).
        //
        // Page 1 (this test): emit item1 (row 0, height 100) +
        // defer row 1 via GridContinuation. Resume cache pins
        // row 1 size = 300 (= the authored fr resolution); page
        // 2 would emit item2 at that size.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        // Build grid with rows: 100px Length + 1fr.
        var rowsBuilder = ImmutableArray.CreateBuilder<TrackListItem>(2);
        rowsBuilder.Add(new TrackListEntry(TrackEntry.ForLength(100)));
        rowsBuilder.Add(new TrackListEntry(TrackEntry.ForFr(1)));
        var rowsTrackList = new TrackList(rowsBuilder.ToImmutable());
        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForLength(100))));
        var gridStyle = MakeStyle();
        gridStyle.SetSideTablePayload(PropertyId.GridTemplateRows, rowsTrackList);
        gridStyle.Set(PropertyId.GridTemplateRows, ComputedSlot.FromSideTableIndex(0));
        gridStyle.SetSideTablePayload(PropertyId.GridTemplateColumns, cols);
        gridStyle.Set(PropertyId.GridTemplateColumns, ComputedSlot.FromSideTableIndex(0));
        SetLengthPx(gridStyle, PropertyId.Height, 400);
        SetLengthPx(gridStyle, PropertyId.Width, 100);
        var grid = Box.ForElement(BoxKind.GridContainer, gridStyle, MakeElement());

        // Two items, one per row, to capture row geometry.
        var item1Style = MakeStyle();
        var item1RowValue = NetPdf.Css.ComputedValues.GridLineValue.ForLineNumber(1);
        item1Style.SetSideTablePayload(PropertyId.GridRowStart, (object)item1RowValue);
        item1Style.Set(PropertyId.GridRowStart, ComputedSlot.FromSideTableIndex(0));
        var item1ColValue = NetPdf.Css.ComputedValues.GridLineValue.ForLineNumber(1);
        item1Style.SetSideTablePayload(PropertyId.GridColumnStart, (object)item1ColValue);
        item1Style.Set(PropertyId.GridColumnStart, ComputedSlot.FromSideTableIndex(0));
        var item1 = Box.ForElement(BoxKind.BlockContainer, item1Style, MakeElement());
        grid.AppendChild(item1);

        var item2Style = MakeStyle();
        var item2RowValue = NetPdf.Css.ComputedValues.GridLineValue.ForLineNumber(2);
        item2Style.SetSideTablePayload(PropertyId.GridRowStart, (object)item2RowValue);
        item2Style.Set(PropertyId.GridRowStart, ComputedSlot.FromSideTableIndex(0));
        var item2ColValue = NetPdf.Css.ComputedValues.GridLineValue.ForLineNumber(1);
        item2Style.SetSideTablePayload(PropertyId.GridColumnStart, (object)item2ColValue);
        item2Style.Set(PropertyId.GridColumnStart, ComputedSlot.FromSideTableIndex(0));
        var item2 = Box.ForElement(BoxKind.BlockContainer, item2Style, MakeElement());
        grid.AppendChild(item2);

        root.AppendChild(grid);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 250);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // Page 1: item1 emitted at row 0 (= 100 tall). item2
        // (row 1) is deferred via GridContinuation; the resume
        // cache carries row sizes [100, 300] (= the AUTHORED fr
        // distribution against 400, NOT the clamped 250).
        var item1Fragment = sink.Fragments
            .First(f => ReferenceEquals(f.Box, item1));
        Assert.Equal(100.0, item1Fragment.BlockSize, precision: 3);
        // item2 NOT in page 1 fragments (= deferred to page 2).
        Assert.DoesNotContain(sink.Fragments, f => ReferenceEquals(f.Box, item2));
        // Verify the resume cache pins row 1 size = AUTHORED 300
        // (= the F3 correctness contract — fr resolved against
        // authored 400, not clamped 250). Pre-F3 the cache
        // would have shown row 1 size = 150 (silent geometry
        // loss).
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var bc = (BlockContinuation)result.Continuation!;
        var gridCont = (GridContinuation)bc.LayouterState!;
        Assert.Equal(1, gridCont.RowIndex);
        Assert.NotNull(gridCont.Cache);
        Assert.Equal(2, gridCont.Cache!.RowBaseSizes.Length);
        Assert.Equal(100.0, gridCont.Cache.RowBaseSizes[0], precision: 3);
        Assert.Equal(300.0, gridCont.Cache.RowBaseSizes[1], precision: 3);
    }

    [Fact]
    public void Cycle5c2c_post_PR_101_F1_does_not_fabricate_continuation_when_clamp_does_not_fire()
    {
        // Per PR-#101 review P1#3 — F1 must only fire when the
        // dispatch will actually paginate. Pre-fix F1's gate was
        // <c>IsPaginatableGrid && !_disableGridPagination &&
        // strategy != LastResort && emittedThisAttempt > 0</c> —
        // it could fabricate a GridContinuation in a scenario
        // where DispatchGridInner would run with
        // allowPagination=false. Post-fix F1 also gates on
        // <c>paginateGridForOuterChild</c> so the F1 pre-empt
        // matches the dispatch's pagination decision.
        //
        // Fixture: sibling 150 + explicit-height grid 100 (= fits
        // pageRemaining 150 → clamp doesn't fire, gate stays
        // false) + first row 200 (= exceeds pageRemaining-content
        // 150). Pre-fix F1 would defer + fabricate
        // GridContinuation. Post-fix F1's
        // <c>paginateGridForOuterChild</c> gate is false → F1
        // doesn't fire → normal dispatch.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 150);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 200.0 });
        SetLengthPx(grid.Style, PropertyId.Height, 100);
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        root.AppendChild(grid);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 300);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // No fabricated GridContinuation. Either:
        //   - Sibling emitted + grid emitted normally with
        //     overflow (= clamp doesn't fire, atomic dispatch).
        //   - Or sibling emitted + grid deferred to next page
        //     via the standard break-check (= no GridContinuation
        //     in the LayouterState).
        if (result.Continuation is BlockContinuation bc)
        {
            Assert.Null(bc.LayouterState);
        }
    }

    [Fact]
    public void Cycle5c2b_P1_2_F2_final_resume_fragment_with_margin_advances_correctly()
    {
        // Per PR-#100 review P1#2 required test #2: final resume
        // fragment of a split grid with margins must compute cursor
        // advance correctly. The resume page's grid is the first
        // emittable child → topShift = grid's marginStart. F2
        // resize fires (incoming continuation) + cursor advance
        // uses topShift-based form.
        //
        // Page 2 of a split: incoming GridContinuation(RowIndex=1,
        // Cache=null for simplicity). Grid marginTop=20, rows
        // [100, 100, 100]. Page 300. The resume emits rows 1+2
        // (= 200 extent). UsedBlockSize after grid:
        //   topShift (= marginStart = 20)
        //   + resized wrapper (= chrome 0 + emittedExtent 200 = 200)
        //   + marginEnd (= 0) = 220.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var grid = BuildGridContainerWithRowTracks(rowsPx: new[] { 100.0, 100.0, 100.0 });
        SetLengthPx(grid.Style, PropertyId.MarginTop, 20);
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        grid.AppendChild(Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement()));
        root.AppendChild(grid);

        var incoming = new BlockContinuation(
            ResumeAtChild: 0,
            ConsumedBlockSize: 100,
            LayouterState: new GridContinuation(RowIndex: 1));

        using var layouter = new BlockLayouter(root, sink, incoming);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 300);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // UsedBlockSize after: topShift (= marginTop 20) + emitted
        // (200) + marginEnd (0) = 220.
        Assert.Equal(220.0, ctx.UsedBlockSize, precision: 3);
        // Wrapper resized to chrome (0) + emittedExtent (200) = 200.
        var wrapperFragment = sink.Fragments
            .First(f => ReferenceEquals(f.Box, grid));
        Assert.Equal(200.0, wrapperFragment.BlockSize, precision: 3);
    }

    // ====================================================================
    //  Test doubles + helpers
    // ====================================================================

    /// <summary>Recording sink that captures every emitted fragment +
    /// supports rollback.</summary>
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

    /// <summary>Resolver that returns Continue for the first
    /// opportunity + BreakHere for every subsequent. Used to force a
    /// page break at a specific point.</summary>
    private sealed class BreakAfterFirstResolver : IBreakResolver
    {
        private int _calls;

        public BreakDecision ConsiderBreakAt(BreakOpportunity opportunity, FragmentainerContext ctx)
        {
            _calls++;
            return _calls == 1
                ? BreakDecision.Continue
                : new BreakDecision(BreakAction.BreakHere, 0, RewindTo: null);
        }

        public OptimizerResult ResolveBreaks(
            IReadOnlyList<BreakOpportunity> opportunities, FragmentainerContext ctx, CancellationToken cancellationToken = default)
            => OptimizerResult.Empty;

        public void RegisterCheckpoint(CheckpointLease lease) { }
        public LayoutCheckpoint? GetLastCheckpoint() => null;
        public void Dispose() { }
    }

    /// <summary>Resolver that counts RegisterCheckpoint calls.</summary>
    private sealed class RegisterCountingResolver : IBreakResolver
    {
        public int RegisterCount { get; private set; }
        private CheckpointLease _last;

        public BreakDecision ConsiderBreakAt(BreakOpportunity opportunity, FragmentainerContext ctx)
            => BreakDecision.Continue;

        public OptimizerResult ResolveBreaks(
            IReadOnlyList<BreakOpportunity> opportunities, FragmentainerContext ctx, CancellationToken cancellationToken = default)
            => OptimizerResult.Empty;

        public void RegisterCheckpoint(CheckpointLease lease)
        {
            RegisterCount++;
            if (_last.Checkpoint is not null)
            {
                LayoutCheckpointPool.Return(_last);
            }
            _last = lease;
        }

        public LayoutCheckpoint? GetLastCheckpoint() => _last.Checkpoint;

        public void Dispose()
        {
            if (_last.Checkpoint is not null)
            {
                LayoutCheckpointPool.Return(_last);
                _last = default;
            }
        }
    }

    /// <summary>Resolver that returns Continue for blocks 0 + 1 +
    /// Rewind for block 2 (first time only) + Continue for block 2
    /// on retry. Used to verify PR #23 review fix #1 (no fragment
    /// duplication after rewind).</summary>
    private sealed class RewindAtBlock2Resolver : IBreakResolver
    {
        private int _calls;
        private bool _rewoundOnce;
        private CheckpointLease _lastLease;

        public BreakDecision ConsiderBreakAt(BreakOpportunity opportunity, FragmentainerContext ctx)
        {
            _calls++;
            // First call (block 0) + second call (block 1): Continue.
            // Third call (block 2): Rewind (once). Fourth+: Continue.
            if (_calls == 3 && !_rewoundOnce)
            {
                _rewoundOnce = true;
                // The rewind-target IS the just-registered checkpoint
                // (block 2 boundary, LEC=1).
                return new BreakDecision(
                    BreakAction.Rewind, 0, _lastLease.Checkpoint);
            }
            return BreakDecision.Continue;
        }

        public OptimizerResult ResolveBreaks(
            IReadOnlyList<BreakOpportunity> opportunities, FragmentainerContext ctx, CancellationToken cancellationToken = default)
            => OptimizerResult.Empty;

        public void RegisterCheckpoint(CheckpointLease lease)
        {
            if (_lastLease.Checkpoint is not null
                && !ReferenceEquals(_lastLease.Checkpoint, lease.Checkpoint))
            {
                LayoutCheckpointPool.Return(_lastLease);
            }
            _lastLease = lease;
        }

        public LayoutCheckpoint? GetLastCheckpoint() => _lastLease.Checkpoint;

        public void Dispose()
        {
            if (_lastLease.Checkpoint is not null)
            {
                LayoutCheckpointPool.Return(_lastLease);
                _lastLease = default;
            }
        }
    }

    /// <summary>Per PR #26 review fix #2 — variant of
    /// <see cref="RewindAtBlock2Resolver"/> that adds a forced
    /// <see cref="BreakAction.BreakHere"/> on call 5 (block 3 after
    /// the retry). Drives the
    /// <c>Cycle2_rewind_retry_preserves_cumulative_consumed_block_size</c>
    /// test, which needs the layouter to reach <c>PageComplete</c> on
    /// the retry attempt so the cumulative <c>ConsumedBlockSize</c>
    /// invariant can be asserted.
    ///
    /// <para>Call sequence (4-block tree):
    /// <list type="number">
    ///   <item>Block 0 → Continue</item>
    ///   <item>Block 1 → Continue</item>
    ///   <item>Block 2 (first attempt) → Rewind</item>
    ///   <item>Block 2 (retry per PR #23 fix #1, layouter resumes from
    ///   <c>LastEmittedChildIndex + 1 = 2</c>) → Continue</item>
    ///   <item>Block 3 → BreakHere</item>
    /// </list></para></summary>
    private sealed class RewindAtBlock2ThenBreakAtBlock3Resolver : IBreakResolver
    {
        private int _calls;
        private bool _rewoundOnce;
        private CheckpointLease _lastLease;

        public BreakDecision ConsiderBreakAt(BreakOpportunity opportunity, FragmentainerContext ctx)
        {
            _calls++;
            // Call 3: rewind (only the first time we see block 2).
            if (_calls == 3 && !_rewoundOnce)
            {
                _rewoundOnce = true;
                return new BreakDecision(
                    BreakAction.Rewind, 0, _lastLease.Checkpoint);
            }
            // Call 5: BreakHere on block 3 (after retry consumed
            // block 2 on call 4). This is what produces PageComplete
            // so the cumulative ConsumedBlockSize accounting can be
            // asserted.
            if (_calls == 5)
            {
                return new BreakDecision(
                    BreakAction.BreakHere, 0, RewindTo: null);
            }
            return BreakDecision.Continue;
        }

        public OptimizerResult ResolveBreaks(
            IReadOnlyList<BreakOpportunity> opportunities, FragmentainerContext ctx, CancellationToken cancellationToken = default)
            => OptimizerResult.Empty;

        public void RegisterCheckpoint(CheckpointLease lease)
        {
            if (_lastLease.Checkpoint is not null
                && !ReferenceEquals(_lastLease.Checkpoint, lease.Checkpoint))
            {
                LayoutCheckpointPool.Return(_lastLease);
            }
            _lastLease = lease;
        }

        public LayoutCheckpoint? GetLastCheckpoint() => _lastLease.Checkpoint;

        public void Dispose()
        {
            if (_lastLease.Checkpoint is not null)
            {
                LayoutCheckpointPool.Return(_lastLease);
                _lastLease = default;
            }
        }
    }

    /// <summary>Resolver that records the captured state of each
    /// registered checkpoint for inspection. Verifies sink.Cursor +
    /// child index are correctly captured.</summary>
    private sealed class CapturingCheckpointInspector : IBreakResolver
    {
        public List<CapturedSnapshot> CapturedSnapshots { get; } = new();
        private CheckpointLease _last;

        public BreakDecision ConsiderBreakAt(BreakOpportunity opportunity, FragmentainerContext ctx)
            => BreakDecision.Continue;

        public OptimizerResult ResolveBreaks(
            IReadOnlyList<BreakOpportunity> opportunities, FragmentainerContext ctx, CancellationToken cancellationToken = default)
            => OptimizerResult.Empty;

        public void RegisterCheckpoint(CheckpointLease lease)
        {
            // Snapshot the captured state for assertion.
            var cp = lease.Checkpoint!;
            CapturedSnapshots.Add(new CapturedSnapshot(
                cp.FragmentOutputCursor,
                cp.LastEmittedChildIndex,
                cp.PageIndex,
                cp.UsedBlockSize));
            if (_last.Checkpoint is not null)
            {
                LayoutCheckpointPool.Return(_last);
            }
            _last = lease;
        }

        public LayoutCheckpoint? GetLastCheckpoint() => _last.Checkpoint;

        public void Dispose()
        {
            if (_last.Checkpoint is not null)
            {
                LayoutCheckpointPool.Return(_last);
                _last = default;
            }
        }

        public readonly record struct CapturedSnapshot(
            int FragmentOutputCursor,
            int LastEmittedChildIndex,
            int PageIndex,
            double UsedBlockSize);
    }

    // --- Tree builders -----------------------------------------

    private static (Box root, List<Box> children) BuildTree(
        params (double height, double marginTop, double marginBottom)[] specs)
    {
        var root = Box.CreateRoot(MakeStyle());
        var children = new List<Box>();
        foreach (var spec in specs)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Height, spec.height);
            SetLengthPx(style, PropertyId.MarginTop, spec.marginTop);
            SetLengthPx(style, PropertyId.MarginBottom, spec.marginBottom);
            var child = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            root.AppendChild(child);
            children.Add(child);
        }
        return (root, children);
    }

    // =====================================================================
    //  Phase 3 Task 19 cycle 1 — position: absolute. Explicit-only
    //  placement anchored to the establishing block's content box
    //  (= the fragmentainer content area for the top-level layouter),
    //  removed from normal flow.
    // =====================================================================

    [Fact]
    public void Cycle1_abspos_child_emits_at_explicit_offsets()
    {
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var absStyle = MakeStyle();
        SetKeyword(absStyle, PropertyId.Position, 2); // absolute
        SetLengthPx(absStyle, PropertyId.Top, 10);
        SetLengthPx(absStyle, PropertyId.Left, 20);
        SetLengthPx(absStyle, PropertyId.Width, 50);
        SetLengthPx(absStyle, PropertyId.Height, 30);
        var abs = Box.ForElement(BoxKind.BlockContainer, absStyle, MakeElement());
        root.AppendChild(abs);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        var fragment = sink.Fragments.First(f => ReferenceEquals(f.Box, abs));
        // CB = fragmentainer content area origin (0,0); top/left anchor.
        Assert.Equal(20.0, fragment.InlineOffset, precision: 3);
        Assert.Equal(10.0, fragment.BlockOffset, precision: 3);
        Assert.Equal(50.0, fragment.InlineSize, precision: 3);
        Assert.Equal(30.0, fragment.BlockSize, precision: 3);
        // Abspos box is out-of-flow → UsedBlockSize NOT advanced by it.
        Assert.Equal(0.0, ctx.UsedBlockSize, precision: 3);
    }

    [Fact]
    public void Cycle1_abspos_is_removed_from_flow_siblings_unaffected()
    {
        // Two in-flow blocks bracketing an abspos box. The abspos box
        // must NOT advance the cursor + must NOT break margin
        // adjacency — the two in-flow blocks stack as if the abspos
        // box weren't there.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var firstStyle = MakeStyle();
        SetLengthPx(firstStyle, PropertyId.Height, 100);
        var first = Box.ForElement(BoxKind.BlockContainer, firstStyle, MakeElement());
        root.AppendChild(first);

        var absStyle = MakeStyle();
        SetKeyword(absStyle, PropertyId.Position, 2);
        SetLengthPx(absStyle, PropertyId.Top, 500);
        SetLengthPx(absStyle, PropertyId.Left, 0);
        SetLengthPx(absStyle, PropertyId.Width, 50);
        SetLengthPx(absStyle, PropertyId.Height, 50);
        var abs = Box.ForElement(BoxKind.BlockContainer, absStyle, MakeElement());
        root.AppendChild(abs);

        var secondStyle = MakeStyle();
        SetLengthPx(secondStyle, PropertyId.Height, 100);
        var second = Box.ForElement(BoxKind.BlockContainer, secondStyle, MakeElement());
        root.AppendChild(second);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        var firstFrag = sink.Fragments.First(f => ReferenceEquals(f.Box, first));
        var secondFrag = sink.Fragments.First(f => ReferenceEquals(f.Box, second));
        // first at block 0; second directly below at block 100 (= the
        // abspos box between them in source order didn't displace it).
        Assert.Equal(0.0, firstFrag.BlockOffset, precision: 3);
        Assert.Equal(100.0, secondFrag.BlockOffset, precision: 3);
        // abspos emitted at its own anchored position (block 500).
        var absFrag = sink.Fragments.First(f => ReferenceEquals(f.Box, abs));
        Assert.Equal(500.0, absFrag.BlockOffset, precision: 3);
        // UsedBlockSize reflects only the two in-flow blocks (200).
        Assert.Equal(200.0, ctx.UsedBlockSize, precision: 3);
    }

    [Fact]
    public void Cycle2b_abspos_auto_top_uses_static_position()
    {
        // Per Phase 3 Task 19 cycle 2b — `top` auto (unset) is no longer
        // a deferral: top auto + bottom auto + height given → top =
        // static position (CB origin 0). The box emits at (left, 0).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var absStyle = MakeStyle();
        SetKeyword(absStyle, PropertyId.Position, 2);
        // top intentionally unset (auto) → static position.
        SetLengthPx(absStyle, PropertyId.Left, 20);
        SetLengthPx(absStyle, PropertyId.Width, 50);
        SetLengthPx(absStyle, PropertyId.Height, 30);
        var abs = Box.ForElement(BoxKind.BlockContainer, absStyle, MakeElement());
        root.AppendChild(abs);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        var absFrag = sink.Fragments.First(f => ReferenceEquals(f.Box, abs));
        Assert.Equal(20.0, absFrag.InlineOffset, precision: 3);
        Assert.Equal(0.0, absFrag.BlockOffset, precision: 3);  // static position
        Assert.Equal(50.0, absFrag.InlineSize, precision: 3);
        Assert.Equal(30.0, absFrag.BlockSize, precision: 3);
    }

    [Fact]
    public void Cycle1_abspos_inner_content_dispatched_at_translated_coords()
    {
        // An abspos box with an in-flow child block: the child's
        // fragment must be emitted at the abspos box's content origin
        // (= translated by the box's resolved position).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var absStyle = MakeStyle();
        SetKeyword(absStyle, PropertyId.Position, 2);
        SetLengthPx(absStyle, PropertyId.Top, 40);
        SetLengthPx(absStyle, PropertyId.Left, 60);
        SetLengthPx(absStyle, PropertyId.Width, 200);
        SetLengthPx(absStyle, PropertyId.Height, 200);
        var abs = Box.ForElement(BoxKind.BlockContainer, absStyle, MakeElement());

        var innerStyle = MakeStyle();
        SetLengthPx(innerStyle, PropertyId.Height, 30);
        var inner = Box.ForElement(BoxKind.BlockContainer, innerStyle, MakeElement());
        abs.AppendChild(inner);
        root.AppendChild(abs);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        var innerFrag = sink.Fragments.First(f => ReferenceEquals(f.Box, inner));
        // Inner child at the abspos box's content origin (cycle 1 = box
        // border-box origin, no padding inset): block 40, inline 60.
        Assert.Equal(60.0, innerFrag.InlineOffset, precision: 3);
        Assert.Equal(40.0, innerFrag.BlockOffset, precision: 3);
        Assert.Equal(30.0, innerFrag.BlockSize, precision: 3);
    }

    // --- Cycle 2a: nearest-positioned-ancestor containing block -------

    [Fact]
    public void Cycle2a_abspos_anchors_to_positioned_ancestor_padding_box()
    {
        // A position:relative ancestor establishes the containing block.
        // The relative parent sits at block 50 (after a 50px spacer)
        // with a 10px border; its abspos child with top:10 left:20
        // anchors to the parent's PADDING box origin (border-box origin
        // + border widths), NOT the ICB.
        //
        //   spacer: height 50 → relative parent border-box top = 50.
        //   relative parent: border 10, height 200 → padding-box origin
        //     = (10 inline, 50 + 10 = 60 block).
        //   abs child top:10 left:20 → (10+20=30 inline, 60+10=70 block).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var spacerStyle = MakeStyle();
        SetLengthPx(spacerStyle, PropertyId.Height, 50);
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, spacerStyle, MakeElement()));

        var relStyle = MakeStyle();
        SetKeyword(relStyle, PropertyId.Position, 1); // relative
        SetLengthPx(relStyle, PropertyId.Height, 200);
        SolidBorders(relStyle);
        SetLengthPx(relStyle, PropertyId.BorderTopWidth, 10);
        SetLengthPx(relStyle, PropertyId.BorderLeftWidth, 10);
        SetLengthPx(relStyle, PropertyId.BorderRightWidth, 10);
        SetLengthPx(relStyle, PropertyId.BorderBottomWidth, 10);
        var rel = Box.ForElement(BoxKind.BlockContainer, relStyle, MakeElement());
        root.AppendChild(rel);

        var absStyle = MakeStyle();
        SetKeyword(absStyle, PropertyId.Position, 2); // absolute
        SetLengthPx(absStyle, PropertyId.Top, 10);
        SetLengthPx(absStyle, PropertyId.Left, 20);
        SetLengthPx(absStyle, PropertyId.Width, 30);
        SetLengthPx(absStyle, PropertyId.Height, 30);
        var abs = Box.ForElement(BoxKind.BlockContainer, absStyle, MakeElement());
        rel.AppendChild(abs);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        var absFrag = sink.Fragments.First(f => ReferenceEquals(f.Box, abs));
        // Anchored to the relative parent's padding box, NOT the ICB.
        Assert.Equal(30.0, absFrag.InlineOffset, precision: 3); // 10(border)+20(left)
        Assert.Equal(70.0, absFrag.BlockOffset, precision: 3);  // 50(spacer)+10(border)+10(top)
        Assert.Equal(30.0, absFrag.InlineSize, precision: 3);
        Assert.Equal(30.0, absFrag.BlockSize, precision: 3);
    }

    [Fact]
    public void Cycle2a_abspos_no_positioned_ancestor_falls_back_to_icb()
    {
        // Sanity: with the relative parent made static, the abspos box
        // falls back to the ICB (= fragmentainer origin), so top/left
        // are NOT offset by the (now static) ancestor.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var staticParentStyle = MakeStyle();
        SetLengthPx(staticParentStyle, PropertyId.Height, 200);
        var staticParent = Box.ForElement(
            BoxKind.BlockContainer, staticParentStyle, MakeElement());
        root.AppendChild(staticParent);

        var absStyle = MakeStyle();
        SetKeyword(absStyle, PropertyId.Position, 2);
        SetLengthPx(absStyle, PropertyId.Top, 10);
        SetLengthPx(absStyle, PropertyId.Left, 20);
        SetLengthPx(absStyle, PropertyId.Width, 30);
        SetLengthPx(absStyle, PropertyId.Height, 30);
        var abs = Box.ForElement(BoxKind.BlockContainer, absStyle, MakeElement());
        staticParent.AppendChild(abs);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        var absFrag = sink.Fragments.First(f => ReferenceEquals(f.Box, abs));
        // ICB anchoring: top/left from fragmentainer origin (0,0).
        Assert.Equal(20.0, absFrag.InlineOffset, precision: 3);
        Assert.Equal(10.0, absFrag.BlockOffset, precision: 3);
    }

    [Fact]
    public void Cycle2b_abspos_under_relative_ancestor_with_offsets_applies_shift()
    {
        // Per Phase 3 Task 19 cycle 2b — a `position: relative` ancestor
        // with explicit inset offsets (top/left) has its §9.4.3 shift
        // APPLIED to the abspos descendant's CB origin (cycle 2a
        // deferred this). The relative parent's flow position is (0, 0)
        // (first child, no border); its relative shift is (left 10,
        // top 50) → shifted padding-box origin (10, 50). The abspos
        // child top:0 left:0 → (10, 50).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var relStyle = MakeStyle();
        SetKeyword(relStyle, PropertyId.Position, 1); // relative
        SetLengthPx(relStyle, PropertyId.Height, 200);
        SetLengthPx(relStyle, PropertyId.Top, 50);   // relative offset
        SetLengthPx(relStyle, PropertyId.Left, 10);
        var rel = Box.ForElement(BoxKind.BlockContainer, relStyle, MakeElement());
        root.AppendChild(rel);

        var absStyle = MakeStyle();
        SetKeyword(absStyle, PropertyId.Position, 2); // absolute
        SetLengthPx(absStyle, PropertyId.Top, 0);
        SetLengthPx(absStyle, PropertyId.Left, 0);
        SetLengthPx(absStyle, PropertyId.Width, 30);
        SetLengthPx(absStyle, PropertyId.Height, 30);
        var abs = Box.ForElement(BoxKind.BlockContainer, absStyle, MakeElement());
        rel.AppendChild(abs);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        var absFrag = sink.Fragments.First(f => ReferenceEquals(f.Box, abs));
        // CB origin = rel flow (0,0) + relative shift (10, 50); abspos
        // top:0 left:0 → (10, 50).
        Assert.Equal(10.0, absFrag.InlineOffset, precision: 3);
        Assert.Equal(50.0, absFrag.BlockOffset, precision: 3);
    }

    [Fact]
    public void Cycle2a_abspos_relative_ancestor_no_offsets_still_anchors()
    {
        // Sanity: a `position: relative` ancestor WITHOUT offsets is the
        // fully-supported case (the cycle-2a headline) — the abspos
        // child still anchors to its padding box, not dropped.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var relStyle = MakeStyle();
        SetKeyword(relStyle, PropertyId.Position, 1);
        SetLengthPx(relStyle, PropertyId.Height, 200);
        var rel = Box.ForElement(BoxKind.BlockContainer, relStyle, MakeElement());
        root.AppendChild(rel);

        var absStyle = MakeStyle();
        SetKeyword(absStyle, PropertyId.Position, 2);
        SetLengthPx(absStyle, PropertyId.Top, 10);
        SetLengthPx(absStyle, PropertyId.Left, 20);
        SetLengthPx(absStyle, PropertyId.Width, 30);
        SetLengthPx(absStyle, PropertyId.Height, 30);
        var abs = Box.ForElement(BoxKind.BlockContainer, absStyle, MakeElement());
        rel.AppendChild(abs);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        var absFrag = sink.Fragments.First(f => ReferenceEquals(f.Box, abs));
        Assert.Equal(20.0, absFrag.InlineOffset, precision: 3); // rel at (0,0), no border
        Assert.Equal(10.0, absFrag.BlockOffset, precision: 3);
    }

    [Fact]
    public void PostPr114_abspos_relative_ancestor_percent_offsets_resolve_per_axis()
    {
        // Per post-PR-#114 review P1#2 — a `position: relative` ancestor
        // with PERCENTAGE inset offsets shifts per-axis: left/right
        // resolve against the containing-block WIDTH, top/bottom against
        // its HEIGHT. The ancestor fills the 600px content width and is
        // 400px tall (distinct extents) with left:50% top:50%. inline
        // shift = 50% × 600 = 300; block shift = 50% × 400 = 200 (NOT 50%
        // × 600 = 300, the pre-fix wrong-axis result that resolved top
        // against the inline extent). The abspos child (top:0 left:0)
        // anchors to the shifted padding-box origin (300, 200) — the
        // block offset (200 ≠ 300) is the proof the fix uses the HEIGHT.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var relStyle = MakeStyle();
        SetKeyword(relStyle, PropertyId.Position, 1); // relative
        SetLengthPx(relStyle, PropertyId.Height, 400);
        SetPercentage(relStyle, PropertyId.Left, 50);
        SetPercentage(relStyle, PropertyId.Top, 50);
        var rel = Box.ForElement(BoxKind.BlockContainer, relStyle, MakeElement());
        root.AppendChild(rel);

        var absStyle = MakeStyle();
        SetKeyword(absStyle, PropertyId.Position, 2); // absolute
        SetLengthPx(absStyle, PropertyId.Top, 0);
        SetLengthPx(absStyle, PropertyId.Left, 0);
        SetLengthPx(absStyle, PropertyId.Width, 30);
        SetLengthPx(absStyle, PropertyId.Height, 30);
        var abs = Box.ForElement(BoxKind.BlockContainer, absStyle, MakeElement());
        rel.AppendChild(abs);

        using var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        var absFrag = sink.Fragments.First(f => ReferenceEquals(f.Box, abs));
        Assert.Equal(300.0, absFrag.InlineOffset, precision: 3);  // 50% × width 600
        Assert.Equal(200.0, absFrag.BlockOffset, precision: 3);   // 50% × HEIGHT 400
    }

    [Fact]
    public void Task20Cycle1_fixed_box_repeats_on_every_page_anchored_to_page()
    {
        // Per Phase 3 Task 20 cycle 1 — a `position: fixed` box repeats
        // on EVERY page, anchored to the page (ICB). Three 250px in-flow
        // blocks on a 300px page paginate to multiple pages; the fixed
        // box (top:5 left:5, 40×40) is out-of-flow (doesn't affect the
        // page count) and must emit exactly once per page, all at (5, 5).
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        Box MakeBlock(double h)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, h);
            return Box.ForElement(BoxKind.BlockContainer, s, MakeElement());
        }
        root.AppendChild(MakeBlock(250));
        root.AppendChild(MakeBlock(250));
        root.AppendChild(MakeBlock(250));

        var fixedStyle = MakeStyle();
        SetKeyword(fixedStyle, PropertyId.Position, 3); // fixed
        SetLengthPx(fixedStyle, PropertyId.Top, 5);
        SetLengthPx(fixedStyle, PropertyId.Left, 5);
        SetLengthPx(fixedStyle, PropertyId.Width, 40);
        SetLengthPx(fixedStyle, PropertyId.Height, 40);
        var fixedBox = Box.ForElement(BoxKind.BlockContainer, fixedStyle, MakeElement());
        root.AppendChild(fixedBox);

        LayoutContinuation? incoming = null;
        var pageCount = 0;
        const int maxPages = 8;
        LayoutAttemptOutcome lastOutcome;
        do
        {
            using var pageLayouter = new BlockLayouter(root, sink, incoming);
            var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 300);
            var layoutCtx = new LayoutContext(ctx);
            using var resolver = new BreakResolver();
            using var coordinator = new RecordingCoordinator();
            var result = coordinator.RunAndAssertNotNeedsRewind(
                pageLayouter, ctx, ref layoutCtx, resolver);
            lastOutcome = result.Outcome;
            incoming = result.Continuation;
            pageCount++;
        }
        while (lastOutcome != LayoutAttemptOutcome.AllDone
            && incoming is not null
            && pageCount < maxPages);

        Assert.Equal(LayoutAttemptOutcome.AllDone, lastOutcome);
        Assert.True(pageCount >= 2, $"expected multi-page, got {pageCount}");
        // The fixed box emits exactly once per page, all at the same
        // page-anchored position.
        var fixedFrags = sink.Fragments.Where(f => ReferenceEquals(f.Box, fixedBox)).ToList();
        Assert.Equal(pageCount, fixedFrags.Count);
        Assert.All(fixedFrags, f =>
        {
            Assert.Equal(5.0, f.InlineOffset, precision: 3);
            Assert.Equal(5.0, f.BlockOffset, precision: 3);
            Assert.Equal(40.0, f.InlineSize, precision: 3);
            Assert.Equal(40.0, f.BlockSize, precision: 3);
        });
    }

    private sealed class SyntheticShaperResolver : IShaperResolver
    {
        private readonly HbShaper _shaper = new(SyntheticFont.Build(), fontSizePx: 12);
        public HbShaper Resolve(ComputedStyle style) => _shaper;
        public void Dispose() => _shaper.Dispose();
    }

    private static ComputedStyle MakeStyle() => ComputedStyle.RentForExclusiveTesting();

    private static void SetLengthPx(ComputedStyle style, PropertyId id, double px) =>
        style.Set(id, ComputedSlot.FromLengthPx(px));

    private static void SetPercentage(ComputedStyle style, PropertyId id, double pct) =>
        style.Set(id, ComputedSlot.FromPercentage(pct));

    private static void SetKeyword(ComputedStyle style, PropertyId id, int keywordIndex) =>
        style.Set(id, ComputedSlot.FromKeyword(keywordIndex));

    // Per CSS Backgrounds & Borders 3 §4.3 the used border-width is 0 unless the
    // matching border-style is a visible value, so box-model tests that set a
    // synthetic border-*-width must also declare a style (solid = keyword 4) for it
    // to contribute to box sizing / offsets.
    private static void SolidBorders(ComputedStyle style)
    {
        SetKeyword(style, PropertyId.BorderTopStyle, 4);
        SetKeyword(style, PropertyId.BorderRightStyle, 4);
        SetKeyword(style, PropertyId.BorderBottomStyle, 4);
        SetKeyword(style, PropertyId.BorderLeftStyle, 4);
    }

    private static AngleSharp.Dom.IElement MakeElement()
    {
        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var doc = parser.ParseDocument("<div></div>");
        return doc.CreateElement("div");
    }
}
