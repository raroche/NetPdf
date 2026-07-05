# Getting started

## Install

NetPdf targets **.NET 10**. Add the package from NuGet:

```bash
dotnet add package NetPdf
```

The single `NetPdf` package is self-contained — the internal layout / text / PDF assemblies are bundled
inside it, so you reference one package and see one public surface.

## Convert HTML to PDF

The entry point is the static [`HtmlPdf`](api/NetPdf.HtmlPdf.yml) facade:

```csharp
using NetPdf;

byte[] pdf = HtmlPdf.Convert("<html lang=\"en\"><body><h1>Invoice</h1></body></html>");
File.WriteAllBytes("invoice.pdf", pdf);
```

There are overloads for `string`, `ReadOnlySpan<char>`, and async variants that write to a `Stream`:

```csharp
await using var file = File.Create("invoice.pdf");
await HtmlPdf.ConvertAsync(html, file);
```

## Options

Pass an [`HtmlPdfOptions`](api/NetPdf.HtmlPdfOptions.yml) to control page geometry, backgrounds, the base URI
for resolving relative resources, metadata, and feature flags:

```csharp
var pdf = HtmlPdf.Convert(html, new HtmlPdfOptions
{
    BaseUri = new Uri(Path.GetFullPath("invoice.html")),
    PageSize = PageSize.A4,
    Margins = PageMargins.Default,
    PrintBackgrounds = true,
    Title = "Invoice 2026-0042",
    Features = FeatureFlags.DeterministicTimestamps, // byte-identical output across runs
});
```

CSS `@page` rules in the document override the option defaults, so page size and margins are usually best
expressed in the stylesheet:

```css
@page { size: A4; margin: 20mm; }
```

## Error handling

Unsupported features never corrupt output silently — they emit a stable diagnostic code (see
[Diagnostics](diagnostics.md)). Hard failures on hostile or malformed input surface as a typed
[`HtmlPdfException`](api/NetPdf.HtmlPdfException.yml) carrying that code:

```csharp
try
{
    var pdf = HtmlPdf.Convert(html, options);
}
catch (HtmlPdfException ex)
{
    Console.Error.WriteLine($"NetPdf error [{ex.Code}]: {ex.Message}");
}
```

## Hyphenation language packs

`hyphens: auto` uses NetPdf's bundled American-English patterns by default. To hyphenate other languages,
install an optional `NetPdf.Languages.*` pack and register it once at startup; NetPdf then resolves the
hyphenator from a block's effective HTML `lang`:

```bash
dotnet add package NetPdf.Languages.European   # German + French starter set
```

```csharp
using NetPdf.Languages.European;

EuropeanHyphenation.Register(); // call once at startup

// <html lang="de"> content now hyphenates with German rules.
```

| Package | Covers |
|---|---|
| `NetPdf.Languages.European` | German + French (real patterns); more European languages are follow-ups |
| `NetPdf.Languages.Cjk` | Chinese / Japanese / Korean — registered as no-hyphenation (CJK breaks per-character) |
| `NetPdf.Languages.Arabic` | Arabic / Persian / Urdu — registered as no-hyphenation (justification uses kashida) |
| `NetPdf.Languages.Indic` | Hindi, Sanskrit, Tamil, … — routing-aware placeholders pending vendored pattern data |
| `NetPdf.Languages.All` | Meta-package that pulls in and registers all of the above |

> Prefer the explicit `Register()` call over relying on the packs' module initializer — a package reference
> alone doesn't guarantee its assembly is loaded.

## Determinism

With `FeatureFlags.DeterministicTimestamps` set, the same input produces byte-identical PDF bytes across
runs and machines (no `DateTime.Now`, no PRNG, deterministic compression). This makes golden-file testing
and content-addressable caching straightforward.
