// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NetPdf.Css.Cascade;
using NetPdf.Css.Properties;
using NetPdf.Diagnostics;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
using NetPdf.Pdf;
using NetPdf.Pdf.Images;
using NetPdf.Pdf.Objects;

namespace NetPdf.Rendering;

/// <summary>
/// Per-render image store (img-pipeline + bg-image cycles) — the body image pipeline's heart.
/// <see cref="PrefetchAsync"/> walks the box tree BEFORE layout collecting every image
/// reference (an <c>&lt;img src&gt;</c> on a replaced box; a <c>background-image: url(...)</c>
/// winner on any element-backed box), fetches each unique URI ONCE through
/// <see cref="SafeResourceLoader"/> (<c>data:</c> URIs decode inline with no user loader — the
/// self-contained default; other schemes need <see cref="HtmlPdfOptions.ResourceLoader"/> +
/// policy), decodes it to a PDF Image XObject (PNG / JPEG natively;
/// GIF / WebP / BMP via the approved SkiaSharp raster fallback), and records the intrinsic
/// pixel size the sizing pre-pass (<see cref="ReplacedSizeResolver"/>) reads. Paint memoizes
/// the <see cref="PdfDocument.RegisterImage(ImageXObjectResult)"/> ref per entry so one image
/// used N times embeds once (the document also dedups byte-identical streams).
/// </summary>
/// <remarks>Failures NEVER throw: a fetch failure surfaces <c>RES-LOAD-FAILED-001</c>, an
/// undecodable payload <c>IMG-DECODE-FAILED-001</c> (CLAUDE.md #7 — the element still lays out
/// at its declared/attribute size; nothing paints). A single <c>linear-gradient(...)</c> /
/// <c>radial-gradient(...)</c> is now SUPPORTED (collected as a parsed spec, painted as a PDF
/// native shading — Phase 4); only an <c>background-image</c> form still beyond the engine (a
/// conic / repeating gradient, a multi-layer list, or an unrecognized value) surfaces
/// <c>CSS-BACKGROUND-IMAGE-UNSUPPORTED-001</c> once per render.</remarks>
internal sealed class ImageResourceCache
{
    /// <summary>One decoded image: the intrinsic pixel size (CSS px at 1:1, the first-cut
    /// density rule) + the built XObject, with the registered ref memoized at first paint.</summary>
    internal sealed class Entry
    {
        public required double WidthPx { get; init; }
        public required double HeightPx { get; init; }
        public required ImageXObjectResult XObject { get; init; }
        public PdfIndirectRef? RegisteredRef;

        /// <summary>Phase 4 filters (PR 2) — the RAW fetched image bytes, retained so a CSS
        /// <c>filter</c> on an <c>&lt;img&gt;</c> can re-decode + filter the image into a separate
        /// XObject. (A minor retention cost on every image; a documented optimization is to keep it
        /// only when a filtered box references the URI.)</summary>
        public required byte[] SourceBytes { get; init; }

        /// <summary>Phase 4 filters — per-(filter-key) cache of the filtered XObject registration +
        /// its drop-shadow padding, so N <c>&lt;img&gt;</c>s with the same source + filter share one
        /// filtered XObject.</summary>
        public Dictionary<string, (PdfIndirectRef Ref, NetPdf.Pdf.Images.FilterPadding Pad)>? FilteredRefs;
    }

    private readonly Dictionary<string, Entry> _byUri = new(StringComparer.Ordinal);

    /// <summary>An <c>&lt;img&gt;</c> box's image reference (img-pipeline + object-fit +
    /// object-position cycles): the resolved URI key + the COMPUTED <c>object-fit</c> keyword
    /// index (the KeywordResolver table order — 0 = <c>fill</c>, the initial; a registered
    /// property since PR #168 review P2) + the RAW <c>object-position</c> winner (null = unset
    /// → the 50% 50% initial; the property stays UNREGISTERED — a 2-component position needs a
    /// new metadata type, so the raw read is the documented seam, like border-radius).</summary>
    internal readonly record struct ImgSpec(
        string UriKey, int ObjectFitKeyword, string? ObjectPositionRaw, CssFilter? Filter = null);

