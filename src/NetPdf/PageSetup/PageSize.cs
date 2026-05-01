// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// A page size expressed in CSS pixels. NetPdf's canonical layout unit is the CSS pixel
/// (1 px = 0.75 pt at 96 DPI); conversion to PDF points happens at emission only.
/// </summary>
public readonly record struct PageSize(double WidthPx, double HeightPx)
{
    // ISO 216 A-series (landscape variants accessible via .Landscape).
    public static PageSize A0 { get; } = new(3179, 4494);
    public static PageSize A1 { get; } = new(2245, 3179);
    public static PageSize A2 { get; } = new(1587, 2245);
    public static PageSize A3 { get; } = new(1123, 1587);
    public static PageSize A4 { get; } = new(794, 1123);
    public static PageSize A5 { get; } = new(559, 794);
    public static PageSize A6 { get; } = new(397, 559);

    // North American.
    public static PageSize Letter { get; } = new(816, 1056);
    public static PageSize Legal { get; } = new(816, 1344);
    public static PageSize Tabloid { get; } = new(1056, 1632);
    public static PageSize Executive { get; } = new(696, 1008);

    // ISO B-series.
    public static PageSize B4 { get; } = new(944, 1334);
    public static PageSize B5 { get; } = new(665, 944);

    /// <summary>The same page rotated 90 degrees.</summary>
    public PageSize Landscape => new(HeightPx, WidthPx);

    /// <summary>True when width &gt; height.</summary>
    public bool IsLandscape => WidthPx > HeightPx;
}
