# NetPdf

A pure C# / .NET HTML+CSS-to-PDF rendering engine. **Free, open-source, Apache-2.0.**

NetPdf is a true paged-media renderer written from scratch in managed code. It does **not** wrap a browser engine, **not** spawn a subprocess at render time, **not** depend on revenue-capped or copyleft libraries. It converts a single HTML+CSS string into a deterministic PDF byte stream optimized for documents — invoices, statements, contracts, reports, certificates, catalogs.

**Native dependencies, honestly listed.** NetPdf is "pure managed code" at the engine layer but ships with two permissive-licensed native bundles that Phase 1's tests prove are AOT-clean and reflection-free:

- **HarfBuzzSharp** (MIT) wraps **HarfBuzz** (Old MIT-style). Drives OpenType shaping (kerning, ligatures, RTL, CJK).
- **SkiaSharp** (MIT) wraps **Skia** (BSD-3). Used **only** for image decode (WebP / AVIF / GIF) and the post-Phase-3 raster fallback for filters / blurred shadows / conic gradients. Not a primary graphics path.

Both are loaded as in-process libraries via the official `*.NativeAssets.{Linux,macOS,Win32}` packages — no `Process.Start`, no executable spawning, no IPC. AOT publish + execution is verified on every commit via `scripts/aot-parity.sh`, which asserts the published native binary produces byte-identical PDF output to the JIT path.

> **Status:** Phases 1–4 ✅ — **`0.9.0-rc1`** (release candidate; Phase 5 packaging in progress toward `1.0.0`). Phase 1 (`0.1.0-alpha`) shipped the PDF writer + text foundation: deterministic PDF 1.7 bytes, embedded subsetted fonts via WOFF/WOFF2, JPEG/PNG/WebP/AVIF/GIF embedders, full UAX #9/#14/#29 text shaping, AOT-clean with enforced JIT/AOT byte-parity. Phase 2 (`0.3.0-alpha`) shipped the internal HTML parsing + CSS cascade + computed-value + box-tree pipeline. **Phase 3 (`0.7.0-beta`) wired the facade end-to-end**: `HtmlPdf.Convert(html)` returns real PDF bytes — fragmentainer-aware layout (block / inline / flex / grid / table / multicol / absolute), the pagination optimizer, paged media (`@page` + the 16 margin boxes + generated content), and text shaping + painting + image embedding. **Phase 4 (`0.9.0-rc1`) is visual parity**: gradients, box/text shadows, 2D transforms, CSS filters, borders + clip-path, masks/blend modes, Link annotations + outlines, and static SVG. All six W3C conformance exit criteria are met (CSS 2.2 / Flexbox / Grid / Fragmentation / Backgrounds & Borders / Transforms — see the [pass-rate table](#w3c-conformance-pass-rates) below); perf + memory are signed off measured-with-documented-residuals. **Phase 5 (packaging) is now closing out toward `1.0.0`**: cross-platform CI, language packs, docs site, package validation, corpus acceptance, and the NuGet publish pipeline. Git tags are created by the maintainer; the repository is private until the v1.0 launch.

## Why another HTML-to-PDF library?

Every existing free option for .NET fails one or more critical criteria:

| Library | Issue |
|---|---|
| HtmlRenderer.PdfSharp | CSS 2 only, abandoned |
| DinkToPdf / wkhtmltopdf | Abandoned, unpatched CVE-2022-35583, obsolete WebKit |
| QuestPDF | MIT under $1M revenue, commercial above |
| SixLabors.ImageSharp / .Fonts | Same revenue clause |
| iText pdfHTML | AGPL or commercial |
| Syncfusion / IronPDF / Aspose / Spire | Commercial |
| PuppeteerSharp / PlaywrightSharp | Heavy; embeds Chromium |

NetPdf fills the gap: **truly free, truly forever, truly open source.**

## Direction

Closer to **Prince**, **WeasyPrint**, **OpenHTMLtoPDF** than to Playwright/Puppeteer/wkhtmltopdf. Layout-engine first, paint-engine second, PDF byte-writer third — all built from scratch using public W3C / Unicode / ISO specifications under a clean-room policy.

## Public API surface (frozen for v1)

> ✅ **At `0.7.0-beta` the facade is wired end-to-end** — every overload below returns real PDF bytes for static documents (the Phase 1–3 feature set: layout + pagination + paged media + text + images + basic paint). Phase 4 visual-parity hardening (gradients, shadows, filters, full SVG) is the remaining work before `1.0.0`; unsupported features emit structured diagnostics rather than throwing. The shape below is the frozen v1 contract.

