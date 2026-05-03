// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Bidi;
using NetPdf.Text.Bidi.Rules;
using Xunit;

namespace NetPdf.UnitTests.Text.Bidi.Rules;

/// <summary>
/// UAX #9 BD7 (level runs) + BD13 (isolating run sequences) unit tests. Each test seeds
/// a synthetic <see cref="BidiCharInfo"/>[] post-X-rules and verifies the segmenter
/// produces the expected runs and sequences with correct sos/eos directions.
/// </summary>
public sealed class BidiRunSegmenterTests
{
    // ───── Level runs (BD7) ───────────────────────────────────────────────────

    [Fact]
    public void Single_level_paragraph_yields_one_level_run()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.L, BidiClass.L, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        var runs = BidiRunSegmenter.ComputeLevelRuns(chars);
        Assert.Single(runs);
        Assert.Equal(0, runs[0].Level);
        Assert.Equal(new[] { 0, 1, 2 }, runs[0].Indices);
    }

    [Fact]
    public void Mixed_levels_split_into_separate_runs()
    {
        // L RLE R PDF L → after X10: levels are 0, _, 1, _, 0 (X9 marks RLE/PDF removed).
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.L, BidiClass.RLE, BidiClass.R, BidiClass.PDF, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        var runs = BidiRunSegmenter.ComputeLevelRuns(chars);
        Assert.Equal(3, runs.Length);
        Assert.Equal(0, runs[0].Level);
        Assert.Equal(new[] { 0 }, runs[0].Indices);
        Assert.Equal(1, runs[1].Level);
        Assert.Equal(new[] { 2 }, runs[1].Indices);
        Assert.Equal(0, runs[2].Level);
        Assert.Equal(new[] { 4 }, runs[2].Indices);
    }

    [Fact]
    public void X9_removed_chars_are_excluded_from_runs()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.L, BidiClass.BN, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        var runs = BidiRunSegmenter.ComputeLevelRuns(chars);
        Assert.Single(runs);
        Assert.Equal(new[] { 0, 2 }, runs[0].Indices); // BN at index 1 skipped
    }

    // ───── Isolating run sequences (BD13) ─────────────────────────────────────

    [Fact]
    public void Plain_text_yields_single_isolating_run_sequence()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.L, BidiClass.L, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        var runs = BidiRunSegmenter.ComputeLevelRuns(chars);
        var sequences = BidiRunSegmenter.ComputeIsolatingRunSequences(chars, runs, paragraphLevel: 0);
        Assert.Single(sequences);
        Assert.Single(sequences[0].Runs);
        Assert.Equal(0, sequences[0].Level);
        Assert.Equal(BidiClass.L, sequences[0].Sos);
        Assert.Equal(BidiClass.L, sequences[0].Eos);
    }

    [Fact]
    public void RLI_PDI_pair_links_two_runs_into_one_sequence()
    {
        // L RLI R PDI L → X-rules give levels 0, 0, 1, 0, 0.
        // Three level runs: [0,1] level 0, [2] level 1, [3,4] level 0.
        // RLI at index 1 ends run 0; matching PDI at index 3 starts run 2.
        // BD13 links: run 0 → run 2 (skipping run 1, which is the inner isolated content).
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.L, BidiClass.RLI, BidiClass.R, BidiClass.PDI, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        var runs = BidiRunSegmenter.ComputeLevelRuns(chars);
        Assert.Equal(3, runs.Length);
        var sequences = BidiRunSegmenter.ComputeIsolatingRunSequences(chars, runs, paragraphLevel: 0);
        Assert.Equal(2, sequences.Length);
        // The outer sequence (the L runs around the isolate) joins runs 0 and 2.
        var outer = sequences[0];
        Assert.Equal(2, outer.Runs.Length);
        Assert.Equal(new[] { 0, 1 }, outer.Runs[0].Indices);
        Assert.Equal(new[] { 3, 4 }, outer.Runs[1].Indices);
        Assert.Equal(0, outer.Level);
        // The inner sequence is the R between the isolate.
        var inner = sequences[1];
        Assert.Single(inner.Runs);
        Assert.Equal(new[] { 2 }, inner.Runs[0].Indices);
        Assert.Equal(1, inner.Level);
    }

    [Fact]
    public void Unmatched_RLI_does_not_link_runs_into_sequence()
    {
        // L RLI R   (no PDI at end of paragraph)
        var chars = BidiTestHelpers.FromClasses(BidiClass.L, BidiClass.RLI, BidiClass.R);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        var runs = BidiRunSegmenter.ComputeLevelRuns(chars);
        var sequences = BidiRunSegmenter.ComputeIsolatingRunSequences(chars, runs, paragraphLevel: 0);
        // Outer L+RLI sequence ends at unmatched RLI; inner R is its own sequence.
        Assert.Equal(2, sequences.Length);
        Assert.Single(sequences[0].Runs);
        Assert.Equal(new[] { 0, 1 }, sequences[0].Runs[0].Indices);
        Assert.Single(sequences[1].Runs);
        Assert.Equal(new[] { 2 }, sequences[1].Runs[0].Indices);
    }

    // ───── sos / eos derivation ───────────────────────────────────────────────

    [Fact]
    public void Sos_uses_paragraph_level_when_sequence_starts_at_paragraph_start()
    {
        var chars = BidiTestHelpers.FromClasses(BidiClass.R, BidiClass.R);
        BidiX10Resolver.Apply(chars, paragraphLevel: 1);
        var runs = BidiRunSegmenter.ComputeLevelRuns(chars);
        var sequences = BidiRunSegmenter.ComputeIsolatingRunSequences(chars, runs, paragraphLevel: 1);
        Assert.Single(sequences);
        Assert.Equal(BidiClass.R, sequences[0].Sos); // max(1, 1) = 1 → odd → R
        Assert.Equal(BidiClass.R, sequences[0].Eos);
    }

    [Fact]
    public void Sos_eos_use_max_level_of_neighbor_or_sequence()
    {
        // L RLE R PDF L → outer sequence is level 0, inner is level 1.
        var chars = BidiTestHelpers.FromClasses(
            BidiClass.L, BidiClass.RLE, BidiClass.R, BidiClass.PDF, BidiClass.L);
        BidiX10Resolver.Apply(chars, paragraphLevel: 0);
        var runs = BidiRunSegmenter.ComputeLevelRuns(chars);
        var sequences = BidiRunSegmenter.ComputeIsolatingRunSequences(chars, runs, paragraphLevel: 0);

        // Two outer sequences (level 0) and one inner (level 1).
        // The first outer sequence: pre = paragraph (0), level = 0 → sos = max(0,0) → L.
        //                           post = inner R level 1 → eos = max(1,0) → R.
        var first = sequences[0];
        Assert.Equal(0, first.Level);
        Assert.Equal(BidiClass.L, first.Sos);
        Assert.Equal(BidiClass.R, first.Eos);

        // Inner sequence: pre = outer L (0), level = 1 → sos = max(0,1) → R.
        //                 post = next outer L (0), level = 1 → eos = max(0,1) → R.
        // Find the inner sequence (it's the level-1 one).
        BidiIsolatingRunSequence? inner = null;
        foreach (var seq in sequences)
        {
            if (seq.Level == 1) inner = seq;
        }
        Assert.NotNull(inner);
        Assert.Equal(BidiClass.R, inner!.Sos);
        Assert.Equal(BidiClass.R, inner.Eos);
    }
}
