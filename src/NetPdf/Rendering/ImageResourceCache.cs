// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NetPdf.Css.Cascade;
using NetPdf.Diagnostics;
using NetPdf.Layout.Boxes;
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

    /// <summary>Replaced (<c>&lt;img&gt;</c>) box → its resolved absolute URI. Only boxes whose
    /// fetch + decode SUCCEEDED appear (a failed image lays out / paints nothing).</summary>
    public Dictionary<Box, string> ImageBoxes { get; } = new();

    /// <summary>Element-backed box → its <c>background-image</c> URI (bg-image cycle). Only
    /// successfully decoded references appear.</summary>
    public Dictionary<Box, string> BackgroundImageBoxes { get; } = new();

    public bool TryGet(string absoluteUri, out Entry entry) =>
        _byUri.TryGetValue(absoluteUri, out entry!);

    /// <summary>Register <paramref name="entry"/>'s XObject with <paramref name="document"/>,
    /// memoized — N placements of one image share one XObject (and the document dedups
    /// byte-identical streams across entries).</summary>
    public static PdfIndirectRef GetOrRegister(PdfDocument document, Entry entry) =>
        entry.RegisteredRef ??= document.RegisterImage(entry.XObject);

    /// <summary>Walk <paramref name="boxRoot"/> + fetch/decode every image reference. Never
    /// throws for a bad reference — each failure is a diagnostic + the reference is skipped.</summary>
    public static async Task<ImageResourceCache> PrefetchAsync(
        Box boxRoot,
        ResolvedCascadeResult cascade,
        HtmlPdfOptions options,
        IDiagnosticsSink diagnostics,
        CancellationToken cancellationToken)
    {
        var cache = new ImageResourceCache();
        var context = new ResourceFetchContext(
            options.SecurityPolicy, options.BaseUri, cancellationToken);
        var loader = new SafeResourceLoader(options.ResourceLoader, context);
        var unsupportedBackgroundReported = false;

        // Collect references first (sync tree walk), then fetch each unique URI once.
        var references = new List<(Box Box, string RawUrl, bool IsBackground)>();
        CollectReferences(boxRoot, cascade, references, diagnostics, ref unsupportedBackgroundReported);

        foreach (var (box, rawUrl, isBackground) in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveUri(rawUrl, options.BaseUri, out var uri, out var resolveFailure))
            {
                EmitLoadFailed(diagnostics, rawUrl, resolveFailure);
                continue;
            }
            var key = uri.OriginalString;
            if (!cache._byUri.TryGetValue(key, out var entry))
            {
                // Not fetched yet (a failed URI is recorded as null via the sentinel below).
                if (cache._failedUris.Contains(key)) continue;
                entry = await FetchAndDecodeAsync(loader, uri, diagnostics).ConfigureAwait(false);
                if (entry is null)
                {
                    cache._failedUris.Add(key);
                    continue;
                }
                cache._byUri[key] = entry;
            }
            if (isBackground) cache.BackgroundImageBoxes[box] = key;
            else cache.ImageBoxes[box] = key;
        }
        return cache;
    }

    private readonly HashSet<string> _failedUris = new(StringComparer.Ordinal);

    private static void CollectReferences(
        Box box,
        ResolvedCascadeResult cascade,
        List<(Box, string, bool)> references,
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
            // url(...) is the supported form; anything else surfaces once per render.
            var bgRaw = cascade.TryGetStylesFor(element)?.GetWinner("background-image")?.ResolvedValue;
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
            CollectReferences(child, cascade, references, diagnostics, ref unsupportedBackgroundReported);
    }

    /// <summary>Parse a CSS <c>url(...)</c> token — optionally quoted, the WHOLE value (a
    /// trailing layer / second token makes it a multi-layer form → unsupported).</summary>
    internal static bool TryParseCssUrl(string raw, out string url)
    {
        url = string.Empty;
        var v = raw.Trim();
        if (!v.StartsWith("url(", StringComparison.OrdinalIgnoreCase) || !v.EndsWith(")", StringComparison.Ordinal))
            return false;
        var inner = v[4..^1].Trim();
        // A comma at the top level of the VALUE (outside the url token) would be a multi-layer
        // list; inside the parens any comma belongs to the URL (data: URIs contain one!).
        if (inner.Length >= 2
            && ((inner[0] == '"' && inner[^1] == '"') || (inner[0] == '\'' && inner[^1] == '\'')))
        {
            inner = inner[1..^1];
        }
        if (inner.Length == 0) return false;
        url = inner;
        return true;
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
