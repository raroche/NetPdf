// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Paginate;

/// <summary>
/// Per Phase 3 plan §"Cost model defaults" — penalty assignment for
/// candidate break points. The bounded DP optimizer minimizes total
/// cost across a candidate sequence; lower is better.
///
/// <para><b>Constants only, not configurable.</b> The plan deliberately
/// pins these as compile-time constants tuned against the rendering
/// corpus rather than exposing a knob — the cost model is part of the
/// reproducibility contract (deterministic output requires deterministic
/// costs). A future <c>HtmlPdfOptions.Features</c> escape hatch may
/// allow override; not in v1.</para>
///
/// <para><b>Penalty scale.</b> Costs are unitless. The plan's table
/// pegs the values used as constants below.</para>
/// </summary>
internal static class CostModel
{
    /// <summary>Cost added when a candidate break would split a
    /// <c>break-inside: avoid</c> region. Effectively +∞; the optimizer
    /// only picks this when every alternative also has the penalty
    /// (the bounded retry loop's last-resort phase).</summary>
    public const double BreakInsideAvoidViolation = 1_000_000;

    /// <summary>Cost when a heading lands at the bottom of a page with
    /// zero content lines following (the heading is "stranded"). Per
    /// CSS 2.2 §13.3.4 + WCAG visual layout guidance — common pagination
    /// failure mode.</summary>
    public const double StrandedHeading = 1_000;

    /// <summary>Cost for an orphan: 1 line at the BOTTOM of a page
    /// while the <c>orphans</c> property requires 2+. Per CSS Fragmentation
    /// L3 §4.2.</summary>
    public const double Orphan = 500;

    /// <summary>Cost for a widow: 1 line at the TOP of a page while
    /// the <c>widows</c> property requires 2+. Per CSS Fragmentation
    /// L3 §4.2.</summary>
    public const double Widow = 500;

    /// <summary>Cost for splitting a flex line or grid line across a
    /// page break. Per Phase 3 plan — these are typically intended as
    /// atomic units; mid-line breaks are visually jarring.</summary>
    public const double FlexOrGridLineSplit = 400;

    /// <summary>Cost for splitting a table row across a page break
    /// when the row's cells couldn't all fit on one page. Per CSS
    /// Tables L3 §6.2.</summary>
    public const double TableRowMidCellSplit = 300;

    /// <summary>Cost when a page commits with &gt;
    /// <see cref="LargeBlankTrailingThreshold"/> of its content area
    /// blank at the bottom. Encourages denser packing when the
    /// document allows.</summary>
    public const double LargeBlankTrailingArea = 200;

    /// <summary>Threshold for triggering
    /// <see cref="LargeBlankTrailingArea"/>. 0.30 means &gt; 30% blank
    /// trailing area earns the penalty. CSS px-based: blank ratio =
    /// (BlockSize - usedBlockSize) / BlockSize.</summary>
    public const double LargeBlankTrailingThreshold = 0.30;

    /// <summary>Reward (NEGATIVE cost) for breaking at a section
    /// boundary — between a heading + the prose that follows. Encourages
    /// the optimizer to prefer page breaks at natural document seams
    /// over arbitrary positions.</summary>
    public const double SectionBoundaryReward = -100;

