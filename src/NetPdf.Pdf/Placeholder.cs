// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf;

/// <summary>
/// Phase 0 placeholder. Real PDF byte writer (object model, indirect refs, xref, content streams,
/// font subsetting, image XObjects, ToUnicode CMaps, deterministic serialization) lands in Phase 1
/// per the architecture plan.
/// </summary>
internal static class PdfWriterMarker
{
    internal const string Phase = "0";
    internal const string ExpectedShipPhase = "1";
}
