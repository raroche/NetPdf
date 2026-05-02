// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Bidi.Rules;

/// <summary>
/// UAX #9 §3.3.3 weak resolution rules W1–W7. Operates on a single
/// <see cref="BidiIsolatingRunSequence"/>, mutating <see cref="BidiCharInfo.ResolvedClass"/>
/// of every character in the sequence's <see cref="BidiIsolatingRunSequence.FlatIndices"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Spec basis (clean-room).</b> Unicode UAX #9 §3.3.3, rules W1 through W7. No code
/// transliterated from any third-party implementation; per-rule branches reference the
/// exact rule number and the spec text the branch implements.
/// </para>
/// <para>
/// <b>sos / eos.</b> Each rule treats the sequence's <see cref="BidiIsolatingRunSequence.Sos"/>
/// as a virtual character preceding the first sequence character and
/// <see cref="BidiIsolatingRunSequence.Eos"/> as one following the last. Rules that look
/// for "the first strong type" or "the previous character" treat sos/eos as those types
/// when the search reaches a sequence boundary.
/// </para>
/// <para>
/// <b>X9-removed characters.</b> The flat indices already exclude X9-removed characters
/// (RLE/LRE/RLO/LRO/PDF/BN), so the W rules never see them.
/// </para>
/// </remarks>
internal static class BidiW7Resolver
{
    public static void Apply(Span<BidiCharInfo> chars, BidiIsolatingRunSequence sequence)
    {
        ApplyW1(chars, sequence);
        ApplyW2(chars, sequence);
        ApplyW3(chars, sequence);
        ApplyW4(chars, sequence);
        ApplyW5(chars, sequence);
        ApplyW6(chars, sequence);
        ApplyW7(chars, sequence);
    }

    /// <summary>
    /// W1 — Examine each NSM in the sequence. If the previous character is an isolate
    /// initiator (LRI/RLI/FSI) or PDI, change the NSM to ON. Otherwise set its type to
    /// the previous character's type. NSM at the start of the sequence gets the sos type.
    /// </summary>
    private static void ApplyW1(Span<BidiCharInfo> chars, BidiIsolatingRunSequence sequence)
    {
        var indices = sequence.FlatIndices;
        var prevClass = sequence.Sos;
        for (var i = 0; i < indices.Length; i++)
        {
            ref var ch = ref chars[indices[i]];
            if (ch.ResolvedClass == BidiClass.NSM)
            {
                ch.ResolvedClass = prevClass is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI
                    ? BidiClass.ON
                    : prevClass;
            }
            prevClass = ch.ResolvedClass;
        }
    }

    /// <summary>
    /// W2 — Search backward from each EN until the first strong type (R, L, AL, sos).
    /// If an AL is found, change the EN to AN.
    /// </summary>
    private static void ApplyW2(Span<BidiCharInfo> chars, BidiIsolatingRunSequence sequence)
    {
        var indices = sequence.FlatIndices;
        for (var i = 0; i < indices.Length; i++)
        {
            ref var ch = ref chars[indices[i]];
            if (ch.ResolvedClass != BidiClass.EN)
            {
                continue;
            }
            // Walk backward to find the first strong type.
            BidiClass strong = sequence.Sos;
            for (var j = i - 1; j >= 0; j--)
            {
                var c = chars[indices[j]].ResolvedClass;
                if (c is BidiClass.R or BidiClass.L or BidiClass.AL)
                {
                    strong = c;
                    break;
                }
            }
            if (strong == BidiClass.AL)
            {
                ch.ResolvedClass = BidiClass.AN;
            }
        }
    }

    /// <summary>W3 — Change all ALs to R.</summary>
    private static void ApplyW3(Span<BidiCharInfo> chars, BidiIsolatingRunSequence sequence)
    {
        var indices = sequence.FlatIndices;
        for (var i = 0; i < indices.Length; i++)
        {
            ref var ch = ref chars[indices[i]];
            if (ch.ResolvedClass == BidiClass.AL)
            {
                ch.ResolvedClass = BidiClass.R;
            }
        }
    }

