# Phase 2 — CSS Engine + DOM Pipeline

**Status:** ⏳ pending (after Phase 1).
**Time:** team estimate 10 wk → Claude Opus 4.7 high: **2–3 wk**.
**Tagged release:** `0.3.0-alpha` — HTML+CSS arrives as a styled box tree; rendering still pending.

## Goal

Wire AngleSharp + AngleSharp.Css to parse arbitrary modern HTML+CSS into a DOM and CSSOM, then build NetPdf's own cascade and computed-value layer on top, ending in a `BoxTree` ready for Phase 3 layout. Diagnostics surface for unsupported render features (parsed but not rendered).

## Prerequisites

- Phase 1 complete and `0.1.0-alpha` tagged. PDF writer + text foundation functional.
- Read [phase-1-pdf-writer-and-text.md](phase-1-pdf-writer-and-text.md) — you'll consume `NetPdf.Text` here.
- Read [../compatibility-matrix.md](../compatibility-matrix.md) — every CSS feature listed there as `✅` or `🧪` parses in this phase even if rendering ships later.
- Read [../diagnostics-codes.md](../diagnostics-codes.md) — every diagnostic emitted by the parser/cascade has a stable code.

## Deliverables

### `NetPdf.Css` — CSS engine

- **HTML parsing host** (`src/NetPdf/HtmlParsingHost.cs`):
  - Configures AngleSharp `IConfiguration` **without** `IScripting`. `<script>` elements are collected as a list and surfaced as `HTML-SCRIPT-IGNORED-001` diagnostics with their source location.
  - Honors `HtmlPdfOptions.BaseUri` for relative URI resolution.
- **CSS parser adapter** (`src/NetPdf.Css/Parser/`):
  - Wraps AngleSharp.Css's tokenizer/parser.
  - Adapts AngleSharp.Css AST nodes into NetPdf's internal AST (`StyleRule`, `AtRule`, `Selector`, `Declaration`, `Value`). Downstream stages must NOT see AngleSharp types.
  - **Pre-pass tokenizer extension** for syntax AngleSharp.Css doesn't yet recognize: `oklch()`, `oklab()`, `color-mix()`, `light-dark()`, `@container` syntax, `@layer` ordering, anchor positioning. These tokens are preserved as opaque AST nodes so they survive parsing even when rendering is post-v1.
- **Selector compiler** (`src/NetPdf.Css/Selectors/`):
  - Compiles selectors to compact bytecode for fast match (right-to-left).
  - `SelectorMatcher` evaluates bytecode against an element. Cached per-stylesheet.
  - Supports: type, class, id, attribute (with `=`/`~=`/`|=`/`^=`/`$=`/`*=`), descendant/child/adjacent-sibling/general-sibling combinators, `:hover` (always false in v1), `:focus` (always false), `:first-child`, `:last-child`, `:nth-child(...)`, `:not()`, `:is()`, `:where()`, `:has()` (parsed; rendering is `CSS-HAS-RENDERING-NOT-IMPLEMENTED-001` in v1).
- **Cascade resolver** (`src/NetPdf.Css/Cascade/`):
  - `CascadeResolver` applies origin → importance → layers (`@layer`) → specificity → source order.
  - Selector matching cache (Bloom-filter pre-filter on element class+id, per stylesheet).
  - Outputs `MatchedRuleSet` per element.
