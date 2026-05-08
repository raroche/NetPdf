// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;

namespace NetPdf.Paginate;

/// <summary>
/// Per Phase 3 Task 4 — the result of a single <see cref="Optimizer.Optimize"/>
/// run over a candidate window. Encodes the chosen break positions (as
/// indices into the input <see cref="BreakOpportunity"/> sequence), the
/// summed cost, and whether the optimizer fell through to its greedy
/// fallback path.
///
/// <para><b>BreakIndices semantics.</b> Each entry <c>k</c> in
/// <see cref="BreakIndices"/> means "commit a fragmentainer break at
/// the candidate <c>opportunities[k]</c>". The sequence is strictly
/// monotonically increasing. The trailing tail past the last index
/// (i.e., <c>opportunities[breakIndices[^1] + 1 ..]</c>) continues on
/// the next page without a committed break — the caller is responsible
/// for either extending the optimization window or accepting that tail
/// as "ongoing layout, not yet bounded".</para>
///
/// <para><b>FellBackToGreedy semantics.</b> When the bounded DP's budget
/// is exceeded (candidate set too large) OR the DP cannot find a
/// feasible break sequence within the lookahead window, the optimizer
/// switches to a greedy "break-when-overflowing" pass. The output
/// indices are still valid; the diagnostic
/// <c>PAGINATION-OPTIMIZER-FALLBACK-001</c> SHOULD be emitted by the
/// caller (typically the integrating layouter; see Phase 3 Task 5+).</para>
///
/// <para><b>Why expose this rather than swallowing it inside
/// <see cref="OptimizingBreakResolver"/>.</b> The integrating layouter
/// owns the public-facing <c>IDiagnosticsSink</c> bridge — Paginate
/// itself doesn't reference <c>NetPdf</c> (the dependency direction
/// runs the other way). Returning the flag in the result keeps the
/// optimizer free of a back-channel diagnostic dependency while
/// preserving the signal the facade needs.</para>
/// </summary>
/// <param name="BreakIndices">Indices into the input opportunity sequence
/// where breaks should fire. Empty when the optimizer determined no
/// breaks are needed within the window.</param>
/// <param name="TotalCost">Sum of <see cref="CostModel.Score"/> across
/// all chosen breaks plus any overflow penalties. Lower is better;
/// callers may compare the cost across optimizer-vs-greedy or across
/// alternative window choices.</param>
/// <param name="FellBackToGreedy">When <see langword="true"/>, the
/// returned indices come from the greedy fallback path, not the DP.
/// Caller should emit <c>PAGINATION-OPTIMIZER-FALLBACK-001</c>.</param>
/// <param name="FallbackReason">Human-readable explanation of why the
/// fallback fired. <see langword="null"/> when
/// <see cref="FellBackToGreedy"/> is <see langword="false"/>.</param>
internal sealed record OptimizerResult(
    IReadOnlyList<int> BreakIndices,
    double TotalCost,
    bool FellBackToGreedy,
    string? FallbackReason)
{
    /// <summary>Singleton "no opportunities, no breaks" result. The
    /// optimizer returns this when called with an empty input;
    /// pre-allocated so the trivial case doesn't allocate.</summary>
    public static OptimizerResult Empty { get; } =
        new(Array.Empty<int>(), 0, FellBackToGreedy: false, FallbackReason: null);
}
