# Diagnostics

NetPdf never corrupts output silently. When input can't be rendered identically to a browser print
pipeline, it emits a **stable, versioned diagnostic code**. Codes are grouped by prefix and never change
meaning once published — new conditions get new codes. The full catalog is
[`docs/diagnostics-codes.md`](https://github.com/raroche/NetPdf/blob/main/docs/diagnostics-codes.md).

## Severity

| Severity | Meaning |
|---|---|
| **Error** | Input rejected; no PDF is produced when `FeatureFlags.StrictUnsupportedCss` is set. |
| **Warning** | Feature degraded or skipped; a valid PDF is still produced. |
| **Info** | Feature took an alternative path (e.g. the raster fallback). Output correctness is unchanged. |

## Code prefixes

| Prefix | Area |
|---|---|
| `HTML-*` | HTML parsing & content (scripts, media, DOM limits, sanitization). |
| `CSS-*` | CSS parsing & rendering (unsupported properties, invalid values, amplification caps). |
| `LAYOUT-*` | Layout engine (unsupported box types, recursion depth, atomic inlines). |
| `PDF-*` | PDF emission (page/output caps, resource limits). |
| `FONT-*` | Font resolution, subsetting, and safety validation. |
| `SVG-*` | SVG parsing & rendering. |
| `IMG-*` | Image decoding and embedding. |

Each code is a single, greppable token — for example `HTML-SCRIPT-IGNORED-001` (a `<script>` was removed) or
`LAYOUT-RECURSION-DEPTH-EXCEEDED-001` (untrusted markup nested past the layout depth guard, degraded to a
valid PDF). Consume them from the diagnostics sink your integration wires up, or treat a typed
[`HtmlPdfException`](api/NetPdf.HtmlPdfException.yml)'s `Code` at the render boundary.

## Adding a code

Contributors: new diagnostics are registered in `docs/diagnostics-codes.md` and validated by
`DiagnosticCodesTests`. Run the `/add-diagnostic-code` project skill to keep the doc, the enum, and the test
in sync.
