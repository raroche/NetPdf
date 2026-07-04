// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// Stable string constants for every diagnostic code emitted by NetPdf. The single
/// source of truth for the registry is <c>docs/diagnostics-codes.md</c>; the constants
/// here let emission sites and tests share one literal so a typo at the call site
/// is impossible. Constants are kept <see langword="internal"/>: consumers receive the
/// codes as <see cref="Diagnostic.Code"/> string values, not via a named-constant API.
/// </summary>
internal static class DiagnosticCodes
{
    // region HTML-*

    /// <summary>
    /// A <c>&lt;script&gt;</c> element was encountered. NetPdf does not execute JavaScript in v1.
    /// The element was removed from the rendering tree. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string HtmlScriptIgnored001 = "HTML-SCRIPT-IGNORED-001";

    /// <summary>
    /// An <c>href</c> / <c>xlink:href</c> / <c>action</c> / <c>src</c> / <c>data</c> /
    /// <c>content</c> attribute carried a <c>javascript:</c> / <c>vbscript:</c> /
    /// <c>data:</c> URL. The attribute was removed so the URL is not honored downstream;
    /// the surrounding element + its text content remain. Phase A (Phase 2 deep review
    /// security pass) widened the strip beyond the original <c>&lt;a&gt;</c>/<c>&lt;area&gt;</c>
    /// /<c>xlink:href</c> coverage to also include <c>&lt;form action&gt;</c>,
    /// <c>&lt;iframe src&gt;</c>, <c>&lt;object data&gt;</c>, <c>&lt;embed src&gt;</c>,
    /// <c>&lt;base href&gt;</c>, and <c>&lt;meta http-equiv="refresh" content="0;url=..."&gt;</c>.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string HtmlJavaScriptUrlIgnored001 = "HTML-JAVASCRIPT-URL-IGNORED-001";

    /// <summary>
    /// An <c>on*</c> event-handler attribute (e.g., <c>onclick</c>, <c>onload</c>,
    /// <c>onerror</c>, <c>onmouseover</c>) was found on an element. NetPdf does not
    /// execute scripts in v1, so the handler is inert today — but Phase 5 PDF/UA
    /// emission could surface attribute values into accessibility metadata. The
    /// attribute is stripped at parse time as a defense-in-depth measure.
    /// Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string HtmlEventHandlerIgnored001 = "HTML-EVENT-HANDLER-IGNORED-001";

    /// <summary>
    /// A parsed HTML document exceeded one of the per-document DoS caps:
    /// total element count, nesting depth, attributes per element, attribute
    /// value length, or text-node content length. The offending region was
    /// truncated (excess elements / attributes removed; excess text /
    /// attribute-value strings clamped) so downstream stages still see a
    /// rendering-shaped document. One diagnostic is emitted per violation
    /// kind, naming the cap that fired. Per Phase B B-1.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string HtmlDomLimitExceeded001 = "HTML-DOM-LIMIT-EXCEEDED-001";

    /// <summary>
    /// The iterative HTML strip pass (per Phase B B-4 — defense against
    /// AngleSharp normalization or SVG <c>&lt;foreignObject&gt;</c>
    /// re-introducing stripped script / javascript-URL / event-handler
    /// content) failed to converge after the maximum iteration cap.
    /// Dangerous content may still be present in the DOM. The diagnostic
    /// message names the surviving kind(s) — <c>&lt;script&gt;</c>,
    /// <c>on*</c> event handler, or <c>javascript:</c>/<c>vbscript:</c>/<c>data:</c>
    /// URL — so the author can investigate.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string HtmlStripNotStable001 = "HTML-STRIP-NOT-STABLE-001";

    /// <summary>
    /// The HTML input length exceeded the per-document character cap
    /// (<see cref="HtmlParsingHost.MaxInputLength"/>). The parse is
    /// rejected before AngleSharp materializes the tree — defends against
    /// "1 GiB single-string" attacks where post-parse caps would only
    /// fire after the parser had already consumed memory + CPU on the
    /// full input. Per Phase B B-1 follow-up (PR #16 review).
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string HtmlInputTooLarge001 = "HTML-INPUT-TOO-LARGE-001";

    // endregion HTML-*

    // region CSS-*

    /// <summary>
    /// A CSS rule was malformed and skipped. The cascade resolver emits this when a rule's
    /// selector text fails to parse (e.g., unsupported pseudo-class, unterminated function).
    /// The rest of the stylesheet still loads. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssParseWarning001 = "CSS-PARSE-WARNING-001";

    /// <summary>
    /// A <c>:has()</c> selector was encountered. NetPdf's v1 contract is that <c>:has()</c>
    /// parses but never matches — the rule has no rendering effect. Roadmap v1.4 will plug
    /// in real <c>:has()</c> evaluation. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssHasRenderingNotImplemented001 = "CSS-HAS-RENDERING-NOT-IMPLEMENTED-001";

    /// <summary>
    /// A <c>background-image</c> form (or one of its variant longhands) is outside the engine's
    /// supported set, so it was ignored (any <c>background-color</c> still paints) — surfaced once
    /// per render rather than dropped silently. Supported: a single <c>url(...)</c> / <c>none</c>,
    /// a linear / radial / conic gradient (incl. their <c>repeating-</c> forms), and a multi-layer
    /// (comma-separated) list of those. Still surfaced here: an unrecognized <c>background-image</c>
    /// value; a multi-layer list with an unparseable layer; a page-margin-box gradient / multi-layer
    /// list (margin boxes take a single <c>url(...)</c>); a <c>background-repeat</c>/<c>-size</c>/
    /// <c>-position</c> value outside the supported set; a gradient <c>background-size</c>/<c>-position</c>
    /// in a non-absolute unit (e.g. <c>em</c>) or other unsupported VALUE (the longhand falls back to its
    /// initial); and a gradient whose <c>background-size</c>/<c>-repeat</c> would tile beyond the tile cap
    /// (painted once, untiled). Gradient <c>background-origin</c>/<c>-clip</c> and SUPPORTED
    /// <c>-size</c>/<c>-position</c>/<c>-repeat</c> (absolute / percentage sizes, keyword / length
    /// positions, repeat / no-repeat / repeat-x / repeat-y / space / round) ARE honored (tiled).
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssBackgroundImageUnsupported001 = "CSS-BACKGROUND-IMAGE-UNSUPPORTED-001";

    /// <summary>
    /// Phase 4 — a <c>box-shadow</c> with a non-zero blur radius was painted via the Skia raster
    /// fallback (the shadow shape is rasterized at <c>DevicePixelRatio × 96</c> DPI and placed as
    /// a PNG XObject) because PDF has no native Gaussian-blur primitive. Sharp (blur = 0) shadows
    /// paint as native filled rects. Surfaced once per render.
    /// Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string CssBoxShadowBlurRaster001 = "CSS-BOXSHADOW-BLUR-RASTER-001";

    /// <summary>
    /// Phase 4 — a CSS <c>conic-gradient</c> / <c>repeating-conic-gradient</c> background was
    /// painted via the Skia raster fallback (a sweep gradient rasterized at <c>2×</c> the box size
    /// and placed as a PNG XObject) because PDF has no native conic/sweep shading. Linear + radial
    /// gradients stay PDF-native shadings. Per-stop alpha is preserved (the raster carries an alpha
    /// <c>/SMask</c>). Surfaced once per render. Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string CssConicGradientRaster001 = "CSS-CONIC-GRADIENT-RASTER-001";

