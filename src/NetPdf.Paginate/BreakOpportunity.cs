// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Paginate;

/// <summary>
/// A candidate break point produced by a layouter at a position where
/// CSS Fragmentation L3 allows a break (between blocks, between lines,
/// at a table-row boundary, between flex / grid items, etc.).
/// <see cref="IBreakResolver.ConsiderBreakAt"/> consumes a sequence of
/// these + asks the cost model whether breaking there is preferable to
/// continuing.
/// </summary>
/// <param name="UsedHeight">Cumulative height (CSS px) consumed in the
/// current fragmentainer at the moment this candidate was offered.
/// Compared against <see cref="FragmentainerContext.RemainingHeight"/>
/// to decide whether the next chunk fits.</param>
/// <param name="ChunkHeight">Height (CSS px) of the next chunk that
/// would land if the layouter <see cref="BreakAction.Continue"/>'s past
/// this point. The cost model uses this to detect candidates that would
/// trigger an overflow (chunk &gt; remaining height).</param>
/// <param name="Class">What kind of boundary this is — block / line /
/// table-row / etc. The cost model's penalty matrix keys off this.</param>
/// <param name="ForbidsBreak">When <see langword="true"/>, the boundary
/// is inside a <c>break-inside: avoid</c> region. The cost model adds
/// the +∞ "do-not-break" penalty unless this is the last-resort
/// fallback path.</param>
/// <param name="WidowOrphanLineCount">For inline-block contexts, the
/// number of lines the chunk occupies. Drives widows / orphans
/// calculation.</param>
internal readonly record struct BreakOpportunity(
    double UsedHeight,
    double ChunkHeight,
    BreakOpportunityClass Class,
    bool ForbidsBreak,
    int WidowOrphanLineCount);

/// <summary>
/// Categorization for <see cref="BreakOpportunity"/>; selects which
/// penalty in <see cref="CostModel"/> applies.
/// </summary>
internal enum BreakOpportunityClass
{
    /// <summary>Between two block-level siblings (CSS 2.2 §13.3).</summary>
    BlockBoundary = 0,
    /// <summary>Between two inline-formatted lines (CSS 2.2 §13.3.5).</summary>
    LineBoundary = 1,
    /// <summary>Between rows of a table.</summary>
    TableRowBoundary = 2,
    /// <summary>Inside a single table row (between cells of a wide row).
    /// Cost model's "split row mid-cell" penalty applies.</summary>
    InsideTableRow = 3,
    /// <summary>Between flex items / lines in a flex container.</summary>
    FlexBoundary = 4,
    /// <summary>Between grid items / lines in a grid container.</summary>
    GridBoundary = 5,
    /// <summary>A break ALLOWED by CSS but at a "section boundary"
    /// (heading + content) — usually rewarded with a negative penalty so
    /// the optimizer prefers breaking here over arbitrary positions.</summary>
    SectionBoundary = 6,
}
