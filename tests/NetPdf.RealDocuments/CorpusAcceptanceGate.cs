// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf;
using Xunit;

namespace NetPdf.RealDocuments;

/// <summary>
/// Phase-5 task 21 — the RENDER-ACCEPTANCE half of the final corpus gate: every
/// real-document corpus file must convert end-to-end through the public facade to
/// a STRUCTURALLY VALID PDF (valid header + trailer, at least one page). This is
/// discovered by globbing the corpus tree, so any file added to
/// <c>Corpus/</c> is automatically gated — a new sample that crashes the pipeline,
/// produces zero pages, or emits malformed bytes fails CI without a test edit.
///
/// <para>The PIXEL-tolerance half of the corpus gate (each page within Chrome
/// reference tolerance) lives in <c>NetPdf.RenderingCorpus</c> and activates once
/// the maintainer commits the Chrome reference PNGs — see
/// <c>tests/NetPdf.RenderingCorpus/references/README.md</c>. Together they are the
/// release-candidate corpus acceptance gate (docs/phases/phase-5-packaging-and-release.md).</para>
///
/// <para>Corpus files legitimately emit diagnostics — the Tailwind-CDN samples
/// render unstyled without runtime JS (documented limitation) and the Anvil sample
/// carries remote images that the default security policy skips offline — so this
/// gate asserts VALID OUTPUT, not zero warnings. Content is never dropped silently:
/// each skip is a structured diagnostic on <see cref="PdfRenderResult.Warnings"/>.</para>
/// </summary>
public sealed class CorpusAcceptanceGate
{
    public static TheoryData<string> CorpusDocuments()
    {
        var root = LocateCorpusRoot();
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(root, "Corpus"), "*.html", SearchOption.AllDirectories))
        {
            // Store the corpus-root-relative path so the test name is stable + readable.
            data.Add(Path.GetRelativePath(root, path));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusDocuments))]
    public void Corpus_document_renders_to_a_valid_pdf(string relativePath)
    {
        var fullPath = Path.Combine(LocateCorpusRoot(), relativePath);
        var html = File.ReadAllText(fullPath);

        // Root relative URLs (img / CSS / font `src`) at the document's own directory, exactly as a real
        // consumer would. Without a BaseUri a corpus file carrying a relative asset renders DEGRADED (the
        // asset fails to resolve — see `A_relative_asset_is_unresolvable_without_BaseUri` below) yet still
        // produces a "structurally valid" PDF, so the gate would silently miss that regression. The
        // directory needs a trailing separator so relative resolution keeps it (RFC 3986 §5.3).
        var baseUri = new Uri(Path.GetDirectoryName(fullPath)! + Path.DirectorySeparatorChar);
        var result = HtmlPdf.ConvertDetailed(html, new HtmlPdfOptions { BaseUri = baseUri });

        Assert.True(result.PageCount >= 1,
            $"{relativePath}: rendered to {result.PageCount} pages — a corpus document must produce at least one page.");
        AssertStructurallyValidPdf(result.Pdf, relativePath);
    }

    [Fact]
    public void A_relative_asset_is_unresolvable_without_BaseUri()
    {
        // Proves the BaseUri wiring above is load-bearing (review [P2]). A relative image reference is
        // UNRESOLVABLE with no BaseUri — the engine surfaces RES-LOAD-FAILED-001 "…no HtmlPdfOptions.BaseUri
        // to resolve it against…" (content is never dropped silently) — and that specific failure disappears
        // once a BaseUri is supplied. If a future edit dropped BaseUri from the gate, this documents the
        // degraded-render consequence the gate would otherwise pass over.
        const string html =
            "<!DOCTYPE html><html><body><img src=\"logo-relative.png\" width=\"10\" height=\"10\"></body></html>";

        var withoutBase = HtmlPdf.ConvertDetailed(html);
        Assert.Contains(withoutBase.Warnings,
            d => d.Message.Contains("HtmlPdfOptions.BaseUri", StringComparison.Ordinal));

        // With a BaseUri the relative URL RESOLVES (to a path that doesn't exist → a different, resolution-
        // stage failure), so the "no BaseUri" diagnostic is gone — proving BaseUri changed resolution.
        var withBase = HtmlPdf.ConvertDetailed(html, new HtmlPdfOptions { BaseUri = new Uri(Path.GetTempPath()) });
        Assert.DoesNotContain(withBase.Warnings,
            d => d.Message.Contains("no HtmlPdfOptions.BaseUri", StringComparison.Ordinal));
    }

    [Fact]
    public void Corpus_acceptance_gate_covers_at_least_the_known_invoice_set()
    {
        // Guard the glob itself: if the corpus were emptied / mis-copied to the test
        // output, the [Theory] above would silently run zero cases and pass. Pin a
        // floor so a vanished corpus fails loudly.
        var count = Directory.GetFiles(
            Path.Combine(LocateCorpusRoot(), "Corpus"), "*.html", SearchOption.AllDirectories).Length;
        Assert.True(count >= 4,
            $"the corpus acceptance gate discovered {count} HTML documents (< the 4 known invoices) — "
            + "the Corpus/ tree is missing or was not copied next to the test binary.");
    }

    private static void AssertStructurallyValidPdf(byte[] pdf, string label)
    {
        Assert.True(pdf.Length > 64, $"{label}: PDF is implausibly small ({pdf.Length} bytes).");

        // Header: a PDF must begin with "%PDF-1.x".
        var header = Encoding.ASCII.GetString(pdf, 0, 8);
        Assert.True(header.StartsWith("%PDF-1.", StringComparison.Ordinal),
            $"{label}: missing %PDF-1.x header (got '{header}').");

        // Trailer: the file must end with the "%%EOF" marker (allowing a trailing newline).
        var tailLen = Math.Min(64, pdf.Length);
        var tail = Encoding.ASCII.GetString(pdf, pdf.Length - tailLen, tailLen);
        Assert.Contains("%%EOF", tail);
    }

    /// <summary>
    /// Walk up from the test assembly's location to the folder that holds the Corpus
    /// directory (the copied-to-output corpus in CI, or the source folder in IDE runs).
    /// </summary>
    private static string LocateCorpusRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Corpus"))) return dir.FullName;
            if (File.Exists(Path.Combine(dir.FullName, "NetPdf.RealDocuments.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the NetPdf.RealDocuments Corpus root.");
    }
}
