# HTML samples

A gallery of self-contained HTML documents you can render to PDF with NetPdf. Every file
is standalone — all CSS is inlined and all graphics are inline SVG or `data:` URIs, so there
are no external assets to fetch and each renders deterministically offline.

They double as a tour of the engine: flexbox and CSS grid, multi-page tables with repeating
`thead`/`tfoot`, `@page` margins and margin boxes (`counter(page)` / `counter(pages)`),
gradients, border-radius, inline SVG, and paged fragmentation.

## Samples

| File | Document | Notes |
|------|----------|-------|
| [`basic-invoice.html`](basic-invoice.html) | Simple one-page invoice | Minimal starting point |
| [`stripe-invoice.html`](stripe-invoice.html) | Stripe-style itemized invoice | Multi-page line-item table |
| [`consulting-invoice.html`](consulting-invoice.html) | Consulting services invoice | Long multi-page table |
| [`legal-invoice.html`](legal-invoice.html) | Legal / attorney billing statement | Multi-page, trust-account summary |
| [`commercial-invoice.html`](commercial-invoice.html) | Commercial (export) invoice | CSS tables, customs layout |
| [`sales-report.html`](sales-report.html) | Sales report | Tabular data + summary |
| [`itinerary-day-by-day.html`](itinerary-day-by-day.html) | Travel itinerary | Day-by-day timeline layout |
| [`agency-commission-statement.html`](agency-commission-statement.html) | Travel-agency commission statement | Tabular financials |
| [`travel-voucher.html`](travel-voucher.html) | Hotel stay voucher | Flexbox, inline SVG, gradients |
| [`event-ticket.html`](event-ticket.html) | Event ticket | Card layout, inline SVG |
| [`course-completion-certificate.html`](course-completion-certificate.html) | Course completion certificate | Centered certificate layout |
| [`terms-and-conditions.html`](terms-and-conditions.html) | Terms & conditions | Multi-column prose |

## Rendering a sample

Using the included [`invoice-cli`](../invoice-cli) sample project:

```bash
dotnet run --project samples/invoice-cli -c Release -- samples/html/basic-invoice.html basic-invoice.pdf
```

Or from your own code with the public API:

```csharp
using NetPdf;

var html = File.ReadAllText("samples/html/travel-voucher.html");
var pdf = HtmlPdf.Convert(html);
File.WriteAllBytes("travel-voucher.pdf", pdf);
```

Each document carries its own `@page` rule (page size and margins), so the output paper size
comes from the HTML rather than from render options.
