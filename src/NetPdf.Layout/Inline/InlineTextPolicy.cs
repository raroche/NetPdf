// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Layouters;

namespace NetPdf.Layout.Inline;

/// <summary>
/// Per Phase 3 Task 10 cycle 2 + post-cycle-2 review hardening — the
/// materialized inline-text policy for the wrap pass. Bundles
/// <see cref="WhiteSpace"/> + <see cref="OverflowWrap"/> +
/// <see cref="WordBreak"/> + <see cref="Hyphens"/> into one struct
/// that the integrating <c>InlineLayouter</c> (cycle 3) reads from
/// each source-TextRun's <see cref="ComputedStyle"/>.
///
/// <para><b>Why a separate materializer + struct?</b> The keyword-id
/// space the cascade stores in <see cref="ComputedSlot"/> is
/// property-local (each property has its own dense 0..N table); the
/// layout-side enums are property-local too but with explicit
/// alias-folding semantics for the cross-property keywords:</para>
/// <list type="bullet">
///   <item><c>overflow-wrap: break-word</c> per CSS Text L3 §5.1
///   produces glyph-boundary line-wrap opportunities (same as
///   <c>anywhere</c>) but those opportunities DO NOT count for
///   min-content sizing — the column min-content remains the full
///   word width. Phase 3 Task 12 sub-cycle 5 hardening Finding 5 —
///   the materializer now maps to a DISTINCT
///   <see cref="OverflowWrap.BreakWord"/> variant (was folded to
///   <see cref="OverflowWrap.Anywhere"/> pre-fix, which let auto-
///   table-layout's min-content pass narrow break-word columns to a
///   single glyph — incorrect per spec).</item>
///   <item><c>word-break: break-word</c> behaves as
///   <c>word-break: normal</c> PLUS <c>overflow-wrap: anywhere</c>
///   per CSS Text L3 §5.2 informative spec note. The materializer
///   sets <see cref="WordBreak"/> = <see cref="WordBreak.Normal"/>
///   AND bumps <see cref="OverflowWrap"/> to
///   <see cref="OverflowWrap.Anywhere"/>, regardless of what the
///   <c>overflow-wrap</c> property declared independently. (The §5.2
///   informative note explicitly maps to <c>anywhere</c>, NOT
///   <c>break-word</c> — so the spec-strict min-content distinction
///   from Finding 5 above flows: <c>word-break: break-word</c> sets
///   <see cref="OverflowWrap.Anywhere"/> with min-content
///   contribution, NOT the new
///   <see cref="OverflowWrap.BreakWord"/>.)</item>
///   <item><c>white-space: break-spaces</c> maps to
///   <see cref="WhiteSpace.BreakSpaces"/> (CSS Text L3 §6.4):
///   preserve like PreWrap, but the flat build adds a wrap
///   opportunity after EVERY preserved SP/TAB (break between
///   consecutive spaces) and trailing spaces take width (no hang).</item>
/// </list>
///
/// <para><b>Integration status.</b>
/// <list type="number">
///   <item>Cycle 3a shipped the uniform-policy <c>Layout</c>
///   overload reading <see cref="InlineTextPolicy"/> off the
///   containing-block <see cref="ComputedStyle"/>.</item>
///   <item>Cycle 3b shipped the per-source-TextRun
///   <c>LayoutPerRun</c> overload that reads each run's policy +
///   throws <see cref="System.NotSupportedException"/> on
///   mismatch — loud-fail semantics until per-glyph plumbing
///   lands.</item>
///   <item>Cycle 3c relaxed the throw for the Normal/NoWrap
///   WhiteSpace matrix only via a per-source-run <c>WhiteSpace</c>
///   array threaded through <see cref="LineBuilder.Wrap"/>'s
///   per-glyph downgrade.</item>
///   <item>Cycle 3d sub-cycle 1 broadened to the FULL six-value
///   WhiteSpace mismatch matrix via
///   <see cref="LineBuilder.PreprocessTextRunsPerRun"/> (per-source-
///   run preprocessor handling collapse-vs-preserve modes coherently).</item>
///   <item>Cycle 3d sub-cycle 2 broadened to OverflowWrap mismatches.
///   Replaced the cycle 3c <c>whiteSpacePerRun</c> + cycle 3d
///   sub-cycle 2 <c>overflowWrapPerRun</c> parallel arrays with a
///   single <see cref="LineBuilder.Wrap"/> <c>inlineTextPolicyPerRun:
///   IReadOnlyList&lt;InlineTextPolicy&gt;?</c> parameter that
///   bundles all 4 dimensions in one coherent struct (per
///   sub-cycle 2 review Rec #4).</item>
///   <item>Cycle 3d sub-cycle 3 broadened to WordBreak.BreakAll
///   mismatches via per-glyph BreakAll upgrade in the flat-build
///   phase. Cross-run BreakAll boundary uses "either side may opt
///   in" (sub-cycle 3 review Finding #3). KeepAll on mismatch
///   still THROWS pending UAX #24 script detection + LB30b
///   handling for CJK inter-character break suppression
///   (sub-cycle 3 review Finding #1).</item>
///   <item>Cycle 3d sub-cycle 4 broadened to Hyphens mismatches.
///   Per-source-run Hyphens flows through
///   <see cref="LineBuilder.Wrap"/>'s hyphenation pipeline:
///   soft-hyphen demotion (Hyphens=None) gates per concat
///   position; Liang application gates per word (apply only when
///   the word's first letter's source-run Hyphens=Auto). The
///   sub-cycle 3 Hyphens-must-match-global defense-in-depth guard
///   is removed. KeepAll on WordBreak mismatch is the only
///   remaining hard-throw case (CJK semantics deferred).</item>
/// </list>
/// </para></summary>
internal readonly record struct InlineTextPolicy(
    WhiteSpace WhiteSpace,
    OverflowWrap OverflowWrap,
    WordBreak WordBreak,
    Hyphens Hyphens)
{
    /// <summary>Default policy — matches CSS Text L3 initial values
    /// for an unstyled element. Useful as a fallback when a
    /// ComputedStyle hasn't been resolved yet.</summary>
    public static InlineTextPolicy Default => new(
        WhiteSpace.Normal, OverflowWrap.Normal, WordBreak.Normal, Hyphens.Manual);
}

