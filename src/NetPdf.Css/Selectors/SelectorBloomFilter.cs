// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Runtime.CompilerServices;

namespace NetPdf.Css.Selectors;

/// <summary>
/// A 4096-bit Bloom filter over selector tokens (tag names, class names, id names). The
/// cascade resolver builds two of these per element-evaluation: one populated with the
/// element + its ancestors' tokens, one populated per stylesheet's selector tokens. If a
/// selector requires a token the element's filter doesn't contain, the matcher is skipped
/// entirely.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sizing.</b> 4096 bits = 512 bytes per filter. With 2 hash functions and roughly 50
/// inserted tokens (typical CSS rule's ancestor chain) the false-positive rate stays under
/// 1%. Larger documents (long class lists, deep ancestry) push the rate up but never
/// produce a false negative — that's the only soundness invariant that matters for a
/// pre-filter.
/// </para>
/// <para>
/// <b>Storage.</b> 64 ulong words inlined into the struct via <see cref="InlineArrayAttribute"/>
/// so the struct is exactly 512 bytes with zero heap allocations. Copy semantics work — but
/// callers should pass by <c>ref</c> when possible to avoid the 512-byte copy.
/// </para>
/// </remarks>
internal struct SelectorBloomFilter
{
    public const int BitCount = 4096;
    public const int WordCount = BitCount / 64;

    private FilterStorage _words;

    /// <summary>Insert a token (case-handling is the caller's responsibility — tags should
    /// be lowercased before insertion, classes / ids preserved verbatim).</summary>
    public void Add(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        Add(token.AsSpan());
    }

    /// <inheritdoc cref="Add(string)"/>
    public void Add(ReadOnlySpan<char> token)
    {
        var (h1, h2) = Hash(token);
        var b1 = h1 & (BitCount - 1);
        var b2 = h2 & (BitCount - 1);
        _words[b1 >> 6] |= 1UL << (b1 & 63);
        _words[b2 >> 6] |= 1UL << (b2 & 63);
    }

    /// <summary>Probabilistic membership test. <see langword="true"/> means the token MAY
    /// be present; <see langword="false"/> means it is definitively absent.</summary>
    public readonly bool MightContain(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return MightContain(token.AsSpan());
    }

    /// <inheritdoc cref="MightContain(string)"/>
    public readonly bool MightContain(ReadOnlySpan<char> token)
    {
        var (h1, h2) = Hash(token);
        var b1 = h1 & (BitCount - 1);
        var b2 = h2 & (BitCount - 1);
        if ((_words[b1 >> 6] & (1UL << (b1 & 63))) == 0) return false;
        if ((_words[b2 >> 6] & (1UL << (b2 & 63))) == 0) return false;
        return true;
    }

    /// <summary>Reset every bit to zero. Lets the cascade reuse a single filter across
    /// element walks instead of allocating per element.</summary>
    public void Clear()
    {
        for (var i = 0; i < WordCount; i++) _words[i] = 0UL;
    }

    /// <summary>Two independent hashes of a single token using FNV-1a + a salt-rotated
    /// second pass. FNV is overkill for soundness (any non-cryptographic hash works) but
    /// it's simple, deterministic, and produces well-distributed values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int h1, int h2) Hash(ReadOnlySpan<char> token)
    {
        const uint Fnv1aOffset = 2166136261u;
        const uint Fnv1aPrime = 16777619u;
        const uint Salt2 = 0x9E3779B9u;     // golden-ratio scrambler

        var h1 = Fnv1aOffset;
        var h2 = Fnv1aOffset ^ Salt2;
        foreach (var c in token)
        {
            h1 ^= (uint)c;
            h1 *= Fnv1aPrime;
            h2 ^= (uint)c + Salt2 + (h2 << 6) + (h2 >> 2);
        }
        return ((int)(h1 & 0x7FFF_FFFF), (int)(h2 & 0x7FFF_FFFF));
    }

    [System.Runtime.CompilerServices.InlineArray(WordCount)]
    private struct FilterStorage
    {
        private ulong _word0;
    }
}
