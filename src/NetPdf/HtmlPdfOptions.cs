// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// All options that control conversion. The default-constructed instance produces sensible
/// behavior for a US Letter / A4 print document with print media stylesheets, backgrounds on,
/// and exact colors honored.
/// </summary>
public sealed class HtmlPdfOptions
{
    /// <summary>
    /// Base URI used to resolve relative URLs in the input HTML — <c>&lt;img src="logo.png"&gt;</c>,
    /// <c>@font-face src: url("font.woff")</c>, etc. When unset, only absolute URIs and
    /// <c>data:</c> URIs are resolvable.
    /// </summary>
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// Which stylesheet block applies. Default is <see cref="CssMediaType.Print"/> because
    /// PDF is paged output.
    /// </summary>
    public CssMediaType MediaType { get; init; } = CssMediaType.Print;

    /// <summary>
    /// The default page size when CSS does not specify one via <c>@page { size: ... }</c>.
    /// </summary>
    public PageSize PageSize { get; init; } = PageSize.A4;

    /// <summary>
    /// The default page margins when CSS does not specify them.
    /// </summary>
    public PageMargins Margins { get; init; } = PageMargins.Default;

    /// <summary>
    /// When <c>true</c>, an <c>@page { size: ... }</c> declaration in CSS overrides
    /// <see cref="PageSize"/>. When <c>false</c>, <see cref="PageSize"/> always wins.
    /// </summary>
    public bool PreferCssPageSize { get; init; } = true;

    /// <summary>
    /// When <c>true</c>, element backgrounds are painted into the PDF — the equivalent of the
    /// browser print dialog's "Background graphics" checkbox.
    /// </summary>
    public bool PrintBackgrounds { get; init; } = true;

    /// <summary>
    /// When <c>true</c>, colors are reproduced exactly without a "color-adjust" override.
    /// Equivalent to <c>print-color-adjust: exact</c> on the root.
    /// </summary>
    public bool ExactColors { get; init; } = true;

    /// <summary>Resolves images / fonts / stylesheets referenced via URL.</summary>
    public IResourceLoader? ResourceLoader { get; init; }

    /// <summary>Resolves font queries to font face data. When unset, system fonts are enumerated.</summary>
    public IFontResolver? FontResolver { get; init; }

    /// <summary>
    /// Receives diagnostics live during conversion. Aggregated diagnostics are also available
    /// via <see cref="HtmlPdf.ConvertDetailed"/>.
    /// </summary>
    public IDiagnosticsSink? Diagnostics { get; init; }

    /// <summary>Per-URI fetch policy. Defaults to data-URI-only.</summary>
    public SecurityPolicy SecurityPolicy { get; init; } = SecurityPolicy.SafeDefault;

    /// <summary>Per-document and process-level cache policy.</summary>
    public CachePolicy CachePolicy { get; init; } = CachePolicy.Default;

    /// <summary>Opt-in feature flags.</summary>
    public FeatureFlags Features { get; init; } = FeatureFlags.Default;

    /// <summary>The PDF version emitted in the file header. Defaults to 1.7 for compatibility.</summary>
    public PdfVersion EmittedPdfVersion { get; init; } = PdfVersion.V1_7;

    /// <summary>Document <c>/Title</c> metadata.</summary>
    public string? Title { get; init; }

    /// <summary>Document <c>/Author</c> metadata.</summary>
    public string? Author { get; init; }

    /// <summary>Document language tag (BCP-47), used for accessibility metadata. Default <c>"en"</c>.</summary>
    public string? Language { get; init; } = "en";

    /// <summary>Hard cap on conversion time. <c>null</c> = no cap.</summary>
    public TimeSpan? Timeout { get; init; }
}
