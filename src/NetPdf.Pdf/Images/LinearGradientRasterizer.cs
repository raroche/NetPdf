// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using SkiaSharp;

namespace NetPdf.Pdf.Images;

/// <summary>
/// Phase 4 gradient refinements — the Skia raster fallback for a CSS <c>linear-gradient</c> whose
/// stops carry PER-STOP ALPHA. A PDF native axial shading is DeviceRGB (no alpha), so a translucent
/// stop can't render natively; this draws the gradient over a transparent RGBA8888 surface and wraps
/// the unpremultiplied result as a PDF Image XObject (RGB plane + alpha <c>/SMask</c>) via
/// <see cref="RasterImageXObject"/> — the same bridge the conic + blurred-shadow paths use. A FULLY
/// OPAQUE gradient stays a native shading (the caller checks before falling back here).
/// </summary>
/// <remarks>Deterministic (CPU surface, no GPU). Caps match <see cref="ShadowRasterizer"/>.</remarks>
internal static class LinearGradientRasterizer
{
    /// <summary>One resolved stop: an offset along the gradient line in [0, 1] + a (possibly
    /// translucent) DeviceRGB color, channels in [0, 1].</summary>
    public readonly record struct Stop(double Offset, double R, double G, double B, double A);

    /// <summary>Rasterize a linear gradient into an Image XObject. The bitmap is
    /// <paramref name="deviceWidth"/> × <paramref name="deviceHeight"/> DEVICE px; the CSS gradient
    /// line runs through the center at <paramref name="angleDeg"/> (0° = to top, increasing clockwise),
    /// its length = the box's projection onto that direction (so offset 0/1 sit at the line ends,
    /// extended/clamped beyond). Returns <see langword="null"/> for &lt; 2 stops or a degenerate /
    /// over-cap bitmap.</summary>
    public static ImageXObjectResult? TryRasterize(
        int deviceWidth, int deviceHeight, double angleDeg, IReadOnlyList<Stop> stops)
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
            positions[i] = (float)Math.Clamp(s.Offset, 0.0, 1.0);
        }

        // CSS gradient line: 0° = to top → direction (0,-1) in device (y-down) space; clockwise.
        var rad = angleDeg * Math.PI / 180.0;
        var dx = Math.Sin(rad);
        var dy = -Math.Cos(rad);
        var lineLen = Math.Abs(deviceWidth * dx) + Math.Abs(deviceHeight * dy);
        var cx = deviceWidth / 2.0;
        var cy = deviceHeight / 2.0;
        var start = new SKPoint((float)(cx - dx * lineLen / 2.0), (float)(cy - dy * lineLen / 2.0));
        var end = new SKPoint((float)(cx + dx * lineLen / 2.0), (float)(cy + dy * lineLen / 2.0));

        var imageInfo = new SKImageInfo(deviceWidth, deviceHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var surface = SKSurface.Create(imageInfo);
        if (surface is null) return null;

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using var shader = SKShader.CreateLinearGradient(start, end, colors, positions, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Shader = shader };
        canvas.DrawRect(new SKRect(0, 0, deviceWidth, deviceHeight), paint);

        return ExtractRgba(surface, deviceWidth, deviceHeight);
    }

    internal static ImageXObjectResult? ExtractRgba(SKSurface surface, int deviceWidth, int deviceHeight)
    {
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

    internal static byte ToByte(double channel) =>
        (byte)Math.Clamp((int)Math.Round(channel * 255.0), 0, 255);
}
