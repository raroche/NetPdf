// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Frozen;
using System.Collections.Generic;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Predicate set for properties whose CSS spec disallows negative values. Used by
/// <see cref="LengthResolver"/> + <see cref="NumberResolver"/> to gate negative
/// inputs and emit <c>CSS-PROPERTY-VALUE-INVALID-001</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Spec sources, by family:</b>
/// </para>
/// <list type="bullet">
///   <item><b>padding-*</b> (CSS Box Model 3 §7.1) — non-negative.</item>
///   <item><b>width / height / min-width / min-height</b> (CSS Sizing 3 §5) —
///     non-negative.</item>
///   <item><b>max-width / max-height</b> (CSS Sizing 3 §5.2) — non-negative.</item>
///   <item><b>flex-grow / flex-shrink</b> (CSS Flexbox 1 §7.1) — non-negative.</item>
/// </list>
/// <para>
/// <b>Allowed-negative properties (NOT in the set):</b> margins (CSS Box Model 3 §6
/// permits negative margins for layout effects), top/right/bottom/left (CSS
/// Positioned Layout 3 §3 — offsets can pull elements either direction),
/// letter-spacing/word-spacing (CSS Text 3 §10.1 — negative spacing tightens text).
/// </para>
/// <para>
/// <b>Cycle-2 additions:</b> <c>border-*-width</c> (LineWidth type — non-negative
/// per CSS Backgrounds 3 §3.1), <c>font-size</c> (FontSize type — non-negative per
/// CSS Fonts 4 §3.4), <c>line-height</c>'s number form (CSS Inline 3 §2.4.4 —
/// non-negative). These property types aren't in the cycle-1 resolver surface yet.
/// </para>
/// </remarks>
internal static class NonNegativeProperties
{
    /// <summary><see langword="true"/> when negative values are invalid for the
    /// given property per its CSS spec.</summary>
    public static bool IsRequired(PropertyId id) => Set.Contains(id);

    private static readonly FrozenSet<PropertyId> Set = new HashSet<PropertyId>
    {
        // padding-* — Box Model 3 §7.1
        PropertyId.PaddingTop, PropertyId.PaddingRight,
        PropertyId.PaddingBottom, PropertyId.PaddingLeft,
        // width / height + min/max — Sizing 3 §5 + §5.2
        PropertyId.Width, PropertyId.Height,
        PropertyId.MinWidth, PropertyId.MinHeight,
        PropertyId.MaxWidth, PropertyId.MaxHeight,
        // flex-grow / flex-shrink — Flexbox 1 §7.1
        PropertyId.FlexGrow, PropertyId.FlexShrink,
        // Per Phase 3 Task 14 cycle 1 hardening (Finding 3) — multicol
        // length properties (CSS Multi-column L1 §3.1 + §6.1).
        // column-width specifies the IDEAL inline-size of each column;
        // column-gap specifies the inline-axis gutter between columns.
        // The spec admits no negative values; a negative value falls
        // back to the property's initial value (auto / normal) + emits
        // CSS-PROPERTY-VALUE-INVALID-001.
        PropertyId.ColumnWidth, PropertyId.ColumnGap,
    }.ToFrozenSet();
}
