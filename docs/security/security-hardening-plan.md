# NetPdf — Security Threat Model & Hardening Plan

> **Status:** living document. First cut written 2026-07-01 against `0.9.0-rc1`, from a four-part
> internal audit (resource-loading/SSRF, XML/XXE, native font/image + DoS, PDF-output + tests) cross-checked
> against the published HTML-to-PDF vulnerability literature (see [References](#references)).
> **Scope:** the threat model for the NetPdf rendering engine, an inventory of the defenses that already
> ship, the residual gaps, and an **ordered remediation task series** to work one PR at a time (the project
> cadence). No code is changed by this document — it is the plan.

This is the security-critical companion to the phase docs. When a task here lands, tick it and cross-link the PR.

---

## 1. Why HTML-to-PDF is a high-value target

An HTML-to-PDF converter takes **attacker-influenced markup** and renders it **server-side**, often with
network + filesystem reach. The public literature shows the same handful of bug classes recur across every
engine (wkhtmltopdf, dompdf, EvoPDF, Apryse, Stirling-PDF, pdfmake, jsPDF, …). The canonical classes:

| # | Class | Mechanism | Representative cases |
|---|---|---|---|
| V1 | **SSRF** | Any resource fetch (`<img>`, `<iframe>`, `<object>`, `<embed>`, `<link>`, `<script>`, `<base>`, `<meta refresh>`, `<input type=image>`, CSS `url()`/`@import`/`@font-face`, SVG `<image>`/`<use>`) reaching internal services + cloud metadata `169.254.169.254` → cloud credentials | Stirling-PDF GHSA-xw8v (8.6), pdfmake CVE-2026-26801, EvoPDF |
| V2 | **Local file disclosure** | `file://` (also `gopher:`/`dict:`) in the same vectors → `/etc/passwd` embedded in the PDF | CVE-2025-55853, dompdf |
| V3 | **XXE** | SVG/XML external entities → file read + SSRF; billion-laughs entity expansion → DoS | classic |
| V4 | **RCE via `@font-face`** | Malicious/oversized font → font-parser bug, or cache-to-webroot with a preserved `.php` extension | dompdf CVE-2023-23924 (9.8), positive.security |
| V5 | **RCE via native decoder** | Memory-safety bug in a font/image parser reached with crafted bytes | neodyme, libwebp CVE-2023-4863 |
| V6 | **RCE via JS engine / arg injection** | V8 in an embedded browser; user input reaching a CLI (`--renderer-cmd-prefix`) | EO.Pdf, Apryse |
| V7 | **DoS / resource exhaustion** | Infinite JS loops, entity expansion, decompression/font bombs, unbounded fetch counts, huge/deeply-nested docs, PDF amplification | portswigger, all engines |
| V8 | **Malicious PDF output** | `javascript:`/`file:` link URIs, `/Launch`, `/JavaScript`, `/OpenAction`, `/SubmitForm`, `/EmbeddedFile`, string-injection into PDF syntax | — |
| V9 | **Deserialization / path traversal** | Object deserialization gadgets; cached-file naming traversal | Synacktiv, jsPDF CVE-2025-68428 |

---

## 2. Use cases & trust model

NetPdf is a **library**, so the trust boundary depends on how it is embedded. Three canonical deployments:

1. **Trusted-template rendering** (invoices from your own templates + your data). Input is trusted-ish; the
   data may still be user-influenced (an invoice "notes" field). → `SecurityPolicy.TrustedTemplate`.
2. **API accepting arbitrary customer HTML → returns a PDF** (multi-tenant SaaS, "paste HTML, get PDF").
   **This is the hostile case** the rest of this document optimizes for. Input is fully attacker-controlled.
   → `SecurityPolicy.UntrustedHtml` + no ambient `ResourceLoader` + a render `Timeout` + OS-level isolation.
3. **Desktop / batch** (a user converting their own files with `file://` assets). → `SafeDefault` with a
   scoped `BaseUri`.

**Golden rule for use case #2 (untrusted HTML):**

```csharp
var pdf = HtmlPdf.Convert(untrustedHtml, new HtmlPdfOptions
{
    SecurityPolicy = SecurityPolicy.UntrustedHtml, // no http/https, no file:, no data:, tight budgets
    ResourceLoader = null,                         // no ambient network/file fetch at all
    Timeout = TimeSpan.FromSeconds(10),            // bound render time (also honors a CancellationToken)
    // BaseUri: leave null — nothing to resolve relative refs against
});
```

Even with all in-process guards, deployment #2 SHOULD additionally run in a **network-egress-restricted,
filesystem-restricted, memory/CPU-capped container** (defense-in-depth; see task **SEC-11**). The library cannot
substitute for OS-level isolation against native-decoder 0-days (V5).

---

## 3. NetPdf's structural advantages (classes eliminated *by design*)

NetPdf's architecture removes several whole bug classes that plague browser-backed converters. These are the
project's biggest security wins and MUST be preserved:

- **No JavaScript engine, no browser.** Pure C# layout/paint. → **eliminates V6 (browser JS-engine RCE, e.g. the V8 CVEs), server-side XSS,
  JS-driven SSRF, and JS infinite-loop DoS.** `<script>` and `on*` event-handler attributes are both stripped
  at parse (`HTML-SCRIPT-IGNORED-001` / `HTML-EVENT-HANDLER-IGNORED-001`), and no PDF reader would dispatch them anyway.
- **No process spawning at render time** (a hard CLAUDE.md rule; verified — zero `Process.Start`). →
  **eliminates the Apryse-class argument-injection RCE (V6).**
- **AOT-clean: no reflection-driven type activation / plugin loading / deserialization over untrusted input, no `Activator.CreateInstance`, no runtime deserialization.** (Fixed reflection over trusted, compile-time-known targets — reading the assembly's own `AssemblyInformationalVersion`, loading embedded hyphenation resources — is fine; the invariant is that *no attacker-controlled input selects a type, assembly, or serialized graph*.) → **eliminates the
  deserialization gadget class (V9).**
- **We own the PDF byte writer** and validate the object graph before emit. → tight control over **V8 (malicious PDF output)**.
- **Clean-room, minimal vetted dependency set** (AngleSharp, AngleSharp.Css, HarfBuzzSharp, SkiaSharp). →
  smaller supply-chain surface, but see V5 / task **SEC-10** (native deps are the residual RCE surface).

---

## 4. Current defenses (what already ships in `0.9.0-rc1`)

The audit found a **mature, defense-in-depth posture**. Inventory (file references are anchors, not exact
line pins — they drift):

### 4.1 Resource loading / SSRF / LFI (V1, V2)
- **Single choke point:** every internal consumer fetches through `SafeResourceLoader`
  (`src/NetPdf/Resources/SafeResourceLoader.cs`), never a raw loader. A constructor guard enforces that a
  wrapped `SafeHttpResourceLoader` shares the *same* `SecurityPolicy` instance (no policy divergence).
- **`UriSafetyValidator`** — scheme allowlist + a comprehensive **IP blocklist**: IPv4 `0/8`, `10/8`,
  `100.64/10`, `127/8`, **`169.254/16` (cloud metadata `169.254.169.254`)**, `172.16/12`, `192.168/16`,
  TEST-NET/benchmark/multicast/reserved; IPv6 `::`, `::1`, `::ffff:0:0/96` (mapped-IPv4, recursively checked),
  `fc00::/7`, `fe80::/10`, `ff00::/8`. Optional `AllowedHosts` allowlist (single-label wildcard).
- **`SafeHttpResourceLoader`** — `AllowAutoRedirect = false` with per-hop re-validation, **DNS-rebinding
  defense** via a `ConnectCallback` that pins the validated IP, cross-scheme-downgrade rejection, a
  `MaxRedirectHops` cap (5), and a bounded response read (`MaxResourceBytes`).
- **`SecurityPolicy` profiles** — `SafeDefault` (http/https OFF, `data:` ON, `file:` under `BaseUri` ON),
  `UntrustedHtml` (everything OFF, budgets tightened), `TrustedTemplate`. Default on `HtmlPdfOptions` is
  `SafeDefault`.
- **`file://` base-path check** — symlink-aware resolution (`File.ResolveLinkTarget(returnFinalTarget: true)`)
  re-validates the final target is under `BaseUri` (defeats symlink traversal; TOCTOU is an accepted residual).
- **HTML "URL strip" pass** (`HtmlParsingHost`) — strips `javascript:`/`vbscript:`/`data:` from ~30
  attribute/element pairs (`<a>`, `<iframe src>`, `<object data>`, `<embed>`, `<base href>`, `<meta refresh>`,
  SVG `xlink:href`/`href`, animation targets, …), **iterated up to 4× until stable**, with diagnostics
  (`HTML-JAVASCRIPT-URL-IGNORED-001`, `HTML-SCRIPT-IGNORED-001`). `<img src="data:image/...">` is exempted
  only for allowlisted image MIME types.
- **Per-kind MIME allowlist** at fetch time (Image / Font / Stylesheet), exception-message sanitization.

### 4.2 XML / XXE (V3)
- Both SVG parse sites (`SvgRasterizer`, `SvgNativeEmitter`) use **identical** hardened `XmlReaderSettings`:
  `DtdProcessing.Prohibit`, `XmlResolver = null`, `MaxCharactersFromEntities = 1024`,
  `MaxCharactersInDocument = 8 MiB`.
- **AngleSharp** HTML parsing runs with `IsScripting = false` and `IsResourceLoadingEnabled = false` — no
  network at parse time. No XML/XHTML path.

### 4.3 Native decoders — fonts & images (V4, V5)
- **`FontSafetyValidator`** — magic-byte sniff (TTF/OTTO/WOFF/WOFF2), 32 MiB cap, sfnt table count ≤ 64,
  per-table offset/length bounds, **danger-table denylist** (`SVG `, `sbix`, `CBDT/CBLC`, `EBDT/EBLC`) — runs
  *before* HarfBuzz sees the bytes. WOFF/WOFF2 (compressed) are rejected in v1.
- **`ImageSafetyValidator`** — magic-byte sniff, 32 MiB cap, header-only dimension peek with per-path caps
  (passthrough JPEG/PNG ≤ 8192 px / 67 MP; raster GIF/WebP/BMP ≤ 4096 px / 16.7 MP), PNG-with-alpha
  reclassified to the tighter raster caps, AVIF rejected. `RasterImageDecoder` re-validates decoded pixels
  (≤ 25 MP, checked arithmetic) and accepts only `SKCodecResult.Success`.

### 4.4 DoS budgets (V7)
- HTML input 16 MiB; DOM caps (250k elements, 1024 nesting depth, 256 attrs/elem, 1 MiB attr value, 4 MiB
  text); layout recursion 256; CSS `calc` depth 32 / body 4 KiB; SVG depth 80 / 50k elements / 8 MiB chars.
- Per-render resource budget: 200 fetches / 100 MiB / 25 MiB-per-resource / 10 s-per-fetch (tightened to
  50 / 20 MiB / 5 MiB / 5 s under `UntrustedHtml`), concurrency-safe via `Interlocked` CAS.
- Render `Timeout` on `HtmlPdfOptions` + a `CancellationToken` threaded through the pipeline.

### 4.5 PDF output safety (V8)
- **`PdfPreflightValidator`** walks the whole object graph before any bytes are written and **rejects a closed
  denylist**: `/OpenAction`, `/AA`, `/JavaScript`, `/JS`, `/Launch`, `/SubmitForm`, `/ImportData`, `/GoToR`,
  `/GoToE`, `/EmbeddedFile(s)`, `/RichMedia`. `/URI` is allowed only inside a well-formed URI action and only
  when `AllowUriLinkAnnotations` is opted in.
- **`LinkUriPolicy`** — link annotations only for `http`/`https`/`mailto`; `file:`/`data:`/`javascript:`/
  `vbscript:` rejected (`LINK-URI-UNSUPPORTED-001`).
- **String encoding** — outline titles / metadata sanitized (C0/C1/DEL stripped, capped) and escaped
  (`PdfLiteralString` octal-escapes; non-ASCII → UTF-16BE hex) so attacker strings can't break PDF syntax.

### 4.6 Existing tests
`PdfUriLinkPreflightTests`, `LinkAnnotationTests`, `HtmlParsingHostTests` (40+ sanitization cases), plus the
`tests/NetPdf.Fuzz` harness and the validator unit tests.

---

## 5. Residual gaps (prioritized)

Nothing critical is *open* in the shipped surface — the strongest finding is that the biggest future risk is a
**Phase-5 wiring hazard**, not a current hole. Severity = risk **if left unaddressed as the library grows**.

| ID | Sev | Gap | Where |
|---|---|---|---|
| **G1** | **HIGH** | Phase-5 CSS/font resource loading (`@import`, `@font-face src:url()`, CSS `url()` props, `<link rel=stylesheet>`) is *extracted but not fetched* today. When wired, it MUST route through `SafeResourceLoader` with kind-specific MIME — otherwise it reintroduces V1/V2 wholesale. There is no *enforced invariant* preventing a future fetch from bypassing the choke point. | `CssImportRule`, `CssResourceExtractor`, `HtmlParsingHost` |
| **G2** | MED | No DNS-resolution timeout — `Dns.GetHostAddressesAsync` can hang the render thread (a slow-resolver DoS, V7). | `SafeHttpResourceLoader.ResolveAndValidateAsync` |
| **G3** | MED | HTML can amplify into a huge PDF (many pages / huge image cascades). A hard `PdfRenderPipeline.MaxPages = 20_000` **emergency backstop** exists, but it is **not configurable / policy-driven**, is far too high for untrusted input, and there is **no output-byte budget**. (V7) | `PdfRenderPipeline`, writer |
| **G4** | MED | WOFF/WOFF2 **decoder infrastructure exists**, but the `FontFace` / `@font-face` render path currently **rejects wrapped WOFF/WOFF2 before parsing** (safe). Wiring the existing decoder in MUST add a decompression-ratio + absolute-size cap and **re-run sfnt validation on the *decompressed* bytes** before HarfBuzz (font-bomb / V4/V7). | `FontFace`, WOFF decoder, `FontSafetyValidator` |
| **G5** | MED | `data:` URI with a missing/empty mediatype passes the MIME gate (relies on downstream magic-byte). `data:text/html` is a known SSRF/polyglot sneak-path; disabled under `UntrustedHtml`, but `SafeDefault` accepts `data:`. (V1/V8) | `SafeResourceLoader` data: path |
| **G6** | LOW | `MaxCharactersFromEntities = 1024` may false-reject valid SVG; parse failure is a silent `catch (Exception) { return null }` with no diagnostic (observability — can't tell an attack from a malformed doc). (V3) | `SvgRasterizer`, `SvgNativeEmitter` |
| **G7** | LOW | CSS `repeat()` / counter / grid-track counts not *explicitly* capped (bounded only by calc-depth + layout recursion). Defense-in-depth for CSS-amplification DoS. (V7) | grid track sizing |
| **G8** | LOW | Accepted residuals to *document*, not fix: symlink TOCTOU (OS-level), `AllowedHosts` single-label wildcard scope. | various |
| **G9** | INFO | **No formal threat-model / SECURITY.md / secure-usage doc** existed before this file. Security decisions live in code comments only. Several audits flagged this as the top weakness. | docs |
| **G10** | INFO | No documented **native-dependency CVE-monitoring / patch policy**. V5 (Skia/HarfBuzz/PDFium memory-safety) is the residual RCE surface and is mitigated primarily by staying patched. | process |

---

## 6. Remediation plan — ordered task series

Worked **one PR at a time**, in order (foundational/observability first, then the ranked code gaps, then
process). Each is PR-sized with explicit acceptance criteria. Track this as the **Phase-5 security-hardening
sub-track**.

### Track A — Foundation & verification (do first)

- [x] **SEC-1 — Threat model + SECURITY.md + secure-usage guide.** ✅ Done. This document is the threat model;
  a root [`SECURITY.md`](../../SECURITY.md) (supported versions, GitHub-private-advisory disclosure channel,
  in/out-of-scope, response targets) landed; the README's
  [Running NetPdf on untrusted HTML](../../README.md#running-netpdf-on-untrusted-html) section codifies the §2
  golden rule (full `UntrustedHtml` + `ResourceLoader = null` + `Timeout` snippet) and now links the threat
  model + `SECURITY.md`. All three docs cross-link. (Also corrected a stale README note: `<a href>` → `/URI`
  link annotations *did* ship in Phase 4, scheme-restricted by `LinkUriPolicy`.)
- [x] **SEC-2 — Security regression suite + fuzz expansion.** ✅ Done. Consolidated
  [`SecurityHardeningTests`](../../tests/NetPdf.UnitTests/Security/SecurityHardeningTests.cs) (`Category=Security`,
  24 tests) pins SSRF/LFI/XXE/DoS/PDF-output as executable assertions, organized by the §5 taxonomy — the one
  home each subsequent SEC task extends. `tests/NetPdf.Fuzz` (was a no-op stub) now drives all five targets
  (`HtmlPdf.Convert`, `UriSafetyValidator`, `FontSafetyValidator`, `ImageSafetyValidator`, `SvgRasterizer`) via a
  shared `FuzzTargets` body with a sanctioned-exception contract, in two modes: a deterministic seeded `--smoke`
  pass (CI, no instrumentation) and `--libfuzzer` (coverage-guided campaign). The
  [`fuzz-smoke`](../../.github/workflows/fuzz-smoke.yml) CI job runs the smoke pass + the security suite on every
  PR; runbook in [`docs/security/fuzzing.md`](fuzzing.md). **Payoff on day one:** the smoke pass caught a DoS
  guard (deeply-nested HTML, layout depth > 256) escaping `HtmlPdf.Convert` as an untyped
  `InvalidOperationException` — now a typed `LayoutDepthExceededException` caught at the pipeline and degraded to
  a valid PDF + `LAYOUT-RECURSION-DEPTH-EXCEEDED-001` (regression-tested). **Accept:** ✅ suite green;
  ✅ fuzz smoke wired (0 findings over the seeded corpus + mutations); ✅ CI job authored.

### Track B — Close the ranked code gaps (in severity order)

- [x] **SEC-3 (G1, HIGH) — SSRF choke-point invariant for the Phase-5 resource surface.** ✅ Done (the
  **invariant + guards**; the actual CSS/font *fetch enablement* is the Phase-5 feature this now gates). The
  choke point already held (only `ImageResourceCache` fetches, only `ResourceKind.Image`; CSS/font URLs are
  extracted-but-not-fetched). SEC-3 makes that promise **enforced**:
  [`ResourceChokePointInvariantTests`](../../tests/NetPdf.UnitTests/Security/ResourceChokePointInvariantTests.cs)
  scans the production sources and FAILS if any code outside `SafeResourceLoader` invokes the raw
  `IResourceLoader.LoadAsync`, or constructs an `HttpClient`/`SocketsHttpHandler` outside `SafeHttpResourceLoader`
  (with an anti-vacuous "did the scan run" check). The SEC-3 region in `SecurityHardeningTests` adds the
  now-passing behavioral tests: `UntrustedHtml` (and even an http-permissive policy — a tripwire for the Phase-5
  wiring) yields **zero fetches** for `@import`/`@font-face`/`<link rel=stylesheet>`; `SafeResourceLoader.FetchAsync`
  **blocks the metadata IP** for the `Stylesheet` + `Font` kinds (loader never invoked); **MIME mismatch is
  rejected** for those kinds (`text/html` as a stylesheet/font → rejected; `text/css`/`font/ttf` → accepted),
  exercised through the real fetch path via `data:` URIs; the kind→MIME allowlist matrix is pinned and the
  default is **fail-closed** (an unknown `ResourceKind` fetches nothing). **Accept:** ✅ internal-URL
  `@import`/`@font-face` provably makes zero outbound requests; ✅ MIME-mismatch rejected; ✅ SSRF block for the
  Stylesheet/Font kinds green (the HTTP-loader-level redirect-to-internal + DNS-rebind defenses are kind-agnostic
  and covered by the existing `SafeHttpResourceLoader` tests). **When Phase 5 wires CSS/font fetching, it must
  route through `SafeResourceLoader.FetchAsync(…, Stylesheet|Font)`** — the tripwire test above flags any other
  route, and these guards then protect the new fetch path.
- [x] **SEC-4 (G2, MED) — DNS-resolution timeout.** ✅ Done. `SafeHttpResourceLoader.ResolveAndValidateAsync`
  now bounds the resolve at `min(ResourceTimeout, 5s)` via `Task.WaitAsync(DnsTimeout, ct)` — `getaddrinfo`
  does not reliably honor the `CancellationToken` on every platform, so an unbounded resolve of a dead /
  hostile-DNS host could hang the render thread (slow-resolver DoS, V7). On timeout it throws a typed
  `HttpRequestException("DNS resolution for '…' timed out after Ns")`, which `SafeResourceLoader` catches and
  surfaces as a typed `SafeResourceResult` failure — fail-fast, not a hung thread. The host resolver is now
  injectable via an internal constructor so tests can drive the timeout with a non-responding stub. **Accept:**
  ✅ a non-responding resolver fails fast with a typed failure (regression tests
  `Sec4_non_responding_dns_resolver_fails_fast_not_hang` + `Sec4_dns_timeout_surfaces_as_typed_failure_through_the_choke_point`
  in `SecurityHardeningTests`, using an injected never-completing resolver).
- [x] **SEC-5 (G3, MED) — Configurable output-size & page-count caps.** ✅ Done. `SecurityPolicy` gains
  `MaxPages` (default 20 000 = the hard backstop, so trusted rendering is unchanged) and `MaxOutputBytes`
  (default effectively unlimited); `UntrustedHtml` tightens both (500 pages / 50 MiB). `PdfRenderPipeline`
  enforces the configured page cap in the multi-page loop (above the renamed `MaxPagesBackstop = 20 000`
  emergency guard) → stops + emits `PDF-PAGE-LIMIT-EXCEEDED-001`; and checks output size after `Save()` →
  aborts with `HtmlPdfException(PDF-OUTPUT-SIZE-EXCEEDED-001)`. **Accept:** ✅ a page-amplifying HTML stops at
  the configured cap; ✅ the byte budget aborts an oversized output; tests + byte-identical for docs that don't
  trip the caps.
- [x] **SEC-6 (G5, MED) — `data:` URI MIME hardening.** ✅ Done. `SafeResourceLoader`'s `data:` path now
  requires an EXPLICIT, allowlisted mediatype: a missing/empty type (`data:,…` / `data:;base64,…`) or a
  non-allowlisted one (esp. `text/html`) is rejected at the choke point (the HTML img path already enforced
  this; SEC-6 closes it for CSS `url()` / direct loader use). `data:` stays fully off under `UntrustedHtml`.
  **Accept:** ✅ `data:text/html,…` + bare `data:,…` rejected; image `data:` still works; tests.
- [x] **SEC-7 (G4, MED) — WOFF/WOFF2 decompression guard + re-validation.** ✅ Done (the guards; the decoders
  stay dormant — no render path decodes WOFF in v1). The absolute-size cap already existed (WOFF 256 MiB;
  WOFF2 declared-size bound); added a **100:1 decompression-ratio cap** (above a 1 MiB floor) to both decoders
  and **re-run `FontSafetyValidator.Validate` on the reconstructed sfnt** before returning (the wrapper checks
  can't see inside compressed tables). Font-bomb rejection test. The `FontFaceData` XML doc already reflects
  the real state (WOFF/WOFF2 rejected at load pending this wiring). **Accept:** ✅ an over-ratio WOFF/WOFF2 is
  rejected; ✅ decompressed sfnt re-validated; ✅ doc accurate.
- [x] **SEC-8 (G6, LOW) — SVG parser observability.** ✅ Done. `SvgRasterizer.TryRender` gains an overload
  reporting an `SvgParseStatus` (Ok / NotSvg / Malformed / Blocked); the catch distinguishes a security-guard
  rejection (prohibited `<!DOCTYPE`/`<!ENTITY` or over-size — the XXE / billion-laughs defense) from malformed
  XML via a locale-independent prolog scan. `ImageResourceCache` emits a specific `SVG-PARSE-FAILED-001`
  instead of the silent null / misleading generic decode error. Re-evaluated `MaxCharactersFromEntities`:
  documented that `DtdProcessing.Prohibit` forbids author-defined entities entirely, so 1024 only ever bounds
  the five predefined entities — kept as a belt-and-braces floor (no change needed). **Accept:** ✅ a
  billion-laughs SVG is diagnosed, not silent; valid SVG renders (byte-identical); tests.
- [x] **SEC-9 (G7, LOW) — CSS-amplification caps (verified + regression-tested).** ✅ Done. The caps this gap
  called for already exist (G7 was largely stale): grid `repeat()` count is capped at
  `TrackRepeat.MaxRepeatCount = 10 000` (over-cap → `GridParseException` → `CSS-PROPERTY-VALUE-INVALID-001` +
  the declaration dropped), the expanded track count at `TrackList.MaxExpandedTrackCount = 50 000`, and counter
  magnitude is bounded by `CounterStyleFormatter` (roman > 3999 → decimal fallback; alpha/greek logarithmic;
  values int-bounded). Added regression tests locking in both guards. **Accept:** ✅ `repeat(1000000,1px)`
  aborts within the cap + a diagnostic; huge counter values don't amplify.

### Track C — Process & documented residuals

- [x] **SEC-10 (G10, INFO) — Native-dependency CVE policy + banned-API doc correction.** ✅ Done. Added a
  **Native-dependency CVE policy & patch cadence** section to
  [`docs/legal/dependency-dossier.md`](../legal/dependency-dossier.md#native-dependency-cve-policy--patch-cadence-sec-10)
  (version floors for SkiaSharp/HarfBuzzSharp/AngleSharp + test-only PDFium; the libwebp CVE-2023-4863 /
  neodyme V5 lesson; re-verify byte-identity after any native bump). Added a **CI `dependency-scan` job**
  (`dotnet list package --vulnerable --include-transitive`, fails on any advisory) to
  [`.github/workflows/fuzz-smoke.yml`](../../.github/workflows/fuzz-smoke.yml). **Corrected the
  `NetPdf.BannedAnalyzer` claim** in `CLAUDE.md` + the dossier (it does not exist — only `NetPdf.SourceGen`
  ships; the allowlist is enforced by the dossier-review process + code review + the vulnerable-scan, with a
  `BannedApiAnalyzers` machine-enforcement noted as a future option). **Accept:** ✅ policy documented; ✅ CI
  check added; ✅ the docs no longer assert a non-existent analyzer.
- [x] **SEC-11 (G8, INFO) — Accepted residuals + deployment hardening.** ✅ Done. Published
  [`docs/security/deployment.md`](deployment.md): the in-process config, a **container-isolation** baseline
  (no egress, read-only FS, memory/CPU/pids limits, drop-caps, seccomp, one-shot) as the OS-level backstop
  against native-decoder 0-days (V5), and the accepted residuals (symlink TOCTOU — backstopped by a read-only
  FS / `UntrustedHtml`; `AllowedHosts` single-label wildcard scope). Cross-linked from the dossier CVE policy
  and G8 below. **Accept:** ✅ deployment guide published.

---

## 7. Coverage matrix (taxonomy → status)

| Class | Status in `0.9.0-rc1` | Residual work |
|---|---|---|
| V1 SSRF | **Strong** (choke point, IP blocklist, DNS-pin, redirect re-validation; default no-http) | SEC-3 (Phase-5 wiring), SEC-4 (DNS timeout), SEC-6 (data:) |
| V2 Local file read | **Strong** (file: off under `UntrustedHtml`; base-path + symlink check under `SafeDefault`) | SEC-3, SEC-11 (TOCTOU doc) |
| V3 XXE / entity DoS | **Strong** (DTD prohibited, resolver null, entity + doc caps) | SEC-8 (observability) |
| V4 `@font-face` RCE | **Strong** (pre-decode validator, danger-table denylist; WOFF/WOFF2 rejected) | SEC-7 (safe decompression) |
| V5 Native-decoder RCE | **Mitigated** (pre-decode validation + caps) — residual = native 0-days | SEC-10 (patch policy), SEC-11 (sandbox) |
| V6 JS / arg-injection RCE | **Eliminated by design** (no browser, no JS, no process spawn) | preserve the invariant |
| V7 DoS | **Strong** (layered caps + timeout) | SEC-4, SEC-5, SEC-9 |
| V8 Malicious PDF output | **Strong** (preflight denylist, link-URI allowlist, string escaping) | negative tests in SEC-2 |
| V9 Deserialization/traversal | **Eliminated by design** (no untrusted-input-driven type activation / deserialization; no `Activator`) | preserve the invariant |

**Bottom line:** NetPdf is already substantially hardened and structurally immune to several whole classes.
The plan closes the remaining ranked gaps and — most importantly — installs the **SSRF choke-point invariant
(SEC-3)** so the Phase-5 resource-loading work can't silently reintroduce the #1 bug class as the library grows.

---

## References

External research consulted for the taxonomy (accessed 2026-07-01):
Tempest/SideChannel "HTML to PDF converters – can I hack them?", neodyme "HTML renderer to RCE",
Intigriti "Exploiting PDF generators (SSRF)", positive.security "dompdf RCE", AppSecEngineer "Safeguarding
against SSRF", Stratascale "Apryse argument-injection RCE", Stirling-PDF GHSA-xw8v-9mfm-g2pm,
RE:HACK "PDF generators: the SSRF attack surface"; CVEs 2023-23924 (dompdf), 2023-4863 (libwebp),
2025-55853, 2025-68428 (jsPDF), 2026-26801 (pdfmake). Internal source of truth: the 2026-07-01 four-part audit.
