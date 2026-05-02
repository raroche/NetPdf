// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Bidi.Rules;

/// <summary>
/// UAX #9 §3.3.5 rules N1 and N2 — neutral type resolution. Operates on a single
/// <see cref="BidiIsolatingRunSequence"/> after the W rules and N0 paired-bracket
/// resolution have run.
/// </summary>
/// <remarks>
/// <para>
/// <b>NIs.</b> "Neutral or Isolate Formatting" types per BD17 — B, S, WS, ON, FSI, LRI,
/// RLI, PDI. (N0 may have already converted some ON-classified brackets to L or R; those
/// no longer count as NIs.)
/// </para>
/// <para>
/// <b>N1.</b> A run of NIs takes the direction of the surrounding strong types if both
/// sides agree. EN and AN behave as R for these comparisons. sos/eos act as bordering
/// strong types at sequence boundaries.
/// </para>
/// <para>
/// <b>N2.</b> Any remaining NIs take the embedding direction (L for even level, R for odd).
/// </para>
/// </remarks>
internal static class BidiN12NeutralResolver
{
    public static void Apply(Span<BidiCharInfo> chars, BidiIsolatingRunSequence sequence)
    {
        ApplyN1(chars, sequence);
        ApplyN2(chars, sequence);
    }

    /// <summary>N1 — runs of NIs take the surrounding direction when both sides agree.</summary>
    private static void ApplyN1(Span<BidiCharInfo> chars, BidiIsolatingRunSequence sequence)
    {
        var indices = sequence.FlatIndices;
        var i = 0;
        while (i < indices.Length)
        {
            if (!IsNi(chars[indices[i]].ResolvedClass))
            {
                i++;
                continue;
            }
            // Find the run of NIs.
            var runStart = i;
            while (i < indices.Length && IsNi(chars[indices[i]].ResolvedClass))
            {
                i++;
            }
            var runEndExclusive = i;

            // Bordering strong types (with EN/AN as R).
            var leftStrong = runStart > 0
                ? AsStrong(chars[indices[runStart - 1]].ResolvedClass)
                : sequence.Sos;
            var rightStrong = runEndExclusive < indices.Length
                ? AsStrong(chars[indices[runEndExclusive]].ResolvedClass)
                : sequence.Eos;

            if (leftStrong == rightStrong && leftStrong is BidiClass.L or BidiClass.R)
            {
                for (var j = runStart; j < runEndExclusive; j++)
                {
                    chars[indices[j]].ResolvedClass = leftStrong;
                }
            }
        }
    }

    /// <summary>N2 — any remaining NIs take the sequence's embedding direction.</summary>
    private static void ApplyN2(Span<BidiCharInfo> chars, BidiIsolatingRunSequence sequence)
    {
        var indices = sequence.FlatIndices;
        var embedding = (sequence.Level & 1) == 0 ? BidiClass.L : BidiClass.R;
        for (var i = 0; i < indices.Length; i++)
        {
            ref var ch = ref chars[indices[i]];
            if (IsNi(ch.ResolvedClass))
            {
                ch.ResolvedClass = embedding;
            }
        }
    }

    private static bool IsNi(BidiClass cls) =>
        cls is BidiClass.B or BidiClass.S or BidiClass.WS or BidiClass.ON
            or BidiClass.FSI or BidiClass.LRI or BidiClass.RLI or BidiClass.PDI;

    private static BidiClass AsStrong(BidiClass cls) => cls switch
    {
        BidiClass.L => BidiClass.L,
        BidiClass.R => BidiClass.R,
        BidiClass.EN => BidiClass.R,
        BidiClass.AN => BidiClass.R,
        _ => BidiClass.ON,
    };
}
