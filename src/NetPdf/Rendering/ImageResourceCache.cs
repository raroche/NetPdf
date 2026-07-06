// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
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
/// at its declared/attribute size; nothing paints). A <c>linear-gradient(...)</c> /
/// <c>radial-gradient(...)</c> / <c>conic-gradient(...)</c> (incl. the <c>repeating-</c> forms), and a
/// multi-layer (comma-separated) list of url / gradient layers, are SUPPORTED (a gradient is collected
/// as a parsed spec, painted as a PDF native shading — conic via a Skia raster; a multi-layer list as
/// an ordered <see cref="BgLayer"/> list — Phase 4). Only an <c>background-image</c> form still beyond
/// the engine (an unrecognized value, or a multi-layer list with an unparseable layer) surfaces
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

        /// <summary>Phase 4 native SVG — the RAW SVG XML bytes (null for a non-SVG source). Retained so the
        /// opt-in native emitter (<see cref="NetPdf.Svg.SvgNativeEmitter"/>) can draw the SVG as crisp native
        /// PDF path ops at paint time instead of placing the rasterized XObject. Only set for an SVG source.</summary>
        public byte[]? SvgSourceBytes { get; init; }

        /// <summary>Phase 4 filters — per-(filter-key) cache of the filtered XObject registration +
        /// its drop-shadow padding, so N <c>&lt;img&gt;</c>s with the same source + filter share one
        /// filtered XObject.</summary>
        public Dictionary<string, (PdfIndirectRef Ref, NetPdf.Pdf.Images.FilterPadding Pad)>? FilteredRefs;

        /// <summary>Phase 4 mask (PR 4) — per-(mask-URI) cache of the masked XObject registration, so N
        /// <c>&lt;img&gt;</c>s with the same source + mask share one masked XObject.</summary>
        public Dictionary<string, PdfIndirectRef>? MaskedRefs;
    }

    private readonly Dictionary<string, Entry> _byUri = new(StringComparer.Ordinal);

    /// <summary>An <c>&lt;img&gt;</c> box's image reference (img-pipeline + object-fit +
    /// object-position cycles): the resolved URI key + the COMPUTED <c>object-fit</c> keyword
    /// index (the KeywordResolver table order — 0 = <c>fill</c>, the initial; a registered
    /// property since PR #168 review P2) + the RAW <c>object-position</c> winner (null = unset
    /// → the 50% 50% initial; the property stays UNREGISTERED — a 2-component position needs a
    /// new metadata type, so the raw read is the documented seam, like border-radius).</summary>
    internal readonly record struct ImgSpec(
        string UriKey, int ObjectFitKeyword, string? ObjectPositionRaw, CssFilter? Filter = null,
        string? MaskUriKey = null);

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

    /// <summary>Phase 4 gradients — the box-model geometry longhands (RAW winners, null = unset → the
    /// initial) for a SINGLE-LAYER gradient background, parsed by the painter. <c>OriginRaw</c> sets the
    /// positioning area the gradient axis/center/sweep spans (initial padding-box), <c>ClipRaw</c> the
    /// painted/clip area (initial border-box) — matching the <c>url()</c> image path + multi-layer
    /// gradient layers. <c>Size/Position/RepeatRaw</c> are NOT honored for a gradient (the shading fills
    /// the origin box); a non-initial one is diagnosed (<c>CSS-BACKGROUND-IMAGE-UNSUPPORTED-001</c>).</summary>
    internal readonly record struct GradientBgGeometry(
        string? OriginRaw, string? ClipRaw, string? SizeRaw, string? PositionRaw, string? RepeatRaw);

    /// <summary>Phase 4 gradients — element-backed box → its single-layer gradient
    /// <c>background-origin</c>/<c>-clip</c>/<c>-size</c>/<c>-position</c>/<c>-repeat</c> geometry
    /// (parallel to <see cref="BackgroundGradientBoxes"/> / radial / conic; one entry per single-layer
    /// gradient box regardless of gradient type).</summary>
    public Dictionary<Box, GradientBgGeometry> GradientGeometryBoxes { get; } = new();

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

    /// <summary>Phase 4 multi-layer backgrounds — element-backed box → its ORDERED list of background
    /// layers (comma-separated <c>background-image</c>), present ONLY when there are 2+ layers. A
    /// single-layer background uses the per-type dicts above (byte-identical). Layers are stored in SOURCE
    /// order (layer 0 = topmost); the painter draws them BACK-TO-FRONT (last first, CSS B&amp;B §3.10). A box
    /// in this dict is NOT in the single-layer dicts.</summary>
    public Dictionary<Box, List<BgLayer>> MultiLayerBackgroundBoxes { get; } = new();

    internal enum BgLayerKind { Url, Linear, Radial, Conic }

    /// <summary>One resolved background layer (multi-layer path): the kind + the resolved content (a fetched
    /// image <see cref="UriKey"/>, or a parsed gradient) + this layer's OWN position/size/repeat/origin/clip
    /// (cycled from the comma-separated longhand lists). A class so the async fetch can fill in
    /// <see cref="UriKey"/> after collection.</summary>
    internal sealed class BgLayer
    {
        public BgLayerKind Kind;
        public string? RawUrl;      // a Url layer's unresolved url(...) target, fetched after collection
        public string? UriKey;
        public CssLinearGradient? Linear;
        public CssRadialGradient? Radial;
        public CssConicGradient? Conic;
        public string? RepeatRaw;
        public string? SizeRaw;
        public string? PositionRaw;
        public string? OriginRaw;
        public string? ClipRaw;
    }

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

    /// <summary>Phase 4 clip-path (PR 3) — element-backed box → its parsed <c>clip-path</c> basic
    /// shape (the box's OWN declared value; clip-path doesn't inherit). The painter wraps the box's
    /// decoration (+ image) in a native PDF clip; <c>path()</c> + the descendant subtree are
    /// documented residuals.</summary>
    public Dictionary<Box, CssClipPath> ClipPathBoxes { get; } = new();

    /// <summary>Phase 4 border-image (PR 4) — element-backed box → its parsed <c>border-image</c> + the
    /// RESOLVED cache key for the decoded source image. The painter slices the image into the 9 border
    /// regions; only boxes whose source decoded successfully appear.</summary>
    public Dictionary<Box, (CssBorderImage Spec, string UriKey)> BorderImageBoxes { get; } = new();

    /// <summary>Phase 4 mix-blend-mode (PR 4) — element-backed box → its PDF <c>/BM</c> blend-mode name
    /// (e.g. <c>Multiply</c>). The painter wraps the box's decoration in a blend-mode graphics state; only
    /// boxes with a non-<c>normal</c>, recognized mode appear.</summary>
    public Dictionary<Box, string> BlendModeBoxes { get; } = new();

    /// <summary>Phase 4 <c>opacity</c> — box → its EFFECTIVE opacity in [0, 1): the box's own computed value
    /// multiplied by every ancestor's opacity. The painter wraps the box's decoration in a constant-alpha
    /// graphics state (<c>/ca</c>+<c>/CA</c>), and the text pass folds the same alpha into the box's glyph runs.
    /// Only boxes whose effective opacity is STRICTLY below 1 appear (opacity: 1 everywhere is a no-op →
    /// byte-identical). Opacity does not INHERIT as a computed value (CSS Color L4 §12.1), but its rendering
    /// effect fades the whole descendant subtree — so a descendant that declares no opacity of its own still
    /// gets an entry when an ancestor is faded (element-backed AND anonymous boxes both appear). This multiplies
    /// the per-object alpha down the tree, approximating the isolated transparency group (exact for the common
    /// non-self-overlapping case).</summary>
    public Dictionary<Box, double> OpacityBoxes { get; } = new();

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

    /// <summary>Phase 4 mask (PR 4) — register a MASKED variant of <paramref name="entry"/>'s image: its
    /// alpha multiplied by <paramref name="mask"/>'s alpha (<see cref="NetPdf.Pdf.Images.ImageMaskApplier"/>),
    /// memoized by the mask URI key so N identical (image + mask) placements share one XObject. Returns
    /// <see langword="null"/> when either image can't be decoded / is over the raster cap (the caller falls
    /// back to the unmasked image).</summary>
    public static PdfIndirectRef? GetOrRegisterMasked(PdfDocument document, Entry entry, Entry mask, string maskKey)
    {
        entry.MaskedRefs ??= new Dictionary<string, PdfIndirectRef>(StringComparer.Ordinal);
        if (entry.MaskedRefs.TryGetValue(maskKey, out var cached)) return cached;
        var result = NetPdf.Pdf.Images.ImageMaskApplier.TryApply(entry.SourceBytes, mask.SourceBytes);
        if (result is null) return null;
        var entryRef = document.RegisterImage(result);
        entry.MaskedRefs[maskKey] = entryRef;
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
        var borderImages = new List<(Box Box, CssBorderImage Spec)>(); // Phase 4 border-image (PR 4)
        var boxShadowUnsupportedReported = false;
        var textShadowUnsupportedReported = false;
        var textShadowBlurRasterReported = false;
        var transform3DReported = false;
        var transformUnsupportedReported = false;
        var filterElementReported = false;
        var clipPathUnsupportedReported = false;
        var maskElementReported = false;
        var borderImageReported = false;
        var opacityApproximatedReported = false;
        CollectReferences(
            boxRoot, cascade, references, cache.BackgroundGradientBoxes,
            cache.BackgroundRadialGradientBoxes, cache.BackgroundConicGradientBoxes,
            cache.GradientGeometryBoxes,
            cache.MultiLayerBackgroundBoxes,
            cache.BoxShadowBoxes, cache.TextShadowBoxes,
            cache.TransformBoxes, cache.ClipPathBoxes,
            collectBackgrounds: options.PrintBackgrounds,
            diagnostics, borderImages, cache.BlendModeBoxes, cache.OpacityBoxes,
            ref unsupportedBackgroundReported, ref boxShadowUnsupportedReported,
            ref textShadowUnsupportedReported, ref textShadowBlurRasterReported,
            inheritedTextShadow: null,
            inheritedOpacity: 1.0,
            inheritedComputedOpacity: 1.0,
            ref transform3DReported, ref transformUnsupportedReported,
            ref filterElementReported, ref clipPathUnsupportedReported, ref maskElementReported,
            ref borderImageReported, ref opacityApproximatedReported);

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
                // mask / mask-image (Phase 4 PR 4) — on an <img>, a url() mask source is fetched + applied
                // as an alpha mask (the raster path). mask-image wins over the mask shorthand. A non-url
                // value (gradient / none) → no mask; a general element's mask is handled in CollectReferences.
                // The `mask` shorthand is expanded into `mask-image` by the preprocessor (source order
                // respected; PR-229 review [P2]), so reading the mask-image winner alone is correct.
                var maskRaw = imgRules?.GetWinner("mask-image")?.ResolvedValue;
                string? maskKey = null;
                if (ExtractFirstUrl(maskRaw) is { } maskUrl)
                    maskKey = await ResolveAndFetchAsync(
                        cache, loader, maskUrl, options.BaseUri, diagnostics, cancellationToken)
                        .ConfigureAwait(false);
                cache.ImageBoxes[box] = new ImgSpec(
                    key,
                    box.Style.ReadKeywordOrDefault(PropertyId.ObjectFit, defaultIndex: 0),
                    imgRules?.GetWinner("object-position")?.ResolvedValue,
                    filter, maskKey);
            }
        }

        // Phase 4 border-image (PR 4) — resolve + decode each border-image source; store the spec + key
        // for the painter. A failed fetch is already diagnosed by ResolveAndFetchAsync (the box simply
        // gets no border-image; its normal border, if any, paints).
        foreach (var (box, spec) in borderImages)
        {
            var key = await ResolveAndFetchAsync(
                cache, loader, spec.SourceUrl, options.BaseUri, diagnostics, cancellationToken)
                .ConfigureAwait(false);
            if (key is not null) cache.BorderImageBoxes[box] = (spec, key);
        }

        // Phase 4 multi-layer backgrounds — fetch each url() layer's image (gradient layers need no fetch).
        // A failed fetch leaves UriKey null → the painter skips that layer (its diagnostic already fired).
        foreach (var (_, list) in cache.MultiLayerBackgroundBoxes)
            foreach (var layer in list)
                if (layer is { Kind: BgLayerKind.Url, RawUrl: { } layerUrl })
                    layer.UriKey = await ResolveAndFetchAsync(
                        cache, loader, layerUrl, options.BaseUri, diagnostics, cancellationToken)
                        .ConfigureAwait(false);

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

        // Inline <svg> elements (RC3) — serialize each inline SVG's own markup + render it through the
        // SAME SVG pipeline as an <img> SVG source (SvgRasterizer + the security guards). No fetch: the
        // element's markup IS the content. Runs after the URL passes so a data-URI <img src=svg> is
        // unaffected.
        RegisterInlineSvgs(boxRoot, cache, diagnostics);
        return cache;
    }

    /// <summary>Walk the box tree for replaced inline <c>&lt;svg&gt;</c> boxes, serialize each element's
    /// own markup, and decode it through the SVG pipeline (identical to an <c>&lt;img&gt;</c> SVG source),
    /// registering an <see cref="Entry"/> keyed by content so identical SVGs dedup. A parse failure is
    /// diagnosed (SVG-PARSE-FAILED-001) and the box simply paints nothing.</summary>
    private static void RegisterInlineSvgs(Box box, ImageResourceCache cache, IDiagnosticsSink diagnostics)
    {
        if (box.Kind is BoxKind.BlockReplacedElement or BoxKind.InlineReplacedElement
            && box.SourceElement is { } element
            && string.Equals(element.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
        {
            var markup = element.OuterHtml;
            if (!string.IsNullOrWhiteSpace(markup))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(markup);
                // Content key — identical inline SVGs (repeated logos) share one rendered XObject.
                var key = "inline-svg:" + Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(bytes));
                if (!cache._byUri.ContainsKey(key) && !cache._failedUris.Contains(key))
                {
                    try
                    {
                        cache._byUri[key] = Decode(bytes, diagnostics);
                    }
                    catch (SvgImageParseException ex)
                    {
                        diagnostics.Emit(new Diagnostic(
                            DiagnosticCodes.SvgParseFailed001,
                            ex.Status == NetPdf.Svg.SvgParseStatus.Blocked
                                ? "An inline <svg> was rejected by the parser's security guards (a prohibited "
                                  + "<!DOCTYPE / <!ENTITY, or an over-size document). It was not rendered."
                                : "An inline <svg> could not be parsed as well-formed XML. It was not rendered.",
                            DiagnosticSeverity.Warning));
                        cache._failedUris.Add(key);
                    }
                }
                if (cache._byUri.ContainsKey(key))
                {
                    cache.ImageBoxes[box] = new ImgSpec(
                        key,
                        box.Style.ReadKeywordOrDefault(PropertyId.ObjectFit, defaultIndex: 0),
                        ObjectPositionRaw: null);
                }
            }
        }

        foreach (var child in box.Children)
            RegisterInlineSvgs(child, cache, diagnostics);
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
        Dictionary<Box, GradientBgGeometry> gradientGeometryBoxes,
        Dictionary<Box, List<BgLayer>> multiLayerBoxes,
        Dictionary<Box, IReadOnlyList<CssBoxShadow>> boxShadowBoxes,
        Dictionary<Box, IReadOnlyList<CssTextShadow>> textShadowBoxes,
        Dictionary<Box, BoxTransform> transformBoxes,
        Dictionary<Box, CssClipPath> clipPathBoxes,
        bool collectBackgrounds,
        IDiagnosticsSink diagnostics,
        List<(Box Box, CssBorderImage Spec)> borderImages,
        Dictionary<Box, string> blendModeBoxes,
        Dictionary<Box, double> opacityBoxes,
        ref bool unsupportedBackgroundReported,
        ref bool boxShadowUnsupportedReported,
        ref bool textShadowUnsupportedReported,
        ref bool textShadowBlurRasterReported,
        // text-shadow is inherited — the nearest ancestor's effective list (null = none), threaded down
        // the recursion so a descendant box without its own value inherits it. Passed explicitly at both
        // call sites (root = null; children = the box's effective list).
        IReadOnlyList<CssTextShadow>? inheritedTextShadow,
        // opacity's rendering effect fades the whole subtree, so the PRODUCT of every ancestor's opacity is
        // threaded down (root = 1.0; children = this box's effective opacity). opacity doesn't INHERIT as a
        // computed value, but a faded group fades everything painted under it — see the effective store below.
        double inheritedOpacity,
        // The parent element's COMPUTED opacity (root = 1.0), used to resolve a child's `opacity: inherit`.
        // Distinct from inheritedOpacity: this is the parent's own value, NOT the multiplied subtree alpha.
        double inheritedComputedOpacity,
        ref bool transform3DReported,
        ref bool transformUnsupportedReported,
        ref bool filterElementReported,
        ref bool clipPathUnsupportedReported,
        ref bool maskElementReported,
        ref bool borderImageReported,
        ref bool opacityApproximatedReported)
    {
        // text-shadow inherits through this box (incl. anonymous boxes with no SourceElement) — default to
        // the ancestor's value; an element-backed box may REPLACE it below before passing it to children.
        var effectiveTextShadow = inheritedTextShadow;
        // This box's OWN opacity (default 1.0 = the initial + every anonymous box). Set from the element's
        // computed value below; multiplied with the ancestor product to get the effective subtree alpha.
        var ownOpacity = 1.0;
        // The COMPUTED opacity threaded to children to resolve their `opacity: inherit` (the parent's computed
        // value, NOT the multiplied subtree alpha). An anonymous box has no opacity of its own → it passes the
        // ancestor's computed opacity straight through, since CSS inherits element-to-element.
        var computedOpacityForChildren = inheritedComputedOpacity;
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
                // Font context for em/rem in the transform / transform-origin lengths (recovered raw, so the
                // cascade hasn't resolved their units): em = this box's font-size, rem = the root element's.
                // A translate % is resolved later against the box border-box (TransformResolver / ToPdfMatrix).
                var emPx = box.Style.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16);
                var remPx = RootFontSizePx(box);
                var transform = CssTransform_Parser.TryParse(transformRaw, emPx, remPx);
                if (transform is not null)
                {
                    if (!transform.IsIdentity)
                    {
                        var origin = CssTransformOrigin_Parser.Parse(rules?.GetWinner("transform-origin")?.ResolvedValue, emPx, remPx);
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
            // clip-path (Phase 4 PR 3) — the box's OWN declared basic shape (clip-path doesn't inherit).
            // The painter clips the box decoration (+ image) to it. A non-none value the parser CAN'T
            // turn into a supported basic shape (url(#…), <geometry-box>, em/rem, malformed) surfaces
            // CSS-CLIP-PATH-UNSUPPORTED-001 once per render — never a silent unclipped paint. Always
            // collected (it clips text + image too, not just backgrounds).
            var clipPathRaw = rules?.GetWinner("clip-path")?.ResolvedValue;
            if (!string.IsNullOrWhiteSpace(clipPathRaw)
                && !clipPathRaw.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                if (CssClipPath_Parser.TryParse(clipPathRaw) is { } clip)
                    clipPathBoxes[box] = clip;
                else
                    Report(diagnostics, ref clipPathUnsupportedReported, DiagnosticCodes.CssClipPathUnsupported001,
                        "A clip-path value could not be applied — it is a url(#…) SVG reference, a "
                        + "<geometry-box> keyword, a font-relative (em/rem) length, or malformed basic-shape "
                        + "syntax. The element painted unclipped.");
            }
            // border-image (Phase 4 PR 4) — NOT gated by PrintBackgrounds (it paints the BORDER area, which
            // renders regardless, like normal borders; PR-229 review [P2]). Read the LONGHANDS — the
            // `border-image` SHORTHAND is expanded into them by the preprocessor, so the cascade resolves
            // shorthand-vs-longhand by source order (PR-229 [P2]). A url() source resolves → queued for fetch
            // (the painter slices it into the 9 border regions).
            var biSource = rules?.GetWinner("border-image-source")?.ResolvedValue;
            if (!string.IsNullOrWhiteSpace(biSource)
                && !biSource.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                var biSlice = rules?.GetWinner("border-image-slice")?.ResolvedValue;
                var biRepeat = rules?.GetWinner("border-image-repeat")?.ResolvedValue;
                var biWidth = rules?.GetWinner("border-image-width")?.ResolvedValue;
                var biOutset = rules?.GetWinner("border-image-outset")?.ResolvedValue;
                // Edge tiling (repeat/round/space) + border-image-width + -outset are now honored by the
                // painter (border-image completion PR), so the prior "approximated sub-feature" diagnostic
                // is gone; only a non-url() source stays unsupported (below).
                var biSpec = CssBorderImage_Parser.TryParse(biSource, biSlice, biRepeat, biWidth, biOutset);
                if (biSpec is not null)
                {
                    borderImages.Add((box, biSpec));
                }
                else if (CssBorderImage_Parser.IsUnsupportedSource(biSource))
                {
                    Report(diagnostics, ref borderImageReported, DiagnosticCodes.CssBorderImageUnsupported001,
                        "A border-image-source that is not a url() (e.g. a gradient) is not supported yet; the "
                        + "border-image was not painted.");
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
            // mask / mask-image (Phase 4 PR 4) — applied to an <img> (the img path resolves the mask + the
            // painter composites it). On a NON-image element, masking the rendered subtree needs the same
            // Skia subtree renderer, so the element paints UNMASKED + CSS-MASK-ELEMENT-UNSUPPORTED-001 once.
            var elementMaskRaw = rules?.GetWinner("mask-image")?.ResolvedValue; // `mask` shorthand → mask-image (preprocessor)
            if (!string.IsNullOrWhiteSpace(elementMaskRaw)
                && !elementMaskRaw.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)
                && !isReplacedImg)
            {
                Report(diagnostics, ref maskElementReported, DiagnosticCodes.CssMaskElementUnsupported001,
                    "A CSS mask on a non-image element was ignored — masking a general element's rendered "
                    + "subtree needs a Skia subtree renderer NetPdf doesn't have yet; the element painted "
                    + "unmasked. Masks on <img> elements ARE applied.");
            }
            // mix-blend-mode (Phase 4 PR 4) — the box's OWN declared value (doesn't inherit) → a PDF /BM
            // blend-mode name. The painter wraps the box's decoration in that blend mode. `normal` (the
            // initial) + an unrecognized keyword → no entry (composite normally).
            if (PdfBlendModeName(rules?.GetWinner("mix-blend-mode")?.ResolvedValue) is { } bmName)
                blendModeBoxes[box] = bmName;
            // opacity (Phase 4) — the box's OWN computed value. opacity does NOT inherit (CSS Color L4 §12.1),
            // but the CSS-wide keywords resolve explicitly (`inherit` → the parent's computed opacity,
            // `initial`/`unset`/`revert`/`revert-layer` → 1) via ResolveOpacity. Only a value strictly below 1
            // wraps the box in a constant /ca+/CA alpha (opacity: 1, the initial, is a no-op → the decoration +
            // text pass stay byte-identical). This is a PER-OBJECT alpha, not a true isolated transparency
            // GROUP — so a box whose own painting self-overlaps (e.g. a translucent border over a translucent
            // background of the same box) composites twice. That gap is diagnosed once per render (never a
            // silent wrong composite); the faithful group needs a transparency-group Form XObject (the deferred
            // IPaintTarget epic). The resolved value is threaded to children for their `opacity: inherit`.
            ownOpacity = ResolveOpacity(rules?.GetWinner("opacity")?.ResolvedValue, inheritedComputedOpacity);
            computedOpacityForChildren = ownOpacity;
            if (ownOpacity < 1.0 && !opacityApproximatedReported)
            {
                diagnostics.Emit(new Diagnostic(
                    DiagnosticCodes.CssOpacityGroupApproximated001,
                    "An element with opacity < 1 was composited with a constant alpha applied per painting "
                    + "operation, not as an isolated transparency group. This matches CSS for a "
                    + "non-self-overlapping element (the common case); an element whose own background, "
                    + "border, and text overlap will composite slightly differently than group opacity. "
                    + "Faithful group isolation needs a transparency-group Form XObject.",
                    DiagnosticSeverity.Info));
                opacityApproximatedReported = true;
            }
            // text-shadow (Phase 4 shadows) — an INHERITED property (CSS Text Decoration L3 §3): a box's
            // OWN declared value REPLACES the inherited one, else it inherits the ancestor's. The CSS-wide
            // keywords resolve explicitly (the cascade leaves them as the literal token here): `none` /
            // `initial` → no shadow (the initial value is none); `inherit` / `unset` → the inherited value
            // (text-shadow is inherited, so `unset` = inherit); `revert` / `revert-layer` → also the
            // inherited value (NetPdf has no UA text-shadow rule, so reverting an inherited property yields
            // the parent's value). The effective list is collected for this box's text + threaded to
            // children. Always collected (text paints regardless of PrintBackgrounds). A non-zero blur is
            // RENDERED via the Skia glyph-blur raster (CSS-TEXTSHADOW-BLUR-RASTER-001, Info); an unparseable
            // own value surfaces CSS-TEXTSHADOW-UNSUPPORTED-001 (once) and keeps the inherited value.
            // Diagnostics fire only for a box's OWN value (an inherited value was diagnosed at the ancestor).
            var textShadowRaw = rules?.GetWinner("text-shadow")?.ResolvedValue; // reuse `rules` (Copilot #210)
            if (!string.IsNullOrWhiteSpace(textShadowRaw))
            {
                var trimmed = textShadowRaw.Trim();
                if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Equals("initial", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveTextShadow = null; // own `none` / `initial` → no shadow (children inherit none)
                }
                else if (trimmed.Equals("inherit", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Equals("unset", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Equals("revert", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Equals("revert-layer", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveTextShadow = inheritedTextShadow; // explicit inherit (no warning)
                }
                else if (CssTextShadow_Parser.TryParse(textShadowRaw) is { } own)
                {
                    effectiveTextShadow = own;
                    var hasBlur = false;
                    foreach (var s in own) if (s.BlurPx > 0) { hasBlur = true; break; }
                    if (hasBlur) ReportTextShadowBlurRaster(diagnostics, ref textShadowBlurRasterReported);
                }
                else
                {
                    // An invalid own value is ignored → the property keeps the inherited value.
                    ReportTextShadowUnsupported(diagnostics, ref textShadowUnsupportedReported);
                }
            }
            if (effectiveTextShadow is not null) textShadowBoxes[box] = effectiveTextShadow;
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
                // Multi-layer: a comma-separated background-image list (paren-aware, so a gradient's own
                // commas don't split it). A SINGLE layer goes through the existing single-layer dispatch
                // below — byte-identical. 2+ layers build an ordered BgLayer list (painted back-to-front).
                var layers = CssLinearGradient_Parser.SplitTopLevelCommas(bgRaw);
                if (layers.Count > 1
                    && BuildMultiLayerBackground(layers, rules) is { } multi)
                {
                    multiLayerBoxes[box] = multi;
                }
                else if (layers.Count > 1)
                {
                    // A multi-layer list with an unparseable layer → unsupported (background-color paints).
                    if (!unsupportedBackgroundReported)
                    {
                        diagnostics.Emit(new Diagnostic(
                            DiagnosticCodes.CssBackgroundImageUnsupported001,
                            "A multi-layer background-image list contained a layer outside the supported set "
                            + "(url(...) / linear / radial / conic gradient); the list was ignored "
                            + "(background-color still paints).",
                            DiagnosticSeverity.Warning));
                        unsupportedBackgroundReported = true;
                    }
                }
                else if (TryParseCssUrl(bgRaw, out var bgUrl))
                {
                    references.Add((box, bgUrl, true));
                }
                else if (CssLinearGradient_Parser.TryParse(bgRaw) is { } gradient)
                {
                    // Phase 4 gradients — a linear-gradient(...) needs no fetch; store the
                    // parsed spec for the painter to emit as a PDF native axial shading.
                    gradientBoxes[box] = gradient;
                    gradientGeometryBoxes[box] = ReadGradientGeometry(rules);
                }
                else if (CssRadialGradient_Parser.TryParse(bgRaw) is { } radial)
                {
                    radialGradientBoxes[box] = radial;
                    gradientGeometryBoxes[box] = ReadGradientGeometry(rules);
                }
                else if (CssConicGradient_Parser.TryParse(bgRaw) is { } conic)
                {
                    // Phase 4 gradients (PR 1) — conic / repeating-conic has no PDF native shading;
                    // store the parsed spec for the painter to rasterize via Skia (a sweep gradient).
                    conicGradientBoxes[box] = conic;
                    gradientGeometryBoxes[box] = ReadGradientGeometry(rules);
                }
                else if (!unsupportedBackgroundReported)
                {
                    diagnostics.Emit(new Diagnostic(
                        DiagnosticCodes.CssBackgroundImageUnsupported001,
                        "background-image supports a single url(...), linear-gradient(...), "
                        + "radial-gradient(...), or conic-gradient(...) (incl. repeating-linear / "
                        + "repeating-radial / repeating-conic) this cycle; an "
                        + "unrecognized form was ignored (background-color still paints).",
                        DiagnosticSeverity.Warning));
                    unsupportedBackgroundReported = true;
                }
            }
        }
        // The effective subtree alpha = this box's own opacity × the ancestor product. opacity's rendering
        // effect applies to the element AND its whole descendant subtree, so we store the EFFECTIVE value on
        // every box (element-backed OR anonymous) whose effective alpha is < 1 — a descendant that declares no
        // opacity of its own still gets a faded entry from a faded ancestor. The painters look this up by
        // fragment box, so the box's decoration / image / text all fade. Multiplying the per-object alpha down
        // the tree approximates the isolated transparency group (exact for the common non-self-overlapping
        // case), consistent with the per-object diagnostic emitted once at the declaring box.
        var effectiveOpacity = inheritedOpacity * ownOpacity;
        if (effectiveOpacity < 1.0) opacityBoxes[box] = effectiveOpacity;
        foreach (var child in box.Children)
            CollectReferences(
                child, cascade, references, gradientBoxes, radialGradientBoxes, conicGradientBoxes,
                gradientGeometryBoxes,
                multiLayerBoxes,
                boxShadowBoxes, textShadowBoxes, transformBoxes, clipPathBoxes, collectBackgrounds, diagnostics,
                borderImages, blendModeBoxes, opacityBoxes,
                ref unsupportedBackgroundReported, ref boxShadowUnsupportedReported,
                ref textShadowUnsupportedReported, ref textShadowBlurRasterReported,
                effectiveTextShadow,
                effectiveOpacity,
                computedOpacityForChildren,
                ref transform3DReported, ref transformUnsupportedReported,
                ref filterElementReported, ref clipPathUnsupportedReported, ref maskElementReported,
                ref borderImageReported, ref opacityApproximatedReported);
    }

    /// <summary>Extract the first <c>url(...)</c> target from a CSS value (mask-image / mask shorthand),
    /// stripping quotes. Returns <see langword="null"/> when there's no url() (a gradient / <c>none</c> /
    /// keyword value).</summary>
    private static string? ExtractFirstUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var idx = value.IndexOf("url(", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var open = idx + 4;
        var close = value.IndexOf(')', open);
        if (close < 0) return null;
        var inner = value.Substring(open, close - open).Trim().Trim('"', '\'');
        return inner.Length == 0 ? null : inner;
    }

    /// <summary>Map a CSS <c>mix-blend-mode</c> keyword (CSS Compositing &amp; Blending L1) to its PDF
    /// <c>/BM</c> blend-mode name (ISO 32000-2 §11.3.5). <c>normal</c> (the initial), <c>plus-lighter</c>
    /// (no PDF equivalent), and any unrecognized value → <see langword="null"/> (composite normally).</summary>
    private static string? PdfBlendModeName(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "multiply" => "Multiply",
        "screen" => "Screen",
        "overlay" => "Overlay",
        "darken" => "Darken",
        "lighten" => "Lighten",
        "color-dodge" => "ColorDodge",
        "color-burn" => "ColorBurn",
        "hard-light" => "HardLight",
        "soft-light" => "SoftLight",
        "difference" => "Difference",
        "exclusion" => "Exclusion",
        "hue" => "Hue",
        "saturation" => "Saturation",
        "color" => "Color",
        "luminosity" => "Luminosity",
        _ => null,
    };

    /// <summary>Parse a CSS <c>opacity</c> value — a <c>&lt;number&gt;</c> or a <c>&lt;percentage&gt;</c>
    /// (CSS Color L4 §12.1) — to a fraction, CLAMPED to [0, 1] (values outside the range are allowed but
    /// clamped for compositing). Returns <see langword="null"/> for a missing/blank/unparseable value or a
    /// CSS-wide keyword (<c>inherit</c>/<c>initial</c>/…), so the caller treats it as the initial (opaque).</summary>
    private static double? ParseOpacity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        var isPercent = v.EndsWith('%');
        var num = isPercent ? v[..^1].Trim() : v;
        if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return null;
        if (isPercent) parsed /= 100.0;
        if (!double.IsFinite(parsed)) return null;
        return Math.Clamp(parsed, 0.0, 1.0);
    }

    /// <summary>Resolve a box's OWN computed <c>opacity</c> from the cascade winner, in [0, 1]. opacity is NOT
    /// an inherited property, but the CSS-wide keywords (the cascade leaves them as the literal token for a
    /// non-inherited property) resolve locally until a central cascade interceptor lands: <c>inherit</c> → the
    /// parent's computed opacity (<paramref name="inheritedComputedOpacity"/>); <c>initial</c> / <c>unset</c> →
    /// 1 (opacity's initial is 1, and <c>unset</c> on a non-inherited property = initial); <c>revert</c> /
    /// <c>revert-layer</c> → 1 (NetPdf has no UA opacity rule). A <c>&lt;number&gt;</c>/<c>&lt;percentage&gt;</c>
    /// parses + clamps; a missing/unparseable value → 1 (opaque).</summary>
    private static double ResolveOpacity(string? value, double inheritedComputedOpacity)
    {
        if (string.IsNullOrWhiteSpace(value)) return 1.0;
        var v = value.Trim();
        if (v.Equals("inherit", StringComparison.OrdinalIgnoreCase)) return inheritedComputedOpacity;
        if (v.Equals("initial", StringComparison.OrdinalIgnoreCase)
            || v.Equals("unset", StringComparison.OrdinalIgnoreCase)
            || v.Equals("revert", StringComparison.OrdinalIgnoreCase)
            || v.Equals("revert-layer", StringComparison.OrdinalIgnoreCase))
            return 1.0;
        return ParseOpacity(value) ?? 1.0; // number / percentage (clamped); unparseable → opaque
    }

    /// <summary>Emit <paramref name="code"/> once per render (the <paramref name="reported"/> latch).</summary>
    private static void Report(IDiagnosticsSink diagnostics, ref bool reported, string code, string message)
    {
        if (reported) return;
        diagnostics.Emit(new Diagnostic(code, message, DiagnosticSeverity.Warning));
        reported = true;
    }

    /// <summary>The CSS <c>rem</c> basis — the root element's computed font-size (CSS Values §5.1.1).
    /// Walks to the topmost element-backed ancestor (the <c>&lt;html&gt;</c> box, above the synthetic
    /// document root which has no source element); defaults to 16px.</summary>
    private static double RootFontSizePx(Box box)
    {
        var root = box;
        for (Box? b = box; b is not null; b = b.Parent)
            if (b.SourceElement is not null) root = b;
        return root.Style.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16);
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
            "A text-shadow was ignored — an offset/blur used a unit the parser can't resolve "
            + "(px + absolute units supported; em/rem/% not), so the whole value was dropped.",
            DiagnosticSeverity.Warning));
        reported = true;
    }

    private static void ReportTextShadowBlurRaster(IDiagnosticsSink diagnostics, ref bool reported)
    {
        if (reported) return;
        diagnostics.Emit(new Diagnostic(
            DiagnosticCodes.CssTextShadowBlurRaster001,
            "A blurred text-shadow was routed to the Skia raster fallback (PDF has no native Gaussian "
            + "blur); the run's glyph outlines are rasterized at 2× and placed as an image XObject with "
            + "an alpha /SMask. An over-cap run (or a font Skia can't read) falls back to a sharp offset; "
            + "a fully transparent layer is skipped.",
            DiagnosticSeverity.Info));
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

    /// <summary>Read a SINGLE-LAYER gradient's box-model geometry longhands from the cascade — the same
    /// raw winners the <c>url()</c> image path + multi-layer gradient layers read. <c>-origin</c>/
    /// <c>-clip</c> are simple winners; <c>-position</c>/<c>-repeat</c> recompose from the per-axis
    /// longhands AngleSharp expands. Null winners = unset → the painter applies the initial.</summary>
    private static GradientBgGeometry ReadGradientGeometry(ResolvedRuleSet? rules) => new(
        OriginRaw: rules?.GetWinner("background-origin")?.ResolvedValue,
        ClipRaw: rules?.GetWinner("background-clip")?.ResolvedValue,
        SizeRaw: rules?.GetWinner("background-size")?.ResolvedValue,
        PositionRaw: ComposeAxisLonghands(rules, "background-position", axisInitial: "0%"),
        RepeatRaw: ComposeAxisLonghands(rules, "background-repeat", axisInitial: "repeat"));

    /// <summary>Build the ordered <see cref="BgLayer"/> list for a multi-layer <c>background-image</c>
    /// (2+ comma-separated layers, source order = topmost first). Each layer is parsed as
    /// url / linear / radial / conic (a <c>none</c> layer is a no-op slot, preserving longhand alignment);
    /// the per-layer <c>background-position</c>/<c>-size</c>/<c>-repeat</c>/<c>-origin</c>/<c>-clip</c> are
    /// taken from the comma-separated longhand lists (cycled — CSS B&amp;B §3.10). Returns
    /// <see langword="null"/> when any layer is outside the supported set.</summary>
    private static List<BgLayer>? BuildMultiLayerBackground(List<string> layers, ResolvedRuleSet? rules)
    {
        var n = layers.Count;
        var positions = LayerAxisLonghand(rules, "background-position", n, "0%");
        var repeats = LayerAxisLonghand(rules, "background-repeat", n, "repeat");
        var sizes = LayerLonghand(rules, "background-size", n);
        var origins = LayerLonghand(rules, "background-origin", n);
        var clips = LayerLonghand(rules, "background-clip", n);

        var list = new List<BgLayer>(n);
        for (var i = 0; i < n; i++)
        {
            var raw = layers[i];
            var layer = new BgLayer
            {
                PositionRaw = positions[i], SizeRaw = sizes[i], RepeatRaw = repeats[i],
                OriginRaw = origins[i], ClipRaw = clips[i],
            };
            if (raw.Equals("none", StringComparison.OrdinalIgnoreCase)) { layer.Kind = BgLayerKind.Url; } // no-op slot
            else if (TryParseCssUrl(raw, out var url)) { layer.Kind = BgLayerKind.Url; layer.RawUrl = url; }
            else if (CssLinearGradient_Parser.TryParse(raw) is { } lin) { layer.Kind = BgLayerKind.Linear; layer.Linear = lin; }
            else if (CssRadialGradient_Parser.TryParse(raw) is { } rad) { layer.Kind = BgLayerKind.Radial; layer.Radial = rad; }
            else if (CssConicGradient_Parser.TryParse(raw) is { } con) { layer.Kind = BgLayerKind.Conic; layer.Conic = con; }
            else return null; // an unsupported layer fails the whole list
            list.Add(layer);
        }
        return list;
    }

    /// <summary>Per-layer values of a non-axis longhand (<c>background-size</c>/<c>-origin</c>/<c>-clip</c>):
    /// split the winner on top-level commas + cycle to <paramref name="count"/> (null when unset).</summary>
    private static string?[] LayerLonghand(ResolvedRuleSet? rules, string property, int count)
    {
        var result = new string?[count];
        var whole = rules?.GetWinner(property)?.ResolvedValue;
        if (string.IsNullOrWhiteSpace(whole)) return result;
        var parts = CssLinearGradient_Parser.SplitTopLevelCommas(whole);
        for (var i = 0; i < count; i++) result[i] = parts[i % parts.Count];
        return result;
    }

    /// <summary>Per-layer values of an axis longhand (<c>background-position</c>/<c>-repeat</c>, which
    /// AngleSharp may expand to <c>-x</c>/<c>-y</c> comma lists): recompose each layer's
    /// <c>"&lt;x&gt; &lt;y&gt;"</c> from the cycled lists (a missing axis takes
    /// <paramref name="axisInitial"/>). Falls back to the un-expanded whole winner when present.</summary>
    private static string?[] LayerAxisLonghand(ResolvedRuleSet? rules, string property, int count, string axisInitial)
    {
        var result = new string?[count];
        var whole = rules?.GetWinner(property)?.ResolvedValue;
        if (!string.IsNullOrWhiteSpace(whole))
        {
            var parts = CssLinearGradient_Parser.SplitTopLevelCommas(whole);
            for (var i = 0; i < count; i++) result[i] = parts[i % parts.Count];
            return result;
        }
        var xRaw = rules?.GetWinner(property + "-x")?.ResolvedValue;
        var yRaw = rules?.GetWinner(property + "-y")?.ResolvedValue;
        if (string.IsNullOrWhiteSpace(xRaw) && string.IsNullOrWhiteSpace(yRaw)) return result;
        var xs = string.IsNullOrWhiteSpace(xRaw) ? null : CssLinearGradient_Parser.SplitTopLevelCommas(xRaw);
        var ys = string.IsNullOrWhiteSpace(yRaw) ? null : CssLinearGradient_Parser.SplitTopLevelCommas(yRaw);
        for (var i = 0; i < count; i++)
        {
            var x = xs is { Count: > 0 } ? xs[i % xs.Count] : axisInitial;
            var y = ys is { Count: > 0 } ? ys[i % ys.Count] : axisInitial;
            result[i] = $"{x} {y}";
        }
        return result;
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
            return Decode(bytes, diagnostics);
        }
        catch (SvgImageParseException ex)
        {
            // SEC-8 — a specific SVG parse-failure diagnostic (blocked-by-guard vs malformed), instead of
            // the generic IMG-DECODE-FAILED the raster fallback would otherwise report for SVG's text bytes.
            diagnostics.Emit(new Diagnostic(
                DiagnosticCodes.SvgParseFailed001,
                ex.Status == NetPdf.Svg.SvgParseStatus.Blocked
                    ? "An SVG image was rejected by the parser's security guards — a prohibited <!DOCTYPE / "
                      + "<!ENTITY, or an over-size document (the XXE / entity-expansion 'billion laughs' "
                      + "defense). The image was not rendered."
                    : "An SVG image could not be parsed as well-formed XML. The image was not rendered.",
                DiagnosticSeverity.Warning));
            return null;
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
    private static Entry Decode(byte[] bytes, IDiagnosticsSink diagnostics)
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
        // SVG (Phase 4 PR 5) — a text/XML format Skia's codecs don't decode; the first-cut SVG renderer
        // (NetPdf.Svg) rasterizes the shapes via Skia → an RGBA raster XObject. Sniffed before the raster
        // fallback (which would reject SVG's text bytes).
        if (NetPdf.Svg.SvgRasterizer.LooksLikeSvg(bytes))
        {
            var svgRaster = NetPdf.Svg.SvgRasterizer.TryRender(bytes, out var svgUnsupported, out var svgStatus);
            if (svgRaster is null)
            {
                // SEC-8 — surface WHY the SVG didn't render (a security-guard rejection vs malformed) instead
                // of falling through to the raster decoder's generic, misleading IMG-DECODE-FAILED error. The
                // fetch wrapper (FetchAndDecodeAsync) catches this + emits SVG-PARSE-FAILED-001 once.
                throw new SvgImageParseException(svgStatus);
            }

            if (svgUnsupported)
                diagnostics.Emit(new Diagnostic(
                    DiagnosticCodes.CssSvgUnsupported001,
                    "An SVG image used a feature the renderer doesn't support (an EXTERNAL / non-data: "
                    + "<image> href, a filter with an unsupported primitive [feImage/feTile/feTurbulence/…] or "
                    + "input/region attributes, a filter/marker/clip-path/mask/textPath referencing a "
                    + "wrong-kind or unresolved target, an unresolved url(#…) reference, an unknown element "
                    + "such as foreignObject, or content beyond the depth / element budget); the supported "
                    + "shapes, text (incl. %/em coords, spacing, rotate, dominant-baseline, textPath), "
                    + "gradients, pattern paint-servers, filter graphs (feGaussianBlur/feOffset/feColorMatrix/"
                    + "feDropShadow/feFlood/feMerge/feComposite/feBlend + named routing), clip-path/mask/marker "
                    + "references, preserveAspectRatio, use/symbol viewports, data: images, stroke dashes, "
                    + "opacity, and nested viewports still rendered. An unresolvable paint server was painted "
                    + "transparent (not black).",
                    DiagnosticSeverity.Info));
            // SourceBytes = the RASTERIZED RGBA re-encoded as PNG (NOT the SVG XML), so a CSS filter / mask
            // on the <img> can re-decode it via SKCodec (PR-230 review [P2]); fall back to the SVG bytes if
            // the encode fails (the base image still renders; filter/mask just won't apply).
            var svgSource = SubtreeRasterizer.EncodePng(svgRaster) ?? bytes;
            return new Entry
            {
                WidthPx = svgRaster.Width,
                HeightPx = svgRaster.Height,
                XObject = RasterImageXObject.Build(svgRaster),
                SourceBytes = svgSource,
                SvgSourceBytes = bytes, // the raw SVG XML — for the opt-in native emitter (crisp vector)
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

    /// <summary>SEC-8 — thrown by <c>Decode</c> when an SVG image can't be parsed, carrying WHY so the
    /// fetch wrapper emits the specific <c>SVG-PARSE-FAILED-001</c> diagnostic (not the generic decode error).</summary>
    private sealed class SvgImageParseException(NetPdf.Svg.SvgParseStatus status) : Exception
    {
        public NetPdf.Svg.SvgParseStatus Status { get; } = status;
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
