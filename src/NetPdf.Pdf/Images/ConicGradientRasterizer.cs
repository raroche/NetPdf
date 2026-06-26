// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using SkiaSharp;

namespace NetPdf.Pdf.Images;

/// <summary>
/// Phase 4 gradients — the Skia raster fallback for a CSS <c>conic-gradient</c> (PDF has no native
/// sweep/conic shading). Draws a sweep gradient over a transparent RGBA8888 surface and wraps the
/// unpremultiplied result as a PDF Image XObject (RGB plane + alpha <c>/SMask</c>) via
/// <see cref="RasterImageXObject"/> — the same bridge the blurred-shadow path uses, so per-stop
/// alpha is preserved.
/// </summary>
/// <remarks>Deterministic: a CPU raster surface (no GPU context) + Skia's sweep gradient are pure
/// functions of the inputs (CLAUDE.md #4). Caps match <see cref="ShadowRasterizer"/> — per-side
/// <see cref="ShadowRasterizer.MaxDeviceDimension"/> and total <see cref="ShadowRasterizer.MaxDevicePixels"/>;
/// an over-cap request returns <see langword="null"/> so the caller can skip (the background-color shows).</remarks>
internal static class ConicGradientRasterizer
{
    /// <summary>One resolved conic stop: a turn fraction in [0, 1] + an opaque-or-translucent
    /// DeviceRGB color with channels in [0, 1].</summary>
    public readonly record struct Stop(double Position, double R, double G, double B, double A);

    /// <summary>Rasterize a conic (sweep) gradient into an Image XObject. The bitmap is
    /// <paramref name="deviceWidth"/> × <paramref name="deviceHeight"/> DEVICE px; the sweep is
    /// centered at (<paramref name="centerX"/>, <paramref name="centerY"/>) device px and starts at
    /// <paramref name="fromAngleDeg"/> (CSS convention — 0° = up / 12 o'clock, increasing clockwise),
    /// sweeping a full turn through <paramref name="stops"/> (positions in [0, 1], already sorted
    /// non-decreasing by the caller). Returns <see langword="null"/> for a degenerate / over-cap
    /// bitmap or fewer than 2 stops.</summary>
    public static ImageXObjectResult? TryRasterize(
        int deviceWidth, int deviceHeight, float centerX, float centerY, double fromAngleDeg,
        IReadOnlyList<Stop> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        if (stops.Count < 2) return null;
        if (deviceWidth <= 0 || deviceHeight <= 0) return null;
        if (deviceWidth > ShadowRasterizer.MaxDeviceDimension || deviceHeight > ShadowRasterizer.MaxDeviceDimension) return null;
        if ((long)deviceWidth * deviceHeight > ShadowRasterizer.MaxDevicePixels) return null;

        var colors = new SKColor[stops.Count];
        var positions = new float[stops.Count];
        for (var i = 0; i < stops.Count; i++)
        {
            var s = stops[i];
            colors[i] = new SKColor(ToByte(s.R), ToByte(s.G), ToByte(s.B), ToByte(s.A));
            positions[i] = (float)Math.Clamp(s.Position, 0.0, 1.0);
        }

        var imageInfo = new SKImageInfo(deviceWidth, deviceHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var surface = SKSurface.Create(imageInfo);
        if (surface is null) return null;

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var center = new SKPoint(centerX, centerY);
        // Skia's sweep gradient starts at +x (3 o'clock) and sweeps clockwise in this y-down device
        // space; CSS conic 0° is "up" (12 o'clock) and sweeps clockwise — so rotate the gradient by
        // (fromAngle − 90)° about the center to align position 0 with the CSS start angle.
        var rotation = SKMatrix.CreateRotationDegrees((float)(fromAngleDeg - 90.0), centerX, centerY);
        using var shader = SKShader.CreateSweepGradient(center, colors, positions).WithLocalMatrix(rotation);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Shader = shader };
        canvas.DrawRect(new SKRect(0, 0, deviceWidth, deviceHeight), paint);

        using var image = surface.Snapshot();
        using var pixmap = image.PeekPixels();
        var rowBytes = pixmap.RowBytes;
        var width4 = deviceWidth * 4;
        var pixels = new byte[width4 * deviceHeight];
        var source = pixmap.GetPixelSpan();
        for (var y = 0; y < deviceHeight; y++)
            source.Slice(y * rowBytes, width4).CopyTo(pixels.AsSpan(y * width4));

        var info = new RasterImageInfo
        {
            Width = deviceWidth,
            Height = deviceHeight,
            HasAlpha = true,
            PixelBytes = pixels,
        };
        return RasterImageXObject.Build(info);
    }

    private static byte ToByte(double channel) =>
        (byte)Math.Clamp((int)Math.Round(channel * 255.0), 0, 255);
}
