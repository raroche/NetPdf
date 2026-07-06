# Authoring HTML & CSS for NetPdf

NetPdf is a **print/paged-media** renderer, not a browser. It implements the HTML+CSS *layout* model (block / inline / flex / grid / table / floats / positioning) and paginates the result onto fixed-size pages. It does **not** run JavaScript, and it fetches remote resources only under an explicit, sandboxed policy.

Most well-formed HTML renders faithfully. This guide collects the authoring patterns that produce the best PDFs — and the handful of pitfalls that make a document render worse than its author expects. Each pitfall notes whether NetPdf has since been fixed or whether the guidance is a durable best practice.

> **Rule of thumb:** author for *print*. Give the paginator seams to break at, keep assets self-contained, and size boxes so they fit the page.

---

## 1. Pagination — give the engine places to break

A PDF has hard page boundaries. When a block is taller than the space left on a page, NetPdf must split it; when a block has **no valid break opportunity inside it**, the only option is a last-resort forced split, which lands in an ugly place and emits `PAGINATION-FORCED-OVERFLOW-001`. That diagnostic is a *signal to you*, not an engine defect: "this block has no clean break here."

**Do:**

- Keep logical sections in their own elements and mark the ones that must stay whole:
  ```css
  .invoice-section,
  .card,
  .signature-block { break-inside: avoid; }
  ```
- Let long tables split between rows, and repeat the header on every page:
  ```css
  table { break-inside: auto; }
  thead { display: table-header-group; }  /* repeats on each page */
  tr    { break-inside: avoid; }           /* never split a row mid-height */
  ```
- Use `break-before: page` / `break-after: page` to start a section on a fresh page.
- Control widows/orphans on prose: `p { orphans: 3; widows: 3; }`.

**Avoid:**

- Wrapping the **entire document in one deeply-nested block/table** with no internal break opportunities. If that block exceeds one page it *cannot* be split cleanly by any engine (Prince and WeasyPrint included).
- Oversized padding that inflates a would-be one-page document past a page (e.g. stacked large paddings on every wrapper). Trim padding so single-page content fits a single page.

---

## 2. Images & logos — make them self-contained

NetPdf renders offline and deterministically, and it blocks remote fetches by default (SSRF / untrusted-content hardening). A `<img src="https://…">` will be **blocked** and reported as `RES-LOAD-FAILED-001`, leaving a reserved-but-empty gap where the image should be.

**Do:**

- Embed raster images as `data:` URIs: `<img src="data:image/png;base64,…">`.
- Embed logos and icons as **inline `<svg>`** (a broad subset is supported — paths, strokes, fills, gradients, `<use>`/`<symbol>`, text) or as an SVG `data:` URI. Inline SVG is a robust, resolution-independent choice for self-contained graphics. It is a **supported subset**, not a full SVG 1.1/2 implementation — check [`docs/compatibility-matrix.md`](compatibility-matrix.md) and [`docs/deferrals.md`](deferrals.md) for the current SVG boundaries, and note that an inline `<svg>` used as an absolutely-positioned decoration inside a flex/grid item can hit the positioned-layout residuals tracked there.
- If you must load remote assets, opt into it explicitly via the resource-loader policy (see `docs/secrets-and-credentials.md` / `SecurityPolicy`) — and understand the security trade-off.

**Watch out for:**

- A **white/inverted logo shown only via a colored background box.** If the box's fill doesn't paint for any reason, a white SVG on white paper is invisible. Prefer an SVG that carries its own visible fill.

---

## 3. Sizing — fit the page, prefer flexible over exact

The page content area is finite (A4 with 16 mm margins ≈ 178 mm ≈ 504 pt wide). Content wider than that overflows the page edge and is clipped (`PDF-CONTENT-OVERFLOW-TRUNCATED-001`).

**Do:**

