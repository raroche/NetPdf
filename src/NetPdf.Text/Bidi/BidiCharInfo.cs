// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Bidi;

/// <summary>
/// Per-character state used by every UAX #9 rule pass. One instance per Unicode codepoint
/// (not per UTF-16 code unit) — supplementary-plane characters span two code units but
/// represent a single bidi character with a single class.
/// </summary>
/// <remarks>
/// <para>
/// Mutable through the algorithm:
/// </para>
/// <list type="bullet">
///   <item><see cref="OriginalClass"/> is set once from the UCD lookup and never changed
///         (used by L1 to find paragraph separators / segment separators / trailing whitespace).</item>
///   <item><see cref="ResolvedClass"/> starts equal to <see cref="OriginalClass"/> and is
///         mutated by X6 override application, then by W1–W7, N0, N1–N2.</item>
///   <item><see cref="Level"/> is set by X1–X10 (and possibly N0/I1/I2 later).</item>
///   <item><see cref="IsRemovedByX9"/> flags X9-removed characters (RLE/LRE/RLO/LRO/PDF/BN)
///         that participate in level assignment but are skipped during W/N/I run-sequence
///         processing.</item>
/// </list>
/// </remarks>
internal struct BidiCharInfo
{
    /// <summary>Bidi class read from the UCD; never mutated after initial population.</summary>
    public BidiClass OriginalClass;

    /// <summary>Bidi class as mutated by the rule passes (X-override → W → N).</summary>
    public BidiClass ResolvedClass;

    /// <summary>Embedding level assigned by X1–X10 (and refined by N0 / I1–I2).</summary>
    public byte Level;

    /// <summary>True when this codepoint is one of the X9-removed classes (RLE/LRE/RLO/LRO/PDF/BN).</summary>
    public bool IsRemovedByX9;

    /// <summary>Index into the source UTF-16 string where this codepoint starts.</summary>
    public int Utf16Index;

    /// <summary>Number of UTF-16 code units representing this codepoint (1 = BMP, 2 = surrogate pair).</summary>
    public byte Utf16Length;

    /// <summary>The full Unicode codepoint (decoded once during pipeline build), used by N0 paired-bracket resolution and any future rule that needs codepoint identity rather than just bidi class.</summary>
    public int Codepoint;
}
