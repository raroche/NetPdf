# Invoice Corpus

Real-world invoice templates used to stress-test NetPdf's rendering pipeline. Each file is paired with a Chromium-rendered reference PNG (committed once the visual-regression harness lands in Phase 5) and exercises a distinct slice of the compatibility matrix.

## Files

### `01-classic-pure-css.html`

A traditional invoice template using **pure inline CSS** — no JavaScript, no CSS framework, no external stylesheets. The bread-and-butter case NetPdf must render perfectly.

**CSS features exercised:**
- `@page { margin: ... }` — paged-media setup
- `display: flex; justify-content: space-between` — flexbox header
- `float: right` + `clear: both` — totals-block layout
- `border-collapse: collapse` + `<thead>`/`<tbody>` — table rendering with header
- `page-break-inside: avoid` — fragmentation control
- `font-family: 'Helvetica Neue', Arial, sans-serif` — font fallback chain
- Inline column widths (`style="width:5%"`)
- External `<img src="https://...">` — requires HTTPS opt-in via `SecurityPolicy`

**Phase to render correctly:** Phase 3 (`0.7.0-beta`).

### `02-tailwind-cdn.html`

A "Professional Invoice Template" built with **Tailwind CSS loaded at runtime via CDN**. The `<script src="https://cdn.tailwindcss.com">` tag generates utility-class rules in the browser at page load. **Without JavaScript execution, no Tailwind CSS is generated** — every utility class (`flex`, `bg-indigo-700`, `text-4xl`, etc.) maps to no rule, and the page renders as if all those classes didn't exist.

**Why we keep it in the corpus:** it's representative of a common modern pattern that **NetPdf cannot render correctly** by design — JavaScript is intentionally not executed in v1 (per `docs/compatibility-matrix.md`). Including it as a corpus entry makes that limitation testable and visible.

**Expected behavior:**
- The `<script>` tag is collected and surfaced as `HTML-SCRIPT-IGNORED-001`.
- The remaining inline `<style>` block (with `@media print`, the `body` `font-family` rule) applies; everything else falls back to user-agent defaults.
- The output is intentionally degraded compared to a browser-rendered version.

**Workaround for users:** pre-compile Tailwind to a static CSS file (`tailwindcss -i input.css -o output.css`), then reference it via `<link rel="stylesheet" href="output.css">`. NetPdf renders the static CSS the same way a browser does.

**Phase to render correctly with workaround:** Phase 3 (`0.7.0-beta`) — the Tailwind utility classes are vanilla CSS once compiled.

### `03-tailwind-cdn-responsive.html`

Same pattern as `02-tailwind-cdn.html` but adds **responsive table layout** via `@media (max-width: 639px)` rules with `::before { content: attr(data-label) }` pseudo-elements. Demonstrates:
- `@media` queries (CSS feature)
- `::before` pseudo-elements (CSS feature)
- `attr()` value (CSS feature)
- `data-*` attributes consumed by CSS

The Tailwind-CDN-via-JS limitation applies (same as 02). The custom `<style>` block does render.

**Phase to render correctly with workaround:** Phase 3 (`0.7.0-beta`).

### `04-anvil-running-elements.html`

Adapted from the open-source [Anvil HTML-PDF invoice template](https://github.com/anvilco/html-pdf-invoice-template). The most advanced sample in the corpus — exercises features only paged-media engines support (Prince, WeasyPrint, PagedJS). **Browsers cannot render this correctly** because they don't implement CSS Paged Media Level 3 running elements; the test of NetPdf's competence is whether it matches the *paged-media engine* output, not the browser output.

**CSS features exercised:**
- `position: running(footer)` and `position: running(pageContainer)` — **named running elements** (CSS Paged Media L3)
- `@page { @bottom-right { content: element(pageContainer); } @bottom-left { content: element(footer); } }` — **page-margin boxes pulling from running elements** (CSS GCPM L3)
- `counter(page)` and `counter(pages)` via `content: counter(...)` in `::after` — **page-counter generated content**
- **Multi-stylesheet composition**: a common `<style>` block plus a `<style media="print">` block scoped to PDF rendering — tests our `MediaType` option
- `<td rowspan="2">` — table with row-span (non-trivial table-layout test)
- `:first-child` and `:last-child` pseudo-classes
- `::after` pseudo-elements with `content`
- Float-based footer layout (in screen mode)
- External `<img>` from CDN (HTTPS, requires `SecurityPolicy.AllowHttpsScheme = true`)

**Phase to render correctly:** Phase 3 (`0.7.0-beta`) — running elements and page-margin boxes are explicit deliverables in the layout/pagination milestone (`docs/compatibility-matrix.md` § CSS — Paged Media).

**Why it's the keystone test:** if NetPdf renders this faithfully (footer + "Page N of M" appearing on every page in the right place across multi-page splits), it has demonstrated competence at the paged-media features that distinguish a real document engine from a screen-rendering wrapper. This is the file that decides whether NetPdf belongs in the same conversation as Prince and WeasyPrint.

## What the corpus is for

- **Phase 0 (now):** smoke-tested only — confirms the files are present, parseable as UTF-8, and reachable from the test project.
- **Phase 1:** parsing fidelity — feed each through AngleSharp + AngleSharp.Css and assert the DOM and CSSOM are populated.
- **Phase 2:** cascade fidelity — assert computed styles for key elements match expected values.
- **Phase 3:** layout + visual regression — render to PDF and pixel-diff against the committed Chromium reference PNGs (per `docs/compatibility-matrix.md` and the pinned-Chrome-Docker-image strategy in the plan).
- **Phase 5:** corpus locked as a release-acceptance gate.

## Adding new samples

When adding to this corpus:
1. Use a numeric prefix that sorts naturally (`04-...`, `05-...`).
2. Add a section to this README describing what the file exercises.
3. Note the earliest phase that should render it correctly.
4. Inline external resources where possible (especially fonts) so the corpus is reproducible offline; document any required network access otherwise.
5. Once the visual-regression harness ships in Phase 5, commit a Chromium-rendered reference PNG alongside.
