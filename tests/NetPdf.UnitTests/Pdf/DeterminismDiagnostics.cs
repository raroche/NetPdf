// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

/// <summary>
/// Helpers used by the determinism harness (and any future byte-deterministic test) to
/// produce diagnosable failures when bytes drift, and to gate platform-pinned snapshots
/// to the platform they were captured on.
/// </summary>
internal static class DeterminismDiagnostics
{
    /// <summary>
    /// Coarse OS+architecture identifier ("osx-arm64", "linux-x64", "win-x64", etc).
    /// Used as a key into platform-pinned hash maps. Coarse rather than full RID
    /// (e.g. "osx.14.5-arm64") because zlib output and PDF byte format are stable
    /// across point releases of the OS — only the OS family and CPU architecture
    /// matter. Major .NET runtime version bumps may still drift the hash; the harness
    /// documents that assumption and re-pins on bump.
    /// </summary>
    public static string CurrentPlatformKey
    {
        get
        {
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X86 => "x86",
                Architecture.X64 => "x64",
                Architecture.Arm => "arm",
                Architecture.Arm64 => "arm64",
                _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            };
            var os = OperatingSystem.IsMacOS() ? "osx"
                   : OperatingSystem.IsLinux() ? "linux"
                   : OperatingSystem.IsWindows() ? "win"
                   : "unknown";
            return $"{os}-{arch}";
        }
    }

    /// <summary>
    /// Assert two byte arrays are equal, producing a diagnosable message on failure that
    /// localizes the first divergence and prints both context windows in hex + ASCII.
    /// Plain <c>Assert.Equal(byte[], byte[])</c> on multi-KB arrays prints the entire
    /// array, which is unreadable; this helper prints a 64-byte window centered on the
    /// first differing byte plus per-half SHA-256 to identify which section drifted
    /// (header vs. body vs. xref vs. trailer).
    /// </summary>
    public static void AssertByteEqualsWithDiagnostics(byte[] expected, byte[] actual)
    {
        if (expected.Length == actual.Length)
        {
            var firstDiff = -1;
            for (var i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    firstDiff = i;
                    break;
                }
            }
            if (firstDiff < 0) return; // equal
            Assert.Fail(BuildMismatchMessage(expected, actual, firstDiff));
        }

        // Length mismatch: still find first difference within the common prefix.
        var common = Math.Min(expected.Length, actual.Length);
        var firstDiffOffset = -1;
        for (var i = 0; i < common; i++)
        {
            if (expected[i] != actual[i])
            {
                firstDiffOffset = i;
                break;
            }
        }
        if (firstDiffOffset < 0) firstDiffOffset = common; // diverged only by length
        Assert.Fail(BuildMismatchMessage(expected, actual, firstDiffOffset));
    }

    /// <summary>
    /// Cheap structural sanity assertions on emitted PDF bytes — every well-formed PDF
    /// must (a) start with the version comment <c>%PDF-X.Y</c>, (b) contain the
    /// keywords <c>xref</c> and <c>startxref</c>, and (c) end with <c>%%EOF</c>
    /// (followed by an optional newline). This guards against the failure mode where
    /// a future regression produces stable-but-corrupt bytes — the SHA-256 pin alone
    /// would still pass, but these checks would catch it.
    /// </summary>
    public static void AssertWellFormedPdfShape(byte[] bytes)
    {
        Assert.True(bytes.Length > 256, $"PDF bytes too short to be valid (got {bytes.Length}).");

        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(16, bytes.Length));
        Assert.True(head.StartsWith("%PDF-", StringComparison.Ordinal),
            $"Missing %PDF- header (head bytes: '{head}').");

        var content = Encoding.ASCII.GetString(bytes);
        Assert.Contains("xref", content, StringComparison.Ordinal);
        Assert.Contains("startxref", content, StringComparison.Ordinal);

        // %%EOF can be followed by any of CR / LF / CRLF / nothing per ISO 32000-2 §7.5.5.
        // Search the trailing 16 bytes (generous) for the literal sequence.
        var tailLen = Math.Min(16, bytes.Length);
        var tail = Encoding.ASCII.GetString(bytes, bytes.Length - tailLen, tailLen);
        Assert.Contains("%%EOF", tail, StringComparison.Ordinal);
    }

    private static string BuildMismatchMessage(byte[] expected, byte[] actual, int offset)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"Byte arrays differ. expected.Length={expected.Length}, actual.Length={actual.Length}, firstDiffOffset={offset}.");
        sb.AppendLine();

        sb.Append(CultureInfo.InvariantCulture,
            $"  SHA-256 expected: {Convert.ToHexString(SHA256.HashData(expected))}");
        sb.AppendLine();
        sb.Append(CultureInfo.InvariantCulture,
            $"  SHA-256 actual:   {Convert.ToHexString(SHA256.HashData(actual))}");
        sb.AppendLine();

        // Per-half SHA-256: localizes drift to "is the divergence in the front half or
        // back half." Useful when the first-differing-offset is near the boundary —
        // sometimes the back half is byte-identical because only a length-affecting
        // earlier insertion has shifted everything.
        if (expected.Length >= 4 && actual.Length >= 4)
        {
            var halfE = expected.Length / 2;
            var halfA = actual.Length / 2;
            sb.Append(CultureInfo.InvariantCulture,
                $"  expected first-half SHA-256:  {Convert.ToHexString(SHA256.HashData(expected.AsSpan(0, halfE)))}");
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture,
                $"  actual   first-half SHA-256:  {Convert.ToHexString(SHA256.HashData(actual.AsSpan(0, halfA)))}");
            sb.AppendLine();
        }

        // 32-byte window before and after the first difference, in hex + ASCII.
        const int Window = 32;
        var winStart = Math.Max(0, offset - Window);
        AppendWindow(sb, "expected", expected, winStart, offset, Window);
        AppendWindow(sb, "actual  ", actual, winStart, offset, Window);
        return sb.ToString();
    }

    private static void AppendWindow(StringBuilder sb, string label, byte[] bytes, int start, int diffAt, int radius)
    {
        var end = Math.Min(bytes.Length, diffAt + radius);
        sb.Append(CultureInfo.InvariantCulture, $"  {label}[{start}..{end}): ");
        for (var i = start; i < end; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{bytes[i]:X2}");
            if (i == diffAt - 1) sb.Append('|');
            else if (i < end - 1) sb.Append(' ');
        }
        sb.AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"  {label} ASCII:        ");
        for (var i = start; i < end; i++)
        {
            var c = bytes[i];
            sb.Append(c is >= 0x20 and < 0x7F ? (char)c : '.');
        }
        sb.AppendLine();
    }
}
