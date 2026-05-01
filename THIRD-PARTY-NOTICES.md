# Third-Party Notices

NetPdf depends on the following third-party components. Each is used in compliance with its respective license.

This file is distributed alongside the NetPdf NuGet package and is referenced from the `NOTICE` file as required by Apache-2.0 §4(d).

---

## Runtime dependencies (shipped via NuGet transitive references)

### AngleSharp
- **SPDX:** MIT
- **Copyright:** Copyright (c) 2013–2026 Florian Rappl and AngleSharp contributors
- **Source:** https://github.com/AngleSharp/AngleSharp
- **License text:** https://github.com/AngleSharp/AngleSharp/blob/main/LICENSE
- **Used for:** HTML5 parsing and DOM only. Configured without scripting support.

### AngleSharp.Css
- **SPDX:** MIT
- **Copyright:** Copyright (c) 2017–2026 Florian Rappl and AngleSharp contributors
- **Source:** https://github.com/AngleSharp/AngleSharp.Css
- **License text:** https://github.com/AngleSharp/AngleSharp.Css/blob/devel/LICENSE
- **Used for:** CSS tokenizer + parser + selector input. NetPdf consumes the parsed AST through an adapter and runs its own cascade and computed-value resolution on top.

### HarfBuzzSharp
- **SPDX:** MIT
- **Copyright:** Copyright (c) Microsoft Corporation and Mono Project contributors
- **Source:** https://github.com/mono/SkiaSharp
- **License text:** https://github.com/mono/SkiaSharp/blob/main/LICENSE.md
- **Used for:** P/Invoke wrapper around HarfBuzz for OpenType text shaping.

### HarfBuzz (native)
- **SPDX:** OldMIT (a permissive MIT-style license)
- **Copyright:** Copyright © 2010–2026 Google, Inc.; 2018–2020 Ebrahim Byagowi; 2004–2013 Red Hat, Inc.; 1998–2004 David Turner and Werner Lemberg; and additional contributors as listed in the HarfBuzz COPYING file.
- **Source:** https://github.com/harfbuzz/harfbuzz
- **License text:** https://github.com/harfbuzz/harfbuzz/blob/main/COPYING
- **Used for:** Bundled native shaping library invoked via HarfBuzzSharp.

### SkiaSharp
- **SPDX:** MIT
- **Copyright:** Copyright (c) 2015–2026 Microsoft Corporation
- **Source:** https://github.com/mono/SkiaSharp
- **License text:** https://github.com/mono/SkiaSharp/blob/main/LICENSE.md
- **Used for:** Image decoding (PNG/JPEG/WebP/AVIF) and subtree raster fallback for filters/conic-gradients/blurred-shadows/complex-clip-paths. Not used in the primary graphics path.

### Skia (native)
- **SPDX:** BSD-3-Clause
- **Copyright:** Copyright (c) 2011–2026 Google LLC and contributors
- **Source:** https://skia.org/
- **License text:** https://skia.googlesource.com/skia/+/main/LICENSE
- **Used for:** Bundled native graphics library invoked via SkiaSharp.

---

## Embedded data (shipped inside NetPdf assemblies)

### Unicode Character Database (UCD) tables
- **SPDX:** Unicode-DFS-2016
- **Copyright:** Copyright © 1991–2026 Unicode, Inc. All rights reserved.
- **Source:** https://www.unicode.org/Public/
- **License text:** https://www.unicode.org/license.html
- **Used for:** Bidi properties (UAX #9), line break classes (UAX #14), word/grapheme break properties (UAX #29), script extensions, joining types — embedded as compiled .NET resources.

### Liang hyphenation patterns
- **SPDX:** Various per-language; predominantly LPPL-1.3+ (TeX) for many languages
- **Source:** https://www.ctan.org/tex-archive/language/hyph-utf8/tex/generic/hyph-utf8/patterns/
- **Used for:** Hyphenation pattern data tables for `hyphens: auto` rendering. Each language pattern file's specific copyright and license is preserved in `src/NetPdf.Text/Hyphenation/Patterns/LICENSES.md`.

### sRGB v4 ICC profile
- **SPDX:** Public domain / ICC license
- **Copyright:** Copyright © International Color Consortium
- **Source:** https://www.color.org/srgbprofiles.xalter
- **Used for:** Embedded in PDF/A output (post-v1) as the default OutputIntent ICC profile.

---

## Test-only dependencies (NOT shipped in the NetPdf NuGet package)

These are listed for transparency. They are referenced only by test projects under `tests/` and never appear in the runtime assembly's dependency graph.

- **xUnit** (Apache-2.0) — unit test framework.
- **BenchmarkDotNet** (MIT) — performance benchmarking.
- **SharpFuzz** (MIT) — fuzz testing harness.
- **Microsoft.Playwright** (Apache-2.0) — Chromium reference renderer for visual-regression tests.
- **PDFium** (BSD-3-Clause) — independent PDF parser used for structure validation.
- **qpdf** (Apache-2.0, invoked as external binary in CI) — independent PDF validator.
- **veraPDF** (GPL-3.0, invoked as external binary in CI for PDF/A validation) — never linked into runtime; CI tool only.

The GPL-3.0 license of veraPDF applies only to its own redistribution. Invoking it as an external command-line validator in CI does not impose any license obligation on NetPdf, which remains Apache-2.0 throughout.
