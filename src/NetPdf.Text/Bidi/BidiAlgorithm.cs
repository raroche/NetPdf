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
/// in ~5,000 lines. This module ships in three stages so each stage can be reviewed
/// and hardened before the next:
/// </para>
/// <list type="number">
///   <item>
///     <b>Stage 12.1 (current):</b> Foundation — bidi class enum, codepoint→class lookup
///     for ASCII / Latin-1 / Greek / Cyrillic / Hebrew / Arabic / explicit-formatting
///     characters; P2/P3 paragraph-level resolution; public API surface.
///   </item>
///   <item>
///     <b>Stage 12.2:</b> Resolution rules — explicit levels (X1–X10), weak types
///     (W1–W7), neutral types (N0–N2 with paired brackets), implicit levels (I1–I2),
///     reordering (L1–L4).
///   </item>
///   <item>
///     <b>Stage 12.3:</b> Source-generator-emitted compressed bidi-class table over the
///     full UCD; <c>BidiTest.txt</c> + <c>BidiCharacterTest.txt</c> integration; iterate
///     to 100% pass per the phase exit criteria.
///   </item>
/// </list>
/// <para>
/// Calling <see cref="ResolveLevels"/> before Stage 12.2 lands throws
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
    /// Stage 12.2 ships the rule implementations; until then this throws
    /// <see cref="NotImplementedException"/>.
    /// </remarks>
    public static byte[] ResolveLevels(ReadOnlySpan<char> utf16Text, ParagraphDirection requested = ParagraphDirection.Auto)
    {
        _ = utf16Text;
        _ = requested;
        throw new NotImplementedException(
            "BidiAlgorithm.ResolveLevels is Stage 12.2 work — explicit / weak / neutral / implicit / reordering rules. " +
            "Stage 12.1 ships paragraph-level resolution only via ResolveParagraphLevel.");
    }
}
