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
/// consumed in the current fragmentainer at the moment this candidate
/// was offered. Per Phase 3 review fix #4, the cost model scores
/// against this snapshot value (not the live FragmentainerContext)
/// so the optimizer can evaluate historical candidates after the
/// layouter has moved past them.</param>
/// <param name="ChunkBlockSize">Block-axis size (CSS px) of the next
/// chunk that would land if the layouter <see cref="BreakAction.Continue"/>'s
/// past this point. Cost model uses this to detect candidates that
/// would trigger a fragmentainer overflow.</param>
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
/// the orphan penalty. The widows penalty uses the
/// <c>lineCountAfterBreak</c> argument the resolver computes
/// separately. Pre-fix this was named <c>WidowOrphanLineCount</c>
/// with a docstring claiming it was "lines the chunk occupies",
/// but the cost model + the <see cref="Line"/> helper both treated
/// it as lines-before-the-break — the ambiguous name encouraged
/// future layouters to wire it wrong.</param>
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
internal readonly record struct BreakOpportunity(
    double UsedBlockSize,
    double ChunkBlockSize,
    BreakOpportunityClass Class,
    bool ForceBreak,
    bool AvoidBreak,
    PageParity ForceParity,
    int LinesBeforeBreak,
    bool StrandsHeading,
    bool SplitsFlexOrGridLine)
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
    }

    /// <summary>Convenience helper for the common case of a
    /// no-special-flags block boundary (the bulk of opportunities a
    /// real layouter offers).</summary>
    public static BreakOpportunity Block(double usedBlockSize, double chunkBlockSize) =>
        new(usedBlockSize, chunkBlockSize, BreakOpportunityClass.BlockBoundary,
            ForceBreak: false, AvoidBreak: false, ForceParity: PageParity.Any,
            LinesBeforeBreak: 0, StrandsHeading: false, SplitsFlexOrGridLine: false);

    /// <summary>Convenience helper for a line boundary inside a
    /// paragraph; carries the line count so the orphan / widow
    /// penalty fires correctly.</summary>
    public static BreakOpportunity Line(double usedBlockSize, double chunkBlockSize, int linesBefore) =>
        new(usedBlockSize, chunkBlockSize, BreakOpportunityClass.LineBoundary,
            ForceBreak: false, AvoidBreak: false, ForceParity: PageParity.Any,
            LinesBeforeBreak: linesBefore, StrandsHeading: false, SplitsFlexOrGridLine: false);
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
    /// <summary><c>break-before: left</c> — break to a left page (verso
    /// in LTR, recto in RTL).</summary>
    Left = 1,
    /// <summary><c>break-before: right</c> — break to a right page (recto
    /// in LTR, verso in RTL).</summary>
    Right = 2,
    /// <summary><c>break-before: recto</c> — break to a right-hand page
    /// regardless of writing direction (per CSS Page L3 §3.4.1).</summary>
    Recto = 3,
    /// <summary><c>break-before: verso</c> — break to a left-hand page
    /// regardless of writing direction.</summary>
    Verso = 4,
}
