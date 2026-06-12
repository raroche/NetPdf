// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;

namespace NetPdf.Rendering;

/// <summary>
/// Pre-layout used-size pass for replaced (<c>&lt;img&gt;</c>) boxes (img-pipeline cycle) — the
/// CSS 2.2 §10.3.2 / §10.6.2 first cut, written IN PLACE into the style's <c>width</c> /
/// <c>height</c> slots (the established in-place used-value pattern — DeferredLengthResolver,
/// ResolveUsedPercentPaddingInPlace) so the existing layout machinery sizes the box like any
/// explicit-size block:
/// <list type="number">
///   <item>A DECLARED CSS <c>width</c>/<c>height</c> (length or percentage slot) always wins —
///   the slot is left alone.</item>
///   <item>Else the HTML <c>width</c>/<c>height</c> ATTRIBUTE (a bare CSS-pixel integer per the
///   HTML dimension rules; percentage attributes are ignored).</item>
///   <item>Else the image's INTRINSIC size (1 image px = 1 CSS px, the first-cut density).</item>
///   <item>A side still missing after 1–3 completes from the OTHER side × the intrinsic
///   aspect ratio (§10.3.2) when that other side resolved to an ABSOLUTE length (a percentage
///   needs the containing block — deferred, documented).</item>
/// </list>
/// A failed image (no cache entry — fetch/decode already diagnosed) keeps rules 1–2 only:
/// it lays out at its declared/attribute size and paints nothing.
/// </summary>
internal static class ReplacedSizeResolver
{
    public static void ResolveTreeInPlace(Box root, ImageResourceCache cache)
    {
        Visit(root, cache);
    }

    private static void Visit(Box box, ImageResourceCache cache)
    {
        if (box.Kind is BoxKind.BlockReplacedElement or BoxKind.InlineReplacedElement
            && box.SourceElement is { } element)
        {
            ImageResourceCache.Entry? entry = null;
            if (cache.ImageBoxes.TryGetValue(box, out var uri)) cache.TryGet(uri, out entry!);
            ResolveBox(box, element, entry);
        }
        foreach (var child in box.Children)
            Visit(child, cache);
    }

    private static void ResolveBox(Box box, AngleSharp.Dom.IElement element, ImageResourceCache.Entry? entry)
    {
        var style = box.Style;
        var widthDeclared = IsExplicit(style, PropertyId.Width);
        var heightDeclared = IsExplicit(style, PropertyId.Height);
        if (widthDeclared && heightDeclared) return;

        double? width = widthDeclared ? null : ReadDimensionAttributePx(element, "width");
        double? height = heightDeclared ? null : ReadDimensionAttributePx(element, "height");

        if (entry is not null && entry.WidthPx > 0 && entry.HeightPx > 0)
        {
            var ratio = entry.HeightPx / entry.WidthPx;
            // Complete a missing side from the other side × the intrinsic ratio. The "other
            // side" is the attr value, or a declared ABSOLUTE CSS length (a percentage's used
            // value needs the containing block — that side completes to the intrinsic instead).
            if (!widthDeclared && width is null)
            {
                var basisH = height ?? ReadAbsoluteLengthPx(style, PropertyId.Height, heightDeclared);
                width = basisH is { } h ? h / ratio : entry.WidthPx;
            }
            if (!heightDeclared && height is null)
            {
                var basisW = width ?? ReadAbsoluteLengthPx(style, PropertyId.Width, widthDeclared);
                height = basisW is { } w ? w * ratio : entry.HeightPx;
            }
        }

        if (!widthDeclared && width is { } usedW && usedW >= 0)
            style.Set(PropertyId.Width, ComputedSlot.FromLengthPx(usedW));
        if (!heightDeclared && height is { } usedH && usedH >= 0)
            style.Set(PropertyId.Height, ComputedSlot.FromLengthPx(usedH));
    }

    private static bool IsExplicit(ComputedStyle style, PropertyId id) =>
        style.Get(id).Tag is ComputedSlotTag.LengthPx or ComputedSlotTag.Percentage;

    private static double? ReadAbsoluteLengthPx(ComputedStyle style, PropertyId id, bool declared)
    {
        if (!declared) return null;
        var slot = style.Get(id);
        return slot.Tag == ComputedSlotTag.LengthPx ? style.ReadLengthPxOrZero(id) : null;
    }

    /// <summary>The HTML dimension attribute: a non-negative integer in CSS px. Legacy
    /// percentage forms (<c>width="50%"</c>) are ignored (a documented first-cut bound).</summary>
    private static double? ReadDimensionAttributePx(AngleSharp.Dom.IElement element, string name)
    {
        var raw = element.GetAttribute(name);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var px) && px >= 0
            ? px
            : null;
    }
}
