// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace NetPdf.UnitTests.Languages;

/// <summary>
/// Pins the SHAPE of the <c>NetPdf.Languages.European</c> NuGet package: it must declare exactly one
/// direct dependency — <c>NetPdf</c> — so a consumer receives NetPdf's own runtime deps
/// (AngleSharp / HarfBuzzSharp / SkiaSharp) transitively through it, instead of the pack re-declaring a
/// dozen dependencies it neither references nor needs. Per PR #264 review [P2].
/// </summary>
/// <remarks>
/// The <c>dotnet pack</c>-generated <c>.nuspec</c> <c>&lt;dependencies&gt;</c> set is fully determined by
/// the csproj metadata asserted here, so this pins the package shape without invoking <c>dotnet pack</c>
/// inside the test (nested MSBuild under <c>dotnet test</c> is slow and flaky). Two invariants:
/// (1) exactly one non-private <c>ProjectReference</c>, to NetPdf (→ the single <c>&lt;dependency
/// id="NetPdf"&gt;</c>); (2) every external <c>PackageReference</c> NetPdf declares is present-and-
/// suppressed (<c>PrivateAssets="all"</c>) in the pack, so none of them leak into the nuspec. Invariant (2)
/// cross-checks against <c>NetPdf.csproj</c>: if NetPdf gains a new external dependency, this test fails
/// until the pack suppresses it too.
/// </remarks>
public sealed class EuropeanPackageShapeTests
{
    [Fact]
    public void Pack_declares_exactly_one_project_reference_and_it_is_netpdf()
    {
        var proj = LoadCsproj("src", "NetPdf.Languages.European", "NetPdf.Languages.European.csproj");
        var projectRefs = proj.Descendants("ProjectReference").ToList();

        Assert.Single(projectRefs);
        var include = ((string?)projectRefs[0].Attribute("Include") ?? string.Empty).Replace('\\', '/');
        // The FACADE (…/NetPdf/NetPdf.csproj), not an internal project like NetPdf.Text.
        Assert.EndsWith("NetPdf/NetPdf.csproj", include);
        // A non-private ProjectReference to a packable project is what emits <dependency id="NetPdf">.
        Assert.Null(projectRefs[0].Attribute("PrivateAssets"));
    }

    [Fact]
    public void Pack_suppresses_every_external_dependency_netpdf_declares()
    {
        var pack = LoadCsproj("src", "NetPdf.Languages.European", "NetPdf.Languages.European.csproj");
        var suppressed = pack.Descendants("PackageReference")
            .Where(e => string.Equals((string?)e.Attribute("PrivateAssets"), "all", StringComparison.OrdinalIgnoreCase))
            .Select(e => (string?)e.Attribute("Include"))
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var netpdfExternals = ExternalPackageIds(LoadCsproj("src", "NetPdf", "NetPdf.csproj"));
        Assert.NotEmpty(netpdfExternals);

        foreach (var dep in netpdfExternals)
        {
            Assert.True(
                suppressed.Contains(dep),
                $"NetPdf declares external dependency '{dep}', but the European pack does not suppress it " +
                "(PrivateAssets=\"all\"). It would leak into the pack's nuspec as a redundant direct " +
                "dependency. Add it to the suppression ItemGroup in NetPdf.Languages.European.csproj.");
        }
    }

    /// <summary>Non-private <c>PackageReference</c> ids of a project (its real external NuGet dependencies).</summary>
    private static string[] ExternalPackageIds(XDocument proj) =>
        proj.Descendants("PackageReference")
            .Where(e => !string.Equals((string?)e.Attribute("PrivateAssets"), "all", StringComparison.OrdinalIgnoreCase))
            .Select(e => (string?)e.Attribute("Include"))
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToArray();

    private static XDocument LoadCsproj(params string[] relativeParts) =>
        XDocument.Load(Path.Combine(new[] { RepoRoot() }.Concat(relativeParts).ToArray()));

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "NetPdf.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException(
            "Could not locate the repo root (NetPdf.slnx) from the test assembly's location. " +
            $"AppContext.BaseDirectory = {AppContext.BaseDirectory}.");
    }
}
