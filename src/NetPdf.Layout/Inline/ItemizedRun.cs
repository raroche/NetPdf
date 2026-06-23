// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Layout.Inline;

/// <summary>
/// Per Phase 3 Task 9 cycle 1 — one homogenous chunk of text after
/// itemization. An <see cref="ItemizedRun"/> has:
/// <list type="bullet">
///   <item>A single bidi level (= a single visual direction —
///   even-level runs are LTR, odd-level runs are RTL).</item>
///   <item>A single source <see cref="TextRun"/> (and therefore a
///   single <c>ComputedStyle</c>: font-family, font-size, color,
///   etc.).</item>
/// </list>
/// The line builder produces an <see cref="ItemizedRun"/> array as
/// the input to the shaping pass (cycle 2).
///
/// <para><b>UTF-16 indices.</b> <see cref="Utf16Start"/> +
/// <see cref="Utf16Length"/> are offsets into the line builder's
/// CONCATENATED input text (= the source TextRuns joined together).
/// <see cref="SourceTextRunIndex"/> identifies which source run the
/// chunk came from so the shaper can read its style.</para>
///
/// <para><b>Cycle 1 itemization rules.</b> A run boundary is created
/// when EITHER:
/// <list type="bullet">
///   <item>The bidi level changes (e.g., LTR English ↔ RTL Arabic
///   inside a paragraph).</item>
///   <item>The source TextRun changes (e.g., an
///   <c>&lt;em&gt;</c> child with a different font).</item>
/// </list>
/// Future cycles will add script-change boundaries (cycle 3 —
/// re-scoped from the original cycle-2 plan; cycle 2 wired up the
/// shaper but kept itemization at cycle-1 granularity. A run of
/// Latin can't share a HarfBuzz shaping pass with a run of Hebrew
/// even at the same bidi level + same font, because each script
/// has different OpenType feature requirements).</para>
/// </summary>
/// <param name="Utf16Start">UTF-16 code-unit start offset into the
/// line builder's concatenated input text.</param>
/// <param name="Utf16Length">UTF-16 code-unit length of this run
/// (number of code units, not codepoints — surrogate pairs count
/// as 2).</param>
/// <param name="BidiLevel">The bidi level for every char in the run.
/// Even = LTR; odd = RTL. The shaper reverses RTL runs at the glyph
/// level after shaping.</param>
/// <param name="SourceTextRunIndex">Index into the original source
/// TextRun array — the shaper reads
/// <c>sourceRuns[SourceTextRunIndex].Style</c> to get font / size /
/// color / etc.</param>
/// <param name="ScriptIso15924">The run's UAX #24 script as an ISO 15924
/// four-letter tag (cycle 3 — script-change itemization). <see langword="null"/>
/// when the run is all-Common (digits / punctuation / spaces) or the script
/// table doesn't cover it, in which case the shaper falls back to the caller's
/// uniform script. Even-handed with <see cref="BidiLevel"/> + style: a script
/// change opens a new run so each shaping pass uses one OpenType feature set.</param>
internal readonly record struct ItemizedRun(
    int Utf16Start,
    int Utf16Length,
    byte BidiLevel,
    int SourceTextRunIndex,
    string? ScriptIso15924 = null)
{
    /// <summary><see langword="true"/> when the run's bidi level is
    /// odd — i.e., the run is right-to-left.</summary>
    public bool IsRtl => (BidiLevel & 1) == 1;
}
