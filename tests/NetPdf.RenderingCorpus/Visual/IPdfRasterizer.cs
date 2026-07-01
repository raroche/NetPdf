// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;

namespace NetPdf.RenderingCorpus.Visual;

/// <summary>Rasterizes a PDF byte stream to one RGBA <see cref="RasterImage"/> PER PAGE at a target DPI. The
/// visual-regression harness rasterizes BOTH the NetPdf output and (for the reference path) the Chrome
/// print-to-PDF this way and diffs page-for-page — so regressions on LATER pages (page counters, running
/// headers/footers, fragmentation) are caught, not just the first page. <b>SkiaSharp cannot read PDF</b> —
/// only write it — so the concrete implementation is <see cref="PdfiumRasterizer"/>, a PDFium adapter (via the
/// test-only <c>PDFtoImage</c> package, which ships PDFium native assets — no separate install; see
/// <see cref="PdfRasterizers"/>).</summary>
public interface IPdfRasterizer
{
    /// <summary>Render every page of <paramref name="pdf"/> to an RGBA raster at <paramref name="dpi"/>, in
    /// document order (index 0 = page 1).</summary>
    IReadOnlyList<RasterImage> RasterizeAllPages(byte[] pdf, int dpi);
}

/// <summary>Thrown when a PDF rasterizer is invoked but the native backend (PDFium) failed to load on this
/// platform. The diff-runner catches the unavailable condition up front (via
/// <see cref="PdfRasterizers.TryCreateDefault"/>, whose native-load probe reports it) and SKIPS rather than
/// failing, so the harness stays green.</summary>
public sealed class PdfRasterizationUnavailableException(string message) : Exception(message);

/// <summary>Factory + seam for the PDF rasterizer. The PDFium backend (via <c>PDFtoImage</c>, a test-only
/// dependency that ships PDFium native assets for macOS/Linux/Windows) is now wired, so
/// <see cref="TryCreateDefault"/> returns a working <see cref="PdfiumRasterizer"/> once a cheap native-load
/// probe succeeds — the diff-runner then enforces the visual gate as soon as a committed reference exists. If
/// PDFium fails to load, the probe reports unavailable; per <see cref="VisualGatePolicy"/> the runner then
/// SKIPS while the gate is inert (no reference committed and not forced), but FAILS once a reference exists /
/// the gate is required — so a broken native backend can't silently pass an active gate.</summary>
public static class PdfRasterizers
{
    // The native-load probe runs ONCE per test process (PDFium is not thread-safe and a probe render isn't
    // free) — the result is cached. The stateless PdfiumRasterizer instance is reused across every invoice.
    private static readonly System.Lazy<(IPdfRasterizer? Rasterizer, string Reason)> ProbeResult =
        new(() =>
        {
            try
            {
                // Force the native PDFium library to load + parse a real (NetPdf-produced) PDF. A missing
                // native asset throws (e.g. DllNotFoundException) HERE rather than mid-gate.
                var probe = NetPdf.HtmlPdf.Convert("<html><body></body></html>");
                var rasterizer = new PdfiumRasterizer();
                _ = rasterizer.RasterizeAllPages(probe, dpi: 24);
                return (rasterizer, "");
            }
            catch (System.Exception ex) when (ex is not System.OutOfMemoryException)
            {
                // A DllNotFoundException / BadImageFormatException / PDFium error → "unavailable". A fatal
                // process-level failure (OOM) is NOT swallowed — it rethrows so genuine failures surface.
                return (null, $"PDFium backend unavailable ({ex.GetType().Name}: {ex.Message})");
            }
        });

    /// <summary>Try to get the configured PDF rasterizer. On the first call, a native-load probe renders a
    /// trivial NetPdf PDF through PDFium (a missing native lib throws there, not mid-gate); the result is
    /// cached, so this is cheap on every subsequent invoice. Returns the shared stateless
    /// <see cref="PdfiumRasterizer"/> on success, or <see langword="false"/> with a human-readable
    /// <paramref name="unavailableReason"/> so the caller can skip cleanly.</summary>
    public static bool TryCreateDefault(out IPdfRasterizer? rasterizer, out string unavailableReason)
    {
        (rasterizer, unavailableReason) = ProbeResult.Value;
        return rasterizer is not null;
    }

    /// <summary>A placeholder rasterizer that always throws — used where an <see cref="IPdfRasterizer"/> is
    /// structurally required but none is configured. Calling <c>Rasterize</c> surfaces a clear message
    /// rather than a null-deref / native crash.</summary>
    public static IPdfRasterizer NotConfigured { get; } = new NotConfiguredRasterizer();

    private sealed class NotConfiguredRasterizer : IPdfRasterizer
    {
        public System.Collections.Generic.IReadOnlyList<RasterImage> RasterizeAllPages(byte[] pdf, int dpi) =>
            throw new PdfRasterizationUnavailableException(
                "PDFium is not available on this platform (the native-load probe failed). "
                + "See tests/NetPdf.RenderingCorpus/docker/README.md.");
    }
}
