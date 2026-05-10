// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Layout.Inline;

/// <summary>
/// Per Phase 3 Task 9 cycle 3b sub-cycle 2 — CSS Text Module Level 3
/// §5.2 <c>word-break</c> property values that the inline wrap pass
/// honors. Cycle 3b sub-cycle 2 ships <see cref="Normal"/> +
/// <see cref="BreakAll"/>; <see cref="KeepAll"/> is recognized but
/// has no observable effect for Latin / Cyrillic / Greek scripts
/// (which is v1's primary content envelope) — its CJK semantics
/// activate when UAX #24 script detection lands in cycle 4.
///
/// <para><b>Behavior summary (CSS Text L3 §5.2).</b></para>
/// <list type="table">
///   <listheader>
///     <term>Mode</term>
///     <description>Effect on wrap-time break opportunities</description>
///   </listheader>
///   <item>
///     <term><see cref="Normal"/></term>
///     <description>Honor UAX #14 break opportunities as-is.</description>
///   </item>
///   <item>
///     <term><see cref="BreakAll"/></term>
///     <description>Treat EVERY glyph boundary as a soft break
///     candidate (Allowed). Forces aggressive wrapping; useful for
///     fitting long unbreakable runs (URLs, code identifiers,
///     hex strings) into narrow columns.</description>
///   </item>
///   <item>
///     <term><see cref="KeepAll"/></term>
///     <description>Suppress UAX #14 inter-CJK-character break
///     opportunities (LB23/LB23a/LB28/LB30); only break at explicit
///     wordlike boundaries (SP, hyphen, etc.). For Latin/Cyrillic/
///     Greek content this behaves identically to <see cref="Normal"/>.
///     Cycle 4 will activate the CJK-specific suppression once
///     UAX #24 script detection lands.</description>
///   </item>
/// </list>
/// </summary>
internal enum WordBreak : byte
{
    /// <summary>CSS default. Honor UAX #14 break opportunities
    /// as-is.</summary>
    Normal = 0,

    /// <summary>Treat every glyph boundary as a soft-break
    /// opportunity. Aggressive wrapping for long unbreakable runs.</summary>
    BreakAll = 1,

    /// <summary>Suppress inter-CJK-character break opportunities.
    /// No observable effect for Latin/Cyrillic/Greek content (cycle
    /// 3b sub-cycle 2 simplification — meaningful CJK semantics
    /// require UAX #24 script detection in cycle 4).</summary>
    KeepAll = 2,
}
