# Changelog

All notable changes to NetPdf are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Post-`1.1.1` improvements accumulate here until the next release is cut.

## [1.1.1]

A patch release: layout fixes found in a visual review of real travel documents against a browser. No public API changes.

### Fixed
- **Absolutely positioned boxes with `width: auto` shrink to fit their content** (CSS 2.1 §10.3.7). A box such as a `position: absolute; top: 18px; right: 22px` "PAID" stamp stretched across its whole containing block and hid its text; it now hugs its content and keeps its inset from the edge. Boxes pinned by both `left` and `right` still fill the space between them.
- **Inline images and SVGs taller than the text stay inside their line.** A baseline-aligned inline `<img>` / `<svg>` was drawn above its own line box, over the parent's padding and border (for example a check badge overlapping the top edge of its card). The line baseline now follows the CSS 2.2 §10.8.1 rule, as it already did for `inline-block`.
- **`flex: 1` items are not made narrower than their content.** The automatic minimum size (CSS Flexbox §4.5) now also applies when items grow from a `0` / percentage basis, so a card holding a long e-mail address widens (and its siblings share the rest) instead of letting the address overflow it — the same as a browser.
- **No leading space after `<br>`.** The source line break and indentation after a `<br>` became a space at the start of the next line, and the space before it stayed at the end of the previous one. Both are now removed (CSS Text §4.1.2), so lines after a `<br>` start flush and right-aligned lines end flush.

## [1.1.0]

A minor release: stable dependencies, page-break and flexbox layout fixes, security hardening for untrusted HTML, and a default render time limit. Every runtime dependency is now a stable release, and the current AngleSharp vulnerability warning is resolved.

### Added
- **Page-break guide** ([docs-site/page-breaks.md](docs-site/page-breaks.md), linked from the README): how NetPdf chooses each page break, forced and avoided breaks and their limits, recipes, and troubleshooting.
- **`SecurityPolicy.RenderTimeout`** — a default time limit on conversion for renders that use the policy. `SecurityPolicy.UntrustedHtml` now defaults to **30 seconds**, so a pathological document is stopped even when the caller sets no timeout. The limit is cooperative: the render ends with a `TimeoutException` at the pipeline's next cancellation check after the deadline (checks run between layout pages, in resource loading, and after PDF serialization), so a single long step can overrun it — run untrusted input in an isolated process as well (see `docs/security/deployment.md`). `SafeDefault` and `TrustedTemplate` stay uncapped. An explicit `HtmlPdfOptions.Timeout` always wins, and `Timeout.InfiniteTimeSpan` removes the cap. The `TimeoutException` message names which setting fired, and a cancellation the timer did not cause is no longer reported as a timeout.

### Changed
- Updated **AngleSharp** to 1.8.2 (resolves GHSA-pgww-w46g-26qg).
- Updated **AngleSharp.Css** from the 1.0.0-beta.144 prerelease to the stable **1.1.2**. Every runtime dependency is now a stable release, so the `NU5104` prerelease-dependency exception is gone.
- Updated bundled native/runtime dependencies to **SkiaSharp** 4.153.1 and **HarfBuzzSharp** 14.2.1.301, and migrated call sites away from APIs newly obsolete in SkiaSharp 4.x.
- Updated build and test tooling, including **Microsoft.NET.Test.Sdk** 18.10.1, **xunit.runner.visualstudio** 4.0.0, **coverlet.collector** 10.1.0, **BenchmarkDotNet** 0.15.8, **Microsoft.Playwright** 1.63.0, **PDFtoImage** 5.4.0, **docfx** 2.81.0, and **Microsoft.SourceLink.GitHub** 10.0.401.
- Updated CI actions to **actions/setup-dotnet** v6 and **github/codeql-action** v4.
- `Timeout = Timeout.InfiniteTimeSpan` now means "no limit". It previously cancelled immediately, like any negative value.

### Fixed
- `@layer` blocks now apply, in layer order. Previously their rules were dropped with `CSS-AT-RULE-UNKNOWN-001`.
- An `!important` grid longhand now correctly beats a later normal shorthand in the same rule (for example `grid-row-end: 6 !important; grid-row: 2 / 4` now ends at line 6).
- A single-value `gap` / `grid-gap` sets both the row and the column gap again (AngleSharp.Css 1.1.x left the column gap empty).
- **`break-after: avoid` / `break-before: avoid` now keep a heading with its content.** A heading that fitted at the page bottom while the block after it did not was left alone on the page; it now moves to the next page with its section (also inside wrapper elements, and across up to three consecutive avoided boundaries). A keep never moves the first block on a page, is dropped when the two blocks cannot share even a fresh page, and gives way to a forced break at the same boundary.
- **Column flexbox items are sized fit-content.** An auto-width item in a `flex-direction: column` container that is not stretched (`align-items: center`, `flex-end`, …) is now fit-content wide (clamped by its `min-width` / `max-width`); it was placed as if 0 wide, so centered text started at the center line and the item's background was missing.
- **A flex item's own text uses its percentage width once.** `li { width: 50% }` in a row flexbox wrapped its text at a quarter of the container, because the percentage was applied again when the item's text was laid out.
- **A padded flex item's text uses its whole content box.** The item's border and padding were subtracted twice from the width its own text was laid out at, so the text wrapped early.
- **Flex items no longer shrink below their longest word.** `min-width: auto` on a row flex item is now the automatic minimum size (CSS Flexbox §4.5), so a long value in a `justify-content: space-between` row overflows the row instead of overlapping its label. `min-width: 0` or a scroll container (`overflow: hidden` / `scroll` / `auto`) opts out, as in browsers; `overflow: clip` does not.
- **The `overflow` shorthand is applied.** `overflow: hidden` (one or two values) now sets `overflow-x` / `overflow-y`; before, only the longhands took effect.

### Security
- **SSRF: IPv6 forms that embed an IPv4 address are now blocked.** NAT64 (`64:ff9b::/96`), 6to4 (`2002::/16`), IPv4-compatible (`::a.b.c.d`) and IPv4-translated (`::ffff:0:a.b.c.d`) addresses are checked against the IPv4 blocklist, so `64:ff9b::a9fe:a9fe` can no longer reach `169.254.169.254`. Local-use NAT64, Teredo, site-local (`fec0::/10`) and discard-only (`100::/64`) ranges are blocked outright. Public addresses reached through NAT64 or 6to4 keep working. Only affects deployments that enable `http`/`https` fetching; the default policies do not.
- **The built-in HTTP loader ignores ambient proxy settings** (`HTTP_PROXY`, `HTTPS_PROXY`, `ALL_PROXY`). Routing through a proxy bypassed the loader's pinned-IP connect, which is its defense against DNS rebinding. Deployments that need an egress proxy should supply their own `IResourceLoader`.

## [1.0.2]

A maintenance release: dependency updates only. No functional or public API changes, and output is byte-for-byte unchanged (all rendering goldens are identical).

### Changed
- Updated bundled native/runtime dependencies to their latest patch releases: **SkiaSharp** 3.119.4 and **HarfBuzzSharp** 8.3.1.5.
- Updated build and test tooling (test SDK, analyzers pin, CI actions).

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

[Unreleased]: https://github.com/raroche/NetPdf/compare/v1.1.1...HEAD
[1.1.1]: https://github.com/raroche/NetPdf/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/raroche/NetPdf/compare/v1.0.2...v1.1.0
[1.0.2]: https://github.com/raroche/NetPdf/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/raroche/NetPdf/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/raroche/NetPdf/compare/0.9.0-rc1...v1.0.0
