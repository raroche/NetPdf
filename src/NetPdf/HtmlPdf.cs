// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Runtime.CompilerServices;

namespace NetPdf;

/// <summary>
/// The public entry point. Convert an HTML+CSS string to a PDF byte stream.
/// </summary>
/// <remarks>
/// <para>
/// All overloads share the same pipeline: parse → style → box gen → layout → paginate
/// → paint → emit. JavaScript in the input is ignored with a <c>HTML-SCRIPT-IGNORED-001</c>
/// diagnostic; this is a deliberate design choice — see <c>docs/compatibility-matrix.md</c>.
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
    public static ValueTask<byte[]> ConvertAsync(
        string html,
        HtmlPdfOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        ct.ThrowIfCancellationRequested();
        throw new NotImplementedException(
            "NetPdf is in Phase 0 (architecture lock). The pipeline lands across Phases 1-5; " +
            "see CHANGELOG.md and the README's roadmap.");
    }

    /// <summary>
    /// Convert HTML to PDF, writing the bytes to <paramref name="output"/> as they're produced.
    /// </summary>
    public static ValueTask ConvertAsync(
        string html,
        Stream output,
        HtmlPdfOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(output);
        ct.ThrowIfCancellationRequested();
        throw new NotImplementedException(
            "NetPdf is in Phase 0 (architecture lock). The pipeline lands across Phases 1-5; " +
            "see CHANGELOG.md and the README's roadmap.");
    }

    /// <summary>
    /// Convert HTML to PDF and return diagnostics, metrics, and timing alongside the bytes.
    /// </summary>
    public static PdfRenderResult ConvertDetailed(string html, HtmlPdfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        throw new NotImplementedException(
            "NetPdf is in Phase 0 (architecture lock). The pipeline lands across Phases 1-5; " +
            "see CHANGELOG.md and the README's roadmap.");
    }

    /// <summary>
    /// The version of NetPdf currently running. Sourced from assembly informational metadata.
    /// </summary>
    public static string Version
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        get => typeof(HtmlPdf).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
