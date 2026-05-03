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

        var baseline = LoadBenchmarkMeansFromDirectory(baselineDir);
        var current = LoadBenchmarkMeansFromDirectory(currentDir);

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

        var failures = 0;
        var compared = 0;
        foreach (var (key, baselineMeanNs) in baseline.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!current.TryGetValue(key, out var currentMeanNs))
            {
                Console.WriteLine($"{key,-90} {FormatNanos(baselineMeanNs),12} {"missing",12} {"-",10}");
                continue;
            }
            compared++;
            var ratio = currentMeanNs / baselineMeanNs;
            var status = ratio > tolerance ? "FAIL" : ratio > 1.10 ? "warn" : "ok";
            Console.WriteLine($"{key,-90} {FormatNanos(baselineMeanNs),12} {FormatNanos(currentMeanNs),12} {ratio,9:F2}× {status}");
            if (ratio > tolerance) failures++;
        }
        Console.WriteLine();
        Console.WriteLine($"Compared {compared} benchmarks. Failures (ratio > {tolerance:F2}): {failures}");
        return failures > 0 ? 1 : 0;
    }

    private static IReadOnlyDictionary<string, double> LoadBenchmarkMeansFromDirectory(string dir)
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

    private static string FormatNanos(double ns)
    {
        if (ns < 1_000) return $"{ns:F0} ns";
        if (ns < 1_000_000) return $"{ns / 1_000:F1} us";
        if (ns < 1_000_000_000) return $"{ns / 1_000_000:F2} ms";
        return $"{ns / 1_000_000_000:F2} s";
    }
}
