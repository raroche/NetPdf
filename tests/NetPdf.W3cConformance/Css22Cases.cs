// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;

namespace NetPdf.W3cConformance;

/// <summary>Per Phase 3 PR 1 — curated CSS 2.1/2.2 BLOCK-LAYOUT conformance
/// cases. Each asserts spec-correct border-box geometry of an <c>id</c>'d
/// element. Content area is 600×800 px; <c>body</c> margin is zeroed so
/// coordinates are content-area-relative. The set is REPRESENTATIVE, not
/// cherry-picked: it includes a handful of cases that exercise features NetPdf
/// deliberately approximates (auto-height shrink-to-fit, <c>box-sizing</c>,
/// min/max sizing on an explicit width) so the published pass-rate reflects
/// honest conformance + the gate guards against regression of what works
/// today. See the README + docs/deferrals.md for the gap analysis.</summary>
internal static class Css22Cases
{
    private static string Doc(string body) =>
        "<!DOCTYPE html><html><body style=\"margin:0\">" + body + "</body></html>";

    public static IReadOnlyList<ConformanceCase> All { get; } = new[]
    {
        // ---- §8/§9/§10 features NetPdf renders to spec ----

        new ConformanceCase("css22-block-explicit-size", "CSS 2.1 §10.2/§10.6.3",
            Doc("<div id='a' style='width:120px;height:40px'></div>"),
            new[] { new BoxExpectation("a", X: 0, Y: 0, Width: 120, Height: 40) }),

        new ConformanceCase("css22-block-auto-width", "CSS 2.1 §10.3.3",
            Doc("<div id='a' style='height:40px'></div>"),
            new[] { new BoxExpectation("a", X: 0, Y: 0, Width: 600, Height: 40) }),

        new ConformanceCase("css22-block-vertical-stack", "CSS 2.1 §9.4.1",
            Doc("<div id='a' style='height:30px'></div><div id='b' style='height:50px'></div>"),
            new[]
            {
                new BoxExpectation("a", Y: 0, Height: 30),
                new BoxExpectation("b", Y: 30, Height: 50),
            }),

        new ConformanceCase("css22-block-three-stack", "CSS 2.1 §9.4.1",
            Doc("<div id='a' style='height:20px'></div><div id='b' style='height:20px'></div>"
                + "<div id='c' style='height:20px'></div>"),
            new[]
            {
                new BoxExpectation("a", Y: 0),
                new BoxExpectation("b", Y: 20),
                new BoxExpectation("c", Y: 40),
            }),

        new ConformanceCase("css22-margin-top", "CSS 2.1 §8.3",
            Doc("<div id='a' style='height:30px;margin-top:25px'></div>"),
            new[] { new BoxExpectation("a", Y: 25, Height: 30) }),

        new ConformanceCase("css22-margin-left", "CSS 2.1 §8.3",
            Doc("<div id='a' style='width:100px;height:30px;margin-left:40px'></div>"),
            new[] { new BoxExpectation("a", X: 40, Width: 100, Height: 30) }),

        new ConformanceCase("css22-margin-collapse-siblings", "CSS 2.1 §8.3.1",
            Doc("<div id='a' style='height:20px;margin-bottom:30px'></div>"
                + "<div id='b' style='height:20px;margin-top:20px'></div>"),
            new[]
            {
                new BoxExpectation("a", Y: 0, Height: 20),
                new BoxExpectation("b", Y: 50, Height: 20), // 20 + max(30,20)
            }),

        // Padding insets the child within the parent's content box (§8.4) — the
        // CHILD position is what NetPdf gets right; the parent's auto-height is a
        // separate (approximated) concern covered by its own case below.
        new ConformanceCase("css22-padding-insets-child", "CSS 2.1 §8.4",
            Doc("<div id='p' style='padding:20px'><div id='c' style='height:30px'></div></div>"),
            new[] { new BoxExpectation("c", X: 20, Y: 20, Width: 560, Height: 30) }),

        new ConformanceCase("css22-border-insets-child", "CSS 2.1 §8.1",
            Doc("<div id='p' style='border:10px solid #000'><div id='c' style='height:30px'></div></div>"),
            new[] { new BoxExpectation("c", X: 10, Y: 10, Width: 580, Height: 30) }),

        new ConformanceCase("css22-nested-auto-width", "CSS 2.1 §10.3.3",
            Doc("<div id='p' style='width:400px'><div id='c' style='height:20px'></div></div>"),
            new[] { new BoxExpectation("c", X: 0, Y: 0, Width: 400, Height: 20) }),

        // `width` is content-box by default, so the parent's content width IS
        // 300; the child fills it and is inset by the 25px left padding.
        new ConformanceCase("css22-nested-padding-width", "CSS 2.1 §10.3.3/§8.4",
            Doc("<div id='p' style='width:300px;padding:0 25px'>"
                + "<div id='c' style='height:20px'></div></div>"),
            new[] { new BoxExpectation("c", X: 25, Width: 300, Height: 20) }),

        new ConformanceCase("css22-percentage-width", "CSS 2.1 §10.2",
            Doc("<div id='a' style='width:50%;height:30px'></div>"),
            new[] { new BoxExpectation("a", Width: 300, Height: 30) }),

        new ConformanceCase("css22-percentage-width-nested", "CSS 2.1 §10.2",
            Doc("<div id='p' style='width:400px'><div id='c' style='width:25%;height:20px'></div></div>"),
            new[] { new BoxExpectation("c", Width: 100, Height: 20) }), // 25% of 400

        new ConformanceCase("css22-auto-margin-center", "CSS 2.1 §10.3.3",
            Doc("<div id='a' style='width:200px;height:30px;margin-left:auto;margin-right:auto'></div>"),
            new[] { new BoxExpectation("a", X: 200, Width: 200) }),

        new ConformanceCase("css22-float-left-position", "CSS 2.1 §9.5.1",
            Doc("<div id='f' style='float:left;width:100px;height:50px'></div>"),
            new[] { new BoxExpectation("f", X: 0, Y: 0, Width: 100, Height: 50) }),

        new ConformanceCase("css22-float-right-position", "CSS 2.1 §9.5.1",
            Doc("<div id='f' style='float:right;width:100px;height:50px'></div>"),
            new[] { new BoxExpectation("f", X: 500, Y: 0, Width: 100, Height: 50) }), // 600-100

        // ---- features NetPdf approximates (honest gap cases) ----

        // §10.6.3 — an AUTO-height block should shrink-to-fit its in-flow
        // children. NetPdf resolves auto height to 0 + chrome (documented
        // approximation), so the parent under-sizes. EXPECTED here is spec.
        new ConformanceCase("css22-auto-height-contains-child", "CSS 2.1 §10.6.3",
            Doc("<div id='p' style='padding:20px'><div id='c' style='height:30px'></div></div>"),
            new[] { new BoxExpectation("p", Height: 70) }), // 30 + 20 + 20

        // CSS3-UI — box-sizing:border-box (width/height include padding+border).
        new ConformanceCase("css22-box-sizing-border-box", "CSS3-UI box-sizing",
            Doc("<div id='a' style='width:200px;height:50px;padding:20px;box-sizing:border-box'></div>"),
            new[] { new BoxExpectation("a", Width: 200, Height: 50) }),

        // §10.4 — min-width raises a smaller explicit width.
        new ConformanceCase("css22-min-width-on-explicit", "CSS 2.1 §10.4",
            Doc("<div id='a' style='width:50px;min-width:150px;height:20px'></div>"),
            new[] { new BoxExpectation("a", Width: 150) }),
    };
}
