// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Properties;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Phase 3 Task 21 — expander for the three <c>border-width</c> / <c>border-style</c> /
/// <c>border-color</c> 1–4-value box shorthands (CSS Backgrounds &amp; Borders L3 §4) into their per-edge
/// <c>border-{top,right,bottom,left}-{width,style,color}</c> longhands, for <c>@page</c> margin-box bodies.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="BorderShorthandExpander"/> (which handles <c>border</c> / <c>border-&lt;side&gt;</c>,
/// each a <c>&lt;line-width&gt; || &lt;line-style&gt; || &lt;color&gt;</c>): these set ONE property
/// (width / style / color) across all four edges from a 1–4-value box list (1 = all; 2 = vertical
/// horizontal; 3 = top horizontal bottom; 4 = top right bottom left). Only used for margin-box bodies,
/// which AngleSharp.Css never sees. CSS comments are stripped first and the value is tokenized
/// PAREN-AWARE so a functional <c>border-color</c> (<c>rgb(0, 0, 0)</c>) stays one token. A whole-value
/// CSS-wide keyword (<c>inherit</c> / <c>initial</c> / <c>unset</c> / <c>revert</c> / <c>revert-layer</c>)
/// maps to every longhand (resolved at the cascade level by <c>MarginBoxStyle</c>).
/// </para>
/// <para>
/// <b>Atomic.</b> Every generated longhand is validated through the production
/// <see cref="PropertyResolverDispatch"/> resolvers, so any invalid part rejects the WHOLE shorthand —
/// the caller keeps the raw declaration as a marker for <c>MarginBoxStyle</c> to diagnose.
/// </para>
/// </remarks>
internal static class BorderBoxShorthandExpander
{
    private static readonly string[] Edges = { "top", "right", "bottom", "left" };

    // shorthand name → the per-edge longhand suffix it distributes (`border-width` → `width`, …).
    private static readonly FrozenDictionary<string, string> Suffixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["border-width"] = "width",
        ["border-style"] = "style",
        ["border-color"] = "color",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="propertyName"/> is one of the three border box shorthands this
    /// expander handles (<c>border-width</c> / <c>border-style</c> / <c>border-color</c>).</summary>
    public static bool IsBorderBoxShorthand(string propertyName) => Suffixes.ContainsKey(propertyName);

    /// <summary>Expand a <c>border-width</c> / <c>border-style</c> / <c>border-color</c> box shorthand
    /// into its four per-edge longhands. Returns <see langword="false"/> (caller keeps the raw
    /// declaration) for a malformed value, the wrong value count, or any part that fails resolver
    /// validation.</summary>
    public static bool TryExpand(string propertyName, string rawValue, out List<(string Property, string Value)> longhands)
    {
        longhands = new List<(string, string)>(4);
        if (!Suffixes.TryGetValue(propertyName, out var suffix)) return false;
        if (string.IsNullOrWhiteSpace(rawValue)) return false;

        // CSS comments are whitespace — strip them quote-aware via the shared helper.
        var stripped = CssShorthandHelpers.StripBlockComments(rawValue);

        // A whole-value CSS-wide keyword applies to every longhand (resolved at the cascade level by
        // MarginBoxStyle), so the leaf-validation loop is bypassed.
        var trimmed = stripped.Trim();
        if (CssWideKeyword.Is(trimmed))
        {
            foreach (var edge in Edges) longhands.Add(($"border-{edge}-{suffix}", trimmed));
            return true;
        }

        if (!CssShorthandHelpers.SplitTopLevel(stripped, out var values) || values.Count is < 1 or > 4)
            return false;

        var (top, right, bottom, left) = CssShorthandHelpers.ExpandBoxEdges(values);
        longhands.Add(($"border-top-{suffix}", top));
        longhands.Add(($"border-right-{suffix}", right));
        longhands.Add(($"border-bottom-{suffix}", bottom));
        longhands.Add(($"border-left-{suffix}", left));

        // Atomic validation: every emitted longhand must resolve (or none applies).
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