    /// <summary>Replaced (<c>&lt;img&gt;</c>) box → its image spec. Only boxes whose fetch +
    /// decode SUCCEEDED appear (a failed image lays out / paints nothing).</summary>
    public Dictionary<Box, ImgSpec> ImageBoxes { get; } = new();

    /// <summary>A box's background-image reference (bg-image + bg-variants cycles): the resolved
    /// URI key + the RAW declared <c>background-repeat</c> / <c>-size</c> / <c>-position</c> /
    /// <c>-origin</c> / <c>-clip</c> winners (null = unset → the initial), parsed by the tiler at
    /// paint. <c>-origin</c> sets the positioning area (initial padding-box), <c>-clip</c> the
    /// paint area (initial border-box) — bg-origin / bg-clip cycles.</summary>
    internal readonly record struct BackgroundSpec(
        string UriKey, string? RepeatRaw, string? SizeRaw, string? PositionRaw,
        string? OriginRaw = null, string? ClipRaw = null);

    /// <summary>Element-backed box → its <c>background-image</c> spec (bg-image cycle). Only
    /// successfully decoded references appear.</summary>
    public Dictionary<Box, BackgroundSpec> BackgroundImageBoxes { get; } = new();

    /// <summary>Phase 4 gradients — element-backed box → its parsed
    /// <c>background-image: linear-gradient(...)</c>. Populated during collection (no
    /// network/decode — a gradient is computed, not fetched); the painter emits a PDF
    /// native axial shading per box.</summary>
    public Dictionary<Box, CssLinearGradient> BackgroundGradientBoxes { get; } = new();

    /// <summary>Phase 4 gradients — element-backed box → its parsed
    /// <c>background-image: radial-gradient(...)</c> (PDF native radial shading).</summary>
    public Dictionary<Box, CssRadialGradient> BackgroundRadialGradientBoxes { get; } = new();

    /// <summary>Phase 4 gradients (PR 1 refinements) — element-backed box → its parsed
    /// <c>conic-gradient(...)</c> / <c>repeating-conic-gradient(...)</c>. PDF has no native conic
    /// shading, so the painter rasterizes it via Skia (a sweep gradient) and places it as an image
    /// XObject with an alpha <c>/SMask</c> — emitting <c>CSS-CONIC-GRADIENT-RASTER-001</c>.</summary>
    public Dictionary<Box, CssConicGradient> BackgroundConicGradientBoxes { get; } = new();

    /// <summary>Phase 4 shadows — element-backed box → its parsed <c>box-shadow</c> layers
    /// (computed, not fetched). The painter emits each OUTSET layer UNDER the background — sharp
    /// as a native filled (rounded) rect, blurred via the Skia raster bridge.</summary>
    public Dictionary<Box, IReadOnlyList<CssBoxShadow>> BoxShadowBoxes { get; } = new();

    /// <summary>Phase 4 shadows — element-backed box → its parsed <c>text-shadow</c> layers (the
    /// box's OWN declared value; inheritance to descendant text is a documented first-cut residual).
    /// The text painter draws the glyph run offset in the shadow color UNDER the main text.</summary>
    public Dictionary<Box, IReadOnlyList<CssTextShadow>> TextShadowBoxes { get; } = new();

    /// <summary>Phase 4 transforms — element-backed box → its parsed 2D <c>transform</c> + resolved
    /// <c>transform-origin</c>. Both the fragment's decoration (FragmentPainter) and its text
    /// (TextPainter) wrap their ops in the matching PDF <c>cm</c> about the origin.</summary>
    internal readonly record struct BoxTransform(CssTransform Transform, TransformOrigin Origin);

    public Dictionary<Box, BoxTransform> TransformBoxes { get; } = new();

    /// <summary>RAW url → resolved URI key for EXTRA (non-box) references — the page margin
    /// boxes' <c>background-image</c> urls (margin-box-bg-image cycle). Only successfully
    /// decoded references appear.</summary>
    public Dictionary<string, string> ExtraImagesByRawUrl { get; } = new(StringComparer.Ordinal);

    public bool TryGet(string absoluteUri, out Entry entry) =>
        _byUri.TryGetValue(absoluteUri, out entry!);

    /// <summary>Look an EXTRA reference's entry up by its RAW url (margin-box-bg-image cycle).</summary>
    public bool TryGetByRawUrl(string rawUrl, out Entry entry)
    {
        entry = null!;
        return ExtraImagesByRawUrl.TryGetValue(rawUrl, out var key) && _byUri.TryGetValue(key, out entry!);
    }

