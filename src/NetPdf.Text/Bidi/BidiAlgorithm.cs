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
/// <b>Implementation roadmap.</b> UAX #9 is a multi-pass algorithm that ICU implements
/// in ~5,000 lines. This module ships in incremental stages so each stage can be reviewed
/// and hardened before the next:
/// </para>
/// <list type="number">
///   <item>
///     <b>Stage 12.1 (shipped):</b> Foundation — bidi class enum, codepoint→class lookup
///     for ASCII / Latin-1 / Greek / Cyrillic / Hebrew / Arabic / explicit-formatting
///     characters; P2/P3 paragraph-level resolution; public API surface.
///   </item>
///   <item>
///     <b>Stage 12.2 (shipped):</b> UCD-derived bidi class table covering every assigned
///     Unicode block under a binary-search lookup; per-codepoint NSM/L distinction for
///     Devanagari, Thai, Tibetan, Myanmar, Balinese (combining-mark scripts where the
///     paragraph-direction inference depends on per-codepoint precision).
///   </item>
///   <item>
///     <b>Stage 12.3a (current):</b> Algorithm core — X1–X10 explicit/isolate processing
///     via the BD13 directional status stack; level-run + isolating-run-sequence
///     segmentation (BD7 / BD13). Foundation for the W/N/I rule passes.
///   </item>
///   <item>
///     <b>Stage 12.3b–d (next):</b> W1–W7 weak resolution; N0 paired brackets (BD16);
///     N1–N2 neutral resolution; I1–I2 implicit levels; L1 trailing-whitespace reset.
///     Lights up <see cref="ResolveLevels"/> for the full per-character output.
///   </item>
///   <item>
///     <b>Stage 12.4:</b> <c>BidiTest.txt</c> + <c>BidiCharacterTest.txt</c> integration;
///     iterate to 100% pass per the Phase 1 exit criteria.
///   </item>
/// </list>
/// <para>
/// Calling <see cref="ResolveLevels"/> before Stage 12.3b lands throws
/// <see cref="NotImplementedException"/> with a precise diagnostic so consumers know
/// they're depending on incomplete work.
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
    /// Output is one byte per UTF-16 code unit. Surrogate pairs share a level. Empty input
    /// returns an empty array.
    /// </remarks>
    public static byte[] ResolveLevels(ReadOnlySpan<char> utf16Text, ParagraphDirection requested = ParagraphDirection.Auto)
    {
        if (utf16Text.IsEmpty)
        {
            return [];
        }
        var paragraphLevel = ParagraphLevelResolver.Resolve(utf16Text, requested);
        var chars = BidiPipeline.BuildCharInfos(utf16Text);
        return BidiPipeline.ResolveLevelsForUtf16(chars.AsSpan(), paragraphLevel, utf16Text.Length);
    }
}
