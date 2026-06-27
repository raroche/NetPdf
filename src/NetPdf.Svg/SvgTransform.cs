// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;
using SkiaSharp;

namespace NetPdf.Svg;

/// <summary>Parse an SVG <c>transform</c> / <c>gradientTransform</c> list
/// (<c>translate</c>/<c>scale</c>/<c>rotate</c>/<c>matrix</c>/<c>skewX</c>/<c>skewY</c>) into a single
/// matrix. Shared by the shape walk (<see cref="SvgRasterizer"/>) and the gradient resolver
/// (<see cref="SvgPaintServers"/>).</summary>
internal static class SvgTransform
{
    /// <summary>Returns the composed matrix, or <see langword="null"/> when the list is empty / unparseable.</summary>
    public static SKMatrix? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = SKMatrix.Identity;
        var any = false;
        var i = 0;
        while (i < raw.Length)
        {
            var open = raw.IndexOf('(', i);
            if (open < 0) break;
            var name = raw[i..open].Trim().TrimStart(',', ' ').ToLowerInvariant();
            var close = raw.IndexOf(')', open);
            if (close < 0) break;
            var args = raw[(open + 1)..close].Split(new[] { ' ', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            float A(int k) => k < args.Length && float.TryParse(args[k], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
            SKMatrix step = name switch
            {
                "translate" => SKMatrix.CreateTranslation(A(0), args.Length > 1 ? A(1) : 0),
                "scale" => SKMatrix.CreateScale(A(0), args.Length > 1 ? A(1) : A(0)),
                "rotate" => args.Length >= 3
                    ? SKMatrix.CreateRotationDegrees(A(0), A(1), A(2))
                    : SKMatrix.CreateRotationDegrees(A(0)),
                "skewx" => SKMatrix.CreateSkew((float)Math.Tan(A(0) * Math.PI / 180.0), 0),
                "skewy" => SKMatrix.CreateSkew(0, (float)Math.Tan(A(0) * Math.PI / 180.0)),
                "matrix" => new SKMatrix { ScaleX = A(0), SkewY = A(1), SkewX = A(2), ScaleY = A(3), TransX = A(4), TransY = A(5), Persp2 = 1 },
                _ => SKMatrix.Identity,
            };
            m = m.PreConcat(step);
            any = true;
            i = close + 1;
        }
        return any ? m : null;
    }
}