    /// <summary>
    /// Phase 4 — a <c>conic-gradient</c> / <c>repeating-conic-gradient</c> could NOT be rasterized
    /// because the sweep bitmap would exceed the 4096 px (or 4 Mpx total) cap, so the gradient was
    /// SKIPPED (the background-color shows). Distinct from the Info <see cref="CssConicGradientRaster001"/>
    /// (a successful raster fallback) so the over-cap loss reads as a Warning. Once per render.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssConicGradientUnsupported001 = "CSS-CONIC-GRADIENT-UNSUPPORTED-001";

    /// <summary>
    /// Phase 4 — a TRANSLUCENT <c>linear-gradient</c> / <c>radial-gradient</c> (a stop with alpha
    /// &lt; 1) could NOT be rasterized because the alpha bitmap would exceed the 4096 px (or 4 Mpx
    /// total) cap. A native PDF axial / radial shading is DeviceRGB (no alpha), so rather than DROP
    /// the transparency by painting an opaque approximation, the gradient is SKIPPED (the
    /// background-color shows). Opaque linear / radial gradients always stay native shadings and are
    /// never affected. Once per render. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssGradientAlphaUnsupported001 = "CSS-GRADIENT-ALPHA-UNSUPPORTED-001";

    /// <summary>
    /// Phase 4 — a CSS <c>filter</c> on an image was applied via the Skia raster fallback (the
    /// decoded image is run through the filter chain — color matrices for grayscale / sepia / invert
    /// / brightness / contrast / saturate / hue-rotate / opacity, plus blur / drop-shadow — and
    /// re-embedded as a raster XObject) because PDF has no native filter primitive. Surfaced once per
    /// render. Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string CssFilterRasterFallback001 = "CSS-FILTER-RASTER-FALLBACK-001";

    /// <summary>
    /// Phase 4 — a CSS <c>filter</c> on a NON-replaced element (a <c>&lt;div&gt;</c> / text box, not
    /// an <c>&lt;img&gt;</c>) was IGNORED: filtering a general element's rendered subtree needs a
    /// Skia subtree renderer NetPdf does not have yet (the painter draws straight to PDF), so the
    /// element painted UNFILTERED. Filters on images ARE applied. A tracked follow-up. Surfaced once
    /// per render. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssFilterElementUnsupported001 = "CSS-FILTER-ELEMENT-UNSUPPORTED-001";

    /// <summary>
    /// Phase 4 — a CSS <c>filter</c> VALUE on an <c>&lt;img&gt;</c> could not be parsed into the
    /// supported function set (an <c>url(#id)</c> SVG-filter reference, an unknown function, an
    /// out-of-range / bad argument, or an unresolvable color), so the filter was DROPPED and the image
    /// painted UNFILTERED. Surfaced once per render so deferred <c>url()</c> filters + bad values are
    /// visible. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssFilterUnsupported001 = "CSS-FILTER-UNSUPPORTED-001";

    /// <summary>
    /// Phase 4 — a <c>clip-path: path("…")</c> string could not be PARSED (or exceeded the path-complexity
    /// cap), so it was not applied and the element painted UNCLIPPED. A parseable, in-budget <c>path()</c> is
    /// now clipped NATIVELY (the SVG path → PDF path operators + <c>W n</c> / <c>W* n</c>, curves preserved),
    /// as are the basic shapes <c>inset</c> / <c>circle</c> / <c>ellipse</c> / <c>polygon</c>. Surfaced once
    /// per render. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssClipPathRasterFallback001 = "CSS-CLIP-PATH-RASTER-FALLBACK-001";

    /// <summary>
    /// Phase 4 — a <c>clip-path</c> on an element with CHILDREN clipped only the element's OWN
    /// decoration (background / border / image), not its descendant content — a general subtree clip
    /// needs the Skia subtree renderer NetPdf doesn't have yet. The descendants painted unclipped.
    /// Surfaced once per render. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssClipPathSubtreeUnsupported001 = "CSS-CLIP-PATH-SUBTREE-UNSUPPORTED-001";

    /// <summary>
    /// Phase 4 — a non-<c>none</c> <c>clip-path</c> value could not be applied because it isn't a
    /// supported basic shape: a <c>url(#clip)</c> SVG-reference clip, a bare <c>&lt;geometry-box&gt;</c>
    /// keyword, a font-relative length (em/rem) the parser can't resolve, or otherwise malformed
    /// syntax. The element painted UNCLIPPED rather than dropping content silently. Surfaced once per
    /// render. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssClipPathUnsupported001 = "CSS-CLIP-PATH-UNSUPPORTED-001";

    /// <summary>
    /// Phase 4 — a non-<c>url()</c> <c>border-image-source</c> (e.g. a gradient) was dropped; only
    /// <c>url()</c> sources are supported (the normal border paints instead). Edge tiling
    /// (<c>border-image-repeat</c>: repeat / round / space) and <c>border-image-width</c> / <c>-outset</c>
    /// are now honored, so they no longer trigger this. Surfaced once per render.
    /// Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string CssBorderImageUnsupported001 = "CSS-BORDER-IMAGE-UNSUPPORTED-001";

    /// <summary>
    /// Phase 4 — a <c>mask</c> / <c>mask-image</c> on an <c>&lt;img&gt;</c> was applied via the Skia raster
    /// fallback (PDF has no native CSS-mask primitive): the image's alpha was multiplied by the mask
    /// image's alpha and the result re-embedded as a raster XObject + <c>/SMask</c>. Surfaced once per
    /// render. Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string CssMaskRasterFallback001 = "CSS-MASK-RASTER-FALLBACK-001";

    /// <summary>
    /// Phase 4 — a <c>mask</c> / <c>mask-image</c> on a NON-image element was ignored: masking a general
    /// element's rendered subtree needs the Skia subtree renderer NetPdf doesn't have yet, so the element
    /// painted UNMASKED. Masks on <c>&lt;img&gt;</c> elements ARE applied. Surfaced once per render.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssMaskElementUnsupported001 = "CSS-MASK-ELEMENT-UNSUPPORTED-001";

    /// <summary>
    /// Phase 4 — an element with <c>opacity &lt; 1</c> was composited with a CONSTANT alpha applied per
    /// painting operation (<c>/ca</c>+<c>/CA</c> ExtGState) rather than as an isolated transparency GROUP.
    /// This is exact for a non-self-overlapping element (the common case); an element whose own background,
    /// border, and text overlap composites slightly differently than true group opacity. Faithful group
    /// isolation needs a transparency-group Form XObject (the deferred IPaintTarget epic). Surfaced once per
    /// render. Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string CssOpacityGroupApproximated001 = "CSS-OPACITY-GROUP-APPROXIMATED-001";

    /// <summary>
    /// Phase 4 — an <c>&lt;a href&gt;</c> hyperlink was NOT emitted as a PDF <c>/Link</c> annotation because
    /// its URI scheme is not on the safe allowlist (<c>http</c> / <c>https</c> / <c>mailto</c>). Blocked
    /// schemes include <c>file:</c>, <c>data:</c>, <c>javascript:</c>, <c>ftp:</c>, custom schemes, and a
    /// relative href that can't resolve against a safe <c>http(s)</c> base. The link text still renders;
    /// only the clickable annotation is dropped. Surfaced once per render.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LinkUriUnsupported001 = "LINK-URI-UNSUPPORTED-001";

