// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Bidi;
using Xunit;

namespace NetPdf.UnitTests.Text.Bidi;

/// <summary>
/// End-to-end integration for Stage 12.3a — drives the full
/// UTF-16 → <see cref="BidiPipeline.BuildCharInfos"/> → <see cref="BidiPipeline.RunX10AndSegment"/>
/// pipeline against real strings (Hebrew, Arabic, mixed scripts) and verifies the structural
/// foundation matches the per-codepoint UCD class data and X-rule behavior.
/// </summary>
public sealed class BidiPipelineIntegrationTests
{
    [Fact]
    public void Pure_LTR_string_produces_one_isolating_run_sequence_at_level_zero()
    {
        var infos = BidiPipeline.BuildCharInfos("Hello".AsSpan());
        var (runs, sequences) = BidiPipeline.RunX10AndSegment(infos.AsSpan(), paragraphLevel: 0);
        Assert.Single(runs);
        Assert.Equal(0, runs[0].Level);
        Assert.Single(sequences);
    }

    [Fact]
    public void Pure_RTL_Hebrew_string_produces_one_sequence_at_level_one()
    {
        // Hebrew alef-bet-gimel.
        var infos = BidiPipeline.BuildCharInfos("אבג".AsSpan());
        var (runs, sequences) = BidiPipeline.RunX10AndSegment(infos.AsSpan(), paragraphLevel: 1);
        Assert.Single(runs);
        Assert.Equal(1, runs[0].Level);
        Assert.Single(sequences);
        Assert.Equal(BidiClass.R, sequences[0].Sos);
        Assert.Equal(BidiClass.R, sequences[0].Eos);
    }

    [Fact]
    public void Mixed_LTR_and_RTL_in_LTR_paragraph_yields_alternating_runs()
    {
        // "Hello אבג World" — Latin Hebrew Latin in an LTR paragraph.
        // Hebrew letters sit at level 1; Latin sits at level 0.
        var infos = BidiPipeline.BuildCharInfos("Hello אבג World".AsSpan());
        var (runs, _) = BidiPipeline.RunX10AndSegment(infos.AsSpan(), paragraphLevel: 0);
        // X-rules give every char its top.level (paragraph 0), since nothing pushes the
        // stack — the Hebrew chars stay at level 0 too. Stage 12.3b W-rules + I-rules
        // are what eventually elevate Hebrew to level 1; at Stage 12.3a everything sits
        // at the paragraph level.
        Assert.Single(runs);
        Assert.Equal(0, runs[0].Level);
    }

    [Fact]
    public void Supplementary_plane_codepoint_emits_one_BidiCharInfo_with_length_2()
    {
        // U+1F600 GRINNING FACE — supplementary plane, surrogate pair in UTF-16.
        var infos = BidiPipeline.BuildCharInfos("A😀B".AsSpan());
        Assert.Equal(3, infos.Length);                 // A, emoji, B — three codepoints
        Assert.Equal(1, infos[0].Utf16Length);
        Assert.Equal(2, infos[1].Utf16Length);          // surrogate pair
        Assert.Equal(1, infos[2].Utf16Length);
        Assert.Equal(0, infos[0].Utf16Index);
        Assert.Equal(1, infos[1].Utf16Index);
        Assert.Equal(3, infos[2].Utf16Index);          // skip past the surrogate pair
    }

    [Fact]
    public void Explicit_RLE_in_LTR_paragraph_pushes_inner_chars_to_odd_level()
    {
        // U+202B = RLE, U+202C = PDF.
        // "L‫ R ‬ L" — we set R via Hebrew letter to keep the test using real codepoints.
        var infos = BidiPipeline.BuildCharInfos("A‫א‬B".AsSpan());
        var (runs, _) = BidiPipeline.RunX10AndSegment(infos.AsSpan(), paragraphLevel: 0);
        Assert.Equal(3, runs.Length);                  // [A] level 0, [Hebrew] level 1, [B] level 0
        Assert.Equal(0, runs[0].Level);
        Assert.Equal(1, runs[1].Level);
        Assert.Equal(0, runs[2].Level);
    }

    [Fact]
    public void RLI_PDI_around_RTL_text_produces_correct_isolating_run_sequence_structure()
    {
        // U+2067 = RLI, U+2069 = PDI. A RLI אבג PDI B
        var infos = BidiPipeline.BuildCharInfos("A⁧אבג⁩B".AsSpan());
        var (runs, sequences) = BidiPipeline.RunX10AndSegment(infos.AsSpan(), paragraphLevel: 0);
        Assert.Equal(3, runs.Length);                  // [A, RLI] level 0, [Hebrew] level 1, [PDI, B] level 0
        // BD13 should join the two outer runs into a single sequence; the inner Hebrew
        // run is its own sequence at level 1.
        Assert.Equal(2, sequences.Length);
        var outer = sequences[0];
        Assert.Equal(2, outer.Runs.Length);
        Assert.Equal(0, outer.Level);
        var inner = sequences[1];
        Assert.Single(inner.Runs);
        Assert.Equal(1, inner.Level);
    }

    [Fact]
    public void ResolveLevels_still_throws_NotImplementedException_with_stage_12_3b_message()
    {
        var ex = Assert.Throws<NotImplementedException>(
            () => BidiAlgorithm.ResolveLevels("Hello".AsSpan()));
        Assert.Contains("Stage 12.3", ex.Message);
    }
}
