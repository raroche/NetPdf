// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.SystemFonts;

/// <summary>
/// Searchable in-memory index of <see cref="SystemFontEntry"/> records, keyed by family
/// name (case-insensitive ASCII per CSS rules). Built once per process via
/// <see cref="Build"/>; subsequent <see cref="FindBest"/> calls match a query against
/// every entry sharing the family name and pick the best fit by CSS-style weight +
/// italic distance.
/// </summary>
internal sealed class SystemFontIndex
{
    private readonly Dictionary<string, List<SystemFontEntry>> _byFamily;

    private SystemFontIndex(Dictionary<string, List<SystemFontEntry>> byFamily)
    {
        _byFamily = byFamily;
    }

    /// <summary>Total number of indexed faces.</summary>
    public int Count => _byFamily.Values.Sum(list => list.Count);

    /// <summary>Number of distinct family names in the index.</summary>
    public int FamilyCount => _byFamily.Count;

    /// <summary>
    /// Build an index from an enumerator. Caller controls when this runs — typically once
    /// at process start and re-used across documents.
    /// </summary>
    public static SystemFontIndex Build(SystemFontEnumerator enumerator)
    {
        ArgumentNullException.ThrowIfNull(enumerator);
        var dict = new Dictionary<string, List<SystemFontEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in enumerator.Enumerate())
        {
            if (string.IsNullOrEmpty(entry.FamilyName)) continue;
            if (!dict.TryGetValue(entry.FamilyName, out var list))
            {
                list = [];
                dict[entry.FamilyName] = list;
            }
            list.Add(entry);
        }
        return new SystemFontIndex(dict);
    }

    /// <summary>
    /// Build an index directly from a sequence of entries — for tests and for callers
    /// that already have a curated list.
    /// </summary>
    public static SystemFontIndex BuildFromEntries(IEnumerable<SystemFontEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var dict = new Dictionary<string, List<SystemFontEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.FamilyName)) continue;
            if (!dict.TryGetValue(entry.FamilyName, out var list))
            {
                list = [];
                dict[entry.FamilyName] = list;
            }
            list.Add(entry);
        }
        return new SystemFontIndex(dict);
    }

    /// <summary>True when at least one face is registered under <paramref name="family"/>.</summary>
    public bool HasFamily(string family) => _byFamily.ContainsKey(family);

    /// <summary>
    /// Find the best face within <paramref name="family"/> for the requested stretch +
    /// style + weight. Returns <c>null</c> when no entry exists for the family at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implements <a href="https://www.w3.org/TR/css-fonts-4/#font-matching-algorithm">CSS
    /// Fonts Module Level 4 §5.2 "Matching font styles"</a>. The algorithm is a
    /// <b>direction-first, distance-second ordered search</b>, not a distance function.
    /// At each axis (in this priority order: <c>font-stretch → font-style →
    /// font-weight</c>) candidates are partitioned into tiers; only candidates in the
    /// best occupied tier survive to the next axis.
    /// </para>
    /// <list type="number">
    ///   <item>
    ///     <b>font-stretch</b> (§5.2.3) — exact match wins. Otherwise:
    ///     <list type="bullet">
    ///       <item>If <paramref name="stretchCss"/> ≤ 5: tier 1 = narrower-or-equal
    ///             (descending; closest narrower first); tier 2 = wider (ascending).</item>
    ///       <item>If <paramref name="stretchCss"/> &gt; 5: tier 1 = wider-or-equal
    ///             (ascending); tier 2 = narrower (descending).</item>
    ///     </list>
    ///   </item>
    ///   <item><b>font-style</b> (§5.2.5) — italic match wins. Per CSS Fonts 4 user
    ///         agents may treat italic and oblique as synonyms; this matcher does (see
    ///         <see cref="SystemFontEntry.IsItalic"/>).</item>
    ///   <item>
    ///     <b>font-weight</b> (§5.2.4) — exact match wins. Otherwise:
    ///     <list type="bullet">
    ///       <item>Light regime (request &lt; 400): tier 1 = lighter-or-equal (descending);
    ///             tier 2 = heavier (ascending).</item>
    ///       <item>Normal regime (400 ≤ request ≤ 500): tier 1 = [request..500] (ascending);
    ///             tier 2 = below request (descending); tier 3 = above 500 (ascending).</item>
    ///       <item>Heavy regime (request &gt; 500): tier 1 = heavier-or-equal (ascending);
    ///             tier 2 = lighter (descending).</item>
    ///     </list>
    ///   </item>
    /// </list>
    /// <para>
    /// Implementation: ordinal lexicographic ordering via penalty stacking with
    /// non-overlapping tier bands. Magnitudes:
    /// <c>stretch ∈ [0, 200]</c>, <c>style ∈ {0, 1}</c>, <c>weight ∈ [0, 3000]</c>.
    /// The total score is computed as
    /// <c>stretch × 6002 + style × 3001 + weight</c> so that a one-tier increment on the
    /// higher axis always dominates any combination of lower-axis values.
    /// </para>
    /// </remarks>
    public SystemFontEntry? FindBest(string family, int weightCss, bool italic, int stretchCss = 5)
    {
        ArgumentNullException.ThrowIfNull(family);
        if (!_byFamily.TryGetValue(family, out var list) || list.Count == 0) return null;

        SystemFontEntry best = list[0];
        var bestScore = ScoreCandidate(best, weightCss, italic, stretchCss);
        for (var i = 1; i < list.Count; i++)
        {
            var candidate = list[i];
            var score = ScoreCandidate(candidate, weightCss, italic, stretchCss);
            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }
        return best;
    }

    // Non-overlapping tier bands. weight max ≤ 3000, style ∈ {0,1}, stretch max ≤ 200.
    private const long WeightMaxPlus1 = 3001L;
    private const long StyleSlot = WeightMaxPlus1;          // style × 3001
    private const long StyleMaxPlus1 = 2L;
    private const long StretchSlot = StyleSlot * StyleMaxPlus1;  // stretch × 6002

    private static long ScoreCandidate(SystemFontEntry candidate, int weightCss, bool italic, int stretchCss)
    {
        var stretch = StretchScore(candidate.StretchCss, stretchCss);
        var style = candidate.IsItalic == italic ? 0L : 1L;
        var weight = WeightScore(candidate.WeightCss, weightCss);
        return stretch * StretchSlot + style * StyleSlot + weight;
    }

    /// <summary>
    /// Score a candidate's stretch against the request per CSS Fonts 4 §5.2.3. Output is
    /// non-negative; lower = better. Exact match scores 0; same-direction candidates score
    /// in <c>[1, 8]</c> by absolute delta; opposite-direction candidates score in
    /// <c>[101, 108]</c> so any same-direction candidate beats any opposite-direction one.
    /// </summary>
    private static long StretchScore(int candidateStretch, int requestStretch)
    {
        if (candidateStretch == requestStretch) return 0;
        var delta = candidateStretch - requestStretch;
        var absDelta = Math.Abs(delta);
        if (requestStretch <= 5)
        {
            // Narrow regime: narrower-or-equal first (delta ≤ 0), then wider (delta > 0).
            return delta < 0 ? absDelta : 100L + absDelta;
        }
        // Wide regime: wider-or-equal first (delta ≥ 0), then narrower (delta < 0).
        return delta > 0 ? absDelta : 100L + absDelta;
    }

    /// <summary>
    /// Score a candidate's weight against the request per CSS Fonts 4 §5.2.4. Output is
    /// non-negative; lower = better. Exact match scores 0; tier-1 candidates score by
    /// |delta|; subsequent tiers add 1000-step constants so every tier-N candidate beats
    /// any tier-(N+1) candidate. Bounded by 3000 (worst case: tier 3 in normal regime).
    /// </summary>
    private static long WeightScore(int candidateWeight, int requestWeight)
    {
        if (candidateWeight == requestWeight) return 0;
        var delta = candidateWeight - requestWeight;
        var absDelta = Math.Abs(delta);
        if (requestWeight < 400)
        {
            // Light regime: lighter-or-equal first, then heavier.
            return delta < 0 ? absDelta : 1000L + absDelta;
        }
        if (requestWeight <= 500)
        {
            // Normal regime: [request..500] ascending, then below-request descending,
            // then above-500 ascending.
            if (candidateWeight > requestWeight && candidateWeight <= 500) return absDelta;
            if (candidateWeight < requestWeight) return 1000L + absDelta;
            return 2000L + (candidateWeight - 500); // candidate > 500
        }
        // Heavy regime: heavier-or-equal first, then lighter.
        return delta > 0 ? absDelta : 1000L + absDelta;
    }
}