- **Computed value resolver** (`src/NetPdf.Css/ComputedValues/`):
  - Resolves `inherit`, `initial`, `unset`, `revert`, `revert-layer`.
  - Full `var()` substitution with circular-reference detection (emit `CSS-VAR-CIRCULAR-001`) + expansion-limit guard (emit `CSS-VAR-EXPANSION-LIMIT-001` for non-cyclic depth/output overruns). **Known v1 gap:** `var()`-bearing shorthand declarations (`border: var(--bundle)`, `background: var(--bg)`) rely on AngleSharp.Css's parse-time longhand expansion, which is incorrect for opaque substitution where the var() resolves to multiple tokens that need re-parsing. CSS Custom Properties L1 §3 calls these "pending substitution values" and requires shorthand expansion to happen AFTER var() resolution. Tasks 9–10 typed-value parsers will close the gap when they re-expand shorthands.
  - `calc()` / `min()` / `max()` / `clamp()` / `abs()` / `sign()` reduction in [`CalcResolver`](../../src/NetPdf.Css/Cascade/CalcResolver.cs) — recursive-descent parser per CSS Values L4 §10. Reduces fully when all operands resolve to a single dimension class (`px`, `%`, `deg`, `ms`, `Hz`, `dppx`) or unitless `Number`. Spec-correct subset (Task 9 + cycle 1): operator precedence + paren grouping; ASCII case-insensitive function names; explicit unit conversion (`1in = 96px` etc.); nested math (`calc(min(8px, 4px) + 2px)` → `6px`); same-unit dimension division yields a unitless `Number` per §10.10 (so `calc(16px / 2px) = 8`); `+`/`-` require whitespace on both sides per §10.4. **v1 deferred** (preserved verbatim, no diagnostic — typed-value pipeline in Task 10+ finalizes once font/viewport/container metrics are known): font-relative units (`em`, `rem`, `ch`, `ex`, `lh`, `rlh`, `cap`, `ic`), viewport-relative (`vw`/`vh`/`svw`/`lvw`/`dvw`/`vmin`/`vmax`/etc.), container-relative (`cqw`/`cqh`/`cqi`/`cqb`/`cqmin`/`cqmax`), and any expression mixing percentage with another dimension class. **v1 invalid** (preserved + `CSS-CALC-INVALID-001`): `dim * dim` (no area unit), cross-class division (length / angle), missing whitespace around `+`/`-`, malformed input. Division by zero emits `CSS-CALC-DIV-BY-ZERO-001`. Pipeline order is `var()` substitution → calc reduction (verified by [`VarToCalcPipelineTests`](../../tests/NetPdf.UnitTests/Css/Cascade/VarToCalcPipelineTests.cs)). Trigonometric (`sin`/`cos`/`tan`), exponential (`pow`/`sqrt`/`hypot`/`log`/`exp`), and stepped-value (`mod`/`rem`/`round`) functions are **post-v1**.
  - Property-specific resolvers for: lengths, colors (incl. `oklch`/`color-mix` / `lab` / etc.), font-family lists, gradients, backgrounds, transforms.
- **`ComputedStyle` storage layer** (`src/NetPdf.Css/ComputedValues/ComputedStyle.cs`):
  - **Sealed class** (not the readonly struct originally sketched) so the cascade can share instances and pass to layout passes without struct-copy worries. Per-instance ≈ 24-byte object header + 8 × `PropertyMetadata.Count` slot bytes + bitmap bytes (~530 bytes for the current 63-property registry).
  - `[InlineArray(PropertyMetadata.Count)]` slot storage + `[InlineArray]` ulong "is-set" bitmap so `Get`/`Set`/`IsSet`/`Unset` are O(1) cache-friendly index operations.
  - Companion `ComputedSlot` 8-byte readonly struct holds each value with a tag byte + payload (color / float32-length / int32 / keyword / fixed-point percentage / side-table-index / composite). Per-property typed accessors (e.g., `style.Color` returning a typed `RgbaColor`) live in Tasks 9–10 atop the typed value tree.
  - Custom properties (`--*`) in a lazily-allocated `Dictionary<string, ComputedSlot>` with ordinal (case-sensitive) comparer per CSS Custom Properties L1 §2; names validated for `--` prefix + identifier body.
  - **Pooled** via `Rent`/`Dispose` against a process-wide bounded `ConcurrentBag<ComputedStyle>` (capped at 256 instances). On `Rent` the instance is reset (slots, bitmap, custom-property dict cleared); `Dispose` queues it back. ArrayPool was the original sketch but a class-instance pool fits the reference semantics and lets `[InlineArray]` keep its inline-storage promise.
- **Property tables via source generator** (`src/NetPdf.SourceGen/CssPropertyGenerator.cs`):
  - Reads `properties.json` at `src/NetPdf.Css/properties.json` — the single source of truth for every CSS property the cascade knows about. Schema fields (all REQUIRED, validated by the generator with `NPDFGEN0005`): `name` (CSS property name), `id` (PascalCase enum identifier), `type` (`PropertyType` value name), `default` (initial value text per spec), `inherit` (per spec), `applies_to` (`AppliesTo` value name), `computed` (`ComputedValueKind` value name).
  - Emits a generated `PropertyId : ushort` enum (one value per property, doubles as `Table` index), `PropertyMetadata.Table` (`ImmutableArray<PropertyMeta>` so consumers cannot mutate entries), `PropertyMetadata.Count`, and `PropertyMetadata.NameToId` (`FrozenDictionary<string, PropertyId>` with case-insensitive lookup, lazily built).
  - **Task 4 scope** is the `PropertyId` enum + `PropertyMetadata` table + `NameToId` lookup. **Per-property typed accessors** (e.g., `style.Color`, `style.MarginTop`) are part of **Task 5**, where they live as instance methods on the `[InlineArray]`-backed `ComputedStyle` struct and call into per-property parser hooks. Parser/computed-value hooks themselves are introduced incrementally in Tasks 9–10 alongside the typed value tree.
  - Single command to add a new property: append to `properties.json`, rebuild. The generator emits `NPDFGEN0001`–`NPDFGEN0005` diagnostics on empty/malformed JSON, missing required fields, duplicate names/ids, and invalid C# identifiers — build breaks rather than silently emitting wrong code.

