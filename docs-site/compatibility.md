# Compatibility

NetPdf implements a curated, **print-focused subset** of HTML and CSS. The goal is faithful, deterministic
paged output — not parity with an interactive browser. This page summarizes what's in and out of scope; the
authoritative, feature-by-feature matrix is
[`docs/compatibility-matrix.md`](https://github.com/raroche/NetPdf/blob/main/docs/compatibility-matrix.md).

## Rendering fidelity — what to expect

NetPdf does **not** aim to render identically to a web browser, and it deliberately doesn't try to. It is a
print- and paged-media engine: its purpose is to turn document-shaped HTML and CSS into well-formed,
deterministic PDFs, not to reproduce a browser's on-screen rendering pixel-for-pixel.

For the content it is built for — invoices, statements, reports, letters, certificates, and similar
documents — NetPdf targets **close visual parity with a browser's _print_ output** (the result of the
browser's "Print → Save as PDF"), a different and more constrained target than interactive on-screen layout.

"Renders identically to any browser" is therefore not a claim NetPdf makes. Screen-oriented or interactive
layouts, scripted content, and pixel-exact matching of a specific browser engine are out of scope. Where a
feature falls outside that scope, NetPdf emits a stable, structured [diagnostic](diagnostics.md) rather than
silently approximating or dropping content, so the places where output differs are explicit rather than
surprising.

## Status legend

| | Meaning |
|---|---|
| ✅ Supported | Fully implemented and tested. |
| 🧪 Partial | Implemented with documented caveats. |
| 📥 Parsed only | Grammar accepted; rendering pending; emits a stable diagnostic. |
| ❌ Out of scope | Not rendered for v1; emits a diagnostic (content is never dropped silently). |

## In scope

- **Layout** — block, inline, flex, grid (Level 1), tables, and multi-column, all fragmentation-aware
  across pages.
- **Paged media** — `@page`, the 16 margin boxes, running headers/footers via `string()` / `element()`,
  `counter(page)`, orphans / widows, and `break-before` / `break-after` / `break-inside`.
- **Visual** — gradients (linear / radial / conic), box- and text-shadows, 2-D transforms, borders +
  `border-radius` + `border-image`, `clip-path`, multi-layer backgrounds, `opacity`, and CSS filters on
  images.
- **Text** — HarfBuzz shaping, bidi (UAX #9), line breaking (UAX #14), and `hyphens: auto` with
  language routing via the [language packs](getting-started.md#hyphenation-language-packs).
- **Images** — `data:` URIs inline (no network by default); PNG / JPEG native, other formats via the Skia
  raster fallback; `object-fit` / `object-position`.
- **SVG** — a static subset, rendered natively (with a raster fallback where needed).
- **Links** — `<a href>` becomes a PDF `Link` annotation.

## Out of scope for v1

- **JavaScript** — `<script>` never executes (emits `HTML-SCRIPT-IGNORED-001`). This means CDN-based
  Tailwind and other runtime-CSS tools won't work; pre-compile your CSS to static output.
- `<form>` widgets, `<video>` / `<audio>`, `<iframe>`, `<canvas>`, `<object>` / `<embed>`.
- CSS animations / transitions (no timeline in a static document).

Anything unsupported emits a stable [diagnostic code](diagnostics.md) rather than silently corrupting output.

## Accessibility (PDF/UA)

Tagged-PDF / PDF/UA-1 emission is not yet produced; it is on the post-1.0 roadmap.
