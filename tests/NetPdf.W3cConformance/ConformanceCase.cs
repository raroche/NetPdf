// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Text;

namespace NetPdf.W3cConformance;

/// <summary>Per Phase 3 PR 1 — one curated conformance case: an HTML fragment +
/// a set of expected box GEOMETRIES the spec-correct layout must produce. A case
/// PASSES iff every <see cref="BoxExpectation"/> matches (within tolerance).
/// Cases are DATA (not individual <c>[Fact]</c>s) so a per-category runner can
/// compute a PASS-RATE — some cases legitimately fail where the engine has a
/// documented gap, and the gate asserts the aggregate rate clears the exit-
/// criteria threshold rather than demanding 100%.</summary>
internal sealed record ConformanceCase(
    string Id,
    string Spec,
    string Html,
    IReadOnlyList<BoxExpectation> Expectations,
    double PageWidthPx = 600,
    double PageHeightPx = 800);

/// <summary>Expected border-box geometry of the element with <see cref="Id"/> on
/// page <see cref="Page"/> (0-based). A null coordinate is not checked — assert
/// only the axes a case is about.</summary>
internal sealed record BoxExpectation(
    string Id,
    double? X = null,
    double? Y = null,
    double? Width = null,
    double? Height = null,
    int Page = 0,
    double Tol = 0.5);

internal static class ConformanceRunner
{
    /// <summary>Run a case; returns <see langword="null"/> when it PASSED, else a
    /// short human-readable failure reason (first failing expectation).</summary>
    public static string? Run(ConformanceCase c)
    {
        IReadOnlyList<ConformanceHarness.LaidOutBox> boxes;
        try
        {
            boxes = ConformanceHarness.Render(c.Html, c.PageWidthPx, c.PageHeightPx);
        }
        catch (Exception ex)
        {
            return $"render threw {ex.GetType().Name}: {ex.Message}";
        }

        foreach (var e in c.Expectations)
        {
            ConformanceHarness.LaidOutBox? hit = null;
            foreach (var b in boxes)
            {
                if (b.Id == e.Id && b.Page == e.Page) { hit = b; break; }
            }
            if (hit is null)
            {
                return $"#{e.Id} not laid out on page {e.Page}";
            }
            var b2 = hit.Value;
            var miss = Check("x", e.X, b2.X, e.Tol)
                ?? Check("y", e.Y, b2.Y, e.Tol)
                ?? Check("w", e.Width, b2.Width, e.Tol)
                ?? Check("h", e.Height, b2.Height, e.Tol);
            if (miss is not null) return $"#{e.Id} {miss}";
        }
        return null;
    }

    private static string? Check(string axis, double? expected, double actual, double tol)
    {
        if (expected is null) return null;
        return Math.Abs(expected.Value - actual) <= tol
            ? null
            : $"{axis} expected {expected.Value:0.##} got {actual:0.##}";
    }

    /// <summary>Run every case in <paramref name="cases"/> and return the
    /// pass-rate (passing / total) plus a report of the failures (so the gate
    /// can surface WHICH cases regressed, not just the number).</summary>
    public static (double Rate, int Passed, int Total, string Report) Evaluate(
        string category, IReadOnlyList<ConformanceCase> cases)
    {
        var passed = 0;
        var sb = new StringBuilder();
        foreach (var c in cases)
        {
            var fail = Run(c);
            if (fail is null) { passed++; continue; }
            sb.Append("  FAIL ").Append(c.Id).Append(" — ").Append(fail).Append('\n');
        }
        var rate = cases.Count == 0 ? 0.0 : (double)passed / cases.Count;
        var report = $"{category}: {passed}/{cases.Count} = {rate:P1}\n{sb}";
        return (rate, passed, cases.Count, report);
    }
}
