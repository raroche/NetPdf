// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
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
///     parses each <c>&lt;style&gt;</c>'s CSS through AngleSharp's parser and
///     overlays the preprocessor's recovery side-data
///     (modern-color-function raw-value preservation, multi-arg
///     <c>attr()</c> recovery per Task 16 hardening Rec 1, etc.).</item>
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
/// <b>Determinism contract.</b> Same input HTML + same options → identical
/// box tree shape (modulo per-rental <c>ComputedStyle</c> instances from
/// the cascade pool — the property values they carry are deterministic).
/// The dedup HashSet for diagnostics is build-scoped so emission counts
/// are stable per run.
/// </para>
/// <para>
/// <b>Cycle-1 deferrals.</b> External stylesheet loading (cross-origin
/// <c>&lt;link rel="stylesheet"&gt;</c>) — Phase 2 disables resource loading
/// at the AngleSharp boundary; Phase 5 wires the resource pipeline. Inline
/// <c>style</c> attributes are honored via <see cref="CascadeResolver"/>'s
/// internal walk (no extra wiring needed here).
/// </para>
/// </remarks>
internal static class Phase2Pipeline
{
    /// <summary>HTML-string entry point — the test-friendly path. Parses
    /// <paramref name="html"/> via <see cref="HtmlParsingHost"/>, extracts
    /// the document's <c>&lt;style&gt;</c> elements via the preprocessor +
    /// adapter, then runs <see cref="Run"/>.</summary>
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

        var sheets = ExtractStylesheets(document);
        return Run(document, sheets, diagnostics);
    }

    /// <summary>Document entry point — for callers that already have an
    /// <see cref="IDocument"/> + adapted stylesheet list (e.g., when
    /// integration tests parse the document themselves to inject custom
    /// metadata).</summary>
    public static Phase2Result Run(
        IDocument document,
        ImmutableArray<CssStylesheet> sheets,
        ICssDiagnosticsSink? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(document);

        var cascade = CascadeResolver.Resolve(document, sheets, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, document);

        var boxRoot = BoxBuilder.Build(document, resolved, diagnostics);
        var semanticRoot = SemanticTreeBuilder.Build(document, resolved);

        return new Phase2Result(boxRoot, semanticRoot, resolved, sheets);
    }

    /// <summary>Extract every <c>&lt;style&gt;</c> element's CSS content,
    /// preprocess it for recovery side-data, and adapt the AngleSharp.Css
    /// CSSOM into NetPdf's typed AST. Mirrors the pattern used by the
    /// per-stage corpus tests so behavior stays consistent.</summary>
    private static ImmutableArray<CssStylesheet> ExtractStylesheets(IDocument document)
    {
        var output = ImmutableArray.CreateBuilder<CssStylesheet>();
        var order = 0;
        var styleElements = document.QuerySelectorAll("style");
        var styleIdx = 0;
        foreach (var rawSheet in document.StyleSheets.OfType<ICssStyleSheet>())
        {
            string rawText;
            if (styleIdx < styleElements.Length)
            {
                rawText = styleElements[styleIdx].TextContent ?? string.Empty;
                styleIdx++;
            }
            else
            {
                rawText = string.Empty;
            }
            var preprocess = string.IsNullOrEmpty(rawText)
                ? CssPreprocessResult.Empty
                : CssPreprocessor.Process(rawText);
            output.Add(CssParserAdapter.Adapt(
                rawSheet, preprocess,
                href: null,
                origin: CssStylesheetOrigin.Author,
                ownerKind: CssStylesheetOwnerKind.StyleElement,
                mediaQuery: null,
                isDisabled: false,
                order: order++));
        }
        return output.ToImmutable();
    }
}
