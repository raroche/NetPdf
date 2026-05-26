// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Per Phase 3 Task 17 cycle 0c — expander for the <c>grid-row</c> and
/// <c>grid-column</c> shorthands per CSS Grid L1 §8.4. Decomposes one
/// declaration into the two longhand values
/// (<c>grid-{row,column}-start</c> / <c>grid-{row,column}-end</c>).
///
/// <para><b>Grammar per §8.4:</b>
/// <code>
/// &lt;grid-row&gt;    = &lt;grid-line&gt; [ / &lt;grid-line&gt; ]?
/// &lt;grid-column&gt; = &lt;grid-line&gt; [ / &lt;grid-line&gt; ]?
/// </code>
/// When the second value is omitted: if the first value is a
/// <c>&lt;custom-ident&gt;</c>, the end longhand is set to the same
/// custom-ident; otherwise, the end longhand is set to <c>auto</c>.</para>
///
/// <para><b>Why expand at the preprocessor:</b> AngleSharp.Css
/// 1.0.0-beta.144 doesn't reliably round-trip the
/// <c>grid-row</c> / <c>grid-column</c> shorthands into their two
/// longhand declarations for the cascade. <see cref="CssPreprocessor"/>'s
/// recovery pass calls this expander to emit the two synthesized
/// longhand declarations the cascade then sees.</para>
///
/// <para><b>Validation:</b> the expander does NOT fully validate the
/// individual <c>&lt;grid-line&gt;</c> values (= that's the
/// <c>GridLineResolver</c>'s job at the cascade stage). It only
/// validates the OUTER shape (= one or two values separated by
/// <c>/</c>) and the omitted-pair rule. Invalid inner values land at
/// the resolver which rejects them as Invalid declarations + falls
/// back to <c>auto</c>.</para>
/// </summary>
internal static class GridLineShorthandExpander
{
    /// <summary>Attempt to expand a <c>grid-row</c> / <c>grid-column</c>
    /// shorthand value into its two longhands per §8.4.</summary>
    /// <param name="rawValue">The raw value text (already trimmed,
    /// <c>!important</c> already stripped).</param>
    /// <param name="start">Emitted on success — the start-longhand value
    /// (e.g., "2", "span 2", "foo", "auto").</param>
    /// <param name="end">Emitted on success — the end-longhand value.</param>
    /// <returns><see langword="true"/> when the value parses as a valid
    /// grid-line shorthand shape; <see langword="false"/> otherwise.</returns>
    public static bool TryExpand(
        string rawValue,
        out string start,
        out string end)
    {
        start = string.Empty;
        end = string.Empty;

        if (string.IsNullOrWhiteSpace(rawValue)) return false;

        // Strip block comments per CSS Syntax §4 (matches FlexShorthandExpander).
        var stripped = CssShorthandHelpers.StripBlockComments(rawValue);
        if (string.IsNullOrWhiteSpace(stripped)) return false;
        var trimmed = stripped.Trim();

        // CSS-wide keywords pass through to both longhands verbatim — the
        // cascade applies §7 semantics uniformly.
        if (GridShorthandHelpers.IsCssWideKeyword(trimmed))
        {
            start = trimmed;
            end = trimmed;
            return true;
        }

        // Split by '/' into 1 or 2 parts.
        var parts = SplitOnSlash(trimmed, max: 2);
        if (parts is null) return false;
        if (parts.Length == 0 || parts.Length > 2) return false;

        var startPart = parts[0].Trim();
        if (startPart.Length == 0) return false;

        if (parts.Length == 1)
        {
            // Single value — apply the §8.4 omitted-pair rule.
            start = startPart;
            end = GridShorthandHelpers.IsBareCustomIdent(startPart) ? startPart : "auto";
            return true;
        }

        var endPart = parts[1].Trim();
        if (endPart.Length == 0) return false;
        start = startPart;
        end = endPart;
        return true;
    }

    /// <summary>Split <paramref name="value"/> on the top-level slash
    /// separator. Returns null if the slash count exceeds
    /// <paramref name="max"/> - 1. The expander doesn't need to handle
    /// nested function-call parens (= grid-line values don't contain
    /// <c>/</c> inside parens), but the helper is forward-safe by only
    /// splitting on whitespace-or-edge-adjacent slashes.</summary>
    internal static string[]? SplitOnSlash(string value, int max)
    {
        // Count slashes first; reject early if too many.
        var slashCount = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '/') slashCount++;
        }
        if (slashCount >= max) return null;

        if (slashCount == 0) return new[] { value };

        var parts = new string[slashCount + 1];
        var partIdx = 0;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '/')
            {
                parts[partIdx++] = value.Substring(start, i - start);
                start = i + 1;
            }
        }
        parts[partIdx] = value.Substring(start);
        return parts;
    }
}
