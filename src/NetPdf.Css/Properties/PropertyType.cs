// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Properties;

/// <summary>
/// The value-type taxonomy for a CSS property's declared value. Drives per-property parser
/// dispatch in Tasks 9–10. The set is intentionally pragmatic — the most common shapes that
/// actual CSS properties use, not a full CSS Values L4 type system. New values get added as
/// new properties land in <c>properties.json</c>.
/// </summary>
internal enum PropertyType : byte
{
    /// <summary>An unknown / placeholder type. Treated as opaque text.</summary>
    Unknown = 0,
    /// <summary>A CSS color (<c>rgb()</c>, <c>oklch()</c>, named, hex, current-color, …).</summary>
    Color = 1,
    /// <summary>A CSS length (px, em, rem, pt, %, …).</summary>
    Length = 2,
    /// <summary>A length OR percentage (e.g., <c>padding</c>).</summary>
    LengthPercentage = 3,
    /// <summary>A length, percentage, or the keyword <c>auto</c> (e.g., <c>margin</c>, <c>width</c>).</summary>
    LengthPercentageAuto = 4,
    /// <summary>A unitless number (e.g., <c>line-height</c>).</summary>
    Number = 5,
    /// <summary>A signed integer.</summary>
    Integer = 6,
    /// <summary>A standalone percentage (rare — most properties use <see cref="LengthPercentage"/>).</summary>
    Percentage = 7,
    /// <summary>A keyword from a closed set (e.g., <c>display</c>, <c>position</c>).</summary>
    Keyword = 8,
    /// <summary>A quoted CSS string.</summary>
    String = 9,
    /// <summary>A URL (<c>url(...)</c>).</summary>
    Url = 10,
    /// <summary>A CSS time value (s, ms).</summary>
    Time = 11,
    /// <summary>A CSS angle value (deg, rad, grad, turn).</summary>
    Angle = 12,
    /// <summary>A CSS resolution value (dpi, dpcm, dppx).</summary>
    Resolution = 13,
    /// <summary>A font-family list (comma-separated identifiers/strings).</summary>
    FontFamilyList = 14,
    /// <summary>A font-weight (normal, bold, bolder, lighter, 100..900).</summary>
    FontWeight = 15,
    /// <summary>A custom property type — value is opaque to the parser dispatch table.</summary>
    Custom = 255,
}
