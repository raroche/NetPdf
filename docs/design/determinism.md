# Determinism contract

NetPdf produces **byte-identical PDF output for byte-identical input**. This is a public guarantee, not a happy-path coincidence — it is enforced by a CI-gated test harness and is one of the differentiators vs. browser-print pipelines (Chromium / wkhtmltopdf / Playwright print) which interleave timestamps, randomized object IDs, and ambient state into their output.

## What "byte-identical input" means

Two builds count as having identical input when:

- The HTML source string is the same.
- The CSS source string is the same.
- The `HtmlPdfOptions` instance carries the same configuration (page size, margins, base URI, feature flags, etc.).
- The `IResourceLoader` returns the same bytes for the same URL when invoked at the same step in the build.
- The `IFontResolver` returns the same `FontFaceData` for the same query.
- Any explicit timestamps the caller sets (`CreationDate`, `ModDate`) are the same.

If those inputs match, the output bytes match — every byte, including the trailer `/ID`.

## What is *not* part of the input

NetPdf never reads from these on its own:

- `DateTime.Now` / `DateTime.UtcNow` — never read in shipped code; verified by grep in CI.
- `Random` / `Guid.NewGuid` — never used.
- The host filesystem at render time — only via the explicit `IResourceLoader` and `BaseUri` sandbox.
- The system clock — `CreationDate` / `ModDate` default to `null` (omitted). Set explicitly to enable.
- Network state — there is no implicit HTTP fetch.

This is what makes byte-determinism *possible*. Removing those sources of ambient state up front is cheaper than chasing them down per-feature.

## How determinism is enforced

### In the source tree

- **`PdfDictionary` is backed by `OrderedDictionary<TKey, TValue>`** — emission order matches insertion order, which is deterministic per the build code.
- **The image cache** (`PdfDocument._imageCache`) is a `Dictionary<string, PdfIndirectRef>` but is only consulted via `TryGetValue` and never enumerated for emission, so iteration order has no effect.
- **The font subsetter** (`GlyphSubsetPlan`, `ToUnicodeCMap`) accumulates intermediate state in `Dictionary` / `HashSet` but sorts before emission (`Array.Sort`, `List.Sort`).
- **`FlateDecode` compression level** is centralized at `PdfFormat.PdfDeflateCompressionLevel = CompressionLevel.SmallestSize` — pins the deflate level so all stream emitters produce byte-equal output for byte-equal input. Two builds that disagreed on the level (e.g., `Optimal` vs. `SmallestSize`) would each be valid PDF but byte-different, silently breaking signing / content-addressing / snapshot tests.
- **No `DateTime.Now` in shipped code.** Caller-supplied `DateTimeOffset` is the only date input; it is formatted via `CultureInfo.InvariantCulture`.

### In the test tree

- **`PdfDocumentDeterminismHarnessTests`** (`tests/NetPdf.UnitTests/Pdf/`) — 75-test harness:
  - **Property tests** over 18 document shapes (blank docs, metadata-rich docs, JPEG / opaque PNG / RGBA PNG / indexed PNG variants, transparent GIF, image dedup, multi-page mixed sizes, raw content-stream operators on both `AppendContent` overloads, explicit dates with UTC + positive half-hour offset + negative offset, long metadata with parens / backslashes, many-AppendContent stress).
  - **Structural sanity** on every shape: bytes start with `%PDF-`, contain `xref` and `startxref`, and end with `%%EOF`. Catches the "stable but corrupt" failure mode.
  - **Per-platform SHA-256 pinned snapshot** for every shape. Outer key is OS-family + CPU-architecture (`osx-arm64`, `linux-x64`, `win-x64`, …); inner key is the shape name. When the running platform has no pin in the map, the snapshot test logs "no pin, snapshot skipped" and the property tests still run.
  - **`/ID` extraction** test: parses the trailer `/ID` byte string from emitted bytes and asserts equality across two builds. Names the property explicitly (it is also covered indirectly by byte-equal-twice).
  - **Error-path determinism**: `RegisterImage(invalid)` and double-`Save()` must throw the same exception type and the same `Message` across runs. Diagnostic surfaces (logs, telemetry) stay byte-stable.

### Diagnostics on drift

When a byte-array assertion fails, the harness emits via `DeterminismDiagnostics.AssertByteEqualsWithDiagnostics`:

- `expected.Length`, `actual.Length`, first-differing offset.
- SHA-256 of each whole array.
- SHA-256 of each *first half* of the array — localizes drift to "before vs. after the midpoint" without manual byte hunting.
- Hex + ASCII windows (32 bytes either side of the first divergence) with a `|` marker at the diff point.

This converts a 3001-byte mismatch from "needles in two haystacks" into an instantly-reviewable diff.

## Re-pin protocol

A pinned hash drift is *expected* on:

- A change to the PDF emit logic (any of `NetPdf.Pdf.*`).
- A `.NET` major-version upgrade that shifts zlib output.
- A new `OS-arch` platform key that has no pin yet.

Re-pinning steps:

