// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Linq;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Io;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;
using NetPdf.Css.Selectors;
using Xunit;

namespace NetPdf.UnitTests.Phase2;

/// <summary>
/// Regression tests for the 3 Critical findings from the Phase 2 deep review:
/// C-1 (selector recursion stack-overflow), C-2 (selector text injection into
/// diagnostics), C-3 (alpha component clamping instead of validation).
/// </summary>
public sealed class Phase2DeepReviewCriticalFixesTests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    // --- C-1: Selector parser depth limit ------------------------------------

    [Fact]
    public void C1_deeply_nested_has_pseudo_throws_parse_exception_not_stack_overflow()
    {
        // Build `:has(:has(:has(... 200 levels ...:has(.x)...)))` — well past
        // the depth limit (64) but well below what would actually overflow
        // the .NET 1MiB default stack. :has() uses StrictSubGroup context so
        // an inner parse failure (the depth-exceeded throw) propagates
        // outward instead of being swallowed by the forgiving wrapper that
        // :is() / :where() use. Without the depth guard, the parser would
        // recurse + eventually SOE on a much deeper input. The guard turns
        // the failure into a catchable SelectorParseException.
        const int depth = 200;
        var selector = string.Concat(Enumerable.Repeat(":has(", depth)) + ".x" + new string(')', depth);

        var ex = Assert.Throws<SelectorParseException>(() => SelectorCompiler.Compile(selector));
        Assert.Contains("depth", ex.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void C1_deeply_nested_not_pseudo_also_throws_parse_exception()
    {
        // Same depth-guard verification for `:not()`.
        // (:is() / :where() are intentionally NOT tested — they're "forgiving"
        // sub-groups per CSS Selectors L4 §3.7, so a depth-exceeded throw
        // inside them is correctly swallowed; the alternative is dropped + the
        // forgiving wrapper returns SelectorList.Empty. Strict-context
        // pseudo-classes — :not() and :has() — are where the guard manifests
        // as a propagated exception.)
        const int depth = 200;
        var selector = string.Concat(Enumerable.Repeat(":not(", depth)) + ".x" + new string(')', depth);
        Assert.Throws<SelectorParseException>(() => SelectorCompiler.Compile(selector));
    }

    [Fact]
    public void C1_reasonable_nesting_still_compiles_cleanly()
    {
        // 5 levels of nesting — typical real-world stylesheet usage —
        // must NOT trip the depth limit (which is set to 64).
        const string selector = ":is(:is(:not(:is(.foo, .bar))) .x .y)";
        var compiled = SelectorCompiler.Compile(selector);
        Assert.False(compiled.Alternatives.IsDefaultOrEmpty);
    }

    // --- C-2: Diagnostic message sanitization --------------------------------

    [Fact]
    public void C2_invalid_selector_with_ansi_escapes_strips_control_chars()
    {
        // A hostile selector embeds an ANSI red-color escape sequence + a
        // bell + a NUL. Without sanitization those would land in the
        // diagnostic message verbatim, potentially injecting into a sink
        // that interprets terminal escapes (CI logs, JSON encoders, etc.).
        // The sanitizer replaces controls with U+FFFD so the redaction is
        // observable but inert.
        const string hostile = "\x1b[31m.dangerous\x07\x00 {{not-a-selector";
        var rule = new CssStyleRule(
            new CssSelector(hostile),
            System.Collections.Immutable.ImmutableArray<CssDeclaration>.Empty,
            CssSourceLocation.Unknown);
        var sink = new CapturingSink();

        // Reach the diagnostic emission via a dedicated probe — the
        // CompileSelectorWithDiagnostics path is internal but called from
        // CascadeResolver.CollectRules. We compile a stylesheet with a single
        // rule whose selector is hostile + no rules to avoid a separate
        // failure.
        var sheet = new CssStylesheet(
            Rules: System.Collections.Immutable.ImmutableArray.Create<CssRule>(rule),
            Href: null,
            Origin: CssStylesheetOrigin.Author,
            OwnerKind: CssStylesheetOwnerKind.StyleElement,
            MediaQuery: null,
            IsDisabled: false,
            Order: 0,
            Location: CssSourceLocation.Unknown);
        var doc = ParseDoc("<!doctype html><html><body></body></html>");
        var media = NetPdf.Css.Cascade.CssMediaContext.DefaultPrint;
        _ = NetPdf.Css.Cascade.CascadeResolver.Resolve(doc, [sheet], media, sink);

        var dx = Assert.Single(sink.Diagnostics);
        Assert.Equal("CSS-PARSE-WARNING-001", dx.Code);
        // No raw control chars (0x00..0x1F or 0x7F..0x9F) — they're replaced
        // with U+FFFD. Specifically, the original NUL/BEL/ESC/[/3/1/m/<NUL>
        // become the replacement marker.
        foreach (var ch in dx.Message)
        {
            Assert.False(ch < 0x20 || ch == 0x7F || (ch >= 0x80 && ch <= 0x9F),
                $"control character U+{(int)ch:X4} leaked into diagnostic message");
        }
    }

    [Fact]
    public void C2_extremely_long_selector_truncated_with_ellipsis()
    {
        // A multi-megabyte hostile selector would bloat the diagnostic
        // message size proportionally without a length cap. Verify the cap
        // applies + appends the ellipsis marker.
        var hostile = "{{" + new string('a', 10_000) + " bad-selector";
        var rule = new CssStyleRule(
            new CssSelector(hostile),
            System.Collections.Immutable.ImmutableArray<CssDeclaration>.Empty,
            CssSourceLocation.Unknown);
        var sheet = new CssStylesheet(
            Rules: System.Collections.Immutable.ImmutableArray.Create<CssRule>(rule),
            Href: null,
            Origin: CssStylesheetOrigin.Author,
            OwnerKind: CssStylesheetOwnerKind.StyleElement,
            MediaQuery: null,
            IsDisabled: false,
            Order: 0,
            Location: CssSourceLocation.Unknown);
        var doc = ParseDoc("<!doctype html><html><body></body></html>");
        var media = NetPdf.Css.Cascade.CssMediaContext.DefaultPrint;
        var sink = new CapturingSink();
        _ = NetPdf.Css.Cascade.CascadeResolver.Resolve(doc, [sheet], media, sink);

        var dx = Assert.Single(sink.Diagnostics);
        Assert.Contains("…", dx.Message);
        // Message length is bounded — message format is roughly
        // 'Invalid selector "<truncated 80 chars>…" — <reason>. Rule skipped.'
        // so total stays under ~250 chars.
        Assert.True(dx.Message.Length < 300, $"diagnostic message too long: {dx.Message.Length} chars");
    }

    // --- C-3: Alpha range validation -----------------------------------------

    [Fact]
    public void C3_alpha_above_one_in_modern_rgb_is_rejected_not_clamped()
    {
        // `rgb(255 0 0 / 2.0)` — alpha 2.0 is out of range. Per CSS Color L4
        // §4.2.1 this must reject the declaration, not silently clamp to
        // opaque. Cycle-1 silently clamped.
        var sink = new CapturingSink();
        var result = ColorResolver.Resolve(
            "rgb(255 0 0 / 2.0)", PropertyId.Color, "color", sink, CssSourceLocation.Unknown);
        Assert.Equal(ResolutionState.Invalid, result.State);
        Assert.Contains(sink.Diagnostics, d => d.Code == "CSS-PROPERTY-VALUE-INVALID-001");
    }

    [Fact]
    public void C3_alpha_below_zero_in_modern_rgb_is_rejected()
    {
        var sink = new CapturingSink();
        var result = ColorResolver.Resolve(
            "rgb(255 0 0 / -0.5)", PropertyId.Color, "color", sink, CssSourceLocation.Unknown);
        Assert.Equal(ResolutionState.Invalid, result.State);
    }

    [Fact]
    public void C3_alpha_percentage_above_100_is_rejected()
    {
        var sink = new CapturingSink();
        var result = ColorResolver.Resolve(
            "rgb(255 0 0 / 200%)", PropertyId.Color, "color", sink, CssSourceLocation.Unknown);
        Assert.Equal(ResolutionState.Invalid, result.State);
    }

    [Fact]
    public void C3_alpha_at_boundary_values_accepts_zero_and_one_inclusive()
    {
        // Boundary check: 0 and 1 must be valid; 0% and 100% must be valid.
        var sink = new CapturingSink();
        Assert.Equal(ResolutionState.Resolved,
            ColorResolver.Resolve("rgb(255 0 0 / 0)", PropertyId.Color, "color", sink, CssSourceLocation.Unknown).State);
        Assert.Equal(ResolutionState.Resolved,
            ColorResolver.Resolve("rgb(255 0 0 / 1)", PropertyId.Color, "color", sink, CssSourceLocation.Unknown).State);
        Assert.Equal(ResolutionState.Resolved,
            ColorResolver.Resolve("rgb(255 0 0 / 0%)", PropertyId.Color, "color", sink, CssSourceLocation.Unknown).State);
        Assert.Equal(ResolutionState.Resolved,
            ColorResolver.Resolve("rgb(255 0 0 / 100%)", PropertyId.Color, "color", sink, CssSourceLocation.Unknown).State);
    }

    [Fact]
    public void C3_legacy_rgba_alpha_above_one_also_rejected()
    {
        // Legacy comma-form alpha must also follow the validation rule.
        var sink = new CapturingSink();
        var result = ColorResolver.Resolve(
            "rgba(255, 0, 0, 1.5)", PropertyId.Color, "color", sink, CssSourceLocation.Unknown);
        Assert.Equal(ResolutionState.Invalid, result.State);
    }

    private static AngleSharp.Dom.IDocument ParseDoc(string html)
    {
        var config = Configuration.Default
            .WithCss()
            .WithDefaultLoader(new LoaderOptions { IsResourceLoadingEnabled = false });
        var ctx = BrowsingContext.New(config);
        return ctx.OpenAsync(req => req.Content(html).Address("about:blank")).Result;
    }
}
