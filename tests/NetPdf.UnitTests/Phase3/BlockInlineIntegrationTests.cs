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
/// Phase 3 Task 11 cycle 1 sub-cycle 1 — BlockLayouter ↔ InlineLayouter
/// integration tests. Drives <see cref="BlockLayouter"/> through
/// inline-only block containers (the smallest dispatch case — a single
/// <see cref="BoxKind.BlockContainer"/> whose only child is a
/// <see cref="BoxKind.TextRun"/>) + verifies:
/// <list type="bullet">
///   <item>The dispatch emits ONE <see cref="BoxFragment"/> with
///   <see cref="BoxFragment.InlineLayout"/> populated.</item>
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
        Assert.NotNull(fragment.InlineLayout?.Lines);
        // The single "A" should fit on one line (well under 600 px).
        Assert.Single(fragment.InlineLayout!.Value.Lines);
        // InlineSize is the available width (no margin honoring in
        // sub-cycle 1).
        Assert.Equal(600, fragment.InlineSize);
        // BlockOffset = caller-provided cursor start (= 0 on a fresh
        // page).
        Assert.Equal(0, fragment.BlockOffset);
        Assert.Equal(0, fragment.InlineOffset);
    }

    [Fact]
    public void Inline_only_block_centres_against_its_min_width_clamped_width()
    {
        // PR #203 Copilot review — a text-bearing `width:200; min-width:400;
        // margin:0 auto` block must centre against its USED (clamped) width 400,
        // not the declared 200. Pre-fix the auto-margin distribution in
        // ReadInlineOnlyBlockMetrics used the UNCLAMPED width, so margin-left
        // centred a 200px box (→ InlineOffset 200) while the emitted box was the
        // clamped 400px wide → off-centre. The clamp now precedes the distribution.
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var blockStyle = MakeStyle();
        blockStyle.Set(PropertyId.Width, ComputedSlot.FromLengthPx(200));
        blockStyle.Set(PropertyId.MinWidth, ComputedSlot.FromLengthPx(400));
        blockStyle.Set(PropertyId.MarginLeft, ComputedSlot.FromKeyword(0));   // auto
        blockStyle.Set(PropertyId.MarginRight, ComputedSlot.FromKeyword(0));  // auto
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        block.AppendChild(Box.TextRun("A", MakeStyle()));
        root.AppendChild(block);

        using var layouter = new BlockLayouter(
            root, sink, incomingContinuation: null, diagnostics: null, shaperResolver: resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var breakResolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, breakResolver, LayoutAttemptStrategy.Strict);

        var fragment = sink.Fragments[0];
        Assert.Equal(400, fragment.InlineSize, precision: 3);    // raised to min-width
        Assert.Equal(100, fragment.InlineOffset, precision: 3);  // (600-400)/2 centred
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
        Assert.NotNull(fragment.InlineLayout?.Lines);
        var expectedAdvance = fragment.InlineLayout!.Value.Lines.Length * 16 * 1.2;
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
    //  Phase 3 Task 11 cycle 1 sub-cycle 1 hardening review tests
    //  (Findings 1-6, 8 — Finding 7 lives in DiagnosticCodesTests.cs).
    // ====================================================================

    [Fact]
    public void Block_with_inline_content_emits_BoxFragment_with_resolvable_glyph_data()
    {
        // Per Finding #1 — the painter's contract. Each emitted line's
        // ShapedRunSlice indexes into result.InlineLayout.ShapedRuns;
        // each slice's glyph range must be non-empty + every glyph
        // must have a finite XAdvance. Pre-Finding-1 the bare
        // LineFragment[] return value made these indices unresolvable
        // because the ShapedRun[] went out of scope.
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        block.AppendChild(Box.TextRun("AB", MakeStyle()));
        root.AppendChild(block);

        using var layouter = new BlockLayouter(
            root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        var fragment = Assert.Single(sink.Fragments);
        Assert.NotNull(fragment.InlineLayout);
        var inlineLayout = fragment.InlineLayout!.Value;
        Assert.NotEmpty(inlineLayout.Lines);
        Assert.NotEmpty(inlineLayout.ShapedRuns);
        Assert.NotEmpty(inlineLayout.PreprocessedRuns);

        // Walk every slice + resolve its glyphs via the bundled
        // ShapedRuns array. The contract is that the slice indices
        // are valid + the glyphs have finite XAdvance.
        var totalGlyphsSeen = 0;
        foreach (var line in inlineLayout.Lines)
        {
            foreach (var slice in line.Slices)
            {
                Assert.True(slice.ShapedRunIndex >= 0
                    && slice.ShapedRunIndex < inlineLayout.ShapedRuns.Count,
                    $"ShapedRunIndex={slice.ShapedRunIndex} out of range "
                    + $"[0, {inlineLayout.ShapedRuns.Count})");
                var shapedRun = inlineLayout.ShapedRuns[slice.ShapedRunIndex];
                var glyphEnd = slice.GlyphStart + slice.GlyphLength;
                Assert.True(slice.GlyphStart >= 0 && glyphEnd <= shapedRun.Glyphs.Length,
                    $"Slice glyph range [{slice.GlyphStart},{glyphEnd}) outside "
                    + $"shaped run's glyph array length {shapedRun.Glyphs.Length}");
                Assert.True(slice.GlyphLength > 0,
                    "Slice glyph length must be positive (zero-length slices "
                    + "are illegal per ShapedRunSlice.GlyphLength docs).");
                for (var gi = slice.GlyphStart; gi < glyphEnd; gi++)
                {
                    var glyph = shapedRun.Glyphs[gi];
                    Assert.True(double.IsFinite(glyph.XAdvance),
                        $"Glyph at index {gi} has non-finite XAdvance={glyph.XAdvance}");
                    totalGlyphsSeen++;
                }
                // Resolve the source TextRun for this slice via the
                // shaped run's Source.SourceTextRunIndex.
                Assert.True(shapedRun.Source.SourceTextRunIndex >= 0
                    && shapedRun.Source.SourceTextRunIndex < inlineLayout.PreprocessedRuns.Count);
            }
        }
        Assert.True(totalGlyphsSeen > 0,
            "Expected at least one drawable glyph for 'AB' input.");
    }

    [Fact]
    public void Block_with_br_emits_two_lines()
    {
        // Per Finding #4 — <br> injects a synthetic LF TextRun that
        // LineBuilder treats as a UAX #14 Mandatory break. The
        // sequence A<br>B produces two lines.
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        block.AppendChild(Box.TextRun("A", MakeStyle()));
        block.AppendChild(Box.ForElement(BoxKind.LineBreak, MakeStyle(), MakeElement()));
        block.AppendChild(Box.TextRun("B", MakeStyle()));
        root.AppendChild(block);

        using var layouter = new BlockLayouter(
            root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        var fragment = Assert.Single(sink.Fragments);
        Assert.NotNull(fragment.InlineLayout);
        // A + LF + B → two lines (the LF terminates line 1, B starts
        // line 2). LineBuilder's mandatory-break trim drops the LF
        // glyph itself so each line has its own visible content.
        Assert.Equal(2, fragment.InlineLayout!.Value.Lines.Length);
    }

    [Fact]
    public void Block_with_marker_includes_marker_text_in_inline_content()
    {
        // Per Finding #4 — list-item markers (Lists L3 §3.1) carry
        // bullet/number content as child TextRuns. CollectInlineTextRuns
        // recurses into the marker so its text flows into the line.
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        // The dispatch needs an inline-only block. A ListItem with a
        // Marker child + a TextRun is structurally inline-only (both
        // children are inline-level per BoxKind classification).
        var listItem = Box.ForElement(BoxKind.ListItem, MakeStyle(), MakeElement());
        var marker = Box.ForPseudo(BoxKind.Marker, MakeStyle(), MakeElement(), BoxPseudo.Marker);
        marker.AppendChild(Box.TextRun("A", MakeStyle()));  // synthetic font has 'A'
        listItem.AppendChild(marker);
        listItem.AppendChild(Box.TextRun("B", MakeStyle()));
        root.AppendChild(listItem);

        using var layouter = new BlockLayouter(
            root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        var fragment = Assert.Single(sink.Fragments);
        Assert.NotNull(fragment.InlineLayout);
        Assert.NotEmpty(fragment.InlineLayout!.Value.Lines);
        // Marker text + body text both contribute glyphs — total
        // should reflect both 'A' (marker) + 'B' (body).
        var totalGlyphs = 0;
        foreach (var line in fragment.InlineLayout.Value.Lines)
        {
            foreach (var slice in line.Slices)
            {
                totalGlyphs += slice.GlyphLength;
            }
        }
        Assert.True(totalGlyphs >= 2,
            "Expected at least 2 glyphs (marker 'A' + body 'B'); got "
            + totalGlyphs);
    }

    [Fact]
    public void Block_with_atomic_inline_emits_diagnostic_and_skips_atomic()
    {
        // Per Finding #4 — atomic inlines (inline-block / inline-flex
        // / etc. / inline-replaced) emit a diagnostic so consumers
        // can detect that a placeholder is missing from the line.
        // Sub-cycle 1 still proceeds with the rest of the inline
        // content.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var resolver = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        block.AppendChild(Box.TextRun("A", MakeStyle()));
        block.AppendChild(Box.ForElement(BoxKind.InlineReplacedElement, MakeStyle(), MakeElement()));
        root.AppendChild(block);

        using var layouter = new BlockLayouter(
            root, sink, null, diagSink, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        // Fragment still emitted (for the 'A' textrun) + diagnostic
        // for the skipped inline-replaced.
        Assert.Single(sink.Fragments);
        var atomicDiag = diagSink.Diagnostics
            .Find(d => d.Code == PaginateDiagnosticCodes.LayoutInlineAtomicNotSupported001);
        Assert.NotEqual(default, atomicDiag);
        Assert.Equal(PaginateDiagnosticSeverity.Warning, atomicDiag.Severity);
    }

    [Fact]
    public void Inline_block_atomic_explicit_size_emits_border_box_and_flushes_content()
    {
        // Inline-atomic-boxes cycle (inline-block first cut) — a `display: inline-block`
        // span participates in the line as an atomic: it gets a positioned BORDER-box
        // decoration fragment (content-box width 50 + 2×5 padding = 60; height 30 + 10 =
        // 40), its content is flushed (a second box==inline-block fragment carrying the
        // text), and NO atomic-not-supported diagnostic is emitted.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var resolver = new SyntheticShaperResolver();

        var ibStyle = MakeStyle();
        ibStyle.Set(PropertyId.Width, ComputedSlot.FromLengthPx(50));
        ibStyle.Set(PropertyId.Height, ComputedSlot.FromLengthPx(30));
        foreach (var p in new[] { PropertyId.PaddingTop, PropertyId.PaddingRight, PropertyId.PaddingBottom, PropertyId.PaddingLeft })
            ibStyle.Set(p, ComputedSlot.FromLengthPx(5));
        var inlineBlock = Box.ForElement(BoxKind.InlineBlockContainer, ibStyle, MakeElement());
        inlineBlock.AppendChild(Box.TextRun("A", MakeStyle()));

        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        block.AppendChild(Box.TextRun("A", MakeStyle()));
        block.AppendChild(inlineBlock);
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink, null, diagSink, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        // The decoration fragment (box == inline-block, no InlineLayout — the placed atomic).
        BoxFragment? decoration = null, content = null;
        foreach (var f in sink.Fragments)
        {
            if (!ReferenceEquals(f.Box, inlineBlock)) continue;
            if (f.InlineLayout is null) decoration = f; else content = f;
        }
        Assert.NotNull(decoration);
        Assert.Equal(60, decoration!.Value.InlineSize, precision: 3);
        Assert.Equal(40, decoration.Value.BlockSize, precision: 3);
        // Content laid out + flushed (the inline-block's own text fragment).
        Assert.NotNull(content);
        // No "atomic not supported" diagnostic — the inline-block was laid out, not skipped.
        Assert.DoesNotContain(diagSink.Diagnostics,
            d => d.Code == PaginateDiagnosticCodes.LayoutInlineAtomicNotSupported001);
    }

    [Fact]
    public void Inline_block_atomic_honors_box_sizing_border_box()
    {
        // Inline-atomic-boxes cycle — under `box-sizing: border-box` the declared width/
        // height ARE the border box (50 × 30), the padding coming out of the content area.
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var ibStyle = MakeStyle();
        ibStyle.Set(PropertyId.Width, ComputedSlot.FromLengthPx(50));
        ibStyle.Set(PropertyId.Height, ComputedSlot.FromLengthPx(30));
        ibStyle.Set(PropertyId.BoxSizing, ComputedSlot.FromKeyword(1)); // border-box
        foreach (var p in new[] { PropertyId.PaddingTop, PropertyId.PaddingRight, PropertyId.PaddingBottom, PropertyId.PaddingLeft })
            ibStyle.Set(p, ComputedSlot.FromLengthPx(5));
        var inlineBlock = Box.ForElement(BoxKind.InlineBlockContainer, ibStyle, MakeElement());
        inlineBlock.AppendChild(Box.TextRun("A", MakeStyle()));

        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        block.AppendChild(inlineBlock);
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        BoxFragment? decoration = null;
        foreach (var f in sink.Fragments)
            if (ReferenceEquals(f.Box, inlineBlock) && f.InlineLayout is null) decoration = f;
        Assert.NotNull(decoration);
        Assert.Equal(50, decoration!.Value.InlineSize, precision: 3);
        Assert.Equal(30, decoration.Value.BlockSize, precision: 3);
    }

    [Fact]
    public void Inline_block_atomic_auto_width_shrinks_to_fit_content()
    {
        // Inline-atomic-boxes cycle — an `auto`-width inline-block SHRINK-TO-FITs to its
        // content (+ chrome), NOT the full available line width. With a single 'x' glyph +
        // 4px padding each side, the border box is small (≪ the 600px line) but ≥ the 8px chrome.
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var ibStyle = MakeStyle();
        foreach (var p in new[] { PropertyId.PaddingLeft, PropertyId.PaddingRight })
            ibStyle.Set(p, ComputedSlot.FromLengthPx(4));
        var inlineBlock = Box.ForElement(BoxKind.InlineBlockContainer, ibStyle, MakeElement());
        inlineBlock.AppendChild(Box.TextRun("A", MakeStyle()));

        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        block.AppendChild(inlineBlock);
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        BoxFragment? decoration = null;
        foreach (var f in sink.Fragments)
            if (ReferenceEquals(f.Box, inlineBlock) && f.InlineLayout is null) decoration = f;
        Assert.NotNull(decoration);
        Assert.True(decoration!.Value.InlineSize > 8,
            $"shrink-to-fit width should exceed the 8px chrome; got {decoration.Value.InlineSize}");
        Assert.True(decoration.Value.InlineSize < 100,
            $"shrink-to-fit width should be ≪ the 600px line, not stretched; got {decoration.Value.InlineSize}");
    }

    [Fact]
    public void Inline_block_definite_width_measures_content_at_its_own_width()
    {
        // Post-PR-#190 Copilot review — a DEFINITE-width inline-block measures its content at its
        // OWN content width (not the whole remaining line), so multi-word content WRAPS + the auto
        // height reflects it. A narrow 12px inline-block with several words + auto height is several
        // ~19.2px lines tall (≫ a single line); pre-fix it measured at the 600px line → one line.
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var ibStyle = MakeStyle();
        ibStyle.Set(PropertyId.Width, ComputedSlot.FromLengthPx(12)); // narrow definite width, auto height
        var inlineBlock = Box.ForElement(BoxKind.InlineBlockContainer, ibStyle, MakeElement());
        inlineBlock.AppendChild(Box.TextRun("A A A A A A", MakeStyle()));

        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        block.AppendChild(inlineBlock);
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        BoxFragment? decoration = null;
        foreach (var f in sink.Fragments)
            if (ReferenceEquals(f.Box, inlineBlock) && f.InlineLayout is null) decoration = f;
        Assert.NotNull(decoration);
        // Wrapped to multiple ~19.2px lines (≫ one line). Pre-fix (measured at the 600px line) the
        // words fit on one line → ~19.2px.
        Assert.True(decoration!.Value.BlockSize > 30,
            $"definite narrow width should wrap content + grow tall; got {decoration.Value.BlockSize}");
    }

    [Fact]
    public void Inline_block_with_text_aligns_by_last_line_baseline_without_overflowing_line_top()
    {
        // Inline-block last-line-baseline cycle (CSS 2.2 §10.8.1) — an inline-block WITH an in-flow line
        // box aligns by its LAST line's baseline (it sits ON the surrounding text baseline), and the line
        // box is sized by max-ascent / max-descent so the box fits. Previously the box was placed margin-
        // box-bottom-on-baseline (an img-ish approximation), which pushed a full-height box ABOVE the line
        // top (a NEGATIVE block offset — overflow). The box now fits within its (grown) line box.
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var ibStyle = MakeStyle();   // auto size → a single line of "A"
        var inlineBlock = Box.ForElement(BoxKind.InlineBlockContainer, ibStyle, MakeElement());
        inlineBlock.AppendChild(Box.TextRun("A", MakeStyle()));
        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        block.AppendChild(inlineBlock);
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        BoxFragment? decoration = null;
        BoxFragment? outer = null;
        foreach (var f in sink.Fragments)
        {
            if (ReferenceEquals(f.Box, inlineBlock) && f.InlineLayout is null) decoration = f;
            else if (ReferenceEquals(f.Box, block) && f.InlineLayout is not null) outer = f;
        }
        Assert.NotNull(decoration);
        Assert.NotNull(outer);
        // Baseline-aligned → no overflow above the line top (≥ 0; the old bottom-on-baseline was negative).
        Assert.True(decoration!.Value.BlockOffset >= -0.01,
            $"baseline-aligned inline-block should not overflow the line top; got {decoration.Value.BlockOffset}");
        // And it fits inside the line box the §10.8.1 max-ascent model grew for it.
        Assert.True(decoration.Value.BlockOffset + decoration.Value.BlockSize <= outer!.Value.BlockSize + 0.01,
            $"inline-block should fit its line: box bottom {decoration.Value.BlockOffset + decoration.Value.BlockSize}" +
            $" vs line {outer.Value.BlockSize}");
    }

    [Fact]
    public void Inline_atomic_vertical_align_top_bottom_order_a_short_box_within_the_line()
    {
        // vertical-align cycle (CSS 2.2 §10.8.1) — a SHORT inline atomic (10px) sharing a ~19.2px line
        // with text is placed by its vertical-align: `top` puts its margin-box top at the LINE-BOX top
        // (offset ≈ 0); `bottom` puts its bottom at the line bottom (the lowest top offset); `baseline`
        // (the default — here an img-ish atomic, no own line box) sits margin-bottom-on-baseline, in
        // between. So the block offsets strictly increase top < baseline < bottom.
        static double AtomicTop(int valignKeyword)
        {
            var sink = new RecordingFragmentSink();
            using var resolver = new SyntheticShaperResolver();
            var ibStyle = MakeStyle();
            ibStyle.Set(PropertyId.Width, ComputedSlot.FromLengthPx(20));
            ibStyle.Set(PropertyId.Height, ComputedSlot.FromLengthPx(10));   // short → fits with room to move
            ibStyle.Set(PropertyId.VerticalAlign, ComputedSlot.FromKeyword(valignKeyword));
            var inlineBlock = Box.ForElement(BoxKind.InlineBlockContainer, ibStyle, MakeElement());  // no line box
            var root = Box.CreateRoot(MakeStyle());
            var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
            block.AppendChild(Box.TextRun("A", MakeStyle()));   // establishes the line baseline
            block.AppendChild(inlineBlock);
            root.AppendChild(block);
            using var layouter = new BlockLayouter(root, sink, null, null, resolver);
            var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
            var layoutCtx = new LayoutContext(ctx);
            using var br = new BreakResolver();
            layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);
            foreach (var f in sink.Fragments)
                if (ReferenceEquals(f.Box, inlineBlock) && f.InlineLayout is null) return f.BlockOffset;
            throw new Xunit.Sdk.XunitException("no inline-block decoration fragment");
        }

        var top = AtomicTop(6);        // top
        var baseline = AtomicTop(0);   // baseline (the default)
        var bottom = AtomicTop(7);     // bottom

        Assert.True(top < baseline - 0.5, $"top-aligned should sit above baseline: top={top} baseline={baseline}");
        Assert.True(baseline < bottom - 0.5, $"baseline should sit above bottom-aligned: baseline={baseline} bottom={bottom}");
        Assert.True(top <= 0.01, $"top-aligned box should sit at the line top; got {top}");
    }

    // Lays out [text "A", inline-block(20×10, given vertical-align)] and returns the atomic's block offset.
    private static double ValignAtomicTop(int valignKeyword)
    {
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();
        var ibStyle = MakeStyle();
        ibStyle.Set(PropertyId.Width, ComputedSlot.FromLengthPx(20));
        ibStyle.Set(PropertyId.Height, ComputedSlot.FromLengthPx(10));
        ibStyle.Set(PropertyId.VerticalAlign, ComputedSlot.FromKeyword(valignKeyword));
        var inlineBlock = Box.ForElement(BoxKind.InlineBlockContainer, ibStyle, MakeElement());   // no line box
        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        block.AppendChild(Box.TextRun("A", MakeStyle()));
        block.AppendChild(inlineBlock);
        root.AppendChild(block);
        using var layouter = new BlockLayouter(root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);
        foreach (var f in sink.Fragments)
            if (ReferenceEquals(f.Box, inlineBlock) && f.InlineLayout is null) return f.BlockOffset;
        throw new Xunit.Sdk.XunitException("no inline-block decoration fragment");
    }

    [Fact]
    public void Inline_atomic_vertical_align_super_raises_and_sub_lowers_off_the_baseline()
    {
        // vertical-align sub/super cycle (CSS 2.2 §10.8.1) — `super` raises the atomic off the line
        // baseline, `sub` lowers it (both shift the baseline placement by a fraction of the parent
        // font-size). So super sits ABOVE the baseline placement (smaller top offset), sub BELOW it.
        var baseline = ValignAtomicTop(0);   // baseline
        var super = ValignAtomicTop(2);      // super
        var sub = ValignAtomicTop(1);        // sub

        Assert.True(super < baseline - 0.5, $"super should raise the atomic above baseline: super={super} baseline={baseline}");
        Assert.True(sub > baseline + 0.5, $"sub should lower the atomic below baseline: sub={sub} baseline={baseline}");
    }

    [Fact]
    public void Justify_shifts_an_inline_atomic_by_the_gaps_before_it()
    {
        // text-align: justify on an atomic-bearing line (post-PR-#194 task 1) — an inline atomic shifts
        // RIGHT by the inter-word gaps accumulated BEFORE it, so it stays glued to its justified text
        // (pre-fix the line stayed start-aligned). justify-all justifies the single (last) line; with
        // free space on the line, the atomic's inline offset must exceed the un-justified (start) one.
        double AtomicInlineOffset(int textAlign)
        {
            var sink = new RecordingFragmentSink();
            using var resolver = new SyntheticShaperResolver();
            var blockStyle = MakeStyle();
            blockStyle.Set(PropertyId.TextAlign, ComputedSlot.FromKeyword(textAlign));
            var ibStyle = MakeStyle();
            ibStyle.Set(PropertyId.Width, ComputedSlot.FromLengthPx(20));
            ibStyle.Set(PropertyId.Height, ComputedSlot.FromLengthPx(10));
            var inlineBlock = Box.ForElement(BoxKind.InlineBlockContainer, ibStyle, MakeElement());
            var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
            block.AppendChild(Box.TextRun("A A", MakeStyle()));   // one interior space → one gap before the atomic
            block.AppendChild(inlineBlock);
            var root = Box.CreateRoot(MakeStyle());
            root.AppendChild(block);
            using var layouter = new BlockLayouter(root, sink, null, null, resolver);
            var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
            var layoutCtx = new LayoutContext(ctx);
            using var br = new BreakResolver();
            layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);
            foreach (var f in sink.Fragments)
                if (ReferenceEquals(f.Box, inlineBlock) && f.InlineLayout is null) return f.InlineOffset;
            throw new Xunit.Sdk.XunitException("no inline-block decoration fragment");
        }

        var start = AtomicInlineOffset(0);       // text-align: start — no justify
        var justifyAll = AtomicInlineOffset(7);  // text-align: justify-all — the single line justifies

        Assert.True(justifyAll > start + 1,
            $"justify should shift the atomic right by the gap(s) before it: justifyAll={justifyAll} start={start}");
    }

    // Lays out [text "A", inline-block(20×10, given vertical-align + optional line-height)] and returns
    // the atomic's (top, bottom) plus the outer line-box height — for containment + own-line-height checks.
    private static (double Top, double Bottom, double LineHeight) ValignAtomicGeometry(
        ComputedSlot valignSlot, double ownLineHeightPx = 0)
    {
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();
        var ibStyle = MakeStyle();
        ibStyle.Set(PropertyId.Width, ComputedSlot.FromLengthPx(20));
        ibStyle.Set(PropertyId.Height, ComputedSlot.FromLengthPx(10));
        ibStyle.Set(PropertyId.VerticalAlign, valignSlot);
        if (ownLineHeightPx > 0) ibStyle.Set(PropertyId.LineHeight, ComputedSlot.FromLengthPx(ownLineHeightPx));
        var inlineBlock = Box.ForElement(BoxKind.InlineBlockContainer, ibStyle, MakeElement());
        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        block.AppendChild(Box.TextRun("A", MakeStyle()));
        block.AppendChild(inlineBlock);
        root.AppendChild(block);
        using var layouter = new BlockLayouter(root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);
        double top = double.NaN, lineHeight = 0;
        foreach (var f in sink.Fragments)
        {
            if (ReferenceEquals(f.Box, inlineBlock) && f.InlineLayout is null) top = f.BlockOffset;
            else if (ReferenceEquals(f.Box, block) && f.InlineLayout is not null) lineHeight = f.BlockSize;
        }
        if (double.IsNaN(top)) throw new Xunit.Sdk.XunitException("no inline-block decoration fragment");
        return (top, top + 10, lineHeight);   // height is the explicit 10px
    }

    [Theory]
    [InlineData(2, 0.0)]     // super (keyword)
    [InlineData(1, 0.0)]     // sub (keyword)
    [InlineData(0, 12.0)]    // +12px length raise
    [InlineData(0, -12.0)]   // −12px length lower
    public void Inline_atomic_vertical_align_shift_is_contained_in_the_line(int keyword, double lengthPx)
    {
        // Post-PR-#193 review P1 — a baseline-relative SHIFTED atomic (super / sub / a numeric length or
        // percentage) is now SIZED into the line, not just moved: even an IMG-ish atomic (no own line box)
        // is CONTAINED — no negative top overflow and no spill past the line bottom. Pre-fix only a
        // baseline-OWNING inline-block triggered the max-ascent model, so a shifted img-ish box hung above
        // the line top.
        var slot = lengthPx != 0 ? ComputedSlot.FromLengthPx(lengthPx) : ComputedSlot.FromKeyword(keyword);
        var (top, bottom, lineHeight) = ValignAtomicGeometry(slot);

        Assert.True(top >= -0.01, $"shifted atomic should not overflow the line top; got top={top}");
        Assert.True(bottom <= lineHeight + 0.01,
            $"shifted atomic should fit the line; bottom={bottom} lineHeight={lineHeight}");
    }

    [Fact]
    public void Inline_atomic_vertical_align_large_percentage_is_contained()
    {
        // Post-PR-#193 review P1 — a large `vertical-align: 200%` raise on an img-ish atomic is contained
        // (the line grows to hold the raised box), and a positive percentage RAISES it above a negative one.
        var (rTop, rBottom, rLine) = ValignAtomicGeometry(ComputedSlot.FromPercentage(200));
        var (lTop, lBottom, lLine) = ValignAtomicGeometry(ComputedSlot.FromPercentage(-200));

        Assert.True(rTop >= -0.01 && rBottom <= rLine + 0.01, $"+200% should be contained; top={rTop} bottom={rBottom} line={rLine}");
        Assert.True(lTop >= -0.01 && lBottom <= lLine + 0.01, $"−200% should be contained; top={lTop} bottom={lBottom} line={lLine}");
        Assert.True(rTop < lTop - 1, $"a positive % should raise the box above a negative %: +={rTop} −={lTop}");
    }

    [Fact]
    public void Inline_atomic_vertical_align_percentage_uses_the_boxs_own_line_height()
    {
        // Post-PR-#193 review P2 — a `vertical-align: %` resolves against the ELEMENT's OWN line-height
        // (CSS 2.2 §10.8.1), not the parent's / the grown line box. `50%` of an own line-height of 40px is
        // a 20px raise; of 20px, a 10px raise. The bigger raise grows the line box by the extra 10px.
        var lh40 = ValignAtomicGeometry(ComputedSlot.FromPercentage(50), ownLineHeightPx: 40);
        var lh20 = ValignAtomicGeometry(ComputedSlot.FromPercentage(50), ownLineHeightPx: 20);

        Assert.Equal(10.0, lh40.LineHeight - lh20.LineHeight, precision: 1);   // (50% of 40) − (50% of 20)
    }

    // The line baseline (where the surrounding text sits) for the inline-block on the outer line.
    private static double OuterLineBaseline(Box outerInlineOnlyBlock, RecordingFragmentSink sink)
    {
        foreach (var f in sink.Fragments)
            if (ReferenceEquals(f.Box, outerInlineOnlyBlock) && f.InlineLayout is not null
                && f.PerLineBaselineTopPx is { Count: > 0 } b)
                return b[0];
        throw new Xunit.Sdk.XunitException("no outer line fragment with a per-line baseline");
    }

    [Fact]
    public void Inline_block_baseline_anchors_to_last_line_box_not_a_trailing_block()
    {
        // Post-PR-#192 review P1 — an inline-block with text FOLLOWED by a tall non-line block takes its
        // baseline from the LAST line box (the text, near the top), NOT the content bottom. So the line
        // baseline (PerLineBaselineTopPx — where the surrounding text sits) stays near the top, not
        // pulled down to the ~119px content bottom (which the old ContentBlockExtent-based math did).
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var textBlock = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        textBlock.AppendChild(Box.TextRun("A", MakeStyle()));   // the last in-flow LINE box (~19px)
        var tallStyle = MakeStyle();
        tallStyle.Set(PropertyId.Height, ComputedSlot.FromLengthPx(100));
        var tall = Box.ForElement(BoxKind.BlockContainer, tallStyle, MakeElement());   // tall, NO line box

        var ibStyle = MakeStyle();
        ibStyle.Set(PropertyId.Width, ComputedSlot.FromLengthPx(20));   // narrow → stays on the line with "A"
        var inlineBlock = Box.ForElement(BoxKind.InlineBlockContainer, ibStyle, MakeElement());
        inlineBlock.AppendChild(textBlock);
        inlineBlock.AppendChild(tall);

        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        block.AppendChild(Box.TextRun("A", MakeStyle()));   // surrounding text → the outer line baseline
        block.AppendChild(inlineBlock);
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        var lineBaseline = OuterLineBaseline(block, sink);
        Assert.True(lineBaseline < 40,
            $"the baseline should anchor to the last line box near the top, not the ~119px content bottom; got {lineBaseline}");
    }

    [Fact]
    public void Inline_block_baseline_follows_its_content_line_not_the_outer_box_line_height()
    {
        // inline-block last-line baseline real metrics (post-PR-#194 task 2) — an inline-block's §10.8.1
        // baseline is the descent below its CONTENT's last line box, read from the ACTUAL last line-
        // bearing fragment's metrics. The inline-block's OWN `line-height` does NOT apply to its (block)
        // content, so setting it must NOT move the baseline. Pre-fix the descent was read from the
        // inline-block's own style, so a line-height:40px on the box wrongly shifted the baseline though
        // the content line (a default ~19px) was unchanged.
        double OuterBaseline(bool lineHeightOnBox)
        {
            var sink = new RecordingFragmentSink();
            using var resolver = new SyntheticShaperResolver();
            var ibStyle = MakeStyle();
            if (lineHeightOnBox) ibStyle.Set(PropertyId.LineHeight, ComputedSlot.FromLengthPx(40));
            var inlineBlock = Box.ForElement(BoxKind.InlineBlockContainer, ibStyle, MakeElement());
            var contentBlock = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
            contentBlock.AppendChild(Box.TextRun("x", MakeStyle()));   // the inline-block's BLOCK content
            inlineBlock.AppendChild(contentBlock);

            var root = Box.CreateRoot(MakeStyle());
            var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
            block.AppendChild(Box.TextRun("A", MakeStyle()));   // surrounding text → the outer line baseline
            block.AppendChild(inlineBlock);
            root.AppendChild(block);
            using var layouter = new BlockLayouter(root, sink, null, null, resolver);
            var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
            var layoutCtx = new LayoutContext(ctx);
            using var br = new BreakResolver();
            layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);
            return OuterLineBaseline(block, sink);
        }

        // The box's own line-height is inert (its content is a block); the baseline tracks the content's
        // line in both cases. Pre-fix these diverged (descent read from the box's 40px line-height).
        Assert.Equal(OuterBaseline(lineHeightOnBox: false), OuterBaseline(lineHeightOnBox: true), precision: 2);
    }

    [Fact]
    public void Inline_block_baseline_uses_the_pinned_baseline_of_its_content_line()
    {
        // inline-block last-line baseline, pinned-baseline refinement (post-PR-#195 review P2) — when an
        // inline-block's deepest CONTENT line is PINNED by a tall baseline-aligned INNER inline-block, the
        // outer's §10.8.1 baseline must use that pinned baseline's descent (= lineHeight − pinnedBaseline,
        // EXACT), not the generic centred-font fallback. The tall (40px) inner inline-block pins the content
        // line near its own HIGH baseline, so the descent below it is SMALL → the outer line baseline sits
        // LOW (a large offset, ≈ the inner ascent). The centred fallback over-estimates the descent → a
        // markedly HIGHER baseline; assert the LOW (pinned) one.
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var innerStyle = MakeStyle();
        innerStyle.Set(PropertyId.FontSize, ComputedSlot.FromLengthPx(40));   // tall → grows + PINS the content line
        var inner = Box.ForElement(BoxKind.InlineBlockContainer, innerStyle, MakeElement());
        inner.AppendChild(Box.TextRun("z", innerStyle));   // a line box → baseline-aligned → pins the content line

        var contentBlock = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        contentBlock.AppendChild(Box.TextRun("x", MakeStyle()));   // 16px body text on the same content line
        contentBlock.AppendChild(inner);

        var outer = Box.ForElement(BoxKind.InlineBlockContainer, MakeStyle(), MakeElement());
        outer.AppendChild(contentBlock);   // the OUTER inline-block's content is a BLOCK (so it has no own line box)

        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        block.AppendChild(Box.TextRun("A", MakeStyle()));   // surrounding text → the outer line baseline
        block.AppendChild(outer);
        root.AppendChild(block);
        using var layouter = new BlockLayouter(root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        // Pinned descent ≈ the inner ascent (~32px) dominates → outer baseline LOW (≈ 28+). The centred
        // fallback (descent ≈ lineHeight/2 − 0.3·16 ≈ 15px) would give a baseline ≈ 25 — below this floor.
        var lineBaseline = OuterLineBaseline(block, sink);
        Assert.True(lineBaseline > 28,
            $"the outer baseline should use the inner inline-block's PINNED baseline (low), not the centred fallback; got {lineBaseline}");
    }

    [Fact]
    public void Text_vertical_align_super_grows_the_line_box_to_contain_the_shift()
    {
        // text vertical-align line-growth cycle — an inline-level TEXT run with `vertical-align: super`
        // grows its line box so the raised glyph is CONTAINED (doesn't spill into the line above). A
        // block whose run is super is TALLER than the same block with a baseline run, by the raise.
        double BlockHeight(int valign)
        {
            var sink = new RecordingFragmentSink();
            using var resolver = new SyntheticShaperResolver();
            var textStyle = MakeStyle();   // a DISTINCT style (an inline element's) → the gate lets it shift
            textStyle.Set(PropertyId.VerticalAlign, ComputedSlot.FromKeyword(valign));
            var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
            block.AppendChild(Box.TextRun("A", textStyle));
            var root = Box.CreateRoot(MakeStyle());
            root.AppendChild(block);
            using var layouter = new BlockLayouter(root, sink, null, null, resolver);
            var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
            var layoutCtx = new LayoutContext(ctx);
            using var br = new BreakResolver();
            layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);
            foreach (var f in sink.Fragments)
                if (ReferenceEquals(f.Box, block) && f.InlineLayout is not null) return f.BlockSize;
            throw new Xunit.Sdk.XunitException("no block line fragment");
        }

        var baseline = BlockHeight(0);   // vertical-align: baseline → no shift, no growth
        var super = BlockHeight(2);      // vertical-align: super → +0.3em raise grows the line

        Assert.True(super > baseline + 1, $"super should grow the line box: super={super} baseline={baseline}");
    }

    [Theory]
    [InlineData("super")]
    [InlineData("sub")]
    [InlineData("length")]
    [InlineData("percent")]
    public void Text_vertical_align_shift_grows_the_line_by_the_runs_own_metrics_not_the_block_strut(string kind)
    {
        // post-PR-#194 review P2 — line growth for a shifted TEXT run must use the RUN's OWN font-size /
        // line-height strut, not the block's. A font-size:32px shifted span grows the line by its own
        // ~32px+ extent; reusing the block's 16px strut under-grows it and the raised 32px glyph spills
        // above the line top. For super / sub / a <length>, the block-strut model produced a line
        // SHORTER than the 32px run (≈25–29px) — the `large >= 32` assertion fails without the fix.
        double BlockHeight(double runFontSizePx)
        {
            var sink = new RecordingFragmentSink();
            using var resolver = new SyntheticShaperResolver();
            var textStyle = MakeStyle();   // a DISTINCT style (an inline element's) → the gate lets it shift
            textStyle.Set(PropertyId.FontSize, ComputedSlot.FromLengthPx(runFontSizePx));
            textStyle.Set(PropertyId.VerticalAlign, kind switch
            {
                "super" => ComputedSlot.FromKeyword(2),
                "sub" => ComputedSlot.FromKeyword(1),
                "length" => ComputedSlot.FromLengthPx(8),
                _ => ComputedSlot.FromPercentage(50),    // "percent" — 50% of the run's OWN line-height
            });
            var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
            block.AppendChild(Box.TextRun("A", textStyle));
            var root = Box.CreateRoot(MakeStyle());
            root.AppendChild(block);
            using var layouter = new BlockLayouter(root, sink, null, null, resolver);
            var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
            var layoutCtx = new LayoutContext(ctx);
            using var br = new BreakResolver();
            layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);
            foreach (var f in sink.Fragments)
                if (ReferenceEquals(f.Box, block) && f.InlineLayout is not null) return f.BlockSize;
            throw new Xunit.Sdk.XunitException("no block line fragment");
        }

        var small = BlockHeight(16);   // run == block size → the run strut equals the block strut.
        var large = BlockHeight(32);   // run twice the block → its own strut must drive the growth.

        // The 32px run's own strut grows the line markedly more than the 16px run's (proving growth uses
        // the run's own metrics), AND the line CONTAINS the 32px run (no spill above the line top).
        Assert.True(large > small + 8,
            $"{kind}: a 32px shifted run should grow the line by its OWN metrics: large={large} small={small}");
        Assert.True(large >= 32, $"{kind}: the 32px run must be contained in its line box: large={large}");
    }

    [Fact]
    public void Inline_block_with_non_visible_overflow_uses_the_bottom_margin_edge_baseline()
    {
        // CSS 2.2 §10.8.1 exception — an inline-block whose computed `overflow` is NOT `visible` takes
        // its baseline from the BOTTOM MARGIN EDGE (the img-ish placement), not its last line box. So a
        // visible-overflow inline-block is baseline-aligned (its outer line gets a per-line baseline),
        // but a hidden-overflow one is img-ish (no per-line baseline on the outer line).
        static bool OuterIsBaselineAligned(int overflowKeyword)
        {
            var sink = new RecordingFragmentSink();
            using var resolver = new SyntheticShaperResolver();
            var ibStyle = MakeStyle();
            ibStyle.Set(PropertyId.OverflowX, ComputedSlot.FromKeyword(overflowKeyword));
            ibStyle.Set(PropertyId.OverflowY, ComputedSlot.FromKeyword(overflowKeyword));
            var inlineBlock = Box.ForElement(BoxKind.InlineBlockContainer, ibStyle, MakeElement());
            inlineBlock.AppendChild(Box.TextRun("A", MakeStyle()));   // has a line box
            var root = Box.CreateRoot(MakeStyle());
            var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
            block.AppendChild(Box.TextRun("A", MakeStyle()));
            block.AppendChild(inlineBlock);
            root.AppendChild(block);
            using var layouter = new BlockLayouter(root, sink, null, null, resolver);
            var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
            var layoutCtx = new LayoutContext(ctx);
            using var br = new BreakResolver();
            layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);
            foreach (var f in sink.Fragments)
                if (ReferenceEquals(f.Box, block) && f.InlineLayout is not null)
                    return f.PerLineBaselineTopPx is { Count: > 0 } b && !double.IsNaN(b[0]);
            throw new Xunit.Sdk.XunitException("no outer line fragment");
        }

        Assert.True(OuterIsBaselineAligned(0), "overflow:visible inline-block should align by its last line baseline");
        Assert.False(OuterIsBaselineAligned(1), "overflow:hidden inline-block should use the bottom margin edge");
    }

    [Fact]
    public void Inline_block_baseline_excludes_padding_below_the_last_line()
    {
        // Post-PR-#192 review P1 — an inline-block with bottom PADDING after its text takes its baseline
        // from the text line, not the padding bottom. The outer line baseline stays near the text.
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var ibStyle = MakeStyle();
        ibStyle.Set(PropertyId.PaddingBottom, ComputedSlot.FromLengthPx(50));   // padding after the last line
        var inlineBlock = Box.ForElement(BoxKind.InlineBlockContainer, ibStyle, MakeElement());
        inlineBlock.AppendChild(Box.TextRun("A", MakeStyle()));

        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        block.AppendChild(Box.TextRun("A", MakeStyle()));
        block.AppendChild(inlineBlock);
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        var lineBaseline = OuterLineBaseline(block, sink);
        Assert.True(lineBaseline < 40,
            $"the baseline should exclude the 50px bottom padding below the last line; got {lineBaseline}");
    }

    [Fact]
    public void Inline_block_with_tall_middle_atomic_sibling_does_not_overflow_the_line()
    {
        // Post-PR-#192 review P1 — a baseline-aligned inline-block on a line WITH a tall vertical-align:
        // middle atomic grows the line by the middle atomic's BASELINE-RELATIVE extents (not just its
        // raw margin-box height), so the middle atomic is CONTAINED: no negative top overflow and no
        // bottom overflow past the line box. Pre-fix the line was sized by the small inline-block's
        // ascent/descent + the raw height, leaving the middle atomic hanging above the line top.
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var smallIb = Box.ForElement(BoxKind.InlineBlockContainer, MakeStyle(), MakeElement());
        smallIb.AppendChild(Box.TextRun("A", MakeStyle()));   // baseline-aligned, ~19px

        var midStyle = MakeStyle();
        midStyle.Set(PropertyId.Width, ComputedSlot.FromLengthPx(20));
        midStyle.Set(PropertyId.Height, ComputedSlot.FromLengthPx(100));   // TALL
        midStyle.Set(PropertyId.VerticalAlign, ComputedSlot.FromKeyword(5));   // middle
        var tallMiddle = Box.ForElement(BoxKind.InlineBlockContainer, midStyle, MakeElement());   // no line box

        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        block.AppendChild(smallIb);
        block.AppendChild(tallMiddle);
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        BoxFragment? mid = null;
        BoxFragment? outer = null;
        foreach (var f in sink.Fragments)
        {
            if (ReferenceEquals(f.Box, tallMiddle) && f.InlineLayout is null) mid = f;
            else if (ReferenceEquals(f.Box, block) && f.InlineLayout is not null) outer = f;
        }
        Assert.NotNull(mid);
        Assert.NotNull(outer);
        Assert.True(mid!.Value.BlockOffset >= -0.01,
            $"the tall middle atomic should not overflow the line top; got {mid.Value.BlockOffset}");
        Assert.True(mid.Value.BlockOffset + mid.Value.BlockSize <= outer!.Value.BlockSize + 0.01,
            $"the tall middle atomic should fit the line: bottom {mid.Value.BlockOffset + mid.Value.BlockSize}" +
            $" vs line {outer.Value.BlockSize}");
    }

    [Theory]
    [InlineData(0, 0.0)]   // start  → no shift (the initial)
    [InlineData(2, 0.0)]   // left   → no shift (LTR)
    [InlineData(4, 0.5)]   // center → half the free space
    [InlineData(3, 1.0)]   // right  → all the free space
    [InlineData(1, 1.0)]   // end    → right (LTR)
    [InlineData(5, 0.0)]   // justify → factor 0 (NOT a whole-line shift; distributes via JustifyLines)
    public void Body_text_align_sets_inline_line_align_factor(int textAlignKeyword, double expectedFactor)
    {
        // Body text-align cycle — text-align on an inline-only block sets the emitted fragment's
        // LineAlignFactor, which TextPainter consumes to shift each glyph line by
        // (content width − line advance) × factor. (Was always 0 — text-align unimplemented.)
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var blockStyle = MakeStyle();
        blockStyle.Set(PropertyId.TextAlign, ComputedSlot.FromKeyword(textAlignKeyword));
        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        block.AppendChild(Box.TextRun("A", MakeStyle()));
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        var fragment = Assert.Single(sink.Fragments);
        Assert.Equal(expectedFactor, fragment.LineAlignFactor, precision: 3);
    }

    [Theory]
    [InlineData(0, false)]   // start    → no justify
    [InlineData(2, false)]   // left     → no justify
    [InlineData(4, false)]   // center   → no justify
    [InlineData(5, true)]    // justify  → JustifyLines
    [InlineData(7, true)]    // justify-all → JustifyLines (last-line distinction approximated)
    public void Body_text_align_justify_sets_fragment_justify_lines(int textAlignKeyword, bool expectedJustify)
    {
        // text-align: justify cycle — `justify` / `justify-all` set the emitted fragment's JustifyLines
        // flag (the painter then distributes each non-last line's free space across inter-word gaps),
        // while keeping LineAlignFactor 0 (justify is NOT a whole-line shift). Non-justify values leave
        // JustifyLines false, so they stay byte-identical.
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var blockStyle = MakeStyle();
        blockStyle.Set(PropertyId.TextAlign, ComputedSlot.FromKeyword(textAlignKeyword));
        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        block.AppendChild(Box.TextRun("A A A", MakeStyle()));
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        var fragment = Assert.Single(sink.Fragments);
        Assert.Equal(expectedJustify, fragment.JustifyLines);
        // A justified fragment never ALSO carries a whole-line shift (justify distributes, it doesn't
        // shift); non-justify keywords keep their own factor (center 0.5, etc.) so aren't checked here.
        if (expectedJustify) Assert.Equal(0.0, fragment.LineAlignFactor, precision: 3);
    }

    [Fact]
    public void Inline_atomic_shifts_with_centered_text_align()
    {
        // Body text-align cycle — an inline atomic moves WITH its line under text-align: center.
        // A 50px-wide inline-block alone on a 600px centred line shifts to (600 − 50) / 2 = 275;
        // under start it stays at 0.
        static double DecorationX(int textAlignKeyword)
        {
            var sink = new RecordingFragmentSink();
            using var resolver = new SyntheticShaperResolver();
            var blockStyle = MakeStyle();
            blockStyle.Set(PropertyId.TextAlign, ComputedSlot.FromKeyword(textAlignKeyword));
            var ibStyle = MakeStyle();
            ibStyle.Set(PropertyId.Width, ComputedSlot.FromLengthPx(50));
            ibStyle.Set(PropertyId.Height, ComputedSlot.FromLengthPx(30));
            var inlineBlock = Box.ForElement(BoxKind.InlineBlockContainer, ibStyle, MakeElement());
            inlineBlock.AppendChild(Box.TextRun("A", MakeStyle()));
            var root = Box.CreateRoot(MakeStyle());
            var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
            block.AppendChild(inlineBlock);
            root.AppendChild(block);
            using var layouter = new BlockLayouter(root, sink, null, null, resolver);
            var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
            var layoutCtx = new LayoutContext(ctx);
            using var br = new BreakResolver();
            layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);
            foreach (var f in sink.Fragments)
                if (ReferenceEquals(f.Box, inlineBlock) && f.InlineLayout is null) return f.InlineOffset;
            throw new Xunit.Sdk.XunitException("no inline-block decoration fragment");
        }

        Assert.Equal(0, DecorationX(0), precision: 1);     // start
        Assert.Equal(275, DecorationX(4), precision: 1);   // center: (600 − 50) / 2
    }

    [Fact]
    public void Inline_atomic_with_preceding_text_shifts_by_line_align_plus_text_advance()
    {
        // Post-PR-#191 review P3 — when an inline atomic SHARES its line with preceding text, its
        // offset must be (line-align shift) + (preceding text advance): the alignment shift LAYERS ON
        // TOP of the sliceStartXPx cursor, it doesn't replace it. The prior coverage put the atomic
        // ALONE on the line (sliceStartXPx = 0), so a bug that dropped sliceStartXPx — or applied the
        // shift to the wrong cursor — would have passed. Layout: block > ["A", inline-block 50×30, "B"]
        // with the synthetic font (12px, 'A'/'B' advance = 500/1000em = 6px), on a 600px line.
        static double AtomicX(int textAlignKeyword)
        {
            var sink = new RecordingFragmentSink();
            using var resolver = new SyntheticShaperResolver();
            var blockStyle = MakeStyle();
            blockStyle.Set(PropertyId.TextAlign, ComputedSlot.FromKeyword(textAlignKeyword));
            var ibStyle = MakeStyle();
            ibStyle.Set(PropertyId.Width, ComputedSlot.FromLengthPx(50));
            ibStyle.Set(PropertyId.Height, ComputedSlot.FromLengthPx(30));
            var inlineBlock = Box.ForElement(BoxKind.InlineBlockContainer, ibStyle, MakeElement());
            inlineBlock.AppendChild(Box.TextRun("A", MakeStyle()));
            var root = Box.CreateRoot(MakeStyle());
            var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
            block.AppendChild(Box.TextRun("A", MakeStyle()));   // preceding text → sliceStartX = 6px
            block.AppendChild(inlineBlock);
            block.AppendChild(Box.TextRun("B", MakeStyle()));   // trailing text → grows TotalAdvance
            root.AppendChild(block);
            using var layouter = new BlockLayouter(root, sink, null, null, resolver);
            var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
            var layoutCtx = new LayoutContext(ctx);
            using var br = new BreakResolver();
            layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);
            foreach (var f in sink.Fragments)
                if (ReferenceEquals(f.Box, inlineBlock) && f.InlineLayout is null) return f.InlineOffset;
            throw new Xunit.Sdk.XunitException("no inline-block decoration fragment");
        }

        var start = AtomicX(0);     // start  → preceding text advance only
        var center = AtomicX(4);    // center → preceding advance + half the free space
        var right = AtomicX(3);     // right  → preceding advance + all the free space

        // sliceStartXPx is carried: the atomic sits AFTER the "A" glyph (≈6px), not at x = 0.
        Assert.True(start > 1.0, $"the preceding text advance should offset the atomic; got {start}");
        // The shift LAYERS on top, and is the line-align geometry (free space × factor via TotalAdvance):
        // center = start + free/2, right = start + free ⇒ center − start is exactly half of right − start.
        Assert.True(center > start && right > center,
            $"the atomic should shift right with its line; got {start}/{center}/{right}");
        Assert.Equal(start + (right - start) / 2.0, center, precision: 1);
        // Exact derived geometry: "A"(6) + atomic(50) + "B"(6) = TotalAdvance 62 on a 600px line ⇒
        // free 538; start = 6, center = 6 + 269 = 275, right = 6 + 538 = 544.
        Assert.Equal(6, start, precision: 1);
        Assert.Equal(275, center, precision: 1);
        Assert.Equal(544, right, precision: 1);
    }

    [Theory]
    [InlineData(0, 1.0)]   // start → RIGHT edge in RTL (the swap)
    [InlineData(1, 0.0)]   // end   → LEFT edge in RTL (the swap)
    [InlineData(2, 0.0)]   // left  stays physical-left
    [InlineData(3, 1.0)]   // right stays physical-right
    [InlineData(4, 0.5)]   // center symmetric
    public void Rtl_text_align_swaps_inline_line_align_factor(int textAlignKeyword, double expectedFactor)
    {
        // Direction pipeline + task 5 — `direction: rtl` makes `text-align: start/end` direction-
        // relative end-to-end: the cascade resolves direction, BlockLayouter reads it (the emitted
        // fragment's LineAlignFactor = ReadInlineAlignFactor), and start/end swap (start → right edge).
        // Physical left/right and center are unchanged, mirroring the LTR theory above.
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var blockStyle = MakeStyle();
        blockStyle.Set(PropertyId.Direction, ComputedSlot.FromKeyword(1));   // rtl
        blockStyle.Set(PropertyId.TextAlign, ComputedSlot.FromKeyword(textAlignKeyword));
        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        block.AppendChild(Box.TextRun("A", MakeStyle()));
        root.AppendChild(block);

        using var layouter = new BlockLayouter(root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        var fragment = Assert.Single(sink.Fragments);
        Assert.Equal(expectedFactor, fragment.LineAlignFactor, precision: 3);
    }

    [Fact]
    public void Rtl_inline_atomic_shifts_to_right_edge_under_default_start()
    {
        // RTL inline-atomic alignment — a `direction: rtl` block with the initial `text-align: start`
        // right-aligns, so a 50px inline-block alone on a 600px line shifts to the RIGHT edge
        // (600 − 50 = 550); the same default in LTR leaves it at 0. The atomic moves WITH its line
        // because the placement reads the SAME ReadInlineAlignFactor the glyph painter does.
        static double DecorationX(int direction)
        {
            var sink = new RecordingFragmentSink();
            using var resolver = new SyntheticShaperResolver();
            var blockStyle = MakeStyle();
            blockStyle.Set(PropertyId.Direction, ComputedSlot.FromKeyword(direction));
            var ibStyle = MakeStyle();
            ibStyle.Set(PropertyId.Width, ComputedSlot.FromLengthPx(50));
            ibStyle.Set(PropertyId.Height, ComputedSlot.FromLengthPx(30));
            var inlineBlock = Box.ForElement(BoxKind.InlineBlockContainer, ibStyle, MakeElement());
            inlineBlock.AppendChild(Box.TextRun("A", MakeStyle()));
            var root = Box.CreateRoot(MakeStyle());
            var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
            block.AppendChild(inlineBlock);
            root.AppendChild(block);
            using var layouter = new BlockLayouter(root, sink, null, null, resolver);
            var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
            var layoutCtx = new LayoutContext(ctx);
            using var br = new BreakResolver();
            layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);
            foreach (var f in sink.Fragments)
                if (ReferenceEquals(f.Box, inlineBlock) && f.InlineLayout is null) return f.InlineOffset;
            throw new Xunit.Sdk.XunitException("no inline-block decoration fragment");
        }

        Assert.Equal(550, DecorationX(1), precision: 1);   // rtl: 600 − 50, the right edge
        Assert.Equal(0, DecorationX(0), precision: 1);     // ltr: start = left
    }

    [Fact]
    public void Rtl_block_threads_rtl_base_direction_into_bidi_levels()
    {
        // PR 2 review P3 — prove the production wiring BlockLayouter → InlineLayouter.LayoutPerRun(
        // paragraphDirection: ReadParagraphDirection()) actually reaches the BIDI algorithm, not just the
        // align factor. The SAME Latin "A" itemizes to bidi level 0 under an LTR base paragraph but level 2
        // under an RTL base (UAX #9 I2 lifts an L run one level above the odd RTL base level 1). A differing
        // level on identical text is direct evidence the CSS `direction` set the paragraph base direction
        // threaded into LayoutPerRun — independent of ReadInlineAlignFactor (which drives the x-position).
        static int BaseDirectionBidiLevel(int direction)
        {
            var sink = new RecordingFragmentSink();
            using var resolver = new SyntheticShaperResolver();
            var blockStyle = MakeStyle();
            blockStyle.Set(PropertyId.Direction, ComputedSlot.FromKeyword(direction));
            var root = Box.CreateRoot(MakeStyle());
            var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
            block.AppendChild(Box.TextRun("A", MakeStyle()));
            root.AppendChild(block);
            using var layouter = new BlockLayouter(root, sink, null, null, resolver);
            var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
            var layoutCtx = new LayoutContext(ctx);
            using var br = new BreakResolver();
            layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);
            foreach (var f in sink.Fragments)
                if (f.InlineLayout is { } il && il.ShapedRuns.Count > 0)
                    return il.ShapedRuns[0].Source.BidiLevel;
            throw new Xunit.Sdk.XunitException("no inline-layout fragment with shaped runs");
        }

        Assert.Equal(0, BaseDirectionBidiLevel(0));   // ltr base → "A" stays at level 0
        Assert.Equal(2, BaseDirectionBidiLevel(1));   // rtl base → "A" (L) lifted to level 2 (UAX #9 I2)
    }

    [Theory]
    [InlineData(0, false)]   // baseline — control: a 48px baseline run does NOT grow the line (mixed-font growth is separate)
    [InlineData(6, true)]    // top      — grows the line to contain the 48px run
    [InlineData(7, true)]    // bottom   — grows the line
    [InlineData(5, true)]    // middle   — grows the line
    [InlineData(3, true)]    // text-top — grows the line
    public void Line_edge_vertical_align_grows_line_to_contain_tall_run(int valignKeyword, bool expectGrowth)
    {
        // PR 3 task 7 — a line-edge-aligned (top/bottom/middle/text-top/text-bottom) inline TEXT run TALLER
        // than the baseline-sized line GROWS the line so it is CONTAINED (it previously overflowed — the
        // PR #195 deferral). A 48px line-edge span on a 16px line (~19.2px) grows the line to ≥ ~40px; a
        // 48px BASELINE span leaves the line at ~19.2 (the separate mixed-font-size growth is out of scope).
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();
        var blockStyle = MakeStyle();   // default 16px font → ~19.2px line
        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        block.AppendChild(Box.TextRun("A", MakeStyle()));
        var spanStyle = MakeStyle();
        spanStyle.Set(PropertyId.VerticalAlign, ComputedSlot.FromKeyword(valignKeyword));
        spanStyle.Set(PropertyId.FontSize, ComputedSlot.FromLengthPx(48));
        block.AppendChild(Box.TextRun("B", spanStyle));
        root.AppendChild(block);
        using var layouter = new BlockLayouter(root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);
        var fragment = Assert.Single(sink.Fragments);

        if (expectGrowth)
            Assert.True(fragment.BlockSize >= 40.0,
                $"line-edge valign {valignKeyword} should grow the line to contain the 48px run; got {fragment.BlockSize}");
        else
            Assert.True(fragment.BlockSize < 25.0,
                $"a baseline 48px run should leave the line baseline-sized (~19.2); got {fragment.BlockSize}");
    }

    [Fact]
    public void Line_edge_growth_uses_the_runs_line_height_not_just_font_size()
    {
        // Post-PR-#197 review P2 — line-edge growth uses the run's INLINE-BOX height (its USED line-height),
        // so a tall `line-height` (not only a large font-size) grows the line. A 16px-font `vertical-align:
        // top` span with line-height:80px grows the line to ~80px; the same span with normal line-height
        // (~19.2px) leaves a small line. (Pre-fix the growth used font-size only, so both gave ~16-19px.)
        double LineSize(double spanLineHeightPx)
        {
            var sink = new RecordingFragmentSink();
            using var resolver = new SyntheticShaperResolver();
            var blockStyle = MakeStyle();
            var root = Box.CreateRoot(MakeStyle());
            var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
            block.AppendChild(Box.TextRun("A", MakeStyle()));
            var spanStyle = MakeStyle();
            spanStyle.Set(PropertyId.VerticalAlign, ComputedSlot.FromKeyword(6));   // top
            spanStyle.Set(PropertyId.FontSize, ComputedSlot.FromLengthPx(16));
            if (spanLineHeightPx > 0)
                spanStyle.Set(PropertyId.LineHeight, ComputedSlot.FromLengthPx(spanLineHeightPx));
            block.AppendChild(Box.TextRun("B", spanStyle));
            root.AppendChild(block);
            using var layouter = new BlockLayouter(root, sink, null, null, resolver);
            var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
            var layoutCtx = new LayoutContext(ctx);
            using var br = new BreakResolver();
            layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);
            return Assert.Single(sink.Fragments).BlockSize;
        }

        Assert.True(LineSize(80) >= 70.0,
            $"line-height:80 + vertical-align:top should grow the line to ~80; got {LineSize(80)}");
        Assert.True(LineSize(0) < 30.0,
            $"the same 16px-font span at normal line-height should leave a small line; got {LineSize(0)}");
    }

    [Fact]
    public void Block_with_margin_border_padding_honors_box_model()
    {
        // Per Finding #5 — margin/border/padding/width applied to the
        // inline-only block flow through to the emitted fragment's
        // border-box geometry. With margin:10, padding:5, border:2,
        // width:200 — the border-box is 200+10 = wait, width is
        // CONTENT-box per CSS 2.1 §10.3. Border-box = width + borders
        // + padding = 200 + 2*2 + 2*5 = 214. InlineOffset =
        // marginInlineStart = 10. BlockOffset = startCursor (=0) +
        // marginBlockStart = 10. BlockSize = contentBlock +
        // padding-vertical + border-vertical (computed line-height +
        // padding 10 + border 4).
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var blockStyle = MakeStyle();
        blockStyle.Set(PropertyId.MarginTop, ComputedSlot.FromLengthPx(10));
        blockStyle.Set(PropertyId.MarginBottom, ComputedSlot.FromLengthPx(10));
        blockStyle.Set(PropertyId.MarginLeft, ComputedSlot.FromLengthPx(10));
        blockStyle.Set(PropertyId.MarginRight, ComputedSlot.FromLengthPx(10));
        blockStyle.Set(PropertyId.PaddingTop, ComputedSlot.FromLengthPx(5));
        blockStyle.Set(PropertyId.PaddingBottom, ComputedSlot.FromLengthPx(5));
        blockStyle.Set(PropertyId.PaddingLeft, ComputedSlot.FromLengthPx(5));
        blockStyle.Set(PropertyId.PaddingRight, ComputedSlot.FromLengthPx(5));
        blockStyle.Set(PropertyId.BorderTopWidth, ComputedSlot.FromLengthPx(2));
        blockStyle.Set(PropertyId.BorderBottomWidth, ComputedSlot.FromLengthPx(2));
        blockStyle.Set(PropertyId.BorderLeftWidth, ComputedSlot.FromLengthPx(2));
        blockStyle.Set(PropertyId.BorderRightWidth, ComputedSlot.FromLengthPx(2));
        // Per CSS B&B 3 §4.3 a used border-width needs a visible style (solid = keyword 4).
        blockStyle.Set(PropertyId.BorderTopStyle, ComputedSlot.FromKeyword(4));
        blockStyle.Set(PropertyId.BorderBottomStyle, ComputedSlot.FromKeyword(4));
        blockStyle.Set(PropertyId.BorderLeftStyle, ComputedSlot.FromKeyword(4));
        blockStyle.Set(PropertyId.BorderRightStyle, ComputedSlot.FromKeyword(4));
        blockStyle.Set(PropertyId.Width, ComputedSlot.FromLengthPx(200));

        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        block.AppendChild(Box.TextRun("A", MakeStyle()));
        root.AppendChild(block);

        using var layouter = new BlockLayouter(
            root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        var fragment = Assert.Single(sink.Fragments);
        // InlineOffset = marginInlineStart = 10.
        Assert.Equal(10, fragment.InlineOffset);
        // BlockOffset = fragmentainer.UsedBlockSize-at-entry (0) +
        // marginBlockStart = 10.
        Assert.Equal(10, fragment.BlockOffset);
        // Border-box inline size = width + borders + padding =
        // 200 + 2*2 + 2*5 = 214.
        Assert.Equal(214, fragment.InlineSize);
        // Border-box block size = content + padding-vert + border-vert
        // (= 1 line × 16 × 1.2 + 2*5 + 2*2 = 19.2 + 14 = 33.2).
        Assert.Equal(33.2, fragment.BlockSize, precision: 1);
    }

    [Fact]
    public void Block_with_lineHeight_override_uses_declared_line_height()
    {
        // Per Finding #5 — `line-height: 24px` overrides the
        // 1.2×font-size fallback. One line of 'A' produces a
        // 24-px-tall content block (no padding/border).
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var blockStyle = MakeStyle();
        blockStyle.Set(PropertyId.LineHeight, ComputedSlot.FromLengthPx(24));

        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        block.AppendChild(Box.TextRun("A", MakeStyle()));
        root.AppendChild(block);

        using var layouter = new BlockLayouter(
            root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        var fragment = Assert.Single(sink.Fragments);
        // With no border/padding, BlockSize = lines × line-height =
        // 1 × 24 = 24.
        Assert.Equal(24, fragment.BlockSize);
    }

    [Fact]
    public void Nested_div_with_inline_p_descendant_emits_p_fragment_with_InlineLayout()
    {
        // Per Finding #2 — <div><p>A</p></div>. The outer div is a
        // block container with one block-level child (the p); the p
        // is inline-only because its only child is a TextRun. The
        // recursive subtree walker must dispatch the p through the
        // inline pass + emit ITS fragment with InlineLayout set.
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var divBox = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        var pBox = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        pBox.AppendChild(Box.TextRun("A", MakeStyle()));
        divBox.AppendChild(pBox);
        root.AppendChild(divBox);

        using var layouter = new BlockLayouter(
            root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        // Two fragments — one for div, one for p.
        Assert.Equal(2, sink.Fragments.Count);
        var divFragment = sink.Fragments[0];
        var pFragment = sink.Fragments[1];
        Assert.Same(divBox, divFragment.Box);
        Assert.Same(pBox, pFragment.Box);
        // Outer div has no InlineLayout (its child is block-level).
        Assert.Null(divFragment.InlineLayout);
        // Inner p HAS InlineLayout populated (Finding #2 fix).
        Assert.NotNull(pFragment.InlineLayout);
        Assert.NotEmpty(pFragment.InlineLayout!.Value.Lines);
        // P's block offset is inside the div's content area —
        // divFragment.BlockOffset is 0, so pFragment.BlockOffset
        // should be >= 0 (no margins → exactly 0 with no border/
        // padding on the div).
        Assert.True(pFragment.BlockOffset >= divFragment.BlockOffset);
    }

    [Fact]
    public void Triple_nested_inline_p_emits_with_resolvable_geometry()
    {
        // Per Finding #2 — <div><div><p>A</p></div></div>. Tests
        // that the recursive walker handles depth >= 2 + still
        // dispatches the deepest inline-only block correctly.
        var sink = new RecordingFragmentSink();
        using var resolver = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var outerDiv = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        var innerDiv = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        var pBox = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        pBox.AppendChild(Box.TextRun("A", MakeStyle()));
        innerDiv.AppendChild(pBox);
        outerDiv.AppendChild(innerDiv);
        root.AppendChild(outerDiv);

        using var layouter = new BlockLayouter(
            root, sink, null, null, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        Assert.Equal(3, sink.Fragments.Count);
        var pFragment = sink.Fragments[2];
        Assert.Same(pBox, pFragment.Box);
        Assert.NotNull(pFragment.InlineLayout);
    }

    [Fact]
    public void Block_with_inline_content_too_tall_emits_forced_overflow_diagnostic()
    {
        // Per Finding #3 — pagination integration. An inline-only
        // block whose computed border-box is taller than the
        // fragmentainer triggers PAGINATION-FORCED-OVERFLOW-001 +
        // the layouter returns PageComplete (forward-progress).
        // We force the overflow via an outsized line-height.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var resolver = new SyntheticShaperResolver();

        var blockStyle = MakeStyle();
        blockStyle.Set(PropertyId.LineHeight, ComputedSlot.FromLengthPx(2000));

        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        block.AppendChild(Box.TextRun("A", MakeStyle()));
        root.AppendChild(block);

        using var layouter = new BlockLayouter(
            root, sink, null, diagSink, resolver);
        // 800-px page — line height 2000 → forced overflow.
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var overflowDiag = diagSink.Diagnostics
            .Find(d => d.Code == PaginateDiagnosticCodes.PaginationForcedOverflow001);
        Assert.NotEqual(default, overflowDiag);
        // Fragment still emitted (forward-progress semantics).
        Assert.Single(sink.Fragments);
    }

    [Fact]
    public void Block_with_inline_content_that_fits_emits_no_overflow_diagnostic()
    {
        // Per Finding #3 — clean dispatch on the happy path: a
        // paragraph that fits the page produces NO overflow
        // diagnostic.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var resolver = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        block.AppendChild(Box.TextRun("A", MakeStyle()));
        root.AppendChild(block);

        using var layouter = new BlockLayouter(
            root, sink, null, diagSink, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        Assert.Single(sink.Fragments);
        // No forced-overflow diagnostic should fire.
        Assert.DoesNotContain(diagSink.Diagnostics,
            d => d.Code == PaginateDiagnosticCodes.PaginationForcedOverflow001);
    }

    [Fact]
    public void Block_with_mixed_word_break_inline_lays_out_without_a_diagnostic()
    {
        // word-break-keep-all-cjk closed — a block whose inline content mixes a normal run with a
        // keep-all run (which previously made InlineLayouter.LayoutPerRun throw NotSupportedException,
        // degrading to a LAYOUT-INLINE-UNSUPPORTED-001 Warning + a skipped block) now lays out normally:
        // LineBuilder applies LB30b CJK suppression per source run, so the block emits its text fragment
        // and no unsupported-inline diagnostic fires. (The BlockLayouter's defensive
        // NotSupportedException catch remains for genuinely-unsupported future inline features.)
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var resolver = new SyntheticShaperResolver();

        var styleA = MakeStyle();  // default WordBreak = Normal (index 0).
        var styleB = MakeStyle();
        // word-break: keep-all is keyword index 2 in the source-gen'd table — set as a Keyword slot.
        styleB.Set(PropertyId.WordBreak, ComputedSlot.FromKeyword(2));

        var root = Box.CreateRoot(MakeStyle());
        var block = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        block.AppendChild(Box.TextRun("A", styleA));
        block.AppendChild(Box.TextRun("B", styleB));
        root.AppendChild(block);

        using var layouter = new BlockLayouter(
            root, sink, null, diagSink, resolver);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var br = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, br, LayoutAttemptStrategy.Strict);

        // Lays out: AllDone, a fragment emitted, and NO unsupported-inline diagnostic.
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.NotEmpty(sink.Fragments);
        Assert.DoesNotContain(diagSink.Diagnostics,
            d => d.Code == PaginateDiagnosticCodes.LayoutInlineUnsupported001);
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

        public void UpdateFragmentBlockSize(int cursor, double newBlockSize)
        {
            // Per Phase 3 Task 17 cycle 5c.2b — F2 wrapper-resize.
            // Mutate the BoxFragment at <c>cursor</c> in place so the
            // BlockLayouter's post-dispatch wrapper-resize consumer
            // can shrink a paginatable-grid / paginatable-flex
            // wrapper from the clamped budget to the actual emitted
            // extent without breaking z-order (= the wrapper stays
            // ahead of its children in the fragment list).
            if (cursor < 0 || cursor >= Fragments.Count) return;
            Fragments[cursor] = Fragments[cursor] with { BlockSize = newBlockSize };
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