    /// <summary>
    /// Phase 4 — a rendered SVG image used a feature the renderer doesn't support, so part of it did not
    /// draw: an <c>&lt;image&gt;</c> with an EXTERNAL / non-<c>data:</c> href (no fetch), a <c>filter</c> with
    /// an unsupported primitive (the supported set is <c>feGaussianBlur</c> / <c>feOffset</c> /
    /// <c>feColorMatrix</c> / <c>feDropShadow</c> / <c>feFlood</c> / <c>feMerge</c> / <c>feComposite</c> /
    /// <c>feBlend</c> — feImage/feTile/feTurbulence/… aren't), an unsupported filter input
    /// (<c>BackgroundImage</c>/<c>FillPaint</c>/…) or a filter region / primitive subregion / <c>*Units</c>, a
    /// <c>filter</c> / <c>marker</c> / <c>clip-path</c> / <c>mask</c> / <c>textPath</c> referencing a target of
    /// the wrong kind (or an unresolved id), an unresolved gradient/pattern ref (<c>fill="url(#…)"</c> →
    /// painted transparent, NOT black), an element the renderer doesn't draw (e.g. <c>&lt;foreignObject&gt;</c>
    /// / <c>&lt;switch&gt;</c>), or content truncated by the depth / element budget. Shapes, paths, text (incl.
    /// <c>%</c>/<c>em</c> coordinates, spacing, per-glyph <c>rotate</c>, <c>dominant-baseline</c>,
    /// <c>textPath</c> on any shape), gradients, <c>&lt;pattern&gt;</c> paint servers, <c>filter</c> graphs
    /// (named <c>in</c>/<c>result</c> routing), <c>clip-path</c>/<c>mask</c>/<c>marker</c> references (markers
    /// cascade), full <c>preserveAspectRatio</c>, <c>&lt;use&gt;</c>/<c>&lt;symbol&gt;</c> (with the viewport
    /// clip+scale), <c>data:</c> images, stroke dashes, opacity, and nested viewports DO render. Surfaced once
    /// per SVG image.
    /// Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string CssSvgUnsupported001 = "CSS-SVG-UNSUPPORTED-001";

    /// <summary>
    /// Phase 4 — a <c>box-shadow</c> form NetPdf does not paint exactly was ignored or
    /// approximated: a value whose offsets / blur / spread use a unit the parser can't resolve
    /// (e.g. <c>em</c>/<c>rem</c> — absolute units + <c>px</c> are supported, rejecting the whole
    /// value); or a BLURRED shadow (outset or inset) too large to rasterize (the bitmap would exceed
    /// the 4096 px cap, so it was painted SHARP instead of blurred). Both outset AND inset shadows
    /// are otherwise painted (PR 1 refinements). Any other shadow layers in the list still paint.
    /// Surfaced once per render. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssBoxShadowUnsupported001 = "CSS-BOXSHADOW-UNSUPPORTED-001";

    /// <summary>
    /// Phase 4 — a <c>text-shadow</c> form NetPdf does not paint exactly yet was ignored: an
    /// offset/blur used a unit the parser can't resolve (px + absolute units supported;
    /// <c>em</c>/<c>rem</c>/<c>%</c> not — the whole value is dropped). A non-zero blur is now
    /// RENDERED (see <see cref="CssTextShadowBlurRaster001"/>), not flagged here. Surfaced once per
    /// render. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssTextShadowUnsupported001 = "CSS-TEXTSHADOW-UNSUPPORTED-001";

    /// <summary>
    /// Phase 4 — a <c>text-shadow</c> with a non-zero blur radius was ROUTED to the Skia raster
    /// fallback (the run's glyph outlines are rasterized at <c>2×</c> resolution, Gaussian-blurred
    /// with σ = blur/2, and placed as an image XObject with an alpha <c>/SMask</c>) because PDF has no
    /// native Gaussian-blur primitive. Emitted at resource collection when a blur is present (the
    /// "raster path requested" signal), so it surfaces even if a particular run then falls back: an
    /// over-cap run (or a font Skia can't read) paints a sharp offset, and a fully transparent layer is
    /// skipped. Sharp (blur = 0) shadows stay glyph-shows in the shadow color. Surfaced once per render.
    /// Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string CssTextShadowBlurRaster001 = "CSS-TEXTSHADOW-BLUR-RASTER-001";

    /// <summary>
    /// Phase 4 — a CSS <c>transform</c> contained a 3D function (<c>rotateX</c>/<c>rotateY</c>,
    /// <c>translateZ</c>/<c>translate3d</c>'s z, <c>scale3d</c>'s z, <c>perspective</c>,
    /// <c>rotate3d</c>, <c>matrix3d</c>); it was FLATTENED to 2D (the 2D-meaningful part of
    /// <c>translate3d</c>/<c>scale3d</c> is kept; the rest projects to identity). Surfaced once per
    /// render. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssTransform3DUnsupported001 = "CSS-TRANSFORM-3D-UNSUPPORTED-001";

    /// <summary>
    /// Phase 4 — a CSS <c>transform</c> value could not be parsed into the supported 2D function
    /// set (<c>translate</c>/<c>scale</c>/<c>rotate</c>/<c>skew</c>/<c>matrix</c> + their axis
    /// variants) — an unknown function, a malformed argument, or an angle in an unsupported unit.
    /// (<c>em</c>/<c>rem</c>/<c>%</c> translate offsets and <c>transform-origin</c> lengths DO resolve.)
    /// The element painted UNTRANSFORMED. Surfaced once per render.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssTransformUnsupported001 = "CSS-TRANSFORM-UNSUPPORTED-001";

    /// <summary>
    /// An unrecognized at-rule was preserved in the AST but had no rendering effect — the
    /// cascade resolver couldn't decompose its body or its conditions weren't evaluable.
    /// Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string CssAtRuleUnknown001 = "CSS-AT-RULE-UNKNOWN-001";

    /// <summary>
    /// An <c>@container</c> rule was encountered. NetPdf does not evaluate container queries
    /// in v1 — the contained rules are skipped. Roadmap v1.4 will add container-query
    /// evaluation. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssContainerQueryUnsupported001 = "CSS-CONTAINER-QUERY-UNSUPPORTED-001";

    /// <summary>
    /// A <c>var()</c> chain produced a circular reference (e.g.,
    /// <c>--a: var(--b); --b: var(--a)</c>); the substitution stopped + resolved to the
    /// fallback value or <c>unset</c>. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssVarCircular001 = "CSS-VAR-CIRCULAR-001";

    /// <summary>
    /// A <c>var()</c> substitution exceeded the user-agent's depth or output-length
    /// safety limit. Distinct from a circular reference — the chain is acyclic but
    /// pathological (e.g., long non-cyclic chain past 32 frames, or an exponentially
    /// expanding chain past 1 MiB output). The substitution is treated as "invalid at
    /// computed value time" per CSS Custom Properties L1 §3.5; references resolve to
    /// the fallback or <c>unset</c>. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssVarExpansionLimit001 = "CSS-VAR-EXPANSION-LIMIT-001";

    /// <summary>
    /// A <c>calc()</c> / <c>min()</c> / <c>max()</c> / <c>clamp()</c> / <c>abs()</c> /
    /// <c>sign()</c> expression was syntactically invalid or had a type mismatch
    /// (e.g., adding a number to a length). Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssCalcInvalid001 = "CSS-CALC-INVALID-001";

