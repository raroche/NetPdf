// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;

namespace NetPdf.Layout.Layouters;

/// <summary>Non-block-pagination arc (flex item content + grid content-sized
/// rows) — a fragment sink that BUFFERS the fragments a nested
/// <see cref="BlockLayouter"/> emits (in INNER, content-box-relative
/// coordinates) so the caller can (a) read the content's block / inline
/// extent for sizing an indefinite box, and (b) re-emit the content at the
/// box's FINAL position once that position is known (e.g. after a flex
/// item-split re-anchors the item, or after grid row heights settle).
///
/// <para>Mirrors <see cref="TableLayouter.MeasuringFragmentSink"/>'s
/// measure-then-flush role but keeps the buffered fragments in their raw
/// INNER coordinates and applies the FULL translation at
/// <see cref="FlushTo"/> time (rather than baking a partial translation at
/// <see cref="Emit"/> time). That suits callers whose final origin is not
/// known until after measurement: the flex item-content pass measures every
/// item up front (to content-size auto-height column items) but only learns
/// each item's re-anchored page offset during the emission loop.</para>
///
/// <para>Extracted as a shared sink (rule-of-three: the table cell sink, the
/// grid item translating sink, and this) rather than reusing
/// <see cref="TableLayouter.MeasuringFragmentSink"/> — that one is a private
/// nested type with a table-specific split-translation contract; the existing
/// table / grid measurement code is deliberately left untouched to keep the
/// regression surface small.</para></summary>
internal sealed class BufferingMeasureSink : IBlockFragmentSink
{
    private readonly List<BoxFragment> _buffered = new();

    /// <summary>The box whose content this sink buffers. A buffered fragment
    /// whose <see cref="BoxFragment.Box"/> IS this box (= the inline-only-root
    /// content fragment a flex / grid item emits for its own text) is marked
    /// <see cref="BoxFragment.SuppressBoxDecoration"/> so it paints text only —
    /// the item's flex / grid GEOMETRY fragment already paints the box
    /// decoration. Null = no marking (the fragment's box never matches).</summary>
    private readonly Box? _decorationOwner;

    public BufferingMeasureSink(Box? decorationOwner = null)
    {
        _decorationOwner = decorationOwner;
    }

    /// <summary>Maximum block extent (<c>BlockOffset + BlockSize</c>) observed
    /// across buffered fragments, in the inner content-box coordinate space
    /// (= relative to the content-box block-start, NOT the fragmentainer).
    /// Drives the auto-height main-size of an indefinite box.</summary>
    public double ContentBlockExtent { get; private set; }

    /// <summary>Maximum inline extent observed across buffered fragments, in
    /// the inner content-box coordinate space. For inline-only fragments
    /// (shaped text) this is the widest LINE advance — the ACTUAL text width,
    /// not the wrapper's available width — so a caller can derive a
    /// max-content-ish inline contribution. Mirrors
    /// <see cref="TableLayouter.MeasuringFragmentSink.MaxInlineExtentFromCellOrigin"/>.</summary>
    public double ContentInlineExtent { get; private set; }

    /// <summary>Count of buffered fragments — the rollback / checkpoint
    /// cursor for the inner <see cref="BlockLayouter"/>.</summary>
    public int Cursor => _buffered.Count;

    /// <summary>Flex content-inset cycle — whether any buffered fragment IS the
    /// decoration owner (box == the measured box), i.e. the box laid out as an
    /// INLINE-ONLY root (its direct content is text, emitted as one own-box
    /// fragment). In that shape the fragment is already the box's BORDER box
    /// (<see cref="ContentBlockExtent"/> includes the box's own border + padding) and
    /// <c>TextPainter</c> insets the glyphs by that border + padding — so the flex
    /// caller must NOT inset it again. When false, the buffer holds BLOCK-CHILD
    /// fragments (box != the measured box) laid out at the content-box origin without
    /// the box's own chrome — the caller insets them + adds the chrome for the border
    /// box. A box never produces BOTH (mixed inline + block children wrap inline runs
    /// in anonymous blocks, so no own-box fragment).</summary>
    public bool ContainsDecorationOwnerFragment { get; private set; }

