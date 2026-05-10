// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Layout.Inline;

/// <summary>
/// Per Phase 3 Task 9 cycle 3b — CSS Text Module Level 3 §3
/// <c>white-space</c> property values that the inline pass honors.
/// All six CSS keywords have enum members (cycle 3 review User #3
/// added <see cref="BreakSpaces"/>). Behavior fidelity ladder:
/// <list type="bullet">
///   <item><see cref="Normal"/>, <see cref="Pre"/>,
///   <see cref="NoWrap"/>, <see cref="PreWrap"/>,
///   <see cref="PreLine"/> — full CSS Text L3 §3 semantics for
///   collapse / preserve / wrap honored end-to-end through
///   preprocessing + wrap.</item>
///   <item><see cref="BreakSpaces"/> — cycle-3 simplification:
///   behaves identically to <see cref="PreWrap"/> (preserve all
///   whitespace + wrap at UAX #14 Allowed opportunities). The
///   "wrap at every preserved space" + trailing-space wrap-vs-hang
///   detail per CSS Text L3 §6.4 lands in a subsequent cycle that
///   adds forced wrap candidates at every SP glyph. The simplified
///   behavior preserves authored whitespace correctly — the
///   user-visible guarantee — at the cost of slightly less
///   aggressive wrap candidate placement.</item>
/// </list>
///
/// <para><b>Per-source-run honoring.</b> Per Phase 3 Task 10
/// cycle 3c, <see cref="LineBuilder.Wrap"/> accepts an optional
/// per-source-run <c>whiteSpacePerRun</c> array that downgrades
/// UAX #14 Allowed opportunities to Prohibited for glyphs in
/// <see cref="NoWrap"/> / <see cref="Pre"/> source runs. The
/// <see cref="InlineLayouter.LayoutPerRun"/> facade builds this
/// array automatically when source TextRuns have mismatched
/// WhiteSpace values within the Normal/NoWrap matrix (both share
/// collapse semantics per CSS Text L3 §4.1); mixes involving
/// Pre/PreWrap/PreLine/BreakSpaces still require per-source-run
/// preprocessing (deferred to cycle 3d).</para>
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
    /// preserve all whitespace AND allow wrapping at every preserved
    /// space. Per CSS Text L3 §3 Table 1 + §6.4 — like
    /// <see cref="PreWrap"/> but trailing spaces wrap (rather than
    /// hang) at line ends. Cycle 3 simplification: behaves like
    /// PreWrap for now (preserve + wrap at UAX #14 Allowed
    /// opportunities); the "wrap at every preserved space" detail
    /// requires forced wrap candidates at every SP glyph + lands
    /// in a subsequent cycle. The simplification preserves
    /// authored whitespace correctly (which is the user-visible
    /// guarantee) at the cost of slightly less aggressive wrap
    /// candidate placement.</summary>
    BreakSpaces = 5,
}