### `NetPdf.Layout.Boxes` — box generation

- **`Box` hierarchy** (`src/NetPdf.Layout/Boxes/`):
  - `Box` abstract base + `BoxKind` byte enum.
  - Concrete: `BlockBox`, `InlineBox`, `AnonymousBlockBox`, `AnonymousInlineBox`, `FlexContainerBox`, `FlexItemBox`, `GridContainerBox`, `GridItemBox`, `TableBox`, `TableRowGroupBox`, `TableRowBox`, `TableCellBox`, `TableColumnBox`, `TableColumnGroupBox`, `TableCaptionBox`, `MulticolBox`, `ReplacedAtomicBox`, `ReplacedBlockBox`, `BrBox`, `GeneratedContentBox`.
  - `BoxList` struct wrapping `ArrayPool<Box>` for child collections.
- **`BoxBuilder`** (`src/NetPdf.Layout/Boxes/BoxBuilder.cs`):
  - Walks the styled DOM.
  - Emits one box per element + pseudo-elements per `display` value.
  - Handles **anonymous box insertion**: when a block has mixed inline+block children, wrap inline runs in anonymous block boxes.
  - Handles **table fixup**: missing `<tbody>` / `<tr>` / `<td>` get auto-generated; `display: table-cell` without ancestor `display: table-row` synthesizes wrappers.
  - Materializes `::before`, `::after`, `::marker`, `::first-line`, `::first-letter` pseudo-elements.

### `NetPdf.Layout.Semantic` — semantic IR (built but not emitted)

- **`SemanticTreeBuilder`** runs in parallel with `BoxBuilder`. Captures HTML semantics: H1–H6, P, L/LI (with markers), Table/TR/TH/TD, Link (with `href`), Figure (alt from `alt`/`aria-label`/`figcaption`), BlockQuote, Code, Sect (header/footer/nav/main/aside/article/section).
- Each text run / image / vector path stores a `SemanticId` linking back to the tree.
- v1 carries this IR but does not emit tagged PDF; v1.1 wires it into `NetPdf.Pdf.StructTree*`.

### Diagnostics framework integration

- `Diagnostic` records emitted via `IDiagnosticsSink` if provided; aggregated into `PdfRenderResult.Warnings` / `UnsupportedFeatures` / `ResourceFailures` for `ConvertDetailed`.
- Every code in `docs/diagnostics-codes.md` that's CSS-related (`CSS-*`) gets emitted from this phase's components.

## Spec references

| Topic | Source |
|---|---|
| HTML5 (parsing) | https://html.spec.whatwg.org/multipage/parsing.html |
| CSS Syntax L3 | https://www.w3.org/TR/css-syntax-3/ |
| CSS Selectors L4 | https://www.w3.org/TR/selectors-4/ |
| CSS Cascade L4 | https://www.w3.org/TR/css-cascade-4/ |
| CSS Custom Properties L1 | https://www.w3.org/TR/css-variables-1/ |
| CSS Values L4 (`calc`, `min`, `max`, etc.) | https://www.w3.org/TR/css-values-4/ |
| CSS Color L4 | https://www.w3.org/TR/css-color-4/ |
| CSS Color L5 (`color-mix`, `light-dark`) | https://www.w3.org/TR/css-color-5/ |
| CSS Display L3 | https://www.w3.org/TR/css-display-3/ |
| CSS Box L4 | https://www.w3.org/TR/css-box-4/ |
| CSS Pseudo L4 | https://www.w3.org/TR/css-pseudo-4/ |
| AngleSharp docs | https://anglesharp.github.io/ |

## Work breakdown (ordered)

