// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Immutable;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
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
/// The style is built from the box's OWN declarations only (no page/root inheritance this cycle):
/// each known longhand is resolved through <see cref="PropertyResolverDispatch"/> onto a rented
/// style; unspecified properties stay unset and the shaper/painter readers fall back to their CSS
/// initial defaults (16px / the resolver's default family / 400 / black / <c>start</c>). Because no
/// defaults are pre-applied, <see cref="ComputedStyle.IsSet"/> means "the box declared this".
/// </para>
/// <para>
/// <b>Cycle 4 scope.</b> LONGHANDS only — <c>font-family</c> / <c>font-size</c> / <c>font-weight</c>
/// / <c>font-style</c> / <c>color</c> / <c>text-align</c> (+ <c>vertical-align</c>, read raw since
/// it isn't cascade-resolved yet). The <c>font</c> shorthand, page/root inheritance, and
/// relative (<c>em</c> / <c>%</c> / <c>larger</c>) font sizes are tracked follow-ups
/// (deferrals.md#layout-to-pdf-pipeline).
/// </para>
/// </remarks>
internal static class MarginBoxStyle
{
    /// <summary>Build the margin box's <see cref="ComputedStyle"/> from its declarations (applied
    /// in source order; later wins). The result is marked box-owned (its <c>Dispose</c> becomes a
    /// no-op) for the synthetic <c>Box</c> the painter wraps it in.</summary>
    public static ComputedStyle Build(ImmutableArray<CssDeclaration> declarations)
    {
        var style = ComputedStyle.Rent();
        if (!declarations.IsDefaultOrEmpty)
        {
            foreach (var decl in declarations)
            {
                // `content` (handled separately via CssContentList) + the `font` shorthand + any
                // other non-longhand fall through NameToId and are skipped.
                if (!PropertyMetadata.NameToId.TryGetValue(decl.Property, out var id)) continue;
                PropertyResolverDispatch.Resolve(id, decl.Value.RawText).MaterializeInto(style, id);
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
        // justify-all=7.
        return style.ReadKeywordOrDefault(PropertyId.TextAlign, defaultIndex: 0) switch
        {
            4 => 0.5,            // center
            1 or 3 or 7 => 1.0,  // end / right / justify-all
            _ => 0.0,            // start / left / justify / match-parent
        };
    }

    /// <summary>The fraction of leftover block space before the line (0 = top, 0.5 = middle,
    /// 1 = bottom) implied by a DECLARED <c>vertical-align: top|middle|bottom</c>, or
    /// <see langword="null"/> when none (the caller keeps the name-derived default).
    /// <c>vertical-align</c> isn't cascade-resolved yet, so it's read from the raw declarations;
    /// the last recognized value wins.</summary>
    public static double? VerticalAlignFactor(ImmutableArray<CssDeclaration> declarations)
    {
        if (declarations.IsDefaultOrEmpty) return null;
        double? factor = null;
        foreach (var decl in declarations)
        {
            if (!string.Equals(decl.Property, "vertical-align", StringComparison.OrdinalIgnoreCase)) continue;
            factor = decl.Value.RawText.Trim().ToLowerInvariant() switch
            {
                "top" => 0.0,
                "middle" => 0.5,
                "bottom" => 1.0,
                _ => factor, // unrecognized (e.g. baseline) — ignore, keep the last recognized value
            };
        }
        return factor;
    }
}
