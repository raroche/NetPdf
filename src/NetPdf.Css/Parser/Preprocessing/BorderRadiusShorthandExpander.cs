// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Properties;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Expander for the <c>border-radius</c> shorthand (CSS Backgrounds &amp; Borders L3 §6.1) into its
/// per-corner longhands, for inputs AngleSharp.Css never sees (<c>@page</c> margin-box bodies) or drops
/// (the body elliptical slash form). The circular 1–4-value form expands onto the four corner longhands
/// (<c>border-{top-left,top-right,bottom-right,bottom-left}-radius</c>); the elliptical
/// <c>&lt;horizontal&gt; / &lt;vertical&gt;</c> form (border-radius-elliptical cycle) ALSO expands the
/// vertical radii onto the INTERNAL <c>-netpdf-border-{corner}-radius-y</c> longhands, which the painter
/// (<c>FragmentPainter.ReadCornerRadii</c>) reads as each corner's vertical radius (a circular corner
/// has no <c>-y</c> longhand, so the painter falls its vertical back to the horizontal).
/// </summary>
/// <remarks>
/// The 1–4-value distribution is IDENTICAL in position to the edge box shorthands
/// (<see cref="BorderBoxShorthandExpander"/>): 1 = all four; 2 = TL/BR then TR/BL; 3 = TL, TR/BL, BR;
/// 4 = TL TR BR BL — via the shared <see cref="CssShorthandHelpers.ExpandBoxEdges"/>. A TOP-LEVEL
/// <c>/</c> separates the horizontal + vertical radii; a <c>/</c> inside a function (e.g.
/// <c>calc(10px / 2)</c>) is a division and stays one token. CSS comments are stripped and the value
/// tokenized paren-aware; a whole-value CSS-wide keyword maps to every corner. ATOMIC: any part that
/// fails resolver validation rejects the whole shorthand (the raw declaration is kept + unrounds).
/// </remarks>
internal static class BorderRadiusShorthandExpander
{
    private static readonly string[] Corners =
        { "top-left", "top-right", "bottom-right", "bottom-left" };

