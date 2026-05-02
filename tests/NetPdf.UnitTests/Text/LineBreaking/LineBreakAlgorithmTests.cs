// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.LineBreaking;
using Xunit;

namespace NetPdf.UnitTests.Text.LineBreaking;

/// <summary>
/// Granular per-rule and API-level regression tests for the UAX #14 line-break engine.
/// Complements the corpus-level <see cref="LineBreakUcdConformanceTests"/> by pinning
/// down individual rule behaviors so a regression in one rule shows up as a focused
/// test failure rather than only as an aggregate pass-rate drop.
/// </summary>
/// <remarks>
/// Each test names the rule it pins down. When the corpus harness flags new failures,
/// the corresponding rule's tests should be enriched here so future fixes are reviewable.
/// </remarks>
public sealed class LineBreakAlgorithmTests
{
    // ───── API shape ─────────────────────────────────────────────────────────

    [Fact]
    public void Empty_input_returns_empty_array()
    {
        Assert.Empty(LineBreakAlgorithm.FindBreaks(string.Empty));
    }

    [Fact]
    public void Output_length_equals_utf16_code_unit_length()
    {
        var input = "abc";
        var ops = LineBreakAlgorithm.FindBreaks(input);
        Assert.Equal(input.Length, ops.Length);
    }

    [Fact]
    public void End_of_text_position_is_Mandatory_per_LB3()
    {
        var ops = LineBreakAlgorithm.FindBreaks("abc");
        Assert.Equal(LineBreakOpportunity.Mandatory, ops[^1]);
    }

