// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using NetPdf;
using NetPdf.Css.Cascade;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;
using NetPdf.Layout.Boxes;
using Xunit;

namespace NetPdf.RealDocuments.Css;

/// <summary>
/// Production-path BoxBuilder tests per Task 12 hardening review Rec 9 — every
/// invoice in the corpus runs through the full pipeline (HtmlParsingHost →
/// CssPreprocessor → CssParserAdapter → CascadeResolver → VarResolver →
/// BoxBuilder) and produces a non-empty box tree without throwing. Catches
/// integration regressions that the unit-test path (synthetic in-memory parse)
/// would miss.
/// </summary>
public sealed class BoxBuilderCorpusTests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public System.Collections.Generic.List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    private static ImmutableArray<CssStylesheet> AdaptAllSheetsViaPreprocessor(IDocument document)
    {
        var output = ImmutableArray.CreateBuilder<CssStylesheet>();
        var order = 0;

        var styleElements = document.QuerySelectorAll("style");
        var styleIdx = 0;
        foreach (var rawSheet in document.StyleSheets.OfType<ICssStyleSheet>())
        {
            string rawText;
            if (styleIdx < styleElements.Length)
            {
                rawText = styleElements[styleIdx].TextContent ?? string.Empty;
                styleIdx++;
            }
            else
            {
                rawText = string.Empty;
            }
            var preprocess = string.IsNullOrEmpty(rawText)
                ? CssPreprocessResult.Empty
                : CssPreprocessor.Process(rawText);
            output.Add(CssParserAdapter.Adapt(
                rawSheet, preprocess,
                href: null,
                origin: CssStylesheetOrigin.Author,
                ownerKind: CssStylesheetOwnerKind.StyleElement,
                mediaQuery: null,
                isDisabled: false,
                order: order++));
        }
        return output.ToImmutable();
    }

    private static int CountBoxes(Box root)
    {
        var n = 1;
        foreach (var c in root.Children) n += CountBoxes(c);
        return n;
    }

    private static System.Collections.Generic.IEnumerable<Box> Walk(Box root)
    {
        yield return root;
        foreach (var c in root.Children)
        {
            foreach (var d in Walk(c)) yield return d;
        }
    }

    [Theory]
    [InlineData("Corpus/Invoices/01-classic-pure-css.html")]
    [InlineData("Corpus/Invoices/02-tailwind-cdn.html")]
    [InlineData("Corpus/Invoices/03-tailwind-cdn-responsive.html")]
    [InlineData("Corpus/Invoices/04-anvil-running-elements.html")]
    public async Task Corpus_invoice_runs_through_BoxBuilder_without_throwing(string relativePath)
    {
        var html = LoadCorpusFile(relativePath);
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheets = AdaptAllSheetsViaPreprocessor(document);
        var cascade = CascadeResolver.Resolve(document, sheets, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, document);

        var sink = new CapturingSink();
        var root = BoxBuilder.Build(document, resolved, sink);

        Assert.Equal(BoxKind.Root, root.Kind);
        Assert.NotEmpty(root.Children);
        // Box tree should have at least <html> + <body> + some content.
        Assert.True(CountBoxes(root) >= 3,
            $"Expected at least 3 boxes for '{relativePath}', got {CountBoxes(root)}");
    }

    [Fact]
    public async Task Corpus_classic_pure_css_box_tree_excludes_metadata_text()
    {
        // Per Task 12 hardening Rec 1: <head>, <title>, <style>, <link>, <meta>
        // must default to display: none. Their contents (CSS rule text in
        // <style>, the document title in <title>, etc.) must NOT appear as
        // visible TextRun boxes.
        var html = LoadCorpusFile("Corpus/Invoices/01-classic-pure-css.html");
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheets = AdaptAllSheetsViaPreprocessor(document);
        var cascade = CascadeResolver.Resolve(document, sheets, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, document);
        var root = BoxBuilder.Build(document, resolved);

        // Collect every TextRun's text. Metadata content should NOT appear:
        // - The <style> block contains rules like "color: ...". If it leaked,
        //   we'd see CSS source text in the box tree.
        var allText = string.Join("\n", Walk(root)
            .Where(b => b.Kind == BoxKind.TextRun)
            .Select(b => b.Text));

        // The corpus has CSS rules like ".invoice" and "{ font-family: ... }".
        // None of those should appear as visible text.
        Assert.DoesNotContain("font-family:", allText);
        Assert.DoesNotContain("@page", allText);
    }

    [Fact]
    public async Task Corpus_classic_invoice_table_wrappers_all_own_a_TableGrid()
    {
        // Task 13 — every Table / InlineTable wrapper produced from the
        // corpus must own exactly one anonymous TableGrid child holding the
        // row-groups. The classic-pure-css invoice uses real <table> markup
        // for the line items.
        var html = LoadCorpusFile("Corpus/Invoices/01-classic-pure-css.html");
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheets = AdaptAllSheetsViaPreprocessor(document);
        var cascade = CascadeResolver.Resolve(document, sheets, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, document);
        var root = BoxBuilder.Build(document, resolved);

        var wrappers = Walk(root).Where(b => b.IsTableWrapper).ToList();
        Assert.NotEmpty(wrappers);
        foreach (var w in wrappers)
        {
            // Wrapper children are zero-or-more captions followed by exactly
            // one TableGrid. The grid must always be present.
            var grids = w.Children.Where(c => c.Kind == BoxKind.TableGrid).ToList();
            Assert.Single(grids);
            // No row-group lives directly under the wrapper — they all moved
            // under the grid.
            Assert.DoesNotContain(w.Children, c =>
                c.Kind is BoxKind.TableRowGroup
                    or BoxKind.TableHeaderGroup
                    or BoxKind.TableFooterGroup);
        }
    }

    [Fact]
    public async Task Corpus_classic_invoice_no_anonymous_table_cells_carry_whitespace()
    {
        // Task 13 — whitespace-only text between table internals must be
        // stripped per Tables L3 §3.1, NOT wrapped in anonymous cells.
        var html = LoadCorpusFile("Corpus/Invoices/01-classic-pure-css.html");
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheets = AdaptAllSheetsViaPreprocessor(document);
        var cascade = CascadeResolver.Resolve(document, sheets, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, document);
        var root = BoxBuilder.Build(document, resolved);

        var anonCells = Walk(root)
            .Where(b => b.Kind == BoxKind.TableCell && b.IsAnonymous)
            .ToList();
        // Any anon cells synthesized must contain real content (TextRun with
        // non-whitespace, an inline-level child, a block, etc.) — not a
        // single whitespace-only TextRun.
        foreach (var cell in anonCells)
        {
            var hasNonWhitespace = false;
            foreach (var c in cell.Children)
            {
                if (c.Kind != BoxKind.TextRun || !IsAllWhitespace(c.Text))
                {
                    hasNonWhitespace = true;
                    break;
                }
            }
            Assert.True(hasNonWhitespace,
                "Anonymous TableCell contains only whitespace text — should have been stripped before wrapping.");
        }

        static bool IsAllWhitespace(string s)
        {
            foreach (var c in s)
            {
                if (c is not (' ' or '\t' or '\n' or '\r' or '\f')) return false;
            }
            return true;
        }
    }

    [Fact]
    public async Task Corpus_invoice_does_not_emit_warnings_for_known_good_input()
    {
        // The classic-pure-css invoice is hand-crafted to exercise the cascade
        // cleanly. BoxBuilder should not flood the diagnostic sink with
        // CSS-PROPERTY-VALUE-INVALID-001 warnings on it.
        var html = LoadCorpusFile("Corpus/Invoices/01-classic-pure-css.html");
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheets = AdaptAllSheetsViaPreprocessor(document);
        var cascade = CascadeResolver.Resolve(document, sheets, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, document);
        var sink = new CapturingSink();
        BoxBuilder.Build(document, resolved, sink);

        // Some diagnostics may legitimately fire (e.g., for unsupported color
        // forms in the fixture); the assertion is that BoxBuilder doesn't
        // generate floods of property-value-invalid noise per element.
        var invalidValueDiags = sink.Diagnostics
            .Count(d => d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001);
        Assert.True(invalidValueDiags < 50,
            $"Expected fewer than 50 CSS-PROPERTY-VALUE-INVALID-001 diagnostics on the classic invoice; got {invalidValueDiags}");
    }

    private static string LoadCorpusFile(string relativePath)
    {
        var path = System.IO.Path.Combine(System.AppContext.BaseDirectory, relativePath);
        return System.IO.File.ReadAllText(path);
    }
}
