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

    /// <summary>
    /// Phase 4 native vector SVG (opt-in, first cut). When <c>true</c>, an <c>&lt;img&gt;</c> whose source is
    /// SVG is drawn as native PDF vector operators (crisp at any zoom) IF the whole document is within the
    /// supported subset (basic shapes + <c>&lt;path&gt;</c>, <c>&lt;g&gt;</c>, element <c>transform</c>s, solid
    /// fill/stroke, <c>fill-rule</c>, root <c>viewBox</c>/<c>preserveAspectRatio</c>); otherwise it falls back
    /// to the raster path. Default <c>false</c> → SVG always rasterizes, so output is byte-identical to before.
    /// The native path uses the SVG's own <c>preserveAspectRatio</c> (meet), so it doesn't apply
    /// <c>object-fit: fill</c> stretching.
    /// </summary>
    public bool NativeSvgRendering { get; init; }

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

    /// <summary>Document <c>/Title</c> metadata. When set, overrides the HTML <c>&lt;title&gt;</c>.</summary>
    public string? Title { get; init; }

    /// <summary>Document <c>/Author</c> metadata. When set, overrides <c>&lt;meta name="author"&gt;</c>.</summary>
    public string? Author { get; init; }

    /// <summary>Document <c>/Subject</c> metadata. When set, overrides <c>&lt;meta name="description"&gt;</c>.</summary>
    public string? Subject { get; init; }

    /// <summary>Document <c>/Keywords</c> metadata. When set, overrides <c>&lt;meta name="keywords"&gt;</c>.</summary>
    public string? Keywords { get; init; }

    /// <summary>Document <c>/Creator</c> metadata — the authoring application. When set, emitted verbatim.</summary>
    public string? Creator { get; init; }

    /// <summary>Document language tag (BCP-47), used for the catalog <c>/Lang</c> and XMP metadata.
    /// The HTML root <c>&lt;html lang&gt;</c> attribute takes precedence when present; this is the
    /// fallback. <see langword="null"/> (the default) means "not declared" — no <c>/Lang</c> is
    /// manufactured, so a document that declares no language is not presumed to be English.</summary>
    public string? Language { get; init; }

    /// <summary>Custom document-information entries emitted as extra keys in the PDF <c>/Info</c>
    /// dictionary (beyond the standard Title/Author/Subject/Keywords/Creator). Keys must be
    /// non-empty and are matched case-sensitively; the standard keys and <c>Producer</c> cannot be
    /// overridden here. <see langword="null"/> or empty = none.</summary>
    public IReadOnlyDictionary<string, string>? DocumentProperties { get; init; }

    /// <summary>Document creation timestamp emitted as <c>/CreationDate</c> (and in XMP). <see langword="null"/>
    /// (the default) omits it entirely — the deterministic default, since no wall-clock time is ever read.
    /// Set an explicit value when a real (or fixed) timestamp is wanted.</summary>
    public DateTimeOffset? CreationDate { get; init; }

    /// <summary>Document modification timestamp emitted as <c>/ModDate</c>. <see langword="null"/> (the
    /// default) omits it. Same determinism rule as <see cref="CreationDate"/>.</summary>
    public DateTimeOffset? ModDate { get; init; }

    /// <summary>Hard cap on conversion time. <c>null</c> = no cap.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>The page layout a reader uses when it first opens the document (catalog <c>/PageLayout</c>).
    /// <see langword="null"/> (the default) omits the entry, leaving the reader's own default.</summary>
    public PdfPageLayout? PageLayout { get; init; }

    /// <summary>How a reader presents its navigation UI when the document opens (catalog <c>/PageMode</c>) —
    /// e.g. <see cref="PdfPageMode.UseOutlines"/> opens the bookmarks panel. <see langword="null"/> (the
    /// default) omits the entry, leaving the reader's own default.</summary>
    public PdfPageMode? PageMode { get; init; }
}
