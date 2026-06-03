// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;
using NetPdf.Layout.Layouters;

namespace NetPdf.Rendering;

/// <summary>
/// Builds a <see cref="ComputedStyle"/> for a CSS Paged Media §6.4 margin box (or the page context)
/// from declared longhands + inherited values, and reads the alignment a declared <c>text-align</c>
/// / <c>vertical-align</c> implies. Phase 3 Task 21 cycle 4 (per-box style) + cycle 5 (inheritance)
/// — running headers/footers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Inheritance (cycle 5).</b> A style is built by INHERITING the supported properties from a
/// parent then cascading this level's own declarations on top. The CSS Page 3 chain is built in two
/// calls: <c>pageContext = Build(@page declarations, rootElementStyle)</c> then
/// <c>marginBox = Build(box declarations, pageContext)</c> — so <c>@page { color: gray; @top-center
/// { … } }</c> tints the box and <c>html { font-family: … }</c> flows into headers/footers. Own
/// declarations cascade by importance then source order (a later normal can't override an earlier
/// <c>!important</c>) and override the inherited value; unspecified properties keep the inherited
/// one, or fall back to the reader defaults (16px / default family / 400 / black / <c>start</c>)
/// when nothing up the chain set them.
/// </para>
/// <para>
/// <b>Supported properties (post-PR-#133 review P2 — a WHITELIST).</b> Only <c>font-family</c> /
/// <c>font-size</c> / <c>font-weight</c> / <c>font-style</c> / <c>color</c> / <c>text-align</c> are
/// materialized + inherited (all six are inherited properties), plus <c>vertical-align</c> read raw
/// (it isn't cascade-resolved, and isn't an inherited property). Other declarations (<c>padding</c>
/// / <c>border</c> / <c>background</c> / …) are deliberately IGNORED — the painter derives content
/// origin from the box's border + padding, so materializing them would shift (or paint behind) the
/// margin-box text before that's intended.
/// </para>
/// <para>
/// <b>Deferred (later cycles, deferrals.md#layout-to-pdf-pipeline).</b> The <c>font</c> shorthand,
/// relative font sizes (<c>em</c> / <c>%</c> / <c>larger</c> — a deferred inherited font-size is
/// copied but not re-resolved against the parent), and the §5.3 three-box-per-edge sizing.
/// </para>
/// </remarks>
internal static class MarginBoxStyle
{
    /// <summary>The materializable longhands cycle 4 honors, in a fixed order (deterministic
    /// materialization). <c>vertical-align</c> is supported too but read raw (not cascade-resolved).</summary>
    private static readonly ImmutableArray<PropertyId> SupportedStyleIds = ImmutableArray.Create(
        PropertyId.FontFamily, PropertyId.FontSize, PropertyId.FontWeight, PropertyId.FontStyle,
        PropertyId.Color, PropertyId.TextAlign);

    private static readonly FrozenSet<PropertyId> SupportedStyleIdSet = SupportedStyleIds.ToFrozenSet();