- Prefer `flex: 1` (grow/shrink to share space) over exact percentage widths on flex children. Two columns at `width: 48%` + a fixed `gap` sum to *nearly* 100 % and are fragile — any rounding or extra chrome tips them into overflow. `flex: 1` (or `flex: 1 1 0`) is self-correcting.
- If you use percentages, budget the gap into them (`width: 47%`, not `48%`) or rely on `flex-shrink`.
- Use `max-width: 100%` on images and wide media so they never exceed their container.
- Give real content-box widths, not fixed pixel widths that assume a screen viewport.

> Percentage sizes on flex items (`width: 48%`, `height: 100%`) and flexible `flex: 1` items are supported and resolve against the container. The guidance above is about *robustness* — leaving a little slack — not a limitation.

---

## 4. Backgrounds, borders & custom properties

`var()`, gradients, and modern color functions are supported. A couple of authoring habits keep them trouble-free:

- **Prefer longhands with `var()`** where practical — `background-color: var(--brand)` and `background-image: linear-gradient(…, var(--a), var(--b))` are unambiguous. (NetPdf also recovers `var()` inside the `background` / `border` *shorthands*, but the longhand form is clearest and future-proof.)
- Use `background-color:` rather than the `background:` shorthand when you only want a color — the shorthand resets every background longhand, which can surprise.
- For a border, `border-bottom: 1px solid var(--ink)` works; so does the split `border-bottom-width/-style/-color`.

---

## 5. Tables & boxes

- **Minify or don't rely on whitespace collapsing inside padded wrappers.** Pretty-printed HTML is fine, but historically a padded auto-height box with newline-separated block children could over-inflate. (Fixed — the box height is now whitespace-independent — but keeping wrapper markup tight never hurts.)
- Give a data table explicit column intent (`<colgroup>`, or fixed `table-layout`) when you need predictable widths.
- Don't rely on a flex/grid *item* to stretch a decorative box to full height unless you set a definite container size — an item's `height: 100%` needs a definite parent height to resolve against.

---

## 6. Tailwind & utility CSS

- **Do not use the Tailwind CDN (`<script src="https://cdn.tailwindcss.com">`).** It generates utility CSS *at runtime with JavaScript*, which NetPdf does not execute — so the classes produce no styling and the document renders unstyled.
- **Pre-compile Tailwind to a static stylesheet** and inline it (`<style>…</style>`) or link a local file. A pre-compiled, inlined Tailwind document renders well.
- Tailwind utilities don't emit paged-media hints — add your own `break-inside`, `thead` repetition, and page-fit padding (see §1).
- Browser-only reset rules (`:-moz-focusring`, `::-webkit-*`, `:disabled`, …) do nothing in PDF and emit `CSS-PARSE-WARNING-001`; removing them quiets the warnings.

---

## 7. What NetPdf deliberately does not do

- **No JavaScript.** Anything computed at runtime (charts drawn by JS, CDN Tailwind, dynamic content) must be pre-rendered into static HTML/CSS/SVG.
- **No live network by default.** Embed assets; opt into fetching explicitly.
- **Diagnostics, not silent corruption.** When a feature is unsupported or content can't fit, NetPdf emits a stable diagnostic code (see `docs/diagnostics-codes.md`) instead of silently dropping content. Read the diagnostics from `ConvertDetailed(...).Warnings` — they usually tell you exactly what to change.

---

## Quick checklist

- [ ] Assets embedded as `data:` URIs or inline SVG (no remote `src`).
- [ ] Sections marked `break-inside: avoid`; tables split between rows with a repeating `thead`.
- [ ] No single block taller than a page with no internal break points.
- [ ] Content fits the page width; flexible (`flex: 1`) rather than exact-percentage columns.
- [ ] Tailwind pre-compiled and inlined, not CDN.
- [ ] Checked `ConvertDetailed(...).Warnings` for `RES-LOAD-FAILED`, `PAGINATION-FORCED-OVERFLOW`, `PDF-CONTENT-OVERFLOW-TRUNCATED`.
