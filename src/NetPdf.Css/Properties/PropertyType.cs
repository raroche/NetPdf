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
    /// <summary>A line-width per CSS Backgrounds &amp; Borders 3 §3.1: <c>&lt;length&gt; | thin | medium | thick</c>.</summary>
    LineWidth = 16,
    /// <summary>A font-size per CSS Fonts 4 §3.4: <c>&lt;absolute-size&gt; | &lt;relative-size&gt; | &lt;length-percentage&gt; | math</c>.</summary>
    FontSize = 17,
    /// <summary>A line-height per CSS Inline 3 §2.4.4: <c>normal | &lt;number&gt; | &lt;length-percentage&gt;</c>.</summary>
    LineHeight = 18,
    /// <summary>The CSS <c>content</c> value: <c>normal | none | &lt;content-list&gt; | image | string | counter() | …</c></summary>
    Content = 19,
    /// <summary>A vertical-align value: <c>baseline | sub | super | top | … | &lt;length-percentage&gt;</c>.</summary>
    VerticalAlign = 20,
    /// <summary>A flex-basis value: <c>content | &lt;length-percentage&gt; | auto</c>.</summary>
    FlexBasis = 21,
    /// <summary>Text-spacing per CSS Text 3 §10.1: <c>normal | &lt;length&gt;</c>. Used by
    /// <c>letter-spacing</c> and <c>word-spacing</c>.</summary>
    TextSpacing = 22,
    /// <summary>A max-* dimension per CSS Sizing 3 §5.2: <c>none | &lt;length-percentage&gt; |
    /// &lt;intrinsic-sizing&gt;</c>. Used by <c>max-width</c> and <c>max-height</c>.</summary>
    MaxSize = 23,
    /// <summary>A CSS Grid L1 §7.2 track list (= the value of
    /// <c>grid-template-rows</c> / <c>grid-template-columns</c>). Stores
    /// the parsed AST including <c>&lt;length&gt;</c> tracks, <c>fr</c>
    /// tracks, <c>auto</c> / <c>min-content</c> / <c>max-content</c>
    /// keywords, <c>minmax()</c> + <c>fit-content()</c> functions,
    /// <c>repeat(&lt;int&gt;, …)</c> integer-count expansions, named
    /// lines, and <c>repeat(auto-fill, …)</c> / <c>repeat(auto-fit, …)</c>
    /// markers. <c>auto-fill</c> / <c>auto-fit</c> expansion defers to
    /// layout time when container size is known. ComputedSlot stores the
    /// AST via the side-table pattern.</summary>
    GridTemplateList = 24,
    /// <summary>A CSS Grid L1 §8.3 grid-line value (= the value of
    /// <c>grid-row-start</c> / <c>grid-row-end</c> / <c>grid-column-start</c>
    /// / <c>grid-column-end</c>). Encodes <c>auto</c>, <c>&lt;integer&gt;</c>
    /// line numbers, <c>span &lt;integer&gt;</c>, and <c>&lt;custom-ident&gt;</c>
    /// named lines. The 4-arg <c>grid-area</c> shorthand expands into four
    /// of these (= one per longhand).</summary>
    GridLine = 25,
    /// <summary>A custom property type — value is opaque to the parser dispatch table.</summary>
    Custom = 255,
}
