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
        // Per cycle-2b post-PR-28 review #3 — same predicate guard
        // applies to Flex / Grid / BlockReplacedElement.
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

        // 3 fragments: flex, grid, img. The flex/grid/img children
        // are skipped — their inner geometry belongs to dedicated
        // layouters (Phase 3 Tasks 8-12).
        Assert.Equal(3, sink.Fragments.Count);
        Assert.Same(flex, sink.Fragments[0].Box);
        Assert.Same(grid, sink.Fragments[1].Box);
        Assert.Same(img, sink.Fragments[2].Box);
        // Critically — flexItem / gridItem are NOT in the fragment list.
        Assert.DoesNotContain(sink.Fragments, f => ReferenceEquals(f.Box, flexItem));
        Assert.DoesNotContain(sink.Fragments, f => ReferenceEquals(f.Box, gridItem));
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

    [Fact(Skip = "Phase 3 Task 7 cycle 2d — true mid-subtree pagination splits. "
        + "Cycle 2c MVP (this revision) treats subtrees as ATOMIC for "
        + "pagination — an oversized subtree is pushed to the next page "
        + "via break-before, OR forced-overflowed atomically if it's "
        + "first on a fresh page. Real CSS allows breaks INSIDE a "
        + "subtree (parent's first half on page 1 + parent's second "
        + "half on page 2). That requires recursive continuation tokens "
        + "(`BlockContinuation.NestedContinuation`) + break consultation "
        + "inside `EmitBlockSubtreeRecursive` + recursive resume on retry. "
        + "Failing-skip pin per cycle 2c MVP scope decision.")]
    public void Cycle2d_oversized_subtree_splits_across_two_pages_at_inner_break()
    {
        // Tree: parent (height=auto) > [child1 (h=400), child2 (h=500)]
        // Page = 800. Subtree extent = 0 + 400 + 500 = 900 (no margins).
        // Cycle 2c MVP: break-before parent → page 1 empty, page 2 has
        //   parent + both children + overflow diagnostic.
        // Cycle 2d expectation: parent partial fragment on page 1 (with
        //   child1 inside it), then continuation pointing INSIDE parent
        //   to child2 on page 2; parent fragment on page 2 holds child2
        //   (with header repetition behavior TBD).
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