| # | Task | Mini-est. | Depends on |
|---|---|---|---|
| 1 | HTML parsing host (AngleSharp wireup, no scripting) | 1 d | — |
| 2 | CSS parser adapter (AngleSharp.Css → internal AST) | 2 d | 1 |
| 3 | Pre-pass tokenizer extension (modern color/calc/at-rules) | 2 d | 2 |
| 4 | Source-generated property tables from `properties.json` | 3 d | — |
| 5 | `ComputedStyle` flat struct + `[InlineArray]` backing | 1 d | 4 |
| 6 | Selector compiler + bytecode + matcher | 4 d | 2 |
| 7 | Cascade resolver (origin/importance/layers/specificity/order) | 3 d | 6 |
| 8 | `var()` substitution with circular-ref detection | 2 d | 5, 7 |
| 9 | `calc`/`min`/`max`/`clamp`/`abs`/`sign` resolver | 2 d | 8 |
| 10 | Per-property resolvers (length, color, font-family, gradients, transforms) | 4 d | 9 |
| 11 | `Box` hierarchy + `BoxKind` enum | 1 d | — |
| 12 | `BoxBuilder` DOM walk + anonymous box insertion | 2 d | 5, 11 |
| 13 | Table fixup in `BoxBuilder` | 2 d | 12 |
| 14 | Pseudo-element materialization (`::before`/`::after`/`::marker`/`::first-line`/`::first-letter`) | 3 d | 12 |
| 15 | `SemanticTreeBuilder` parallel pass | 2 d | 12 |
| 16 | Diagnostics emission for unsupported CSS features | 2 d | 7, 10 |
| 17 | Integration test: HTML+CSS → BoxTree end-to-end | 2 d | 12–16 |
| 18 | Layout snapshot test infrastructure (`tests/NetPdf.LayoutSnapshots`) | 1 d | 17 |
| 19 | Tag `0.3.0-alpha` + CHANGELOG | 0.5 d | all |

**Total: ~35 days. With Claude Opus 4.7 high + daily Roland review: 2–3 calendar weeks.**

## Implementation notes

### AngleSharp configuration
```csharp
var config = Configuration.Default
    .WithCss()        // AngleSharp.Css; we bridge to our parser via the adapter
    .WithStandardLoader(setup => setup.IsResourceLoadingEnabled = false);  // resources via our IResourceLoader, not AngleSharp's
// NO .WithJs() — scripting stays off.
var context = BrowsingContext.New(config);
var doc = await context.OpenAsync(req => req.Content(html).Address(baseUri));
```

### Pre-pass tokenizer
Don't replace AngleSharp.Css's tokenizer. Run a thin pre-tokenizer over the input that:
1. Recognizes modern functions/at-rules our compatibility-matrix lists.
2. Either rewrites them to a form AngleSharp.Css accepts, OR replaces them with a sentinel token that our adapter recognizes and substitutes for an opaque AST node.
3. Preserves source location for diagnostics.

Keeps AngleSharp.Css unmodified; we extend at the edges.

### Property tables — `properties.json` schema
```json
{
  "properties": [
    {
      "name": "color",
      "id": "Color",
      "type": "color",
      "default": "canvastext",
      "inherit": true,
      "applies_to": "all",
      "computed": "absolute_color"
    },
    {
      "name": "margin-top",
      "id": "MarginTop",
      "type": "length_percent",
      "default": "0",
      "inherit": false,
      "applies_to": "all_block_inline_replaced"
    }
  ]
}
```

The source generator emits:
- `enum PropertyId : ushort { ... }`
- `PropertyMetadata.NameToId` — `FrozenDictionary<string, PropertyId>` (built once at type-init, case-insensitive O(1) lookup).
- `PropertyMetadata.Table` — `ImmutableArray<PropertyMeta>` indexed by `(int)PropertyId`. Returned as `ImmutableArray<T>` so consumers can't mutate entries.
- `PropertyMetadata.Count` — total registered properties.

Per-property typed accessor methods on `ComputedStyle` are introduced in **Task 5**, not here. Task 4 ships the registry; Task 5 wires the `[InlineArray]`-backed value type that exposes `style.Color`/`style.MarginTop`/etc. accessors. Per-property parser/computed-value hooks themselves come in Tasks 9–10 alongside the typed value tree.

### `ComputedStyle` access pattern
```csharp
public readonly struct ComputedStyle
{
    [InlineArray(140)]
    private struct PropertyStorage { private long _slot0; }

    private readonly PropertyStorage _storage;

    public Color Color
    {
        get => Color.Decode(GetSlot(PropertyId.Color));
    }

    private long GetSlot(PropertyId id) => _storage[(int)id];
}
```