    /// <summary>Build a margin-box (or page-context) <see cref="ComputedStyle"/>: first INHERIT the
    /// supported properties (all are inherited) from <paramref name="parentStyle"/>, then resolve +
    /// materialize this level's own declarations on top (each property's cascade winner — importance
    /// then source order). Used twice for the CSS Page 3 chain: page-context = Build(@page decls,
    /// rootStyle); margin box = Build(box decls, pageContext). <paramref name="parentStyle"/> null
    /// at the top (own-declarations only). <paramref name="diagnostics"/> receives invalid-value
    /// diagnostics (e.g. <c>color: bogus</c>). The result is marked box-owned (its <c>Dispose</c>
    /// becomes a no-op) for the synthetic <c>Box</c> the painter wraps it in.</summary>
    public static ComputedStyle Build(
        ImmutableArray<CssDeclaration> declarations,
        ComputedStyle? parentStyle = null,
        ICssDiagnosticsSink? diagnostics = null)
    {
        var style = ComputedStyle.Rent();

        // Inheritance (cycle 5): copy each supported property's slot from the parent first, so an
        // own-declaration below overwrites it but an unspecified one flows down (margin box ← @page
        // ← root). All six supported properties are inherited (vertical-align isn't, and is read
        // raw — never inherited). Mirrors BoxBuilder.ApplyInheritance (slot + side-table + deferred).
        if (parentStyle is not null)
        {
            foreach (var id in SupportedStyleIds)
            {
                if (!parentStyle.IsSet(id)) continue;
                var slot = parentStyle.Get(id);
                if (!slot.IsUnset)
                {
                    style.Set(id, slot);
                    if (slot.Tag == ComputedSlotTag.SideTableIndex
                        && parentStyle.TryGetSideTablePayloadRaw(id, out var payload) && payload is not null)
                        style.SetSideTablePayload(id, payload); // the font-family list
                }
                else if (parentStyle.TryGetDeferred(id, out var raw) && raw is not null)
                {
                    style.SetDeferred(id, raw);
                }
            }
        }

        if (!declarations.IsDefaultOrEmpty)
        {
            // Cascade each supported longhand by importance then source order.
            Dictionary<PropertyId, Winner>? winners = null;
            foreach (var decl in declarations)
            {
                if (!PropertyMetadata.NameToId.TryGetValue(decl.Property, out var id)) continue;
                if (!SupportedStyleIdSet.Contains(id)) continue; // whitelist (review P2)
                winners ??= new Dictionary<PropertyId, Winner>();
                if (winners.TryGetValue(id, out var w) && w.Important && !decl.IsImportant) continue;
                winners[id] = new Winner(decl.Value.RawText, decl.IsImportant);
            }

            if (winners is not null)
            {
                // Materialize in the fixed whitelist order (deterministic; these longhands are
                // independent so order doesn't change the result, but the walk is stable).
                foreach (var id in SupportedStyleIds)
                {
                    if (winners.TryGetValue(id, out var w))
                        PropertyResolverDispatch.Resolve(id, w.Value, diagnostics).MaterializeInto(style, id);
                }
            }
        }
        style.MarkAsBoxOwned();
        return style;
    }

    /// <summary>The fraction of leftover inline space before the line (0 = start, 0.5 = center,
    /// 1 = end) implied by a DECLARED <c>text-align</c>, or <see langword="null"/> when the box
    /// didn't declare one (the caller keeps the box's name-derived default). LTR mapping.</summary>
    public static double? HorizontalAlignFactor(ComputedStyle style)
    {
        if (!style.IsSet(PropertyId.TextAlign)) return null;
        // KeywordResolver indices: start=0 end=1 left=2 right=3 center=4 justify=5 match-parent=6
        // justify-all=7. On a single margin-box line, justify / justify-all behave as start
        // (post-PR-#133 review P3 — justify-all is not an end alignment).
        return style.ReadKeywordOrDefault(PropertyId.TextAlign, defaultIndex: 0) switch
        {
            4 => 0.5,       // center
            1 or 3 => 1.0,  // end / right
            _ => 0.0,       // start / left / justify / justify-all / match-parent
        };
    }

    /// <summary>The fraction of leftover block space before the line (0 = top, 0.5 = middle,
    /// 1 = bottom) implied by a DECLARED <c>vertical-align: top|middle|bottom</c>, or
    /// <see langword="null"/> when none / unrecognized (the caller keeps the name-derived default).
    /// <c>vertical-align</c> isn't cascade-resolved yet, so it's read from the raw declarations,
    /// cascaded by importance then source order (review P2/P3).</summary>
    public static double? VerticalAlignFactor(ImmutableArray<CssDeclaration> declarations)
    {
        if (declarations.IsDefaultOrEmpty) return null;
        string? winner = null;
        var winnerImportant = false;
        foreach (var decl in declarations)
        {
            if (!string.Equals(decl.Property, "vertical-align", StringComparison.OrdinalIgnoreCase)) continue;
            if (winner is not null && winnerImportant && !decl.IsImportant) continue; // importance
            winner = decl.Value.RawText;
            winnerImportant = decl.IsImportant;
        }
        return winner?.Trim().ToLowerInvariant() switch
        {
            "top" => 0.0,
            "middle" => 0.5,
            "bottom" => 1.0,
            _ => null, // unrecognized (e.g. baseline) / none → keep the name-derived default
        };
    }

    /// <summary>A property's cascade winner so far: its raw value + whether it came from an
    /// <c>!important</c> declaration.</summary>
    private readonly record struct Winner(string Value, bool Important);
}
