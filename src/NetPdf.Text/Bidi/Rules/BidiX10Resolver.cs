// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Bidi.Rules;

/// <summary>
/// UAX #9 §3.3.2 rules X1–X10 — explicit embeddings, overrides, and isolates. Walks the
/// paragraph once, mutating <see cref="BidiCharInfo.Level"/>, <see cref="BidiCharInfo.ResolvedClass"/>,
/// and <see cref="BidiCharInfo.IsRemovedByX9"/> per the BD13 directional status stack.
/// </summary>
/// <remarks>
/// <para>
/// <b>Spec basis (clean-room).</b> Unicode UAX #9 (https://www.unicode.org/reports/tr9/)
/// §3.3.2 "Explicit Levels and Directions". No code transliterated from any third-party
/// implementation; per-rule branches reference the exact rule number.
/// </para>
/// <para>
/// <b>FSI direction inference.</b> X5c says "compute as if applying P2 and P3 to the
/// substring between this FSI and its matching PDI": if the first strong character is
/// AL or R the FSI behaves like RLI, otherwise like LRI. <see cref="ResolveFsiDirection"/>
/// runs that mini-P2/P3 inline; matched-PDI search is bracket-balance-aware so an FSI/LRI/RLI
/// nested between this FSI and a candidate PDI claims the candidate first.
/// </para>
/// <para>
/// <b>X9 — Removed-mark semantics.</b> RLE/LRE/RLO/LRO/PDF/BN are flagged
/// <see cref="BidiCharInfo.IsRemovedByX9"/> = true after this pass. Their
/// <see cref="BidiCharInfo.Level"/> is still set (to the level the X-rules saw), but the
/// run-segmenter and W/N/I rule passes skip them. Their <see cref="BidiCharInfo.OriginalClass"/>
/// is preserved so L1's trailing-whitespace rule can still find paragraph / segment
/// separators by their original class.
/// </para>
/// </remarks>
internal static class BidiX10Resolver
{
    /// <summary>UAX #9 BD2 maximum embedding level.</summary>
    public const byte MaxEmbeddingLevel = DirectionalStatusStack.MaxEmbeddingLevel;

    /// <summary>
    /// Apply X1–X10 to <paramref name="chars"/> in place. <paramref name="paragraphLevel"/>
    /// is the value previously resolved by <see cref="ParagraphLevelResolver.Resolve"/>.
    /// </summary>
    public static void Apply(Span<BidiCharInfo> chars, byte paragraphLevel)
    {
        // X1 — initialize the directional status stack with the paragraph entry.
        var stack = new DirectionalStatusStack();
        stack.Push(paragraphLevel, DirectionalOverride.Neutral, isIsolate: false);

        var overflowIsolateCount = 0;
        var overflowEmbeddingCount = 0;
        var validIsolateCount = 0;

        for (var i = 0; i < chars.Length; i++)
        {
            ref var ch = ref chars[i];
            switch (ch.OriginalClass)
            {
                // X2 — RLE
                case BidiClass.RLE:
                    HandleEmbedding(stack, ref ch, isRtl: true, ref overflowIsolateCount, ref overflowEmbeddingCount, applyOverride: false);
                    break;

                // X3 — LRE
                case BidiClass.LRE:
                    HandleEmbedding(stack, ref ch, isRtl: false, ref overflowIsolateCount, ref overflowEmbeddingCount, applyOverride: false);
                    break;

                // X4 — RLO
                case BidiClass.RLO:
                    HandleEmbedding(stack, ref ch, isRtl: true, ref overflowIsolateCount, ref overflowEmbeddingCount, applyOverride: true);
                    break;

                // X5 — LRO
                case BidiClass.LRO:
                    HandleEmbedding(stack, ref ch, isRtl: false, ref overflowIsolateCount, ref overflowEmbeddingCount, applyOverride: true);
                    break;

                // X5a — RLI
                case BidiClass.RLI:
                    HandleIsolate(stack, ref ch, isRtl: true, ref overflowIsolateCount, ref overflowEmbeddingCount, ref validIsolateCount);
                    break;

                // X5b — LRI
                case BidiClass.LRI:
                    HandleIsolate(stack, ref ch, isRtl: false, ref overflowIsolateCount, ref overflowEmbeddingCount, ref validIsolateCount);
                    break;

                // X5c — FSI
                case BidiClass.FSI:
                    {
                        var fsiIsRtl = ResolveFsiDirection(chars, i);
                        HandleIsolate(stack, ref ch, fsiIsRtl, ref overflowIsolateCount, ref overflowEmbeddingCount, ref validIsolateCount);
                    }
                    break;

                // X6a — PDI
                case BidiClass.PDI:
                    HandlePdi(stack, ref ch, ref overflowIsolateCount, ref overflowEmbeddingCount, ref validIsolateCount);
                    break;

                // X7 — PDF
                case BidiClass.PDF:
                    HandlePdf(stack, ref ch, ref overflowIsolateCount, ref overflowEmbeddingCount);
                    break;

                // X8 — B (paragraph separator). Spec language: "the algorithm is restarted
                // as for a new paragraph". Reset the directional status stack and overflow
                // counters so post-B characters start fresh; reuse the input paragraphLevel
                // (callers that expect different per-paragraph levels should split on B and
                // call BidiX10Resolver per-paragraph).
                case BidiClass.B:
                    ch.Level = paragraphLevel;
                    ch.ResolvedClass = ch.OriginalClass;
                    stack.Clear();
                    stack.Push(paragraphLevel, DirectionalOverride.Neutral, isIsolate: false);
                    overflowIsolateCount = 0;
                    overflowEmbeddingCount = 0;
                    validIsolateCount = 0;
                    break;

                // X6 — BN: assign top.level, mark for X9 removal, no override applied.
                case BidiClass.BN:
                    ch.Level = stack.Top.Level;
                    ch.ResolvedClass = ch.OriginalClass;
                    ch.IsRemovedByX9 = true;
                    break;

                // X6 — every other class (L, R, AL, EN, ES, ET, AN, CS, NSM, S, WS, ON).
                default:
                    ApplyOverrideAndLevel(stack, ref ch);
                    break;
            }
        }
    }

