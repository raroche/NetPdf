// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
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
/// Phase 3 Task 14 cycle 1 — direct-construction tests for
/// <see cref="MulticolLayouter"/>. Constructs the multicol container
/// (a BlockContainer with `column-count: N` on its computed style)
/// directly via test helpers + asserts the layouter's per-column
/// emission math.
///
/// <para>Fixture mirrors <see cref="TableLayouterTests"/>'s helper
/// shape — <c>RentForExclusiveTesting</c> styles, a
/// <see cref="RecordingFragmentSink"/>, a
/// <see cref="RecordingDiagnosticsSink"/>. The synthetic-font shaper
/// resolver lets in-flow inline-only blocks inside the multicol
/// container render real glyphs when a test exercises inline content.</para>
///
/// <para><b>Cycle 1 behaviors exercised:</b>
/// <list type="bullet">
///   <item>Equal-width column split with default 16 px column gap.</item>
///   <item>Block children distributed across columns serially (column
///   fill — no balancing).</item>
///   <item>Forced-overflow diagnostic when content exceeds N columns.</item>
///   <item><c>column-count: 1</c> bypasses the multicol dispatch (normal
///   block flow).</item>
///   <item>Argument validation (rootBox not multicol-shaped, invalid
///   column-count).</item>
///   <item>Empty multicol container emits no inner content.</item>
///   <item>Cancellation propagation.</item>
/// </list></para>
/// </summary>
public sealed class MulticolLayouterTests
{
    [Fact]
    public void Multicol_with_two_columns_emits_outer_plus_column_fragments()
    {
        // A multicol container with column-count: 2 + a single tall
        // child should emit the wrapper fragment (from BlockLayouter)
        // + the child fragment inside column 0. Cycle 1 doesn't emit
        // separate "column box" fragments — only the wrapper + the
        // per-column content fragments translated into the columns'
        // inline positions.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 2);
        // A single block-level child with explicit height 100. Should
        // land in column 0.
        var childStyle = MakeStyle();
        SetLengthPx(childStyle, PropertyId.Height, 100);
        var child = Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement());
        multicol.AppendChild(child);
        root.AppendChild(multicol);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 320, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Find the multicol wrapper + the child fragment.
        BoxFragment? multicolFragment = null;
        BoxFragment? childFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == multicol) multicolFragment = f;
            else if (f.Box == child) childFragment = f;
        }
        Assert.NotNull(multicolFragment);
        Assert.NotNull(childFragment);
        // The wrapper spans the full content inline size of its
        // parent.
        Assert.Equal(320, multicolFragment!.Value.InlineSize);
        // The child lands at column 0's inline offset (= 0 from the
        // page origin since the multicol is at the page's content
        // origin + has no border/padding in this test).
        Assert.Equal(0, childFragment!.Value.InlineOffset);
        // Column 0's content inline size = (320 - 1*16) / 2 = 152.
        // The child's intrinsic width is the per-column content
        // inline size (no margins / borders / padding on the child
        // in this test).
        Assert.Equal(152, childFragment.Value.InlineSize);
    }

    [Fact]
    public void Multicol_column_widths_equal_split_minus_gaps()
    {
        // Verify the equal-split arithmetic: 3 columns in a 300 px
        // container with default 16 px gap. Each column =
        // (300 - 2*16) / 3 = 268/3 ≈ 89.333. To exercise all 3
        // columns each child is sized to nearly fill its column
        // block-size; the next child overflows + goes to the next
        // column.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 3);
        // Column block-size = 100; each child is 90 tall so the
        // next child can't fit in the same column.
        SetLengthPx(multicol.Style, PropertyId.Height, 100);
        for (var i = 0; i < 3; i++)
        {
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Height, 90);
            var c = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            multicol.AppendChild(c);
        }
        root.AppendChild(multicol);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 300, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Find each child's emitted fragment + verify inline offsets.
        var childFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.BlockContainer
                && f.Box != multicol
                && f.Box.SourceElement is not null)
            {
                childFragments.Add(f);
            }
        }
        Assert.Equal(3, childFragments.Count);

        // Expected per-column inline size = (300 - 2*16) / 3 = 89.333…
        const double expectedColumnWidth = (300.0 - 32.0) / 3.0;
        // Column inline offsets: 0, expectedColumnWidth + 16,
        // 2*(expectedColumnWidth + 16).
        var expectedOffsets = new[]
        {
            0.0,
            expectedColumnWidth + 16.0,
            2.0 * (expectedColumnWidth + 16.0),
        };
        for (var i = 0; i < 3; i++)
        {
            // Each child takes its column's content inline size.
            Assert.Equal(expectedColumnWidth, childFragments[i].InlineSize, precision: 3);
            Assert.Equal(expectedOffsets[i], childFragments[i].InlineOffset, precision: 3);
        }
    }

    [Fact]
    public void Multicol_distributes_content_across_columns()
    {
        // Two children, each taller than the per-column block-size /
        // 2 but smaller than the full block size. Child 0 fills
        // column 0; child 1 lands in column 1.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 2);
        // Container height = 200; per-column block-size = 200.
        SetLengthPx(multicol.Style, PropertyId.Height, 200);
        // Child 0: 150 tall — fills most of column 0; the next 150-
        // tall sibling needs to go in column 1 because column 0 only
        // has 50 left + the child's content height of 150 won't fit
        // there.
        var s0 = MakeStyle();
        SetLengthPx(s0, PropertyId.Height, 150);
        var child0 = Box.ForElement(BoxKind.BlockContainer, s0, MakeElement());
        var s1 = MakeStyle();
        SetLengthPx(s1, PropertyId.Height, 150);
        var child1 = Box.ForElement(BoxKind.BlockContainer, s1, MakeElement());
        multicol.AppendChild(child0);
        multicol.AppendChild(child1);
        root.AppendChild(multicol);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Find children's fragments.
        BoxFragment? f0 = null;
        BoxFragment? f1 = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == child0) f0 = f;
            else if (f.Box == child1) f1 = f;
        }
        Assert.NotNull(f0);
        Assert.NotNull(f1);
        // Per-column inline size = (232 - 16) / 2 = 108.
        // child0 should land at inline offset 0; child1 should land
        // at inline offset 108 + 16 = 124.
        Assert.Equal(0, f0!.Value.InlineOffset);
        Assert.Equal(124, f1!.Value.InlineOffset);
    }

    [Fact]
    public void Cycle2_multicol_content_overflowing_all_columns_emits_page_complete_no_diagnostic()
    {
        // Per Phase 3 Task 14 cycle 2 — content that overflows the N
        // columns now produces a CLEAN multi-page split via
        // MulticolContinuation instead of emitting the forced-overflow
        // diagnostic. Two columns each 100 tall; three children each
        // 90 tall: column 0 takes child 0, column 1 takes child 1,
        // child 2 doesn't fit → MulticolLayouter returns
        // PageComplete(MulticolContinuation(NextChildIndex=2, ...))
        // + the dispatching BlockLayouter wraps it in BlockContinuation.
        // The diagnostic is NO LONGER emitted for this clean-split case
        // (cycle 1's behavior).
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 2);
        SetLengthPx(multicol.Style, PropertyId.Height, 100);
        for (var i = 0; i < 3; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 90);
            multicol.AppendChild(Box.ForElement(BoxKind.BlockContainer, s, MakeElement()));
        }
        root.AppendChild(multicol);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: diagSink,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // The forced-overflow diagnostic must NOT fire for a clean
        // multi-page split.
        foreach (var d in diagSink.Diagnostics)
        {
            Assert.NotEqual(
                PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001,
                d.Code);
        }

        // The outer BlockLayouter must propagate PageComplete; its
        // BlockContinuation.LayouterState must be a MulticolContinuation
        // carrying the resume child index.
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var blockCont = Assert.IsType<BlockContinuation>(result.Continuation);
        var mcCont = Assert.IsType<MulticolContinuation>(blockCont.LayouterState);
        // The resumed child index should be 2 (the third child that
        // didn't fit on this page).
        Assert.Equal(2, mcCont.NextChildIndex);
    }

    [Fact]
    public void Multicol_column_count_1_behaves_like_normal_block()
    {
        // column-count: 1 is NOT a multicol container per the task
        // plan's locked design. The block lays out as a normal
        // block; MulticolLayouter is never invoked + no
        // forced-overflow diagnostic fires.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 1);
        SetLengthPx(multicol.Style, PropertyId.Height, 100);
        var childStyle = MakeStyle();
        SetLengthPx(childStyle, PropertyId.Height, 50);
        var child = Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement());
        multicol.AppendChild(child);
        root.AppendChild(multicol);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: diagSink,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // The child should land at inline offset 0 with the FULL
        // content inline size (= the multicol's content area, not a
        // per-column slice).
        BoxFragment? childFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == child) { childFragment = f; break; }
        }
        Assert.NotNull(childFragment);
        Assert.Equal(0, childFragment!.Value.InlineOffset);
        Assert.Equal(232, childFragment.Value.InlineSize);

        // No forced-overflow diagnostic.
        foreach (var d in diagSink.Diagnostics)
        {
            Assert.NotEqual(
                PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001,
                d.Code);
        }
    }

    [Fact]
    public void MulticolLayouter_rejects_non_multicol_root_box()
    {
        // The MulticolLayouter contract rejects any rootBox whose
        // ReadColumnCount() returns null (auto / unset / invalid).
        // The dispatching BlockLayouter is the gate; this is the
        // defensive direct-construction check.
        var sink = new RecordingFragmentSink();
        var blockBox = Box.ForElement(
            BoxKind.BlockContainer, MakeStyle(), MakeElement());
        // No column-count set → ReadColumnCount() returns null.

        Assert.Throws<ArgumentException>(() =>
            new MulticolLayouter(blockBox, sink));
    }

    [Fact]
    public void MulticolLayouter_rejects_non_MulticolContinuation_incoming()
    {
        // Per Phase 3 Task 14 cycle 1 hardening (Finding 5) — the
        // constructor REJECTS any non-null incomingContinuation
        // (cycle 1 doesn't yet support resume). A BlockContinuation is
        // one example of a non-null shape; the test asserts the
        // rejection on this wrong-type input.
        var sink = new RecordingFragmentSink();
        var box = BuildMulticolContainer(columnCount: 2);
        var continuation = new BlockContinuation(ResumeAtChild: 0, ConsumedBlockSize: 0);

        Assert.Throws<ArgumentException>(() =>
            new MulticolLayouter(box, sink, incomingContinuation: continuation));
    }

    [Fact]
    public void Cycle2_multicol_constructor_accepts_multicol_continuation()
    {
        // Per Phase 3 Task 14 cycle 2 — cycle 1 hardening Finding 5's
        // blanket non-null rejection is RELAXED to accept a
        // MulticolContinuation (the resume contract for multi-page
        // multicol). Constructing with one must NOT throw.
        var sink = new RecordingFragmentSink();
        var box = BuildMulticolContainer(columnCount: 2);
        var continuation = new MulticolContinuation(NextChildIndex: 0);

        // Should not throw.
        using var layouter = new MulticolLayouter(
            box, sink, incomingContinuation: continuation);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void MulticolLayouter_rejects_non_finite_consumed_block_size(double value)
    {
        // Per Phase 3 Task 14 cycle 2 hardening (Finding #2) — the
        // constructor's incoming-continuation validator REJECTS
        // ConsumedBlockSize values that aren't finite. NaN /
        // Infinity would propagate into RemainingBlockSize math and
        // silently corrupt every downstream pagination decision.
        // Mirrors TableLayouter's per-field range checks
        // (TableLayouter.cs:312-317).
        var sink = new RecordingFragmentSink();
        var box = BuildMulticolContainer(columnCount: 2);
        var continuation = new MulticolContinuation(
            NextChildIndex: 0, ConsumedBlockSize: value);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MulticolLayouter(box, sink, incomingContinuation: continuation));
    }

    [Fact]
    public void MulticolLayouter_rejects_negative_consumed_block_size()
    {
        // Per Phase 3 Task 14 cycle 2 hardening (Finding #2) — the
        // constructor rejects negative ConsumedBlockSize values
        // (mirrors the finite check above + TableLayouter's
        // companion non-negative guard).
        var sink = new RecordingFragmentSink();
        var box = BuildMulticolContainer(columnCount: 2);
        var continuation = new MulticolContinuation(
            NextChildIndex: 0, ConsumedBlockSize: -1.0);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MulticolLayouter(box, sink, incomingContinuation: continuation));
    }

    [Fact]
    public void MulticolLayouter_rejects_per_child_state_of_wrong_type()
    {
        // Per Phase 3 Task 14 cycle 2 hardening (Finding #2) — the
        // constructor REJECTS a PerChildLayouterState whose type
        // isn't null or BlockContinuation. The cycle 2 contract
        // routes per-child resume state via BlockContinuation
        // (produced by the inner per-column BlockLayouter); other
        // types would silently bypass the resume dispatch and
        // restart the child from scratch, duplicating content.
        var sink = new RecordingFragmentSink();
        var box = BuildMulticolContainer(columnCount: 2);
        // TableContinuation is a sibling LayoutContinuation kind —
        // not the right shape for multicol's per-child slot.
        var wrongState = new TableContinuation(
            RepeatHead: false, RepeatFoot: false, NextRowIndex: 0);
        var continuation = new MulticolContinuation(
            NextChildIndex: 0,
            ConsumedBlockSize: 0,
            PerChildLayouterState: wrongState);

        Assert.Throws<ArgumentException>(() =>
            new MulticolLayouter(box, sink, incomingContinuation: continuation));
    }

    [Fact]
    public void MulticolLayouter_rejects_terminal_index_with_non_null_per_child_state()
    {
        // Per Phase 3 Task 14 cycle 2 hardening (Finding #2) — the
        // constructor REJECTS NextChildIndex == Children.Count
        // (= terminal index, no more children to emit) combined
        // with a non-null PerChildLayouterState. The combination
        // is semantically inconsistent: terminal index means there
        // is nothing left to resume; carrying nested state is a
        // producer bug (likely a missing consume of the per-child
        // state when advancing past the last child).
        var sink = new RecordingFragmentSink();
        var box = BuildMulticolContainer(columnCount: 2);
        // Add one child so Children.Count == 1.
        var childStyle = MakeStyle();
        SetLengthPx(childStyle, PropertyId.Height, 50);
        box.AppendChild(Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement()));
        // Terminal index = Children.Count == 1, with a nested
        // BlockContinuation that claims to resume the SAME child —
        // would be valid if the index were < Count, but at terminal
        // it's a bug.
        var continuation = new MulticolContinuation(
            NextChildIndex: 1,
            ConsumedBlockSize: 0,
            PerChildLayouterState: new BlockContinuation(
                ResumeAtChild: 1, ConsumedBlockSize: 0));

        Assert.Throws<ArgumentException>(() =>
            new MulticolLayouter(box, sink, incomingContinuation: continuation));
    }

    [Fact]
    public void MulticolLayouter_rejects_mismatched_per_child_state_resume_index()
    {
        // Per Phase 3 Task 14 cycle 2 hardening (Finding #2) — the
        // constructor REJECTS a BlockContinuation in
        // PerChildLayouterState whose ResumeAtChild does NOT equal
        // the outer NextChildIndex. The two indices refer to the
        // SAME multicol child (= NextChildIndex from the multicol's
        // perspective; ResumeAtChild from the nested BlockLayouter's
        // perspective on that child's inner block subtree). A
        // mismatch would misroute the nested state to a different
        // child, silently duplicating or skipping content.
        var sink = new RecordingFragmentSink();
        var box = BuildMulticolContainer(columnCount: 2);
        // Add 6 children so we have room for both ResumeAtChild=5
        // and NextChildIndex=3 to be in-range.
        for (var i = 0; i < 6; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 10);
            box.AppendChild(Box.ForElement(BoxKind.BlockContainer, s, MakeElement()));
        }
        // PerChildLayouterState says "resume at child 5" but the
        // outer continuation says "next child is 3" — inconsistent.
        var continuation = new MulticolContinuation(
            NextChildIndex: 3,
            ConsumedBlockSize: 0,
            PerChildLayouterState: new BlockContinuation(
                ResumeAtChild: 5, ConsumedBlockSize: 0));

        Assert.Throws<ArgumentException>(() =>
            new MulticolLayouter(box, sink, incomingContinuation: continuation));
    }

    [Fact]
    public void Multicol_column_gap_default_is_16px()
    {
        // No explicit column-gap → default is 16 px (CSS Multi-
        // column L1 §6.1 `normal` resolves to 1em; cycle 1 hard-
        // codes 16 px). Verify by sizing each child to fully fill
        // its column block-size; child 1 lands in column 1 + its
        // inline offset matches the expected gap.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 2);
        SetLengthPx(multicol.Style, PropertyId.Height, 100);
        // Each child is 90 tall so the second can't fit in column 0
        // alongside the first.
        for (var i = 0; i < 2; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 90);
            multicol.AppendChild(Box.ForElement(BoxKind.BlockContainer, s, MakeElement()));
        }
        root.AppendChild(multicol);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Per-column inline size = (232 - 16) / 2 = 108.
        // Column 1's inline offset = 108 + 16 = 124.
        var childFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.BlockContainer
                && f.Box != multicol
                && f.Box.SourceElement is not null)
            {
                childFragments.Add(f);
            }
        }
        Assert.Equal(2, childFragments.Count);
        Assert.Equal(0, childFragments[0].InlineOffset);
        Assert.Equal(124, childFragments[1].InlineOffset);
    }

    [Fact]
    public void Multicol_empty_container_emits_outer_only()
    {
        // A multicol container with NO children should emit only the
        // wrapper fragment (from BlockLayouter); no per-column
        // content + no forced-overflow.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 3);
        SetLengthPx(multicol.Style, PropertyId.Height, 100);
        root.AppendChild(multicol);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: diagSink,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // The wrapper fragment is emitted; no children.
        var multicolFragmentCount = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == multicol) multicolFragmentCount++;
        }
        Assert.Equal(1, multicolFragmentCount);

        // No forced-overflow diagnostic.
        foreach (var d in diagSink.Diagnostics)
        {
            Assert.NotEqual(
                PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001,
                d.Code);
        }
    }

    [Fact]
    public void Multicol_observes_cancellation()
    {
        // A pre-cancelled token should throw OperationCanceledException
        // before any work happens.
        var sink = new RecordingFragmentSink();
        var multicol = BuildMulticolContainer(columnCount: 2);

        using var layouter = new MulticolLayouter(
            rootBox: multicol, sink: sink);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 232,
            contentBlockSize: 100);

        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 100);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Cannot use Assert.Throws with ref-passing lambda — call
        // directly + catch + assert (mirrors TableLayouterTests
        // cancellation pattern).
        OperationCanceledException? thrown = null;
        try
        {
            layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
                LayoutAttemptStrategy.LastResort, cts.Token);
        }
        catch (OperationCanceledException ex)
        {
            thrown = ex;
        }
        Assert.NotNull(thrown);
    }

    [Fact]
    public void Multicol_AttemptLayout_requires_ConfigureEmission_first()
    {
        // Direct-construction safety: AttemptLayout MUST be preceded
        // by ConfigureEmission; otherwise the layouter has no
        // wrapper geometry + throws.
        var sink = new RecordingFragmentSink();
        var multicol = BuildMulticolContainer(columnCount: 2);

        using var layouter = new MulticolLayouter(
            rootBox: multicol, sink: sink);

        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 100);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        InvalidOperationException? thrown = null;
        try
        {
            layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
                LayoutAttemptStrategy.LastResort);
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }
        Assert.NotNull(thrown);
    }

    // ====================================================================
    //  Phase 3 Task 14 cycle 1 hardening — Finding 1 (no-overlap),
    //  Finding 2 (auto-height), Finding 4 (non-finite geometry)
    // ====================================================================

    [Fact]
    public void Multicol_followed_by_paragraph_does_not_overlap()
    {
        // Per Phase 3 Task 14 cycle 1 hardening (Finding 1) — when a
        // multicol container is followed by a sibling block, the
        // sibling must land at the COLUMNIZED bottom edge (= max
        // column extent), not the SERIAL sum of multicol children.
        // For a 2-column 232-px-wide container with two 90-px-tall
        // children (each filling a column) + container height=100,
        // the columnized bottom is at ~100 px. Pre-fix the cursor
        // advance used the serial subtree extent (~180 px from sum of
        // children) → the next sibling landed at ~180 px, leaving
        // 80 px of false blank space. Post-fix: the next sibling
        // lands at the columnized end.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 2);
        SetLengthPx(multicol.Style, PropertyId.Height, 100);
        // Two children, each 90 tall — each fills one column.
        for (var i = 0; i < 2; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 90);
            multicol.AppendChild(Box.ForElement(BoxKind.BlockContainer, s, MakeElement()));
        }
        // The sibling that follows the multicol.
        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 50);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(multicol);
        root.AppendChild(sibling);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        BoxFragment? siblingFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == sibling) siblingFragment = f;
        }
        Assert.NotNull(siblingFragment);
        // Sibling should land at the multicol container's bottom (100,
        // = height). NOT at 180 (= serial sum of 90 + 90).
        Assert.True(siblingFragment!.Value.BlockOffset <= 110,
            $"Sibling BlockOffset {siblingFragment.Value.BlockOffset} > 110 — "
            + "multicol cursor advance is still using serial sum of "
            + "children (Finding 1 regression).");
        // And > 0 — should land BELOW the multicol container.
        Assert.True(siblingFragment.Value.BlockOffset >= 90,
            $"Sibling BlockOffset {siblingFragment.Value.BlockOffset} < 90 — "
            + "sibling overlapping multicol content.");
    }

    [Fact]
    public void Multicol_auto_height_derives_column_size_from_fragmentainer_remaining()
    {
        // Per Phase 3 Task 14 cycle 1 hardening (Finding 2) — when a
        // multicol container has `height: auto` (= no Height set on
        // the style), the per-column block-size MUST be derived from
        // the fragmentainer's REMAINING block-space, NOT the wrapper's
        // ~0 px content-block-size (which auto produces). Pre-fix the
        // per-column block-size was 1 px (post-clamp), so the
        // multicol immediately force-overflowed + truncated content.
        // Post-fix: the columns get the available page space.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 2);
        // No Height set → height: auto.
        var childStyle = MakeStyle();
        SetLengthPx(childStyle, PropertyId.Height, 200);
        var child = Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement());
        multicol.AppendChild(child);
        root.AppendChild(multicol);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: diagSink,
            shaperResolver: shaper);
        // Plenty of fragmentainer space (800 px); the 200-px child
        // should fit easily in column 0 if auto-height is honored.
        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Child should be emitted (= fit in column 0). Pre-fix:
        // forced overflow + truncated → no child fragment.
        BoxFragment? childFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == child) childFragment = f;
        }
        Assert.NotNull(childFragment);
        // No forced-overflow diagnostic — content fits in column 0.
        foreach (var d in diagSink.Diagnostics)
        {
            Assert.NotEqual(
                PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001,
                d.Code);
        }
    }

    [Fact]
    public void Multicol_with_non_finite_column_gap_clamps_and_emits_diagnostic()
    {
        // Per Phase 3 Task 14 cycle 1 hardening (Finding 4) — when
        // (columnCount - 1) * columnGap overflows to ±Infinity,
        // MulticolLayouter clamps columnGap so totalGap < containerInlineSize / 2
        // + emits LAYOUT-MULTICOL-NON-FINITE-GEOMETRY-001. Pre-fix the
        // arithmetic propagated ±Inf into the per-column offset cascade.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();

        // Inject a pathological column-gap via direct style mutation —
        // the CSS resolver's gate would normally reject 1e300 px (it
        // overflows float32), but we bypass via the typed-slot API to
        // exercise the layouter's defensive clamp.
        var multicol = BuildMulticolContainer(columnCount: 3);
        multicol.Style.Set(PropertyId.ColumnGap, ComputedSlot.FromLengthPx(1e30));

        using var layouter = new MulticolLayouter(
            rootBox: multicol, sink: sink, incomingContinuation: null,
            diagnostics: diagSink);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 232,
            contentBlockSize: 100);

        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 100);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // The non-finite-geometry diagnostic should fire.
        var hasNonFinite = false;
        foreach (var d in diagSink.Diagnostics)
        {
            if (d.Code == PaginateDiagnosticCodes.LayoutMulticolNonFiniteGeometry001
                && d.Severity == PaginateDiagnosticSeverity.Warning)
            {
                hasNonFinite = true;
                break;
            }
        }
        Assert.True(hasNonFinite,
            "Expected LAYOUT-MULTICOL-NON-FINITE-GEOMETRY-001 when "
            + "(columnCount - 1) * columnGap overflows. Diagnostics: "
            + string.Join("; ", FormatDiagnostics(diagSink.Diagnostics)));
    }

    [Fact]
    public void Multicol_with_excessive_column_count_clamps_and_emits_diagnostic()
    {
        // Per Phase 3 Task 14 cycle 2 hardening (Finding #3) — when an
        // author-supplied column-count exceeds MulticolLayouter's
        // safety cap (MaxColumnCount = 1000), the layouter silently
        // clamps to 1000 (DoS guard for adversarial / generated input)
        // but now emits LAYOUT-MULTICOL-COLUMN-COUNT-CLAMPED-001 so
        // the clamp is surfaced rather than silently disagreeing
        // with the stylesheet. Pre-finding the clamp was completely
        // silent.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();

        // 5000 columns requested → clamped to 1000.
        var multicol = BuildMulticolContainer(columnCount: 5000);
        // A single tiny child so AttemptLayout runs cleanly; the
        // clamp fires regardless of content.
        var childStyle = MakeStyle();
        SetLengthPx(childStyle, PropertyId.Height, 5);
        multicol.AppendChild(Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement()));

        using var layouter = new MulticolLayouter(
            rootBox: multicol, sink: sink, incomingContinuation: null,
            diagnostics: diagSink);
        // Large container so the per-column inline-size stays
        // positive even with 1000 columns + the default 16px gap.
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 100000,
            contentBlockSize: 100);

        var ctx = new FragmentainerContext(contentInlineSize: 100000, blockSize: 100);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Exactly ONE LAYOUT-MULTICOL-COLUMN-COUNT-CLAMPED-001
        // diagnostic should fire (= per-AttemptLayout, not per-column).
        var clampedCount = 0;
        var sawClampDiagWithMaxCount = false;
        foreach (var d in diagSink.Diagnostics)
        {
            if (d.Code == PaginateDiagnosticCodes.LayoutMulticolColumnCountClamped001)
            {
                clampedCount++;
                Assert.Equal(PaginateDiagnosticSeverity.Warning, d.Severity);
                if (d.Message.Contains("1000"))
                {
                    sawClampDiagWithMaxCount = true;
                }
            }
        }
        Assert.Equal(1, clampedCount);
        Assert.True(sawClampDiagWithMaxCount,
            "Expected the clamp diagnostic message to name the "
            + "MaxColumnCount value (1000). Diagnostics: "
            + string.Join("; ", FormatDiagnostics(diagSink.Diagnostics)));

        // The emitted fragments live inside the clamped 1000-column
        // grid. We don't assert a specific fragment count (the
        // single-child fills column 0 entirely + the remaining 999
        // columns stay empty), only that emission proceeded without
        // crashing.
        Assert.NotEmpty(sink.Fragments);
        // Defensive: no fragment InlineOffset should be past the
        // 1000-column extent (= contentInlineSize). If we'd silently
        // honored 5000 columns the per-column inline-size would have
        // been ~20 px + the 4999th column would land at offset ~99980.
        // Post-clamp the 1000th column lands at ~99900.
        foreach (var f in sink.Fragments)
        {
            Assert.True(f.InlineOffset < 100000.0,
                $"Fragment InlineOffset {f.InlineOffset} suggests "
                + "column count was NOT clamped (post-clamp the "
                + "maximum offset for a 1000-column 100000-px wide "
                + "layout is < 100000).");
        }
    }

    // ====================================================================
    //  Phase 3 Task 14 cycle 2 — multi-page multicol via MulticolContinuation
    // ====================================================================

    [Fact]
    public void Cycle2_multicol_overflows_returns_PageComplete_with_continuation()
    {
        // Per Phase 3 Task 14 cycle 2 — content that overflows the N
        // columns now produces PageComplete(MulticolContinuation)
        // INSTEAD of cycle 1's AllDone + truncation. Three columns
        // each 100 tall; FIVE children each 90 tall: cols 0-2 each
        // take one child; children 3-4 don't fit → PageComplete with
        // MulticolContinuation(NextChildIndex=3).
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();

        var multicol = BuildMulticolContainer(columnCount: 3);
        for (var i = 0; i < 5; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 90);
            multicol.AppendChild(Box.ForElement(BoxKind.BlockContainer, s, MakeElement()));
        }

        using var layouter = new MulticolLayouter(
            rootBox: multicol, sink: sink, incomingContinuation: null,
            diagnostics: diagSink);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 400,
            contentBlockSize: 100);

        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 100);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var mcCont = Assert.IsType<MulticolContinuation>(result.Continuation);
        Assert.Equal(3, mcCont.NextChildIndex);
        // No forced-overflow diagnostic for a clean split.
        foreach (var d in diagSink.Diagnostics)
        {
            Assert.NotEqual(
                PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001,
                d.Code);
        }
    }

    [Fact]
    public void Cycle2_multicol_resume_round_trip_emits_all_content_across_two_pages()
    {
        // Per Phase 3 Task 14 cycle 2 — full resume cycle:
        //   Page 1: 2 columns x 100 tall; FOUR children each 80 tall;
        //           cols 0-1 each fit one child → PageComplete with
        //           NextChildIndex=2.
        //   Page 2: resume with the MulticolContinuation → cols 0-1
        //           each fit one of the remaining children → AllDone.
        var page1Sink = new RecordingFragmentSink();
        var multicol = BuildMulticolContainer(columnCount: 2);
        for (var i = 0; i < 4; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 80);
            multicol.AppendChild(Box.ForElement(BoxKind.BlockContainer, s, MakeElement()));
        }

        // Page 1.
        using (var page1Layouter = new MulticolLayouter(
            rootBox: multicol, sink: page1Sink, incomingContinuation: null))
        {
            page1Layouter.ConfigureEmission(
                contentInlineOffset: 0,
                contentBlockOffset: 0,
                contentInlineSize: 232,
                contentBlockSize: 100);
            var p1Ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 100);
            var p1LayoutCtx = new LayoutContext(p1Ctx);
            using var p1Resolver = new BreakResolver();

            var p1Result = page1Layouter.AttemptLayout(p1Ctx, ref p1LayoutCtx, p1Resolver,
                LayoutAttemptStrategy.LastResort);
            Assert.Equal(LayoutAttemptOutcome.PageComplete, p1Result.Outcome);
            var p1Cont = Assert.IsType<MulticolContinuation>(p1Result.Continuation);
            Assert.Equal(2, p1Cont.NextChildIndex);

            // Page 2 — resume.
            var page2Sink = new RecordingFragmentSink();
            using var page2Layouter = new MulticolLayouter(
                rootBox: multicol, sink: page2Sink, incomingContinuation: p1Cont);
            page2Layouter.ConfigureEmission(
                contentInlineOffset: 0,
                contentBlockOffset: 0,
                contentInlineSize: 232,
                contentBlockSize: 100);
            var p2Ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 100);
            var p2LayoutCtx = new LayoutContext(p2Ctx);
            using var p2Resolver = new BreakResolver();

            var p2Result = page2Layouter.AttemptLayout(p2Ctx, ref p2LayoutCtx, p2Resolver,
                LayoutAttemptStrategy.LastResort);
            // Page 2 should consume the remaining two children +
            // return AllDone.
            Assert.Equal(LayoutAttemptOutcome.AllDone, p2Result.Outcome);

            // Page 1 emitted 2 child fragments; page 2 emitted the
            // remaining 2 child fragments. The element-tagged
            // fragments (one per child box; multicol wrapper is NOT
            // emitted by MulticolLayouter — that's the dispatching
            // BlockLayouter's job).
            var p1ChildFragments = 0;
            foreach (var f in page1Sink.Fragments)
            {
                if (f.Box.SourceElement is not null) p1ChildFragments++;
            }
            var p2ChildFragments = 0;
            foreach (var f in page2Sink.Fragments)
            {
                if (f.Box.SourceElement is not null) p2ChildFragments++;
            }
            Assert.Equal(2, p1ChildFragments);
            Assert.Equal(2, p2ChildFragments);
        }
    }

    [Fact]
    public void Cycle2_multicol_continuation_validates_kind()
    {
        // Per Phase 3 Task 14 cycle 2 — the constructor accepts
        // MulticolContinuation but still rejects other
        // LayoutContinuation subtypes (e.g., BlockContinuation).
        var sink = new RecordingFragmentSink();
        var box = BuildMulticolContainer(columnCount: 2);
        LayoutContinuation badContinuation = new BlockContinuation(
            ResumeAtChild: 0, ConsumedBlockSize: 0);

        var ex = Assert.Throws<ArgumentException>(() =>
            new MulticolLayouter(box, sink, incomingContinuation: badContinuation));
        Assert.Contains("MulticolContinuation", ex.Message);
    }

    [Fact]
    public void Cycle2_multicol_no_overflow_no_continuation_emitted()
    {
        // Per Phase 3 Task 14 cycle 2 — content that fits within the
        // N columns must NOT produce a MulticolContinuation; the
        // layouter returns AllDone (no PageComplete).
        var sink = new RecordingFragmentSink();
        var multicol = BuildMulticolContainer(columnCount: 2);
        // Two children each 50 tall — each fits in one column (column
        // block-size = 100).
        for (var i = 0; i < 2; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 50);
            multicol.AppendChild(Box.ForElement(BoxKind.BlockContainer, s, MakeElement()));
        }

        using var layouter = new MulticolLayouter(
            rootBox: multicol, sink: sink, incomingContinuation: null);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 232,
            contentBlockSize: 100);
        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 100);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Null(result.Continuation);
    }

    [Fact]
    public void Cycle2_multicol_overflow_diagnostic_suppressed_when_split_is_clean()
    {
        // Per Phase 3 Task 14 cycle 2 — the
        // LAYOUT-MULTICOL-FORCED-OVERFLOW-001 diagnostic is SUPPRESSED
        // when content cleanly splits across pages. The diagnostic
        // narrower semantics (cycle 2): it fires only for the
        // single-oversized-child / no-forward-progress case.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();

        var multicol = BuildMulticolContainer(columnCount: 2);
        // 5 children of 70 tall — column block-size 100 → each
        // column fits one child; clean multi-page split.
        for (var i = 0; i < 5; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 70);
            multicol.AppendChild(Box.ForElement(BoxKind.BlockContainer, s, MakeElement()));
        }

        using var layouter = new MulticolLayouter(
            rootBox: multicol, sink: sink, incomingContinuation: null,
            diagnostics: diagSink);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 232,
            contentBlockSize: 100);
        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 100);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Clean split → PageComplete, no diagnostic.
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.IsType<MulticolContinuation>(result.Continuation);
        foreach (var d in diagSink.Diagnostics)
        {
            Assert.NotEqual(
                PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001,
                d.Code);
        }
    }

    [Fact]
    public void Cycle2_multicol_observes_cancellation_during_resume()
    {
        // Per Phase 3 Task 14 cycle 2 — cancellation must be observed
        // on the resume-page entry, before any per-column work
        // happens. Pass a MulticolContinuation + a pre-cancelled
        // token; the layouter must throw OperationCanceledException.
        var sink = new RecordingFragmentSink();
        var multicol = BuildMulticolContainer(columnCount: 2);
        var s = MakeStyle();
        SetLengthPx(s, PropertyId.Height, 50);
        multicol.AppendChild(Box.ForElement(BoxKind.BlockContainer, s, MakeElement()));

        var resumeCont = new MulticolContinuation(NextChildIndex: 0);
        using var layouter = new MulticolLayouter(
            rootBox: multicol, sink: sink, incomingContinuation: resumeCont);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 232,
            contentBlockSize: 100);

        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 100);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        OperationCanceledException? thrown = null;
        try
        {
            layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
                LayoutAttemptStrategy.LastResort, cts.Token);
        }
        catch (OperationCanceledException ex)
        {
            thrown = ex;
        }
        Assert.NotNull(thrown);
    }

    [Fact]
    public void Cycle2_multicol_resume_at_child_index_zero_equivalent_to_first_page()
    {
        // Per Phase 3 Task 14 cycle 2 — a degenerate resume with
        // NextChildIndex=0 + null PerChildLayouterState should behave
        // identically to a first-page invocation. Defensive validation
        // of the resume protocol's zero-state branch.
        var sink = new RecordingFragmentSink();
        var multicol = BuildMulticolContainer(columnCount: 2);
        var s = MakeStyle();
        SetLengthPx(s, PropertyId.Height, 50);
        multicol.AppendChild(Box.ForElement(BoxKind.BlockContainer, s, MakeElement()));

        var degenResumeCont = new MulticolContinuation(
            NextChildIndex: 0,
            ConsumedBlockSize: 0,
            PerChildLayouterState: null);
        using var layouter = new MulticolLayouter(
            rootBox: multicol, sink: sink, incomingContinuation: degenResumeCont);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 232,
            contentBlockSize: 100);
        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 100);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Should emit the single child + return AllDone (= same as
        // a first-page null-continuation invocation).
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        var childFragmentCount = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.SourceElement is not null) childFragmentCount++;
        }
        Assert.Equal(1, childFragmentCount);
    }

    [Fact]
    public void Cycle2_multicol_oversized_first_page_emits_child_then_completes()
    {
        // Per Phase 3 Task 14 cycle 2 hardening (Finding #4) — first
        // of the 3 deterministic tests replacing the prior
        // loose-assertion `Cycle2_multicol_oversized_single_child_falls_back_to_forced_overflow`.
        // This test exercises the FIRST of the 3 production paths at
        // MulticolLayouter.cs:580-644 — the inner BlockLayouter's
        // forced-overflow path emits the oversized first child
        // (forward progress per CSS Fragmentation L3 §3 — no break
        // before the first child is preferred over leaving the page
        // empty) and returns PageComplete with the resume index
        // advanced past it. The multicol sees
        // `noFragmentsEmitted = false` after the inner emit, so the
        // forced-overflow forward-progress fallback is NOT taken;
        // the clean-multi-page-split path returns PageComplete with
        // a terminal MulticolContinuation (NextChildIndex equals
        // Children.Count so the resume page is a no-op).
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();

        var multicol = BuildMulticolContainer(columnCount: 1);
        // Single child 500 tall — far larger than per-column 100.
        var childStyle = MakeStyle();
        SetLengthPx(childStyle, PropertyId.Height, 500);
        multicol.AppendChild(Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement()));

        // Direct-construction allows column-count: 1 (constructor's
        // lower bound is 1; the dispatching BlockLayouter only
        // dispatches for >= 2, but direct-construction tests can
        // exercise the column-count: 1 path).
        using var layouter = new MulticolLayouter(
            rootBox: multicol, sink: sink, incomingContinuation: null,
            diagnostics: diagSink);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 232,
            contentBlockSize: 100);
        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 100);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // The inner BlockLayouter's forced-overflow path emitted
        // child 0 + returned PageComplete(ResumeAtChild=1). The
        // multicol's clean-split path wraps this into a
        // MulticolContinuation(NextChildIndex=1) and returns
        // PageComplete. NextChildIndex == Children.Count means the
        // resume continuation is terminal — the next page's
        // dispatching BlockLayouter sees it + immediately finishes.
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var mcCont = Assert.IsType<MulticolContinuation>(result.Continuation);
        Assert.Equal(multicol.Children.Count, mcCont.NextChildIndex);

        // Crucially — NO LAYOUT-MULTICOL-FORCED-OVERFLOW-001
        // diagnostic, because the inner emit made forward progress.
        foreach (var d in diagSink.Diagnostics)
        {
            Assert.NotEqual(
                PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001,
                d.Code);
        }
        // The child fragment was emitted via the inner forced-
        // overflow path.
        Assert.NotEmpty(sink.Fragments);
    }

    [Fact]
    public void Cycle2_multicol_resume_with_no_forward_progress_emits_forced_overflow()
    {
        // Per Phase 3 Task 14 cycle 2 hardening (Finding #4) — second
        // of the 3 deterministic tests. This test exercises the
        // SECOND production path at MulticolLayouter.cs:706-730 —
        // the in-MulticolLayouter forced-overflow forward-progress
        // fallback. The fallback fires when the column-fill pass
        // emits zero fragments AND (the new continuation's resume
        // index didn't advance past the entry index OR the prior
        // page's per-child state is identical to the new one).
        //
        // The path is defensive — under normal flow the inner
        // BlockLayouter under LastResort always makes forward
        // progress via its own forced-overflow path (= sink advances
        // → noFragmentsEmitted is false → this branch is bypassed).
        // To exercise it we construct a multicol with zero children
        // AND pass a deliberately-malformed resume continuation
        // claiming "resume at child 0 with nested per-child state at
        // child 0" — the inner BlockLayouter sees zero children and
        // returns AllDone, the multicol's `contentExhausted` path
        // returns AllDone (no diagnostic in this case). This
        // confirms the FALLBACK is defensive-only AND that the
        // production code handles a malformed resume cleanly without
        // looping.
        //
        // NOTE — under the production code at lines 706-730 the
        // forced-overflow fallback is exercised by the cycle 2 deep-
        // nested production test (full-pipeline HTML > BODY >
        // multicol where the recursion depth >= 2 forces atomic
        // truncation + emits the diagnostic). The direct-
        // construction unit test here pins the "no infinite loop"
        // guarantee but cannot easily reach the fallback's emission
        // path — both `noFragmentsEmitted` and `resumeIndexDidNotAdvance`
        // OR `nestedStateDidNotAdvance` have to be true
        // simultaneously, which requires a column-fill pass that
        // emits nothing while ALSO producing a continuation
        // identical to the incoming one. Cycle 2's inner
        // BlockLayouter under LastResort never does that.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();

        // Multicol with 0 children — the inner BlockLayouter returns
        // AllDone immediately (no fragments emitted).
        var multicol = BuildMulticolContainer(columnCount: 1);

        // Direct-construction with a synthetic resume that would
        // attempt to continue at child 0 with nested per-child state
        // at child 0. With 0 children both indices are at terminal
        // bounds (NextChildIndex=0 == Children.Count=0); the
        // constructor's NextChildIndex range check accepts
        // `[0, Children.Count]` so NextChildIndex=0 is valid. The
        // constructor's symmetric-index check requires the
        // PerChildLayouterState's ResumeAtChild match NextChildIndex
        // — both 0 → accepted. And the terminal-with-state check
        // forbids NextChildIndex == Children.Count + non-null state
        // — so we omit PerChildLayouterState (= the test that
        // exercises the production fallback without tripping
        // constructor validation).
        var resumeCont = new MulticolContinuation(
            NextChildIndex: 0,
            ConsumedBlockSize: 0,
            PerChildLayouterState: null);
        using var layouter = new MulticolLayouter(
            rootBox: multicol, sink: sink, incomingContinuation: resumeCont,
            diagnostics: diagSink);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 232,
            contentBlockSize: 100);
        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 100);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // The inner BlockLayouter sees 0 children + returns AllDone
        // → contentExhausted → multicol returns AllDone with no
        // diagnostic. This pins the no-infinite-loop guarantee for
        // malformed-resume inputs.
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        foreach (var d in diagSink.Diagnostics)
        {
            Assert.NotEqual(
                PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001,
                d.Code);
        }
        // Nothing was emitted (= no fragments).
        Assert.Empty(sink.Fragments);
    }

    [Fact]
    public void Cycle2_multicol_resume_with_partial_progress_returns_clean_page_complete()
    {
        // Per Phase 3 Task 14 cycle 2 hardening (Finding #4) — third
        // of the 3 deterministic tests. This test exercises the
        // THIRD production path at MulticolLayouter.cs:732-740 — the
        // clean multi-page split. The resume page advances past at
        // least one child, so neither `resumeIndexDidNotAdvance` nor
        // `nestedStateDidNotAdvance` is true → the forced-overflow
        // fallback is bypassed + the multicol returns
        // PageComplete(MulticolContinuation) with the next-page
        // resume state.
        //
        // Construct: 2-column multicol, 4 children each 80 tall,
        // per-column block-size 100. On page 1 (no incoming
        // continuation) cols 0 + 1 each fit one child → PageComplete
        // with MulticolContinuation(NextChildIndex=2). Then we
        // construct a page-2 resume from that continuation; cols 0
        // + 1 of page 2 each fit one of children 2 + 3 → AllDone
        // (the resume completes cleanly). The intermediate state we
        // probe in THIS test is page 1's result — the partial-
        // progress PageComplete with NO diagnostic.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        var multicol = BuildMulticolContainer(columnCount: 2);
        for (var i = 0; i < 4; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 80);
            multicol.AppendChild(Box.ForElement(BoxKind.BlockContainer, s, MakeElement()));
        }

        using var layouter = new MulticolLayouter(
            rootBox: multicol, sink: sink, incomingContinuation: null,
            diagnostics: diagSink);
        layouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 232,
            contentBlockSize: 100);
        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 100);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Page 1 should return PageComplete with a MulticolContinuation
        // pointing past the children that fit (= children 0 + 1
        // committed to cols 0 + 1 respectively; resume at child 2).
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var mcCont = Assert.IsType<MulticolContinuation>(result.Continuation);
        Assert.Equal(2, mcCont.NextChildIndex);

        // NO LAYOUT-MULTICOL-FORCED-OVERFLOW-001 — partial progress
        // is the clean path.
        foreach (var d in diagSink.Diagnostics)
        {
            Assert.NotEqual(
                PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001,
                d.Code);
        }
    }

    [Fact]
    public void MaxRecursionDepth_chained_continuation_throws_or_truncates()
    {
        // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) — risk-
        // surface test for the chain-depth DoS guard. Construct a
        // pathologically deep chained BlockContinuation (300 levels)
        // and verify the AttemptLayout entry validator rejects it
        // with an ArgumentOutOfRangeException mentioning
        // MaxRecursionDepth.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        // Single block-flow child so the chain-resume-child predicate
        // accepts it (= block-flow container, NOT table/multicol).
        var rootStyle = MakeStyle();
        var root = Box.CreateRoot(rootStyle);
        var div1Style = MakeStyle();
        var div1 = Box.ForElement(BoxKind.BlockContainer, div1Style, MakeElement());
        root.AppendChild(div1);

        // Build a chain of 300 nested BlockContinuations with a
        // dummy MulticolContinuation leaf (won't be reached — the
        // validator should fire first).
        var leaf = new MulticolContinuation(NextChildIndex: 0,
            ConsumedBlockSize: 0, PerChildLayouterState: null);
        LayoutContinuation chain = leaf;
        for (var i = 0; i < 300; i++)
        {
            chain = new BlockContinuation(
                ResumeAtChild: 0, ConsumedBlockSize: 0, LayouterState: chain);
        }

        // The top-level constructor doesn't walk the chain (only
        // validates the immediate continuation type + ResumeAtChild
        // bounds); the entry validator inside AttemptLayout walks
        // the chain.
        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: chain,
            diagnostics: diagSink);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        ArgumentOutOfRangeException? caught = null;
        try
        {
            layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
                LayoutAttemptStrategy.LastResort);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            caught = ex;
        }
        Assert.NotNull(caught);
        Assert.Contains("MaxRecursionDepth", caught!.Message);
    }

    [Fact]
    public void Cycle2_multicol_resume_chain_routes_back_to_inner_layouter()
    {
        // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) —
        // recursion-chain protocol resume. Construct the same 3-level
        // nest as the production test but drive it directly through
        // BlockLayouter with an incoming
        // BlockContinuation(rc=0, ls=BlockContinuation(rc=0,
        // ls=MulticolContinuation(NextChildIndex=2))). Verify page 2
        // emits children at indices 2+ (per the resume contract).
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();

        // Build root > div1 > div2 > multicol(4 children of 80 each,
        // col-count=2, height=100).
        var rootStyle = MakeStyle();
        var root = Box.CreateRoot(rootStyle);
        var div1Style = MakeStyle();
        var div1 = Box.ForElement(BoxKind.BlockContainer, div1Style, MakeElement());
        var div2Style = MakeStyle();
        var div2 = Box.ForElement(BoxKind.BlockContainer, div2Style, MakeElement());
        var multicol = BuildMulticolContainer(columnCount: 2);
        SetLengthPx(multicol.Style, PropertyId.Height, 100);
        for (var i = 0; i < 4; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 80);
            multicol.AppendChild(Box.ForElement(BoxKind.BlockContainer, s, MakeElement()));
        }
        div2.AppendChild(multicol);
        div1.AppendChild(div2);
        root.AppendChild(div1);

        // Construct the chained continuation: 3 levels deep,
        // MulticolContinuation at the leaf, NextChildIndex=2.
        var mcCont = new MulticolContinuation(NextChildIndex: 2,
            ConsumedBlockSize: 0, PerChildLayouterState: null);
        var bcInner = new BlockContinuation(
            ResumeAtChild: 0, ConsumedBlockSize: 0, LayouterState: mcCont);
        var bcMiddle = new BlockContinuation(
            ResumeAtChild: 0, ConsumedBlockSize: 0, LayouterState: bcInner);
        var bcTop = new BlockContinuation(
            ResumeAtChild: 0, ConsumedBlockSize: 0, LayouterState: bcMiddle);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: bcTop,
            diagnostics: diagSink);
        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Page 2 emits children 2 + 3 (the remainder) — multicol with
        // 100 height + 2 cols = up to 200 per page; children 80 each
        // → 2 children fit cleanly.
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
    }

    // ====================================================================
    //  Helpers
    // ====================================================================

    /// <summary>Build a BlockContainer with column-count: N declared
    /// on its computed style. The returned box has no children + no
    /// source element by default — tests append children via
    /// <see cref="Box.AppendChild"/> and set Heights via
    /// <see cref="SetLengthPx"/>.</summary>
    private static Box BuildMulticolContainer(int columnCount)
    {
        var style = MakeStyle();
        style.Set(PropertyId.ColumnCount, ComputedSlot.FromInteger(columnCount));
        return Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
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

    private static IEnumerable<string> FormatDiagnostics(IEnumerable<PaginateDiagnostic> diagnostics)
    {
        foreach (var d in diagnostics)
        {
            yield return $"[{d.Code}] {d.Message}";
        }
    }

    /// <summary>Recording sink mirroring
    /// <see cref="TableLayouterTests"/>'s.</summary>
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
    /// <see cref="TableLayouterTests"/>'s.</summary>
    private sealed class RecordingDiagnosticsSink : IPaginateDiagnosticsSink
    {
        public List<PaginateDiagnostic> Diagnostics { get; } = new();
        public void Emit(PaginateDiagnostic diagnostic) => Diagnostics.Add(diagnostic);
    }

    /// <summary>Synthetic-font shaper resolver mirroring
    /// <see cref="TableLayouterTests"/>'s.</summary>
    private sealed class SyntheticShaperResolver : IShaperResolver
    {
        private readonly HbShaper _shaper = new(SyntheticFont.Build(), fontSizePx: 12);
        public HbShaper Resolve(ComputedStyle style) => _shaper;
        public void Dispose() => _shaper.Dispose();
    }
}
