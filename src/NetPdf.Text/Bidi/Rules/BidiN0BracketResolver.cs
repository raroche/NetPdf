// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Bidi.Rules;

/// <summary>
/// UAX #9 §3.3.5 rule N0 — paired bracket resolution. Identifies bracket pairs in an
/// isolating run sequence per BD16 and assigns L or R to both members of each pair based
/// on the strong types appearing inside the brackets and (as fallback) before them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Spec basis (clean-room).</b> UAX #9 §3.3.5 (rule N0), BD16 (paired brackets).
/// Bracket data from <see cref="BidiBrackets"/> (UCD <c>BidiBrackets.txt</c> 16.0).
/// </para>
/// <para>
/// <b>BD16 stack.</b> A simple stack collects opener positions; on encountering a closer
/// whose pair is on the stack, every opener above the matched opener is discarded (the
/// algorithm matches greedily). Up to 63 pairs per sequence are kept (UAX #9 BD16 cap);
/// pairs found after the 63rd are not classified.
/// </para>
/// <para>
/// <b>N0 strong-type resolution per pair.</b> The "embedding direction" comes from the
/// sequence's level parity (even = L, odd = R). For each pair, scan the strong types
/// (L, R; with EN/AN treated as R) inside the brackets — first match of the embedding
/// direction wins; otherwise an opposing strong type triggers a backward scan from the
/// opener for context. If neither finds a strong type, the brackets keep their current
/// (ON) class.
/// </para>
/// </remarks>
internal static class BidiN0BracketResolver
{
    private const int MaxPairs = 63;

    private readonly record struct BracketPair(int OpenIndex, int CloseIndex);

    public static void Apply(Span<BidiCharInfo> chars, BidiIsolatingRunSequence sequence)
    {
        var pairs = FindBracketPairs(chars, sequence);
        if (pairs.Count == 0)
        {
            return;
        }

        var embeddingDirection = (sequence.Level & 1) == 0 ? BidiClass.L : BidiClass.R;
        var oppositeDirection = embeddingDirection == BidiClass.L ? BidiClass.R : BidiClass.L;

        // Pairs are processed in left-to-right order of opener positions per UAX #9.
        pairs.Sort(static (a, b) => a.OpenIndex.CompareTo(b.OpenIndex));

        foreach (var pair in pairs)
        {
            var direction = ResolvePairDirection(chars, sequence, pair, embeddingDirection, oppositeDirection);
            if (direction is BidiClass cls)
            {
                AssignBracketDirection(chars, sequence, pair, cls);
            }
        }
    }

    /// <summary>
    /// BD16 — walk the sequence keeping a stack of unmatched opener positions. On each
    /// closer whose pair codepoint sits on the stack, emit the matched pair and discard
    /// every opener above it.
    /// </summary>
    /// <remarks>
    /// <b>Stack overflow handling.</b> Per UAX #9 BD16: "If an opening paired bracket is
    /// encountered that would exceed this limit, neither it nor any subsequent matching
    /// closing paired bracket is processed." So when an opener is skipped due to the
    /// 63-entry cap, every subsequent closer whose codepoint matches the skipped opener's
    /// canonical pair is also skipped — preventing the algorithm from forming "shifted"
    /// pairs that would mis-match natural-nesting boundaries.
    /// </remarks>
    private static List<BracketPair> FindBracketPairs(Span<BidiCharInfo> chars, BidiIsolatingRunSequence sequence)
    {
        var indices = sequence.FlatIndices;
        var pairs = new List<BracketPair>();
        var stack = new Stack<(int SeqPosition, int PairCodepoint)>();
        // Tracks the canonical-pair codepoints of openers skipped due to the 63-entry stack
        // cap. Subsequent closers whose canonical codepoint is in this set are also skipped.
        HashSet<int>? skippedCloserCanonicals = null;

        for (var i = 0; i < indices.Length; i++)
        {
            ref var ch = ref chars[indices[i]];
            // BD14: only ON-classified brackets are eligible. (W rules don't change brackets;
            // they retain class ON throughout the W passes.)
            if (ch.ResolvedClass != BidiClass.ON)
            {
                continue;
            }
            var entry = BidiBrackets.Lookup(ch.Codepoint);
            if (entry is null)
            {
                continue;
            }
            if (entry.Value.Kind == BidiBrackets.BracketKind.Open)
            {
                if (stack.Count < MaxPairs)
                {
                    stack.Push((i, entry.Value.PairCodepoint));
                }
                else
                {
                    // BD16 stack cap exceeded: this opener AND all subsequent matching
                    // closers are skipped from BD16 processing per the spec.
                    skippedCloserCanonicals ??= [];
                    skippedCloserCanonicals.Add(CanonicalizeBracket(entry.Value.PairCodepoint));
                }
            }
            else
            {
                var closerCanonical = CanonicalizeBracket(ch.Codepoint);
                if (skippedCloserCanonicals is not null && skippedCloserCanonicals.Contains(closerCanonical))
                {
                    continue;
                }
                if (stack.Count == 0)
                {
                    continue;
                }
                // Iterate to find a matching opener; pop all openers above it including the match.
                // UAX #9 BD16 says canonical-equivalent brackets (2329⇔3008, 232A⇔3009) match
                // each other — compare via the canonical representative codepoint.
                var temp = stack.ToArray();
                var matchIdx = -1;
                for (var s = 0; s < temp.Length; s++)
                {
                    if (CanonicalizeBracket(temp[s].PairCodepoint) == closerCanonical)
                    {
                        matchIdx = s;
                        break;
                    }
                }
                if (matchIdx < 0)
                {
                    continue;
                }
                // Pop openers above (and including) the match.
                for (var p = 0; p <= matchIdx; p++)
                {
                    stack.Pop();
                }
                pairs.Add(new BracketPair(temp[matchIdx].SeqPosition, i));
                if (pairs.Count >= MaxPairs)
                {
                    return pairs;
                }
            }
        }
        return pairs;
    }

