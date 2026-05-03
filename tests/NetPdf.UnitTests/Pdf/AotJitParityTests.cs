// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NetPdf.AotSmoke;
using Xunit;
using Xunit.Abstractions;

namespace NetPdf.UnitTests.Pdf;

/// <summary>
/// Enforces — not just documents — the JIT/AOT parity guarantee on the smoke document
/// (Task 24 review follow-up). Two layers:
/// <list type="bullet">
///   <item><b>Factory determinism</b>: <see cref="SmokeDocumentFactory.BuildSmokeDocument"/>
///         must produce byte-identical output across calls in the same process. This
///         catches drift between local edits to the factory and the parity reference.</item>
///   <item><b>Native parity</b>: when an AOT-published binary exists at the expected
///         path (<c>artifacts/aot-smoke/NetPdf.AotSmoke[.exe]</c>), run it via
///         <see cref="Process"/>, parse the printed <c>sha256=&lt;HEX&gt;</c>, and
///         assert it equals the JIT factory's hash. The script
///         <c>scripts/aot-parity.sh</c> publishes the binary then invokes this test;
///         CI runs that script. When the binary is missing, the test logs a clear
///         "skip" message rather than passing silently.</item>
/// </list>
/// <para>
/// The native binary is run with no arguments — it prints to stdout and exits 0 on
/// success. Process spawning here is in TEST code only; the determinism rule "no
/// process spawning at render time" applies to shipped PDF emission, not to test
/// harnesses.
/// </para>
/// </summary>
public sealed class AotJitParityTests
{
    private readonly ITestOutputHelper _output;

    public AotJitParityTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void SmokeDocumentFactory_produces_byte_equal_output_across_calls()
    {
        var first = SmokeDocumentFactory.BuildSmokeDocument();
        var second = SmokeDocumentFactory.BuildSmokeDocument();
        var third = SmokeDocumentFactory.BuildSmokeDocument();

        DeterminismDiagnostics.AssertByteEqualsWithDiagnostics(first, second);
        DeterminismDiagnostics.AssertByteEqualsWithDiagnostics(second, third);
    }

    [Fact]
    public void SmokeDocumentFactory_emits_well_formed_PDF_bytes()
    {
        var bytes = SmokeDocumentFactory.BuildSmokeDocument();
        Assert.True(SmokeDocumentFactory.TryVerifyPdfStructure(bytes, out var failure),
            $"Smoke document failed structural verification: {failure}");
    }

    [Fact]
    public void Native_AOT_binary_produces_byte_identical_output_to_JIT()
    {
        var binaryPath = LocateAotSmokeBinary();
        if (binaryPath is null)
        {
            _output.WriteLine(
                "Native AOT binary not found at expected path " +
                $"({ExpectedAotBinaryRelativePath()}). " +
                "This is expected when running 'dotnet test' directly. " +
                "To enforce JIT/AOT parity, run scripts/aot-parity.sh which publishes the " +
                "binary and re-runs this test.");
            return;
        }

        var jitHash = ComputeHashHex(SmokeDocumentFactory.BuildSmokeDocument());
        _output.WriteLine($"JIT hash: {jitHash}");

        var (aotStdout, aotStderr, exitCode) = RunProcess(binaryPath, []);
        _output.WriteLine($"AOT exit code: {exitCode}");
        _output.WriteLine($"AOT stdout:    {aotStdout}");
        if (!string.IsNullOrEmpty(aotStderr))
        {
            _output.WriteLine($"AOT stderr:    {aotStderr}");
        }

        Assert.Equal(0, exitCode);

        var aotHash = ParseAotHash(aotStdout);
        Assert.True(aotHash is not null,
            $"Could not parse 'sha256=<HEX>' from AOT stdout. Raw stdout: '{aotStdout}'");

        Assert.Equal(jitHash, aotHash);
    }

    [Fact]
    public void Native_AOT_binary_writes_byte_identical_PDF_when_passed_an_output_path()
    {
        var binaryPath = LocateAotSmokeBinary();
        if (binaryPath is null)
        {
            _output.WriteLine(
                $"Native AOT binary not found at expected path ({ExpectedAotBinaryRelativePath()}). " +
                "Run scripts/aot-parity.sh to publish + run this test.");
            return;
        }

        var tmp = Path.Combine(Path.GetTempPath(), $"netpdf-aot-parity-{Guid.NewGuid():N}.pdf");
        try
        {
            var (_, aotStderr, exitCode) = RunProcess(binaryPath, [tmp]);
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(tmp), $"AOT binary did not write the expected file. stderr: {aotStderr}");

            var fileBytes = File.ReadAllBytes(tmp);
            var jitBytes = SmokeDocumentFactory.BuildSmokeDocument();
            DeterminismDiagnostics.AssertByteEqualsWithDiagnostics(jitBytes, fileBytes);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    // ───── Helpers ──────────────────────────────────────────────────────────

    private static string? LocateAotSmokeBinary()
    {
        var repoRoot = TryFindRepoRoot();
        if (repoRoot is null) return null;
        var candidate = Path.Combine(repoRoot, "artifacts", "aot-smoke", AotBinaryFileName());
        return File.Exists(candidate) ? candidate : null;
    }

    private static string ExpectedAotBinaryRelativePath() =>
        Path.Combine("artifacts", "aot-smoke", AotBinaryFileName());

    private static string AotBinaryFileName() =>
        OperatingSystem.IsWindows() ? "NetPdf.AotSmoke.exe" : "NetPdf.AotSmoke";

    private static string? TryFindRepoRoot()
    {
        // AppContext.BaseDirectory is the test binary's bin/Release/net10.0/ output.
        // Walk up looking for the solution file.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (string.IsNullOrEmpty(dir)) break;
            if (File.Exists(Path.Combine(dir, "NetPdf.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static (string Stdout, string Stderr, int ExitCode) RunProcess(string fileName, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {fileName}");
        // 30s safety timeout — the smoke binary should run in milliseconds.
        if (!proc.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            proc.Kill(entireProcessTree: true);
            throw new TimeoutException($"AOT smoke binary did not exit within 30 seconds: {fileName}");
        }
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        return (stdout, stderr, proc.ExitCode);
    }

    private static string? ParseAotHash(string stdout)
    {
        // Expected line shape:
        //   "NetPdf.AotSmoke phase=1 ok byteCount=<N> sha256=<HEX>"
        var match = Regex.Match(stdout, @"sha256=([0-9A-Fa-f]+)");
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static string ComputeHashHex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    // RuntimeInformation here is referenced because future per-platform pin
    // logic (e.g., separate parity reference per OS-arch) lives in this test
    // class. Keeping the using crisp without exposing platform key in a new helper.
    private static string CurrentOsArch =>
        $"{(OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "win" : "unknown")}-{RuntimeInformation.OSArchitecture.ToString().ToLower(CultureInfo.InvariantCulture)}";
}
