// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Paginate;

/// <summary>
/// A candidate break point produced by a layouter at a position where
/// CSS Fragmentation L3 allows a break (between blocks, between lines,
/// at a table-row boundary, between flex / grid items, etc.).
/// <see cref="IBreakResolver.ConsiderBreakAt"/> consumes a sequence of
/// these + asks the cost model whether breaking there is preferable to
/// continuing.
///
/// <para><b>Block-axis naming.</b> Per Phase 3 review fix #2 + CSS
/// Logical Properties L1, fields are named for the BLOCK axis (the
/// fragmentation axis) rather than for vertical "height". Layouters
/// in <c>vertical-rl</c> / <c>vertical-lr</c> writing modes can use
/// the same opportunity record without renaming.</para>
///
/// <para><b>UsedBlockSize coordinate space.</b> Per Phase 3 Task 4
/// review fix #2 + Copilot review #8 — the meaning depends on which
/// resolver path consumes the opportunity:</para>
/// <list type="bullet">
///   <item><b><see cref="IBreakResolver.ConsiderBreakAt"/> (streaming).</b>
///   Per-fragmentainer cumulative size — resets at each page break.
///   Matches the original <c>height-from-page-top</c> semantics.</item>
///   <item><b><see cref="IBreakResolver.ResolveBreaks"/> (batched).</b>
///   Cumulative-across-window — monotonically non-decreasing so the
///   DP can subtract <c>pageStart</c> to recover per-page measurements.</item>
/// </list>
///
/// <para><b>Forced + avoid metadata.</b> Per Phase 3 review fix #3 +
/// CSS Fragmentation L3 §3.1, <c>break-before</c> / <c>break-after</c>
/// can FORCE a break (<c>page</c>, <c>left</c>, <c>right</c>,
/// <c>recto</c>, <c>verso</c>, <c>column</c>) or PREVENT a break
/// (<c>avoid</c>, <c>avoid-page</c>, <c>avoid-column</c>). The
/// <see cref="ForceBreak"/> + <see cref="AvoidBreak"/> +
/// <see cref="ForceParity"/> fields encode the relevant metadata
/// without expanding to the full enum surface (which would invite
/// unused-state bugs).</para>
/// </summary>
/// <param name="UsedBlockSize">Cumulative block-axis size (CSS px)
/// consumed at the moment this candidate was offered. Coordinate
/// space (per-fragmentainer vs cumulative-across-window) depends on
/// the consumer; see the type-level XML doc.</param>
/// <param name="ChunkBlockSize">Block-axis size (CSS px) of the next
/// chunk that would land if the layouter <see cref="BreakAction.Continue"/>'s
/// past this point. Cost model uses this to detect candidates that
/// would trigger a fragmentainer overflow; per Phase 3 Task 4
/// Copilot reviews #3, #4, #5 a value greater than the fragmentainer
/// extent contributes the <see cref="CostModel.BreakInsideAvoidViolation"/>
/// penalty (the chunk can't fit on any page; surface the overflow
/// in the cost so the layouter emits
/// <c>PAGINATION-FORCED-OVERFLOW-001</c>).</param>
/// <param name="Class">What kind of boundary this is — block / line /
/// table-row / etc. The cost model's penalty matrix keys off this.</param>
/// <param name="ForceBreak">Per CSS Fragmentation L3 §3.1 — set when
/// <c>break-before: page</c> / <c>break-after: page</c> / column /
/// region was specified at this opportunity. The optimizer MUST
/// emit a break here regardless of cost; <see cref="CostModel.Score"/>
/// returns 0 for forced breaks (no penalty since the author chose
/// it).</param>
/// <param name="AvoidBreak">Per CSS Fragmentation L3 §3.2 — set when
/// the candidate falls inside a <c>break-inside: avoid</c> region OR
/// <c>break-before/after: avoid</c> applies. Cost model adds the
/// effectively-infinite <see cref="CostModel.BreakInsideAvoidViolation"/>
/// penalty unless this is the last-resort fallback path.</param>
/// <param name="ForceParity">Per CSS Fragmentation L3 §3.1 +
/// CSS Page L3 — when non-<see cref="PageParity.Any"/>, the break
/// must land on a page matching this parity (<c>left</c>/<c>right</c>
/// or <c>recto</c>/<c>verso</c>). The optimizer may emit one extra
/// blank page (<c>:blank</c> @page-rule) to satisfy parity.</param>
/// <param name="LinesBeforeBreak">Per PR #19 Copilot review #3 —
/// the number of lines emitted on the CURRENT page (before the
/// candidate break) for the paragraph this opportunity belongs to.
/// <see cref="CostModel.Score"/> compares this against the author's
/// <c>orphans</c> property: a value below the requirement triggers
/// the orphan penalty.</param>
/// <param name="StrandsHeading">Per Phase 3 review fix #5 — set when
/// breaking here would leave a heading at the bottom of the page with
/// zero content lines following. Triggers the
/// <see cref="CostModel.StrandedHeading"/> penalty. Layouters set this
/// when they detect a heading-then-content pattern at the candidate.</param>
/// <param name="SplitsFlexOrGridLine">Per Phase 3 review fix #5 — set
/// when breaking here would split a flex line or grid row mid-item.
/// Triggers the <see cref="CostModel.FlexOrGridLineSplit"/> penalty.
/// Distinct from <see cref="AvoidBreak"/> — the line CAN be split
/// (cost paid), versus <c>avoid</c> which is effectively forbidden.</param>
/// <param name="ParagraphId">Per Phase 3 Task 4 review fix #5 —
/// optional paragraph identity for accurate widow scoring. Matches
/// CSS Fragmentation L3 §4.2 + L4 §4.5 — the <c>widows</c> property
/// counts lines AFTER a break in the SAME block container. Layouters
/// set this to a non-zero stable identifier per source paragraph
/// (e.g., the box-tree ID of the inline-formatting context). The
/// optimizer uses it to count "how many subsequent line opportunities
/// share this ParagraphId" for the widow check. Default 0 means "no
/// paragraph identity available"; the optimizer falls back to a
/// heuristic counting consecutive <see cref="BreakOpportunityClass.LineBoundary"/>
/// opportunities until a non-line or different-class boundary
/// appears. Block / table / flex / grid opportunities ignore this
/// field — widows apply to inline formatting only.</param>
internal readonly record struct BreakOpportunity(
    double UsedBlockSize,
    double ChunkBlockSize,
    BreakOpportunityClass Class,
    bool ForceBreak,
    bool AvoidBreak,
    PageParity ForceParity,
    int LinesBeforeBreak,
    bool StrandsHeading,
    bool SplitsFlexOrGridLine,
    int ParagraphId = 0)
{
    /// <summary>Per Phase 3 review fix #8 — geometry inputs must be
    /// finite + non-negative. NaN / negative values silently corrupt
    /// cost comparisons.</summary>
    public void EnsureValid()
    {
        if (!double.IsFinite(UsedBlockSize) || UsedBlockSize < 0)
            throw new ArgumentException(
                $"UsedBlockSize must be finite + non-negative; got {UsedBlockSize}", nameof(UsedBlockSize));
        if (!double.IsFinite(ChunkBlockSize) || ChunkBlockSize < 0)
            throw new ArgumentException(
                $"ChunkBlockSize must be finite + non-negative; got {ChunkBlockSize}", nameof(ChunkBlockSize));
        if (LinesBeforeBreak < 0)
            throw new ArgumentException(
                $"LinesBeforeBreak must be non-negative; got {LinesBeforeBreak}", nameof(LinesBeforeBreak));
        if (ParagraphId < 0)
            throw new ArgumentException(
                $"ParagraphId must be non-negative; got {ParagraphId}", nameof(ParagraphId));
    }

    /// <summary>Convenience helper for a block boundary. The
    /// <paramref name="forceBreak"/> / <paramref name="avoidBreak"/> /
    /// <paramref name="forceParity"/> flags default to the no-special-flags case
    /// (the bulk of opportunities a real layouter offers), so existing callers stay
    /// byte-identical; a layouter that has read the child's CSS Fragmentation L3
    /// <c>break-before</c> / <c>break-after</c> / <c>break-inside</c> passes them.</summary>
    public static BreakOpportunity Block(
        double usedBlockSize, double chunkBlockSize,
        bool forceBreak = false, bool avoidBreak = false,
        PageParity forceParity = PageParity.Any) =>
        new(usedBlockSize, chunkBlockSize, BreakOpportunityClass.BlockBoundary,
            ForceBreak: forceBreak, AvoidBreak: avoidBreak, ForceParity: forceParity,
            LinesBeforeBreak: 0, StrandsHeading: false, SplitsFlexOrGridLine: false,
            ParagraphId: 0);

    /// <summary>Convenience helper for a line boundary inside a
    /// paragraph; carries the line count so the orphan / widow
    /// penalty fires correctly. <paramref name="paragraphId"/> per
    /// Phase 3 Task 4 review fix #5 — optional paragraph identity
    /// for accurate widow scoring (default 0 falls back to the
    /// consecutive-LineBoundary heuristic).</summary>
    public static BreakOpportunity Line(
        double usedBlockSize, double chunkBlockSize, int linesBefore, int paragraphId = 0) =>
        new(usedBlockSize, chunkBlockSize, BreakOpportunityClass.LineBoundary,
            ForceBreak: false, AvoidBreak: false, ForceParity: PageParity.Any,
            LinesBeforeBreak: linesBefore, StrandsHeading: false, SplitsFlexOrGridLine: false,
            ParagraphId: paragraphId);
}

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

