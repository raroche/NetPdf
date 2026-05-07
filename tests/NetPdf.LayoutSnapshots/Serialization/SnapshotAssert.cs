// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit.Sdk;

namespace NetPdf.LayoutSnapshots.Serialization;

/// <summary>
/// Task 18 — golden-file snapshot comparison helper. Reads the expected
/// snapshot from disk + compares against <c>actual</c>; throws
/// an xUnit assertion on mismatch with a unified-diff-style report.
/// </summary>
/// <remarks>
/// <para>
/// <b>Update mode.</b> Set the <c>NETPDF_UPDATE_SNAPSHOTS</c> environment
/// variable to <c>"1"</c> / <c>"true"</c> to write <c>actual</c>
/// to the snapshot path instead of comparing. Use this when the underlying
/// box-tree shape intentionally changes (a new feature ships, a hardening
/// rec changes structure, etc.). Review the diff in git before committing.
/// </para>
/// <para>
/// <b>First-run behavior.</b> When the snapshot file does NOT exist + the
/// update env var is NOT set, the helper writes the snapshot anyway + still
/// fails the test so the author notices the first-time generation. This
/// makes new fixtures self-bootstrapping while preventing "test passed
/// because no snapshot existed" silent regressions.
/// </para>
/// <para>
/// <b>Source-tree path.</b> Snapshots are committed alongside the test code
/// (in <c>tests/NetPdf.LayoutSnapshots/Fixtures/</c>) so they're reviewable
/// in git diff. The csproj's <c>CopyToOutputDirectory="PreserveNewest"</c>
/// item group ensures they're available next to the test binary at runtime.
/// In update mode we resolve the source-tree path by walking up from
/// <see cref="AppContext.BaseDirectory"/> until we find the
/// <c>NetPdf.slnx</c> repo root, then descending into the fixtures folder
/// — so <c>NETPDF_UPDATE_SNAPSHOTS=1</c> writes to the source tree, not
/// the bin/ output.
/// </para>
/// </remarks>
internal static class SnapshotAssert
{
    /// <summary>Compare <c>actual</c> against the snapshot
    /// stored at <paramref name="snapshotRelativePath"/> (relative to the
    /// test project's <c>Fixtures/</c> folder). Throws on mismatch.</summary>
    public static void MatchesFile(string actual, string snapshotRelativePath)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(snapshotRelativePath);

        var runtimePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", snapshotRelativePath);
        var sourcePath = ResolveSourceFixturePath(snapshotRelativePath);

        var updateMode = IsUpdateMode();

        if (updateMode)
        {
            WriteSnapshot(sourcePath, actual);
            // Also write to runtime path so subsequent in-run comparisons see
            // the new content. (Each test runs once but the project may have
            // multiple snapshots — keep both in sync.)
            if (sourcePath != runtimePath) WriteSnapshot(runtimePath, actual);
            return;
        }

        if (!File.Exists(runtimePath))
        {
            // First-run bootstrap: write the snapshot to the source tree
            // so the author has a starting point to review + commit. Still
            // fail so the test isn't silently green-on-no-snapshot.
            WriteSnapshot(sourcePath, actual);
            throw new XunitException(
                $"Snapshot did not exist at '{snapshotRelativePath}'. Wrote initial snapshot to {sourcePath}; review + commit, then re-run.");
        }

        var expected = File.ReadAllText(runtimePath);
        var normalizedActual = NormalizeLineEndings(actual);
        var normalizedExpected = NormalizeLineEndings(expected);
        if (normalizedActual == normalizedExpected) return;

        var diff = BuildDiff(normalizedExpected, normalizedActual);
        throw new XunitException(
            $"Snapshot mismatch for '{snapshotRelativePath}'.\n"
            + $"To regenerate: NETPDF_UPDATE_SNAPSHOTS=1 dotnet test tests/NetPdf.LayoutSnapshots\n"
            + $"\n--- expected (snapshot) +++ actual (current run)\n{diff}");
    }

    private static bool IsUpdateMode()
    {
        var env = Environment.GetEnvironmentVariable("NETPDF_UPDATE_SNAPSHOTS");
        return env is not null
            && (env.Equals("1", StringComparison.Ordinal)
                || env.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteSnapshot(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, NormalizeLineEndings(content));
    }

    /// <summary>Walk up from <see cref="AppContext.BaseDirectory"/> until
    /// we find <c>NetPdf.slnx</c> (the repo root marker), then build the
    /// source-tree fixture path. Falls back to the runtime path when the
    /// solution file isn't found (CI standalone test runs, etc.).</summary>
    private static string ResolveSourceFixturePath(string snapshotRelativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NetPdf.slnx")))
            {
                return Path.Combine(
                    dir.FullName,
                    "tests", "NetPdf.LayoutSnapshots", "Fixtures", snapshotRelativePath);
            }
            dir = dir.Parent;
        }
        // Fallback: write next to the binary.
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", snapshotRelativePath);
    }

    private static string NormalizeLineEndings(string s) =>
        s.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>Minimal line-by-line diff for the assertion message — not
    /// a real Myers diff, just first-divergence + a few lines of context.
    /// Sufficient for snapshot mismatches which are typically small + local.</summary>
    private static string BuildDiff(string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        var max = Math.Max(expectedLines.Length, actualLines.Length);
        var sb = new StringBuilder();
        var diffsFound = 0;
        for (var i = 0; i < max && diffsFound < 10; i++)
        {
            var e = i < expectedLines.Length ? expectedLines[i] : "<EOF>";
            var a = i < actualLines.Length ? actualLines[i] : "<EOF>";
            if (e == a) continue;
            sb.Append("L").Append(i + 1).Append(":\n");
            sb.Append("  - ").Append(e).Append('\n');
            sb.Append("  + ").Append(a).Append('\n');
            diffsFound++;
        }
        if (diffsFound == 0)
        {
            sb.Append("(line-by-line equal but text differs — likely trailing-newline mismatch)\n");
        }
        else if (diffsFound == 10)
        {
            sb.Append("(10 differences shown; more may follow)\n");
        }
        return sb.ToString();
    }
}