Each slot is 8 bytes — sufficient for a length, color, percentage, integer, or short interned string ref. Larger values (font-family lists, gradients, transforms) are stored in side tables keyed by `int` index encoded into the slot.

### Selector bytecode
```
PUSH_ELEMENT
MATCH_TYPE   "div"
MATCH_CLASS  "container"
DESCENDANT
MATCH_TYPE   "h1"
COMMIT
```
Right-to-left evaluation: start at the key selector (rightmost), check it matches, then walk parents looking for the next selector component. Bloom filter on each element's `(tag, classList, id)` accelerates rejection.

### Modern syntax tokens
| Function | Status | Note |
|---|---|---|
| `oklch()`, `oklab()` | parsed (Task 3 pre-pass); typed evaluation cycle-2 | Cycle-1 [`ColorResolver`](../../src/NetPdf.Css/ComputedValues/PropertyResolvers/ColorResolver.cs) recognizes the function name and emits `CSS-PROPERTY-VALUE-INVALID-001`. AngleSharp.Css 1.0.0-beta.144 silently corrupts `oklch()` to bogus rgba — the pre-pass preserves the authored text in `CssDeclaration.Value.RawText`. Cycle-2 (paired with the pre-pass capture) computes typed values per CSS Color 4 §6 (Oklab) and §7 (Oklch). |
| `color-mix()` | parsed (Task 3 pre-pass); typed evaluation cycle-2 | Cycle-1 [`ColorResolver`](../../src/NetPdf.Css/ComputedValues/PropertyResolvers/ColorResolver.cs) emits `CSS-PROPERTY-VALUE-INVALID-001`. AngleSharp.Css drops the declaration entirely; the pre-pass restores the authored value as raw text. Cycle-2 computes the mix per CSS Color 5 §2. |
| `light-dark()` | parsed (Task 3 pre-pass); typed evaluation cycle-2 | Cycle-1 [`ColorResolver`](../../src/NetPdf.Css/ComputedValues/PropertyResolvers/ColorResolver.cs) emits `CSS-PROPERTY-VALUE-INVALID-001`. Cycle-2 evaluates against `PreferredColorScheme`. |
| `lab()`, `lch()`, `hwb()` | parsed by AngleSharp.Css; typed evaluation cycle-2 | Cycle-1 emits `CSS-PROPERTY-VALUE-INVALID-001`. Cycle-2 will compute via L4 §4.3 / §4.4 conversion. |
| `rgb()`/`rgba()`, `hsl()`/`hsla()` | ✅ Task 10 cycle 1 (review-pass strict syntax) | Both legacy comma + modern whitespace + slash-alpha. Per Color 4 §4.2.1 / §4.3.1: legacy disallows `/` (4-comma alpha); modern disallows commas; modern with 4 args MUST have `/` before alpha; legacy 3 RGB components must be all-numbers OR all-percentages. Mixed forms emit `CSS-PROPERTY-VALUE-INVALID-001`. All numeric inputs (channels / alpha / hue / sat / light) checked for `IsFinite` before clamping. |
| Named + system colors + `currentcolor` | ✅ Task 10 cycle 1 | 147 named colors per Color 4 §6.1 in [`CssNamedColors`](../../src/NetPdf.Css/ComputedValues/PropertyResolvers/CssNamedColors.cs); 20 system colors per §10 in [`CssSystemColors`](../../src/NetPdf.Css/ComputedValues/PropertyResolvers/CssSystemColors.cs) with print-friendly fixed values. `currentcolor` uses dedicated [`ComputedSlotTag.CurrentColor`](../../src/NetPdf.Css/ComputedValues/ComputedSlot.cs) (no payload — no user color can collide with the sentinel). |
| `@container` | parsed; emits `CSS-CONTAINER-QUERY-UNSUPPORTED-001` rendering-time | Roadmap v1.4. |
| `@layer` | parsed + cascade applies layer ordering | |
| `:has()` | parsed + selector compiles | Rendering emits `CSS-HAS-RENDERING-NOT-IMPLEMENTED-001` until v1.4. |

## Test plan

