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
    public void Block_with_unsupported_inline_throws_caught_as_diagnostic()
    {
        // Per Finding #6 — when InlineLayouter.LayoutPerRun throws
        // NotSupportedException (here induced by KeepAll vs Normal
        // word-break mismatch across two source TextRuns), the
        // block layouter catches it + degrades to a Warning
        // diagnostic + skips the block without bringing the
        // layouter down.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var resolver = new SyntheticShaperResolver();

        // Build two TextRuns with mismatched WordBreak. KeepAll's
        // CJK semantics are deferred; mixed-mode throws per
        // sub-cycle 3 review Finding #1.
        var styleA = MakeStyle();  // default WordBreak = Normal (index 0).
        var styleB = MakeStyle();
        // word-break: keep-all is keyword index 2 in the source-gen'd
        // table — set as Keyword slot.
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

        // No crash; AllDone outcome; no fragment emitted; one
        // LAYOUT-INLINE-UNSUPPORTED-001 diagnostic captured.
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Empty(sink.Fragments);
        var unsupportedDiag = diagSink.Diagnostics
            .Find(d => d.Code == PaginateDiagnosticCodes.LayoutInlineUnsupported001);
        Assert.NotEqual(default, unsupportedDiag);
        Assert.Equal(PaginateDiagnosticSeverity.Warning, unsupportedDiag.Severity);
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
