// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Paginate;

/// <summary>
/// What the layouter should do at a candidate break point. Returned in
/// <see cref="BreakDecision"/> by <see cref="IBreakResolver.ConsiderBreakAt"/>.
/// </summary>
internal enum BreakAction
{
    /// <summary>Keep going. The current position is fine; no fragment is
    /// emitted. Most call sites get this — the optimizer only inserts a
    /// break when the cost model justifies it.</summary>
    Continue = 0,

    /// <summary>Emit the current fragment + start a new fragmentainer
    /// (page). The layouter writes its accumulated content to the active
    /// page, allocates the next page, and resumes layout from the
    /// continuation token.</summary>
    BreakHere = 1,

    /// <summary>Roll back to the checkpoint named in
    /// <see cref="BreakDecision.RewindTo"/> and try a different break
    /// strategy. The optimizer's bounded retry loop uses this when a
    /// break-inside-avoid block can't fit; on the second rewind the
    /// re-layout loop emits PAGINATION-FORCED-OVERFLOW-001 + commits the
    /// best-cost result anyway.</summary>
    Rewind = 2,
}
