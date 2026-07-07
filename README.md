# NetPdf

A pure C# / .NET HTML+CSS-to-PDF rendering engine. **Free, open-source, Apache-2.0.**

NetPdf is a true paged-media renderer written from scratch in managed code. It does **not** wrap a browser engine, **not** spawn a subprocess at render time, **not** depend on revenue-capped or copyleft libraries. It converts a single HTML+CSS string into a deterministic PDF byte stream optimized for documents — invoices, statements, contracts, reports, certificates, catalogs.

**Native dependencies, honestly listed.** NetPdf is "pure managed code" at the engine layer but ships with two permissive-licensed native bundles that are AOT-clean and reflection-free:

- **HarfBuzzSharp** (MIT) wraps **HarfBuzz** (Old MIT-style). Drives OpenType shaping (kerning, ligatures, RTL, CJK).
- **SkiaSharp** (MIT) wraps **Skia** (BSD-3). Used **only** for image decode (WebP / AVIF / GIF) and the raster fallback for filters / blurred shadows / conic gradients. Not a primary graphics path.

Both are loaded as in-process libraries via the official `*.NativeAssets.{Linux,macOS,Win32}` packages — no `Process.Start`, no executable spawning, no IPC. AOT publish + execution is verified on every commit via `scripts/aot-parity.sh`, which asserts the published native binary produces byte-identical PDF output to the JIT path.

## Installation

NetPdf targets **.NET 10** and ships as a **single NuGet package** — that one package bundles the whole engine, so there is nothing else to wire up.

```bash
dotnet add package NetPdf
```

or in your `.csproj`:

```xml
<PackageReference Include="NetPdf" Version="1.0.0" />
```

**Requirements:** the .NET 10 SDK/runtime. NetPdf runs on **Linux, macOS, and Windows** (x64 and arm64); the permissive-licensed HarfBuzz + Skia native assets are restored automatically as part of the package — no extra install, no browser, no system dependency. It is **Native-AOT compatible** and trimmable.

