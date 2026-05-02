// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.LineBreaking;
using Xunit;

namespace NetPdf.UnitTests.Text.LineBreaking;

/// <summary>
/// Pin-down tests for the LineBreakTest.txt 16.0 conformance failures. The full
/// corpus shows <b>8 failing cases</b>, but those reduce to <b>6 distinct minimal
/// patterns</b> (some patterns recur in multiple long-text corpus lines). Each test
/// names the failure category and asserts the CURRENT (incorrect) behavior so that
/// any fix that flips the behavior shows up as a focused test failure here, not
/// only as a pass-rate change in <see cref="LineBreakUcdConformanceTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// When a fix lands, the corresponding test here MUST be flipped to assert the spec-
/// correct behavior + the conformance pass-count constant in
/// <see cref="LineBreakUcdConformanceTests"/> bumped up. This forces a code review of
/// the fix's full impact rather than letting it slide as an aggregate-rate movement.
/// </para>
/// <para>
/// All 6 patterns involve non-Latin scripts (CJK ideographs / Brahmic Indic) that
/// fall outside NetPdf's primary English / Spanish / European production envelope.
/// They are tracked here for spec completeness, not because they affect the supported
/// product profile. The categories are documented in detail on
/// <see cref="LineBreakUcdConformanceTests"/>.
/// </para>
/// </remarks>
public sealed class LineBreakKnownFailuresTests
{
    // ───── East-Asian quotation cases (LB19a/b) — 5 corpus failures, 3 patterns ──────

    [Fact]
    public void Known_failure_LB19a_NS_EA_before_Pi_QU_NotEA_should_allow_but_currently_prohibits()
    {
        // Test corpus: × FF1A ÷ 201C (NS EastAsian before Pi-QU NotEastAsian). Spec rule
        // [999.0] (default ÷ via LB19a relaxation). Current LB19 "× QU" prohibits.
        // Recurs in 2 corpus lines (long Chinese-text quotations).
        // FF1A FULLWIDTH COLON (NS, F=EA-Wide); 201C LEFT DOUBLE QUOTATION MARK (Pi-QU, NotEA).
        var ops = LineBreakAlgorithm.FindBreaks("：“");
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[0]);
    }

    [Fact]
    public void Known_failure_LB19a_ID_W_before_Pi_QU_NotEA_should_allow_but_currently_prohibits()
    {
        // Test corpus: × 4E43 ÷ 201C (CJK ideograph EastAsian before Pi-QU NotEastAsian).
        // Spec rule [999.0] (default ÷ via LB19a relaxation). Current LB19 "× QU" prohibits.
        // Recurs in 2 corpus lines (different ideographs: 4E43, 5403 — same pattern).
        // 4E43 CJK UNIFIED IDEOGRAPH 乃 (ID, EAW=W); 201C LEFT DOUBLE QUOTATION MARK (Pi-QU, NotEA).
        var ops = LineBreakAlgorithm.FindBreaks("乃“");
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[0]);
    }

    [Fact]
    public void Known_failure_LB19b_Pf_QU_NotEA_before_ID_W_should_allow_but_currently_prohibits()
    {
        // Test corpus: × 201D ÷ 53F7 (Pf-QU NotEastAsian before CJK ideograph EastAsian).
        // Spec rule [999.0] (default ÷ via LB19b relaxation). Current LB19 "QU ×" prohibits.
        // 201D RIGHT DOUBLE QUOTATION MARK (Pf-QU, NotEA); 53F7 CJK UNIFIED IDEOGRAPH 号
        // (ID, EAW=W).
        var ops = LineBreakAlgorithm.FindBreaks("”号");
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[0]);
    }

    // ───── Brahmic-script LB28a edge cases — 3 corpus failures, 3 patterns ──────────

    [Fact]
    public void Known_failure_LB28a_Balinese_conjunct_with_ZWNJ_currently_allows_break()
    {
        // U+1B27 (Balinese letter A KARA, AK) + U+1B44 (Balinese ADEG, VI) + U+200C (ZWNJ)
        // + U+1B2B (Balinese letter, AK). The conjunct should not break inside even with
        // the ZWNJ. Currently the algorithm allows break around ZWNJ position.
        var ops = LineBreakAlgorithm.FindBreaks("ᬧ᭄‌ᬫ");
        // Position 2 (after ZWNJ) — pinned at Allowed (current behavior).
        Assert.Equal(LineBreakOpportunity.Allowed, ops[2]);
    }

    [Fact]
    public void Known_failure_LB28a_Sundanese_AK_after_AK_currently_allows_break()
    {
        // U+1BD7 + U+1BEC + U+1BD2 + U+1BEA + ... (Batak/Sundanese script). Spec prohibits
        // break in this conjunct sequence; current impl allows.
        var ops = LineBreakAlgorithm.FindBreaks("ᯗᯬᯒᯪᯉ᯳");
        // Position 3 (after U+1BEA): pinned at Allowed (current behavior; spec wants Prohibited).
        Assert.Equal(LineBreakOpportunity.Allowed, ops[3]);
    }

    [Fact]
    public void Known_failure_LB28a_DOTTED_CIRCLE_then_Javanese_currently_allows_break()
    {
        // U+25CC (DOTTED CIRCLE) followed by U+A9B3 (Javanese vowel sign), then more
        // Javanese chars. Spec prohibits break inside; current impl allows.
        var ops = LineBreakAlgorithm.FindBreaks("◌꦳꧀ꦠ");
        Assert.Equal(LineBreakOpportunity.Allowed, ops[1]);
    }

    // ───── Pi-QU + ZWSP + AL — RESOLVED ──────────────────────────────────────

    [Fact]
    public void Resolved_LB15b_Pi_QU_text_Pf_QU_ZWSP_AL_now_prohibits_break_correctly()
    {
        // From the long test: « Citation »​Klein. Position 10 (between SP and 00BB).
        // Spec [15.21] prohibits; LB15b follower set was missing ZW. Adding ZW (LB15b
        // accepts ZW as one of the followers that triggers the no-break-before-Pf-QU
        // rule) flipped this from Allowed → Prohibited and brings the corpus to 99.952%.
        var input = "« Citation »​Klein";
        var ops = LineBreakAlgorithm.FindBreaks(input);
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[10]);
    }
}
