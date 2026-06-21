// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace NetPdf.UnitTests.Docs;

/// <summary>Per PR #205 review [P1] — a RELEASE GUARD against version drift. The
/// CHANGELOG's "version bumped" claim once outran the actual package version: the
/// staged <c>0.7.0-beta</c> CHANGELOG entry shipped while <c>Directory.Build.props</c>
/// + <c>build/version.json</c> still said <c>0.3.0-alpha</c> (and <c>HtmlPdf.Version</c>
/// with them). This test pins the THREE version surfaces together so the docs /
/// package / tag version cannot silently disagree again:
/// <list type="number">
///   <item><c>Directory.Build.props</c> — <c>VersionPrefix</c> + <c>VersionSuffix</c>
///     (the MSBuild / NuGet source-of-truth; <c>HtmlPdf.Version</c> reads it via
///     <c>AssemblyInformationalVersion</c>).</item>
///   <item><c>build/version.json</c> — the informational copy for non-XML tooling.</item>
///   <item><c>CHANGELOG.md</c> — the latest (non-<c>[Unreleased]</c>) version heading.</item>
/// </list>
/// When you bump the version, update all three in the SAME commit + this test goes
/// green again. The git tag the maintainer creates must match this version too.</summary>
public sealed class ReleaseVersionParityTests
{
    [Fact]
    public void Build_props_version_json_and_changelog_agree_on_the_release_version()
    {
        var root = RepoRoot();
        var propsVersion = ReadBuildPropsVersion(Path.Combine(root, "Directory.Build.props"));
        var jsonVersion = ReadVersionJson(Path.Combine(root, "build", "version.json"));
        var changelogVersion = ReadLatestChangelogVersion(Path.Combine(root, "CHANGELOG.md"));

        Assert.True(propsVersion == jsonVersion,
            $"Version drift: Directory.Build.props = '{propsVersion}' but build/version.json = " +
            $"'{jsonVersion}'. Bump BOTH in the same commit.");
        Assert.True(propsVersion == changelogVersion,
            $"Version drift: Directory.Build.props = '{propsVersion}' but the latest CHANGELOG.md " +
            $"version heading = '{changelogVersion}'. The staged release entry must match the " +
            "package version.");
    }

    private static string ReadBuildPropsVersion(string path)
    {
        var xml = File.ReadAllText(path);
        var prefix = Regex.Match(xml, @"<VersionPrefix>([^<]+)</VersionPrefix>").Groups[1].Value.Trim();
        var suffixMatch = Regex.Match(xml, @"<VersionSuffix>([^<]*)</VersionSuffix>");
        Assert.True(prefix.Length > 0, "Directory.Build.props is missing <VersionPrefix>.");
        var suffix = suffixMatch.Success ? suffixMatch.Groups[1].Value.Trim() : string.Empty;
        return suffix.Length > 0 ? $"{prefix}-{suffix}" : prefix;
    }

    private static string ReadVersionJson(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("version").GetString()
            ?? throw new InvalidOperationException("build/version.json has no 'version' string.");
    }

    private static string ReadLatestChangelogVersion(string path)
    {
        // The first `## [X] — ...` heading that is NOT [Unreleased] is the latest
        // staged / released version.
        foreach (var line in File.ReadLines(path))
        {
            var m = Regex.Match(line, @"^##\s*\[([^\]]+)\]");
            if (!m.Success) continue;
            var v = m.Groups[1].Value.Trim();
            if (string.Equals(v, "Unreleased", StringComparison.OrdinalIgnoreCase)) continue;
            return v;
        }
        throw new InvalidOperationException("No versioned heading found in CHANGELOG.md.");
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "NetPdf.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Could not locate the repo root (NetPdf.slnx) from the test assembly's " +
            $"location. AppContext.BaseDirectory = {AppContext.BaseDirectory}.");
    }
}
