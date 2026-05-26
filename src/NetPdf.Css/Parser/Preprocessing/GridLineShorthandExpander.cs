// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Css.ComputedValues.PropertyResolvers;

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
/// <para><b>Atomic validation per PR-#91 review F1:</b> the expander
/// pre-validates every <c>&lt;grid-line&gt;</c> component via
/// <c>GridLineResolver.TryValidate</c> BEFORE emitting any longhand
/// recovery record. Per CSS Cascade L4 §4.2, an invalid shorthand
/// declaration must contribute none of its longhands. The prior
/// "validate at the resolver" approach allowed partial application
/// (= <c>grid-row: 2 / 0</c> applied start=2 while end=0 dropped to
/// auto, silently placing items incorrectly).</para>
///
/// <para><b>CSS-wide keyword passthrough per PR-#90 review F3 +
/// PR-#91 review F4:</b> the expander passes <c>initial</c> /
/// <c>inherit</c> / <c>unset</c> / <c>revert</c> /
/// <c>revert-layer</c> verbatim to both longhands so the cascade
/// applies §7 semantics uniformly. <b>However</b>, the downstream
/// <c>GridLineResolver</c> currently REJECTS these keywords at the
/// per-property resolver layer (per the PR-#90 F3 defense-in-depth
/// path), so <c>grid-row: inherit</c> currently resolves to <c>auto</c>
/// in the box's ComputedStyle rather than inheriting the parent's
/// placement. The proper fix is central cascade interception of
/// CSS-wide keywords BEFORE per-property resolvers run; this remains
/// a cross-cutting concern tracked as a separate cycle's deferral.</para>
///
/// <para><b>var() per PR-#91 review F2:</b> when the shorthand value
/// contains a <c>var()</c> reference, expansion is SKIPPED (= the
/// declaration silently drops at the cascade since the preprocessor
/// can't know the post-substitution value structure). The flex
/// shorthand has the same pre-existing limitation. Post-substitution
/// re-expansion is a separate cycle's scope.</para>
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

        // Per PR-#91 review F2 — var() substitution runs AFTER the
        // preprocessor, so the expander can't know the final value
        // structure. Pragmatic cycle-0c stance: skip expansion when
        // var() is present; the shorthand silently drops at the cascade
        // (= same outcome as flex shorthand with var() — tracked as a
        // shared deferral). Post-substitution re-expansion is a
        // separate cycle's scope.
        if (ContainsCaseInsensitive(trimmed, "var("))
        {
            return false;
        }

        // Split by '/' into 1 or 2 parts.
        var parts = SplitOnSlash(trimmed, max: 2);
        if (parts is null) return false;
        if (parts.Length == 0 || parts.Length > 2) return false;

        var startPart = parts[0].Trim();
        if (startPart.Length == 0) return false;

        string endPart;
        if (parts.Length == 1)
        {
            // Single value — apply the §8.4 omitted-pair rule.
            endPart = GridShorthandHelpers.IsBareCustomIdent(startPart) ? startPart : "auto";
        }
        else
        {
            endPart = parts[1].Trim();
            if (endPart.Length == 0) return false;
        }

        // Per PR-#91 review F1 — atomically validate every shorthand
        // component BEFORE emitting any longhand. Per CSS Cascade L4 §4.2,
        // an invalid shorthand contributes none of its longhands; a
        // partially-applied shorthand (= start=2 from "2 / 0" surviving
        // while end=0 drops) would silently mis-place grid items.
        if (!GridLineResolver.TryValidate(startPart)) return false;
        if (!GridLineResolver.TryValidate(endPart)) return false;

        start = startPart;
        end = endPart;
        return true;
    }

    private static bool ContainsCaseInsensitive(string haystack, string needle)
        => haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Split <paramref name="value"/> on every <c>/</c> character.
    /// Returns null if the slash count would yield more than
    /// <paramref name="max"/> parts (= more separators than the grammar
    /// admits).
    ///
    /// <para><b>Scope per PR-#91 review F6:</b> this is a FLAT slash split
    /// — no paren-depth tracking, no string-literal awareness, no
    /// awareness of CSS-function syntax. The current grid-line grammar
    /// per §8.3 doesn't admit <c>/</c> inside any function (= no
    /// <c>minmax</c> / <c>fit-content</c> / <c>repeat</c> at this layer;
    /// those are track-list grammar only). If future grid syntax adds
    /// a function that may contain <c>/</c>, callers must invoke a
    /// nesting-aware splitter instead.</para>
    ///
    /// <para><b>Empty-slot safety:</b> the splitter does NOT validate
    /// empty-after-split parts (= <c>"2 / / 4"</c> produces three parts
    /// where the middle is whitespace-only). The caller's per-part
    /// trim-and-check logic catches that case.</para></summary>
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
