// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace NetPdf.UnitTests.Security;

/// <summary>
/// SEC-3 — the enforced SSRF choke-point invariant. `SafeResourceLoader` is documented as the ONLY
/// pipeline-internal entry point for resource fetches (its class doc: "when Phase 5 wires resource
/// fetching, every consumer … calls THIS, never the underlying <c>IResourceLoader</c> directly"). This
/// suite turns that prose promise into a **compile-of-CI guard**: it scans the production sources and fails
/// if any code outside the two sanctioned files reaches the raw fetch primitives.
///
/// <para>This is the sequencing gate for the Phase-5 CSS/font resource-loading feature (@import,
/// @font-face, and stylesheet links are extracted-but-not-fetched today). When that feature is wired,
/// it MUST route through <c>SafeResourceLoader.FetchAsync</c> with the correct <c>ResourceKind</c>; if a
/// future change instead calls <c>IResourceLoader.LoadAsync</c> or news up its own <c>HttpClient</c>, the
/// SSRF / IP-blocklist / redirect / DNS-pin / MIME / budget defenses are bypassed wholesale — and one of
/// these tests goes red.</para>
/// </summary>
[Trait("Category", "Security")]
public sealed class ResourceChokePointInvariantTests
{
    // The raw loader entry (`IResourceLoader.LoadAsync`) may be INVOKED only by the wrapper.
    private const string RawLoaderCall = ".LoadAsync(";
    private static readonly string[] SanctionedLoaderCallers = ["SafeResourceLoader.cs"];

    // A real HTTP client may be constructed only by the one hardened HTTP loader.
    private static readonly string[] HttpConstructionMarkers = ["new HttpClient", "new SocketsHttpHandler"];
    private static readonly string[] SanctionedHttpFiles = ["SafeHttpResourceLoader.cs"];

    [Fact]
    public void Raw_resource_loader_is_invoked_only_by_the_safe_wrapper()
    {
        var offenders = ProductionSources()
            .Where(f => File.ReadAllText(f).Contains(RawLoaderCall, StringComparison.Ordinal))
            .Where(f => !SanctionedLoaderCallers.Contains(Path.GetFileName(f)))
            .ToList();

        Assert.True(offenders.Count == 0,
            "SSRF choke-point violation: IResourceLoader.LoadAsync must only be invoked by SafeResourceLoader " +
            "(the wrapper that applies URI-safety / IP-blocklist / MIME / budget checks). A direct call bypasses " +
            "every defense. Offending file(s):\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Http_clients_are_constructed_only_by_the_safe_http_loader()
    {
        var offenders = ProductionSources()
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return HttpConstructionMarkers.Any(m => text.Contains(m, StringComparison.Ordinal));
            })
            .Where(f => !SanctionedHttpFiles.Contains(Path.GetFileName(f)))
            .ToList();

        Assert.True(offenders.Count == 0,
            "SSRF choke-point violation: a real HTTP client (HttpClient / SocketsHttpHandler) must only be " +
            "constructed by SafeHttpResourceLoader (which pins the resolved IP, disables auto-redirect, and " +
            "re-validates every hop). A raw client bypasses the DNS-rebind + redirect SSRF defenses. Offending " +
            "file(s):\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Guard_actually_scanned_the_production_sources()
    {
        // Fail loudly if the source root couldn't be located — a silently-empty scan would make the two
        // guards above vacuously pass and give false assurance.
        var count = ProductionSources().Count();
        Assert.True(count > 200, $"expected to scan the full src/ tree; only found {count} .cs files");
    }

    /// <summary>Every production C# source under <c>src/</c>, excluding build output and the macOS
    /// "* N.cs" duplicate gremlins (which the build also excludes via Directory.Build.targets).</summary>
    private static IEnumerable<string> ProductionSources()
    {
        var srcRoot = LocateSrcRoot();
        return Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !IsDuplicateGremlin(Path.GetFileNameWithoutExtension(f)));
    }

    // Matches the Directory.Build.targets `**/* N.cs` compile-removal (N = 2..10): a trailing " <digits>".
    private static bool IsDuplicateGremlin(string stem)
    {
        var space = stem.LastIndexOf(' ');
        return space > 0 && space < stem.Length - 1 && stem[(space + 1)..].All(char.IsDigit);
    }

    private static string LocateSrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            // The repo root holds both NetPdf.slnx and the src/ tree.
            if (File.Exists(Path.Combine(dir.FullName, "NetPdf.slnx"))
                && Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return Path.Combine(dir.FullName, "src");
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the src/ root by walking up from " + AppContext.BaseDirectory);
    }
}