    /// <summary>
    /// W4 — A single ES between two ENs becomes EN. A single CS between two numbers of
    /// the same type (both EN or both AN) becomes that type.
    /// </summary>
    private static void ApplyW4(Span<BidiCharInfo> chars, BidiIsolatingRunSequence sequence)
    {
        var indices = sequence.FlatIndices;
        for (var i = 1; i < indices.Length - 1; i++)
        {
            ref var ch = ref chars[indices[i]];
            var cls = ch.ResolvedClass;
            if (cls != BidiClass.ES && cls != BidiClass.CS)
            {
                continue;
            }
            var prev = chars[indices[i - 1]].ResolvedClass;
            var next = chars[indices[i + 1]].ResolvedClass;
            if (cls == BidiClass.ES && prev == BidiClass.EN && next == BidiClass.EN)
            {
                ch.ResolvedClass = BidiClass.EN;
            }
            else if (cls == BidiClass.CS)
            {
                if (prev == BidiClass.EN && next == BidiClass.EN)
                {
                    ch.ResolvedClass = BidiClass.EN;
                }
                else if (prev == BidiClass.AN && next == BidiClass.AN)
                {
                    ch.ResolvedClass = BidiClass.AN;
                }
            }
        }
    }

    /// <summary>
    /// W5 — A sequence of ETs adjacent to an EN (on either side) takes the EN type. Walks
    /// runs of ET; if either end abuts an EN, the whole run becomes EN.
    /// </summary>
    private static void ApplyW5(Span<BidiCharInfo> chars, BidiIsolatingRunSequence sequence)
    {
        var indices = sequence.FlatIndices;
        var i = 0;
        while (i < indices.Length)
        {
            if (chars[indices[i]].ResolvedClass != BidiClass.ET)
            {
                i++;
                continue;
            }
            // Find the run of ETs.
            var runStart = i;
            while (i < indices.Length && chars[indices[i]].ResolvedClass == BidiClass.ET)
            {
                i++;
            }
            var runEndExclusive = i;
            var leftIsEn = runStart > 0 && chars[indices[runStart - 1]].ResolvedClass == BidiClass.EN;
            var rightIsEn = runEndExclusive < indices.Length && chars[indices[runEndExclusive]].ResolvedClass == BidiClass.EN;
            if (leftIsEn || rightIsEn)
            {
                for (var j = runStart; j < runEndExclusive; j++)
                {
                    chars[indices[j]].ResolvedClass = BidiClass.EN;
                }
            }
        }
    }

    /// <summary>W6 — Otherwise, separators (ES, CS) and terminators (ET) become ON.</summary>
    private static void ApplyW6(Span<BidiCharInfo> chars, BidiIsolatingRunSequence sequence)
    {
        var indices = sequence.FlatIndices;
        for (var i = 0; i < indices.Length; i++)
        {
            ref var ch = ref chars[indices[i]];
            if (ch.ResolvedClass is BidiClass.ES or BidiClass.CS or BidiClass.ET)
            {
                ch.ResolvedClass = BidiClass.ON;
            }
        }
    }

    /// <summary>
    /// W7 — Search backward from each EN until the first strong type (R, L, sos). If an
    /// L is found, change the EN to L.
    /// </summary>
    private static void ApplyW7(Span<BidiCharInfo> chars, BidiIsolatingRunSequence sequence)
    {
        var indices = sequence.FlatIndices;
        for (var i = 0; i < indices.Length; i++)
        {
            ref var ch = ref chars[indices[i]];
            if (ch.ResolvedClass != BidiClass.EN)
            {
                continue;
            }
            BidiClass strong = sequence.Sos;
            for (var j = i - 1; j >= 0; j--)
            {
                var c = chars[indices[j]].ResolvedClass;
                if (c is BidiClass.R or BidiClass.L)
                {
                    strong = c;
                    break;
                }
            }
            if (strong == BidiClass.L)
            {
                ch.ResolvedClass = BidiClass.L;
            }
        }
    }
}
