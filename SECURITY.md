# Security Policy

NetPdf is a **browserless, non-subprocess** HTML+CSS-to-PDF engine — a managed C# layer over a small set of
vetted native libraries (HarfBuzz shaping, Skia raster) — that renders **attacker-influenceable input** (HTML,
CSS, SVG, images, fonts) server-side. Security is a first-class concern. This document covers how to report a
vulnerability and what is in scope; the full threat model + hardening roadmap lives in
[`docs/security/security-hardening-plan.md`](docs/security/security-hardening-plan.md), and operational
guidance for running NetPdf on untrusted HTML is in the
[README](README.md#running-netpdf-on-untrusted-html).

## Supported versions

NetPdf is **private and pre-1.0**; there is no public NuGet release yet — the public repository and the NuGet
package are both published at `1.0.0`. Until then, security fixes land on the latest development version only
(`main` / the most recent `-rc`). A formal supported-version matrix begins at `1.0.0`.

| Version | Supported |
|---|---|
| `main` / latest `-rc` (currently `0.9.0-rc1`) | ✅ fixes land here |
| Older pre-1.0 tags (`0.1.0-alpha`, `0.7.0-beta`) | ❌ superseded — upgrade to the latest |

## Reporting a vulnerability

**Please do not open a public issue for a security vulnerability.**

Report privately via **GitHub → the repository's *Security* tab → *Report a vulnerability*** (GitHub private
security advisories). This opens a private channel with the maintainer. Include:

- a description of the issue and the vulnerability class (e.g. SSRF, local-file read, XXE, decoder crash, DoS);
- a minimal reproducing input (the HTML/CSS/SVG/font/image) and the `HtmlPdfOptions` / `SecurityPolicy` used;
- the observed impact (data read, outbound request made, crash/OOM, malicious PDF construct emitted).

**Response targets** (best-effort while pre-1.0): acknowledgement within **3 business days**, an initial
assessment within **10 business days**, and a fix or documented mitigation for confirmed High/Critical issues
before the next release. We will credit reporters who wish to be named once a fix ships.

## Scope

**In scope** — anything where NetPdf itself mishandles untrusted input:

- **SSRF / local-file disclosure** — a resource reference (`<img>`, CSS `url()`, `@import`, `@font-face`, SVG
  `<image>`/`<use>`, etc.) reaching an internal service, cloud metadata (`169.254.169.254`), or the local
  filesystem contrary to the active `SecurityPolicy`.
- **XXE / entity-expansion** in the SVG/XML path.
- **Memory-safety / crash / hang** in parsing, layout, or the font/image decode path reachable from input
  (including a validator bypass that feeds unchecked bytes to a native decoder).
- **Denial of service** — input that defeats the documented resource budgets (billion-laughs, decompression /
  font bombs, page/output amplification, unbounded fetch).
- **Malicious PDF output** — an input that makes the writer emit a rejected active-content construct
  (`/Launch`, `/JavaScript`, `/OpenAction`, `/EmbeddedFile`, …) or a dangerous link URI scheme, or that
  injects/breaks PDF syntax via unescaped strings.
- **A `SecurityPolicy` guard that does not hold** (e.g. `UntrustedHtml` making an outbound request, or the
  IP blocklist / redirect / DNS-rebind defenses being bypassable).

**Out of scope** — please report these to the appropriate party instead:

- Vulnerabilities in a **native dependency** itself (SkiaSharp / HarfBuzz / libwebp / PDFium) — report upstream;
  we track and bump per [`docs/legal/dependency-dossier.md`](docs/legal/dependency-dossier.md). A *validator
  bypass* that lets crafted bytes reach such a decoder **is** in scope.
- Your **deployment configuration** (missing container isolation, an open egress route, exposed secrets) —
  see the [untrusted-HTML checklist](README.md#running-netpdf-on-untrusted-html); the library cannot substitute
  for OS-level isolation.
- Findings that require the caller to **explicitly disable a guard** (e.g. `AllowFileScheme = true`,
  `AllowHttpScheme = true`) — that is a documented, opt-in trust decision by the integrator.

## What NetPdf does *not* do (immune-by-design)

To calibrate reports: NetPdf has **no JavaScript engine and no embedded browser**, **spawns no processes at
render time**, and does **no untrusted-input-driven type activation or deserialization**. So classic
HTML-to-PDF RCE paths that rely on a JS engine (V8 CVEs), CLI argument injection, or object deserialization do
not apply. See the threat model for the full class-by-class analysis.
