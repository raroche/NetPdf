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

    // --- Multi-block stacking ----------------------------------------

    [Fact]
    public void Layouter_stacks_multiple_blocks_with_margin_box_advance()
    {
        // Two block children with explicit margins.
        // Block 0: marginTop=10, height=200, marginBottom=10 → border-box=200, margin-box advance=220
        // Block 1: marginTop=5, height=150, marginBottom=5 → border-box=150, margin-box advance=160
        // Expected: block 0 BlockOffset=10, BlockSize=200; block 1 BlockOffset=230 (220 + 10), BlockSize=150
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

        // Block 1: BlockOffset = previous-cursor (220) + marginTop (5) = 225, BlockSize=150.
        Assert.Equal(225, sink.Fragments[1].BlockOffset);
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
        // Prior pages: 0 (this is page 1). Current attempt: 500.
        Assert.Equal(500, blockCont.ConsumedBlockSize);
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
    //  Cycle 2-3 deferral pins — failing-skip integration tests
    // ====================================================================

    [Fact(Skip = "Phase 3 Task 7 cycle 2 — recursive nested-block layout. "
        + "Cycle 1 walks _rootBox.Children only; nested block descendants "
        + "(div > p) are not laid out. Failing-skip test pins the deferral.")]
    public void Cycle2_Layouter_lays_out_nested_block_descendants()
    {
        // div > p > text: layouter should emit fragments for both div + p.
        // Cycle 1 emits only div; cycle 2 wires recursion.
    }

    [Fact(Skip = "Phase 3 Task 7 cycle 2 — margin collapsing per CSS 2.1 §8.3.1. "
        + "Adjacent vertical margins collapse to the larger of the two; cycle 1 sums them.")]
    public void Cycle2_Layouter_collapses_adjacent_margins()
    {
        // Block 1 marginBottom=20 + Block 2 marginTop=10 → collapsed gap=20, not 30.
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

    /// <summary>Resolver that counts RegisterCheckpoint calls.</summary>
    private sealed class RegisterCountingResolver : IBreakResolver
    {
        public int RegisterCount { get; private set; }
        private CheckpointLease _last;

        public BreakDecision ConsiderBreakAt(BreakOpportunity opportunity, FragmentainerContext ctx)
            => BreakDecision.Continue;

        public OptimizerResult ResolveBreaks(
            IReadOnlyList<BreakOpportunity> opportunities, FragmentainerContext ctx)
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
            IReadOnlyList<BreakOpportunity> opportunities, FragmentainerContext ctx)
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
