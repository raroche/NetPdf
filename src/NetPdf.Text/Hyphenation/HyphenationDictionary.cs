// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Frozen;

namespace NetPdf.Text.Hyphenation;

/// <summary>
/// Explicit-hyphenation exception dictionary. When a word matches an entry exactly
/// (case-insensitive ASCII), the precomputed break positions are returned directly
/// and the pattern-based algorithm is bypassed.
/// </summary>
/// <remarks>
/// <para>
/// The classic en-us exception list (from D. E. Knuth's <c>hyphen.tex</c>) carries
/// 14 words such as <c>as-so-ciate</c>, <c>ta-ble</c>, <c>present</c> (the last
/// signaling a word that should NEVER be hyphenated — empty break list). Custom
/// applications can add their own exceptions through <see cref="Parse"/>.
/// </para>
/// <para>
/// Break positions are 1-based in the sense that a position <c>k</c> means "break
/// between <c>word[k-1]</c> and <c>word[k]</c>" — i.e., the same convention as the
/// pattern algorithm. The dictionary lookups are case-insensitive ASCII.
/// </para>
/// <para>
/// <b>Malformed-token handling.</b> The parser is defensively normalizing rather than
/// strict. Leading hyphens (<c>-foo</c>), trailing hyphens (<c>foo-</c>), and runs of
/// adjacent hyphens (<c>a--b</c>) are silently collapsed: a hyphen contributes a break
/// position only if it follows at least one letter, has at least one following letter
/// in the same token, and is not adjacent to a previously-recorded break. This keeps
/// the parse total over user-supplied data while still rejecting nonsensical breaks
/// (e.g., a break at position 0 or at <c>letterCount</c>).
/// </para>
/// <para>
/// The lookup hot path uses <see cref="FrozenDictionary{TKey, TValue}.AlternateLookup{TAlternateKey}"/>
/// over <c>ReadOnlySpan&lt;char&gt;</c> so per-call lookups happen without allocating
/// intermediate strings. Returned break data is exposed as <see cref="ReadOnlySpan{T}"/>
/// to forbid callers from mutating the cached singleton arrays.
/// </para>
/// </remarks>
internal sealed class HyphenationDictionary
{
    private readonly FrozenDictionary<string, int[]> _wordToBreaks;
    private readonly FrozenDictionary<string, int[]>.AlternateLookup<ReadOnlySpan<char>>? _spanLookup;

    private HyphenationDictionary(FrozenDictionary<string, int[]> wordToBreaks)
    {
        _wordToBreaks = wordToBreaks;
        _spanLookup = wordToBreaks.Count > 0
            ? wordToBreaks.GetAlternateLookup<ReadOnlySpan<char>>()
            : null;
    }

    /// <summary>Number of words in the exception list.</summary>
    public int Count => _wordToBreaks.Count;

    /// <summary>
    /// Look up explicit hyphenation for <paramref name="word"/>. Returns true with the
    /// (possibly empty) span of break positions if the word is in the list. The empty
    /// span represents a word that must not be hyphenated. Allocation-free on the hot
    /// path; the returned span references cached state and must not be mutated.
    /// </summary>
    public bool TryGet(ReadOnlySpan<char> word, out ReadOnlySpan<int> breaks)
    {
        breaks = default;
        if (word.IsEmpty || _spanLookup is null) return false;

        // Lower-case canonicalize for ASCII letters; non-ASCII falls through unchanged
        // (en-us list is pure ASCII, so it never matches if the input has non-ASCII).
        Span<char> buf = word.Length <= 64 ? stackalloc char[word.Length] : new char[word.Length];
        var pure = true;
        for (var i = 0; i < word.Length; i++)
        {
            var c = word[i];
            if (c is >= 'A' and <= 'Z')
            {
                buf[i] = (char)(c + 32);
                pure = false;
            }
            else
            {
                buf[i] = c;
            }
        }

        // Hot-path AlternateLookup over ReadOnlySpan<char> — no string allocation.
        var key = pure ? word : (ReadOnlySpan<char>)buf;
        if (_spanLookup.Value.TryGetValue(key, out var arr))
        {
            breaks = arr;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Parse a textual exception list into a dictionary. Each non-empty, non-comment token
    /// is a hyphenated word like <c>as-so-ciate</c>; tokens without hyphens (e.g.
    /// <c>present</c>) are interpreted as "do not hyphenate this word" — i.e., empty break
    /// list. TeX-style <c>%</c> comments are stripped (line-start or inline). Malformed
    /// hyphen positions (leading, trailing, adjacent) are silently collapsed per the
    /// class remarks.
    /// </summary>
    public static HyphenationDictionary Parse(ReadOnlySpan<char> block)
    {
        var staging = new Dictionary<string, int[]>(StringComparer.Ordinal);
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
            // Token runs to next whitespace OR an inline TeX '%' comment.
            var start = i;
            while (i < block.Length && !char.IsWhiteSpace(block[i]) && block[i] != '%') i++;
            var token = block[start..i];
            // If we stopped on '%', skip the rest of the line for the next iteration.
            if (i < block.Length && block[i] == '%')
            {
                while (i < block.Length && block[i] != '\n') i++;
            }
            if (token.Length == 0) continue;

            // Strip hyphens and collect break positions. Defensive normalization:
            //   1. A hyphen contributes only if at least one letter has been seen so far
            //      (drops leading hyphens like "-foo").
            //   2. After parsing, drop any break == letterCount (drops trailing hyphens
            //      like "foo-").
            //   3. Skip a hyphen whose position equals the most recently recorded one
            //      (collapses adjacent hyphens like "a--b" to a single break).
            // Parse runs once at startup over the embedded resource; per-token allocation
            // here is not on a hot path (the produced FrozenDictionary lookups are).
            var letters = new char[token.Length];
            var letterCount = 0;
            var breakList = new List<int>();
            for (var k = 0; k < token.Length; k++)
            {
                var c = token[k];
                if (c == '-')
                {
                    if (letterCount == 0) continue; // leading hyphen
                    if (breakList.Count > 0 && breakList[^1] == letterCount) continue; // adjacent hyphen
                    breakList.Add(letterCount);
                }
                else
                {
                    letters[letterCount++] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
                }
            }
            if (letterCount == 0) continue;
            // Drop trailing hyphens — break == letterCount means "after last letter", which
            // is meaningless (rightMin>0 would block it anyway and there's no letter after).
            while (breakList.Count > 0 && breakList[^1] >= letterCount) breakList.RemoveAt(breakList.Count - 1);
            staging[new string(letters[..letterCount])] = breakList.ToArray();
        }
        return new HyphenationDictionary(staging.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>Empty dictionary singleton — no exceptions.</summary>
    public static HyphenationDictionary Empty { get; } =
        new(FrozenDictionary<string, int[]>.Empty);
}
