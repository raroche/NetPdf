// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
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
/// Phase 3 Task 11 cycle 1 sub-cycle 1 — BlockLayouter ↔ InlineLayouter
/// integration tests. Drives <see cref="BlockLayouter"/> through
/// inline-only block containers (the smallest dispatch case — a single
/// <see cref="BoxKind.BlockContainer"/> whose only child is a
/// <see cref="BoxKind.TextRun"/>) + verifies:
/// <list type="bullet">
///   <item>The dispatch emits ONE <see cref="BoxFragment"/> with
///   <see cref="BoxFragment.InlineLines"/> populated.</item>
///   <item>The fragmentainer cursor advances by
///   <c>lineCount × placeholder-line-height</c>.</item>
///   <item>An inline-only block with no TextRun text content emits
///   no fragment (no shaped glyphs → no lines → no fragment).</item>
///   <item>Constructing the layouter without a shaper resolver
///   emits the <c>LAYOUT-INLINE-SKIPPED-NO-SHAPER-RESOLVER-001</c>
///   diagnostic + skips the inline content (back-compat with the
///   pre-sub-cycle-1 behavior).</item>
/// </list>
///
/// <para>Fixture mirrors <see cref="BlockLayouterTests"/>'s pattern —
/// <c>ComputedStyle.RentForExclusiveTesting()</c> for styles, a
/// recording fragment sink, the default <see cref="BreakResolver"/>,
/// a synthetic-font-backed <see cref="HbShaper"/> wrapped in a
/// minimal <see cref="IShaperResolver"/>.</para>
/// </summary>
public sealed class BlockInlineIntegrationTests
{
    [Fact]
    public void Block_with_single_TextRun_emits_BoxFragment_with_InlineLines_set()
    {
        // Arrange — a <BlockContainer> with one TextRun child. The
        // BlockLayouter's IsInlineOnlyBlockContainer predicate
        // recognizes this as an inline-only block + dispatches into
        // LayoutInlineContent → InlineLayouter.LayoutPerRun. The
        // resolver hands back a SyntheticFont-backed HbShaper so the
        // shape pass produces real glyphs.
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var blockStyle = MakeStyle();
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        block.AppendChild(Box.TextRun("A", MakeStyle()));
        root.AppendChild(block);

        using var layouter = new BlockLayouter(
            root, sink,
            incomingContinuation: null,
            diagnostics: null,
            shaperResolver: resolver);

        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var breakResolver = new BreakResolver();

        // Act
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, breakResolver, LayoutAttemptStrategy.Strict);

