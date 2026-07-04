// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace NetPdf.UnitTests.Languages;

/// <summary>
/// Pins the SHAPE of the produced NuGet packages for every <c>NetPdf.Languages.*</c> pack. Each leaf pack
/// (European / Cjk / Arabic / Indic) must declare exactly ONE package dependency — <c>NetPdf</c> — so a
/// consumer receives NetPdf's own runtime deps transitively rather than the pack re-declaring a dozen it
/// doesn't use; and the <c>NetPdf.All</c> meta-package must depend on exactly the four leaf packs. Per PR
/// #264 review [P2], extended to the full pack set. The <c>dotnet pack</c> <c>&lt;dependencies&gt;</c> set is
/// fully determined by the csproj metadata asserted here, so this pins the shape without invoking
/// <c>dotnet pack</c> inside the test (nested MSBuild under <c>dotnet test</c> is slow and flaky).
/// </summary>
public sealed class LanguagePackShapeTests
{
    [Theory]
    [InlineData("NetPdf.Languages.European")]
    [InlineData("NetPdf.Languages.Cjk")]
    [InlineData("NetPdf.Languages.Arabic")]
    [InlineData("NetPdf.Languages.Indic")]
    public void Leaf_pack_has_exactly_one_nonprivate_project_reference_and_it_is_the_netpdf_facade(string packId)
    {
        var proj = LoadCsproj("src", packId, packId + ".csproj");
        var projectRefs = proj.Descendants("ProjectReference").ToList();

        // Only NON-private ProjectReferences become package <dependency> entries. Exactly one → NetPdf.
        var nonPrivate = projectRefs.Where(e => !IsPrivateAll(e)).Select(Include).ToList();
        Assert.Single(nonPrivate);
        Assert.EndsWith("NetPdf/NetPdf.csproj", nonPrivate[0]); // the FACADE, → <dependency id="NetPdf">

        // NetPdf.Text is referenced COMPILE-ONLY (PrivateAssets="all"): HyphenationRegistry lives there
        // (so NetPdf.Layout can route by lang), but NetPdf.Text.dll ships inside the NetPdf package at
        // runtime, so it must NOT add a second package dependency.
        var netpdfText = projectRefs.Single(e => Include(e).EndsWith("NetPdf.Text/NetPdf.Text.csproj"));
        Assert.True(IsPrivateAll(netpdfText),
            $"{packId}: NetPdf.Text must be PrivateAssets=\"all\" (compile-only) so it doesn't leak into the nuspec.");
    }

    [Theory]
    [InlineData("NetPdf.Languages.European")]
    [InlineData("NetPdf.Languages.Cjk")]
    [InlineData("NetPdf.Languages.Arabic")]
    [InlineData("NetPdf.Languages.Indic")]
    [InlineData("NetPdf.Languages.All")]
    public void Pack_suppresses_every_external_dependency_netpdf_declares(string packId)
    {
        var pack = LoadCsproj("src", packId, packId + ".csproj");
        var suppressed = pack.Descendants("PackageReference")
            .Where(IsPrivateAll)
            .Select(Include)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var netpdfExternals = ExternalPackageIds(LoadCsproj("src", "NetPdf", "NetPdf.csproj"));
        Assert.NotEmpty(netpdfExternals);

        foreach (var dep in netpdfExternals)
        {
            Assert.True(
                suppressed.Contains(dep),
                $"{packId}: NetPdf declares external dependency '{dep}', but the pack does not suppress it " +
                "(PrivateAssets=\"all\") — it would leak into the pack's nuspec as a redundant direct dependency.");
        }
    }

    [Fact]
    public void All_metapackage_depends_on_exactly_the_four_leaf_packs()
    {
        var proj = LoadCsproj("src", "NetPdf.Languages.All", "NetPdf.Languages.All.csproj");
        var nonPrivate = proj.Descendants("ProjectReference")
            .Where(e => !IsPrivateAll(e))
            .Select(e => Path.GetFileNameWithoutExtension(Include(e)))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "NetPdf.Languages.Arabic",
                "NetPdf.Languages.Cjk",
                "NetPdf.Languages.European",
                "NetPdf.Languages.Indic",
            },
            nonPrivate);
    }

    private static string Include(XElement e) =>
        ((string?)e.Attribute("Include") ?? string.Empty).Replace('\\', '/');

    private static bool IsPrivateAll(XElement e) =>
        string.Equals((string?)e.Attribute("PrivateAssets"), "all", StringComparison.OrdinalIgnoreCase);

    private static string[] ExternalPackageIds(XDocument proj) =>
        proj.Descendants("PackageReference")
            .Where(e => !IsPrivateAll(e))
            .Select(Include)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToArray();

    private static XDocument LoadCsproj(params string[] relativeParts) =>
        XDocument.Load(Path.Combine(new[] { RepoRoot() }.Concat(relativeParts).ToArray()));

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
            "Could not locate the repo root (NetPdf.slnx) from the test assembly's location. " +
            $"AppContext.BaseDirectory = {AppContext.BaseDirectory}.");
    }
}
