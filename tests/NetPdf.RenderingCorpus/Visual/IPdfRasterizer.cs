// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.RenderingCorpus.Visual;

/// <summary>Rasterizes a PDF byte stream to an RGBA <see cref="RasterImage"/> at a target DPI. The
/// visual-regression harness rasterizes BOTH the NetPdf output and (for the reference path) the Chrome
/// print-to-PDF this way. <b>SkiaSharp cannot read PDF</b> — only write it — so the concrete implementation
/// is a PDFium adapter the maintainer installs (see <see cref="PdfRasterizers"/>).</summary>
public interface IPdfRasterizer
{
    /// <summary>Render <paramref name="pdf"/>'s first page to an RGBA raster at <paramref name="dpi"/>.</summary>
    RasterImage Rasterize(byte[] pdf, int dpi);
}

/// <summary>Thrown when a PDF rasterizer is invoked but the native backend (PDFium) is not available. The
/// diff-runner catches the unavailable condition up front (via <see cref="PdfRasterizers.TryCreateDefault"/>)
/// and SKIPS rather than failing, so the harness is green before the maintainer installs PDFium.</summary>
public sealed class PdfRasterizationUnavailableException(string message) : Exception(message);

/// <summary>Factory + seam for the PDF rasterizer. Today no native backend is wired, so
/// <see cref="TryCreateDefault"/> reports unavailable and the diff-runner skips. When the maintainer adds the
/// PDFium package (e.g. <c>PDFiumCore</c> / <c>bblanchon.PDFium</c>), wire a concrete adapter here and have
/// <see cref="TryCreateDefault"/> return it — the runner then starts enforcing the visual gate with zero
/// other changes.</summary>
public static class PdfRasterizers
{
    /// <summary>Try to create the configured PDF rasterizer. Returns <see langword="false"/> with a
    /// human-readable <paramref name="unavailableReason"/> when no native backend is wired (the current
    /// state) so the caller can skip cleanly.</summary>
    public static bool TryCreateDefault(out IPdfRasterizer? rasterizer, out string unavailableReason)
    {
        // No PDFium package is referenced yet (maintainer install — SkiaSharp cannot read PDF). Until then
        // the harness has no way to rasterize the NetPdf PDF, so it reports unavailable and the runner skips.
        rasterizer = null;
        unavailableReason = "no PDF rasterizer configured (PDFium backend not installed — maintainer task; "
            + "see tests/NetPdf.RenderingCorpus/docker/README.md)";
        return false;
    }

    /// <summary>A placeholder rasterizer that always throws — used where an <see cref="IPdfRasterizer"/> is
    /// structurally required but none is configured. Calling <c>Rasterize</c> surfaces a clear message
    /// rather than a null-deref / native crash.</summary>
    public static IPdfRasterizer NotConfigured { get; } = new NotConfiguredRasterizer();

    private sealed class NotConfiguredRasterizer : IPdfRasterizer
    {
        public RasterImage Rasterize(byte[] pdf, int dpi) =>
            throw new PdfRasterizationUnavailableException(
                "PDFium is not available — install the PDFium backend and wire it into PdfRasterizers "
                + "(SkiaSharp cannot read PDF). See tests/NetPdf.RenderingCorpus/docker/README.md.");
    }
}