/// <summary>
/// Per Phase 3 Task 10 cycle 2 + post-cycle-2 review hardening —
/// extension method <c>ReadInlineTextPolicy</c> that materializes
/// a <see cref="InlineTextPolicy"/> from a <see cref="ComputedStyle"/>.
/// The mapping is keyword-id driven against the source-gen'd
/// <c>NetPdf.Css.ComputedValues.PropertyResolvers.KeywordResolver</c>
/// tables. Pinned id contracts (per
/// <c>KeywordResolverTests.OverflowWrap_keyword_ids_are_pinned</c>
/// + companions) lock the mapping for downstream stability.
/// </summary>
internal static class InlineTextPolicyMaterializer
{
    /// <summary>Per cycle 2 hardening — decode the four CSS Text L3
    /// keyword properties off a <see cref="ComputedStyle"/> + apply
    /// the explicit alias-folding semantics described in
    /// <see cref="InlineTextPolicy"/>'s XML doc. Per CSS Text L3
    /// §6.1 (hyphens), §5.1 (overflow-wrap), §5.2 (word-break) +
    /// §3.1 (white-space).</summary>
    public static InlineTextPolicy ReadInlineTextPolicy(this ComputedStyle style)
    {
        // Keyword-id contracts pinned in KeywordResolverTests:
        //   WhiteSpace:   normal=0, pre=1, nowrap=2, pre-wrap=3,
        //                 break-spaces=4, pre-line=5
        //   OverflowWrap: normal=0, anywhere=1, break-word=2
        //   WordBreak:    normal=0, break-all=1, keep-all=2, break-word=3
        //   Hyphens:      none=0, manual=1, auto=2
        var whiteSpace = style.ReadKeywordOrDefault(PropertyId.WhiteSpace, 0) switch
        {
            0 => WhiteSpace.Normal,
            1 => WhiteSpace.Pre,
            2 => WhiteSpace.NoWrap,
            3 => WhiteSpace.PreWrap,
            // Per Phase 3 Task 10 cycle 3 review (User #3) — map
            // break-spaces to its own enum value (no longer silently
            // folds to Normal which would collapse spaces). Cycle 3
            // simplification: BreakSpaces behaves like PreWrap
            // (preserve + wrap at UAX #14 Allowed); the "wrap at
            // every preserved space" detail lands in a subsequent
            // cycle with forced wrap candidates.
            4 => WhiteSpace.BreakSpaces,
            5 => WhiteSpace.PreLine,
            _ => WhiteSpace.Normal,
        };

        var overflowWrap = style.ReadKeywordOrDefault(PropertyId.OverflowWrap, 0) switch
        {
            0 => OverflowWrap.Normal,
            1 => OverflowWrap.Anywhere,
            // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 5 —
            // break-word maps to its own enum variant so auto-table-
            // layout's min-content pass can honor the CSS Text L3 §5.1
            // semantics distinction (break-word's soft opportunities
            // don't count for min-content; anywhere's do). Line-wrap
            // behavior is identical to Anywhere; only intrinsic-sizing
            // measurement distinguishes the two.
            2 => OverflowWrap.BreakWord,
            _ => OverflowWrap.Normal,
        };

        var wordBreakKey = style.ReadKeywordOrDefault(PropertyId.WordBreak, 0);
        var wordBreak = wordBreakKey switch
        {
            0 => WordBreak.Normal,
            1 => WordBreak.BreakAll,
            2 => WordBreak.KeepAll,
            // word-break: break-word is a CROSS-PROPERTY alias per
            // CSS Text L3 §5.2 informative note: "On the contrary,
            // values of word-break that are equivalent to setting
            // overflow-wrap to anywhere are no longer specified".
            // Behavior: word-break stays Normal; overflow-wrap bumps
            // to Anywhere (handled below).
            3 => WordBreak.Normal,
            _ => WordBreak.Normal,
        };

        // Cross-property fold: word-break:break-word → bump
        // overflow-wrap to Anywhere irrespective of its own value.
        if (wordBreakKey == 3 && overflowWrap == OverflowWrap.Normal)
        {
            overflowWrap = OverflowWrap.Anywhere;
        }

        var hyphens = style.ReadKeywordOrDefault(PropertyId.Hyphens, 1) switch
        {
            0 => Hyphens.None,
            1 => Hyphens.Manual,
            2 => Hyphens.Auto,
            _ => Hyphens.Manual,
        };

        return new InlineTextPolicy(whiteSpace, overflowWrap, wordBreak, hyphens);
    }
}
