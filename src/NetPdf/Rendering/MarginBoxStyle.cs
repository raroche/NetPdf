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
/// Builds a <see cref="ComputedStyle"/> for a CSS Paged Media §6.4 margin box from its declared
/// longhands, and reads the alignment a declared <c>text-align</c> / <c>vertical-align</c> implies.
/// Phase 3 Task 21 cycle 4 — per-box style for running headers/footers.
/// </summary>
/// <remarks>
/// <para>
/// The style is built from the box's OWN declarations only (no page/root inheritance this cycle —
/// see below): each SUPPORTED longhand is cascaded by importance then source order (a later normal
/// can't override an earlier <c>!important</c>) and the winners are resolved through
/// <see cref="PropertyResolverDispatch"/> onto a rented style. Unspecified properties stay unset
/// and the shaper/painter readers fall back to their CSS initial defaults (16px / the resolver's
/// default family / 400 / black / <c>start</c>), so <see cref="ComputedStyle.IsSet"/> means "the
/// box declared this".
/// </para>
/// <para>
/// <b>Supported properties (cycle 4, post-PR-#133 review P2 — a WHITELIST).</b> Only
/// <c>font-family</c> / <c>font-size</c> / <c>font-weight</c> / <c>font-style</c> / <c>color</c> /
/// <c>text-align</c> are materialized, plus <c>vertical-align</c> read raw (it isn't cascade-
/// resolved yet). Other declarations (<c>padding</c> / <c>border</c> / <c>background</c> / …) are
/// deliberately IGNORED — the painter derives content origin from the box's border + padding, so
/// materializing them would shift (or paint behind) the margin-box text before that's intended.
/// </para>
/// <para>
/// <b>Deferred (later cycles, deferrals.md#layout-to-pdf-pipeline).</b> Page-context + root
/// INHERITANCE (CSS Page 3: a margin box inherits from the page box, which inherits from the root —
/// so <c>@page { color: red; @top-center { … } }</c> should tint the box; cycle 4 is own-
/// declarations-only, pinned by a test), the <c>font</c> shorthand, and relative font sizes
/// (<c>em</c> / <c>%</c> / <c>larger</c>).
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

    /// <summary>Build the margin box's <see cref="ComputedStyle"/> from its declarations: each
    /// supported longhand's cascade winner (importance then source order) is resolved + materialized;
    /// unsupported properties are skipped. <paramref name="diagnostics"/> receives invalid-value
    /// diagnostics (e.g. <c>color: bogus</c>). The result is marked box-owned (its <c>Dispose</c>
    /// becomes a no-op) for the synthetic <c>Box</c> the painter wraps it in.</summary>
    public static ComputedStyle Build(
        ImmutableArray<CssDeclaration> declarations, ICssDiagnosticsSink? diagnostics = null)
    {
        var style = ComputedStyle.Rent();
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
