// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Bidi.Rules;

/// <summary>
/// UAX #9 §3.3.6 rules I1 and I2 — implicit level adjustment. After W and N rules have
/// resolved every character to one of L, R, EN, or AN, I1/I2 bumps the embedding level
/// up by 1 or 2 to put characters of the "wrong" direction at the next deeper level.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>I1 (even level):</b> R characters bump by 1; AN and EN bump by 2.</item>
///   <item><b>I2 (odd level):</b> L, AN, and EN all bump by 1.</item>
/// </list>
/// X9-removed characters (RLE/LRE/RLO/LRO/PDF/BN) keep the level X-rules assigned them;
/// they're not in the run sequence so I rules never see them.
/// </remarks>
internal static class BidiI12ImplicitResolver
{
    public static void Apply(Span<BidiCharInfo> chars, BidiIsolatingRunSequence sequence)
    {
        var indices = sequence.FlatIndices;
        var levelIsEven = (sequence.Level & 1) == 0;
        for (var i = 0; i < indices.Length; i++)
        {
            ref var ch = ref chars[indices[i]];
            var bump = levelIsEven
                ? ch.ResolvedClass switch
                {
                    BidiClass.R => 1,
                    BidiClass.AN or BidiClass.EN => 2,
                    _ => 0,
                }
                : ch.ResolvedClass switch
                {
                    BidiClass.L or BidiClass.AN or BidiClass.EN => 1,
                    _ => 0,
                };
            ch.Level = (byte)(ch.Level + bump);
        }
    }
}
