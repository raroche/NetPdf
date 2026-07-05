// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;

namespace NetPdf.W3cConformance;

/// <summary>Curated CSS Backgrounds and Borders Module Level 3 conformance cases
/// (Phase-5 task 22 — published pass-rate ≥ 90%). The harness reads the LAID-OUT
/// border box, so this category asserts the two spec properties that are
/// geometry-observable in layout:
/// <list type="bullet">
/// <item><b>Borders inset the content box</b> (§4): a border-width on a side moves
/// the in-flow child in by that width and shrinks the available inline size —
/// symmetric, asymmetric, and longhand (`border-style`+`border-width`) forms.</item>
/// <item><b>Paint-only properties do NOT affect layout</b> (§3 backgrounds, §5
/// `border-radius`): setting a background or a corner radius must leave the border
/// box geometry identical to the un-decorated box — the decoration is emitted in
/// the paint pass, not the layouter.</item>
/// </list>
/// Content area is 600×800 px; <c>body</c> margin is zeroed so coordinates are
/// content-area-relative. Boxes are sized (no text) so the run is font-free.</summary>
internal static class BackgroundsBordersCases
{
    private static string Doc(string body) =>
        "<!DOCTYPE html><html><body style=\"margin:0\">" + body + "</body></html>";

    public static IReadOnlyList<ConformanceCase> All { get; } = new[]
    {
        // §4 — a uniform border insets the in-flow child by the border width on
        // every side and shrinks its inline size by the left+right border.
        new ConformanceCase("bnb-uniform-border-insets-child", "CSS Backgrounds & Borders 3 §4",
            Doc("<div id='p' style='border:15px solid #000'><div id='c' style='height:20px'></div></div>"),
            new[] { new BoxExpectation("c", X: 15, Y: 15, Width: 570, Height: 20) }), // 600 - 2×15

        // §4 — asymmetric per-side border widths: the child's X is the left border,
        // its Y the top border, its width reduced by left+right.
        new ConformanceCase("bnb-asymmetric-border-insets", "CSS Backgrounds & Borders 3 §4",
            Doc("<div id='p' style='border-left:10px solid #000;border-right:30px solid #000;"
                + "border-top:5px solid #000'><div id='c' style='height:20px'></div></div>"),
            new[] { new BoxExpectation("c", X: 10, Y: 5, Width: 560, Height: 20) }), // 600-10-30

        // §4 — the `border-style` + `border-width` longhand pair produces the same
        // inset geometry as the shorthand (border paints only when a style is set).
        new ConformanceCase("bnb-border-width-longhand", "CSS Backgrounds & Borders 3 §4",
            Doc("<div id='p' style='border-style:solid;border-width:8px'>"
                + "<div id='c' style='height:20px'></div></div>"),
            new[] { new BoxExpectation("c", X: 8, Y: 8, Width: 584, Height: 20) }), // 600 - 2×8

        // §3 — a background is a paint-only property: it must NOT change the border
        // box geometry (control proving decorations don't leak into layout).
        new ConformanceCase("bnb-background-no-layout-effect", "CSS Backgrounds & Borders 3 §3",
            Doc("<div id='a' style='width:120px;height:40px;background:#3366cc'></div>"),
            new[] { new BoxExpectation("a", X: 0, Y: 0, Width: 120, Height: 40) }),

        // §4 / CSS3-UI — under box-sizing:border-box the declared size IS the border
        // box, so a 20px border folds inside and the border box stays 200×50.
        new ConformanceCase("bnb-border-box-sizing", "CSS Backgrounds & Borders 3 / CSS3-UI",
            Doc("<div id='a' style='width:200px;height:50px;border:20px solid #000;"
                + "box-sizing:border-box'></div>"),
            new[] { new BoxExpectation("a", Width: 200, Height: 50) }),

        // §5 — border-radius is a paint-only property: rounding the corners must not
        // change the border box geometry.
        new ConformanceCase("bnb-border-radius-no-layout-effect", "CSS Backgrounds & Borders 3 §5",
            Doc("<div id='a' style='width:100px;height:40px;border-radius:12px'></div>"),
            new[] { new BoxExpectation("a", X: 0, Y: 0, Width: 100, Height: 40) }),
    };
}
