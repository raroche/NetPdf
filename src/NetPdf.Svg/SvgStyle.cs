// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using SkiaSharp;

namespace NetPdf.Svg;

/// <summary>Resolved SVG presentation state inherited down the element tree. <see cref="FillRef"/> /
/// <see cref="StrokeRef"/> hold a <c>url(#id)</c> gradient reference (resolved at paint time against the
/// shape's bounding box); a <see langword="null"/> ref means a plain color. Font properties drive
/// <c>&lt;text&gt;</c> shaping.</summary>
internal readonly record struct SvgStyle(
    SKColor Fill, string? FillRef, bool HasExplicitFill,
    SKColor? Stroke, string? StrokeRef, float StrokeWidth,
    float FillOpacity, float StrokeOpacity, SKColor CurrentColor,
    float FontSizePx, string? FontFamily, int FontWeight, bool Italic, string? TextAnchor)
{
    public static SvgStyle Initial => new(
        Fill: SKColors.Black, FillRef: null, HasExplicitFill: false,
        Stroke: null, StrokeRef: null, StrokeWidth: 1,
        FillOpacity: 1, StrokeOpacity: 1, CurrentColor: SKColors.Black,
        FontSizePx: 16, FontFamily: null, FontWeight: 400, Italic: false, TextAnchor: null);
}
