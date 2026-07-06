// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Properties;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Phase 3 Task 21 — expander for the <c>border</c> + per-side <c>border-top</c> / <c>-right</c> /
/// <c>-bottom</c> / <c>-left</c> shorthands (CSS Backgrounds &amp; Borders L3 §4) into the
/// <c>border-*-width</c> / <c>-style</c> / <c>-color</c> longhands NetPdf consumes for page margin
/// boxes.
/// </summary>
/// <remarks>
/// <para>
/// Only used for <c>@page</c> margin-box bodies (<c>CssParserAdapter.ParseRawDeclarations</c>),
/// which AngleSharp.Css never sees — regular style rules get the shorthand expanded by AngleSharp.
/// Each shorthand is <c>&lt;line-width&gt; || &lt;line-style&gt; || &lt;color&gt;</c> (any order,
/// each at most once); omitted components reset to their initial value (<c>medium</c> / <c>none</c> /
/// <c>currentcolor</c>) per §4.3. CSS comments are stripped first (they're whitespace), and the value
/// is tokenized PAREN-AWARE so a functional color (<c>rgb(255, 0, 0)</c>) stays one token. A
/// whole-value CSS-wide keyword (<c>inherit</c> / <c>initial</c> / <c>unset</c> / <c>revert</c> /
/// <c>revert-layer</c>) maps to every longhand (resolved at the cascade level by <c>MarginBoxStyle</c>).
/// </para>
/// <para>
/// <b>Atomic (mirrors <see cref="FontShorthandExpander"/>).</b> Every generated longhand is
/// validated through the production <see cref="PropertyResolverDispatch"/> resolvers, so any invalid
/// part rejects the WHOLE shorthand (the caller keeps the raw declaration as a marker for
/// <c>MarginBoxStyle</c> to diagnose) — no partial border survives.
/// </para>
/// <para>
/// <b>Scope:</b> this expander handles only <c>border</c> / <c>border-&lt;side&gt;</c>. The
/// <c>border-width</c> / <c>border-style</c> / <c>border-color</c> 1–4-value box shorthands are handled
/// by <see cref="BorderBoxShorthandExpander"/>, and the content-origin inset (the margin-box text
/// pushed in by the border width) is applied in <c>PageMarginBoxPainter</c>. <c>border-radius</c> is a
/// tracked follow-up (deferrals.md).
/// </para>
/// </remarks>
internal static class BorderShorthandExpander
{
    private static readonly FrozenSet<string> LineStyles = new[]
    {
        "none", "hidden", "dotted", "dashed", "solid", "double", "groove", "ridge", "inset", "outset",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> LineWidthKeywords = new[]
    {
        "thin", "medium", "thick",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // propertyName → the (width, style, color) longhand prefixes the shorthand resets.
    private static readonly FrozenDictionary<string, string[]> EdgePrefixes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["border"] = new[] { "border-top", "border-right", "border-bottom", "border-left" },
        ["border-top"] = new[] { "border-top" },
        ["border-right"] = new[] { "border-right" },
        ["border-bottom"] = new[] { "border-bottom" },
        ["border-left"] = new[] { "border-left" },
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="propertyName"/> is a border shorthand this expander handles
    /// (<c>border</c> / <c>border-top</c> / <c>-right</c> / <c>-bottom</c> / <c>-left</c>).</summary>
    public static bool IsBorderShorthand(string propertyName) => EdgePrefixes.ContainsKey(propertyName);

    /// <summary>Expand a <c>border</c> / <c>border-&lt;side&gt;</c> shorthand into its
    /// <c>border-*-width</c> / <c>-style</c> / <c>-color</c> longhands. Returns <see langword="false"/>
    /// (caller keeps the raw declaration) for a malformed value, a duplicated component, or any part
    /// that fails resolver validation.</summary>
    public static bool TryExpand(string propertyName, string rawValue, out List<(string Property, string Value)> longhands)
        => TryExpand(propertyName, rawValue, deferValidation: false, out longhands);

    /// <summary>As <see cref="TryExpand(string, string, out List{ValueTuple{string, string}})"/>, but
    /// with <paramref name="deferValidation"/> the caller can skip the atomic leaf-validation loop. Used
    /// for a shorthand whose value contains <c>var()</c>: the reference can't be validated before
    /// substitution (that happens later, at the cascade's <c>VarResolver</c>), and a raw <c>var(--x)</c>
    /// color would otherwise be rejected — so the expander still splits the components and the recovered
    /// longhands (e.g. <c>border-bottom-color: var(--accent)</c>) flow through the normal var pipeline,
    /// which resolves them exactly as an authored longhand does.</summary>
    public static bool TryExpand(string propertyName, string rawValue, bool deferValidation, out List<(string Property, string Value)> longhands)
    {
        longhands = new List<(string, string)>(12);
        if (!EdgePrefixes.TryGetValue(propertyName, out var prefixes)) return false;
        if (string.IsNullOrWhiteSpace(rawValue)) return false;

        // CSS comments are whitespace (CSS Syntax 3 §4.3.2) — strip them before tokenizing so
        // `1px /* c */ solid red` doesn't reject atomically (quote-aware via the shared helper, the
        // same pre-normalization the other shorthand expanders apply).
        var stripped = CssShorthandHelpers.StripBlockComments(rawValue);

        // A whole-value CSS-wide keyword (`inherit` / `initial` / `unset` / `revert` / `revert-layer`)
        // applies to every generated longhand — mirrors FontShorthandExpander. MarginBoxStyle resolves
        // each at the cascade level (the border-* longhands are non-inherited, so `inherit` copies the
        // parent and `initial`/`unset` reset to `none`), so the leaf-validation loop is bypassed (these
        // keywords aren't valid leaf values). A keyword mixed with other tokens (`1px inherit`) is NOT
        // CSS-wide and falls through to tokenization, where it fails validation atomically.
        var trimmed = stripped.Trim();
        if (CssWideKeyword.Is(trimmed))
        {
            foreach (var prefix in prefixes)
            {
                longhands.Add(($"{prefix}-width", trimmed));
                longhands.Add(($"{prefix}-style", trimmed));
                longhands.Add(($"{prefix}-color", trimmed));
            }
            return true;
        }

        if (!CssShorthandHelpers.SplitTopLevel(stripped, out var tokens) || tokens.Count == 0) return false;

        // `<line-width> || <line-style> || <color>` — each component at most once, any order.
        string? width = null, style = null, color = null;
        foreach (var token in tokens)
        {
            switch (Classify(token))
            {
                case Component.Style: if (style is not null) return false; style = token; break;
                case Component.Width: if (width is not null) return false; width = token; break;
                default: if (color is not null) return false; color = token; break; // Color (the remainder)
            }
        }

        // Omitted components reset to their initial value (§4.3).
        width ??= "medium";
        style ??= "none";
        color ??= "currentcolor";

        foreach (var prefix in prefixes)
        {
            longhands.Add(($"{prefix}-width", width));
            longhands.Add(($"{prefix}-style", style));
            longhands.Add(($"{prefix}-color", color));
        }

        // Atomic validation: every emitted longhand must resolve (or none applies). Skipped when the
        // caller defers validation (a var()-carrying value — validated post-substitution by VarResolver).
        if (!deferValidation)
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

    private enum Component { Width, Style, Color }

    /// <summary>Classify a <c>border</c>-shorthand token: a <c>&lt;line-style&gt;</c> keyword, a
    /// <c>&lt;line-width&gt;</c> (<c>thin</c>/<c>medium</c>/<c>thick</c> or a length — starts with a
    /// digit / sign / dot), otherwise a <c>&lt;color&gt;</c> (the remainder).</summary>
    private static Component Classify(string token)
    {
        if (LineStyles.Contains(token)) return Component.Style;
        if (LineWidthKeywords.Contains(token)) return Component.Width;
        var c = token[0];
        if (char.IsAsciiDigit(c) || c is '.' or '+' or '-') return Component.Width;
        return Component.Color;
    }
}
