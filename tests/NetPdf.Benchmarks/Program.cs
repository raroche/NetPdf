// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text.Json;
using BenchmarkDotNet.Running;

namespace NetPdf.Benchmarks;

/// <summary>
/// Two-mode entry point.
/// <list type="bullet">
///   <item><b>BenchmarkDotNet host</b> (default): discovers all <c>[Benchmark]</c>
///         methods and runs the suite. Use BDN command-line options:
///         <code>
///         dotnet run --project tests/NetPdf.Benchmarks -c Release -- --filter "*PageScaling*"
///         dotnet run --project tests/NetPdf.Benchmarks -c Release -- --list flat
///         dotnet run --project tests/NetPdf.Benchmarks -c Release -- --exporters JSON
///         </code></item>
///   <item><b>Comparison mode</b> (<c>--compare BASELINE.json CURRENT.json [tolerance]</c>):
///         reads two BDN <c>*-report-full-compressed.json</c> files (or a directory
///         containing such files) and exits 1 if any benchmark's Mean has regressed
///         beyond <paramref name="tolerance"/> (default 1.25 = +25%). Used by
///         <c>scripts/benchmark-gate.sh</c> to enforce the performance contract.</item>
/// </list>
/// </summary>
internal static class Program
{
    private const double DefaultRegressionTolerance = 1.25; // +25%

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--compare", StringComparison.Ordinal))
        {
            return RunComparisonMode(args);
        }

        _ = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }

    private static int RunComparisonMode(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine(
                "Usage: --compare BASELINE-DIR CURRENT-DIR [tolerance]\n" +
                "  BASELINE-DIR / CURRENT-DIR: directories containing BDN '*-report-full-compressed.json' files.\n" +
                "  tolerance: max ratio current.Mean / baseline.Mean before failure (default 1.25 = +25%).");
            return 2;
        }
        var baselineDir = args[1];
        var currentDir = args[2];
        var tolerance = args.Length >= 4 && double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
            ? t
            : DefaultRegressionTolerance;

        if (!Directory.Exists(baselineDir))
        {
            Console.Error.WriteLine($"Baseline directory not found: {baselineDir}");
            return 2;
        }
        if (!Directory.Exists(currentDir))
        {
            Console.Error.WriteLine($"Current directory not found: {currentDir}");
            return 2;
        }

        var baseline = BaselineComparison.LoadBenchmarkMeansFromDirectory(baselineDir);
        var current = BaselineComparison.LoadBenchmarkMeansFromDirectory(currentDir);

        if (baseline.Count == 0)
        {
            Console.Error.WriteLine($"No benchmark JSON files found in {baselineDir}.");
            return 2;
        }
        if (current.Count == 0)
        {
            Console.Error.WriteLine($"No benchmark JSON files found in {currentDir}.");
            return 2;
        }

        Console.WriteLine($"Baseline benchmarks: {baseline.Count}");
        Console.WriteLine($"Current  benchmarks: {current.Count}");
        Console.WriteLine($"Tolerance: {(tolerance - 1) * 100:F1}%");
        Console.WriteLine();
        Console.WriteLine($"{"Benchmark",-90} {"Baseline",12} {"Current",12} {"Ratio",10}");
        Console.WriteLine(new string('-', 128));

        var report = BaselineComparison.Compare(baseline, current, tolerance, (key, baseNs, curNs, ratio) =>
        {
            if (curNs is null || ratio is null)
            {
                Console.WriteLine($"{key,-90} {FormatNanos(baseNs),12} {"missing",12} {"-",10}");
                return;
            }
            var status = ratio > tolerance ? "FAIL" : ratio > 1.10 ? "warn" : "ok";
            Console.WriteLine($"{key,-90} {FormatNanos(baseNs),12} {FormatNanos(curNs.Value),12} {ratio.Value,9:F2}× {status}");
        });

        Console.WriteLine();
        Console.WriteLine(
            $"Compared {report.Compared} of {report.BaselineCount} baseline benchmarks. " +
            $"Missing: {report.Missing.Count}. Failures (ratio > {tolerance:F2}): {report.Regressed.Count}");

        if (report.Outcome == ComparisonOutcome.Incomplete)
        {
            Console.Error.WriteLine(
                $"error: {report.Missing.Count} baseline benchmark(s) had no current measurement — the suite " +
                "is INCOMPLETE, so the gate cannot certify performance. Re-run the full suite; if a benchmark " +
                "was removed on purpose, re-capture the baseline (./scripts/benchmark-gate.sh capture).");
            foreach (var key in report.Missing) Console.Error.WriteLine($"       missing: {key}");
        }
        return (int)report.Outcome;
    }

    private static string FormatNanos(double ns)
    {
        if (ns < 1_000) return $"{ns:F0} ns";
        if (ns < 1_000_000) return $"{ns / 1_000:F1} us";
        if (ns < 1_000_000_000) return $"{ns / 1_000_000:F2} ms";
        return $"{ns / 1_000_000_000:F2} s";
    }
}
