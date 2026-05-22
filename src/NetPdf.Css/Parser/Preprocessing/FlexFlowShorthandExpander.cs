// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Per Phase 3 Task 15 L16 — expander for the <c>flex-flow</c> shorthand
/// property per CSS Flexbox L1 §6.1. Decomposes one <c>flex-flow: …</c>
/// declaration into the two longhand values <c>flex-direction</c> /
/// <c>flex-wrap</c>.
/// <para>
/// AngleSharp.Css 1.0.0-beta.144 — like with the <c>flex</c> shorthand
/// (handled by <see cref="FlexShorthandExpander"/> per L13) —
/// does not reliably round-trip the <c>flex-flow</c> shorthand into its
/// two longhand declarations for the cascade. The
/// <see cref="CssPreprocessor"/>'s recovery pass calls this expander to
/// emit the two synthesized longhand declarations the cascade then sees.
/// </para>
/// <para>
/// <b>Grammar per §6.1:</b> <c>&lt;flex-direction&gt; || &lt;flex-wrap&gt;</c>
/// — at least one of the two values, in any order. The expander
/// recognizes:
/// </para>
/// <list type="bullet">
///   <item><c>&lt;flex-direction&gt;</c> only — keyword from
///   <c>row</c> / <c>row-reverse</c> / <c>column</c> /
///   <c>column-reverse</c>; <c>flex-wrap</c> defaults to <c>nowrap</c>.</item>
///   <item><c>&lt;flex-wrap&gt;</c> only — keyword from
///   <c>nowrap</c> / <c>wrap</c> / <c>wrap-reverse</c>;
///   <c>flex-direction</c> defaults to <c>row</c>.</item>
///   <item><c>&lt;flex-direction&gt; &lt;flex-wrap&gt;</c> or
///   <c>&lt;flex-wrap&gt; &lt;flex-direction&gt;</c> — both, any order.</item>
/// </list>
/// <para>
/// CSS-wide keywords (<c>initial</c>, <c>inherit</c>, <c>unset</c>,
/// <c>revert</c>, <c>revert-layer</c>) pass through to both longhands
/// verbatim per CSS Cascade §7.
/// </para>
/// <para>
/// Invalid input (= 0 or &gt; 2 tokens, two direction tokens, two wrap
/// tokens, or unknown keywords) returns <see langword="false"/> and the
/// caller leaves the raw shorthand for AngleSharp.Css's partial
/// handling.
/// </para>
/// </summary>
internal static class FlexFlowShorthandExpander
{
    /// <summary>Attempt to expand a <c>flex-flow</c> shorthand value
    /// into its two longhands.</summary>
    /// <param name="rawValue">The raw value text (already trimmed,
    /// <c>!important</c> already stripped).</param>
    /// <param name="direction">Emitted on success — the
    /// <c>flex-direction</c> value (e.g., "row", "column-reverse").</param>
    /// <param name="wrap">Emitted on success — the <c>flex-wrap</c>
    /// value (e.g., "nowrap", "wrap-reverse").</param>
    /// <returns><see langword="true"/> when the value parses as a
    /// valid <c>flex-flow</c> shorthand per §6.1;
    /// <see langword="false"/> otherwise.</returns>
    public static bool TryExpand(
        string rawValue,
        out string direction,
        out string wrap)
    {
        direction = string.Empty;
        wrap = string.Empty;

        if (string.IsNullOrWhiteSpace(rawValue)) return false;

        var trimmed = rawValue.Trim();

        // CSS-wide keywords pass through to both longhands per CSS
        // Cascade §7.
        if (IsCssWideKeyword(trimmed))
        {
            direction = trimmed;
            wrap = trimmed;
            return true;
        }

        var tokens = trimmed.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || tokens.Length > 2) return false;

        if (tokens.Length == 1)
        {
            // Either a flex-direction value (= flex-wrap defaults to
            // nowrap) or a flex-wrap value (= flex-direction defaults
            // to row).
            if (IsFlexDirectionValue(tokens[0]))
            {
                direction = tokens[0];
                wrap = "nowrap";
                return true;
            }
            if (IsFlexWrapValue(tokens[0]))
            {
                direction = "row";
                wrap = tokens[0];
                return true;
            }
            return false;
        }

        // tokens.Length == 2 — one of each, in any order. Per §6.1
        // both orderings are valid (`row wrap` and `wrap row` mean
        // the same thing).
        var firstIsDirection = IsFlexDirectionValue(tokens[0]);
        var firstIsWrap = IsFlexWrapValue(tokens[0]);
        var secondIsDirection = IsFlexDirectionValue(tokens[1]);
        var secondIsWrap = IsFlexWrapValue(tokens[1]);

        // Each token must be exactly one of direction or wrap (= the
        // value sets are disjoint per §5.1 + §6.3 grammars, so no
        // ambiguity).
        if (firstIsDirection && secondIsWrap)
        {
            direction = tokens[0];
            wrap = tokens[1];
            return true;
        }
        if (firstIsWrap && secondIsDirection)
        {
            direction = tokens[1];
            wrap = tokens[0];
            return true;
        }
        return false;
    }

    /// <summary>Per CSS Cascade §7 — the wide keywords every property accepts.</summary>
    private static bool IsCssWideKeyword(string value) =>
        value.Equals("initial", StringComparison.OrdinalIgnoreCase)
        || value.Equals("inherit", StringComparison.OrdinalIgnoreCase)
        || value.Equals("unset", StringComparison.OrdinalIgnoreCase)
        || value.Equals("revert", StringComparison.OrdinalIgnoreCase)
        || value.Equals("revert-layer", StringComparison.OrdinalIgnoreCase);

    /// <summary>Per CSS Flexbox L1 §5.1 — the four valid
    /// <c>flex-direction</c> keywords.</summary>
    private static bool IsFlexDirectionValue(string token) =>
        token.Equals("row", StringComparison.OrdinalIgnoreCase)
        || token.Equals("row-reverse", StringComparison.OrdinalIgnoreCase)
        || token.Equals("column", StringComparison.OrdinalIgnoreCase)
        || token.Equals("column-reverse", StringComparison.OrdinalIgnoreCase);

    /// <summary>Per CSS Flexbox L1 §6.3 — the three valid
    /// <c>flex-wrap</c> keywords.</summary>
    private static bool IsFlexWrapValue(string token) =>
        token.Equals("nowrap", StringComparison.OrdinalIgnoreCase)
        || token.Equals("wrap", StringComparison.OrdinalIgnoreCase)
        || token.Equals("wrap-reverse", StringComparison.OrdinalIgnoreCase);
}
