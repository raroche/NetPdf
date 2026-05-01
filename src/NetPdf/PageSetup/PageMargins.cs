// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// Default page margins applied unless overridden by <c>@page</c> CSS.
/// All values are in CSS pixels (1 in = 96 px at default DPI).
/// </summary>
public readonly record struct PageMargins(double TopPx, double RightPx, double BottomPx, double LeftPx)
{
    /// <summary>1-inch margin on every side. Equivalent to <c>@page { margin: 1in; }</c>.</summary>
    public static PageMargins Default { get; } = new(96, 96, 96, 96);

    /// <summary>Zero margin (edge-to-edge).</summary>
    public static PageMargins None { get; } = new(0, 0, 0, 0);

    /// <summary>Uniform margin on all four sides.</summary>
    public static PageMargins Uniform(double px) => new(px, px, px, px);

    /// <summary>Vertical / horizontal pair.</summary>
    public static PageMargins Symmetric(double verticalPx, double horizontalPx) =>
        new(verticalPx, horizontalPx, verticalPx, horizontalPx);
}
