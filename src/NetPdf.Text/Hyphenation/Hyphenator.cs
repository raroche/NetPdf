// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Hyphenation;

/// <summary>
/// Liang-style hyphenation engine. Given a word and a pattern set (plus an optional
/// exceptions dictionary), returns the indices in the word at which a soft-hyphen
/// break is permitted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm (clean-room).</b> Implements F. M. Liang, <i>Word Hy-phen-a-tion by
/// Com-pu-ter</i> (Stanford CS PhD thesis, 1983), §3. For each substring of a word
/// surrounded by <c>.</c> word-boundary markers, look up matching patterns in
/// <see cref="HyphenationPatternSet"/>; merge their priority arrays into a per-position
/// running maximum; positions whose final priority is odd are valid hyphenation points.
/// Exceptions in <see cref="HyphenationDictionary"/> short-circuit to a precomputed
/// break list. Standard <c>leftMin</c> / <c>rightMin</c> constraints (default 2 / 3
/// for English typesetting, per Knuth's <c>hyphen.tex</c> header) trim hyphens too
/// close to either edge.
/// </para>
/// <para>
/// <b>Output positions.</b> Returned indices <c>k</c> mean "a soft-hyphen may appear
/// between <c>word[k-1]</c> and <c>word[k]</c>". For the word <c>"hyphenation"</c>
/// (length 11) with the standard en-us pattern set the typical result is
/// <c>[2, 6]</c> — i.e., <c>hy-phen-ation</c>.
/// </para>
/// <para>
/// <b>Layout-integration contract — caller responsibilities.</b> This API hyphenates a
/// pre-tokenized word; it does not perform any tokenization itself. Layout (Phase 3)
/// is responsible for the upstream decisions before calling
/// <see cref="FindHyphenationPoints"/>:
/// </para>
/// <list type="bullet">
///   <item><b>Word boundaries.</b> Use <see cref="Segmentation.GraphemeClusterBreaker"/> +
///         the word-segmentation engine (post-Phase-1) to identify word tokens. Whitespace
///         and most punctuation are word separators; apostrophes inside contractions
///         (<c>don't</c>) typically stay inside the word for hyphenation purposes.</item>
///   <item><b>Apostrophes / curly quotes.</b> ASCII <c>'</c> (U+0027) and curly <c>’</c>
///         (U+2019) inside a word should be passed through as letters; the en-us pattern
///         set does not include patterns over apostrophes, so they will not contribute
///         break opportunities but they participate in <c>leftMin</c> / <c>rightMin</c>
///         counting.</item>
///   <item><b>Existing hard hyphens.</b> If a word already contains a hyphen
///         (<c>state-of-the-art</c>), tokenize on the hyphen first and hyphenate each
///         segment independently — the hard hyphen is itself a break opportunity.</item>
///   <item><b>Soft hyphens (U+00AD).</b> If the input already contains soft hyphens, the
///         caller should treat those as authoritative break opportunities and decide
///         whether to additionally call this engine; passing soft hyphens through to
///         <see cref="FindHyphenationPoints"/> as letters yields garbage because
///         U+00AD is not in any pattern's letter form.</item>
///   <item><b>Punctuation stripping.</b> Trailing <c>.</c>, <c>,</c>, <c>;</c>, <c>:</c>,
///         <c>!</c>, <c>?</c>, etc. should be stripped before calling this API. The <c>.</c>
///         characters are reserved internally as boundary markers; passing them through as
///         input letters can produce spurious matches against patterns like <c>".ach4"</c>.</item>
///   <item><b>Language selection.</b> The pattern set + exception dictionary are passed
///         in via the constructor — language routing happens at construction time. For
///         multi-language layouts, layout maintains a per-language hyphenator instance
///         (<see cref="EnUsHyphenation.Default"/> for English; future
///         <c>NetPdf.Languages.*</c> packs supply other languages).</item>
/// </list>
/// </remarks>
internal sealed class Hyphenator
{
    private readonly HyphenationPatternSet _patterns;
    private readonly HyphenationDictionary _exceptions;

