# Accessibility & tagged PDF — roadmap (TODO)

NetPdf's v1.0 output is **NOT** a tagged PDF. Tagged-PDF emission (= PDF/UA-1
conformance) ships in **v1.1**. This doc consolidates the work surface so the
v1.1 cycle has a single checklist to follow instead of re-deriving the
scope from scattered mentions.

## Current state (v1.0)

What's already in place (= the prep work CLAUDE.md's "Common pitfalls"
section calls out — *"Tagged PDF / PDF/UA / PDF/A are post-v1. Build the
semantic IR alongside layout (it's prepared for) but don't emit tagged
structure in v1."*):

- **`NetPdf.Layout.Semantic`** — the semantic IR is **built** by the
  Phase 2 pipeline (`Phase2Pipeline` runs `SemanticTreeBuilder.Build`
  in parallel with `BoxBuilder` so each Phase 2 result carries both a
  box tree and a semantic tree). Reference:
  `src/NetPdf.Layout/Semantic/SemanticKind.cs`,
  `src/NetPdf.Layout/Semantic/SemanticTreeBuilder.cs`,
  `src/NetPdf/Phase2/Phase2Pipeline.cs`.
- **`IContentStream.BeginMarkedContent` /
  `BeginMarkedContentWithProperties` / `EndMarkedContent`** — the PDF
  writer surface for the BDC/BMC/EMC operators per ISO 32000-2 §14.6
  is already implemented. The *WithProperties* overload is the one
  v1.1's tagging emission will use (= it writes the inline
  `<< /MCID N >>` property dictionary directly after the tag name).
  Reference: `src/NetPdf.Pdf/Content/IContentStream.cs`,
  `src/NetPdf.Pdf/Content/ContentStreamWriter.cs`.
- **`HTML-EVENT-HANDLER-IGNORED-001` diagnostic** — `on*` event-handler
  attributes are stripped at HTML parse time so they can't leak into
  v1.1's accessibility metadata.
- **Spec resolution** — see [`docs/pdf-spec-notes.md`](pdf-spec-notes.md)
  §14.8.2 for the "v1 is NOT tagged" decision + rationale.
- **Compatibility matrix row** — see
  [`docs/compatibility-matrix.md`](compatibility-matrix.md): "Tagged PDF
  / PDF/UA-1 | 📥 | post-v1 | Semantic IR built; emission deferred to
  v1.1."

What's deliberately deferred to v1.1 (= the TODO):

- Structure-tree emission (= `/StructTreeRoot` in the Catalog).
- Wrapping every painted text run / image / table cell in
  BDC/EMC marked-content sequences keyed to a structure element.
- Document-level metadata (`/Lang`, `/Title`, `/Subject` in Info /
  Metadata).
- Reading-order validation against the visual order.

## v1.1 work breakdown (TODO checklist)

Each item below should land as its own task/PR (= the cycle 6-7 style).
Phases / cycles to be assigned at v1.1 planning time.

### Phase A — `/StructTreeRoot` infrastructure

- [ ] **A1.** Add `/StructTreeRoot` dictionary emission in
  `src/NetPdf.Pdf/Document/PdfDocumentWriter.cs` (or equivalent). Wire it
  into the `Catalog`'s `/StructTreeRoot` key.
- [ ] **A2.** Build the `/StructElem` writer + per-element child / `/K`
  encoding. Structure elements live in their own indirect-object range to
  match Acrobat / Foxit conventions.
- [ ] **A3.** Emit `/MarkInfo << /Marked true >>` in the Catalog
  per ISO 32000-2 §14.8.2.

### Phase B — semantic-IR → structure-tree bridge

- [ ] **B1.** Build a `SemanticTree → /StructTreeRoot` projection in a new
  `NetPdf.Pdf.Tagging` namespace. Consumes the
  `SemanticTreeBuilder` output (= already populated by Phase 2/3).
- [ ] **B2.** Map each `SemanticKind` to the PDF/UA structure type per
  ISO 32000-2 §14.8.4 (Document, Sect, P, H1..H6, L, LI, Table, TR, TD,
  Figure, Caption, Link, etc.).
