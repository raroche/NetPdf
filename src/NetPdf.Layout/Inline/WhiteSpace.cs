// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Layout.Inline;

/// <summary>
/// Per Phase 3 Task 9 cycle 3b — CSS Text Module Level 3 §3
/// <c>white-space</c> property values that the inline pass honors.
/// All six CSS keywords have enum members and are honored end-to-end
/// (cycle 3 review User #3 added <see cref="BreakSpaces"/>; the
/// white-space-break-spaces cycle gave it its distinguishing wrap behavior):
/// <list type="bullet">
///   <item><b>Full spec fidelity</b> — <see cref="Normal"/>,
///   <see cref="Pre"/>, <see cref="NoWrap"/>, <see cref="PreWrap"/>,
///   <see cref="PreLine"/>: collapse / preserve / wrap semantics
///   honored end-to-end through preprocessing + wrap per CSS Text
///   L3 §3 Table.</item>
///   <item><see cref="BreakSpaces"/> (CSS Text L3 §6.4): preserve like
///   <see cref="PreWrap"/>, but the flat-build adds a wrap opportunity
///   AFTER every preserved SP/TAB (break between consecutive spaces),
///   and trailing spaces take up width (no hang). See its member doc.</item>
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
/// participates in the matrix as a preserve mode, with its per-space
/// break-after upgrade applied per source run in the flat build.</para>
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

    /// <summary>CSS Text L3 §3 Table 1 + §6.4 — preserve all whitespace AND allow wrapping
    /// after EVERY preserved space, with trailing spaces taking up width (not hanging) at line
    /// ends. Like <see cref="PreWrap"/> except for the per-space break opportunities.
    ///
    /// <para><b>BEHAVIOR.</b> The flat-build phase (<c>LineBuilder</c>) upgrades each preserved
    /// SP/TAB glyph in a break-spaces source run to a UAX #14 <c>Allowed</c> break-after — so the
    /// wrap pass can break between consecutive spaces, not just after the whole space sequence
    /// (the pre-wrap behavior). Preserve-mode spaces are never trimmed (<c>IsBreakSpace</c> stays
    /// false), so trailing break-spaces spaces keep their advance and wrap rather than hang. The
    /// whitespace-preprocessing pass shares <see cref="PreWrap"/>'s preserve rules (both keep all
    /// SP/TAB/LF/CR); only the wrap-opportunity placement differs.</para>
    /// </summary>
    BreakSpaces = 5,
}
