// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Xunit;
using Xunit.Abstractions;

namespace NetPdf.UnitTests.Text.Bidi.Conformance;

/// <summary>
/// Stage 12.4 — UCD conformance validation against <c>BidiTest.txt</c> +
/// <c>BidiCharacterTest.txt</c> (Unicode 16.0). Each test loads the gzipped data from
/// embedded resources, runs every test case through <see cref="BidiUcdConformanceHarness"/>,
/// and asserts the pass rate meets a tracked baseline. The baseline ratchets up as
/// the algorithm hardens — drops below it should fail CI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a baseline instead of 100%?</b> The implementation is built clean-room from
/// the spec text and may have subtle gaps the spec corpus exposes (paired-bracket edge
/// cases, deeply-nested isolate scenarios, NSM after isolates, etc.). Each iteration
/// drives the rate higher; pinning a 100% gate before the algorithm passes 100% would
/// mean the gate goes red on every commit. The baseline tracks the actual achieved rate
/// after each hardening pass.
/// </para>
/// <para>
/// <b>Output details.</b> <see cref="ITestOutputHelper"/> writes the pass / fail / rate
/// summary plus up to 20 sample failures so each test run records the current state
/// of conformance for the next iteration's review.
/// </para>
/// </remarks>
public sealed class BidiUcdConformanceTests
{
    /// <summary>
    /// Minimum pass rate for <c>BidiTest.txt</c> (synthetic-class spec corpus). 100% —
    /// every X/W/N/I rule combination the file exercises (770,241 cases) matches spec.
    /// Any drop indicates a regression.
    /// </summary>
    private const double BidiTestBaseline = 1.0;

    /// <summary>
    /// Minimum pass rate for <c>BidiCharacterTest.txt</c> (real-codepoint corpus,
    /// 91,707 cases including paired-bracket and explicit-control scenarios). 100%.
    /// </summary>
    private const double BidiCharacterTestBaseline = 1.0;

    private readonly ITestOutputHelper _output;

    public BidiUcdConformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void BidiTest_txt_conformance_meets_baseline()
    {
        var result = BidiUcdConformanceHarness.RunBidiTest(sampleFailureLimit: 20);

        _output.WriteLine($"BidiTest.txt: {result.Passed} / {result.Total} passed ({result.PassRate:P3}); {result.Failed} failed.");
        if (result.FirstFailures.Count > 0)
        {
            _output.WriteLine($"First {result.FirstFailures.Count} failures:");
            foreach (var f in result.FirstFailures)
            {
                _output.WriteLine($"  {f}");
            }
        }

        Assert.True(result.Total > 0, "Expected at least one BidiTest.txt case to run.");
        Assert.True(result.PassRate >= BidiTestBaseline,
            $"BidiTest.txt pass rate {result.PassRate:P3} below baseline {BidiTestBaseline:P3}.");
    }

    [Fact]
    public void BidiCharacterTest_txt_conformance_meets_baseline()
    {
        var result = BidiUcdConformanceHarness.RunBidiCharacterTest(sampleFailureLimit: 20);

        _output.WriteLine($"BidiCharacterTest.txt: {result.Passed} / {result.Total} passed ({result.PassRate:P3}); {result.Failed} failed.");
        if (result.FirstFailures.Count > 0)
        {
            _output.WriteLine($"First {result.FirstFailures.Count} failures:");
            foreach (var f in result.FirstFailures)
            {
                _output.WriteLine($"  {f}");
            }
        }

        Assert.True(result.Total > 0, "Expected at least one BidiCharacterTest.txt case to run.");
        Assert.True(result.PassRate >= BidiCharacterTestBaseline,
            $"BidiCharacterTest.txt pass rate {result.PassRate:P3} below baseline {BidiCharacterTestBaseline:P3}.");
    }
}
