// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;

namespace NetPdf.RenderingCorpus.Visual;

/// <summary>Rasterizes a PDF byte stream to one RGBA <see cref="RasterImage"/> PER PAGE at a target DPI. The
/// visual-regression harness rasterizes BOTH the NetPdf output and (for the reference path) the Chrome
/// print-to-PDF this way and diffs page-for-page — so regressions on LATER pages (page counters, running
/// headers/footers, fragmentation) are caught, not just the first page. <b>SkiaSharp cannot read PDF</b> —
/// only write it — so the concrete implementation is a PDFium adapter the maintainer installs (see
/// <see cref="PdfRasterizers"/>).</summary>
public interface IPdfRasterizer
{
    /// <summary>Render every page of <paramref name="pdf"/> to an RGBA raster at <paramref name="dpi"/>, in
    /// document order (index 0 = page 1).</summary>
    IReadOnlyList<RasterImage> RasterizeAllPages(byte[] pdf, int dpi);
}

/// <summary>Thrown when a PDF rasterizer is invoked but the native backend (PDFium) is not available. The
/// diff-runner catches the unavailable condition up front (via <see cref="PdfRasterizers.TryCreateDefault"/>)
/// and SKIPS rather than failing, so the harness is green before the maintainer installs PDFium.</summary>
public sealed class PdfRasterizationUnavailableException(string message) : Exception(message);

/// <summary>Factory + seam for the PDF rasterizer. The PDFium backend (via <c>PDFtoImage</c>, a test-only
/// dependency that ships PDFium native assets for macOS/Linux/Windows) is now wired, so
/// <see cref="TryCreateDefault"/> returns a working <see cref="PdfiumRasterizer"/> once a cheap native-load
/// probe succeeds — the diff-runner then enforces the visual gate as soon as a committed reference exists. If
/// PDFium fails to load on some platform, the probe reports unavailable and the runner skips (rather than
/// crashing), so the harness stays green.</summary>
public static class PdfRasterizers
{
    /// <summary>Try to create the configured PDF rasterizer. Renders a trivial NetPdf PDF through PDFium as a
    /// native-load probe: on success returns a <see cref="PdfiumRasterizer"/>; on any failure (native lib
    /// absent / load error) returns <see langword="false"/> with a human-readable
    /// <paramref name="unavailableReason"/> so the caller can skip cleanly.</summary>
    public static bool TryCreateDefault(out IPdfRasterizer? rasterizer, out string unavailableReason)
    {
        try
        {
            // Force the native PDFium library to load + parse a real (NetPdf-produced) PDF. A missing native
            // asset throws (e.g. DllNotFoundException) HERE rather than mid-gate.
            var probe = NetPdf.HtmlPdf.Convert("<html><body></body></html>");
            _ = new PdfiumRasterizer().RasterizeAllPages(probe, dpi: 24);
            rasterizer = new PdfiumRasterizer();
            unavailableReason = "";
            return true;
        }
        catch (System.Exception ex)
        {
            rasterizer = null;
            unavailableReason = $"PDFium backend unavailable ({ex.GetType().Name}: {ex.Message})";
            return false;
        }
    }

    /// <summary>A placeholder rasterizer that always throws — used where an <see cref="IPdfRasterizer"/> is
    /// structurally required but none is configured. Calling <c>Rasterize</c> surfaces a clear message
    /// rather than a null-deref / native crash.</summary>
    public static IPdfRasterizer NotConfigured { get; } = new NotConfiguredRasterizer();

    private sealed class NotConfiguredRasterizer : IPdfRasterizer
    {
        public System.Collections.Generic.IReadOnlyList<RasterImage> RasterizeAllPages(byte[] pdf, int dpi) =>
            throw new PdfRasterizationUnavailableException(
                "PDFium is not available — install the PDFium backend and wire it into PdfRasterizers "
                + "(SkiaSharp cannot read PDF). See tests/NetPdf.RenderingCorpus/docker/README.md.");
    }
}
