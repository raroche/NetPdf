// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
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
    private readonly System.Text.StringBuilder _contentStreamBuilder = new();
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
    /// Append raw PDF content-stream bytes (operators like <c>q</c> / <c>Q</c>,
    /// <c>cm</c>, <c>Do</c>, text-show ops). Spaces / newlines between operators are
    /// the caller's responsibility; the page emits whatever is appended.
    /// </summary>
    public void AppendContent(string contentStreamFragment)
    {
        ArgumentNullException.ThrowIfNull(contentStreamFragment);
        ThrowIfFinalized();
        _contentStreamBuilder.Append(contentStreamFragment);
    }

    /// <summary>
    /// Place an Image XObject at the given page-space rectangle (PDF points; origin at
    /// bottom-left). Returns the resource name (e.g. <c>Im1</c>) used internally to
    /// reference the image. The image must already be registered with the parent
    /// document via <see cref="PdfDocument.RegisterImage(PdfStream)"/>.
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
        // translates to (x, y).
        _contentStreamBuilder.Append("q ");
        AppendNumber(width); _contentStreamBuilder.Append(' ');
        _contentStreamBuilder.Append("0 0 ");
        AppendNumber(height); _contentStreamBuilder.Append(' ');
        AppendNumber(x); _contentStreamBuilder.Append(' ');
        AppendNumber(y); _contentStreamBuilder.Append(" cm /");
        _contentStreamBuilder.Append(resourceName);
        _contentStreamBuilder.Append(" Do Q\n");
        return resourceName;
    }

    private void AppendNumber(double value)
    {
        // PDF numbers are written without exponent notation, finite, with a maximum
        // of 5 fractional digits per ISO 32000-2:2020 §7.3.3.
        if (!double.IsFinite(value))
        {
            throw new ArgumentException(
                $"PDF number must be finite; got {value}.", nameof(value));
        }
        _contentStreamBuilder.Append(value.ToString("0.#####", CultureInfo.InvariantCulture));
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

        var contentBytes = System.Text.Encoding.ASCII.GetBytes(_contentStreamBuilder.ToString());
        return (pageDict, contentBytes);
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
