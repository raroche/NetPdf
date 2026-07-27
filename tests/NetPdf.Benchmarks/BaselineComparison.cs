// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;

namespace NetPdf.Benchmarks;

/// <summary>How a baseline-vs-current comparison came out. The numeric values ARE the process exit
/// codes the gate contract defines (<c>scripts/benchmark-gate.sh</c>): 0 clean, 1 regression,
/// 2 environmental — the suite did not fully run, so nothing can be certified.</summary>
internal enum ComparisonOutcome
{
    Passed = 0,
    Regressed = 1,
    Incomplete = 2,
}

/// <summary>Result of comparing a current benchmark export against a pinned baseline.</summary>
/// <param name="Outcome">Verdict; its numeric value is the intended exit code.</param>
/// <param name="Compared">Baseline benchmarks that had a current measurement.</param>
/// <param name="BaselineCount">Total benchmarks in the baseline.</param>
/// <param name="Missing">Baseline keys with NO current measurement, ordinal-sorted.</param>
/// <param name="Regressed">Keys whose ratio exceeded the tolerance, ordinal-sorted.</param>
internal sealed record ComparisonReport(
    ComparisonOutcome Outcome,
    int Compared,
    int BaselineCount,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Regressed);

/// <summary>
/// Pure baseline-vs-current comparison, split out of <c>Program</c> so it can be unit-tested
/// without pulling BenchmarkDotNet into the test project — <c>NetPdf.UnitTests</c> LINKS this single
/// source file rather than referencing this project. Keep it free of BenchmarkDotNet types.
/// </summary>
internal static class BaselineComparison
{
    /// <summary>Reads every BDN <c>*-report-full-compressed.json</c> under <paramref name="dir"/> into a
    /// benchmark-key → mean-nanoseconds map. The key is <c>FullName</c>, suffixed with
    /// <c>[Parameters]</c> when the benchmark is parameterised.</summary>
    public static IReadOnlyDictionary<string, double> LoadBenchmarkMeansFromDirectory(string dir)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(dir, "*-report-full-compressed.json", SearchOption.AllDirectories))
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("Benchmarks", out var benchmarks)) continue;
            foreach (var bench in benchmarks.EnumerateArray())
            {
                if (!bench.TryGetProperty("FullName", out var fullName)) continue;
                if (!bench.TryGetProperty("Parameters", out var parameters)) continue;
                if (!bench.TryGetProperty("Statistics", out var stats)) continue;
                if (!stats.TryGetProperty("Mean", out var mean)) continue;
                var fullNameStr = fullName.GetString() ?? "";
                var paramsStr = parameters.GetString() ?? "";
                var key = string.IsNullOrEmpty(paramsStr) ? fullNameStr : $"{fullNameStr}[{paramsStr}]";
                result[key] = mean.GetDouble();
            }
        }
        return result;
    }

    /// <summary>
    /// Compares every BASELINE key against <paramref name="current"/>.
    ///
    /// <para>A baseline benchmark with no current measurement makes the run INCOMPLETE, and that
    /// outranks a regression: the old behaviour printed "missing" and still returned success, so a
    /// truncated export reported a clean gate. The shell wrapper's report-FILE count cannot catch this —
    /// one file carries many benchmarks (the pinned files hold 33 between 7 files), so dropping
    /// measurements need not change the file count. When the suite is incomplete we also cannot argue
    /// the benchmarks that DID run are representative, so <see cref="ComparisonOutcome.Incomplete"/>
    /// wins even if some of them also regressed (both lists are still reported for diagnosis).</para>
    /// </summary>
    public static ComparisonReport Compare(
        IReadOnlyDictionary<string, double> baseline,
        IReadOnlyDictionary<string, double> current,
        double tolerance,
        Action<string, double, double?, double?>? onRow = null)
    {
        var missing = new List<string>();
        var regressed = new List<string>();
        var compared = 0;

        foreach (var key in baseline.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var baselineMeanNs = baseline[key];
            if (!current.TryGetValue(key, out var currentMeanNs))
            {
                missing.Add(key);
                onRow?.Invoke(key, baselineMeanNs, null, null);
                continue;
            }
            compared++;
            var ratio = currentMeanNs / baselineMeanNs;
            if (ratio > tolerance) regressed.Add(key);
            onRow?.Invoke(key, baselineMeanNs, currentMeanNs, ratio);
        }

        var outcome = missing.Count > 0
            ? ComparisonOutcome.Incomplete
            : regressed.Count > 0 ? ComparisonOutcome.Regressed : ComparisonOutcome.Passed;

        return new ComparisonReport(outcome, compared, baseline.Count, missing, regressed);
    }
}