    /// <summary>
    /// A <c>calc()</c> expression divided by zero. Per CSS Values L4 §10.1, the result
    /// is treated as "invalid at computed value time"; the property's initial value
    /// applies. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssCalcDivByZero001 = "CSS-CALC-DIV-BY-ZERO-001";

    /// <summary>
    /// A property's value text could not be parsed into the property's typed value
    /// per its declared <c>PropertyType</c> (e.g., <c>color: not-a-color</c>,
    /// <c>width: nonsense</c>, <c>display: foo</c>). The cascade's "invalid at computed
    /// value time" rule applies — the property's initial value (or the inherited value
    /// for inherited properties) is used. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssPropertyValueInvalid001 = "CSS-PROPERTY-VALUE-INVALID-001";

    /// <summary>
    /// A <c>content:</c> value (on <c>::before</c> / <c>::after</c> / <c>::marker</c>)
    /// used a function / keyword that cycle 1 doesn't yet handle (<c>counter()</c> /
    /// <c>counters()</c> / <c>url()</c> / <c>image()</c> / <c>image-set()</c> /
    /// <c>linear-gradient()</c> / <c>open-quote</c> / <c>close-quote</c> /
    /// <c>no-open-quote</c> / <c>no-close-quote</c>). The pseudo-element generates no
    /// box. Roadmap cycle 2 ships counter machinery + the resource pipeline +
    /// quotation-stack tracking. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssContentFunctionUnsupported001 = "CSS-CONTENT-FUNCTION-UNSUPPORTED-001";

    /// <summary>
    /// A modern <c>attr(name type, fallback)</c> form was rejected — cycle 1 supports
    /// the bare <c>attr(name)</c> form only. The pseudo-element generates no box rather
    /// than silently dropping the type / fallback args. Roadmap cycle 2 delivers the
    /// typed-value pipeline. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssAttrMultiArgUnsupported001 = "CSS-ATTR-MULTI-ARG-UNSUPPORTED-001";

    /// <summary>
    /// A modern color function (<c>oklch()</c> / <c>oklab()</c> / <c>lab()</c> /
    /// <c>lch()</c> / <c>color()</c> / <c>color-mix()</c>) was used in a property
    /// value. Cycle 1 rejects these — the cascade's "invalid at computed value
    /// time" rule applies. Roadmap cycle 2 ships sRGB-conversion so the rendered
    /// color is approximate but visible. Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string CssModernColorFunctionUnsupported001 = "CSS-MODERN-COLOR-FUNCTION-UNSUPPORTED-001";

    /// <summary>
    /// A <c>::before</c> / <c>::after</c> rule targeted a replaced element
    /// (<c>&lt;img&gt;</c> / <c>&lt;video&gt;</c> / <c>&lt;canvas&gt;</c> /
    /// <c>&lt;iframe&gt;</c> / <c>&lt;object&gt;</c> / <c>&lt;embed&gt;</c>); per
    /// CSS Pseudo L4 §3 the pseudo-element is suppressed because replaced elements
    /// are atomic and can't host generated content. The author rule has no effect.
    /// Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string CssPseudoSuppressedOnReplaced001 = "CSS-PSEUDO-SUPPRESSED-ON-REPLACED-001";

    // endregion CSS-*

    // region PAGINATION-*

    /// <summary>
    /// The bounded DP optimizer in <c>NetPdf.Paginate</c> exceeded its
    /// time / candidate-set budget for the document under layout, and the
    /// paginator fell back to greedy pagination (take each break-point as
    /// it arrives, no lookahead). The PDF still emits cleanly; layout
    /// quality is the same as a non-optimizing renderer (typically just
    /// "fine" — orphan / widow / heading-stranding penalties are not
    /// minimized for this document). Per Phase 3 §pagination.
    /// Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string PaginationOptimizerFallback001 = "PAGINATION-OPTIMIZER-FALLBACK-001";

    /// <summary>
    /// A region marked <c>break-inside: avoid</c> (or otherwise un-splittable
    /// per the cost model) is taller than a single fragmentainer (page) and
    /// had to be split anyway. The first piece occupies the remainder of
    /// the current page; the rest cascades onto subsequent pages. PDF
    /// renders correctly but the author's break constraint was violated.
    /// Per Phase 3 §pagination + CSS Fragmentation L3 §3.2 last-resort
    /// fallback. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string PaginationForcedOverflow001 = "PAGINATION-FORCED-OVERFLOW-001";

    // endregion PAGINATION-*

    // region LAYOUT-*

    /// <summary>
    /// Per Phase 3 Task 11 cycle 1 sub-cycle 1 — emitted by the block
    /// layouter when a block container with inline-level children is
    /// encountered but the pipeline did not supply an inline shaper
    /// resolver. The inline children are skipped (rendered as empty
    /// space, equivalent to the pre-sub-cycle-1 behavior) so layout
    /// can still complete. Production callers wire a shaper resolver
    /// when constructing the renderer; the diagnostic exists for
    /// test harnesses and tooling that drive the layouter directly.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutInlineSkippedNoShaperResolver001 = "LAYOUT-INLINE-SKIPPED-NO-SHAPER-RESOLVER-001";

    /// <summary>
    /// Per Phase 3 Task 11 cycle 1 sub-cycle 1 hardening review
    /// Finding #4 — emitted by the block layouter when an inline-only
    /// block contains an atomic inline descendant
    /// (<c>inline-block</c>, <c>inline-flex</c>, <c>inline-grid</c>,
    /// <c>inline-table</c>, <c>inline replaced</c>) whose dedicated
    /// layout pipeline has not yet shipped. The atomic inline is
    /// skipped in the line; surrounding text continues to render.
    /// Sub-cycle 2 will inject atomic-inline placeholders into the
    /// line via per-layouter intrinsic-sizing hooks.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutInlineAtomicNotSupported001 = "LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001";

    /// <summary>
    /// Per Phase 3 Task 11 cycle 1 sub-cycle 1 hardening review
    /// Finding #6 — emitted by the block layouter when the inline
    /// pass throws <see cref="System.NotSupportedException"/> for a
    /// configuration the inline layouter doesn't yet support (e.g.,
    /// per-source-TextRun <c>word-break: keep-all</c> mismatch — CJK
    /// semantics need UAX #24 script detection). The inline-only
    /// block emits no fragment + the block layouter continues with
    /// the next child; the exception message is carried in the
    /// diagnostic's <c>MessageDetail</c>.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutInlineUnsupported001 = "LAYOUT-INLINE-UNSUPPORTED-001";

    /// <summary>
    /// Per Phase 3 Task 12 sub-cycle 1 — emitted by the table
    /// layouter when the input box tree exercises a CSS Tables L3
    /// feature the sub-cycle 1 algorithm doesn't yet implement
    /// (currently: <c>colspan</c> / <c>rowspan</c> cell merging;
    /// sub-cycle 2 will add <c>table-layout: auto</c> /
    /// <c>fixed</c>, <c>border-collapse</c>, captions,
    /// <c>&lt;col&gt;</c> widths, header/footer repetition across
    /// pages). The table renders with the feature silently
    /// ignored. See
    /// <c>docs/deferrals.md#table-auto-fixed-spans-borders</c> for
    /// the full deferral list. Severity:
    /// <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutTableFeatureUnsupported001 = "LAYOUT-TABLE-FEATURE-UNSUPPORTED-001";

