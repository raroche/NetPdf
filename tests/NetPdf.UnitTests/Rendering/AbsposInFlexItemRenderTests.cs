// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Linq;
using System.Text;
using NetPdf;
using NetPdf.Paginate.Diagnostics;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// RC2 (travel-doc corpus fidelity) — a <c>position: absolute</c> decoration whose
/// containing block is a <c>position: relative</c> FLEX ITEM (a card corner, a day-badge,
/// a bullet dot) must render. Flex is not a delegation boundary, so before the fix the
/// item's geometry wasn't recorded and the abspos pass DROPPED the decoration with
/// <c>LAYOUT-ABSOLUTE-FEATURE-UNSUPPORTED-001</c>. The facade-level paint proof: the red
/// fill operator is present AND no unsupported-feature diagnostic is emitted.
/// </summary>
public sealed class AbsposInFlexItemRenderTests
{
    private static (string pdf, System.Collections.Generic.IReadOnlyList<Diagnostic> warnings) Render(string html)
    {
        var result = HtmlPdf.ConvertDetailed(html, new HtmlPdfOptions { PrintBackgrounds = true });
        return (Encoding.Latin1.GetString(result.Pdf), result.Warnings);
    }

    [Fact]
    public void Abspos_corner_on_a_flex_item_card_renders_and_is_not_dropped()
    {
        // Mirrors the corpus "card with a corner seal" pattern: a flex row of cards, each
        // card (a positioned flex item) carries an absolutely-positioned corner decoration.
        const string html = """
            <!DOCTYPE html><html><head><style>
              .row { display: flex; gap: 10px; }
              .card { position: relative; width: 120px; height: 80px; background: #eee; }
              .corner { position: absolute; top: 0; right: 0; width: 16px; height: 16px; background: #f00; }
            </style></head><body>
              <div class="row">
                <div class="card"><span class="corner"></span></div>
                <div class="card"><span class="corner"></span></div>
              </div>
            </body></html>
            """;

        var (pdf, warnings) = Render(html);
        // The red corner fill paints (RGB 1 0 0 via `rg`).
        Assert.Contains("1 0 0 rg", pdf);
        // The decoration was NOT deferred/dropped.
        Assert.DoesNotContain(warnings, w =>
            w.Code == PaginateDiagnosticCodes.LayoutAbsoluteFeatureUnsupported001);
    }

    [Fact]
    public void Abspos_on_a_non_positioned_flex_item_still_anchors_to_the_page_unchanged()
    {
        // Guard: an abspos child of a NON-positioned flex item is unaffected — it still
        // anchors to the page ICB and paints (the fix only adds recording for POSITIONED
        // items). No unsupported diagnostic either.
        const string html = """
            <!DOCTYPE html><html><head><style>
              .row { display: flex; }
              .item { width: 120px; height: 80px; }
              .dot { position: absolute; top: 10px; left: 10px; width: 8px; height: 8px; background: #00f; }
            </style></head><body>
              <div class="row"><div class="item"><span class="dot"></span></div></div>
            </body></html>
            """;

        var (pdf, warnings) = Render(html);
        Assert.Contains("0 0 1 rg", pdf);   // blue dot paints
        Assert.DoesNotContain(warnings, w =>
            w.Code == PaginateDiagnosticCodes.LayoutAbsoluteFeatureUnsupported001);
    }
}
