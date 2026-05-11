# Known Deferrals

Approximation contracts: features the codebase **approximates** or **rejects
loudly** today, with the conditions under which each gets picked up.

This file is **not** a status tracker for in-progress work — that lives in
[PROGRESS.md](../PROGRESS.md) (active state) and
[docs/phases/](phases/) (planned breakdown). It is also not a duplicate of
those files. Update this file when (a) you ship a cycle that adds a new
approximation/throw, or (b) you pick up a deferral and replace the
approximation with full behavior.

## Schema

Every deferral entry follows the same labels so the file is grep-able + a
parity test in
[`tests/NetPdf.UnitTests/Docs/DeferralsParityTests.cs`](../tests/NetPdf.UnitTests/Docs/DeferralsParityTests.cs)
can verify entries don't drift away from source. Required labels:

- **ID** — stable kebab-case identifier. Once published, do not rename;
  removing the entry requires removing the matching parity-test assertion in
  the same commit.
- **Status** — one of `approximated`, `throws`, `not-started`.
- **Behavior** — one line describing what the code does today.
- **Missing** — what's not implemented relative to the spec.
- **Trigger** — the condition that moves this off the deferral list.
- **Owner files** — files (and rough locations) that change when picked up.
- **Added** — PR or cycle that documented the deferral.
- **Removal condition** — explicit criterion for deleting the entry.

When you add a deferral, also add its ID to the expected list in
`DeferralsParityTests.cs` (and reference this doc in the throw-message /
approximation comment in source so a future agent can find the entry by
grepping the ID).

---

## word-break-keep-all-cjk

- **ID** — `word-break-keep-all-cjk`
- **Status** — `approximated` (uniform), `throws` (mismatch).
- **Behavior** — Uniform `word-break: keep-all` silently behaves like
  `normal` (no break suppression). A `keep-all` ↔ {`normal`, `break-all`}
  mismatch across source TextRuns throws
  `NotSupportedException` from `InlineLayouter.LayoutPerRun`.