    /// <summary>
    /// Per Phase 3 Task 12 sub-cycle 2 hardening Finding 4 — emitted
    /// by the table layouter when a table's cumulative
    /// <c>rowspan × colspan</c> slot count would exceed the
    /// 1,000,000 slot DoS budget. Cells crossing the budget are
    /// capped at <c>rowspan = colspan = 1</c>; the table still
    /// renders (truncated geometry, not dropped content). Defends
    /// against hostile HTML where legal attribute values (e.g.,
    /// <c>rowspan="65534" colspan="1000"</c> on multiple cells) would
    /// force unbounded CPU + memory work in the placement pass.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutTableSlotBudgetExceeded001 = "LAYOUT-TABLE-SLOT-BUDGET-EXCEEDED-001";

    /// <summary>
    /// Per Phase 3 Task 12 sub-cycle 4 hardening Finding 1 — emitted
    /// by the table layouter when the sum of declared column widths
    /// under <c>table-layout: fixed</c> exceeds the table wrapper's
    /// content-inline-size. CSS 2.1 §17.5.2.1 says the table grid's
    /// inline extent grows to fit the declared column widths in that
    /// case — the table overflows its wrapper in the inline axis. The
    /// layouter keeps the declared widths intact (row + caption
    /// fragments grow to the column sum); the diagnostic surfaces the
    /// overflow so authors can tune their declarations.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutTableInlineOverflow001 = "LAYOUT-TABLE-INLINE-OVERFLOW-001";

    /// <summary>
    /// Per Phase 3 Task 12 sub-cycle 5 hardening Finding 4 — emitted
    /// by the table layouter when the auto-table-layout intrinsic-
    /// measurement pass exceeds its per-table speculative-measurement
    /// budget. Each cell normally runs two speculative nested
    /// BlockLayouter passes (min-content at 1px + max-content at 1e6
    /// px) — i.e., 2 ops per cell. For tables with thousands of cells
    /// the cumulative work is bounded by this budget; cells beyond the
    /// cap fall back to <c>(minContent=0,
    /// maxContent=contentInlineSize)</c>, producing a degenerate
    /// equal-split-like distribution rather than DoS-amplifying the
    /// speculative passes. Defends against hostile HTML with
    /// pathologically deep cell content trees in very large tables.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutTableIntrinsicMeasurementBudgetExceeded001 =
        "LAYOUT-TABLE-INTRINSIC-MEASUREMENT-BUDGET-EXCEEDED-001";

    /// <summary>
    /// Emitted by the nested content measurer when speculative content
    /// measurement (the intrinsic min/max-content probes for content-sized
    /// flex / grid / table boxes) nests deeper than its budget. Each
    /// content-sized box can spawn its own probes, so unbounded nesting is
    /// ~exponential; past the cap the innermost box measures as 0-extent
    /// rather than DoS-amplifying. The cap is generous — no real document
    /// nests content-sized boxes this deep, so it never fires in practice.
    /// Defends against hostile HTML with pathologically deep content-sized
    /// box trees. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutMeasureNestingBudgetExceeded001 =
        "LAYOUT-MEASURE-NESTING-BUDGET-EXCEEDED-001";

    /// <summary>
    /// Emitted by the render pipeline when block layout recurses past
    /// <c>BlockLayouter.MaxRecursionDepth</c> (256) — a DoS guard against
    /// pathologically deep untrusted HTML that would otherwise
    /// <c>StackOverflow</c> and halt the process. The DOM parser's own
    /// nesting cap (1024) is higher, so a document between the two limits
    /// reaches layout and trips this guard. Rather than let an untyped
    /// exception escape <c>HtmlPdf.Convert</c>, the pipeline catches the
    /// guard, degrades to a valid PDF built from the content laid out
    /// before the cap, and surfaces this diagnostic (diagnostics-not-
    /// silent-corruption). Distinct from the speculative-measure budget
    /// (<see cref="LayoutMeasureNestingBudgetExceeded001"/>), which
    /// degrades a probe to 0-extent without stopping layout.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutRecursionDepthExceeded001 =
        "LAYOUT-RECURSION-DEPTH-EXCEEDED-001";

    /// <summary>
    /// Per Phase 3 Task 13 cycle 1 hardening Finding 5 — emitted by the
    /// table layouter when the break resolver returns
    /// <c>BreakAction.Rewind</c> at a table row boundary. Cycle 1 does
    /// NOT register per-row checkpoints (the outer block layouter owns
    /// the pre-table rewind frontier), so a resolver-named rewind
    /// checkpoint inside the table is a contract violation. The
    /// layouter falls back to <c>Continue</c> (preserving the pre-
    /// finding behavior) + surfaces this diagnostic so authors /
    /// integrators see the dropped rewind. Per-row checkpoint capture
    /// is cycle 2+ scope.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutTableRewindNotSupported001 =
        "LAYOUT-TABLE-REWIND-NOT-SUPPORTED-001";

    /// <summary>
    /// Per Phase 3 Task 13 cycle 1 hardening Finding 6 — emitted by the
    /// table layouter when a row break would cut through a cell whose
    /// <c>rowspan&gt;1</c> origin row commits on the current page but
    /// whose span extends past the break. Cycle 1 keeps rowspan cells
    /// atomic across pages: the layouter forces the break BEFORE the
    /// rowspan origin row (the whole spanning cell stays together on
    /// the next page) when at least one row + optional captions have
    /// already committed on the current page; otherwise it falls back
    /// to the existing forced-overflow path. CSS Tables L3 §11 spec-
    /// strict rowspan distribution across pages is sub-cycle 6+ scope.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutTableRowspanCrossesPage001 =
        "LAYOUT-TABLE-ROWSPAN-CROSSES-PAGE-001";

    /// <summary>
    /// Per Phase 3 Task 13 cycle 2 — emitted by the table layouter when
    /// the combined <c>&lt;thead&gt;</c> + <c>&lt;tfoot&gt;</c> stack
    /// height (header rows + footer rows) exceeds the fragmentainer's
    /// available block-size, leaving no room to repeat the header +
    /// footer on every page along with any body row. Per CSS Tables L3
    /// §3.6 / §11 the header + footer repeat at the top + bottom of
    /// each page; if they exceed the fragmentainer no body row can fit
    /// on a page that also honors the repeat contract. The layouter
    /// commits the header + footer once (atomically) on the current
    /// page, skips the body to avoid infinite continuation loops, and
    /// surfaces this diagnostic so authors can reduce header / footer
    /// content or widen the page.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutTableHeaderFooterOversized001 =
        "LAYOUT-TABLE-HEADER-FOOTER-OVERSIZED-001";

    /// <summary>
    /// Per Phase 3 Task 14 cycle 2 hardening (Finding #3) — emitted by
    /// the multicol layouter when the author-supplied
    /// <c>column-count</c> exceeds the internal safety cap (= 1000)
    /// and is silently clamped. The clamp protects against the
    /// per-column arithmetic's O(N) cost on adversarial inputs (a
    /// <c>column-count: 1000000</c> would otherwise allocate ~1M
    /// per-column geometry slots + invoke 1M nested BlockLayouter
    /// calls per page). Authors who legitimately hit the cap
    /// (generated CSS, configuration mistakes) see the warning + can
    /// reduce the requested column count. The rendered output will
    /// have AT MOST 1000 columns.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutMulticolColumnCountClamped001 =
        "LAYOUT-MULTICOL-COLUMN-COUNT-CLAMPED-001";

