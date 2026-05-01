# NetPdf — Progress Status

**Current phase:** Phase 1 — PDF writer + text foundation
**Tagged release:** `0.0.1-phase0` (Phase 0 complete)
**Target next tag:** `0.1.0-alpha` (Phase 1 complete)
**Last updated:** 2026-05-01 (Task 3 ✅ + 2 hardening rounds: trailer-graph + foreign-store + cycle detection + transient trailer + tightened /Root + Get scope + WriteAscii fail-fast + PdfStream /Length self-correction)

This file is the at-a-glance "where are we?" tracker. It is updated whenever a phase task ships. For execution detail per phase, see [`docs/phases/`](docs/phases/). For session bootstrap, see [`CLAUDE.md`](CLAUDE.md).

---

## Phase 0 — Legal & architecture lock ✅ Complete

- **Tagged:** `0.0.1-phase0`
- **Completed:** 2026-05-01
- **Time:** ~1 day calendar (Claude Opus 4.7 high)

### What was done
Apache-2.0-licensed scaffolding established. 9 src + 11 test + 3 sample projects organized in `NetPdf.slnx` with central package management. Public API surface frozen (`HtmlPdf`, `HtmlPdfOptions`, `PdfRenderResult`, all interfaces). Governance docs in place: clean-room policy, dependency dossier, compatibility matrix, diagnostic codes registry, PDF spec notes. Per-phase execution guides under `docs/phases/`. Project-scoped skills under `.claude/skills/`. Test corpus seeded with 4 invoice samples (classic CSS, Tailwind CDN, responsive Tailwind, Anvil running-elements). NuGet ID `NetPdf` reserved on nuget.org via `0.0.1-phase0` placeholder (unlisted). `NUGET_API_KEY` set up as GitHub Actions repository secret. `CLAUDE.md` session-bootstrap added.

### What's next
Phase 1 — PDF writer + text foundation. See [`docs/phases/phase-1-pdf-writer-and-text.md`](docs/phases/phase-1-pdf-writer-and-text.md).

### How to verify the Phase 0 baseline
```bash
dotnet build NetPdf.slnx -c Release         # 0 errors
dotnet test NetPdf.slnx -c Release          # 12/12 tests passing
dotnet run --project samples/invoice-cli/InvoiceCli.csproj -c Release -- \
  tests/NetPdf.RealDocuments/Corpus/Invoices/01-classic-pure-css.html /tmp/test.pdf
# Expected output: "NetPdf is in Phase 0 — conversion not yet implemented." with exit code 3.
```

---

## Phase 1 — PDF writer + text foundation ⏳ In progress

- **Target tag:** `0.1.0-alpha`
- **Started:** 2026-05-01
- **Time estimate:** 4–6 weeks calendar (Claude Opus 4.7 high)
- **Doc:** [`docs/phases/phase-1-pdf-writer-and-text.md`](docs/phases/phase-1-pdf-writer-and-text.md)

### Active task
**Task 4 — Content stream writer + minimal operator vocab** (mini-est. 3 days)

### Subtasks completed

- **Task 1 — PDF object model + `WriteTo`** ✅ (2026-05-01)
  - `PdfWriter` byte writer over `IBufferWriter<byte>` with cumulative position tracking, plus `Hex` helper.
  - `PdfObject` abstract base + 11 concrete types: `PdfBoolean`, `PdfNull`, `PdfInteger`, `PdfReal`, `PdfName`, `PdfLiteralString`, `PdfHexString`, `PdfArray`, `PdfDictionary`, `PdfStream`, `PdfIndirectRef`. All emit byte sequences conforming to ISO 32000-2:2020 §7.3.
  - `PdfNames` static class with ~85 pre-allocated standard names used throughout the rest of Phase 1+.
  - `PdfDictionary` uses .NET 10's `OrderedDictionary<TKey, TValue>` so insertion order is preserved → byte-deterministic output.
  - 85 unit tests covering primitives, name escaping, literal-string octal escapes, hex-string UTF-16BE-with-BOM, array/dictionary nesting, stream `/Length` auto-set, determinism property test.

- **Task 2 — Indirect-object store + xref table writer** ✅ (2026-05-01)
  - `IndirectObjectStore` — 1-based deterministic numbering, `Allocate`/`Add`/`Assign` for forward references, byte-offset recording, `ValidateAllAssigned` gate before emit.
  - `PdfDocumentWriter` — orchestrates header → indirect objects → xref → trailer → `startxref` → `%%EOF`. Header emits binary marker (4 bytes ≥ 0x80) per §7.5.2. xref entries are **exactly 20 bytes** including the 2-byte ` LF` terminator per §7.5.4. Trailer `/Size` auto-managed.
  - `Version` is a `string` property; facade translates the public `PdfVersion` enum at the API boundary.
  - 30 unit tests covering header/binary marker, all 5 PDF version strings, xref entry byte-exactness, xref offset correctness, `/Size`, `startxref`, EOF marker, determinism, minimal-valid-PDF end-to-end.

