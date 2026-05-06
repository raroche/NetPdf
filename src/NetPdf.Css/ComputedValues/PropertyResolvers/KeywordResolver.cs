// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Resolves CSS keyword values per CSS Values L4 §4.1 against per-property keyword
/// tables. Each <see cref="PropertyType.Keyword"/> property has a closed set of
/// admitted identifiers; this resolver maps the input string (ASCII case-insensitive
/// per CSS Syntax L3) to a stable small-integer id stored in
/// <see cref="ComputedSlot.FromKeyword"/>. Downstream stages (BoxBuilder, layout)
/// switch on the id without needing to re-parse the source text.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyword id stability.</b> The ids assigned per property are part of the internal
/// contract between cascade and downstream consumers. They're zero-based, dense, and
/// never change for an existing keyword once shipped — adding a new admitted keyword
/// appends a new id at the end. Renaming or reordering would silently break BoxBuilder.
/// </para>
/// <para>
/// <b>Cycle 1 coverage.</b> Tables are populated for the 24 keyword-typed properties
/// in <c>properties.json</c>. Unrecognized keywords for a covered property emit
/// <see cref="CssDiagnosticCodes.CssPropertyValueInvalid001"/>. Values that resemble
/// CSS-wide keywords (<c>inherit</c>, <c>initial</c>, <c>unset</c>, <c>revert</c>,
/// <c>revert-layer</c>) are NOT this resolver's job — the cascade handles them at
/// declaration assignment time and they never reach a per-property resolver.
/// </para>
/// </remarks>
internal static class KeywordResolver
{
    public static ResolverResult Resolve(
        string value,
        PropertyId propertyId,
        string propertyName,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location)
    {
        if (!Tables.TryGetValue(propertyId, out var table))
        {
            // No table registered for this Keyword-typed property — defer to a later
            // cycle. Carry the raw text so a future resolver can pick up where we
            // left off without having to re-cascade.
            return ResolverResult.Deferred(value);
        }
        if (table.TryGetValue(value.ToLowerInvariant(), out var id))
            return ResolverResult.Resolved(ComputedSlot.FromKeyword(id));

        diagnostics?.Emit(new CssDiagnostic(
            CssDiagnosticCodes.CssPropertyValueInvalid001,
            $"'{propertyName}: {value}' — '{value}' is not an admitted keyword for this property.",
            CssDiagnosticSeverity.Warning,
            location));
        return ResolverResult.Invalid();
    }

    /// <summary>Try to look up a keyword id without dispatching through the resolver
    /// (used by tests + by future composite-type resolvers that branch on a keyword
    /// alternative).</summary>
    public static bool TryGetId(PropertyId propertyId, string keyword, out int id)
    {
        id = -1;
        if (!Tables.TryGetValue(propertyId, out var table)) return false;
        return table.TryGetValue(keyword.ToLowerInvariant(), out id);
    }

    /// <summary>The per-property keyword tables. Each property maps every admitted
    /// keyword (lower-case) to a stable zero-based id. Populated at module load.</summary>
    private static readonly FrozenDictionary<PropertyId, FrozenDictionary<string, int>> Tables =
        BuildAllTables();