    /// <summary>
    /// Per Phase 3 Task 14 cycle 1 — emitted by the multicol layouter
    /// when a multicol container's in-flow content can't make forward
    /// progress through pagination. Cycle 2 ships multi-page multicol
    /// via <c>MulticolContinuation</c> + cycle 2 hardening Finding #1
    /// lifted the recursion-depth limit on continuation propagation;
    /// a clean multi-page split is no longer an error. The diagnostic
    /// now fires only when a <c>MulticolLayouter</c> resume page
    /// emits zero fragments and its continuation doesn't advance past
    /// the entry index — the forward-progress safety fallback for the
    /// single-oversized-child case (analog to TableLayouter's
    /// single-oversized-row fallback). See
    /// <c>docs/deferrals.md#multicol-balancing-pagination</c>.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutMulticolForcedOverflow001 =
        "LAYOUT-MULTICOL-FORCED-OVERFLOW-001";

    /// <summary>
    /// Per Phase 3 Task 14 cycle 1 hardening (Finding 4) — emitted by
    /// the multicol layouter when an arithmetic combination of
    /// <c>column-count</c> + <c>column-gap</c> would produce
    /// non-finite per-column inline-axis geometry (e.g.,
    /// <c>column-gap: 1e300</c> with 100 columns drives
    /// <c>totalGap = (N-1) * columnGap</c> past <c>double.MaxValue</c>
    /// → <c>Infinity</c>). The layouter clamps the bad value to a
    /// sane cap (column-gap is forced to a value that keeps
    /// <c>totalGap &lt; containerInlineSize / 2</c>) so emission can
    /// continue.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutMulticolNonFiniteGeometry001 =
        "LAYOUT-MULTICOL-NON-FINITE-GEOMETRY-001";

    /// <summary>
    /// Per Phase 3 Task 14 cycle 2 hardening (Finding #1) — emitted
    /// by the block layouter when a float subtree's nested recursion
    /// returns a non-null <c>LayoutContinuation</c> (indicating a
    /// multicol or table inside the float broke mid-emission). Floats
    /// are out-of-flow per CSS 2.2 §9.5; propagating their
    /// continuation requires float-tracking machinery that's an
    /// existing Phase 3 Task 8 deferral. The layouter discards the
    /// returned continuation (atomic-fallback behavior) + surfaces
    /// this diagnostic so authors see the truncation. Fires at most
    /// once per page.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutFloatBreakInsideNested001 =
        "LAYOUT-FLOAT-BREAK-INSIDE-NESTED-001";

    /// <summary>
    /// Per Phase 3 Task 15 L6 post-PR-#66 review F#4 — emitted by the
    /// flex layouter when the flex container's <c>flex-wrap</c> property
    /// resolves to <c>wrap-reverse</c>. L6 ships <c>wrap</c> in full;
    /// <c>wrap-reverse</c> requires an additional cross-axis line-
    /// stacking reversal transform per CSS Flexbox L1 §6.3 ("same as
    /// wrap but the cross-start and cross-end directions are swapped")
    /// which is L7+ scope. Until then the layouter approximates
    /// <c>wrap-reverse</c> as <c>wrap</c>: items wrap correctly in
    /// main-axis DOM order, but the lines stack in the natural cross-
    /// axis direction rather than the reversed direction the author
    /// requested. Without this diagnostic the wrong rendering would be
    /// silent (the CSS declaration parses successfully but behaves like
    /// <c>flex-wrap: wrap</c>). Fires at most once per <c>AttemptLayout</c>
    /// invocation.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutFlexWrapReverseApproximated001 =
        "LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001";

    /// <summary>Row-nowrap intra-item content fragmentation (PR #189 review P2) —
    /// a row-nowrap flex item's measured content block extent reached the practical
    /// intra-item measurement budget cap, so content taller than the cap is clipped
    /// (the atomic measure pass does not paginate). The cap is huge (~10,400 inches),
    /// so this is unreachable for any real document; the page-by-page streaming measure
    /// is the documented follow-up. Surfaced so the truncation is not silent.
    /// Mirrors <c>NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutFlexItemContentTruncated001</c>.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutFlexItemContentTruncated001 =
        "LAYOUT-FLEX-ITEM-CONTENT-TRUNCATED-001";

    /// <summary>
    /// Per Phase 3 Task 17 cycle 1 (Hello World) — emitted by the grid
    /// layouter when a <c>grid-template-rows</c> / <c>-columns</c>
    /// track entry uses a kind that cycle 1 doesn't yet support
    /// (= anything other than <c>&lt;length&gt;</c>: fr / auto /
    /// min-content / max-content / minmax / fit-content / repeat).
    /// Cycle 0a/0b's AST contract parses these forms; cycle 1 only
    /// layouts pixel tracks. Until cycles 2+ expand coverage,
    /// non-length tracks contribute 0 px to the track sum. Fires once
    /// per <c>AttemptLayout</c>. Mirrors
    /// <c>NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridTrackKindUnsupported001</c>.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutGridTrackKindUnsupported001 =
        "LAYOUT-GRID-TRACK-KIND-UNSUPPORTED-001";

    /// <summary>
    /// Per Phase 3 Task 17 cycle 1 (Hello World) + post-PR-#92 review
    /// F5 — emitted by the grid layouter when an item's declared
    /// placement uses a value cycle 1 doesn't yet support: <c>span N</c>
    /// / <c>&lt;custom-ident&gt;</c> (= named line) /
    /// <c>&lt;custom-ident&gt; N</c>, OR when an item declares a
    /// non-default <c>grid-{row,column}-end</c> (= author requested a
    /// multi-cell span). Cycle 1's placement algorithm treats these
    /// as auto-placement + emits this diagnostic. Cycle 6 ships span;
    /// cycle 7 ships named lines + areas. Fires per item per axis.
    /// Mirrors
    /// <c>NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridPlacementApproximated001</c>.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutGridPlacementApproximated001 =
        "LAYOUT-GRID-PLACEMENT-APPROXIMATED-001";

    /// <summary>
    /// Per Phase 3 Task 17 cycle 1 (Hello World) — emitted by the grid
    /// layouter when a grid item is placed at a cell OUTSIDE the
    /// explicit grid bounds (= row/column index exceeds the declared
    /// track count, OR a 0-track grid has no cells to place into).
    /// Per CSS Grid §7.5 the implicit grid should auto-generate tracks
    /// via <c>grid-auto-rows</c> / <c>grid-auto-columns</c>; cycle 1
    /// doesn't yet support implicit tracks, so the item silently drops
    /// (no fragment emitted). Cycle 6 ships implicit-track generation.
    /// Fires once per dropped item. Mirrors
    /// <c>NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridImplicitTrackUnsupported001</c>.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutGridImplicitTrackUnsupported001 =
        "LAYOUT-GRID-IMPLICIT-TRACK-UNSUPPORTED-001";

    /// <summary>
    /// Per Phase 3 Task 17 cycle 1 post-PR-#92 review F9 — emitted by
    /// the grid layouter when cumulative track positions or fragment
    /// geometry produce a non-finite value (NaN / ±Infinity). Individual
    /// track sizes are validated finite at AST-construction time, but
    /// cumulative sums can still overflow when summing very large finite
    /// tracks (hostile CSS). The layouter detects the non-finite
    /// cumulative position + skips item emission so paint / PDF cannot
    /// be corrupted by garbage geometry. Mirrors
    /// <c>NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridNonFiniteGeometry001</c>
    /// + the LayoutMulticolNonFiniteGeometry001 pattern. Fires once per
    /// dispatch.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutGridNonFiniteGeometry001 =
        "LAYOUT-GRID-NON-FINITE-GEOMETRY-001";

