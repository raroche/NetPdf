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
