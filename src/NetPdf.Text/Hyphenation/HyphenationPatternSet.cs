// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Frozen;

namespace NetPdf.Text.Hyphenation;

/// <summary>
/// A set of Liang-style hyphenation patterns. Each pattern is a sequence of letters
/// (and an optional <c>.</c> word-boundary marker on either side) interspersed with
/// single-digit priority values. Digits between letters describe whether a hyphen
/// is favored (odd) or forbidden (even) at that position; higher digits win.
/// </summary>
/// <remarks>
/// <para>
/// Spec basis (clean-room): F. M. Liang, <i>Word Hy-phen-a-tion by Com-pu-ter</i>
/// (Stanford CS PhD thesis, 1983), §3 — Knuth–Liang trie-based pattern lookup. The
/// pattern data is treated as language data (analogous to UCD test corpora), not as
/// algorithm code; no implementation source is read or transliterated.
/// </para>
/// <para>
/// Internally the set indexes pattern letter-strings into a <see cref="FrozenDictionary{TKey,TValue}"/>
/// keyed by the letters-only form (digits stripped). The associated priority array
/// has length <c>letters.Length + 1</c>: <c>priorities[k]</c> is the priority of a
/// hyphen between letters <c>k-1</c> and <c>k</c> (priorities[0] = before-first,
/// priorities[length] = after-last). Most entries are zero.
/// </para>
/// <para>
/// The lookup hot path uses <see cref="FrozenDictionary{TKey, TValue}.AlternateLookup{TAlternateKey}"/>
/// over <c>ReadOnlySpan&lt;char&gt;</c> so the per-window substring lookups in
/// <c>Hyphenator</c> happen without allocating intermediate <see cref="string"/>
/// instances. Returned priority data is exposed as <see cref="ReadOnlySpan{T}"/>
/// to forbid callers from mutating the cached singleton arrays.
/// </para>
/// </remarks>
internal sealed class HyphenationPatternSet
{
    private readonly FrozenDictionary<string, byte[]> _byLetters;
    private readonly FrozenDictionary<string, byte[]>.AlternateLookup<ReadOnlySpan<char>> _spanLookup;

    /// <summary>Maximum letter-length of any pattern in the set; used to bound the lookup window.</summary>
    public int MaxPatternLength { get; }

    /// <summary>Number of patterns in the set.</summary>
    public int Count => _byLetters.Count;

    private HyphenationPatternSet(FrozenDictionary<string, byte[]> byLetters, int maxPatternLength)
    {
        _byLetters = byLetters;
        // StringComparer.Ordinal supports ReadOnlySpan<char> alternate lookup since .NET 9.
        _spanLookup = byLetters.GetAlternateLookup<ReadOnlySpan<char>>();
        MaxPatternLength = maxPatternLength;
    }

    /// <summary>
    /// Try to look up the priority array for a letter-span. Returns false if no
    /// pattern matches the letter-span. The returned <see cref="ReadOnlySpan{T}"/>
    /// references the cached pattern array; callers must not attempt to mutate it.
    /// Allocation-free on the hot path.
    /// </summary>
    public bool TryGet(ReadOnlySpan<char> letters, out ReadOnlySpan<byte> priorities)
    {
        if (_spanLookup.TryGetValue(letters, out var arr))
        {
            priorities = arr;
            return true;
        }
        priorities = default;
        return false;
    }

