// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Linq;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Io;
using NetPdf.Css.Cascade;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using Xunit;

namespace NetPdf.UnitTests.Phase2;

/// <summary>
/// Regression tests for the 8 Phase A security-hardening tasks
/// (A-1 .. A-8). Each fact pins a specific cycle-1 hole closed by Phase A.
/// </summary>
public sealed class PhaseASecurityHardeningTests
{
    private sealed class CapturingPublicSink : IDiagnosticsSink
    {
        public System.Collections.Generic.List<Diagnostic> Diagnostics { get; } = new();
        public void Emit(Diagnostic d) => Diagnostics.Add(d);
    }

    private sealed class CapturingCssSink : ICssDiagnosticsSink
    {
        public System.Collections.Generic.List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    // --- A-1: extended URL strip ---------------------------------------------

    [Theory]
    [InlineData("<form action=\"javascript:alert('x')\"><input></form>", "form", "action")]
    [InlineData("<iframe src=\"javascript:alert('x')\"></iframe>", "iframe", "src")]
    [InlineData("<object data=\"javascript:alert('x')\"></object>", "object", "data")]
    [InlineData("<embed src=\"javascript:alert('x')\">", "embed", "src")]
    [InlineData("<base href=\"javascript:alert('x')\">", "base", "href")]
    public async Task A1_javascript_url_stripped_from_extended_attribute_set(string html, string tag, string attr)
    {
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync($"<html><body>{html}</body></html>",
            new HtmlPdfOptions { Diagnostics = sink });
        Assert.Contains(sink.Diagnostics, d => d.Code == "HTML-JAVASCRIPT-URL-IGNORED-001");
        Assert.False(doc.QuerySelector(tag)!.HasAttribute(attr),
            $"expected {tag}.{attr} stripped, but it remains");
    }

