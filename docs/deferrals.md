# Known Deferrals

A focused, machine-friendly list of features and spec corners that NetPdf either
**approximates** or **rejects loudly** today, with the conditions under which
each gets picked up. The Phase docs (`docs/phases/`) cover the planned **work
breakdown**; this file covers what's **deliberately on hold** so the next agent
doesn't re-litigate prior decisions.

**Conventions:**

- **Status: approximated** — current behavior is a documented stand-in that
  works for the v1 invoice/report corpus. Throws loudly if the approximation
  could silently produce wrong output (e.g., mixed-mode `KeepAll`).
- **Status: throws** — `NotSupportedException` at the appropriate seam to fail
  loud + early. Never silent.
- **Pickup trigger** — the condition that would move the item off the deferral
  list. Usually "corpus needs it", "user-reported failing case", or "blocked
  by downstream task".

This file is updated when (a) something is **added** to the deferral list as
part of a shipped cycle, or (b) something is **picked up** and shipped. Update
in the same commit as the code change.

## Phase 3 Task 10 — Inline layout edge cases

### `word-break: keep-all` — CJK inter-character break suppression
- **Status:** approximated.
  - Uniform `keep-all` silently behaves like `normal` (no breaks suppressed).
  - Mixed-mode (`keep-all` ↔ {`normal`, `break-all`}) **throws**
    `NotSupportedException` from `InlineLayouter.LayoutPerRun` so the
    no-op approximation can't silently lose CJK content's intent.
- **What's missing:** UAX #24 script detection so we can identify CJK code
  points + UAX #14 LB30b's "do not break between two ID class characters
  when `word-break: keep-all`" rule.
- **Pickup trigger:** corpus adds CJK content (Chinese/Japanese/Korean
  invoices, reports, or books).
- **Files when picked up:** `NetPdf.Text/Bidi/UnicodeScripts.cs` (new — needs
  the script table); `NetPdf.Layout/Inline/LineBuilder.cs` flat-build phase
  (read script per glyph + suppress LB30b boundaries when both sides ID +
  per-run `WordBreak == KeepAll`); remove the `KeepAll` mismatch throw in
  `InlineLayouter.LayoutPerRun`.
- **Shipped in:** Phase 3 Task 10 cycle 3d sub-cycle 3 (added throw).

### `white-space: break-spaces` — distinctive wrap semantics
- **Status:** approximated.
  - Both the preprocessor and the wrap pass treat `break-spaces`
    **identically to `pre-wrap`** (preserve all whitespace + wrap at UAX #14
    Allowed opportunities).
  - Documented in `WhiteSpace.cs` XML.
- **What's missing:** per CSS Text L3 §6.4, `break-spaces` must add forced
  wrap candidates at **every** preserved space glyph (not just UAX #14
  Allowed positions) AND trailing-space wrap-vs-hang behavior at line ends.
- **Pickup trigger:** corpus needs `break-spaces` semantics (rare; mainly
  legal/typographic content with explicit trailing-space wrapping).
- **Files when picked up:** `NetPdf.Layout/Inline/LineBuilder.cs` flat-build
  phase (synthesize Allowed at every SP glyph in `break-spaces` source runs)
  + wrap loop's trailing-trim path (hang trailing SPs past the line edge
  instead of trimming them).

### UAX #24 script detection + RTL fragment-level reversal
- **Status:** approximated.
  - Shaping passes ISO 15924 + BCP 47 uniformly to the whole inline pass
    (caller-supplied).
  - RTL glyphs come out of HarfBuzz in visual order; `LineFragment[]`'s
    slices stay in document order. Painters can already walk LTR runs
    straight + RTL runs as-is per-shaped-run.
- **What's missing:**
  - UAX #24 per-codepoint script detection → script-change boundary in
    `LineBuilder.Itemize` so multi-script paragraphs shape each script with
    its proper OpenType feature set.
  - Fragment-level slice order reversal for RTL paragraphs so the painter
    walks slices visually right-to-left.
