// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using NetPdf.Css.Cascade;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;
using NetPdf.Diagnostics;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Semantic;

namespace NetPdf.Phase2;

/// <summary>
/// Task 17 — canonical composition of every Phase 2 stage in one call. This
/// is the pipeline the public <c>HtmlPdf.Convert</c> facade will eventually
/// invoke (Phase 5 wires the public surface). For Phase 2 + cycle-1 testing
/// it lives as an internal helper that integration tests can drive
/// directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stages composed:</b>
/// <list type="number">
///   <item><see cref="HtmlParsingHost"/> — HTML → AngleSharp DOM with
///     scripting permanently off.</item>
///   <item><see cref="CssPreprocessor"/> + <see cref="CssParserAdapter"/> —
///     parses each <c>&lt;style&gt;</c>'s CSS through AngleSharp's parser
///     and overlays the preprocessor's recovery side-data
///     (modern-color-function raw-value preservation, multi-arg
///     <c>attr()</c> recovery per Task 16 hardening Rec 1, etc.). Per
///     Task 17 review Rec 6, sheets are paired with their owning
///     <c>&lt;style&gt;</c> element via <see cref="IStyleSheet.OwnerNode"/>
///     rather than fragile ordinal-index pairing — stylesheets that get
///     disabled or come from external <c>&lt;link&gt;</c> elements no
///     longer corrupt the order.</item>
///   <item><see cref="CascadeResolver"/> — origin / importance / layer /
///     specificity / source-order resolution per CSS Cascade L4 §6.4.</item>
///   <item><see cref="VarResolver"/> — <c>var()</c> + custom-property
///     substitution; <c>CalcResolver</c> + <c>PropertyResolverDispatch</c>
///     are invoked transitively during the box-builder walk.</item>
///   <item><see cref="BoxBuilder"/> — DOM walk producing the box tree (with
///     pseudo materialization, table fixup, anonymous-block insertion,
///     orphan-table fixup).</item>
///   <item><see cref="SemanticTreeBuilder"/> — parallel walk producing the
///     accessibility / PDF-UA structure tree (consults the cascade for
///     hidden-element exclusion).</item>
/// </list>
/// </para>
/// <para>
/// <b>Diagnostic threading.</b> Per Task 17 review Rec 1, the
/// <see cref="ICssDiagnosticsSink"/> is forwarded to every stage that
/// emits — not just <see cref="BoxBuilder"/>. Lost diagnostics in cycle 1
/// included <c>:has()</c> rendering-not-implemented, malformed selectors,
/// unsupported at-rules / container queries (cascade), <c>var()</c>
/// circular / expansion-limit, <c>calc()</c> invalid / div-by-zero
/// (var-resolver). All now reach the supplied sink.
/// </para>
/// <para>
/// <b>Media-context threading.</b> Per Task 17 review Rec 2, the cascade's
/// <see cref="CssMediaContext"/> is built from the supplied
/// <see cref="HtmlPdfOptions"/> rather than always using
/// <see cref="CssMediaContext.DefaultPrint"/>. Authors who pass
/// <c>MediaType = CssMediaType.Screen</c> or a non-default
/// <c>PageSize</c> get correct <c>@media</c>-query evaluation against
/// their actual viewport.
/// </para>
/// <para>
/// <b>Determinism contract.</b> Same input HTML + same options → identical
/// box tree shape (modulo per-rental <c>ComputedStyle</c> instances from
/// the cascade pool — the property values they carry are deterministic).
/// The dedup HashSet for diagnostics is build-scoped so emission counts
/// are stable per run.
/// </para>
/// </remarks>
internal static class Phase2Pipeline
{
    /// <summary>HTML-string entry point — the test-friendly path. Parses
    /// <paramref name="html"/> via <see cref="HtmlParsingHost"/>, extracts
    /// the document's <c>&lt;style&gt;</c> elements via the preprocessor +
    /// adapter, then runs <see cref="Run"/>. Per Task 17 review Rec 5,
    /// <paramref name="cancellationToken"/> is checked at each stage
    /// boundary (parsing → cascade → var → box → semantic).</summary>
    public static async Task<Phase2Result> RunFromHtmlAsync(
        string html,
        HtmlPdfOptions options,
        ICssDiagnosticsSink? diagnostics = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(options);

        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, options, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var sheets = ExtractStylesheets(document);
        // Per Task 17 review Rec 4: when no explicit sink is supplied, auto-
        // wire HtmlPdfOptions.Diagnostics through the adapter so a single
        // public sink collects HTML + CSS + layout diagnostics.
        var effectiveSink = diagnostics ?? PublicDiagnosticsSinkAdapter.ForOptions(options);
        return Run(document, sheets, options, effectiveSink, cancellationToken);
    }

