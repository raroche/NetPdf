// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Objects;

namespace NetPdf.Pdf.Images;

/// <summary>
/// Builds a PDF Image XObject around a raw JPEG byte stream — no re-encode, just a
/// dictionary wrapper with the <c>DCTDecode</c> filter so the JPEG bytes flow into
/// the PDF verbatim. Saving the re-encode pass is the headline win of "JPEG
/// passthrough" embedding (a 100 KB JPEG goes from ~50 ms re-encode to &lt; 1 ms wrap).
/// </summary>
/// <remarks>
/// <para>
/// Spec basis: ISO 32000-2:2020 §8.9.5 (Image XObjects) and §7.4.8 (DCTDecode filter).
/// The required dictionary keys are <c>/Type /XObject</c>, <c>/Subtype /Image</c>,
/// <c>/Width</c>, <c>/Height</c>, <c>/ColorSpace</c>, <c>/BitsPerComponent</c>, and
/// <c>/Filter /DCTDecode</c>; <c>/Length</c> is filled in automatically by
/// <see cref="PdfStream"/>.
/// </para>
/// <para>
/// <b>Adobe-inverted CMYK.</b> When <see cref="JpegImageInfo.IsAdobeInvertedCmyk"/> is
/// true (Photoshop-saved CMYK JPEGs), the builder adds
/// <c>/Decode [1 0 1 0 1 0 1 0]</c> so PDF viewers invert each channel. Without it the
/// image renders as a photo-negative. Every other ColorSpace passes through with no
/// <c>/Decode</c> array.
/// </para>
/// </remarks>
internal static class JpegImageXObject
{
    /// <summary>
    /// Build a PDF Image XObject stream from raw JPEG bytes. The JPEG bytes are NOT
    /// copied — they are referenced directly by the resulting <see cref="PdfStream"/>.
    /// </summary>
    public static PdfStream Build(byte[] jpegBytes)
    {
        ArgumentNullException.ThrowIfNull(jpegBytes);
        var info = JpegHeaderParser.Parse(jpegBytes);
        return Build(jpegBytes, info);
    }

    /// <summary>Build using a pre-parsed <see cref="JpegImageInfo"/> (test seam).</summary>
    public static PdfStream Build(byte[] jpegBytes, JpegImageInfo info)
    {
        ArgumentNullException.ThrowIfNull(jpegBytes);
        ArgumentNullException.ThrowIfNull(info);

        var dict = new PdfDictionary();
        dict.Set(PdfNames.Type, PdfNames.XObject);
        dict.Set(PdfNames.Subtype, PdfNames.Image);
        dict.Set(PdfNames.Width, new PdfInteger(info.Width));
        dict.Set(PdfNames.Height, new PdfInteger(info.Height));
        // Reuse JpegImageInfo.ColorSpaceName so the component-count → name mapping has a
        // single source of truth.
        dict.Set(PdfNames.ColorSpace, new PdfName(info.ColorSpaceName));
        dict.Set(PdfNames.BitsPerComponent, new PdfInteger(info.BitsPerComponent));
        dict.Set(PdfNames.Filter, PdfNames.DCTDecode);

        if (info.IsAdobeInvertedCmyk)
        {
            // Photoshop-saved CMYK JPEGs store inverted channel values; PDF /Decode
            // [1 0 1 0 1 0 1 0] flips each channel back during rendering.
            var decode = new PdfArray();
            for (var c = 0; c < 4; c++)
            {
                decode.Add(new PdfInteger(1));
                decode.Add(new PdfInteger(0));
            }
            dict.Set(PdfNames.Decode, decode);
        }

        return new PdfStream(jpegBytes, dict);
    }
}
