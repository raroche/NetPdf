// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf;
using Xunit;

namespace NetPdf.UnitTests;

/// <summary>
/// Public-surface contract tests for the <see cref="HtmlPdf"/> facade. Phase 1 alpha
/// keeps the facade's <c>Convert</c> family throwing <see cref="NotImplementedException"/>;
/// Phase 2 wires HTML parsing + CSS cascade + layout through to the existing byte writer.
/// These tests pin the public contract during the gap so a regression doesn't quietly
/// drift back to Phase 0 messaging or mis-report the running version.
/// </summary>
public sealed class HtmlPdfFacadeTests
{
    [Fact]
    public void Version_reports_the_package_informational_version_not_the_assembly_version()
    {
        // The fix from the Phase 1 global review: HtmlPdf.Version must read
        // AssemblyInformationalVersionAttribute (which carries the prerelease tag —
        // "0.1.0-alpha+<sha>") rather than AssemblyName.Version (which gives the
        // 4-part assembly version "0.1.0.0" and silently drops the prerelease).
        var version = HtmlPdf.Version;

        Assert.False(string.IsNullOrEmpty(version));
        // Must NOT be the 4-part assembly version with all-numeric segments.
        Assert.False(System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+\.\d+$"),
            $"HtmlPdf.Version returned the 4-part assembly version '{version}' — should be the " +
            "informational/package version (with the prerelease tag preserved).");
        // Phase 2 carries the 0.3.0-alpha tag. When this assertion fails on a
        // legitimate version bump (Phase 3 → 0.7.0-beta, etc.), update the expectation.
        Assert.Contains("0.3.0", version, StringComparison.Ordinal);
        Assert.Contains("alpha", version, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_string_throws_NotImplementedException_at_phase_2_alpha()
    {
        var ex = Assert.Throws<NotImplementedException>(() =>
            HtmlPdf.Convert("<p>hello</p>"));
        AssertPhase2AlphaMessage(ex.Message);
    }

    [Fact]
    public async Task ConvertAsync_throws_NotImplementedException_at_phase_2_alpha()
    {
        var ex = await Assert.ThrowsAsync<NotImplementedException>(async () =>
            await HtmlPdf.ConvertAsync("<p>hello</p>"));
        AssertPhase2AlphaMessage(ex.Message);
    }

    [Fact]
    public void ConvertDetailed_throws_NotImplementedException_at_phase_2_alpha()
    {
        var ex = Assert.Throws<NotImplementedException>(() =>
            HtmlPdf.ConvertDetailed("<p>hello</p>"));
        AssertPhase2AlphaMessage(ex.Message);
    }

    [Fact]
    public void Convert_throws_ArgumentNullException_for_null_html()
    {
        Assert.Throws<ArgumentNullException>(() => HtmlPdf.Convert(html: null!));
    }

    [Fact]
    public async Task ConvertAsync_string_overload_throws_ArgumentNullException_for_null_html()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await HtmlPdf.ConvertAsync(html: null!));
    }

    /// <summary>
    /// Pins the throw-message contract so a regression to "Phase 0" / stale "Phase 1"
    /// messaging would fail loudly. Phase 5 wires the public facade through to the
    /// internal byte writer, at which point this helper goes away with the throws.
    /// </summary>
    private static void AssertPhase2AlphaMessage(string message)
    {
        // The message must NOT identify itself as Phase 0 / 1 — those were prior states.
        Assert.DoesNotContain("Phase 0", message, StringComparison.Ordinal);
        Assert.DoesNotContain("0.1.0-alpha", message, StringComparison.Ordinal);
        // The message MUST acknowledge the 0.3.0-alpha milestone or Phase 3+ dependency.
        Assert.True(
            message.Contains("0.3.0-alpha", StringComparison.Ordinal)
            || message.Contains("Phase 3", StringComparison.Ordinal)
            || message.Contains("Phase 5", StringComparison.Ordinal),
            $"Phase-2-alpha throw message must reference '0.3.0-alpha' or a future-phase dependency " +
            $"so the user knows which milestone is in flight. Got: '{message}'");
    }
}
