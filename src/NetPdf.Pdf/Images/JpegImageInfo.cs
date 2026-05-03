// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf.Images;

/// <summary>
/// Image-XObject metadata extracted from a JPEG <c>SOFn</c> frame header. Used by
/// <see cref="JpegImageXObject"/> to populate the required PDF dictionary keys
/// (<c>/Width</c>, <c>/Height</c>, <c>/ColorSpace</c>, <c>/BitsPerComponent</c>) without
/// re-encoding the JPEG bytes.
/// </summary>
internal sealed record JpegImageInfo
{
    /// <summary>Image width in pixels (X dimension).</summary>
    public required int Width { get; init; }

    /// <summary>Image height in pixels (Y dimension).</summary>
    public required int Height { get; init; }

    /// <summary>Sample precision in bits per component (typically 8 — 12 is allowed but rare).</summary>
    public required int BitsPerComponent { get; init; }

    /// <summary>Number of color components: 1 = grayscale, 3 = RGB / YCbCr, 4 = CMYK / YCCK.</summary>
    public required int ComponentCount { get; init; }

    /// <summary>
    /// True when the JPEG carries an Adobe APP14 marker with <c>ColorTransform = 0</c>
    /// indicating uninterpreted CMYK channels. Such JPEGs are typically saved by Photoshop
    /// with INVERTED CMYK values; the PDF <c>/Decode [1 0 1 0 1 0 1 0]</c> array is used
    /// by <see cref="JpegImageXObject"/> to compensate.
    /// </summary>
    public required bool IsAdobeInvertedCmyk { get; init; }

    /// <summary>The PDF color-space name corresponding to <see cref="ComponentCount"/>.</summary>
    public string ColorSpaceName => ComponentCount switch
    {
        1 => "DeviceGray",
        3 => "DeviceRGB",
        4 => "DeviceCMYK",
        _ => throw new InvalidDataException(
            $"JPEG: unsupported component count {ComponentCount} for PDF embedding (expected 1, 3, or 4)."),
    };
}
