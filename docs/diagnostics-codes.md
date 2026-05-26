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
| `HTML-JAVASCRIPT-URL-IGNORED-001` | Warning | An `href`/`xlink:href` attribute carried a `javascript:` URL. The attribute was removed so the link will not appear in the emitted PDF; the surrounding element and its text content remain. |
| `HTML-CANVAS-IGNORED-001` | Warning | A `<canvas>` element was encountered. NetPdf does not execute the scripted drawing API. Replaced with empty space. |
| `HTML-OBJECT-EMBED-UNSUPPORTED-001` | Warning | `<object>` or `<embed>` encountered; not supported. |
| `HTML-EVENT-HANDLER-IGNORED-001` | Info | An `on*` event-handler attribute (e.g., `onclick`, `onload`) was found on an element and stripped. NetPdf does not execute scripts in v1; the strip is defense-in-depth so Phase 5 PDF/UA emission can't surface attribute values into accessibility metadata. |
| `HTML-DOM-LIMIT-EXCEEDED-001` | Warning | The parsed DOM exceeded one of the per-document DoS caps (element count, nesting depth, attributes per element, attribute value length, or text-node content length). Excess regions are removed; over-long values + text are clamped with `…`. One diagnostic per violation kind. Per Phase B B-1. |
| `HTML-STRIP-NOT-STABLE-001` | Warning | The iterative HTML strip pass did not converge after the maximum iteration cap; dangerous content (script / `on*` handler / `javascript:`/`vbscript:`/`data:` URL) may remain in the DOM. Defense against AngleSharp normalization or SVG `<foreignObject>` re-introducing stripped content. The message names the surviving kinds. Per Phase B B-4. |
| `HTML-INPUT-TOO-LARGE-001` | Warning | The HTML input length exceeded the per-document character cap; the parse was rejected before AngleSharp materialized the tree. Defends against very large single-string inputs that would otherwise consume parser CPU + heap. Per Phase B B-1 (PR #16 follow-up). |

---

## CSS-* — CSS parsing & rendering

| Code | Severity | Meaning |
|---|---|---|
| `CSS-PARSE-WARNING-001` | Warning | A CSS rule was malformed and skipped. |
| `CSS-AT-RULE-UNKNOWN-001` | Info | An unrecognized at-rule was preserved in the AST but had no rendering effect. |
| `CSS-PROPERTY-UNKNOWN-001` | Info | A vendor-prefixed or unknown CSS property was ignored. |
| `CSS-VAR-CIRCULAR-001` | Warning | A `var()` chain produced a circular reference; resolved to fallback or `unset`. |
| `CSS-VAR-EXPANSION-LIMIT-001` | Warning | A `var()` substitution exceeded the user-agent's depth or output-length safety limit (non-cyclic but pathological); resolved to fallback or `unset`. Distinct from circular references — see `CSS-VAR-CIRCULAR-001`. |
| `CSS-CALC-INVALID-001` | Warning | A `calc()` / `min()` / `max()` / `clamp()` / `abs()` / `sign()` expression had invalid syntax or a type mismatch (e.g., adding a number to a length, mixing incompatible units in a context that doesn't allow it). The function text is preserved verbatim so layout can react; the property may compute to its initial value. |
| `CSS-CALC-DIV-BY-ZERO-001` | Warning | A `calc()` expression divided by zero. Per CSS Values L4 §10.1, the result is the IEEE-754 NaN value; the property is treated as "invalid at computed value time" — initial value applies. |
| `CSS-PROPERTY-VALUE-INVALID-001` | Warning | A property's value text could not be parsed into the property's typed value per its declared `PropertyType` (e.g., `color: not-a-color`, `width: nonsense`, `display: foo`). The cascade's "invalid at computed value time" rule applies — the property's initial value (or the inherited value for inherited properties) is used. |
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
| `CSS-CONTENT-FUNCTION-UNSUPPORTED-001` | Warning | A `content:` value (on `::before`/`::after`/`::marker`) used a function/keyword that cycle 1 doesn't yet handle (`counter()` / `counters()` / `url()` / `image()` / `image-set()` / `linear-gradient()` / `open-quote` / `close-quote` / `no-open-quote` / `no-close-quote`). The pseudo-element generates no box. Roadmap cycle 2 ships counter machinery + the resource pipeline + quotation-stack tracking. |
| `CSS-ATTR-MULTI-ARG-UNSUPPORTED-001` | Warning | A modern `attr(name type, fallback)` form was rejected — cycle 1 supports the bare `attr(name)` form only. The pseudo-element generates no box rather than silently dropping the type / fallback args. Roadmap cycle 2 delivers the typed-value pipeline. |
| `CSS-MODERN-COLOR-FUNCTION-UNSUPPORTED-001` | Info | A modern color function (`oklch()` / `oklab()` / `lab()` / `lch()` / `color()` / `color-mix()`) was used in a property value. Cycle 1 rejects these — the cascade's "invalid at computed value time" rule applies (initial / inherited value used). Roadmap cycle 2 ships sRGB-conversion of these so the rendered color is approximate but visible. |
| `CSS-PSEUDO-SUPPRESSED-ON-REPLACED-001` | Info | A `::before` / `::after` rule targeted a replaced element (`<img>` / `<video>` / `<canvas>` / `<iframe>` / `<object>` / `<embed>`); per CSS Pseudo L4 §3 the pseudo-element is suppressed because replaced elements are atomic and can't host generated content. The author rule has no effect. |
| `CSS-VAR-EXPANSION-LIMIT-001` | Warning | A `var()` substitution exceeded a depth, output-length, or per-element cumulative-output budget. Per Phase A A-3. |
| `CSS-CONTENT-FUNCTION-UNSUPPORTED-001` | Warning | `content` value used a function or keyword the cycle-1 list parser doesn't accept (e.g., `counter()`, `url()`, `open-quote`), or the per-pseudo generated-content output exceeded the 64 KiB cap. Per Phase A A-5. |
| `CSS-RULE-LIMIT-EXCEEDED-001` | Warning | A stylesheet exceeded one of the per-stylesheet / per-rule DoS caps: rule count (50 000 per sheet), declarations on a single rule (256), or selector alternatives in one rule's selector list (1 024). On rule-count overflow the cascade stops processing further rules; on per-rule overflow the declaration / alternative list is tail-truncated. Per Phase B B-2 (selector-alternative enforcement added in PR #16 follow-up). |
| `CSS-CASCADE-OVERFLOW-001` | Warning | The cascade exceeded the per-render cumulative-matched-declaration cap (5 000 000). Catches the compound per-rule × per-element explosion that stays under each individual cap but blows the matched-rule table. Subsequent matches are skipped; emitted once per render. Per Phase C C-4. |

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
| `PAGINATION-OPTIMIZER-FALLBACK-001` | Info | The bounded DP optimizer exceeded its time / candidate-set budget on a long document; greedy pagination (no lookahead) used. PDF still emits cleanly; layout quality is the same as a non-optimizing renderer. Per Phase 3 §pagination. |
| `PAGINATION-FORCED-OVERFLOW-001` | Warning | A region marked `break-inside: avoid` (or otherwise un-splittable per the cost model) was taller than a single fragmentainer; forced to split anyway. The first piece occupies the remainder of the current page; the rest cascades onto subsequent pages. PDF renders correctly but the author's break constraint was violated. Per CSS Fragmentation L3 §3.2 last-resort fallback. |

---

## LAYOUT-* — Layout

| Code | Severity | Meaning |
|---|---|---|
| `LAYOUT-INLINE-SKIPPED-NO-SHAPER-RESOLVER-001` | Warning | A block container with inline-level children was encountered, but the layouter was constructed without an inline shaper resolver. The inline children are skipped (rendered as empty space) so layout can still complete. Production callers wire a shaper resolver when constructing the renderer; the diagnostic exists for test harnesses and tooling that drive the layouter directly. Per Phase 3 Task 11 cycle 1 sub-cycle 1. |
| `LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001` | Warning | An inline-only block contained an atomic inline descendant (`inline-block` / `inline-flex` / `inline-grid` / `inline-table` / `inline replaced`). Sub-cycle 1 skips the atomic inline in the resulting line; sub-cycle 2 will inject an atomic-inline placeholder line metric box via a per-layouter intrinsic-sizing seam. One diagnostic per inline-only block aggregates the skipped-atomic count. Per Phase 3 Task 11 cycle 1 sub-cycle 1 hardening review Finding #4. |
| `LAYOUT-INLINE-UNSUPPORTED-001` | Warning | The inline pass for an inline-only block threw `NotSupportedException` for a configuration the inline layouter doesn't yet support (e.g., per-source-TextRun `word-break: keep-all` mismatch — CJK semantics need UAX #24 script detection). The block emits no fragment + the block layouter advances past it (same chain-reset semantics as the no-resolver skip). The thrown exception's message is carried in the diagnostic's message. Per Phase 3 Task 11 cycle 1 sub-cycle 1 hardening review Finding #6. |
| `LAYOUT-TABLE-FEATURE-UNSUPPORTED-001` | Warning | The table layouter encountered a CSS Tables L3 feature its current algorithm doesn't implement. Sub-cycle 2 added `colspan` / `rowspan` cell merging via the 2D occupancy-grid algorithm, so spans no longer emit this code. Sub-cycle 3 added caption layout (`caption-side: top` / `bottom`), so captions no longer emit this code either. Remaining triggers: `rowspan="0"` / `colspan="0"` HTML5 §4.9.11 "remainder of row-group / column-group" semantics (clamped to 1 + diagnostic carries the cell + axis), and the structural-anomaly case where a `<table>` wrapper has no `TableGrid` child (malformed box tree from `BoxBuilder`). Sub-cycle 4+ will also lift `table-layout: auto`/`fixed`, `border-collapse`, `<col>` widths, and header/footer repetition across pages. The table renders with the feature silently ignored. See `docs/deferrals.md#table-auto-fixed-spans-borders`. Per Phase 3 Task 12 sub-cycles 1 + 2 + 3. |
| `LAYOUT-TABLE-SLOT-BUDGET-EXCEEDED-001` | Warning | The table layouter's cumulative `rowspan × colspan` slot count exceeded the 1,000,000-slot DoS budget. Cells past the budget are capped at `rowspan = colspan = 1`; the table still renders (truncated geometry, not dropped content) but the author's recorded spans were ignored. Defends against hostile HTML where legal attribute values (e.g., `rowspan="65534" colspan="1000"` on multiple cells) would otherwise force unbounded CPU + memory work in the placement pass. Per Phase 3 Task 12 sub-cycle 2 hardening Finding 4. |
| `LAYOUT-TABLE-INLINE-OVERFLOW-001` | Warning | Under `table-layout: fixed`, the sum of declared column widths (from `<col>` / `<colgroup>` / first-row cell widths) exceeded the table wrapper's content-inline-size. CSS 2.1 §17.5.2.1 says the table grid grows to fit the declared widths in that case — the table overflows its wrapper in the inline axis. The layouter keeps the declared widths intact (row + caption fragments grow to the column sum) so author intent is preserved; the diagnostic message names the column sum + the wrapper's content-inline-size so authors can tune their declarations. Sub-cycle 5 — also fires under `table-layout: auto` when the sum of min-content column widths exceeds the wrapper's content-inline-size (the shrink-to-fit algorithm cannot narrow columns below their min-content widths without splitting words). See `docs/deferrals.md#table-auto-fixed-spans-borders`. Per Phase 3 Task 12 sub-cycle 4 hardening Finding 1. |
| `LAYOUT-TABLE-INTRINSIC-MEASUREMENT-BUDGET-EXCEEDED-001` | Warning | Under `table-layout: auto`, the per-table speculative intrinsic-measurement budget was exceeded. Each cell normally runs two speculative nested `BlockLayouter` passes (min-content at 1px + max-content at 1e6 px) — i.e., 2 ops per cell. For very large tables the cumulative work is capped at 10,000 ops; cells beyond the cap fall back to `(minContent=0, maxContent=contentInlineSize)`, producing a degenerate equal-split-like distribution rather than DoS-amplifying the speculative passes. The diagnostic carries the budget, the cell count, and the number of cells that fell back. Defends against hostile HTML with pathologically deep cell content trees in very large tables. See `docs/deferrals.md#table-auto-fixed-spans-borders`. Per Phase 3 Task 12 sub-cycle 5 hardening Finding 4. |
| `LAYOUT-TABLE-REWIND-NOT-SUPPORTED-001` | Warning | The break resolver returned `BreakAction.Rewind` at a table row boundary. Cycle 1's `TableLayouter` does NOT register per-row checkpoints (the outer `BlockLayouter` owns the pre-table rewind frontier), so a resolver-named rewind checkpoint inside the table is a contract violation. The layouter falls back to `Continue` (preserving the pre-finding behavior) + emits this diagnostic so authors / integrators see the dropped rewind. Per-row checkpoint capture is cycle 2+ scope. Per Phase 3 Task 13 cycle 1 hardening Finding 5. |
| `LAYOUT-TABLE-ROWSPAN-CROSSES-PAGE-001` | Warning | A row break would cut through a cell whose `rowspan>1` origin row commits on the current page but whose span extends past the break. Cycle 1 keeps rowspan cells atomic across pages: the layouter forces the break BEFORE the rowspan origin row (the whole spanning cell stays together on the next page) when at least one row + optional captions have already committed on the current page; otherwise it falls back to the existing forced-overflow path. The diagnostic message names the rowspan cell's row index + span. CSS Tables L3 §11 spec-strict rowspan distribution across pages is sub-cycle 6+ scope. Per Phase 3 Task 13 cycle 1 hardening Finding 6. |
| `LAYOUT-TABLE-HEADER-FOOTER-OVERSIZED-001` | Warning | The combined `<thead>` + `<tfoot>` stack height (header rows + footer rows) exceeds the fragmentainer's available block-size, leaving no room to repeat the header + footer on every page along with any body row. Per CSS Tables L3 §3.6 / §11 the header + footer repeat at the top + bottom of each page; if they exceed the fragmentainer no body row can fit on a page that also honors the repeat contract. The layouter commits the header + footer once (atomically) on the current page, skips the body to avoid infinite continuation loops, and surfaces this diagnostic so authors can reduce header / footer content or widen the page. The diagnostic message names the combined header+footer height + the fragmentainer block-size. Per Phase 3 Task 13 cycle 2. |
| `LAYOUT-MULTICOL-COLUMN-COUNT-CLAMPED-001` | Warning | An author-supplied `column-count` exceeds the layouter's safety cap (= 1000) and is silently clamped. The cap protects against the per-column arithmetic's O(N)-per-child cost on adversarial inputs (a `column-count: 1000000` would otherwise allocate ~1M per-column geometry slots + invoke 1M nested `BlockLayouter` calls per page — a DoS vector). The clamp itself is intentional; the diagnostic surfaces the cap so authors who legitimately hit it (generated CSS, configuration mistakes) know that the rendered output has AT MOST 1000 columns rather than the requested N. The diagnostic message names the requested `column-count` and the clamped value (= 1000). Per Phase 3 Task 14 cycle 2 hardening Finding 3. |
| `LAYOUT-MULTICOL-FORCED-OVERFLOW-001` | Warning | A multicol container's in-flow content can't make forward progress through pagination. Cycle 1 shipped Hello World multi-column layout + emitted this diagnostic any time content overflowed N columns (truncating the remainder); cycle 2 ships multi-page multicol via `MulticolContinuation` — a clean multi-page split is NO LONGER an error. The cycle 2 hardening (Finding #1) further lifted the recursion-depth limit on continuation propagation; deep-nested multicols (`html > body > div.multicol` from a real HTML document, where the multicol sits at recursion depth ≥ 2 inside `EmitBlockSubtreeRecursive`) now split cleanly via the chained-`BlockContinuation` propagation. The diagnostic now fires only in the narrow forward-progress safety fallback: a `MulticolLayouter` resume page emits zero fragments AND its continuation doesn't advance past the entry index — the single-oversized-child fallback (analog to `TableLayouter`'s single-oversized-row fallback). The diagnostic message names the column count, the per-column block-size, and the resume child index of the unemitted remainder. See `docs/deferrals.md#multicol-balancing-pagination`. Per Phase 3 Task 14 cycles 1-2 + cycle 2 hardening + post-PR-#57 review #2 hardening. |
| `LAYOUT-MULTICOL-NON-FINITE-GEOMETRY-001` | Warning | An arithmetic combination of `column-count` + `column-gap` would produce non-finite per-column inline-axis geometry (e.g., `column-gap: 1e300` with 100 columns drives `totalGap = (N-1) * columnGap` past `double.MaxValue` → `Infinity`). The CSS resolver's NaN / ±Infinity gate catches most pathological inputs at parse time; this code defends against the multiplicative blow-up that can still arise from individually-finite operands. The layouter clamps `column-gap` so `totalGap < containerInlineSize / 2` and continues emission; rendered geometry differs visually from author intent but doesn't NaN-poison downstream pagination math. The diagnostic message names the original column-gap, the column count, the container inline-size, the (non-finite) total gap, and the clamped value. Per Phase 3 Task 14 cycle 1 hardening Finding 4. |
| `LAYOUT-FLOAT-BREAK-INSIDE-NESTED-001` | Warning | A float subtree contains nested multicol or table content that broke mid-emission, returning a `LayoutContinuation` from the in-flow recursion. Floats are out-of-flow per CSS 2.2 §9.5; propagating their continuation through the in-flow pagination machinery requires float-tracking continuation machinery that's an existing Phase 3 Task 8 deferral (cycle 3+ scope). The block layouter discards the returned continuation (atomic-fallback behavior: the float's first-page slice is committed; the remainder is truncated rather than continued onto the next page) + emits this diagnostic so authors see the truncation. Fires at most once per page to avoid spam from pages with many such floats. The diagnostic message names the float box-kind. See `docs/deferrals.md#float-continuation-propagation`. Per Phase 3 Task 14 cycle 2 hardening Finding 1. |
| `LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001` | Warning | A flex container declared `flex-wrap: wrap-reverse`. L6 ships `wrap` in full (multi-line greedy packing, per-line alignment, sum-of-lines auto cross-size); `wrap-reverse` requires an additional cross-axis line-stacking reversal transform per CSS Flexbox L1 §6.3 ("same as wrap but the cross-start and cross-end directions are swapped") which is L7+ scope. Until then the layouter approximates `wrap-reverse` as `wrap`: items wrap correctly in main-axis DOM order, but the lines stack in the natural cross-axis direction rather than the reversed direction the author requested. Without this diagnostic the wrong rendering would be silent — the CSS declaration parses successfully but behaves like `flex-wrap: wrap`. Fires at most once per `AttemptLayout` invocation to avoid spam from containers with many items. See `docs/deferrals.md#flex-layouter-features`. Per Phase 3 Task 15 L6 post-PR-#66 review F#4. |
| `LAYOUT-GRID-TRACK-KIND-UNSUPPORTED-001` | Warning | A `grid-template-rows` / `-columns` track entry uses a kind that the cycle-1 (Hello World) GridLayouter doesn't yet size: fr / auto / min-content / max-content / minmax / fit-content / repeat. The cycle-0a/0b AST contract parses these forms; cycle 1 only layouts `<length>` (pixel) tracks. Until cycles 2+ expand coverage (cycle 2 ships fr; cycle 3 ships intrinsic; cycle 4 ships minmax / fit-content / repeat(int); cycle 7 ships auto-fill / auto-fit), non-length tracks contribute 0 px to the track sum + this diagnostic surfaces the silent drop. Fires once per `AttemptLayout`. Per Phase 3 Task 17 cycle 1. |
| `LAYOUT-GRID-PLACEMENT-APPROXIMATED-001` | Warning | A grid item's declared placement uses a value the cycle-1 GridLayouter doesn't yet support: `span N`, `<custom-ident>` (= named line), `<custom-ident> N`, OR a non-default `grid-{row,column}-end` (= author requested a multi-cell span). Cycle 1 supports only integer line-number placement + single-cell items; the diagnostic surfaces the author intent being approximated as auto-placement / shrunk to a single cell. Cycle 6 ships span; cycle 7 ships named lines + areas. Fires per item per axis. Per Phase 3 Task 17 cycle 1 + post-PR-#92 review F5. |
| `LAYOUT-GRID-IMPLICIT-TRACK-UNSUPPORTED-001` | Warning | A grid item was placed at a cell OUTSIDE the explicit grid bounds (= row/column index exceeds the declared track count, OR a 0-track grid has no cells to place into). Per CSS Grid §7.5 the implicit grid should auto-generate tracks via `grid-auto-rows` / `grid-auto-columns`; cycle 1 doesn't yet support implicit tracks → the item silently drops (no fragment emitted). Cycle 6 ships implicit-track generation. Fires once per dropped item. Per Phase 3 Task 17 cycle 1 + post-PR-#92 review F6. |
| `LAYOUT-GRID-NON-FINITE-GEOMETRY-001` | Warning | A grid container's cumulative track positions or fragment geometry overflowed to a non-finite value (NaN / ±Infinity). Individual track sizes are validated finite at AST-construction time (cycle-0a P3 #8 factories), but cumulative sums can still overflow when summing very large finite tracks (= hostile CSS like `grid-template-rows: 1e300px 1e300px`). The layouter detects the non-finite cumulative position + skips item emission so paint / PDF cannot be corrupted by garbage geometry. Mirrors `LAYOUT-MULTICOL-NON-FINITE-GEOMETRY-001`. Fires once per dispatch. Per Phase 3 Task 17 cycle 1 + post-PR-#92 review F9. |

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

Last updated: 2026-05-19 (Phase 3 Task 15 L6 post-PR-#66 review F#4: added `LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001` — emitted by `FlexLayouter` when an author declares `flex-wrap: wrap-reverse`. L6 ships `wrap` in full; the cross-axis line-stacking reversal that distinguishes `wrap-reverse` is L7+ scope. Pre-finding the layouter silently treated `wrap-reverse` as `wrap` — correct CSS declaration, wrong rendering. The diagnostic surfaces the approximation so authors aren't misled into thinking the layouter honored their request.). Earlier 2026-05-13 (Phase 3 Task 14 post-PR-#57 review #2 hardening Finding 2: cleaned up the `LAYOUT-MULTICOL-FORCED-OVERFLOW-001` row + the matching XML doc-comments in `PaginateDiagnosticCodes.cs` + `DiagnosticCodes.cs` — pre-fix they still listed branch (b), the "deep-nested multicol containers can't propagate" case, even though that branch was removed in the cycle 2 hardening Finding #1's multi-level continuation propagation lift. The docs now describe only the surviving forward-progress safety fallback for single-oversized-child resume pages.). Earlier 2026-05-13 (Phase 3 Task 14 cycle 2 hardening Finding 1: added `LAYOUT-FLOAT-BREAK-INSIDE-NESTED-001` — emitted by `BlockLayouter` when a float subtree contains nested multicol/table content that broke mid-emission. The cycle 2 hardening lifted the depth==1-only continuation propagation limit for in-flow recursion; floats remain out-of-flow per CSS 2.2 §9.5, so propagating their nested continuation requires float-tracking machinery that's an existing Phase 3 Task 8 deferral. The block layouter discards the float's nested-continuation return + emits this diagnostic so the truncation is visible. The same hardening pass also narrowed `LAYOUT-MULTICOL-FORCED-OVERFLOW-001`: the deep-nested multicol case (recursion depth ≥ 2) no longer emits the diagnostic — it now splits cleanly through the in-flow propagation lift). Earlier 2026-05-13 (Phase 3 Task 14 cycle 2 hardening Finding 3: added `LAYOUT-MULTICOL-COLUMN-COUNT-CLAMPED-001` — the `MulticolLayouter` surfaces this when an author-supplied `column-count` exceeds the safety cap (= 1000) and is silently clamped. Pre-finding the clamp was silent; without surfacing it, rendered output with N > 1000 columns visually disagrees with the stylesheet — the diagnostic now lets authors know the requested column count was reduced). Earlier 2026-05-12 (Phase 3 Task 14 cycle 2: narrowed `LAYOUT-MULTICOL-FORCED-OVERFLOW-001` semantics. Cycle 2 ships multi-page multicol via `MulticolContinuation` — clean multi-page splits no longer emit the diagnostic. The code now fires only for forward-progress safety fallbacks (resume page with zero-progress) + the deep-nested multicol case (depth ≥ 2 in `EmitBlockSubtreeRecursive` can't propagate cycle 2's single-level callback). Sub-cycle 3+ may generalize multi-level recursive propagation). Earlier 2026-05-12 (Phase 3 Task 14 cycle 1: added `LAYOUT-MULTICOL-FORCED-OVERFLOW-001` — the MulticolLayouter surfaces this when in-flow content of a multicol container overflows its N columns; Hello World multi-column layout ships with fixed-column-count, equal-split column widths, single-page atomic emit. Multi-page multicol + balancing + column rules + `column-width` auto-count are sub-cycle 2+ scope; see `docs/deferrals.md#multicol-balancing-pagination`). Earlier 2026-05-12 (Phase 3 Task 13 cycle 2: added `LAYOUT-TABLE-HEADER-FOOTER-OVERSIZED-001` — the row-pagination loop now surfaces this when `<thead>` + `<tfoot>` combined exceed the fragmentainer, leaving no room for body rows alongside the per-page repeat contract). Earlier 2026-05-11 (Phase 3 Task 13 cycle 1 hardening review: added `LAYOUT-TABLE-REWIND-NOT-SUPPORTED-001` (Finding 5) + `LAYOUT-TABLE-ROWSPAN-CROSSES-PAGE-001` (Finding 6) — the row-pagination loop now surfaces these when the resolver returns Rewind at a row boundary or when a rowspan cell would otherwise cross a page boundary). Earlier 2026-05-10 (Phase 3 Task 11 cycle 1 sub-cycle 1 hardening review: added `LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001` (Finding #4) + `LAYOUT-INLINE-UNSUPPORTED-001` (Finding #6) — the inline-only-block dispatch now emits one of these when an atomic inline is encountered or the inline pass throws `NotSupportedException`). Earlier 2026-05-08 (Phase 3 Task 1: hardened the `PAGINATION-OPTIMIZER-FALLBACK-001` + `PAGINATION-FORCED-OVERFLOW-001` descriptions when the pagination foundation landed). Earlier 2026-05-07 (Phase C: added `CSS-CASCADE-OVERFLOW-001`; Phases A + B added `HTML-EVENT-HANDLER-IGNORED-001`, `HTML-DOM-LIMIT-EXCEEDED-001`, `HTML-STRIP-NOT-STABLE-001`, `HTML-INPUT-TOO-LARGE-001`, `CSS-VAR-EXPANSION-LIMIT-001`, `CSS-CONTENT-FUNCTION-UNSUPPORTED-001`, `CSS-RULE-LIMIT-EXCEEDED-001`).
