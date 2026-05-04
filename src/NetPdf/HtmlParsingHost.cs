// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Io;

namespace NetPdf;

/// <summary>
/// First Phase 2 stage: parses the input HTML through AngleSharp into an <see cref="IDocument"/>
/// while keeping JavaScript permanently off and surfacing every <c>&lt;script&gt;</c> element +
/// every <c>javascript:</c> link target as a diagnostic. The host is <see langword="internal"/>
/// — the public API surface remains the frozen 0.1.0-alpha facade (<see cref="HtmlPdf"/>,
/// <see cref="HtmlPdfOptions"/>, <see cref="PdfRenderResult"/>, and the public interfaces).
/// </summary>
/// <remarks>
/// <para>
/// <b>JavaScript sanitization model.</b> Three execution surfaces exist in HTML:
/// <c>&lt;script&gt;</c> elements (inline + external), <c>javascript:</c> URLs in href-bearing
/// attributes, and event-handler attributes (<c>onclick</c>, <c>onload</c>, etc.).
/// </para>
/// <list type="bullet">
///   <item><description><c>&lt;script&gt;</c> elements: emit <c>HTML-SCRIPT-IGNORED-001</c> and
///   remove the element from the tree. AngleSharp itself never executes them — no
///   <c>IScripting</c> service is registered and <c>AngleSharp.Js</c> is not in the dependency
///   graph — but stripping makes the contract documented in the registry visible.</description></item>
///   <item><description><c>javascript:</c> URLs in <c>&lt;a href&gt;</c> / <c>&lt;area href&gt;</c>
///   / SVG <c>xlink:href</c>: emit <c>HTML-JAVASCRIPT-URL-IGNORED-001</c> and remove the
///   attribute. The element + its text content remain so the link text still renders, but
///   Phase 4 link emission has nothing to attach a clickable target to. Detection follows
///   the WHATWG URL Living Standard (<c>https://url.spec.whatwg.org/#concept-basic-url-parser</c>):
///   leading C0-control + space stripped, embedded tab/CR/LF/FF stripped, scheme matched
///   case-insensitively. Other URL-bearing attributes (<c>src</c>, <c>action</c>, etc.) are
///   not scanned because no Phase 1–4 emission consumer interprets them as actionable URLs.</description></item>
///   <item><description><c>on*</c> event-handler attributes: left in place. They are inert
///   without a JS engine, no PDF reader can dispatch them, and stripping them adds DOM churn
///   for no security benefit. If a future phase emits tagged-PDF metadata that surfaces
///   attribute values, this decision is revisited then.</description></item>
/// </list>
/// <para>
/// <b>Resource loading.</b> AngleSharp's loader is configured with
/// <c>IsResourceLoadingEnabled = false</c>, so it never reaches the network for
/// <c>&lt;img src&gt;</c>, <c>&lt;link rel="stylesheet"&gt;</c>, <c>&lt;script src&gt;</c>, or
/// <c>@import</c>. Resources flow through <see cref="HtmlPdfOptions.ResourceLoader"/> in later
/// Phase 2/3 tasks. The <c>HtmlParsingHostTests.ParseAsync_no_network_traffic_when_resource_loading_disabled</c>
/// regression test installs a counting <c>IRequester</c> and asserts zero outbound calls.
/// </para>
/// <para>
/// <b>CSS ownership.</b> The host enables <c>WithCss()</c> so AngleSharp.Css populates
/// <c>document.StyleSheets</c>. Task 2 (the CSS parser adapter under
/// <c>src/NetPdf.Css/Parser/</c>) consumes that CSSOM and adapts AngleSharp.Css AST nodes into
/// NetPdf's internal AST. AngleSharp.Css types must never leak past <c>NetPdf.Css.Parser</c> —
/// downstream stages (cascade, computed values, box generation) see only NetPdf types.
/// </para>
/// <para>
/// <b>Timeout.</b> <see cref="HtmlPdfOptions.Timeout"/> is enforced at the public facade
/// (<see cref="HtmlPdf.ConvertAsync(string, HtmlPdfOptions?, CancellationToken)"/>) when Phase 2
/// Task 12+ wires the facade end-to-end. The host itself honors only the
/// <see cref="CancellationToken"/> it is handed; the facade will compose Timeout + caller's ct
/// via <c>CancellationTokenSource.CreateLinkedTokenSource</c>.
/// </para>
/// </remarks>
internal sealed class HtmlParsingHost
{
    private readonly IConfiguration _configuration;

