// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.RenderingCorpus.Visual;
using Xunit;

namespace NetPdf.RenderingCorpus.Visual;

/// <summary>Phase 4 PR 8 (PR-242 review [P1]) — the gate-activation policy: inert only while there are no
/// references and the gate isn't forced; once a reference exists or the gate is required, a missing PDF
/// rasterizer (or a missing per-invoice reference under a forced gate) is a hard FAIL — CI can't be green by
/// silently never rasterizing.</summary>
public sealed class VisualGatePolicyTests
{
    [Theory]
    // referenceExists, rasterizerAvailable, required → expected action
    [InlineData(false, false, false, VisualGateAction.Skip)] // nothing yet → inert
    [InlineData(false, true, false, VisualGateAction.Skip)]  // rasterizer ready but no reference → still inert
    [InlineData(true, true, false, VisualGateAction.Diff)]   // reference + rasterizer → diff
    [InlineData(true, false, false, VisualGateAction.Fail)]  // KEY: reference committed but no rasterizer → FAIL
    [InlineData(false, false, true, VisualGateAction.Fail)]  // forced required, nothing wired → FAIL
    [InlineData(false, true, true, VisualGateAction.Fail)]   // forced required, rasterizer ok, no reference → FAIL
    [InlineData(true, true, true, VisualGateAction.Diff)]    // forced + ready → diff
    public void Decide_maps_inputs_to_the_expected_action(
        bool referenceExists, bool rasterizerAvailable, bool required, VisualGateAction expected)
    {
        var decision = VisualGatePolicy.Decide(referenceExists, rasterizerAvailable, required);
        Assert.Equal(expected, decision.Action);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void A_committed_reference_without_a_rasterizer_fails_with_a_clear_reason()
    {
        // Simulates the maintainer committing references but forgetting to install/wire PDFium.
        var decision = VisualGatePolicy.Decide(referenceExists: true, rasterizerAvailable: false, required: false);
        Assert.Equal(VisualGateAction.Fail, decision.Action);
        Assert.Contains("rasterizer", decision.Reason);
    }
}
