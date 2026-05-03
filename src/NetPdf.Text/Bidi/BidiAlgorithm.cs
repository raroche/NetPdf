// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Bidi;

/// <summary>
/// Entry point for the Unicode Bidirectional Algorithm (UAX #9, https://www.unicode.org/reports/tr9/).
/// Layout (Phase 3) calls <see cref="ResolveParagraphLevel"/> to determine the base
/// direction of a paragraph and (post-Stage-12.2) <see cref="ResolveLevels"/> to
/// produce per-character embedding levels for run segmentation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Implementation status.</b> Stages 12.1, 12.2, 12.3a, 12.3b, and 12.3c are all
/// shipped. <see cref="ResolveLevels"/> runs the full UAX #9 algorithm end-to-end —
/// X1–X10, BD13 isolating run sequences, W1–W7, N0 (BD16 paired brackets) / N1 / N2,
/// I1 / I2, and L1 trailing-whitespace reset. <see cref="ResolveParagraphLevel"/>
/// handles single-paragraph P2/P3 paragraph-level resolution (used by both this class
/// and CSS auto-direction inference).
/// </para>
/// <para>
/// <b>Multi-paragraph input.</b> <see cref="ResolveLevels"/> splits the input on
/// paragraph separators (UCD class B) per UAX #9 §3.3.1 P1 and runs the algorithm
/// independently on each paragraph with its own P2/P3-resolved paragraph level. The B
/// character itself stays with the preceding paragraph per the spec. Concatenated
/// output is byte-deterministic for byte-equal input.
/// </para>
/// <para>
/// <b>Stage 12.4 (next).</b> UCD <c>BidiTest.txt</c> + <c>BidiCharacterTest.txt</c>
/// conformance test integration; iterate to 100% pass per the Phase 1 exit criteria.
/// May co-occur with the deferred Stage 12.2.x Roslyn UCD source generator since both
/// depend on a checked-in UCD snapshot.
/// </para>
/// </remarks>
internal static class BidiAlgorithm
{
    /// <summary>
    /// Resolve only the paragraph embedding level (0 = LTR, 1 = RTL) for a paragraph of
    /// text per UAX #9 rules P2/P3. Useful when callers only need the base direction —
    /// e.g. CSS <c>direction</c> auto-detection — without per-character resolution.
    /// </summary>
    public static byte ResolveParagraphLevel(ReadOnlySpan<char> utf16Text, ParagraphDirection requested = ParagraphDirection.Auto)
    {
        return ParagraphLevelResolver.Resolve(utf16Text, requested);
    }

    /// <summary>
    /// Run the full algorithm: resolve paragraph level, then per-character embedding
    /// levels via the X / W / N / I / L rules. Returns one byte per UTF-16 code unit
    /// representing its final embedding level.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Output is one byte per UTF-16 code unit. Surrogate pairs share a level. Empty
    /// input returns an empty array.
    /// </para>
    /// <para>
    /// Multi-paragraph input is split on UCD class-B characters (LF, CR, NEL,
    /// PARAGRAPH SEPARATOR, FILE/GROUP/RECORD SEPARATOR, etc.) per UAX #9 §3.3.1 P1.
    /// Each paragraph runs the algorithm independently with its own P2/P3-resolved
    /// paragraph level — later paragraphs do NOT inherit the first paragraph's base
    /// level or any explicit-formatting state from earlier paragraphs.
    /// </para>
    /// </remarks>
    public static byte[] ResolveLevels(ReadOnlySpan<char> utf16Text, ParagraphDirection requested = ParagraphDirection.Auto)
    {
        if (utf16Text.IsEmpty)
        {
            return [];
        }

        var output = new byte[utf16Text.Length];
        var start = 0;
        while (start < utf16Text.Length)
        {
            var end = FindParagraphEnd(utf16Text, start);
            var slice = utf16Text[start..end];
            var paragraphLevel = ParagraphLevelResolver.Resolve(slice, requested);
            var chars = BidiPipeline.BuildCharInfos(slice);
            var levels = BidiPipeline.ResolveLevelsForUtf16(chars.AsSpan(), paragraphLevel, slice.Length);
            levels.AsSpan().CopyTo(output.AsSpan(start, slice.Length));
            start = end;
        }
        return output;
    }

    /// <summary>
    /// Find the end (exclusive) of the paragraph starting at <paramref name="start"/> —
    /// the position immediately after the first paragraph-separator unit, or the end of
    /// the text if no separator is found. Per UAX #9 §3.3.1 P1, the separator stays with
    /// the preceding paragraph.
    /// </summary>
    /// <remarks>
    /// <b>CRLF handling.</b> The Unicode standard does not explicitly call out CRLF as a
    /// single paragraph-break unit, but the de-facto convention across reference
    /// implementations (ICU, WeasyPrint, Pango, Chromium's bidi processor) is to treat
    /// the sequence U+000D U+000A as one paragraph break — both code units stay with the
    /// preceding paragraph at its base level. Without this, common Windows newline input
    /// would split into a spurious LF-only paragraph after the CR. Other multi-codepoint
    /// paragraph-break sequences are not defined by Unicode; every other class-B character
    /// is its own break.
    /// </remarks>
    private static int FindParagraphEnd(ReadOnlySpan<char> text, int start)
    {
        for (var i = start; i < text.Length; /* advance inside */)
        {
            int codepoint;
            int unitLen;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codepoint = char.ConvertToUtf32(text[i], text[i + 1]);
                unitLen = 2;
            }
            else
            {
                codepoint = text[i];
                unitLen = 1;
            }
            if (BidiClassTable.GetClass(codepoint) == BidiClass.B)
            {
                var endOfBreak = i + unitLen;
                // CRLF: extend the paragraph break to include the LF when CR is followed
                // by LF directly (single break unit, both code units stay with this paragraph).
                if (codepoint == '\r' && endOfBreak < text.Length && text[endOfBreak] == '\n')
                {
                    endOfBreak++;
                }
                return endOfBreak;
            }
            i += unitLen;
        }
        return text.Length;
    }
}
