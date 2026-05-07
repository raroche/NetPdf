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
/// regression test verifies the boundary by parsing a doc that references a deliberately-bad
/// host (<c>nope.invalid</c>) and asserting (a) the parse completes within a tight time
/// budget — a real DNS failure would take seconds — and (b) <c>link.Sheet</c> stays
/// <see langword="null"/>. If AngleSharp ever started honoring resource loading despite
/// the configuration, the parse would either hang on DNS or throw, and the test fails.
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
        StripEventHandlerAttributes(document, options.Diagnostics, sourceFile);
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
        // Per Phase A security hardening A-1 — the strip walk covers every HTML
        // attribute that names a navigable URL or fetched resource. Cycle 1 only
        // touched <a href>, <area href>, and xlink:href; the rest survived
        // verbatim. The deep-review threat model + the [DomPDF / Apryse / EvoPDF]
        // CVE survey confirms that <form action>, <iframe src>, <object data>,
        // <embed src>, <base href>, and <meta http-equiv="refresh" content="0;url=...">
        // all reach a sink that interprets URLs in real-world HTML-to-PDF
        // engines — and Phase 5 paint or PDF/UA emission could route any of them
        // into the output PDF. Stripping aggressive but consistent: for any URL
        // attribute on any element, if the value names javascript: / vbscript: /
        // data: schemes, drop the attribute + emit one diagnostic per attribute.
        //
        // A ToArray() snapshot is taken at each query because the mutating walks
        // would otherwise invalidate AngleSharp's live collection.
        StripDangerousUrlOnAttribute(document, "a[href], area[href]", "href", sink, sourceFile);
        // Per Phase A review feedback (user rec #2) — the strip walk is widened
        // beyond the cycle-A-1 set to cover every URL-bearing attribute that
        // can host a fetched resource. The full audit list (img/src,
        // link/href, source/src + srcset, audio/src, video/src + poster,
        // track/src, input[type=image]/src, input[formaction]) wasn't in
        // Phase A's first cut because they're inert today (resource loading
        // off) — but defense-in-depth: when Phase 5 wires IResourceLoader,
        // any of these could carry a javascript:/vbscript:/data: payload
        // that bypasses the loader's IP/scheme allowlist if the value passes
        // through the parser without the strip first.
        StripDangerousUrlOnAttribute(document, "form[action]", "action", sink, sourceFile);
        StripDangerousUrlOnAttribute(document, "iframe[src]", "src", sink, sourceFile);
        StripDangerousUrlOnAttribute(document, "object[data]", "data", sink, sourceFile);
        StripDangerousUrlOnAttribute(document, "embed[src]", "src", sink, sourceFile);
        StripDangerousUrlOnAttribute(document, "base[href]", "href", sink, sourceFile);
        StripDangerousUrlOnAttribute(document, "img[src]", "src", sink, sourceFile);
        StripDangerousUrlOnAttribute(document, "link[href]", "href", sink, sourceFile);
        StripDangerousUrlOnAttribute(document, "source[src]", "src", sink, sourceFile);
        StripDangerousUrlOnAttribute(document, "source[srcset]", "srcset", sink, sourceFile);
        StripDangerousUrlOnAttribute(document, "img[srcset]", "srcset", sink, sourceFile);
        StripDangerousUrlOnAttribute(document, "audio[src]", "src", sink, sourceFile);
        StripDangerousUrlOnAttribute(document, "video[src]", "src", sink, sourceFile);
        StripDangerousUrlOnAttribute(document, "video[poster]", "poster", sink, sourceFile);
        StripDangerousUrlOnAttribute(document, "track[src]", "src", sink, sourceFile);
        StripDangerousUrlOnAttribute(document, "input[src]", "src", sink, sourceFile);
        StripDangerousUrlOnAttribute(document, "input[formaction]", "formaction", sink, sourceFile);
        StripDangerousUrlOnAttribute(document, "button[formaction]", "formaction", sink, sourceFile);
        // <meta http-equiv="refresh" content="0;url=javascript:..."> — the URL
        // is embedded inside the content attribute. Per Copilot review #1 +
        // user recommendation #4, parsing accepts whitespace around the
        // `url=` separator (HTML5 §4.2.5.3 grammar permits whitespace) and
        // the diagnostic now describes the EXTRACTED URL's scheme rather
        // than the whole content attribute (which would always fall through
        // to "dangerous" because "0;url=..." doesn't start with a scheme).
        foreach (var meta in document.QuerySelectorAll("meta").ToArray())
        {
            var httpEquiv = meta.GetAttribute("http-equiv");
            if (!string.Equals(httpEquiv, "refresh", System.StringComparison.OrdinalIgnoreCase)) continue;
            var content = meta.GetAttribute("content");
            if (string.IsNullOrEmpty(content)) continue;
            var extractedUrl = ExtractMetaRefreshUrl(content);
            if (extractedUrl is null || !IsDangerousUrl(extractedUrl)) continue;
            sink?.Emit(new Diagnostic(
                DiagnosticCodes.HtmlJavaScriptUrlIgnored001,
                $"A <meta http-equiv=\"refresh\"> declared a {DescribeUrlScheme(extractedUrl)} URL. The content attribute was removed.",
                DiagnosticSeverity.Warning,
                ToSourceLocation(meta, sourceFile)));
            meta.RemoveAttribute("content");
        }

        // SVG <a> elements use xlink:href (or, in modern SVG 2, plain href),
        // but the xlink:href attribute can technically appear on any element.
        // Walk the entire document and strip every dangerous-scheme xlink:href
        // value, not just SVG anchors. Per Copilot review #3 + user
        // recommendation #1, the recognition is widened from
        // <see cref="IsJavaScriptUrl"/> to <see cref="IsDangerousUrl"/> so
        // data: + vbscript: in xlink:href are also caught (matching what the
        // explicit-attribute strip does on regular href / src / etc.).
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
            if (xlinkHref is null || !IsDangerousUrl(xlinkHref)) continue;

            sink?.Emit(new Diagnostic(
                DiagnosticCodes.HtmlJavaScriptUrlIgnored001,
                $"A {DescribeUrlScheme(xlinkHref)} URL was found on a <{element.LocalName}> element's xlink:href attribute. The attribute was removed; element text retained.",
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
    internal static bool IsJavaScriptUrl(string? value) => SchemeMatches(value, "javascript:");

    /// <summary>Per Phase A security hardening A-1 — the dangerous-URL set:
    /// <c>javascript:</c> (scripting), <c>vbscript:</c> (legacy IE scripting),
    /// and <c>data:</c> (carries arbitrary bytes; `data:text/html` + `data:image/svg+xml`
    /// are common SSRF / XSS sneak paths in HTML-to-PDF research). Other schemes
    /// (<c>file:</c>, <c>http:</c>, <c>https:</c>) are NOT stripped here — they're
    /// inert today (resource loading off) + Phase 5's IResourceLoader is the
    /// right control point for those once fetching is wired.</summary>
    internal static bool IsDangerousUrl(string? value) =>
        SchemeMatches(value, "javascript:") || SchemeMatches(value, "vbscript:") || SchemeMatches(value, "data:");

    private static bool SchemeMatches(string? value, string target)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var span = value.AsSpan();

        // Per WHATWG URL §3.5, the basic URL parser removes leading C0-control + space.
        while (!span.IsEmpty && span[0] <= 0x20) span = span[1..];

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

    private static string DescribeUrlScheme(string? value) =>
        SchemeMatches(value, "javascript:") ? "javascript:" :
        SchemeMatches(value, "vbscript:") ? "vbscript:" :
        SchemeMatches(value, "data:") ? "data:" : "dangerous";

    private static void StripDangerousUrlOnAttribute(
        IDocument document, string querySelector, string attributeName,
        IDiagnosticsSink? sink, string? sourceFile)
    {
        foreach (var element in document.QuerySelectorAll(querySelector).ToArray())
        {
            var value = element.GetAttribute(attributeName);
            if (!IsDangerousUrl(value)) continue;
            sink?.Emit(new Diagnostic(
                DiagnosticCodes.HtmlJavaScriptUrlIgnored001,
                $"A <{element.LocalName}> element's {attributeName} attribute carried a {DescribeUrlScheme(value)} URL. The attribute was removed; element text retained.",
                DiagnosticSeverity.Warning,
                ToSourceLocation(element, sourceFile)));
            element.RemoveAttribute(attributeName);
        }
    }

    /// <summary>Parses the value of <c>&lt;meta http-equiv="refresh" content="..."&gt;</c>
    /// and returns the embedded URL (or <see langword="null"/> when no URL
    /// segment is present). The HTML5 "refresh" pragma format per §4.2.5.3
    /// is <c>{seconds}[ ;url={url}]</c> with the URL optional; whitespace is
    /// forgiving on both sides of the <c>url=</c> separator + the leading
    /// delay; case-insensitive on the <c>url</c> token. Per Copilot review
    /// #1 + user recommendation #4, the previous version of this method
    /// (a) returned a yes/no instead of the URL — which forced the diagnostic
    /// caller to re-describe the WHOLE content attribute via DescribeUrlScheme,
    /// and (b) didn't accept whitespace around <c>url =</c>.</summary>
    private static string? ExtractMetaRefreshUrl(string content)
    {
        var span = content.AsSpan();
        var sepIdx = span.IndexOfAny(';', ',');
        if (sepIdx < 0) return null; // no URL segment present
        span = span[(sepIdx + 1)..].TrimStart();
        // Optional `url=` prefix (case-insensitive). Whitespace on EITHER side
        // of the `=` is permitted ("url = http://..." / "url= http://..." /
        // "url =http://...") per the spec's tolerant parsing rule.
        if (span.Length >= 3
            && (span[0] == 'u' || span[0] == 'U')
            && (span[1] == 'r' || span[1] == 'R')
            && (span[2] == 'l' || span[2] == 'L'))
        {
            var afterUrl = span[3..].TrimStart();
            if (afterUrl.Length > 0 && afterUrl[0] == '=')
            {
                span = afterUrl[1..].TrimStart();
            }
            // If 'url' isn't followed by '=' (or just whitespace+'='), fall
            // through with the original span — caller treats the whole
            // post-separator text as the URL per HTML5's tolerant parsing.
        }
        // Optional surrounding quotes.
        if (span.Length >= 2 && (span[0] == '"' || span[0] == '\''))
        {
            var quote = span[0];
            var end = span[1..].IndexOf(quote);
            if (end >= 0) span = span[1..(end + 1)];
        }
        return span.IsEmpty ? null : span.ToString();
    }

    /// <summary>Per Phase A security hardening A-2 — strip every <c>on*</c>
    /// event-handler attribute from every element. Inert today (NetPdf does not
    /// run scripts), but defense-in-depth for Phase 5's PDF/UA tagged-structure
    /// emission, which could otherwise route an attacker-controlled
    /// <c>onclick="alert(...)"</c> into accessibility metadata. The recognition
    /// rule is the WHATWG HTML Standard's: any attribute whose ASCII-lowercased
    /// name starts with <c>on</c> is a content-attribute event handler. We
    /// don't try to validate the JavaScript inside — the attribute itself is
    /// sufficient signal to drop.</summary>
    private static void StripEventHandlerAttributes(IDocument document, IDiagnosticsSink? sink, string? sourceFile)
    {
        foreach (var element in document.All.ToArray())
        {
            // Snapshot the attribute names; we can't iterate a live attribute
            // collection while removing entries.
            var toRemove = default(System.Collections.Generic.List<string>);
            foreach (var attr in element.Attributes)
            {
                if (attr is null) continue;
                var name = attr.LocalName;
                if (name.Length < 3) continue;
                // ASCII case-insensitive prefix check on "on".
                if ((name[0] == 'o' || name[0] == 'O') && (name[1] == 'n' || name[1] == 'N'))
                {
                    (toRemove ??= new()).Add(name);
                }
            }
            if (toRemove is null) continue;
            foreach (var name in toRemove)
            {
                sink?.Emit(new Diagnostic(
                    DiagnosticCodes.HtmlEventHandlerIgnored001,
                    $"Event-handler attribute '{name}' was found on a <{element.LocalName}> element. The attribute was removed; element + text retained.",
                    DiagnosticSeverity.Info,
                    ToSourceLocation(element, sourceFile)));
                element.RemoveAttribute(name);
            }
        }
    }

    private static SourceLocation ToSourceLocation(IElement element, string? sourceFile)
    {
        // AngleSharp populates IElement.SourceReference only when IsKeepingSourceReferences is on
        // (set above) AND the element actually came from the parser. Synthesized fixup elements
        // can return null; fall back to Unknown so the diagnostic still emits cleanly.
        //
        // Per Phase A review user-recommendation #3 — sanitize the source path
        // here at the HTML emission site, not just at the CSS-side adapter.
        // A host-supplied BaseUri like file:///C:/Users/Foo/secret/index.html
        // would otherwise leak host filesystem topology into HTML diagnostics
        // (HTML-SCRIPT-IGNORED-001, HTML-JAVASCRIPT-URL-IGNORED-001,
        // HTML-EVENT-HANDLER-IGNORED-001). Reduce absolute paths to basename;
        // sentinels + http(s) URLs pass through unchanged.
        var safeFile = sourceFile is null
            ? null
            : NetPdf.Css.Diagnostics.DiagnosticTextSanitizer.SanitizeFilePath(sourceFile);
        if (safeFile == "<unknown>") safeFile = null;
        var position = element.SourceReference?.Position;
        return position is { } pos
            ? new SourceLocation(safeFile, pos.Line, pos.Column)
            : SourceLocation.Unknown;
    }
}
