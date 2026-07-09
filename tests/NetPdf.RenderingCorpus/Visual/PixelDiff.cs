// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.RenderingCorpus.Visual;

/// <summary>The result of comparing two equal-size rasters: the largest per-pixel RGBA channel difference
/// (0–255) and the mean structural similarity (SSIM, 0–1). <see cref="WithinTolerance"/> applies the
/// harness contract (per-pixel Δ &lt; <see cref="PixelDiff.MaxPerPixelDelta"/> AND SSIM ≥
/// <see cref="PixelDiff.MinSsim"/>).</summary>
public readonly record struct PixelDiffResult(int MaxChannelDelta, double Ssim, int Width, int Height)
{
    public bool WithinTolerance =>
        MaxChannelDelta < PixelDiff.MaxPerPixelDelta && Ssim >= PixelDiff.MinSsim;

    public override string ToString() =>
        $"{Width}x{Height} maxΔ={MaxChannelDelta} ssim={Ssim:F4} (limits: Δ<{PixelDiff.MaxPerPixelDelta}, ssim≥{PixelDiff.MinSsim})";
}

/// <summary>Pure pixel-comparison core for the visual-regression harness (PR 8). Computes the maximum
/// per-pixel RGBA channel delta and the mean SSIM over two equal-size rasters; no I/O, no rendering, fully
/// unit-testable with synthetic bitmaps. The reference renderer (pinned Chrome) and the NetPdf output are
/// rasterized to <see cref="RasterImage"/> elsewhere; this only compares.</summary>
public static class PixelDiff
{
    // These are the engine-vs-SELF regression tolerances. Activating the gate against the pinned-Chrome
    // reference PNGs is a CROSS-engine diff (two rasterizers differ at every AA glyph edge), which these
    // strict values reject even on pixel-correct layout — see the `visual-regression-cross-engine-tolerance`
    // deferral in docs/deferrals.md for the tolerance-policy decision that must precede committing references.

    /// <summary>Per-pixel RGBA channel tolerance: a difference of this much or more on any channel of any
    /// pixel fails the gate (Phase-4 exit criterion: per-pixel RGBA Δ &lt; 4).</summary>
    public const int MaxPerPixelDelta = 4;

    /// <summary>Mean-SSIM floor (Phase-4 exit criterion: SSIM &gt; 0.98).</summary>
    public const double MinSsim = 0.98;

    // SSIM stabilization constants for an 8-bit dynamic range L=255 (Wang et al. 2004):
    // C1 = (K1·L)², C2 = (K2·L)² with K1=0.01, K2=0.03.
    private const double C1 = 0.01 * 255 * (0.01 * 255);
    private const double C2 = 0.03 * 255 * (0.03 * 255);
    private const int Window = 8; // non-overlapping window edge for the mean SSIM

    /// <summary>Compare two equal-size rasters. Throws <see cref="ArgumentException"/> on a size mismatch
    /// (a size mismatch is a harness error, not a tolerance failure — the caller must rasterize both to the
    /// same dimensions).</summary>
    public static PixelDiffResult Compare(RasterImage expected, RasterImage actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        // Validate buffer lengths up front so a malformed rasterizer fails with a clear message rather than
        // silently truncating (min-length) or crashing in the luma indexing.
        expected.EnsureValid();
        actual.EnsureValid();
        if (!expected.SameSizeAs(actual))
            throw new ArgumentException(
                $"raster size mismatch: {expected.Width}x{expected.Height} vs {actual.Width}x{actual.Height}");

        var ea = expected.Rgba;
        var ab = actual.Rgba;
        var n = ea.Length; // equal length guaranteed by EnsureValid + SameSizeAs
        var maxDelta = 0;
        for (var i = 0; i < n; i++)
        {
            var d = Math.Abs(ea[i] - ab[i]);
            if (d > maxDelta) maxDelta = d;
        }
        var ssim = MeanSsim(expected, actual);
        return new PixelDiffResult(maxDelta, ssim, expected.Width, actual.Height);
    }

    /// <summary>Mean SSIM over non-overlapping <see cref="Window"/>×<see cref="Window"/> luma blocks (edge
    /// blocks use their actual partial size). Equal-weight average across blocks (MSSIM).</summary>
    private static double MeanSsim(RasterImage a, RasterImage b)
    {
        var w = a.Width;
        var h = a.Height;
        if (w <= 0 || h <= 0) return 1.0;
        var la = Luma(a);
        var lb = Luma(b);

        var sum = 0.0;
        var blocks = 0;
        for (var by = 0; by < h; by += Window)
            for (var bx = 0; bx < w; bx += Window)
            {
                var bw = Math.Min(Window, w - bx);
                var bh = Math.Min(Window, h - by);
                sum += BlockSsim(la, lb, w, bx, by, bw, bh);
                blocks++;
            }
        return blocks == 0 ? 1.0 : sum / blocks;
    }

    private static double BlockSsim(double[] a, double[] b, int stride, int x0, int y0, int bw, int bh)
    {
        double sumA = 0, sumB = 0;
        var count = bw * bh;
        for (var y = 0; y < bh; y++)
            for (var x = 0; x < bw; x++)
            {
                var i = (y0 + y) * stride + (x0 + x);
                sumA += a[i];
                sumB += b[i];
            }
        var muA = sumA / count;
        var muB = sumB / count;

        double varA = 0, varB = 0, cov = 0;
        for (var y = 0; y < bh; y++)
            for (var x = 0; x < bw; x++)
            {
                var i = (y0 + y) * stride + (x0 + x);
                var da = a[i] - muA;
                var db = b[i] - muB;
                varA += da * da;
                varB += db * db;
                cov += da * db;
            }
        // Sample variance/covariance (divide by N-1); a 1-pixel block has zero variance → SSIM driven by the
        // luminance term (means), which is the right degenerate behavior.
        var denom = count > 1 ? count - 1 : 1;
        varA /= denom;
        varB /= denom;
        cov /= denom;

        var num = (2 * muA * muB + C1) * (2 * cov + C2);
        var den = (muA * muA + muB * muB + C1) * (varA + varB + C2);
        return den == 0 ? 1.0 : num / den;
    }

    /// <summary>Rec.601 luma per pixel (alpha ignored — structural similarity lives in the color channels).</summary>
    private static double[] Luma(RasterImage img)
    {
        var px = img.Width * img.Height;
        var luma = new double[px];
        var rgba = img.Rgba;
        for (var i = 0; i < px; i++)
        {
            var o = i * 4;
            luma[i] = 0.299 * rgba[o] + 0.587 * rgba[o + 1] + 0.114 * rgba[o + 2];
        }
        return luma;
    }
}
