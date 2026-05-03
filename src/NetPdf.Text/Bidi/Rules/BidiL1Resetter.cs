// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Bidi.Rules;

/// <summary>
/// UAX #9 §3.4 rule L1 — reset the embedding level of paragraph separators, segment
/// separators, and trailing whitespace + isolate-formatting characters to the paragraph
/// embedding level.
/// </summary>
/// <remarks>
/// <para>
/// L1's character classes are derived from <see cref="BidiCharInfo.OriginalClass"/>, not
/// from the resolved class — the spec is explicit that L1 uses the original types so
/// W/N/I rule outputs don't influence whether a character qualifies as a "trailing
/// whitespace" or "segment separator".
/// </para>
/// <para>
/// <b>Reset triggers:</b>
/// </para>
/// <list type="number">
///   <item>Segment separator (S) — reset its own level + walk backward over WS / FSI / LRI / RLI / PDI runs and reset them too.</item>
///   <item>Paragraph separator (B) — same backward reset behavior.</item>
///   <item>End of text — walk backward from the last character, resetting WS / FSI / LRI / RLI / PDI until a non-WS / non-isolate character is found.</item>
/// </list>
/// X9-removed characters (RLE/LRE/RLO/LRO/PDF/BN) are skipped during the backward walk
/// — their levels were already assigned by X-rules and don't participate in L1's
/// "trailing whitespace" notion.
/// </remarks>
internal static class BidiL1Resetter
{
    public static void Apply(Span<BidiCharInfo> chars, byte paragraphLevel)
    {
        // Pass 1: reset S and B chars + walk backward over preceding WS/isolate runs.
        for (var i = 0; i < chars.Length; i++)
        {
            ref var ch = ref chars[i];
            if (ch.OriginalClass is BidiClass.S or BidiClass.B)
            {
                ch.Level = paragraphLevel;
                ResetTrailingWhitespaceBackward(chars, i - 1, paragraphLevel);
            }
        }

        // Pass 2: walk backward from the end of text, resetting WS/isolate-formatting until
        // the first non-WS / non-isolate-formatting character (or X9-removed; skip those).
        ResetTrailingWhitespaceBackward(chars, chars.Length - 1, paragraphLevel);
    }

    private static void ResetTrailingWhitespaceBackward(Span<BidiCharInfo> chars, int startIndex, byte paragraphLevel)
    {
        for (var i = startIndex; i >= 0; i--)
        {
            ref var ch = ref chars[i];
            if (ch.IsRemovedByX9)
            {
                // Skip X9-removed characters in the trailing-whitespace search; they're not
                // structurally meaningful for L1 but they shouldn't break the backward walk.
                continue;
            }
            if (IsResettable(ch.OriginalClass))
            {
                ch.Level = paragraphLevel;
            }
            else
            {
                return;
            }
        }
    }

    private static bool IsResettable(BidiClass cls) =>
        cls is BidiClass.WS or BidiClass.FSI or BidiClass.LRI or BidiClass.RLI or BidiClass.PDI;
}
