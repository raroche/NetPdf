// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NetPdf;
using NetPdf.Layout.Semantic;
using Xunit;

namespace NetPdf.RealDocuments.Semantic;

/// <summary>
/// Task 15 cycle 1 — production-path SemanticTreeBuilder coverage. Runs the
/// invoice corpus through HtmlParsingHost + SemanticTreeBuilder and asserts
/// the structural shape is sane (table cells captured, no metadata leaks,
/// document root present).
/// </summary>
public sealed class SemanticTreeCorpusTests
{
    private static IEnumerable<SemanticNode> Walk(SemanticNode root)
    {
        yield return root;
        foreach (var c in root.Children)
            foreach (var d in Walk(c))
                yield return d;
    }

    private static string LoadCorpusFile(string relativePath)
    {
        var path = System.IO.Path.Combine(System.AppContext.BaseDirectory, relativePath);
        return System.IO.File.ReadAllText(path);
    }

    [Theory]
    [InlineData("Corpus/Invoices/01-classic-pure-css.html")]
    [InlineData("Corpus/Invoices/04-anvil-running-elements.html")]
    public async Task Corpus_invoice_produces_a_semantic_tree_without_throwing(string relativePath)
    {
        var html = LoadCorpusFile(relativePath);
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var tree = SemanticTreeBuilder.Build(document);

        Assert.Equal(SemanticKind.Document, tree.Kind);
        // The classic invoice has multiple semantic landmarks; assert at
        // minimum that the tree is non-trivial.
        Assert.True(tree.CountDescendants() > 0,
            $"Expected non-empty semantic tree for '{relativePath}'.");
    }

    [Fact]
    public async Task Corpus_classic_invoice_table_rows_and_cells_are_captured()
    {
        var html = LoadCorpusFile("Corpus/Invoices/01-classic-pure-css.html");
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());
        var tree = SemanticTreeBuilder.Build(document);

        var rows = Walk(tree).Where(n => n.Kind == SemanticKind.TableRow).ToList();
        var cells = Walk(tree).Where(n =>
            n.Kind == SemanticKind.TableCell || n.Kind == SemanticKind.TableHeaderCell).ToList();
        Assert.NotEmpty(rows);
        Assert.NotEmpty(cells);
    }

    [Fact]
    public async Task Corpus_classic_invoice_does_not_leak_metadata_text_into_semantic_tree()
    {
        // Per Task 12 hardening: <head>, <title>, <style>, <link>, <meta> have
        // display:none defaults so their text is suppressed in the box tree.
        // The semantic tree walks the DOM directly (no cascade), but those
        // elements are also transparent for semantic purposes — they emit no
        // SemanticNode and their children (CSS rules, link metadata) don't
        // become accessibility-tree nodes.
        var html = LoadCorpusFile("Corpus/Invoices/01-classic-pure-css.html");
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());
        var tree = SemanticTreeBuilder.Build(document);

        // No semantic node should have @page or font-family in its text — those
        // are CSS rules from <style>, not displayed content.
        var allText = string.Join("\n", Walk(tree).Select(n => n.Text));
        Assert.DoesNotContain("@page", allText);
        Assert.DoesNotContain("font-family:", allText);
    }
}
