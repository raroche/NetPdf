# Visual-regression reference generator — pinned Chrome on Linux (maintainer task; see README.md).
#
# Pin the Playwright version (which pins a specific Chromium revision) — NEVER use a floating tag — so the
# committed reference PNGs are reproducible and CI (Linux) does not drift against a developer's macOS fonts.
# Bump this tag deliberately as part of a reference-regeneration, never implicitly.
FROM mcr.microsoft.com/playwright/python:v1.49.0-jammy

# pypdfium2 rasterizes the Chrome print-to-PDF at the harness DPI (PDFium — SkiaSharp cannot read PDF).
# Install playwright into the SAME python that runs the generator too: the base image ships Playwright but
# in a location the plain `python3 generate-references.py` invocation didn't import ("No module named
# 'playwright'"); reinstalling via pip puts it on the invoked interpreter's path alongside pypdfium2. Chromium
# itself is already bundled in the base image, so no `playwright install` is needed.
RUN pip install --no-cache-dir playwright==1.49.0 pypdfium2==4.30.0

# A PINNED font pack used by BOTH Chrome and NetPdf keeps the diff stable across machines. Copy the repo's
# committed DejaVu Sans pack in and alias the corpus families + generic sans-serif to it (00-netpdf-pin.conf),
# so Chrome shapes the SAME glyphs the NetPdf-side PinnedFontResolver does. Build context = repo ROOT:
#   docker build -f tests/NetPdf.RenderingCorpus/docker/Dockerfile -t netpdf-visual-refs .
COPY tests/NetPdf.RenderingCorpus/fonts/ /usr/share/fonts/netpdf/
COPY tests/NetPdf.RenderingCorpus/docker/00-netpdf-pin.conf /etc/fonts/conf.d/00-netpdf-pin.conf
RUN fc-cache -f

WORKDIR /work
# The generator is invoked explicitly (see README.md) so the image is a reusable reference-rendering tool:
#   docker run --rm -v "$PWD:/work" -w /work netpdf-visual-refs \
#       python3 tests/NetPdf.RenderingCorpus/docker/generate-references.py
