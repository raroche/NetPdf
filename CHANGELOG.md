# Changelog

All notable changes to NetPdf are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Post-`1.0.1` improvements accumulate here until the next release is cut.

## [1.0.1]

A patch release: layout fixes for auto-height floats around page breaks, plus repository and release hygiene. No public API changes.

### Fixed
- **Auto-height float sizing in the break planner.** The break-planning pre-check now content-sizes an auto-height float, so a float taller than the remaining space on a page defers to a fresh page instead of overflowing it. (#314, #316)
- **`clear` after a float.** An inline-only (text) block with `clear` now resolves clearance correctly and no longer overlaps a preceding auto-height float. (#314, #316)

## [1.0.0]

The first stable release. `HtmlPdf.Convert(html)` runs the full HTML → CSS → layout → paginate → paint → PDF pipeline end-to-end, producing deterministic PDF bytes with no browser, no subprocess, and no revenue-capped or copyleft dependencies.

> **Dependency note.** NetPdf 1.0.0 depends on **`AngleSharp.Css 1.0.0-beta.*`**, which has no stable 1.x release — the last stable `0.17.0` targets the AngleSharp 0.x API and is incompatible with the AngleSharp 1.1.x the engine requires. `dotnet pack` therefore emits **NU5104** (demoted from error to a visible warning, not hidden) so the exception stays auditable. To be revisited when AngleSharp.Css ships a stable 1.x.

### Layout & pagination
- Block, inline, flex (Level 1), grid (Level 1), table, and multi-column layout, with absolute/fixed positioning.
- Fragmentainer-aware pagination with a break cost model: long content flows across as many pages as needed; `break-before` / `break-after` / `break-inside`, `widows`, and `orphans` are honored.
- Tables repeat `<thead>` / `<tfoot>` across every page they span.
- A block-flow subtree that doesn't fit the remaining page starts on the current page and breaks between its children (rather than moving wholly and wasting space).
- `position: absolute` boxes anchored to content that paginates are emitted on the page where their containing block lands.

### Paged media
- `@page` size/margins and all 16 margin boxes; running headers/footers via `position: fixed`, `position: running()` + `element()`, and `string()`.
- Page numbers via `counter(page)` / `counter(pages)`.

### Text
- OpenType shaping via HarfBuzz (kerning, ligatures), bidirectional text (UAX #9), line breaking (UAX #14, including CJK), and grapheme segmentation (UAX #29).
- Web fonts (`@font-face` with TTF/OTF/WOFF/WOFF2), font fallback, and glyph subsetting on embed.
- English hyphenation is bundled; other languages ship as optional `NetPdf.Languages.*` packs.

### Visual parity
- Backgrounds, borders, `border-radius`, gradients (linear/radial/conic), box & text shadows, 2D transforms, opacity, `clip-path`, masks, and blend modes.
- CSS filters via a subtree raster fallback (blur, drop-shadow, brightness, contrast, …).
- Static SVG (shapes, paths, gradients, transforms, text).
- Images: JPEG passthrough, PNG (incl. RGBA soft masks), and WebP/AVIF/GIF via Skia decode, with content-hash deduplication.

### Documents & navigation
- Same-document `<a href="#id">` links become `/GoTo` jumps resolved across the whole document; external `http`/`https`/`mailto` links become `/URI` annotations (other schemes are dropped with a diagnostic).
- Headings become the PDF outline (bookmarks); `<title>`, `<meta>` descriptors, and `<html lang>` flow into `/Info`, an XMP `/Metadata` stream, and the catalog `/Lang`. Initial view (`PageMode` / `PageLayout`) is configurable.

### CSS
- Cascade, `var()` custom properties, and `calc()` / `min()` / `max()` / `clamp()` / `abs()` / `sign()`.
- `::before` / `::after` / `::marker` / `::first-line` / `::first-letter`.

### Engine guarantees
- **Deterministic:** identical input produces identical bytes; no timestamp is read unless you set one.
- **Native-AOT compatible** and trimmable, with a JIT/AOT byte-parity gate.
- **No process spawning** at render time.
- Unsupported features emit a stable structured diagnostic rather than throwing or silently dropping content — see the [diagnostics code registry](https://github.com/raroche/NetPdf/blob/main/docs/diagnostics-codes.md).

### Security
- Hardening against the known HTML-to-PDF attack classes (SSRF, local-file read, resource bombs, decoder bugs, PDF active content); PDF active-content keys are rejected unconditionally at preflight.
- `SecurityPolicy` presets (`UntrustedHtml` / `SafeDefault` / `TrustedTemplate`) with per-render resource budgets. See the [security guidance in the README](https://github.com/raroche/NetPdf/blob/main/README.md#running-netpdf-on-untrusted-html).

### Packaging
- Single `NetPdf` NuGet package bundling the whole engine; optional `NetPdf.Languages.*` hyphenation add-ons.
- Source Link + symbol packages for source-stepping.

[Unreleased]: https://github.com/raroche/NetPdf/compare/v1.0.1...HEAD
[1.0.1]: https://github.com/raroche/NetPdf/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/raroche/NetPdf/compare/0.9.0-rc1...v1.0.0
