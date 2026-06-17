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