    /// <summary>Whether <paramref name="propertyName"/> is the <c>border-radius</c> shorthand.</summary>
    public static bool IsBorderRadiusShorthand(string propertyName) =>
        propertyName.Equals("border-radius", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="rawValue"/> is the elliptical <c>Rx / Ry</c> form (a well-formed
    /// TOP-LEVEL <c>/</c> with both sides present), which AngleSharp.Css drops — so the body preprocessor
    /// recovers it for <see cref="TryExpand"/>. Comment-stripped + paren-aware (a <c>/</c> inside
    /// <c>calc()</c> is a division, not the separator), matching <see cref="TryExpand"/>.</summary>
    public static bool HasTopLevelSlash(string rawValue) =>
        !string.IsNullOrWhiteSpace(rawValue)
        && TrySplitTopLevelSlash(CssShorthandHelpers.StripBlockComments(rawValue), out _, out _);

    /// <summary>Whether <paramref name="propertyName"/> is one of the four <c>border-{corner}-radius</c>
    /// corner LONGHANDS (which also accept a 1- or 2-value <c>h [v]</c> and so must co-write the internal
    /// `-y` slot — post-PR-#186 review P1).</summary>
    public static bool IsCornerRadiusLonghand(string propertyName) =>
        propertyName is "border-top-left-radius" or "border-top-right-radius"
            or "border-bottom-right-radius" or "border-bottom-left-radius";

    /// <summary>For a SINGLE-value (or CSS-wide) <c>border-{corner}-radius</c> corner longhand, the
    /// internal vertical (<c>-netpdf-…-radius-y</c>) longhand it should co-write so the corner's two axes
    /// stay in lockstep — chiefly so a 1-value circular corner longhand RESETS the `-y` a prior elliptical
    /// `Rx / Ry` set (post-PR-#186 review P1; `-y` = the same value is byte-identical to the painter's
    /// horizontal-vs-height fallback otherwise). The 2-value elliptical <c>h v</c> corner longhand stays
    /// unsupported (returns <see langword="false"/> — the same pre-cycle behavior), as does a malformed
    /// value.</summary>
    public static bool TryExpandCornerVertical(string propertyName, string rawValue, out string verticalProperty, out string verticalValue)
    {
        verticalProperty = $"-netpdf-{propertyName}-y";
        verticalValue = string.Empty;
        if (!IsCornerRadiusLonghand(propertyName) || string.IsNullOrWhiteSpace(rawValue)) return false;
        var trimmed = CssShorthandHelpers.StripBlockComments(rawValue).Trim();
        if (CssWideKeyword.Is(trimmed)) { verticalValue = trimmed; return true; }
        if (!CssShorthandHelpers.SplitTopLevel(trimmed, out var values) || values.Count != 1)
            return false;
        verticalValue = values[0];   // 1-value → circular: `-y` == the horizontal.
        return IsValidLonghand(verticalProperty, verticalValue);
    }

    /// <summary>Expand <c>border-radius</c> into its corner longhands — the four
    /// <c>border-{corner}-radius</c> horizontal longhands plus, for the elliptical <c>Rx / Ry</c> slash
    /// form, the four internal <c>-netpdf-border-{corner}-radius-y</c> vertical longhands. Returns
    /// <see langword="false"/> (the caller keeps the raw declaration, which simply unrounds) for a
    /// malformed value, the wrong value count, more than one top-level slash, an empty slash side, or
    /// any part that fails resolver validation.</summary>
    public static bool TryExpand(string propertyName, string rawValue, out List<(string Property, string Value)> longhands)
    {
        longhands = new List<(string, string)>(8);
        if (!IsBorderRadiusShorthand(propertyName)) return false;
        if (string.IsNullOrWhiteSpace(rawValue)) return false;

        var stripped = CssShorthandHelpers.StripBlockComments(rawValue);
        var trimmed = stripped.Trim();

        // A `border-radius` declaration ALWAYS sets BOTH axes of every corner (CSS B&B §6.1 — each
        // `border-{corner}-radius` is one property holding an h + v pair), so it ALWAYS emits both the
        // horizontal corner longhands AND the internal `-netpdf-border-{corner}-radius-y` vertical
        // longhands. Emitting `-y` even for the circular / CSS-wide / no-slash forms keeps the vertical
        // radii in LOCKSTEP with the horizontal: a later circular `border-radius` RESETS the `-y` slots a
        // prior elliptical `Rx / Ry` set, so `border-radius: 10px / 30px; border-radius: 5px` renders
        // circular 5px, not 5px / 30px (post-PR-#186 review P1).

        // A whole-value CSS-wide keyword applies to every corner, both axes.
        if (CssWideKeyword.Is(trimmed))
        {
            foreach (var corner in Corners) longhands.Add(($"border-{corner}-radius", trimmed));
            foreach (var corner in Corners) longhands.Add(($"-netpdf-border-{corner}-radius-y", trimmed));
            return true;
        }

        // The elliptical `<horizontal> / <vertical>` form (border-radius-elliptical cycle): split at the
        // TOP-LEVEL `/` (a `/` inside `calc(...)` is a division, NOT the separator) → horizontal radii
        // onto the corner longhands, vertical radii onto the `-y` longhands.
        if (TrySplitTopLevelSlash(stripped, out var horizontal, out var vertical))
        {
            if (!ExpandFourCorners(horizontal, vertical: false, longhands)
                || !ExpandFourCorners(vertical, vertical: true, longhands))
            {
                longhands.Clear();
                return false;
            }
            return ValidateAll(longhands);
        }

        // A circular value (no top-level `/`): the vertical radii equal the horizontal (X == Y), emitted
        // onto the `-y` longhands too so they stay in lockstep / reset a prior elliptical.
        if (!ExpandFourCorners(stripped, vertical: false, longhands)
            || !ExpandFourCorners(stripped, vertical: true, longhands))
        {
            longhands.Clear();
            return false;
        }
        return ValidateAll(longhands);
    }

    /// <summary>Expand a 1–4-value radius list onto the four corners — the HORIZONTAL longhands
    /// (<c>border-{corner}-radius</c>) or, when <paramref name="vertical"/>, the internal
    /// <c>-netpdf-border-{corner}-radius-y</c> longhands.</summary>
    private static bool ExpandFourCorners(string part, bool vertical, List<(string Property, string Value)> longhands)
    {
        if (!CssShorthandHelpers.SplitTopLevel(part, out var values) || values.Count is < 1 or > 4)
            return false;
        var (tl, tr, br, bl) = CssShorthandHelpers.ExpandBoxEdges(values);
        longhands.Add((Name("top-left", vertical), tl));
        longhands.Add((Name("top-right", vertical), tr));
        longhands.Add((Name("bottom-right", vertical), br));
        longhands.Add((Name("bottom-left", vertical), bl));
        return true;
    }

    private static string Name(string corner, bool vertical) =>
        vertical ? $"-netpdf-border-{corner}-radius-y" : $"border-{corner}-radius";

    private static bool ValidateAll(List<(string Property, string Value)> longhands)
    {
        foreach (var (property, value) in longhands)
            if (!IsValidLonghand(property, value))
            {
                longhands.Clear();
                return false;
            }
        return true;
    }

    private static bool IsValidLonghand(string property, string value) =>
        PropertyMetadata.NameToId.TryGetValue(property, out var id)
        && !PropertyResolverDispatch.Resolve(id, value).IsInvalid;

    /// <summary>Split <paramref name="value"/> at its single TOP-LEVEL <c>/</c> (the elliptical
    /// separator) into the horizontal + vertical parts, ignoring any <c>/</c> nested inside a function
    /// such as <c>calc(10px / 2)</c> (a division). Returns <see langword="false"/> when there is no
    /// top-level slash, MORE than one, or either side is empty.</summary>
    private static bool TrySplitTopLevelSlash(string value, out string horizontal, out string vertical)
    {
        horizontal = string.Empty;
        vertical = string.Empty;
        var depth = 0;
        var slashIndex = -1;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '(') depth++;
            else if (c == ')') { if (depth > 0) depth--; }
            else if (c == '/' && depth == 0)
            {
                if (slashIndex >= 0) return false; // two top-level slashes → malformed.
                slashIndex = i;
            }
        }
        if (slashIndex < 0) return false;
        horizontal = value.Substring(0, slashIndex).Trim();
        vertical = value.Substring(slashIndex + 1).Trim();
        return horizontal.Length > 0 && vertical.Length > 0;
    }
}
