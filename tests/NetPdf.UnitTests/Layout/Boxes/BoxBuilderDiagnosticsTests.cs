// Copyright 2026 Roland Aroche and NetPdf contributors.
// Linq is intentionally avoided in production code per CLAUDE.md, but tests
// freely use it.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using NetPdf.Css.Cascade;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Layout.Boxes;
using Xunit;

namespace NetPdf.UnitTests.Layout.Boxes;

/// <summary>
/// Task 16 cycle 1 — verify that BoxBuilder + the parsers it calls emit the
/// new diagnostic codes when the cascade carries unsupported features.
/// </summary>
public sealed class BoxBuilderDiagnosticsTests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
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

    private static async Task<(Box root, CapturingSink sink)> BuildAsync(string html, string? css = null)
    {
        var doc = await ParseHtml(html);
        var sheets = css is null
            ? ImmutableArray<CssStylesheet>.Empty
            : ImmutableArray.Create(await ParseSheet(css));
        var sink = new CapturingSink();
        var cascade = CascadeResolver.Resolve(doc, sheets, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);
        var root = BoxBuilder.Build(doc, resolved, sink);
        return (root, sink);
    }

    // ============================================================
    // CSS-CONTENT-FUNCTION-UNSUPPORTED-001
    // ============================================================

    [Theory]
    [InlineData("counter(items)")]
    [InlineData("counters(items, '.')")]
    [InlineData("url(image.png)")]
    [InlineData("open-quote")]
    [InlineData("close-quote")]
    [InlineData("no-open-quote")]
    public async Task Unsupported_content_token_emits_CONTENT_FUNCTION_UNSUPPORTED_001(string contentValue)
    {
        var (_, sink) = await BuildAsync(
            "<p class='x'>body</p>",
            $".x::before {{ content: {contentValue} }}");
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssContentFunctionUnsupported001
                 && d.Severity == CssDiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task Supported_content_string_does_not_emit_diagnostic()
    {
        var (_, sink) = await BuildAsync(
            "<p class='x'>body</p>",
            ".x::before { content: 'just a string' }");
        Assert.DoesNotContain(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssContentFunctionUnsupported001);
    }

    // ============================================================
    // CSS-ATTR-MULTI-ARG-UNSUPPORTED-001
    //
    // NOTE: AngleSharp.Css normalizes attr() function calls before they
    // reach the cascade — the cascade-adapter path doesn't preserve
    // multi-arg attr() syntax verbatim. We test CssContentList.TryParse
    // directly so the emission contract is pinned regardless of upstream
    // parsing quirks.
    // ============================================================

    private static async Task<IElement> MakeHost(string html, string id)
    {
        var ctx = BrowsingContext.New(Configuration.Default);
        var doc = await ctx.OpenAsync(req => req.Content(html));
        return doc.QuerySelector("#" + id)!;
    }

    [Fact]
    public async Task Multi_arg_attr_with_type_emits_ATTR_MULTI_ARG_UNSUPPORTED_001()
    {
        var host = await MakeHost("<p id='h' data-x='real'>body</p>", "h");
        var sink = new CapturingSink();
        var ok = CssContentList.TryParse("attr(data-x string)", host, sink,
            CssSourceLocation.Unknown, out _);
        Assert.False(ok);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssAttrMultiArgUnsupported001
                 && d.Severity == CssDiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task Multi_arg_attr_with_fallback_emits_ATTR_MULTI_ARG_UNSUPPORTED_001()
    {
        var host = await MakeHost("<p id='h'>body</p>", "h");
        var sink = new CapturingSink();
        var ok = CssContentList.TryParse("attr(missing, 'fallback')", host, sink,
            CssSourceLocation.Unknown, out _);
        Assert.False(ok);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssAttrMultiArgUnsupported001);
    }

    [Fact]
    public async Task Bare_attr_name_does_not_emit_ATTR_MULTI_ARG_UNSUPPORTED_001()
    {
        var host = await MakeHost("<p id='h' data-x='hi'>body</p>", "h");
        var sink = new CapturingSink();
        var ok = CssContentList.TryParse("attr(data-x)", host, sink,
            CssSourceLocation.Unknown, out var result);
        Assert.True(ok);
        Assert.Equal("hi", result);
        Assert.DoesNotContain(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssAttrMultiArgUnsupported001);
    }

    // ============================================================
    // CSS-MODERN-COLOR-FUNCTION-UNSUPPORTED-001
    //
    // NOTE: AngleSharp.Css 1.0.0-beta.144 munges oklch()/lab()/lch()/oklab()
    // → bogus rgba(), and drops color-mix() entirely (per CssPreprocessor
    // remarks). Going through the cascade-adapter path therefore wouldn't
    // exercise the modern-color branch. The preprocessor's recovery
    // pipeline carries the original raw value forward in production, but
    // for cycle-1 hardening we test ColorResolver.Resolve directly so the
    // emission contract is pinned regardless of the upstream parser quirk.
    // ============================================================

    [Theory]
    [InlineData("oklch(0.7 0.15 200)", "oklch")]
    [InlineData("oklab(0.7 0.1 -0.05)", "oklab")]
    [InlineData("lab(70% -45 0)", "lab")]
    [InlineData("lch(70% 50 200)", "lch")]
    [InlineData("color-mix(in srgb, red, blue)", "color-mix")]
    [InlineData("color(display-p3 0.5 0.3 0.7)", "color")]
    public void Modern_color_function_emits_MODERN_COLOR_FUNCTION_UNSUPPORTED_001(
        string value, string expectedFn)
    {
        var sink = new CapturingSink();
        var result = NetPdf.Css.ComputedValues.PropertyResolvers.ColorResolver.Resolve(
            value,
            NetPdf.Css.Properties.PropertyId.Color,
            "color",
            sink,
            CssSourceLocation.Unknown);
        Assert.True(result.IsInvalid);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssModernColorFunctionUnsupported001
                 && d.Severity == CssDiagnosticSeverity.Info
                 && d.Message.Contains(expectedFn + "()"));
    }

    [Fact]
    public void Standard_rgb_does_not_emit_MODERN_COLOR_FUNCTION_UNSUPPORTED_001()
    {
        var sink = new CapturingSink();
        NetPdf.Css.ComputedValues.PropertyResolvers.ColorResolver.Resolve(
            "rgb(255, 0, 0)",
            NetPdf.Css.Properties.PropertyId.Color,
            "color",
            sink,
            CssSourceLocation.Unknown);
        Assert.DoesNotContain(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssModernColorFunctionUnsupported001);
    }

    [Fact]
    public void Truly_invalid_color_emits_PROPERTY_VALUE_INVALID_not_modern_color_diagnostic()
    {
        // A bogus value (not a recognized function) emits the generic
        // CSS-PROPERTY-VALUE-INVALID-001 (Warning), NOT the modern-color
        // (Info) — they're semantically different.
        var sink = new CapturingSink();
        NetPdf.Css.ComputedValues.PropertyResolvers.ColorResolver.Resolve(
            "nonsense-color",
            NetPdf.Css.Properties.PropertyId.Color,
            "color",
            sink,
            CssSourceLocation.Unknown);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001);
        Assert.DoesNotContain(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssModernColorFunctionUnsupported001);
    }

    // ============================================================
    // CSS-PSEUDO-SUPPRESSED-ON-REPLACED-001
    // ============================================================

    [Theory]
    [InlineData("img")]
    [InlineData("video")]
    [InlineData("iframe")]
    [InlineData("canvas")]
    [InlineData("object")]
    [InlineData("embed")]
    public async Task Pseudo_on_any_replaced_element_emits_PSEUDO_SUPPRESSED_ON_REPLACED_001(string tag)
    {
        var (_, sink) = await BuildAsync(
            $"<{tag}>",
            $"{tag}::before {{ content: 'X' }}");
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssPseudoSuppressedOnReplaced001
                 && d.Severity == CssDiagnosticSeverity.Info);
    }

    [Fact]
    public async Task Pseudo_on_non_replaced_does_not_emit_PSEUDO_SUPPRESSED_ON_REPLACED_001()
    {
        var (_, sink) = await BuildAsync(
            "<p class='x'>body</p>",
            ".x::before { content: 'X' }");
        Assert.DoesNotContain(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssPseudoSuppressedOnReplaced001);
    }

    [Fact]
    public async Task Replaced_element_without_pseudo_rule_does_not_emit_diagnostic()
    {
        // No author rule → no noise. The diagnostic only fires when an
        // author specifically attached generated content to a replaced
        // element.
        var (_, sink) = await BuildAsync("<img>");
        Assert.DoesNotContain(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssPseudoSuppressedOnReplaced001);
    }

    [Fact]
    public async Task Pseudo_suppressed_diagnostic_names_the_pseudo_and_element()
    {
        var (_, sink) = await BuildAsync(
            "<img>",
            "img::before { content: 'X' } img::after { content: 'Y' }");
        // Both ::before and ::after rules → two diagnostics.
        var emitted = sink.Diagnostics
            .Where(d => d.Code == CssDiagnosticCodes.CssPseudoSuppressedOnReplaced001)
            .ToList();
        Assert.Equal(2, emitted.Count);
        Assert.Contains(emitted, d => d.Message.Contains("::before"));
        Assert.Contains(emitted, d => d.Message.Contains("::after"));
        Assert.All(emitted, d => Assert.Contains("img", d.Message));
    }
}
