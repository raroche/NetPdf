// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Bidi.Rules;

/// <summary>
/// UAX #9 §3.3.2 BD7 + BD13 — partition a paragraph (post-X-rules) into level runs and
/// isolating run sequences. Inputs come from <see cref="BidiX10Resolver.Apply"/>;
/// outputs feed the W/N/I rule passes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Spec basis.</b> UAX #9 §3.3.2 (rule X10), BD7 (level run), BD13 (isolating run
/// sequence). Sos/eos derivation per the bullet list following BD13. No code transliterated
/// from any third-party implementation.
/// </para>
/// <para>
/// <b>Algorithm shape.</b>
/// </para>
/// <list type="number">
///   <item>Walk <see cref="BidiCharInfo"/>[] skipping <see cref="BidiCharInfo.IsRemovedByX9"/>;
///         start a new <see cref="BidiLevelRun"/> whenever the current Level differs from
///         the previous non-removed character's Level.</item>
///   <item>Build a "next run by isolate initiator" map: for each level run that ends with
///         a matched LRI/RLI/FSI (matched = its PDI exists later in the paragraph at the
///         same isolate-nesting depth), record the run that starts at that matching PDI.</item>
///   <item>Walk runs in source order; each run that is not the continuation of an earlier
///         sequence becomes the head of a new <see cref="BidiIsolatingRunSequence"/> and
///         we follow the next-run links until exhaustion (a run ending with an unmatched
///         isolate initiator, or with no isolate initiator).</item>
///   <item>For each sequence, derive sos/eos from the levels of the bordering characters
///         (or the paragraph level) per UAX #9.</item>
/// </list>
/// </remarks>
internal static class BidiRunSegmenter
{
    /// <summary>
    /// Compute the BD7 level runs over <paramref name="chars"/>. Skips X9-removed characters.
    /// </summary>
    public static BidiLevelRun[] ComputeLevelRuns(ReadOnlySpan<BidiCharInfo> chars)
    {
        var runs = new List<BidiLevelRun>();
        var current = new List<int>();
        byte? currentLevel = null;
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i].IsRemovedByX9)
            {
                continue;
            }
            if (currentLevel is null || chars[i].Level == currentLevel)
            {
                current.Add(i);
                currentLevel = chars[i].Level;
            }
            else
            {
                runs.Add(new BidiLevelRun { Indices = current.ToArray(), Level = currentLevel.Value });
                current = new List<int> { i };
                currentLevel = chars[i].Level;
            }
        }
        if (current.Count > 0)
        {
            runs.Add(new BidiLevelRun { Indices = current.ToArray(), Level = currentLevel!.Value });
        }
        return runs.ToArray();
    }

    /// <summary>
    /// Compute BD13 isolating run sequences over the paragraph. Each sequence's W/N/I
    /// rules will eventually run independently of the others.
    /// </summary>
    public static BidiIsolatingRunSequence[] ComputeIsolatingRunSequences(
        ReadOnlySpan<BidiCharInfo> chars,
        BidiLevelRun[] levelRuns,
        byte paragraphLevel)
    {
        // Map every character index to its enclosing level-run index for fast lookup.
        var charIndexToRunIndex = new Dictionary<int, int>(chars.Length);
        for (var r = 0; r < levelRuns.Length; r++)
        {
            foreach (var idx in levelRuns[r].Indices)
            {
                charIndexToRunIndex[idx] = r;
            }
        }

        // Pair every matched isolate initiator (LRI/RLI/FSI) with its PDI by walking the
        // paragraph with a stack of pending initiator indices. An initiator is "matched"
        // only when a PDI eventually closes it; unmatched initiators stay in the stack
        // and stay unlinked. PDIs without a matching open initiator are likewise ignored.
        var initiatorToPdi = new Dictionary<int, int>();
        var pending = new Stack<int>();
        for (var i = 0; i < chars.Length; i++)
        {
            switch (chars[i].OriginalClass)
            {
                case BidiClass.LRI:
                case BidiClass.RLI:
                case BidiClass.FSI:
                    pending.Push(i);
                    break;
                case BidiClass.PDI:
                    if (pending.Count > 0)
                    {
                        var initiatorIndex = pending.Pop();
                        initiatorToPdi[initiatorIndex] = i;
                    }
                    break;
            }
        }

        // For each level run, if its last non-removed character is a matched initiator,
        // the next run in the sequence is the run that contains the matching PDI.
        var nextRunInSequence = new int?[levelRuns.Length];
        for (var r = 0; r < levelRuns.Length; r++)
        {
            var lastIdx = levelRuns[r].LastIndex;
            var lastClass = chars[lastIdx].OriginalClass;
            if (lastClass is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI
                && initiatorToPdi.TryGetValue(lastIdx, out var pdiIdx)
                && charIndexToRunIndex.TryGetValue(pdiIdx, out var pdiRunIdx))
            {
                nextRunInSequence[r] = pdiRunIdx;
            }
        }

        // Identify run-sequence heads: runs whose first character is not the PDI of an
        // earlier run's terminating initiator. Equivalently, a run is a head if no earlier
        // run links to it via the nextRunInSequence map.
        var isContinuation = new bool[levelRuns.Length];
        for (var r = 0; r < levelRuns.Length; r++)
        {
            if (nextRunInSequence[r] is int next)
            {
                isContinuation[next] = true;
            }
        }

        // Build sequences by walking from each head along the next-run chain.
        var sequences = new List<BidiIsolatingRunSequence>();
        for (var r = 0; r < levelRuns.Length; r++)
        {
            if (isContinuation[r])
            {
                continue;
            }
            var chain = new List<BidiLevelRun>();
            var cursor = (int?)r;
            while (cursor is int c)
            {
                chain.Add(levelRuns[c]);
                cursor = nextRunInSequence[c];
            }
            var sequenceRuns = chain.ToArray();
            var seqLevel = sequenceRuns[0].Level;
            var (sos, eos) = ComputeSosEos(chars, sequenceRuns, seqLevel, paragraphLevel);
            var flatIndices = FlattenIndices(sequenceRuns);
            sequences.Add(new BidiIsolatingRunSequence
            {
                Runs = sequenceRuns,
                Level = seqLevel,
                Sos = sos,
                Eos = eos,
                FlatIndices = flatIndices,
            });
        }

        return sequences.ToArray();
    }

    /// <summary>Flatten the per-run index arrays into a single source-order int[].</summary>
    private static int[] FlattenIndices(BidiLevelRun[] runs)
    {
        var total = 0;
        foreach (var run in runs)
        {
            total += run.Indices.Length;
        }
        var flat = new int[total];
        var pos = 0;
        foreach (var run in runs)
        {
            run.Indices.AsSpan().CopyTo(flat.AsSpan(pos));
            pos += run.Indices.Length;
        }
        return flat;
    }

    /// <summary>
    /// Derive sos and eos directions per UAX #9 §3.3.2 (the paragraph-level rules paragraph
    /// following BD13). sos uses the level just before the sequence's first character;
    /// eos uses the level just after the sequence's last character — or the paragraph
    /// level when the sequence ends with an unmatched isolate initiator.
    /// </summary>
    private static (BidiClass Sos, BidiClass Eos) ComputeSosEos(
        ReadOnlySpan<BidiCharInfo> chars,
        BidiLevelRun[] runs,
        byte sequenceLevel,
        byte paragraphLevel)
    {
        var firstIdx = runs[0].FirstIndex;
        var lastIdx = runs[^1].LastIndex;

        // sos: search backward from firstIdx for the nearest non-removed character; if
        // none, use paragraphLevel. The sos class is determined by max(thatLevel, seqLevel)
        // — even → L, odd → R.
        var preLevel = paragraphLevel;
        for (var i = firstIdx - 1; i >= 0; i--)
        {
            if (!chars[i].IsRemovedByX9)
            {
                preLevel = chars[i].Level;
                break;
            }
        }
        var sosLevel = Math.Max(preLevel, sequenceLevel);
        var sos = (sosLevel & 1) == 1 ? BidiClass.R : BidiClass.L;

        // eos: if the sequence's last character is an unmatched isolate initiator, the
        // paragraph-level boundary is used instead of the next char's level (since the
        // BD13 chain stopped because no PDI closes it). Otherwise, search forward from
        // lastIdx for the nearest non-removed character.
        byte postLevel;
        var lastClass = chars[lastIdx].OriginalClass;
        var lastIsUnmatchedInitiator = lastClass is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI;
        if (lastIsUnmatchedInitiator)
        {
            postLevel = paragraphLevel;
        }
        else
        {
            postLevel = paragraphLevel;
            for (var i = lastIdx + 1; i < chars.Length; i++)
            {
                if (!chars[i].IsRemovedByX9)
                {
                    postLevel = chars[i].Level;
                    break;
                }
            }
        }
        var eosLevel = Math.Max(postLevel, sequenceLevel);
        var eos = (eosLevel & 1) == 1 ? BidiClass.R : BidiClass.L;
        return (sos, eos);
    }
}
