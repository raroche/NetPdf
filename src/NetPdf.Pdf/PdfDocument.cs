// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using NetPdf.Pdf.Images;
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
///   <item>The image cache deduplicates by SHA-256 of the stream payload <b>and</b> the
///         stream dictionary's canonical bytes (and, for transparent images, the SMask
///         bytes too) — the same image used N times produces a single XObject regardless
///         of how many pages reference it; two identical-pixel buffers with different
///         color spaces or filter params remain distinct.</item>
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
    /// Register an opaque Image XObject stream with the document. Returns an indirect ref
    /// usable in any page's <see cref="PdfPage.PlaceImage"/> call. If a byte-identical
    /// Image XObject (same payload <b>and</b> same dictionary) has already been
    /// registered, the existing ref is returned — N references to the same image produce
    /// a single XObject in the emitted file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For images that carry a soft mask (PNG with full alpha, raster RGBA), use the
    /// <see cref="RegisterImage(ImageXObjectResult)"/> overload instead so the SMask
    /// is allocated as its own indirect object and wired through the primary image's
    /// <c>/SMask</c> entry. Passing a primary image with an inline SMask through this
    /// overload would emit a malformed PDF (direct streams nested in dictionaries are
    /// rejected by ISO 32000-2 §7.3.8).
    /// </para>
    /// </remarks>
    public PdfIndirectRef RegisterImage(PdfStream imageStream)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        return RegisterImage(new ImageXObjectResult { Image = imageStream });
    }

    /// <summary>
    /// Register an Image XObject (and its optional SMask) with the document. The primary
    /// image and the SMask each get their own indirect-object slot; the primary image's
    /// <c>/SMask</c> entry is rewritten to an indirect reference to the SMask slot.
    /// Dedup keys cover the entire pair, so two registrations of the same
    /// <c>(Image, SMask)</c> content collapse to a single XObject pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Builders (<see cref="PngImageXObject"/>, <see cref="RasterImageXObject"/>,
    /// <see cref="JpegImageXObject"/>) construct an <see cref="ImageXObjectResult"/>
    /// whose primary image dictionary does <b>not</b> have <c>/SMask</c> set —
    /// registration is the place that allocates indirect refs and wires them in.
    /// </para>
    /// </remarks>
    public PdfIndirectRef RegisterImage(ImageXObjectResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ThrowIfSaved();
        ValidateImageXObjectShape(result.Image, parameterName: nameof(result), isSMask: false);
        if (result.SMask is not null)
        {
            ValidateImageXObjectShape(result.SMask, parameterName: nameof(result), isSMask: true);
        }

        var key = ComputeContentKey(result);
        if (_imageCache.TryGetValue(key, out var existing))
        {
            return existing;
        }

        // SMask must be allocated and assigned before we set the primary image's /SMask
        // pointer — otherwise the primary's dictionary holds a forward ref to an
        // unallocated slot, which preflight would reject.
        //
        // The primary image's dictionary is CLONED before wiring /SMask. Mutating
        // the caller's instance would be a correctness bug:
        //   * Same-instance dedup would break: the first call hashes a clean dict and
        //     caches under key K1; a second call with the same instance would hash a
        //     now-mutated dict (carrying /SMask), miss the cache under K2, and
        //     allocate a duplicate XObject pair — silent emission bloat and broken
        //     dedup.
        //   * Cross-document reuse would carry document-A's indirect ref into
        //     document B's dictionary (different store id; preflight would reject it
        //     after a confusing chain of mutations).
        // Cloning is shallow (PdfDictionary entries shared by reference, payload
        // bytes shared via PdfStream.WithDictionary) — a few-ns allocation overhead
        // per registration, negligible vs. the SHA-256 dedup-key hash.
        var imageRef = _writer.Objects.Allocate();
        var imageToAssign = result.Image;
        if (result.SMask is not null)
        {
            var smaskRef = _writer.Objects.Allocate();
            _writer.Objects.Assign(smaskRef, result.SMask);
            imageToAssign = CloneWithSMaskWired(result.Image, smaskRef);
        }
        _writer.Objects.Assign(imageRef, imageToAssign);
        _imageCache[key] = imageRef;
        return imageRef;
    }

    private static PdfStream CloneWithSMaskWired(PdfStream source, PdfIndirectRef smaskRef)
    {
        // Shallow-copy the dictionary entries into a fresh one, then add /SMask.
        // PdfStream's constructor will reset /Length on the new dict from the payload
        // length, so we skip the source's /Length entry to avoid carrying a stale
        // value.
        var clonedDict = new PdfDictionary();
        foreach (var entry in source.Dictionary)
        {
            if (entry.Key.Equals(PdfNames.Length)) continue;
            clonedDict.Set(entry.Key, entry.Value);
        }
        clonedDict.Set(PdfNames.SMask, smaskRef);
        return source.WithDictionary(clonedDict);
    }

    /// <summary>
    /// Number of unique image registrations cached in the document. Each distinct
    /// <c>(Image, SMask?)</c> pair counts once, regardless of how many pages reference
    /// it. (An opaque image and the same image registered as part of an
    /// <see cref="ImageXObjectResult"/> with a non-null SMask are <b>distinct</b>
    /// entries — their emitted XObject graphs differ.)
    /// </summary>
    public int RegisteredImageCount => _imageCache.Count;

    private static void ValidateImageXObjectShape(PdfStream stream, string parameterName, bool isSMask)
    {
        var dict = stream.Dictionary;
        var role = isSMask ? "SMask Image XObject" : "Image XObject";

        // Subtype is the canonical "this is an Image XObject" marker — required.
        var subtype = dict.Get(PdfNames.Subtype);
        if (subtype is not PdfName subtypeName || !subtypeName.Equals(PdfNames.Image))
        {
            throw new ArgumentException(
                $"{role} validation failed: /Subtype must be /Image " +
                $"(got {subtype?.GetType().Name ?? "null"}). Use the appropriate XObject " +
                "builder (JpegImageXObject, PngImageXObject, RasterImageXObject) to construct " +
                "Image XObjects with the required dictionary keys.",
                parameterName);
        }
        // Type is optional per §8.9.5 Table 87, but if present must be /XObject.
        var typeValue = dict.Get(PdfNames.Type);
        if (typeValue is PdfName typeName && !typeName.Equals(PdfNames.XObject))
        {
            throw new ArgumentException(
                $"{role} /Type is /{typeName.Value}; expected /XObject (or omitted).",
                parameterName);
        }

        // Width / Height are required and must be strictly positive — per ISO
        // 32000-2:2020 §8.9.5 Table 87, Width and Height "shall be the width [or
        // height], in samples, of the image". Zero or negative samples don't
        // describe a paintable image, and PDF readers silently misrender or crash on
        // them.
        if (dict.Get(PdfNames.Width) is not PdfInteger widthInt)
        {
            throw new ArgumentException(
                $"{role} is missing required /Width (PdfInteger).",
                parameterName);
        }
        if (widthInt.Value <= 0)
        {
            throw new ArgumentException(
                $"{role} /Width must be > 0 (got {widthInt.Value}).",
                parameterName);
        }
        if (dict.Get(PdfNames.Height) is not PdfInteger heightInt)
        {
            throw new ArgumentException(
                $"{role} is missing required /Height (PdfInteger).",
                parameterName);
        }
        if (heightInt.Value <= 0)
        {
            throw new ArgumentException(
                $"{role} /Height must be > 0 (got {heightInt.Value}).",
                parameterName);
        }

        if (dict.Get(PdfNames.ColorSpace) is null)
        {
            throw new ArgumentException(
                $"{role} is missing required /ColorSpace.",
                parameterName);
        }

        // BitsPerComponent: the spec-allowed values for general Image XObjects are
        // 1, 2, 4, 8, 16 (§8.9.5 Table 89). For soft masks, §11.6 narrows this to
        // 8 or 16 — sub-byte alpha planes aren't permitted because alpha is a
        // continuous value, not an indexed lookup.
        if (dict.Get(PdfNames.BitsPerComponent) is not PdfInteger bpcInt)
        {
            throw new ArgumentException(
                $"{role} is missing required /BitsPerComponent (PdfInteger).",
                parameterName);
        }
        if (isSMask)
        {
            if (bpcInt.Value != 8 && bpcInt.Value != 16)
            {
                throw new ArgumentException(
                    $"SMask Image XObject /BitsPerComponent must be 8 or 16 per ISO 32000-2 §11.6 " +
                    $"(got {bpcInt.Value}).",
                    parameterName);
            }
        }
        else
        {
            if (bpcInt.Value is not (1 or 2 or 4 or 8 or 16))
            {
                throw new ArgumentException(
                    $"Image XObject /BitsPerComponent must be one of {{1, 2, 4, 8, 16}} per " +
                    $"ISO 32000-2 §8.9.5 Table 89 (got {bpcInt.Value}).",
                    parameterName);
            }
        }
    }

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

    private static string ComputeContentKey(ImageXObjectResult result)
    {
        // Dedup key covers the entire image graph: primary stream payload + dictionary
        // for the Image XObject, plus the same for the SMask when present. Hashing the
        // dictionary (not just the payload) is required for correctness: two pixel
        // buffers with identical bytes but different /ColorSpace, /BitsPerComponent,
        // /Filter, /DecodeParms, /Decode, or /Mask render entirely differently. Without
        // dictionary inclusion, two RGBA8888 pixel buffers that happen to coincide
        // post-FlateDecode but have different color models would silently dedupe — a
        // visual-corruption bug.
        var imageDigest = HashStream(result.Image);
        if (result.SMask is null)
        {
            return $"img1:{Convert.ToHexString(imageDigest)}";
        }
        var smaskDigest = HashStream(result.SMask);
        return $"img2:{Convert.ToHexString(imageDigest)}|{Convert.ToHexString(smaskDigest)}";
    }

    private static byte[] HashStream(PdfStream stream)
    {
        // SHA-256 over (payload bytes ⨁ dictionary canonical bytes). The dictionary's
        // own writer produces deterministic output (insertion order preserved by
        // OrderedDictionary), so two PdfStreams built from the same data + same Set()
        // sequence hash identically — that's the desired dedup grain.
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(stream.Data);
        var dictBuffer = new ArrayBufferWriter<byte>();
        var dictWriter = new PdfWriter(dictBuffer);
        stream.Dictionary.WriteTo(dictWriter);
        hasher.AppendData(dictBuffer.WrittenSpan);
        return hasher.GetHashAndReset();
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