    public Hyphenator(HyphenationPatternSet patterns, HyphenationDictionary? exceptions = null)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        _patterns = patterns;
        _exceptions = exceptions ?? HyphenationDictionary.Empty;
    }

    /// <summary>
    /// Find allowed hyphenation positions in <paramref name="word"/>. Each returned
    /// integer <c>k</c> means a hyphen may be inserted between <c>word[k-1]</c> and
    /// <c>word[k]</c>. Result is sorted ascending and contains no duplicates.
    /// </summary>
    /// <param name="word">The word to hyphenate. Letters are matched case-insensitively
    /// for ASCII; non-ASCII characters fall through unchanged and only match patterns
    /// whose letter form contains them verbatim. The caller is responsible for word
    /// tokenization, punctuation stripping, and pre-existing hyphen handling — see the
    /// class-level layout-integration contract.</param>
    /// <param name="leftMin">Minimum number of letters required before the first allowed
    /// hyphen. Default 2 per the en-us pattern file's <c>typesetting.left</c> hyphenmin.
    /// Must be non-negative; use 0 to disable the left-edge constraint.</param>
    /// <param name="rightMin">Minimum number of letters required after the last allowed
    /// hyphen. Default 3 per the en-us pattern file's <c>typesetting.right</c> hyphenmin.
    /// Must be non-negative; use 0 to disable the right-edge constraint.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="leftMin"/>
    /// or <paramref name="rightMin"/> is negative.</exception>
    public int[] FindHyphenationPoints(ReadOnlySpan<char> word, int leftMin = 2, int rightMin = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(leftMin);
        ArgumentOutOfRangeException.ThrowIfNegative(rightMin);
        if (word.Length < leftMin + rightMin) return [];

        // Lowercase ASCII canonicalize.
        Span<char> lower = word.Length <= 64 ? stackalloc char[word.Length] : new char[word.Length];
        for (var i = 0; i < word.Length; i++)
        {
            var c = word[i];
            lower[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        }

        // Exception list short-circuit.
        if (_exceptions.TryGet(lower, out var explicitBreaks))
        {
            // Filter by leftMin / rightMin and return a fresh array (caller may mutate).
            var trimmed = new List<int>(explicitBreaks.Length);
            foreach (var k in explicitBreaks)
            {
                if (k >= leftMin && k <= word.Length - rightMin) trimmed.Add(k);
            }
            return trimmed.ToArray();
        }

        // Pattern application: priorities[k] is the priority of a hyphen between
        // boundary-text[k-1] and boundary-text[k]. Boundary text is "." + lower + "."
        // so position k=1 corresponds to "before word[0]" (always 0) and position
        // k=word.Length+1 corresponds to "after word[Length-1]" (always 0).
        var n = word.Length;
        var bnLen = n + 2; // boundary-marked length
        Span<char> boundary = bnLen <= 64 ? stackalloc char[bnLen] : new char[bnLen];
        boundary[0] = '.';
        lower.CopyTo(boundary[1..(n + 1)]);
        boundary[n + 1] = '.';

        Span<byte> priorities = bnLen + 1 <= 96 ? stackalloc byte[bnLen + 1] : new byte[bnLen + 1];
        priorities.Clear();

        var maxLen = Math.Min(_patterns.MaxPatternLength, bnLen);
        for (var i = 0; i < bnLen; i++)
        {
            var maxJ = Math.Min(i + maxLen, bnLen);
            for (var j = i + 1; j <= maxJ; j++)
            {
                // Span-based lookup — no per-window string allocation.
                if (!_patterns.TryGet(boundary[i..j], out var p)) continue;
                // p has length (j - i) + 1 = (j-i) + 1; merge into priorities[i..i+p.Length].
                for (var k = 0; k < p.Length; k++)
                {
                    if (p[k] > priorities[i + k]) priorities[i + k] = p[k];
                }
            }
        }

        // Hyphenation points: in the boundary text, position k corresponds to "between
        // boundary[k-1] and boundary[k]". We want word indices m where 1 <= m <= n-1,
        // mapped to boundary position k = m + 1 (since boundary[0]='.', boundary[m+1]=word[m]).
        // Apply leftMin / rightMin: m >= leftMin and m <= n - rightMin.
        var results = new List<int>();
        var minM = Math.Max(1, leftMin);
        var maxM = n - rightMin;
        for (var m = minM; m <= maxM; m++)
        {
            if ((priorities[m + 1] & 1) == 1) results.Add(m);
        }
        return results.ToArray();
    }
}
