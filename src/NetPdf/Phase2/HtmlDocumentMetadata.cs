// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using AngleSharp.Dom;

namespace NetPdf.Phase2;

/// <summary>
/// Document-level metadata harvested from the parsed HTML head — the <c>&lt;title&gt;</c>,
/// the standard <c>&lt;meta name="…"&gt;</c> descriptors, and the root <c>&lt;html lang&gt;</c>.
/// These feed the PDF <c>/Info</c> dictionary, the XMP metadata stream, and the catalog
/// <c>/Lang</c> so a generated document is searchable / catalogued by name rather than by
/// filename. Every field is nullable: a missing head element leaves it <see langword="null"/>,
/// and an explicit <see cref="HtmlPdfOptions"/> value takes precedence over the harvested one
/// (see <c>PdfRenderPipeline</c>).
/// </summary>
/// <param name="Title">The document <c>&lt;title&gt;</c> text → PDF <c>/Title</c>.</param>
/// <param name="Author"><c>&lt;meta name="author"&gt;</c> content → PDF <c>/Author</c>.</param>
/// <param name="Description"><c>&lt;meta name="description"&gt;</c> content → PDF <c>/Subject</c>
/// (PDF has no distinct "description" field; <c>/Subject</c> is its conventional home).</param>
/// <param name="Keywords"><c>&lt;meta name="keywords"&gt;</c> content → PDF <c>/Keywords</c>.</param>
/// <param name="Lang">The root element's <c>lang</c> attribute → catalog <c>/Lang</c>.</param>
internal readonly record struct HtmlDocumentMetadata(
    string? Title,
    string? Author,
    string? Description,
    string? Keywords,
    string? Lang)
{
    /// <summary>Nothing harvested — the all-null baseline.</summary>
    public static HtmlDocumentMetadata Empty => default;

    /// <summary>
    /// Harvest metadata from the parsed <paramref name="document"/>. Reads <c>document.Title</c>
    /// (AngleSharp already resolves the <c>&lt;title&gt;</c> element's text), the three standard
    /// named <c>&lt;meta&gt;</c> descriptors (author / description / keywords, matched
    /// case-insensitively on the <c>name</c> attribute — the first non-empty wins), and the root
    /// element's <c>lang</c>. Empty / whitespace values are normalized to <see langword="null"/>.
    /// </summary>
    public static HtmlDocumentMetadata Extract(IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var title = NullIfBlank(document.Title);
        string? author = null, description = null, keywords = null;

        // Only <head> descriptors are document metadata — a <meta name="author"> in the body is
        // content, not document-level metadata. Scope the scan strictly to the head element; if there
        // is no head (malformed HTML), harvest NO meta descriptors rather than falling back to the whole
        // document (which would wrongly pick up body <meta> tags). AngleSharp synthesizes a <head> for
        // conformant documents, so the null branch is the defensive malformed-input case.
        var headMetas = document.Head?.QuerySelectorAll("meta");
        foreach (var meta in headMetas ?? Enumerable.Empty<IElement>())
        {
            var name = meta.GetAttribute("name");
            if (string.IsNullOrEmpty(name)) continue;
            var content = NullIfBlank(meta.GetAttribute("content"));
            if (content is null) continue;

            // First non-empty descriptor wins (a document rarely repeats these; if it does,
            // the head-order-first value is the authoritative one).
            if (author is null && name.Equals("author", StringComparison.OrdinalIgnoreCase))
                author = content;
            else if (description is null && name.Equals("description", StringComparison.OrdinalIgnoreCase))
                description = content;
            else if (keywords is null && name.Equals("keywords", StringComparison.OrdinalIgnoreCase))
                keywords = content;
        }

        var lang = NullIfBlank(document.DocumentElement?.GetAttribute("lang"));

        return new HtmlDocumentMetadata(title, author, description, keywords, lang);
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
