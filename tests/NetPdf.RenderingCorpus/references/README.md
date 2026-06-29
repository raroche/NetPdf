# Visual-regression reference PNGs

This directory holds the **committed Chrome reference renders** the visual-regression gate
(`CorpusVisualRegressionTests`) diffs the NetPdf output against — one PNG **per page**, named
`<invoice-stem>-page-NNN.png` (1-based, zero-padded), e.g. `01-classic-pure-css-page-001.png`. The gate
diffs page-for-page and asserts the page count matches.

**The directory is intentionally empty of PNGs until a maintainer generates them.** While a
reference is absent the diff runner SKIPS that invoice (it still asserts NetPdf produced a PDF),
so the build stays green. Once a reference lands, the runner starts enforcing the tolerance
(per-pixel RGBA Δ < 4, SSIM ≥ 0.98) for it.

## Generating / regenerating references

References are generated from a **pinned Chrome** (Linux/Docker) at the harness DPI
(`VisualHarness.Dpi` = 300) so CI (Linux) does not drift against a developer's macOS fonts /
anti-aliasing. See [`../docker/README.md`](../docker/README.md) for the runbook.

> Reference regeneration is a **deliberate, separate manual step** — never auto-regenerated in CI —
> so upstream Chrome/font drift can never silently change what the tests assert.
