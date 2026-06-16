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
        // Per Phase 3 Task 15 L8 post-PR-#68 hardening F#3 — flex-basis
        // is non-negative per CSS Flexbox L1 §7.2 (the `<'width'>`
        // reference subsumes Sizing §5's non-negative rule). Pre-fix,
        // `flex-basis: -10px` / `-10%` resolved successfully (because
        // FlexBasis joined the LengthResolver dispatch in L8 without
        // joining this set), then floored to 0 silently in the
        // hypothetical-size resolver. Now negatives invalidate cleanly
        // with CSS-PROPERTY-VALUE-INVALID-001 + the cascade falls back
        // to the property's initial value (`auto`).
        PropertyId.FlexBasis,
        // Per Phase 3 Task 14 cycle 1 hardening (Finding 3) — multicol
        // length properties (CSS Multi-column L1 §3.1 + §6.1).
        // column-width specifies the IDEAL inline-size of each column;
        // column-gap specifies the inline-axis gutter between columns.
        // The spec admits no negative values; a negative value falls
        // back to the property's initial value (auto / normal) + emits
        // CSS-PROPERTY-VALUE-INVALID-001.
        PropertyId.ColumnWidth, PropertyId.ColumnGap,
        // Per Phase 5 layout→PDF cycle 3 — <line-width> is non-negative (CSS
        // Backgrounds & Borders 3 §4.2). border-*-width + column-rule-width join
        // the resolver dispatch (LineWidthResolver) + this set together so a
        // negative width invalidates cleanly + falls back to the initial value.
        PropertyId.BorderTopWidth, PropertyId.BorderRightWidth,
        PropertyId.BorderBottomWidth, PropertyId.BorderLeftWidth,
        PropertyId.ColumnRuleWidth,
        // Per the outline cycle (PR #173 Copilot review) — outline-width is also a
        // <line-width> (CSS UI 4 §5.1), so a negative value invalidates + falls back
        // to the initial (medium) rather than resolving to a negative LengthPx.
        PropertyId.OutlineWidth,
        // Per Phase 5 layout→PDF cycle 4 — font-size is non-negative (CSS Fonts 4
        // §3.4). FontSizeResolver delegates absolute <length> forms to
        // LengthResolver, which consults this set to reject a negative size.
        PropertyId.FontSize,
        // Per the margin-box-border-radius cycle (PR #174 review P2) — a border-radius
        // is non-negative (CSS Backgrounds & Borders 3 §6.1: negative values are
        // invalid), so a negative corner radius invalidates cleanly + falls back to the
        // initial 0 (square) instead of resolving to a negative LengthPx the painter
        // would silently clamp. Applies to body + margin boxes (the longhands are shared).
        PropertyId.BorderTopLeftRadius, PropertyId.BorderTopRightRadius,
        PropertyId.BorderBottomRightRadius, PropertyId.BorderBottomLeftRadius,
        // The internal vertical-radius longhands (border-radius-elliptical cycle) — a negative vertical
        // radius is invalid (CSS B&B §6.1) exactly like the horizontal, so `10px / -5px` rejects the
        // whole shorthand instead of clamping the vertical to 0 (post-PR-#186 Copilot review).
        PropertyId.BorderTopLeftRadiusY, PropertyId.BorderTopRightRadiusY,
        PropertyId.BorderBottomRightRadiusY, PropertyId.BorderBottomLeftRadiusY,
    }.ToFrozenSet();
}
