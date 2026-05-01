# NetPdf

A pure C# / .NET HTML+CSS-to-PDF rendering engine. **Free, open-source, Apache-2.0.**

NetPdf is a true paged-media renderer written from scratch in managed code. It does **not** wrap a browser, **not** shell out to native binaries, **not** depend on revenue-capped or copyleft libraries. It converts a single HTML+CSS string into a deterministic PDF byte stream optimized for documents — invoices, statements, contracts, reports, certificates, catalogs.

> **Status:** Phase 0 — architecture lock. Public API surface defined; internals under active construction. **Not yet functional.** First alpha (`0.1.0-alpha`) targets programmatic PDF construction; first useful release (`0.7.0-beta`) targets static-document rendering.

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

## Public API (frozen for v1)

```csharp
using NetPdf;

// One-liner
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
```

See [`docs/compatibility-matrix.md`](docs/compatibility-matrix.md) for the supported / not-supported feature list.
See [`docs/diagnostics-codes.md`](docs/diagnostics-codes.md) for the stable diagnostic code registry.
See [`docs/phases/`](docs/phases/) for per-phase execution guides — what to build, in what order, with exit criteria.

## v1 capability summary

**Supported:**
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

**Not supported in v1 (parsed without error; emits structured diagnostics):**
- JavaScript, canvas, `<video>`, `<audio>`, service workers
- 3D transforms, sticky positioning, animations/transitions
- CSS Grid Level 2 (subgrid), container queries, `:has()` rendering, anchor positioning

**Post-v1 roadmap:** tagged PDF (PDF/UA-1) → PDF/A-3u → Grid L2 → container queries → `:has()` → anchor positioning.

## Performance gates (enforced in CI)

- **3-page invoice ≤ 200 ms p50** on commodity desktop.
- **20-page report ≤ 1.5 s p50.**
- **Linear memory growth** with page count.
- **No process spawning at render time.**
- **Native AOT compatible.**
- **Deterministic output:** identical input → identical bytes.

## License

[Apache-2.0](LICENSE).

Third-party attributions: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
