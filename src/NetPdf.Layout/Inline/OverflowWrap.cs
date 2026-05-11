// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Layout.Inline;

/// <summary>
/// Per Phase 3 Task 9 cycle 3b sub-cycle 2 — CSS Text Module Level 3
/// §5.1 <c>overflow-wrap</c> property values that the inline wrap
/// pass honors. Cycle 3b sub-cycle 2 ships <see cref="Normal"/> +
/// <see cref="Anywhere"/>; Phase 3 Task 12 sub-cycle 5 hardening
/// Finding 5 adds <see cref="BreakWord"/> as a distinct variant so
/// that auto-table-layout's min-content measurement can distinguish
/// the two CSS Text L3 §5.1 semantics:
/// <list type="bullet">
///   <item><c>overflow-wrap: anywhere</c> — soft glyph-break
///     opportunities COUNT for min-content sizing (the algorithm
///     pretends every glyph boundary is a soft-break opportunity).</item>
///   <item><c>overflow-wrap: break-word</c> — soft glyph-break
///     opportunities only affect LINE-WRAP, NOT min-content sizing
///     (CSS Text L3 §5.1 explicitly carves this out).</item>
/// </list>
/// At line-wrap time both variants behave identically (force a glyph
/// boundary break when no UAX #14 Allowed candidate exists); the
/// distinction matters for intrinsic-sizing measurement only.
///
/// <para><b>Behavior summary.</b></para>
/// <list type="table">
///   <listheader>
///     <term>Mode</term>
///     <description>When line overflows + no UAX #14 Allowed candidate exists</description>
///   </listheader>
///   <item>
///     <term><see cref="Normal"/></term>
///     <description>Allow overflow (cycle 3a fallback — line keeps
///     growing past the budget; the painter clips per
///     <c>overflow:hidden</c> or lets the content visually escape).</description>
///   </item>
///   <item>
///     <term><see cref="Anywhere"/></term>
///     <description>Force a break at the previous glyph boundary —
///     wrap as if every glyph were a soft-break candidate. Used by
///     CSS callers wanting to prevent overflow at any cost. Per CSS
///     Text L3 §5.1 the soft opportunities ALSO contribute to
///     min-content sizing — the column min-content can be the width
///     of a single glyph.</description>
///   </item>
///   <item>
///     <term><see cref="BreakWord"/></term>
///     <description>Same line-wrap behavior as
///     <see cref="Anywhere"/>: force a glyph-boundary break when the
///     line would otherwise overflow. BUT per CSS Text L3 §5.1 the
///     soft opportunities DO NOT count for min-content sizing — the
///     column min-content remains the full word width. Auto-table-
///     layout reads this distinction during its speculative min-
///     content measurement pass.</description>
///   </item>
/// </list>
/// </summary>
internal enum OverflowWrap : byte
{
    /// <summary>CSS default. Overflow allowed when no UAX #14
    /// Allowed-break candidate exists in the line.</summary>
    Normal = 0,

    /// <summary>Force a break at any glyph boundary when the line
    /// would otherwise overflow + no candidate exists. Preserves
    /// UAX #14 candidates as preferred — the per-glyph fallback
    /// only fires when no Allowed break is available. Per CSS Text
    /// L3 §5.1 soft opportunities also count for min-content
    /// sizing.</summary>
    Anywhere = 1,

    /// <summary>Per Phase 3 Task 12 sub-cycle 5 hardening Finding 5 —
    /// same line-wrap behavior as <see cref="Anywhere"/> (glyph-
    /// boundary break when no UAX #14 Allowed candidate fits), but
    /// per CSS Text L3 §5.1 soft opportunities introduced by
    /// <c>break-word</c> do NOT count for min-content sizing. The
    /// min-content of a <c>break-word</c> column is the full word
    /// width, not a single glyph. Auto-table-layout reads this
    /// distinction during its speculative min-content measurement
    /// pass via <see cref="LineBuilder.Wrap"/>'s
    /// <c>intrinsicSizingMode</c> flag.</summary>
    BreakWord = 2,
}
