// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using SkiaSharp;

namespace NetPdf.Pdf.Images;

/// <summary>
/// Phase 4 gradient refinements — the Skia raster fallback for a CSS <c>radial-gradient</c> whose
/// stops carry PER-STOP ALPHA (a native DeviceRGB radial shading can't represent alpha). Draws the
/// gradient over a transparent RGBA8888 surface and wraps the result as an image XObject (RGB plane +
/// alpha <c>/SMask</c>) via <see cref="RasterImageXObject"/>. A fully opaque radial gradient stays a
/// native shading (the caller checks first).
/// </summary>
/// <remarks>Deterministic (CPU surface). Caps match <see cref="ShadowRasterizer"/>.</remarks>
internal static class RadialGradientRasterizer
{
    /// <summary>One resolved stop: an offset in [0, 1] along the ray + a (possibly translucent)
    /// DeviceRGB color, channels in [0, 1].</summary>
    public readonly record struct Stop(double Offset, double R, double G, double B, double A);

    /// <summary>Rasterize a radial gradient into an Image XObject. The bitmap is
    /// <paramref name="deviceWidth"/> × <paramref name="deviceHeight"/> DEVICE px; the gradient is
    /// centered at (<paramref name="centerX"/>, <paramref name="centerY"/>) device px (y-down, from the
    /// bitmap top), with per-axis ending radii <paramref name="radiusX"/> / <paramref name="radiusY"/>
    /// (an ellipse is a circle of the larger radius scaled on the other axis about the center). Returns
    /// <see langword="null"/> for &lt; 2 stops, a non-positive radius, or a degenerate / over-cap
    /// bitmap.</summary>
    public static ImageXObjectResult? TryRasterize(
        int deviceWidth, int deviceHeight, float centerX, float centerY, float radiusX, float radiusY,
        IReadOnlyList<Stop> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        if (stops.Count < 2) return null;
        if (deviceWidth <= 0 || deviceHeight <= 0) return null;
        if (deviceWidth > ShadowRasterizer.MaxDeviceDimension || deviceHeight > ShadowRasterizer.MaxDeviceDimension) return null;
        if ((long)deviceWidth * deviceHeight > ShadowRasterizer.MaxDevicePixels) return null;
        var baseRadius = Math.Max(radiusX, radiusY);
        if (!(baseRadius > 0)) return null;

        var colors = new SKColor[stops.Count];
        var positions = new float[stops.Count];
        for (var i = 0; i < stops.Count; i++)
        {
            var s = stops[i];
            colors[i] = new SKColor(LinearGradientRasterizer.ToByte(s.R), LinearGradientRasterizer.ToByte(s.G),
                LinearGradientRasterizer.ToByte(s.B), LinearGradientRasterizer.ToByte(s.A));
            positions[i] = (float)Math.Clamp(s.Offset, 0.0, 1.0);
        }

        var imageInfo = new SKImageInfo(deviceWidth, deviceHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var surface = SKSurface.Create(imageInfo);
        if (surface is null) return null;

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var center = new SKPoint(centerX, centerY);
        // A circle of the larger radius, squashed on the other axis about the center → an ellipse.
        SKShader shader;
        if (Math.Abs(radiusX - radiusY) < 1e-6f)
        {
            shader = SKShader.CreateRadialGradient(center, baseRadius, colors, positions, SKShaderTileMode.Clamp);
        }
        else
        {
            var local = SKMatrix.CreateScale(radiusX / baseRadius, radiusY / baseRadius, centerX, centerY);
            shader = SKShader.CreateRadialGradient(center, baseRadius, colors, positions, SKShaderTileMode.Clamp)
                .WithLocalMatrix(local);
        }
        using (shader)
        using (var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Shader = shader })
            canvas.DrawRect(new SKRect(0, 0, deviceWidth, deviceHeight), paint);

        return LinearGradientRasterizer.ExtractRgba(surface, deviceWidth, deviceHeight);
    }
}
