# PDF Spec Notes & Errata Index

This document is the single source of truth for the PDF spec interpretation choices NetPdf has made. ISO 32000-2:2020 (PDF 2.0) is the **normative reference**; PDF 1.7-compatible bytes are the **default emission** for viewer compatibility.

When two parts of the spec are in tension, when the spec is ambiguous, or when the PDF Association has issued an erratum, the resolution is recorded here.

---

## Primary references

- **ISO 32000-2:2020** — The PDF 2.0 specification. Available free from the PDF Association.
- **ISO 32000-1:2008** — The PDF 1.7 specification. Adobe's [royalty-free public patent license](https://www.adobe.com/pdf/pdfs/ISO32000-1PublicPatentLicense.pdf) covers this.
- **PDF Association Errata** — https://pdf-issues.pdfa.org/32000-2-2020/
- **Adobe PDF 1.7 archive** — https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/PDF32000_2008.pdf
- **PDF/A** — ISO 19005 series.
- **PDF/UA** — ISO 14289-1.

---

## File structure choices

### Header
- Default: `%PDF-1.7\n%âãÏÓ\n` (the 4-byte high-bit comment is a binary marker per §7.5.2 to indicate binary content; aids transmission integrity).
- When `EmittedPdfVersion = V2_0`: header becomes `%PDF-2.0\n` with the same binary marker comment.

### EOL convention
- Single LF (`\n`) throughout, including in xref entries. PDF spec §7.5.4 allows LF, CR, or CRLF, but xref entry rows must be exactly 20 bytes; LF is one byte and avoids the CRLF padding issue.

### xref padding
- Every xref entry: `%010d %05d n \n` (10-digit offset + space + 5-digit gen + space + `n` + space + LF) = exactly 20 bytes.
- The trailing space before LF is the documented pad per §7.5.4.

### startxref
- Points to the byte offset of the `xref` keyword, not the `trailer` keyword. Confirmed from spec §7.5.5 example.

---

## Object encoding

### Strings
- Literal strings `(...)` for ASCII-only content.
- Hex strings `<...>` for any content with non-ASCII bytes or where escaping is otherwise complex.
- Within literal strings, escape `(`, `)`, `\` per §7.3.4.2.

### Names
- Names are encoded with `#XX` hex escaping for any byte outside `!`–`~` excluding whitespace and delimiters. Per §7.3.5.

### Indirect references
- Format `<num> 0 R` — generation number always 0 in our writer (we never produce incremental updates in v1).

---

## Font choices

### Type 0 (CID) for everything
- Even ASCII-only text uses Type 0 CID fonts in NetPdf. Reasons:
  - Uniform handling across scripts.
  - Identity-H CMap encoding allows direct glyph-ID emission, bypassing single-byte encoding limitations.
  - Avoids the deprecated Standard 14 fonts entirely.

### Subsetting
- 6-uppercase-letter prefix on font name, per §9.6.4 convention. NetPdf's prefix algorithm: take the SHA-256 of the font's BaseFont name + used-glyphs bitmap, hex-encode, take first 6 hex digits, convert to uppercase letters via `'A' + (digit % 26)`.
- This keeps the prefix deterministic for byte-equal output across runs.

### ToUnicode CMap
- Generated for every embedded font, even before tagged PDF lands.
- `bfchar` entries map every used glyph ID back to its source UTF-16 codepoint(s).
- For ligature glyphs (e.g., `fi` glyph), the `bfchar` mapping uses a multi-character target string so text extraction recovers the original characters.

### CIDToGIDMap
- TrueType subsets emit `/CIDToGIDMap /Identity` after re-numbering glyphs to a compact range (CID 0..N-1 maps to internal glyph 0..N-1).

---

## Coordinate system

- PDF: origin bottom-left, units = 1/72 inch (1 pt).
- HTML/CSS: origin top-left, units = CSS pixels (1 px = 0.75 pt at default 96 DPI).
- **NetPdf layout works exclusively in CSS pixels.** Conversion happens in `ContentStreamWriter.PushPageMatrix()` which emits `1 0 0 -1 0 H cm` (where H is page height in points after px→pt conversion).
- All `DisplayCommand` coordinates are in CSS pixels; PDF emission scales by `0.75` and applies the y-flip.

---

## Compression

### Content streams
- `FlateDecode` (zlib/deflate). Default compression level: 6 (balanced).
- For deterministic output, we use a fixed compression level and a known deflate implementation (`System.IO.Compression.DeflateStream`).

### Images
- JPEG: passthrough via `DCTDecode`. The original bytes are embedded with no recompression. Major speed and quality win.
- PNG / raw RGB(A): `FlateDecode`. PNG-style predictor allowed.
- WebP / AVIF: decoded via SkiaSharp into raw pixels, then `FlateDecode`-emitted.

### Object streams (PDF 1.5+)
- Disabled by default in v1.7-compatible output.
- Enabled when `EmittedPdfVersion = V2_0`. Reduces file size; requires PDF 1.5+ readers.

---

## Determinism rules

- Object numbering: by traversal order (not insertion order — traversal is reproducible).
- `/CreationDate` and `/ModDate`: optional. When `Features.DeterministicTimestamps` is set, both are written as a fixed string `"D:20260101000000Z"` (or another fixed date the user specifies).
- `/ID` array: derived from a SHA-256 of the entire content (excluding `/ID` itself). Stable for byte-equal input.
- No PRNG anywhere in the writer.
- Font subsetter prefix: deterministic per the algorithm above.

---

## Areas of spec ambiguity & resolved interpretations

### §7.5.4 xref entries — trailing whitespace
> "Each entry shall be exactly 20 bytes long including the end-of-line marker."

Resolution: We pad with a single space before the LF terminator. Some old viewers tolerate CR+LF; we always emit LF.

### §9.10.2 ToUnicode CMap — coverage of decorative glyphs
> "These maps are used during text searching and when a PDF processor extracts text from a document."

Resolution: Decorative-only glyphs (e.g., flourishes added by ligature substitutions) get an `<>` empty target in `bfchar`. Some tools emit `<FFFD>` (replacement character) instead; we prefer empty to avoid polluting extracted text.

### §14.8.2 Tagged PDF — required when?
> "A PDF document that conforms to PDF/UA shall be a tagged PDF."

Resolution: Tagged PDF is **post-v1**. v1 output is **not tagged**. Users who need tagged output wait for v1.1.

### Dealing with inconsistencies between PDF 1.7 and 2.0
When ISO 32000-2 and ISO 32000-1 differ on a non-deprecated feature, NetPdf follows ISO 32000-2 (the newer, clearer text). When emitting in `V1_7` mode, we restrict ourselves to features valid in 1.7. Examples:
- AES-256 encryption: 2.0 only. v1.7 mode falls back to AES-128.
- Object streams: 1.5+ but a 1.7 reader supports them; we still skip them by default for max compat.

---

## Known PDF Association errata applied

(Index will grow as we hit errata during implementation.)

| Erratum ID | Subject | Resolution |
|---|---|---|
| (none yet) | | |

---

Last updated: 2026-04-30 (Phase 0).