        // Assert — one fragment with non-null InlineLines.
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Single(sink.Fragments);
        var fragment = sink.Fragments[0];
        Assert.Same(block, fragment.Box);
        Assert.NotNull(fragment.InlineLines);
        // The single "A" should fit on one line (well under 600 px).
        Assert.Single(fragment.InlineLines!);
        // InlineSize is the available width (no margin honoring in
        // sub-cycle 1).
        Assert.Equal(600, fragment.InlineSize);
        // BlockOffset = caller-provided cursor start (= 0 on a fresh
        // page).
        Assert.Equal(0, fragment.BlockOffset);
        Assert.Equal(0, fragment.InlineOffset);
    }

    [Fact]
    public void Block_with_inline_content_advances_fragmentainer_by_lineCount_times_lineHeight()
    {
        // Arrange — same setup as the first test. Sub-cycle 1's
        // placeholder line-height is `font-size × 1.2`. With no
        // font-size set on the block's style, ReadLengthPxOrDefault
        // returns the 16-px default → placeholder lineHeight =
        // 16 × 1.2 = 19.2.
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        block.AppendChild(Box.TextRun("A", MakeStyle()));
        root.AppendChild(block);

        using var layouter = new BlockLayouter(
            root, sink,
            incomingContinuation: null,
            diagnostics: null,
            shaperResolver: resolver);

        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var breakResolver = new BreakResolver();

        // Act
        layouter.AttemptLayout(
            ctx, ref layoutCtx, breakResolver, LayoutAttemptStrategy.Strict);

        // Assert — cursor advanced by lineCount × 19.2. One line for
        // a single "A".
        Assert.Single(sink.Fragments);
        var fragment = sink.Fragments[0];
        Assert.NotNull(fragment.InlineLines);
        var expectedAdvance = fragment.InlineLines!.Length * 16 * 1.2;
        Assert.Equal(expectedAdvance, fragment.BlockSize);
        Assert.Equal(expectedAdvance, ctx.UsedBlockSize);
        // At least one line × 19.2 ≥ 19.2 (sanity guard for the
        // floating-point compare).
        Assert.True(ctx.UsedBlockSize >= 19.2);
    }

    [Fact]
    public void Block_with_no_inline_content_emits_no_fragment_and_no_lines()
    {
        // Arrange — a <BlockContainer> with one empty-string TextRun
        // child. The dispatch routes into LayoutInlineContent (the
        // block IS inline-only by structure) but CollectInlineTextRuns
        // skips empty-text TextRun leaves so the resulting textRuns
        // list is empty → emit-nothing path fires.
        //
        // Note: Box.TextRun("", style) is legal — the box-tree
        // producer can synthesize empty runs (e.g., for an empty
        // <span> or pre-collapse whitespace placeholder).
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        block.AppendChild(Box.TextRun(string.Empty, MakeStyle()));
        root.AppendChild(block);

        using var layouter = new BlockLayouter(
            root, sink,
            incomingContinuation: null,
            diagnostics: null,
            shaperResolver: resolver);

        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var breakResolver = new BreakResolver();

        // Act
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, breakResolver, LayoutAttemptStrategy.Strict);

        // Assert — no fragment, no cursor advance.
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Empty(sink.Fragments);
        Assert.Equal(0, ctx.UsedBlockSize);
    }

    [Fact]
    public void Block_without_shaperResolver_emits_diagnostic_and_skips()
    {
        // Arrange — content present but the layouter constructed
        // WITHOUT a shaper resolver. Sub-cycle 1 emits the
        // LAYOUT-INLINE-SKIPPED-NO-SHAPER-RESOLVER-001 diagnostic +
        // skips the inline-only block as if its children formed a
        // line box that breaks margin adjacency (the cycle-1-2c
        // behavior preserved).
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();

        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        block.AppendChild(Box.TextRun("A", MakeStyle()));
        root.AppendChild(block);

        using var layouter = new BlockLayouter(
            root, sink,
            incomingContinuation: null,
            diagnostics: diagSink,
            shaperResolver: null);

        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var breakResolver = new BreakResolver();

        // Act
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, breakResolver, LayoutAttemptStrategy.Strict);

        // Assert — no fragment emitted; one diagnostic with the
        // sub-cycle-1 code.
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Empty(sink.Fragments);
        Assert.Single(diagSink.Diagnostics);
        Assert.Equal(
            PaginateDiagnosticCodes.LayoutInlineSkippedNoShaperResolver001,
            diagSink.Diagnostics[0].Code);
        Assert.Equal(
            PaginateDiagnosticSeverity.Warning,
            diagSink.Diagnostics[0].Severity);
        // Cursor didn't advance.
        Assert.Equal(0, ctx.UsedBlockSize);
    }

    // ====================================================================
    //  Test doubles + helpers
    // ====================================================================

    /// <summary>Recording sink for emitted fragments (mirrors the one
    /// in <see cref="BlockLayouterTests"/>).</summary>
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

    /// <summary>Recording sink for emitted diagnostics (mirrors the
    /// one in <see cref="BlockLayouterTests"/>).</summary>
    private sealed class RecordingDiagnosticsSink : IPaginateDiagnosticsSink
    {
        public List<PaginateDiagnostic> Diagnostics { get; } = new();
        public void Emit(PaginateDiagnostic diagnostic) => Diagnostics.Add(diagnostic);
    }

    /// <summary>Minimal <see cref="IShaperResolver"/> backed by a
    /// synthetic-font <see cref="HbShaper"/>. Returns the same shaper
    /// for every style (cycle 1 / sub-cycle 1 doesn't need style-
    /// dependent shaping — the synthetic font has 3 glyphs and the
    /// tests only exercise a single 'A').</summary>
    private sealed class SyntheticShaperResolver : IShaperResolver
    {
        private readonly HbShaper _shaper = new(SyntheticFont.Build(), fontSizePx: 12);
        public HbShaper Resolve(ComputedStyle style) => _shaper;
        public void Dispose() => _shaper.Dispose();
    }

    private static ComputedStyle MakeStyle() => ComputedStyle.RentForExclusiveTesting();

    private static AngleSharp.Dom.IElement MakeElement()
    {
        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var doc = parser.ParseDocument("<div></div>");
        return doc.CreateElement("div");
    }
}
