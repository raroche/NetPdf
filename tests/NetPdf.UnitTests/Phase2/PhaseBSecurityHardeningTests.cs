// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Linq;
using System.Threading.Tasks;
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
        // Per PR #16 Copilot review #2 — clamped text now has the U+2026
        // ellipsis appended, so allow length = cap + 1.
        Assert.True(pre.TextContent.Length <= 4 * 1024 * 1024 + 1,
            $"expected text clamped; got {pre.TextContent.Length}");
        Assert.EndsWith("…", pre.TextContent);
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
        // 6 separate over-long attribute values across 3 elements should
        // produce ONE attr-length diagnostic, not 6 — to keep adversarial
        // documents from flooding the sink. (Pre-parse 16 MiB cap means
        // we can't pile too many huge attrs into one input string.)
        var sb = new System.Text.StringBuilder("<html><body>");
        var huge = new string('y', 1024 * 1024 + 100); // 1 MiB + 100 — just over cap
        for (var i = 0; i < 3; i++)
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
    // Per PR #16 Copilot review #11 — `CapturingCssSink` was previously
    // declared up here but unused. The follow-up B3 test (unterminated
    // calc()) uses CalcResolver.Resolve directly, so a small CapturingCssSink
    // is re-introduced near that test rather than living unused at the top.
    // CSS-side diagnostics for the rest of the suite route through the
    // public sink (CapturingPublicSink) via PublicDiagnosticsSinkAdapter
    // wired by Phase2Pipeline.

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
        // Per PR #16 Copilot review #12 — search only the plaintext regions
        // of the PDF. The byte stream contains FlateDecode'd compressed
        // payloads (image data, content streams) where short ASCII
        // substrings like "/JS" or "javascript:" can occur by chance,
        // producing false-positive failures. Strip the
        // `stream\n...\nendstream` regions before scanning so the test
        // pins what the WRITER intentionally emits, not what compressed
        // bytes happen to look like.
        var plainText = ExtractPdfPlaintext(NetPdf.AotSmoke.SmokeDocumentFactory.BuildSmokeDocument());
        Assert.False(plainText.Contains(token, System.StringComparison.Ordinal),
            $"PDF output unexpectedly contained dangerous token '{token}' in plaintext regions.");
    }

    [Fact]
    public void B6_smoke_pdf_emits_no_uri_action()
    {
        // Both /URI as an action subtype and /URI as a key — the writer
        // must not emit any link-action surfaces in v1. Plaintext-only
        // search per Copilot review #12.
        var plainText = ExtractPdfPlaintext(NetPdf.AotSmoke.SmokeDocumentFactory.BuildSmokeDocument());
        Assert.False(plainText.Contains("/Type /Action", System.StringComparison.Ordinal),
            "PDF output unexpectedly contained an Action object.");
        Assert.False(plainText.Contains("/S /URI", System.StringComparison.Ordinal),
            "PDF output unexpectedly contained a /URI action.");
    }

    /// <summary>Extract only the PDF's plaintext regions (object dictionaries,
    /// xref table, trailer) — i.e., everything OUTSIDE
    /// <c>stream...endstream</c> blocks. Per ISO 32000-2 §7.3.8, the
    /// <c>stream</c> keyword starts a binary payload that ends at the
    /// <c>endstream</c> keyword on its own line; those bytes can be
    /// FlateDecode-compressed and contain arbitrary substrings. Searching
    /// only the surrounding plaintext lets the B-6 contract test pin
    /// what the WRITER emits without false positives from compressed payloads.</summary>
    private static string ExtractPdfPlaintext(byte[] bytes)
    {
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        var sb = new System.Text.StringBuilder(text.Length);
        var pos = 0;
        while (pos < text.Length)
        {
            var streamIdx = text.IndexOf("\nstream\n", pos, System.StringComparison.Ordinal);
            if (streamIdx < 0) { sb.Append(text, pos, text.Length - pos); break; }
            sb.Append(text, pos, streamIdx - pos);
            // Skip the binary payload — find matching endstream.
            var endIdx = text.IndexOf("\nendstream", streamIdx, System.StringComparison.Ordinal);
            if (endIdx < 0) break; // malformed; ignore the rest
            pos = endIdx + "\nendstream".Length;
        }
        return sb.ToString();
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

    // --- PR #16 follow-up: review fixes -------------------------------------

    [Fact]
    public void B7Followup_file_uri_returns_requires_base_path_check_outcome()
    {
        // Per PR #16 review user-recommendation #5 + Copilot review #8 —
        // the validator can't perform the under-baseuri check (no BaseUri
        // in scope), so it returns the new RequiresBasePathCheck outcome
        // rather than masquerading as Safe.
        var verdict = UriSafetyValidator.Validate(
            new System.Uri("file:///home/user/img.png"), SecurityPolicy.SafeDefault);
        Assert.Equal(UriSafetyValidator.SafetyOutcome.RequiresBasePathCheck, verdict.Outcome);
        Assert.False(verdict.IsSafe);
        Assert.Contains("BaseUri", verdict.Reason!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void B7Followup_file_uri_safe_when_allow_file_scheme_set()
    {
        // When AllowFileScheme is explicitly true, the loader is permitted
        // to read any file:// path. No follow-up check needed.
        var policy = new SecurityPolicy { AllowFileScheme = true, AllowFileSchemeUnderBaseUri = false };
        var verdict = UriSafetyValidator.Validate(
            new System.Uri("file:///etc/passwd"), policy);
        Assert.Equal(UriSafetyValidator.SafetyOutcome.Safe, verdict.Outcome);
    }

    [Fact]
    public void B7Followup_data_uri_skips_ip_blocklist_and_allowed_hosts()
    {
        // Per Copilot review #8 — IP/host policy applies only to http(s).
        // A data: URI's "host" portion is empty/special; AllowedHosts must
        // not reject it.
        var policy = new SecurityPolicy
        {
            AllowDataUri = true,
            AllowedHosts = new[] { "example.com" }, // would block any other host
        };
        var verdict = UriSafetyValidator.Validate(
            new System.Uri("data:text/plain,hello"), policy);
        Assert.True(verdict.IsSafe, verdict.Reason);
    }

    // PR #16 follow-up — selector alternative cap (user #3 / Copilot #6).
    [Fact]
    public async Task B2Followup_excessive_selector_alternatives_truncated()
    {
        // Build a single rule with > 1024 comma-separated alternatives.
        // Compilation succeeds; the cascade truncates the SelectorList +
        // emits CSS-RULE-LIMIT-EXCEEDED-001.
        var alternatives = new System.Text.StringBuilder();
        const int count = 1500; // > 1024
        for (var i = 0; i < count; i++)
        {
            if (i > 0) alternatives.Append(", ");
            alternatives.Append($".x{i}");
        }
        var html = $"<!doctype html><html><head><style>{alternatives} {{ color: red }}</style></head><body><div class='x0'>x</div></body></html>";
        var sink = new CapturingPublicSink();
        using var result = await NetPdf.Phase2.Phase2Pipeline.RunFromHtmlAsync(
            html, new HtmlPdfOptions { Diagnostics = sink });
        Assert.Contains(sink.Diagnostics,
            d => d.Code == "CSS-RULE-LIMIT-EXCEEDED-001"
                 && d.Message.Contains("selector-alternative cap"));
    }

    // PR #16 follow-up — per-stylesheet rule counter (user #4).
    [Fact]
    public async Task B2Followup_rule_cap_emitted_only_once_per_overflowing_sheet()
    {
        // Pre-fix: the rule-count check used the global output.Count, so
        // sheet 1's overflow at 50k poisoned every subsequent sheet —
        // sheet 2 (one rule) immediately hit "output.Count >= 50_000" on
        // its first rule + emitted ITS OWN cap diagnostic. So pre-fix the
        // diagnostic count was 2.
        // Post-fix: sheetStartCount is captured per-sheet; sheet 2's
        // counter starts at 0 + its single rule never overflows. So
        // post-fix the diagnostic count is 1 (sheet 1 only).
        var sheet1 = new System.Text.StringBuilder();
        for (var i = 0; i < 51_000; i++) sheet1.Append($".x{i} {{ color: red }} ");
        var html = "<!doctype html><html><head><style>" + sheet1
            + "</style><style>.kept { color: blue }</style></head>"
            + "<body><div class=\"kept\">x</div></body></html>";
        var sink = new CapturingPublicSink();
        using var result = await NetPdf.Phase2.Phase2Pipeline.RunFromHtmlAsync(
            html, new HtmlPdfOptions { Diagnostics = sink });
        var ruleCapDiagnostics = sink.Diagnostics.Count(d =>
            d.Code == "CSS-RULE-LIMIT-EXCEEDED-001"
            && d.Message.Contains("50000-rule cap"));
        Assert.Equal(1, ruleCapDiagnostics);
    }

    // PR #16 follow-up — pre-parse byte cap (user #1).
    [Fact]
    public async Task B1Followup_oversized_input_rejected_before_parse()
    {
        // 17 MiB string > 16 MiB cap. Parse rejected before AngleSharp
        // materializes the DOM.
        var huge = new string('a', 17 * 1024 * 1024);
        var html = $"<html><body><div>{huge}</div></body></html>";
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        await Assert.ThrowsAsync<System.IO.InvalidDataException>(
            () => host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink }));
        Assert.Contains(sink.Diagnostics,
            d => d.Code == "HTML-INPUT-TOO-LARGE-001");
    }

    // PR #16 follow-up — wide-children DOM walk (Copilot #1).
    [Fact]
    public async Task B1Followup_wide_body_with_excess_children_truncated()
    {
        // Build a body with a moderate number of direct children, well
        // under the input-byte cap but over the element cap (250k). The
        // bounded-snapshot fix keeps this from materializing the full
        // children array before EnforceDomSizeCaps fires.
        // Use 300_000 minimal <p/> children — at 4 chars each that's
        // ~1.2 MiB input, well under the 16 MiB cap.
        var sb = new System.Text.StringBuilder("<html><body>");
        for (var i = 0; i < 300_000; i++) sb.Append("<p/>");
        sb.Append("</body></html>");
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync(sb.ToString(), new HtmlPdfOptions { Diagnostics = sink });
        Assert.Contains(sink.Diagnostics,
            d => d.Code == "HTML-DOM-LIMIT-EXCEEDED-001"
                 && d.Message.Contains("element count"));
        // Total elements left should be near or below the cap.
        var elementsAfter = 0;
        foreach (var _ in doc.All) elementsAfter++;
        Assert.True(elementsAfter <= NetPdf.HtmlParsingHost.MaxElementCount + 4,
            $"expected ≤ {NetPdf.HtmlParsingHost.MaxElementCount} + 4 elements; got {elementsAfter}");
    }

    // PR #16 follow-up — SVG animation xlink:href (user #2 / Copilot #5).
    [Fact]
    public async Task B5Followup_svg_animate_targeting_xlink_href_dropped()
    {
        var html = """
            <html><body><svg xmlns="http://www.w3.org/2000/svg">
              <a><animate attributeName="xlink:href" to="javascript:alert(1)"/>x</a>
            </svg></body></html>
            """;
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink });
        Assert.Contains(sink.Diagnostics,
            d => d.Code == "HTML-JAVASCRIPT-URL-IGNORED-001"
                 && d.Message.Contains("animate"));
        Assert.Empty(doc.QuerySelectorAll("animate"));
    }

    // PR #16 follow-up — namespace-aware attribute mutation (Copilot #3 / #4).
    [Fact]
    public async Task B1Followup_xlink_href_with_huge_value_clamped_correctly()
    {
        // An SVG element with a huge xlink:href value should be clamped to
        // 1 MiB + ellipsis on the SAME attribute (xlink:href), not an
        // accidental new attribute in the no-namespace bucket.
        var hugeUrl = "https://example.com/" + new string('z', 1024 * 1024 + 100);
        var html = $"""
            <html><body><svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
              <a xlink:href="{hugeUrl}">x</a>
            </svg></body></html>
            """;
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink });
        var anchor = doc.QuerySelector("a")!;
        // The xlink:href should still be present (not duplicated).
        var clamped = anchor.GetAttribute("http://www.w3.org/1999/xlink", "href");
        Assert.NotNull(clamped);
        Assert.True(clamped!.Length <= 1024 * 1024 + 1,
            $"expected clamped length ≤ 1 MiB + 1; got {clamped.Length}");
        Assert.EndsWith("…", clamped);
    }

    // PR #16 follow-up — calc() unterminated cap (user #6 / Copilot #7).
    // The cascade pipeline can't reliably feed an unterminated calc() into
    // the resolver because the CSS parser rejects malformed declarations
    // earlier; test CalcResolver.Resolve directly instead.
    [Fact]
    public void B3Followup_unterminated_calc_with_huge_tail_emits_diagnostic()
    {
        // 5 KiB of digits inside an UNTERMINATED calc() — pre-fix the
        // resolver passed unterminated bodies through verbatim with no
        // length check; post-fix it emits CSS-CALC-INVALID-001 since the
        // remaining length exceeds the 4 KiB cap.
        var huge = new string('1', 5 * 1024);
        var rawValue = $"calc({huge}"; // no closing paren
        var sink = new CapturingCssSink();
        var result = NetPdf.Css.Cascade.CalcResolver.Resolve(rawValue, sink, NetPdf.Css.Parser.CssSourceLocation.Unknown);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == "CSS-CALC-INVALID-001" && d.Message.Contains("char cap"));
        // Verbatim-pass through the original rawValue.
        Assert.Equal(rawValue, result);
    }

    /// <summary>Per PR #16 follow-up — re-introduced for the
    /// B3Followup direct-resolver test only. Phase B's Copilot review #11
    /// removed the previous CapturingCssSink because no Phase B test used
    /// it; the unterminated-calc test below needs CSS-side diagnostic
    /// inspection without the public sink adapter.</summary>
    private sealed class CapturingCssSink : NetPdf.Css.Diagnostics.ICssDiagnosticsSink
    {
        public System.Collections.Generic.List<NetPdf.Css.Diagnostics.CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(NetPdf.Css.Diagnostics.CssDiagnostic d) => Diagnostics.Add(d);
    }
}