- [ ] **B3.** Decorative content (= images with explicit `alt=""`,
  CSS-generated content, presentational borders, etc.) does NOT enter
  the tagged structure tree. The painter wraps it in
  `/Artifact … BDC … EMC` per §14.8.2.2.2 — Artifact has no `/K`
  entry, no parent struct elem, and no MCID. The
  `SemanticTreeBuilder.cs` decorative-branch already separates
  decorative content from semantic content; the v1.1 work is wiring
  that separation through to the painter.

### Phase C — marked-content sequences

Operator order per ISO 32000-2 §14.6: `/Tag << /MCID N >> BDC … EMC`
(= tag name first, property dictionary second, `BDC` operator last).
The writer surface for the tag + dict + operator combo is
`IContentStream.BeginMarkedContentWithProperties(tag, dict)`; the
caller emits `EndMarkedContent()` to close the sequence.

- [ ] **C1.** Painter wraps every text run with `/Span << /MCID N >> BDC`
  … `EMC`. The MCID is the index in the parent structure element's
  `/K` array; the painter calls `BeginMarkedContentWithProperties(Span,
  { MCID: N })`.
- [ ] **C2.** Painter wraps every **meaningful** image with
  `/Figure << /MCID N >> BDC` … `EMC` + a Figure structure element
  whose `/Alt` is sourced from the HTML `alt` attribute. Images
  authored as decorative (`alt=""`) skip the Figure path entirely and
  go through the **B3** `/Artifact` branch instead (= per Matterhorn
  Protocol 1.1 every `/Figure` MUST have a meaningful `/Alt`; the
  empty-`/Alt ()` shortcut is a known PAC warning).
- [ ] **C3.** Painter wraps table cells (TD/TH) with their MCID-keyed
  marked-content. TH gets `/Scope` (Row/Column/Both per `<th scope>`
  parsing).
- [ ] **C4.** Link annotations get a sibling `/Link` structure element
  whose `/A` matches the annotation's `/A` action.

### Phase D — document metadata

- [ ] **D1.** Surface a per-document `/Lang` value (BCP 47, sourced from
  `<html lang="…">` or a `HtmlPdfOptions.DocumentLanguage` override).
- [ ] **D2.** Per-element `/Lang` override on structure elements where
  the source HTML changed the language inline (`<span lang="es">…`).
- [ ] **D3.** `/Title`, `/Subject`, `/Author` in `/Info` + the
  XMP metadata stream per ISO 32000-2 §14.3.2.
- [ ] **D4.** `/DisplayDocTitle true` in `/ViewerPreferences` so
  AT tools announce the document title.

### Phase E — reading-order + role guarantees

- [ ] **E1.** Validation pass: the semantic tree's DFS order must
  match the natural visual reading order. Add a debug-build assertion
  for the common cases (= floats + position:absolute can break this;
  document where v1.1 ships approximations vs. spec-correct ordering).
- [ ] **E2.** Heading hierarchy validation: no skipped levels (H1 → H3
  without H2). Emit `LAYOUT-ACCESSIBILITY-HEADING-SKIP-001` (= new
  diagnostic code per [`docs/diagnostics-codes.md`](diagnostics-codes.md)
  workflow).
- [ ] **E3.** Empty / missing alt-text on `<img>` emits an
  `HTML-IMAGE-MISSING-ALT-001` diagnostic so authors see the gap.
- [ ] **E4.** Table-without-headers + form-control-without-label
  diagnostics for the corpus authoring tier.

### Phase F — conformance + test harness

PDF/UA conformance has two distinct gate shapes — the automated
**veraPDF** check (= command-line, deterministic, runs in CI) and the
manual **PAC 2024** check (= GUI-only tool from Foxit; reviewer runs
it during release validation). The two are NOT interchangeable: PAC
catches several semantic failures veraPDF flags only as warnings (=
heading-skip detection, table-header coverage, link discoverability).

- [ ] **F1a.** Add a `NetPdf.PdfUaConformance` test project that runs
  the veraPDF PDF/UA-1 validator (JAR launched via `dotnet test`)
  against every corpus document. This is the **CI gate** — must be
  green for the PR to merge.
- [ ] **F1b.** Release validation runbook entry: PAC 2024 (or its
  successor) is **manually run** by the release engineer against the
  acceptance corpus before the v1.1 NuGet push. Failures gate the
  release. Track results in `docs/phases/phase-5-packaging-and-release.md`.
- [ ] **F2.** Capture-tag-tree snapshot tests so future refactors can't
  silently change the structure tree shape.
