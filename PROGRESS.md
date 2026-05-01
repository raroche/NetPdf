# NetPdf — Progress Status

**Current phase:** Phase 1 — PDF writer + text foundation
**Tagged release:** `0.0.1-phase0` (Phase 0 complete)
**Target next tag:** `0.1.0-alpha` (Phase 1 complete)
**Last updated:** 2026-05-01 (post-Task-9 hardening ✅ — Emit() trust boundary, FromSubset early-break, shaping-path doc)

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
**Task 10 — Type 0 / CIDFontType2 wrapper for embedded fonts** (mini-est. 2 days)

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

- **Task 4 — Content stream writer + minimal operator vocab** ✅ (2026-05-01)
  - `ContentStreamWriter` (`src/NetPdf.Pdf/Content/`) — emits PDF content-stream operators per ISO 32000-2 §8/§9 with one operator per line for byte-deterministic, debuggable output. Phase 1 vocabulary covers: graphics state (`q`, `Q`, `cm`); path construction (`m`, `l`, `re`, `h`); path painting (`f`, `S`, `n`); device-color shorthands (`rg`, `RG`, `g`, `G`); line width (`w`); text (`BT`, `ET`, `Tf`, `Td`, `TD`, `Tj`, `TJ`); XObject (`Do`); marked content (`BMC`, `BDC`, `EMC`).
  - **Runtime nesting invariants enforced**: `q`/`Q` balance, `BT`/`ET` pairing (no double-open, no orphan close), `BMC`/`BDC`/`EMC` depth, text-only operators rejected outside `BT`/`ET`, path operators rejected inside `BT`/`ET`. `Finish()` validates final balance and throws with specific messages on violation. Operations after `Finish` throw.
  - `TextArrayElement` — discriminated value-type for `TJ` array elements (string or numeric advance offset).
  - `ContentStreamBuilder.Build(body, compress)` — convenience facade returning a fully-formed `PdfStream`. Optional `compress: true` zlib-deflates via `System.IO.Compression.ZLibStream` (the PDF `FlateDecode` filter expects zlib-framed bytes, not raw deflate) and sets `/Filter /FlateDecode` on the dictionary.
  - 47 new tests covering: every operator's exact byte output; state-validation throws (unbalanced `q`/`Q`, double `BT`, orphan `ET`/`EMC`, text-op outside text, path-op inside text, op-after-`Finish`, `Finish` twice); literal-string escape correctness in `Tj`; `TJ` array with mixed strings + offsets; builder produces correct `PdfStream`; FlateDecode round-trip via `ZLibStream` decompression; compression actually shrinks repetitive content; determinism property test.
  - Total tests: **231 unit / 242 solution-wide passing**.

- **Post-Task-4 hardening pass** ✅ (2026-05-01) — fourth review-driven round.
  - **Color-component validation**: `SetFillRgb`, `SetStrokeRgb`, `SetFillGray`, `SetStrokeGray` now reject components outside `[0, 1]` and reject `NaN`/`±∞` via a single `EnsureNormalizedComponent` helper, with parameter-targeted `ArgumentOutOfRangeException`. Previously the docs said `[0, 1]` but the code happily emitted `2 0 0 rg`. PDF DeviceRGB / DeviceGray spaces are normalized; out-of-range values produce undefined viewer behavior. Endpoints (`0`, `1`) and midpoints continue to round-trip exactly.
  - **`IContentStream` callback narrowing** (`src/NetPdf.Pdf/Content/IContentStream.cs`): new internal interface listing the 26 operator-emission methods on `ContentStreamWriter` but **not** `Finish()`. `ContentStreamBuilder.Build` now takes `Action<IContentStream>` instead of `Action<ContentStreamWriter>`. The builder retains lifecycle ownership: a callback can no longer call `Finish()` early and trip every later operator into "after-finish" throws. Direct callers who genuinely need lifecycle control still use `ContentStreamWriter` concretely.
  - **`Do`/`BMC`/`BDC` format normalization**: emission was inconsistent — most operators wrote operand + space + operator on the same line, but `PaintXObject`, `BeginMarkedContent`, and `BeginMarkedContentWithProperties` separated operand from operator with a newline (e.g., `/Im1\nDo\n`). All three now use the same-line convention (`/Im1 Do\n`, `/Span BMC\n`, `/Span << ... >> BDC\n`). The output is still byte-deterministic; just consistent and easier to eyeball.
  - **Compression-determinism test**: `Build_compressed_is_deterministic` renders the same body twice with `compress: true` and asserts byte-equal output, locking down `ZLibStream`'s deterministic behavior (it's currently deterministic — the test catches if a future BCL change introduces nondeterminism).
  - 22 new tests (color out-of-range/NaN/infinity per component × 4 setters, endpoint + midpoint acceptance, compressed determinism, compile-time `IContentStream` callback assertion); 3 existing tests updated for the `Do`/`BMC`/`BDC` format change.
  - Total tests: **253 unit / 264 solution-wide passing**.

