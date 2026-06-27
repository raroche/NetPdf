// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using SkiaSharp;

namespace NetPdf.Svg;

/// <summary>SVG/CSS color parsing shared by the shape renderer (<see cref="SvgRasterizer"/>) and the
/// paint-server resolver (<see cref="SvgPaintServers"/>): hex, <c>rgb()</c>/<c>rgba()</c>, the SVG named
/// colors, plus the <c>currentColor</c> / <c>transparent</c> keywords. A best-effort Skia parse catches the
/// long tail of named colors this table doesn't list.</summary>
internal static class SvgColor
{
    public static bool TryParse(string raw, out SKColor color)
    {
        color = SKColors.Black;
        var s = raw.Trim();
        if (s.Length == 0) return false;
        if (s.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) { color = SKColors.Black; return true; }
        if (s.Equals("transparent", StringComparison.OrdinalIgnoreCase)) { color = SKColors.Transparent; return true; }
        if (s.StartsWith('#') && SKColor.TryParse(s, out color)) return true;
        if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)) return TryRgb(s, out color);
        if (NamedColors.TryGetValue(s, out var named)) { color = named; return true; }
        return SKColor.TryParse(s, out color); // best-effort (Skia parses some names)
    }

    private static bool TryRgb(string s, out SKColor color)
    {
        color = SKColors.Black;
        var open = s.IndexOf('(');
        var close = s.IndexOf(')');
        if (open < 0 || close <= open) return false;
        var parts = s[(open + 1)..close].Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        byte Ch(string p) => (byte)Math.Clamp(
            p.EndsWith('%')
                ? (int)Math.Round(double.Parse(p[..^1], CultureInfo.InvariantCulture) / 100.0 * 255)
                : (int)Math.Round(double.Parse(p, CultureInfo.InvariantCulture)), 0, 255);
        try
        {
            var a = parts.Length >= 4
                ? (byte)Math.Clamp((int)Math.Round(double.Parse(parts[3], CultureInfo.InvariantCulture) * 255), 0, 255)
                : (byte)255;
            color = new SKColor(Ch(parts[0]), Ch(parts[1]), Ch(parts[2]), a);
            return true;
        }
        catch (Exception) { return false; }
    }

    private static readonly Dictionary<string, SKColor> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = SKColors.Black, ["white"] = SKColors.White, ["red"] = SKColors.Red,
        ["green"] = new SKColor(0, 128, 0), ["blue"] = SKColors.Blue, ["yellow"] = SKColors.Yellow,
        ["cyan"] = SKColors.Cyan, ["magenta"] = SKColors.Magenta, ["gray"] = SKColors.Gray,
        ["grey"] = SKColors.Gray, ["orange"] = new SKColor(255, 165, 0), ["purple"] = new SKColor(128, 0, 128),
        ["silver"] = new SKColor(192, 192, 192), ["maroon"] = new SKColor(128, 0, 0),
        ["navy"] = new SKColor(0, 0, 128), ["teal"] = new SKColor(0, 128, 128),
        ["lime"] = SKColors.Lime, ["olive"] = new SKColor(128, 128, 0),
    };
}
