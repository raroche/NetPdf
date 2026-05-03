// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Reflection;
using System.Runtime.CompilerServices;

namespace NetPdf;

/// <summary>
/// The public entry point. Convert an HTML+CSS string to a PDF byte stream.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 1 alpha (`0.1.0-alpha`) status — call sites still throw.</b> All
/// <see cref="Convert(string, HtmlPdfOptions?)"/> /
/// <see cref="ConvertAsync(string, HtmlPdfOptions?, CancellationToken)"/> /
/// <see cref="ConvertDetailed(string, HtmlPdfOptions?)"/> overloads currently throw
/// <see cref="NotImplementedException"/>. The internal byte-writer pipeline (deterministic
/// PDF emission, OpenType subsetting, WOFF/WOFF2, image embedding, full UAX #9/#14/#29 text
/// shaping) is complete and exercised by 1546 unit tests, an enforced JIT/AOT byte-parity
/// gate, and a +25%-tolerance performance regression gate — but the HTML parsing → CSS
/// cascade → layout → paint glue that this facade depends on lands in Phase 2.
/// </para>
/// <para>
/// <b>Future pipeline (Phase 2+):</b> parse (AngleSharp) → style (AngleSharp.Css + custom
/// cascade) → box gen → fragmentainer-aware layout → paint → emit. JavaScript in the input
/// will be ignored with a <c>HTML-SCRIPT-IGNORED-001</c> diagnostic; see
/// <c>docs/compatibility-matrix.md</c>.
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
            "NetPdf 0.1.0-alpha — the public HtmlPdf.Convert facade is not yet wired. " +
            "Phase 1 shipped the deterministic PDF byte writer, font subsetter, image " +
            "embedders, and UAX #9/#14/#29 text shaping (all internal); Phase 2 wires HTML " +
            "parsing + CSS cascade + box-tree generation through to that writer. " +
            "Track progress in CHANGELOG.md and PROGRESS.md.");
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
            "NetPdf 0.1.0-alpha — the public HtmlPdf.Convert facade is not yet wired. " +
            "Phase 1 shipped the deterministic PDF byte writer, font subsetter, image " +
            "embedders, and UAX #9/#14/#29 text shaping (all internal); Phase 2 wires HTML " +
            "parsing + CSS cascade + box-tree generation through to that writer. " +
            "Track progress in CHANGELOG.md and PROGRESS.md.");
    }

    /// <summary>
    /// Convert HTML to PDF and return diagnostics, metrics, and timing alongside the bytes.
    /// </summary>
    public static PdfRenderResult ConvertDetailed(string html, HtmlPdfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        throw new NotImplementedException(
            "NetPdf 0.1.0-alpha — the public HtmlPdf.Convert facade is not yet wired. " +
            "Phase 1 shipped the deterministic PDF byte writer, font subsetter, image " +
            "embedders, and UAX #9/#14/#29 text shaping (all internal); Phase 2 wires HTML " +
            "parsing + CSS cascade + box-tree generation through to that writer. " +
            "Track progress in CHANGELOG.md and PROGRESS.md.");
    }

    /// <summary>
    /// The package's informational version (e.g., <c>0.1.0-alpha</c>, optionally suffixed with
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
