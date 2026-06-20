// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;

namespace NetPdf.W3cConformance;

/// <summary>Per Phase 3 PR 1 — curated CSS Fragmentation L3 conformance cases.
/// Each asserts that content lands on the correct PAGE (0-based) at the correct
/// resume position when a subtree is taller than the fragmentainer. Pages are
/// 600×400 px so two 200px blocks fill a page exactly. Covers block-flow
/// pagination + resume-at-top, force-overflow forward progress, table row
/// pagination, intra-cell row splitting (PR 7), and grid/flex pagination.</summary>
internal static class FragmentationCases
{
    private static string Doc(string body) =>
        "<!DOCTYPE html><html><body style=\"margin:0\">" + body + "</body></html>";

    private static string Block(string id, int h) =>
        $"<div id='{id}' style='height:{h}px'></div>";

    public static IReadOnlyList<ConformanceCase> All { get; } = new[]
    {
        // §3 — a block stack taller than the page breaks at block boundaries;
        // each page resumes at the content-box top.
        new ConformanceCase("frag-block-stack-paginates", "CSS Fragmentation L3 §3",
            Doc(Block("a", 200) + Block("b", 200) + Block("c", 200) + Block("d", 200) + Block("e", 200)),
            new[]
            {
                new BoxExpectation("a", Page: 0, Y: 0),
                new BoxExpectation("b", Page: 0, Y: 200),
                new BoxExpectation("c", Page: 1, Y: 0),
                new BoxExpectation("d", Page: 1, Y: 200),
                new BoxExpectation("e", Page: 2, Y: 0),
            },
            PageHeightPx: 400),

        // §3 — the first block on a resumed page anchors at the page top.
        new ConformanceCase("frag-resume-anchors-at-top", "CSS Fragmentation L3 §3",
            Doc(Block("a", 300) + Block("b", 300)),
            new[]
            {
                new BoxExpectation("a", Page: 0, Y: 0),
                new BoxExpectation("b", Page: 1, Y: 0), // b doesn't fit under a → page 1 top
            },
            PageHeightPx: 400),

        // §3 — three full-page blocks → one per page.
        new ConformanceCase("frag-one-block-per-page", "CSS Fragmentation L3 §3",
            Doc(Block("a", 380) + Block("b", 380) + Block("c", 380)),
            new[]
            {
                new BoxExpectation("a", Page: 0, Y: 0),
                new BoxExpectation("b", Page: 1, Y: 0),
                new BoxExpectation("c", Page: 2, Y: 0),
            },
            PageHeightPx: 400),

        // §4.4 — forced-overflow forward progress: a block taller than the WHOLE
        // page is emitted at the page top (and overflows) rather than looping.
        new ConformanceCase("frag-oversized-block-force-overflow", "CSS Fragmentation L3 §4.4",
            Doc(Block("a", 800)),
            new[] { new BoxExpectation("a", Page: 0, Y: 0, Height: 800) },
            PageHeightPx: 400),

        // §3 — a block AFTER an oversized one still advances to a later page.
        new ConformanceCase("frag-after-oversized-advances", "CSS Fragmentation L3 §3",
            Doc(Block("a", 600) + Block("b", 100)),
            new[]
            {
                new BoxExpectation("a", Page: 0, Y: 0),
                new BoxExpectation("b", Page: 1, Y: 0),
            },
            PageHeightPx: 400),

        // §3 — table rows paginate at row boundaries (thead/tbody flow).
        new ConformanceCase("frag-table-rows-paginate", "CSS Fragmentation L3 §3 / CSS Tables L3",
            Doc("<table><tbody>"
                + "<tr><td><div id='r0' style='height:180px'></div></td></tr>"
                + "<tr><td><div id='r1' style='height:180px'></div></td></tr>"
                + "<tr><td><div id='r2' style='height:180px'></div></td></tr>"
                + "</tbody></table>"),
            new[]
            {
                new BoxExpectation("r0", Page: 0),
                new BoxExpectation("r2", Page: 1), // 3×180=540 > 400 → row 2 to page 1
            },
            PageHeightPx: 400),

        // PR 7 — intra-cell row splitting: a single row whose cell stacks block
        // children taller than the page breaks WITHIN itself across pages.
        new ConformanceCase("frag-intra-cell-row-split", "CSS Tables L3 / NetPdf PR 7",
            Doc("<table><tbody><tr><td>"
                + Block("c0", 200) + Block("c1", 200) + Block("c2", 200)
                + "</td></tr></tbody></table>"),
            new[]
            {
                new BoxExpectation("c0", Page: 0),
                new BoxExpectation("c2", Page: 1), // 3×200=600 > 400 → split, c2 on page 1
            },
            PageHeightPx: 400),

        // §3 — an auto-row grid taller than the page paginates at row boundaries.
        new ConformanceCase("frag-grid-rows-paginate", "CSS Fragmentation L3 §3 / Grid L1",
            Doc("<div style='display:grid;grid-template-columns:100px;grid-auto-rows:200px'>"
                + "<div id='g0'></div><div id='g1'></div><div id='g2'></div></div>"),
            new[]
            {
                new BoxExpectation("g0", Page: 0),
                new BoxExpectation("g2", Page: 1), // 3×200=600 > 400
            },
            PageHeightPx: 400),

        // §3 — a column flex taller than the page paginates between items.
        new ConformanceCase("frag-flex-column-paginates", "CSS Fragmentation L3 §3 / Flexbox L1",
            Doc("<div style='display:flex;flex-direction:column'>"
                + "<div id='f0' style='height:200px'></div>"
                + "<div id='f1' style='height:200px'></div>"
                + "<div id='f2' style='height:200px'></div></div>"),
            new[]
            {
                new BoxExpectation("f0", Page: 0),
                new BoxExpectation("f2", Page: 1),
            },
            PageHeightPx: 400),

        // §3.1 — `break-before: page` forces a page break even when the block
        // would otherwise fit. (NetPdf does not yet propagate forced-break
        // metadata from the box — honest gap; EXPECTED is spec.)
        new ConformanceCase("frag-break-before-page", "CSS Fragmentation L3 §3.1",
            Doc(Block("a", 100) + "<div id='b' style='height:100px;break-before:page'></div>"),
            new[]
            {
                new BoxExpectation("a", Page: 0, Y: 0),
                new BoxExpectation("b", Page: 1, Y: 0), // forced onto page 1
            },
            PageHeightPx: 400),
    };
}
