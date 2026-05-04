// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Properties;

/// <summary>
/// The element class a CSS property applies to. The cascade resolver consults this when
/// evaluating whether a declaration affects a given element — properties that don't apply
/// to the element are skipped before specificity comparison.
/// </summary>
/// <remarks>
/// Values match the "Applies to" column in the CSS specs (e.g., CSS Backgrounds 3 §3.1
/// lists <c>background-color</c> as applying to "all elements"). The set is pragmatic —
/// new categories get added as new properties land.
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
