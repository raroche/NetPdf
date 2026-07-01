// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Svg;
using Xunit;

namespace NetPdf.UnitTests.Svg;

/// <summary>Phase 4 SVG part 5 — <c>marker-start</c> / <c>marker-mid</c> / <c>marker-end</c> (and the
/// <c>marker</c> shorthand): a referenced <c>&lt;marker&gt;</c> renders at a shape's vertices, honoring
/// <c>refX</c>/<c>refY</c>, <c>orient</c> (auto), and <c>markerUnits</c>.</summary>
public sealed class SvgMarkerRasterizerTests
{
    private static byte[] Svg(string s) => Encoding.UTF8.GetBytes(s);

    private static (byte R, byte G, byte B, byte A) Px(NetPdf.Pdf.Images.RasterImageInfo info, int x, int y)
    {
        var i = (y * info.Width + x) * 4;
        return (info.PixelBytes[i], info.PixelBytes[i + 1], info.PixelBytes[i + 2], info.PixelBytes[i + 3]);
    }

    private const string RedSquareMarker =
        "<marker id=\"m\" markerWidth=\"10\" markerHeight=\"10\" markerUnits=\"userSpaceOnUse\" refX=\"0\" refY=\"5\">" +
        "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"red\"/></marker>";

    [Fact]
    public void Marker_end_renders_at_the_last_vertex()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"20\">" +
            RedSquareMarker +
            "<line x1=\"5\" y1=\"10\" x2=\"40\" y2=\"10\" stroke=\"black\" stroke-width=\"1\" marker-end=\"url(#m)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 45, 10).R > 200);    // red square at the end vertex (refX=0 → spans 40..50)
        Assert.True(Px(info!, 8, 8).R < 120);      // no marker at the start
    }

    [Fact]
    public void Marker_start_renders_at_the_first_vertex()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"20\">" +
            RedSquareMarker +
            "<line x1=\"5\" y1=\"10\" x2=\"40\" y2=\"10\" stroke=\"black\" stroke-width=\"1\" marker-start=\"url(#m)\"/></svg>"),
            out _);
        Assert.NotNull(info);
        Assert.True(Px(info!, 10, 10).R > 200);    // red square at the start vertex (spans 5..15)
        Assert.Equal(0, Px(info!, 45, 10).A);      // nothing past the line end (no end marker)
    }

    [Fact]
    public void Marker_mid_renders_at_interior_vertices_only()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"70\" height=\"20\">" +
            RedSquareMarker +
            "<polyline points=\"5,10 30,10 55,10\" fill=\"none\" stroke=\"black\" stroke-width=\"1\" marker-mid=\"url(#m)\"/></svg>"),
            out _);
        Assert.NotNull(info);
        Assert.True(Px(info!, 35, 10).R > 200);    // mid vertex at (30,10) → square spans 30..40
        Assert.True(Px(info!, 8, 8).R < 120);      // no marker at the start vertex
    }

    [Fact]
    public void Orient_auto_rotates_the_marker_to_the_path_direction()
    {
        // A marker whose content is a horizontal bar (x 0..8, y 0..2) at the end of a DOWNWARD line. With
        // orient=auto the bar rotates 90° → it extends downward from the vertex; with a fixed orient it does not.
        const string bar = "<marker id=\"m\" markerWidth=\"8\" markerHeight=\"2\" markerUnits=\"userSpaceOnUse\" refX=\"0\" refY=\"1\" orient=\"{0}\">" +
            "<rect x=\"0\" y=\"0\" width=\"8\" height=\"2\" fill=\"red\"/></marker>";
        const string doc = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"30\" height=\"60\">{0}" +
            "<line x1=\"10\" y1=\"5\" x2=\"10\" y2=\"40\" stroke=\"black\" stroke-width=\"1\" marker-end=\"url(#m)\"/></svg>";

        var auto = SvgRasterizer.TryRender(Svg(string.Format(doc, string.Format(bar, "auto"))), out _);
        var fixed0 = SvgRasterizer.TryRender(Svg(string.Format(doc, string.Format(bar, "0"))), out _);
        Assert.NotNull(auto);
        Assert.NotNull(fixed0);
        Assert.True(Px(auto!, 10, 46).R > 200);    // rotated bar extends DOWN from the (10,40) vertex
        Assert.Equal(0, Px(fixed0!, 10, 46).A);    // unrotated bar extends along +x → nothing at (10,46)
    }

    [Fact]
    public void Marker_dimensions_accept_length_units()
    {
        // PR-243 review [P2] — markerWidth/Height="10px" must parse as 10, not fall back to the default 3
        // (which would clip the 10×10 content to a 3×3 viewport, hiding most of it).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"20\">" +
            "<marker id=\"m\" markerWidth=\"10px\" markerHeight=\"10px\" markerUnits=\"userSpaceOnUse\" refX=\"0\" refY=\"5\">" +
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"red\"/></marker>" +
            "<line x1=\"5\" y1=\"10\" x2=\"40\" y2=\"10\" stroke=\"black\" stroke-width=\"1\" marker-end=\"url(#m)\"/></svg>"),
            out _);
        Assert.NotNull(info);
        Assert.True(Px(info!, 48, 10).R > 200);    // the 10px-wide square reaches x≈48 (40..50)
    }

    [Fact]
    public void Marker_end_on_a_closed_path_lands_at_the_close_vertex()
    {
        // PR-243 review [P2] — a Z close adds the segment back to the subpath start, so marker-end is at the
        // START point (10,10), not the last drawn vertex (40,40).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"60\">" +
            RedSquareMarker +
            "<path d=\"M 10 10 L 40 10 L 40 40 Z\" fill=\"none\" stroke=\"black\" stroke-width=\"1\" marker-end=\"url(#m)\"/></svg>"),
            out _);
        Assert.NotNull(info);
        Assert.True(Px(info!, 15, 10).R > 200);    // marker at the close vertex (10,10) → square spans 10..20
        Assert.Equal(0, Px(info!, 50, 40).A);      // NOT at the last drawn corner (40,40)
    }

    [Fact]
    public void Marker_refs_inherit_from_an_ancestor_group()
    {
        // SVG part 6 — marker properties cascade: marker-end on a <g> applies to the line inside it.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"20\">" +
            RedSquareMarker +
            "<g marker-end=\"url(#m)\"><line x1=\"5\" y1=\"10\" x2=\"40\" y2=\"10\" stroke=\"black\" stroke-width=\"1\"/></g></svg>"),
            out _);
        Assert.NotNull(info);
        Assert.True(Px(info!, 45, 10).R > 200);    // inherited marker renders at the line end
    }

    [Fact]
    public void A_child_marker_none_overrides_an_inherited_marker()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"20\">" +
            RedSquareMarker +
            "<g marker-end=\"url(#m)\"><line x1=\"5\" y1=\"10\" x2=\"40\" y2=\"10\" stroke=\"black\" stroke-width=\"1\" marker-end=\"none\"/></g></svg>"),
            out _);
        Assert.NotNull(info);
        Assert.Equal(0, Px(info!, 45, 10).A);      // child marker-end:none cancels the inherited marker
    }

    [Fact]
    public void Marker_referencing_a_non_marker_is_flagged()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"20\">" +
            "<linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"red\"/></linearGradient>" +
            "<line x1=\"5\" y1=\"10\" x2=\"30\" y2=\"10\" stroke=\"black\" stroke-width=\"1\" marker-end=\"url(#g)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
    }

    [Fact]
    public void Path_marker_orients_to_the_exact_curve_tangent_not_the_chord()
    {
        // The cubic M10,40 C10,10 40,10 40,40 ARRIVES at (40,40) travelling straight DOWN (tangent from the
        // last control (40,10) → end = +90°), although the start→end CHORD is horizontal (0°). An end marker
        // bar with orient=auto must rotate to the exact tangent (extend downward), not the chord (extend right).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"60\">" +
            "<marker id=\"m\" markerWidth=\"8\" markerHeight=\"2\" markerUnits=\"userSpaceOnUse\" refX=\"0\" refY=\"1\" orient=\"auto\">" +
            "<rect x=\"0\" y=\"0\" width=\"8\" height=\"2\" fill=\"red\"/></marker>" +
            "<path d=\"M10,40 C10,10 40,10 40,40\" fill=\"none\" stroke=\"black\" marker-end=\"url(#m)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 40, 46).R > 200);    // bar extends DOWN from (40,40) per the exact tangent (+90°)
        Assert.Equal(0, Px(info!, 46, 40).A);      // NOT extending right along the chord (the old approximation)
    }
}
