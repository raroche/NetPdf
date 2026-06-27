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
}
