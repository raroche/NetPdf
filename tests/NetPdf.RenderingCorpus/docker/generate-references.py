#!/usr/bin/env python3
# Copyright 2026 Roland Aroche and NetPdf contributors.
# Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.
#
# Visual-regression REFERENCE generator (maintainer task — runs in the pinned-Chrome Docker image, see
# README.md). For each diffable corpus invoice it drives Playwright Chromium to print the page to PDF, then
# rasterizes EVERY page at the harness DPI via PDFium (pypdfium2) and writes one PNG per page,
# references/<stem>-page-NNN.png (1-based), matching the runner's per-page diff contract.
#
# This is the Chrome ORACLE side only; NetPdf never bundles a browser. Run it deliberately, then commit the
# regenerated PNGs — never wire it into CI (upstream Chrome/font drift must not silently change references).
#
#   docker run --rm -v "$PWD:/work" -w /work netpdf-visual-refs \
#       python3 tests/NetPdf.RenderingCorpus/docker/generate-references.py

import pathlib
import sys

# Keep in sync with VisualHarness.Dpi and VisualHarness.DiffableInvoices (the C# runner reads the same set).
# The harness corpus holds SELF-CONTAINED invoices (remote assets vendored as data: URIs) so Chrome fetches
# nothing the NetPdf side blocks — a hard requirement for a deterministic diff.
DPI = 300
# Keep in sync with VisualHarness.DiffableInvoices (the C# runner diffs the same set). Chrome resolves these
# invoices' font families to the committed DejaVu Sans via the image's fontconfig pin (00-netpdf-pin.conf),
# matching NetPdf's PinnedFontResolver so the diff isolates layout deltas from font differences.
DIFFABLE_INVOICES = [
    "01-classic-pure-css.html",
    "04-anvil-running-elements.html",
]

REPO_ROOT = pathlib.Path(__file__).resolve().parents[3]
CORPUS_DIR = REPO_ROOT / "tests" / "NetPdf.RenderingCorpus" / "corpus"
REFERENCE_DIR = REPO_ROOT / "tests" / "NetPdf.RenderingCorpus" / "references"


def main() -> int:
    try:
        from playwright.sync_api import sync_playwright
        import pypdfium2 as pdfium
    except ImportError as exc:  # pragma: no cover - maintainer environment
        print(f"missing dependency: {exc}. Run inside the docker image (see README.md).", file=sys.stderr)
        return 2

    REFERENCE_DIR.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as pw:
        browser = pw.chromium.launch()
        try:
            for invoice in DIFFABLE_INVOICES:
                html_path = CORPUS_DIR / invoice
                page = browser.new_page()
                page.goto(html_path.as_uri(), wait_until="networkidle")
                pdf_bytes = page.pdf(print_background=True, prefer_css_page_size=True)
                page.close()

                # Rasterize EVERY page (page counters / running headers / fragmentation regress on later
                # pages, so the gate diffs page-for-page) → references/<stem>-page-NNN.png (1-based).
                doc = pdfium.PdfDocument(pdf_bytes)
                stem = pathlib.Path(invoice).stem
                for page_index in range(len(doc)):
                    bitmap = doc[page_index].render(scale=DPI / 72.0)
                    image = bitmap.to_pil()
                    out = REFERENCE_DIR / f"{stem}-page-{page_index + 1:03d}.png"
                    image.save(out)
                    print(f"wrote {out.relative_to(REPO_ROOT)} ({image.width}x{image.height} @ {DPI} dpi)")
        finally:
            browser.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
