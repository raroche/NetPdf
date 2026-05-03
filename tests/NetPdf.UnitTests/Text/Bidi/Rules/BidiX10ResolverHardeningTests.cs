// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Bidi;
using NetPdf.Text.Bidi.Rules;
using Xunit;

namespace NetPdf.UnitTests.Text.Bidi.Rules;

/// <summary>
/// Stage 12.3a hardening — review-driven correctness fixes against UAX #9 §3.3.2 X-rules
/// + §5.2 retained-formatting semantics. Each test pins down a behavior that was wrong
/// (or missing) before the hardening pass and that the W/N/I rule passes are about to
/// build on.
/// </summary>
public sealed class BidiX10ResolverHardeningTests
{
    // ───── Retained explicit-formatting characters take ENCLOSING level (§5.2) ───

    [Fact]
    public void RLE_retained_at_enclosing_level_not_post_push()
    {
        // L RLE R PDF L → enclosing level when RLE encountered is 0 (paragraph). The retained
        // RLE character must keep level 0, not 1 (the post-push level inside the embedding).
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.L, BidiClass.RLE, BidiClass.R, BidiClass.PDF, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        Assert.Equal(0, chars[1].Level); // RLE retained at enclosing level
        Assert.True(chars[1].IsRemovedByX9);
        Assert.Equal(1, chars[2].Level); // R inside the RLE — level 1 (post-push)
        Assert.Equal(0, chars[3].Level); // PDF retained at enclosing level (post-pop top)
        Assert.True(chars[3].IsRemovedByX9);
    }

