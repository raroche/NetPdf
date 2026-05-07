// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Linq;
using System.Threading.Tasks;
using NetPdf.Css.Cascade;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using Xunit;

namespace NetPdf.UnitTests.Phase2;

/// <summary>
/// Regression tests for the Phase B security-hardening tasks (B-1 through
/// B-7). Phase B builds on Phase A's input-sanitization layer with DOM size
/// caps, CSS rule/declaration caps, calc() complexity caps, iterative HTML
/// strip until stable, SVG-namespace strip, PDF output hardening, and the
/// SafeDefaultResourceLoader contract.
/// </summary>
public sealed class PhaseBSecurityHardeningTests
{
    private sealed class CapturingPublicSink : IDiagnosticsSink
    {
        public System.Collections.Generic.List<Diagnostic> Diagnostics { get; } = new();
        public void Emit(Diagnostic d) => Diagnostics.Add(d);
    }

    // --- B-1: DOM size caps --------------------------------------------------

    [Fact]
    public async Task B1_huge_attribute_value_is_clamped()
    {
        // 2 MiB > 1 MiB cap.
        var huge = new string('x', 2 * 1024 * 1024);
        var html = $"<html><body><div data-x=\"{huge}\">x</div></body></html>";
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink });
        Assert.Contains(sink.Diagnostics,
            d => d.Code == "HTML-DOM-LIMIT-EXCEEDED-001"
                 && d.Message.Contains("attribute value"));
        var attr = doc.QuerySelector("div")!.GetAttribute("data-x")!;
        Assert.True(attr.Length <= 1024 * 1024 + 2,
            $"expected attribute value clamped to ≤ 1 MiB + ellipsis; got {attr.Length}");
    }

    [Fact]
    public async Task B1_excess_attributes_per_element_are_removed()
    {
        var attrs = new System.Text.StringBuilder();
        const int count = 500; // > 256 cap
        for (var i = 0; i < count; i++) attrs.Append($"data-x{i}=\"v{i}\" ");
        var html = $"<html><body><div {attrs}>x</div></body></html>";
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink });
        Assert.Contains(sink.Diagnostics,
            d => d.Code == "HTML-DOM-LIMIT-EXCEEDED-001"
                 && d.Message.Contains("attributes-per-element"));
        var div = doc.QuerySelector("div")!;
        Assert.True(div.Attributes.Length <= 256,
            $"expected ≤ 256 attributes after cap; got {div.Attributes.Length}");
    }

    [Fact]
    public async Task B1_huge_text_node_is_clamped()
    {
        // 6 MiB text node > 4 MiB cap.
        var huge = new string('A', 6 * 1024 * 1024);
        var html = $"<html><body><pre>{huge}</pre></body></html>";
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink });
        Assert.Contains(sink.Diagnostics,
            d => d.Code == "HTML-DOM-LIMIT-EXCEEDED-001"
                 && d.Message.Contains("text node"));
        var pre = doc.QuerySelector("pre")!;
        Assert.True(pre.TextContent.Length <= 4 * 1024 * 1024,
            $"expected text clamped; got {pre.TextContent.Length}");
    }

    [Fact]
    public async Task B1_safe_documents_emit_no_dom_limit_diagnostic()
    {
        // Realistic doc — three nested divs with normal attributes + text.
        var html = """
            <html><body>
              <div class="container">
                <p data-id="1">Hello world.</p>
                <span style="color:red">x</span>
              </div>
            </body></html>
            """;
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        await host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink });
        Assert.DoesNotContain(sink.Diagnostics,
            d => d.Code == "HTML-DOM-LIMIT-EXCEEDED-001");
    }

    [Fact]
    public async Task B1_one_diagnostic_per_violation_kind()
    {
        // 10 separate over-long attribute values across 5 elements should
        // produce ONE attr-length diagnostic, not 10 — to keep adversarial
        // documents from flooding the sink.
        var sb = new System.Text.StringBuilder("<html><body>");
        var huge = new string('y', 2 * 1024 * 1024);
        for (var i = 0; i < 5; i++)
        {
            sb.Append($"<div data-x=\"{huge}\" data-y=\"{huge}\">x</div>");
        }
        sb.Append("</body></html>");
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        await host.ParseAsync(sb.ToString(), new HtmlPdfOptions { Diagnostics = sink });
        var attrLen = sink.Diagnostics.Count(d =>
            d.Code == "HTML-DOM-LIMIT-EXCEEDED-001"
            && d.Message.Contains("attribute value"));
        Assert.Equal(1, attrLen);
    }

    // --- B-2: CSS rule + declaration caps -----------------------------------

    private sealed class CapturingCssSink : ICssDiagnosticsSink
    {
        public System.Collections.Generic.List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    [Fact]
    public async Task B2_excessive_rule_count_capped_per_stylesheet()
    {
        // 60_000 rules > 50_000 cap. Pipeline emits CSS-RULE-LIMIT-EXCEEDED-001
        // and stops processing further rules.
        var rules = new System.Text.StringBuilder();
        const int count = 60_000;
        for (var i = 0; i < count; i++) rules.Append($".x{i} {{ color: red }} ");
        var html = $"<!doctype html><html><head><style>{rules}</style></head><body></body></html>";
        var sink = new CapturingPublicSink();
        using var result = await NetPdf.Phase2.Phase2Pipeline.RunFromHtmlAsync(
            html, new HtmlPdfOptions { Diagnostics = sink });
        Assert.Contains(sink.Diagnostics,
            d => d.Code == "CSS-RULE-LIMIT-EXCEEDED-001"
                 && d.Message.Contains("50000-rule cap"));
    }

    [Fact]
    public async Task B2_excessive_declarations_per_rule_truncated()
    {
        // Build a rule with 500 distinct custom-property declarations — > 256
        // cap. Custom properties are guaranteed to preserve as separate
        // declarations (each name is unique), unlike repeated standard
        // properties which AngleSharp.Css collapses to last-wins.
        var decls = new System.Text.StringBuilder();
        for (var i = 0; i < 500; i++) decls.Append($"--p{i}: red; ");
        var html = $"<!doctype html><html><head><style>.x {{ {decls} }}</style></head><body><div class='x'>x</div></body></html>";
        var sink = new CapturingPublicSink();
        using var result = await NetPdf.Phase2.Phase2Pipeline.RunFromHtmlAsync(
            html, new HtmlPdfOptions { Diagnostics = sink });
        Assert.Contains(sink.Diagnostics,
            d => d.Code == "CSS-RULE-LIMIT-EXCEEDED-001"
                 && d.Message.Contains("declaration cap"));
    }

    [Fact]
    public async Task B2_normal_stylesheet_emits_no_rule_limit_diagnostic()
    {
        var html = """
            <!doctype html><html><head><style>
            body { margin: 0; padding: 0; font-family: sans-serif; }
            .container { max-width: 1024px; margin: 0 auto; }
            .header { background: #eee; padding: 1em; }
            .content { padding: 2em; }
            .footer { background: #333; color: white; padding: 1em; }
            </style></head><body><div class="container"></div></body></html>
            """;
        var sink = new CapturingPublicSink();
        using var result = await NetPdf.Phase2.Phase2Pipeline.RunFromHtmlAsync(
            html, new HtmlPdfOptions { Diagnostics = sink });
        Assert.DoesNotContain(sink.Diagnostics, d => d.Code == "CSS-RULE-LIMIT-EXCEEDED-001");
    }

    // --- B-3: calc() body-length cap ----------------------------------------

    [Fact]
    public async Task B3_huge_calc_body_capped_with_diagnostic()
    {
        // Build a calc() body with thousands of operands, each `+ 1px` — the
        // depth cap (32) doesn't catch this because it's flat, not nested.
        // The body-length cap (4 KiB) triggers and the expression is preserved
        // verbatim.
        var sb = new System.Text.StringBuilder("calc(1px");
        for (var i = 0; i < 5_000; i++) sb.Append(" + 1px");
        sb.Append(')');
        var calcExpr = sb.ToString();
        var html = $"<!doctype html><html><head><style>.x {{ width: {calcExpr} }}</style></head><body><div class='x'>x</div></body></html>";
        var sink = new CapturingPublicSink();
        using var result = await NetPdf.Phase2.Phase2Pipeline.RunFromHtmlAsync(
            html, new HtmlPdfOptions { Diagnostics = sink });
        Assert.Contains(sink.Diagnostics,
            d => d.Code == "CSS-CALC-INVALID-001"
                 && d.Message.Contains("char cap"));
    }

    [Fact]
    public async Task B3_normal_calc_expression_reduces_cleanly()
    {
        var html = """
            <!doctype html><html><head><style>
            .x { width: calc(100% - 32px); padding: calc(1em + 4px); }
            </style></head><body><div class="x">x</div></body></html>
            """;
        var sink = new CapturingPublicSink();
        using var result = await NetPdf.Phase2.Phase2Pipeline.RunFromHtmlAsync(
            html, new HtmlPdfOptions { Diagnostics = sink });
        Assert.DoesNotContain(sink.Diagnostics,
            d => d.Code == "CSS-CALC-INVALID-001" && d.Message.Contains("char cap"));
    }

    // --- B-4: iterative HTML strip until stable -----------------------------

    [Fact]
    public async Task B4_strip_loop_converges_on_first_pass_for_normal_doc()
    {
        // Simple doc with a script — single strip pass should remove it and
        // the convergence check confirms nothing left, no NOT-STABLE diagnostic.
        var html = "<html><body><script>alert(1)</script><div>x</div></body></html>";
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink });
        Assert.Contains(sink.Diagnostics, d => d.Code == "HTML-SCRIPT-IGNORED-001");
        Assert.DoesNotContain(sink.Diagnostics, d => d.Code == "HTML-STRIP-NOT-STABLE-001");
        Assert.Empty(doc.QuerySelectorAll("script"));
    }

    [Fact]
    public async Task B4_strip_loop_handles_svg_foreign_object_with_handlers()
    {
        // SVG <foreignObject> contains HTML that AngleSharp normalizes
        // separately. After the first strip pass the inner content is parsed
        // and its event handlers are reachable; the iterative loop catches
        // them on a subsequent pass.
        var html = """
            <html><body><svg xmlns="http://www.w3.org/2000/svg">
              <foreignObject><div xmlns="http://www.w3.org/1999/xhtml" onclick="alert(1)">x</div></foreignObject>
            </svg></body></html>
            """;
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink });
        // Should NOT report unstable strip — convergence happens within the cap.
        Assert.DoesNotContain(sink.Diagnostics, d => d.Code == "HTML-STRIP-NOT-STABLE-001");
        // No surviving onclick anywhere in the tree.
        foreach (var el in doc.All)
        {
            Assert.False(el.HasAttribute("onclick"),
                $"<{el.LocalName}> retained onclick after iterative strip");
        }
    }

    // --- B-5: SVG-namespace strip pass --------------------------------------

    [Fact]
    public async Task B5_svg_use_href_javascript_url_stripped()
    {
        var html = """
            <html><body><svg xmlns="http://www.w3.org/2000/svg">
              <use href="javascript:alert(1)"/>
            </svg></body></html>
            """;
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink });
        Assert.Contains(sink.Diagnostics,
            d => d.Code == "HTML-JAVASCRIPT-URL-IGNORED-001"
                 && d.Message.Contains("SVG <use>"));
        Assert.False(doc.QuerySelector("use")!.HasAttribute("href"));
    }

    [Fact]
    public async Task B5_svg_animate_targeting_href_with_javascript_dropped()
    {
        var html = """
            <html><body><svg xmlns="http://www.w3.org/2000/svg">
              <a><animate attributeName="href" to="javascript:alert(1)"/>x</a>
            </svg></body></html>
            """;
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink });
        Assert.Contains(sink.Diagnostics,
            d => d.Code == "HTML-JAVASCRIPT-URL-IGNORED-001"
                 && d.Message.Contains("animate"));
        // The animation element itself should be removed.
        Assert.Empty(doc.QuerySelectorAll("animate"));
    }

    [Fact]
    public async Task B5_svg_set_with_dangerous_values_list_dropped()
    {
        // <set values="..."> with a semicolon-separated list — one segment
        // is dangerous, others are benign; the whole element drops.
        var html = """
            <html><body><svg xmlns="http://www.w3.org/2000/svg">
              <a><set attributeName="href" values="https://safe.example;javascript:alert(1)"/>x</a>
            </svg></body></html>
            """;
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink });
        Assert.Contains(sink.Diagnostics,
            d => d.Code == "HTML-JAVASCRIPT-URL-IGNORED-001"
                 && d.Message.Contains("set"));
        Assert.Empty(doc.QuerySelectorAll("set"));
    }

    [Fact]
    public async Task B5_svg_animate_with_safe_url_target_preserved()
    {
        // <animate attributeName="href" to="https://safe..."> is safe — the
        // strip pass leaves it alone.
        var html = """
            <html><body><svg xmlns="http://www.w3.org/2000/svg">
              <a><animate attributeName="href" to="https://example.com/x"/>x</a>
            </svg></body></html>
            """;
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink });
        Assert.NotEmpty(doc.QuerySelectorAll("animate"));
        Assert.DoesNotContain(sink.Diagnostics,
            d => d.Code == "HTML-JAVASCRIPT-URL-IGNORED-001" && d.Message.Contains("animate"));
    }

    [Fact]
    public async Task B5_svg_animate_targeting_non_url_attribute_preserved()
    {
        // attributeName="opacity" — not in the URL-bearing set; the strip
        // does not inspect to/from/values for opacity even if they happen
        // to start with "javascript:" (they wouldn't render anyway).
        var html = """
            <html><body><svg xmlns="http://www.w3.org/2000/svg">
              <rect><animate attributeName="opacity" to="0.5"/></rect>
            </svg></body></html>
            """;
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink });
        Assert.NotEmpty(doc.QuerySelectorAll("animate"));
    }

    // --- B-6: PDF output hardening contract ---------------------------------
    // The Phase 1 PDF writer never emits any of these dangerous PDF dictionary
    // keys (verified by greps over src/NetPdf.Pdf at the time Phase B was
    // landed). These tests pin the contract: if any future code path adds
    // /OpenAction, /AA, /JavaScript, /Launch, /URI with a JS scheme,
    // /EmbeddedFiles, etc., the test below fails immediately. SmokeDocumentFactory
    // exercises every Phase-1 byte-emit path the AOT canary covers.

    [Theory]
    [InlineData("/OpenAction")]   // opens an action when the PDF is opened
    [InlineData("/AA")]            // additional actions on catalog/pages/fields
    [InlineData("/JavaScript")]    // embedded JS action
    [InlineData("/JS")]            // JS action body key
    [InlineData("/Launch")]        // launches an external program
    [InlineData("/EmbeddedFile")]  // embedded file substream prefix (covers EmbeddedFile + EmbeddedFiles)
    [InlineData("/RichMedia")]     // RichMedia annotation (Flash/3D)
    [InlineData("/SubmitForm")]    // posts form data to a URL
    [InlineData("/ImportData")]    // imports form data from a URL
    [InlineData("/GoToR")]         // remote GoTo — fetches a URL
    [InlineData("/GoToE")]         // GoTo embedded — references an embedded file
    [InlineData("javascript:")]    // dangerous URI scheme (used by /URI actions)
    [InlineData("vbscript:")]      // legacy IE scripting URI
    public void B6_smoke_pdf_byte_stream_omits_dangerous_token(string token)
    {
        var bytes = NetPdf.AotSmoke.SmokeDocumentFactory.BuildSmokeDocument();
        // PDF tokens are ASCII; Latin1 round-trips cleanly without throwing on
        // non-ASCII bytes from compressed streams.
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.False(text.Contains(token, System.StringComparison.Ordinal),
            $"PDF output unexpectedly contained dangerous token '{token}'.");
    }

    [Fact]
    public void B6_smoke_pdf_emits_no_uri_action()
    {
        // Both /URI as an action subtype and /URI as a key — the writer
        // must not emit any link-action surfaces in v1.
        var bytes = NetPdf.AotSmoke.SmokeDocumentFactory.BuildSmokeDocument();
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.False(text.Contains("/Type /Action", System.StringComparison.Ordinal),
            "PDF output unexpectedly contained an Action object.");
        Assert.False(text.Contains("/S /URI", System.StringComparison.Ordinal),
            "PDF output unexpectedly contained a /URI action.");
    }

    // --- B-7: SafeDefaultResourceLoader / UriSafetyValidator contract -------

    [Theory]
    [InlineData("http://127.0.0.1/x", "loopback")]
    [InlineData("http://localhost/x", null)] // symbolic — loader handles post-DNS
    [InlineData("http://10.0.0.1/x", "private")]
    [InlineData("http://172.20.5.5/x", "private")]
    [InlineData("http://192.168.1.1/x", "private")]
    [InlineData("http://169.254.169.254/latest/meta-data/", "link-local-or-metadata")]
    [InlineData("http://0.0.0.0/x", "unspecified")]
    [InlineData("http://198.51.100.42/x", "test-net")]
    [InlineData("http://224.0.0.1/x", "multicast")]
    [InlineData("http://255.255.255.255/x", "reserved")]
    [InlineData("https://[::1]/x", "loopback")]
    [InlineData("https://[fc00::1]/x", "unique-local")]
    [InlineData("https://[fe80::1]/x", "link-local")]
    [InlineData("https://[ff00::1]/x", "multicast")]
    // ::ffff:127.0.0.1 reports as "loopback" (IPAddress.IsLoopback recognizes
    // v4-mapped loopback first); the validator's v4-mapped path handles the
    // non-loopback v4-mapped ranges below.
    [InlineData("https://[::ffff:127.0.0.1]/x", "loopback")]
    [InlineData("https://[::ffff:169.254.169.254]/x", "v4-mapped-link-local-or-metadata")]
    public void B7_blocked_ip_ranges_rejected_when_http_allowed(string url, string? expectedReasonFragment)
    {
        var policy = new SecurityPolicy { AllowHttpScheme = true, AllowHttpsScheme = true };
        var verdict = UriSafetyValidator.Validate(new Uri(url), policy);
        if (expectedReasonFragment is null)
        {
            // Symbolic hostnames (localhost) pass the validator; the loader
            // is expected to call IsBlockedIp post-DNS.
            Assert.True(verdict.IsSafe);
        }
        else
        {
            Assert.False(verdict.IsSafe);
            Assert.Contains(expectedReasonFragment, verdict.Reason!, System.StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("https://example.com/foo")]
    [InlineData("http://example.com/foo")]
    [InlineData("https://api.example.com/v1/x.json")]
    public void B7_safe_public_urls_accepted_when_http_allowed(string url)
    {
        var policy = new SecurityPolicy { AllowHttpScheme = true, AllowHttpsScheme = true };
        var verdict = UriSafetyValidator.Validate(new Uri(url), policy);
        Assert.True(verdict.IsSafe, verdict.Reason);
    }

    [Theory]
    [InlineData("http://example.com/x")]
    [InlineData("https://example.com/x")]
    public void B7_safe_default_policy_blocks_http_and_https(string url)
    {
        // Default policy disables http(s); only file (under-baseuri) + data.
        var verdict = UriSafetyValidator.Validate(new Uri(url), SecurityPolicy.SafeDefault);
        Assert.False(verdict.IsSafe);
        Assert.Contains("disabled by SecurityPolicy", verdict.Reason!,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void B7_unknown_scheme_rejected()
    {
        var verdict = UriSafetyValidator.Validate(
            new Uri("gopher://example.com/x"), SecurityPolicy.SafeDefault);
        Assert.False(verdict.IsSafe);
        Assert.Contains("'gopher:'", verdict.Reason!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void B7_data_uri_allowed_by_default()
    {
        var verdict = UriSafetyValidator.Validate(
            new Uri("data:image/png;base64,iVBORw0KG..."), SecurityPolicy.SafeDefault);
        Assert.True(verdict.IsSafe, verdict.Reason);
    }

    [Fact]
    public void B7_data_uri_rejected_when_disabled()
    {
        var policy = new SecurityPolicy { AllowDataUri = false };
        var verdict = UriSafetyValidator.Validate(
            new Uri("data:image/png;base64,iVBORw0KG..."), policy);
        Assert.False(verdict.IsSafe);
        Assert.Contains("data:", verdict.Reason!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void B7_allowed_host_list_blocks_off_list_targets()
    {
        var policy = new SecurityPolicy
        {
            AllowHttpsScheme = true,
            AllowedHosts = new[] { "example.com", "*.cdn.example.com" },
        };
        Assert.True(UriSafetyValidator.Validate(
            new Uri("https://example.com/x"), policy).IsSafe);
        Assert.True(UriSafetyValidator.Validate(
            new Uri("https://foo.cdn.example.com/x"), policy).IsSafe);
        // Not on the list:
        var verdict = UriSafetyValidator.Validate(
            new Uri("https://attacker.com/x"), policy);
        Assert.False(verdict.IsSafe);
        Assert.Contains("not in the allowed-host list", verdict.Reason!,
            System.StringComparison.Ordinal);
        // Wildcard "*.cdn.example.com" should NOT match the bare cdn.example.com
        // (single-label wildcard semantics).
        var bareCdn = UriSafetyValidator.Validate(
            new Uri("https://cdn.example.com/x"), policy);
        Assert.False(bareCdn.IsSafe);
    }

    [Fact]
    public void B7_is_blocked_ip_returns_specific_reason_for_aws_metadata()
    {
        Assert.True(UriSafetyValidator.IsBlockedIp(
            System.Net.IPAddress.Parse("169.254.169.254"), out var reason));
        Assert.Equal("link-local-or-metadata", reason);
    }
}
