// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Layout.Inline;

/// <summary>
/// Per Phase 3 Task 9 cycle 3b — CSS Text Module Level 3 §3
/// <c>white-space</c> property values that the inline pass honors.
/// All six CSS keywords have enum members (cycle 3 review User #3
/// added <see cref="BreakSpaces"/>). Behavior fidelity ladder:
/// <list type="bullet">
///   <item><b>Full spec fidelity</b> — <see cref="Normal"/>,
///   <see cref="Pre"/>, <see cref="NoWrap"/>, <see cref="PreWrap"/>,
///   <see cref="PreLine"/>: collapse / preserve / wrap semantics
///   honored end-to-end through preprocessing + wrap per CSS Text
///   L3 §3 Table.</item>
///   <item><b>Approximated</b> — <see cref="BreakSpaces"/>:
///   <b>currently treated as <see cref="PreWrap"/></b> in both the
///   preprocessor + wrap pass. The distinguishing CSS Text L3 §6.4
///   semantics (forced wrap candidates at EVERY preserved SP, plus
///   trailing-space wrap-vs-hang) are NOT yet implemented. Authored
///   whitespace is preserved correctly (the user-visible guarantee
///   for content), at the cost of less aggressive wrap candidate
///   placement at preserved spaces — a known fidelity gap. Tracked
///   for a subsequent cycle.</item>
/// </list>
///
/// <para><b>Per-source-run honoring (cycle 3c + 3d sub-cycle 1).</b>
/// <see cref="LineBuilder.Wrap"/> accepts an optional per-source-run
/// <c>whiteSpacePerRun</c> array that downgrades UAX #14 Allowed
/// opportunities to Prohibited for glyphs in <see cref="NoWrap"/> /
/// <see cref="Pre"/> source runs (cycle 3c per-glyph downgrade) +
/// gates the <c>overflow-wrap: anywhere</c> forced-break fallback by
/// per-glyph WhiteSpace (cycle 3d sub-cycle 1 review Rec #2).
/// <see cref="InlineLayouter.LayoutPerRun"/> builds this array
/// automatically for the <b>full six-value mismatch matrix</b>
/// (cycle 3d sub-cycle 1) — each source run is preprocessed with its
/// own WhiteSpace via <see cref="LineBuilder.PreprocessTextRunsPerRun"/>
/// while preserve-mode runs retain spaces and collapse-mode runs
/// chain <c>inWs</c> across boundaries. <see cref="BreakSpaces"/>
/// participates in the matrix via its PreWrap-equivalent
/// approximation.</para>
///
/// <para><b>Behavior summary (CSS Text L3 §3 Table).</b></para>
/// <list type="table">
///   <listheader>
///     <term>Mode</term>
///     <description>New lines · Spaces+tabs · Wrapping</description>
///   </listheader>
///   <item>
///     <term><see cref="Normal"/></term>
///     <description>collapse · collapse · wrap</description>
///   </item>
///   <item>
///     <term><see cref="NoWrap"/></term>
///     <description>collapse · collapse · NO wrap (UAX #14 Allowed
///     suppressed; only Mandatory honored)</description>
///   </item>
///   <item>
///     <term><see cref="Pre"/></term>
///     <description>preserve · preserve · NO wrap</description>
///   </item>
///   <item>
///     <term><see cref="PreWrap"/></term>
///     <description>preserve · preserve · wrap</description>
///   </item>
///   <item>
///     <term><see cref="PreLine"/></term>
///     <description>preserve · collapse · wrap</description>
///   </item>
/// </list>
/// </summary>
internal enum WhiteSpace : byte
{
    /// <summary>CSS default. Whitespace runs (SP/TAB/LF/CR/FF) collapse
    /// to a single SP; leading + trailing whitespace within the run is
    /// stripped; wrapping is allowed at UAX #14 break opportunities.</summary>
    Normal = 0,

    /// <summary>Preserve all whitespace (SP/TAB/LF/CR/FF unchanged).
    /// Suppress wrapping (UAX #14 Allowed treated as Prohibited;
    /// Mandatory still honored).</summary>
    Pre = 1,

    /// <summary>Like <see cref="Normal"/> but suppresses wrapping —
    /// the wrap pass treats UAX #14 Allowed opportunities as
    /// Prohibited; only Mandatory breaks split the line.</summary>
    NoWrap = 2,

    /// <summary>Preserve all whitespace (like <see cref="Pre"/>) but
    /// allow wrapping at UAX #14 Allowed opportunities (e.g., between
    /// preserved spaces).</summary>
    PreWrap = 3,

    /// <summary>Preserve LF/CR (segment breaks) but collapse
    /// SP/TAB runs to a single SP. Wrapping allowed.</summary>
    PreLine = 4,

    /// <summary>Per Phase 3 Task 10 cycle 3 review (User #3) —
    /// the CSS spec defines BreakSpaces (CSS Text L3 §3 Table 1 +
    /// §6.4) as: preserve all whitespace AND allow wrapping at
    /// every preserved space, with trailing spaces wrapping (rather
    /// than hanging) at line ends.
    ///
    /// <para><b>CURRENT BEHAVIOR — APPROXIMATION.</b> Both the
    /// preprocessor + wrap pass treat <see cref="BreakSpaces"/>
    /// identically to <see cref="PreWrap"/>: preserve all SP/TAB/
    /// LF/CR + wrap at UAX #14 Allowed opportunities. The
    /// distinguishing semantics — forced wrap candidates at EVERY
    /// preserved SP glyph + trailing-space wrap-vs-hang — are NOT
    /// implemented yet (would require synthesizing forced UAX #14
    /// candidates at every SP and a separate trailing-space
    /// pre-wrap pass). The approximation preserves authored
    /// whitespace correctly (the user-visible guarantee) at the
    /// cost of less aggressive wrap candidate placement. Per cycle
    /// 3d sub-cycle 1 review Rec #3, this fidelity gap is now
    /// explicitly documented (the prior "deferred to a subsequent
    /// cycle" framing implied a cleaner ladder than reality).</para>
    /// </summary>
    BreakSpaces = 5,
}
