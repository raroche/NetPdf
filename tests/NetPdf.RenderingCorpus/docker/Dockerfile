# Visual-regression reference generator — pinned Chrome on Linux (maintainer task; see README.md).
#
# Pin the Playwright version (which pins a specific Chromium revision) — NEVER use a floating tag — so the
# committed reference PNGs are reproducible and CI (Linux) does not drift against a developer's macOS fonts.
# Bump this tag deliberately as part of a reference-regeneration, never implicitly.
FROM mcr.microsoft.com/playwright/python:v1.49.0-jammy

# pypdfium2 rasterizes the Chrome print-to-PDF at the harness DPI (PDFium — SkiaSharp cannot read PDF).
RUN pip install --no-cache-dir pypdfium2==4.30.0

# A PINNED font pack used by BOTH Chrome and NetPdf keeps the diff stable across machines. Copy the repo's
# committed fonts in here and point fontconfig at them (and wire a matching FontResolver on the NetPdf side).
#   COPY tests/NetPdf.RenderingCorpus/fonts/ /usr/share/fonts/netpdf/
#   RUN fc-cache -f

WORKDIR /work
# The generator is invoked explicitly (see README.md) so the image is a reusable reference-rendering tool:
#   docker run --rm -v "$PWD:/work" -w /work netpdf-visual-refs \
#       python3 tests/NetPdf.RenderingCorpus/docker/generate-references.py
