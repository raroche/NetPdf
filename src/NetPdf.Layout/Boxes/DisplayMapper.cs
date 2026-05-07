// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Layout.Boxes;

/// <summary>
/// Maps a CSS computed <c>display</c> keyword (the user-authored or UA-default
/// value, e.g., <c>"block"</c>, <c>"inline-flex"</c>, <c>"table-row"</c>) to
/// the matching <see cref="BoxKind"/>. Cross-references
/// <see cref="HtmlReplacedElements"/> so that a replaced element with
/// <c>display: inline</c> resolves to <see cref="BoxKind.InlineReplacedElement"/>
/// (not a generic <see cref="BoxKind.InlineBox"/>) and likewise for block.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate mapper</b> instead of a switch in <see cref="BoxBuilder"/>:
/// the keyword → kind translation is the one place the mapping is encoded; if
/// CSS adds a new <c>display</c> value (e.g., a future rendering module),
/// only this file needs to grow. The mapper is also pure (no dependencies on
/// cascade state) so tests cover the matrix exhaustively without DOM
/// scaffolding.
/// </para>
/// <para>
/// <b>Cycle 1 deferrals</b> — <c>ruby</c> + descendants emit
/// <see cref="DisplayMappingResult.Unsupported"/> so <see cref="BoxBuilder"/>
/// can decide to skip / fall back / diagnose; <c>display: contents</c> emits
/// <see cref="DisplayMappingResult.Contents"/> which BoxBuilder handles by
/// promoting the children up one level (per Display L3 §3.1.1);
/// <c>display: none</c> emits <see cref="DisplayMappingResult.None"/> which
/// BoxBuilder honors by skipping the element entirely.
/// </para>
/// </remarks>
internal static class DisplayMapper
{
    /// <summary>Resolution outcome categories — distinguishes "found a kind"
    /// from "skip this element" from "this display value isn't a normal box
    /// kind".</summary>
    public enum DisplayMappingResult : byte
    {
        /// <summary>Resolved to a concrete <see cref="BoxKind"/>; consult
        /// the <c>out kind</c> parameter.</summary>
        Resolved = 0,
        /// <summary><c>display: none</c> — caller skips the element entirely.</summary>
        None = 1,
        /// <summary><c>display: contents</c> — per Display L3 §3.1.1 the box
        /// itself doesn't render but its children do, attached to the
        /// grandparent. <see cref="BoxBuilder"/> handles this by promoting
        /// the element's children up one level.</summary>
        Contents = 2,
        /// <summary>Display value is post-v1 (ruby family, math, etc.). Caller
        /// decides whether to skip + diagnose or fall back to a generic kind.</summary>
        Unsupported = 3,
    }

    /// <summary>Map a computed <c>display</c> keyword to a <see cref="BoxKind"/>.
    /// <paramref name="elementLocalName"/> is consulted to detect replaced
    /// elements (per <see cref="HtmlReplacedElements"/>) so the right
    /// block-vs-inline replaced kind comes back. Pass <see langword="null"/>
    /// when there's no element (e.g., synthetic anonymous content).</summary>
    public static DisplayMappingResult Map(
        string display,
        string? elementLocalName,
        out BoxKind kind)
    {
        ArgumentNullException.ThrowIfNull(display);
        kind = default;

        // Normalize once for the switch.
        var d = display.ToLowerInvariant();

        // Replaced detection short-circuits the inline / block kind selection.
        var isReplaced = elementLocalName is not null
            && HtmlReplacedElements.IsReplaced(elementLocalName);

        // CSS Tables L3 §2: a replaced element specifying a table-internal
        // display value (table-row, table-cell, table-column, table-caption,
        // any of the *-group / column-group variants) is treated as
        // inline-level — replaced elements are atomic and cannot host the
        // structural roles those display values describe.
        if (isReplaced && IsTableInternalDisplay(d))
        {
            kind = BoxKind.InlineReplacedElement;
            return DisplayMappingResult.Resolved;
        }

        switch (d)
        {
            case "none":
                return DisplayMappingResult.None;

            case "contents":
                return DisplayMappingResult.Contents;

            // Block-level outer.
            case "block":
            case "flow-root":   // BFC variant — same kind in cycle 1
                kind = isReplaced ? BoxKind.BlockReplacedElement : BoxKind.BlockContainer;
                return DisplayMappingResult.Resolved;

            case "list-item":
                kind = BoxKind.ListItem;
                return DisplayMappingResult.Resolved;

            case "flex":
                kind = BoxKind.FlexContainer;
                return DisplayMappingResult.Resolved;

            case "grid":
                kind = BoxKind.GridContainer;
                return DisplayMappingResult.Resolved;

            case "table":
                kind = BoxKind.Table;
                return DisplayMappingResult.Resolved;

            // Inline-level outer.
            case "inline":
                kind = isReplaced ? BoxKind.InlineReplacedElement : BoxKind.InlineBox;
                return DisplayMappingResult.Resolved;

            case "inline-block":
                // The inner FC is flow-root regardless of replaced-ness; replaced
                // elements with display: inline-block use InlineReplacedElement
                // since the inner FC is moot for an atomic replaced element.
                kind = isReplaced ? BoxKind.InlineReplacedElement : BoxKind.InlineBlockContainer;
                return DisplayMappingResult.Resolved;

            case "inline-flex":
                kind = BoxKind.InlineFlexContainer;
                return DisplayMappingResult.Resolved;

            case "inline-grid":
                kind = BoxKind.InlineGridContainer;
                return DisplayMappingResult.Resolved;

            case "inline-table":
                kind = BoxKind.InlineTable;
                return DisplayMappingResult.Resolved;

            // Table internals (Tables L3 §2).
            case "table-row-group":
                kind = BoxKind.TableRowGroup;
                return DisplayMappingResult.Resolved;
            case "table-header-group":
                kind = BoxKind.TableHeaderGroup;
                return DisplayMappingResult.Resolved;
            case "table-footer-group":
                kind = BoxKind.TableFooterGroup;
                return DisplayMappingResult.Resolved;
            case "table-row":
                kind = BoxKind.TableRow;
                return DisplayMappingResult.Resolved;
            case "table-cell":
                kind = BoxKind.TableCell;
                return DisplayMappingResult.Resolved;
            case "table-column-group":
                kind = BoxKind.TableColumnGroup;
                return DisplayMappingResult.Resolved;
            case "table-column":
                kind = BoxKind.TableColumn;
                return DisplayMappingResult.Resolved;
            case "table-caption":
                kind = BoxKind.TableCaption;
                return DisplayMappingResult.Resolved;

            // Ruby family — post-v1.
            case "ruby":
            case "ruby-base":
            case "ruby-text":
            case "ruby-base-container":
            case "ruby-text-container":
                return DisplayMappingResult.Unsupported;

            default:
                return DisplayMappingResult.Unsupported;
        }
    }

    /// <summary><see langword="true"/> when <paramref name="display"/> is a
    /// table-internal display value per Tables L3 §2 — i.e., one of the
    /// values that describes a structural role inside a table (row group,
    /// row, cell, column / column-group, caption). The outer table
    /// values (<c>table</c>, <c>inline-table</c>) are NOT included; those
    /// are wrapper kinds, not internals.</summary>
    private static bool IsTableInternalDisplay(string display) => display switch
    {
        "table-row-group" or "table-header-group" or "table-footer-group"
            or "table-row" or "table-cell"
            or "table-column-group" or "table-column"
            or "table-caption" => true,
        _ => false,
    };
}
