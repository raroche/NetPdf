// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Properties;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Margin-box-border-radius cycle — expander for the <c>border-radius</c> 1–4-value shorthand (CSS
/// Backgrounds &amp; Borders L3 §6.1) into its four corner longhands
/// (<c>border-{top-left,top-right,bottom-right,bottom-left}-radius</c>), for <c>@page</c> margin-box
/// bodies (which AngleSharp.Css never sees, so the shorthand would otherwise be dropped).
/// </summary>
/// <remarks>
/// <para>
/// The 1–4-value distribution is IDENTICAL in position to the edge box shorthands
/// (<see cref="BorderBoxShorthandExpander"/>): 1 = all four; 2 = TL/BR then TR/BL; 3 = TL, TR/BL, BR;
/// 4 = TL TR BR BL — so the shared <see cref="CssShorthandHelpers.ExpandBoxEdges"/> (which returns the
/// values in top/right/bottom/left order) maps directly onto the corners top-left ← top,
/// top-right ← right, bottom-right ← bottom, bottom-left ← left.
/// </para>
/// <para>
/// The elliptical <c>Rx / Ry</c> slash form is DEFERRED (a value with a top-level <c>/</c> returns
/// <see langword="false"/> — the raw declaration is then skipped at the cascade, leaving square corners,
/// the same fallback as the body, where AngleSharp drops the slash form). CSS comments are stripped and
/// the value tokenized paren-aware (so a <c>calc()</c> term stays one token); a whole-value CSS-wide
/// keyword maps to every longhand. ATOMIC: any part that fails resolver validation rejects the whole
/// shorthand (the raw declaration is kept and simply unrounds).
/// </para>
/// </remarks>
internal static class BorderRadiusShorthandExpander
{
    private static readonly string[] Corners =
        { "top-left", "top-right", "bottom-right", "bottom-left" };

    /// <summary>Whether <paramref name="propertyName"/> is the <c>border-radius</c> shorthand.</summary>
    public static bool IsBorderRadiusShorthand(string propertyName) =>
        propertyName.Equals("border-radius", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>Expand <c>border-radius</c> into its four corner longhands. Returns
    /// <see langword="false"/> (the caller keeps the raw declaration) for the elliptical slash form, a
    /// malformed value, the wrong value count, or any part that fails resolver validation.</summary>
    public static bool TryExpand(string propertyName, string rawValue, out List<(string Property, string Value)> longhands)
    {
        longhands = new List<(string, string)>(4);
        if (!IsBorderRadiusShorthand(propertyName)) return false;
        if (string.IsNullOrWhiteSpace(rawValue)) return false;

        var stripped = CssShorthandHelpers.StripBlockComments(rawValue);
        var trimmed = stripped.Trim();

        // The elliptical `<horizontal> / <vertical>` form needs per-corner (rx, ry) pairs the single-value
        // corner longhands can't store — defer it (square fallback, matching the body's AngleSharp drop).
        if (trimmed.Contains('/')) return false;

        // A whole-value CSS-wide keyword applies to every corner (resolved at the cascade by MarginBoxStyle).
        if (CssWideKeyword.Is(trimmed))
        {
            foreach (var corner in Corners) longhands.Add(($"border-{corner}-radius", trimmed));
            return true;
        }

        if (!CssShorthandHelpers.SplitTopLevel(stripped, out var values) || values.Count is < 1 or > 4)
            return false;

        // The corner distribution matches the edge box distribution positionally (TL←top, TR←right,
        // BR←bottom, BL←left).
        var (tl, tr, br, bl) = CssShorthandHelpers.ExpandBoxEdges(values);
        longhands.Add(("border-top-left-radius", tl));
        longhands.Add(("border-top-right-radius", tr));
        longhands.Add(("border-bottom-right-radius", br));
        longhands.Add(("border-bottom-left-radius", bl));

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
}
