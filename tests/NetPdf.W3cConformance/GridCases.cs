// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;

namespace NetPdf.W3cConformance;

/// <summary>Per Phase 3 PR 1 — curated CSS Grid L1 conformance cases. Each
/// asserts spec-correct border-box geometry of grid items. Covers explicit
/// track sizing (px + fr), row-major auto-placement, implicit rows, gaps,
/// line-based placement, and spanning.</summary>
internal static class GridCases
{
    private static string Doc(string body) =>
        "<!DOCTYPE html><html><body style=\"margin:0\">" + body + "</body></html>";

    // A grid container fills its PARENT's content width (it doesn't honor an
    // explicit `width` on itself), so fr-track cases nest the grid inside a sized
    // block so the fraction base is the parent width.
    private static string Doc(int parentWidth, string gridBody) =>
        Doc($"<div style='width:{parentWidth}px'>{gridBody}</div>");

    public static IReadOnlyList<ConformanceCase> All { get; } = new[]
    {
        // §8.5 — row-major auto-placement into a 2×2 explicit grid.
        new ConformanceCase("grid-2x2-auto-placement", "CSS Grid L1 §8.5",
            Doc("<div style='display:grid;grid-template-columns:100px 100px;"
                + "grid-template-rows:50px 50px'>"
                + "<div id='a'></div><div id='b'></div><div id='c'></div><div id='d'></div></div>"),
            new[]
            {
                new BoxExpectation("a", X: 0, Y: 0, Width: 100, Height: 50),
                new BoxExpectation("b", X: 100, Y: 0),
                new BoxExpectation("c", X: 0, Y: 50),
                new BoxExpectation("d", X: 100, Y: 50),
            }),

        // §7.2 — explicit px column tracks size the items.
        new ConformanceCase("grid-explicit-columns", "CSS Grid L1 §7.2",
            Doc("<div style='display:grid;grid-template-columns:80px 150px;grid-auto-rows:40px'>"
                + "<div id='a'></div><div id='b'></div></div>"),
            new[]
            {
                new BoxExpectation("a", X: 0, Width: 80, Height: 40),
                new BoxExpectation("b", X: 80, Width: 150),
            }),

        // §7.2.3 — fr tracks split the container width equally.
        new ConformanceCase("grid-fr-columns", "CSS Grid L1 §7.2.3",
            Doc(400, "<div style='display:grid;grid-template-columns:1fr 1fr;grid-auto-rows:40px'>"
                + "<div id='a'></div><div id='b'></div></div>"),
            new[]
            {
                new BoxExpectation("a", X: 0, Width: 200),
                new BoxExpectation("b", X: 200, Width: 200),
            }),

        // §7.2.3 — asymmetric fr tracks (1fr 2fr) split 1:2.
        new ConformanceCase("grid-fr-asymmetric", "CSS Grid L1 §7.2.3",
            Doc(300, "<div style='display:grid;grid-template-columns:1fr 2fr;grid-auto-rows:40px'>"
                + "<div id='a'></div><div id='b'></div></div>"),
            new[]
            {
                new BoxExpectation("a", X: 0, Width: 100),
                new BoxExpectation("b", X: 100, Width: 200),
            }),

        // §7.1 — implicit (auto) rows stack at the auto-row size.
        new ConformanceCase("grid-implicit-rows", "CSS Grid L1 §7.1/§7.5",
            Doc("<div style='display:grid;grid-template-columns:100px;grid-auto-rows:60px'>"
                + "<div id='a'></div><div id='b'></div><div id='c'></div></div>"),
            new[]
            {
                new BoxExpectation("a", Y: 0, Height: 60),
                new BoxExpectation("b", Y: 60),
                new BoxExpectation("c", Y: 120),
            }),

        // §10.1 — column-gap inserts a gutter between column tracks.
        new ConformanceCase("grid-column-gap", "CSS Grid L1 §10.1",
            Doc("<div style='display:grid;grid-template-columns:100px 100px;"
                + "column-gap:20px;grid-auto-rows:40px'>"
                + "<div id='a'></div><div id='b'></div></div>"),
            new[]
            {
                new BoxExpectation("a", X: 0),
                new BoxExpectation("b", X: 120), // 100 + 20 gap
            }),

        // §10.1 — row-gap inserts a gutter between row tracks.
        new ConformanceCase("grid-row-gap", "CSS Grid L1 §10.1",
            Doc("<div style='display:grid;grid-template-columns:100px;"
                + "grid-template-rows:50px 50px;row-gap:30px'>"
                + "<div id='a'></div><div id='b'></div></div>"),
            new[]
            {
                new BoxExpectation("a", Y: 0),
                new BoxExpectation("b", Y: 80), // 50 + 30 gap
            }),

        // §10.1 — the `gap` shorthand sets both row-gap + column-gap; three fixed
        // columns gutter-spaced.
        new ConformanceCase("grid-gap-shorthand-columns", "CSS Grid L1 §10.1",
            Doc("<div style='display:grid;grid-template-columns:60px 60px 60px;"
                + "gap:15px;grid-auto-rows:40px'>"
                + "<div id='a'></div><div id='b'></div><div id='c'></div></div>"),
            new[]
            {
                new BoxExpectation("a", X: 0),
                new BoxExpectation("b", X: 75),  // 60 + 15
                new BoxExpectation("c", X: 150), // 60 + 15 + 60 + 15
            }),

        // §10.1 + §7.2.3 — column-gap reduces the space `fr` tracks distribute:
        // 2 fr in a 400px parent with column-gap:20 → each fr = (400-20)/2 = 190.
        // (NetPdf positions the gutters but doesn't yet subtract them from the fr
        // free space — `grid-gap-fr-track-sizing` deferral; EXPECTED is spec.)
        new ConformanceCase("grid-fr-columns-with-gap", "CSS Grid L1 §7.2.3/§10.1",
            Doc(400, "<div style='display:grid;grid-template-columns:1fr 1fr;"
                + "column-gap:20px;grid-auto-rows:40px'>"
                + "<div id='a'></div><div id='b'></div></div>"),
            new[]
            {
                new BoxExpectation("a", X: 0, Width: 190),
                new BoxExpectation("b", X: 210, Width: 190), // 190 + 20 gap
            },
            KnownGap: "fr tracks don't subtract gaps from their distributed free space"),

        // §8.3 — line-based placement (grid-column / grid-row) drops an item
        // into a specific cell.
        new ConformanceCase("grid-line-placement", "CSS Grid L1 §8.3",
            Doc("<div style='display:grid;grid-template-columns:100px 100px;"
                + "grid-template-rows:50px 50px'>"
                + "<div id='a' style='grid-column:2;grid-row:2'></div></div>"),
            new[] { new BoxExpectation("a", X: 100, Y: 50) }),

        // §8.3 — a column span makes an item cover two tracks.
        new ConformanceCase("grid-column-span", "CSS Grid L1 §8.3",
            Doc("<div style='display:grid;grid-template-columns:100px 100px;grid-auto-rows:40px'>"
                + "<div id='a' style='grid-column:1 / span 2'></div></div>"),
            new[] { new BoxExpectation("a", X: 0, Width: 200) }),

        // §7.3 — repeat() expands to N identical tracks.
        new ConformanceCase("grid-repeat-columns", "CSS Grid L1 §7.2.3",
            Doc("<div style='display:grid;grid-template-columns:repeat(3,100px);grid-auto-rows:40px'>"
                + "<div id='a'></div><div id='b'></div><div id='c'></div></div>"),
            new[]
            {
                new BoxExpectation("a", X: 0),
                new BoxExpectation("b", X: 100),
                new BoxExpectation("c", X: 200),
            }),

        // §7.2.3 — an explicit `width` on the GRID container sizes the fr tracks:
        // a 200px grid with 1fr 1fr splits into two 100px columns (against the
        // declared 200, not the 600px page). No gap → unaffected by the fr-gap
        // deferral.
        new ConformanceCase("grid-explicit-container-width", "CSS Grid L1 §7.2.3",
            Doc("<div style='display:grid;width:200px;grid-template-columns:1fr 1fr;"
                + "grid-auto-rows:40px'><div id='a'></div><div id='b'></div></div>"),
            new[]
            {
                new BoxExpectation("a", X: 0, Width: 100),
                new BoxExpectation("b", X: 100, Width: 100),
            }),
    };
}
