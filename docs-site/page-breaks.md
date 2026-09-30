# Controlling page breaks

NetPdf splits your HTML into pages with standard CSS: `break-before`, `break-after`, `break-inside`,
`orphans`, `widows`, and the legacy `page-break-*` aliases. There is no proprietary API. This page explains
how NetPdf chooses where a page ends, and which CSS to write to get the layout you want.

> **The short version.** Put this in your print stylesheet and most documents paginate well:
>
> ```css
> h1, h2, h3, h4, h5, h6 { break-after: avoid; }   /* a heading stays with the content after it */
> tr, figure, .card      { break-inside: avoid; }  /* these never split across pages */
> thead { display: table-header-group; }           /* table headers repeat on every page */
> tfoot { display: table-footer-group; }           /* table footers repeat on every page */
> ```
>
> Headings are **not** kept with their content automatically — browsers don't do that either. Add the
> `break-after: avoid` rule, and add it to any element you use as a heading (for example a
> `<div class="section-title">`).

## How NetPdf chooses a page break

NetPdf fills each page from top to bottom and ends the page when the next piece of content does not fit.

| Content | What happens when it does not fit in the space left on the page |
|---|---|
| A paragraph (or any block that holds only text) | It moves to the next page **as a whole**. Only a paragraph taller than a whole page is split between lines, and that split honors `orphans` and `widows`. |
| A block that contains other blocks (`<div>`, `<section>`, `<article>`, a list) | It starts on the current page and breaks **between its children**. The first child must fit, or the whole block moves to the next page. |
| A table | It breaks **between rows**. `<thead>` and `<tfoot>` repeat on every page. |
| A flex or grid container | It moves to the next page as a whole when it fits on one page. A taller one breaks between its items or rows. |
| An image, or any element with `break-inside: avoid` | It moves to the next page as a whole. |
| Anything taller than a whole page | It starts at the top of a new page and continues on the next pages. NetPdf never loops and never drops content. |

A break never leaves an empty page: a forced break at the very top of a page does nothing, and the first
block on a page always stays there.

> **Difference from browsers.** A browser splits a paragraph between lines at the bottom of a page. NetPdf
> moves a paragraph that fits on one page to the next page whole. Your pages can therefore end with a little
> more white space than in a browser's print preview, but no paragraph is split for a short page tail.

## Force a page break

Use `break-before` or `break-after` with one of these values:

| Value | Result |
|---|---|
| `page` (or legacy `always`) | Start a new page. |
| `left` / `right` | Start a new page, and make it a left (even) or right (odd) page. NetPdf inserts a blank page when needed; style it with `@page :blank`. In a right-to-left document, left and right swap. |
| `recto` / `verso` | Like `right` / `left`, but always odd / even page numbers, in any writing direction. |

```css
.invoice             { break-before: page; }   /* each invoice starts a new page */
.invoice:first-child { break-before: auto; }   /* …but not the first one */
.chapter             { break-before: right; }  /* chapters start on a right-hand page */
```

The break works at any depth: a `break-before: page` on an element deep inside other elements still starts a
new page.

## Keep content together

### Keep a heading with the content after it

```css
h2 { break-after: avoid; }
```

NetPdf checks, before it places the heading, whether the **start** of the next block also fits on the page.
If it does not, the heading moves to the next page together with that block. "The start" means:

- the whole next block when it is text (a paragraph moves as a whole, see above);
- the first child of the next block when it contains other blocks (a `<section>` whose first paragraph fits
  can start under the heading);
- about two lines when it is a table, a grid, or a flex container.

`break-before: avoid` on the block after the heading has the same effect. So does the legacy
`page-break-after: avoid`.

Rules and limits:

- **Chains work.** An `h2` followed by an `h3` followed by a paragraph, with `break-after: avoid` on both
  headings, moves as one unit. NetPdf follows up to three kept elements in a row.
- **The first block on a page never moves.** A heading at the top of a page stays there.
- **Avoid is a preference.** If the heading and the start of its content cannot fit together even on an
  empty page (for example a heading followed by an image taller than a page), NetPdf keeps the heading where
  it is instead of creating a wasted page.

### Keep a whole element on one page

```css
figure, .card, .signature-block { break-inside: avoid; }
tr { break-inside: avoid; }
```

An element with `break-inside: avoid` that does not fit in the space left moves to the next page as a whole.
If it is taller than a whole page, it has to split anyway.

### Control where a long paragraph splits

`orphans` (lines left at the bottom of a page) and `widows` (lines carried to the top of the next page)
control where NetPdf splits a paragraph that is taller than a page. Both default to 2.

```css
p { orphans: 3; widows: 3; }
```

## Recipes

**Report with numbered sections**

```css
h1, h2, h3 { break-after: avoid; }
section    { break-before: auto; }
table      { width: 100%; }
thead      { display: table-header-group; }
tr         { break-inside: avoid; }
```

**Invoice with a totals block that must not split**

```css
.totals, .payment-details { break-inside: avoid; }
```

**One record per page**

```css
.record + .record { break-before: page; }
```

**A heading made from a `div`**

```html
<div class="section-title">Highlights</div>
<ul>…</ul>
```
```css
.section-title { break-after: avoid; }
```

## Troubleshooting

| You see | Cause | Fix |
|---|---|---|
| A heading alone at the bottom of a page | No `break-after: avoid` on it (NetPdf, like browsers, does not keep headings automatically). | Add `break-after: avoid` to the heading, or `break-before: avoid` to the block after it. |
| A heading still alone although it has `break-after: avoid` | It is the first block on the page, or it and the start of its content are taller than a page. | Make the content after the heading start with a smaller block (for example a short introduction paragraph). |
| A large empty space at the bottom of a page | A paragraph or a `break-inside: avoid` element did not fit and moved whole. | Remove `break-inside: avoid` from very tall elements, or split a very long paragraph. |
| A blank page | A `left` / `right` / `recto` / `verso` break needed the next page to be a specific side. | Use `page` if the side does not matter. |
| A table header missing on later pages | The header rows are in `<tbody>`. | Put them in `<thead>` (and keep `thead { display: table-header-group; }`). |
