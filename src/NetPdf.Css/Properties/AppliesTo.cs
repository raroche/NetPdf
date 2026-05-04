// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Properties;

/// <summary>
/// The element class a CSS property applies to per its CSS spec — for example
/// <c>background-color</c> applies to "all elements" while <c>top</c>/<c>right</c>/
/// <c>bottom</c>/<c>left</c> apply only to positioned elements.
/// </summary>
/// <remarks>
/// <para>
/// <b>Important:</b> this metadata is for VALIDATION + USED-VALUE / LAYOUT decisions, NOT
/// for filtering during cascade resolution. The CSS Cascade L4 §6 algorithm computes the
/// specified value of every property for every element regardless of whether the property
/// applies; the "applies to" gate kicks in at used-value / layout time when the cascade's
/// output is consumed. Filtering during cascade would silently drop declarations that
/// downstream stages (e.g., custom-property fallback chains, computed-value resolvers)
/// might still need to inspect, and would break inheritance through elements that the
/// property doesn't apply to.
/// </para>
/// <para>
/// Pragmatic enum set — new categories get added as new properties land. Values match the
/// "Applies to" column in the CSS specs (e.g., CSS Backgrounds 3 §3.1).
/// </para>
/// </remarks>
internal enum AppliesTo : byte
{
    /// <summary>Unknown / unset — defaults to <see cref="All"/> for safety.</summary>
    Unknown = 0,
    /// <summary>All elements.</summary>
    All = 1,
    /// <summary>All block, inline, and replaced elements (the typical "box" selector).</summary>
    BlockOrInlineOrReplaced = 2,
    /// <summary>Positioned elements (those with <c>position</c> not <c>static</c>).</summary>
    Positioned = 3,
    /// <summary>Block-level elements only.</summary>
    BlockOnly = 4,
    /// <summary>Inline-level elements only.</summary>
    InlineOnly = 5,
    /// <summary>List items (<c>display: list-item</c>).</summary>
    ListItem = 6,
    /// <summary>Table elements (table, row, cell, etc.).</summary>
    TableElements = 7,
    /// <summary>Replaced elements only (img, video, etc.).</summary>
    ReplacedOnly = 8,
    /// <summary>Flex items (children of <c>display: flex</c>).</summary>
    FlexItems = 9,
    /// <summary>Grid items (children of <c>display: grid</c>).</summary>
    GridItems = 10,
}
