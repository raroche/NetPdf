// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace NetPdf.UnitTests;

/// <summary>
/// Pins the SHAPE of the shipped <c>NetPdf</c> NuGet package — the single-package strategy (per CLAUDE.md):
/// one package that BUNDLES every internal <c>NetPdf.*</c> assembly into <c>lib/&lt;tfm&gt;/</c> and declares
/// only the real external runtime dependencies. Phase-5 task 19 (NuGet pack validation). A regression here —
/// dropping a <c>PrivateAssets="all"</c>, adding an internal project without bundling its DLL, or losing an
/// external dependency — would ship a broken package (phantom <c>NetPdf.*</c> deps, or types that don't
/// resolve at consumer load). Asserting the csproj metadata pins this without invoking <c>dotnet pack</c>
/// inside the test.
/// </summary>
public sealed class NetPdfPackageShapeTests
{
    [Fact]
    public void Facade_suppresses_every_internal_project_reference_from_the_nuspec()
    {
        // Each internal NetPdf.* ProjectReference is PrivateAssets="all" so `dotnet pack` does NOT emit a
        // <dependency> on a non-existent NetPdf.* package; the DLLs are bundled instead (test below).
        var internalRefs = InternalProjectReferences(LoadNetPdfCsproj()).ToList();
        Assert.NotEmpty(internalRefs);
        foreach (var r in internalRefs)
        {
            Assert.True(
                string.Equals((string?)r.Attribute("PrivateAssets"), "all", StringComparison.OrdinalIgnoreCase),
                $"Internal ref '{Include(r)}' must be PrivateAssets=\"all\" so it isn't emitted as a package dependency.");
        }
    }

    [Fact]
    public void Facade_declares_its_real_external_runtime_dependencies()
    {
        var pkgs = DeclaredPackageDependencies();

        // The managed external dependency families a consumer needs at runtime (parsing + shaping + raster).
        foreach (var dep in new[] { "AngleSharp", "AngleSharp.Css", "HarfBuzzSharp", "SkiaSharp" })
        {
            Assert.True(pkgs.Contains(dep), $"NetPdf's nuspec must declare the external dependency '{dep}'.");
        }
    }

    [Fact]
    public void Facade_declares_the_native_runtime_asset_packages_for_every_shipping_platform()
    {
        // The managed HarfBuzzSharp / SkiaSharp packages carry NO native binary — the platform `.so`/`.dylib`/
        // `.dll` ship in separate `*.NativeAssets.*` packages. Dropping one (e.g. `SkiaSharp.NativeAssets.Linux`)
        // still passes the managed-dep test above but silently breaks consumers on that OS at first shape/raster
        // call. Pin all six so a regression that removes a native-asset dep fails here (review P2 / Copilot).
        var pkgs = DeclaredPackageDependencies();
        foreach (var dep in new[]
                 {
                     "HarfBuzzSharp.NativeAssets.Linux", "HarfBuzzSharp.NativeAssets.macOS",
                     "HarfBuzzSharp.NativeAssets.Win32", "SkiaSharp.NativeAssets.Linux",
                     "SkiaSharp.NativeAssets.macOS", "SkiaSharp.NativeAssets.Win32",
                 })
        {
            Assert.True(pkgs.Contains(dep),
                $"NetPdf's nuspec must declare the native-asset package '{dep}' — without it the shaping/raster "
                + "native binary is absent on that platform and consumers fault at runtime.");
        }
    }

    // The external <dependency> set a consumer sees in the produced .nuspec: every PackageReference NOT
    // suppressed via PrivateAssets="all" (internal NetPdf.* refs are suppressed and bundled instead).
    private static System.Collections.Generic.HashSet<string> DeclaredPackageDependencies() =>
        LoadNetPdfCsproj().Descendants("PackageReference")
            .Where(e => !string.Equals((string?)e.Attribute("PrivateAssets"), "all", StringComparison.OrdinalIgnoreCase))
            .Select(e => (string?)e.Attribute("Include"))
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Facade_bundles_the_dll_of_every_internal_project_it_references()
    {
        // The BundleInternalProjectAssemblies target must copy each referenced internal project's DLL into
        // lib/<tfm>/. If a NetPdf.* ProjectReference is added but not bundled, its types won't resolve at
        // consumer load time (the ref is PrivateAssets="all", so nothing else pulls the DLL in).
        var proj = LoadNetPdfCsproj();

        var referenced = InternalProjectReferences(proj)
            .Select(r => Path.GetFileNameWithoutExtension(Include(r))) // "…/NetPdf.Css.csproj" → "NetPdf.Css"
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var bundled = proj.Descendants("BuildOutputInPackage")
            .Select(e => Include(e))
            .Where(inc => inc.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(inc => Path.GetFileNameWithoutExtension(inc)) // "…/NetPdf.Css.dll" → "NetPdf.Css"
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(referenced);
        foreach (var name in referenced)
        {
            Assert.True(bundled.Contains(name),
                $"Internal project '{name}' is referenced (PrivateAssets=\"all\") but its DLL is not bundled " +
                "into lib/ by BundleInternalProjectAssemblies — consumers would fail to load its types.");
        }
    }

    // Internal NetPdf.* project references (everything except the facade referencing itself, which it doesn't).
    private static System.Collections.Generic.IEnumerable<XElement> InternalProjectReferences(XDocument proj) =>
        proj.Descendants("ProjectReference").Where(e => Include(e).Contains("/NetPdf.", StringComparison.Ordinal));

    private static string Include(XElement e) =>
        ((string?)e.Attribute("Include") ?? string.Empty).Replace('\\', '/');

    private static XDocument LoadNetPdfCsproj() =>
        XDocument.Load(Path.Combine(RepoRoot(), "src", "NetPdf", "NetPdf.csproj"));

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "NetPdf.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException(
            $"Could not locate the repo root (NetPdf.slnx). AppContext.BaseDirectory = {AppContext.BaseDirectory}.");
    }
}