    /// <summary>
    /// N0 — examine strong types inside the bracket pair. Returns L/R/null per the rule.
    /// </summary>
    private static BidiClass? ResolvePairDirection(
        Span<BidiCharInfo> chars,
        BidiIsolatingRunSequence sequence,
        BracketPair pair,
        BidiClass embeddingDirection,
        BidiClass oppositeDirection)
    {
        var indices = sequence.FlatIndices;

        // (a) If a strong type matching the embedding direction is found inside the pair,
        //     both brackets get embedding direction.
        for (var i = pair.OpenIndex + 1; i < pair.CloseIndex; i++)
        {
            var strong = AsStrong(chars[indices[i]].ResolvedClass);
            if (strong == embeddingDirection)
            {
                return embeddingDirection;
            }
        }

        // (b) Else if a strong type opposing the embedding direction is found inside,
        //     look at the strong context preceding the opener. If that's the opposing
        //     direction (or sos is opposing), use opposing; otherwise embedding.
        var hasOpposite = false;
        for (var i = pair.OpenIndex + 1; i < pair.CloseIndex; i++)
        {
            var strong = AsStrong(chars[indices[i]].ResolvedClass);
            if (strong == oppositeDirection)
            {
                hasOpposite = true;
                break;
            }
        }
        if (!hasOpposite)
        {
            // (c) No strong type inside — brackets keep their current (ON) class.
            return null;
        }

        // Walk backward from the opener for the first strong type.
        var contextStrong = sequence.Sos;
        for (var i = pair.OpenIndex - 1; i >= 0; i--)
        {
            var strong = AsStrong(chars[indices[i]].ResolvedClass);
            if (strong is BidiClass.L or BidiClass.R)
            {
                contextStrong = strong;
                break;
            }
        }
        return contextStrong == oppositeDirection ? oppositeDirection : embeddingDirection;
    }

    /// <summary>Assign the resolved direction to both brackets and any NSMs immediately following them (UAX #9 N0 last paragraph).</summary>
    private static void AssignBracketDirection(
        Span<BidiCharInfo> chars,
        BidiIsolatingRunSequence sequence,
        BracketPair pair,
        BidiClass direction)
    {
        var indices = sequence.FlatIndices;
        chars[indices[pair.OpenIndex]].ResolvedClass = direction;
        chars[indices[pair.CloseIndex]].ResolvedClass = direction;

        // Per N0 final paragraph: any NSMs following each bracket inherit its new class.
        // The W1 pass already replaced NSMs with their predecessor's class, so the NSM
        // class here would only re-appear if a bracket character itself had a following
        // NSM in the source — but W1 already handled that case using the bracket's
        // (then-ON) class. Re-applying here per spec keeps the rule explicit.
        for (var i = pair.OpenIndex + 1; i < indices.Length; i++)
        {
            if (chars[indices[i]].OriginalClass == BidiClass.NSM)
            {
                chars[indices[i]].ResolvedClass = direction;
            }
            else
            {
                break;
            }
        }
        for (var i = pair.CloseIndex + 1; i < indices.Length; i++)
        {
            if (chars[indices[i]].OriginalClass == BidiClass.NSM)
            {
                chars[indices[i]].ResolvedClass = direction;
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>Map a class to its "strong" form for N0 — L stays L; R/EN/AN all count as R.</summary>
    private static BidiClass AsStrong(BidiClass cls) => cls switch
    {
        BidiClass.L => BidiClass.L,
        BidiClass.R => BidiClass.R,
        BidiClass.EN => BidiClass.R,
        BidiClass.AN => BidiClass.R,
        _ => BidiClass.ON,
    };

    /// <summary>
    /// Canonicalize bracket codepoints under canonical equivalence per UCD
    /// <c>BidiMirroring.txt</c>. UAX #9 BD16 specifies that canonical-equivalent brackets
    /// match each other for pair detection — so U+2329 and U+3008 (left-pointing angle
    /// brackets) form a pair with either U+232A or U+3009 (right-pointing angle brackets).
    /// </summary>
    private static int CanonicalizeBracket(int codepoint) => codepoint switch
    {
        0x3008 => 0x2329, // 〈 LEFT ANGLE BRACKET — canonical-equivalent to U+2329
        0x3009 => 0x232A, // 〉 RIGHT ANGLE BRACKET — canonical-equivalent to U+232A
        _ => codepoint,
    };
}