- **Hardening pass** ✅ (2026-05-01) — review-driven correctness reinforcement before Task 3.
  - `PdfFormat` constants (xref widths, byte-offset limit, generation max, binary-marker bytes, supported-versions allow-list) — connects emitted bytes to ISO 32000-2 sections explicitly.
  - `PdfObject.EnumerateChildren()` virtual + container overrides — lets graph operations walk the tree without subtype switches.
  - `PdfPreflightValidator` — single structural pass before write: version on allow-list, all refs assigned, `/Root` present + indirect + allocated + `/Catalog`-typed, every reachable indirect ref allocated (no dangling), every ref generation = 0.
  - `PdfIndirectRef.StoreId` — refs allocated by a store carry that store's id; `Assign` rejects synthetic and cross-store refs. Catches the silent-corruption risk of cross-document binding.
  - xref byte-offset limit enforced (`> 9_999_999_999` throws — files larger require xref streams).
  - Dropped unused `NetPdf.Paint` / `NetPdf.Text` / `SkiaSharp` refs from `NetPdf.Pdf.csproj`.
  - 25 new tests; total 141 unit / 152 solution-wide.

- **Task 3 — Trailer `/ID` from content SHA-256** ✅ (2026-05-01)
  - `PdfWriter` gains an optional `IncrementalHash` tee — every emitted byte is fed into the hash sink without buffering. `StopHashing()` detaches before the trailer (the trailer would otherwise self-reference).
  - `PdfDocumentWriter.WriteTo` auto-derives `/ID` when not user-set: SHA-256 of header + indirect objects + xref, first 16 bytes (the conventional 128-bit ID size per §14.4), encoded as `PdfHexString`. The `/ID` array contains the 16-byte digest twice (original-doc id + current-revision id, equal for fresh files).
  - User-provided `/ID` is preserved (writer skips auto-derivation when `Trailer.ContainsKey(/ID)` — pays no hash cost in that path).
  - 7 new tests including a strong correctness assertion that re-derives the hash externally (`SHA256.HashData(bytes[..trailerOffset])`) and compares against the emitted `/ID`. Determinism preserved end-to-end (identical input → identical hash → identical `/ID` → byte-equal output).

- **Post-Task-3 hardening pass** ✅ (2026-05-01) — second review-driven hardening round.
  - **Trailer-graph traversal**: preflight now walks the entire trailer dictionary, not just `/Root`. Catches dangling/foreign refs in `/Info`, `/Encrypt`, future trailer entries.
  - **Foreign-store ref rejection in preflight**: previously only `Assign()` rejected cross-store refs; refs embedded inside arrays/dictionaries could pass with in-range numbers. Validator now rejects `StoreId != 0 && StoreId != store.Id` everywhere.
  - **Path-tracking cycle detection**: switched from "visit-once-and-skip" to "ancestor-set-and-throw." Indirect cycles (A→B→A in the direct object graph) are now rejected at preflight instead of silently passing then stack-overflowing in `PdfDictionary.WriteTo`. Sibling-shared direct objects still allowed.
  - **Direct-cycle guard at `Add`/`Set`**: `arr.Add(arr)` and `dict.Set(_, dict)` throw immediately at insertion — eager rejection of the most common case.
  - **Explicit `/ID` shape validation**: when a user provides `/ID`, preflight requires array of exactly 2 byte strings (rejects `PdfInteger`, single-element arrays, indirect-ref entries).
  - **Transient trailer emit**: `WriteTo` no longer mutates the user's `Trailer` dictionary. `/Size` and auto-derived `/ID` live only in a per-write transient dict. Fixes the stale-`/ID`-on-reuse bug — mutating the body and re-writing now correctly re-derives the `/ID`. Also makes `WriteTo` exception-safe (no leaked state on partial failure).
  - **`PdfFormat.SupportedVersions`**: ordered string array for deterministic diagnostic messages, plus a separate `IReadOnlySet` for O(1) lookup.
  - **Support project reclassification**: `NetPdf.Benchmarks`, `NetPdf.Fuzz`, `NetPdf.AotSmoke`, `NetPdf.TestKit` no longer have `<IsTestProject>true</IsTestProject>`. They're not unit-test projects and shouldn't be invoked by `dotnet test`. Each now sets the AOT/pack/docs flags directly. `dotnet test NetPdf.slnx` now runs cleanly with no spurious "exited with error" messages.
  - 21 new tests covering all the above. Total tests: **169 unit / 180 solution-wide passing** (cleanly — no "exited with error" noise from support projects).

