# Clean-Room Development Policy

This is the legal contract every contributor agrees to by submitting a pull request to NetPdf. It exists to keep the project provably free of derivative-work claims so it can remain Apache-2.0 forever.

## Principles

1. **Algorithms come from normative specifications, not from reading other implementations.** Permitted sources of algorithmic guidance:
   - W3C specifications (CSS Working Group documents, WHATWG HTML).
   - Unicode Technical Reports & Annexes (UAX #9, #14, #29, #24, etc.).
   - ISO standards (ISO 32000-2:2020 PDF, ISO 14289-1 PDF/UA, ISO 19005 PDF/A).
   - PDF Association errata and technical notes.
   - Public academic papers (Knuth-Plass line breaking, Liang hyphenation, etc.).
   - Other normative specifications you can cite by document number / URL.

2. **Reading other open-source implementations for understanding is permitted; transliterating their code is not.**
   - You may read Servo, Taffy, WeasyPrint, OpenHTMLtoPDF, Skia, Chromium, Firefox, WebKit source code to understand how a hard algorithm works.
   - You may **not** open their source in one window and re-type it into our repo with cosmetic renaming. That is a derivative work even if the language differs.
   - When you read another implementation for non-trivial guidance, **leave a one-line note at the top of the affected file** like `// Algorithm understanding informed by reading WeasyPrint v68's table-fixup logic; no code copied.` This creates a paper trail.

3. **No vendor SDK code, no decompilation, no copy-paste from blogs/Stack Overflow.**
   - Stack Overflow snippets are licensed CC BY-SA 4.0 — incompatible with our Apache-2.0 distribution. Don't copy them.
   - If you need a snippet, re-derive it from the underlying spec or reference docs.

4. **Every dependency must pass written license review before being added.**
   - File a PR that updates `docs/legal/dependency-dossier.md` with: SPDX identifier, version, copyright, link to license text, why it's compatible with Apache-2.0 redistribution.
   - Only after that PR merges may a `<PackageReference>` be added in `Directory.Packages.props`.
   - Banned licenses in core path: AGPL-*, GPL-*, LGPL-*, SSPL, Commons Clause, BUSL, ELv2, any "free under threshold" or revenue-capped license.
   - Allowed in test-only projects (and never shipped in `NetPdf` package): Apache-2.0, MIT, BSD-2/3, MPL-2.0, ISC, Unicode-DFS-2016, GPL/LGPL when only invoked as external CLI tools (e.g., veraPDF).

5. **The banned-deps Roslyn analyzer is the enforcement mechanism.**
   - `build/NetPdf.BannedAnalyzer/` blocks compilation if a banned namespace is referenced.
   - Updates to the banned list go through PR review.

## Patent posture

The project ships under Apache-2.0, which includes an explicit royalty-free patent grant from contributors. By submitting a contribution, you confirm:

- You have the right to license your contribution under Apache-2.0.
- You are not knowingly contributing material that infringes a third party's patent.
- Your contribution does not import code from a competing PDF or HTML engine (Chromium, WebKit, Gecko, iText, PDFsharp, QuestPDF, etc.).

The PDF format itself is covered by Adobe's [royalty-free public patent license for ISO 32000-1:2008](https://www.adobe.com/pdf/pdfs/ISO32000-1PublicPatentLicense.pdf). PDF 2.0 (ISO 32000-2:2020) is governed by ISO's standard fair-reasonable-non-discriminatory patent policy. Implementation of the PDF spec is fully permitted.

## Practical workflow

When writing a hard algorithm (CSS Grid track sizing, font shaping integration, pagination DP):

1. Open the spec in one tab. Read the relevant section.
2. Write a short prose summary of the algorithm in your own words, in a comment at the top of the file.
3. Implement from the prose summary — not from the spec text directly, and certainly not from another implementation.
4. Test against the W3C / Unicode reference test data.

When stuck:

- **OK:** Read the spec again. Read PDF Association errata. Search for an academic paper.
- **OK with attribution diary entry:** Read another implementation (Servo, Taffy, WeasyPrint, Chromium) **for understanding** of where the spec is ambiguous. Note in a file-level comment.
- **Not OK:** Copy code, even with renamed identifiers. Even with reformatting. Even from a "reference" implementation.

## File header

Every source file MUST start with:

```csharp
// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.
```

Enforced by the `NetPdf.HeaderAnalyzer` Roslyn analyzer (NETPDF0010).

## Reporting concerns

If you suspect a contribution may have copied from a non-permissive source, open a private security advisory on the repository. Do not file a public issue.

## Versioning of this policy

This document is versioned alongside the code. Material changes (e.g., adding/removing an allowed license) require a PR with explicit reviewer approval from a maintainer.

Last updated: 2026-04-30.
