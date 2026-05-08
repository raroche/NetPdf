// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Layout.Layouters;

/// <summary>
/// Per Phase 3 Task 7 cycle 2 + CSS 2.1 §8.3.1 — collapsing of
/// adjacent vertical margins. When two or more in-flow block-level
/// margins meet (the bottom margin of one block + the top margin of
/// the next), they collapse to a SINGLE margin per the formula:
///
/// <para><b>collapsed = max(positive margins) - max(absolute values
/// of negative margins)</b></para>
///
/// <para>From the spec: "When two or more margins collapse, the
/// resulting margin width is the maximum of the collapsing margins'
/// widths. In the case of negative margins, the maximum of the
/// absolute values of the negative adjoining margins is deducted
/// from the maximum of the positive adjoining margins."</para>
///
/// <para><b>Cycle 2 scope.</b> Cycle 2 implements adjacent SIBLING
/// collapse only — block N's bottom margin + block N+1's top margin
/// collapse when both are in-flow children of the same block formatting
/// context. The cycle-2 simplification:</para>
/// <list type="bullet">
///   <item><b>Parent/first-child + parent/last-child collapse</b> —
///   per §8.3.1 a parent's top margin can collapse with its first
///   in-flow child's top margin (when no padding/border separates
///   them). Deferred to Phase 3 Task 7 cycle 3 (requires recursive
///   layout + BFC detection).</item>
///   <item><b>BFC root suppression</b> — boxes that establish a new
///   BFC (<c>display: flow-root</c>; <c>overflow</c> other than
///   <c>visible</c>; floats; absolutely-positioned; flex / grid items)
///   do NOT collapse margins with their children. Deferred to
///   cycle 3 (requires BFC detection).</item>
///   <item><b>Cross-page collapse</b> — per CSS Fragmentation L3 §6.1,
///   margins that meet at a fragmentainer boundary do NOT collapse.
///   Cycle 2 handles this implicitly: the layouter resets the
///   collapse chain at each page break (via the constructor receiving
///   a fresh continuation), so block N+1's top margin on a new page
///   is honored without collapse with the prior page's bottom margin.</item>
///   <item><b>Empty box collapse</b> — per §8.3.1 a box with no
///   border/padding/inline content/height collapses its top + bottom
///   margins. Cycle 2 doesn't synthesize empty-box collapse; layouters
///   that emit fragments for empty boxes get the standard
///   adjacent-sibling collapse via cycle-2's path.</item>
/// </list>
///
/// <para><b>Why a separate helper.</b> The collapse formula is small
/// enough to inline at the call site, but factoring it out as
/// <see cref="Collapse"/> makes the math testable in isolation +
/// gives the BlockLayouter / FlexLayouter / GridLayouter / TableLayouter
/// a single shared implementation. The 4 layouters that handle
/// margin collapse all share this helper.</para>
/// </summary>
internal static class MarginCollapse
{
    /// <summary>Collapse two adjacent vertical margins per CSS 2.1
    /// §8.3.1. Returns the single collapsed margin value that
    /// REPLACES the sum of the two inputs.
    ///
    /// <para>Algorithm: take the maximum of the positive margins,
    /// subtract the maximum of the absolute values of the negative
    /// margins. Equivalent (and computationally simpler) to
    /// <c>max(0, max(m1, m2)) - max(0, max(-m1, -m2))</c>.</para>
    ///
    /// <para>Examples:</para>
    /// <list type="bullet">
    ///   <item><c>Collapse(10, 20) = 20</c> — both positive, max wins.</item>
    ///   <item><c>Collapse(10, 5) = 10</c> — both positive, max wins.</item>
    ///   <item><c>Collapse(10, -5) = 5</c> — mixed; +10 -5 = 5.</item>
    ///   <item><c>Collapse(20, -10) = 10</c> — mixed; +20 -10 = 10.</item>
    ///   <item><c>Collapse(-10, -20) = -20</c> — both negative, MOST
    ///   negative wins (= max of absolute values, with the sign
    ///   negated).</item>
    ///   <item><c>Collapse(0, 15) = 15</c> — zero is treated as a
    ///   positive 0 (no negative contribution).</item>
    /// </list>
    ///
    /// <para>Commutative: <c>Collapse(a, b) == Collapse(b, a)</c>.
    /// Pure function — no state.</para>
    /// </summary>
    /// <param name="m1">First margin (CSS px). Sign-significant.</param>
    /// <param name="m2">Second margin (CSS px). Sign-significant.</param>
    public static double Collapse(double m1, double m2)
    {
        // max of positive margins (clamped to 0 for non-positive inputs)
        var maxPos = Math.Max(0, Math.Max(m1, m2));
        // max of absolute values of negative margins (clamped to 0)
        var maxAbsNeg = Math.Max(0, Math.Max(-m1, -m2));
        return maxPos - maxAbsNeg;
    }
}
