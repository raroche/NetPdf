// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Svg;
using Xunit;

namespace NetPdf.UnitTests.Svg;

/// <summary>Phase 4 SVG part 5 — <c>&lt;textPath&gt;</c>: glyphs are laid along a referenced path, rotated to
/// the path tangent. Assertions are font-agnostic (ink bounding box / counts) so they hold regardless of the
/// system default font.</summary>
public sealed class SvgTextPathRasterizerTests
{
    private static byte[] Svg(string s) => Encoding.UTF8.GetBytes(s);

    private static (int MinX, int MaxX, int MinY, int MaxY, int Count) InkBox(NetPdf.Pdf.Images.RasterImageInfo info)
    {
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1, count = 0;
        for (var y = 0; y < info.Height; y++)
            for (var x = 0; x < info.Width; x++)
                if (info.PixelBytes[(y * info.Width + x) * 4 + 3] > 40)
                {
                    count++;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
        return (minX, maxX, minY, maxY, count);
    }

    [Fact]
    public void Text_along_a_horizontal_path_spreads_horizontally()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"140\" height=\"60\">" +
            "<defs><path id=\"p\" d=\"M 10 35 L 130 35\"/></defs>" +
            "<text font-size=\"22\" fill=\"black\"><textPath href=\"#p\">Hello</textPath></text></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        var box = InkBox(info!);
        Assert.True(box.Count > 30);
        Assert.True(box.MaxX - box.MinX > box.MaxY - box.MinY);   // wider than tall → horizontal text
    }

    [Fact]
    public void Text_along_a_vertical_path_is_rotated_to_run_vertically()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"140\">" +
            "<defs><path id=\"p\" d=\"M 30 10 L 30 130\"/></defs>" +
            "<text font-size=\"22\" fill=\"black\"><textPath href=\"#p\">Hello</textPath></text></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        var box = InkBox(info!);
        Assert.True(box.Count > 30);
        Assert.True(box.MaxY - box.MinY > box.MaxX - box.MinX);   // taller than wide → rotated to vertical
    }

    [Fact]
    public void Start_offset_shifts_where_the_text_begins()
    {
        var at0 = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"160\" height=\"50\">" +
            "<defs><path id=\"p\" d=\"M 5 30 L 155 30\"/></defs>" +
            "<text font-size=\"20\"><textPath href=\"#p\">Hi</textPath></text></svg>"), out _);
        var at60 = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"160\" height=\"50\">" +
            "<defs><path id=\"p\" d=\"M 5 30 L 155 30\"/></defs>" +
            "<text font-size=\"20\"><textPath href=\"#p\" startOffset=\"60\">Hi</textPath></text></svg>"), out _);
        Assert.NotNull(at0);
        Assert.NotNull(at60);
        Assert.True(InkBox(at60!).MinX > InkBox(at0!).MinX + 30);   // startOffset pushes the ink right
    }

    [Fact]
    public void Referenced_path_transform_is_applied_before_measuring()
    {
        // PR-243 review [P2] — the referenced path's own transform applies. translate(0,40) moves the path
        // (and the text on it) down, so the ink sits lower than on the untransformed path.
        var plain = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"140\" height=\"80\">" +
            "<defs><path id=\"p\" d=\"M 10 15 L 130 15\"/></defs>" +
            "<text font-size=\"20\"><textPath href=\"#p\">Hi</textPath></text></svg>"), out _);
        var moved = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"140\" height=\"80\">" +
            "<defs><path id=\"p\" d=\"M 10 15 L 130 15\" transform=\"translate(0,40)\"/></defs>" +
            "<text font-size=\"20\"><textPath href=\"#p\">Hi</textPath></text></svg>"), out _);
        Assert.NotNull(plain);
        Assert.NotNull(moved);
        Assert.True(InkBox(moved!).MinY > InkBox(plain!).MinY + 30);   // ~40px lower
    }

    [Fact]
    public void Textpath_along_a_basic_shape_renders()
    {
        // SVG part 6 — textPath geometry can be any basic shape (here a horizontal line), not just <path>.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"140\" height=\"50\">" +
            "<defs><line id=\"l\" x1=\"10\" y1=\"30\" x2=\"130\" y2=\"30\"/></defs>" +
            "<text font-size=\"20\" fill=\"black\"><textPath href=\"#l\">Hello</textPath></text></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        var box = InkBox(info!);
        Assert.True(box.Count > 30);
        Assert.True(box.MaxX - box.MinX > box.MaxY - box.MinY);   // runs horizontally along the line
    }

    [Fact]
    public void Textpath_referencing_a_non_shape_is_flagged()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"40\">" +
            "<defs><g id=\"g\"/></defs>" +
            "<text><textPath href=\"#g\">Hi</textPath></text></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
    }
}
