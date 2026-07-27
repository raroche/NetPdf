// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NetPdf.Benchmarks;
using Xunit;

namespace NetPdf.UnitTests.Performance;

/// <summary>
/// Per PR #356 review [P1] — the benchmark gate must not report success on an INCOMPLETE suite.
///
/// <para>The gate's shell wrapper only counts report FILES, which is not sufficient: a single
/// <c>*-report-full-compressed.json</c> carries many benchmarks (the pinned baselines hold 33 across
/// 7 files), so a truncated export can drop measurements while keeping the file count identical.
/// Comparison used to print "missing" for those keys and still exit 0, meaning benchmarks nobody
/// measured looked exactly like benchmarks that passed. These tests pin the corrected contract:
/// a missing baseline key is <see cref="ComparisonOutcome.Incomplete"/> (exit 2), and it OUTRANKS a
/// regression, because an incomplete run cannot vouch for the benchmarks that did run.</para>
/// </summary>
public sealed class BenchmarkGateComparisonTests
{
    private const double Tolerance = 1.25; // +25%, the gate default

    private static IReadOnlyDictionary<string, double> Means(params (string Key, double Ns)[] entries)
    {
        var d = new Dictionary<string, double>(System.StringComparer.Ordinal);
        foreach (var (key, ns) in entries) d[key] = ns;
        return d;
    }

    [Fact]
    public void Current_matching_the_baseline_passes()
    {
        var baseline = Means(("A", 100), ("B", 200));
        var current = Means(("A", 105), ("B", 190));

        var report = BaselineComparison.Compare(baseline, current, Tolerance);

        Assert.Equal(ComparisonOutcome.Passed, report.Outcome);
        Assert.Equal(2, report.Compared);
        Assert.Empty(report.Missing);
        Assert.Empty(report.Regressed);
    }

    [Fact]
    public void A_benchmark_slower_than_the_tolerance_is_a_regression()
    {
        var baseline = Means(("A", 100), ("B", 200));
        var current = Means(("A", 100), ("B", 260)); // 1.30× > 1.25

        var report = BaselineComparison.Compare(baseline, current, Tolerance);

        Assert.Equal(ComparisonOutcome.Regressed, report.Outcome);
        Assert.Equal(1, (int)report.Outcome);
        Assert.Equal("B", Assert.Single(report.Regressed));
    }

    [Fact]
    public void A_baseline_benchmark_with_no_current_measurement_is_incomplete_not_a_pass()
    {
        // The regression this pins: every benchmark that DID run is well within tolerance, so the old
        // code returned 0 and the gate went green while "B" was never measured at all.
        var baseline = Means(("A", 100), ("B", 200));
        var current = Means(("A", 100));

        var report = BaselineComparison.Compare(baseline, current, Tolerance);

        Assert.Equal(ComparisonOutcome.Incomplete, report.Outcome);
        Assert.Equal(2, (int)report.Outcome); // the gate contract's "environmental" exit code
        Assert.Equal("B", Assert.Single(report.Missing));
        Assert.Equal(1, report.Compared);
        Assert.Equal(2, report.BaselineCount);
    }

    [Fact]
    public void Incomplete_outranks_a_regression_when_both_are_present()
    {
        var baseline = Means(("A", 100), ("B", 200), ("C", 300));
        var current = Means(("A", 400)); // A regressed hard, B and C never ran

        var report = BaselineComparison.Compare(baseline, current, Tolerance);

        Assert.Equal(ComparisonOutcome.Incomplete, report.Outcome);
        Assert.Equal(new[] { "B", "C" }, report.Missing);
        Assert.Equal("A", Assert.Single(report.Regressed)); // still reported, for diagnosis
    }

    [Fact]
    public void Extra_current_benchmarks_absent_from_the_baseline_do_not_fail_the_gate()
    {
        // A newly ADDED benchmark has no baseline entry yet. That is not a measurement gap, so it must
        // not block the gate — it simply is not compared until the baseline is re-captured.
        var baseline = Means(("A", 100));
        var current = Means(("A", 100), ("BrandNew", 999));

        var report = BaselineComparison.Compare(baseline, current, Tolerance);

        Assert.Equal(ComparisonOutcome.Passed, report.Outcome);
        Assert.Equal(1, report.Compared);
    }

    [Fact]
    public void A_report_file_that_drops_benchmarks_is_caught_even_though_the_file_count_matches()
    {
        // The exact hole the shell wrapper cannot see: SAME number of report files on both sides, but
        // one file exports fewer benchmark entries than the baseline pinned.
        var dir = Directory.CreateTempSubdirectory("netpdf-bench-gate-");
        try
        {
            var baselineDir = Path.Combine(dir.FullName, "baseline");
            var currentDir = Path.Combine(dir.FullName, "current");
            Directory.CreateDirectory(baselineDir);
            Directory.CreateDirectory(currentDir);

            WriteReport(Path.Combine(baselineDir, "Suite-report-full-compressed.json"),
                ("Ns.Suite.Alpha", 100), ("Ns.Suite.Beta", 200), ("Ns.Suite.Gamma", 300));
            WriteReport(Path.Combine(currentDir, "Suite-report-full-compressed.json"),
                ("Ns.Suite.Alpha", 100)); // Beta + Gamma silently absent

            var baseline = BaselineComparison.LoadBenchmarkMeansFromDirectory(baselineDir);
            var current = BaselineComparison.LoadBenchmarkMeansFromDirectory(currentDir);

            Assert.Equal(3, baseline.Count);
            Assert.Single(current);
            Assert.Equal(
                Directory.GetFiles(baselineDir).Length,
                Directory.GetFiles(currentDir).Length); // file counts agree — the wrapper sees nothing wrong

            var report = BaselineComparison.Compare(baseline, current, Tolerance);

            Assert.Equal(ComparisonOutcome.Incomplete, report.Outcome);
            Assert.Equal(new[] { "Ns.Suite.Beta", "Ns.Suite.Gamma" }, report.Missing);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Parameterised_benchmarks_key_on_name_plus_parameters()
    {
        var dir = Directory.CreateTempSubdirectory("netpdf-bench-gate-params-");
        try
        {
            var path = Path.Combine(dir.FullName, "Suite-report-full-compressed.json");
            WriteReport(path, ("Ns.Suite.Scaling", 100, "PageCount=1"), ("Ns.Suite.Scaling", 900, "PageCount=100"));

            var means = BaselineComparison.LoadBenchmarkMeansFromDirectory(dir.FullName);

            // Same FullName, different Parameters — they must NOT collapse onto one key, or the gate
            // would compare only whichever entry happened to be read last.
            Assert.Equal(2, means.Count);
            Assert.Equal(100, means["Ns.Suite.Scaling[PageCount=1]"]);
            Assert.Equal(900, means["Ns.Suite.Scaling[PageCount=100]"]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static void WriteReport(string path, params (string FullName, double Mean)[] benchmarks)
        => WriteReport(path, System.Array.ConvertAll(benchmarks, b => (b.FullName, b.Mean, "")));

    private static void WriteReport(string path, params (string FullName, double Mean, string Parameters)[] benchmarks)
    {
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteStartArray("Benchmarks");
        foreach (var (fullName, mean, parameters) in benchmarks)
        {
            writer.WriteStartObject();
            writer.WriteString("FullName", fullName);
            writer.WriteString("Parameters", parameters);
            writer.WriteStartObject("Statistics");
            writer.WriteNumber("Mean", mean);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
