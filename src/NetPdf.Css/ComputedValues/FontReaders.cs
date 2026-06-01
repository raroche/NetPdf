// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues;

/// <summary>
/// Readers that decode the resolved <c>font-family</c> + <c>font-weight</c> slots
/// for downstream consumers (the text shaper). Mirrors <c>GridReaders</c> — a slot
/// of the expected tag is decoded, anything else maps to the property's initial
/// value.
/// </summary>
internal static class FontReaders
{
    /// <summary>Read the resolved <c>font-family</c> list from the side table;
    /// falls back to the initial single-<c>serif</c> list.</summary>
    public static FontFamilyList ReadFontFamily(this ComputedStyle style)
    {
        var slot = style.Get(PropertyId.FontFamily);
        if (slot.Tag == ComputedSlotTag.SideTableIndex
            && style.TryGetSideTablePayload<FontFamilyList>(PropertyId.FontFamily, out var list))
        {
            return list;
        }
        return FontFamilyList.Default;
    }

    /// <summary>Read the resolved <c>font-weight</c> (1–1000); falls back to the
    /// initial <c>normal</c> = 400 for an unset / non-integer slot.</summary>
    public static int ReadFontWeight(this ComputedStyle style)
    {
        var slot = style.Get(PropertyId.FontWeight);
        return slot.Tag == ComputedSlotTag.Integer ? slot.AsInteger() : 400;
    }
}
