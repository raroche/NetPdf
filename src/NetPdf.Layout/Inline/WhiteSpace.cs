// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Layout.Inline;

/// <summary>
/// Per Phase 3 Task 9 cycle 3b — CSS Text Module Level 3 §3
/// <c>white-space</c> property values that the inline pass honors.
/// Cycle 3b sub-cycle 1 ships <see cref="Normal"/>, <see cref="Pre"/>,
/// <see cref="NoWrap"/>, <see cref="PreWrap"/>, <see cref="PreLine"/>.
/// <c>break-spaces</c> is deferred — wraps at every preserved
/// space which is rarely needed for v1's invoice / report use cases.
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
