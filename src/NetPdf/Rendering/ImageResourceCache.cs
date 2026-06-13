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
/// at its declared/attribute size; nothing paints). An unsupported <c>background-image</c> form
/// (gradient / multi-layer) surfaces <c>CSS-BACKGROUND-IMAGE-UNSUPPORTED-001</c> once per
/// render.</remarks>
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
    }

    private readonly Dictionary<string, Entry> _byUri = new(StringComparer.Ordinal);

    /// <summary>An <c>&lt;img&gt;</c> box's image reference (img-pipeline + object-fit cycles):
    /// the resolved URI key + the COMPUTED <c>object-fit</c> keyword index (the KeywordResolver
    /// table order — 0 = <c>fill</c>, the initial; object-fit is a registered property since
    /// PR #168 review P2, so the slot carries the validated keyword and CSS-wide keywords get
    /// property-aware handling for free).</summary>
    internal readonly record struct ImgSpec(string UriKey, int ObjectFitKeyword);

    /// <summary>Replaced (<c>&lt;img&gt;</c>) box → its image spec. Only boxes whose fetch +
    /// decode SUCCEEDED appear (a failed image lays out / paints nothing).</summary>
    public Dictionary<Box, ImgSpec> ImageBoxes { get; } = new();

    /// <summary>A box's background-image reference (bg-image + bg-variants cycles): the resolved
    /// URI key + the RAW declared <c>background-repeat</c> / <c>-size</c> / <c>-position</c>
    /// winners (null = unset → the initial), parsed by the tiler at paint.</summary>
    internal readonly record struct BackgroundSpec(
        string UriKey, string? RepeatRaw, string? SizeRaw, string? PositionRaw);

    /// <summary>Element-backed box → its <c>background-image</c> spec (bg-image cycle). Only
    /// successfully decoded references appear.</summary>
    public Dictionary<Box, BackgroundSpec> BackgroundImageBoxes { get; } = new();

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
        CollectReferences(
            boxRoot, cascade, references, collectBackgrounds: options.PrintBackgrounds,
            diagnostics, ref unsupportedBackgroundReported);

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
                    ComposeAxisLonghands(rules, "background-position", axisInitial: "0%"));
            }
            else
            {
                // object-fit rides along as the COMPUTED keyword index (object-fit cycle;
                // the registered property's slot — 0 = fill, the initial).
                cache.ImageBoxes[box] = new ImgSpec(
                    key, box.Style.ReadKeywordOrDefault(PropertyId.ObjectFit, defaultIndex: 0));
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
        bool collectBackgrounds,
        IDiagnosticsSink diagnostics,
        ref bool unsupportedBackgroundReported)
    {
        if (box.SourceElement is { } element)
        {
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
                ? cascade.TryGetStylesFor(element)?.GetWinner("background-image")?.ResolvedValue
                : null;
            if (!string.IsNullOrWhiteSpace(bgRaw)
                && !bgRaw.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseCssUrl(bgRaw, out var bgUrl))
                {
                    references.Add((box, bgUrl, true));
                }
                else if (!unsupportedBackgroundReported)
                {
                    diagnostics.Emit(new Diagnostic(
                        DiagnosticCodes.CssBackgroundImageUnsupported001,
                        "background-image supports a single url(...) this cycle; a gradient "
                        + "function, multi-layer list, or unrecognized form was ignored "
                        + "(background-color still paints). Gradients are the Phase 4 "
                        + "shading-pattern work.",
                        DiagnosticSeverity.Warning));
                    unsupportedBackgroundReported = true;
                }
            }
        }
        foreach (var child in box.Children)
            CollectReferences(
                child, cascade, references, collectBackgrounds, diagnostics,
                ref unsupportedBackgroundReported);
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
            };
        }
        var raster = RasterImageDecoder.Decode(bytes);
        return new Entry
        {
            WidthPx = raster.Width,
            HeightPx = raster.Height,
            XObject = RasterImageXObject.Build(raster),
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
