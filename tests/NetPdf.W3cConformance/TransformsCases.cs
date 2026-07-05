// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;

namespace NetPdf.W3cConformance;

/// <summary>Curated CSS Transforms Module Level 1 conformance cases (Phase-5 task
/// 22 — published pass-rate ≥ 85%). Per CSS Transforms 1 §3, a transform affects
/// only the element's VISUAL rendering, not layout: "the transform property does
/// not affect the flow of the content surrounding the transformed element" and the
/// element occupies its normal, un-transformed box in layout. The harness reads
/// the LAID-OUT border box (the paint-stage transform is applied downstream in the
/// display list), so this category asserts the invariant directly — a translated /
/// scaled / rotated element keeps its normal-flow geometry, and its siblings flow
/// as if the transform were absent.
///
/// <para>Content area is 600×800 px; <c>body</c> margin is zeroed so coordinates
/// are content-area-relative. Boxes are sized (no text) so the run is font-free.
/// A regression that folded the transform into layout would move these boxes and
/// turn the gate red.</para></summary>
internal static class TransformsCases
{
    private static string Doc(string body) =>
        "<!DOCTYPE html><html><body style=\"margin:0\">" + body + "</body></html>";

    public static IReadOnlyList<ConformanceCase> All { get; } = new[]
    {
        // §3 — translate is paint-only: the layout box stays at its normal-flow
        // origin (0,0) despite the 50px/60px visual shift.
        new ConformanceCase("transforms-translate-no-layout-effect", "CSS Transforms 1 §3",
            Doc("<div id='a' style='width:100px;height:40px;transform:translate(50px,60px)'></div>"),
            new[] { new BoxExpectation("a", X: 0, Y: 0, Width: 100, Height: 40) }),

        // §3 — scale is paint-only: the layout border box keeps its declared size
        // (the visual 2× happens in the display list, not layout).
        new ConformanceCase("transforms-scale-no-layout-effect", "CSS Transforms 1 §3",
            Doc("<div id='a' style='width:100px;height:40px;transform:scale(2)'></div>"),
            new[] { new BoxExpectation("a", X: 0, Y: 0, Width: 100, Height: 40) }),

        // §3 — rotate is paint-only: the layout box is unrotated (axis-aligned) and
        // keeps its origin + declared size.
        new ConformanceCase("transforms-rotate-no-layout-effect", "CSS Transforms 1 §3",
            Doc("<div id='a' style='width:100px;height:40px;transform:rotate(45deg)'></div>"),
            new[] { new BoxExpectation("a", X: 0, Y: 0, Width: 100, Height: 40) }),

        // §3 — the surrounding flow is unaffected: a following sibling stacks below
        // the transformed element's NORMAL-flow box, not its translated position.
        new ConformanceCase("transforms-sibling-flow-unaffected", "CSS Transforms 1 §3",
            Doc("<div id='a' style='height:30px;transform:translateY(100px)'></div>"
                + "<div id='b' style='height:20px'></div>"),
            new[]
            {
                new BoxExpectation("a", Y: 0, Height: 30),
                new BoxExpectation("b", Y: 30, Height: 20), // stacks below a's un-translated box
            }),

        // §3 — `transform:none` is the identity: geometry equals the untransformed
        // box (the control for the paint-only invariant).
        new ConformanceCase("transforms-none-identity", "CSS Transforms 1 §3",
            Doc("<div id='a' style='width:120px;height:40px;transform:none'></div>"),
            new[] { new BoxExpectation("a", X: 0, Y: 0, Width: 120, Height: 40) }),

        // §3 — a percentage translate (resolved against the element's own border
        // box) is still paint-only; the layout box is unmoved.
        new ConformanceCase("transforms-translate-percentage", "CSS Transforms 1 §3",
            Doc("<div id='a' style='width:100px;height:40px;transform:translateX(25%)'></div>"),
            new[] { new BoxExpectation("a", X: 0, Y: 0, Width: 100, Height: 40) }),
    };
}
