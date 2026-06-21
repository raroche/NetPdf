// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;

namespace NetPdf.W3cConformance;

/// <summary>Per Phase 3 PR 1 — curated CSS Flexbox L1 conformance cases. Each
/// asserts spec-correct border-box geometry of flex items. Items carry explicit
/// sizes so the cases are deterministic without text metrics. Covers main-axis
/// packing (row / column / flex-start), justify-content, align-items, flex-grow
/// distribution, and multi-line wrap.</summary>
internal static class FlexboxCases
{
    private static string Doc(string body) =>
        "<!DOCTYPE html><html><body style=\"margin:0\">" + body + "</body></html>";

    // A flex/grid container fills its PARENT's content width (it doesn't honor an
    // explicit `width` on itself), so a constrained-width case nests the flex
    // container inside a sized block — the standard authoring pattern.
    private static string Doc(int parentWidth, string flexBody) =>
        Doc($"<div style='width:{parentWidth}px'>{flexBody}</div>");

    private static string Item(string id, int w, int h) =>
        $"<div id='{id}' style='width:{w}px;height:{h}px'></div>";

    public static IReadOnlyList<ConformanceCase> All { get; } = new[]
    {
        // §5.1 — row direction packs items along the inline axis from the start.
        new ConformanceCase("flex-row-flex-start", "CSS Flexbox L1 §5.1/§9.5",
            Doc("<div style='display:flex'>" + Item("a", 100, 50) + Item("b", 120, 50) + "</div>"),
            new[]
            {
                new BoxExpectation("a", X: 0, Y: 0, Width: 100, Height: 50),
                new BoxExpectation("b", X: 100, Y: 0, Width: 120, Height: 50),
            }),

        // §5.1 — column direction stacks items along the block axis.
        new ConformanceCase("flex-column-stack", "CSS Flexbox L1 §5.1",
            Doc("<div style='display:flex;flex-direction:column'>"
                + Item("a", 100, 40) + Item("b", 100, 60) + "</div>"),
            new[]
            {
                new BoxExpectation("a", X: 0, Y: 0, Height: 40),
                new BoxExpectation("b", X: 0, Y: 40, Height: 60),
            }),

        // §9.5 — three items pack contiguously from the main-start edge.
        new ConformanceCase("flex-row-three-items", "CSS Flexbox L1 §9.5",
            Doc("<div style='display:flex'>"
                + Item("a", 80, 50) + Item("b", 90, 50) + Item("c", 100, 50) + "</div>"),
            new[]
            {
                new BoxExpectation("a", X: 0),
                new BoxExpectation("b", X: 80),
                new BoxExpectation("c", X: 170),
            }),

        // §9.5 — justify-content:center centers the line within the container.
        new ConformanceCase("flex-justify-center", "CSS Flexbox L1 §9.5",
            Doc(400, "<div style='display:flex;justify-content:center'>"
                + Item("a", 100, 50) + "</div>"),
            new[] { new BoxExpectation("a", X: 150, Width: 100) }), // (400-100)/2

        // §9.5 — justify-content:flex-end packs items to the main-end edge.
        new ConformanceCase("flex-justify-end", "CSS Flexbox L1 §9.5",
            Doc(400, "<div style='display:flex;justify-content:flex-end'>"
                + Item("a", 100, 50) + "</div>"),
            new[] { new BoxExpectation("a", X: 300) }), // 400-100

        // §9.5 — justify-content:space-between pins first + last to the edges.
        new ConformanceCase("flex-justify-space-between", "CSS Flexbox L1 §9.5",
            Doc(400, "<div style='display:flex;justify-content:space-between'>"
                + Item("a", 100, 50) + Item("b", 100, 50) + "</div>"),
            new[]
            {
                new BoxExpectation("a", X: 0),
                new BoxExpectation("b", X: 300), // 400-100
            }),

        // §9.4 — align-items:center centers items on the cross axis.
        new ConformanceCase("flex-align-items-center", "CSS Flexbox L1 §9.4",
            Doc("<div style='display:flex;align-items:center;height:200px'>"
                + Item("a", 100, 50) + "</div>"),
            new[] { new BoxExpectation("a", Y: 75, Height: 50) }), // (200-50)/2

        // §9.4 — align-items:flex-end aligns items to the cross-end edge.
        new ConformanceCase("flex-align-items-end", "CSS Flexbox L1 §9.4",
            Doc("<div style='display:flex;align-items:flex-end;height:200px'>"
                + Item("a", 100, 50) + "</div>"),
            new[] { new BoxExpectation("a", Y: 150) }), // 200-50

        // §7.1 — flex-grow distributes free space equally.
        new ConformanceCase("flex-grow-equal", "CSS Flexbox L1 §7.1/§9.7",
            Doc(400, "<div style='display:flex'>"
                + "<div id='a' style='flex-grow:1;height:50px'></div>"
                + "<div id='b' style='flex-grow:1;height:50px'></div></div>"),
            new[]
            {
                new BoxExpectation("a", X: 0, Width: 200),
                new BoxExpectation("b", X: 200, Width: 200),
            }),

        // §9.3 — flex-wrap:wrap moves an over-wide item onto the next line.
        new ConformanceCase("flex-wrap-second-line", "CSS Flexbox L1 §9.3",
            Doc(250, "<div style='display:flex;flex-wrap:wrap'>"
                + Item("a", 100, 50) + Item("b", 100, 50) + Item("c", 100, 50) + "</div>"),
            new[]
            {
                new BoxExpectation("a", X: 0, Y: 0),
                new BoxExpectation("b", X: 100, Y: 0),
                new BoxExpectation("c", X: 0, Y: 50), // wraps under the first line
            }),

        // §8.1 — the `gap` shorthand spaces flex items along the main axis.
        new ConformanceCase("flex-gap-main-axis", "CSS Box Alignment L3 §8 / Flexbox L1",
            Doc("<div style='display:flex;gap:20px'>" + Item("a", 100, 50) + Item("b", 100, 50) + "</div>"),
            new[]
            {
                new BoxExpectation("a", X: 0),
                new BoxExpectation("b", X: 120), // 100 + 20 gap
            }),

        // §8 — `column-gap` is the main-axis gutter for row direction (3 items).
        new ConformanceCase("flex-column-gap-three-items", "CSS Box Alignment L3 §8",
            Doc("<div style='display:flex;column-gap:10px'>"
                + Item("a", 80, 50) + Item("b", 80, 50) + Item("c", 80, 50) + "</div>"),
            new[]
            {
                new BoxExpectation("b", X: 90),  // 80 + 10
                new BoxExpectation("c", X: 180), // 80 + 10 + 80 + 10
            }),

        // §8 — the main-axis gaps consume free space BEFORE justify-content
        // distributes the remainder: in a 400px parent, gap:20 then flex-end packs
        // both items (200) + their gap (20) against the end edge.
        new ConformanceCase("flex-gap-with-justify-end", "CSS Box Alignment L3 §8",
            Doc(400, "<div style='display:flex;gap:20px;justify-content:flex-end'>"
                + Item("a", 100, 50) + Item("b", 100, 50) + "</div>"),
            new[]
            {
                new BoxExpectation("a", X: 180), // freeSpace = 400 - 200 - 20 = 180
                new BoxExpectation("b", X: 300), // 180 + 100 + 20 gap
            }),

        // §8 — `row-gap` is the main-axis gutter for COLUMN direction.
        new ConformanceCase("flex-row-gap-column-direction", "CSS Box Alignment L3 §8",
            Doc("<div style='display:flex;flex-direction:column;row-gap:15px'>"
                + Item("a", 100, 40) + Item("b", 100, 60) + "</div>"),
            new[]
            {
                new BoxExpectation("a", Y: 0),
                new BoxExpectation("b", Y: 55), // 40 + 15 gap
            }),

        // §8 — cross-axis gutter (`row-gap`) between WRAPPED lines (row direction).
        new ConformanceCase("flex-row-gap-wrapped-lines", "CSS Box Alignment L3 §8",
            Doc(250, "<div style='display:flex;flex-wrap:wrap;row-gap:30px'>"
                + Item("a", 100, 50) + Item("b", 100, 50) + Item("c", 100, 50) + "</div>"),
            new[]
            {
                new BoxExpectation("a", X: 0, Y: 0),
                new BoxExpectation("b", X: 100, Y: 0),
                new BoxExpectation("c", X: 0, Y: 80), // wraps; 50 line + 30 row-gap
            }),

        // §9.2 — an explicit `width` on the flex container itself sizes its main
        // axis. NetPdf's flex/grid containers ignore their own `width` and fill
        // the parent content width instead (the rest of this suite works AROUND
        // that gap by nesting in a sized parent). This case exercises the gap
        // HEAD-ON so the published Flexbox rate isn't inflated by avoiding it
        // (PR 1 review [P1]): a 200px container with justify-content:flex-end
        // must place the 100px item at X=100; the engine fills the 600px body
        // and places it at X=500. EXPECTED is spec.
        new ConformanceCase("flex-explicit-container-width", "CSS Flexbox L1 §9.2",
            Doc("<div style='display:flex;width:200px;justify-content:flex-end'>"
                + Item("a", 100, 50) + "</div>"),
            new[] { new BoxExpectation("a", X: 100) }, // 200 - 100
            KnownGap: "flex container ignores its own explicit width, fills the parent"),
    };
}