    public HtmlParsingHost()
    {
        // IsKeepingSourceReferences powers SourceLocation in HTML-* diagnostics.
        // IsScripting = false makes the parser's "scripting flag" (HTML5 §13.2.5.4.7) explicit:
        //   - <noscript> content is parsed as DOM, not RAWTEXT, so the no-JS fallback content
        //     renders into the PDF (the rendering target has no JS engine — fallback is the
        //     correct branch).
        //   - <script> content is still treated as raw text per HTML5 hard-coded handling.
        // Setting it explicitly protects against an upstream AngleSharp default change.
        var sourceTrackingParser = new HtmlParser(new HtmlParserOptions
        {
            IsKeepingSourceReferences = true,
            IsScripting = false,
        });

        // WithCss() registers AngleSharp.Css services so document.StyleSheets is populated
        // during HTML load. Task 2's CSS adapter (NetPdf.Css.Parser) consumes that CSSOM —
        // never the other way around. WithDefaultLoader with IsResourceLoadingEnabled = false
        // ensures AngleSharp never issues an outbound HTTP request for a referenced resource.
        _configuration = Configuration.Default
            .WithCss()
            .WithDefaultLoader(new LoaderOptions { IsResourceLoadingEnabled = false })
            .With(sourceTrackingParser);
    }

    /// <summary>
    /// Parses <paramref name="html"/> into a styled-but-unlayouted <see cref="IDocument"/>.
    /// Every <c>&lt;script&gt;</c> encountered emits <c>HTML-SCRIPT-IGNORED-001</c> and is
    /// removed; every <c>javascript:</c> URL in <c>href</c>/<c>xlink:href</c> emits
    /// <c>HTML-JAVASCRIPT-URL-IGNORED-001</c> and the attribute is removed.
    /// </summary>
    /// <param name="html">The input HTML document.</param>
    /// <param name="options">Conversion options. <see cref="HtmlPdfOptions.BaseUri"/> resolves
    /// relative URLs and is also recorded as the <c>File</c> field on diagnostic source
    /// locations. <see cref="HtmlPdfOptions.Diagnostics"/>, when non-<see langword="null"/>,
    /// receives one diagnostic per stripped script element / javascript: URL. <b>Note:</b>
    /// <see cref="HtmlPdfOptions.Timeout"/> is enforced by the public facade, not by this host.</param>
    /// <param name="ct">Cancels the parse promptly. Throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>The parsed document with all <c>&lt;script&gt;</c> elements stripped and all
    /// <c>javascript:</c>-bearing href attributes removed.</returns>
    public async Task<IDocument> ParseAsync(
        string html,
        HtmlPdfOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        var context = BrowsingContext.New(_configuration);
        var address = options.BaseUri?.AbsoluteUri ?? "about:blank";

        var document = await context.OpenAsync(req => req.Content(html).Address(address), ct)
            .ConfigureAwait(false);

        var sourceFile = options.BaseUri?.AbsoluteUri;
        StripScripts(document, options.Diagnostics, sourceFile);
        StripJavaScriptUrls(document, options.Diagnostics, sourceFile);
        return document;
    }