    [Fact]
    public async Task A1_data_url_stripped_from_iframe_src()
    {
        // data: URIs carry arbitrary bytes; data:text/html is a known sneak
        // path for SSRF/XSS in HTML-to-PDF research.
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync(
            "<html><body><iframe src=\"data:text/html,<script>alert(1)</script>\"></iframe></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });
        Assert.Contains(sink.Diagnostics, d => d.Code == "HTML-JAVASCRIPT-URL-IGNORED-001");
        Assert.False(doc.QuerySelector("iframe")!.HasAttribute("src"));
    }

    [Fact]
    public async Task A1_meta_refresh_javascript_url_stripped()
    {
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync(
            "<html><head><meta http-equiv=\"refresh\" content=\"0;url=javascript:alert(1)\"></head><body></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });
        Assert.Contains(sink.Diagnostics, d => d.Code == "HTML-JAVASCRIPT-URL-IGNORED-001");
        Assert.False(doc.QuerySelector("meta")!.HasAttribute("content"));
    }

    [Fact]
    public async Task A1_safe_https_urls_pass_through_untouched()
    {
        // The strip targets javascript:/vbscript:/data: only; https: + http:
        // remain intact (they're inert today since resource loading is off,
        // and Phase 5's IResourceLoader is the right control point).
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync(
            "<html><body><iframe src=\"https://example.com/safe\"></iframe></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });
        Assert.DoesNotContain(sink.Diagnostics, d => d.Code == "HTML-JAVASCRIPT-URL-IGNORED-001");
        Assert.Equal("https://example.com/safe", doc.QuerySelector("iframe")!.GetAttribute("src"));
    }

    // --- A-2: event handler strip --------------------------------------------

    [Theory]
    [InlineData("onclick")]
    [InlineData("onload")]
    [InlineData("onerror")]
    [InlineData("onmouseover")]
    [InlineData("ONCLICK")]
    [InlineData("OnClick")]
    public async Task A2_event_handler_attributes_stripped(string handler)
    {
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync(
            $"<html><body><div {handler}=\"x\">text</div></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });
        Assert.Contains(sink.Diagnostics, d => d.Code == "HTML-EVENT-HANDLER-IGNORED-001");
        Assert.False(doc.QuerySelector("div")!.HasAttribute(handler.ToLowerInvariant()));
    }

    [Fact]
    public async Task A2_non_event_attributes_pass_through()
    {
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync(
            "<html><body><div class=\"keep\" id=\"also-keep\">text</div></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });
        Assert.DoesNotContain(sink.Diagnostics, d => d.Code == "HTML-EVENT-HANDLER-IGNORED-001");
        var div = doc.QuerySelector("div")!;
        Assert.True(div.HasAttribute("class"));
        Assert.True(div.HasAttribute("id"));
    }

    // --- A-3: cumulative var() expansion budget ------------------------------

    [Fact]
    public void A3_var_substitution_budget_caps_cumulative_output()
    {
        // Inputs MUST contain `var(...)` to exercise the slow-path that
        // consumes the budget. Pure literals would short-circuit through
        // ContainsVarFunction without touching the budget.
        var customProperties = new CustomPropertyTable(parent: null);
        customProperties.Set("--big", new string('a', 60));
        var sink = new CapturingCssSink();
        var budget = new VarSubstitution.Budget(bytesPerElement: 100);

        // Each `var(--big)` expands to 60 chars. Two calls = 120 > 100.
        var first = VarSubstitution.Substitute(
            "var(--big)", customProperties, sink, budget: budget);
        var second = VarSubstitution.Substitute(
            "var(--big)", customProperties, sink, budget: budget);

        Assert.False(first.IsInvalid);
        Assert.True(second.IsInvalid, "second substitution should hit the cumulative budget");
        Assert.Contains(sink.Diagnostics, d => d.Code == "CSS-VAR-EXPANSION-LIMIT-001");
    }

    // --- A-4: nested at-rule depth limit -------------------------------------

    [Fact]
    public async Task A4_excessively_nested_media_rules_emit_warning_and_stop()
    {
        var nested = new System.Text.StringBuilder();
        const int depth = 100;
        for (var i = 0; i < depth; i++) nested.Append("@media print { ");
        nested.Append(".x { color: red }");
        for (var i = 0; i < depth; i++) nested.Append(" }");

        var sink = new CapturingPublicSink();
        var options = new HtmlPdfOptions { Diagnostics = sink };
        var html = $"<!doctype html><html><head><style>{nested}</style></head><body></body></html>";

        // Should NOT throw / SOE.
        using var result = await NetPdf.Phase2.Phase2Pipeline.RunFromHtmlAsync(html, options);

        Assert.Contains(sink.Diagnostics,
            d => d.Code == "CSS-AT-RULE-UNKNOWN-001"
                 && d.Message.Contains("nesting"));
    }

    // --- A-5: CssContentList output cap --------------------------------------

    [Fact]
    public async Task A5_pseudo_content_with_huge_attr_blocked_by_output_cap()
    {
        var huge = new string('A', 70 * 1024); // 70 KiB > 64 KiB
        var html = $"<!doctype html><html><head><style>" +
                   $".big::before {{ content: attr(data-x) }}" +
                   $"</style></head><body><span class=\"big\" data-x=\"{huge}\">x</span></body></html>";
        var sink = new CapturingPublicSink();
        using var result = await NetPdf.Phase2.Phase2Pipeline.RunFromHtmlAsync(
            html, new HtmlPdfOptions { Diagnostics = sink });

        Assert.Contains(sink.Diagnostics,
            d => d.Code == "CSS-CONTENT-FUNCTION-UNSUPPORTED-001"
                 && d.Message.Contains("KiB output cap"));
    }

    // --- A-6: diagnostic text sanitizer --------------------------------------

    [Fact]
    public void A6_diagnostic_text_sanitizer_strips_control_chars()
    {
        // Build the test input by char-codes to avoid C#'s greedy `\x` escape
        // ambiguity. ANSI ESC + bracketed color, BEL, NUL, DEL, C1 (U+0080)
        // all need to be redacted; the visible "red" + "end" must survive.
        var input = new string(new[]
        {
            (char)0x1B, '[', '3', '1', 'm',     // ANSI red prefix (ESC + bracket)
            'r', 'e', 'd',
            (char)0x07,                          // BEL
            (char)0x00,                          // NUL
            (char)0x7F,                          // DEL
            'e', 'n', 'd',
            (char)0x80,                          // C1
        });
        var output = DiagnosticTextSanitizer.Sanitize(input);
        foreach (var ch in output)
        {
            Assert.False(ch < 0x20 || ch == 0x7F || (ch >= 0x80 && ch <= 0x9F),
                $"U+{(int)ch:X4} leaked into sanitized output");
        }
        Assert.Contains("red", output);
        Assert.Contains("end", output);
    }

    [Fact]
    public void A6_diagnostic_text_sanitizer_caps_length_with_ellipsis()
    {
        var input = new string('x', 1000);
        var output = DiagnosticTextSanitizer.Sanitize(input, maxLength: 50);
        Assert.True(output.Length <= 51, "should be 50 chars + ellipsis");
        Assert.EndsWith("…", output);
    }

    // --- A-7: SourceLocation.File path normalization -------------------------

    [Fact]
    public void A7_absolute_filesystem_path_reduced_to_basename()
    {
        Assert.Equal("secret.html",
            DiagnosticTextSanitizer.SanitizeFilePath("file:///C:/Users/Foo/secret.html"));
        Assert.Equal("passwd",
            DiagnosticTextSanitizer.SanitizeFilePath("/etc/passwd"));
        Assert.Equal("config.html",
            DiagnosticTextSanitizer.SanitizeFilePath("file:///home/user/config.html"));
    }

    [Fact]
    public void A7_well_known_sentinels_preserved()
    {
        Assert.Equal("<inline>", DiagnosticTextSanitizer.SanitizeFilePath("<inline>"));
        Assert.Equal("<unknown>", DiagnosticTextSanitizer.SanitizeFilePath("<unknown>"));
        Assert.Equal("about:blank", DiagnosticTextSanitizer.SanitizeFilePath("about:blank"));
    }

    [Fact]
    public void A7_https_urls_pass_through_with_only_control_char_strip()
    {
        Assert.Equal("https://example.com/foo",
            DiagnosticTextSanitizer.SanitizeFilePath("https://example.com/foo"));
    }

    // --- A-8: per-stylesheet selector-parse-warning budget ------------------

    [Fact]
    public async Task A8_excessive_broken_selectors_capped_per_stylesheet()
    {
        // 200 broken selectors. Budget caps emissions at 100 + appends one
        // summary diagnostic.
        var rules = new System.Text.StringBuilder();
        for (var i = 0; i < 200; i++)
        {
            rules.Append($".x{i}:fake-pseudo-class-{i} {{ color: red }} ");
        }
        var html = $"<!doctype html><html><head><style>{rules}</style></head><body></body></html>";

        var sink = new CapturingPublicSink();
        using var result = await NetPdf.Phase2.Phase2Pipeline.RunFromHtmlAsync(
            html, new HtmlPdfOptions { Diagnostics = sink });

        var parseWarnings = sink.Diagnostics.Where(d => d.Code == "CSS-PARSE-WARNING-001").ToList();
        // 100 individual + 1 summary = at most 101.
        Assert.True(parseWarnings.Count <= 101,
            $"expected ≤101 selector-parse warnings; got {parseWarnings.Count}");
        Assert.Contains(parseWarnings,
            d => d.Message.Contains("budget") && d.Message.Contains("suppressed"));
    }
}
