// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Bidi;
using NetPdf.Text.Bidi.Rules;
using Xunit;

namespace NetPdf.UnitTests.Text.Bidi.Rules;

/// <summary>
/// UAX #9 §3.3.2 X-rule unit tests. Inputs are synthetic bidi-class sequences (not real
/// codepoints) so each rule branch is exercised independently of the UCD lookup table.
/// </summary>
public sealed class BidiX10ResolverTests
{
    // ───── X1 — initialization, default LTR paragraph ────────────────────────

    [Fact]
    public void Plain_LTR_paragraph_keeps_all_chars_at_paragraph_level()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.L, BidiClass.L, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        Assert.All(chars, c => Assert.Equal(0, c.Level));
        Assert.All(chars, c => Assert.False(c.IsRemovedByX9));
    }

    [Fact]
    public void Plain_RTL_paragraph_keeps_all_chars_at_paragraph_level_1()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.R, BidiClass.R, BidiClass.R);
        BidiX10Resolver.Apply(chars, paragraphLevel: 1);
        Assert.All(chars, c => Assert.Equal(1, c.Level));
    }

    // ───── X2 — RLE pushes the next odd level ─────────────────────────────────

    [Fact]
    public void RLE_pushes_next_odd_level_then_PDF_pops()
    {
        // L RLE R PDF L
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.L, BidiClass.RLE, BidiClass.R, BidiClass.PDF, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        Assert.Equal(0, chars[0].Level);
        Assert.True(chars[1].IsRemovedByX9);          // RLE removed
        Assert.Equal(1, chars[2].Level);              // R inside the RLE — odd level 1
        Assert.True(chars[3].IsRemovedByX9);          // PDF removed
        Assert.Equal(0, chars[4].Level);              // back at paragraph level
    }

    // ───── X3 — LRE pushes the next even level ────────────────────────────────

    [Fact]
    public void LRE_inside_RTL_paragraph_pushes_next_even_level()
    {
        // R LRE L PDF R
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.R, BidiClass.LRE, BidiClass.L, BidiClass.PDF, BidiClass.R);
        BidiX10Resolver.Apply(chars, paragraphLevel: 1);
        Assert.Equal(1, chars[0].Level);
        Assert.True(chars[1].IsRemovedByX9);
        Assert.Equal(2, chars[2].Level);              // L inside LRE — even level 2 above paragraph 1
        Assert.True(chars[3].IsRemovedByX9);
        Assert.Equal(1, chars[4].Level);
    }

    // ───── X4 — RLO overrides class to R ──────────────────────────────────────

    [Fact]
    public void RLO_overrides_class_to_R_on_inner_chars()
    {
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.L, BidiClass.RLO, BidiClass.L, BidiClass.L, BidiClass.PDF, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        // chars[0] outside the override
        Assert.Equal(BidiClass.L, chars[0].ResolvedClass);
        Assert.Equal(0, chars[0].Level);
        // chars[1] is RLO itself — removed
        Assert.True(chars[1].IsRemovedByX9);
        // chars[2] and chars[3] under RLO override — resolved to R, level 1
        Assert.Equal(BidiClass.R, chars[2].ResolvedClass);
        Assert.Equal(BidiClass.R, chars[3].ResolvedClass);
        Assert.Equal(1, chars[2].Level);
        Assert.Equal(1, chars[3].Level);
        // chars[4] PDF — removed
        Assert.True(chars[4].IsRemovedByX9);
        // chars[5] back to paragraph
        Assert.Equal(BidiClass.L, chars[5].ResolvedClass);
        Assert.Equal(0, chars[5].Level);
    }

    // ───── X5 — LRO overrides class to L ──────────────────────────────────────

    [Fact]
    public void LRO_overrides_inner_chars_resolved_class_to_L()
    {
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.R, BidiClass.LRO, BidiClass.R, BidiClass.AL, BidiClass.PDF, BidiClass.R);
        BidiX10Resolver.Apply(chars, paragraphLevel: 1);
        Assert.Equal(BidiClass.R, chars[0].ResolvedClass);
        Assert.True(chars[1].IsRemovedByX9);
        Assert.Equal(BidiClass.L, chars[2].ResolvedClass);
        Assert.Equal(BidiClass.L, chars[3].ResolvedClass);
        Assert.Equal(2, chars[2].Level); // even level above 1
        Assert.True(chars[4].IsRemovedByX9);
        Assert.Equal(BidiClass.R, chars[5].ResolvedClass);
    }

    // ───── X5a — RLI assigns its own level then pushes ────────────────────────

    [Fact]
    public void RLI_assigns_top_level_to_itself_then_pushes_new_odd_level()
    {
        // L RLI R PDI L
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.L, BidiClass.RLI, BidiClass.R, BidiClass.PDI, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        Assert.Equal(0, chars[0].Level);
        Assert.Equal(0, chars[1].Level);              // RLI itself: top level (paragraph) = 0
        Assert.False(chars[1].IsRemovedByX9);         // isolate initiators are NOT X9-removed
        Assert.Equal(1, chars[2].Level);              // R inside RLI — odd level 1
        Assert.Equal(0, chars[3].Level);              // PDI: back to top after pop = 0
        Assert.Equal(0, chars[4].Level);
    }

    // ───── X5b — LRI in RTL paragraph ─────────────────────────────────────────

    [Fact]
    public void LRI_inside_RTL_paragraph_pushes_next_even_level()
    {
        // R LRI L PDI R
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.R, BidiClass.LRI, BidiClass.L, BidiClass.PDI, BidiClass.R);
        BidiX10Resolver.Apply(chars, paragraphLevel: 1);
        Assert.Equal(1, chars[0].Level);
        Assert.Equal(1, chars[1].Level);              // LRI itself at paragraph level 1
        Assert.Equal(2, chars[2].Level);              // L inside LRI — even level 2
        Assert.Equal(1, chars[3].Level);
        Assert.Equal(1, chars[4].Level);
    }

    // ───── X5c — FSI inferring direction ──────────────────────────────────────

    [Fact]
    public void FSI_with_first_strong_R_behaves_like_RLI()
    {
        // FSI R PDI
        var chars = BidiTestHelpers.FromClasses(BidiClass.FSI, BidiClass.R, BidiClass.PDI);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        Assert.Equal(0, chars[0].Level);              // FSI itself at paragraph level
        Assert.Equal(1, chars[1].Level);              // R inside FSI — odd
    }

    [Fact]
    public void FSI_with_first_strong_L_behaves_like_LRI()
    {
        // R FSI L PDI R   (paragraph RTL)
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.R, BidiClass.FSI, BidiClass.L, BidiClass.PDI, BidiClass.R);
        BidiX10Resolver.Apply(chars, paragraphLevel: 1);
        Assert.Equal(1, chars[0].Level);
        Assert.Equal(1, chars[1].Level);              // FSI at paragraph level 1
        Assert.Equal(2, chars[2].Level);              // L inside FSI behaving as LRI — even level 2
    }

    [Fact]
    public void FSI_with_no_strong_chars_defaults_to_LRI()
    {
        // FSI WS PDI
        var chars = BidiTestHelpers.FromClasses(BidiClass.FSI, BidiClass.WS, BidiClass.PDI);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        Assert.Equal(0, chars[0].Level);
        // WS inside FSI-as-LRI would still be at level (top + LRI-push). Top after FSI push
        // is the next-even level above paragraph 0 — that's 2.
        Assert.Equal(2, chars[1].Level);
    }

    // ───── X6 — generic class application + override ──────────────────────────

    [Fact]
    public void Class_AL_inside_RLE_keeps_AL_class_with_neutral_override()
    {
        // L RLE AL PDF L
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.L, BidiClass.RLE, BidiClass.AL, BidiClass.PDF, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        Assert.Equal(BidiClass.AL, chars[2].ResolvedClass); // RLE has neutral override
        Assert.Equal(1, chars[2].Level);
    }

    // ───── X7 — PDF without preceding RLE/LRE/RLO/LRO is no-op ────────────────

    [Fact]
    public void PDF_without_matching_open_does_not_affect_stack()
    {
        // L PDF L
        var chars = BidiTestHelpers.FromClasses(BidiClass.L, BidiClass.PDF, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        Assert.Equal(0, chars[0].Level);
        Assert.True(chars[1].IsRemovedByX9);
        Assert.Equal(0, chars[2].Level);              // unchanged — no over-pop
    }

    // ───── X8 — paragraph separator B ─────────────────────────────────────────

    [Fact]
    public void B_assigns_paragraph_level_regardless_of_embedding_context()
    {
        // L RLE B PDF L
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.L, BidiClass.RLE, BidiClass.B, BidiClass.PDF, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        Assert.Equal(0, chars[2].Level);              // B uses paragraph level, not stack top
    }

    // ───── X9 — BN is removed ─────────────────────────────────────────────────

    [Fact]
    public void BN_is_marked_removed_with_top_level()
    {
        // L BN L
        var chars = BidiTestHelpers.FromClasses(BidiClass.L, BidiClass.BN, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        Assert.True(chars[1].IsRemovedByX9);
        Assert.Equal(0, chars[1].Level);
    }

    // ───── BD2 — embedding depth cap ──────────────────────────────────────────

    [Fact]
    public void Embedding_overflow_above_BD2_cap_does_not_grow_stack()
    {
        // Open enough RLEs to exceed BD2 cap (125). After cap, further opens are tracked
        // as overflow without pushing. Then strong chars at the bottom should still resolve
        // to a sensible level (not 125+ or out-of-range).
        var classes = new List<BidiClass>();
        for (var i = 0; i < 130; i++)
        {
            classes.Add(BidiClass.RLE);
        }
        classes.Add(BidiClass.R);
        for (var i = 0; i < 130; i++)
        {
            classes.Add(BidiClass.PDF);
        }
        classes.Add(BidiClass.L);
        var chars = BidiTestHelpers.FromClasses(classes.ToArray());
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        Assert.True(chars[130].Level <= 125);         // R inside the 130th RLE: capped at ≤125
        Assert.Equal(0, chars[^1].Level);             // final L back at paragraph level after balanced pops
    }

    // ───── PDI without matching isolate — no stack change ─────────────────────

    [Fact]
    public void Unmatched_PDI_does_not_pop_stack_or_affect_following_chars()
    {
        // L PDI L
        var chars = BidiTestHelpers.FromClasses(BidiClass.L, BidiClass.PDI, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        Assert.Equal(0, chars[0].Level);
        Assert.Equal(0, chars[1].Level);              // PDI gets top level
        Assert.Equal(0, chars[2].Level);
    }

    // ───── Mixed RLE + RLI nesting ────────────────────────────────────────────

    [Fact]
    public void RLE_inside_RLI_pushes_above_isolate_level()
    {
        // RLI RLE R PDF PDI
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.RLI, BidiClass.RLE, BidiClass.R, BidiClass.PDF, BidiClass.PDI);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        Assert.Equal(0, chars[0].Level);              // RLI at paragraph level 0
        Assert.True(chars[1].IsRemovedByX9);
        Assert.Equal(3, chars[2].Level);              // R: RLI pushed level 1 + RLE pushed odd above 1 = 3
        Assert.True(chars[3].IsRemovedByX9);
        Assert.Equal(0, chars[4].Level);              // PDI: stack popped down to paragraph entry
    }
}