```csharp
using NetPdf;

// One-liner — returns PDF bytes at 0.7.0-beta
var pdf = HtmlPdf.Convert("<h1>Invoice #1234</h1><p>Hello world.</p>");
File.WriteAllBytes("out.pdf", pdf);

// With options
var pdf = HtmlPdf.Convert(html, new HtmlPdfOptions
{
    BaseUri = new Uri("file:///app/templates/"),
    PageSize = PageSize.Letter,
    PrintBackgrounds = true,
});

// Async streaming
await using var fs = File.Create("report.pdf");
await HtmlPdf.ConvertAsync(html, fs, new HtmlPdfOptions { PreferCssPageSize = true });

// Diagnostic mode
var result = HtmlPdf.ConvertDetailed(html);
foreach (var d in result.Warnings) Console.WriteLine($"{d.Code}: {d.Message}");

// Version string:
Console.WriteLine(HtmlPdf.Version); // -> "0.7.0-beta+<commit-sha>"
```

See [`docs/compatibility-matrix.md`](docs/compatibility-matrix.md) for the supported / not-supported feature list.
See [`docs/diagnostics-codes.md`](docs/diagnostics-codes.md) for the stable diagnostic code registry.
See [`docs/phases/`](docs/phases/) for per-phase execution guides — what to build, in what order, with exit criteria.

## What's actually shipped (current: `0.9.0-rc1`)

These columns are the honest answer to "what does NetPdf do today?" `HtmlPdf.Convert(html)` runs the full HTML → CSS → layout → paginate → paint → PDF pipeline. The Phase 1–3 feature set is live (public since `0.7.0-beta`), and **Phase 4 visual parity — gradients, shadows, filters, full SVG — is feature-complete and shipped at `0.9.0-rc1`** (tagged; the only remaining Phase-4 item is committing the CI visual-regression reference images). The remaining ❌ below is post-v1 tagged-PDF emission.

| Capability | Implemented internally now | Reachable through public `HtmlPdf` API now | Wired in |
|---|:---:|:---:|---|
| Deterministic PDF 1.7 byte writer (objects, xref, trailer auto-derived `/ID`, preflight cycle + nested-stream rejection) | ✅ | ✅ | Phase 1 internal; public ✅ at 0.7.0-beta |
| Font pipeline: parsing for TTF/OTF/CFF; **embedding for TTF only** (glyph subset → ToUnicode CMap → Type 0/CIDFontType2 wrapper, deterministic 6-letter prefix). CFF subsetting + `FontFile3`/`CIDFontType0C` embedding deferred to a Phase 1.x follow-up. | 🧪 | ✅ | Phase 1 internal; public ✅ at 0.7.0-beta |
| WOFF 1.0 (zlib) + WOFF 2.0 (Brotli + glyf/loca transform reverse) | ✅ | ✅ | Phase 1 internal; public ✅ at 0.7.0-beta |
| OpenType shaping via HarfBuzzSharp (kerning, ligatures, RTL, CJK) | ✅ | ✅ | Phase 1 internal; public ✅ at 0.7.0-beta |
| UAX #9 Bidi (100% UCD), UAX #14 Line Break (99.952% UCD), UAX #29 grapheme clusters (100% UCD) | ✅ | ✅ | Phase 1 internal; public ✅ at 0.7.0-beta |
| Liang hyphenation (en-US bundled; other languages ship as `NetPdf.Languages.*` packs at v1.0+) | ✅ | ✅ | Phase 1 internal; public ✅ at 0.7.0-beta |
| Image embedders (JPEG passthrough, PNG 4 paths inc. RGBA `/SMask`, WebP/AVIF/GIF via Skia raster) with content-hash dedup | ✅ | ✅ | Phase 1 internal; public ✅ at 0.7.0-beta |
| HTML parsing host (AngleSharp, no scripting) + `<script>` / `javascript:` URL stripping with diagnostics | ✅ | ✅ | Phase 2 internal; public ✅ at 0.7.0-beta |
| CSS cascade + `var()` substitution + `calc()`/`min()`/`max()`/`clamp()`/`abs()`/`sign()` (subset: absolute units fully reduce; context-relative units — `em`/`rem`/`vh`/`vw`/etc. — defer to Phase 3 typed pipeline) | ✅ | ✅ | Phase 2 internal; public ✅ at 0.7.0-beta |
| Box-tree generation (block/inline dispatch, anonymous-block insertion, table fixup, `::before`/`::after`/`::marker`/`::first-line`/`::first-letter` materialization) | ✅ | ✅ | Phase 2 internal; public ✅ at 0.7.0-beta |
| Semantic-tree generation (PDF/UA-aligned roles for v1.1 tagged-PDF) | ✅ | ❌ | Phase 2 internal; semantic IR built, tagged-PDF emission v1.1 |
| Diagnostics emission for unsupported / silently-dropped features (HTML-* + CSS-* codes; public sink wiring) | ✅ | ✅ | Phase 2 internal; public ✅ at 0.7.0-beta |
| AOT-clean publish + JIT/AOT byte-parity gate | ✅ | n/a (gate, not API) | Phase 1 ships `scripts/aot-parity.sh` |
| Determinism harness (per-platform pinned SHA-256, 72 property tests) | ✅ | n/a (gate, not API) | Phase 1 |
| Performance baseline + +25%-tolerance regression gate | ✅ | n/a (gate, not API) | Phase 1 ships `scripts/benchmark-gate.sh` |
| `HtmlPdf.Convert(html)` — public facade end-to-end | ✅ | ✅ | Phase 5 wired at `0.7.0-beta` |
| Layout (block/inline/flex/grid/table/multicol/absolute) + fragmentainer-aware pagination | ✅ | ✅ | Phase 3 (`0.7.0-beta`) |
| Visual-parity hardening (filters via Skia raster fallback, gradients, shadows, transforms, borders/clip-path, opacity, full SVG parts 1–13) | ✅ | ✅ | Phase 4; public ✅ at `0.9.0-rc1` |

