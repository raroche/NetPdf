// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Svg;
using Xunit;

namespace NetPdf.UnitTests.Svg;

/// <summary>Phase 4 SVG part 2 (PR 7) — <c>&lt;use&gt;</c> / <c>&lt;symbol&gt;</c> / <c>&lt;defs&gt;</c>: a
/// <c>&lt;use href="#id"&gt;</c> clones a referenced shape / group / symbol at its (x, y) without flagging,
/// and a bare <c>&lt;defs&gt;</c> definition renders nothing.</summary>
public sealed class SvgUseRasterizerTests
{
    private static byte[] Svg(string s) => Encoding.UTF8.GetBytes(s);

    private static (byte R, byte G, byte B, byte A) Px(NetPdf.Pdf.Images.RasterImageInfo info, int x, int y)
    {
        var i = (y * info.Width + x) * 4;
        return (info.PixelBytes[i], info.PixelBytes[i + 1], info.PixelBytes[i + 2], info.PixelBytes[i + 3]);
    }

    [Fact]
    public void Use_clones_a_defined_shape_at_its_position()
    {
        // A red rect defined in <defs>, instanced twice at different x offsets.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"20\">" +
            "<defs><rect id=\"box\" x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"red\"/></defs>" +
            "<use href=\"#box\" x=\"0\" y=\"0\"/>" +
            "<use href=\"#box\" x=\"40\" y=\"5\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 5, 5).R > 200);      // first instance at (0,0)
        Assert.True(Px(info!, 5, 5).A > 200);
        Assert.True(Px(info!, 45, 10).R > 200);    // second instance at (40,5)
        Assert.True(Px(info!, 45, 10).A > 200);
        Assert.Equal(0, Px(info!, 25, 15).A);      // the gap between them is empty
    }

    [Fact]
    public void Use_of_a_symbol_renders_its_children()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<symbol id=\"sym\"><circle cx=\"10\" cy=\"10\" r=\"8\" fill=\"blue\"/></symbol>" +
            "<use href=\"#sym\" x=\"5\" y=\"5\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 15, 15).B > 200);    // circle center moved to (15,15) by the use offset
    }

    [Fact]
    public void Bare_defs_renders_nothing_and_does_not_flag()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<defs><rect id=\"box\" width=\"20\" height=\"20\" fill=\"red\"/></defs></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.Equal(0, Px(info!, 10, 10).A);      // defs content is not painted in place
    }

    [Fact]
    public void Use_with_a_missing_reference_is_flagged()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<use href=\"#nope\" x=\"0\" y=\"0\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
        Assert.Equal(0, Px(info!, 10, 10).A);
    }

    [Fact]
    public void Use_inherits_fill_from_the_use_element()
    {
        // The referenced shape has no fill of its own → it inherits the green fill set on the <use>.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<defs><rect id=\"box\" width=\"20\" height=\"20\"/></defs>" +
            "<use href=\"#box\" fill=\"#00ff00\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        var p = Px(info!, 10, 10);
        Assert.True(p.G > 200 && p.R < 80 && p.B < 80);
    }

    [Fact]
    public void Symbol_own_presentation_attributes_apply_to_its_children()
    {
        // PR-231 review [P2] — the symbol's own fill must reach a child that has no fill of its own (the
        // symbol style sits between the <use> and the children). The child should be blue, not default black.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<symbol id=\"s\" fill=\"#0000ff\"><rect x=\"0\" y=\"0\" width=\"20\" height=\"20\"/></symbol>" +
            "<use href=\"#s\" x=\"5\" y=\"5\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        var p = Px(info!, 12, 12);
        Assert.True(p.B > 200 && p.R < 80 && p.G < 80);
    }

    [Fact]
    public void Use_on_the_use_element_overrides_a_symbols_default_but_symbol_fill_wins_where_set()
    {
        // The <use> fill is the inherited context; the symbol sets its own fill, which wins for its children.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<symbol id=\"s\" fill=\"#0000ff\"><rect x=\"0\" y=\"0\" width=\"20\" height=\"20\"/></symbol>" +
            "<use href=\"#s\" x=\"5\" y=\"5\" fill=\"#ff0000\"/></svg>"), out _);
        Assert.NotNull(info);
        var p = Px(info!, 12, 12);
        Assert.True(p.B > 200 && p.R < 80);    // the symbol's explicit fill beats the use's
    }

    [Fact]
    public void Use_of_a_symbol_with_a_viewbox_scales_content_to_the_use_viewport()
    {
        // SVG §5.6 / §7.2 — a <use> referencing a <symbol> establishes a viewport from the use width/height;
        // the symbol's viewBox scales to fit. viewBox 0 0 10 10 instanced at 20×20 → 2× scale, so a circle at
        // (5,5) r=4 in viewBox space maps to center (10,10) r≈8 in the 20×20 viewport.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<symbol id=\"s\" viewBox=\"0 0 10 10\"><circle cx=\"5\" cy=\"5\" r=\"4\" fill=\"red\"/></symbol>" +
            "<use href=\"#s\" width=\"20\" height=\"20\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 10, 10).R > 200);    // center of the scaled circle filled
        Assert.Equal(0, Px(info!, 1, 1).A);        // a corner is outside the radius-8 circle → empty
    }

    [Fact]
    public void Use_of_a_symbol_clips_content_to_the_use_viewport()
    {
        // The symbol's content extends well beyond the 10×10 use viewport; the overflow is clipped (§7.2).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<symbol id=\"s\"><rect x=\"0\" y=\"0\" width=\"100\" height=\"100\" fill=\"red\"/></symbol>" +
            "<use href=\"#s\" x=\"5\" y=\"5\" width=\"10\" height=\"10\"/></svg>"), out _);
        Assert.NotNull(info);
        Assert.True(Px(info!, 10, 10).R > 200);    // inside the viewport (5..15)
        Assert.Equal(0, Px(info!, 20, 20).A);      // beyond the 10×10 clip → empty
    }

    [Fact]
    public void Use_explicit_zero_viewport_renders_nothing()
    {
        // An explicit zero width on the <use> collapses the viewport → nothing renders (§7.2).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<symbol id=\"s\"><rect x=\"0\" y=\"0\" width=\"20\" height=\"20\" fill=\"red\"/></symbol>" +
            "<use href=\"#s\" width=\"0\" height=\"20\"/></svg>"), out _);
        Assert.NotNull(info);
        Assert.Equal(0, Px(info!, 10, 10).A);
    }

    [Fact]
    public void Oversized_definition_only_svg_is_truncated_and_flagged()
    {
        // PR-231 review [P1] — a <defs> with more than the element budget (50,000) must NOT bypass the DoS
        // guard via the id-map walk; it returns (no crash) and is flagged unsupported.
        var sb = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\"><defs>");
        for (var i = 0; i < 51000; i++) sb.Append("<rect id=\"r").Append(i).Append("\"/>");
        sb.Append("</defs><rect x=\"0\" y=\"0\" width=\"20\" height=\"20\" fill=\"red\"/></svg>");
        var info = SvgRasterizer.TryRender(Svg(sb.ToString()), out var unsupported);
        Assert.NotNull(info);          // no crash / hang
        Assert.True(unsupported);      // budget exceeded → flagged
        Assert.True(Px(info!, 10, 10).R > 200);   // the in-budget visible rect still rendered
    }
}
