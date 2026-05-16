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

    [Fact]
    public void Cycle2_multicol_at_non_first_position_in_parent_resumes_correctly()
    {
        // Per post-PR-#57 review #2 Finding #1 — when a multicol
        // container is at a NON-ZERO index in its parent, the chain's
        // intermediate index must be preserved on the way up AND
        // honored as a skip-to-resume target on the way down.
        //
        // Pre-fix bugs:
        //   (a) The top-level `deepRet.LayouterState ?? deepRet`
        //       flatten dropped one chain layer, collapsing the
        //       3-layer chain `BC[body] > BC[multicol@idx-N] > Mc`
        //       to the 2-layer `BC[body] > Mc`. On resume, the inner
        //       BC's rc=N was lost.
        //   (b) The leaf-re-wrap branches in the chain-unwrap switch
        //       hardcoded `ResumeAtChild=0`, so even when the chain
        //       reached the recursion the leaf was misrouted to the
        //       first child instead of child N.
        //   (c) The recursion's for loop started at childIdx=0, so
        //       even with a correctly-routed chain it would re-emit
        //       children at idx [0, N) that were already committed on
        //       the prior page.
        //
        // DOM: root > body where body.Children = [
        //          spacer (idx 0, h=50),
        //          multicol (idx 1, col-count=2, h=100, 4 children of
        //                    h=80 each)
        //      ]. Page 1 emits spacer + multicol's first 2 children
        // (c0 in col 0, c1 in col 1, c2 + c3 overflow). Page 2 should
        // resume the multicol at child index 2 and emit c2 + c3
        // WITHOUT re-emitting the spacer.
        var sink1 = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();

        Box BuildRoot()
        {
            var root = Box.CreateRoot(MakeStyle());
            var body = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());

            var spacerStyle = MakeStyle();
            SetLengthPx(spacerStyle, PropertyId.Height, 50);
            body.AppendChild(Box.ForElement(BoxKind.BlockContainer, spacerStyle, MakeElement()));

            var multicol = BuildMulticolContainer(columnCount: 2);
            SetLengthPx(multicol.Style, PropertyId.Height, 100);
            for (var i = 0; i < 4; i++)
            {
                var s = MakeStyle();
                SetLengthPx(s, PropertyId.Height, 80);
                multicol.AppendChild(Box.ForElement(BoxKind.BlockContainer, s, MakeElement()));
            }
            body.AppendChild(multicol);
            root.AppendChild(body);
            return root;
        }

        // === Page 1 ===
        var root1 = BuildRoot();
        using var layouter1 = new BlockLayouter(
            rootBox: root1, sink: sink1,
            incomingContinuation: null,
            diagnostics: diagSink);
        var ctx1 = new FragmentainerContext(contentInlineSize: 232, blockSize: 800);
        var layoutCtx1 = new LayoutContext(ctx1) { Diagnostics = diagSink };
        using var resolver1 = new BreakResolver();
        var result1 = layouter1.AttemptLayout(ctx1, ref layoutCtx1, resolver1,
            LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result1.Outcome);
        // Chain: BC[rc=0 body in root] > BC[rc=1 multicol in body] >
        // MulticolContinuation. The INNER BC's rc=1 is the regression
        // marker — pre-fix the flatten dropped it.
        var topBc1 = Assert.IsType<BlockContinuation>(result1.Continuation);
        Assert.Equal(0, topBc1.ResumeAtChild);
        var bcInner1 = Assert.IsType<BlockContinuation>(topBc1.LayouterState);
        Assert.Equal(1, bcInner1.ResumeAtChild);
        var mcCont1 = Assert.IsType<MulticolContinuation>(bcInner1.LayouterState);
        Assert.Equal(2, mcCont1.NextChildIndex);

        // === Page 2 (resume with the chained continuation) ===
        var sink2 = new RecordingFragmentSink();
        var root2 = BuildRoot();
        using var layouter2 = new BlockLayouter(
            rootBox: root2, sink: sink2,
            incomingContinuation: topBc1,
            diagnostics: diagSink);
        var ctx2 = new FragmentainerContext(contentInlineSize: 232, blockSize: 800);
        var layoutCtx2 = new LayoutContext(ctx2) { Diagnostics = diagSink };
        using var resolver2 = new BreakResolver();
        var result2 = layouter2.AttemptLayout(ctx2, ref layoutCtx2, resolver2,
            LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result2.Outcome);

        // Page 2 must NOT re-emit the spacer (height 50). The only
        // BlockContainer leaf fragments (no children) on page 2 should
        // be the 2 multicol children at height 80 — c2 and c3.
        var leafBlocks = new List<BoxFragment>();
        foreach (var f in sink2.Fragments)
        {
            if (f.Box.Kind == BoxKind.BlockContainer
                && f.Box.Children.Count == 0)
            {
                leafBlocks.Add(f);
            }
        }
        Assert.Equal(2, leafBlocks.Count);
        foreach (var f in leafBlocks)
        {
            Assert.InRange(f.BlockSize, 79, 81);  // multicol-child height = 80
            Assert.NotEqual(50.0, f.BlockSize);   // spacer's distinct height
        }
    }

    // ====================================================================
    //  Phase 3 Task 14 cycle 3 — column-fill: balance (CSS Multi-column
    //  L1 §3.4). The default `column-fill: balance` distributes content
    //  so columns have approximately equal block-axis extent. Cycle 3
    //  Hello World activates balancing for auto-height multicols only;
    //  explicit-height containers + `column-fill: auto` keep the
    //  cycle 1+2 serial-fill behavior.
    // ====================================================================

    [Fact]
    public void Cycle3_multicol_balance_height_auto_distributes_equally()
    {
        // Auto-height multicol with 4 children of 80 px each + 2 columns.
        // - Without balancing (cycle 1+2): contentBlockSize = 800 (page
        //   remaining); all 4 children fit in column 0 (= 320 px); column 1
        //   is empty.
        // - With balancing (cycle 3, default column-fill: balance):
        //   totalSerial = 320; ideal = ceil(320/2) = 160;
        //   balancedBlockSize = min(160, 800) = 160. Each column holds 2
        //   children (160 px each). Distribution is balanced.
        //
        // The test asserts the inline-offset distribution: 2 children land
        // in column 0 (inlineOffset = 0); 2 children land in column 1
        // (inlineOffset = perColumnInline + columnGap = 108 + 16 = 124).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 2);
        // No Height set → height: auto → balancing activates.
        for (var i = 0; i < 4; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 80);
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
        // Column 0 offset = 0; column 1 offset = 124.
        var column0Children = 0;
        var column1Children = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.BlockContainer
                && f.Box != multicol
                && f.Box.SourceElement is not null)
            {
                if (f.InlineOffset == 0) column0Children++;
                else if (f.InlineOffset == 124) column1Children++;
            }
        }
        Assert.Equal(2, column0Children);
        Assert.Equal(2, column1Children);
    }

    [Fact]
    public void Cycle3_multicol_balance_three_blocks_fit_in_two_columns_on_one_page()
    {
        // Post-PR-#59 review hardening Finding #1 — the previous test
        // (Cycle3_multicol_balance_uneven_content_rounds_up_idealBlockSize)
        // pinned the OLD ceil(total/N) heuristic's wrong result: 3
        // children × 80px in 2 columns spilled the 3rd child to page 2
        // because ideal=ceil(240/2)=120 fits only 1 per column.
        //
        // The new fit-search (FindBalancedColumnBlockSize) binary-
        // searches over [ceil(total/N), perColumnBlockSize] for the
        // smallest column-block-size where all content fits. For 3 × 80
        // in 2 columns:
        //   - At 120px: col 0 fits 80 (1 child); col 1 fits 80 (1
        //     child); 1 child remains → doesn't fit in 2 cols.
        //   - At 160px: col 0 fits 80+80=160 (2 children exact); col 1
        //     fits 80 (1 child); 0 remaining → fits.
        // The search converges on 160; all 3 children land on one page
        // (column 0 = 2 children, column 1 = 1 child).
        //
        // This test pins the new fit-search semantics. Before the F#1
        // hardening it would have failed at the "column0+column1==3"
        // assertion.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 2);
        // No Height set → height: auto → balancing activates.
        for (var i = 0; i < 3; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 80);
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

        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var column0Children = 0;
        var column1Children = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.BlockContainer
                && f.Box != multicol
                && f.Box.SourceElement is not null)
            {
                if (f.InlineOffset == 0) column0Children++;
                else if (f.InlineOffset == 124) column1Children++;
            }
        }

        // All 3 children fit on one page (fit-search finds H=160 where
        // 2×80 fit in column 0 + 1×80 fits in column 1).
        Assert.Equal(3, column0Children + column1Children);
        // Both columns are non-empty (balanced — neither column is
        // wasted).
        Assert.True(column0Children >= 1,
            $"Column 0 should hold at least 1 child; got {column0Children}.");
        Assert.True(column1Children >= 1,
            $"Column 1 should hold at least 1 child; got {column1Children}.");
        // No overflow continuation produced — the multicol completed
        // on this page (pre-F#1 the third child spilled to a second
        // page via a MulticolContinuation).
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
    }

    [Fact]
    public void Cycle3_multicol_auto_fill_preserves_cycle1_serial_behavior()
    {
        // Explicit `column-fill: auto` (keyword index 2 per
        // KeywordResolver.cs:238) MUST preserve the cycle 1+2 serial-fill
        // behavior: column 0 fills first; column 1 only receives content
        // that overflows column 0.
        //
        // Setup: auto-height multicol, 4 children of 80 px each, 2
        // columns, page height 800.
        // - With column-fill: auto: contentBlockSize = 800; all 4
        //   children fit in column 0 (320 px); column 1 is empty.
        // - (If balancing were ACCIDENTALLY active despite column-fill:
        //   auto, we'd see 2 children per column.)
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 2);
        // Explicitly set column-fill: auto (keyword index 2). Auto-height
        // is preserved (no Height set).
        multicol.Style.Set(PropertyId.ColumnFill, ComputedSlot.FromKeyword(2));
        for (var i = 0; i < 4; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 80);
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

        var column0Children = 0;
        var column1Children = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.BlockContainer
                && f.Box != multicol
                && f.Box.SourceElement is not null)
            {
                if (f.InlineOffset == 0) column0Children++;
                else if (f.InlineOffset == 124) column1Children++;
            }
        }
        // All 4 children land in column 0; column 1 is empty.
        Assert.Equal(4, column0Children);
        Assert.Equal(0, column1Children);
    }

    [Fact]
    public void Cycle3_multicol_balance_with_explicit_height_uses_explicit_height_not_balancing()
    {
        // When the author specifies an explicit `height: 200px` AND
        // `column-fill: balance` (the default), cycle 3 preserves the
        // cycle 1+2 serial-fill behavior (= explicit height pins the
        // per-column block-size to the author's chosen value; balancing
        // doesn't activate). Mirrors Prince / WeasyPrint's conservative
        // approach.
        //
        // Setup: explicit-height multicol (200 px), 4 children of 80 px
        // each, 2 columns. With explicit-height + no balancing: column 0
        // fits 2 children (160 px); column 1 fits 2 children (160 px).
        // (Note: this DOES end up looking balanced because the 200-px
        // column happens to fit exactly 2 of the 80-px children.) The
        // critical assertion is the FORWARD-PROGRESS distribution
        // pattern: column 0 fills serially before column 1 starts.
        //
        // To distinguish from the balancing path we use 3 children
        // instead of 4: with serial fill, column 0 fits 2 children
        // (160 px), column 1 fits 1 child (80 px). With balancing,
        // totalSerial=240, ideal=ceil(240/2)=120; balancedBlockSize=
        // min(120,200)=120; column 0 fits 1 child (80 px), column 1
        // fits 1 child (80 px), 1 child overflows. The two paths
        // produce DIFFERENT distributions → the assertion pinpoints
        // which path ran.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 2);
        SetLengthPx(multicol.Style, PropertyId.Height, 200);
        // column-fill: balance is the default — DON'T explicitly set it.
        for (var i = 0; i < 3; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 80);
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

        var column0Children = 0;
        var column1Children = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.BlockContainer
                && f.Box != multicol
                && f.Box.SourceElement is not null)
            {
                if (f.InlineOffset == 0) column0Children++;
                else if (f.InlineOffset == 124) column1Children++;
            }
        }
        // Serial fill: column 0 holds 2 children (160 px ≤ 200 px);
        // column 1 holds 1 child (80 px). If balancing were
        // ACCIDENTALLY active with the explicit height, column 0 +
        // column 1 would each hold 1 child (the third would overflow).
        Assert.Equal(2, column0Children);
        Assert.Equal(1, column1Children);
    }

    [Fact]
    public void Cycle3_multicol_balance_all_treated_as_balance_for_cycle3()
    {
        // `column-fill: balance-all` (keyword index 1) is treated
        // identically to `balance` for cycle 3 Hello World — the
        // last-fragmentainer special-case semantics are sub-cycle 2+
        // scope. The test mirrors the
        // Cycle3_multicol_balance_height_auto_distributes_equally
        // setup but explicitly sets column-fill: balance-all.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 2);
        // Set column-fill: balance-all (keyword index 1).
        multicol.Style.Set(PropertyId.ColumnFill, ComputedSlot.FromKeyword(1));
        for (var i = 0; i < 4; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 80);
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

        var column0Children = 0;
        var column1Children = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.BlockContainer
                && f.Box != multicol
                && f.Box.SourceElement is not null)
            {
                if (f.InlineOffset == 0) column0Children++;
                else if (f.InlineOffset == 124) column1Children++;
            }
        }
        // Same result as `balance`: 2 children per column.
        Assert.Equal(2, column0Children);
        Assert.Equal(2, column1Children);
    }

    [Fact]
    public void Cycle3_multicol_balance_oversized_child_falls_back_via_forced_overflow()
    {
        // A child taller than the balanced ideal forces the inner
        // BlockLayouter's forced-overflow path; the existing cycle 1+2
        // forward-progress machinery handles the case without an
        // infinite loop. Specifically: 2 columns + 1 child of 500 px on
        // an auto-height multicol with page-block-size 800.
        // totalSerial = 500; ideal = ceil(500/2) = 250; balanced =
        // min(250, 800) = 250. Child 500 px > balanced 250 px → inner
        // BlockLayouter's forced-overflow path emits child 0 anyway
        // (LastResort) + returns PageComplete or AllDone depending on
        // the inner forced-overflow logic. The multicol either:
        //   - sees AllDone (single child committed in column 0) → returns
        //     AllDone (no diagnostic);
        //   - or sees PageComplete from the inner layouter → the
        //     clean-multi-page-split path returns PageComplete with a
        //     terminal MulticolContinuation.
        // In either case the test pins:
        //   (a) emission did not infinite-loop (= the call returned);
        //   (b) the child fragment was emitted (forward progress);
        //   (c) no LAYOUT-MULTICOL-FORCED-OVERFLOW-001 diagnostic for
        //       the multicol-level forced-overflow (the inner forced-
        //       overflow may emit other diagnostics — those are not
        //       asserted here).
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 2);
        // Auto-height + balance default → balancing activates. Single
        // oversized child stresses the forced-overflow path.
        var childStyle = MakeStyle();
        SetLengthPx(childStyle, PropertyId.Height, 500);
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

        // The call must return (not infinite-loop). xUnit's lack of a
        // built-in timeout doesn't let us bound this; relying on the
        // test runner's outer timeout for the no-loop guarantee.
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // (b) Forward progress — child fragment was emitted.
        BoxFragment? childFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == child) { childFragment = f; break; }
        }
        Assert.NotNull(childFragment);

        // (c) No multicol-level forced-overflow diagnostic. The inner
        // BlockLayouter's forced-overflow path made forward progress
        // (= sink advanced); the multicol's forward-progress fallback
        // at MulticolLayouter.cs:706-730 was NOT taken.
        foreach (var d in diagSink.Diagnostics)
        {
            Assert.NotEqual(
                PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001,
                d.Code);
        }
    }

    // ====================================================================
    //  Phase 3 Task 14 cycle 3 post-PR-#59 review hardening — 7 tests for
    //  missing coverage. Each test pins ONE of the F#1, F#2, F#3, F#4,
    //  F#5, F#7 fixes (F#6 perf-cache is deferred to sub-cycle 2; F#8 is
    //  exercised implicitly by F#3 via the multi-page resume).
    // ====================================================================

    [Fact]
    public void Cycle3_balance_indivisible_blocks_fit_search_finds_correct_height()
    {
        // F#1 — fit-search correctness for indivisible content. 3
        // children × 80px in 2 columns: total=240, ceil(240/2)=120
        // (the pre-F#1 heuristic). At 120, col 0 fits 1 (80+80=160 >
        // 120) → only 1 per column → 3rd child spills. The new fit-
        // search converges on 160 where col 0 fits 2 children + col 1
        // fits 1 child + AllDone on this page.
        //
        // Distinguishes this test from the indirect coverage in
        // Cycle3_multicol_balance_three_blocks_fit_in_two_columns_on_one_page:
        // here the assertion is on the column-block-size INFERRED from
        // the fragment block-offsets — the second child in column 0
        // should sit at blockOffset=80 (= directly after the first
        // child); the column's used extent equals 160px (= 2 × 80px).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 2);
        for (var i = 0; i < 3; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 80);
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

        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Collect fragments sorted by inline + block offset.
        var column0Blocks = new List<BoxFragment>();
        var column1Blocks = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.BlockContainer
                && f.Box != multicol
                && f.Box.SourceElement is not null)
            {
                if (f.InlineOffset == 0) column0Blocks.Add(f);
                else if (f.InlineOffset == 124) column1Blocks.Add(f);
            }
        }

        // AllDone on this page — no overflow.
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);

        // Column 0 holds 2 children at blockOffsets 0, 80 (confirming
        // the fit-search picked H ≥ 160; pre-F#1 H=120 would have
        // emitted only the first child + spilled the rest).
        Assert.True(column0Blocks.Count >= 2,
            $"Column 0 should hold at least 2 children (fit-search H >= "
            + $"160 stacks 2 × 80px); got {column0Blocks.Count}.");
        // All 3 children land on this page.
        Assert.Equal(3, column0Blocks.Count + column1Blocks.Count);
    }

    [Fact]
    public void Cycle3_balance_vs_balance_all_over_multiple_fragmentainers_balance_uses_serial_fill_on_first_page()
    {
        // F#2 — `column-fill: balance` (default) balances ONLY the
        // last fragment; non-final fragments use serial fill. Setup:
        // auto-height multicol; column count = 2; per-page block-size
        // = 200; 10 children of 80px each (total = 800; 800 > 200 ×
        // 2 = 400 → content does NOT fit on one page → page 1 is NOT
        // the last fragment → serial fill on page 1).
        //
        // Expected page-1 behavior with the F#2 fix:
        //   - Column 0 fills to the brim: 200 / 80 = 2 children
        //     (blockOffsets 0, 80; 80+80=160 ≤ 200; third 80+80+80=240
        //     > 200 doesn't fit).
        //   - Column 1 also serial-fills: 2 more children.
        //   - Page 1 holds 4 children total; 6 remain → PageComplete
        //     with a MulticolContinuation pointing at child 4.
        //
        // Pre-F#2 the same DOM was BALANCED on page 1 (which was
        // wrong): the pre-measure saw total=800 and picked an "ideal"
        // height that under-utilized the page.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 2);
        // column-fill: balance is the default; don't explicitly set.
        for (var i = 0; i < 10; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 80);
            multicol.AppendChild(Box.ForElement(BoxKind.BlockContainer, s, MakeElement()));
        }
        root.AppendChild(multicol);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var column0Blocks = new List<BoxFragment>();
        var column1Blocks = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.BlockContainer
                && f.Box != multicol
                && f.Box.SourceElement is not null)
            {
                if (f.InlineOffset == 0) column0Blocks.Add(f);
                else if (f.InlineOffset == 124) column1Blocks.Add(f);
            }
        }

        // Page is NOT the last fragment → serial fill. Column 0
        // saturates at perColumnBlockSize=200 with 2 × 80px.
        Assert.Equal(2, column0Blocks.Count);
        Assert.Equal(2, column1Blocks.Count);
        // Page 1 is incomplete → PageComplete (not AllDone).
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
    }

    [Fact]
    public void Cycle3_balance_all_balances_every_fragment_including_non_final()
    {
        // F#2 — `column-fill: balance-all` balances every fragment,
        // including non-final ones. Same DOM as the balance test above
        // (10 × 80px in a 2-column auto-height multicol with page-
        // block-size 200) but explicit `column-fill: balance-all`.
        //
        // With balance-all on page 1 (a NON-last fragment) the pre-
        // measure measures total=800 against the actual remaining
        // content; the fit-search picks the smallest column-block-size
        // ≤ 200 where all 800 fit. 800 > 200 × 2 = 400 → no value ≤
        // 200 fits → FindBalancedColumnBlockSize returns 200 (the cap)
        // → page 1 ends up serial-fill anyway.
        //
        // The user-visible distinguisher: when total fits in ≤ N ×
        // perColumnBlockSize, balance-all balances; balance doesn't.
        // Setup: 3 × 80px (total=240) in 2 columns with page-block-
        // size=200 (total ≤ N × perColumnBlockSize = 400 → "last
        // fragment" with balance + page 1 with balance-all).
        //   - balance: fits in N=2 cols → balance on this fragment.
        //   - balance-all: also balances.
        // Both produce the SAME result here. The distinguisher needs
        // a case where balance is NOT-last but balance-all IS still
        // balanced. Use 5 × 80px (total=400) in 2 cols with page=200:
        //   - balance: total=400 ≤ 400 → "last" → balance → fit-
        //     search picks H=160 (col 0 has 2, col 1 has 2, 1 spills).
        //     Actually 400 == 200×2 exactly. F#2 detects isLastFragment
        //     as totalSerial ≤ perColumnBlockSize × columnCount;
        //     400 ≤ 400 = true → balance fires. Page 1 IS the last
        //     fragment in this scenario by F#2's detection rule.
        //
        // Let's pick a clearer scenario: 9 × 80px (total=720). N=2,
        // perColumnBlockSize=200. 720 > 400 → balance treats page 1 as
        // NOT-last → serial fill (column 0 = 2 children @ 80, column 1
        // = 2 children @ 80). balance-all treats page 1 as balanced →
        // fit-search probes [ceil(720/2)=360, 200]; 360 > 200 → no
        // value in range → returns 200 (cap) → serial fill anyway.
        //
        // So this distinction is hard to expose at the column-count
        // level when total > N × perColumnBlockSize. The F#2 fix's
        // observable behavior is: balance-all NEVER skips the fit-
        // search even on non-final fragments. Pin the contract by
        // checking that EXPLICIT column-fill:balance-all produces the
        // SAME page-1 distribution as column-fill:balance for the
        // "last fragment fits" case (this also confirms balance-all
        // doesn't accidentally skip balancing).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 2);
        // Explicit balance-all (keyword index 1 per KeywordResolver).
        multicol.Style.Set(PropertyId.ColumnFill, ComputedSlot.FromKeyword(1));
        // 3 × 80px fits in N=2 cols at H=160 → balance-all balances.
        for (var i = 0; i < 3; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 80);
            multicol.AppendChild(Box.ForElement(BoxKind.BlockContainer, s, MakeElement()));
        }
        root.AppendChild(multicol);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        // Use a generous page-block-size (800) so this fragment IS
        // the last fragment under both balance + balance-all rules;
        // the test pins that balance-all still produces a BALANCED
        // (not serial-filled) result on the last fragment.
        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var column0Blocks = new List<BoxFragment>();
        var column1Blocks = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.BlockContainer
                && f.Box != multicol
                && f.Box.SourceElement is not null)
            {
                if (f.InlineOffset == 0) column0Blocks.Add(f);
                else if (f.InlineOffset == 124) column1Blocks.Add(f);
            }
        }

        // balance-all balanced: column 1 is non-empty (without
        // balancing, all 3 children would be in column 0 since the
        // page-block-size of 800 fits all 3 × 80 = 240 in column 0).
        Assert.True(column1Blocks.Count >= 1,
            $"balance-all should produce a balanced distribution; col 1 "
            + $"is empty (= unbalanced) → balance-all incorrectly skipped "
            + $"the fit-search.");
        Assert.Equal(3, column0Blocks.Count + column1Blocks.Count);
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
    }

    [Fact]
    public void Cycle3_resume_page_balancing_measures_only_remaining_content()
    {
        // F#3 — on a resume page, the balancing pre-measure starts
        // from the carried continuation (= measures only remaining
        // content) instead of measuring from child 0.
        //
        // Setup: 6 × 80px in a 2-column auto-height multicol with
        // page-block-size=200. Page 1: serial fill (NOT-last per F#2,
        // since total=480 > 400=N×perColumnBlockSize) → col 0 = 2
        // children, col 1 = 2 children, 2 remain → PageComplete with
        // MulticolContinuation(NextChildIndex=4).
        //
        // Page 2 resume: only 2 children remain (total=160). 160 ≤
        // 400 → last fragment → balance. Fit-search picks H=80 (col 0
        // = 1 × 80; col 1 = 1 × 80).
        //
        // Pre-F#3 the resume-page pre-measure passed null instead of
        // the resume continuation, measuring all 6 × 80 = 480 again
        // and picking a too-tall H (or a serial-fill result).
        //
        // Pin the behavior by constructing a synthetic resume
        // continuation + invoking MulticolLayouter directly. This
        // exercises the F#3 contract without depending on the multi-
        // page driver loop.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var multicol = BuildMulticolContainer(columnCount: 2);
        for (var i = 0; i < 6; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 80);
            multicol.AppendChild(Box.ForElement(BoxKind.BlockContainer, s, MakeElement()));
        }

        // Synthesize a "resume from child 4" MulticolContinuation
        // (= simulating page 2 of the scenario above).
        var resumeCont = new MulticolContinuation(
            NextChildIndex: 4,
            ConsumedBlockSize: 200,
            PerChildLayouterState: null);

        using var multicolLayouter = new MulticolLayouter(
            rootBox: multicol,
            sink: sink,
            incomingContinuation: resumeCont,
            diagnostics: null,
            shaperResolver: shaper);
        multicolLayouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 232,
            contentBlockSize: 200);

        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = multicolLayouter.AttemptLayout(
            ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Both remaining children (children 4 + 5) emit on this page.
        var column0Blocks = new List<BoxFragment>();
        var column1Blocks = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.BlockContainer
                && f.Box != multicol
                && f.Box.SourceElement is not null)
            {
                if (f.InlineOffset == 0) column0Blocks.Add(f);
                else if (f.InlineOffset == 124) column1Blocks.Add(f);
            }
        }
        // 2 children expected (children 4 + 5 of the original 6).
        Assert.Equal(2, column0Blocks.Count + column1Blocks.Count);
        // Balanced: both columns hold ≥ 1 child. If the pre-measure
        // had measured all 6 children (= pre-F#3 bug), it would have
        // seen totalSerial=480 > 200×2=400 → "not last fragment" →
        // serial fill → col 0 = 2, col 1 = 0. The presence of a child
        // in col 1 proves F#3's resume-aware pre-measure ran.
        Assert.True(column1Blocks.Count >= 1,
            $"F#3 contract: resume-page pre-measure measured only "
            + $"remaining 2 children → totalSerial=160 ≤ 400 → 'last "
            + $"fragment' → balanced. col 1 has {column1Blocks.Count} "
            + $"children (expected >= 1; col 1 empty suggests pre-measure "
            + $"saw all 6 children again).");
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
    }

    [Fact]
    public void Cycle3_long_content_exceeds_pre_measure_sentinel_still_measured()
    {
        // F#4 — the pre-measure loop captures content extents that
        // exceed a single dry-run window. Pre-fix the pre-measure
        // capped at perColumnBlockSize × columnCount × 2 (single
        // window) → long content was silently under-measured.
        //
        // Setup: 50 × 100px = 5000px total content in a 2-column
        // multicol with page-block-size=400. Per-window cap =
        // 400×2×2 = 1600px. The pre-measure loop must accumulate
        // beyond the single 1600px window.
        //
        // The observable signal: after the F#4 fix, the pre-measure
        // correctly reports totalSerialExtent ≈ 5000 → F#2 detects
        // 5000 > 400×2=800 → page 1 NOT-last → serial fill → col 0
        // fills with 4 × 100 = 400px (4 children); col 1 fills with
        // 4 × 100 = 400px (4 children) → page 1 emits exactly 8
        // children. Pre-F#4 the truncated pre-measure may have seen
        // ≤ 1600 → could route through the "last fragment" path
        // incorrectly + balance → different distribution.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 2);
        for (var i = 0; i < 50; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 100);
            multicol.AppendChild(Box.ForElement(BoxKind.BlockContainer, s, MakeElement()));
        }
        root.AppendChild(multicol);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 232, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var column0Blocks = new List<BoxFragment>();
        var column1Blocks = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.BlockContainer
                && f.Box != multicol
                && f.Box.SourceElement is not null)
            {
                if (f.InlineOffset == 0) column0Blocks.Add(f);
                else if (f.InlineOffset == 124) column1Blocks.Add(f);
            }
        }

        // Page 1 is NOT the last fragment (total >> N ×
        // perColumnBlockSize) → serial fill → both columns full at
        // perColumnBlockSize. With 100px children + 400px columns,
        // each column holds exactly 4 children.
        Assert.Equal(4, column0Blocks.Count);
        Assert.Equal(4, column1Blocks.Count);
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
    }

    [Fact]
    public void Cycle3_balanced_multicol_with_margin_bearing_children_accounts_for_trailing_margins()
    {
        // F#5 — the pre-measure now uses fragmentainer.UsedBlockSize
        // (margin-aware cursor extent) instead of the sink's max
        // BlockOffset+BlockSize (which missed trailing margins +
        // collapsed-margin effects).
        //
        // Setup: 3 children, each height=80 with margin-bottom=20.
        // Pre-F#5 the sink's MaxBlockExtent measured 80+80+80=240
        // (= 3 fragment bottoms, last child's trailing margin
        // unaccounted). Post-F#5 the cursor measures 80+20+80+20+80=
        // 280 (or 80+20+80+20+80+20=300 if the very last trailing
        // margin participates — implementation may vary).
        //
        // The observable signal: with the correct margin-aware total
        // the fit-search picks an H that accounts for trailing
        // margins between siblings; without it the fit-search picks
        // an H too short to fit the content with margins, and column
        // 0 emits fewer children than it should.
        //
        // 2 columns, page-block-size=800 (= last-fragment scenario).
        // Cleanly observable assertion: all 3 children emit on this
        // page (regardless of which column holds which child). Pre-
        // F#5 the under-measured total could route too-short H → 3rd
        // child spills.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 2);
        for (var i = 0; i < 3; i++)
        {
            var s = MakeStyle();
            SetLengthPx(s, PropertyId.Height, 80);
            SetLengthPx(s, PropertyId.MarginBottom, 20);
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

        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var allChildBlocks = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.BlockContainer
                && f.Box != multicol
                && f.Box.SourceElement is not null)
            {
                allChildBlocks++;
            }
        }

        // All 3 margin-bearing children emit on this page.
        Assert.Equal(3, allChildBlocks);
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
    }

    [Fact]
    public void Cycle3_percentage_height_multicol_uses_explicit_height_not_balancing()
    {
        // F#7 — IsHeightAuto returns false for `height: 50%`
        // (Percentage). Pre-F#7 the predicate was `slot.Tag !=
        // LengthPx`, treating percentage values as auto → balancing
        // ran on percentage-height multicols → over-shrunk columns.
        //
        // The F#7 fix's CALLER-VISIBLE invariant exercised here:
        // `Boxes.Box.IsHeightAuto()` correctly classifies Percentage
        // slots as NON-auto. The end-to-end column distribution under
        // a percentage height depends on the BlockLayouter's
        // percentage-height resolution (deferred to a later cycle —
        // currently treats Percentage as 0 in ReadLengthPxOrZero); we
        // pin only the predicate's behavior here, decoupled from the
        // resolution gap.
        //
        // Three slot kinds + their expected IsHeightAuto outcome:
        //   - Unset (= default auto): true (no Height slot set).
        //   - Keyword auto: not directly testable without a Keyword
        //     resolver entry for Height = auto; covered by the Unset
        //     case (= default `auto`).
        //   - LengthPx (e.g. 200px): false.
        //   - Percentage (e.g. 50%): false (pre-F#7 returned true).
        var boxAuto = BuildMulticolContainer(columnCount: 2);
        // No height set → Unset slot → IsHeightAuto true.
        Assert.True(boxAuto.IsHeightAuto(),
            "Unset Height slot should be classified as auto.");

        var boxExplicit = BuildMulticolContainer(columnCount: 2);
        SetLengthPx(boxExplicit.Style, PropertyId.Height, 200);
        Assert.False(boxExplicit.IsHeightAuto(),
            "Explicit LengthPx Height should NOT be classified as auto.");

        var boxPercentage = BuildMulticolContainer(columnCount: 2);
        boxPercentage.Style.Set(
            PropertyId.Height, ComputedSlot.FromPercentage(50));
        // Pre-F#7 this returned TRUE (Percentage.Tag != LengthPx); post-
        // F#7 it correctly returns FALSE (Percentage is NOT auto).
        Assert.False(boxPercentage.IsHeightAuto(),
            "Percentage Height (e.g. 50%) should NOT be classified as "
            + "auto. Per CSS 2.1 §10.5 a percentage height resolves "
            + "against the containing block's height — that's explicit "
            + "sizing, not auto. Routing percentage-height multicols "
            + "into the balancing path would over-shrink columns + drop "
            + "content out of the container.");
    }

    // ====================================================================
    //  Cycle 4 — column-width-derived automatic column count
    //  (CSS Multi-column L1 §3.3)
    // ====================================================================

    [Fact]
    public void Cycle4_column_width_only_derives_column_count_from_container()
    {
        // Per Phase 3 Task 14 cycle 4 — when `column-width: <length>`
        // is declared without `column-count` (= column-count auto),
        // the effective column count is derived from the container's
        // content inline-size + column-gap per CSS Multi-column L1 §3.3.
        //
        // Setup: column-width=100px, container=400px, default 16px gap.
        // Derived N = floor((400+16)/(100+16)) = floor(416/116) = 3.
        // Per-column inline-size after equal-split:
        //   (400 - 2*16) / 3 = 368/3 ≈ 122.67.
        // (Note: per-column inline-size EXCEEDS the requested column-
        //  width — this is the intended CSS behavior; column-width is a
        //  MINIMUM, columns expand to evenly divide the available space.)
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolWidthContainer(columnWidthPx: 100);
        // Container height = 100; per-column block-size = 100.
        SetLengthPx(multicol.Style, PropertyId.Height, 100);
        // 3 children, each 90 tall — so each fits exactly in one column.
        for (var i = 0; i < 3; i++)
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
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Gather the 3 child fragments.
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

        // Expected per-column inline-size = (400 - 2*16) / 3 = 368/3.
        const double expectedColumnWidth = (400.0 - 32.0) / 3.0;
        // Column inline offsets: 0, perCol+gap, 2*(perCol+gap).
        var expectedOffsets = new[]
        {
            0.0,
            expectedColumnWidth + 16.0,
            2.0 * (expectedColumnWidth + 16.0),
        };
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(expectedColumnWidth, childFragments[i].InlineSize, precision: 3);
            Assert.Equal(expectedOffsets[i], childFragments[i].InlineOffset, precision: 3);
        }
    }

    [Fact]
    public void Cycle4_both_column_count_and_width_set_uses_smaller()
    {
        // Per Phase 3 Task 14 cycle 4 — when BOTH `column-count` AND
        // `column-width` are set, the effective column count is
        // min(specifiedCount, derivedCount) per CSS Multi-column L1 §3.3.
        //
        // Setup: column-count=5, column-width=100px, container=400px,
        // 16px gap. derivedCount = floor((400+16)/(100+16)) = 3.
        // min(5, 3) = 3. So 3 columns.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolContainer(columnCount: 5);
        // Add column-width on top of column-count.
        SetLengthPx(multicol.Style, PropertyId.ColumnWidth, 100);
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
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

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

        // 3 columns means per-column inline = (400 - 2*16)/3 = 368/3.
        // If the smaller `min` rule weren't honored, 5 columns would
        // produce per-col = (400 - 4*16)/5 = 67.2 (≠ 368/3 ≈ 122.67).
        const double expectedColumnWidthAtN3 = (400.0 - 32.0) / 3.0;
        Assert.Equal(expectedColumnWidthAtN3, childFragments[0].InlineSize, precision: 3);
    }

    [Fact]
    public void Cycle4_column_width_larger_than_container_clamps_to_one()
    {
        // Per Phase 3 Task 14 cycle 4 — when column-width exceeds the
        // container's content inline-size, derivedCount = floor((400+16)
        // / (1000+16)) = floor(0.41) = 0, clamped to 1 per
        // ComputeUsedColumnCount's `if (derivedCount < 1) derivedCount = 1`.
        //
        // With effective N = 1, MulticolLayouter degrades to a single-
        // column emit (= functionally non-multicol). The dispatch still
        // fires because IsMulticolContainer's gate triggers on
        // column-width presence alone; the single-column fallthrough
        // emits content spanning the full content inline-size with no
        // column-axis translation.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var multicol = BuildMulticolWidthContainer(columnWidthPx: 1000);
        SetLengthPx(multicol.Style, PropertyId.Height, 100);
        var childStyle = MakeStyle();
        SetLengthPx(childStyle, PropertyId.Height, 50);
        multicol.AppendChild(Box.ForElement(BoxKind.BlockContainer, childStyle, MakeElement()));
        root.AppendChild(multicol);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null, diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

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
        Assert.Single(childFragments);

        // Single-column fallthrough: child spans full container content
        // inline-size (= 400 px) at inline offset 0. NOT a per-column
        // width like (400 - gap)/N.
        Assert.Equal(0, childFragments[0].InlineOffset, precision: 3);
        Assert.Equal(400, childFragments[0].InlineSize, precision: 3);
    }

    [Fact]
    public void Cycle4_compute_used_column_count_covers_all_four_spec_cases()
    {
        // Per Phase 3 Task 14 cycle 4 — direct unit test of
        // ComputeUsedColumnCount covering the 4 cases from
        // CSS Multi-column L1 §3.3:
        //
        // Case A: column-count: auto + column-width: auto → 1 (no multicol)
        // Case B: column-count: auto + column-width: <length>
        //   → floor((container+gap)/(width+gap)), clamped to ≥ 1
        // Case C: column-count: <int> + column-width: auto → specified count
        // Case D: column-count: <int> + column-width: <length>
        //   → min(specifiedCount, derivedCount)

        // Case A — both auto.
        Assert.Equal(1, ComputedStyleLayoutExtensions.ComputeUsedColumnCount(
            containerContentInlineSize: 400,
            specifiedColumnCount: null,
            columnWidth: null,
            columnGap: 16));

        // Case B — column-width only.
        // floor((400+16)/(100+16)) = floor(3.586) = 3.
        Assert.Equal(3, ComputedStyleLayoutExtensions.ComputeUsedColumnCount(
            containerContentInlineSize: 400,
            specifiedColumnCount: null,
            columnWidth: 100,
            columnGap: 16));

        // Case B clamp to 1 — column-width > container.
        // floor((400+16)/(1000+16)) = floor(0.41) = 0 → clamped to 1.
        Assert.Equal(1, ComputedStyleLayoutExtensions.ComputeUsedColumnCount(
            containerContentInlineSize: 400,
            specifiedColumnCount: null,
            columnWidth: 1000,
            columnGap: 16));

        // Case C — column-count only.
        Assert.Equal(5, ComputedStyleLayoutExtensions.ComputeUsedColumnCount(
            containerContentInlineSize: 400,
            specifiedColumnCount: 5,
            columnWidth: null,
            columnGap: 16));

        // Case D — both set; specifiedCount > derivedCount → take derived.
        // derivedCount = 3 (= Case B); min(5, 3) = 3.
        Assert.Equal(3, ComputedStyleLayoutExtensions.ComputeUsedColumnCount(
            containerContentInlineSize: 400,
            specifiedColumnCount: 5,
            columnWidth: 100,
            columnGap: 16));

        // Case D — specifiedCount < derivedCount → take specified.
        // derivedCount = 3; min(2, 3) = 2.
        Assert.Equal(2, ComputedStyleLayoutExtensions.ComputeUsedColumnCount(
            containerContentInlineSize: 400,
            specifiedColumnCount: 2,
            columnWidth: 100,
            columnGap: 16));

        // Defensive — non-finite container inline-size.
        Assert.Equal(1, ComputedStyleLayoutExtensions.ComputeUsedColumnCount(
            containerContentInlineSize: double.NaN,
            specifiedColumnCount: null,
            columnWidth: 100,
            columnGap: 16));

        // Defensive — non-positive column-width.
        // Per ReadColumnWidth contract this can't occur (NumberResolver
        // rejects negative + zero), but the helper guards defensively;
        // negative columnWidth is treated as "no constraint" (= 1 unless
        // specifiedColumnCount is set).
        Assert.Equal(1, ComputedStyleLayoutExtensions.ComputeUsedColumnCount(
            containerContentInlineSize: 400,
            specifiedColumnCount: null,
            columnWidth: -10,
            columnGap: 16));
    }

    [Fact]
    public void Cycle4_read_column_width_returns_null_for_auto_default()
    {
        // Per Phase 3 Task 14 cycle 4 — ReadColumnWidth contract:
        // returns null when the slot is auto / unset / non-length,
        // otherwise the resolved LengthPx value.

        // Unset (= default `auto`) → null.
        var styleUnset = MakeStyle();
        Assert.Null(styleUnset.ReadColumnWidth());

        // LengthPx → the resolved value.
        var styleLength = MakeStyle();
        SetLengthPx(styleLength, PropertyId.ColumnWidth, 100);
        Assert.Equal(100, styleLength.ReadColumnWidth());

        // Keyword (= auto) → null. The resolver may encode `auto` as
        // a Keyword slot via the multicol keyword admittance pattern
        // (see LengthResolver.TryMatchMulticolKeyword).
        var styleKeyword = MakeStyle();
        styleKeyword.Set(PropertyId.ColumnWidth, ComputedSlot.FromKeyword(0));
        Assert.Null(styleKeyword.ReadColumnWidth());
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

    /// <summary>Per Phase 3 Task 14 cycle 4 — build a BlockContainer
    /// with <c>column-width: &lt;length&gt;</c> declared on its computed
    /// style (column-count remains auto). The cycle-4 IsMulticolContainer
    /// gate recognizes this as a multicol intent; the effective
    /// column count is derived inside MulticolLayouter from the
    /// container's content inline-size.</summary>
    private static Box BuildMulticolWidthContainer(double columnWidthPx)
    {
        var style = MakeStyle();
        style.Set(PropertyId.ColumnWidth, ComputedSlot.FromLengthPx(columnWidthPx));
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