    /// <summary>
    /// X2/X3/X4/X5 — push an embedding entry on the stack, or bump the overflow counter
    /// when the BD2 cap or an enclosing isolate overflow blocks the push.
    /// </summary>
    /// <remarks>
    /// Per UAX #9 §5.2 ("Retaining Format Characters"), retained explicit formatting
    /// characters take the level of their surrounding text — the enclosing level captured
    /// <i>before</i> any push. Assigning the post-push level (the level inside the
    /// embedding) would group the formatting character into the wrong level run; the
    /// run-segmenter skips X9-removed characters anyway, but downstream consumers
    /// inspecting <see cref="BidiCharInfo.Level"/> on a retained character expect the
    /// enclosing level per the spec.
    /// </remarks>
    private static void HandleEmbedding(
        DirectionalStatusStack stack,
        ref BidiCharInfo ch,
        bool isRtl,
        ref int overflowIsolateCount,
        ref int overflowEmbeddingCount,
        bool applyOverride)
    {
        // Capture the enclosing level BEFORE any push so the retained X9 character ends
        // up at the surrounding-text level, not at the post-push (inside-embedding) level.
        var enclosingLevel = stack.Top.Level;
        var newLevel = isRtl ? NextOddLevel(enclosingLevel) : NextEvenLevel(enclosingLevel);
        if (newLevel <= MaxEmbeddingLevel && overflowIsolateCount == 0 && overflowEmbeddingCount == 0)
        {
            var @override = applyOverride
                ? (isRtl ? DirectionalOverride.R : DirectionalOverride.L)
                : DirectionalOverride.Neutral;
            stack.Push(newLevel, @override, isIsolate: false);
        }
        else if (overflowIsolateCount == 0)
        {
            overflowEmbeddingCount++;
        }
        ch.Level = enclosingLevel;
        ch.ResolvedClass = ch.OriginalClass;
        ch.IsRemovedByX9 = true;
    }

    /// <summary>
    /// X5a/X5b — assign the LRI/RLI's level + apply pending override before pushing the
    /// isolate entry. The isolate character itself is NOT removed by X9; it's a regular
    /// codepoint that participates in run determination.
    /// </summary>
    private static void HandleIsolate(
        DirectionalStatusStack stack,
        ref BidiCharInfo ch,
        bool isRtl,
        ref int overflowIsolateCount,
        ref int overflowEmbeddingCount,
        ref int validIsolateCount)
    {
        // The isolate initiator inherits the current top level + any pending override.
        ApplyOverrideAndLevel(stack, ref ch);

        var newLevel = isRtl ? NextOddLevel(stack.Top.Level) : NextEvenLevel(stack.Top.Level);
        if (newLevel <= MaxEmbeddingLevel && overflowIsolateCount == 0 && overflowEmbeddingCount == 0)
        {
            validIsolateCount++;
            stack.Push(newLevel, DirectionalOverride.Neutral, isIsolate: true);
        }
        else
        {
            overflowIsolateCount++;
        }
    }

