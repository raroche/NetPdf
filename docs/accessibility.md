# Accessibility & tagged PDF — roadmap (TODO)

NetPdf's v1.0 output is **NOT** a tagged PDF. Tagged-PDF emission (= PDF/UA-1
conformance) ships in **v1.1**. This doc consolidates the work surface so the
v1.1 cycle has a single checklist to follow instead of re-deriving the
scope from scattered mentions.

## Current state (v1.0)

What's already in place (= the prep work CLAUDE.md rule #7 calls out):

- **`NetPdf.Layout.Semantic`** — the semantic IR is **built** during BoxBuilder
  + layout (per Phase 2 §"NetPdf.Layout.Semantic — semantic IR (built but not
  emitted)"). Reference: `src/NetPdf.Layout/Semantic/SemanticKind.cs`,
  `src/NetPdf.Layout/Semantic/SemanticTreeBuilder.cs`.
- **`IContentStream.BeginMarkedContent` / `EndMarkedContent`** — the PDF
  writer surface for marked-content sequences (= the BDC/EMC operators
  per ISO 32000-2 §14.6) is already implemented. Reference:
  `src/NetPdf.Pdf/Content/IContentStream.cs`,
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
- [ ] **B3.** Decorative content (= the existing "doesn't enter the
  tagged structure tree" branch in `SemanticTreeBuilder.cs`) emits
  `/Artifact` BDC/EMC pairs per §14.8.2.2.2.

### Phase C — marked-content sequences

- [ ] **C1.** Painter wraps every text run with `BDC /Span <</MCID N>>` …
  `EMC`. The MCID is the index in the parent structure element's `/K`
  array.
- [ ] **C2.** Painter wraps every image with `BDC /Figure <</MCID N>>` …
  `EMC` + Figure structure element + `/Alt` value sourced from the
  HTML `alt` attribute (or an empty `/Alt ()` for decorative images).
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

- [ ] **F1.** Add a `NetPdf.PdfUaConformance` test project. Cases:
  PDF/UA-1 PAC 2024 pass; veraPDF PDF/UA-1 validator pass on each
  corpus document.
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
  v1.2). PDF/A requires tagged PDF (PDF/A-2u and later levels) so v1.1's
  tagging work is a prerequisite, but PDF/A has its own conformance
  surface (= embedded fonts only, no encryption, XMP requirements).
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

- [`CLAUDE.md`](../CLAUDE.md) cross-cutting rule #7 — semantic IR built
  in v1; emission post-v1.
- [`docs/phases/phase-0-architecture-lock.md`](phases/phase-0-architecture-lock.md)
  — phase-0 commitment that the IR is built early.
- [`docs/phases/phase-2-css-engine.md`](phases/phase-2-css-engine.md)
  §"NetPdf.Layout.Semantic" — the IR's home.
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
