// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

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
/// pegs:
/// <list type="bullet">
///   <item><c>break-inside: avoid</c> violation = 1_000_000 (effectively
///   +∞ — the optimizer only picks this when there's no alternative,
///   the last-resort fallback path).</item>
///   <item>Heading stranded at page bottom = 1000.</item>
///   <item>Orphan / widow = 500 each.</item>
///   <item>Splitting a flex / grid line = 400.</item>
///   <item>Splitting a table row mid-cell = 300.</item>
///   <item>&gt; 30% trailing blank space on a page = 200.</item>
///   <item>Section-boundary break (heading + content) = -100 (reward).</item>
/// </list>
/// </para>
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
    /// (ContentAreaHeight - UsedHeight) / ContentAreaHeight.</summary>
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
    /// with no widow/orphan + no break-inside-avoid).</summary>
    /// <param name="opportunity">The candidate break's classification +
    /// metadata.</param>
    /// <param name="usedHeight">Height (CSS px) committed on the
    /// current page.</param>
    /// <param name="contentAreaHeight">Total content area height
    /// (CSS px) on the current page.</param>
    /// <param name="orphansRequired">Author's <c>orphans</c> property,
    /// usually 2.</param>
    /// <param name="widowsRequired">Author's <c>widows</c> property,
    /// usually 2.</param>
    /// <param name="lineCountAfterBreak">Lines emitted on the next
    /// page from the same paragraph; drives the widow penalty.</param>
    public static double Score(
        BreakOpportunity opportunity,
        double usedHeight,
        double contentAreaHeight,
        int orphansRequired,
        int widowsRequired,
        int lineCountAfterBreak)
    {
        double cost = 0;

        if (opportunity.ForbidsBreak)
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
            case BreakOpportunityClass.FlexBoundary:
            case BreakOpportunityClass.GridBoundary:
                // Boundary itself is fine; mid-line split is the
                // expensive case + the layouter signals that via
                // ForbidsBreak above (the flex / grid layouter sets
                // ForbidsBreak when the candidate would split a line
                // mid-item).
                break;
            case BreakOpportunityClass.LineBoundary:
                if (opportunity.WidowOrphanLineCount < orphansRequired) cost += Orphan;
                if (lineCountAfterBreak < widowsRequired) cost += Widow;
                break;
            case BreakOpportunityClass.BlockBoundary:
            case BreakOpportunityClass.TableRowBoundary:
                // Block + row boundaries have no inherent penalty.
                break;
        }

        // Large trailing blank — applies regardless of class. Costly
        // when the page is mostly empty + the chunk would have fit on
        // the same page anyway.
        var blankRatio = (contentAreaHeight - usedHeight) / contentAreaHeight;
        if (blankRatio > LargeBlankTrailingThreshold)
        {
            cost += LargeBlankTrailingArea;
        }

        return cost;
    }
}
