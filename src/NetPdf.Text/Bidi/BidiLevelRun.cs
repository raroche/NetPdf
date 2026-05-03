// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Bidi;

/// <summary>
/// A maximal run of consecutive characters that share the same embedding
/// <see cref="Level"/> after X-rule application, skipping characters marked by X9
/// (RLE/LRE/RLO/LRO/PDF/BN). Per UAX #9 §3.3.2 BD7.
/// </summary>
/// <remarks>
/// <see cref="Indices"/> stores the actual character indices (into <see cref="BidiCharInfo"/>[])
/// that belong to this run. We do not collapse to a contiguous <c>(start, length)</c>
/// because X9-removed characters interleave the original sequence — the BD7 rule says a
/// level run is "the maximum substring of characters with the same level after X9", and
/// that substring is sparse over the original index space.
/// </remarks>
internal sealed class BidiLevelRun
{
    /// <summary>Indices into the per-codepoint <see cref="BidiCharInfo"/> array, in source order.</summary>
    public required int[] Indices { get; init; }

    /// <summary>The shared embedding level for every character in this run.</summary>
    public required byte Level { get; init; }

    /// <summary>The first index in the run.</summary>
    public int FirstIndex => Indices[0];

    /// <summary>The last index in the run.</summary>
    public int LastIndex => Indices[^1];
}
