// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace NetPdf.UnitTests.Diagnostics;

/// <summary>
/// Parity tests between <see cref="DiagnosticCodes"/> constants and the registry doc at
/// <c>docs/diagnostics-codes.md</c>. The doc is the single source of truth for code names
/// and severities; the constants are emit-side conveniences. If they ever drift, every
/// diagnostic emitted via the constant misses the registry — these tests catch that.
/// </summary>
public sealed class DiagnosticCodesTests
{
    [Fact]
    public void Html_script_ignored_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(
            registry,
            @"\|\s*`?(HTML-SCRIPT-IGNORED-001)`?\s*\|\s*(\w+)\s*\|");

        Assert.True(match.Success, "HTML-SCRIPT-IGNORED-001 row not found in docs/diagnostics-codes.md");

        // Get the typed constant via reflection (DiagnosticCodes is internal — InternalsVisibleTo
        // already covers NetPdf.UnitTests, so a direct ref also works; reflection here keeps the
        // test resilient to future renames of the constant identifier itself.)
        var constantValue = typeof(DiagnosticCodes)
            .GetField(nameof(DiagnosticCodes.HtmlScriptIgnored001), BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null) as string;

        Assert.Equal(match.Groups[1].Value, constantValue);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Html_script_ignored_001_constant_value_is_stable()
    {
        // Codes are documented as stable once published. Pin the literal so a cosmetic
        // rename of the constant identifier never silently changes the wire format.
        Assert.Equal("HTML-SCRIPT-IGNORED-001", DiagnosticCodes.HtmlScriptIgnored001);
    }

    [Fact]
    public void Html_javascript_url_ignored_001_constant_matches_registry_doc()
    {
        var registry = LoadRegistry();
        var match = Regex.Match(
            registry,
            @"\|\s*`?(HTML-JAVASCRIPT-URL-IGNORED-001)`?\s*\|\s*(\w+)\s*\|");

        Assert.True(match.Success, "HTML-JAVASCRIPT-URL-IGNORED-001 row not found in docs/diagnostics-codes.md");

        var constantValue = typeof(DiagnosticCodes)
            .GetField(nameof(DiagnosticCodes.HtmlJavaScriptUrlIgnored001), BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null) as string;

        Assert.Equal(match.Groups[1].Value, constantValue);
        Assert.Equal("Warning", match.Groups[2].Value);
    }

    [Fact]
    public void Html_javascript_url_ignored_001_constant_value_is_stable()
    {
        Assert.Equal("HTML-JAVASCRIPT-URL-IGNORED-001", DiagnosticCodes.HtmlJavaScriptUrlIgnored001);
    }

    private static string LoadRegistry()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "diagnostics-codes.md");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate docs/diagnostics-codes.md by walking up from " + AppContext.BaseDirectory);
    }
}