- **Round-2 hardening pass** ✅ (2026-05-01) — third review-driven round.
  - **Tightened `/Root` validation**: `/Root` must resolve to a `PdfDictionary` with `/Type /Catalog`. The previous lenient rule only failed when `/Type` was set and explicitly non-`/Catalog`; non-dictionary targets, dictionaries without `/Type`, and dictionaries with malformed `/Type` all silently passed. Now all four cases are rejected with descriptive messages.
  - **Tightened `IndirectObjectStore.Get`**: symmetric with `Assign` — rejects refs whose `StoreId` is non-zero and doesn't match this store. Synthetic refs (`StoreId == 0`) and local refs still resolve. Closes the silent-retargeting risk where a foreign ref would resolve to an unrelated local object that happened to have the same number.
  - **`PdfWriter.WriteAscii` fail-fast**: throws `ArgumentException` for any character > 0x7F. Previously truncated to the low byte silently. The XML doc warning was insufficient defense at this critical layer.
  - **`PdfStream` `/Length` self-correction**: `WriteTo` re-sets `/Length` from the payload byte count regardless of any post-construction mutations. Catches the case where `/Filter` is swapped or the dictionary is cleared, leaving a stale length.
  - 6 existing tests using `/Root → ref-to-PdfInteger` migrated to use a properly-seeded catalog (they were testing emit format with structurally-invalid input).
  - 15 new tests: 4 for `/Root` validity (PdfInteger target, dict-without-Type, dict-with-non-name-Type, PdfArray target); 4 for `Get` scope (foreign rejected, synthetic resolves locally, local resolves, unallocated returns null); 4 for `WriteAscii` fail-fast (non-ASCII string throws, Unicode char throws, full ASCII range accepted, `Write(span)` still handles arbitrary bytes); 3 for `PdfStream` self-correction (overwritten `/Length` reset, removed `/Length` restored, idempotent on correct value).
  - Total tests: **184 unit / 195 solution-wide passing**.

### What's next when Phase 1 completes
Phase 2 — CSS engine + DOM pipeline. See [`docs/phases/phase-2-css-engine.md`](docs/phases/phase-2-css-engine.md).

### How to test current Phase 1 progress
```bash
# Build the full solution (zero errors, zero warnings expected).
dotnet build NetPdf.slnx -c Release

# Run all xUnit tests across the solution. With the post-Task-3 hardening, the
# four support projects (Benchmarks, Fuzz, AotSmoke, TestKit) are no longer flagged
# as unit-test projects, so this runs cleanly with no "exited with error" noise.
dotnet test NetPdf.slnx -c Release --nologo

# Or just the PDF byte writer tests for a fast inner loop:
dotnet test tests/NetPdf.UnitTests/NetPdf.UnitTests.csproj -c Release \
  --filter "FullyQualifiedName~Pdf"
```

---

## Phase 2 — CSS engine + DOM pipeline ⏸️ Not started

- **Target tag:** `0.3.0-alpha`
- **Time estimate:** 2–3 weeks
- **Doc:** [`docs/phases/phase-2-css-engine.md`](docs/phases/phase-2-css-engine.md)

## Phase 3 — Fragmentainer-aware layout + pagination ⏸️ Not started

- **Target tag:** `0.7.0-beta` (the bottleneck phase)
- **Time estimate:** 8–12 weeks
- **Doc:** [`docs/phases/phase-3-layout-and-pagination.md`](docs/phases/phase-3-layout-and-pagination.md)

## Phase 4 — Visual parity hardening ⏸️ Not started

- **Target tag:** `0.9.0-rc1`
- **Time estimate:** 3–5 weeks
- **Doc:** [`docs/phases/phase-4-visual-parity.md`](docs/phases/phase-4-visual-parity.md)

## Phase 5 — Packaging, hardening, release ⏸️ Not started

- **Target tag:** `1.0.0`
- **Time estimate:** 1–2 weeks
- **Doc:** [`docs/phases/phase-5-packaging-and-release.md`](docs/phases/phase-5-packaging-and-release.md)

---

## Update protocol

When a task or phase ships:
1. Move the task from "Active" to "Subtasks completed" with a 1–2 sentence note.
2. When the phase ships: replace the "⏳ In progress" entry with a "✅ Complete" entry following the same format as Phase 0 above (what was done, what's next, how to verify).
3. Bump the "Active task" pointer for the next phase.
4. Update the "Last updated" date at the top.
5. Commit with message `docs: PROGRESS.md — <phase/task description>`.