1. **Diagnose the cause.** Read the diagnostic message; narrow to a subsystem (header / xref / specific stream / trailer).
2. **Verify the new bytes are still well-formed.** Run a sample build through `qpdf --check` and PDFium open. If they fail, the change broke PDF correctness — fix that, do not re-pin around it.
3. **Capture the new hashes.** Open `PdfDocumentDeterminismHarnessTests.Capture_all_shape_hashes_for_pinning`, remove the `[Fact(Skip = ...)]` attribute argument so it runs as `[Fact]`, run with verbosity ≥ "normal", copy the printed `["<name>"] = "<HASH>",` lines.
4. **Paste the captured lines into `s_pinsByPlatform[<platform-key>]`.** Replace existing pins for that platform key, or add a new platform-key entry if you are adding cross-platform coverage.
5. **Re-add the `Skip` argument** to `Capture_all_shape_hashes_for_pinning` so it is dormant until the next re-pin.
6. **Run the harness.** All 75 tests should pass for the platform you re-pinned.
7. **Commit with a message that names the cause.** Future-you wants to know whether the drift was a real PDF change, a runtime bump, or a new platform.

Never blindly re-pin to silence a failing test. Drift is a signal — investigate first.

## Why we go to this trouble

Byte-determinism unlocks several uses that approximate-match outputs cannot:

- **Reproducible builds.** A document built on Roland's laptop and built again in CI produces the same SHA-256.
- **Content addressing.** A PDF's hash is its identity. Sign once, reuse across copies, audit later.
- **Snapshot testing.** Consumers can pin their own canonical PDFs in their tests and detect changes at byte resolution.
- **Differential analysis.** A 1-byte diff is meaningful; a fuzzy diff is not.
- **Audit + compliance.** Long-term archival pipelines need stable hashes for chain-of-custody attestations.

## Known gaps

- **Cross-platform pins** are currently captured for `osx-arm64` only. Phase 5 brings the containerized reference environment that will pin `linux-x64` and `linux-arm64` from a known Docker image; `win-x64` will follow.
- **Non-ASCII metadata** (Title / Author / Subject / Keywords / Creator with chars > U+007F) is rejected at the public-API boundary by `PdfLiteralString`. The byte writer supports `PdfHexString` (UTF-16BE) but the public facade does not yet route through it. Tracked in [`docs/compatibility-matrix.md`](../compatibility-matrix.md).
- **`FeatureFlags.DeterministicTimestamps`** is declared in `src/NetPdf/FeatureFlags.cs` but is not yet wired through the public `HtmlPdf.Convert(html, options)` path because that path itself ships in Phase 2. The internal `PdfDocument` already defaults `CreationDate` / `ModDate` to `null`, satisfying the flag's intent by default.

## Task 23 review record

This document and the harness it describes incorporate the following review recommendations (captured 2026-05-03):

| ID | Recommendation | Status |
|---|---|---|
| H1 | Platform-gate the pinned snapshot via OS+arch key map; skip with informative log on platforms without a pin | ✅ Implemented (`s_pinsByPlatform` keyed by `DeterminismDiagnostics.CurrentPlatformKey`) |
| H2 | Validate emitted bytes are well-formed PDF (header, xref, startxref, trailing %%EOF) | ✅ Implemented (`Document_shape_emits_well_formed_PDF_bytes` + `AssertWellFormedPdfShape`) |
| H3 | Pin `FlateDecode` compression level explicitly via shared constant | ✅ Implemented (`PdfFormat.PdfDeflateCompressionLevel`, replacing 3 `CompressionLevel.Optimal` sites) |
| M1 | Per-shape pinned hashes (not just one canonical doc) | ✅ Implemented (every shape has its own pin in the inner dictionary) |
| M2 | Diagnosable byte-mismatch helper (first-diff offset + per-half SHA + hex/ASCII windows) | ✅ Implemented (`DeterminismDiagnostics.AssertByteEqualsWithDiagnostics`) |
| M3 | Error-path determinism (same invalid input → same exception type + message) | ✅ Implemented (`RegisterImage_invalid_input_throws_identical_message_across_runs`, `Save_after_save_throws_identical_message_across_runs`) |
| M4 | Explicit `/ID` determinism test | ✅ Implemented (`Canonical_document_has_deterministic_trailer_ID`) |
| M5 | Non-UTC creation date shapes (positive half-hour + negative offset paths in `FormatPdfDate`) | ✅ Implemented (`explicit-creation-date-positive-half-hour-offset`, `explicit-creation-date-negative-offset`) |
| M6 | Edge-case shapes: long metadata with PDF-special chars, many-AppendContent stress | ✅ Implemented (`long-metadata-strings`, `many-append-content-calls`) |
| M7 | Document the hex-string metadata gap | ✅ Documented in `docs/compatibility-matrix.md` (PDF metadata strings section) and in this file's "Known gaps" |
| L1 | Fix misleading Skia disclaimer in harness XML | ✅ Rewritten — no longer claims property tests carry Skia paths they don't |
| L4 | Replace ⨁ in PdfDocument.cs comment with ASCII | ✅ Replaced with `\|\|` |
| A1 | Wire `FeatureFlags.DeterministicTimestamps` (Phase 2 follow-up; flag exists but no consumer until `HtmlPdf.Convert` lands) | 🧪 Documented in "Known gaps"; tracked for Phase 2 |
| A2 | Document the determinism contract publicly | ✅ This file + compatibility-matrix entry |
| A3 | Phase 5 containerized pin environment | 🧪 Tracked in Phase 5 work; this file's "Known gaps" notes the dependency |
| A4 | Property-based testing of determinism via FsCheck | ⏸️ Deferred to post-v1 — out of scope for v1 |
