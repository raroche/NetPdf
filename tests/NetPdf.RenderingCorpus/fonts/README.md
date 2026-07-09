# Pinned font pack — visual-regression harness

The **DejaVu Sans** family (RIBBI: Regular, Bold, Oblique, Bold Oblique), committed so the visual-regression
gate is **font-deterministic**. Both sides of the diff use exactly these files:

- **NetPdf side** — `PinnedFontResolver` (`../Visual/PinnedFontResolver.cs`) serves these bytes for every
  family, selected by weight (`>= 600` → Bold) and style (italic/oblique → Oblique). Wired into
  `CorpusVisualRegressionTests` via `VisualHarness.PinnedFonts()`.
- **Chrome side** — the reference generator's Docker image (`../docker/Dockerfile`) copies these in and
  aliases the corpus families + CSS generics to `DejaVu Sans` (`../docker/00-netpdf-pin.conf`).

With fonts controlled on both sides, the page-for-page diff isolates **layout-engine** deltas (NetPdf vs.
Chrome) from font-rendering differences.

Do not swap or bump these files casually — a font change invalidates every committed reference PNG and
requires regenerating them (see `../docker/README.md`).

## License / attribution

DejaVu Fonts — a public-domain-friendly superset of the **Bitstream Vera** fonts. Bitstream Vera is
© 2003 Bitstream, Inc. (Bitstream Vera Fonts Copyright, a permissive MIT-style license); the DejaVu changes
are in the public domain. "DejaVu" and "Bitstream Vera" are not used to promote derivative works without
permission, per the license. Full text: <https://dejavu-fonts.github.io/License.html>. These files are used
here only as **test fixtures** — they are **not** redistributed in any shipped NuGet package.

Source: DejaVu Fonts v2.37 (`ttf/DejaVuSans*.ttf`), <https://github.com/dejavu-fonts/dejavu-fonts>.