    private static void StripScripts(IDocument document, IDiagnosticsSink? sink, string? sourceFile)
    {
        // Snapshot before mutating — DOM live collections invalidate as nodes are removed.
        var scripts = document.QuerySelectorAll("script").ToArray();

        foreach (var script in scripts)
        {
            sink?.Emit(new Diagnostic(
                DiagnosticCodes.HtmlScriptIgnored001,
                "A <script> element was encountered. NetPdf does not execute JavaScript in v1. The element was removed from the rendering tree.",
                DiagnosticSeverity.Warning,
                ToSourceLocation(script, sourceFile)));

            script.Remove();
        }
    }

    private static void StripJavaScriptUrls(IDocument document, IDiagnosticsSink? sink, string? sourceFile)
    {
        // Only attributes that produce link annotations in Phase 4 emission are scanned:
        //   - href on <a> / <area>
        //   - xlink:href on SVG <a> (matched via attribute presence — XPath-style namespace
        //     selectors are awkward in CSS; we walk the SVG <a> elements directly).
        // A ToArray() snapshot is taken because we will be mutating attributes during the walk.
        foreach (var element in document.QuerySelectorAll("a[href], area[href]").ToArray())
        {
            var href = element.GetAttribute("href");
            if (!IsJavaScriptUrl(href)) continue;

            sink?.Emit(new Diagnostic(
                DiagnosticCodes.HtmlJavaScriptUrlIgnored001,
                $"A javascript: URL was found on a <{element.LocalName}> element's href attribute. The attribute was removed; element text retained.",
                DiagnosticSeverity.Warning,
                ToSourceLocation(element, sourceFile)));

            element.RemoveAttribute("href");
        }

        // SVG <a> elements use xlink:href (or, in modern SVG 2, plain href). Walk the document
        // and remove any element's xlink:href whose value is a javascript: URL.
        foreach (var element in document.All.ToArray())
        {
            string? xlinkHref = null;
            foreach (var attr in element.Attributes)
            {
                if (attr is null) continue;
                if (attr.LocalName == "href" && attr.Prefix == "xlink")
                {
                    xlinkHref = attr.Value;
                    break;
                }
            }
            if (xlinkHref is null || !IsJavaScriptUrl(xlinkHref)) continue;

            sink?.Emit(new Diagnostic(
                DiagnosticCodes.HtmlJavaScriptUrlIgnored001,
                $"A javascript: URL was found on a <{element.LocalName}> element's xlink:href attribute. The attribute was removed; element text retained.",
                DiagnosticSeverity.Warning,
                ToSourceLocation(element, sourceFile)));

            // Remove via (namespaceUri, localName) so the right xlink-namespaced attribute drops.
            element.RemoveAttribute(NamespaceNames.XLinkUri, "href");
        }
    }

    /// <summary>
    /// Recognizes <c>javascript:</c> URLs per the WHATWG URL Living Standard. Leading C0-control
    /// and space characters are stripped; embedded tab/CR/LF/FF inside the scheme are skipped;
    /// the scheme is matched case-insensitively.
    /// </summary>
    internal static bool IsJavaScriptUrl(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var span = value.AsSpan();

        // Per WHATWG URL §3.5, the basic URL parser removes leading C0-control + space.
        while (!span.IsEmpty && span[0] <= 0x20) span = span[1..];

        const string target = "javascript:";
        var matched = 0;
        foreach (var c in span)
        {
            // Tab/LF/CR/FF inside the URL are removed before scheme parsing.
            if (c == '\t' || c == '\n' || c == '\r' || c == '\f') continue;
            if (matched >= target.Length) break;
            if (char.ToLowerInvariant(c) != target[matched]) return false;
            matched++;
        }
        return matched == target.Length;
    }

    private static SourceLocation ToSourceLocation(IElement element, string? sourceFile)
    {
        // AngleSharp populates IElement.SourceReference only when IsKeepingSourceReferences is on
        // (set above) AND the element actually came from the parser. Synthesized fixup elements
        // can return null; fall back to Unknown so the diagnostic still emits cleanly.
        var position = element.SourceReference?.Position;
        return position is { } pos
            ? new SourceLocation(sourceFile, pos.Line, pos.Column)
            : SourceLocation.Unknown;
    }
}
