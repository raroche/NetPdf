// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;
using System.Globalization;
using System.Text;
using NetPdf.Pdf.Objects;

namespace NetPdf.Pdf;

/// <summary>
/// A single page in a <see cref="PdfDocument"/>. Owns its <c>/MediaBox</c>, per-page
/// <c>/Resources</c> dictionary (<c>/Font</c> + <c>/XObject</c> sub-dicts), and the
/// content-stream byte payload that the PDF reader executes to draw the page.
/// </summary>
/// <remarks>
/// <para>
/// Pages are obtained from <see cref="PdfDocument.AddPage(MediaBoxSize)"/>; the document
/// owns the page's indirect-object slot allocations. Content is built by appending
/// PDF content-stream operators via <see cref="AppendContent(string)"/> (the simplest
/// path for hand-built tests / AOT smoke) or by the higher-level draw helpers
/// (<see cref="PlaceImage(PdfIndirectRef, double, double, double, double)"/>).
/// </para>
/// </remarks>
internal sealed class PdfPage
{
    private readonly PdfIndirectRef _parentRef;
    private readonly PdfDictionary _fontsResource = new();
    private readonly PdfDictionary _xobjectsResource = new();
    // Content-stream payload is built byte-oriented from the start: PDF content streams
    // are byte sequences (operators are ASCII, but text-show operands and inline-image
    // bytes can be arbitrary 8-bit data per ISO 32000-2 §7.8). A StringBuilder would
    // force an ASCII transcode at every write boundary and silently corrupt non-ASCII
    // bytes; ArrayBufferWriter<byte> stores the raw payload faithfully.
    private readonly ArrayBufferWriter<byte> _contentBuffer = new();
    private bool _finalized;

    public PdfIndirectRef PageRef { get; }
    public PdfIndirectRef ContentsRef { get; }
    public MediaBoxSize Size { get; }

    /// <summary>The page's <c>/Resources</c> dictionary; populated automatically by the placement helpers.</summary>
    public PdfDictionary Resources { get; } = new();

    internal PdfPage(PdfIndirectRef pageRef, PdfIndirectRef contentsRef, MediaBoxSize size, PdfIndirectRef parentRef)
    {
        PageRef = pageRef;
        ContentsRef = contentsRef;
        Size = size;
        _parentRef = parentRef;
    }

    /// <summary>
    /// Append ASCII PDF content-stream text (operators like <c>q</c> / <c>Q</c>,
    /// <c>cm</c>, <c>Do</c>, and ASCII text-show ops). Spaces / newlines between
    /// operators are the caller's responsibility.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ASCII-only.</b> Each char in the string must be in the range <c>U+0000</c>
    /// to <c>U+007F</c>. A non-ASCII character throws <see cref="ArgumentException"/>
    /// rather than silently transcoding (the safe default for content streams that
    /// will have arbitrary 8-bit bytes appended via the
    /// <see cref="AppendContent(ReadOnlySpan{byte})"/> overload alongside ASCII
    /// operators). For binary or non-ASCII content (e.g., raw bytes inside a literal
    /// string operand for a Type 3 font), use the byte overload.
    /// </para>
    /// </remarks>
    public void AppendContent(string contentStreamFragment)
    {
        ArgumentNullException.ThrowIfNull(contentStreamFragment);
        ThrowIfFinalized();
        if (contentStreamFragment.Length == 0) return;

        var span = _contentBuffer.GetSpan(contentStreamFragment.Length);
        for (var i = 0; i < contentStreamFragment.Length; i++)
        {
            var c = contentStreamFragment[i];
            if (c > 0x7F)
            {
                throw new ArgumentException(
                    $"AppendContent(string) requires ASCII-only input (each char ≤ U+007F); " +
                    $"got non-ASCII char U+{(int)c:X4} at index {i}. For binary or non-ASCII " +
                    $"content use the AppendContent(ReadOnlySpan<byte>) overload instead.",
                    nameof(contentStreamFragment));
            }
            span[i] = (byte)c;
        }
        _contentBuffer.Advance(contentStreamFragment.Length);
    }

