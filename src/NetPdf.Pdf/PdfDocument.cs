// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using NetPdf.Pdf.Objects;

namespace NetPdf.Pdf;

/// <summary>
/// High-level PDF document builder. Owns the indirect-object store, the page list, the
/// per-document image XObject cache (with content-hash deduplication), and the
/// trailer / metadata wiring. Underneath it delegates byte emission to
/// <see cref="PdfDocumentWriter"/>.
/// </summary>
/// <remarks>
/// <para>
/// Phase 1 surface focuses on the orchestration the AOT-smoke test and Task 23
/// determinism harness need: add a page → place text or images → save to bytes. Drawing
/// primitives that depend on the layout / paint phases (Phase 3 work) come through the
/// public <c>HtmlPdf.Convert(html)</c> path, which constructs a <see cref="PdfDocument"/>
/// internally and feeds it via the display-list IR.
/// </para>
/// <para>
/// <b>Determinism.</b> Identical input must produce byte-identical output:
/// </para>
/// <list type="bullet">
///   <item><see cref="PdfDocumentWriter"/> already pins object numbering by allocation
///         order and xref padding to a fixed format.</item>
///   <item>The image cache deduplicates by SHA-256 of the stream payload — the same image
///         used N times produces a single XObject regardless of how many pages reference
///         it.</item>
///   <item><see cref="CreationDate"/> / <see cref="ModDate"/> default to <c>null</c>;
///         when set, they are formatted via the ISO 32000 PDF date convention. Callers
///         that need bit-for-bit reproducibility leave them null.</item>
/// </list>
/// </remarks>
internal sealed class PdfDocument
{
    private readonly PdfDocumentWriter _writer;
    private readonly PdfIndirectRef _catalogRef;
    private readonly PdfIndirectRef _pagesRef;
    private readonly List<PdfPage> _pages = [];
    private readonly Dictionary<string, PdfIndirectRef> _imageCache = [];
    private bool _saved;

    public PdfDocument(string version = "1.7")
    {
        _writer = new PdfDocumentWriter { Version = version };
        _catalogRef = _writer.Objects.Allocate();
        _pagesRef = _writer.Objects.Allocate();
    }

    // ───── Document metadata (Info dict) ─────────────────────────────────────

    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Subject { get; set; }
    public string? Keywords { get; set; }
    public string? Creator { get; set; }

    /// <summary>Producer string emitted in the Info dict. Defaults to "NetPdf".</summary>
    public string Producer { get; set; } = "NetPdf";

    /// <summary>
    /// Document creation date. Null = omit from the Info dict (the deterministic default).
    /// Set explicitly when the consumer wants a real timestamp emitted.
    /// </summary>
    public DateTimeOffset? CreationDate { get; set; }

    /// <summary>Document modification date. Null = omit. Same determinism rule as <see cref="CreationDate"/>.</summary>
    public DateTimeOffset? ModDate { get; set; }

    // ───── Pages ─────────────────────────────────────────────────────────────

    public IReadOnlyList<PdfPage> Pages => _pages;

    /// <summary>Add a new page at <paramref name="size"/> and return it for content population.</summary>
    public PdfPage AddPage(MediaBoxSize size)
    {
        ThrowIfSaved();
        var pageRef = _writer.Objects.Allocate();
        var contentsRef = _writer.Objects.Allocate();
        var page = new PdfPage(pageRef, contentsRef, size, _pagesRef);
        _pages.Add(page);
        return page;
    }

    // ───── Image XObject registration with content-hash dedup ────────────────

    /// <summary>
    /// Register an Image XObject stream with the document. Returns an indirect ref
    /// usable in any page's <see cref="PdfPage.PlaceImage"/> call. If the same image
    /// content has already been registered (byte-identical), the existing ref is
    /// returned — N references to the same image produce a single XObject in the
    /// emitted file.
    /// </summary>
    public PdfIndirectRef RegisterImage(PdfStream imageStream)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        ThrowIfSaved();

