# NetPdf — Progress Status

**Current phase:** Phase 1 — PDF writer + text foundation
**Tagged release:** `0.0.1-phase0` (Phase 0 complete)
**Target next tag:** `0.1.0-alpha` (Phase 1 complete)
**Last updated:** 2026-05-01 (Task 1 ✅)

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
**Task 2 — Indirect-object store + xref table writer** (mini-est. 2 days)

### Subtasks completed

- **Task 1 — PDF object model + `WriteTo`** ✅ (2026-05-01)
  - `PdfWriter` byte writer over `IBufferWriter<byte>` with cumulative position tracking, plus `Hex` helper.
  - `PdfObject` abstract base + 11 concrete types: `PdfBoolean`, `PdfNull`, `PdfInteger`, `PdfReal`, `PdfName`, `PdfLiteralString`, `PdfHexString`, `PdfArray`, `PdfDictionary`, `PdfStream`, `PdfIndirectRef`. All emit byte sequences conforming to ISO 32000-2:2020 §7.3.
  - `PdfNames` static class with ~85 pre-allocated standard names (`/Type`, `/Pages`, `/Catalog`, font/image/metadata/annotation/structure names) used throughout the rest of Phase 1+.
  - `PdfDictionary` uses .NET 10's `OrderedDictionary<TKey, TValue>` so insertion order is preserved → byte-deterministic output.
  - 85 unit tests (`tests/NetPdf.UnitTests/Pdf/Objects/`) covering primitives, name escaping (delimiters + `#` introducer), literal-string octal escapes, hex-string UTF-16BE-with-BOM helper, array/dictionary nesting, stream `/Length` auto-set, and a determinism property test (same input → same bytes).
  - Total tests: 97/97 passing across the solution.

### What's next when Phase 1 completes
Phase 2 — CSS engine + DOM pipeline. See [`docs/phases/phase-2-css-engine.md`](docs/phases/phase-2-css-engine.md).

### How to test current Phase 1 progress
```bash
dotnet build NetPdf.slnx -c Release
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