    /// <summary>Inline-block last-line-baseline cycle (CSS 2.2 §10.8.1) — whether any buffered fragment
    /// carries an in-flow LINE BOX (an <see cref="BoxFragment.InlineLayout"/> with ≥ 1 line). An
    /// inline-block with a line box takes its baseline from its last line; with NONE (e.g. only empty
    /// blocks), the baseline is the bottom margin edge (the img-ish placement).</summary>
    public bool HasInFlowLineBox { get; private set; }

    /// <summary>Inline-block last-line-baseline cycle (CSS 2.2 §10.8.1) — the BOTTOM (in the buffer's
    /// content coordinate space, the same as <see cref="ContentBlockExtent"/>) of the LAST in-flow line
    /// box: the greatest line-bearing fragment's inner bottom MINUS that fragment's block-end border +
    /// padding (so trailing chrome below the last line, or a later non-line block, padding, or empty
    /// box, doesn't pull the baseline down — post-PR-#192 review P1). 0 until a line box is seen
    /// (read only when <see cref="HasInFlowLineBox"/>).</summary>
    public double LastLineBoxBottom { get; private set; }

    /// <summary>Inline-block last-line-baseline cycle (CSS 2.2 §10.8.1) — the DESCENT below the baseline
    /// of that LAST in-flow line box (px), captured from THAT (deepest) line-bearing fragment's OWN
    /// metrics (its <see cref="BoxFragment.TextMetricsStyle"/> ?? box-style font + its actual last-line
    /// height), NOT the inline-block's OUTER font. So a nested-block inline-block whose content declares a
    /// different font-size / line-height than the outer box gets an exact §10.8.1 baseline. 0 until a line
    /// box is seen (read only when <see cref="HasInFlowLineBox"/>). APPROXIMATION: an inline SPAN that
    /// overrides the font ON the last line still falls back to the fragment box font — the layout layer
    /// has no per-RUN metrics.</summary>
    public double LastLineBoxDescentBelowBaselinePx { get; private set; }

