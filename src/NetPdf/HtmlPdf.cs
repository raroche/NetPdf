// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Reflection;
using System.Runtime.CompilerServices;
using NetPdf.Rendering;

namespace NetPdf;

/// <summary>
/// The public entry point. Convert an HTML+CSS string to a PDF byte stream.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 5 layout→PDF wiring — early (cycle 3).</b> The facade now renders end
/// to end: HTML → cascade → box tree → fragmentainer-aware layout → paint → PDF
/// bytes. The paint bridge emits each box's <c>background-color</c> fill +
/// <c>border-*</c> edges, on a single page. Deliberately not yet painted
/// (tracked in <c>docs/deferrals.md#layout-to-pdf-pipeline</c>): <b>text runs</b>
/// (waiting on the CSS font-property resolvers), background images / gradients,
/// border-radius, and multi-page output — content overflowing the first page is
/// reported via <c>PDF-CONTENT-OVERFLOW-TRUNCATED-001</c> rather than dropped
/// silently. Output is deterministic (text-free content shapes no glyphs, so the
/// system-font dependency does not affect the bytes).
/// </para>
/// <para>
/// <b>Pipeline status:</b>
/// parse (AngleSharp) ✅ → style (AngleSharp.Css + custom cascade) ✅ → box gen ✅
/// → fragmentainer-aware layout ✅ → paint (backgrounds + borders ✅; text pending)
/// → emit ✅. JavaScript in the input is ignored with a
/// <c>HTML-SCRIPT-IGNORED-001</c> diagnostic; see <c>docs/compatibility-matrix.md</c>.
/// </para>
/// <para>
/// For typical synchronous use, call <see cref="Convert(string, HtmlPdfOptions?)"/>. For ASP.NET
/// or other async flows, use <see cref="ConvertAsync(string, HtmlPdfOptions?, CancellationToken)"/>.
/// For diagnostic-rich output (warnings, unsupported feature counts, timing breakdown),
/// use <see cref="ConvertDetailed(string, HtmlPdfOptions?)"/>.
/// </para>
/// </remarks>
public static class HtmlPdf
{
    /// <summary>
    /// Convert HTML to PDF synchronously. Use this when no external resources are loaded
    /// (no <see cref="HtmlPdfOptions.ResourceLoader"/> or <see cref="HtmlPdfOptions.FontResolver"/>);
    /// otherwise prefer <see cref="ConvertAsync(string, HtmlPdfOptions?, CancellationToken)"/>
    /// to avoid sync-over-async.
    /// </summary>
    /// <param name="html">The HTML document string.</param>
    /// <param name="options">Conversion options. <c>null</c> uses defaults.</param>
    /// <returns>The PDF bytes.</returns>
    public static byte[] Convert(string html, HtmlPdfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        return ConvertAsync(html, options, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Convert HTML (as a span) to PDF synchronously. Lower-allocation overload for callers
    /// that already hold the input as a span.
    /// </summary>
    public static byte[] Convert(ReadOnlySpan<char> html, HtmlPdfOptions? options = null)
    {
        // Span overloads cannot be async; materialize once and dispatch.
        return Convert(new string(html), options);
    }

    /// <summary>
    /// Convert HTML to PDF asynchronously. Use this when external resources may be loaded.
    /// </summary>
    public static async ValueTask<byte[]> ConvertAsync(
        string html,
        HtmlPdfOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        ct.ThrowIfCancellationRequested();
        var outcome = await RenderWithTimeoutAsync(html, options ?? new HtmlPdfOptions(), ct)
            .ConfigureAwait(false);
        return outcome.Pdf;
    }

    /// <summary>
    /// Convert HTML to PDF, writing the bytes to <paramref name="output"/> as they're produced.
    /// </summary>
    public static async ValueTask ConvertAsync(
        string html,
        Stream output,
        HtmlPdfOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(output);
        ct.ThrowIfCancellationRequested();
        var outcome = await RenderWithTimeoutAsync(html, options ?? new HtmlPdfOptions(), ct)
            .ConfigureAwait(false);
        await output.WriteAsync(outcome.Pdf, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Convert HTML to PDF and return diagnostics, metrics, and timing alongside the bytes.
    /// </summary>
    public static PdfRenderResult ConvertDetailed(string html, HtmlPdfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        var outcome = RenderWithTimeoutAsync(html, options ?? new HtmlPdfOptions(), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

        // Cycle-2 surface: the bytes, page count, and the full diagnostic set are
        // real. Richer LayoutMetrics (block / inline / glyph counts, display-command
        // totals) and per-stage Timing are not yet wired — they're populated once
        // the paint pipeline + multi-page driver land. See
        // docs/deferrals.md#layout-to-pdf-pipeline.
        return new PdfRenderResult
        {
            Pdf = outcome.Pdf,
            Warnings = outcome.Diagnostics,
            UnsupportedFeatures = Array.Empty<UnsupportedFeature>(),
            ResourceFailures = Array.Empty<ResourceFailure>(),
            LayoutMetrics = new LayoutMetrics
            {
                PageCount = outcome.PageCount,
                BlockCount = 0,
                InlineCount = 0,
                TextRunCount = 0,
                ImageCount = 0,
                FontFaceCount = 0,
                FontGlyphCount = 0,
                TotalDisplayCommands = 0,
                RasterFallbackCount = 0,
                PaginationOptimizerStateCount = 0,
            },
            Timing = default,
            PageCount = outcome.PageCount,
        };
    }

    /// <summary>
    /// Run the render pipeline, applying <see cref="HtmlPdfOptions.Timeout"/> as a hard cap.
    /// A linked <see cref="CancellationTokenSource"/> combines the caller's <paramref name="ct"/>
    /// with the timeout; when the timeout fires (and the caller did not itself cancel) the
    /// resulting <see cref="OperationCanceledException"/> is surfaced as a
    /// <see cref="TimeoutException"/>, while caller cancellation propagates as
    /// <see cref="OperationCanceledException"/>. A non-positive timeout cancels immediately
    /// (so <see cref="TimeSpan.Zero"/> fails fast). When the timeout is <see langword="null"/>
    /// the caller token is used unchanged.
    /// </summary>
    private static async ValueTask<PdfRenderPipeline.RenderOutcome> RenderWithTimeoutAsync(
        string html, HtmlPdfOptions options, CancellationToken ct)
    {
        if (options.Timeout is not { } timeout)
            return await PdfRenderPipeline.RenderAsync(html, options, ct).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout <= TimeSpan.Zero) timeoutCts.Cancel();
        else timeoutCts.CancelAfter(timeout);

        try
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            return await PdfRenderPipeline.RenderAsync(html, options, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"HTML-to-PDF conversion exceeded the configured timeout of {timeout}.");
        }
    }

    /// <summary>
    /// The package's informational version (e.g., <c>0.3.0-alpha</c>, optionally suffixed with
    /// <c>+&lt;commit-sha&gt;</c> when built with Source Link / CI metadata). Sourced from
    /// <see cref="AssemblyInformationalVersionAttribute"/>, which MSBuild auto-populates from
    /// the package's <c>VersionPrefix</c> + <c>VersionSuffix</c>. The Semver-2.0 build-metadata
    /// suffix (<c>+sha</c>) is preserved for traceability; consumers that want only
    /// <c>MAJOR.MINOR.PATCH-prerelease</c> can split on <c>'+'</c>.
    /// </summary>
    /// <remarks>
    /// Note: <see cref="AssemblyName.Version"/> reports the four-part assembly version
    /// (<c>0.1.0.0</c>), which loses the prerelease tag. The informational version is the
    /// correct field for human-facing display and prerelease awareness.
    /// </remarks>
    public static string Version
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        get => typeof(HtmlPdf).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "unknown";
    }
}