    /// <summary>
    /// Per Phase 3 Task 17 cycle 2 post-PR-#93 review F2 — emitted
    /// when a grid item's cell resolves to a zero-sized area AND the
    /// item has child content. The outer item fragment still emits at
    /// the zero-sized geometry, but cycle 2's sub-BlockLayouter
    /// dispatch can't run with a non-positive content extent
    /// (= FragmentainerContext's positive-size validation), so the
    /// inner content is skipped + this diagnostic surfaces the silent
    /// drop. A zero-area grid cell is NOT equivalent to
    /// <c>display: none</c> per CSS — content should overflow or be
    /// clipped per the painter's overflow rules; cycle 3 ships the
    /// zero-area inner-layout strategy. Mirrors
    /// <c>NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridZeroSizedCellContentSkipped001</c>.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutGridZeroSizedCellContentSkipped001 =
        "LAYOUT-GRID-ZERO-SIZED-CELL-CONTENT-SKIPPED-001";

    /// <summary>
    /// Per Phase 3 Task 17 cycle 2 post-PR-#93 review F3 — emitted
    /// when a grid container with auto block-size (or auto inline-size
    /// for column-flow grids) contains <c>fr</c> tracks on the same
    /// axis. Per CSS Grid §11.7 flexible tracks under indefinite
    /// available space resolve via intrinsic / max-content sizing
    /// (= cycle 3 scope). Cycle 2's pre-measure can't fold fr
    /// contributions into the wrapper's natural extent without that
    /// intrinsic branch, so fr tracks on the indefinite axis collapse
    /// to 0 + this diagnostic surfaces the approximation. Fires once
    /// per AttemptLayout. Mirrors
    /// <c>NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridFrUnderIndefiniteApproximated001</c>.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutGridFrUnderIndefiniteApproximated001 =
        "LAYOUT-GRID-FR-UNDER-INDEFINITE-APPROXIMATED-001";

    /// <summary>
    /// Per Phase 3 Task 17 cycle 4 post-PR-#95 review hardening
    /// (C3 + H3) — emitted when a grid track uses a percentage value
    /// at the top level OR inside a <c>minmax()</c> / <c>fit-content()</c>
    /// sub-arg that the cycle-4 sizing path doesn't yet resolve.
    /// Percentages are silently treated as 0 to prevent silent
    /// pixel-vs-percent mismatch (= preferable to interpreting
    /// <c>50%</c> as <c>50px</c>). Cycle 5+ ships percentage
    /// resolution against the container's definite extent. Fires
    /// once per AttemptLayout. Mirrors
    /// <c>NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridPercentageTrackApproximated001</c>.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutGridPercentageTrackApproximated001 =
        "LAYOUT-GRID-PERCENTAGE-TRACK-APPROXIMATED-001";

    /// <summary>
    /// Per Phase 3 Task 17 cycle 4 post-PR-#95 review hardening
    /// (R2 + T4) — emitted when a grid track list's <c>repeat(N, ...)</c>
    /// expansion would exceed <c>TrackList.MaxExpandedTrackCount</c>
    /// (50,000). Expansion truncates at the cap to prevent unbounded
    /// memory allocation from hostile CSS like
    /// <c>repeat(10000, 1px 1fr 1px 1fr 1fr 1px)</c> (= 60,000 expanded
    /// entries). Items in the truncated tail are silently dropped.
    /// Fires once per AttemptLayout. Mirrors
    /// <c>NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridMaxExpandedTracksTruncated001</c>.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutGridMaxExpandedTracksTruncated001 =
        "LAYOUT-GRID-MAX-EXPANDED-TRACKS-TRUNCATED-001";

    /// <summary>
    /// Per Phase 3 Task 17 cycle 5 — emitted when a single grid row's
    /// height exceeds the fragmentainer's block-axis budget on its
    /// first attempt. Per CSS Fragmentation L3 §4.4 progress rule the
    /// row is force-emitted (= "you must commit at least one element
    /// per page" or pagination would deadlock); content overflows the
    /// fragmentainer-block-end region. Cycle 5 ships row-atomic
    /// pagination only; intra-row item splitting is post-v1. Mirrors
    /// <c>NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridForcedOverflow001</c>.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutGridForcedOverflow001 =
        "LAYOUT-GRID-FORCED-OVERFLOW-001";

    /// <summary>
    /// Per Phase 3 Task 17 cycle 5 + post-PR-#96 review F3 — emitted
    /// when a grid resume continuation arrives at a page with a
    /// different <c>contentInlineSize</c> than the cache was built for
    /// (e.g., left/right pages with different margins, or nested
    /// fragmentainers). The cached fr / Maximize'd column widths are
    /// stale at the new inline size; the cache is invalidated + a
    /// fresh §11 sizing + §8.5 placement pass runs. Note that sparse
    /// auto-placement is order-sensitive — a different placement may
    /// emerge from the fresh resolve if items were partially emitted
    /// on the prior page. Mirrors
    /// <c>NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridResumeInlineSizeMismatch001</c>.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutGridResumeInlineSizeMismatch001 =
        "LAYOUT-GRID-RESUME-INLINE-SIZE-MISMATCH-001";

    /// <summary>
    /// Per Phase 3 Task 17 cycle 5 + post-PR-#96 review F5 — emitted
    /// when a grid resume cache is rejected for a structural anomaly:
    /// the cache's GridIdentity doesn't match the receiving rootBox
    /// (= cache routed to the wrong grid), array lengths are
    /// inconsistent, an item placement is out of bounds, geometry is
    /// non-finite, or an item payload is not a <c>Box</c>. The cache
    /// is rejected + a fresh resolve runs. Indicates a layouter-
    /// dispatch bug in the BlockLayouter continuation routing. Mirrors
    /// <c>NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes.LayoutGridResumeCacheRejected001</c>.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutGridResumeCacheRejected001 =
        "LAYOUT-GRID-RESUME-CACHE-REJECTED-001";

    // endregion LAYOUT-*

    // region PDF-*

    /// <summary>
    /// Per Phase 5 layout→PDF wiring cycle 2 — emitted by the public facade when a
    /// document's laid-out content exceeds a single page but the cycle-2 renderer
    /// emits only the first page. The first page renders correctly; content that
    /// would flow onto subsequent pages is not yet emitted. The multi-page driver
    /// (looping <c>BlockLayouter.AttemptLayout</c> over continuations, a page per
    /// fragmentainer) is a tracked follow-up — see
    /// <c>docs/deferrals.md#layout-to-pdf-pipeline</c>. Surfaced rather than
    /// silently dropped per the diagnostics-not-silent-corruption rule. Fires at
    /// most once per conversion. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string PdfContentOverflowTruncated001 = "PDF-CONTENT-OVERFLOW-TRUNCATED-001";

    /// <summary>
    /// Per SEC-5 — a conversion tried to emit more pages than the configured
    /// <c>SecurityPolicy.MaxPages</c> cap (a policy-driven, tighter-for-untrusted
    /// layer above the pipeline's hard 20 000-page emergency backstop). Rendering
    /// stops at the cap and drops the remaining content; a page-amplifying untrusted
    /// document is bounded rather than exhausting memory / time. Raise
    /// <c>SecurityPolicy.MaxPages</c> for a legitimately long trusted document.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string PdfPageLimitExceeded001 = "PDF-PAGE-LIMIT-EXCEEDED-001";

