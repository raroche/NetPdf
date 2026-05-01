# Phase 1 — PDF Writer + Text Foundation

**Status:** ⏳ next.
**Time:** team estimate 16 wk → Claude Opus 4.7 high + Roland-in-the-loop: **4–6 wk**.
**Tagged release:** `0.1.0-alpha` — programmatic PDF construction works end-to-end.

## Goal

Build the foundational byte writer and the text shaping pipeline. By the end of this phase, NetPdf can produce a well-formed, byte-deterministic PDF programmatically (no HTML yet) containing subsetted embedded fonts, ToUnicode-searchable text, and embedded images. Internationalized text rendering (bidi, complex scripts, CJK, ligatures) works. WOFF2 fonts load. AOT publish + execution succeeds.

## Prerequisites

- Phase 0 complete. `dotnet build NetPdf.slnx` is green.
- Read [phase-0-architecture-lock.md](phase-0-architecture-lock.md) to know what's in place.
- Read [../clean-room-policy.md](../clean-room-policy.md) — algorithms from specs, not transliterated from other implementations.
- Read [../pdf-spec-notes.md](../pdf-spec-notes.md) — our resolved interpretations for spec ambiguities.

## Deliverables

### `NetPdf.Pdf` — the byte writer

Produces deterministic PDF 1.7-compatible bytes (PDF 2.0 opt-in via `EmittedPdfVersion = V2_0`).

- **PDF object model** (`src/NetPdf.Pdf/Objects/`):
  - `PdfObject` abstract base + concrete types: `PdfBoolean`, `PdfInteger`, `PdfReal`, `PdfName`, `PdfLiteralString`, `PdfHexString`, `PdfArray`, `PdfDictionary`, `PdfStream`, `PdfNull`, `PdfIndirectRef`.
  - Each implements `WriteTo(IBufferWriter<byte>)` — span-based, zero-allocation hot path.
  - `PdfName` interned via `FrozenDictionary<string, PdfName>` at startup for the standard names (`/Type`, `/Pages`, `/Page`, `/Catalog`, etc.).
