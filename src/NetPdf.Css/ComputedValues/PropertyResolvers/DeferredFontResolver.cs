// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Resolves the PARENT-RELATIVE font properties that the cascade dispatch leaves
/// <see cref="ResolverResult.Deferred(string)"/> — they can't be resolved context-free because they
/// scale by the parent's value:
/// <list type="bullet">
///   <item><c>font-size</c> — <c>em</c> / <c>ex</c> / <c>ch</c> / <c>%</c> + the
///   <c>larger</c> / <c>smaller</c> relative-size keywords (CSS Fonts 4 §3.4), against the parent's
///   resolved font-size px (default <see cref="FontSizeResolver.MediumPx"/> when none).</item>
///   <item><c>font-weight</c> — <c>bolder</c> / <c>lighter</c> (CSS Fonts 4 §2.2.1), against the
///   parent's computed weight (default <see cref="FontWeightResolver.Normal"/>).</item>
/// </list>
/// Shared by the box-builder's top-down walk (<c>BoxBuilder</c>) and the <c>@page</c> margin-box
/// style builder (<c>MarginBoxStyle</c>) so both resolve these forms identically.
/// </summary>
internal static class DeferredFontResolver
{
    /// <summary>Resolve any deferred parent-relative <c>font-size</c> / <c>font-weight</c> on
    /// <paramref name="style"/> against <paramref name="parentStyle"/>, writing the resolved typed
    /// slot in place. A <see langword="null"/> parent (top of the chain) resolves against the
    /// initial 16px / 400. A form that isn't parent-relative (e.g. <c>rem</c> / viewport units) is
    /// left deferred for a later stage.</summary>
    public static void ResolveAgainstParent(ComputedStyle style, ComputedStyle? parentStyle)
    {
        if (style.TryGetDeferred(PropertyId.FontSize, out var rawSize) && rawSize is not null)
        {
            var parentFontSizePx = FontSizeResolver.MediumPx;
            if (parentStyle is not null)
            {
                var parentSlot = parentStyle.Get(PropertyId.FontSize);
                if (parentSlot.Tag == ComputedSlotTag.LengthPx) parentFontSizePx = parentSlot.AsLengthPx();
            }
            if (FontSizeResolver.TryResolveRelativeToParent(rawSize, parentFontSizePx, out var px)
                && double.IsFinite(px) && px >= 0)
            {
                style.Set(PropertyId.FontSize, ComputedSlot.FromLengthPx(px));
            }
        }

        if (style.TryGetDeferred(PropertyId.FontWeight, out var rawWeight) && rawWeight is not null)
        {
            var parentWeight = parentStyle?.ReadFontWeight() ?? FontWeightResolver.Normal;
            if (FontWeightResolver.TryResolveRelativeToParent(rawWeight, parentWeight, out var weight))
                style.Set(PropertyId.FontWeight, ComputedSlot.FromInteger(weight));
        }
    }
}
