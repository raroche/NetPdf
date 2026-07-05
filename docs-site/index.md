---
_layout: landing
---

# NetPdf

**Pure C# / .NET 10 HTML + CSS → PDF.** No browser, no native Chromium, no subprocess at render time, no
AGPL or revenue-capped dependencies. Apache-2.0. Direction: [Prince](https://www.princexml.com/) /
[WeasyPrint](https://weasyprint.org/), not Playwright / wkhtmltopdf.

NetPdf ships its own HTML/CSS layout engine — block / inline / flex / grid / table layout, fragmentation
across pages, international text shaping (HarfBuzz), a bounded "least-ugly page split" pagination cost model,
and its own PDF byte writer — rendered natively to PDF, with a Skia raster fallback only where a feature
can't be expressed natively.

```csharp
using NetPdf;

byte[] pdf = HtmlPdf.Convert("""
    <!DOCTYPE html>
    <html lang="en"><body>
      <h1>Hello, PDF</h1>
      <p>Rendered by NetPdf — no browser required.</p>
    </body></html>
    """);

File.WriteAllBytes("hello.pdf", pdf);
```

## Highlights

- **Deterministic** — the same input produces byte-identical PDF output (opt-in frozen `/CreationDate`).
- **AOT-clean** — no reflection in core paths; a Native AOT smoke gate runs in CI.
- **Paged media** — `@page`, the 16 margin boxes, running headers/footers via `string()` / `element()`,
  `counter(page)`, orphans/widows, and break controls.
- **International text** — HarfBuzz shaping, UAX #9 bidi, UAX #14 line breaking, and language-aware
  `hyphens: auto` via the optional [`NetPdf.Languages.*`](getting-started.md#hyphenation-language-packs) packs.
- **Diagnostics, not silent corruption** — unsupported features emit a stable, documented code.

## Next steps

- **[Getting started](getting-started.md)** — install, convert, and configure.
- **[Compatibility](compatibility.md)** — what CSS is in and out of scope.
- **[Diagnostics](diagnostics.md)** — the stable diagnostic codes NetPdf emits.
- **[API reference](api/index.md)** — the public `NetPdf` surface.
