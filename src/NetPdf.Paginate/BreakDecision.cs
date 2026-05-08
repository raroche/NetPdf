// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Paginate;

/// <summary>
/// What <see cref="IBreakResolver.ConsiderBreakAt"/> tells the calling
/// layouter to do at the current candidate break point. Carries the
/// resolver's <see cref="Cost"/> (computed via <see cref="CostModel"/>)
/// so callers can sort / compare candidates inside their own loops
/// without round-tripping through the resolver again. <see cref="RewindTo"/>
/// is set only when <see cref="Action"/> is <see cref="BreakAction.Rewind"/>.
/// </summary>
/// <param name="Action">Continue / BreakHere / Rewind. See
/// <see cref="BreakAction"/> for semantics.</param>
/// <param name="Cost">Penalty score for this decision. Lower is better;
/// used to compare candidate break sequences inside the bounded DP
/// optimizer.</param>
/// <param name="RewindTo">Checkpoint to roll back to when
/// <see cref="Action"/> is <see cref="BreakAction.Rewind"/>; otherwise
/// <see langword="null"/>. The checkpoint must have been previously
/// registered with <see cref="IBreakResolver.RegisterCheckpoint"/>.</param>
internal readonly record struct BreakDecision(
    BreakAction Action,
    double Cost,
    LayoutCheckpoint? RewindTo)
{
    /// <summary>Convenience: a no-op continue with zero cost (the
    /// stub <see cref="BreakResolver"/> + most "trivial fit" cases
    /// return this).</summary>
    public static BreakDecision Continue { get; } = new(BreakAction.Continue, 0, null);
}
