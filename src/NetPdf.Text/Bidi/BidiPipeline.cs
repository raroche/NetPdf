// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Bidi.Rules;

namespace NetPdf.Text.Bidi;

/// <summary>
/// Internal orchestrator that runs the UAX #9 rule passes in order. Currently lights up
/// Stage 12.3a (X-rules + run/sequence segmentation); subsequent stages add W/N/I/L
/// rules as they land. <see cref="BidiAlgorithm.ResolveLevels"/> stays gated behind the
/// full algorithm but tests exercise the foundation through this class directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Conversion contract.</b> The public API takes <see cref="ReadOnlySpan{Char}"/>
/// (UTF-16 code units), but the bidi algorithm operates on Unicode codepoints. The
/// pipeline starts by walking the UTF-16 input and producing one
/// <see cref="BidiCharInfo"/> per codepoint (1 entry for BMP, 1 entry per surrogate pair).
/// The eventual public ResolveLevels output expands per-codepoint levels back to one
/// byte per UTF-16 code unit.
/// </para>
/// </remarks>
internal static class BidiPipeline
{
    /// <summary>
    /// Build the per-codepoint <see cref="BidiCharInfo"/> array from a UTF-16 input.
    /// Each entry carries its <see cref="BidiCharInfo.OriginalClass"/> (from
    /// <see cref="BidiClassTable.GetClass"/>) and the source-text offset / length so
    /// post-pipeline level expansion can map back to code units.
    /// </summary>
    public static BidiCharInfo[] BuildCharInfos(ReadOnlySpan<char> utf16Text)
    {
        var infos = new List<BidiCharInfo>(utf16Text.Length);
        for (var i = 0; i < utf16Text.Length; /* advance inside */)
        {
            int codepoint;
            byte unitLen;
            if (char.IsHighSurrogate(utf16Text[i]) && i + 1 < utf16Text.Length && char.IsLowSurrogate(utf16Text[i + 1]))
            {
                codepoint = char.ConvertToUtf32(utf16Text[i], utf16Text[i + 1]);
                unitLen = 2;
            }
            else
            {
                codepoint = utf16Text[i];
                unitLen = 1;
            }
            var cls = BidiClassTable.GetClass(codepoint);
            infos.Add(new BidiCharInfo
            {
                OriginalClass = cls,
                ResolvedClass = cls,
                Level = 0,
                IsRemovedByX9 = false,
                Utf16Index = i,
                Utf16Length = unitLen,
                Codepoint = codepoint,
            });
            i += unitLen;
        }
        return infos.ToArray();
    }

    /// <summary>
    /// Run the X-rules over <paramref name="chars"/> and produce the BD7 level runs +
    /// BD13 isolating run sequences. This is Stage 12.3a's deliverable; the W/N/I/L
    /// rule passes consume the sequences and transform the chars in place.
    /// </summary>
    public static (BidiLevelRun[] LevelRuns, BidiIsolatingRunSequence[] Sequences) RunX10AndSegment(
        Span<BidiCharInfo> chars,
        byte paragraphLevel)
    {
        BidiX10Resolver.Apply(chars, paragraphLevel);
        var levelRuns = BidiRunSegmenter.ComputeLevelRuns(chars);
        var sequences = BidiRunSegmenter.ComputeIsolatingRunSequences(chars, levelRuns, paragraphLevel);
        return (levelRuns, sequences);
    }

    /// <summary>
    /// Apply every UAX #9 rule pass — X1–X10, BD13 segmentation, W1–W7, N0/N1/N2,
    /// I1/I2, L1 — and expand per-codepoint <see cref="BidiCharInfo.Level"/> back to a
    /// per-UTF-16-code-unit byte array of length <paramref name="utf16Length"/> (the
    /// shape <see cref="BidiAlgorithm.ResolveLevels"/> returns).
    /// </summary>
    public static byte[] ResolveLevelsForUtf16(
        Span<BidiCharInfo> chars,
        byte paragraphLevel,
        int utf16Length)
    {
        var (_, sequences) = RunX10AndSegment(chars, paragraphLevel);
        foreach (var sequence in sequences)
        {
            BidiW7Resolver.Apply(chars, sequence);
            BidiN0BracketResolver.Apply(chars, sequence);
            BidiN12NeutralResolver.Apply(chars, sequence);
            BidiI12ImplicitResolver.Apply(chars, sequence);
        }
        BidiL1Resetter.Apply(chars, paragraphLevel);

        // Expand per-codepoint levels back to per-UTF-16-code-unit. Both halves of a
        // surrogate pair share the codepoint's level.
        var levels = new byte[utf16Length];
        for (var i = 0; i < chars.Length; i++)
        {
            var ch = chars[i];
            for (var k = 0; k < ch.Utf16Length; k++)
            {
                levels[ch.Utf16Index + k] = ch.Level;
            }
        }
        return levels;
    }
}