    [Fact]
    public void LRE_retained_at_enclosing_level_in_RTL_paragraph()
    {
        // R LRE L PDF R → enclosing level when LRE encountered is 1 (paragraph). Retained
        // LRE must be at level 1, not 2.
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.R, BidiClass.LRE, BidiClass.L, BidiClass.PDF, BidiClass.R);
        BidiX10Resolver.Apply(chars, paragraphLevel: 1);
        Assert.Equal(1, chars[1].Level);
        Assert.True(chars[1].IsRemovedByX9);
        Assert.Equal(2, chars[2].Level); // L inside the LRE
    }

    [Fact]
    public void RLO_retained_at_enclosing_level()
    {
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.L, BidiClass.RLO, BidiClass.L, BidiClass.PDF, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        Assert.Equal(0, chars[1].Level);              // RLO at enclosing level
        Assert.True(chars[1].IsRemovedByX9);
        Assert.Equal(BidiClass.R, chars[2].ResolvedClass); // RLO override → R
        Assert.Equal(1, chars[2].Level);
    }

    [Fact]
    public void LRO_retained_at_enclosing_level_in_RTL_paragraph()
    {
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.R, BidiClass.LRO, BidiClass.R, BidiClass.PDF, BidiClass.R);
        BidiX10Resolver.Apply(chars, paragraphLevel: 1);
        Assert.Equal(1, chars[1].Level);              // LRO at enclosing level (paragraph)
        Assert.True(chars[1].IsRemovedByX9);
        Assert.Equal(BidiClass.L, chars[2].ResolvedClass); // LRO override → L
        Assert.Equal(2, chars[2].Level);
    }

    [Fact]
    public void Nested_embedding_retained_at_immediately_enclosing_level_not_paragraph()
    {
        // L RLE LRE L PDF PDF L
        // After paragraph (level 0):
        //   RLE pushes level 1 — RLE itself at enclosing level 0.
        //   LRE pushes level 2 — LRE itself at enclosing level 1 (the RLE-pushed level).
        //   L inside LRE: level 2.
        //   First PDF pops: enclosing-of-popped = 1 → PDF at level 1.
        //   Second PDF pops: enclosing-of-popped = 0 → PDF at level 0.
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.L, BidiClass.RLE, BidiClass.LRE, BidiClass.L, BidiClass.PDF, BidiClass.PDF, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        Assert.Equal(0, chars[1].Level); // RLE: enclosing 0
        Assert.Equal(1, chars[2].Level); // LRE: enclosing 1 (RLE pushed)
        Assert.Equal(2, chars[3].Level); // L inside both
        Assert.Equal(1, chars[4].Level); // first PDF: post-pop top = 1
        Assert.Equal(0, chars[5].Level); // second PDF: post-pop top = 0
        Assert.Equal(0, chars[6].Level);
    }

    // ───── X6a clears overflowEmbeddingCount on matched-PDI close ──────────────

    [Fact]
    public void Matched_PDI_clears_overflow_embedding_count()
    {
        // Setup: an LRI, then enough RLEs nested to overflow the BD2 cap (raising
        // overflowEmbeddingCount > 0), then the matching PDI. After the PDI, X6a step 3
        // requires overflowEmbeddingCount = 0; without it, a subsequent RLE would fail
        // to push (the overflow gate would still be active).
        //
        //   LRI → push isolate level 2; stack=[(0), (2 iso)].
        //   62 RLEs → push levels 3, 5, 7, ..., 125; stack depth 64; top=125.
        //   1 RLE → would push 127 > BD2 cap; overflow, embCount=1.
        //   PDI → matched: clear embCount; pop until isolate, pop isolate; stack=[(0)].
        //   RLE → enclosing=0, push 1; stack=[(0), (1)]; embCount stays 0.
        //   R → at top=1.
        //
        // Without the X6a fix, the post-PDI RLE wouldn't push (embCount=1 > 0) and the
        // R would land at the still-unchanged top level 0.
        var classes = new System.Collections.Generic.List<BidiClass> { BidiClass.LRI };
        for (var i = 0; i < 63; i++)
        {
            classes.Add(BidiClass.RLE);
        }
        classes.Add(BidiClass.PDI);
        classes.Add(BidiClass.RLE);
        classes.Add(BidiClass.R);

        var chars = BidiTestHelpers.FromClasses(classes.ToArray());
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        Assert.Equal(1, chars[^1].Level);
    }

    // ───── X8 paragraph separator resets stack + overflow counters ─────────────

    [Fact]
    public void B_resets_directional_stack_for_following_chars()
    {
        // L RLE R B L → without X8 reset, the post-B L would inherit the still-pushed
        // RLE level (1). With reset, post-B L starts fresh at paragraph level 0.
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.L, BidiClass.RLE, BidiClass.R, BidiClass.B, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        Assert.Equal(0, chars[0].Level);
        Assert.Equal(1, chars[2].Level);              // R inside the RLE
        Assert.Equal(0, chars[3].Level);              // B at paragraph level
        Assert.Equal(0, chars[4].Level);              // L after B: stack reset → paragraph
    }

    [Fact]
    public void B_clears_overflow_state_so_following_RLE_can_push()
    {
        // 63 RLEs that overflow the BD2 cap, then B, then RLE+R. After B's reset, the
        // overflow counters are zero, so the post-B RLE successfully pushes level 1.
        var classes = new System.Collections.Generic.List<BidiClass>();
        for (var i = 0; i < 63; i++)
        {
            classes.Add(BidiClass.RLE);
        }
        classes.Add(BidiClass.B);
        classes.Add(BidiClass.RLE);
        classes.Add(BidiClass.R);

        var chars = BidiTestHelpers.FromClasses(classes.ToArray());
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        Assert.Equal(1, chars[^1].Level);
    }

    [Fact]
    public void B_clears_validIsolateCount_so_unmatched_PDI_after_B_is_no_op()
    {
        // LRI X B PDI Y → before B, the isolate is open (validIsolateCount=1). After B's
        // reset, validIsolateCount=0, so the PDI behaves as unmatched (no stack pop) and
        // Y stays at paragraph level.
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.LRI, BidiClass.L, BidiClass.B, BidiClass.PDI, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        Assert.Equal(0, chars[3].Level); // unmatched PDI after reset → top.level (which is paragraph 0)
        Assert.Equal(0, chars[4].Level);
    }
}
