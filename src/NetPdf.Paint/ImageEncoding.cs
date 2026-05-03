// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Paint;

/// <summary>
/// Encoding of the bytes carried in <see cref="RasterImage.EncodedBytes"/>. Phase 1
/// supports the two formats embeddable directly into PDF without re-encoding (JPEG via
/// <c>DCTDecode</c>, PNG via <c>FlateDecode</c>+optional <c>SMask</c>); WebP / AVIF / GIF
/// land in Phase 4 — they decode through SkiaSharp and re-emit as one of these two.
/// </summary>
internal enum ImageEncoding : byte
{
    /// <summary>PNG bytes. Embedded with <c>FlateDecode</c>; alpha channel becomes an <c>SMask</c>.</summary>
    Png = 0,

    /// <summary>Baseline JPEG bytes. Passthrough via <c>DCTDecode</c> — never re-encoded.</summary>
    Jpeg = 1,
}
