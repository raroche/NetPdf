// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Bidi;
using NetPdf.Text.Bidi.Rules;
using Xunit;

namespace NetPdf.UnitTests.Text.Bidi.Rules;

/// <summary>
/// Granular per-rule unit tests for the W/N/I/L rule passes. Each test constructs a
/// minimal isolating run sequence over synthetic class data, runs ONE rule pass against
/// it, and asserts the per-character class / level output. Complements the broader
/// <see cref="BidiAlgorithmEndToEndTests"/> by isolating each rule from the others —
/// makes regression-finding easier when a future Stage 12.4 UCD validation surfaces a
/// failure tied to a specific rule.
/// </summary>
public sealed class BidiRulePassUnitTests
{
    private static BidiIsolatingRunSequence MakeSequence(
        BidiCharInfo[] chars,
        byte level,
        BidiClass sos,
        BidiClass eos)
    {
        var indices = new int[chars.Length];
        for (var i = 0; i < indices.Length; i++) indices[i] = i;
        var run = new BidiLevelRun { Indices = indices, Level = level };
        return new BidiIsolatingRunSequence
        {
            Runs = [run],
            Level = level,
            Sos = sos,
            Eos = eos,
            FlatIndices = indices,
        };
    }

    // ───── W1 — NSM resolution ────────────────────────────────────────────────

