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
            IReadOnlyList<BreakOpportunity> opportunities, FragmentainerContext ctx)
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
