// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Properties;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Phase 3 Task 21 — expander for the <c>padding</c> 1–4-value box shorthand (CSS Box Model §8.4)
/// into the <c>padding-top</c> / <c>-right</c> / <c>-bottom</c> / <c>-left</c> longhands NetPdf
/// consumes for page margin boxes.
/// </summary>
/// <remarks>
/// <para>
/// Only used for <c>@page</c> margin-box bodies (<c>CssParserAdapter.ParseRawDeclarations</c>), which
/// AngleSharp.Css never sees — regular style rules get the shorthand expanded by AngleSharp, and the
/// per-side <c>padding-&lt;side&gt;</c> longhands already pass through unchanged (only the <c>padding</c>
/// shorthand needs expansion here). The 1–4-value mapping follows the CSS box convention: 1 = all; 2 =
/// vertical horizontal; 3 = top horizontal bottom; 4 = top right bottom left. CSS comments are stripped
/// first (they're whitespace) and the value is tokenized PAREN-AWARE so a functional value
/// (<c>calc(1px + 2px)</c>) stays one token. A whole-value CSS-wide keyword
/// (<c>inherit</c> / <c>initial</c> / <c>unset</c> / <c>revert</c> / <c>revert-layer</c>) maps to every
/// longhand (resolved at the cascade level by <c>MarginBoxStyle</c>).
/// </para>
/// <para>
/// <b>Atomic (mirrors <see cref="BorderShorthandExpander"/>).</b> Every generated longhand is validated
/// through the production <see cref="PropertyResolverDispatch"/> resolvers, so any invalid part (a bad
/// unit, too many values) rejects the WHOLE shorthand — the caller keeps the raw declaration as a marker
/// for <c>MarginBoxStyle</c> to diagnose, no partial padding survives.
/// </para>
/// </remarks>
internal static class PaddingShorthandExpander
{
    private static readonly string[] Sides = { "padding-top", "padding-right", "padding-bottom", "padding-left" };

    /// <summary>Whether <paramref name="propertyName"/> is the <c>padding</c> box shorthand this
    /// expander handles (the per-side <c>padding-&lt;side&gt;</c> forms are longhands, not shorthands).</summary>
    public static bool IsPaddingShorthand(string propertyName) =>
        string.Equals(propertyName, "padding", StringComparison.OrdinalIgnoreCase);

    /// <summary>Expand a <c>padding</c> shorthand into its <c>padding-top</c> / <c>-right</c> /
    /// <c>-bottom</c> / <c>-left</c> longhands. Returns <see langword="false"/> (caller keeps the raw
    /// declaration) for a malformed value, the wrong value count, or any part that fails resolver
    /// validation.</summary>
    public static bool TryExpand(string propertyName, string rawValue, out List<(string Property, string Value)> longhands)
    {
        longhands = new List<(string, string)>(4);
        if (!IsPaddingShorthand(propertyName)) return false;
        if (string.IsNullOrWhiteSpace(rawValue)) return false;

        // CSS comments are whitespace (CSS Syntax 3 §4.3.2) — strip them before tokenizing, quote-aware
        // via the shared helper, like the other shorthand expanders.
        var stripped = CssShorthandHelpers.StripBlockComments(rawValue);

        // A whole-value CSS-wide keyword applies to every longhand (mirrors BorderShorthandExpander) —
        // MarginBoxStyle resolves each at the cascade level, so the leaf-validation loop is bypassed.
        var trimmed = stripped.Trim();
        if (CssWideKeyword.Is(trimmed))
        {
            foreach (var side in Sides) longhands.Add((side, trimmed));
            return true;
        }

        if (!CssShorthandHelpers.SplitTopLevel(stripped, out var values) || values.Count is < 1 or > 4)
            return false;

        // 1–4-value box mapping → (top, right, bottom, left).
        var (top, right, bottom, left) = values.Count switch
        {
            1 => (values[0], values[0], values[0], values[0]),
            2 => (values[0], values[1], values[0], values[1]),
            3 => (values[0], values[1], values[2], values[1]),
            _ => (values[0], values[1], values[2], values[3]),
        };
        longhands.Add(("padding-top", top));
        longhands.Add(("padding-right", right));
        longhands.Add(("padding-bottom", bottom));
        longhands.Add(("padding-left", left));

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
