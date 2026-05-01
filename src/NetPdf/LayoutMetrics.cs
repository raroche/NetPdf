// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// Metrics describing how the document was laid out. Surfaced via
/// <see cref="PdfRenderResult.LayoutMetrics"/>; useful for diagnostics dashboards
/// and regression detection.
/// </summary>
public sealed class LayoutMetrics
{
    public required int PageCount { get; init; }
    public required int BlockCount { get; init; }
    public required int InlineCount { get; init; }
    public required int TextRunCount { get; init; }
    public required int ImageCount { get; init; }
    public required int FontFaceCount { get; init; }
    public required int FontGlyphCount { get; init; }
    public required long TotalDisplayCommands { get; init; }
    public required int RasterFallbackCount { get; init; }
    public required int PaginationOptimizerStateCount { get; init; }
}