    public void Emit(BoxFragment fragment)
    {
        // Out-of-flow (position: absolute / fixed) descendants of a flex / grid
        // item are emitted + anchored by the OUTER layout pass (flex is NOT a
        // delegation boundary for abspos — the outer walk reaches them), so the
        // nested content measure must NOT buffer them: re-emitting on FlushTo
        // would DOUBLE the box (anchored to the wrong containing block), and
        // counting it would wrongly inflate the in-flow content extent. Drop it.
        // NB: dropping means Cursor (= _buffered.Count) advances only for
        // BUFFERED (in-flow) fragments — safe because the inner BlockLayouter's
        // abspos / fixed passes run AFTER the in-flow child loop that captures
        // cursors + issues UpdateFragmentBlockSize, so no in-flow cursor target
        // is shifted by a skipped out-of-flow emission.
        if (fragment.Box.Style.IsOutOfFlow()) return;

        // The inline-only-root content fragment (box == the item) paints text
        // only — its decoration is the item's flex / grid geometry fragment's
        // job. Block-CHILD fragments (box != the item) keep their own decoration.
        if (_decorationOwner is not null && ReferenceEquals(fragment.Box, _decorationOwner))
        {
            fragment = fragment with { SuppressBoxDecoration = true };
            ContainsDecorationOwnerFragment = true;
        }

        var innerBottom = fragment.BlockOffset + fragment.BlockSize;
        if (innerBottom > ContentBlockExtent)
        {
            ContentBlockExtent = innerBottom;
        }

        // Inline extent: prefer the widest shaped LINE advance for inline-only
        // fragments (the natural text width), else the fragment's border-box
        // inline right edge. Mirrors the table cell sink's two-path tracker.
        if (fragment.InlineLayout is { } inlineLayout && inlineLayout.Lines.Length > 0)
        {
            HasInFlowLineBox = true;   // §10.8.1 — an inline-block with a line box has a baseline.
            // The last line box's bottom = this fragment's inner bottom minus its OWN block-end chrome
            // (the lines sit inside the border box). Track the GREATEST so the LAST in-flow line box wins;
            // a trailing non-line block / padding (counted in ContentBlockExtent) does NOT move it.
            var style = fragment.Box.Style;
            var blockEndChrome = style.BlockBorderPaddingPx() - style.BlockStartBorderPaddingPx();
            var lineBoxBottom = innerBottom - blockEndChrome;
            if (lineBoxBottom > LastLineBoxBottom)
            {
                LastLineBoxBottom = lineBoxBottom;
                // Capture THIS now-deepest line box's descent below its own baseline. When the line has a
                // PINNED per-line baseline (a baseline-aligned inner atomic / shifted text grew + pinned it,
                // so PerLineBaselineTopPx[^1] is finite), the descent is EXACT — the line height minus that
                // baseline's offset from the line top (post-PR-#195 review P2). Otherwise fall back to the
                // fragment's REAL metrics (its TextMetricsStyle ?? box-style font + its actual last-line
                // height PerLineHeightsPx[^1]), descent = lineHeight/2 − (ascent+descent)/2 with the
                // 0.8/0.2-em approximation (ascent+descent)/2 = 0.3·fontSize — NOT the inline-block's OUTER
                // font, so a nested-block inline-block with a different font-size / line-height stays exact.
                var metricsStyle = fragment.TextMetricsStyle ?? style;
                // Per-run last-line metrics (PR-3 task 9; PR #198 review P2) — a run on the last line whose
                // OWN used metrics imply a deeper descent below the baseline deepens the inline-block's
                // baseline. The candidate is chosen by each run's USED DESCENT (its used line-height AND
                // font-size: lineHeight/2 − 0.3·fontSize), not font-size alone — so a SAME-font run with a
                // larger line-height (e.g. `font-size:16px; line-height:80px`) is caught too, not only a
                // larger-font run. Strict deepen-only (override only when a run is deeper than the fragment's
                // metrics style), so the element()-running TextMetricsStyle case and the uniform-font case
                // stay byte-identical; a larger-font run still wins exactly as before (its descent is deeper).
                var metricsFontSizePx = metricsStyle.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16);
                var deepestStyle = DeepestLastLineRunStyle(inlineLayout, metricsStyle, metricsFontSizePx) ?? metricsStyle;
                var lastLineFontSizePx = deepestStyle.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16);
                var lastLineHeightPx = fragment.PerLineHeightsPx is { Count: > 0 } perLineHeights
                    ? perLineHeights[perLineHeights.Count - 1]
                    : NetPdf.Layout.Inline.InlineVerticalAlign.OwnLineHeightPx(deepestStyle, lastLineFontSizePx);
                LastLineBoxDescentBelowBaselinePx =
                    fragment.PerLineBaselineTopPx is { Count: > 0 } baselines
                        && double.IsFinite(baselines[baselines.Count - 1])
                        ? System.Math.Max(0.0, lastLineHeightPx - baselines[baselines.Count - 1])
                        : System.Math.Max(0.0, lastLineHeightPx / 2.0 - 0.3 * lastLineFontSizePx);
            }
            var lines = inlineLayout.Lines;
            for (var i = 0; i < lines.Length; i++)
            {
                var lineRight = fragment.InlineOffset + lines[i].TotalAdvance;
                if (lineRight > ContentInlineExtent)
                {
                    ContentInlineExtent = lineRight;
                }
            }
        }
        else
        {
            var innerRight = fragment.InlineOffset + fragment.InlineSize;
            if (innerRight > ContentInlineExtent)
            {
                ContentInlineExtent = innerRight;
            }
        }

        _buffered.Add(fragment);
    }

    /// <summary>Per-run last-line metrics (PR-3 task 9; PR #198 review P2) — the style of the run on
    /// <paramref name="inline"/>'s LAST line with the DEEPEST descent below the baseline (computed from each
    /// run's OWN used line-height AND font-size, not font-size alone), or <see langword="null"/> when no
    /// last-line run is deeper than <paramref name="fallbackStyle"/> (so the caller keeps its own metrics
    /// style — the deepen-only contract that preserves the uniform-font + the element()-running cases).
    /// Selecting by used descent (rather than font-size) catches a same-font run with a larger line-height,
    /// while a larger-font run still wins exactly as before (its descent is deeper). Walks the last line's
    /// slices → shaped runs → preprocessed source styles.</summary>
    private static NetPdf.Css.ComputedValues.ComputedStyle? DeepestLastLineRunStyle(
        NetPdf.Layout.Inline.InlineLayoutResult inline,
        NetPdf.Css.ComputedValues.ComputedStyle fallbackStyle, double fallbackFontSizePx)
    {
        var lines = inline.Lines;
        if (lines.Length == 0) return null;
        var lastLine = lines[lines.Length - 1];
        NetPdf.Css.ComputedValues.ComputedStyle? deepest = null;
        var deepestDescentPx = LastLineRunDescentBelowBaselinePx(fallbackStyle, fallbackFontSizePx);
        foreach (var slice in lastLine.Slices)
        {
            if (slice.ShapedRunIndex < 0 || slice.ShapedRunIndex >= inline.ShapedRuns.Count) continue;
            var sourceIdx = inline.ShapedRuns[slice.ShapedRunIndex].Source.SourceTextRunIndex;
            if (sourceIdx < 0 || sourceIdx >= inline.PreprocessedRuns.Count) continue;
            var runStyle = inline.PreprocessedRuns[sourceIdx].Style;
            var fs = runStyle.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16);
            var descent = LastLineRunDescentBelowBaselinePx(runStyle, fs);
            if (descent > deepestDescentPx)
            {
                deepestDescentPx = descent;
                deepest = runStyle;
            }
        }
        return deepest;
    }

    /// <summary>A run's approximate descent below its baseline (px) from its USED line metrics —
    /// <c>OwnLineHeightPx/2 − 0.3·fontSize</c> (the half-leading model: 0.2-em font descent + half the
    /// leading), clamped ≥ 0. Drives the deepest-last-line-run selection so a larger line-height — not just a
    /// larger font-size — can deepen the inline-block's §10.8.1 baseline.</summary>
    private static double LastLineRunDescentBelowBaselinePx(
        NetPdf.Css.ComputedValues.ComputedStyle style, double fontSizePx)
        => System.Math.Max(0.0,
            NetPdf.Layout.Inline.InlineVerticalAlign.OwnLineHeightPx(style, fontSizePx) / 2.0 - 0.3 * fontSizePx);

    public void RollbackTo(int cursor)
    {
        if (cursor < 0 || cursor > _buffered.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(cursor),
                $"BufferingMeasureSink.RollbackTo: cursor {cursor} out of range "
                + $"[0, {_buffered.Count}].");
        }
        if (cursor < _buffered.Count)
        {
            _buffered.RemoveRange(cursor, _buffered.Count - cursor);
        }
        // Per the table sink's documented approximation — leave the extent
        // trackers stale (over-estimated) after a partial rollback rather than
        // re-scanning; the inner LastResort strategy suppresses rewinds so this
        // path isn't reached for the measure pass.
    }

    public void UpdateFragmentBlockSize(int cursor, double newBlockSize)
    {
        if (cursor < 0 || cursor >= _buffered.Count) return;
        var existing = _buffered[cursor];
        _buffered[cursor] = existing with { BlockSize = newBlockSize };
    }

    /// <summary>Re-emit every buffered fragment into <paramref name="target"/>,
    /// translated by (<paramref name="inlineTranslation"/>,
    /// <paramref name="blockTranslation"/>) — the box's FINAL content-box
    /// origin in the outer fragmentainer's coordinate space. Clears the buffer
    /// (content commits once). The inner fragments carry offsets relative to
    /// the content box's origin (0,0), so adding the final origin yields
    /// absolute fragmentainer coordinates.</summary>
    public void FlushTo(IBlockFragmentSink target, double inlineTranslation, double blockTranslation)
    {
        ArgumentNullException.ThrowIfNull(target);
        for (var i = 0; i < _buffered.Count; i++)
        {
            var f = _buffered[i];
            target.Emit(f with
            {
                InlineOffset = f.InlineOffset + inlineTranslation,
                BlockOffset = f.BlockOffset + blockTranslation,
            });
        }
        _buffered.Clear();
    }

    /// <summary>Row-nowrap intra-item content pagination — flush only the
    /// buffered fragments whose CROSS-AXIS (block) top falls within the page
    /// window <c>[<paramref name="windowFrom"/>, <paramref name="windowTo"/>)</c>
    /// (in the flex container's content-cross coordinate space), re-anchored so
    /// the window top maps to the container content-box cross-start
    /// (<paramref name="contentCrossOriginAbs"/>) on this page. A
    /// <c>flex-direction: row</c> / <c>nowrap</c> line taller than the page
    /// splits at this SHARED cross cut: every item re-emits its fragments that
    /// fall in this page's window, so all items continue at the same cross
    /// position (the page top) on the next page.
    ///
    /// <para>Partition-by-top (child-boundary granularity): a fragment whose top
    /// is in the window emits HERE — even if it overruns <paramref name="windowTo"/>
    /// (force-overflow, like the block layouter's over-tall child); a fragment
    /// whose top is &gt;= <paramref name="windowTo"/> defers (sets the returned
    /// <c>AnyRemaining</c>). This guarantees each fragment emits on exactly one
    /// page — no loss, no double — with a single accumulated cut. Intra-fragment
    /// splitting (a single line / block taller than the page) is deferred.</para>
    ///
    /// <para>Does NOT clear the buffer: the row-nowrap path builds a FRESH
    /// measure sink per page (the layouter re-measures), so each page slices its
    /// own buffer. Returns the emitted cross extent RELATIVE to the window top
    /// (for the wrapper resize + <c>LastEmittedBlockExtent</c>) and whether any
    /// fragment remains beyond this page.</para>
    ///
    /// <para><c>itemCrossOffsetAbs</c> is the item content-box cross-start in
    /// fragmentainer coordinates (= the item's <c>blockOffset</c> for row);
    /// <c>contentCrossOriginAbs</c> is the flex container's content-box
    /// cross-start in fragmentainer coordinates.</para></summary>
    public (double EmittedCrossExtent, bool AnyRemaining) FlushRangeTo(
        IBlockFragmentSink target,
        double inlineTranslation,
        double itemCrossOffsetAbs,
        double contentCrossOriginAbs,
        double windowFrom,
        double windowTo)
    {
        ArgumentNullException.ThrowIfNull(target);
        // The item content-box cross-start expressed in the container's
        // content-cross coordinate space (where the shared cut accumulates).
        var crossBase = itemCrossOffsetAbs - contentCrossOriginAbs;
        var emittedExtent = 0.0;
        var anyRemaining = false;
        for (var i = 0; i < _buffered.Count; i++)
        {
            var f = _buffered[i];
            var lineCrossTop = crossBase + f.BlockOffset;
            if (lineCrossTop >= windowTo)
            {
                anyRemaining = true;
                continue;       // starts on a later page
            }
            if (lineCrossTop < windowFrom)
            {
                continue;       // already emitted on a prior page
            }
            var relTop = lineCrossTop - windowFrom;
            target.Emit(f with
            {
                InlineOffset = f.InlineOffset + inlineTranslation,
                BlockOffset = contentCrossOriginAbs + relTop,
            });
            var bottom = relTop + f.BlockSize;
            if (bottom > emittedExtent)
            {
                emittedExtent = bottom;
            }
        }
        return (emittedExtent, anyRemaining);
    }
}