## v1 capability targets (when `1.0.0` ships)

Below is the v1 contract — what `HtmlPdf.Convert(html)` supports. The Phase 1–3 rows are live since `0.7.0-beta`; the Phase 4 visual-parity rows (gradients, shadows, filters, SVG) are live since `0.9.0-rc1`, on the way to `1.0.0`.

**Targeted for v1 support:**
- Block & inline layout, lists, tables (with `<thead>`/`<tfoot>` repetition across pages)
- Web fonts (`@font-face` with TTF/OTF/WOFF/WOFF2), font fallback, OpenType shaping, ligatures, kerning, RTL, CJK
- Images (JPEG passthrough, PNG/Flate, WebP/AVIF via Skia decode)
- Static SVG (shapes, paths, gradients, transforms, text)
- Custom properties (`--*`), `var()`, `calc()`/`min()`/`max()`/`clamp()`
- `@media print`, `@page` (size, margins, margin boxes), `break-*`, `widows`, `orphans`
- `position: absolute`/`fixed` (fixed repeats per page)
- 2D transforms, opacity, gradients, box/text shadows
- Flexbox L1, CSS Grid Level 1
- Filters via subtree raster fallback (blur, drop-shadow, brightness, contrast, etc.)

**Out of scope for v1 (parsed without error; emits structured diagnostics):**
- JavaScript, canvas, `<video>`, `<audio>`, service workers
- 3D transforms, sticky positioning, animations/transitions
- CSS Grid Level 2 (subgrid), container queries, `:has()` rendering, anchor positioning

**Post-v1 roadmap:** tagged PDF (PDF/UA-1) → PDF/A-3u → Grid L2 → container queries → `:has()` → anchor positioning.

## W3C conformance pass-rates

NetPdf ships a **curated W3C conformance suite** (`tests/NetPdf.W3cConformance/`) — each
case is an HTML fragment plus the spec-correct border-box geometry the layout must
produce, measured against the internal layout pipeline. It is a clean-room assertion
suite, not vendored web-platform-tests (which fits the deterministic + AOT ethos). The
numbers below are measured by the `ConformanceGates` tests and gated per-case in CI —
a regression or a silently-closed gap turns the build red.

**These are LAYOUT-conformance rates** — the harness asserts spec-correct box geometry
(the internal layout pipeline has no PDF content-stream reader). Paint fidelity — that
backgrounds actually fill, borders and `border-radius` render, transforms visually apply,
gradients/shadows/filters/SVG look right — is a *separate* axis covered by the
visual-regression corpus (`tests/NetPdf.RenderingCorpus/`, per-page pixel diff vs Chrome).
The two rows marked † measure the layout-observable subset of their module (e.g. borders
inset the content box; a `transform` must **not** move the layout box) rather than the
module's full visual behavior.