| Component | Test type | Location |
|---|---|---|
| HTML parser | Smoke | `tests/NetPdf.UnitTests/Css/HtmlParsingHostTests.cs` — round-trip arbitrary HTML, verify DOM. |
| CSS parser adapter | Unit | Round-trip every CSS construct in our compatibility matrix; verify AST. |
| Pre-pass tokenizer | Unit | Per-feature: `oklch()`, `color-mix()`, `@container`, etc. |
| Selector compiler | Unit | All selector forms; bytecode verification. |
| Selector matcher | Unit | Hand-built DOMs + selectors → expected match/no-match. |
| Cascade resolver | Unit | Specificity/origin/importance/layers test matrix from CSS Cascade L4 examples. |
| `var()` resolution | Unit | Including circular references and fallback chains. |
| `calc()` resolution | Unit | Mixed units, nested calc, percentage resolution. See [`CalcResolverTests`](../../tests/NetPdf.UnitTests/Css/Cascade/CalcResolverTests.cs) + [`CalcResolverReviewCycle1Tests`](../../tests/NetPdf.UnitTests/Css/Cascade/CalcResolverReviewCycle1Tests.cs) (regression coverage for spec-correctness recs) + [`VarToCalcPipelineTests`](../../tests/NetPdf.UnitTests/Css/Cascade/VarToCalcPipelineTests.cs) (var → calc end-to-end). |
| `ComputedStyle` | Unit | Set/get every property; verify cache-friendly layout (sizeof check). |
| Box generation | Snapshot | `tests/NetPdf.LayoutSnapshots/` — input HTML/CSS → expected `BoxTree` JSON. |
| Anonymous box insertion | Snapshot | Specific cases from CSS Box L4. |
| Table fixup | Snapshot | Missing tbody/tr/td cases. |
| Pseudo-elements | Snapshot | `::before`/`::after` content; `::marker`; `::first-line`. |
| Diagnostics emission | Unit | Inputs that should emit each `CSS-*` code; verify code + severity + location. |
| Real corpus parsing | Integration | Run `tests/NetPdf.RealDocuments/Corpus/Invoices/*.html` through the pipeline; verify zero unhandled exceptions and the expected diagnostic set. |

## Exit criteria

Phase 2 is complete when:

1. ✅ All Phase 2 unit tests pass.
2. ✅ Layout snapshot tests pass for hand-built corpus.
3. ✅ All 4 invoice corpus files parse to `BoxTree` without exception.
4. ✅ Tailwind-CDN samples emit `HTML-SCRIPT-IGNORED-001` and the inline `<style>` blocks parse correctly.
5. ✅ Anvil running-elements sample's `@page { @bottom-* { content: element(...) } }` parses without error (rendering is Phase 3).
6. ✅ AOT smoke test still passes — no reflection introduced.
7. ✅ Performance: parse + style + box-gen for a 100 KB invoice in < 50 ms p50.
8. ✅ CHANGELOG updated, `0.3.0-alpha` tagged.

## Common pitfalls

- **AngleSharp DOM types leaking into public API.** Strict rule: nothing under `src/NetPdf/` re-exports AngleSharp types. Use the adapter consistently.
- **AngleSharp.Css missing modern syntax.** Don't crash; pre-pass extends. Add a unit test for every modern-CSS feature in our compatibility matrix.
- **Selector caching staleness.** When stylesheets change between conversions, invalidate the bloom-filter caches. Clean per-document.
- **Cascade layer ordering subtlety.** First `@layer` declared has *lowest* precedence — opposite of intuition. Re-read CSS Cascade L4 §6.4.
- **`var()` infinite recursion.** Detect via depth counter or visited-set; emit `CSS-VAR-CIRCULAR-001`, fall back to `unset`.
- **Anonymous box rules.** When a block's children alternate inline/block, every inline run becomes an anonymous block. Easy to miss the "and pseudo-elements count" detail.
- **`::first-letter` is hard.** Affects only the first letter of the first formatted line — interacts with line layout. v1 can defer detail to Phase 3 (where lines exist); v0 just identify it.
- **Table fixup order matters.** Generate `<tbody>` before generating missing `<tr>` before generating missing `<td>`. Spec ordering in CSS Tables L3.
- **Performance regressions in cascade.** Selector matching is the hot path. Profile early; the bloom-filter pre-filter is essential, not optional.

## Hand-off to Phase 3

State of the repo at end of Phase 2:
- HTML+CSS parses to a fully-styled `BoxTree` with computed values resolved.
- Semantic IR is built (deferred emission until v1.1).
- All `CSS-*` diagnostic codes emit at the right places.
- Performance baseline: parse-style-box-gen < 50 ms for 100 KB invoice.

Phase 3 begins by implementing the layouters that consume `BoxTree`. The first layouter (`BlockLayouter`) consumes `BoxTree` → produces a `FragmentTree` (paginated). All subsequent layouters follow the same pattern.
