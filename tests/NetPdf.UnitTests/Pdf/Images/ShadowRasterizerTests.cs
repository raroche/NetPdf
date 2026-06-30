// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf;
using NetPdf.Pdf.Images;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Images;

/// <summary>Phase 4 shadows — the blurred box-shadow raster bridge honors PER-CORNER elliptical
/// <c>border-radius</c> (it previously collapsed to one representative radius). A sharp (blur = 0)
/// raster is byte-stable, so two corner-radius sets that differ only at some corners produce
/// different alpha planes.</summary>
public sealed class ShadowRasterizerTests
{
    [Fact]
    public void Per_corner_radii_change_the_rasterized_outset_shadow_shape()
    {
        // blur 0 ⇒ a sharp rounded rect. Uniform 20px on all corners vs the same shape with the
        // top-right + bottom-right corners SHARP (0) ⇒ different alpha planes; identical inputs are stable.
        var uniform = ShadowRasterizer.TryRasterize(60, 60, 5, 5, 50, 50,
            CornerRadii.Uniform(20), blurSigma: 0f, 0, 0, 0, 1)!;
        var uniformAgain = ShadowRasterizer.TryRasterize(60, 60, 5, 5, 50, 50,
            CornerRadii.Uniform(20), blurSigma: 0f, 0, 0, 0, 1)!;
        var perCorner = ShadowRasterizer.TryRasterize(60, 60, 5, 5, 50, 50,
            new CornerRadii(20, 20, 0, 0, 0, 0, 20, 20), blurSigma: 0f, 0, 0, 0, 1)!;

        Assert.NotNull(uniform.SMask);
        Assert.True(uniform.SMask!.Data.SequenceEqual(uniformAgain.SMask!.Data));   // deterministic
        Assert.False(uniform.SMask!.Data.SequenceEqual(perCorner.SMask!.Data));     // per-corner differs
    }

    [Fact]
    public void Per_corner_radii_change_the_rasterized_inset_band_shape()
    {
        // The inset band's padding box + lit hole both round per-corner now.
        var uniform = ShadowRasterizer.TryRasterizeInset(60, 60, CornerRadii.Uniform(15),
            holeLeft: 5, holeTop: 5, holeWidth: 50, holeHeight: 50, CornerRadii.Uniform(10),
            blurSigma: 0f, 0, 0, 0, 1)!;
        var perCorner = ShadowRasterizer.TryRasterizeInset(60, 60,
            new CornerRadii(15, 15, 0, 0, 15, 15, 0, 0),
            holeLeft: 5, holeTop: 5, holeWidth: 50, holeHeight: 50, CornerRadii.Uniform(10),
            blurSigma: 0f, 0, 0, 0, 1)!;

        Assert.NotNull(uniform.SMask);
        Assert.False(uniform.SMask!.Data.SequenceEqual(perCorner.SMask!.Data));
    }

    [Fact]
    public void A_square_shadow_has_no_rounded_corners()
    {
        // No corner rounds ⇒ the plain-rect path (AnyPositive == false); still a valid raster.
        var square = ShadowRasterizer.TryRasterize(40, 40, 4, 4, 32, 32,
            default, blurSigma: 0f, 0, 0, 0, 1);
        Assert.NotNull(square);
        Assert.NotNull(square!.SMask);
    }
}