| Category | Spec | Pass-rate | Target |
|---|---|---|---|
| CSS 2.2 layout | CSS 2.1/2.2 §8–10 (block, box model, margins, floats, sizing) | **100%** (30/30) | ≥ 90% |
| Fragmentation | CSS Fragmentation L3 (breaks, widows/orphans) | **100%** (12/12) | ≥ 80% |
| Flexbox | CSS Flexbox L1 | **100%** (19/19) | ≥ 85% |
| Grid | CSS Grid L1 | **100%** (15/15) | ≥ 70% |
| Backgrounds & Borders † | CSS Backgrounds & Borders L3 — layout-observable subset (border insets, box-sizing; background/radius are paint-only ⇒ must not affect layout) | **100%** (6/6) | ≥ 90% |
| Transforms † | CSS Transforms L1 — layout-observable subset (paint-only; layout box unaffected) | **100%** (6/6) | ≥ 85% |

**All six layout-conformance targets are met — 88/88 curated cases pass** (measured 2026-07-05). See
[`tests/NetPdf.W3cConformance/README.md`](tests/NetPdf.W3cConformance/README.md) for the
per-case breakdown, the per-case regression gate, and why the suite is curated rather
than vendored. Paint-fidelity beyond layout geometry (gradients, shadows, filters, SVG)
is covered separately by the visual-regression corpus (`tests/NetPdf.RenderingCorpus/`).

## Performance gates (enforced in CI)

- **3-page invoice ≤ 200 ms p50** on commodity desktop.
- **20-page report ≤ 1.5 s p50.**
- **Linear memory growth** with page count.
- **No process spawning at render time.**
- **Native AOT compatible.**
- **Deterministic output:** identical input → identical bytes.

## Running NetPdf on untrusted HTML