    /// <summary>Compute the penalty for a single candidate break.
    /// Combines the structural penalty (heading-stranding /
    /// orphan-widow / etc.) with the row/line-split + trailing-blank
    /// penalties. Returns 0 for the trivial-fit case (block boundary
    /// with no widow/orphan + no break-inside-avoid).
    ///
    /// <para>Per Phase 3 review fix #4 — scoring uses the
    /// opportunity's <see cref="BreakOpportunity.UsedBlockSize"/>
    /// snapshot, NOT a separate "current used size" parameter. This
    /// lets the bounded DP optimizer score historical candidates
    /// after the layouter has moved past them.</para>
    ///
    /// <para>Per Phase 3 review fix #5 — wires the previously-dead
    /// <see cref="StrandedHeading"/> + <see cref="FlexOrGridLineSplit"/>
    /// penalties via the <see cref="BreakOpportunity.StrandsHeading"/>
    /// + <see cref="BreakOpportunity.SplitsFlexOrGridLine"/>
    /// metadata flags.</para>
    ///
    /// <para>Per Phase 3 review fix #8 — geometry inputs are validated
    /// before any cost arithmetic. NaN / non-positive content area
    /// silently corrupts the optimizer's candidate ranking.</para>
    ///
    /// <para>Per Phase 3 Task 4 review fix #2 + Copilot reviews #1
    /// + #8 — <paramref name="pageStart"/> parameter (default 0) is
    /// the cumulative position at which the current page begins.
    /// In the streaming
    /// <see cref="IBreakResolver.ConsiderBreakAt"/> path
    /// <see cref="BreakOpportunity.UsedBlockSize"/> is already
    /// per-fragmentainer (resets at page break) so callers leave
    /// <paramref name="pageStart"/> at 0. In the batched
    /// <see cref="IBreakResolver.ResolveBreaks"/> path, the optimizer
    /// passes the cumulative position of the most recent committed
    /// break, so per-page measurements (used for the trailing-blank
    /// ratio + the chunk-overflow check) recover correctly. Without
    /// this parameter, batched scoring on pages 2+ would compute a
    /// negative blank ratio (the cumulative <see cref="BreakOpportunity.UsedBlockSize"/>
    /// would exceed <paramref name="contentBlockSize"/>) + miss the
    /// trailing-blank penalty entirely.</para>
    ///
    /// <para>Per Phase 3 Task 4 Copilot reviews #3, #4, #5 — when
    /// <see cref="BreakOpportunity.ChunkBlockSize"/> exceeds
    /// <paramref name="contentBlockSize"/> the cost picks up a
    /// <see cref="BreakInsideAvoidViolation"/> penalty regardless of
    /// the chosen break position. Such a chunk cannot fit on any
    /// fragmentainer; the DP can't avoid the overflow by shifting
    /// breaks. Surfacing the penalty in the cost lets the layouter
    /// emit <c>PAGINATION-FORCED-OVERFLOW-001</c> when the chosen
    /// break sequence accumulates a cost above the threshold.</para>
    /// </summary>
    /// <param name="opportunity">The candidate break's classification +
    /// metadata.</param>
    /// <param name="contentBlockSize">The fragmentainer's block-axis
    /// extent (CSS px). Must be finite + positive.</param>
    /// <param name="orphansRequired">Author's <c>orphans</c> property,
    /// usually 2.</param>
    /// <param name="widowsRequired">Author's <c>widows</c> property,
    /// usually 2.</param>
    /// <param name="lineCountAfterBreak">Lines emitted on the next
    /// page from the same paragraph; drives the widow penalty.</param>
    /// <param name="pageStart">Cumulative position (CSS px) at which
    /// the current page begins. Defaults to 0 for the streaming /
    /// per-fragmentainer caller (see XML doc).</param>
    public static double Score(
        BreakOpportunity opportunity,
        double contentBlockSize,
        int orphansRequired,
        int widowsRequired,
        int lineCountAfterBreak,
        double pageStart = 0)
    {
        // Per Phase 3 review fix #8 — guard against NaN / non-positive
        // geometry before any arithmetic.
        opportunity.EnsureValid();
        if (!double.IsFinite(contentBlockSize) || contentBlockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(contentBlockSize),
                $"contentBlockSize must be finite + positive; got {contentBlockSize}");
        if (orphansRequired < 0)
            throw new ArgumentOutOfRangeException(nameof(orphansRequired));
        if (widowsRequired < 0)
            throw new ArgumentOutOfRangeException(nameof(widowsRequired));
        if (lineCountAfterBreak < 0)
            throw new ArgumentOutOfRangeException(nameof(lineCountAfterBreak));
        // Per Phase 3 Task 4 review fix #2 — pageStart must be finite +
        // non-negative + not in the future of the opportunity's
        // UsedBlockSize. (A pageStart past the opportunity would imply
        // we're scoring an opportunity from a previous page, which is
        // a caller bug.)
        if (!double.IsFinite(pageStart) || pageStart < 0)
            throw new ArgumentOutOfRangeException(nameof(pageStart),
                $"pageStart must be finite + non-negative; got {pageStart}");
        if (pageStart > opportunity.UsedBlockSize)
            throw new ArgumentOutOfRangeException(nameof(pageStart),
                $"pageStart ({pageStart}) cannot exceed opportunity.UsedBlockSize ({opportunity.UsedBlockSize})");

        // Per Phase 3 review fix #3 — author-forced breaks zero out
        // the cost. The optimizer MUST emit them regardless of
        // structural penalty (the author chose the break point).
        if (opportunity.ForceBreak) return 0;

        double cost = 0;

        if (opportunity.AvoidBreak)
        {
            cost += BreakInsideAvoidViolation;
        }

        // Per Phase 3 Task 4 Copilot reviews #3, #4, #5 — chunks taller
        // than the fragmentainer can't fit on any page. The DP can't
        // avoid the overflow by choosing a different break; signal it
        // via the cost so the integrating layouter emits
        // PAGINATION-FORCED-OVERFLOW-001.
        if (opportunity.ChunkBlockSize > contentBlockSize)
        {
            cost += BreakInsideAvoidViolation;
        }

        switch (opportunity.Class)
        {
            case BreakOpportunityClass.SectionBoundary:
                cost += SectionBoundaryReward;
                break;
            case BreakOpportunityClass.InsideTableRow:
                cost += TableRowMidCellSplit;
                break;
            case BreakOpportunityClass.LineBoundary:
                if (opportunity.LinesBeforeBreak < orphansRequired) cost += Orphan;
                if (lineCountAfterBreak < widowsRequired) cost += Widow;
                break;
            case BreakOpportunityClass.BlockBoundary:
            case BreakOpportunityClass.TableRowBoundary:
            case BreakOpportunityClass.FlexBoundary:
            case BreakOpportunityClass.GridBoundary:
                // Boundary itself is fine; the layouter signals the
                // expensive cases (mid-line / heading-stranding) via
                // the metadata flags below.
                break;
        }

        // Per Phase 3 review fix #5 — apply the previously-dead
        // StrandedHeading + FlexOrGridLineSplit penalties when the
        // opportunity carries the corresponding metadata flag.
        if (opportunity.StrandsHeading)
        {
            cost += StrandedHeading;
        }
        if (opportunity.SplitsFlexOrGridLine)
        {
            cost += FlexOrGridLineSplit;
        }

        // Per Phase 3 Task 4 review fix #2 + Copilot reviews #1, #8 —
        // page-local trailing-blank ratio. Compute against
        // (UsedBlockSize - pageStart) so the optimizer's batched mode
        // (where UsedBlockSize is cumulative-across-window) gets
        // correct per-page measurements on pages 2+.
        var pageLocalUsed = opportunity.UsedBlockSize - pageStart;
        var blankRatio = (contentBlockSize - pageLocalUsed) / contentBlockSize;
        if (blankRatio > LargeBlankTrailingThreshold)
        {
            cost += LargeBlankTrailingArea;
        }

        return cost;
    }
}
