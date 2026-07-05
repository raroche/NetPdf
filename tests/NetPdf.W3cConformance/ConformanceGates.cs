// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using Xunit;
using Xunit.Abstractions;

namespace NetPdf.W3cConformance;

/// <summary>Per Phase 3 PR 1 — curated W3C conformance gates (exit criteria 3–6).
/// Each category runs its case set and gates on a PER-CASE BASELINE, not the
/// aggregate pass-rate: every case with no <c>KnownGap</c> marker MUST pass (its
/// regression turns the gate red), and every <c>KnownGap</c> case MUST still fail
/// (if a gap closes, the gate goes red so the marker + published rate get
/// updated). A loose pass-rate floor could let a passing case break while a
/// known-failing case starts passing — aggregate unchanged, CI green — which the
/// PR 1 review [P1] flagged; the per-case baseline closes that hole.
///
/// <para>The pass-rate is still computed and written to the test output as the
/// PUBLISHED MEASUREMENT next to its roadmap target (CSS 2.2 ≥90%, Fragmentation
/// ≥80%, Flexbox ≥85%, Grid ≥70%) — see <c>README.md</c> — but it is not the
/// gate.</para></summary>
public sealed class ConformanceGates
{
    private readonly ITestOutputHelper _out;
    public ConformanceGates(ITestOutputHelper output) => _out = output;

    // Roadmap exit-criteria TARGETS (3–6). Reported next to the measured rate;
    // not asserted (the per-case baseline is the gate). A category below target
    // is honest — it has documented gaps (see README + docs/deferrals.md).
    private const double Css22Target = 0.90;
    private const double FragmentationTarget = 0.80;
    private const double FlexboxTarget = 0.85;
    private const double GridTarget = 0.70;
    private const double BackgroundsBordersTarget = 0.90;
    private const double TransformsTarget = 0.85;

    [Fact]
    public void Css22_layout_conformance_baseline()
        => AssertBaseline("CSS 2.2", Css22Cases.All, Css22Target);

    [Fact]
    public void Fragmentation_conformance_baseline()
        => AssertBaseline("Fragmentation", FragmentationCases.All, FragmentationTarget);

    [Fact]
    public void Flexbox_conformance_baseline()
        => AssertBaseline("Flexbox", FlexboxCases.All, FlexboxTarget);

    [Fact]
    public void Grid_conformance_baseline()
        => AssertBaseline("Grid", GridCases.All, GridTarget);

    [Fact]
    public void BackgroundsBorders_conformance_baseline()
        => AssertBaseline("Backgrounds & Borders", BackgroundsBordersCases.All, BackgroundsBordersTarget);

    [Fact]
    public void Transforms_conformance_baseline()
        => AssertBaseline("Transforms", TransformsCases.All, TransformsTarget);

    private void AssertBaseline(
        string category, IReadOnlyList<ConformanceCase> cases, double target)
    {
        var e = ConformanceRunner.Evaluate(category, cases);
        _out.WriteLine(e.Report);
        _out.WriteLine(
            $"published pass-rate {e.Rate:P1} vs roadmap target {target:P0} "
            + (e.Rate >= target ? "(MET)" : "(below — documented gaps)"));

        Assert.True(e.Regressions.Count == 0,
            $"{category} REGRESSION — these expected-passing cases now FAIL:\n  "
            + string.Join("\n  ", e.Regressions)
            + "\nFix the regression, or (if the change is intended) mark the case "
            + "KnownGap and document it.");

        Assert.True(e.ClosedGaps.Count == 0,
            $"{category} GAP CLOSED — these cases are marked KnownGap but now PASS:\n  "
            + string.Join("\n  ", e.ClosedGaps)
            + "\nRemove the KnownGap marker + update the published rate in "
            + "tests/NetPdf.W3cConformance/README.md (a gap closing is good news).");
    }
}