    private static FrozenDictionary<PropertyId, FrozenDictionary<string, int>> BuildAllTables()
    {
        var b = new Dictionary<PropertyId, FrozenDictionary<string, int>>();

        // align-items / align-self / justify-content — CSS Box Alignment L3 §4.
        b[PropertyId.AlignItems] = T("normal", "stretch", "center", "start", "end",
            "flex-start", "flex-end", "self-start", "self-end", "baseline", "first", "last");
        b[PropertyId.AlignSelf] = T("auto", "normal", "stretch", "center", "start", "end",
            "flex-start", "flex-end", "self-start", "self-end", "baseline");
        b[PropertyId.JustifyContent] = T("normal", "stretch", "center", "start", "end",
            "flex-start", "flex-end", "left", "right", "space-between", "space-around",
            "space-evenly");

        // Border style — CSS Backgrounds & Borders 3 §4.4.
        var borderStyle = T("none", "hidden", "dotted", "dashed", "solid", "double",
            "groove", "ridge", "inset", "outset");
        b[PropertyId.BorderTopStyle] = borderStyle;
        b[PropertyId.BorderRightStyle] = borderStyle;
        b[PropertyId.BorderBottomStyle] = borderStyle;
        b[PropertyId.BorderLeftStyle] = borderStyle;

        // border-collapse — CSS Tables 3 §6.
        b[PropertyId.BorderCollapse] = T("separate", "collapse");

        // box-sizing — CSS Basic UI 4 §10.
        b[PropertyId.BoxSizing] = T("content-box", "border-box");

        // break-inside — CSS Fragmentation 3 §3.2.
        b[PropertyId.BreakInside] = T("auto", "avoid", "avoid-page", "avoid-column", "avoid-region");

        // clear — CSS 2.2 §9.5.2 (still active in L3).
        b[PropertyId.Clear] = T("none", "left", "right", "both", "inline-start", "inline-end");

        // cursor — CSS Basic UI 4 §8.1.1 (subset of admitted bare keywords; URL forms
        // are out-of-scope for the keyword resolver).
        b[PropertyId.Cursor] = T("auto", "default", "none", "context-menu", "help", "pointer",
            "progress", "wait", "cell", "crosshair", "text", "vertical-text", "alias", "copy",
            "move", "no-drop", "not-allowed", "grab", "grabbing", "all-scroll", "col-resize",
            "row-resize", "n-resize", "e-resize", "s-resize", "w-resize", "ne-resize",
            "nw-resize", "se-resize", "sw-resize", "ew-resize", "ns-resize", "nesw-resize",
            "nwse-resize", "zoom-in", "zoom-out");

        // display — CSS Display 3 §2 (the common single-keyword forms; multi-keyword
        // syntax `inline flex` etc. is out-of-scope for cycle 1).
        b[PropertyId.Display] = T("block", "inline", "inline-block", "list-item", "flex",
            "inline-flex", "grid", "inline-grid", "flow-root", "table", "inline-table",
            "table-row-group", "table-header-group", "table-footer-group", "table-row",
            "table-cell", "table-column-group", "table-column", "table-caption",
            "ruby", "ruby-base", "ruby-text", "ruby-base-container", "ruby-text-container",
            "contents", "none");

        // flex-direction / flex-wrap — CSS Flexbox 1 §5.
        b[PropertyId.FlexDirection] = T("row", "row-reverse", "column", "column-reverse");
        b[PropertyId.FlexWrap] = T("nowrap", "wrap", "wrap-reverse");

        // float — CSS 2.2 §9.5.1.
        b[PropertyId.Float] = T("none", "left", "right", "inline-start", "inline-end");

        // font-style — CSS Fonts 4 §3.5 (single-keyword form; oblique-with-angle is
        // out-of-scope for cycle 1).
        b[PropertyId.FontStyle] = T("normal", "italic", "oblique");

        // overflow — CSS Overflow 3 §3.1.
        var overflow = T("visible", "hidden", "clip", "scroll", "auto");
        b[PropertyId.OverflowX] = overflow;
        b[PropertyId.OverflowY] = overflow;

        // position — CSS Positioned Layout 3 §2.
        b[PropertyId.Position] = T("static", "relative", "absolute", "fixed", "sticky");

        // text-align — CSS Text 3 §7.1.
        b[PropertyId.TextAlign] = T("start", "end", "left", "right", "center", "justify",
            "match-parent", "justify-all");

        // text-decoration-line — CSS Text Decoration 3 §2 (single-keyword form; the
        // shorthand syntax allowing multiple lines like `underline overline` is
        // cycle-2 work).
        b[PropertyId.TextDecorationLine] = T("none", "underline", "overline", "line-through",
            "blink");

        // text-transform — CSS Text 3 §6.1.
        b[PropertyId.TextTransform] = T("none", "capitalize", "uppercase", "lowercase",
            "full-width", "full-size-kana");

        // white-space — CSS Text 3 §3.1 (legacy single-keyword form; the modern
        // `white-space: <white-space-collapse> || <text-wrap-mode>` shorthand is
        // out-of-scope for cycle 1).
        b[PropertyId.WhiteSpace] = T("normal", "pre", "nowrap", "pre-wrap", "break-spaces", "pre-line");

        return b.ToFrozenDictionary();
    }

    /// <summary>Build a keyword → id table from a sequence of admitted keywords.
    /// Ids are zero-based + dense in argument order so each entry's id matches its
    /// position in the source code (handy for downstream switches).</summary>
    private static FrozenDictionary<string, int> T(params string[] keywords)
    {
        var dict = new Dictionary<string, int>(keywords.Length, StringComparer.Ordinal);
        for (var i = 0; i < keywords.Length; i++) dict[keywords[i]] = i;
        return dict.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