    /// <summary>
    /// Per SEC-5 — the produced PDF exceeded the configured
    /// <c>SecurityPolicy.MaxOutputBytes</c> cap. Unlike the page cap this ABORTS the
    /// conversion (throws <c>HtmlPdfException</c> carrying this code) — a truncated PDF
    /// is not meaningful. Bounds output amplification (huge image cascades / page
    /// explosions) independently of the page count. Default is effectively unlimited;
    /// <c>SecurityPolicy.UntrustedHtml</c> caps at 50 MiB. Severity: fatal (thrown).
    /// </summary>
    public const string PdfOutputSizeExceeded001 = "PDF-OUTPUT-SIZE-EXCEEDED-001";

    // endregion PDF-*

    // region RES-*

    /// <summary>
    /// Per the img-pipeline cycle — a <c>url(...)</c> resource (an <c>&lt;img src&gt;</c> or a
    /// <c>background-image</c>) failed to load: the URI failed the <see cref="SecurityPolicy"/>
    /// safety checks, no <c>IResourceLoader</c> was configured for a non-<c>data:</c> scheme,
    /// the loader returned an error / not-found, or a budget cap rejected it. The element still
    /// lays out (an unsized image keeps its declared/attribute dimensions; nothing paints) —
    /// surfaced rather than dropped silently. The message carries the sanitized failure reason.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string ResLoadFailed001 = "RES-LOAD-FAILED-001";

    // endregion RES-*

    // region IMG-*

    /// <summary>
    /// Per the img-pipeline cycle — fetched image bytes could not be DECODED: not a recognizable
    /// raster format (PNG / JPEG natively; GIF / WebP / BMP via the SkiaSharp raster fallback),
    /// a corrupt stream, or a decoded size beyond the pixel cap. The element still lays out at
    /// its declared/attribute size (no intrinsic dimensions are available); nothing paints.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string ImgDecodeFailed001 = "IMG-DECODE-FAILED-001";

    // endregion IMG-*

    // region PAINT-*

    /// <summary>
    /// <b>Reserved — no longer emitted.</b> Was emitted (Phase 5 layout→PDF cycle 2,
    /// PR #118 review) when a partial-alpha <c>background-color</c> was painted fully
    /// opaque. The Phase 4 paint-alpha pass wired PDF constant-alpha compositing
    /// (ExtGState <c>/ca</c>) into <c>FragmentPainter</c> / <c>PdfPage.FillRectangle</c>,
    /// so partial alpha is now composited faithfully and this approximation no longer
    /// occurs. The constant is retained for code-registry stability.
    /// Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string PaintBackgroundAlphaApproximated001 = "PAINT-BACKGROUND-ALPHA-APPROXIMATED-001";

    /// <summary>
    /// Per Phase 5 layout→PDF wiring cycle 3 — emitted by the <c>FragmentPainter</c>
    /// when a <c>border-style</c> / <c>outline-style</c> is painted as a solid-ring
    /// APPROXIMATION. <c>dashed</c> / <c>dotted</c> / <c>double</c> now render FAITHFULLY
    /// everywhere (square + uniform-rounded borders + sharp/rounded outlines), so this
    /// fires only for: (a) a 3D style (<c>groove</c> / <c>ridge</c> / <c>inset</c> /
    /// <c>outset</c>) on a ROUNDED border ring or an outline ring (its per-side bevel
    /// can't follow a concentric rounded ring), or (b) a ROUNDED border whose per-edge
    /// styles are NON-UNIFORM (mixed), painted via the clipped per-edge path (straight
    /// dashes clipped to the rounded outline + square inner corners). <c>solid</c> /
    /// <c>none</c> / <c>hidden</c> never emit this. Fires at most once per conversion.
    /// Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string PaintBorderStyleApproximated001 = "PAINT-BORDER-STYLE-APPROXIMATED-001";

    /// <summary>
    /// <b>Reserved — no longer emitted.</b> Was emitted (Phase 5 layout→PDF cycle 3,
    /// PR #119 review) when a partial-alpha border color was painted fully opaque — the
    /// border counterpart of <see cref="PaintBackgroundAlphaApproximated001"/>. The
    /// Phase 4 paint-alpha pass wired ExtGState <c>/ca</c> constant-alpha compositing, so
    /// it's now composited faithfully and this approximation no longer occurs. The
    /// constant is retained for code-registry stability.
    /// Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string PaintBorderAlphaApproximated001 = "PAINT-BORDER-ALPHA-APPROXIMATED-001";

    /// <summary>
    /// Per Phase 5 layout→PDF cycle 5a-2-ii — a font could not be used for some text, so that
    /// text is NOT painted. Two emit sites: the render pipeline (text shaping runs during
    /// layout, so a <c>font-family</c> stack that resolves to no face — or whose bytes are
    /// unsafe / WOFF-wrapped — surfaces from layout and is caught as a backstop), and the
    /// <c>TextPainter</c> (a resolved font that can't be parsed / subset / embedded, e.g. a
    /// CFF/OTF outline font). The rest of the document still renders; only the affected text is
    /// skipped. Surfaced rather than dropped silently per the diagnostics-not-silent-corruption
    /// rule. Deduplicated within a conversion. A bundled deterministic last-resort font (so the
    /// default path always resolves) is a tracked follow-up —
    /// <c>docs/deferrals.md#layout-to-pdf-pipeline</c>.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string PaintTextFontUnresolved001 = "PAINT-TEXT-FONT-UNRESOLVED-001";

    /// <summary>
    /// Per Phase 3 Task 23 — a page margin box's laid-out content is TALLER than its content area: the box
    /// was clamped to the page-margin band but its content block-height exceeds the available height. The
    /// common case is a vertical (left/right) EDGE box whose shrink-to-fit height hit the band limit, or a
    /// multi-line <c>element()</c> running header taller than its band. The content still PAINTS (it
    /// overflows the box) — content-box clipping / truncation is a tracked follow-up
    /// (<c>docs/deferrals.md#layout-to-pdf-pipeline</c>). Surfaced rather than letting the overflow pass
    /// silently. The message NAMES the box (e.g. <c>@left-middle</c>) + the measured content vs available
    /// height; fired once PER overflowing box (so multiple overflowing headers/footers are each diagnosable).
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string PaintMarginBoxContentOverflow001 = "PAINT-MARGIN-BOX-CONTENT-OVERFLOW-001";

    /// <summary>
    /// <b>Reserved — no longer emitted.</b> Was emitted (bg-image cycle) when a
    /// <c>background-image</c> tiling exceeded the 4096-tile per-fragment cap (a tiny tile
    /// over a large box — a content-stream DoS guard) and the image was skipped. The
    /// tiling-patterns cycle made large tilings emit ONE PDF tiling-pattern fill
    /// (ISO 32000-2 §8.7.3 — O(1) content-stream size for any count), so the cap and its
    /// skip path became unreachable. Constant retained for code-registry stability
    /// (PR #168 review P2 — codes never change meaning once published; prior resolved
    /// PAINT approximations follow the same convention).
    /// Severity (when it fired): <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string PaintBgImageTileCap001 = "PAINT-BG-IMAGE-TILE-CAP-001";

    // endregion PAINT-*
}
