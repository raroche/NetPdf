# NetPdf Diagnostic Codes

Stable, versioned codes emitted by NetPdf when input cannot be rendered identically to a browser print pipeline. Codes are categorized by prefix and never change meaning once published. New conditions get new codes.

Severity levels:
- **Error** — input rejected; no PDF produced when `Features.StrictUnsupportedCss` is set.
- **Warning** — feature degraded or skipped; PDF still produced.
- **Info** — feature took an alternative path (e.g., raster fallback). PDF unchanged in correctness.

---

## HTML-* — HTML parsing & content

| Code | Severity | Meaning |
|---|---|---|
| `HTML-SCRIPT-IGNORED-001` | Warning | A `<script>` element was encountered. NetPdf does not execute JavaScript in v1. The element was removed from the rendering tree. |
| `HTML-MEDIA-UNSUPPORTED-001` | Warning | A `<video>` or `<audio>` element was encountered. Replaced with empty space. |
| `HTML-IFRAME-UNSUPPORTED-001` | Warning | An `<iframe>` element was encountered. Replaced with empty space. |
| `HTML-CANVAS-IGNORED-001` | Warning | A `<canvas>` element was encountered. NetPdf does not execute the scripted drawing API. Replaced with empty space. |
| `HTML-OBJECT-EMBED-UNSUPPORTED-001` | Warning | `<object>` or `<embed>` encountered; not supported. |

---

## CSS-* — CSS parsing & rendering

| Code | Severity | Meaning |
|---|---|---|
| `CSS-PARSE-WARNING-001` | Warning | A CSS rule was malformed and skipped. |
| `CSS-AT-RULE-UNKNOWN-001` | Info | An unrecognized at-rule was preserved in the AST but had no rendering effect. |
| `CSS-PROPERTY-UNKNOWN-001` | Info | A vendor-prefixed or unknown CSS property was ignored. |
| `CSS-VAR-CIRCULAR-001` | Warning | A `var()` chain produced a circular reference; resolved to fallback or `unset`. |
| `CSS-CONTAINER-QUERY-UNSUPPORTED-001` | Warning | `@container` rule encountered; condition not evaluated, contained rules skipped. Roadmap v1.4. |
| `CSS-HAS-RENDERING-NOT-IMPLEMENTED-001` | Warning | `:has()` selector parsed but treated as no-match in v1. Roadmap v1.4. |
| `CSS-SUBGRID-UNSUPPORTED-001` | Warning | `subgrid` value used; treated as `none`. Roadmap v1.3. |
| `CSS-ANCHOR-POSITIONING-UNSUPPORTED-001` | Warning | `anchor()`/`anchor-size()` used; falls back to `auto`. |
| `CSS-POSITION-STICKY-UNSUPPORTED-001` | Warning | `position: sticky` treated as `relative` in v1. |
| `CSS-TRANSFORM-3D-UNSUPPORTED-001` | Warning | A 3D transform was found; the matrix was projected to 2D. |
| `CSS-ANIMATION-UNSUPPORTED-001` | Info | `@keyframes`/`animation`/`transition` were ignored. PDF is static. |
| `CSS-FILTER-RASTER-FALLBACK-001` | Info | A subtree with `filter` was rasterized at `DevicePixelRatio * 96` DPI and embedded as PNG. |
| `CSS-CLIP-PATH-RASTER-FALLBACK-001` | Info | `clip-path: path()` triggered raster fallback. |
| `CSS-MASK-RASTER-FALLBACK-001` | Info | `mask`/`mask-image` triggered raster fallback. |
| `CSS-CONIC-GRADIENT-RASTER-001` | Info | Conic gradient triggered raster fallback. |
| `CSS-BOXSHADOW-BLUR-RASTER-001` | Info | Blurred box-shadow triggered raster fallback. |
| `CSS-TEXTSHADOW-BLUR-RASTER-001` | Info | Blurred text-shadow triggered raster fallback. |
| `CSS-WRITING-MODE-SIDEWAYS-UNSUPPORTED-001` | Warning | `sideways-rl`/`sideways-lr` writing modes treated as `vertical-rl`/`vertical-lr`. |

---

## FONT-* — Font loading & shaping

| Code | Severity | Meaning |
|---|---|---|
| `FONT-LOAD-FAILED-001` | Warning | A web font's URL could not be fetched within the timeout; fallback chain consulted. |
| `FONT-PARSE-FAILED-001` | Warning | A font file's binary could not be parsed; fallback chain consulted. |
| `FONT-MISSING-GLYPH-001` | Info | A character was rendered as `.notdef` (tofu) because no font in the fallback chain contained the glyph. |
| `FONT-SUBSETTING-FAILED-001` | Warning | Subsetting a font failed; full font embedded as fallback. PDF size larger than expected. |

---

## RES-* — Resource loading

| Code | Severity | Meaning |
|---|---|---|
| `RES-LOAD-FAILED-001` | Warning | A `url(...)` resource (image, font, stylesheet) failed to load. |
| `RES-SECURITY-DENIED-001` | Warning | A resource URL was denied by the configured `SecurityPolicy` (e.g., HTTPS not allowed). |
| `RES-TIMEOUT-001` | Warning | A resource exceeded `SecurityPolicy.ResourceTimeout`. |
| `RES-TOO-LARGE-001` | Warning | A resource exceeded `SecurityPolicy.MaxResourceBytes`. |
| `RES-UNSUPPORTED-MIME-001` | Warning | A resource had an unsupported MIME type for its referenced kind. |

---

## PDF-* — PDF emission

| Code | Severity | Meaning |
|---|---|---|
| `PDF-WRITER-WARNING-001` | Warning | The PDF writer produced output but encountered a non-fatal issue (e.g., a fragment was clipped because it exceeded paper size). |
| `PDF-FONT-NOT-EMBEDDABLE-001` | Warning | A font's license bits forbid embedding. PDF still emitted but the font is referenced, not embedded. |

---

## PAGINATION-* — Pagination optimizer

| Code | Severity | Meaning |
|---|---|---|
| `PAGINATION-OPTIMIZER-FALLBACK-001` | Info | The DP optimizer exceeded its time budget on a long document; greedy pagination used. |
| `PAGINATION-FORCED-OVERFLOW-001` | Warning | A `break-inside: avoid` region was larger than a single page; forced to break despite the rule. |

---

## INTERNAL-* — Internal/unexpected

| Code | Severity | Meaning |
|---|---|---|
| `INTERNAL-INVARIANT-VIOLATED-001` | Error | An internal invariant was violated. Always a bug — please report. |

---

## Diagnostic structure

Every diagnostic carries:

```csharp
public readonly record struct Diagnostic(
    string Code,             // stable, e.g. "CSS-CONTAINER-QUERY-UNSUPPORTED-001"
    string Message,          // human-readable, may include input context
    DiagnosticSeverity Severity,
    SourceLocation? Location // file/line/col when available, e.g. CSS source position
);
```

Consumed via:

```csharp
var result = HtmlPdf.ConvertDetailed(html);
foreach (var d in result.Warnings)
    Console.WriteLine($"[{d.Code}] {d.Message}");
```

Or streamed live via `HtmlPdfOptions.Diagnostics: IDiagnosticsSink`.

---

Last updated: 2026-04-30 (Phase 0).
