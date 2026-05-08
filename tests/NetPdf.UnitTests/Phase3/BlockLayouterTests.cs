// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Threading;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
using NetPdf.Paginate;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>
/// Phase 3 Task 7 cycle 1 — BlockLayouter tests. Drives the layouter
/// through a synthesized box tree (3 stacked block boxes with explicit
/// dimensions) + verifies position math, page-break dispatch via the
/// IBreakResolver, continuation token shape, and the strategy /
/// cancellation hooks inherited from ILayouter.
///
/// <para>Cycle 1 covers explicit-px dimensions only. Cycle 2 adds
/// margin collapsing tests; cycle 3 adds auto / percentage resolution
/// tests; Phase 3 Task 8 adds float-interaction tests.</para>
/// </summary>
public sealed class BlockLayouterTests
{
    // --- Trivial: a single block fits ---------------------------------

    [Fact]
    public void Layouter_emits_single_block_fragment_when_content_fits()
    {
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 200, marginTop: 10, marginBottom: 10),
            (height: 150, marginTop: 5, marginBottom: 5));

        var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Equal(2, sink.Fragments.Count);

        // First fragment: block-axis offset 0, block-axis size 220 (10 + 200 + 10).
        Assert.Equal(0, sink.Fragments[0].BlockOffset);
        Assert.Equal(220, sink.Fragments[0].BlockSize);

        // Second fragment: starts at 220 (after first), size 160 (5 + 150 + 5).
        Assert.Equal(220, sink.Fragments[1].BlockOffset);
        Assert.Equal(160, sink.Fragments[1].BlockSize);
    }

    [Fact]
    public void Layouter_inline_offset_uses_margin_left_and_size_uses_content_inline()
    {
        var sink = new RecordingFragmentSink();
        var style = MakeStyle();
        SetLengthPx(style, PropertyId.MarginLeft, 30);
        SetLengthPx(style, PropertyId.MarginRight, 20);
        SetLengthPx(style, PropertyId.Height, 100);

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, style, MakeElement()));

        var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Inline offset = MarginLeft = 30
        Assert.Equal(30, sink.Fragments[0].InlineOffset);
        // Inline size = ContentInlineSize - MarginLeft - MarginRight = 600 - 30 - 20 = 550
        Assert.Equal(550, sink.Fragments[0].InlineSize);
    }

    // --- Page break: third box doesn't fit ---------------------------

    [Fact]
    public void Layouter_returns_PageComplete_when_next_block_overflows()
    {
        var sink = new RecordingFragmentSink();
        // Page = 800. Block 1 = 250 (incl margin), block 2 = 250, block 3 = 350.
        // After 2 blocks: used = 500. Block 3 would push to 850 > 800.
        var (root, _) = BuildTree(
            (height: 230, marginTop: 10, marginBottom: 10),  // 250
            (height: 230, marginTop: 10, marginBottom: 10),  // 250
            (height: 330, marginTop: 10, marginBottom: 10)); // 350

        var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        // Two blocks emitted before the break.
        Assert.Equal(2, sink.Fragments.Count);
        // Continuation says "resume at child 2" (the 3rd block).
        Assert.NotNull(result.Continuation);
        var blockCont = Assert.IsType<BlockContinuation>(result.Continuation);
        Assert.Equal(2, blockCont.ResumeAtChild);
        // ConsumedBlockSize matches what was placed on this page.
        Assert.Equal(500, blockCont.ConsumedBlockSize);
    }

    [Fact]
    public void Layouter_resumes_at_continuation_child_index()
    {
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 230, marginTop: 10, marginBottom: 10),  // 250 — already on page 1
            (height: 230, marginTop: 10, marginBottom: 10),  // 250 — already on page 1
            (height: 330, marginTop: 10, marginBottom: 10)); // 350 — RESUMING here on page 2

        var continuation = new BlockContinuation(ResumeAtChild: 2, ConsumedBlockSize: 500);
        var layouter = new BlockLayouter(root, sink, continuation);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        // Only one fragment emitted — the resumed block.
        Assert.Single(sink.Fragments);
        Assert.Equal(350, sink.Fragments[0].BlockSize);
    }

    // --- Skip non-block-level children -------------------------------

    [Fact]
    public void Layouter_skips_inline_children_silently()
    {
        // Cycle 1: inline content not yet wired (Task 10 InlineLayouter).
        // The layouter must not crash on inline children — it skips them.
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        // First a block, then a TextRun (inline), then another block.
        var blockStyle = MakeStyle();
        SetLengthPx(blockStyle, PropertyId.Height, 100);
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement()));

        root.AppendChild(Box.TextRun("inline text", MakeStyle()));

        var blockStyle2 = MakeStyle();
        SetLengthPx(blockStyle2, PropertyId.Height, 50);
        root.AppendChild(Box.ForElement(BoxKind.BlockContainer, blockStyle2, MakeElement()));

        var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        // Only the two block children emit fragments; the TextRun is skipped.
        Assert.Equal(2, sink.Fragments.Count);
    }

    // --- Empty input -------------------------------------------------

    [Fact]
    public void Layouter_returns_AllDone_for_empty_root()
    {
        var sink = new RecordingFragmentSink();
        var root = Box.CreateRoot(MakeStyle());

        var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Empty(sink.Fragments);
    }

    // --- Constructor null guards ------------------------------------

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

        var layouter = new BlockLayouter(root, sink);
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

    // --- Force break ------------------------------------------------

    [Fact]
    public void Layouter_emits_PageComplete_when_resolver_returns_BreakHere_for_force_break()
    {
        // The greedy BreakResolver returns BreakHere for ForceBreak
        // opportunities even when the chunk fits. We test this end-to-end
        // by manually synthesizing a force-break opportunity through a
        // custom resolver.
        var sink = new RecordingFragmentSink();
        var (root, _) = BuildTree(
            (height: 100, marginTop: 0, marginBottom: 0),  // block 0
            (height: 100, marginTop: 0, marginBottom: 0)); // block 1

        var layouter = new BlockLayouter(root, sink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        // Custom resolver that ALWAYS says BreakHere on the second
        // ConsiderBreakAt call.
        using var resolver = new BreakAfterFirstResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        // First block emitted before the break-here decision.
        Assert.Single(sink.Fragments);
        var cont = Assert.IsType<BlockContinuation>(result.Continuation);
        Assert.Equal(1, cont.ResumeAtChild);  // resume at block 1
    }

    // --- IBlockFragmentSink contract ---------------------------

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
            IReadOnlyList<BreakOpportunity> opportunities, FragmentainerContext ctx)
            => OptimizerResult.Empty;

        public void RegisterCheckpoint(CheckpointLease lease) { }
        public LayoutCheckpoint? GetLastCheckpoint() => null;
        public void Dispose() { }
    }

    // --- Tree builders -----------------------------------------

    /// <summary>Build a root box with N block children, each with the
    /// given dimensions. Returns root + the list of created child boxes.</summary>
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

    private static ComputedStyle MakeStyle() => ComputedStyle.RentForExclusiveTesting();

    private static void SetLengthPx(ComputedStyle style, PropertyId id, double px) =>
        style.Set(id, ComputedSlot.FromLengthPx(px));

    private static AngleSharp.Dom.IElement MakeElement()
    {
        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var doc = parser.ParseDocument("<div></div>");
        return doc.CreateElement("div");
    }
}