    /// <summary>
    /// Parse a TeX-style Liang pattern string into letters + priority array.
    /// Examples:
    ///   <c>".ach4"</c> → letters=<c>".ach"</c>, priorities=<c>[0, 0, 0, 0, 4]</c>.
    ///   <c>"hy3pho"</c> → letters=<c>"hypho"</c>, priorities=<c>[0, 0, 3, 0, 0, 0]</c>.
    /// </summary>
    public static (string Letters, byte[] Priorities) ParseRawPattern(ReadOnlySpan<char> raw)
    {
        // Walk once. Each digit becomes the priority "before the next letter"; if the
        // pattern ends with a digit it becomes the priority "after the last letter".
        Span<char> letters = raw.Length <= 32 ? stackalloc char[raw.Length] : new char[raw.Length];
        var letterCount = 0;
        // Priorities buffer: at most raw.Length + 1 entries.
        Span<byte> priors = raw.Length + 1 <= 64 ? stackalloc byte[raw.Length + 1] : new byte[raw.Length + 1];
        var priorCount = 0;
        byte pending = 0;

        foreach (var c in raw)
        {
            if (c is >= '0' and <= '9')
            {
                pending = (byte)(c - '0');
            }
            else
            {
                // Letter (or '.' boundary marker). Emit the priority that belongs BEFORE this letter.
                priors[priorCount++] = pending;
                pending = 0;
                letters[letterCount++] = c;
            }
        }
        // Trailing priority (the priority AFTER the last letter).
        priors[priorCount++] = pending;

        return (new string(letters[..letterCount]), priors[..priorCount].ToArray());
    }

    /// <summary>
    /// Build a pattern set from a sequence of raw pattern strings (one pattern per item;
    /// digits and letters interleaved per the Liang format). Whitespace-only entries are
    /// skipped. When two patterns share an identical letter form, their priority arrays
    /// are merged element-wise via maximum — this matches Liang's algorithm semantics
    /// (the priority at each position is the max across all matching patterns, regardless
    /// of whether two patterns happen to share letter form or merely overlap during
    /// lookup).
    /// </summary>
    public static HyphenationPatternSet Build(IEnumerable<string> rawPatterns)
    {
        var staging = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var maxLen = 0;
        foreach (var raw in rawPatterns)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var trimmed = raw.AsSpan().Trim();
            if (trimmed.Length == 0) continue;
            var (letters, priorities) = ParseRawPattern(trimmed);
            if (staging.TryGetValue(letters, out var existing))
            {
                // Merge element-wise via max (priority arrays are identical length since
                // the letter form determines the array length: letters.Length + 1).
                for (var k = 0; k < existing.Length; k++)
                {
                    if (priorities[k] > existing[k]) existing[k] = priorities[k];
                }
            }
            else
            {
                staging[letters] = priorities;
                if (letters.Length > maxLen) maxLen = letters.Length;
            }
        }
        return new HyphenationPatternSet(staging.ToFrozenDictionary(StringComparer.Ordinal), maxLen);
    }

    /// <summary>
    /// Build a pattern set from a single block of text containing whitespace-separated
    /// patterns (newlines, spaces, or tabs as separators). TeX-style comments — a <c>%</c>
    /// character begins a comment that runs to the next newline, regardless of whether
    /// it appears at line start or inline after a token — are stripped. This matches the
    /// upstream <c>hyph-en-us.tex</c> format as well as raw <c>\patterns{...}</c> contents
    /// from a TeX file with the wrapping stripped.
    /// </summary>
    public static HyphenationPatternSet ParseBlock(ReadOnlySpan<char> block)
    {
        var list = new List<string>();
        var i = 0;
        while (i < block.Length)
        {
            // Skip leading whitespace.
            while (i < block.Length && char.IsWhiteSpace(block[i])) i++;
            if (i >= block.Length) break;
            // TeX comment at the start of a token: skip to end-of-line.
            if (block[i] == '%')
            {
                while (i < block.Length && block[i] != '\n') i++;
                continue;
            }
            // Token runs until next whitespace OR an inline TeX comment.
            var start = i;
            while (i < block.Length && !char.IsWhiteSpace(block[i]) && block[i] != '%') i++;
            if (i > start) list.Add(new string(block[start..i]));
            // If we stopped on an inline '%', skip the rest of the line.
            if (i < block.Length && block[i] == '%')
            {
                while (i < block.Length && block[i] != '\n') i++;
            }
        }
        return Build(list);
    }
}
