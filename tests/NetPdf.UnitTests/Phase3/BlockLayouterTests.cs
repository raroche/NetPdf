// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Threading;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
using NetPdf.Paginate;
using NetPdf.Paginate.Diagnostics;
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

    [Fact(Skip = "Phase 3 Task 7 cycle 2 — recursive nested-block layout. "
        + "Cycle 1 walks _rootBox.Children only; nested block descendants "
        + "(div > p) are not laid out. Failing-skip test pins the deferral.")]
    public void Cycle2_Layouter_lays_out_nested_block_descendants()
    {
        // div > p > text: layouter should emit fragments for both div + p.
        // Cycle 1 emits only div; cycle 2 wires recursion.
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