    [Fact]
    public void W1_NSM_at_start_of_sequence_takes_sos_type()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.NSM, BidiClass.L);
        var seq = MakeSequence(chars, 0, BidiClass.L, BidiClass.L);
        BidiW7Resolver.Apply(chars, seq);
        Assert.Equal(BidiClass.L, chars[0].ResolvedClass);
    }

    [Fact]
    public void W1_NSM_after_isolate_initiator_becomes_ON()
    {
        // RLI followed by NSM: NSM should become ON per W1 special case.
        var chars = BidiTestHelpers.FromClasses(BidiClass.RLI, BidiClass.NSM);
        var seq = MakeSequence(chars, 0, BidiClass.L, BidiClass.L);
        BidiW7Resolver.Apply(chars, seq);
        Assert.Equal(BidiClass.ON, chars[1].ResolvedClass);
    }

    [Fact]
    public void W1_NSM_after_PDI_becomes_ON()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.PDI, BidiClass.NSM);
        var seq = MakeSequence(chars, 0, BidiClass.L, BidiClass.L);
        BidiW7Resolver.Apply(chars, seq);
        Assert.Equal(BidiClass.ON, chars[1].ResolvedClass);
    }

    [Fact]
    public void W1_NSM_after_letter_inherits_letter_class()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.R, BidiClass.NSM, BidiClass.NSM);
        var seq = MakeSequence(chars, 1, BidiClass.R, BidiClass.R);
        BidiW7Resolver.Apply(chars, seq);
        Assert.Equal(BidiClass.R, chars[1].ResolvedClass);
        Assert.Equal(BidiClass.R, chars[2].ResolvedClass);
    }

    // ───── W2 — EN after AL becomes AN ────────────────────────────────────────

    [Fact]
    public void W2_EN_after_AL_becomes_AN()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.AL, BidiClass.EN);
        var seq = MakeSequence(chars, 1, BidiClass.R, BidiClass.R);
        BidiW7Resolver.Apply(chars, seq);
        Assert.Equal(BidiClass.AN, chars[1].ResolvedClass);
    }

    [Fact]
    public void W2_EN_with_no_strong_predecessor_uses_sos()
    {
        // No preceding strong; sos = R → no AL found → EN stays EN.
        var chars = BidiTestHelpers.FromClasses(BidiClass.EN);
        var seq = MakeSequence(chars, 1, BidiClass.R, BidiClass.R);
        BidiW7Resolver.Apply(chars, seq);
        // After W2, before W7: EN unchanged (sos = R, no AL).
        // W7 then sees sos = R → no L → EN stays EN.
        Assert.Equal(BidiClass.EN, chars[0].ResolvedClass);
    }

    // ───── W7 — EN after L becomes L ──────────────────────────────────────────

    [Fact]
    public void W7_EN_after_L_becomes_L()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.L, BidiClass.EN);
        var seq = MakeSequence(chars, 0, BidiClass.L, BidiClass.L);
        BidiW7Resolver.Apply(chars, seq);
        Assert.Equal(BidiClass.L, chars[1].ResolvedClass);
    }

    [Fact]
    public void W7_EN_after_R_stays_EN()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.R, BidiClass.EN);
        var seq = MakeSequence(chars, 1, BidiClass.R, BidiClass.R);
        BidiW7Resolver.Apply(chars, seq);
        Assert.Equal(BidiClass.EN, chars[1].ResolvedClass);
    }

    // ───── N1 — neutral runs at boundaries ────────────────────────────────────

    [Fact]
    public void N1_neutral_run_between_matching_strong_types_takes_their_direction()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.L, BidiClass.WS, BidiClass.L);
        var seq = MakeSequence(chars, 0, BidiClass.L, BidiClass.L);
        BidiN12NeutralResolver.Apply(chars, seq);
        Assert.Equal(BidiClass.L, chars[1].ResolvedClass);
    }

    [Fact]
    public void N1_neutral_at_sos_eos_takes_those_boundary_directions_when_matching()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.WS);
        var seq = MakeSequence(chars, 1, BidiClass.R, BidiClass.R);
        BidiN12NeutralResolver.Apply(chars, seq);
        Assert.Equal(BidiClass.R, chars[0].ResolvedClass);
    }

    [Fact]
    public void N2_neutral_run_with_mismatched_borders_takes_embedding_direction()
    {
        // sos = L, eos = R → N1 doesn't apply → N2 sets embedding direction (level 0 even → L).
        var chars = BidiTestHelpers.FromClasses(BidiClass.WS);
        var seq = MakeSequence(chars, 0, BidiClass.L, BidiClass.R);
        BidiN12NeutralResolver.Apply(chars, seq);
        Assert.Equal(BidiClass.L, chars[0].ResolvedClass);
    }

    [Fact]
    public void N1_treats_EN_AN_as_R_for_strong_type_comparison()
    {
        // R, WS, EN → leftStrong=R, rightStrong=R (EN treated as R) → both same → N1 sets WS to R.
        var chars = BidiTestHelpers.FromClasses(BidiClass.R, BidiClass.WS, BidiClass.EN);
        var seq = MakeSequence(chars, 1, BidiClass.R, BidiClass.R);
        BidiN12NeutralResolver.Apply(chars, seq);
        Assert.Equal(BidiClass.R, chars[1].ResolvedClass);
    }

    // ───── I1 — even level bumps ──────────────────────────────────────────────

    [Fact]
    public void I1_even_level_R_bumps_by_one()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.R);
        chars[0].Level = 0;
        var seq = MakeSequence(chars, 0, BidiClass.L, BidiClass.L);
        BidiI12ImplicitResolver.Apply(chars, seq);
        Assert.Equal(1, chars[0].Level);
    }

    [Fact]
    public void I1_even_level_EN_AN_bump_by_two()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.EN, BidiClass.AN);
        chars[0].Level = 0;
        chars[1].Level = 0;
        var seq = MakeSequence(chars, 0, BidiClass.L, BidiClass.L);
        BidiI12ImplicitResolver.Apply(chars, seq);
        Assert.Equal(2, chars[0].Level);
        Assert.Equal(2, chars[1].Level);
    }

    [Fact]
    public void I1_even_level_L_does_not_bump()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.L);
        chars[0].Level = 0;
        var seq = MakeSequence(chars, 0, BidiClass.L, BidiClass.L);
        BidiI12ImplicitResolver.Apply(chars, seq);
        Assert.Equal(0, chars[0].Level);
    }

    // ───── I2 — odd level bumps ───────────────────────────────────────────────

    [Fact]
    public void I2_odd_level_L_AN_EN_all_bump_by_one()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.L, BidiClass.AN, BidiClass.EN);
        chars[0].Level = 1;
        chars[1].Level = 1;
        chars[2].Level = 1;
        var seq = MakeSequence(chars, 1, BidiClass.R, BidiClass.R);
        BidiI12ImplicitResolver.Apply(chars, seq);
        Assert.Equal(2, chars[0].Level);
        Assert.Equal(2, chars[1].Level);
        Assert.Equal(2, chars[2].Level);
    }

    [Fact]
    public void I2_odd_level_R_does_not_bump()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.R);
        chars[0].Level = 1;
        var seq = MakeSequence(chars, 1, BidiClass.R, BidiClass.R);
        BidiI12ImplicitResolver.Apply(chars, seq);
        Assert.Equal(1, chars[0].Level);
    }

    // ───── L1 — trailing whitespace + B/S reset ───────────────────────────────

    [Fact]
    public void L1_resets_segment_separator_to_paragraph_level()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.L, BidiClass.S);
        chars[0].Level = 0;
        chars[1].Level = 5; // simulate inflated level
        BidiL1Resetter.Apply(chars, paragraphLevel: 0);
        Assert.Equal(0, chars[1].Level);
    }

    [Fact]
    public void L1_resets_paragraph_separator_to_paragraph_level()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.L, BidiClass.B);
        chars[1].Level = 7;
        BidiL1Resetter.Apply(chars, paragraphLevel: 0);
        Assert.Equal(0, chars[1].Level);
    }

    [Fact]
    public void L1_resets_trailing_whitespace_at_end_of_text()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.L, BidiClass.WS, BidiClass.WS);
        chars[1].Level = 3;
        chars[2].Level = 3;
        BidiL1Resetter.Apply(chars, paragraphLevel: 1);
        Assert.Equal(1, chars[1].Level);
        Assert.Equal(1, chars[2].Level);
    }

    [Fact]
    public void L1_resets_whitespace_run_preceding_paragraph_separator()
    {
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.L, BidiClass.WS, BidiClass.WS, BidiClass.B, BidiClass.L);
        chars[1].Level = 3;
        chars[2].Level = 3;
        chars[3].Level = 3;
        BidiL1Resetter.Apply(chars, paragraphLevel: 0);
        Assert.Equal(0, chars[1].Level);
        Assert.Equal(0, chars[2].Level);
        Assert.Equal(0, chars[3].Level);
    }

    [Fact]
    public void L1_does_not_reset_strong_chars()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.L, BidiClass.WS);
        chars[0].Level = 5;
        chars[1].Level = 5;
        BidiL1Resetter.Apply(chars, paragraphLevel: 0);
        Assert.Equal(5, chars[0].Level); // L is not resettable
        Assert.Equal(0, chars[1].Level); // WS resets
    }

    // ───── N0 paired brackets ─────────────────────────────────────────────────

    [Fact]
    public void N0_brackets_with_inner_strong_matching_embedding_direction_take_embedding()
    {
        // L0 sequence with parens around an L: brackets get embedding (L). U+0028='(', U+0029=')'.
        var chars = BidiTestHelpers.FromClassesAndCodepoints(
            (BidiClass.ON, 0x0028),
            (BidiClass.L, 'a'),
            (BidiClass.ON, 0x0029));
        var seq = MakeSequence(chars, 0, BidiClass.L, BidiClass.L);
        BidiN0BracketResolver.Apply(chars, seq);
        Assert.Equal(BidiClass.L, chars[0].ResolvedClass);
        Assert.Equal(BidiClass.L, chars[2].ResolvedClass);
    }

    [Fact]
    public void N0_brackets_with_only_opposing_strong_inside_consult_backward_context()
    {
        // L1 sequence with parens around an L (opposing). Backward context: sos = R. Per N0,
        // when only opposing is inside, the result is the opposing direction if the backward
        // context matches it, else the embedding direction. sos = R → embedding (R).
        var chars = BidiTestHelpers.FromClassesAndCodepoints(
            (BidiClass.ON, 0x0028),
            (BidiClass.L, 'a'),
            (BidiClass.ON, 0x0029));
        var seq = MakeSequence(chars, 1, BidiClass.R, BidiClass.R);
        BidiN0BracketResolver.Apply(chars, seq);
        Assert.Equal(BidiClass.R, chars[0].ResolvedClass);
        Assert.Equal(BidiClass.R, chars[2].ResolvedClass);
    }

    [Fact]
    public void N0_brackets_with_no_strong_inside_keep_ON_class()
    {
        var chars = BidiTestHelpers.FromClassesAndCodepoints(
            (BidiClass.ON, 0x0028),
            (BidiClass.WS, ' '),
            (BidiClass.ON, 0x0029));
        var seq = MakeSequence(chars, 0, BidiClass.L, BidiClass.L);
        BidiN0BracketResolver.Apply(chars, seq);
        // Brackets stay ON; N1/N2 will resolve them later in the full pipeline.
        Assert.Equal(BidiClass.ON, chars[0].ResolvedClass);
        Assert.Equal(BidiClass.ON, chars[2].ResolvedClass);
    }

    [Fact]
    public void N0_unpaired_bracket_remains_unchanged()
    {
        // Just an opener with no closer — BD16 leaves it unpaired and N0 does nothing.
        var chars = BidiTestHelpers.FromClassesAndCodepoints(
            (BidiClass.ON, 0x0028),
            (BidiClass.L, 'a'));
        var seq = MakeSequence(chars, 0, BidiClass.L, BidiClass.L);
        BidiN0BracketResolver.Apply(chars, seq);
        Assert.Equal(BidiClass.ON, chars[0].ResolvedClass);
    }
}
