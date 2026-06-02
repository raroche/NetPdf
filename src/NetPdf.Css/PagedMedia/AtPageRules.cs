// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using NetPdf.Css.Cascade;
using NetPdf.Css.Parser;

namespace NetPdf.Css.PagedMedia;

/// <summary>
/// Shared walk over a document's stylesheets that yields the BARE <c>@page</c> rules applicable
/// to a given <see cref="CssMediaContext"/>, filtered + recursed like the cascade: skips
/// <see cref="CssStylesheet.IsDisabled"/> sheets, honors <see cref="CssStylesheet.MediaQuery"/>,
/// and recurses into <c>@media</c> blocks only when their condition matches — in source order
/// (sheet order → rule order). Selector-scoped page rules (<c>@page :first</c> etc.) are excluded
/// (deferred). The <c>@page</c> margin + size resolvers share this so applicability stays
/// consistent between them.
/// </summary>
/// <remarks>
/// <b>Scope vs. the cascade (Task 21).</b> Only the sheet-level media query and nested
/// <c>@media</c> blocks are traversed. The other conditional grouping rules the cascade honors
/// — <c>@supports</c>, <c>@layer</c>, <c>@container</c> — are NOT yet recursed for <c>@page</c>,
/// so a bare <c>@page</c> nested inside one of them does not contribute. That is a tracked
/// follow-up (deferrals.md#layout-to-pdf-pipeline); recursing it needs the cascade's
/// <c>@supports</c> evaluator lifted out of <see cref="CascadeResolver"/> into a shared helper.
/// <para>
/// <b>Paper-size conditioning (CSS Page 3 §3.3).</b> Each yielded rule carries
/// <see cref="BarePageRule.PaperSizeConditioned"/> — <see langword="true"/> when the sheet or
/// any matched <c>@media</c> on the path to the rule is qualified by a paper-size media feature
/// (<c>width</c> / <c>height</c> / <c>device-width</c> / <c>device-height</c> /
/// <c>aspect-ratio</c> / <c>device-aspect-ratio</c> / <c>orientation</c>, with <c>min-</c> /
/// <c>max-</c> prefixes). The <c>size</c> resolver must IGNORE <c>size</c> in that context (it
/// would otherwise be a circular page-size dependency); margins are unaffected and use the
/// rule regardless.
/// </para>
/// </remarks>
internal static class AtPageRules
{
    /// <summary>A bare <c>@page</c> rule yielded by the walk, paired with whether the path that
    /// reached it (sheet media query + nested <c>@media</c> preludes) is conditioned on a
    /// paper-size media feature — see the type remarks and CSS Page 3 §3.3.</summary>
    public readonly record struct BarePageRule(CssAtRule Rule, bool PaperSizeConditioned);

    /// <summary>Yields the applicable bare <c>@page</c> rules together with their paper-size
    /// conditioning. The margin resolver ignores the flag; the size resolver honors it.</summary>
    public static IEnumerable<BarePageRule> EnumerateBarePageRulesWithMediaInfo(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(media);

        foreach (var sheet in sheets)
        {
            if (sheet.IsDisabled) continue;                 // disabled sheets don't contribute
            if (!media.Matches(sheet.MediaQuery)) continue; // sheet media must match (e.g. print)
            var conditioned = IsPaperSizeConditioned(sheet.MediaQuery);
            foreach (var page in FromRules(sheet.Rules, media, conditioned)) yield return page;
        }
    }

    /// <summary>Yields just the applicable bare <c>@page</c> rules (paper-size conditioning
    /// dropped) — the margin resolver's view, which the flag does not affect.</summary>
    public static IEnumerable<CssAtRule> EnumerateBarePageRules(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media)
    {
        foreach (var bare in EnumerateBarePageRulesWithMediaInfo(sheets, media))
            yield return bare.Rule;
    }

    private static IEnumerable<BarePageRule> FromRules(
        ImmutableArray<CssRule> rules, CssMediaContext media, bool conditioned)
    {
        foreach (var rule in rules)
        {
            if (rule is not CssAtRule at) continue;

            if (string.Equals(at.Name, "media", StringComparison.OrdinalIgnoreCase))
            {
                if (media.Matches(at.Prelude)) // recurse only when the @media condition matches
                {
                    // The conditioning is sticky down the tree: once a paper-size @media wraps
                    // the rule, nested rules stay conditioned even if their own @media isn't.
                    var nested = conditioned || IsPaperSizeConditioned(at.Prelude);
                    foreach (var page in FromRules(at.ChildRules, media, nested)) yield return page;
                }
            }
            else if (string.Equals(at.Name, "page", StringComparison.OrdinalIgnoreCase)
                     && string.IsNullOrWhiteSpace(at.Prelude)) // bare @page only this cycle
            {
                yield return new BarePageRule(at, conditioned);
            }
        }
    }

    /// <summary>Returns <see langword="true"/> when <paramref name="mediaQuery"/> references a
    /// paper-size media feature (CSS Page 3 §3.3) in feature position — i.e. as the identifier
    /// immediately after a <c>(</c>. <c>min-</c> / <c>max-</c> range prefixes are stripped before
    /// the check, so <c>(min-width: 1px)</c>, <c>(width &gt;= 800px)</c>,
    /// <c>(orientation: landscape)</c>, and <c>(aspect-ratio: 4/3)</c> all qualify, while
    /// non-size features (<c>(min-resolution: 2dppx)</c>, <c>(prefers-color-scheme: dark)</c>) do
    /// not.</summary>
    internal static bool IsPaperSizeConditioned(string? mediaQuery)
    {
        if (string.IsNullOrWhiteSpace(mediaQuery)) return false;
        var s = mediaQuery.AsSpan();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '(') continue;
            var j = i + 1;
            while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
            var start = j;
            while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '-')) j++;
            if (j > start && IsPaperSizeFeature(s[start..j])) return true;
        }
        return false;
    }

    private static bool IsPaperSizeFeature(ReadOnlySpan<char> name)
    {
        if (name.StartsWith("min-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("max-", StringComparison.OrdinalIgnoreCase))
            name = name[4..];
        return name.Equals("width", StringComparison.OrdinalIgnoreCase)
            || name.Equals("height", StringComparison.OrdinalIgnoreCase)
            || name.Equals("device-width", StringComparison.OrdinalIgnoreCase)
            || name.Equals("device-height", StringComparison.OrdinalIgnoreCase)
            || name.Equals("aspect-ratio", StringComparison.OrdinalIgnoreCase)
            || name.Equals("device-aspect-ratio", StringComparison.OrdinalIgnoreCase)
            || name.Equals("orientation", StringComparison.OrdinalIgnoreCase);
    }
}