- **Task 5 — `DisplayCommand` IR + `DisplayList` buffer** ✅ (2026-05-01)
  - `DisplayCommand` (`src/NetPdf.Paint/DisplayCommand.cs`) — 64-byte tagged-union value type via `[StructLayout(LayoutKind.Explicit, Size = 64)]`. `Kind` discriminator at offset 0; per-kind payload structs (`RectFillPayload`, `TextRunPayload`, `ImageDrawPayload`, `TransformPushPayload`, `OpacityPushPayload`) overlay at offset 8 via `[FieldOffset(8)]`. Cache-line-aligned for fast sequential walks. Static factories pair the kind + payload write so the union invariant can never be partially set; `As*` accessors verify the kind on read and throw on mismatch. Bitwise equality via `MemoryMarshal.AsBytes` so identical-input commands compare equal even though `[FieldOffset]` overlap defeats the BCL's default `ValueType.Equals` heuristics.
  - `DisplayCommandKind` (`src/NetPdf.Paint/DisplayCommandKind.cs`) — `byte`-backed enum. Phase 1 vocabulary: `None`, `RectFill`, `TextRun`, `ImageDraw`, `TransformPush`, `TransformPop`, `OpacityPush`, `OpacityPop`. Append-only — Phase 3 will extend with `PathFill`, `PathStroke`, `ClipPush/Pop`, `LinkAnnotation`, `BookmarkAnchor`.
  - `RgbaColor` (`src/NetPdf.Paint/RgbaColor.cs`) — packed `0xRRGGBBAA` `readonly struct` (4 bytes). Fits inside command payloads. Convenience converters (`ToNormalizedRgb`, `NormalizedAlpha`) bridge to PDF DeviceRGB/DeviceGray normalized components. Wide-gamut (display-p3, oklch) deliberately out of scope for Phase 1.
  - `TextRun` (`src/NetPdf.Paint/TextRun.cs`) — sealed reference type for the side table. Carries `FontId`, `FontSize`, `Color`, source `Text`, optional shaped `GlyphIds` + `Advances`. Side-table because shaped glyph buffers are variable-length. Until shaping lands (Phase 1 Tasks 6–9), the emitter falls back to encoding `Text` through the resolved font's `cmap`.
  - `RasterImage` (`src/NetPdf.Paint/RasterImage.cs`) + `ImageEncoding` enum — sealed reference type with `EncodedBytes`, `Encoding` (Png/Jpeg in Phase 1; WebP/AVIF post-Phase-4), `PixelWidth`/`PixelHeight`, `HasAlpha`. The same path serves both source images and Phase 4 raster-fallback tiles.
  - `DisplayList` (`src/NetPdf.Paint/DisplayList.cs`) — `ArrayPool<DisplayCommand>.Shared`-rented buffer. Initial capacity 64; doubles on grow with cap `1 << 28` (safety brake against runaway layouts). `Add(in DisplayCommand)` for the command stream; `AddTextRun(TextRun)` / `AddImage(RasterImage)` return sequential indices into the side tables (`List<TextRun>`, `List<RasterImage>`). `IDisposable`: returns the buffer to the pool with `clearArray: false` (no GC refs in `DisplayCommand`); post-dispose access to any member throws `ObjectDisposedException` so a stale span can't read pooled memory after another consumer rents it. `Dispose` is idempotent.
  - **Project hygiene**: dropped the placeholder `Placeholder.cs` and the unused project refs (`NetPdf.Layout`, `NetPdf.Text`) and `SkiaSharp` package ref from `NetPdf.Paint.csproj`. The csproj documents which Phase pulls each dep back in. Same hardening rule that trimmed `NetPdf.Pdf.csproj` after Task 1.
  - 49 new tests (`tests/NetPdf.UnitTests/Paint/`) covering: 64-byte struct size, default-init kind = `None`, every payload's factory + accessor round-trip, factory rejects (negative side-table index, alpha out-of-range/NaN/±∞), bitwise equality across kinds and payloads, `DisplayList` insertion order, growth past initial capacity (1000 commands → multiple `ArrayPool` rents), sequential side-table indices, null guards on side-table inserts, `GetTextRun`/`GetImage` out-of-range throws, identical-build-sequence value equality (determinism), all post-`Dispose` member access throws, idempotent `Dispose`, `RgbaColor` channel packing / round-trip / equality / formatting.
  - Total tests: **302 unit / 313 solution-wide passing**.

