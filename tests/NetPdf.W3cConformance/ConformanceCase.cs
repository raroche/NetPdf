// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Text;
using NetPdf.Paginate.Diagnostics;

namespace NetPdf.W3cConformance;

/// <summary>Per Phase 3 PR 1 — one curated conformance case: an HTML fragment +
/// a set of expected box GEOMETRIES the spec-correct layout must produce. A case
/// PASSES iff every <see cref="BoxExpectation"/> matches (within tolerance) AND
/// no unexpected diagnostic fired. Cases are DATA (not individual <c>[Fact]</c>s)
/// so a per-category runner can both PUBLISH a pass-rate and gate per-case.
///
/// <para><see cref="KnownGap"/> tags a case that documents a deliberate engine
/// approximation and is EXPECTED to fail; the gate then guards it from the
/// OPPOSITE direction — if the gap closes (the case starts passing) the gate goes
/// red so the marker + published rate get updated. A null <see cref="KnownGap"/>
/// is a case that MUST pass; its regression turns the gate red. That per-case
/// baseline — not the aggregate pass-rate — is the regression gate (PR 1 review
/// [P1]); the rate is published measurement only.</para></summary>
internal sealed record ConformanceCase(
    string Id,
    string Spec,
    string Html,
    IReadOnlyList<BoxExpectation> Expectations,
    double PageWidthPx = 600,
    double PageHeightPx = 800,
    string? KnownGap = null);

/// <summary>Expected border-box geometry of the element with <see cref="Id"/> on
/// page <see cref="Page"/> (0-based). A null coordinate is not checked — assert
/// only the axes a case is about. <see cref="Fragments"/> is the number of
/// fragments the element should produce on that page (default 1) — more than
/// expected is a duplicate-emission failure, common in pagination/split bugs
/// (PR 1 review [P3]); geometry is asserted against the first fragment.</summary>
internal sealed record BoxExpectation(
    string Id,
    double? X = null,
    double? Y = null,
    double? Width = null,
    double? Height = null,
    int Page = 0,
    double Tol = 0.5,
    int Fragments = 1);

internal static class ConformanceRunner
{
    /// <summary>Diagnostics that mean content was SKIPPED / not laid out as
    /// authored — the element may simply be ABSENT, which a geometry assertion
    /// can't always catch (a case might not assert the dropped box). The
    /// canonical hazard: the harness runs <c>shaperResolver: null</c>, so any
    /// accidental text content is silently skipped (PR 1 review [P2]). These fail
    /// a case unconditionally. Approximation / overflow diagnostics
    /// (forced-overflow, grid-fr-under-indefinite, best-effort breaks) are
    /// DELIBERATELY excluded: that content IS emitted, so the geometry checks
    /// below validate it directly — a wrong position fails as a wrong position,
    /// not as a diagnostic.</summary>
    private static readonly HashSet<string> ContentOmissionDiagnostics = new()
    {
        PaginateDiagnosticCodes.LayoutInlineSkippedNoShaperResolver001,
        PaginateDiagnosticCodes.LayoutInlineAtomicNotSupported001,
        PaginateDiagnosticCodes.LayoutInlineUnsupported001,
        PaginateDiagnosticCodes.LayoutAbsoluteFeatureUnsupported001,
    };

    /// <summary>Run a case; returns <see langword="null"/> when it PASSED, else a
    /// short human-readable failure reason (first failing check).</summary>
    public static string? Run(ConformanceCase c)
    {
        ConformanceHarness.RenderResult render;
        try
        {
            render = ConformanceHarness.Render(c.Html, c.PageWidthPx, c.PageHeightPx);
        }
        catch (Exception ex)
        {
            return $"render threw {ex.GetType().Name}: {ex.Message}";
        }

        // The page loop hit maxPages with layout still incomplete — an unconsumed
        // continuation means the case never finished, so any geometry it asserts
        // is on partial output. Never a real pass (PR 1 review [P2]).
        if (render.MaxPagesExhausted)
        {
            return "max pages exhausted — layout never completed (unconsumed continuation)";
        }

        // A content-omission diagnostic means an element was silently skipped, so
        // any geometry the case asserts is on incomplete output (PR 1 review [P2]).
        foreach (var d in render.Diagnostics)
        {
            if (ContentOmissionDiagnostics.Contains(d.Code))
            {
                return $"content-omission diagnostic {d.Code}: {d.Message}";
            }
        }

        foreach (var e in c.Expectations)
        {
            // Group by (id, page) — the FIRST-match shortcut hid duplicate /
            // unexpected fragments, which are a common pagination failure mode
            // (PR 1 review [P3]). Assert the fragment count, then geometry.
            ConformanceHarness.LaidOutBox? first = null;
            var count = 0;
            foreach (var b in render.Boxes)
            {
                if (b.Id != e.Id || b.Page != e.Page) continue;
                count++;
                first ??= b;
            }
            if (count != e.Fragments)
            {
                return $"#{e.Id} on page {e.Page}: expected {e.Fragments} fragment(s), got {count}";
            }
            var b2 = first!.Value;
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

    /// <summary>Per-category evaluation. <see cref="Regressions"/> are cases that
    /// MUST pass but failed; <see cref="ClosedGaps"/> are <c>KnownGap</c> cases
    /// that now pass (the marker + published rate need updating). Both empty ⇒
    /// the gate is green. <see cref="Rate"/> is published measurement only.</summary>
    internal readonly record struct CategoryEvaluation(
        int Total,
        int Passed,
        IReadOnlyList<string> Regressions,
        IReadOnlyList<string> ClosedGaps,
        string Report)
    {
        public double Rate => Total == 0 ? 0.0 : (double)Passed / Total;
    }

    /// <summary>Run every case in <paramref name="cases"/>, classifying each
    /// against its <c>KnownGap</c> baseline so a single passing-case regression
    /// (or a silently-closed gap) turns the gate red — the aggregate pass-rate
    /// can't mask either (PR 1 review [P1]). The report names every case's
    /// status so a failure says WHICH case slipped, not just a number.</summary>
    public static CategoryEvaluation Evaluate(
        string category, IReadOnlyList<ConformanceCase> cases)
    {
        var passed = 0;
        var regressions = new List<string>();
        var closedGaps = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in cases)
        {
            var fail = Run(c);
            var isPass = fail is null;
            if (isPass) passed++;

            if (c.KnownGap is null)
            {
                if (isPass)
                {
                    sb.Append("  PASS ").Append(c.Id).Append('\n');
                }
                else
                {
                    sb.Append("  FAIL ").Append(c.Id).Append(" — ").Append(fail).Append('\n');
                    regressions.Add($"{c.Id} — {fail}");
                }
            }
            else if (isPass)
            {
                sb.Append("  GAP-CLOSED ").Append(c.Id)
                    .Append(" (was: ").Append(c.KnownGap).Append(")\n");
                closedGaps.Add($"{c.Id} (documented gap: {c.KnownGap})");
            }
            else
            {
                sb.Append("  gap  ").Append(c.Id).Append(" — ").Append(c.KnownGap).Append('\n');
            }
        }
        var rate = cases.Count == 0 ? 0.0 : (double)passed / cases.Count;
        var report = $"{category}: {passed}/{cases.Count} = {rate:P1} "
            + $"(regression gate is per-case, not this rate)\n{sb}";
        return new CategoryEvaluation(cases.Count, passed, regressions, closedGaps, report);
    }
}
