// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;
using NetPdf.Pdf;
using NetPdf.Pdf.Objects;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

/// <summary>Phase 4 links (PR 4) — the active-content preflight's narrow opt-in for hyperlink <c>/URI</c>
/// actions. A <c>/URI</c> is permitted ONLY inside a well-formed URI action (<c>/S /URI</c>) AND only when
/// <see cref="PdfDocumentWriter.AllowUriLinkAnnotations"/> is set; everything else stays blocked.</summary>
public sealed class PdfUriLinkPreflightTests
{
    private static ArrayBufferWriter<byte> Buffer() => new();

    private static PdfDocumentWriter WriterWithChild(PdfObject child)
    {
        var w = new PdfDocumentWriter();
        var catalog = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Catalog)
            .Set(new PdfName("X"), child); // reachable from /Root so the walk visits it
        w.Trailer.Set(PdfNames.Root, w.Objects.Add(catalog));
        return w;
    }

    private static PdfDictionary UriAction(string uri) => new PdfDictionary()
        .Set(PdfNames.S, PdfNames.URI)
        .Set(PdfNames.URI, new PdfLiteralString(uri));

    [Fact]
    public void Uri_action_throws_when_not_opted_in()
    {
        var w = WriterWithChild(UriAction("https://example.com/"));
        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(Buffer()));
        Assert.Contains("/URI", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Uri_action_passes_when_opted_in()
    {
        var w = WriterWithChild(UriAction("https://example.com/"));
        w.AllowUriLinkAnnotations = true;
        var buf = Buffer();
        w.WriteTo(buf);
        Assert.True(buf.WrittenCount > 0);
    }

    [Fact]
    public void Uri_key_outside_a_uri_action_still_throws_even_when_opted_in()
    {
        // A /URI key in a dict that is NOT a /S /URI action is still suspicious → blocked.
        var notAnAction = new PdfDictionary().Set(PdfNames.URI, new PdfLiteralString("https://example.com/"));
        var w = WriterWithChild(notAnAction);
        w.AllowUriLinkAnnotations = true;
        Assert.Throws<InvalidOperationException>(() => w.WriteTo(Buffer()));
    }

    [Fact]
    public void Launch_action_still_throws_when_uri_opt_in_is_set()
    {
        // The opt-in unblocks ONLY /URI — /Launch (and JS / SubmitForm / embedded files) stay blocked.
        var launch = new PdfDictionary()
            .Set(PdfNames.S, new PdfName("Launch"))
            .Set(new PdfName("Launch"), new PdfLiteralString("calc.exe"));
        var w = WriterWithChild(launch);
        w.AllowUriLinkAnnotations = true;
        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(Buffer()));
        Assert.Contains("Launch", ex.Message, StringComparison.Ordinal);
    }
}
