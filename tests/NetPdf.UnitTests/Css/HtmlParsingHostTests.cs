// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Xunit;
using Xunit.Abstractions;

namespace NetPdf.UnitTests.Css;

/// <summary>
/// Per-class unit tests for the Phase 2 Task 1 HTML parsing host. Covers the three
/// contracts the Phase 2 doc requires: AngleSharp wired without scripting, every
/// <c>&lt;script&gt;</c> emits <c>HTML-SCRIPT-IGNORED-001</c> and is stripped from the
/// returned tree, and <see cref="HtmlPdfOptions.BaseUri"/> propagates to the document.
/// </summary>
public sealed class HtmlParsingHostTests
{
    private readonly ITestOutputHelper _output;

    public HtmlParsingHostTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ParseAsync_simple_html_returns_document_with_root()
    {
        var host = new HtmlParsingHost();

        var document = await host.ParseAsync(
            "<html><body><p>hi</p></body></html>",
            new HtmlPdfOptions());

        Assert.NotNull(document.DocumentElement);
        Assert.Equal("HTML", document.DocumentElement!.TagName);
        Assert.Single(document.QuerySelectorAll("p"));
        Assert.Equal("hi", document.QuerySelector("p")!.TextContent);
    }

    [Fact]
    public async Task ParseAsync_document_without_scripts_emits_no_diagnostics()
    {
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        await host.ParseAsync(
            "<html><body><h1>Title</h1><p>body</p></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });

        Assert.Empty(sink.Diagnostics);
    }

    [Fact]
    public async Task ParseAsync_single_script_emits_html_script_ignored_001()
    {
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        await host.ParseAsync(
            "<html><body><script>alert(1)</script></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });

        var diagnostic = Assert.Single(sink.Diagnostics);
        Assert.Equal("HTML-SCRIPT-IGNORED-001", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("script", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JavaScript", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_single_script_removes_script_from_tree()
    {
        var host = new HtmlParsingHost();

        var document = await host.ParseAsync(
            "<html><body><script>alert(1)</script><p>kept</p></body></html>",
            new HtmlPdfOptions());

        Assert.Empty(document.QuerySelectorAll("script"));
        Assert.Single(document.QuerySelectorAll("p"));
    }

    [Fact]
    public async Task ParseAsync_multiple_scripts_emits_one_diagnostic_per_script()
    {
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        await host.ParseAsync(
            "<html><body><script>1</script><script>2</script><script>3</script></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });

        Assert.Equal(3, sink.Diagnostics.Count);
        Assert.All(sink.Diagnostics, d => Assert.Equal("HTML-SCRIPT-IGNORED-001", d.Code));
    }

    [Fact]
    public async Task ParseAsync_external_script_emits_diagnostic()
    {
        // <script src="..."> is just as forbidden as inline <script> — we never
        // execute or fetch anything. The element is diagnosed and stripped.
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        var document = await host.ParseAsync(
            "<html><body><script src=\"app.js\"></script></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });

        Assert.Single(sink.Diagnostics);
        Assert.Empty(document.QuerySelectorAll("script"));
    }

    [Fact]
    public async Task ParseAsync_script_in_head_emits_diagnostic()
    {
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        await host.ParseAsync(
            "<html><head><script>boot()</script></head><body></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });

        Assert.Single(sink.Diagnostics);
    }

    [Fact]
    public async Task ParseAsync_source_location_is_populated_for_script_diagnostic()
    {
        // Hard contract: AngleSharp 1.1.2 with IsKeepingSourceReferences = true on the
        // registered IHtmlParser populates IElement.SourceReference for parser-emitted
        // elements. The host's "with source location" obligation in HTML-SCRIPT-IGNORED-001
        // depends on this. If a future AngleSharp version regresses and returns Unknown
        // here, this test fails loudly — the previous "graceful degradation" behavior
        // was hiding a real regression risk.
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        await host.ParseAsync(
            "<html><body>\n  <script>alert(1)</script>\n</body></html>",
            new HtmlPdfOptions { Diagnostics = sink });

        var diagnostic = Assert.Single(sink.Diagnostics);
        Assert.NotEqual(SourceLocation.Unknown, diagnostic.Location);
        Assert.True(diagnostic.Location.Line > 0,
            $"expected non-zero line, got {diagnostic.Location.Line}");
        Assert.True(diagnostic.Location.Column > 0,
            $"expected non-zero column, got {diagnostic.Location.Column}");
    }

    [Fact]
    public async Task ParseAsync_source_location_line_matches_authored_position()
    {
        // The <script> sits at line 3 of the input string (1-indexed). AngleSharp emits
        // the position of the start tag's `<`. This test pins the reported line so a
        // future off-by-one or normalization change is caught.
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        // Line 1: <html><body>
        // Line 2:   <p>filler</p>
        // Line 3:   <script>alert(1)</script>
        await host.ParseAsync(
            "<html><body>\n  <p>filler</p>\n  <script>alert(1)</script>\n</body></html>",
            new HtmlPdfOptions { Diagnostics = sink });

        var diagnostic = Assert.Single(sink.Diagnostics);
        Assert.Equal(3, diagnostic.Location.Line);
    }

    [Fact]
    public async Task ParseAsync_source_location_file_reflects_base_uri_when_supplied()
    {
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();
        var baseUri = new Uri("https://invoices.example.com/2026/05/x.html");

        await host.ParseAsync(
            "<html><body><script>alert(1)</script></body></html>",
            new HtmlPdfOptions { BaseUri = baseUri, Diagnostics = sink });

        var diagnostic = Assert.Single(sink.Diagnostics);
        Assert.Equal(baseUri.AbsoluteUri, diagnostic.Location.File);
    }

    [Fact]
    public async Task ParseAsync_source_location_file_is_null_when_base_uri_unset()
    {
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        await host.ParseAsync(
            "<html><body><script>alert(1)</script></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });

        var diagnostic = Assert.Single(sink.Diagnostics);
        Assert.Null(diagnostic.Location.File);
    }

    [Fact]
    public async Task ParseAsync_base_uri_is_applied_to_document()
    {
        var host = new HtmlParsingHost();

        var document = await host.ParseAsync(
            "<html><body></body></html>",
            new HtmlPdfOptions { BaseUri = new Uri("https://example.com/") });

        Assert.Equal("https://example.com/", document.BaseUri);
    }

    [Fact]
    public async Task ParseAsync_base_uri_unset_yields_about_blank()
    {
        var host = new HtmlParsingHost();

        var document = await host.ParseAsync(
            "<html><body></body></html>",
            new HtmlPdfOptions());

        Assert.Equal("about:blank", document.BaseUri);
    }

    [Fact]
    public async Task ParseAsync_null_sink_does_not_throw_when_scripts_present()
    {
        var host = new HtmlParsingHost();

        var document = await host.ParseAsync(
            "<html><body><script>alert(1)</script></body></html>",
            new HtmlPdfOptions { Diagnostics = null });

        // Scripts are still stripped — diagnostic emission is best-effort, removal is mandatory.
        Assert.Empty(document.QuerySelectorAll("script"));
    }

    [Fact]
    public async Task ParseAsync_null_html_throws_argument_null_exception()
    {
        var host = new HtmlParsingHost();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await host.ParseAsync(html: null!, new HtmlPdfOptions()));
    }

    [Fact]
    public async Task ParseAsync_null_options_throws_argument_null_exception()
    {
        var host = new HtmlParsingHost();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await host.ParseAsync("<p/>", options: null!));
    }

    [Fact]
    public async Task ParseAsync_cancelled_token_throws_operation_cancelled()
    {
        var host = new HtmlParsingHost();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await host.ParseAsync("<p/>", new HtmlPdfOptions(), cts.Token));
    }

    [Fact]
    public async Task ParseAsync_malformed_html_does_not_throw()
    {
        // HTML5 is forgiving — unclosed tags recover. The host must not surface
        // those parser recoveries as exceptions to upstream callers.
        var host = new HtmlParsingHost();

        var document = await host.ParseAsync(
            "<div><p>unclosed",
            new HtmlPdfOptions());

        Assert.NotNull(document.DocumentElement);
        Assert.Single(document.QuerySelectorAll("p"));
    }

    [Fact]
    public async Task ParseAsync_is_deterministic_across_repeated_calls()
    {
        // Two parses of the same input must produce the same script-element count and
        // the same diagnostic stream — locks down a contract the rest of the determinism
        // story (see PROGRESS.md Phase 1 exit criteria) depends on.
        var host = new HtmlParsingHost();
        var sink1 = new CapturingSink();
        var sink2 = new CapturingSink();

        const string html = "<html><body><script>a</script><p>x</p><script>b</script></body></html>";

        await host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink1 });
        await host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink2 });

        Assert.Equal(sink1.Diagnostics.Count, sink2.Diagnostics.Count);
        for (var i = 0; i < sink1.Diagnostics.Count; i++)
        {
            Assert.Equal(sink1.Diagnostics[i].Code, sink2.Diagnostics[i].Code);
            Assert.Equal(sink1.Diagnostics[i].Severity, sink2.Diagnostics[i].Severity);
            Assert.Equal(sink1.Diagnostics[i].Location, sink2.Diagnostics[i].Location);
        }
    }

    // ------------------------------------------------------------
    // <noscript> parsing — must produce DOM, not RAWTEXT.
    // ------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_noscript_content_parses_as_dom_not_rawtext()
    {
        // HTML5 §13.2.5.4.7: when the parser's scripting flag is OFF, <noscript> content
        // is parsed as normal in-body content. NetPdf has no JS engine, so the no-script
        // fallback is exactly what should render. The host explicitly sets
        // HtmlParserOptions.IsScripting = false to lock this in even if AngleSharp's
        // default flips upstream.
        var host = new HtmlParsingHost();

        var document = await host.ParseAsync(
            "<html><body><noscript><p id=\"fallback\">Please enable JavaScript</p></noscript></body></html>",
            new HtmlPdfOptions());

        // If <noscript> were RAWTEXT, the inner <p> would not be a DOM element — it would
        // be a single text-node child of <noscript> with the literal "<p>...</p>" text.
        var fallback = document.QuerySelector("#fallback");
        Assert.NotNull(fallback);
        Assert.Equal("P", fallback!.TagName);
        Assert.Equal("Please enable JavaScript", fallback.TextContent);
    }

    [Fact]
    public async Task ParseAsync_noscript_does_not_emit_a_diagnostic()
    {
        // <noscript> content is desired output, not something to flag.
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        await host.ParseAsync(
            "<html><body><noscript><p>fallback</p></noscript></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });

        Assert.Empty(sink.Diagnostics);
    }

    // ------------------------------------------------------------
    // javascript: URL stripping (HTML-JAVASCRIPT-URL-IGNORED-001).
    // ------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_anchor_with_javascript_href_strips_attribute_and_emits_diagnostic()
    {
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        var document = await host.ParseAsync(
            "<html><body><a href=\"javascript:alert(1)\">click</a></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });

        var diagnostic = Assert.Single(sink.Diagnostics);
        Assert.Equal("HTML-JAVASCRIPT-URL-IGNORED-001", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);

        var anchor = document.QuerySelector("a");
        Assert.NotNull(anchor);
        Assert.Null(anchor!.GetAttribute("href"));
        Assert.Equal("click", anchor.TextContent);
    }

    [Fact]
    public async Task ParseAsync_anchor_with_https_href_is_kept()
    {
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        var document = await host.ParseAsync(
            "<html><body><a href=\"https://example.com/x\">click</a></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });

        Assert.Empty(sink.Diagnostics);
        Assert.Equal("https://example.com/x", document.QuerySelector("a")!.GetAttribute("href"));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JAVASCRIPT:alert(1)")]
    [InlineData("Javascript:alert(1)")]
    [InlineData("  javascript:alert(1)")]              // leading spaces
    [InlineData("\t\njavascript:alert(1)")]            // leading tab + LF
    [InlineData("java\tscript:alert(1)")]              // embedded tab inside scheme
    [InlineData("java\nscript:alert(1)")]              // embedded LF inside scheme
    [InlineData(" \r\n\tjavascript:void(0)")]          // mixed leading whitespace
    public async Task ParseAsync_javascript_url_variants_are_all_stripped(string href)
    {
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        var document = await host.ParseAsync(
            $"<html><body><a href=\"{href}\">x</a></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });

        Assert.Single(sink.Diagnostics);
        Assert.Null(document.QuerySelector("a")!.GetAttribute("href"));
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com")]
    [InlineData("/relative/path")]
    [InlineData("#anchor")]
    [InlineData("mailto:test@example.com")]
    [InlineData("tel:+1234567890")]
    [InlineData("ftp://example.com")]
    [InlineData("javascripty:fake")]                   // similar prefix but not the scheme
    [InlineData("notjavascript:still-fine")]
    public async Task ParseAsync_non_javascript_urls_are_kept(string href)
    {
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        var document = await host.ParseAsync(
            $"<html><body><a href=\"{href}\">x</a></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });

        Assert.Empty(sink.Diagnostics);
        Assert.Equal(href, document.QuerySelector("a")!.GetAttribute("href"));
    }

    [Fact]
    public async Task ParseAsync_area_with_javascript_href_is_stripped()
    {
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        var document = await host.ParseAsync(
            "<html><body><map><area href=\"javascript:bad()\" shape=\"rect\" coords=\"0,0,10,10\"></map></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });

        Assert.Single(sink.Diagnostics);
        Assert.Null(document.QuerySelector("area")!.GetAttribute("href"));
    }

    [Fact]
    public async Task ParseAsync_svg_anchor_with_xlink_javascript_href_is_stripped()
    {
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        var document = await host.ParseAsync(
            "<html><body><svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\">" +
            "<a xlink:href=\"javascript:alert(1)\"><text>x</text></a></svg></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });

        var diagnostic = Assert.Single(sink.Diagnostics);
        Assert.Equal("HTML-JAVASCRIPT-URL-IGNORED-001", diagnostic.Code);
        // Confirm the xlink:href attribute is gone (look it up via the xlink namespace URI).
        var svgAnchor = document.QuerySelector("svg a");
        Assert.NotNull(svgAnchor);
        Assert.Null(svgAnchor!.GetAttribute("http://www.w3.org/1999/xlink", "href"));
    }

    [Fact]
    public async Task ParseAsync_event_handler_attributes_are_left_in_place()
    {
        // Documented decision: event handlers are inert without a JS engine. Stripping
        // them would add DOM churn for no security benefit (PDF readers cannot dispatch
        // them). If a future phase emits attribute values into tagged-PDF metadata, this
        // policy is revisited then.
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        var document = await host.ParseAsync(
            "<html><body><button onclick=\"doStuff()\">click</button></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });

        Assert.Empty(sink.Diagnostics);
        Assert.Equal("doStuff()", document.QuerySelector("button")!.GetAttribute("onclick"));
    }

    [Fact]
    public async Task ParseAsync_script_and_javascript_url_emit_distinct_diagnostics()
    {
        // Ensures the two sanitization passes don't double-count the same element and
        // that both codes appear when both surfaces are present.
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        await host.ParseAsync(
            "<html><body><script>alert(1)</script><a href=\"javascript:alert(2)\">x</a></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });

        Assert.Equal(2, sink.Diagnostics.Count);
        Assert.Contains(sink.Diagnostics, d => d.Code == "HTML-SCRIPT-IGNORED-001");
        Assert.Contains(sink.Diagnostics, d => d.Code == "HTML-JAVASCRIPT-URL-IGNORED-001");
    }

    // ------------------------------------------------------------
    // No-network regression: prove IsResourceLoadingEnabled = false really stops fetches.
    // ------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_no_network_traffic_when_resource_loading_disabled()
    {
        // We can't reach into HtmlParsingHost's private configuration to install a
        // recording IRequester, but the contract is observable from outside: parsing
        // an HTML doc that *would* require many HTTP fetches if loading were on must
        // (a) finish quickly (single-millisecond ballpark on a no-op path) and (b) leave
        // the link.Sheet null (a successful fetch would have populated it). A "successful"
        // fetch against a deliberately-bad host would also throw; if the test passes,
        // AngleSharp didn't try.
        var host = new HtmlParsingHost();

        var html = """
            <html><head>
            <link rel="stylesheet" href="https://nope.invalid/style.css">
            <style>@import url('https://nope.invalid/import.css');</style>
            </head><body>
            <img src="https://nope.invalid/pic.png" alt="x">
            <script src="https://nope.invalid/app.js"></script>
            <iframe src="https://nope.invalid/frame.html"></iframe>
            </body></html>
            """;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());
        sw.Stop();

        // Generous budget — a real DNS failure for nope.invalid would take seconds.
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"parse took {sw.ElapsedMilliseconds} ms — IsResourceLoadingEnabled = false " +
            "regressed and AngleSharp tried to fetch external resources.");

        // <link> remains in DOM but Sheet must be null (no remote stylesheet was fetched).
        var link = document.QuerySelector("link") as AngleSharp.Html.Dom.IHtmlLinkElement;
        Assert.NotNull(link);
        Assert.Null(link!.Sheet);
    }

    private sealed class CapturingSink : IDiagnosticsSink
    {
        public List<Diagnostic> Diagnostics { get; } = new();

        public void Emit(Diagnostic diagnostic) => Diagnostics.Add(diagnostic);
    }
}
