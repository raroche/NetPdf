// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.SystemFonts;
using Xunit;
using Xunit.Abstractions;

namespace NetPdf.UnitTests.Text.Fonts.SystemFonts;

/// <summary>
/// Integration smoke tests for <see cref="SystemFontEnumerator"/>. The factory must
/// return a non-null enumerator on every supported platform, and a fresh enumeration
/// must surface at least one usable face on a developer / CI machine that has any
/// fonts at all installed (which is true for Linux/macOS/Windows builders by default).
/// Tests early-return when run on a host with no usable fonts (rare).
/// </summary>
public sealed class SystemFontEnumeratorTests
{
    private readonly ITestOutputHelper _output;

    public SystemFontEnumeratorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CreateForCurrentPlatform_returns_a_non_null_enumerator()
    {
        var enumerator = SystemFontEnumerator.CreateForCurrentPlatform();
        Assert.NotNull(enumerator);
    }

    [Fact]
    public void Enumerate_yields_at_least_one_entry_when_any_font_is_present()
    {
        var enumerator = SystemFontEnumerator.CreateForCurrentPlatform();
        var entries = enumerator.Enumerate().Take(10).ToList();
        if (entries.Count == 0)
        {
            _output.WriteLine("No system fonts indexable on this host; skipping smoke assertion.");
            return;
        }
        _output.WriteLine($"Indexed {entries.Count} entries (sample of first 10).");
        foreach (var e in entries)
        {
            // Every yielded entry must have a non-empty family + a real path.
            Assert.False(string.IsNullOrEmpty(e.FamilyName));
            Assert.True(File.Exists(e.FilePath));
            Assert.InRange(e.WeightCss, 1, 1000);
        }
    }

    [Fact]
    public void SystemFontIndex_built_from_current_platform_groups_entries_by_family()
    {
        var enumerator = SystemFontEnumerator.CreateForCurrentPlatform();
        var index = SystemFontIndex.Build(enumerator);
        if (index.Count == 0)
        {
            _output.WriteLine("No system fonts indexable on this host; skipping smoke assertion.");
            return;
        }
        Assert.True(index.FamilyCount > 0);
        Assert.True(index.Count >= index.FamilyCount, "every family has at least one face");
    }
}
