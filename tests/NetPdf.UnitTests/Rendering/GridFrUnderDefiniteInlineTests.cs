// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// Corpus-fidelity (10 event-ticket) — the <c>LAYOUT-GRID-FR-UNDER-INDEFINITE-APPROXIMATED-001</c>
/// diagnostic keyed purely on the <c>width</c>/<c>height</c> SLOT being <c>auto</c>. But an
/// <c>auto</c>-width grid nested in a definite containing chain (a <c>repeat(4,1fr)</c> grid inside a
/// flex item) has a DEFINITE used inline size per CSS Grid L1 §5.2, so its fr columns resolve exactly —
/// the warning was a false positive. It's now suppressed on the INLINE axis when the used width is a
/// real positive value, while the BLOCK axis (auto-height fr rows sized against the fragmentainer
/// fallback) still warns because that extent is genuinely indefinite.
/// </summary>
public sealed class GridFrUnderDefiniteInlineTests
{
    private static int FrUnderIndefiniteWarnings(string html)
    {
        var res = HtmlPdf.ConvertDetailed(html, new HtmlPdfOptions { PrintBackgrounds = true });
        var n = 0;
        foreach (var w in res.Warnings)
            if (w.Code.Contains("GRID-FR-UNDER-INDEFINITE")) n++;
        return n;
    }

    [Fact]
    public void Auto_width_fr_columns_in_definite_container_do_not_warn()
    {
        // `repeat(4, 1fr)` columns, grid has no `width` → auto, but the parent is 400px wide → the used
        // inline size is definite, so fr resolves exactly. No approximation warning.
        Assert.Equal(0, FrUnderIndefiniteWarnings(
            "<!DOCTYPE html><html><head><style>"
            + ".w{width:400px}.g{display:grid;grid-template-columns:repeat(4,1fr);gap:8px}"
            + "</style></head><body><div class=\"w\"><div class=\"g\">"
            + "<span>a</span><span>b</span><span>c</span><span>d</span></div></div></body></html>"));
    }

    [Fact]
    public void Auto_height_fr_rows_still_warn()
    {
        // fr ROWS with auto height: the block extent is the fragmentainer fallback (indefinite), so fr
        // rows genuinely approximate — the diagnostic must still surface it.
        Assert.True(FrUnderIndefiniteWarnings(
            "<!DOCTYPE html><html><head><style>"
            + ".g{display:grid;grid-template-rows:repeat(3,1fr)}"
            + "</style></head><body><div class=\"g\">"
            + "<span>a</span><span>b</span><span>c</span></div></body></html>") >= 1);
    }
}