    /// <summary>Register <paramref name="entry"/>'s XObject with <paramref name="document"/>,
    /// memoized — N placements of one image share one XObject (and the document dedups
    /// byte-identical streams across entries).</summary>
    public static PdfIndirectRef GetOrRegister(PdfDocument document, Entry entry) =>
        entry.RegisteredRef ??= document.RegisterImage(entry.XObject);

    /// <summary>Phase 4 filters (PR 2) — register a FILTERED variant of <paramref name="entry"/>'s
    /// image (the source bytes run through <paramref name="steps"/> via
    /// <see cref="NetPdf.Pdf.Images.ImageFilterApplier"/>), memoized by <paramref name="filterKey"/> so
    /// N identical (image + filter) placements share one XObject. Returns <see langword="null"/> when
    /// the image can't be decoded / is over the raster cap (the caller falls back to the unfiltered
    /// image).</summary>
    public static (PdfIndirectRef Ref, NetPdf.Pdf.Images.FilterPadding Pad)? GetOrRegisterFiltered(
        PdfDocument document, Entry entry, IReadOnlyList<NetPdf.Pdf.Images.ImageFilterStep> steps, string filterKey)
    {
        entry.FilteredRefs ??= new Dictionary<string, (PdfIndirectRef, NetPdf.Pdf.Images.FilterPadding)>(StringComparer.Ordinal);
        if (entry.FilteredRefs.TryGetValue(filterKey, out var cached)) return cached;
        var result = NetPdf.Pdf.Images.ImageFilterApplier.TryApply(entry.SourceBytes, steps);
        if (result is null) return null;
        var entryRef = (document.RegisterImage(result.Value.Image), result.Value.Padding);
        entry.FilteredRefs[filterKey] = entryRef;
        return entryRef;
    }

