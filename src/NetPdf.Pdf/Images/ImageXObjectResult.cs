// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Objects;

namespace NetPdf.Pdf.Images;

/// <summary>
/// Common return type for the family of image-XObject builders
/// (<see cref="JpegImageXObject"/>, <see cref="PngImageXObject"/>,
/// <see cref="RasterImageXObject"/>). Carries the primary Image XObject plus an optional
/// SMask. The high-level <c>PdfDocument</c> builder (Task 22) writes both as separate
/// indirect objects and wires the <c>/SMask</c> back-reference automatically.
/// </summary>
internal sealed class ImageXObjectResult
{
    /// <summary>The primary Image XObject containing the color (or grayscale / indexed) plane.</summary>
    public required PdfStream Image { get; init; }

    /// <summary>
    /// Optional soft-mask stream carrying per-pixel alpha. Null for opaque images and for
    /// images that use a color-key <c>/Mask</c> (which lives on the primary Image's
    /// dictionary instead).
    /// </summary>
    public PdfStream? SMask { get; init; }
}
