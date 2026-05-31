// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using NetPdf;
using NetPdf.Css.Cascade;
using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
using NetPdf.Paginate;
using NetPdf.Shaping;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Shaping;

/// <summary>
/// Phase 5 layout→PDF cycle 1 — integration test: drive the PRODUCTION
/// <see cref="HarfBuzzShaperResolver"/> through the real
/// HTML → cascade → box-tree → <see cref="BlockLayouter"/> pipeline (the
/// same chain HtmlPdf.Convert will use), proving the shaper's real glyph
/// metrics feed inline layout. A synthetic font keeps it deterministic +
/// system-font-independent.
/// </summary>
public sealed class HarfBuzzShaperResolverIntegrationTests
{
    [Fact]
    public async Task Production_shaper_lays_out_a_text_paragraph_end_to_end()
    {
        const string html =
            "<!DOCTYPE html><html><body><p>Hello world</p></body></html>";

        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());
        var sheets = AdaptSheets(document);
        var cascade = CascadeResolver.Resolve(document, sheets, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, document);
        var box = BoxBuilder.Build(document, resolved);

        var sink = new ListFragmentSink();
        using var shaper = new HarfBuzzShaperResolver(new SyntheticFontResolver());
        using var layouter = new BlockLayouter(
            rootBox: box,
            sink: sink,
            incomingContinuation: null,
            diagnostics: null,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // The document laid out to completion using the production shaper,
        // and the <p> emitted with inline (text) layout — i.e. the real
        // HarfBuzz glyph advances flowed into line building.
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.NotEmpty(sink.Fragments);
        Assert.Contains(sink.Fragments, f => f.InlineLayout is not null);
    }

    private static ImmutableArray<CssStylesheet> AdaptSheets(IDocument document)
    {
        var output = ImmutableArray.CreateBuilder<CssStylesheet>();
        var order = 0;
        var styleElements = document.QuerySelectorAll("style");
        var styleIdx = 0;
        foreach (var rawSheet in document.StyleSheets.OfType<ICssStyleSheet>())
        {
            var rawText = styleIdx < styleElements.Length
                ? styleElements[styleIdx].TextContent ?? string.Empty
                : string.Empty;
            styleIdx++;
            var preprocess = string.IsNullOrEmpty(rawText)
                ? CssPreprocessResult.Empty
                : CssPreprocessor.Process(rawText);
            output.Add(CssParserAdapter.Adapt(
                rawSheet, preprocess, href: null,
                origin: CssStylesheetOrigin.Author,
                ownerKind: CssStylesheetOwnerKind.StyleElement,
                mediaQuery: null, isDisabled: false, order: order++));
        }
        return output.ToImmutable();
    }

    private sealed class ListFragmentSink : IBlockFragmentSink
    {
        public List<BoxFragment> Fragments { get; } = new();
        public int Cursor => Fragments.Count;
        public void Emit(BoxFragment fragment) => Fragments.Add(fragment);
        public void RollbackTo(int cursor)
        {
            if (cursor < Fragments.Count)
                Fragments.RemoveRange(cursor, Fragments.Count - cursor);
        }
        public void UpdateFragmentBlockSize(int cursor, double newBlockSize)
        {
            if (cursor >= 0 && cursor < Fragments.Count)
                Fragments[cursor] = Fragments[cursor] with { BlockSize = newBlockSize };
        }
    }

    private sealed class SyntheticFontResolver : IFontResolver
    {
        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = SyntheticFont.Build(), Family = query.Family });
    }
}