    /// <summary>
    /// Append raw PDF content-stream bytes — the binary-safe overload. The caller is
    /// responsible for emitting only well-formed PDF content-stream syntax; this method
    /// performs no validation or escaping.
    /// </summary>
    public void AppendContent(ReadOnlySpan<byte> contentStreamFragment)
    {
        ThrowIfFinalized();
        if (contentStreamFragment.IsEmpty) return;
        _contentBuffer.Write(contentStreamFragment);
    }

    /// <summary>
    /// Place an Image XObject at the given page-space rectangle (PDF points; origin at
    /// bottom-left). Returns the resource name (e.g. <c>Im1</c>) used internally to
    /// reference the image. The image must already be registered with the parent
    /// document — use <see cref="PdfDocument.RegisterImage(Objects.PdfStream)"/> for
    /// opaque images (JPEG, opaque PNG / Raster) or
    /// <see cref="PdfDocument.RegisterImage(Images.ImageXObjectResult)"/> for any
    /// image carrying a soft mask (RGBA PNG, indexed PNG with non-binary tRNS, raster
    /// formats with full alpha). The latter wires the SMask through an indirect ref;
    /// passing a primary image whose dictionary already inlines a direct
    /// <c>/SMask</c> stream into the simpler overload would emit malformed PDF.
    /// </summary>
    public string PlaceImage(PdfIndirectRef imageRef, double x, double y, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(imageRef);
        ThrowIfFinalized();

        // Allocate a per-page resource name. The pattern "Im1, Im2, …" is conventional.
        var resourceName = $"Im{_xobjectsResource.Count + 1}";
        _xobjectsResource.Set(new PdfName(resourceName), imageRef);

        // Emit content operators: q (push graphics state), cm (set CTM to scale + translate),
        // /ResourceName Do (invoke the XObject), Q (pop graphics state).
        // The cm matrix [w 0 0 h x y] scales the unit square (0..1) to (w × h) and
        // translates to (x, y). Build the fragment in a small string then route through
        // AppendContent so the ASCII-validation path runs on the operator text.
        var sb = new StringBuilder(64);
        sb.Append("q ");
        AppendNumber(sb, width); sb.Append(' ');
        sb.Append("0 0 ");
        AppendNumber(sb, height); sb.Append(' ');
        AppendNumber(sb, x); sb.Append(' ');
        AppendNumber(sb, y); sb.Append(" cm /");
        sb.Append(resourceName);
        sb.Append(" Do Q\n");
        AppendContent(sb.ToString());
        return resourceName;
    }

    private static void AppendNumber(StringBuilder sb, double value)
    {
        // PDF numbers are written without exponent notation, finite, with a maximum
        // of 5 fractional digits per ISO 32000-2:2020 §7.3.3.
        if (!double.IsFinite(value))
        {
            throw new ArgumentException(
                $"PDF number must be finite; got {value}.", nameof(value));
        }
        sb.Append(value.ToString("0.#####", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Finalize: build the page dictionary, attach the resources tree, return the
    /// content-stream bytes for the document to assign to <see cref="ContentsRef"/>.
    /// Called by <see cref="PdfDocument.Save"/>.
    /// </summary>
    internal (PdfDictionary PageDict, byte[] ContentBytes) Finalize()
    {
        ThrowIfFinalized();
        _finalized = true;

        if (_fontsResource.Count > 0) Resources.Set(PdfNames.Font, _fontsResource);
        if (_xobjectsResource.Count > 0) Resources.Set(PdfNames.XObject, _xobjectsResource);

        var mediaBox = new PdfArray()
            .Add(new PdfReal(0))
            .Add(new PdfReal(0))
            .Add(new PdfReal(Size.WidthPts))
            .Add(new PdfReal(Size.HeightPts));

        var pageDict = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Page)
            .Set(PdfNames.Parent, _parentRef)
            .Set(PdfNames.Resources, Resources)
            .Set(PdfNames.MediaBox, mediaBox)
            .Set(PdfNames.Contents, ContentsRef);

        return (pageDict, _contentBuffer.WrittenSpan.ToArray());
    }

    private void ThrowIfFinalized()
    {
        if (_finalized)
        {
            throw new InvalidOperationException(
                "PdfPage has already been finalized via PdfDocument.Save() — adding content after save is not supported.");
        }
    }
}
