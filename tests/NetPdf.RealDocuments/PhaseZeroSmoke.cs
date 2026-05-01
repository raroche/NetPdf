// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Xunit;

namespace NetPdf.RealDocuments;

public sealed class PhaseZeroSmoke
{
    [Fact]
    public void Solution_Compiles() => Assert.True(true);

    [Theory]
    [InlineData("Corpus/Invoices/01-classic-pure-css.html", 1024)]
    [InlineData("Corpus/Invoices/02-tailwind-cdn.html", 1024)]
    [InlineData("Corpus/Invoices/03-tailwind-cdn-responsive.html", 1024)]
    [InlineData("Corpus/Invoices/04-anvil-running-elements.html", 1024)]
    [InlineData("Corpus/Invoices/README.md", 256)]
    public void Corpus_File_Exists_And_Is_Non_Trivial(string relativePath, int minBytes)
    {
        var corpusRoot = LocateCorpusRoot();
        var fullPath = Path.Combine(corpusRoot, relativePath);
        Assert.True(File.Exists(fullPath), $"corpus file missing: {fullPath}");
        var size = new FileInfo(fullPath).Length;
        Assert.True(size >= minBytes, $"corpus file too small ({size} < {minBytes}): {fullPath}");
    }

    /// <summary>
    /// Walk up from the test assembly's location until we find the test project's source folder
    /// (where the Corpus directory lives). Works in both build-output and IDE-debug runs.
    /// </summary>
    private static string LocateCorpusRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Corpus");
            if (Directory.Exists(candidate)) return dir.FullName;
            // Or if we're under bin/Release/net10.0, walk up to the csproj folder
            var csproj = Path.Combine(dir.FullName, "NetPdf.RealDocuments.csproj");
            if (File.Exists(csproj)) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the NetPdf.RealDocuments source folder.");
    }
}
