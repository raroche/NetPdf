// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using NetPdf.Css.Cascade;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using Xunit;

namespace NetPdf.UnitTests.Css.Cascade;

/// <summary>
/// Review-cycle 2 regression tests for Phase 2 Task 8 — covers the four deeper review
/// recommendations: structured invalidation propagation (no "unset" stored as a
/// real value), distinct expansion-limit diagnostic, ASCII-case-insensitive var()
/// detection, and the Phase 2 doc note for var-bearing shorthands (verified by
/// the matrix update).
/// </summary>
public sealed class VarSubstitutionReviewCycle2Tests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    private static CustomPropertyTable Table(params (string Name, string Value)[] entries)
    {
        var t = new CustomPropertyTable(parent: null);
        foreach (var (n, v) in entries) t.Set(n, v);
        return t;
    }

    private static async Task<IDocument> ParseHtml(string html)
    {
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        return await ctx.OpenAsync(req => req.Content(html));
    }

    private static async Task<CssStylesheet> ParseSheet(string css)
    {
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        var parser = ctx.GetService<AngleSharp.Css.Parser.ICssParser>()!;
        var sheet = parser.ParseStyleSheet(css);
        return CssParserAdapter.Adapt(sheet, href: null,
            origin: CssStylesheetOrigin.Author,
            ownerKind: CssStylesheetOwnerKind.StyleElement,
            mediaQuery: null, isDisabled: false, order: 0);
    }

    private static IElement Q(IDocument doc, string css) => doc.QuerySelector(css)!;

    // ============================================================
    // Rec 1 — Structured SubstitutionResult + invalidation propagation
    // ============================================================

    [Fact]
    public void Rec1_Substitute_returns_invalid_when_var_has_no_fallback_and_name_missing()
    {
        var result = VarSubstitution.Substitute("var(--missing)", Table());
        Assert.True(result.IsInvalid);
        Assert.Equal(VarSubstitution.UnsetSentinel, result.Value);
    }

    [Fact]
    public void Rec1_Substitute_returns_valid_when_fallback_used()
    {
        var result = VarSubstitution.Substitute("var(--missing, blue)", Table());
        Assert.False(result.IsInvalid);
        Assert.Equal("blue", result.Value);
    }

    [Fact]
    public void Rec1_Substitute_returns_valid_for_empty_fallback_substitution()
    {
        // Empty fallback per spec is VALID empty string, not invalid.
        var result = VarSubstitution.Substitute("var(--missing,)", Table());
        Assert.False(result.IsInvalid);
        Assert.Equal("", result.Value);
    }

    [Fact]
    public void Rec1_Substitute_returns_invalid_when_any_inner_var_invalid()
    {
        // padding: 16px var(--missing) — outer is partially-invalid because var() failed.
        var result = VarSubstitution.Substitute("16px var(--missing)", Table());
        Assert.True(result.IsInvalid);
    }

    [Fact]
    public async Task Rec1_Custom_property_with_invalid_value_marks_property_invalid_in_table()
    {
        // p { --a: var(--missing); color: var(--a, green) }
        // --a's value resolves to invalid (var(--missing) → no fallback → invalid).
        // Per spec, --a becomes invalid at computed value time. External var(--a, green)
        // should fall through to "green" (the call site fallback), NOT pick up the
        // "unset" sentinel string from --a's stored value (which was the cycle-1 bug).
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("p { --a: var(--missing); color: var(--a, green) }");
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);

        var color = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Equal("green", color!.ResolvedValue);
    }

    [Fact]
    public async Task Rec1_Chained_invalid_custom_property_falls_through_to_caller_fallback()
    {
        // --a: var(--missing) → invalid
        // --b: var(--a)        → invalid (--a is invalid)
        // color: var(--b, red) → should be "red" (--b invalid, fallback used)
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet(
            "p { --a: var(--missing); --b: var(--a); color: var(--b, red) }");
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);

        var color = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Equal("red", color!.ResolvedValue);
    }

    // ============================================================
    // Rec 2 — Distinct expansion-limit diagnostic
    // ============================================================

    [Fact]
    public void Rec2_Depth_overrun_emits_expansion_limit_not_circular()
    {
        // Long non-cyclic chain past MaxRecursionDepth.
        var entries = new List<(string, string)>();
        for (var i = 0; i < VarSubstitution.MaxRecursionDepth + 5; i++)
        {
            entries.Add(("--n" + i, "var(--n" + (i + 1) + ")"));
        }
        entries.Add(("--n" + (VarSubstitution.MaxRecursionDepth + 5), "x"));
        var t = Table(entries.ToArray());
        var sink = new CapturingSink();

        _ = VarSubstitution.Substitute("var(--n0)", t, sink);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssVarExpansionLimit001);
        Assert.DoesNotContain(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssVarCircular001);
    }

    [Fact]
    public void Rec2_Cycle_still_emits_circular_not_expansion_limit()
    {
        // Sanity — actual cycles still get CssVarCircular001, not CssVarExpansionLimit001.
        var t = Table(("--a", "var(--a)"));
        var sink = new CapturingSink();
        _ = VarSubstitution.Substitute("var(--a)", t, sink);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssVarCircular001);
        Assert.DoesNotContain(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssVarExpansionLimit001);
    }

    [Fact]
    public void Rec2_SCC_pre_pass_cycle_still_emits_circular()
    {
        var t = Table(("--a", "var(--b)"), ("--b", "var(--a)"));
        var sink = new CapturingSink();
        CustomPropertyCycleDetector.DetectAndMarkInvalid(t, sink);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssVarCircular001);
        Assert.DoesNotContain(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssVarExpansionLimit001);
    }

    // ============================================================
    // Rec 3 — ASCII-case-insensitive var() function detection
    // ============================================================

    [Theory]
    [InlineData("VAR(--x)")]
    [InlineData("Var(--x)")]
    [InlineData("vAr(--x)")]
    [InlineData("VaR(--x)")]
    public void Rec3_Var_function_detected_case_insensitively(string source)
    {
        var t = Table(("--x", "red"));
        var result = VarSubstitution.SubstituteToString(source, t);
        Assert.Equal("red", result);
    }

    [Fact]
    public void Rec3_Custom_property_name_inside_var_stays_case_sensitive()
    {
        // VAR(--X) should NOT match a table entry under --x (lowercase) — only the
        // function name is case-insensitive; custom-property names are case-sensitive.
        var t = Table(("--x", "red"));
        var result = VarSubstitution.Substitute("VAR(--X, fallback)", t);
        Assert.Equal("fallback", result.Value);
    }

    [Fact]
    public void Rec3_Cycle_detector_recognizes_case_insensitive_var()
    {
        // --a's value uses VAR(...) uppercase. The cycle detector must still find
        // the dependency for SCC analysis to work.
        var t = Table(("--a", "VAR(--b)"), ("--b", "VAR(--a)"));
        var sink = new CapturingSink();
        CustomPropertyCycleDetector.DetectAndMarkInvalid(t, sink);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssVarCircular001);
    }

    // ============================================================
    // Rec 4 — Phase 2 guide doc note (compatibility-matrix already updated in cycle 1)
    // ============================================================

    [Fact]
    public void Rec4_Phase2_doc_or_compatibility_matrix_mentions_var_bearing_shorthand_gap()
    {
        // The compatibility-matrix entry was added in cycle 1; the Phase 2 guide entry
        // is added in this cycle. Verify both files mention the limitation.
        var compatPath = LocateRepoFile("docs/compatibility-matrix.md");
        var phasePath = LocateRepoFile("docs/phases/phase-2-css-engine.md");
        var compatText = System.IO.File.ReadAllText(compatPath);
        var phaseText = System.IO.File.ReadAllText(phasePath);
        Assert.Contains("pending substitution", compatText, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pending substitution", phaseText, System.StringComparison.OrdinalIgnoreCase);
    }

    private static string LocateRepoFile(string relativePath)
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, relativePath);
            if (System.IO.File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new System.IO.FileNotFoundException(
            $"Could not locate {relativePath} by walking up from {System.AppContext.BaseDirectory}");
    }
}