> **See also:** the full threat model + class-by-class analysis + hardening roadmap in
> [`docs/security/security-hardening-plan.md`](https://github.com/raroche/NetPdf/blob/main/docs/security/security-hardening-plan.md),
> and how to report a vulnerability in [`SECURITY.md`](https://github.com/raroche/NetPdf/blob/main/SECURITY.md).
> (Absolute links so they resolve from the packaged NuGet README as well as on GitHub.)

Library-level guards (Phases A–D security hardening) protect against the
known HTML-to-PDF attack classes documented in the threat-model corpus
(SSRF tags, CSS SSRF, local-file read, SVG animation tricks, data-URI
polyglots, resource bombs, image / font decoder bugs, PDF active content,
diagnostic log injection). They do NOT replace OS-level isolation. If
you run NetPdf in an API or web service that accepts customer-supplied
HTML, follow this checklist:

### 1. Pin `SecurityPolicy.UntrustedHtml`

```csharp
var options = new HtmlPdfOptions
{
    SecurityPolicy = SecurityPolicy.UntrustedHtml, // Phase D D-2: no file/http/data, tight budgets
    ResourceLoader = null,                         // no ambient network/file fetch at all
    Timeout = TimeSpan.FromSeconds(10),            // bound a pathological render
    Diagnostics = sink,
    // BaseUri: leave null — nothing to resolve relative refs against
};
var pdf = HtmlPdf.Convert(untrustedHtml, options);
// For caller cancellation, use the async overload:
//   await HtmlPdf.ConvertAsync(untrustedHtml, options, cancellationToken);
```

`UntrustedHtml` disables every URL-fetching surface (file://, http(s),
data:) + tightens per-render fetch budgets. Leaving `ResourceLoader`
null means no loader is even available, and `Timeout` bounds a
pathological render (the synchronous `Convert` takes no token —
external cancellation is available on the `ConvertAsync` overloads).
Use `TrustedTemplate` only for HTML you authored;
the default `SafeDefault` is a middle-ground for desktop / batch use
cases.

### 2. Process / container isolation

Run the conversion worker as a low-privilege user in a
container / process boundary:

- **Drop privileges:** non-root user, no `CAP_*` capabilities, `umask 077`.
- **Read-only root filesystem:** mount `/tmp` as `tmpfs` with `noexec`.
- **No ambient network access:** if Phase 5 enables outbound fetches,
  use an egress proxy + outbound allowlist + block route to
  `169.254.169.254` (AWS / GCE / Azure / Alibaba IMDS),
  `127.0.0.0/8`, `10/8`, `172.16-31/12`, `192.168/16`,
  `fc00::/7`, `fe80::/10`. NetPdf's `UriSafetyValidator` does this at
  the application layer; route-level blocking is defense in depth.
- **No ambient secrets:** no AWS profile, no GCP service account, no
  `.kube/config`, no SSH keys, no `/proc/.../environ` reachable from
  the renderer's user.
- **CPU + memory limits:** `cgroups` / `--memory` / `--cpus`. NetPdf's
  per-render caps (DOM size, CSS rule count, calc body length, image /
  font validators) bound the typical worst case, but a kernel-level
  limit is the final backstop.
- **No shell execution:** the worker never exec's a subprocess.
  NetPdf does not spawn processes, but third-party deps in your
  service might.

### 3. Vulnerability scanning

- Run `dotnet list package --vulnerable --include-transitive` on every
  build. NetPdf vendors only AngleSharp + AngleSharp.Css + HarfBuzzSharp
  + SkiaSharp; none has a current CVE in the v1 dependency ranges.
- Subscribe to security advisories for HarfBuzz (CVE-2024-56732 class)
  + libwebp (CVE-2023-4863 class) + libjpeg-turbo + libpng. NetPdf's
  pre-decode validators (Phase C C-1, Phase D D-4) bound the attack
  surface but don't fix decoder bugs.
- NetPdf keeps a deliberately small, vetted dependency set (a clean-room
  policy documented in [`docs/legal/dependency-dossier.md`](https://github.com/raroche/NetPdf/blob/main/docs/legal/dependency-dossier.md));
  adding a dependency requires a reviewed dossier entry. A compile-time
  banned-API analyzer to *enforce* the allowlist automatically is planned
  (security task **SEC-10**) but is not yet shipped — do not rely on it as
  a build-time guard in your service today.

### 4. Resource allowlist

- If Phase 5's resource loader is enabled in your service, configure
  `SecurityPolicy.AllowedHosts` to an explicit allowlist of domains
  your templates are permitted to fetch from. Wildcards
  (`*.cdn.example.com`) match a single subdomain level.
- Set `MaxResourcesPerRender` / `MaxTotalResourceBytes` /
  `MaxRedirectHops` lower than the defaults if your use case allows.

### 5. Output handling

- The conversion produces deterministic bytes (same input → same PDF).
  This means you can cache results by input hash without worrying
  about timestamp-based cache poisoning.
- The PDF preflight (Phase D D-6) rejects every active-content key
  (`/OpenAction`, `/AA`, `/JavaScript`, `/Launch`, `/SubmitForm`,
  `/ImportData`, `/GoToR`, `/GoToE`, `/EmbeddedFile`, `/EmbeddedFiles`,
  `/RichMedia`) **unconditionally**, before bytes are written — there is
  no opt-in for these in v1.
- `<a href>` hyperlinks **are** emitted as `/URI` Link annotations
  (shipped in Phase 4), but only for `http` / `https` / `mailto` schemes
  (`LinkUriPolicy`); `javascript:` / `file:` / `data:` / other schemes are
  dropped with a `LINK-URI-UNSUPPORTED-001` diagnostic, so a dangerous
  link scheme can never reach the PDF. Note the *value* of a benign
  `http(s)` link is still attacker-controlled template content, so a
  clickable link in the output is expected — if your PDFs are shown to
  end users and you do not want any attacker-supplied links, strip
  `<a href>` before conversion or post-process the annotations.
- Set `HtmlPdfOptions.Title` / `Author` / `Subject` to constants you
  control, not to attacker-supplied template content. NetPdf
  sanitizes these (Phase C C-3) but a constant is one less surface to
  worry about.

### 6. Threat-model corpus

The exploit-corpus regression tests in
[`tests/NetPdf.UnitTests/Phase2/PhaseDSecurityHardeningTests.cs`](tests/NetPdf.UnitTests/Phase2/PhaseDSecurityHardeningTests.cs)
under the `D8_corpus_*` prefix pin one test per known attack class.
When adding a feature that touches input parsing or resource loading,
add a corresponding `D8_corpus_*` test that verifies the new feature
doesn't regress the defense.

## License

[Apache-2.0](LICENSE).

Third-party attributions: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
