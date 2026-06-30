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
    public void Textpath_honors_rotate_supplemental_to_the_tangent()
    {
        // PR-244 review [P2] — a per-glyph rotate adds to the path-tangent rotation. On a horizontal path,
        // rotate="90" makes each glyph run vertically, so the ink is taller than the unrotated run.
        var plain = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"160\" height=\"80\">" +
            "<defs><path id=\"p\" d=\"M 10 40 L 150 40\"/></defs>" +
            "<text font-size=\"22\" fill=\"black\"><textPath href=\"#p\">Hi</textPath></text></svg>"), out _);
        var rotated = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"160\" height=\"80\">" +
            "<defs><path id=\"p\" d=\"M 10 40 L 150 40\"/></defs>" +
            "<text font-size=\"22\" fill=\"black\" rotate=\"90\"><textPath href=\"#p\">Hi</textPath></text></svg>"), out _);
        Assert.NotNull(plain);
        Assert.NotNull(rotated);
        // Rotating each glyph about its on-path origin moves the ink to a different vertical band than the
        // unrotated (above-the-baseline) run.
        Assert.True(System.Math.Abs(InkBox(rotated!).MaxY - InkBox(plain!).MaxY) > 5
            || System.Math.Abs(InkBox(rotated!).MinY - InkBox(plain!).MinY) > 5);
    }

    [Fact]
    public void Textpath_honors_dominant_baseline()
    {
        // PR-244 review [P2] — dominant-baseline shifts glyphs perpendicular to the path; hanging drops them.
        var alphabetic = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"160\" height=\"80\">" +
            "<defs><path id=\"p\" d=\"M 10 40 L 150 40\"/></defs>" +
            "<text font-size=\"22\" fill=\"black\"><textPath href=\"#p\">Hi</textPath></text></svg>"), out _);
        var hanging = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"160\" height=\"80\">" +
            "<defs><path id=\"p\" d=\"M 10 40 L 150 40\"/></defs>" +
            "<text font-size=\"22\" fill=\"black\" dominant-baseline=\"hanging\"><textPath href=\"#p\">Hi</textPath></text></svg>"), out _);
        Assert.NotNull(alphabetic);
        Assert.NotNull(hanging);
        Assert.True(InkBox(hanging!).MinY > InkBox(alphabetic!).MinY + 8);   // dropped below the path
    }

    [Fact]
    public void Textpath_geometry_uses_the_referenced_shapes_own_font_size_for_em()
    {
        // PR-244 review [P3] — the referenced line's x2="4em" resolves against the LINE's own font-size (20 →
        // 80px), not the <text> font-size (8). So the text spreads to ~x80, not ~x32.
        int MaxInkX(NetPdf.Pdf.Images.RasterImageInfo info)
        {
            var maxX = 0;
            for (var y = 0; y < info.Height; y++)
                for (var x = 0; x < info.Width; x++)
                    if (info.PixelBytes[(y * info.Width + x) * 4 + 3] > 40 && x > maxX) maxX = x;
            return maxX;
        }
        // The line's x2="10em" uses the LINE's font-size (18 → 180px), not the <text>'s (9 → 90px). Glyphs
        // whose midpoint passes the path end are dropped, so a wrong (90px) path would cap the ink near x≈90;
        // the correct 180px path lets the run reach past x≈120.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"210\" height=\"40\">" +
            "<defs><line id=\"l\" x1=\"5\" y1=\"30\" x2=\"10em\" y2=\"30\" font-size=\"18\"/></defs>" +
            "<text font-size=\"9\" fill=\"black\"><textPath href=\"#l\">WWWWWWWWWWWWWWWWWWWW</textPath></text></svg>"), out _);
        Assert.NotNull(info);
        Assert.True(MaxInkX(info!) > 120);   // correct 180px path; a text-font 90px path would cap near x90
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