        var hashKey = ComputeContentKey(imageStream);
        if (_imageCache.TryGetValue(hashKey, out var existing))
        {
            return existing;
        }
        var imageRef = _writer.Objects.Allocate();
        _writer.Objects.Assign(imageRef, imageStream);
        _imageCache[hashKey] = imageRef;
        return imageRef;
    }

    /// <summary>
    /// Number of unique Image XObjects registered with the document. Counts each
    /// distinct content-hash entry once, regardless of how many pages reference it.
    /// </summary>
    public int RegisteredImageCount => _imageCache.Count;

    // ───── Save ──────────────────────────────────────────────────────────────

    /// <summary>Render the document to a byte array.</summary>
    public byte[] Save()
    {
        var buf = new ArrayBufferWriter<byte>();
        SaveTo(buf);
        return buf.WrittenSpan.ToArray();
    }

    /// <summary>Render the document to <paramref name="output"/>.</summary>
    public void SaveTo(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        ThrowIfSaved();
        _saved = true;

        // Finalize each page: build page dict + attach content stream.
        var kids = new PdfArray();
        foreach (var page in _pages)
        {
            var (pageDict, contentBytes) = page.Finalize();
            _writer.Objects.Assign(page.PageRef, pageDict);
            // Wrap the content bytes in a PdfStream so the existing writer can length-prefix
            // and emit them. Compression is opt-in (off for now — keeps debugging trivial).
            _writer.Objects.Assign(page.ContentsRef, new PdfStream(contentBytes));
            kids.Add(page.PageRef);
        }

        // Build the /Pages and /Catalog dictionaries.
        var pagesDict = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Pages)
            .Set(PdfNames.Kids, kids)
            .Set(PdfNames.Count, new PdfInteger(_pages.Count));
        _writer.Objects.Assign(_pagesRef, pagesDict);

        var catalogDict = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Catalog)
            .Set(PdfNames.Pages, _pagesRef);
        _writer.Objects.Assign(_catalogRef, catalogDict);
        _writer.Trailer.Set(PdfNames.Root, _catalogRef);

        // Build /Info dict (always — Producer is always emitted).
        var info = BuildInfoDictionary();
        var infoRef = _writer.Objects.Allocate();
        _writer.Objects.Assign(infoRef, info);
        _writer.Trailer.Set(PdfNames.Info, infoRef);

        _writer.WriteTo(output);
    }

    private PdfDictionary BuildInfoDictionary()
    {
        // The Producer is always emitted (NetPdf identifies itself); other fields are
        // emitted only when set by the caller.
        var info = new PdfDictionary();
        info.Set(PdfNames.Producer, new PdfLiteralString(Producer));
        if (Title is not null) info.Set(PdfNames.Title, new PdfLiteralString(Title));
        if (Author is not null) info.Set(PdfNames.Author, new PdfLiteralString(Author));
        if (Subject is not null) info.Set(PdfNames.Subject, new PdfLiteralString(Subject));
        if (Keywords is not null) info.Set(PdfNames.Keywords, new PdfLiteralString(Keywords));
        if (Creator is not null) info.Set(PdfNames.Creator, new PdfLiteralString(Creator));
        if (CreationDate is { } cd) info.Set(PdfNames.CreationDate, new PdfLiteralString(FormatPdfDate(cd)));
        if (ModDate is { } md) info.Set(PdfNames.ModDate, new PdfLiteralString(FormatPdfDate(md)));
        return info;
    }

    /// <summary>
    /// Format a date per ISO 32000-2:2020 §7.9.4: <c>D:YYYYMMDDHHmmSSOHH'mm'</c>. The
    /// <c>O</c> sign is <c>+</c> / <c>-</c> / <c>Z</c> for UTC offset.
    /// </summary>
    private static string FormatPdfDate(DateTimeOffset value)
    {
        var local = value;
        var sign = local.Offset.Ticks switch
        {
            0L => "Z",
            > 0L => "+",
            _ => "-",
        };
        if (sign == "Z")
        {
            return local.UtcDateTime.ToString("'D:'yyyyMMddHHmmss'Z'", CultureInfo.InvariantCulture);
        }
        var offsetHours = Math.Abs(local.Offset.Hours);
        var offsetMinutes = Math.Abs(local.Offset.Minutes);
        return string.Create(CultureInfo.InvariantCulture,
            $"D:{local:yyyyMMddHHmmss}{sign}{offsetHours:D2}'{offsetMinutes:D2}'");
    }

    private static string ComputeContentKey(PdfStream stream)
    {
        // SHA-256 of the stream payload — same payload always hashes to the same key,
        // so dedup is content-identical, not reference-identical. The dictionary keys
        // ride on the hash bytes formatted as hex; the cache is per-document so the
        // hashing cost is paid once per unique image.
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(stream.Data, digest);
        return Convert.ToHexString(digest);
    }

    private void ThrowIfSaved()
    {
        if (_saved)
        {
            throw new InvalidOperationException(
                "PdfDocument.Save has already been invoked — further mutation is not supported. Build a new PdfDocument for a second save.");
        }
    }
}