    /// <summary>Walk <paramref name="boxRoot"/> + fetch/decode every image reference. Never
    /// throws for a bad reference — each failure is a diagnostic + the reference is skipped.
    /// <paramref name="extraImageUrls"/> (margin-box-bg-image cycle) carries non-box references
    /// — the page margin boxes' <c>background-image</c> urls, already
    /// <c>PrintBackgrounds</c>-gated by the caller — resolved into
    /// <see cref="ExtraImagesByRawUrl"/>.</summary>
    public static async Task<ImageResourceCache> PrefetchAsync(
        Box boxRoot,
        ResolvedCascadeResult cascade,
        HtmlPdfOptions options,
        IDiagnosticsSink diagnostics,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? extraImageUrls = null)
    {
        var cache = new ImageResourceCache();
        var context = new ResourceFetchContext(
            options.SecurityPolicy, options.BaseUri, cancellationToken);
        var loader = new SafeResourceLoader(options.ResourceLoader, context);
        var unsupportedBackgroundReported = false;

        // Collect references first (sync tree walk), then fetch each unique URI once.
        // background-image references are collected ONLY when backgrounds will paint
        // (PR #166 review P1 — PrintBackgrounds=false must not trigger network loads,
        // data-URI decode cost, diagnostics, or budget consumption for backgrounds the
        // caller explicitly disabled; <img> is content and always fetches).
        var references = new List<(Box Box, string RawUrl, bool IsBackground)>();
        var boxShadowUnsupportedReported = false;
        var textShadowUnsupportedReported = false;
        var transform3DReported = false;
        var transformUnsupportedReported = false;
        var filterElementReported = false;
        CollectReferences(
            boxRoot, cascade, references, cache.BackgroundGradientBoxes,
            cache.BackgroundRadialGradientBoxes, cache.BackgroundConicGradientBoxes,
            cache.BoxShadowBoxes, cache.TextShadowBoxes,
            cache.TransformBoxes,
            collectBackgrounds: options.PrintBackgrounds,
            diagnostics, ref unsupportedBackgroundReported, ref boxShadowUnsupportedReported,
            ref textShadowUnsupportedReported, ref transform3DReported, ref transformUnsupportedReported,
            ref filterElementReported);

        var filterValueReported = false; // Phase 4 filters — once-per-render unparseable-value Warning.
        foreach (var (box, rawUrl, isBackground) in references)
        {
            var key = await ResolveAndFetchAsync(
                cache, loader, rawUrl, options.BaseUri, diagnostics, cancellationToken)
                .ConfigureAwait(false);
            if (key is null) continue;
            if (isBackground)
            {
                // The bg-variants cycle reads the three layout longhands' RAW winners alongside
                // the image (null = unset → the initial); the tiler parses them at paint.
                // AngleSharp.Css (beta) EXPANDS background-repeat/-position into their per-axis
                // longhands (-x/-y), so those two recompose from the axis pair.
                var rules = box.SourceElement is { } el ? cascade.TryGetStylesFor(el) : null;
                cache.BackgroundImageBoxes[box] = new BackgroundSpec(
                    key,
                    ComposeAxisLonghands(rules, "background-repeat", axisInitial: "repeat"),
                    rules?.GetWinner("background-size")?.ResolvedValue,
                    ComposeAxisLonghands(rules, "background-position", axisInitial: "0%"),
                    rules?.GetWinner("background-origin")?.ResolvedValue,
                    rules?.GetWinner("background-clip")?.ResolvedValue);
            }
            else
            {
                // object-fit rides along as the COMPUTED keyword index (object-fit cycle;
                // the registered property's slot — 0 = fill, the initial); object-position as
                // the RAW winner (object-position cycle — unregistered, the documented seam).
                var imgRules = box.SourceElement is { } imgEl ? cascade.TryGetStylesFor(imgEl) : null;
                // filter (Phase 4 PR 2) — the box's OWN declared value (filter doesn't inherit),
                // parsed into the function chain; the painter applies it to the decoded image.
                var filterRaw = imgRules?.GetWinner("filter")?.ResolvedValue;
                CssFilter? filter = null;
                if (!string.IsNullOrWhiteSpace(filterRaw)
                    && !filterRaw.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    filter = CssFilter_Parser.TryParse(filterRaw);
                    // A non-none value that won't parse (url(#id) SVG ref, unknown function, bad arg /
                    // color) is DROPPED — surface it (PR 227 review [P2]) instead of silently ignoring.
                    if (filter is null && !filterValueReported)
                    {
                        diagnostics.Emit(new Diagnostic(
                            DiagnosticCodes.CssFilterUnsupported001,
                            "A CSS filter value on an <img> could not be parsed (a url(#id) SVG-filter "
                            + "reference, an unknown function, or a bad argument / color); the filter was "
                            + "dropped and the image painted unfiltered.",
                            DiagnosticSeverity.Warning));
                        filterValueReported = true;
                    }
                }
                cache.ImageBoxes[box] = new ImgSpec(
                    key,
                    box.Style.ReadKeywordOrDefault(PropertyId.ObjectFit, defaultIndex: 0),
                    imgRules?.GetWinner("object-position")?.ResolvedValue,
                    filter);
            }
        }

        if (extraImageUrls is not null)
        {
            foreach (var rawUrl in extraImageUrls)
            {
                var key = await ResolveAndFetchAsync(
                    cache, loader, rawUrl, options.BaseUri, diagnostics, cancellationToken)
                    .ConfigureAwait(false);
                if (key is not null) cache.ExtraImagesByRawUrl[rawUrl] = key;
            }
        }
        return cache;
    }

