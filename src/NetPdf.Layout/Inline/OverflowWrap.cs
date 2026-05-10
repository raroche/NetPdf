// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Layout.Inline;

/// <summary>
/// Per Phase 3 Task 9 cycle 3b sub-cycle 2 — CSS Text Module Level 3
/// §5.1 <c>overflow-wrap</c> property values that the inline wrap
/// pass honors. Cycle 3b sub-cycle 2 ships <see cref="Normal"/> +
/// <see cref="Anywhere"/>; <c>break-word</c> (a deprecated alias for
/// <see cref="Anywhere"/> with subtle min-content-size differences)
/// is deferred — most production callers use <see cref="Anywhere"/>.
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
///     CSS callers wanting to prevent overflow at any cost.</description>
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
    /// only fires when no Allowed break is available.</summary>
    Anywhere = 1,
}
