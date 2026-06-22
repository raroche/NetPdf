// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Threading;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Inline;
using NetPdf.Paginate;
using NetPdf.Paginate.Diagnostics;

namespace NetPdf.Layout.Layouters;

/// <summary>Non-block-pagination arc (flex item content + grid content-sized
/// rows) — lay out a box's INNER content via a nested <see cref="BlockLayouter"/>
/// into a fresh <see cref="BufferingMeasureSink"/> and return the buffer. The
/// caller reads <see cref="BufferingMeasureSink.ContentBlockExtent"/> /
/// <see cref="BufferingMeasureSink.ContentInlineExtent"/> for content sizing and
/// (optionally) flushes the buffered fragments at the box's final position.
///
/// <para>One shared dispatch (rule-of-three: <see cref="FlexLayouter"/> item
/// content, <see cref="GridLayouter"/>'s row content-sizing, and
/// <c>BlockLayouter.PreMeasureGridRowExtent</c>) so the nested-layout contract
/// — atomic <c>LastResort</c> pass, grid pagination suppressed (parallel flow),
/// inline-only ROOT content opted in (a `<c>&lt;div&gt;text&lt;/div&gt;</c>`
/// item / cell has DIRECT inline children) — lives in ONE place.</para></summary>
internal static class NestedContentMeasurer
{
    /// <summary>Row-flex intra-item content-measurement block budget (CSS px). The
    /// row-nowrap pre-measure (<c>BlockLayouter.PreMeasureFlexCrossExtent</c>) + the
    /// emission measure (<c>FlexLayouter</c>) size the inner fragmentainer to this so the
    /// item's FULL natural extent is captured — the atomic <c>LastResort</c> pass does NOT
    /// paginate, so a page-sized budget would CLIP the very overflow being detected.
    ///
    /// <para><b>Not truly unbounded.</b> <see cref="Measure"/> runs ONE atomic pass and
    /// ignores continuations, so content TALLER than this budget is still clipped. At
    /// ~10,400 inches no real document reaches it, and <c>FlexLayouter</c> surfaces
    /// <c>LAYOUT-FLEX-ITEM-CONTENT-TRUNCATED-001</c> if a measured extent does — so the
    /// truncation is never SILENT. Streaming the measure/slice page-by-page (consuming
    /// nested continuations) is the documented follow-up; see <c>docs/deferrals.md</c>.</para></summary>
    public const double EffectivelyUnboundedBlockBudgetPx = 1_000_000.0;

    /// <summary>Lay out <paramref name="box"/>'s content at
    /// <paramref name="availInlineContentSize"/> available inline size into a
    /// new buffer. <paramref name="blockBudget"/> sizes the inner
    /// fragmentainer's block axis (the atomic pass never paginates, so it only
    /// needs to be large enough not to clip — callers pass the outer
    /// fragmentainer's block size). Both extents are clamped to ≥ 1 (the
    /// <see cref="FragmentainerContext"/> rejects non-positive sizes).
    /// <para><paramref name="intrinsicSizingMode"/> (PR #208 [P2]) — when true the inner
    /// line layout downgrades <c>overflow-wrap: break-word</c>'s soft break opportunities
    /// to Normal per CSS Text L3 §5.1 (they don't reduce min-content), mirroring the table
    /// min-content measure pass; <c>overflow-wrap: anywhere</c> still breaks. Set it for the
    /// flex min-content base-size measure so a `break-word` item isn't collapsed to glyph
    /// width.</para></summary>
    public static BufferingMeasureSink Measure(
        Box box,
        double availInlineContentSize,
        double blockBudget,
        IShaperResolver? shaperResolver,
        WritingMode writingMode,
        bool isRtl,
        CancellationToken cancellationToken,
        IPaginateDiagnosticsSink? diagnostics = null,
        bool intrinsicSizingMode = false)
    {
        // The box is its own content's decoration owner: a content fragment
        // whose box IS this box (the inline-only-root case) paints text only.
        var buffer = new BufferingMeasureSink(decorationOwner: box);
        var innerFragmentainer = new FragmentainerContext(
            contentInlineSize: availInlineContentSize > 0 ? availInlineContentSize : 1,
            blockSize: Math.Max(blockBudget, 1));
        var innerLayout = new LayoutContext(innerFragmentainer)
        {
            WritingMode = writingMode,
            IsRtl = isRtl,
            Diagnostics = diagnostics,
        };
        using var layouter = new BlockLayouter(
            rootBox: box,
            sink: buffer,
            incomingContinuation: null,
            // PR-#182 review P2 — an optional diagnostics sink (the caller passes
            // a BUFFERING one for flex item content so a nested inline / font
            // problem surfaces, flushed only when the item commits). Null = the
            // grid sizing / pre-measure dry-runs (grid's real emission surfaces
            // content diagnostics on its own DispatchGridItemContents pass).
            diagnostics: diagnostics,
            shaperResolver: shaperResolver,
            // A nested paginatable grid / flex inside the box is a CSS
            // Fragmentation "parallel flow"; suppress its pagination here (the
            // content commits atomically) so a discarded continuation doesn't
            // silently lose content (PR-#182 review P1 — flex too, not just grid).
            disableGridPagination: true,
            disableFlexPagination: true,
            // The common item (`<div>text</div>`) has DIRECT inline children;
            // opt the nested layouter into emitting the inline-only ROOT's own
            // content (else the block-only child loop skips the box's text).
            layoutRootInlineContent: true);
        // PR #208 [P2] — intrinsic (min-content) measurement ignores break-word's soft
        // opportunities so they don't collapse min-content to glyph width (mirrors the
        // table cell min-content pass via TableLayouter.MeasureCellContent).
        layouter.SetIntrinsicSizingMode(intrinsicSizingMode);
        using var resolver = new BreakResolver();
        _ = layouter.AttemptLayout(
            innerFragmentainer, ref innerLayout, resolver,
            LayoutAttemptStrategy.LastResort, cancellationToken);
        return buffer;
    }
}
