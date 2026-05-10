// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Layout.Inline;

/// <summary>
/// Per Phase 3 Task 9 cycle 3b sub-cycle 3 — CSS Text Module Level 3
/// §6.1 <c>hyphens</c> property values that the inline wrap pass
/// honors. Cycle 3b sub-cycle 3 ships all three values, with
/// language-pack support deferred (en-US Liang patterns ship via
/// <c>NetPdf.Text.Hyphenation.EnUsHyphenation.Default</c>; other
/// languages land via future <c>NetPdf.Languages.*</c> packs).
///
/// <para><b>Behavior summary (CSS Text L3 §6.1).</b></para>
/// <list type="table">
///   <listheader>
///     <term>Mode</term>
///     <description>Treatment of soft-hyphens + Liang patterns</description>
///   </listheader>
///   <item>
///     <term><see cref="None"/></term>
///     <description>No hyphenation. Soft-hyphens (U+00AD) in source
///     are NOT break opportunities. Layout never inserts hyphens.</description>
///   </item>
///   <item>
///     <term><see cref="Manual"/></term>
///     <description>CSS default. Soft-hyphens (U+00AD) act as
///     break opportunities; Liang pattern auto-hyphenation is OFF.
///     The author controls where hyphens may appear via explicit
///     soft-hyphens in the source.</description>
///   </item>
///   <item>
///     <term><see cref="Auto"/></term>
///     <description>Soft-hyphens AND Liang pattern auto-hyphenation
///     both contribute break opportunities. The pattern set is
///     selected by language tag (en-US ships in-tree; other
///     languages via language packs).</description>
///   </item>
/// </list>
///
/// <para><b>Cycle 3b sub-cycle 3 simplifications.</b></para>
/// <list type="bullet">
///   <item>The visible-hyphen-on-break ("show U+2010 HYPHEN at the
///   end of the wrapping line, hide the soft-hyphen otherwise") is
///   not yet rendered — the painter cycle (Phase 4) will wire the
///   display-list IR to emit the hyphen glyph at break points.
///   Currently the soft-hyphen glyph stays in the drawable slice
///   like any other glyph.</item>
///   <item><c>hyphenate-character</c>, <c>hyphenate-limit-chars</c>,
///   <c>hyphenate-limit-lines</c>, <c>hyphenate-limit-zone</c>
///   properties (CSS Text L4) are deferred.</item>
///   <item>Auto mode applies en-US patterns regardless of language;
///   per-language pattern routing lands when Task 10's
///   <c>InlineLayouter</c> integrates per-source-TextRun language.</item>
/// </list>
/// </summary>
internal enum Hyphens : byte
{
    /// <summary>No hyphenation. Soft-hyphens are not treated as
    /// break opportunities.</summary>
    None = 0,

    /// <summary>CSS default. Soft-hyphens (U+00AD) are break
    /// opportunities; Liang pattern auto-hyphenation is OFF.</summary>
    Manual = 1,

    /// <summary>Soft-hyphens AND Liang pattern auto-hyphenation both
    /// contribute break opportunities.</summary>
    Auto = 2,
}