- **Testing discipline formalized** ✅ (2026-05-01)
  - Per Roland: every shipped functionality must carry **both unit and integration test coverage**, in the same commit/PR as the functionality. Tests are the safety net that lets the codebase grow without regressions stalling refactors.
  - Codified in [`docs/coding-standards.md`](docs/coding-standards.md) as the dual-layer rule (non-negotiable) — added to the testing-patterns section, the code-review checklist, and the "what to test" breakdown by layer (unit = per-class isolation, contract, edge cases, validation, invariants; integration = cross-component composition, specs as gates, real-world corpus, end-to-end determinism, external-tool validation).
  - Saved as a feedback memory so future sessions inherit the rule automatically.
  - Applied to Task 6 below: per-table unit tests + an `OpenTypeFontTests` integration suite exercising the full SFNT-directory → 10-table-parser → top-level orchestrator composition.

- **Post-Task-5 hardening pass** ✅ (2026-05-01) — fifth review-driven round.
  - **Tagged-union invariant locked at the type level (P1)**: `DisplayCommand.Kind` was a public mutable field — any consumer or friend assembly could retag a constructed command, leaving the overlaid payload misinterpreted. Now `_kind` is a private `[FieldOffset(0)]` field with a `public readonly DisplayCommandKind Kind => _kind` getter. Factories assign `_kind` directly; no path exists to retag from outside `DisplayCommand` itself. All `As*` / `Equals` / `GetHashCode` / `ToString` members are now `readonly` so callers passing `in DisplayCommand` skip defensive copies.
  - **Finite-geometry rejection at IR boundary (P2)**: `RectFill`, `TextRun`, `ImageDraw`, and `TransformPush` factories now reject `NaN` / `±∞` for every coordinate, size, and matrix term via `EnsureFinite`. Negative coordinates and sizes remain accepted (PDF's `re` operator allows them; layout uses negatives routinely). Mirrors the round-4 color-component rejection — producer bugs surface at the IR boundary instead of much later in the PDF emitter.
  - **`None` sentinel rejected at `DisplayList.Add` (P2)**: appending a `default(DisplayCommand)` (Kind == None) now throws `ArgumentException`. The sentinel is documented as "uninitialized"; letting it leak into the rendering pipeline forces every downstream consumer to defend against an obviously-invalid command.
  - **Side-table buffer ownership decided (P2)**: `TextRun.GlyphIds` and `TextRun.Advances` now copy on `init` — caller mutations to the source array no longer affect the inserted run. Cost is one short array allocation per shaped run (typically < 1 KB) which preserves the determinism guarantee documented on `DisplayList`. `RasterImage.EncodedBytes` keeps reference semantics (encoded payloads are tens-to-hundreds of KB and the typical path is a shared cache slot — copying every insert would defeat that), with the immutability contract now documented explicitly on the type and on the `EncodedBytes` property.
  - **Boundary validation in `AddTextRun` / `AddImage`**: `AddTextRun` rejects non-positive or non-finite `FontSize` and rejects mismatched `GlyphIds` / `Advances` lengths when either buffer is populated. `AddImage` rejects empty `EncodedBytes` and non-positive pixel dimensions. Contract violations surface at insertion, not at emit.
  - 41 new tests (`tests/NetPdf.UnitTests/Paint/DisplayCommandHardeningTests.cs` + `DisplayListHardeningTests.cs`) covering: reflection-level absence of a `Kind` setter; non-finite rejection per-parameter for every geometry-bearing factory (3 bad values × 4 RectFill params + 2 TextRun + 4 ImageDraw + 6 TransformPush); negative-geometry acceptance; default-command rejection at `Add`; all six `FontSize` rejection cases (zero, negative, NaN, ±∞); glyph/advance length-mismatch rejection in three shapes; empty `EncodedBytes` and bad pixel-dimension rejection; `TextRun` copy-on-init isolation (mutate source post-construction → run unchanged); empty-buffer no-allocation case.
  - Total tests: **343 unit / 354 solution-wide passing**.

- **Task 6 — TTF / OpenType parser** ✅ (2026-05-01)
  - All 10 required tables parsed under `src/NetPdf.Text/Fonts/OpenType/`: `head`, `hhea`, `hmtx`, `maxp`, `name`, `OS/2`, `post`, `cmap` (formats 4 and 12, with the OpenType-spec best-subtable selector), `loca` (short and long formats), and `glyf` (byte slices indexed by glyph id; deep glyph parsing deferred to Task 8 subsetter).
  - **Foundation**: `BigEndianReader` `ref struct` over `ReadOnlySpan<byte>` for stack-resident, allocation-free reads via `BinaryPrimitives.Read*BigEndian`; `OpenTypeTags` for the 11 known table tags as `uint` constants (big-endian-encoded ASCII) and SFNT version magics; `TableRecord` 16-byte directory entry; `TableDirectory.Parse` walks the SFNT header + directory and returns a `FrozenDictionary<uint, TableRecord>` for O(1) tag lookup.
  - **Top-level orchestrator** `OpenTypeFont.Parse(ReadOnlyMemory<byte>)` wires every table parser, validates required-table presence, and slices `glyf` via the directory record (the loca offsets are `glyf`-relative, not file-relative). Immutable, safe for concurrent reads. CFF outlines are punted to Task 7 — the type carries `Loca`/`Glyf` as nullable for the OTF/CFF case.
  - **Spec basis (clean-room)**: Microsoft OpenType spec + Apple TrueType reference. No code transliterated from any third-party implementation; per-file rationale documented in summary headers.
  - **Project hygiene**: dropped the placeholder `Placeholder.cs`. Trimmed `HarfBuzzSharp` + the three native-asset packages (`Linux`, `macOS`, `Win32`) from `NetPdf.Text.csproj` — the parser doesn't use them. Task 11 (HarfBuzz wrapper) re-adds. Same precedent as Phase 1 hardening trimmed `NetPdf.Pdf.csproj`.
  - **Dual-layer tests** following the new discipline: `SyntheticFont` test helper builds a valid minimal TTF byte stream with all 10 tables (3 glyphs, format-4 cmap mapping `'A'`/`'B'`); 13 unit-test classes one-per-source-class (76 unit tests total) exercise each parser in isolation + edge / corruption / version-skew cases; `OpenTypeFontTests.cs` (9 integration tests) runs the full SFNT-directory → 10-parser → orchestrator pipeline and asserts cross-table consistency (glyph count agreement across `maxp`/`hmtx`/`loca`, cmap → loca → glyf round-trip for 'A', name-record decoding through `Encoding.BigEndianUnicode`, parser determinism on repeated calls, typo-metric agreement between `hhea` and `OS/2`).
  - Total tests: **428 unit / 439 solution-wide passing**.

- **Post-Task-6 hardening pass** ✅ (2026-05-01) — sixth review-driven round.
  - **cmap selector with format-aware fallback (P1)**: previously the selector picked the highest-priority `(platformId, encodingId)` record without checking whether its subtable format was supported (4 or 12). A font carrying its preferred Unicode subtable in an unsupported format alongside a lower-priority format-4/12 fallback would fail outright. Selection now peeks each candidate subtable's format byte during scoring and skips records whose format isn't in the supported set — falling back to the next best supported subtable instead of throwing. The "no usable subtable" path now produces a precise diagnostic naming the supported formats.
  - **cmap subtable length clamp (P1)**: format 4 and format 12 parsers used to operate on `cmap[subtableOffset..]` — a slice from the chosen offset to the end of the parent table — letting a malformed subtable read past its declared length into the next subtable's bytes. A new `ClampSubtableToDeclaredLength` peeks the format-specific length field (uint16 at offset 2 for format 4, uint32 at offset 4 for format 12) and slices the subtable to exactly that many bytes before parsing. Zero / oversized declared-length cases now reject with descriptive messages.
  - **`GetName` considers Platform 0 (P2)**: the resolver looked at Windows + Macintosh records but ignored Platform 0 (Unicode) — even though the parser successfully decoded those names. A font carrying only a Platform 0 PostScript record returned `null`. Resolution chain is now Windows → Unicode → Macintosh; records that failed to decode (`Text is null`) are skipped so a partially-readable font surfaces names from usable records.
  - **Cross-table validation in `OpenTypeFont.Parse` (P2)**: the parser is the trust boundary for downstream shaping / embedding. After `cmap` and `maxp` parse, `ValidateCmapAgainstMaxp` walks every cmap group and rejects fonts whose reachable glyph ids extend past `maxp.numGlyphs` — preventing later code from indexing `hmtx`/`loca`/`glyf` out of range on internally inconsistent fonts.
  - 10 new tests (`CmapTableHardeningTests.cs`, `NameTableHardeningTests.cs`, `OpenTypeFontHardeningTests.cs`) covering: selector fallback with two records (preferred unsupported + lower-priority supported); selector throws when no supported format present; subtable with declared length > available bytes; subtable with declared length 0; format-4 parser doesn't read past clamped length into trailing sentinel bytes; `GetName` returns Platform 0 Unicode-only PostScript name; preference order Windows > Unicode > Mac when multiple platforms present; cross-table integration test rejecting a font where `cmap` maps a code point to a glyph id beyond a shrunk `maxp.numGlyphs` (with `hhea.numberOfHMetrics` mutated in lock-step so the test reaches the cross-table validator instead of failing earlier per-table).
  - Total tests: **438 unit / 449 solution-wide passing**.

- **Task 7 — OTF / CFF v1 parser** ✅ (2026-05-01)
  - Five components under `src/NetPdf.Text/Fonts/OpenType/Cff/`: `CffHeader` (4-byte fixed header), `CffIndex` (generic INDEX with variable-width 1-based offsets and bounds checking on every offset), `CffDict` (operand-then-operator pairs with all five integer-encoding forms + 5-byte real with `0xF` terminator), `CffCharset` (formats 0/1/2 with implicit `.notdef` at glyph 0), `CffTable` orchestrator (header → Name INDEX → Top DICT INDEX → String INDEX → Global Subr INDEX → Charset → CharStrings INDEX, with ROS-based CID detection).
  - **Spec basis (clean-room)**: Adobe Technical Note #5176. Per-file rationale + section pointers documented in summary headers.
  - **`OpenTypeFont.Parse` integration**: when `directory.IsCff`, the orchestrator slices the `CFF ` table, parses it via `CffTable.Parse`, and runs the same cross-table consistency check as TTF — `CharStrings.Count` must equal `maxp.numGlyphs`. The `Cff` property hangs alongside `Loca` / `Glyf` (all three nullable, mutually exclusive: TTF has `Loca` + `Glyf`, OTF/CFF has `Cff`).
  - **CFF2 deferred**: `CffHeader.Parse` rejects `major != 1` with a precise diagnostic naming v1 as the only supported version. CFF2 (variable fonts) is gated to Phase 1.x — variable fonts are post-v1.0 work.
  - **Phase 1 scope** intentionally excludes: predefined-charset fallback (consumers always provide an explicit charset), Encoding parsing (legacy Type 1 use, not relevant to OpenType-embedded fonts), FDSelect parsing (CID-keyed mapping needed by the subsetter; will land alongside CFF subsetting in Task 8 or later), deep Type 2 charstring decoding (subsetter Task 8). The parser exposes byte-level access so these future consumers have what they need.
  - **Dual-layer tests**: 39 per-class unit tests across 5 test files plus a `SyntheticCff` builder that emits a 3-glyph valid CFF byte stream (header, name INDEX, Top DICT with charset + CharStrings + Private offsets, String + Global Subr empty INDEXes, format-0 charset, CharStrings INDEX with `0x0E` "endchar" stub charstrings); `SyntheticOtf` wraps that CFF inside an SFNT envelope (sfntVersion = 'OTTO', tables sorted in canonical order, no loca/glyf) by reusing the per-table builders from `SyntheticFont`; 6 integration tests in `OpenTypeFontCffTests` drive the full SFNT-directory → 8-table-parser → CFF-orchestrator pipeline and assert glyph-count consistency across `maxp` / `hmtx` / `cmap` / `Cff.NumGlyphs`, mutually-exclusive-outline-flavor invariants (`HasCffOutlines` true when `HasTrueTypeOutlines` false), and end-to-end charstring resolution through `font.Cff!.GetCharStringBytes`.
  - Total tests: **483 unit / 494 solution-wide passing**.

- **Post-Task-7 hardening pass** ✅ (2026-05-01) — seventh review-driven round.
  - **Top DICT offset validation (P1)**: `CharStrings` and `charset` offsets used to flow out of `CffDict.Parse` as `double[]` and got blindly cast to `int` in `CffTable.Parse`. A malformed font encoding the offset as `NaN` (silent → 0), `±Infinity` (silent → int extremes), a fractional real, or a negative value would retarget parsing into the wrong part of the table instead of failing at the trust boundary. New `RequireIntegralOffset(double, name, upperBoundExclusive)` helper rejects non-finite values, fractional values, negatives, and values ≥ table length with parameter-named diagnostics.
  - **Name INDEX single-font invariant (P2)**: parser already required `topDictIndex.Count == 1` for OpenType-embedded CFF; the same constraint now applies to `nameIndex`. Empty Name INDEX no longer silently produces `FontName = ""`, and a multi-entry Name INDEX no longer silently picks only the first — both broke font-identity guarantees for downstream BaseFont generation. The dead empty-name fallback simplifies away.
  - **Real-operand exception fence (P2)**: `CffDict.ParseReal` previously called `double.Parse`, letting `FormatException` / `OverflowException` escape for semantically malformed nibble sequences (empty, repeated `E`, orphan `E-`). Switched to `double.TryParse` with a `NumberStyles.Float` + `IsFinite` check that re-throws as `InvalidDataException` — keeps malformed-font handling uniform with the rest of the parser. Also rejects empty digit strings (only-terminator nibble) explicitly.
  - **Charset range wraparound (P2)**: `CffCharset` formats 1 and 2 expanded ranges via `(ushort)(first + k)`, silently wrapping SID/CID values past `0xFFFF` back to low values — corrupting the structural identity downstream consumers rely on. New guard rejects ranges whose last value would exceed `0xFFFF`. Ranges ending exactly at `0xFFFF` still parse (boundary respected).
  - 13 new tests across `CffTableHardeningTests.cs` (6: fractional CharStrings offset, out-of-range CharStrings offset, negative CharStrings offset, fractional charset offset, empty Name INDEX, multi-entry Name INDEX), `CffDictHardeningTests.cs` (4: terminator-only real, repeated exponent marker, orphan `E-`, valid real still parses), `CffCharsetHardeningTests.cs` (3: format 1 wraparound, format 2 wraparound, format 1 boundary at exactly `0xFFFF` accepted).
  - Total tests: **496 unit / 507 solution-wide passing**.

- **Task 8 — TTF glyph subsetter** ✅ (2026-05-01)
  - Four components under `src/NetPdf.Pdf/Fonts/`: `GlyphSubsetPlan` (composite-glyph chase + ordered original-id list with .notdef always at new id 0), `TtfSubsetter` (emits subset bytes for `glyf` + `loca` + `hmtx` + `maxp` + `head` + `hhea`), `SubsetPrefix` (deterministic 6-uppercase-letter prefix from SHA-256 over font name + sorted used glyph ids → first 28 bits → base-26), `TtfSubsetResult` (output shape carrying every emitted byte stream + the plan + the prefixed BaseFont name).
  - **Composite-glyph chase**: `GlyphSubsetPlan.Build` walks every composite glyph's component records (parsing the variable-width record format per OpenType §"glyf" — flags / glyphIndex / args / optional 1×/2×/4× F2DOT14 transform), transitively pulls every referenced component into the subset, and rejects out-of-range component glyph ids. Composite-of-composite supported via worklist iteration. The visit set bounds work to O(numGlyphs).
  - **Component glyphIndex rewrite**: `TtfSubsetter.MaybeRewriteComposite` walks the same record format on emit, overwrites each component's glyphIndex bytes with the new id from the plan, leaves header / args / transform byte-identical so geometry is preserved bit-for-bit.
  - **Glyph alignment**: every emitted glyph's bytes are padded to a 2-byte boundary so `loca`'s short format remains usable. The subsetter chooses short vs long `loca` based on the resulting `glyf` size and writes the corresponding `head.indexToLocFormat`.
  - **`head.checkSumAdjustment` zeroed** — the SFNT envelope assembler in Task 10 recomputes it after final layout.
  - **Determinism**: subset bytes are byte-equal for byte-equal inputs. No PRNG; iteration order driven by `OrderedOldGlyphIds`; subset prefix derived from SHA-256.
  - **CFF subsetting deferred** to a follow-up under Task 8 ("CFF later") or merged with Task 10 — explicit `InvalidOperationException` from `GlyphSubsetPlan.Build` when called on a CFF font keeps the boundary clear.
  - **NetPdf.Pdf project re-references NetPdf.Text** (the Phase 1 hardening had trimmed it; comment in `NetPdf.Pdf.csproj` is updated accordingly).
  - **Dual-layer tests**: 25 tests across 3 test files covering each subsetter component in isolation plus an integration test that re-parses the emitted subset bytes through the OpenType table parsers from Task 6 (`HeadTable.Parse` / `MaxpTable.Parse` / `HmtxTable.Parse` / `LocaTable.Parse`) — the cross-component composition the dual-layer rule requires. `SyntheticFontWithComposite` test helper builds a 4-glyph TTF with composite glyph 3 referencing glyph 1, exercising the composite chase + glyphIndex rewrite end-to-end.
  - Total tests: **521 unit / 532 solution-wide passing**.

- **Post-Task-8 hardening pass** ✅ (2026-05-01) — eighth review-driven round.
  - **Shared composite-glyph walker (P1, #3)**: extracted `CompositeGlyph` (`src/NetPdf.Pdf/Fonts/CompositeGlyph.cs`) — a single source of truth for the OpenType §"glyf" component-record format. Exposes `IsComposite`, `EnsureValidHeader`, and `EnumerateComponents` returning `ComponentLocation` records (byte offset of `glyphIndex` + current value). Both the planner (`GlyphSubsetPlan.Build`) and the emitter (`TtfSubsetter.RewriteCompositeComponents`) now consume the same walker — eliminates the mirrored validation gaps that come from parsing the same wire format in two places.
  - **`WE_HAVE_INSTRUCTIONS` trailer validation (P2, #4)**: walker now reads the optional `instructionLength` (uint16) after the last component when the flag is set, and verifies the declared instruction bytes fit inside the glyph. Prevents truncated instruction payloads from passing planning + emission and producing broken output fonts.
  - **Short-glyph rejection (P2, #2)**: `CompositeGlyph.EnsureValidHeader` rejects any non-empty glyph payload smaller than the 10-byte glyph header. Called by both the planner (during composite chase) and the emitter (during `EmitGlyf`) — defense-in-depth at the trust boundary.
  - **Strict subset-plan preflight (P1, #1)**: `GlyphSubsetPlan.Validate(font)` checks `NumGlyphs ∈ [1, 65535]` (uint16-bound for downstream PDF font tables), glyph 0 at new id 0, no duplicates, every old id within `font.Maxp.NumGlyphs`, and `OldToNew` is the exact inverse of `OrderedOldGlyphIds`. `TtfSubsetter.Subset` calls `Validate` first so hand-constructed or out-of-band plans cannot reach byte emission.
  - **`TtfSubsetResult` ownership (P2, #7)**: every byte field switched from `byte[]` to `ReadOnlyMemory<byte>`. Downstream code can read via `.Span` for fast access but cannot mutate the emitted artifacts — silent corruption from accidental writes is no longer possible.
  - **`SubsetPrefix` Unicode-stable hashing (P2, #5)**: source font name now hashed as NFC-normalized UTF-8 instead of ASCII. Distinct international names like "Söhne" / "Sühne" now produce distinct prefixes; precomposed and decomposed forms of the same string produce equal prefixes. Fixed the algorithm comment (#9) to match the actual 32-bit-seed → div/mod 26 implementation (effective entropy ~28.2 bits, matching 26⁶ output space).
  - **Performance optimization (#6) deferred** per the review's recommendation — `EmitGlyf` could pre-compute padded glyf size and write into a single `byte[]` instead of through `MemoryStream.ToArray()`, but only after benchmarking confirms it matters.
  - 28 new tests across 4 files: `CompositeGlyphTests` (12 tests covering `IsComposite` / `EnsureValidHeader` / `EnumerateComponents` for valid two-component chains, `WE_HAVE_INSTRUCTIONS` trailers, truncated instruction payloads, truncated component headers / argument pairs / transforms, simple-glyph rejection); `GlyphSubsetPlanHardeningTests` (8 tests for every preflight branch — glyph 0 misplaced, duplicate ids, out-of-range ids, mismatched dictionary size, non-inverse mapping, empty plan, cross-font plan); `TtfSubsetterHardeningTests` (4 tests verifying preflight runs before emission, cross-font plan rejected, short-glyph rejected at both planner and emitter); `SubsetPrefixHardeningTests` (3 tests confirming non-ASCII collision avoidance, NFC normalization equivalence, CJK family-name handling).
  - Total tests: **549 unit / 560 solution-wide passing**.

- **Task 9 — ToUnicode CMap generator** ✅ (2026-05-01)
  - `ToUnicodeCMap` (`src/NetPdf.Pdf/Fonts/ToUnicodeCMap.cs`) — produces the PDF CMap stream that PDF readers consume to recover original Unicode text from content streams using Identity-H encoding. The mapping is `SubsetGlyphIdToText : IReadOnlyDictionary<int, string>`; the value is an arbitrary string so ligature glyphs can map to multi-codepoint targets when GSUB-aware shaping lands later.
  - **`FromSubset(font, plan)` factory** walks `font.Cmap.Groups` (which the Task 6 cmap parser already produces in ascending codepoint order), filters to the original glyph ids in `plan.OldToNew`, and records each subset glyph id → first-encountered (= lowest) source codepoint. The "lowest codepoint wins" tie-break is deterministic for fonts where multiple codepoints map to the same glyph (e.g. U+00B5 micro / U+03BC mu often share a glyph). The factory calls `plan.Validate(font)` first, so cross-font plans are rejected at the trust boundary before any cmap walking.
  - **`Emit()` output** is byte-deterministic ASCII (Adobe "PDF Reference" §5.9 + ISO 32000-2:2020 §14.3.4 PostScript-style CMap). Header / `CIDSystemInfo` / `CMapName /Adobe-Identity-UCS` / `CMapType 2` / `1 begincodespacerange <0000> <FFFF>` are constant. Body emits sorted entries in `beginbfchar` blocks chunked at the spec cap of 100 entries per block. Source codes are 4-hex subset glyph ids; targets are UTF-16BE hex (4 hex per BMP char, surrogate pair = 8 hex for supplementary plane).
  - **`bfrange` compaction deferred** — `beginbfchar` uses ~14 bytes per entry; `bfrange` saves ~⅓ on contiguous mappings. Phase 1 prioritizes simplicity; the optimization lands when benchmarks show it matters.
  - **Ligature support staged** — the data shape supports multi-codepoint targets; today's `FromSubset` only produces single-codepoint mappings since shaping (Task 11+) hasn't yet labeled glyphs as ligatures.
  - **Dual-layer tests**: `ToUnicodeCMapTests` (10 unit tests) covers header / footer constants, ASCII-only output property, empty subset, single BMP codepoint, supplementary-plane surrogate pair, multi-codepoint ligature target, sort-by-subset-id determinism, multi-block chunking at 100/100/50 for a 250-entry mapping, byte-determinism property test, uppercase-hex convention. `ToUnicodeCMapIntegrationTests` (6 integration tests) drives the full `OpenTypeFont.Parse → GlyphSubsetPlan.Build → ToUnicodeCMap.FromSubset → Emit` pipeline against `SyntheticFont` and asserts: subset glyphs 1+2 map to "A"/"B" with the expected `<0001> <0041>` / `<0002> <0042>` lines, .notdef is excluded from the map, glyphs outside the subset are skipped, byte-determinism holds end-to-end, and cross-font plans are rejected.
  - Total tests: **565 unit / 576 solution-wide passing**.

- **Post-Task-9 hardening pass** ✅ (2026-05-01) — ninth review-driven round.
  - **`Emit()` trust boundary (P1)**: previously trusted whatever `SubsetGlyphIdToText` held. Direct construction via the `init` property bypassed `FromSubset`'s implicit validation, so a caller could ship invalid CMap content (wrapped CIDs > 65535, empty target strings, unpaired surrogates) that would look like valid ASCII bytes but break PDF readers. New `ValidateForEmit()` runs at the top of `Emit()`: rejects subset glyph ids outside `[0, 65535]` (Identity-H is 16-bit), rejects empty target strings, and validates UTF-16 surrogate pairing (no orphan high or low surrogates).
  - **`FromSubset` asymptotic improvement (P2)**: tracked unresolved subset glyphs in a `HashSet<int>` and break out of the cmap walk as soon as it empties. For the typical case (small subset of a large CJK or symbol font) work is now bounded by the position of the last-needed cmap entry rather than full Unicode coverage. The "lowest-codepoint wins" tie-break is preserved naturally — `unresolved.Remove(originalGid)` only succeeds on the first encounter, and cmap groups are already sorted by codepoint.
  - **Documentation: future shaping-derived path (P2)**: strengthened the type-level XML docs to make explicit that `FromSubset` is the **fallback** path (cmap-only). A future `FromShapedRuns` factory is reserved for Task 11+ when HarfBuzz lands and the shaper can label glyph runs with their exact source Unicode — at that point ligatures and GSUB substitutions round-trip correctly without needing cmap reverse-lookups.
  - **Performance optimization (P2 #6) deferred** per the review's recommendation — `Emit()` builds a `StringBuilder` and converts to ASCII bytes; switching to a direct `ArrayBufferWriter<byte>` ASCII sink lands when benchmarks justify it.
  - 8 new tests in `ToUnicodeCMapHardeningTests`: negative subset glyph id, glyph id above `0xFFFF`, empty target string, unpaired high surrogate (alone at end), high surrogate followed by non-low-surrogate BMP char, unpaired low surrogate (alone), low surrogate after BMP char, and a positive case confirming a valid supplementary-plane pair (U+1F600) still emits successfully.
  - Total tests: **573 unit / 584 solution-wide passing**.

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
