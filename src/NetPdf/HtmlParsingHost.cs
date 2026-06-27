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

        // Per PR #16 review user-recommendation #1 — pre-parse input length
        // cap. EnforceDomSizeCaps runs AFTER AngleSharp materializes the
        // entire DOM, so an attacker who feeds a 1 GiB HTML string still
        // pays parser CPU + heap for the full tree before per-element
        // truncation begins. Capping the input string up-front bounds that
        // worst case at MaxInputLength characters (32 MiB). Treated as a
        // hard reject rather than truncate: a half-parsed document is
        // worse than a clear failure (truncation can split open tags + the
        // DOM caps then can't tell the difference between "intentional
        // partial" and "attack").
        if (html.Length > MaxInputLength)
        {
            options.Diagnostics?.Emit(new Diagnostic(
                DiagnosticCodes.HtmlInputTooLarge001,
                $"HTML input length {html.Length} exceeds the {MaxInputLength}-character cap; the document was rejected before parsing.",
                DiagnosticSeverity.Warning,
                SourceLocation.Unknown));
            throw new System.IO.InvalidDataException(
                $"HTML input length {html.Length} exceeds the {MaxInputLength}-character cap.");
        }

        var context = BrowsingContext.New(_configuration);
        var address = options.BaseUri?.AbsoluteUri ?? "about:blank";

        var document = await context.OpenAsync(req => req.Content(html).Address(address), ct)
            .ConfigureAwait(false);

        var sourceFile = options.BaseUri?.AbsoluteUri;
        // Per Phase B B-4 — iterative strip until stable. AngleSharp normalization
        // can re-route content (e.g., SVG <foreignObject> containing HTML) so a
        // single strip pass may leave dangerous attributes / elements in the
        // tree. Re-run the three strip walks until a pass finds nothing to
        // remove, capped at MaxStripIterations to bound CPU.
        StripUntilStable(document, options.Diagnostics, sourceFile);
        // Per Phase B B-1 — DoS caps on the parsed DOM. Runs AFTER strip so the
        // element / attribute counts measure the real rendering surface, not
        // material that's about to be removed anyway.
        EnforceDomSizeCaps(document, options.Diagnostics, sourceFile);
        return document;
    }

    /// <summary>Per Phase B B-4 — fixed-point loop over the three strip walks.
    /// Most documents converge after the first pass (nothing to remove);
    /// adversarial documents may need a second pass when AngleSharp's
    /// normalization re-emits content. <see cref="MaxStripIterations"/>
    /// bounds wall-clock CPU. If the loop exits at the cap with dangerous
    /// content still present, emit <c>HTML-STRIP-NOT-STABLE-001</c>.</summary>
    private static void StripUntilStable(IDocument document, IDiagnosticsSink? sink, string? sourceFile)
    {
        for (var iter = 0; iter < MaxStripIterations; iter++)
        {
            StripScripts(document, sink, sourceFile);
            StripJavaScriptUrls(document, sink, sourceFile);
            StripEventHandlerAttributes(document, sink, sourceFile);
            if (!HasDangerousContent(document, out _)) return;
        }
        // Per PR #16 Copilot review #9 — name the surviving kind(s) so the
        // diagnostic is actionable. The doc-comment on
        // HTML-STRIP-NOT-STABLE-001 promised this; the original message
        // didn't deliver it. Probe ONCE here so callers see what didn't
        // converge without us re-iterating the DOM in HasDangerousContent.
        HasDangerousContent(document, out var survivingKinds);
        sink?.Emit(new Diagnostic(
            DiagnosticCodes.HtmlStripNotStable001,
            $"Iterative HTML strip did not converge after {MaxStripIterations} passes; surviving dangerous content: {survivingKinds}.",
            DiagnosticSeverity.Warning,
            SourceLocation.Unknown));
    }

    /// <summary>Per Phase B B-4 — maximum number of strip iterations before
    /// emitting <c>HTML-STRIP-NOT-STABLE-001</c>. Most documents finish in
    /// the first pass (nothing to remove); 4 is generous for layered
    /// SVG / foreignObject / nested-namespace cases without inviting
    /// adversarial amplification.</summary>
    internal const int MaxStripIterations = 4;

    /// <summary>Returns <see langword="true"/> when the document still
    /// contains <c>&lt;script&gt;</c> elements, <c>javascript:</c>-bearing
    /// URL attributes, or <c>on*</c> event-handler attributes after the
    /// most recent strip pass. Fast path: walk the document once + check
    /// element name / attribute names against the strip predicates.</summary>
    /// <param name="document">The document to inspect.</param>
    /// <param name="survivingKinds">Comma-separated list of the kinds that
    /// survived (e.g., <c>"script,onclick,javascript-url"</c>) — empty
    /// when the document is clean. Lets the caller emit an actionable
    /// diagnostic per PR #16 Copilot review #9 without re-walking.</param>
    private static bool HasDangerousContent(IDocument document, out string survivingKinds)
    {
        survivingKinds = string.Empty;
        if (document.DocumentElement is null) return false;
        var hasScript = false;
        var hasOnHandler = false;
        var hasDangerousUrl = false;
        foreach (var el in document.All)
        {
            if (el is null) continue;
            // <script> elements (any namespace).
            if (string.Equals(el.LocalName, "script", System.StringComparison.OrdinalIgnoreCase))
                hasScript = true;
            foreach (var attr in el.Attributes)
            {
                if (attr is null) continue;
                var name = attr.LocalName;
                if (name.Length >= 3
                    && (name[0] == 'o' || name[0] == 'O')
                    && (name[1] == 'n' || name[1] == 'N'))
                {
                    hasOnHandler = true;
                }
                // Surviving javascript: / vbscript: / data: URL on a known
                // url-bearing attribute name. The strip walk covers many
                // attributes by selector; this check is a safety net.
                // An <img src> data: URI with an allowlisted IMAGE mediatype is
                // EXEMPT (img-pipeline cycle) — mirroring the strip walk's
                // exemption; the image pipeline validates it downstream
                // (SafeResourceLoader MIME allowlist + magic-byte decode).
                if (IsKnownUrlAttribute(name) && IsDangerousUrl(attr.Value)
                    && !(string.Equals(el.LocalName, "img", System.StringComparison.OrdinalIgnoreCase)
                        && name is "src" && IsAllowedImageDataUri(attr.Value)))
                {
                    hasDangerousUrl = true;
                }
            }
            // Short-circuit when all three are flipped — nothing more to learn.
            if (hasScript && hasOnHandler && hasDangerousUrl) break;
        }
        if (!hasScript && !hasOnHandler && !hasDangerousUrl) return false;
        var parts = new System.Collections.Generic.List<string>(3);
        if (hasScript) parts.Add("<script>");
        if (hasOnHandler) parts.Add("on* event handler");
        if (hasDangerousUrl) parts.Add("javascript:/vbscript:/data: URL");
        survivingKinds = string.Join(", ", parts);
        return true;
    }

    /// <summary>Per Phase B B-4 — attribute names recognized as URL-bearing
    /// for the <see cref="HasDangerousContent"/> safety net. Mirrors the
    /// strip walk's coverage: every name that <see cref="StripJavaScriptUrls"/>
    /// touches.</summary>
    private static bool IsKnownUrlAttribute(string name) =>
        name is "href" or "src" or "srcset" or "action" or "data" or "poster"
             or "formaction" or "content";

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
        // img[src] EXEMPTS an allowlisted-image-mediatype data: URI (img-pipeline cycle —
        // the self-contained embedded-image path). The Phase A strip predated the image
        // pipeline ("inert today — when Phase 5 wires IResourceLoader…"); now that the wireup
        // exists, the data: payload is validated DOWNSTREAM: SafeResourceLoader's MIME
        // allowlist (text/html polyglots rejected; image/svg+xml is now ALLOWED — Phase 4 SVG
        // part 1 added the XXE-safe, no-script, no-external-fetch SvgRasterizer) + the decoder's
        // magic-byte / sniff check (a payload claiming image/png but carrying HTML fails to
        // decode). javascript: / vbscript: / non-image data: still strip.
        StripDangerousUrlOnAttribute(document, "img[src]", "src", sink, sourceFile, allowImageDataUri: true);
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

        // Per Phase B B-5 — SVG-namespace-specific strip pass. SVG carries
        // executable surfaces beyond what the HTML strip walk above covers:
        //   1. <animate>/<set>/<animateTransform>/<animateMotion> can SET an
        //      element's href / xlink:href / src to a javascript: URL via
        //      attributeName + to/from/values. AngleSharp doesn't apply the
        //      animation, but Phase 5 PDF emission could re-route the value
        //      into clickable annotations. Strip the animation if its target
        //      attribute is URL-bearing AND any of its value forms is dangerous.
        //   2. <use href="javascript:...">: SVG 2 unprefixed form. The HTML
        //      strip walk only covers a/area, not <use>. The xlink:href loop
        //      above covers the prefixed variant; add the unprefixed too.
        //   3. Generic SVG namespace `href` (without xlink prefix) on any
        //      element — defense-in-depth for elements not yet enumerated.
        StripSvgSpecific(document, sink, sourceFile);
    }

    /// <summary>Per Phase B B-5 — strip SVG-specific dangerous-URL surfaces
    /// not caught by the HTML walk: <c>&lt;use href&gt;</c> (and other SVG
    /// elements with unprefixed <c>href</c>), and animation elements
    /// (<c>&lt;animate&gt;</c>, <c>&lt;set&gt;</c>, <c>&lt;animateTransform&gt;</c>,
    /// <c>&lt;animateMotion&gt;</c>) whose <c>attributeName</c> is URL-bearing
    /// and whose <c>to</c> / <c>from</c> / <c>values</c> name a dangerous
    /// scheme.</summary>
    private static void StripSvgSpecific(IDocument document, IDiagnosticsSink? sink, string? sourceFile)
    {
        foreach (var element in document.All.ToArray())
        {
            // Only inspect SVG-namespaced elements; HTML href has been
            // covered by the explicit-attribute strip walk above.
            if (!string.Equals(element.NamespaceUri, NamespaceNames.SvgUri, System.StringComparison.Ordinal))
                continue;

            // Unprefixed href on any SVG element (covers <use href>, <image href>,
            // <pattern href>, <linearGradient href>, etc.). The element-name set
            // for SVG href is large and growing; checking every SVG element
            // with an unprefixed href avoids the maintenance treadmill.
            var href = element.GetAttribute("href");
            if (href is not null && IsDangerousUrl(href))
            {
                sink?.Emit(new Diagnostic(
                    DiagnosticCodes.HtmlJavaScriptUrlIgnored001,
                    $"A {DescribeUrlScheme(href)} URL was found on an SVG <{element.LocalName}> element's href attribute. The attribute was removed.",
                    DiagnosticSeverity.Warning,
                    ToSourceLocation(element, sourceFile)));
                element.RemoveAttribute("href");
            }

            // Animation elements: <animate>, <set>, <animateTransform>,
            // <animateMotion>. If attributeName is URL-bearing AND any of
            // to/from/values contains a dangerous scheme, drop the entire
            // animation — there's no safe partial recovery.
            // Per PR #16 Copilot review #5 + user-recommendation #2 —
            // attributeName carries qualified names too: SVG animation
            // commonly targets `xlink:href`. Strip the prefix before the
            // URL-attribute lookup so `<animate attributeName="xlink:href"
            // to="javascript:...">` doesn't slip past the local-name check.
            if (IsSvgAnimationElement(element.LocalName))
            {
                var attributeName = element.GetAttribute("attributeName");
                if (attributeName is null) continue;
                var localTarget = StripNamespacePrefix(attributeName);
                if (!IsKnownUrlAttribute(localTarget)) continue;

                if (HasDangerousAnimationValue(element))
                {
                    sink?.Emit(new Diagnostic(
                        DiagnosticCodes.HtmlJavaScriptUrlIgnored001,
                        $"An SVG <{element.LocalName}> element targeted '{attributeName}' with a dangerous URL value. The animation element was removed.",
                        DiagnosticSeverity.Warning,
                        ToSourceLocation(element, sourceFile)));
                    element.Remove();
                }
            }
        }
    }

    /// <summary>Per PR #16 Copilot review #5 — strip a leading namespace
    /// prefix (everything up to and including the first <c>:</c>) from a
    /// qualified attribute name. <c>xlink:href</c> → <c>href</c>;
    /// <c>href</c> → <c>href</c> (unchanged when no prefix). Returns the
    /// local-name fragment so URL-attribute lookups work uniformly across
    /// HTML + SVG + qualified attribute references.</summary>
    private static string StripNamespacePrefix(string qualifiedName)
    {
        var colon = qualifiedName.IndexOf(':');
        return colon < 0 ? qualifiedName : qualifiedName[(colon + 1)..];
    }

    /// <summary>True for the four SVG animation elements that can set an
    /// attribute to an attacker-controlled value at "play time": <c>animate</c>,
    /// <c>set</c>, <c>animateTransform</c>, <c>animateMotion</c>. Names
    /// match the SVG spec's element local names case-sensitively (SVG is
    /// case-sensitive in the XML grammar; AngleSharp preserves case in the
    /// SVG namespace).</summary>
    private static bool IsSvgAnimationElement(string localName) =>
        localName is "animate" or "set" or "animateTransform" or "animateMotion";

    /// <summary>Returns true when any of the animation element's value
    /// attributes (<c>to</c>, <c>from</c>, <c>values</c>) contains a
    /// javascript: / vbscript: / data: URL. <c>values</c> is a
    /// semicolon-separated list per SVG Animation; check each segment.</summary>
    private static bool HasDangerousAnimationValue(IElement element)
    {
        if (IsDangerousUrl(element.GetAttribute("to"))) return true;
        if (IsDangerousUrl(element.GetAttribute("from"))) return true;
        var values = element.GetAttribute("values");
        if (string.IsNullOrEmpty(values)) return false;
        foreach (var v in values.Split(';'))
        {
            if (IsDangerousUrl(v.Trim())) return true;
        }
        return false;
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
        IDiagnosticsSink? sink, string? sourceFile, bool allowImageDataUri = false)
    {
        foreach (var element in document.QuerySelectorAll(querySelector).ToArray())
        {
            var value = element.GetAttribute(attributeName);
            if (!IsDangerousUrl(value)) continue;
            // img-pipeline cycle — the img[src] call site opts in: a data: URI with an
            // allowlisted IMAGE mediatype survives for the image pipeline (validated
            // downstream by SafeResourceLoader's MIME allowlist + magic-byte decode).
            if (allowImageDataUri && IsAllowedImageDataUri(value)) continue;
            sink?.Emit(new Diagnostic(
                DiagnosticCodes.HtmlJavaScriptUrlIgnored001,
                $"A <{element.LocalName}> element's {attributeName} attribute carried a {DescribeUrlScheme(value)} URL. The attribute was removed; element text retained.",
                DiagnosticSeverity.Warning,
                ToSourceLocation(element, sourceFile)));
            element.RemoveAttribute(attributeName);
        }
    }

    /// <summary>Whether <paramref name="value"/> is a <c>data:</c> URI whose EXPLICIT mediatype
    /// is on the image decoder's MIME allowlist (img-pipeline cycle —
    /// <see cref="SafeResourceLoader.IsMimeAllowedForKind"/>, the single source of truth:
    /// png / jpeg / gif / webp / bmp / <c>image/svg+xml</c> [the last re-enabled in Phase 4 SVG
    /// part 1 — the renderer is XXE-safe + runs no script + fetches nothing]; <c>text/html</c>
    /// polyglots are still NOT). A data: URI with no mediatype (<c>data:,…</c> /
    /// <c>data:;base64,…</c>) stays dangerous — the embedded-image path requires the author to
    /// declare what it is.</summary>
    private static bool IsAllowedImageDataUri(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var v = value.AsSpan().TrimStart();
        if (v.Length < 5 || !v[..5].Equals("data:", System.StringComparison.OrdinalIgnoreCase))
            return false;
        v = v[5..];
        var end = v.IndexOfAny(';', ',');
        if (end <= 0) return false;   // no mediatype declared → keep stripping.
        var mediatype = v[..end].Trim().ToString();
        return mediatype.Length > 0
            && SafeResourceLoader.IsMimeAllowedForKind(mediatype, ResourceKind.Image);
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

    // ----- Phase B B-1: DOM size caps -------------------------------------

    /// <summary>Per PR #16 review user-recommendation #1 — maximum HTML input
    /// length (UTF-16 chars). Defends against the "1 GiB single-string"
    /// attack: <see cref="EnforceDomSizeCaps"/> can only run after AngleSharp
    /// has already parsed the tree, so a hostile input is bounded only by
    /// the parser's intermediate storage cost without an up-front cap. 32 MiB
    /// (16 M chars × 2 bytes/char) is generous for any real document — the
    /// largest invoices in the corpus are &lt; 200 KiB.</summary>
    internal const int MaxInputLength = 16 * 1024 * 1024;

    /// <summary>Maximum total number of <see cref="IElement"/> nodes in the
    /// post-strip document. Real-world large invoices / reports have a few
    /// thousand elements; 250k is a wide allowance that still cuts off
    /// adversarial documents long before downstream stages start consuming
    /// minutes of CPU on selector matching alone.</summary>
    internal const int MaxElementCount = 250_000;

    /// <summary>Maximum nesting depth of element ancestors. Hand-authored docs
    /// rarely exceed 50; mXSS payloads + zip-bomb-shaped HTML deliberately
    /// nest far past that. 1024 keeps the recursive walks (cascade,
    /// VarResolver, BoxBuilder) inside their stack budgets.</summary>
    internal const int MaxNestingDepth = 1024;

    /// <summary>Maximum number of attributes on a single element. Real
    /// elements have &lt; 20; an attacker piling thousands of <c>data-*</c>
    /// attributes on one element would make AngleSharp's per-attribute
    /// lookups quadratic.</summary>
    internal const int MaxAttributesPerElement = 256;

    /// <summary>Maximum length of a single attribute value. 1 MiB is generous
    /// — base64-encoded inline images on <c>src</c> live in the same
    /// neighborhood — but tight enough that a single <c>data-x="...10MB..."</c>
    /// can't pull the document into the multi-megabyte band by itself.</summary>
    internal const int MaxAttributeValueLength = 1024 * 1024;

    /// <summary>Maximum length of a single text-node's content. Legitimate
    /// large text (verbose chapter, JSON fixture, long table cell) exists,
    /// so the cap is permissive. 4 MiB stops a single <c>&lt;pre&gt;</c>
    /// block from becoming a memory bomb on its own.</summary>
    internal const int MaxTextContentLength = 4 * 1024 * 1024;

    /// <summary>Walk the document depth-first, count elements / attributes /
    /// text-content lengths, and TRUNCATE any region that exceeds the
    /// per-document caps. One <c>HTML-DOM-LIMIT-EXCEEDED-001</c> diagnostic
    /// per violation kind so a pathological doc emits at most ~5 entries —
    /// not one per offending node.</summary>
    private static void EnforceDomSizeCaps(IDocument document, IDiagnosticsSink? sink, string? sourceFile)
    {
        if (document.DocumentElement is null) return;

        var emitted = new System.Collections.Generic.HashSet<string>();
        void EmitOnce(string kind, string message, INode? node)
        {
            if (!emitted.Add(kind)) return;
            sink?.Emit(new Diagnostic(
                DiagnosticCodes.HtmlDomLimitExceeded001,
                message,
                DiagnosticSeverity.Warning,
                node is IElement el ? ToSourceLocation(el, sourceFile) : SourceLocation.Unknown));
        }

        var elementCount = 0;
        var stack = new System.Collections.Generic.Stack<(IElement Node, int Depth)>();
        stack.Push((document.DocumentElement, 0));

        while (stack.Count > 0)
        {
            var (element, depth) = stack.Pop();
            elementCount++;

            if (elementCount > MaxElementCount)
            {
                EmitOnce("count",
                    $"DOM element count exceeded the {MaxElementCount} cap; remaining elements were dropped.",
                    element);
                element.Remove();
                continue;
            }

            if (depth > MaxNestingDepth)
            {
                EmitOnce("depth",
                    $"DOM nesting depth exceeded the {MaxNestingDepth} cap; over-deep subtree was dropped.",
                    element);
                element.Remove();
                continue;
            }

            EnforceAttributeCaps(element, EmitOnce);
            EnforceChildTextLengths(element, EmitOnce);

            // Per PR #16 Copilot review #1 — bound the children-snapshot to
            // the remaining element budget. Without this, a wide
            // <body>{1M children} parents into a 1M-entry IElement[] before
            // the per-pop MaxElementCount check fires. Compute the budget
            // up-front; only materialize that many children + delete the
            // rest in place.
            //
            // Per PR #33 review fix — the original `while (Children.Length > budget)`
            // loop hangs on the 300k-child regression because AngleSharp's
            // ChildElements (the live HtmlCollection backing the
            // .Children property) re-walks the child node list on each
            // .Length read, making the truncation loop O(N²). Replaced
            // with a single forward sibling traversal that walks to the
            // budget boundary once + removes the tail in O(doomed).
            var childCount = element.Children.Length;
            var remainingBudget = MaxElementCount - elementCount;
            if (remainingBudget <= 0)
            {
                // Already at the cap. Drop every child of this element + emit
                // the count diagnostic if not yet emitted.
                EmitOnce("count",
                    $"DOM element count exceeded the {MaxElementCount} cap; remaining elements were dropped.",
                    element);
                var doomed = element.FirstElementChild;
                while (doomed is not null)
                {
                    var next = doomed.NextElementSibling;
                    doomed.Remove();
                    doomed = next;
                }
                continue;
            }
            if (childCount > remainingBudget)
            {
                EmitOnce("count",
                    $"DOM element count exceeded the {MaxElementCount} cap; remaining elements were dropped.",
                    element);
                // Walk forward to the cut-off + remove every sibling
                // from there. Single O(N) traversal.
                var cursor = element.FirstElementChild;
                for (var kept = 0; kept < remainingBudget && cursor is not null; kept++)
                {
                    cursor = cursor.NextElementSibling;
                }
                while (cursor is not null)
                {
                    var next = cursor.NextElementSibling;
                    cursor.Remove();
                    cursor = next;
                }
                childCount = remainingBudget;
            }

            // Now snapshot the (bounded) child set + push for traversal.
            // Push in reverse so depth-first pre-order traversal stays
            // left-to-right when popping.
            if (childCount == 0) continue;
            var children = element.Children.ToArray();
            for (var i = children.Length - 1; i >= 0; i--)
            {
                stack.Push((children[i], depth + 1));
            }
        }
    }

    /// <summary>Trim attributes past <see cref="MaxAttributesPerElement"/> and
    /// clamp any over-long attribute value to <see cref="MaxAttributeValueLength"/>
    /// chars (with U+2026 ellipsis). Emits at most one diagnostic per
    /// violation kind.</summary>
    /// <remarks>
    /// Per PR #16 Copilot review #3 + #4 — the
    /// <c>(namespaceUri, localName)</c> overloads of
    /// <c>IElement.RemoveAttribute</c> + <c>IElement.SetAttribute</c>
    /// expect a LOCAL name (e.g., <c>href</c>), not the qualified form
    /// (<c>xlink:href</c>) that <see cref="IAttr.Name"/> returns for
    /// namespaced attributes. Pass <see cref="IAttr.LocalName"/> instead.
    /// For non-namespaced attributes <c>NamespaceUri</c> is <see langword="null"/>;
    /// the no-namespace overload then handles those correctly.
    /// </remarks>
    private static void EnforceAttributeCaps(IElement element, System.Action<string, string, INode?> emitOnce)
    {
        // Snapshot the attribute names; the live attribute collection
        // invalidates as we remove entries.
        var attrSnapshot = element.Attributes.Where(a => a is not null).ToArray();
        if (attrSnapshot.Length > MaxAttributesPerElement)
        {
            emitOnce("attr-count",
                $"Element <{element.LocalName}> exceeded the {MaxAttributesPerElement} attributes-per-element cap; excess attributes removed.",
                element);
            for (var i = MaxAttributesPerElement; i < attrSnapshot.Length; i++)
            {
                RemoveAttributeNamespaceAware(element, attrSnapshot[i]!);
            }
            attrSnapshot = element.Attributes.Where(a => a is not null).ToArray();
        }
        foreach (var attr in attrSnapshot)
        {
            if (attr is null) continue;
            var value = attr.Value;
            if (value is null) continue;
            if (value.Length <= MaxAttributeValueLength) continue;
            emitOnce("attr-length",
                $"An attribute value exceeded the {MaxAttributeValueLength / 1024} KiB cap; value clamped.",
                element);
            // U+2026 sentinel marks the truncation point; downstream code
            // that decodes the value (e.g., url() / srcset) will simply fail
            // to resolve — better than processing megabytes of attacker text.
            SetAttributeNamespaceAware(element, attr,
                value[..MaxAttributeValueLength] + "…");
        }
    }

    /// <summary>Namespace-aware attribute removal. <see cref="IAttr.NamespaceUri"/>
    /// is <see langword="null"/> for plain HTML attributes (<c>href</c>,
    /// <c>class</c>) and a real URI for namespaced attributes
    /// (<c>xlink:href</c> → <c>http://www.w3.org/1999/xlink</c>). Picks the
    /// matching <c>IElement.RemoveAttribute</c> overload so the right
    /// attribute drops in either case.</summary>
    private static void RemoveAttributeNamespaceAware(IElement element, IAttr attr)
    {
        if (string.IsNullOrEmpty(attr.NamespaceUri))
        {
            element.RemoveAttribute(attr.Name);
        }
        else
        {
            element.RemoveAttribute(attr.NamespaceUri, attr.LocalName);
        }
    }

    /// <summary>Namespace-aware attribute set. Mirrors
    /// <see cref="RemoveAttributeNamespaceAware"/> — pick the local-name
    /// overload for namespaced attributes so we don't end up with two
    /// attributes (the original <c>xlink:href</c> + a freshly-created
    /// <c>xlink:href</c> in the no-namespace bucket).</summary>
    private static void SetAttributeNamespaceAware(IElement element, IAttr attr, string newValue)
    {
        if (string.IsNullOrEmpty(attr.NamespaceUri))
        {
            element.SetAttribute(attr.Name, newValue);
        }
        else
        {
            element.SetAttribute(attr.NamespaceUri, attr.LocalName, newValue);
        }
    }

    /// <summary>Walk the element's text-node children and clamp any whose
    /// content exceeds <see cref="MaxTextContentLength"/>. Element children
    /// are not visited here — the depth-first walk in
    /// <see cref="EnforceDomSizeCaps"/> visits them on their own.</summary>
    /// <remarks>Per PR #16 Copilot review #2 — clamped values include the
    /// U+2026 ellipsis sentinel to match the contract documented on
    /// <c>HTML-DOM-LIMIT-EXCEEDED-001</c> + the parallel attribute-value
    /// clamp in <see cref="EnforceAttributeCaps"/>. Lets a downstream reader
    /// know visually that the content was cut off rather than naturally
    /// short.</remarks>
    private static void EnforceChildTextLengths(IElement element, System.Action<string, string, INode?> emitOnce)
    {
        foreach (var child in element.ChildNodes)
        {
            if (child is not IText text) continue;
            var data = text.Data;
            if (data is null || data.Length <= MaxTextContentLength) continue;
            emitOnce("text-length",
                $"A text node exceeded the {MaxTextContentLength / (1024 * 1024)} MiB cap; content clamped.",
                element);
            text.Data = data[..MaxTextContentLength] + "…";
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
