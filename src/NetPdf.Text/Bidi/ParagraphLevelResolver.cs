// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Bidi;

/// <summary>
/// UAX #9 rules P2 and P3: determine the paragraph embedding level (0 = LTR, 1 = RTL)
/// for a paragraph of text. Used by <see cref="BidiAlgorithm"/> as the very first
/// step of the algorithm.
/// </summary>
/// <remarks>
/// <para>
/// <b>P2.</b> In each paragraph, find the first character of type L, AL, or R while
/// skipping over any characters between an isolate initiator (LRI, RLI, FSI) and its
/// matching PDI, or between an unmatched isolate initiator and the end of the paragraph.
/// </para>
/// <para>
/// <b>P3.</b> If a character is found in P2 and it is of type AL or R, then set the
/// paragraph embedding level to one; otherwise, set it to zero.
/// </para>
/// <para>
/// <b>Paragraph segmentation contract.</b> UAX #9 operates one paragraph at a time;
/// callers are expected to pre-segment multi-paragraph text. As a defensive
/// fallback, the auto-detection scan also stops at the first paragraph separator
/// (<see cref="BidiClass.B"/>) outside any isolate — so passing <c>"123\nאבג"</c>
/// returns 0 (P3 default for the first paragraph "123" which has no strong char),
/// not 1.
/// </para>
/// </remarks>
internal static class ParagraphLevelResolver
{
    /// <summary>
    /// Resolve a paragraph's base level (0 = LTR, 1 = RTL) given the input text and
    /// the requested direction. <see cref="ParagraphDirection.Auto"/> applies P2/P3;
    /// the explicit values short-circuit to 0 / 1.
    /// </summary>
    public static byte Resolve(ReadOnlySpan<char> utf16Text, ParagraphDirection requested)
    {
        return requested switch
        {
            ParagraphDirection.LeftToRight => 0,
            ParagraphDirection.RightToLeft => 1,
            ParagraphDirection.Auto => AutoLevelFromFirstStrongChar(utf16Text),
            _ => throw new ArgumentOutOfRangeException(nameof(requested), requested, "Unknown ParagraphDirection."),
        };
    }

    private static byte AutoLevelFromFirstStrongChar(ReadOnlySpan<char> utf16Text)
    {
        // P2: scan for first L / R / AL, skipping isolates (LRI/RLI/FSI..PDI). Stop at
        // the first paragraph separator (B) outside any isolate — UAX #9 algorithms run
        // per-paragraph, and the first-strong scan must not bleed into a following
        // paragraph that the caller forgot to segment.
        var isolateDepth = 0;
        var i = 0;
        while (i < utf16Text.Length)
        {
            var codepoint = ReadCodepoint(utf16Text, ref i);
            var bc = BidiClassTable.GetClass(codepoint);

            switch (bc)
            {
                case BidiClass.LRI:
                case BidiClass.RLI:
                case BidiClass.FSI:
                    isolateDepth++;
                    continue;
                case BidiClass.PDI when isolateDepth > 0:
                    isolateDepth--;
                    continue;
                case BidiClass.B when isolateDepth == 0:
                    // End of the first paragraph in the input span. P3 default — no
                    // strong char found within this paragraph means level 0.
                    return 0;
            }

            if (isolateDepth > 0)
            {
                continue;
            }

            switch (bc)
            {
                case BidiClass.L:
                    return 0;
                case BidiClass.R:
                case BidiClass.AL:
                    return 1;
            }
        }

        // P3: no strong character found → default to LTR (level 0).
        return 0;
    }

    /// <summary>
    /// Read one Unicode codepoint from <paramref name="utf16Text"/> starting at
    /// <paramref name="index"/>, advancing the index past the consumed code units
    /// (1 for BMP, 2 for surrogate pairs). Lone surrogates are returned as-is —
    /// HarfBuzz applies the same replacement-character policy downstream.
    /// </summary>
    public static int ReadCodepoint(ReadOnlySpan<char> utf16Text, ref int index)
    {
        var c = utf16Text[index];
        if (char.IsHighSurrogate(c) && index + 1 < utf16Text.Length && char.IsLowSurrogate(utf16Text[index + 1]))
        {
            var cp = char.ConvertToUtf32(c, utf16Text[index + 1]);
            index += 2;
            return cp;
        }
        index += 1;
        return c;
    }
}
