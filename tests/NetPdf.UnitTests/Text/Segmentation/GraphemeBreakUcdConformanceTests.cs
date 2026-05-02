// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.IO.Compression;
using System.Text;
using NetPdf.Text.Segmentation;
using Xunit;
using Xunit.Abstractions;

namespace NetPdf.UnitTests.Text.Segmentation;

/// <summary>
/// Stage 14.1 — UCD <c>auxiliary/GraphemeBreakTest.txt</c> 16.0 conformance validation.
/// Loads the gzipped corpus, parses each test case (×/÷ symbols between codepoint hex
/// tokens), runs every case through <see cref="GraphemeClusterBreaker.FindBoundaries"/>,
/// and asserts the exact pass count to gate against silent regressions.
/// </summary>
public sealed class GraphemeBreakUcdConformanceTests
{
    /// <summary>Total cases in <c>GraphemeBreakTest.txt</c> 16.0.</summary>
    private const int GraphemeTestTotalCases = 1_093;

    /// <summary>
    /// Exact expected pass count. Pinning this — instead of just a percentage threshold
    /// — gates against silent "fix one, regress one" behavior.
    /// </summary>
    private const int GraphemeTestExpectedPassCount = 1_093;

    private readonly ITestOutputHelper _output;

    public GraphemeBreakUcdConformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void GraphemeBreakTest_txt_conformance_pins_exact_pass_count()
    {
        var (passed, failed, samples) = RunGraphemeBreakTest(sampleFailureLimit: 20);
        var total = passed + failed;
        var passRate = total == 0 ? 0.0 : (double)passed / total;
        _output.WriteLine($"GraphemeBreakTest.txt: {passed} / {total} passed ({passRate:P3}); {failed} failed.");
        if (samples.Count > 0)
        {
            _output.WriteLine($"First {samples.Count} failures:");
            foreach (var f in samples)
            {
                _output.WriteLine($"  {f}");
            }
        }
        Assert.Equal(GraphemeTestTotalCases, total);
        Assert.Equal(GraphemeTestExpectedPassCount, passed);
    }

    private static (int Passed, int Failed, IReadOnlyList<string> Failures) RunGraphemeBreakTest(int sampleFailureLimit)
    {
        var assembly = typeof(GraphemeBreakUcdConformanceTests).Assembly;
        var resourceName = "NetPdf.UnitTests.Resources.Ucd.GraphemeBreakTest.txt.gz";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource '{resourceName}' missing.");
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);

        var passed = 0;
        var failed = 0;
        var failures = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;

            var (codepoints, expectedSymbols) = ParseTokens(line);
            if (codepoints.Length == 0) continue;

            // Build UTF-16 input.
            var sb = new StringBuilder();
            foreach (var cp in codepoints)
            {
                sb.Append(char.ConvertFromUtf32(cp));
            }
            var actual = GraphemeClusterBreaker.FindBoundaries(sb.ToString().AsSpan());

            // Compute expected boundary positions: positions where ÷ appears.
            // expectedSymbols has length = codepoints.Length + 1 (one before each + one after last).
            // expectedSymbols[0] is always ÷ (sot per GB1).
            // expectedSymbols[N] is always ÷ (eot per GB2).
            // expectedSymbols[k] for 0 < k < N corresponds to the gap between codepoint k-1 and k.
            // Boundary at UTF-16 position = sum of UTF-16 lengths up to codepoint k.
            var expectedBoundaries = new List<int>();
            var utf16Pos = 0;
            for (var k = 0; k < expectedSymbols.Length; k++)
            {
                if (expectedSymbols[k] == '÷')
                {
                    expectedBoundaries.Add(utf16Pos);
                }
                if (k < codepoints.Length)
                {
                    utf16Pos += codepoints[k] >= 0x10000 ? 2 : 1;
                }
            }

            if (expectedBoundaries.Count == actual.Length
                && expectedBoundaries.SequenceEqual(actual))
            {
                passed++;
            }
            else
            {
                failed++;
                if (failures.Count < sampleFailureLimit)
                {
                    failures.Add($"{line} | expected boundaries=[{string.Join(',', expectedBoundaries)}] actual=[{string.Join(',', actual)}]");
                }
            }
        }
        return (passed, failed, failures);
    }

    private static (int[] Codepoints, char[] Symbols) ParseTokens(string line)
    {
        var symbols = new List<char>();
        var codepoints = new List<int>();
        var i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            var start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            if (i > start)
            {
                var token = line[start..i];
                if (token.Length == 1 && (token[0] == '×' || token[0] == '÷'))
                {
                    symbols.Add(token[0]);
                }
                else if (int.TryParse(token, System.Globalization.NumberStyles.HexNumber, null, out var cp))
                {
                    codepoints.Add(cp);
                }
            }
        }
        return (codepoints.ToArray(), symbols.ToArray());
    }
}