- **Missing** — UAX #24 per-codepoint script detection + UAX #14 LB30b
  ("do not break between two ID-class characters when `word-break:
  keep-all`") to suppress CJK inter-character breaks.
- **Trigger** — corpus adds CJK content (Chinese / Japanese / Korean
  invoices, reports, books), OR a user-reported failing case.
- **Owner files** — `src/NetPdf.Text/Bidi/UnicodeScripts.cs` (new — needs
  the script table); `src/NetPdf.Layout/Inline/LineBuilder.cs` flat-build
  phase (read script per glyph + suppress LB30b boundaries); remove the
  `KeepAll` mismatch throw in
  `src/NetPdf.Layout/Inline/InlineLayouter.cs::LayoutPerRun`.
- **Added** — Phase 3 Task 10 cycle 3d sub-cycle 3 (added the mismatch
  throw); cycle 3b sub-cycle 2 (added the uniform approximation).
- **Removal condition** — UAX #24 script detection lands AND the wrap
  loop honors KeepAll per glyph AND
  `InlineLayouter.LayoutPerRun` no longer throws on KeepAll mismatch.

---

## white-space-break-spaces

- **ID** — `white-space-break-spaces`
- **Status** — `approximated`.
- **Behavior** — Both the preprocessor and the wrap pass treat
  `white-space: break-spaces` identically to `pre-wrap` (preserve all
  whitespace + wrap at UAX #14 Allowed opportunities only).
- **Missing** — Per CSS Text L3 §6.4, `break-spaces` must add forced wrap
  candidates at **every** preserved space glyph (not just UAX #14 Allowed
  positions) AND honor trailing-space wrap-vs-hang at line ends.
- **Trigger** — corpus needs `break-spaces` semantics (rare; mainly
  legal/typographic content with explicit trailing-space wrapping).
- **Owner files** —
  `src/NetPdf.Layout/Inline/LineBuilder.cs` flat-build phase (synthesize
  Allowed at every SP glyph in `break-spaces` source runs) + wrap loop's
  trailing-trim path (hang trailing SPs past the line edge instead of
  trimming).
- **Added** — Phase 3 Task 10 cycle 3 review (User #3) added the enum
  value with the documented PreWrap-equivalent approximation.
- **Removal condition** — wrap pass synthesizes per-glyph forced
  candidates inside `break-spaces` runs AND trailing-space hang/wrap
  semantics are implemented.

---

## hyphens-auto-language-routing

- **ID** — `hyphens-auto-language-routing`
- **Status** — `approximated`.
- **Behavior** — `Hyphens.Auto` always applies en-US Liang patterns
  regardless of the source run's language. Word tokenization in
  `ApplyLiangPatterns` accepts only ASCII letters [A-Za-z] (+ U+00AD soft
  hyphens per cycle 3d sub-cycle 1 Rec #6) — apostrophes split contractions
  ("don't" → "don" / "t"), and non-ASCII letter sequences (e.g., German
  umlaut, Cyrillic, accented Latin Extended) are skipped entirely.
  CSS Text L4 `hyphenate-character`, `hyphenate-limit-chars`,
  `hyphenate-limit-lines`, `hyphenate-limit-zone` are also not implemented.
- **Missing** —
  - Per-language Liang pattern routing (de-DE, fr-FR, es-ES, etc.) keyed
    off each source TextRun's BCP 47 language tag.
  - UAX #29 word-segmentation so apostrophes inside contractions don't
    truncate words + non-ASCII letter sequences participate.
  - CSS Text L4 `hyphenate-character`, `hyphenate-limit-chars`,
    `hyphenate-limit-lines`, `hyphenate-limit-zone` properties.
- **Trigger** — corpus adds non-English text needing auto-hyphenation, OR
  a user-reported case where contractions / accented letters mis-wrap.
- **Owner files** —
  - `src/NetPdf.Text/Hyphenation/EnUsHyphenation.cs` siblings — e.g.,
    `DeDeHyphenation.cs`, `FrFrHyphenation.cs`, etc., loading their
    Liang TeX pattern resources.
  - New `NetPdf.Text.Hyphenation.LanguagePackRegistry` (BCP 47 lookup →
    Hyphenator).
  - `src/NetPdf.Layout/Inline/LineBuilder.cs::ApplyLiangPatterns` —
    replace ASCII-letter check with UAX #29 word boundaries + per-language
    hyphenator selection.
  - `src/NetPdf.Layout/Inline/Hyphens.cs` XML doc — drop the "deferred"
    framing once shipped.
- **Added** — Phase 3 Task 9 cycle 3b sub-cycle 3 (en-US-only Liang +
  ASCII tokenization); cycle 3d sub-cycle 1 Rec #6 (soft-hyphen
  suppression); cycle 3d sub-cycle 4 review Finding #1 (per-position
  Liang gating, which depends on this entry's owner files for the
  tokenizer when extended).
- **Removal condition** — at least one non-English language pack ships,
  the tokenizer uses UAX #29, AND one CSS Text L4 hyphenate-* property
  is implemented.

---

## uax-24-script-detection

- **ID** — `uax-24-script-detection`
- **Status** — `approximated`.
- **Behavior** — `LineBuilder.Itemize` accepts a single ISO 15924 script
  tag + BCP 47 language passed uniformly from the caller. Multi-script
  paragraphs (Latin + Arabic + Han in one `<p>`) all shape with the same
  feature set.
- **Missing** — Per-codepoint script detection (UAX #24) producing a
  script-change boundary in `Itemize`, so each script-homogeneous
  sub-run gets its own shaping call with the appropriate OpenType
  feature set.
- **Trigger** — mixed-script content enters the corpus, OR a user-reported
  failing case where script-specific shaping features (e.g., Arabic
  joining contextual forms across a Latin↔Arabic boundary) misrender.
- **Owner files** — `src/NetPdf.Text/Bidi/UnicodeScripts.cs` (new — see
  the `word-break-keep-all-cjk` entry; same table);
  `src/NetPdf.Layout/Inline/LineBuilder.cs::Itemize` (insert
  script-change boundaries).
- **Added** — Phase 3 Task 10 cycle 1 documented the deferral.
- **Removal condition** — `Itemize` produces script-typed itemized runs
  and `Shape` consumes them with the script-specific HarfBuzz feature
  set.

---

## rtl-fragment-reversal

- **ID** — `rtl-fragment-reversal`
- **Status** — `approximated`.
- **Behavior** — HarfBuzz shapes RTL runs in visual (reversed) order
  internally; `LineFragment.Slices` stays in document order. Painters
  walk per-shaped-run glyph arrays as-is. Single-direction LTR
  paragraphs paint correctly. Single-direction RTL paragraphs walk in
  the wrong visual order at the fragment level.
- **Missing** — Fragment-level slice reversal for RTL paragraph base
  direction so the painter consumes slices visually right-to-left.
- **Trigger** — RTL primary direction (Arabic / Hebrew) enters the
  corpus, OR a user reports right-to-left mis-rendering.
- **Owner files** — `src/NetPdf.Layout/Inline/LineBuilder.cs::Wrap`
  emission site (`EmitDrawableRange`) — reverse slice order when the
  paragraph base direction is `ParagraphDirection.RightToLeft`.
- **Added** — Phase 3 Task 10 cycle 1 documented the deferral.
- **Removal condition** — Wrap reverses slice order for RTL paragraphs
  AND the painter consumes them visually.

---

## phase-4-painter-wiring

- **ID** — `phase-4-painter-wiring`
- **Status** — `not-started` (the wiring; the IR project itself exists).
- **Behavior** — `BlockLayouter` emits `BoxFragment` records (with
  `LineFragment[]` carried on `BoxFragment.InlineLines` for inline-only
  blocks per Task 11 sub-cycle 1). `src/NetPdf.Paint/` already exists with
  the **display-list IR** types: `DisplayList`, `DisplayCommand`
  (`TextRunPayload`, `RectFillPayload`, `ImageDrawPayload`,
  `TransformPushPayload`, `OpacityPushPayload`), `TextRun`,
  `RasterImage`, `RgbaColor`, `ImageEncoding`. What's missing is the
  Phase 3 → Phase 4 **bridge** that consumes layouter output + emits
  display commands.
- **Missing** — A bridge service (e.g.,
  `NetPdf.Paint.LayoutFragmentEmitter` — final name TBD) that:
  - Consumes `IReadOnlyList<BoxFragment>` (the layouter's sink output).
  - For each fragment, emits the appropriate
    `RectFillPayload` / border / clip `DisplayCommand`s.
  - For fragments with non-null `InlineLines`, walks each
    `LineFragment.Slices` + the originating `ShapedRun` data + emits
    `TextRunPayload`-style `DisplayCommand`s positioned at successive
    baselines (line-height × index from the fragment's
    `BlockOffset`).
  - Renders the visible-hyphen glyph at line ends where
    `LineFragment.EndsWithHyphenationBreak == true`.
- **Trigger** — Phase 4 start, sequenced after Phase 3 task list
  completes — see
  [docs/phases/phase-4-visual-parity.md](phases/phase-4-visual-parity.md).
- **Owner files** —
  - `src/NetPdf.Paint/` (existing) — add the bridge type next to
    `DisplayList` + `DisplayCommand`; no new project needed.
  - `src/NetPdf.Pdf/` (existing) — `DisplayList` → PDF content-stream
    operator emission already lives here; the inline-text path needs
    per-line baseline + glyph-position math wired through.
- **Added** — Phase 3 Task 11 sub-cycle 1 documented the wiring gap
  (the layouter side of the contract shipped, painter side awaits).
- **Removal condition** — The bridge service ships + a corpus invoice
  renders text via the layouter → painter → PDF path end-to-end.

---

## fuzzing-infrastructure

- **ID** — `fuzzing-infrastructure`
- **Status** — `not-started`.
- **Behavior** — Phase A-D's regression corpus + threat-model coverage
  (215+ security tests) is the primary defense surface. No
  coverage-guided fuzzing is wired.
- **Missing** — SharpFuzz + AFL++ harnesses for `HtmlParsingHost`,
  `CssPreprocessor` / `CssParserAdapter`, `SelectorCompiler`,
  `CalcResolver`, `VarSubstitution`, `ImageSafetyValidator`,
  `FontSafetyValidator`, and `PdfPreflightValidator`. CI gate, crash
  triage runbook, 30-day no-new-crashes window before the v1.0 tag.
- **Trigger** — Phase 3 layout work complete (so the fuzz surface is
  stable + the engine is feature-complete enough to be worth fuzzing
  at the planned depth).
- **Owner files** — `tests/NetPdf.Fuzz/` (existing project; needs the
  8 harnesses); CI workflow under `.github/workflows/` for the gate.
- **Added** — Phase D PR #18 documented the deferral; carried into
  `PROGRESS.md` § Pending pre-v1.0 work.
- **Removal condition** — All 8 harnesses ship, CI gate green, 30-day
  no-new-crashes window observed.

---

## In-progress work — pointer

The scheduled Phase 3 work breakdown (Tasks 11–30 — `TableLayouter`,
`MulticolLayouter`, `FlexLayouter`, `GridLayouter`, `AbsoluteLayouter`,
page-margin boxes, `string()` / `running()` / `counter()`, diagnostics,
W3C runner, corpus end-to-end render, performance tuning, `0.7.0-beta`
tag) lives in
[`docs/phases/phase-3-layout-and-pagination.md`](phases/phase-3-layout-and-pagination.md).
Active state for whichever sub-cycle is in flight lives in
[`PROGRESS.md`](../PROGRESS.md). This file deliberately does **not**
duplicate either — they own the live status; this file owns the
approximation/throw contracts.