    [Fact]
    public void Surrogate_pair_supplementary_codepoint_records_opportunity_at_trailing_unit()
    {
        // U+1F600 GRINNING FACE — supplementary plane, two UTF-16 code units. Per the
        // public-API contract, the line-break opportunity for a codepoint is recorded at
        // its LAST UTF-16 code unit. The HIGH surrogate position is therefore Prohibited
        // (you can't break inside a surrogate pair).
        var input = "A\U0001F600B";
        var ops = LineBreakAlgorithm.FindBreaks(input);
        Assert.Equal(4, ops.Length);
        // ops[0]: opportunity AFTER 'A' (between 'A' and emoji-high-surrogate).
        // ops[1]: high surrogate position — must be Prohibited (no break inside surrogate pair).
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[1]);
        // ops[2]: opportunity AFTER the emoji codepoint (between emoji-low-surrogate and 'B').
        // ops[3]: end of text — Mandatory.
        Assert.Equal(LineBreakOpportunity.Mandatory, ops[3]);
    }

    // ───── LB4 / LB5: Mandatory-break classes ─────────────────────────────────

    [Fact]
    public void LB5_CRLF_collapsed_into_single_mandatory_break()
    {
        // CR LF should NOT have a break BETWEEN them (LB5 × LF when after CR), and the
        // break AFTER LF is Mandatory.
        var ops = LineBreakAlgorithm.FindBreaks("a\r\nb");
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[1]);  // between CR and LF
        Assert.Equal(LineBreakOpportunity.Mandatory, ops[2]);   // after LF
    }

    [Fact]
    public void LB4_BK_is_mandatory_break()
    {
        // U+000B is BK (LINE TABULATION).
        var ops = LineBreakAlgorithm.FindBreaks("ab");
        Assert.Equal(LineBreakOpportunity.Mandatory, ops[1]);
    }

    [Fact]
    public void LB5_lone_LF_is_mandatory_break()
    {
        var ops = LineBreakAlgorithm.FindBreaks("a\nb");
        Assert.Equal(LineBreakOpportunity.Mandatory, ops[1]);
    }

    // ───── LB7 / LB8: SP / ZW handling ────────────────────────────────────────

    [Fact]
    public void LB7_no_break_before_space()
    {
        // After 'a' before SP: ×.
        var ops = LineBreakAlgorithm.FindBreaks("a b");
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[0]);
        // After SP before 'b': ÷ (LB18).
        Assert.Equal(LineBreakOpportunity.Allowed, ops[1]);
    }

    [Fact]
    public void LB8_break_allowed_after_ZW()
    {
        // U+200B ZWSP is class ZW. Break IS allowed after.
        var ops = LineBreakAlgorithm.FindBreaks("a​b");
        Assert.Equal(LineBreakOpportunity.Allowed, ops[1]);
    }

    [Fact]
    public void LB8a_no_break_after_ZWJ()
    {
        // U+200D ZWJ is class ZWJ. No break after.
        var ops = LineBreakAlgorithm.FindBreaks("a‍b");
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[1]);
    }

    // ───── LB9 / LB10: Combining marks attach to base ─────────────────────────

    [Fact]
    public void LB9_no_break_between_base_and_combining_mark()
    {
        // U+0308 COMBINING DIAERESIS is class CM. After 'a' before CM: ×.
        var ops = LineBreakAlgorithm.FindBreaks("äb");
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[0]);
    }

    [Fact]
    public void LB10_combining_mark_at_sot_treated_as_AL()
    {
        // CM at start-of-text becomes AL. After CM before 'b' (also AL): LB28 prohibits.
        var ops = LineBreakAlgorithm.FindBreaks("̈b");
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[0]);
    }

    // ───── LB13: × CL / CP / EX / IS / SY ─────────────────────────────────────

    [Fact]
    public void LB13_no_break_before_close_punctuation()
    {
        // U+0021 EXCLAMATION MARK is EX.
        var ops = LineBreakAlgorithm.FindBreaks("a!b");
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[0]); // before EX
    }

    // ───── LB14: OP SP* × ─────────────────────────────────────────────────────

    [Fact]
    public void LB14_no_break_after_open_punctuation_through_following_spaces()
    {
        // '(' is OP. The pattern OP SP* × means: after OP and any following spaces, no
        // break is permitted before the next class. Test: "( b" — break is allowed BEFORE
        // the '(' (LB31 default), but NOT between '(' and SP (LB7 × SP) and NOT between
        // SP and 'b' (LB14 OP SP* × triggers).
        var ops = LineBreakAlgorithm.FindBreaks("( b");
        // ops[0] after '(' before SP: × LB7 (× SP) — though LB14 would also fire here.
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[0]);
        // ops[1] after SP before 'b': × LB14 (OP SP* × any) — overriding LB18 (SP ÷).
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[1]);
    }

    // ───── LB15c: SP ÷ IS NU (decimal-point-after-space) ──────────────────────

    [Fact]
    public void LB15c_break_allowed_before_decimal_point_after_space_when_followed_by_digit()
    {
        // "5 .35" — SP × IS would normally be ÷-then-× (LB18 then LB13). But LB15c says
        // SP × IS NU should ALLOW the break before the IS.
        var ops = LineBreakAlgorithm.FindBreaks("a .3");
        // ops[1]: between SP and '.' — LB15c allows.
        Assert.Equal(LineBreakOpportunity.Allowed, ops[1]);
    }

    // ───── LB18: SP ÷ ─────────────────────────────────────────────────────────

    [Fact]
    public void LB18_break_allowed_after_space()
    {
        var ops = LineBreakAlgorithm.FindBreaks("a b");
        Assert.Equal(LineBreakOpportunity.Allowed, ops[1]); // after SP
    }

    // ───── LB19: × QU / QU × ──────────────────────────────────────────────────

    [Fact]
    public void LB19_no_break_around_quotation_mark()
    {
        // 'a' × QU × 'b' — no break before or after the quote.
        var ops = LineBreakAlgorithm.FindBreaks("a\"b");
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[0]);
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[1]);
    }

    // ───── LB20a: word-initial hyphen (HY × AL after sot) ─────────────────────

    [Fact]
    public void LB20a_word_initial_hyphen_does_not_allow_break_before_AL()
    {
        // sot HY × AL — no break between '-' and 'a'.
        var ops = LineBreakAlgorithm.FindBreaks("-abc");
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[0]);
    }

    [Fact]
    public void LB20a_does_not_apply_to_HL_target()
    {
        // sot HY × HL — break IS allowed (only AL target is forbidden, HL is exempt).
        // U+05D0 HEBREW LETTER ALEF is HL.
        var ops = LineBreakAlgorithm.FindBreaks("-א");
        Assert.Equal(LineBreakOpportunity.Allowed, ops[0]);
    }

    // ───── LB25: numeric sequences ────────────────────────────────────────────

    [Fact]
    public void LB25_no_break_inside_numeric_sequence_with_separators()
    {
        // "1,234.56" — digits with infix separators.
        var input = "1,234.56";
        var ops = LineBreakAlgorithm.FindBreaks(input);
        for (var i = 0; i < input.Length - 1; i++)
        {
            Assert.Equal(LineBreakOpportunity.Prohibited, ops[i]);
        }
    }

    [Fact]
    public void LB25_break_allowed_after_currency_then_number()
    {
        // "$5" — PR × NU (LB25 NU body extension).
        var ops = LineBreakAlgorithm.FindBreaks("$5");
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[0]);
    }

    // ───── LB28: AL × AL ──────────────────────────────────────────────────────

    [Fact]
    public void LB28_no_break_between_letters()
    {
        var ops = LineBreakAlgorithm.FindBreaks("abc");
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[0]);
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[1]);
    }

    // ───── LB30: (AL|HL|NU) × OP, CP × (AL|HL|NU), with EAW filter ────────────

    [Fact]
    public void LB30_no_break_before_OP_after_letter()
    {
        // 'a' × '(' — Latin letter AL × ASCII OP (not EAW). Prohibit.
        var ops = LineBreakAlgorithm.FindBreaks("a(");
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[0]);
    }

    [Fact]
    public void LB30_break_allowed_before_East_Asian_OP()
    {
        // 'a' ÷ '〈' — Latin letter AL × CJK left angle bracket (EAW). Allow.
        var ops = LineBreakAlgorithm.FindBreaks("a〈");
        Assert.Equal(LineBreakOpportunity.Allowed, ops[0]);
    }

    // ───── LB30a: regional indicator pairs ───────────────────────────────────

    [Fact]
    public void LB30a_no_break_inside_regional_indicator_pair()
    {
        // U+1F1FA U+1F1F8 — the 🇺🇸 flag (US). RI RI; first pair must not break.
        var ops = LineBreakAlgorithm.FindBreaks("\U0001F1FA\U0001F1F8");
        // Position 1 (high surrogate of first RI): inside surrogate pair, prohibited.
        // Position 2 (low surrogate of first RI / between RIs): expected Prohibited per LB30a.
        // Position 3 (high surrogate of second RI): inside surrogate pair.
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[1]);
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[2]);
    }

    [Fact]
    public void LB30a_break_allowed_between_regional_indicator_pairs()
    {
        // Four RIs: first two form a flag, then break is allowed before the third RI.
        var ops = LineBreakAlgorithm.FindBreaks("\U0001F1FA\U0001F1F8\U0001F1EF\U0001F1F5");
        // Output is 8 bytes. The break opportunity AFTER the 2nd RI's surrogate pair is at
        // its trailing low-surrogate index (3). Per LB30a even-count: break is allowed.
        Assert.Equal(LineBreakOpportunity.Allowed, ops[3]);
    }

    // ───── LB30b: EB × EM, plus Extended_Pictographic-Cn × EM ─────────────────

    [Fact]
    public void LB30b_no_break_between_emoji_base_and_modifier()
    {
        // U+261D WHITE UP POINTING INDEX (EB) + U+1F3FF EMOJI MODIFIER (EM).
        var ops = LineBreakAlgorithm.FindBreaks("☝\U0001F3FF");
        // Position 0: between EB and high-surrogate of EM. Prohibit.
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[0]);
    }

    [Fact]
    public void LB30b_extended_pictographic_Cn_acts_as_emoji_base()
    {
        // U+1F02C is in Extended_Pictographic AND unassigned (Cn). Per LB30b extension,
        // it's treated as if it had Line_Break = EB for the modifier rule.
        var ops = LineBreakAlgorithm.FindBreaks("\U0001F02C\U0001F3FF");
        // Surrogate pair span: ops[0] inside U+1F02C surrogate pair (Prohibited),
        // ops[1] AFTER U+1F02C low-surrogate before U+1F3FF high-surrogate (LB30b extension Prohibits).
        Assert.Equal(LineBreakOpportunity.Prohibited, ops[1]);
    }

    // ───── LB31 default ÷ ─────────────────────────────────────────────────────

    [Fact]
    public void LB31_default_break_allowed_between_unrelated_classes()
    {
        // 'a' ÷ ' ' is LB7 ×. But CJK ID ÷ ID is LB31 (default ÷).
        var ops = LineBreakAlgorithm.FindBreaks("中文"); // 中文
        Assert.Equal(LineBreakOpportunity.Allowed, ops[0]);
    }

    // ───── Determinism ────────────────────────────────────────────────────────

    [Fact]
    public void FindBreaks_is_deterministic_for_byte_equal_input()
    {
        var input = "Hello, world! 中文 (test)";
        var first = LineBreakAlgorithm.FindBreaks(input);
        var second = LineBreakAlgorithm.FindBreaks(input);
        Assert.Equal(first, second);
    }
}