/// <summary>
/// Per Phase 3 review fix #3 + CSS Fragmentation L3 §3.1 +
/// CSS Page L3 — page-parity constraints from
/// <c>break-before</c> / <c>break-after</c> values.
/// </summary>
internal enum PageParity
{
    /// <summary>No parity constraint. Default for most opportunities.</summary>
    Any = 0,
    /// <summary><c>break-before: left</c> — break to the physical LEFT page
    /// (CSS Page L3 §3.4.1). PHYSICAL, so its page-number parity swaps with the
    /// page direction: left = verso (even) in LTR, but recto (odd) in RTL.</summary>
    Left = 1,
    /// <summary><c>break-before: right</c> — break to the physical RIGHT page.
    /// PHYSICAL, so its parity swaps with the page direction: right = recto (odd)
    /// in LTR, but verso (even) in RTL.</summary>
    Right = 2,
    /// <summary><c>break-before: recto</c> — break to a recto page: the side the
    /// page progression starts (page 1 is a recto). A direction-INDEPENDENT
    /// page-NUMBER parity (recto = odd); the PHYSICAL side it denotes is the right
    /// page in LTR and the left page in RTL (CSS Fragmentation L3 §3.1).</summary>
    Recto = 3,
    /// <summary><c>break-before: verso</c> — break to a verso page (the second
    /// side of the progression). Direction-INDEPENDENT (verso = even); the PHYSICAL
    /// side is the left page in LTR and the right page in RTL.</summary>
    Verso = 4,
}
