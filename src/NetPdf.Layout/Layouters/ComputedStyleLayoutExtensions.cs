// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;

namespace NetPdf.Layout.Layouters;

/// <summary>
/// Per Phase 3 Task 7 — extension methods on <see cref="ComputedStyle"/>
/// that decode the typed <see cref="ComputedSlot"/> payloads layouters
/// need (lengths, length-or-auto, percentages). Cycle 1 covers
/// resolved-px lengths only; cycle 3 will add percentage resolution +
/// <c>auto</c> handling per CSS 2.1 §10.3.
///
/// <para><b>Why extension methods.</b> Box layout reads ~10 properties
/// per box (margins, padding, borders, dimensions). Inlining the
/// <c>style.Get(id)</c> + <c>slot.Tag</c> + <c>slot.AsLengthPx()</c>
/// dance at every call site bloats the layouter; extracting it here
/// keeps the layouter focused on geometry math.</para>
///
/// <para><b>Cycle 1 limitations.</b> The methods return 0 for
/// non-LengthPx slot kinds (Auto, Percentage, Unset) — this is the
/// "TODO cycle 3" path. Real layout needs to resolve percentages
/// against the containing-block size + resolve <c>auto</c> per
/// CSS 2.1 §10.3.3. For cycle 1 the implicit model is "all author
/// values are explicit lengths in px" — sufficient to demonstrate
/// the layouter wiring without the percentage-resolution complexity.</para>
/// </summary>
internal static class ComputedStyleLayoutExtensions
{
    /// <summary>Read a length-typed property as CSS px. Returns
    /// <paramref name="defaultPx"/> when the slot is Unset, Auto,
    /// or any non-LengthPx tag (cycle 1 — see class XML doc for
    /// the "TODO cycle 3" handling of Auto + Percentage).</summary>
    public static double ReadLengthPxOrDefault(
        this ComputedStyle style,
        PropertyId id,
        double defaultPx)
    {
        var slot = style.Get(id);
        return slot.Tag == ComputedSlotTag.LengthPx
            ? slot.AsLengthPx()
            : defaultPx;
    }

    /// <summary>Read a length-typed property as CSS px, defaulting to
    /// 0 when the slot is Unset / Auto / non-length. Most box-model
    /// margin/padding/border properties default to 0 per the CSS
    /// initial-value table; this is the convenience overload for that
    /// case.</summary>
    public static double ReadLengthPxOrZero(this ComputedStyle style, PropertyId id) =>
        style.ReadLengthPxOrDefault(id, defaultPx: 0);
}
