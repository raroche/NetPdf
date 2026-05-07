// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Xunit;

namespace NetPdf.RealDocuments.Css;

/// <summary>
/// Integration coverage for Phase 2 Task 1: every invoice corpus file parses through the
/// host without an exception. The two Tailwind-CDN samples carry a <c>&lt;script&gt;</c>
/// element and must produce at least one <c>HTML-SCRIPT-IGNORED-001</c> diagnostic; the
/// pure-CSS sample must produce none. Phase 2 exit criterion 3 (<i>"all 4 invoice corpus
/// files parse to BoxTree without exception"</i>) — Task 1 is the parser stage of that
/// pipeline.
/// </summary>
public sealed class HtmlParsingHostCorpusTests
{
    [Theory]
    [InlineData("Corpus/Invoices/01-classic-pure-css.html")]
    [InlineData("Corpus/Invoices/02-tailwind-cdn.html")]
    [InlineData("Corpus/Invoices/03-tailwind-cdn-responsive.html")]
    [InlineData("Corpus/Invoices/04-anvil-running-elements.html")]
    public async Task Corpus_invoice_parses_without_throwing(string relativePath)
    {
        var html = LoadCorpusFile(relativePath);
        var host = new HtmlParsingHost();

        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        Assert.NotNull(document.DocumentElement);
    }

    [Fact]
    public async Task Corpus_classic_pure_css_emits_no_script_diagnostics()
    {
        var html = LoadCorpusFile("Corpus/Invoices/01-classic-pure-css.html");
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        await host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink });

        Assert.Empty(sink.Diagnostics);
    }

    [Theory]
    [InlineData("Corpus/Invoices/02-tailwind-cdn.html")]
    [InlineData("Corpus/Invoices/03-tailwind-cdn-responsive.html")]
    public async Task Corpus_tailwind_cdn_emits_script_diagnostic(string relativePath)
    {
        var html = LoadCorpusFile(relativePath);
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        var document = await host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink });

        // Per Phase A A-2: the parser now also strips event-handler
        // attributes; tailwind invoices may carry inline on*-attributes from
        // their UI scripts. The script-stripped requirement still holds, but
        // the diagnostic set is the union of HTML-SCRIPT-IGNORED-001 +
        // HTML-EVENT-HANDLER-IGNORED-001. Assert at least one of the script
        // code, then allow either code in the diagnostic set.
        Assert.NotEmpty(sink.Diagnostics);
        Assert.Contains(sink.Diagnostics, d => d.Code == "HTML-SCRIPT-IGNORED-001");
        Assert.All(sink.Diagnostics, d =>
        {
            Assert.True(
                d.Code is "HTML-SCRIPT-IGNORED-001" or "HTML-EVENT-HANDLER-IGNORED-001",
                $"unexpected diagnostic code '{d.Code}'");
        });
        Assert.Empty(document.QuerySelectorAll("script"));
    }

    [Fact]
    public async Task Corpus_anvil_running_elements_parses_with_no_script_diagnostics()
    {
        // 04-anvil-running-elements.html exercises @page { @bottom-* { content: element(...) } }
        // — that's a pure-CSS construct, so the file should contain no <script> elements.
        var html = LoadCorpusFile("Corpus/Invoices/04-anvil-running-elements.html");
        var sink = new CapturingSink();
        var host = new HtmlParsingHost();

        var document = await host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink });

        Assert.Empty(sink.Diagnostics);
        Assert.NotNull(document.DocumentElement);
    }

    [Fact]
    public async Task Corpus_base_uri_is_applied_for_relative_resource_resolution()
    {
        // A real invoice references relative URLs (Tailwind config inside <script> ignored,
        // but <link>/<img> in other docs would be resolved against BaseUri). This test
        // confirms BaseUri reaches the document so future resource-loading tasks see it.
        var html = LoadCorpusFile("Corpus/Invoices/01-classic-pure-css.html");
        var host = new HtmlParsingHost();
        var baseUri = new Uri("https://invoices.example.com/2026/05/");

        var document = await host.ParseAsync(html, new HtmlPdfOptions { BaseUri = baseUri });

        Assert.Equal(baseUri.AbsoluteUri, document.BaseUri);
    }

    private static string LoadCorpusFile(string relativePath)
    {
        var corpusRoot = LocateCorpusRoot();
        var fullPath = Path.Combine(corpusRoot, relativePath);
        Assert.True(File.Exists(fullPath), $"corpus file missing: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static string LocateCorpusRoot()
    {
        // Mirror PhaseZeroSmoke.LocateCorpusRoot — walk up until we hit the test
        // project's source folder where Corpus/ lives. Works in IDE + CI.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Corpus");
            if (Directory.Exists(candidate)) return dir.FullName;
            var csproj = Path.Combine(dir.FullName, "NetPdf.RealDocuments.csproj");
            if (File.Exists(csproj)) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the NetPdf.RealDocuments source folder.");
    }

    private sealed class CapturingSink : IDiagnosticsSink
    {
        public List<Diagnostic> Diagnostics { get; } = new();

        public void Emit(Diagnostic diagnostic) => Diagnostics.Add(diagnostic);
    }
}
