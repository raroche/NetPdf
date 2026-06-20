// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Xunit;
using Xunit.Abstractions;

namespace NetPdf.W3cConformance;

/// <summary>Per Phase 3 PR 1 — curated W3C conformance pass-rate gates (exit
/// criteria 3–6). Each category runs its case set, computes a pass-rate, and
/// gates against a REGRESSION FLOOR that is met today. The roadmap exit-criteria
/// TARGETS are the aspiration; the curated suite MEASURES the engine's honest
/// conformance — which is below some targets because NetPdf deliberately
/// approximates some features. The floor guards the cases that pass today from
/// regressing; the README publishes the measured rates next to their targets.
///
/// <para>The per-failure report goes to the test output so a regression names
/// WHICH case slipped, not just the number.</para></summary>
public sealed class ConformanceGates
{
    private readonly ITestOutputHelper _out;
    public ConformanceGates(ITestOutputHelper output) => _out = output;

    private const double Css22Floor = 0.80;
    private const double Css22Target = 0.90;

    [Fact]
    public void Css22_layout_pass_rate_meets_floor()
    {
        var (rate, passed, total, report) = ConformanceRunner.Evaluate("CSS 2.2", Css22Cases.All);
        _out.WriteLine(report);
        _out.WriteLine($"floor {Css22Floor:P0}, roadmap target {Css22Target:P0}");
        Assert.True(rate >= Css22Floor,
            $"CSS 2.2 conformance {rate:P1} ({passed}/{total}) fell below the "
            + $"{Css22Floor:P0} regression floor.\n{report}");
    }
}