Optional add-on packages provide non-English hyphenation dictionaries — see [Language packs](#language-packs).

## Quick start

```csharp
using NetPdf;

// HTML + CSS in, PDF bytes out. No browser, no temp files, no subprocess.
byte[] pdf = HtmlPdf.Convert("""
    <style>
      body { font-family: sans-serif; }
      h1   { color: #14396b; }
      table{ width: 100%; border-collapse: collapse; }
      td   { border-bottom: 1px solid #ccc; padding: 6px; }
    </style>
    <h1>Invoice #1234</h1>
    <table>
      <tr><td>Widget</td><td>$19.00</td></tr>
      <tr><td>Gadget</td><td>$42.00</td></tr>
    </table>
    """);

File.WriteAllBytes("invoice.pdf", pdf);
```

That's the whole "hello world" — see [Using the API](#using-the-api) for options, async streaming, diagnostics, and paged-media features.

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

Its direction is closer to **Prince**, **WeasyPrint**, **OpenHTMLtoPDF** than to Playwright/Puppeteer/wkhtmltopdf — layout-engine first, paint-engine second, PDF byte-writer third — all built from scratch using public W3C / Unicode / ISO specifications under a [clean-room policy](https://github.com/raroche/NetPdf/blob/main/docs/clean-room-policy.md).

## Using the API

The public surface is the single static `HtmlPdf` facade plus a small set of option/result types (namespace `NetPdf`). Every overload below returns real PDF bytes for the full feature set — layout, pagination, paged media, text shaping, images, and visual parity (gradients, shadows, transforms, filters, SVG). Unsupported features emit a stable structured diagnostic rather than throwing or silently dropping content.

```csharp
using NetPdf;

string html = "<h1>Invoice #1234</h1><p>Hello world.</p>";

// 1) One-liner — HTML string in, PDF bytes out.
byte[] pdf = HtmlPdf.Convert(html);
File.WriteAllBytes("out.pdf", pdf);

// 2) With options — page size, backgrounds, and a base URI for relative asset/URL resolution.
byte[] letter = HtmlPdf.Convert(html, new HtmlPdfOptions
{
    BaseUri = new Uri("file:///app/templates/"),  // resolves <img src="logo.png">, url(...), @font-face
    PageSize = PageSize.Letter,                    // or A4; or let CSS @page decide (PreferCssPageSize)
    PrintBackgrounds = true,                        // paint CSS background colors/images (on by default)
});

// 3) Async streaming — write bytes to a stream as they're produced.
// ConvertAsync also takes an optional CancellationToken as a final argument.
await using var fs = File.Create("report.pdf");
await HtmlPdf.ConvertAsync(html, fs, new HtmlPdfOptions { PreferCssPageSize = true });

// 4) Diagnostic mode — bytes + page count + every structured warning (unsupported feature, skipped asset, ...).
PdfRenderResult result = HtmlPdf.ConvertDetailed(html);
Console.WriteLine($"{result.PageCount} pages, {result.Warnings.Count} warnings");
foreach (Diagnostic d in result.Warnings)
    Console.WriteLine($"  [{d.Code}] {d.Severity}: {d.Message}");   // codes are stable — see the diagnostics registry

// 5) Hard failures surface as a typed exception carrying a stable code (bytes are never half-written).
try { pdf = HtmlPdf.Convert(html); }
catch (HtmlPdfException ex) { Console.Error.WriteLine($"render failed [{ex.Code}]: {ex.Message}"); }

// Version string, e.g. "1.0.0+<commit-sha>"
Console.WriteLine(HtmlPdf.Version);
```

**Paged media** works through standard CSS — no proprietary API. Use `@page` for size/margins, the 16 margin boxes for running headers/footers, `counter(page)` / `counter(pages)` for page numbers, and `break-before` / `break-inside` / `widows` / `orphans` for pagination control:

```html
<style>
  @page { size: A4; margin: 20mm; @bottom-center { content: "Page " counter(page) " of " counter(pages); } }
  table { break-inside: auto; }  thead { display: table-header-group; }  /* repeat headers across pages */
  h2    { break-after: avoid; }                                          /* keep a heading with its section */
</style>
```

- See the [compatibility matrix](https://github.com/raroche/NetPdf/blob/main/docs/compatibility-matrix.md) for the supported / not-supported feature list.
- See the [diagnostics code registry](https://github.com/raroche/NetPdf/blob/main/docs/diagnostics-codes.md) for the stable diagnostic codes.
- See the [HTML→PDF authoring guide](https://github.com/raroche/NetPdf/blob/main/docs/authoring-html-for-pdf.md) for recommended patterns that produce good PDFs.

## Recipes: pagination, repeating headers & page numbers

NetPdf is a true **paged-media** engine, so multi-page documents, repeating headers, and page numbers are all **plain CSS** — there is no special API to call. You render the HTML the same way; the layout engine splits it across pages for you. The recipes below are the ones you'll reach for most on invoices, statements, and reports.

### Long documents split across pages automatically

Just render — content that doesn't fit flows onto as many pages as it needs. No option, no flag:

```csharp
byte[] pdf = HtmlPdf.Convert(longInvoiceHtml);   // a 120-row table → ~6 pages, automatically
```

### Repeat a table's column headers on every page

Put the headings in `<thead>` — they repeat at the top of each page the table spans. `<tfoot>` repeats a totals row at the bottom:

```css
thead { display: table-header-group; }   /* column headers repeat on every page */
tfoot { display: table-footer-group; }   /* optional running totals repeat on every page */
```

### Repeat a document banner (logo / title) on every page

Two ways — pick whichever fits:

**Simplest — `position: fixed`** (the element is painted on every page):

```css
@page  { size: A4; margin: 28mm 20mm; }         /* leave headroom for the banner */
header { position: fixed; top: -20mm; left: 0; right: 0;
         border-bottom: 2px solid #14396b; font-weight: bold; }
```
```html
<header>ACME Corp — Invoice</header>
<!-- body content -->
```

**CSS Paged Media — `@page` margin boxes** (ideal for text headers/footers + page numbers):

```css
@page {
  size: A4; margin: 20mm;
  @top-center   { content: "ACME — Invoice"; }
  @bottom-right { content: "Page " counter(page) " of " counter(pages); }
}
```

For a **rich** running header (e.g. a logo image), mark it as a running element and place it in a margin box: `header { position: running(hdr); }` + `@top-center { content: element(hdr); }`. All 16 `@page` margin boxes plus `counter(page)` / `counter(pages)` / `string()` / `element()` are supported.

### Put each invoice on its own page (one PDF)

Force a page break before each invoice section:

```css
.invoice              { break-before: page; }
.invoice:first-child  { break-before: auto; }   /* no blank leading page */
```
```html
<section class="invoice">…invoice 1…</section>
<section class="invoice">…invoice 2…</section>   <!-- starts on page 2 -->
```

### One PDF **file** per invoice

NetPdf renders one HTML → one PDF, so split the source and call the API per invoice:

```csharp
foreach (var (name, html) in invoices)
    File.WriteAllBytes($"{name}.pdf", HtmlPdf.Convert(html));
```

### Keep things from breaking awkwardly

```css
h2 { break-after: avoid; }    /* keep a heading with the content that follows it */
tr { break-inside: avoid; }   /* never split a line-item row across a page boundary */
figure, .keep-together { break-inside: avoid; }
```

### A complete invoice stylesheet

```css
@page {
  size: A4; margin: 20mm;
  @top-left     { content: "Invoice #1234"; }
  @bottom-right { content: "Page " counter(page) " / " counter(pages); }
}
table { width: 100%; border-collapse: collapse; }
thead { display: table-header-group; }   /* headers repeat every page */
tfoot { display: table-footer-group; }   /* totals repeat every page */
tr    { break-inside: avoid; }           /* rows stay whole */
h2    { break-after: avoid; }            /* section headings stay with their content */
```

> Two ready-to-run references live in the repo: [`Corpus/Reports/01-quarterly-report.html`](https://github.com/raroche/NetPdf/blob/main/tests/NetPdf.RealDocuments/Corpus/Reports/01-quarterly-report.html) (repeating `thead` + `@page` footer) and [`Corpus/Invoices/04-anvil-running-elements.html`](https://github.com/raroche/NetPdf/blob/main/tests/NetPdf.RealDocuments/Corpus/Invoices/04-anvil-running-elements.html) (running elements + page counters).

## Document metadata

The document's `<title>`, standard `<meta>` descriptors, and `<html lang>` flow automatically into the PDF's `/Info` dictionary, its XMP `/Metadata` stream, and the catalog `/Lang` — so the generated file is catalogued and searched by name, and PDF readers show its title instead of the filename:

```html
<html lang="en">
  <head>
    <title>Invoice #1234</title>
    <meta name="author" content="ACME Billing">
    <meta name="description" content="March 2026 statement">
    <meta name="keywords" content="invoice, acme, march">
  </head>
  <body>…</body>
</html>
```

Anything you'd rather set from code — or that isn't in the HTML — goes through `HtmlPdfOptions`, which **overrides** the harvested values and can add arbitrary custom `/Info` entries:

```csharp
byte[] pdf = HtmlPdf.Convert(html, new HtmlPdfOptions
{
    Title = "Invoice #1234",              // overrides <title>
    Author = "ACME Billing",
    Subject = "March 2026 statement",
    Keywords = "invoice, acme, march",
    Creator = "Acme Billing Service",
    CreationDate = DateTimeOffset.UtcNow, // omitted by default (deterministic output)
    DocumentProperties = new Dictionary<string, string>
    {
        ["InvoiceNumber"] = "1234",       // extra /Info keys
        ["AccountId"] = "AC-99",
    },
});
```

Everything is deterministic: no timestamp is read unless you set one, and a document with **no** metadata emits none of these entries, so its bytes are unchanged. The XMP stream mirrors the descriptive fields in Dublin Core.

## Navigation & initial view

Same-document links work as plain HTML — an `<a href="#id">` becomes a clickable **`/GoTo`** jump to the element with that `id`, resolved across the whole document (the target can be on a later page). The `#id` fragment is percent-decoded the way a browser navigates (`href="#r%C3%A9sum%C3%A9"` matches `id="résumé"`). A **block, inline-block, or inline-flow** element can be the target — an inline target (e.g. `<span id="summary">`) resolves to its containing block/line position. A dangling `#id` (no such element) is reported as a diagnostic and the text still renders. On the **link** side, like external links, the anchor needs its own box (a `display:block` / `inline-block` `<a>`); an inline-flow anchor's precise rectangle is a documented follow-up.

```html
<a href="#summary" style="display:inline-block">Jump to summary</a>
…
<h2 id="summary">Summary</h2>
```

You can also control how the document **opens** in a reader — for example, show the bookmarks panel and use a two-column layout:

```csharp
byte[] pdf = HtmlPdf.Convert(html, new HtmlPdfOptions
{
    PageMode = PdfPageMode.UseOutlines,      // open with the bookmarks panel showing
    PageLayout = PdfPageLayout.TwoColumnLeft, // two continuous columns
});
```

Both options default to omitted (the reader's own default), so a document that doesn't set them is unchanged. Headings (`<h1>`–`<h6>`) already become the PDF outline (bookmarks) automatically.

## Language packs

The core `NetPdf` package bundles **English** hyphenation, registered under the primary subtag `en` (American-English Liang patterns) — so `en`, `en-GB`, `en-US`, etc. all resolve to it. Other languages ship as small, optional add-on packages so the core stays lean — install only what you need, then call the pack's one-line `Register()` at startup. The pack then wires each language into the `lang`-aware pipeline: hyphenation (or explicit *no*-hyphenation) activates automatically for any element whose effective HTML `lang` matches, when the CSS asks for it (`hyphens: auto`).

```bash
dotnet add package NetPdf.Languages.European   # de, fr Liang hyphenation
```

```csharp
using NetPdf.Languages.European;

EuropeanHyphenation.Register();   // once, at startup — wires the European hyphenators

// A German paragraph now hyphenates when CSS opts in:
var pdf = HtmlPdf.Convert(
    "<div lang='de' style='width:120px; text-align:justify; hyphens:auto'>" +
    "Silbentrennung im Donaudampfschifffahrtsgesellschaftskapitän</div>");
```

The table below is the **honest current coverage** — what each pack registers *today*, not an aspirational language list. The pack surface (namespaces + `Register()` entry points) is stable; additional languages fill in behind it as the pattern data is vendored.

| Package | Languages registered today | What it does |
|---|---|---|
| `NetPdf.Languages.European` | `de`, `fr` | Real Liang hyphenation patterns. (More European languages are planned behind the same `Register()`.) |
| `NetPdf.Languages.Cjk` | `zh`, `ja`, `ko` | Registers **no-hyphenation** so `hyphens: auto` inserts no hyphens (correct for CJK) instead of falling back to English rules. Line breaking itself is in the core. |
| `NetPdf.Languages.Arabic` | `ar`, `fa`, `ur` | Registers **no-hyphenation** (these scripts are RTL and do not hyphenate this way). |
| `NetPdf.Languages.Indic` | `hi`, `bn`, `ta`, `te`, … | **Placeholder** registration — reserves the `lang` routing with an empty hyphenator so English rules are never wrongly applied. No Indic hyphenation is performed yet (pending vendored pattern data). |
| `NetPdf.Languages.All` | all of the above | Meta-package — references every pack; `AllLanguages.Register()` wires them all. |

Text **shaping** for these scripts (RTL, CJK, ligatures, kerning) and **line breaking** (UAX #14, including CJK) are always in the core package via HarfBuzz — the language packs only add per-language **hyphenation** dictionaries (or, for CJK/Arabic, the explicit *no-hyphenation* registration).

## Supported features

`HtmlPdf.Convert(html)` runs the full HTML → CSS → layout → paginate → paint → PDF pipeline. The [compatibility matrix](https://github.com/raroche/NetPdf/blob/main/docs/compatibility-matrix.md) is the authoritative feature list; the summary:

**Supported:**
- Block & inline layout, lists, tables (with `<thead>`/`<tfoot>` repetition across pages)
- Flexbox (Level 1) and CSS Grid (Level 1)
- Multi-column layout, absolute/fixed positioning (fixed repeats per page)
- Web fonts (`@font-face` with TTF/OTF/WOFF/WOFF2), font fallback, OpenType shaping, ligatures, kerning, RTL, CJK
- Images (JPEG passthrough, PNG/Flate, WebP/AVIF/GIF via Skia decode)
- Static SVG (shapes, paths, gradients, transforms, text)
- Custom properties (`--*`), `var()`, `calc()`/`min()`/`max()`/`clamp()`
- `@media print`, `@page` (size, margins, the 16 margin boxes), `break-*`, `widows`, `orphans`, `counter(page)`/`counter(pages)`/`string()`/`element()`
- 2D transforms, opacity, gradients, box/text shadows, `border-radius`, `clip-path`, masks/blend modes
- CSS filters via a subtree raster fallback (blur, drop-shadow, brightness, contrast, …)
- Internal (`#fragment`) and external (`http`/`https`/`mailto`) links, PDF outline from headings, document metadata

**Out of scope (parsed without error; emits a structured diagnostic instead of failing):**
- JavaScript, canvas, `<video>`, `<audio>`, service workers
- 3D transforms, sticky positioning, CSS animations/transitions
- CSS Grid Level 2 (subgrid), container queries, `:has()` rendering, anchor positioning
- Tagged PDF (PDF/UA) and PDF/A output

## Performance

- **3-page invoice ≤ 200 ms p50** on commodity desktop.
- **20-page report ≤ 1.5 s p50.**
- **Linear memory growth** with page count.
- **No process spawning at render time.**
- **Native AOT compatible.**
- **Deterministic output:** identical input → identical bytes (so results are safe to cache by input hash).

## Running NetPdf on untrusted HTML

> **See also:** how to report a vulnerability in [`SECURITY.md`](https://github.com/raroche/NetPdf/blob/main/SECURITY.md), and safe deployment guidance in [`docs/security/deployment.md`](https://github.com/raroche/NetPdf/blob/main/docs/security/deployment.md).

Library-level guards protect against the known HTML-to-PDF attack classes (SSRF tags, CSS SSRF, local-file read, SVG animation tricks, data-URI polyglots, resource bombs, image / font decoder bugs, PDF active content, diagnostic log injection). They do NOT replace OS-level isolation. If you run NetPdf in an API or web service that accepts customer-supplied HTML, follow this checklist:

### 1. Pin `SecurityPolicy.UntrustedHtml`

```csharp
var options = new HtmlPdfOptions
{
    SecurityPolicy = SecurityPolicy.UntrustedHtml, // no file/http/data, tight budgets
    ResourceLoader = null,                         // no ambient network/file fetch at all
    Timeout = TimeSpan.FromSeconds(10),            // bound a pathological render
    Diagnostics = sink,
    // BaseUri: leave null — nothing to resolve relative refs against
};
var pdf = HtmlPdf.Convert(untrustedHtml, options);
// For caller cancellation, use the async overload:
//   await HtmlPdf.ConvertAsync(untrustedHtml, options, cancellationToken);
```

`UntrustedHtml` disables every URL-fetching surface (file://, http(s), data:) and tightens per-render fetch budgets. Leaving `ResourceLoader` null means no loader is even available, and `Timeout` bounds a pathological render (the synchronous `Convert` takes no token — external cancellation is available on the `ConvertAsync` overloads). Use `TrustedTemplate` only for HTML you authored; the default `SafeDefault` is a middle-ground for desktop / batch use cases.

### 2. Process / container isolation

Run the conversion worker as a low-privilege user in a container / process boundary:

- **Drop privileges:** non-root user, no `CAP_*` capabilities, `umask 077`.
- **Read-only root filesystem:** mount `/tmp` as `tmpfs` with `noexec`.
- **No ambient network access:** if you enable outbound fetches, use an egress proxy + outbound allowlist + block route to `169.254.169.254` (AWS / GCE / Azure / Alibaba IMDS), `127.0.0.0/8`, `10/8`, `172.16-31/12`, `192.168/16`, `fc00::/7`, `fe80::/10`. NetPdf's `UriSafetyValidator` does this at the application layer; route-level blocking is defense in depth.
- **No ambient secrets:** no AWS profile, no GCP service account, no `.kube/config`, no SSH keys, no `/proc/.../environ` reachable from the renderer's user.
- **CPU + memory limits:** `cgroups` / `--memory` / `--cpus`. NetPdf's per-render caps (DOM size, CSS rule count, calc body length, image / font validators) bound the typical worst case, but a kernel-level limit is the final backstop.
- **No shell execution:** the worker never exec's a subprocess. NetPdf does not spawn processes, but third-party deps in your service might.

### 3. Vulnerability scanning

- Run `dotnet list package --vulnerable --include-transitive` on every build. NetPdf vendors only AngleSharp + AngleSharp.Css + HarfBuzzSharp + SkiaSharp; none has a current CVE in the v1 dependency ranges.
- Subscribe to security advisories for HarfBuzz (CVE-2024-56732 class), libwebp (CVE-2023-4863 class), libjpeg-turbo, and libpng. NetPdf's pre-decode validators bound the attack surface but don't fix decoder bugs.
- NetPdf keeps a deliberately small, vetted dependency set (a [clean-room policy](https://github.com/raroche/NetPdf/blob/main/docs/clean-room-policy.md)); adding a dependency requires review.

### 4. Resource allowlist

- If a resource loader is enabled in your service, configure `SecurityPolicy.AllowedHosts` to an explicit allowlist of domains your templates are permitted to fetch from. Wildcards (`*.cdn.example.com`) match a single subdomain level.
- Set `MaxResourcesPerRender` / `MaxTotalResourceBytes` / `MaxRedirectHops` lower than the defaults if your use case allows.

### 5. Output handling

- The conversion produces deterministic bytes (same input → same PDF), so you can cache results by input hash without timestamp-based cache poisoning.
- The PDF preflight rejects every active-content key (`/OpenAction`, `/AA`, `/JavaScript`, `/Launch`, `/SubmitForm`, `/ImportData`, `/GoToR`, `/GoToE`, `/EmbeddedFile`, `/EmbeddedFiles`, `/RichMedia`) **unconditionally**, before bytes are written — there is no opt-in for these.
- `<a href>` hyperlinks **are** emitted as `/URI` Link annotations, but only for `http` / `https` / `mailto` schemes; `javascript:` / `file:` / `data:` / other schemes are dropped with a `LINK-URI-UNSUPPORTED-001` diagnostic, so a dangerous link scheme can never reach the PDF. Note the *value* of a benign `http(s)` link is still attacker-controlled template content — if you do not want any attacker-supplied links, strip `<a href>` before conversion or post-process the annotations.
- Set `HtmlPdfOptions.Title` / `Author` / `Subject` to constants you control, not to attacker-supplied template content (NetPdf sanitizes these, but a constant is one less surface to worry about).

## License

[Apache-2.0](LICENSE).

Third-party attributions: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