    /// <summary>X6a — pop the directional status stack at a PDI (matched isolate close).</summary>
    /// <remarks>
    /// UAX #9 X6a step 3 explicitly requires "set the overflow embedding count to zero"
    /// before popping back to the matching isolate. Any RLE/LRE that overflowed inside the
    /// isolate region is no longer in scope once the isolate closes, so a subsequent
    /// embedding push must not be blocked by a stale overflow count.
    /// </remarks>
    private static void HandlePdi(
        DirectionalStatusStack stack,
        ref BidiCharInfo ch,
        ref int overflowIsolateCount,
        ref int overflowEmbeddingCount,
        ref int validIsolateCount)
    {
        if (overflowIsolateCount > 0)
        {
            overflowIsolateCount--;
        }
        else if (validIsolateCount == 0)
        {
            // PDI without a matching isolate initiator: spec says do nothing to stack.
        }
        else
        {
            // X6a step 3: clear overflow embedding count BEFORE popping back to the isolate.
            // Any RLE/LRE that overflowed inside the isolate region is now out of scope.
            overflowEmbeddingCount = 0;
            // Pop until the top entry is an isolate, then pop that isolate.
            while (!stack.Top.IsIsolate)
            {
                stack.Pop();
            }
            stack.Pop();
            validIsolateCount--;
        }
        // PDI's level + override come from the new top (post-pop).
        ApplyOverrideAndLevel(stack, ref ch);
    }

    /// <summary>X7 — pop the directional status stack at a PDF (matched embedding/override close).</summary>
    private static void HandlePdf(
        DirectionalStatusStack stack,
        ref BidiCharInfo ch,
        ref int overflowIsolateCount,
        ref int overflowEmbeddingCount)
    {
        if (overflowIsolateCount > 0)
        {
            // Inside an overflowing isolate — PDF cannot escape it.
        }
        else if (overflowEmbeddingCount > 0)
        {
            overflowEmbeddingCount--;
        }
        else if (stack.Depth > 1 && !stack.Top.IsIsolate)
        {
            stack.Pop();
        }
        // X9: PDF is removed. Level recorded for diagnostic completeness.
        ch.Level = stack.Top.Level;
        ch.ResolvedClass = ch.OriginalClass;
        ch.IsRemovedByX9 = true;
    }

    /// <summary>X6 — assign the prevailing top.level and apply any pending override.</summary>
    private static void ApplyOverrideAndLevel(DirectionalStatusStack stack, ref BidiCharInfo ch)
    {
        var top = stack.Top;
        ch.Level = top.Level;
        ch.ResolvedClass = top.Override switch
        {
            DirectionalOverride.L => BidiClass.L,
            DirectionalOverride.R => BidiClass.R,
            _ => ch.OriginalClass,
        };
    }

    /// <summary>
    /// X5c — apply UAX #9 P2 + P3 to the contents of an FSI...PDI region to determine
    /// whether the FSI behaves like LRI (LTR) or RLI (RTL).
    /// </summary>
    /// <remarks>
    /// Walks forward from <paramref name="fsiIndex"/>+1 until either the matching PDI
    /// (tracking nested isolate initiators with a depth counter, exactly as P2's matching
    /// rule says) or end of paragraph. The first strong character of class L returns
    /// LTR (false); the first R or AL returns RTL (true); no strong character defaults
    /// to LTR (false), matching P3's default.
    /// </remarks>
    private static bool ResolveFsiDirection(ReadOnlySpan<BidiCharInfo> chars, int fsiIndex)
    {
        var nestedIsolates = 0;
        for (var i = fsiIndex + 1; i < chars.Length; i++)
        {
            switch (chars[i].OriginalClass)
            {
                case BidiClass.LRI:
                case BidiClass.RLI:
                case BidiClass.FSI:
                    nestedIsolates++;
                    break;

                case BidiClass.PDI:
                    if (nestedIsolates == 0)
                    {
                        // Reached our matching PDI without finding a strong character.
                        return false;
                    }
                    nestedIsolates--;
                    break;

                case BidiClass.L:
                    if (nestedIsolates == 0) return false;
                    break;

                case BidiClass.R:
                case BidiClass.AL:
                    if (nestedIsolates == 0) return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>The least odd embedding level strictly greater than <paramref name="current"/>.</summary>
    public static byte NextOddLevel(byte current) => (byte)((current + 1) | 1);

    /// <summary>The least even embedding level strictly greater than <paramref name="current"/>.</summary>
    public static byte NextEvenLevel(byte current) => (byte)((current + 2) & ~1);
}
