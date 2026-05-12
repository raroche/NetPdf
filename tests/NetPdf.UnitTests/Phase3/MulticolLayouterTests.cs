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
    public void Multicol_content_overflowing_all_columns_emits_forced_overflow_diagnostic()
    {
        // Two columns each 100 tall; three children each 90 tall.
        // Column 0 fits child 0 (90 ≤ 100); column 1 fits child 1
        // (90 ≤ 100); child 2 doesn't fit anywhere → forced-overflow
        // diagnostic.
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

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var hasForcedOverflow = false;
        foreach (var d in diagSink.Diagnostics)
        {
            if (d.Code == PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001
                && d.Severity == PaginateDiagnosticSeverity.Warning)
            {
                hasForcedOverflow = true;
                break;
            }
        }
        Assert.True(hasForcedOverflow,
            "Expected LAYOUT-MULTICOL-FORCED-OVERFLOW-001 when content "
            + "overflows the N multicol columns. Diagnostics: "
            + string.Join("; ", FormatDiagnostics(diagSink.Diagnostics)));
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
        // The constructor accepts MulticolContinuation OR null.
        // Other continuation kinds throw.
        var sink = new RecordingFragmentSink();
        var box = BuildMulticolContainer(columnCount: 2);
        var continuation = new BlockContinuation(ResumeAtChild: 0, ConsumedBlockSize: 0);

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