    /// <summary>Resolve <paramref name="rawUrl"/> + fetch/decode it once (per-URI memo incl. a
    /// failed-URI sentinel so a repeated bad reference diagnoses once). Returns the cache key,
    /// or <see langword="null"/> on failure (already diagnosed).</summary>
    private static async Task<string?> ResolveAndFetchAsync(
        ImageResourceCache cache, SafeResourceLoader loader, string rawUrl, Uri? baseUri,
        IDiagnosticsSink diagnostics, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolveUri(rawUrl, baseUri, out var uri, out var resolveFailure))
        {
            EmitLoadFailed(diagnostics, rawUrl, resolveFailure);
            return null;
        }
        var key = uri.OriginalString;
        if (!cache._byUri.ContainsKey(key))
        {
            if (cache._failedUris.Contains(key)) return null;
            var entry = await FetchAndDecodeAsync(loader, uri, diagnostics).ConfigureAwait(false);
            if (entry is null)
            {
                cache._failedUris.Add(key);
                return null;
            }
            cache._byUri[key] = entry;
        }
        return key;
    }

    private readonly HashSet<string> _failedUris = new(StringComparer.Ordinal);

    private static void CollectReferences(
        Box box,
        ResolvedCascadeResult cascade,
        List<(Box, string, bool)> references,
        Dictionary<Box, CssLinearGradient> gradientBoxes,
        Dictionary<Box, CssRadialGradient> radialGradientBoxes,
        Dictionary<Box, CssConicGradient> conicGradientBoxes,
        Dictionary<Box, IReadOnlyList<CssBoxShadow>> boxShadowBoxes,
        Dictionary<Box, IReadOnlyList<CssTextShadow>> textShadowBoxes,
        Dictionary<Box, BoxTransform> transformBoxes,
        bool collectBackgrounds,
        IDiagnosticsSink diagnostics,
        ref bool unsupportedBackgroundReported,
        ref bool boxShadowUnsupportedReported,
        ref bool textShadowUnsupportedReported,
        ref bool transform3DReported,
        ref bool transformUnsupportedReported,
        ref bool filterElementReported)
    {
        if (box.SourceElement is { } element)
        {
            // transform (Phase 4) — the box's OWN declared value (transform doesn't inherit),
            // ALWAYS collected (it moves text + decoration, not just backgrounds). A 3D function
            // flattens (CSS-TRANSFORM-3D-UNSUPPORTED-001); an unparseable value paints untransformed
            // (CSS-TRANSFORM-UNSUPPORTED-001).
            var rules = cascade.TryGetStylesFor(element);
            var transformRaw = rules?.GetWinner("transform")?.ResolvedValue;
            if (!string.IsNullOrWhiteSpace(transformRaw)
                && !transformRaw.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                var transform = CssTransform_Parser.TryParse(transformRaw);
                if (transform is not null)
                {
                    if (!transform.IsIdentity)
                    {
                        var origin = CssTransformOrigin_Parser.Parse(rules?.GetWinner("transform-origin")?.ResolvedValue);
                        transformBoxes[box] = new BoxTransform(transform, origin);
                    }
                    if (transform.Had3D) Report(diagnostics, ref transform3DReported,
                        DiagnosticCodes.CssTransform3DUnsupported001,
                        "A 3D transform function was flattened to 2D (rotateX/Y, translateZ, "
                        + "perspective, rotate3d, matrix3d project to identity; translate3d/scale3d "
                        + "keep their 2D part).");
                }
                else
                {
                    Report(diagnostics, ref transformUnsupportedReported,
                        DiagnosticCodes.CssTransformUnsupported001,
                        "A transform value could not be parsed into the supported 2D function set "
                        + "(translate/scale/rotate/skew/matrix + axis variants); the element painted "
                        + "untransformed.");
                }
            }
            // filter (Phase 4 PR 2) — a filter on a REPLACED <img> is applied to the image (the img
            // path below). On a NON-replaced element (div / text box), filtering the rendered subtree
            // needs a Skia subtree renderer NetPdf doesn't have yet, so the element paints UNFILTERED
            // and CSS-FILTER-ELEMENT-UNSUPPORTED-001 surfaces once per render.
            var elementFilterRaw = rules?.GetWinner("filter")?.ResolvedValue;
            var isReplacedImg = box.Kind is BoxKind.BlockReplacedElement or BoxKind.InlineReplacedElement
                && string.Equals(element.LocalName, "img", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(elementFilterRaw)
                && !elementFilterRaw.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)
                && !isReplacedImg)
            {
                Report(diagnostics, ref filterElementReported, DiagnosticCodes.CssFilterElementUnsupported001,
                    "A CSS filter on a non-image element was ignored — filtering a general element's "
                    + "rendered subtree needs a Skia subtree renderer NetPdf doesn't have yet; the "
                    + "element painted unfiltered. Filters on <img> elements ARE applied.");
            }
            // text-shadow (Phase 4 shadows) — the box's OWN declared value, ALWAYS collected
            // (text paints regardless of PrintBackgrounds). A non-zero blur is approximated as a
            // sharp offset (CSS-TEXTSHADOW-UNSUPPORTED-001); an unparseable value surfaces the same
            // code. Inheritance to descendant text is a documented first-cut residual.
            var textShadowRaw = rules?.GetWinner("text-shadow")?.ResolvedValue; // reuse `rules` (Copilot #210)
            if (!string.IsNullOrWhiteSpace(textShadowRaw)
                && !textShadowRaw.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                var textShadows = CssTextShadow_Parser.TryParse(textShadowRaw);
                if (textShadows is not null)
                {
                    textShadowBoxes[box] = textShadows;
                    var hasBlur = false;
                    foreach (var s in textShadows) if (s.BlurPx > 0) { hasBlur = true; break; }
                    if (hasBlur) ReportTextShadowUnsupported(diagnostics, ref textShadowUnsupportedReported);
                }
                else
                {
                    ReportTextShadowUnsupported(diagnostics, ref textShadowUnsupportedReported);
                }
            }
            // box-shadow (Phase 4 shadows) — the raw cascade winner, parsed into layers. Gated by
            // PrintBackgrounds like the other decoration. OUTSET + INSET layers (PR 1 refinements)
            // both store + paint; an unparseable value (e.g. an em/rem/% offset the parser can't
            // resolve) surfaces CSS-BOXSHADOW-UNSUPPORTED-001 once per render.
            if (collectBackgrounds)
            {
                var shadowRaw = rules?.GetWinner("box-shadow")?.ResolvedValue; // reuse `rules` (Copilot #210)
                if (!string.IsNullOrWhiteSpace(shadowRaw)
                    && !shadowRaw.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    var shadows = CssBoxShadow_Parser.TryParse(shadowRaw);
                    if (shadows is not null)
                        boxShadowBoxes[box] = shadows;
                    else
                        ReportBoxShadowUnsupported(diagnostics, ref boxShadowUnsupportedReported);
                }
            }
            // <img src> on a replaced box (inline imgs are skipped by the inline pass today —
            // the atomic-inline deferral — but sizing their slots is harmless and future-proof).
            if (box.Kind is BoxKind.BlockReplacedElement or BoxKind.InlineReplacedElement
                && string.Equals(element.LocalName, "img", StringComparison.OrdinalIgnoreCase)
                && element.GetAttribute("src") is { Length: > 0 } src)
            {
                references.Add((box, src, false));
            }

            // background-image winner from the cascade (raw declared value — the established
            // non-slot read, like border-radius / string-set). `none` is the initial; a single
            // url(...) is the supported form; anything else surfaces once per render. Skipped
            // wholesale under PrintBackgrounds=false (PR #166 review P1) — no fetch, no decode,
            // no diagnostics for backgrounds that will not paint.
            var bgRaw = collectBackgrounds
                ? rules?.GetWinner("background-image")?.ResolvedValue // reuse `rules` (Copilot #210)
                : null;
            if (!string.IsNullOrWhiteSpace(bgRaw)
                && !bgRaw.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseCssUrl(bgRaw, out var bgUrl))
                {
                    references.Add((box, bgUrl, true));
                }
                else if (CssLinearGradient_Parser.TryParse(bgRaw) is { } gradient)
                {
                    // Phase 4 gradients — a linear-gradient(...) needs no fetch; store the
                    // parsed spec for the painter to emit as a PDF native axial shading.
                    gradientBoxes[box] = gradient;
                }
                else if (CssRadialGradient_Parser.TryParse(bgRaw) is { } radial)
                {
                    radialGradientBoxes[box] = radial;
                }
                else if (CssConicGradient_Parser.TryParse(bgRaw) is { } conic)
                {
                    // Phase 4 gradients (PR 1) — conic / repeating-conic has no PDF native shading;
                    // store the parsed spec for the painter to rasterize via Skia (a sweep gradient).
                    conicGradientBoxes[box] = conic;
                }
                else if (!unsupportedBackgroundReported)
                {
                    diagnostics.Emit(new Diagnostic(
                        DiagnosticCodes.CssBackgroundImageUnsupported001,
                        "background-image supports a single url(...), linear-gradient(...), "
                        + "radial-gradient(...), or conic-gradient(...) (incl. repeating-linear / "
                        + "repeating-radial / repeating-conic) this cycle; a multi-layer list or an "
                        + "unrecognized form was ignored (background-color still paints).",
                        DiagnosticSeverity.Warning));
                    unsupportedBackgroundReported = true;
                }
            }
        }
        foreach (var child in box.Children)
            CollectReferences(
                child, cascade, references, gradientBoxes, radialGradientBoxes, conicGradientBoxes,
                boxShadowBoxes, textShadowBoxes, transformBoxes, collectBackgrounds, diagnostics,
                ref unsupportedBackgroundReported, ref boxShadowUnsupportedReported,
                ref textShadowUnsupportedReported, ref transform3DReported, ref transformUnsupportedReported,
                ref filterElementReported);
    }

    /// <summary>Emit <paramref name="code"/> once per render (the <paramref name="reported"/> latch).</summary>
    private static void Report(IDiagnosticsSink diagnostics, ref bool reported, string code, string message)
    {
        if (reported) return;
        diagnostics.Emit(new Diagnostic(code, message, DiagnosticSeverity.Warning));
        reported = true;
    }

    private static void ReportBoxShadowUnsupported(IDiagnosticsSink diagnostics, ref bool reported)
    {
        if (reported) return;
        diagnostics.Emit(new Diagnostic(
            DiagnosticCodes.CssBoxShadowUnsupported001,
            "A box-shadow value was ignored — an offset/blur/spread used a unit the parser can't "
            + "resolve (px + absolute units supported; em/rem/% not), so the whole value was dropped. "
            + "Both outset and inset shadows are otherwise painted (an oversize blur falls back to a "
            + "sharp shadow with this same code).",
            DiagnosticSeverity.Warning));
        reported = true;
    }

    private static void ReportTextShadowUnsupported(IDiagnosticsSink diagnostics, ref bool reported)
    {
        if (reported) return;
        diagnostics.Emit(new Diagnostic(
            DiagnosticCodes.CssTextShadowUnsupported001,
            "A text-shadow was approximated or ignored — a non-zero blur was painted as a sharp "
            + "offset (glyph blur is a tracked follow-up) or an offset/blur used a unit the parser "
            + "can't resolve (px + absolute units supported; em/rem/% not).",
            DiagnosticSeverity.Warning));
        reported = true;
    }

    /// <summary>Parse a CSS <c>url(...)</c> token as ONE COMPLETE token (PR #166 review P2 —
    /// a StartsWith/EndsWith scan let <c>url(a),url(b)</c> through as the single bogus URL
    /// <c>a),url(b</c>): the content is either a QUOTED string (scan to the closing quote — a
    /// quoted URL may contain parens) or an UNQUOTED url-token (scan to the first <c>)</c> —
    /// CSS Syntax §4.3.6 forbids unescaped parens inside an unquoted url-token; commas are
    /// legal, so an unquoted data: URI parses whole). Anything but whitespace AFTER the closing
    /// paren (a second layer, extra tokens) rejects → the caller's unsupported-form path.</summary>
    internal static bool TryParseCssUrl(string raw, out string url)
    {
        url = string.Empty;
        var v = raw.AsSpan().Trim();
        if (v.Length < 5 || !v[..4].Equals("url(", StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = v[4..].TrimStart();
        ReadOnlySpan<char> inner;
        ReadOnlySpan<char> after;
        if (rest.Length > 0 && (rest[0] == '"' || rest[0] == '\''))
        {
            var quote = rest[0];
            var close = rest[1..].IndexOf(quote);
            if (close < 0) return false;                      // unterminated string
            inner = rest[1..(1 + close)];
            var tail = rest[(close + 2)..].TrimStart();
            if (tail.Length == 0 || tail[0] != ')') return false;  // junk between quote and ')'
            after = tail[1..];
        }
        else
        {
            var close = rest.IndexOf(')');
            if (close < 0) return false;                      // unterminated url(
            inner = rest[..close].Trim();
            after = rest[(close + 1)..];
        }
        if (!after.IsWhiteSpace()) return false;              // multi-layer list / extra tokens
        if (inner.IsEmpty) return false;
        url = inner.ToString();
        return true;
    }

    /// <summary>The raw winner of <paramref name="property"/>, or — when AngleSharp.Css (beta)
    /// expanded the authored shorthand into its per-axis longhands
    /// (<c>background-repeat-x</c>/<c>-y</c>, <c>background-position-x</c>/<c>-y</c> — it splits
    /// both) — the recomposed two-value form <c>"&lt;x&gt; &lt;y&gt;"</c>; a missing axis takes
    /// <paramref name="axisInitial"/>. Null when neither form is declared.</summary>
    private static string? ComposeAxisLonghands(
        ResolvedRuleSet? rules, string property, string axisInitial)
    {
        if (rules is null) return null;
        var whole = rules.GetWinner(property)?.ResolvedValue;
        if (!string.IsNullOrWhiteSpace(whole)) return whole;
        var x = rules.GetWinner(property + "-x")?.ResolvedValue;
        var y = rules.GetWinner(property + "-y")?.ResolvedValue;
        if (string.IsNullOrWhiteSpace(x) && string.IsNullOrWhiteSpace(y)) return null;
        return $"{(string.IsNullOrWhiteSpace(x) ? axisInitial : x)} {(string.IsNullOrWhiteSpace(y) ? axisInitial : y)}";
    }

    private static bool TryResolveUri(string rawUrl, Uri? baseUri, out Uri uri, out string failure)
    {
        failure = string.Empty;
        if (Uri.TryCreate(rawUrl, UriKind.Absolute, out uri!)) return true;
        if (baseUri is not null && Uri.TryCreate(baseUri, rawUrl, out uri!)) return true;
        failure = "relative URI and no HtmlPdfOptions.BaseUri to resolve it against";
        uri = null!;
        return false;
    }

    private static async Task<Entry?> FetchAndDecodeAsync(
        SafeResourceLoader loader, Uri uri, IDiagnosticsSink diagnostics)
    {
        var result = await loader.FetchAsync(uri, ResourceKind.Image).ConfigureAwait(false);
        if (!result.Success)
        {
            EmitLoadFailed(diagnostics, Redact(uri), result.Failure?.Reason ?? "unknown failure");
            return null;
        }
        var bytes = result.Response.Content.ToArray();
        try
        {
            return Decode(bytes);
        }
        catch (Exception ex) when (ex is System.IO.InvalidDataException or NotSupportedException
            or ArgumentException or System.IO.EndOfStreamException)
        {
            diagnostics.Emit(new Diagnostic(
                DiagnosticCodes.ImgDecodeFailed001,
                $"Image '{Redact(uri)}' could not be decoded and will not render: {ex.Message} "
                + "(PNG/JPEG decode natively; GIF/WebP/BMP via the SkiaSharp raster fallback.)",
                DiagnosticSeverity.Warning));
            return null;
        }
    }

    /// <summary>Sniff the magic bytes and build the matching XObject. PNG + JPEG take the
    /// dedicated passthrough paths (no decode round-trip); everything else goes through the
    /// SkiaSharp raster fallback (the approved-dependency raster-only role).</summary>
    private static Entry Decode(byte[] bytes)
    {
        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P'
            && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
        {
            var info = PngHeaderParser.Parse(bytes);
            return new Entry
            {
                WidthPx = info.Width,
                HeightPx = info.Height,
                XObject = PngImageXObject.Build(info),
                SourceBytes = bytes,
            };
        }
        if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            var info = JpegHeaderParser.Parse(bytes);
            return new Entry
            {
                WidthPx = info.Width,
                HeightPx = info.Height,
                XObject = new ImageXObjectResult { Image = JpegImageXObject.Build(bytes, info) },
                SourceBytes = bytes,
            };
        }
        var raster = RasterImageDecoder.Decode(bytes);
        return new Entry
        {
            WidthPx = raster.Width,
            HeightPx = raster.Height,
            XObject = RasterImageXObject.Build(raster),
            SourceBytes = bytes,
        };
    }

    private static void EmitLoadFailed(IDiagnosticsSink diagnostics, string reference, string reason) =>
        diagnostics.Emit(new Diagnostic(
            DiagnosticCodes.ResLoadFailed001,
            $"Image resource '{reference}' failed to load and will not render: {reason}",
            DiagnosticSeverity.Warning));

    /// <summary>Diagnostic-safe URI text — a data: URI's payload is huge + attacker-shaped;
    /// show the scheme + a length note instead. Other schemes show as-is (SafeResourceLoader
    /// already sanitizes loader-failure text).</summary>
    private static string Redact(Uri uri) =>
        uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase)
            ? $"data: URI ({uri.OriginalString.Length} chars)"
            : uri.OriginalString;
}
