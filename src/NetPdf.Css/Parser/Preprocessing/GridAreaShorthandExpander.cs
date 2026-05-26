// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Css.ComputedValues.PropertyResolvers;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Per Phase 3 Task 17 cycle 0c — expander for the <c>grid-area</c>
/// shorthand per CSS Grid L1 §8.4. Decomposes one declaration into
/// the four grid-line longhands (<c>grid-row-start</c> /
/// <c>grid-column-start</c> / <c>grid-row-end</c> /
/// <c>grid-column-end</c>).
///
/// <para><b>Grammar per §8.4:</b>
/// <code>
/// &lt;grid-area&gt; = &lt;grid-line&gt; [ / &lt;grid-line&gt; ]{0,3}
/// </code>
/// (= 1 to 4 values separated by <c>/</c>.)</para>
///
/// <para><b>Longhand mapping for omitted values per §8.4</b>:</para>
/// <list type="bullet">
///   <item>1 value: <c>A</c> → row-start: A; column-start: &lt;A&gt;;
///         row-end: &lt;A&gt;; column-end: &lt;A&gt;</item>
///   <item>2 values: <c>A / B</c> → row-start: A; column-start: B;
///         row-end: &lt;A&gt;; column-end: &lt;B&gt;</item>
///   <item>3 values: <c>A / B / C</c> → row-start: A; column-start: B;
///         row-end: C; column-end: &lt;B&gt;</item>
///   <item>4 values: <c>A / B / C / D</c> → row-start: A; column-start: B;
///         row-end: C; column-end: D</item>
/// </list>
///
/// <para>The omitted-pair shorthand <c>&lt;X&gt;</c> is: if X is a
/// <c>&lt;custom-ident&gt;</c>, the omitted longhand is set to X;
/// otherwise it's set to <c>auto</c>.</para>
///
/// <para><b>Named-area scope:</b> CSS Grid §8.4 also admits
/// <c>grid-area: my-area</c> referencing a <c>grid-template-areas</c>
/// named area, which expands differently (= all four longhands set to
/// the area name; layout-time resolution against the parent's named
/// areas). That path is cycle 7's scope; cycle 0c treats every
/// identifier as a <c>&lt;custom-ident&gt;</c> (= named line) per the
/// §8.4 fallback. The downstream cascade + GridLineResolver handle
/// bare named-line idents already.</para>
/// </summary>
internal static class GridAreaShorthandExpander
{
    /// <summary>Attempt to expand a <c>grid-area</c> shorthand value
    /// into its four longhands per §8.4.</summary>
    /// <param name="rawValue">The raw value text (already trimmed,
    /// <c>!important</c> already stripped).</param>
    /// <param name="rowStart">Emitted on success — the
    /// <c>grid-row-start</c> longhand value.</param>
    /// <param name="columnStart">Emitted on success — the
    /// <c>grid-column-start</c> longhand value.</param>
    /// <param name="rowEnd">Emitted on success — the
    /// <c>grid-row-end</c> longhand value.</param>
    /// <param name="columnEnd">Emitted on success — the
    /// <c>grid-column-end</c> longhand value.</param>
    /// <returns><see langword="true"/> when the value parses as a
    /// valid <c>grid-area</c> shorthand shape; <see langword="false"/>
    /// otherwise.</returns>
    public static bool TryExpand(
        string rawValue,
        out string rowStart,
        out string columnStart,
        out string rowEnd,
        out string columnEnd)
    {
        rowStart = string.Empty;
        columnStart = string.Empty;
        rowEnd = string.Empty;
        columnEnd = string.Empty;

        if (string.IsNullOrWhiteSpace(rawValue)) return false;

        var stripped = CssShorthandHelpers.StripBlockComments(rawValue);
        if (string.IsNullOrWhiteSpace(stripped)) return false;
        var trimmed = stripped.Trim();

        // CSS-wide keywords pass through verbatim to all four longhands.
        if (GridShorthandHelpers.IsCssWideKeyword(trimmed))
        {
            rowStart = trimmed;
            columnStart = trimmed;
            rowEnd = trimmed;
            columnEnd = trimmed;
            return true;
        }

        // Per PR-#91 review F2 — skip expansion when var() is present
        // (= same rationale as GridLineShorthandExpander). The shorthand
        // silently drops at the cascade. Post-substitution re-expansion
        // is a separate cycle's scope.
        if (ContainsCaseInsensitive(trimmed, "var("))
        {
            return false;
        }

        var parts = GridLineShorthandExpander.SplitOnSlash(trimmed, max: 4);
        if (parts is null) return false;
        if (parts.Length == 0 || parts.Length > 4) return false;

        // Each part must be a non-empty token after trim.
        var a = parts[0].Trim();
        if (a.Length == 0) return false;
        string b = string.Empty, c = string.Empty, d = string.Empty;
        if (parts.Length >= 2)
        {
            b = parts[1].Trim();
            if (b.Length == 0) return false;
        }
        if (parts.Length >= 3)
        {
            c = parts[2].Trim();
            if (c.Length == 0) return false;
        }
        if (parts.Length >= 4)
        {
            d = parts[3].Trim();
            if (d.Length == 0) return false;
        }

        string outRowStart, outColumnStart, outRowEnd, outColumnEnd;
        switch (parts.Length)
        {
            case 1:
                // A → row-start: A; column-start: <A>; row-end: <A>; column-end: <A>
                outRowStart = a;
                outColumnStart = OmittedPair(a);
                outRowEnd = OmittedPair(a);
                outColumnEnd = OmittedPair(a);
                break;
            case 2:
                // A / B → row-start: A; column-start: B; row-end: <A>; column-end: <B>
                outRowStart = a;
                outColumnStart = b;
                outRowEnd = OmittedPair(a);
                outColumnEnd = OmittedPair(b);
                break;
            case 3:
                // A / B / C → row-start: A; column-start: B; row-end: C; column-end: <B>
                outRowStart = a;
                outColumnStart = b;
                outRowEnd = c;
                outColumnEnd = OmittedPair(b);
                break;
            case 4:
                outRowStart = a;
                outColumnStart = b;
                outRowEnd = c;
                outColumnEnd = d;
                break;
            default:
                return false;
        }

        // Per PR-#91 review F1 — atomic validation. Per CSS Cascade L4
        // §4.2, an invalid shorthand contributes none of its longhands.
        // Any per-longhand validation failure drops the whole shorthand.
        if (!GridLineResolver.TryValidate(outRowStart)) return false;
        if (!GridLineResolver.TryValidate(outColumnStart)) return false;
        if (!GridLineResolver.TryValidate(outRowEnd)) return false;
        if (!GridLineResolver.TryValidate(outColumnEnd)) return false;

        rowStart = outRowStart;
        columnStart = outColumnStart;
        rowEnd = outRowEnd;
        columnEnd = outColumnEnd;
        return true;
    }

    /// <summary>Per §8.4 — if <paramref name="value"/> is a bare
    /// <c>&lt;custom-ident&gt;</c>, the omitted pair-longhand is set
    /// to the same ident; otherwise it's set to <c>auto</c>.</summary>
    private static string OmittedPair(string value)
        => GridShorthandHelpers.IsBareCustomIdent(value) ? value : "auto";

    private static bool ContainsCaseInsensitive(string haystack, string needle)
        => haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}