- [ ] **F3.** Add `corpus/accessibility/` subdirectory — real-world
  invoices / reports + their expected PDF/UA tagging shape.

### Phase G — non-PDF/UA accessibility features

(Lower priority; ship after Phase F.)

- [ ] **G1.** `aria-label` / `aria-labelledby` / `aria-describedby`
  → `/T` (title) / `/E` (expansion) / `/ActualText` on structure
  elements per §14.8.4.4.
- [ ] **G2.** `role="…"` overrides (= explicit author intent should
  override the default tag mapping; e.g., `<div role="heading"
  aria-level="2">` → H2).
- [ ] **G3.** WCAG 2.2 contrast-ratio diagnostic for `color` /
  `background-color` pairs below the 4.5:1 threshold (= surfacing
  authoring problems, not blocking emission).

## Out of scope

- **PDF/A-1 / PDF/A-2 / PDF/A-3 conformance.** Tracked separately on
  the post-v1 roadmap (`docs/phases/phase-5-packaging-and-release.md` —
  v1.2). PDF/A has THREE conformance levels per ISO 19005-2:2011
  §6.2: **b** (basic — no tagging required), **u** (Unicode mapping
  required, no tagging required), **a** (accessibility — tagged PDF
  required). So PDF/A and PDF/UA overlap only at the "a" level:
  - PDF/A-2u / PDF/A-3u (= the realistic v1.2 target) do **NOT**
    require tagging; they only require reliable Unicode mapping for
    text extraction. v1.1's tagging work is **NOT a prerequisite** —
    PDF/A-2u/3u could ship without PDF/UA.
  - PDF/A-2a / PDF/A-3a (accessibility level) DO require tagged PDF
    + structure tree. There v1.1's PDF/UA-1 surface IS a prerequisite,
    but the two specs have independent conformance gates (PDF/A
    additionally requires embedded fonts, no encryption, XMP
    metadata, no transparency groups in PDF/A-1, etc.).
- **AcroForm widgets + interactive forms.** Roadmap v2.0.
- **AT-specific testing (NVDA / JAWS / VoiceOver behavior).** Out of
  scope for v1.1 unless we get a community contributor; the spec-
  conformance gates (PAC, veraPDF) are the v1.1 acceptance criteria.

## Spec references

- ISO 32000-2:2020 PDF — §14.7 (Logical structure), §14.8 (Tagged PDF),
  §14.6 (Marked content).
- ISO 14289-1:2014 PDF/UA-1 — the conformance target for v1.1.
- WCAG 2.2 — Web Content Accessibility Guidelines (= the source of the
  contrast-ratio + heading-hierarchy rules referenced above).
- Matterhorn Protocol 1.1 — PDF/UA failure-condition checklist (= the
  PAC 2024 / veraPDF check shape).

## Cross-references

- [`CLAUDE.md`](../CLAUDE.md) "Common pitfalls a new session should
  know" — the *"Tagged PDF / PDF/UA / PDF/A are post-v1"* pitfall
  (= semantic IR built in v1; emission post-v1). NB: this is a
  pitfall callout, not one of the numbered cross-cutting rules
  (rule #7 is the diagnostics-not-silent-corruption rule).
- [`docs/phases/phase-0-architecture-lock.md`](phases/phase-0-architecture-lock.md)
  — phase-0 commitment that the IR is built early.
- [`docs/phases/phase-2-css-engine.md`](phases/phase-2-css-engine.md)
  §"NetPdf.Layout.Semantic" — the IR's home + Phase2Pipeline wire-up.
- [`docs/phases/phase-5-packaging-and-release.md`](phases/phase-5-packaging-and-release.md)
  "Hand-off after `1.0.0`" — v1.1 milestone owner.
- [`docs/pdf-spec-notes.md`](pdf-spec-notes.md) §14.8.2 — spec
  interpretation of "tagged required when?".
- [`docs/compatibility-matrix.md`](compatibility-matrix.md) — the
  per-feature row that says "📥 post-v1, semantic IR built".

## Updating this doc

When a v1.1 task lands, check off the corresponding box above + add a
one-liner to the next "Last verified" stamp below. If the work shape
diverges from the breakdown, update the section heading + leave a `~~`
strike-through on the original box so the history is visible.

## Last verified

Roadmap drafted at: 2026-05-28. No v1.1 work has started yet.
