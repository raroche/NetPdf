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
/// to a given <see cref="CssMediaContext"/>, filtered + recursed EXACTLY like the cascade: skips
/// <see cref="CssStylesheet.IsDisabled"/> sheets, honors <see cref="CssStylesheet.MediaQuery"/>,
/// and recurses into <c>@media</c> blocks only when their condition matches — in source order
/// (sheet order → rule order). Selector-scoped page rules (<c>@page :first</c> etc.) are excluded
/// (deferred). The <c>@page</c> margin + size resolvers share this so applicability stays
/// consistent between them.
/// </summary>
internal static class AtPageRules
{
    public static IEnumerable<CssAtRule> EnumerateBarePageRules(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(media);

        foreach (var sheet in sheets)
        {
            if (sheet.IsDisabled) continue;                 // disabled sheets don't contribute
            if (!media.Matches(sheet.MediaQuery)) continue; // sheet media must match (e.g. print)
            foreach (var page in FromRules(sheet.Rules, media)) yield return page;
        }
    }

    private static IEnumerable<CssAtRule> FromRules(ImmutableArray<CssRule> rules, CssMediaContext media)
    {
        foreach (var rule in rules)
        {
            if (rule is not CssAtRule at) continue;

            if (string.Equals(at.Name, "media", StringComparison.OrdinalIgnoreCase))
            {
                if (media.Matches(at.Prelude)) // recurse only when the @media condition matches
                    foreach (var page in FromRules(at.ChildRules, media)) yield return page;
            }
            else if (string.Equals(at.Name, "page", StringComparison.OrdinalIgnoreCase)
                     && string.IsNullOrWhiteSpace(at.Prelude)) // bare @page only this cycle
            {
                yield return at;
            }
        }
    }
}