- **Pickup trigger:** mixed-script content (Latin + Arabic in one
  paragraph) or right-to-left primary direction enters the corpus.
- **Files when picked up:** `NetPdf.Layout/Inline/LineBuilder.cs` Itemize
  pass; `LineFragment.Slices` ordering at emit time.

## Phase 4 painter — `BoxFragment[]` + `LineFragment[]` → PDF content streams

- **Status:** not started (Phase 4 work).
- **What's there:** `LineFragment` + `ShapedRunSlice` carry glyph + advance +
  per-line metadata (`EndsWithMandatoryBreak`, `EndsWithHyphenationBreak`).
  `BoxFragment` carries per-box border-box geometry.
- **What's missing:** the painter that turns those records into PDF
  content-stream operators (`Tf` set font, `Tj` show text, advances + line
  matrices + hyphen-on-break rendering for `EndsWithHyphenationBreak=true`
  lines).
- **Pickup trigger:** Phase 4 start. Sequenced after Phase 3 completes its
  task list (see `docs/phases/phase-3-layout-and-pagination.md`).
- **Files when picked up:** `NetPdf.Paint/` (new) consuming the fragment
  records emitted by `IBlockFragmentSink`.

## Phase 3 remaining work — Tasks 11–30

These are not "deferrals" in the same sense as the items above — they're
**scheduled** work tracked in
[docs/phases/phase-3-layout-and-pagination.md](phases/phase-3-layout-and-pagination.md).
Listed here only for the agent landing on this file to know where to look:

| # | Task | Status |
|---|---|---|
| 11 | Block + inline integration; widows/orphans | **In progress — Task 11 sub-cycle 1.** |
| 12 | `TableLayouter` auto + fixed + collapse + span | not started |
| 13 | `<thead>`/`<tfoot>` repetition across pages | not started |
| 14 | `MulticolLayouter` | not started |
| 15 | `FlexLayouter` L1 (single-line) | not started |
| 16 | `FlexLayouter` multi-line + cross-fragment baseline | not started |
| 17 | `GridLayouter` track-sizing | not started |
| 18 | `GridLayouter` placement + dense packing + areas | not started |
| 19 | `AbsoluteLayouter` for `position: absolute` | not started |
| 20 | `position: fixed` repetition per page | not started |
| 21 | `@page` rule + 16 page-margin boxes | not started |
| 22 | `string()` + `string-set` | not started |
| 23 | `position: running()` + `content: element()` | not started |
| 24 | `counter(page)` / `counter(pages)` | not started |
| 25 | Diagnostics emission for pagination edge cases | not started |
| 26 | W3C CSS test runner integration | not started |
| 27 | Pagination-golden snapshot infrastructure | not started |
| 28 | Render the 4 invoice corpus files end-to-end | not started |
| 29 | Performance tuning to hit 1-page invoice ≤ 200 ms p50 | not started |
| 30 | Tag `0.7.0-beta` + CHANGELOG | not started |

## Post-Phase-3 (pre-v1.0)

### Coverage-guided fuzzing infrastructure
- **Status:** deferred from Phase D (PR #18) to a dedicated post-Phase-3
  milestone. SharpFuzz + AFL++ harnesses for `HtmlParsingHost`,
  `CssPreprocessor` / `CssParserAdapter`, `SelectorCompiler`,
  `CalcResolver`, `VarSubstitution`, `ImageSafetyValidator`,
  `FontSafetyValidator`, and `PdfPreflightValidator`.
- **Estimated effort:** 4–6 weeks dedicated.
- **Pickup trigger:** Phase 3 complete (so the fuzz surface is stable).
  Gates: 30-day no-new-crashes window before the v1.0 tag.
- **Tracked at:** `PROGRESS.md` § Pending pre-v1.0 work.