    /// <summary>Document entry point — for callers that already have an
    /// <see cref="IDocument"/> + adapted stylesheet list (e.g., when
    /// integration tests parse the document themselves to inject custom
    /// metadata). Honors <paramref name="cancellationToken"/> at each
    /// stage boundary.</summary>
    public static Phase2Result Run(
        IDocument document,
        ImmutableArray<CssStylesheet> sheets,
        HtmlPdfOptions options,
        ICssDiagnosticsSink? diagnostics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        var media = BuildMediaContext(options);

        cancellationToken.ThrowIfCancellationRequested();
        // Per Phase 2 deep review Rec 6 — pass the token INTO each stage so
        // the walkers check at every element, not just at the stage boundary.
        // Hostile inputs (e.g., 100k synthesized rows) now stop within
        // microseconds of cancellation rather than running the full per-stage
        // pass before the next boundary check.
        var cascade = CascadeResolver.Resolve(document, sheets, media, diagnostics, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var resolved = VarResolver.Resolve(cascade, document, diagnostics);

        cancellationToken.ThrowIfCancellationRequested();
        var boxRoot = BoxBuilder.Build(document, resolved, diagnostics, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var semanticRoot = SemanticTreeBuilder.Build(document, resolved, cancellationToken);

        return new Phase2Result(boxRoot, semanticRoot, resolved, sheets);
    }

    /// <summary>Per Task 17 review Rec 2 — build the cascade's media
    /// evaluation context from <paramref name="options"/>. Maps
    /// <see cref="HtmlPdfOptions.MediaType"/> to the lower-cased media
    /// string the cascade expects ("print" / "screen") and projects the
    /// viewport dimensions from <see cref="HtmlPdfOptions.PageSize"/>.
    /// <see cref="CssMediaContext.DevicePixelRatio"/> + <see cref="CssMediaContext.PreferredColorScheme"/>
    /// stay at their defaults until <see cref="HtmlPdfOptions"/> grows
    /// matching options (post-v1).</summary>
    private static CssMediaContext BuildMediaContext(HtmlPdfOptions options)
    {
        var mediaTypeString = options.MediaType switch
        {
            CssMediaType.Screen => "screen",
            _ => "print",
        };
        return new CssMediaContext(
            MediaType: mediaTypeString,
            ViewportWidthPx: options.PageSize.WidthPx,
            ViewportHeightPx: options.PageSize.HeightPx,
            DevicePixelRatio: 1.0,
            PreferredColorScheme: "light");
    }

    /// <summary>Extract every <c>&lt;style&gt;</c> element's CSS content,
    /// preprocess it for recovery side-data, and adapt the AngleSharp.Css
    /// CSSOM into NetPdf's typed AST.</summary>
    /// <remarks>
    /// <para>
    /// <b>Owner-node pairing</b> per Task 17 review Rec 6 — each adapted
    /// stylesheet is sourced from a specific <c>&lt;style&gt;</c> element
    /// via <see cref="IStyleSheet.OwnerNode"/> rather than the previous
    /// ordinal-index pairing (which was fragile when disabled / external
    /// <c>&lt;link&gt;</c> sheets entered <c>document.StyleSheets</c>).
    /// </para>
    /// <para>
    /// <b>Media + disabled metadata</b> per Task 17 review Rec 3 — each
    /// owner element's <c>media</c> attribute (or the rawSheet's
    /// <see cref="IStyleSheet.Media"/> / <see cref="IStyleSheet.IsDisabled"/>)
    /// is passed to <c>CssParserAdapter.Adapt</c> so the cascade's
    /// media-query gate works correctly. Without this, a
    /// <c>&lt;style media="screen"&gt;</c> would have applied during print.
    /// </para>
    /// </remarks>
    private static ImmutableArray<CssStylesheet> ExtractStylesheets(IDocument document)
    {
        var output = ImmutableArray.CreateBuilder<CssStylesheet>();
        var order = 0;
        foreach (var rawSheet in document.StyleSheets.OfType<ICssStyleSheet>())
        {
            // Per Rec 6: pair via OwnerNode rather than ordinal index. When
            // the sheet has no owner (synthesized / detached), fall through
            // to an empty raw-text path — the preprocessor's recovery
            // side-data is empty in that case.
            string rawText;
            string? mediaQuery = null;
            var isDisabled = rawSheet.IsDisabled;
            var ownerKind = CssStylesheetOwnerKind.Unknown;
            if (rawSheet.OwnerNode is IElement ownerElement)
            {
                // Per PR #11 review (Rec 3 of the Copilot pass): infer
                // ownerKind from the actual owner element rather than
                // hardcoding StyleElement. document.StyleSheets includes
                // <link> sheets too (when external stylesheet loading is
                // enabled in a future cycle); their provenance must surface
                // correctly to diagnostics + tracing.
                ownerKind = ownerElement.LocalName.ToLowerInvariant() switch
                {
                    "style" => CssStylesheetOwnerKind.StyleElement,
                    "link" => CssStylesheetOwnerKind.LinkElement,
                    _ => CssStylesheetOwnerKind.Unknown,
                };

                // Inline <style> elements own their CSS as TextContent.
                // <link> elements have no inline text — their CSS is
                // delivered via the resource loader (cycle 2 scope).
                rawText = ownerKind == CssStylesheetOwnerKind.StyleElement
                    ? (ownerElement.TextContent ?? string.Empty)
                    : string.Empty;

                // Per Rec 3: read the `media` attribute from the owner
                // element (authoritative for both inline <style> + <link>).
                var rawMedia = ownerElement.GetAttribute("media");
                mediaQuery = string.IsNullOrWhiteSpace(rawMedia) ? null : rawMedia;
            }
            else
            {
                rawText = string.Empty;
            }

            // Fall back to the rawSheet's MediaText when the owner had no
            // explicit media attribute — covers <link> sheets where
            // AngleSharp populates Media from the link's media attribute.
            if (mediaQuery is null)
            {
                var mediaText = rawSheet.Media?.MediaText;
                if (!string.IsNullOrWhiteSpace(mediaText))
                {
                    mediaQuery = mediaText;
                }
            }

            var preprocess = string.IsNullOrEmpty(rawText)
                ? CssPreprocessResult.Empty
                : CssPreprocessor.Process(rawText);
            output.Add(CssParserAdapter.Adapt(
                rawSheet, preprocess,
                href: null,
                origin: CssStylesheetOrigin.Author,
                ownerKind: ownerKind,
                mediaQuery: mediaQuery,
                isDisabled: isDisabled,
                order: order++));
        }
        return output.ToImmutable();
    }
}
