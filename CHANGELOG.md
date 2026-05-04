# Changelog

All notable changes to NetPdf are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The repository is **private through Phase 5**; tagged releases below are git tags only — NuGet publication happens at the v1.0 launch event (per `docs/phases/phase-5-packaging-and-release.md`).

## [Unreleased]

[Unreleased]: https://example.invalid/NetPdf/compare/0.1.0-alpha...HEAD

## [0.1.0-alpha] — 2026-05-03

Phase 1 — PDF writer + text foundation. NetPdf can now produce well-formed, byte-deterministic PDF 1.7 bytes programmatically from internal `PdfDocument` calls (no HTML pipeline yet — that ships in Phase 2). The PDF byte writer, the international text shaping subsystem (UAX #9 / #14 / #29 conformance), the OpenType font subsetting + ToUnicode CMap pipeline, the WOFF 1.0 / 2.0 decoders, the JPEG / PNG / WebP / AVIF / GIF image embedders with content-hash dedup and indirect `/SMask` wiring, the determinism harness with a per-platform pin map, the AOT-clean smoke binary with an enforced JIT/AOT byte-parity gate, and the BenchmarkDotNet baseline with a +25%-tolerance regression gate are all in place.

### Added

#### PDF byte writer (`NetPdf.Pdf`)
- Full `PdfObject` hierarchy: 11 concrete types (Boolean, Null, Integer, Real, Name, LiteralString, HexString, Array, Dictionary, Stream, IndirectRef) emitting bytes per ISO 32000-2:2020 §7.3. `PdfDictionary` uses `OrderedDictionary<TKey,TValue>` for deterministic insertion-order preservation.
- `IndirectObjectStore` with 1-based deterministic numbering, `Allocate` / `Add` / `Assign` / forward references, byte-offset recording, and `ValidateAllAssigned` gate.
- `PdfDocumentWriter` orchestrating header → indirect objects → xref → trailer → `startxref` → `%%EOF`. xref entries exactly 20 bytes (§7.5.4); 4-byte binary marker comment (§7.5.2); `MaxXrefByteOffset = 9_999_999_999` enforced (4 boundary unit tests cover the throw).
- Auto-derived trailer `/ID` from SHA-256 of header + indirect objects + xref (§14.4) — content-addressing for free.
- `PdfPreflightValidator` runs structural checks before any byte is written: version on allow-list, all slots assigned, `/Root` shape, explicit `/ID` shape, foreign-store reference rejection, direct-cycle + indirect-cycle detection, no nested direct streams (streams must be indirect per §7.3.8).
- `ContentStreamWriter` with `q` / `Q` / `cm` / `re` / `f` / `S` / `Tj` / `TJ` / `Do` / BMC / BDC / EMC operators; `ContentStreamBuilder` facade with optional FlateDecode compression at the centrally-pinned `PdfFormat.PdfDeflateCompressionLevel = SmallestSize`.
- `DisplayCommand` 64-byte tagged-union IR + `DisplayList` buffer (Phase 2/3 paint output).

#### Font subsetter & embedding
- `OpenTypeFont` parser covering `head` / `hhea` / `maxp` / `cmap` / `loca` / `glyf` / `name` / `OS/2` / `post` / `hmtx` (TTF) **and** `CFF` / `CFF2` headers (OTF). Read-only span-based; AOT-clean. **Parsing scope only** — see embedding scope immediately below.
- **TTF embedding pipeline (shipped):** glyph subsetter with composite-glyph closure traversal + `glyf`/`loca` re-numbering; new `cmap` formats 4 + 12 (BMP + supplementary planes); `ToUnicode` CMap generator with deterministic per-glyph mapping (preserves searchable/copyable text without requiring tagged PDF); Type 0 / CIDFontType2 wrapper with full `/FontFile2` + `/FontDescriptor` + `/DescendantFonts` graph; 6-letter deterministic `BaseFont` prefix (SHA-256 of name + glyph bitmap → first 6 hex digits → base-26).
- **OTF / CFF embedding — deferred to Phase 1.x.** `0.1.0-alpha` does NOT ship CFF subsetting or the `FontFile3` / `CIDFontType0C` emit path. `EmbeddedTtfFont.Build` throws `InvalidOperationException` with a clear "OTF/CFF embedding deferred" message when called on a CFF-flavored `OpenTypeFont` — consumers get a precise signal rather than a malformed PDF. CFF2 (variable-fonts) is post-v1.0 work.

#### Text shaping (`NetPdf.Text`)
- HarfBuzzSharp wrapper (`HbShaper.Shape`) producing glyph runs with kerning + ligature + script-aware shaping.
- **UAX #9 Bidi** — full implementation (P / X / W / N / I / L rules + BD7/BD13 segmentation). UCD `BidiTest.txt` + `BidiCharacterTest.txt` at **100% conformance**.
- **UAX #14 Line Breaking** — pair-table-driven LB rules. UCD `LineBreakTest.txt` at **99.952% conformance**.
- **UAX #29 Grapheme Cluster Boundaries** — UCD `GraphemeBreakTest.txt` at **100% conformance**. (Word and sentence boundaries — stages 14.2 / 14.3 — are post-Phase-1.)
- **Liang hyphenation** with English (`en-us`) bundled patterns + exceptions list. Span-based hot-path lookup via `FrozenDictionary.GetAlternateLookup<ReadOnlySpan<char>>`.
- **Font registry** + cross-platform system font enumeration (macOS, Windows, Linux, Alpine). Per-document `FontRegistry` with composite key `(Family, WeightCss, StretchCss, Italic)`. Spec-accurate CSS Fonts 4 §5.2 ordered tier search (stretch direction → style → weight regime).
- **WOFF 1.0** decoder (zlib) and **WOFF 2.0** decoder (Brotli + glyf/loca transform reverse via the 128-entry §5.2 triplet decode table). Real Roboto-Regular.woff2 fixture exercises the full pipeline.

#### Image embedding (`NetPdf.Pdf.Images`)
- **JPEG passthrough** — strict SOI → SOFn → SOS → EOI walk; only SOF0 / SOF2 + 8-bit accepted. Adobe-inverted CMYK detected and emitted with `/Decode [1 0 1 0 1 0 1 0]`. ICC profiles tolerated.
- **PNG embedder** with four paths: opaque passthrough (`Predictor 15`), color-key `/Mask` for binary tRNS, alpha-split SMask for RGBA / GA, indexed-with-non-binary-tRNS via SMask. CRC-32 + structural validation. Bounded zlib decompression for trust-boundary defense.
- **WebP / AVIF / GIF** via SkiaSharp decode → RGB plane + optional alpha plane → FlateDecode. 25-megapixel cap. AVIF fixture (BSD-2-Clause libavif test corpus) committed; tests no-op gracefully on hosts without libavif (macOS).
- **`PdfDocument.RegisterImage(ImageXObjectResult)`** wires the indirect `/SMask` reference correctly: clones the primary image dictionary before assigning so the caller's builder output stays immutable across registrations. Same-instance dedup works; cross-document reuse works.
- **Content-hash dedup** keys on payload + dictionary canonical bytes (SHA-256 of both). Two pixel-identical buffers with different `/ColorSpace` or `/Filter` remain distinct.

#### High-level `PdfDocument` builder
- `AddPage(MediaBoxSize)` / `RegisterImage(PdfStream | ImageXObjectResult)` / `Save()` / `SaveTo(IBufferWriter<byte>)` API. Single-use document model (throws on second `Save()`).
- Info dict metadata: Title / Author / Subject / Keywords / Creator / Producer / CreationDate / ModDate. ISO 32000-2 §7.9.4 date format `D:YYYYMMDDHHmmSS{Z|+HH'mm'|-HH'mm'}`. Defaults to omitted dates for reproducibility.
- Image XObject shape validation: `/Subtype /Image`, Width > 0, Height > 0, ColorSpace, BitsPerComponent ∈ {1,2,4,8,16} (and {8,16} for SMasks per §11.6).

#### Determinism harness
- 18 document shapes × 4 property tests (byte-equal-twice, byte-equal-thrice, structural sanity, per-platform SHA-256 pin) = **72 parameterized determinism tests**.
- Per-platform pin map keyed by `OS-arch` (`osx-arm64` pinned; other platforms log "no pin, snapshot skipped" until Phase 5 captures them in the containerized reference environment).
- `DeterminismDiagnostics.AssertByteEqualsWithDiagnostics` produces diagnosable failures: first-diff offset + per-half SHA + hex/ASCII context windows.
- Error-path determinism: `RegisterImage(invalid)` and double-`Save()` must throw the same exception type and the same `Message` across runs.
- Explicit `/ID` extraction test (`Canonical_document_has_deterministic_trailer_ID`).
- `PdfFormat.PdfDeflateCompressionLevel = SmallestSize` pins the deflate level so all stream emitters produce byte-equal output for byte-equal input.

#### AOT smoke + parity gate
- `tests/NetPdf.AotSmoke/` publishes a 1.5 MB native binary via `dotnet publish -p:PublishAot=true`. Exercises JPEG passthrough + transparent-GIF SMask path through `PdfDocument`.
- `SmokeDocumentFactory` shared between the AOT entry point and the parity test — drift between them is impossible.
- `tests/NetPdf.UnitTests/Pdf/AotJitParityTests.cs` (4 tests): asserts the native binary's SHA-256 output equals the JIT factory's. Negative-path verified — perturbing the JIT factory causes the parity test to fail with a diagnosable SHA mismatch.
- `scripts/aot-parity.sh` — single-command publish + parity-gate run for CI.

#### Performance baseline + regression gate
- 6 benchmark classes split per concern (PageScaling, SinglePage, ImageEmbedding, Dedup, Streaming, MixedDocument) → 23 parameterized benchmarks.
- `[MemoryDiagnoser]` on every benchmark; Workstation GC pinned for cross-host stability.
- Per-platform baseline JSONs committed under `tests/NetPdf.Benchmarks/baselines/phase-1-osx-arm64/`.
- `scripts/benchmark-gate.sh` — single-command gate. Runs the suite, exports JSON, invokes `Program.cs --compare BASELINE-DIR CURRENT-DIR [tolerance]`, exits 1 if any benchmark Mean exceeds +25% of baseline. Negative-path verified.
- Phase 1 baseline highlights (Apple M4 Pro, .NET 10.0.7, macOS arm64, Workstation GC):
  - Single blank A4 page → bytes: **5.6 µs**, 7.45 KB.
  - 1000 blank pages → bytes: **2.59 ms**, 2.6 MB (linear at ~2.46 KB/page across 4 orders of magnitude).
  - JPEG passthrough single page: **9.2 µs**.
  - PNG RGBA + indirect SMask: **19.1 µs**.
  - WebP opaque via raster: **19.0 µs**.
  - Cache-hit-isolated dedup per call: **2.9 µs** (true per-call cost via `[OperationsPerInvoke = 99]`).
  - `SaveTo(IBufferWriter<byte>)` streaming path: **8.9 µs** (3% faster wall-clock vs `Save() → byte[]`).
- All Phase 1 wall-clock targets crushed: 100-page < 500 ms target = 264.7 µs actual ≈ **~1,890× headroom**.

#### Documentation
- [docs/design/determinism.md](docs/design/determinism.md) — public determinism contract, enforcement, re-pin protocol, known gaps.
- [docs/design/aot-smoke.md](docs/design/aot-smoke.md) — AOT-clean contract, banned patterns, run protocol, common AOT failure modes.
- [docs/design/performance.md](docs/design/performance.md) — performance baseline, targets vs. actual, regression-gate protocol, GC mode rationale, comparison with the wkhtmltopdf / Chromium-print / PDFsharp ecosystem.
- [docs/compatibility-matrix.md](docs/compatibility-matrix.md) — added "PDF metadata strings" + "Determinism" sections.
- Per-task subtask log in [PROGRESS.md](PROGRESS.md) covers tasks 1–25 with rationale and verification.

### Test counts
- **1546 unit tests** + 11 cross-project tests = 1557 total passing (1 skipped pin-capture utility).
- **72 determinism harness tests** (18 shapes × 4 property checks).
- **4 JIT/AOT parity tests** (factory determinism + native binary parity, including output-path verification).
- **23 BenchmarkDotNet benchmarks** with per-platform pinned baseline + +25% regression gate.

### Phase 1 exit criteria status
1. ✅ All Phase 1 task tests pass.
2. ✅ UCD reference tests pass: UAX #9 100%, UAX #14 99.952%, UAX #29 (grapheme stage) 100%.
3. ✅ Byte-determinism test passes (per-platform SHA-256 pin + 72 property tests).
4. ✅ AOT smoke publishes and runs successfully on `osx-arm64` (Linux + Windows pinned in Phase 5 CI matrix).
5. ✅ Programmatic `PdfDocument` construction works end-to-end and produces valid PDF 1.7 bytes that open in Acrobat / Firefox / Chrome / macOS Preview (verified via the AOT smoke output).
6. ⏸️ qpdf `--check` deferred to Phase 5 CI — `qpdf` is not yet a local dev dep; Phase 5 wires it into the cross-platform validation matrix.
7. ✅ Roboto-Regular WOFF 2.0 loads, subsets, and embeds end-to-end.
8. ✅ Performance baseline meets target (~1,890× headroom on the 100-page metric).
9. ✅ CHANGELOG updated, `0.1.0-alpha` tagged.

### Known gaps (post-Phase-1)
- **Word boundaries (UAX #29 stage 14.2)** and **sentence boundaries (UAX #29 stage 14.3)** — needed by Phase 2/3 layout, deferred from Phase 1's grapheme-cluster scope.
- **Non-ASCII metadata** — `PdfLiteralString` rejects `char > 0x7E`; the Phase 2 facade will route via `PdfHexString` (UTF-16BE) automatically. Documented in `docs/compatibility-matrix.md`.
- **Cross-platform pinned hashes** for `linux-x64`, `linux-arm64`, `win-x64` — captured by the Phase 5 CI matrix when it lands.
- **AVIF benchmark** — deferred to Phase 5 cross-platform CI (libavif unavailable on macOS dev hosts).
- **`HtmlPdf.Convert(html)`** — public facade not yet wired (Phase 2 work). Phase 1 ships only the byte-writer surface; the public API throws `NotImplementedException` on call.

### Phase 0
- Initial scaffolding.
- Apache-2.0 license established.
- Clean-room policy documented (`docs/clean-room-policy.md`).
- Dependency dossier opened (`docs/legal/dependency-dossier.md`).
- Compatibility matrix published (`docs/compatibility-matrix.md`).
- Diagnostics code registry started (`docs/diagnostics-codes.md`).
- PDF spec notes & errata index opened (`docs/pdf-spec-notes.md`).
- Per-phase execution guides under `docs/phases/`.
- Project-scoped skills under `.claude/skills/`.
- `CLAUDE.md` session-bootstrap added at repo root for any future Claude session.
- Secrets-and-credentials policy documented (`docs/secrets-and-credentials.md`).
- Public API surface frozen: `HtmlPdf`, `HtmlPdfOptions`, `PdfRenderResult`, `IResourceLoader`, `IFontResolver`, `IDiagnosticsSink`, `FeatureFlags`, `SecurityPolicy`, `CachePolicy`.
- Solution scaffolding for nine source projects and eleven test projects.
- NuGet package ID `NetPdf` reserved on nuget.org via `0.0.1-phase0` placeholder (unlisted).
- `NUGET_API_KEY` set up as GitHub Actions repository secret with `NetPdf*` glob scope.

[0.1.0-alpha]: https://example.invalid/NetPdf/compare/0.0.1-phase0...0.1.0-alpha
