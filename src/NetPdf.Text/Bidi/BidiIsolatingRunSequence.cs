// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Bidi;

/// <summary>
/// An isolating run sequence per UAX #9 §3.3.2 BD13 — a maximal sequence of
/// <see cref="BidiLevelRun"/>s linked through unmatched isolate initiators (LRI/RLI/FSI
/// at the end of one run that have a matching PDI at the start of the next run in the
/// sequence). The W/N/I rules operate on these sequences, not on level runs directly,
/// so isolated text "carries through" intervening RTL/LTR runs as if it were contiguous.
/// </summary>
/// <remarks>
/// <para>
/// <b>sos / eos.</b> Each sequence has a start-of-sequence and end-of-sequence direction
/// (one of <see cref="BidiClass.L"/> or <see cref="BidiClass.R"/>) used as virtual
/// boundary characters by the W/N/I rules:
/// </para>
/// <list type="bullet">
///   <item><c>sos</c> = direction of <c>max(level just before sequence, sequence's level)</c>
///         — even level → L, odd level → R.</item>
///   <item><c>eos</c> = direction of <c>max(level just after sequence, sequence's level)</c>
///         (or the paragraph-level continuation when the sequence ends with an unmatched
///         isolate initiator).</item>
/// </list>
/// </remarks>
internal sealed class BidiIsolatingRunSequence
{
    /// <summary>The <see cref="BidiLevelRun"/>s that compose this sequence, in source order.</summary>
    public required BidiLevelRun[] Runs { get; init; }

    /// <summary>The shared embedding level (every level run in the sequence has this level).</summary>
    public required byte Level { get; init; }

    /// <summary>Start-of-sequence direction — virtual boundary character preceding the first run.</summary>
    public required BidiClass Sos { get; init; }

    /// <summary>End-of-sequence direction — virtual boundary character following the last run.</summary>
    public required BidiClass Eos { get; init; }

    /// <summary>The flat list of character indices (across all runs) in source order.</summary>
    public IEnumerable<int> Indices
    {
        get
        {
            foreach (var run in Runs)
            {
                foreach (var idx in run.Indices)
                {
                    yield return idx;
                }
            }
        }
    }
}