- **Indirect-object store** (`src/NetPdf.Pdf/IndirectObjectStore.cs`):
  - Allocates object numbers in traversal order (deterministic).
  - Generation always 0 in v1 (we don't produce incremental updates).
  - Records byte offsets as objects are written for the xref pass.
- **Cross-reference + trailer** (`src/NetPdf.Pdf/Xref/`):
  - Traditional xref table by default for `V1_7` (20-byte entries, single LF terminator per `pdf-spec-notes.md`).
  - Xref streams + object streams (PDF 1.5+) opt-in for `V2_0`.
  - Trailer dictionary with `/Size`, `/Root`, `/Info`, `/ID`. `/ID` derived from SHA-256 of content for determinism.
- **Content stream writer** (`src/NetPdf.Pdf/ContentStreamWriter.cs`):
  - Translates `DisplayCommand` → PDF operators. Operator vocabulary needed in Phase 1 is small (mostly `q`/`Q`, `cm`, `re`, `f`/`S`, `BT`/`ET`, `Tj`/`TJ`, `Tf`, `Td`/`TD`, `Do`); the full vocabulary lands across Phases 1–4.
  - Compresses the resulting stream with `FlateDecode` (`System.IO.Compression.DeflateStream`, fixed compression level 6).
  - Coordinate matrix push: emits `1 0 0 -1 0 H cm` (where H = page height in points after CSS-px → pt conversion) so HTML coordinates flow through directly.
- **Font subsetter** (`src/NetPdf.Pdf/Fonts/Subsetter.cs`):
  - Parses TTF (`loca`, `glyf`, `cmap`, `head`, `hhea`, `hmtx`, `maxp`, `name`, `OS/2`, `post`).
  - Parses OTF (`CFF `, `CFF2`).
  - Re-numbers glyphs to a compact range; tracks usage via `BitArray UsedGlyphs`.
  - Emits new `cmap` (formats 4 + 12 to cover BMP + supplementary planes).
  - Wraps as **Type 0 / CIDFontType2** (TrueType) or **CIDFontType0** (CFF) — uniform handling regardless of script.
  - Emits `ToUnicode` CMap with `bfchar` entries mapping every used glyph → source UTF-16 codepoint(s); ligature glyphs map to multi-character target strings so text extraction recovers original text.
  - 6-uppercase-letter prefix on the BaseFont name, deterministic per `pdf-spec-notes.md` algorithm (SHA-256 of name + glyph bitmap → first 6 hex digits → base-26).
- **Image embedder** (`src/NetPdf.Pdf/Images/`):
  - JPEG: passthrough via `DCTDecode` (no re-encode — significant speed win).
  - PNG / raw RGB(A): `FlateDecode` with `SMask` for alpha. PNG predictor allowed.
  - WebP / AVIF / GIF (first frame): decode via SkiaSharp → re-emit as PNG-style.
- **Catalog + page tree builder** (`src/NetPdf.Pdf/PdfDocument.cs`):
  - High-level builder that owns the indirect-object store, font registry, image cache, and page list.
  - `PdfDocument.Save(Stream output)` runs the full traversal and emits bytes.

### `NetPdf.Text` — text shaping & internationalization

- **HarfBuzz integration** (`src/NetPdf.Text/Shaping/HbShaper.cs`):
  - Wraps HarfBuzzSharp's `Buffer`, `Face`, `Font`, `Shape`.
  - Inputs: font face, UTF-16 text run, language, script, direction, OpenType feature list (default features per HarfBuzz spec).
  - Outputs: array of `ShapedGlyph { ushort GlyphId; float XAdvance; float YAdvance; float XOffset; float YOffset; int Cluster; }`.
- **Bidi (UAX #9)** (`src/NetPdf.Text/Bidi/`):
  - Implement the Unicode Bidirectional Algorithm from the spec (not a port).
  - Embedded UCD `BidiBrackets.txt`, `DerivedBidiClass.txt` as compiled .NET resources via source generator.
  - Validate against UCD reference test data: `BidiTest.txt`, `BidiCharacterTest.txt`. **Both must pass 100%.**
- **Line break (UAX #14)** (`src/NetPdf.Text/LineBreak/`):
  - Implement break-opportunity classification + the pair-table state machine.
  - Embedded UCD `LineBreak.txt` via source generator.
  - Validate against UCD `LineBreakTest.txt`. Must pass 100%.
- **Text segmentation (UAX #29)** (`src/NetPdf.Text/Segmentation/`):
  - Grapheme/word/sentence boundary algorithms.
  - Validate against UCD `GraphemeBreakTest.txt`, `WordBreakTest.txt`.
- **Liang hyphenation** (`src/NetPdf.Text/Hyphenation/`):
  - Implement Liang's TeX hyphenation pattern algorithm.
  - Bundle `en-us` patterns under `Patterns/en-us.dic` (from CTAN, LPPL).
  - Other languages move to `NetPdf.Languages.*` packs in Phase 5.
- **Font registry & fallback** (`src/NetPdf.Text/Fonts/`):
  - `FontFace` represents a parsed font (TTF/OTF metrics + HarfBuzz handle + used-glyph bitmap).
  - `FontRegistry` per-document; LRU `FontCache` process-wide (capacity from `CachePolicy.MaxCachedFontFaces`).
  - **Cross-platform system font enumeration** under `src/NetPdf.Text/Fonts/SystemFonts/`:
    - macOS: `/System/Library/Fonts`, `/Library/Fonts`, `~/Library/Fonts`.
    - Windows: `%WINDIR%\Fonts`.
    - Linux: `/usr/share/fonts`, `/usr/local/share/fonts`, `~/.fonts`, `~/.local/share/fonts`.
    - Alpine/musl: same as Linux.
  - Fallback chain resolution: query primary → CSS-generic (`serif`/`sans-serif`/`monospace`) → system fonts.
- **WOFF / WOFF2 decoders** (`src/NetPdf.Text/Fonts/Woff/`):
  - WOFF: zlib-decompress per-table, reassemble into a TTF/OTF stream.
  - **WOFF2**: Brotli-decompress (via `System.IO.Compression.BrotliStream`) the transformed table directory + `glyf`/`loca`-transform reverse.

### `NetPdf.Paint` skeleton

Just the IR scaffolding for Phase 1; the real painter lands in Phase 3.

- **`DisplayCommand`** (`src/NetPdf.Paint/DisplayCommand.cs`): 64-byte `[StructLayout(LayoutKind.Explicit, Size = 64)]` tagged union. `DisplayCommandKind` enum at offset 0; payload overlays at offset 8.
- **`DisplayList`** (`src/NetPdf.Paint/DisplayList.cs`): `ArrayPool<DisplayCommand>`-backed buffer with side tables (`List<Path>`, `List<TextRun>`, `List<RasterImage>`) referenced by `int` indices.
- **Phase 1 commands needed**: `RectFill`, `TextRun`, `ImageDraw`, `TransformPush`, `TransformPop`, `OpacityPush`, `OpacityPop`. The full vocabulary expands in Phase 3.

### AOT smoke test

`tests/NetPdf.AotSmoke/Program.cs` exercises the Phase 1 PDF writer with a hand-built page: open a `PdfDocument`, add a page with a single text run using a system-resolved font, save to a `byte[]`, assert the byte count is non-zero. CI publishes this with `-p:PublishAot=true` and runs the binary on Linux/macOS/Windows; failure blocks merge.

## Spec references

| Topic | Source |
|---|---|
| PDF 2.0 (normative) | ISO 32000-2:2020 — buy or fetch from PDF Association archive |
| PDF 1.7 (compat target) | https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/PDF32000_2008.pdf |
| PDF errata | https://pdf-issues.pdfa.org/32000-2-2020/ |
| OpenType | https://learn.microsoft.com/typography/opentype/spec/ |
| TrueType | https://developer.apple.com/fonts/TrueType-Reference-Manual/ |
| WOFF | https://www.w3.org/TR/WOFF/ |
| WOFF2 | https://www.w3.org/TR/WOFF2/ |
| HarfBuzz manual | https://harfbuzz.github.io/ |
| UAX #9 (Bidi) | https://www.unicode.org/reports/tr9/ |
| UAX #14 (Line Break) | https://www.unicode.org/reports/tr14/ |
| UAX #29 (Segmentation) | https://www.unicode.org/reports/tr29/ |
| UCD test data | https://www.unicode.org/Public/UCD/latest/ |
| Liang hyphenation | F. M. Liang, "Word Hy-phen-a-tion by Com-pu-ter," 1983 |
| Hyphenation patterns | https://github.com/hyphenation/tex-hyphen |

## Work breakdown (ordered)

| # | Task | Mini-est. | Depends on |
|---|---|---|---|
| 1 | PDF object model + `WriteTo` | 3 d | — |
| 2 | Indirect-object store + xref table writer | 2 d | 1 |
| 3 | Trailer + `/ID` from content SHA-256 | 1 d | 2 |
| 4 | Content stream writer + minimal operator vocab | 3 d | 1 |
| 5 | `DisplayCommand` IR + `DisplayList` buffer | 2 d | — |
| 6 | TTF parser (subset of tables we need) | 3 d | — |
| 7 | OTF/CFF parser | 3 d | 6 |
| 8 | Glyph subsetter (TTF first; CFF later) | 4 d | 6 |
| 9 | ToUnicode CMap generator | 2 d | 8 |
| 10 | Type 0 / CIDFontType2 wrapper for embedded fonts | 2 d | 8, 9 |
| 11 | HarfBuzzSharp wrapper + `HbShaper.Shape` | 3 d | 6 |
| 12 | UAX #9 Bidi + UCD test data validation | 4 d | — |
| 13 | UAX #14 Line break + UCD test validation | 3 d | — |
| 14 | UAX #29 Segmentation + UCD test validation | 2 d | — |
| 15 | Liang hyphenation + en-us bundled patterns | 2 d | 14 |
| 16 | Font registry + cross-platform system font enum | 3 d | 6 |
| 17 | WOFF decoder | 1 d | 6 |
| 18 | WOFF2 decoder (Brotli + glyf/loca transform reverse) | 3 d | 17 |
| 19 | JPEG passthrough image embedder | 1 d | 1 |
| 20 | PNG/Flate image embedder + SMask | 2 d | 19 |
| 21 | WebP/AVIF/GIF via Skia decode | 1 d | 20 |
| 22 | `PdfDocument` high-level builder | 2 d | 1–10, 19–21 |
| 23 | Determinism: byte-hash test harness | 1 d | 22 |
| 24 | AOT smoke test wires up + passes | 1 d | 22 |
| 25 | Performance baseline: BenchmarkDotNet on PDF write | 1 d | 22 |
| 26 | Tag `0.1.0-alpha` + update CHANGELOG | 0.5 d | all |

**Total: ~50 days. With Claude Opus 4.7 high + daily Roland review: 4–6 calendar weeks.**

## Implementation notes

### PDF byte format gotchas (from `pdf-spec-notes.md`)
- xref entries are **exactly 20 bytes** including the LF terminator. Pad with a single space before LF.
- `startxref` points to the byte offset of the `xref` keyword, not the trailer.
- Stream `/Length` must be the byte count *after* compression filters are applied.
- Literal strings escape `(`, `)`, `\`. Hex strings (`<...>`) avoid all escaping concerns — prefer them for any non-ASCII content.

### Font handling
- **Always embed.** Standard 14 fonts are deprecated in PDF 2.0 — never reference Helvetica etc. without embedding.
- **Always emit ToUnicode CMap.** Without it, text in the PDF is not extractable. Even before tagged PDF lands in v1.1, this is required for searchability.
- **Subsetting is mandatory.** A 50 KB font with 12 used glyphs should embed as ~3 KB.
- **Type 0 (CID) fonts for everything**, not simple fonts. Identity-H CMap encoding lets us emit glyph IDs directly; avoids the 256-glyph limit; uniform handling for Latin and CJK.

### Determinism
- Object numbering by traversal order (deterministic given identical input).
- No PRNG anywhere.
- `/CreationDate` and `/ModDate` are optional; when `Features.DeterministicTimestamps` is set, both freeze to a known value.
- Subset font prefix derived deterministically per `pdf-spec-notes.md`.
- Compression: fixed `DeflateStream` level (6).
- CI test: render the same input twice and assert byte-equal output. Add this to `tests/NetPdf.PdfValidation/`.

### AOT discipline
- No `Type.GetType(string)`, no `Activator.CreateInstance`, no reflection-based serialization.
- Source generators (`NetPdf.SourceGen`) for: standard PDF name interning table, UCD tables, font table parsers (optional perf win).
- Test the AOT smoke binary on every CI run.

### Cross-platform native deps
- HarfBuzzSharp ships per-platform native binaries (`HarfBuzzSharp.NativeAssets.Linux/.macOS/.Win32`). Already added to `Directory.Packages.props`.
- SkiaSharp ships native binaries similarly.
- For Alpine (musl), HarfBuzzSharp / SkiaSharp may need explicit `linux-musl-x64` RID. Check at Phase 5 packaging time.

### Text shaping itemization order
Per the plan: **bidi → script → font fallback → style → shape → line break**. Each text run shaped is a maximal contiguous span sharing all five.

## Test plan

| Component | Test type | Location |
|---|---|---|
| PDF object model | Unit | `tests/NetPdf.UnitTests/Pdf/Objects/` — round-trip every type to bytes and back. |
| xref byte exactness | Unit | `tests/NetPdf.UnitTests/Pdf/XrefByteCountTests.cs` — pre-known offset golden file. |
| Content stream operators | Unit | `tests/NetPdf.UnitTests/Pdf/ContentStreamTests.cs` — every operator, byte-equal. |
| Font subsetter round-trip | Unit | `tests/NetPdf.UnitTests/Pdf/Fonts/` — embed subset, extract via FreeType (test-only), verify all used glyphs present. |
| ToUnicode CMap correctness | Unit | Parse generated CMap, verify every used glyph → expected codepoint(s). |
| UAX #9 Bidi | UCD reference | `tests/NetPdf.UnitTests/Text/Bidi/UcdBidiTests.cs` — runs `BidiTest.txt` + `BidiCharacterTest.txt`. **Must pass 100%.** |
| UAX #14 Line break | UCD reference | `LineBreakTest.txt`. **100%.** |
| UAX #29 Segmentation | UCD reference | `GraphemeBreakTest.txt`, `WordBreakTest.txt`. **100%.** |
| HarfBuzz wrapper | Smoke | Latin "fi" ligature input → expect single `fi` glyph in output. Arabic "السلام" → expect joined glyphs. |
| WOFF/WOFF2 decode | Round-trip | Decode a known WOFF2 font, re-parse as TTF, verify metric tables match. |
| Image embedders | Round-trip | Embed JPEG/PNG/WebP, extract via PDFium (test-only), verify pixel-equal. |
| End-to-end "hello world" PDF | Smoke | Single page, single font, "Hello, world." → open in qpdf and verify well-formed; in PDFium and check rendered pixels match expected. |
| Byte determinism | Property | Render same input twice; assert SHA-256 of output bytes equal. |
| AOT smoke | Integration | `dotnet publish tests/NetPdf.AotSmoke -p:PublishAot=true` on Linux/macOS/Windows; run binary; output byte count > 0. |
| Performance baseline | Benchmark | Build a 100-page PDF with 1000 text runs each: target < 500 ms wall on commodity desktop. |

## Exit criteria

Phase 1 is complete when:

1. ✅ All Phase 1 task tests pass.
2. ✅ UCD reference tests for UAX #9, #14, #29 pass 100%.
3. ✅ Byte-determinism test passes (same input → same SHA-256 output).
4. ✅ AOT smoke test publishes and runs successfully on Linux/macOS/Windows.
5. ✅ Sample CLI can be modified to call directly into `PdfDocument` (bypass HTML pipeline) and produce a valid PDF that opens in Acrobat Reader, Firefox PDF.js, Chrome PDF viewer, macOS Preview.
6. ✅ qpdf passes `--check` on a representative output.
7. ✅ A WOFF2 font (e.g., Inter from Google Fonts) loads, subsets, and embeds end-to-end.
8. ✅ Performance baseline meets target (100-page benchmark < 500 ms).
9. ✅ CHANGELOG updated, `0.1.0-alpha` tagged.

## Common pitfalls

- **Off-by-one xref offsets.** Track byte offsets *as you write*. Don't compute them from file size at the end.
- **CRLF in xref entries on Windows.** Force LF terminators in xref output regardless of platform.
- **Forgetting ToUnicode.** A PDF without ToUnicode is technically valid but uncopyable. Test text extraction with pdftotext or PDFium on every output.
- **Standard 14 font confusion.** Tempting to skip embedding Helvetica because "every viewer has it" — don't. Always embed.
- **CIDToGIDMap missing for subsets.** TrueType subsets need `/CIDToGIDMap /Identity` (or a stream) after re-numbering. Without it, glyphs render as `.notdef`.
- **HarfBuzz glyph IDs ≠ Unicode codepoints.** Don't conflate. Always go through the font's cmap.
- **Bidi reordering happens before shaping.** The shaper takes a single direction; ensure the run is unidirectional before calling it.
- **WOFF2 brotli context.** WOFF2's brotli stream has a specific dictionary; `BrotliStream` defaults work but verify the WOFF2-specific `transform` flags are honored.
- **AOT-incompatible reflection.** Some libraries (System.Text.Json default mode) use reflection. Use source generators or skip the library.
- **macOS/Linux file-system case sensitivity.** Font filenames may differ in case across platforms. Normalize lookups.

## Hand-off to Phase 2

State of the repo at end of Phase 1:
- `NetPdf.Pdf` is functional. `PdfDocument` builder produces well-formed, deterministic PDF bytes.
- `NetPdf.Text` is functional. Bidi, line breaking, segmentation, hyphenation work. HarfBuzz shapes correctly. WOFF2 loads.
- `NetPdf.Paint` has the `DisplayCommand` IR but only Phase-1 commands are emitted.
- `HtmlPdf.Convert` still throws `NotImplementedException` — it's the HTML pipeline that hasn't been built yet.
- AOT-publishable. Performance baseline locked.

Phase 2 begins by wiring AngleSharp + AngleSharp.Css and translating an HTML+CSS document into a styled box tree that `NetPdf.Layout` (in Phase 3) will consume.
