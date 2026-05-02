// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.LineBreaking;
using Xunit;

namespace NetPdf.UnitTests.Text.LineBreaking;

/// <summary>
/// Pin-down tests for the 9 currently-known LineBreakTest.txt 16.0 conformance failures.
/// Each test names the failure category and asserts the CURRENT (incorrect) behavior so
/// that any fix that flips the behavior shows up as a focused test failure here, not
/// only as a pass-rate change in <see cref="LineBreakUcdConformanceTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// When a fix lands, the corresponding test here MUST be flipped to assert the spec-
/// correct behavior + the conformance pass-count constant in
/// <see cref="LineBreakUcdConformanceTests"/> bumped up. This forces a code review of the
/// fix's full impact rather than letting it slide as an aggregate-rate movement.
/// </para>
/// <para>
/// The categories are documented in the class XML on
/// <see cref="LineBreakUcdConformanceTests"/>.
/// </para>
/// </remarks>
public sealed class LineBreakKnownFailuresTests
{
    // ───── East-Asian quotation cases (LB19a/b) — 5 failures ──────────────────

    [Fact]
    public void Known_failure_LB19a_NS_before_Pi_QU_should_allow_but_currently_prohibits()
    {
        // Test corpus: × FF1A ÷ 201C (NS before Pi-QU NotEastAsian). Spec rule [999.0]
        // (default ÷). Current implementation fires LB19 "× QU" → Prohibit.
        // Pinned: when LB19a is implemented properly, flip this to Assert.Equal(Allowed).
        var ops = LineBreakAlgorithm.FindBreaks("：“");
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[0]);
    }

    [Fact]
    public void Known_failure_LB19b_Pf_QU_before_EA_AL_should_allow_but_currently_prohibits()
    {
        // Test corpus: × 201D ÷ 53F7 (Pf-QU NotEastAsian before AL EastAsian). Spec rule
        // [999.0] (default ÷ via LB19b relaxation). Current LB19 "QU ×" prohibits.
        // 201D RIGHT DOUBLE QUOTATION MARK (Pf-QU); 53F7 CJK UNIFIED IDEOGRAPH (ID, EAW).
        var ops = LineBreakAlgorithm.FindBreaks("”号");
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[0]);
    }

    // ───── Brahmic-script LB28a edge cases — 3 failures ───────────────────────

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

    // ───── Pi-QU + ZWSP + AL edge case — 1 failure ────────────────────────────

    [Fact]
    public void Known_failure_LB15_Pi_QU_text_Pf_QU_ZWSP_AL_currently_allows_break()
    {
        // From the long test: « Citation » ​ Klein. Position 10 (between SP and 00BB).
        // Spec [15.21] prohibits; current impl allows.
        var input = "« Citation »​Klein";
        var ops = LineBreakAlgorithm.FindBreaks(input);
        Assert.Equal(LineBreakOpportunity.Allowed, ops[10]);
    }
}
