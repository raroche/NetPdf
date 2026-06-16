// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using AngleSharp.Dom;
using NetPdf.Css.Cascade;
using NetPdf.Css.Parser;

namespace NetPdf.Css.PagedMedia;

/// <summary>
/// Shared walk over a document's stylesheets that yields the BARE <c>@page</c> rules applicable
/// to a given <see cref="CssMediaContext"/>, filtered + recursed like the cascade: skips
/// <see cref="CssStylesheet.IsDisabled"/> sheets, honors <see cref="CssStylesheet.MediaQuery"/>,
/// and recurses into <c>@media</c> blocks only when their condition matches — in source order
/// (sheet order → rule order). The <c>EnumerateBarePageRules*</c> view yields ONLY the bare
/// <c>@page</c> rule; <c>EnumeratePageRules*</c> (no context) additionally yields the <c>@page :first</c>
/// rules in cascade-specificity order (bare, then <c>:first</c>) so the resolvers' last-wins cascade lets
/// <c>:first</c> override the bare page — the single-page / page-geometry view. The CONTEXT-aware
/// <see cref="EnumeratePageRules(IEnumerable{CssStylesheet}, CssMediaContext, PageSelectorContext)"/>
/// (multi-page driver cycle 6) yields the rules applicable to a GIVEN page — bare → matching
/// <c>:left</c>/<c>:right</c> → matching <c>:first</c>/<c>:blank</c>, in specificity order. For the
/// structural "does any margin box render anywhere" + background-image prefetch UNION, the margin-box
/// resolver resolves each of <see cref="RepresentativeContexts"/> (the distinct selector match-sets) and
/// combines them, rather than cascading all selectors together (which would let a bare <c>content: none</c>
/// suppress a selector-scoped box — post-PR-#178 review P1). Named-page selectors (<c>@page &lt;name&gt;</c>,
/// cycle 7) match at tier 3 against the page's <see cref="PageSelectorContext.AssignedPageName"/>; COMPOUND
/// selectors (<c>chapter:first</c>) stay deferred.
/// The <c>@page</c> margin + size + margin-box resolvers share this so applicability stays consistent.
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
/// <see cref="PageRule.PaperSizeConditioned"/> — <see langword="true"/> when the sheet or
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
    /// <summary>An applicable <c>@page</c> rule yielded by the walk (the bare page, or an applied
    /// selector-scoped rule such as <c>@page :first</c>), paired with whether the path that reached
    /// it (sheet media query + nested <c>@media</c> preludes) is conditioned on a paper-size media
    /// feature — see the type remarks and CSS Page 3 §3.3.</summary>
    public readonly record struct PageRule(CssAtRule Rule, bool PaperSizeConditioned);

    /// <summary>Yields the applicable bare <c>@page</c> rules together with their paper-size
    /// conditioning. The margin resolver ignores the flag; the size resolver honors it.</summary>
    public static IEnumerable<PageRule> EnumerateBarePageRulesWithMediaInfo(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media)
        => Walk(sheets, media, PageSelectorKind.Bare);

    /// <summary>Yields just the applicable bare <c>@page</c> rules (paper-size conditioning
    /// dropped) — the margin resolver's view, which the flag does not affect.</summary>
    public static IEnumerable<CssAtRule> EnumerateBarePageRules(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media)
    {
        foreach (var bare in EnumerateBarePageRulesWithMediaInfo(sheets, media))
            yield return bare.Rule;
    }

    /// <summary>Yields the <c>@page</c> rules applicable to the page being resolved, in CASCADE
    /// SPECIFICITY ORDER (Task 21 — selectors): the bare <c>@page</c> rules first (specificity 0),
    /// then the <c>@page :first</c> rules (a pseudo-class adds specificity). A resolver that applies
    /// these in iteration order with a last-wins / importance-then-source-order cascade therefore
    /// lets <c>:first</c> override the bare page — and a bare <c>!important</c> still beats a
    /// <c>:first</c> normal (importance outranks specificity). The single page IS the first page, so
    /// <c>:first</c> matches; <c>:left</c> / <c>:right</c> / <c>:blank</c> / named-page selectors need
    /// the multi-page driver's page context and are NOT yet applied (deferrals.md).</summary>
    public static IEnumerable<PageRule> EnumeratePageRulesWithMediaInfo(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media)
    {
        foreach (var r in Walk(sheets, media, PageSelectorKind.Bare)) yield return r;
        foreach (var r in Walk(sheets, media, PageSelectorKind.First)) yield return r;
    }

    /// <summary>The rule-only view of <see cref="EnumeratePageRulesWithMediaInfo(IEnumerable{CssStylesheet}, CssMediaContext)"/>
    /// — bare then <c>:first</c>, in specificity order.</summary>
    public static IEnumerable<CssAtRule> EnumeratePageRules(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media)
    {
        foreach (var r in EnumeratePageRulesWithMediaInfo(sheets, media)) yield return r.Rule;
    }

    /// <summary>Multi-page driver cycle 6 — the per-page selector context the resolvers match an
    /// <c>@page</c> selector against (CSS Page 3 §3.1): the 0-based <paramref name="PageIndex"/> (→
    /// first-page + LTR parity), whether the page is intentionally <paramref name="IsBlank"/>, and the
    /// NAMED page assigned to it (<paramref name="AssignedPageName"/> — cycle 7, the used value of the
    /// <c>page</c> property on the page's break-triggering box; <see langword="null"/>/empty = unnamed). RTL
    /// parity flip is out of scope (the basic LTR parity mapping).</summary>
    public readonly record struct PageSelectorContext(
        int PageIndex, bool IsBlank = false, string? AssignedPageName = null)
    {
        /// <summary>The first page (index 0) matches <c>:first</c>.</summary>
        public bool IsFirstPage => PageIndex == 0;

        /// <summary>LTR page progression: the first page (index 0) is a RIGHT (recto) page and sides
        /// alternate, so a <see langword="true"/> page matches <c>:right</c> and a <see langword="false"/>
        /// one <c>:left</c> (CSS Page 3 §3.1). RTL flips this — out of scope (the basic parity mapping).</summary>
        public bool IsRightPage => (PageIndex & 1) == 0;
    }

    /// <summary>Yields the <c>@page</c> rules applicable TO A GIVEN PAGE (cycle 6) in CASCADE
    /// SPECIFICITY ORDER — bare first, then the matching <c>:left</c>/<c>:right</c> parity rules, then
    /// the matching <c>:first</c>/<c>:blank</c> rules, then the matching named-page (<c>@page &lt;name&gt;</c>)
    /// rules, then compounds — so a resolver applying them in iteration order with a last-wins cascade
    /// lets the higher-specificity selector win (and a bare <c>!important</c> still beats a selector-scoped
    /// normal: importance outranks specificity). A rule whose selector list doesn't match the context
    /// (<see cref="MatchTier"/> &lt; 0 — a <c>:left</c> rule on a right page, an unmatched named page) is
    /// skipped.</summary>
    public static IEnumerable<CssAtRule> EnumeratePageRules(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media, PageSelectorContext ctx)
    {
        foreach (var pr in EnumeratePageRulesWithMediaInfo(sheets, media, ctx)) yield return pr.Rule;
    }

    /// <summary>The context-aware view of <see cref="EnumeratePageRulesWithMediaInfo(IEnumerable{CssStylesheet}, CssMediaContext)"/>
    /// (per-page-geometry cycle): the <c>@page</c> rules applicable TO A GIVEN PAGE in CASCADE
    /// SPECIFICITY ORDER (ascending <see cref="MatchTier"/>, source order within a tier), each WITH its
    /// paper-size conditioning so the context-aware <see cref="AtPageSizeResolver"/> can honor the CSS
    /// Page 3 §3.3 ignore rule per page.</summary>
    public static IEnumerable<PageRule> EnumeratePageRulesWithMediaInfo(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media, PageSelectorContext ctx)
    {
        // ONE walk over the stylesheets, collecting each applicable rule with its (A,B,C)-encoded
        // specificity tier, then a STABLE sort by tier (ascending) — source order within a tier is
        // preserved via the secondary sort key, so the cascade result is unchanged. (A flat tier ARRAY
        // no longer fits: the compound-@page tier encoding A*100 + B*10 + C — pure/multi-pseudo cycle —
        // ranges past the old 0..5.)
        var matched = new List<(int Tier, int Order, PageRule Rule)>();
        var order = 0;
        foreach (var pr in WalkAllPageRules(sheets, media))
        {
            var tier = MatchTier(pr.Rule.Prelude, ctx);
            if (tier >= 0) matched.Add((tier, order, pr));   // no selector matched (< 0) → skip
            order++;
        }
        matched.Sort(static (x, y) => x.Tier != y.Tier ? x.Tier.CompareTo(y.Tier) : x.Order.CompareTo(y.Order));
        foreach (var m in matched) yield return m.Rule;
    }

    /// <summary>The DISTINCT page contexts whose <c>@page</c> selector match-sets differ — every
    /// combination of {first+right, left, right} × {blank, non-blank} (page 0 = first = recto/right;
    /// parity from index). The structural "does any margin box render anywhere" + prefetch UNION
    /// resolves each of these and combines the result, rather than cascading all selectors together
    /// (post-PR-#178 review P1): a single combined cascade lets a later bare <c>content: none</c>
    /// suppress an earlier <c>@page :left { … content }</c> from the union, which would wrongly drop the
    /// page-specific box (and under-prefetch its background image). Resolving per representative context
    /// keeps any box that renders on SOME page.</summary>
    internal static readonly ImmutableArray<PageSelectorContext> RepresentativeContexts =
        ImmutableArray.Create(
            new PageSelectorContext(0, IsBlank: false),   // first, right
            new PageSelectorContext(1, IsBlank: false),   // left
            new PageSelectorContext(2, IsBlank: false),   // right, non-first
            new PageSelectorContext(0, IsBlank: true),    // first, right, blank
            new PageSelectorContext(1, IsBlank: true),    // left, blank
            new PageSelectorContext(2, IsBlank: true));   // right, non-first, blank

    private static IEnumerable<PageRule> Walk(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media, PageSelectorKind want)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(media);

        foreach (var sheet in sheets)
        {
            if (sheet.IsDisabled) continue;                 // disabled sheets don't contribute
            if (!media.Matches(sheet.MediaQuery)) continue; // sheet media must match (e.g. print)
            var conditioned = IsPaperSizeConditioned(sheet.MediaQuery);
            foreach (var page in FromRules(sheet.Rules, media, conditioned, want)) yield return page;
        }
    }

    private static IEnumerable<PageRule> FromRules(
        ImmutableArray<CssRule> rules, CssMediaContext media, bool conditioned, PageSelectorKind want)
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
                    foreach (var page in FromRules(at.ChildRules, media, nested, want)) yield return page;
                }
            }
            else if (string.Equals(at.Name, "page", StringComparison.OrdinalIgnoreCase)
                     && ClassifyPageSelector(at.Prelude) == want)
            {
                yield return new PageRule(at, conditioned);
            }
        }
    }

    /// <summary>Classify an <c>@page</c> rule's recovered selector (its <see cref="CssAtRule.Prelude"/>).
    /// Per CSS Page 3 the prelude is a COMMA-SEPARATED page-selector LIST and the rule applies if ANY
    /// selector matches: an empty prelude → <see cref="PageSelectorKind.Bare"/>; a list with a pure
    /// <c>:first</c> selector (e.g. <c>:first</c> or <c>:first, :left</c>) → <see cref="PageSelectorKind.First"/>
    /// (it matches the first page); anything else (<c>:left</c> / <c>:right</c> / <c>:blank</c>, a named
    /// page, or a compound like <c>chapter:first</c> / <c>:first:left</c>) → <see cref="PageSelectorKind.Deferred"/>
    /// (recognized but not yet applied — they need the multi-page driver's page context).</summary>
    internal static PageSelectorKind ClassifyPageSelector(string? prelude)
    {
        if (string.IsNullOrWhiteSpace(prelude)) return PageSelectorKind.Bare;
        // The rule applies on the first page if ANY selector in the list is the pure `:first`.
        foreach (var selector in prelude.Split(','))
            if (selector.Trim().Equals(":first", StringComparison.OrdinalIgnoreCase))
                return PageSelectorKind.First;
        return PageSelectorKind.Deferred;
    }

    /// <summary>The <c>@page</c> selector kinds the resolvers handle: the bare page, the
    /// <c>:first</c> page, and everything else (deferred — multi-page-gated).</summary>
    internal enum PageSelectorKind { Bare, First, Deferred }

    /// <summary>The matching SPECIFICITY TIER of an <c>@page</c> selector list against
    /// <paramref name="ctx"/> (cycle 6 — generalizes <see cref="ClassifyPageSelector"/> to a page
    /// context), or <c>-1</c> when NO selector in the list matches (the rule doesn't apply). Per CSS
    /// Page 3 the prelude is a comma-separated list and the rule applies if ANY selector matches; the
    /// tier returned is the HIGHEST among the MATCHING selectors (so <c>:first, :left</c> contributes
    /// at <c>:first</c>'s tier on the first page and <c>:left</c>'s tier on a left page). The tier is the
    /// §3.1 (A,B,C) specificity tuple encoded as <c>A*100 + B*10 + C</c> (A = a named page, B = the count
    /// of <c>:first</c>/<c>:blank</c>, C = the count of <c>:left</c>/<c>:right</c>), so a numeric compare
    /// reproduces the lexicographic order: bare (0) &lt; <c>:left</c>/<c>:right</c> (1) &lt;
    /// <c>:first</c>/<c>:blank</c> (10) &lt; the pure-pseudo compound <c>:first:left</c> (11) &lt; a NAMED
    /// page (100) &lt; <c>&lt;name&gt;:left</c> (101) &lt; <c>&lt;name&gt;:first</c> (110) &lt;
    /// <c>&lt;name&gt;:first:left</c> (111). Pure-pseudo + multi-pseudo compounds are now modeled
    /// (pure/multi-pseudo cycle); only an invalid name / unknown pseudo fails to match.</summary>
    internal static int MatchTier(string? prelude, PageSelectorContext ctx)
    {
        if (string.IsNullOrWhiteSpace(prelude)) return 0;   // bare — always applies
        var best = -1;
        foreach (var raw in prelude.Split(','))
        {
            var (tier, matches) = MatchSelector(raw.Trim(), ctx);
            if (matches && tier > best) best = tier;
        }
        return best;
    }

    /// <summary>Match ONE page selector against the context, returning its specificity tier + whether
    /// it matches. A page selector is an optional page-type NAME followed by zero or more page
    /// pseudo-classes (CSS Page 3 §3.1): <c>[&lt;name&gt;] (:first|:blank|:left|:right)*</c> — e.g.
    /// <c>:first</c>, <c>:left</c>, <c>chapter</c>, <c>chapter:first</c>, the PURE-pseudo compound
    /// <c>:first:left</c>, and the multi-pseudo named compound <c>chapter:first:left</c>. The specificity
    /// is the §3.1 (A,B,C) tuple — A = a named page (0/1), B = the count of <c>:first</c>/<c>:blank</c>,
    /// C = the count of <c>:left</c>/<c>:right</c> — compared LEXICOGRAPHICALLY, encoded here as
    /// <c>A*100 + B*10 + C</c> so a numeric compare reproduces the lexicographic order (a higher value =
    /// more specific). The rule matches iff the name (if present) AND EVERY pseudo match the context. An
    /// invalid name, an unknown pseudo, or an empty selector returns (no tier, no match).</summary>
    private static (int Tier, bool Matches) MatchSelector(string selector, PageSelectorContext ctx)
    {
        if (selector.Length == 0) return (-1, false);
        // Split on ':' — the first segment is the optional name; the rest are pseudo-classes (a leading
        // ':' makes segment[0] empty = a pure-pseudo selector).
        var segments = selector.Split(':');
        var name = segments[0];
        var hasName = name.Length > 0;
        if (hasName && !IsBarePageName(name)) return (-1, false);   // an invalid name → not a page selector
        var a = hasName ? 1 : 0;
        var b = 0;   // # of :first / :blank
        var c = 0;   // # of :left / :right
        var matches = !hasName || NamedMatches(name, ctx);
        for (var i = 1; i < segments.Length; i++)
        {
            var (pseudoTier, pMatches, pKnown) = MatchPagePseudo(":" + segments[i], ctx);
            if (!pKnown) return (-1, false);   // an unknown / empty / pseudo-element token → not a selector
            if (pseudoTier == 2) b++; else c++;   // :first/:blank → B axis; :left/:right → C axis
            matches = matches && pMatches;
        }
        if (a == 0 && b == 0 && c == 0) return (-1, false);   // empty selector (no name, no pseudos)
        return (a * 100 + b * 10 + c, matches);
    }

    /// <summary>Does <paramref name="name"/> (a bare custom-ident) match the page's assigned name?
    /// Case-SENSITIVE per CSS custom-idents; a context with no assigned name never matches.</summary>
    private static bool NamedMatches(string name, PageSelectorContext ctx)
        => !string.IsNullOrEmpty(ctx.AssignedPageName)
           && string.Equals(ctx.AssignedPageName, name, StringComparison.Ordinal);

    /// <summary>Classify ONE page pseudo-class token (<c>:first</c>/<c>:blank</c> → axis tier 2,
    /// <c>:left</c>/<c>:right</c> → axis tier 1), returning its tier, whether it matches the context, and
    /// whether the token was a KNOWN single page pseudo (false → not a recognized lone pseudo, so the
    /// caller falls through to named / compound / deferred).</summary>
    private static (int Tier, bool Matches, bool Known) MatchPagePseudo(string pseudo, PageSelectorContext ctx)
    {
        if (pseudo.Equals(":first", StringComparison.OrdinalIgnoreCase)) return (2, ctx.IsFirstPage, true);
        if (pseudo.Equals(":blank", StringComparison.OrdinalIgnoreCase)) return (2, ctx.IsBlank, true);
        if (pseudo.Equals(":left", StringComparison.OrdinalIgnoreCase)) return (1, !ctx.IsRightPage, true);
        if (pseudo.Equals(":right", StringComparison.OrdinalIgnoreCase)) return (1, ctx.IsRightPage, true);
        return (0, false, false);
    }

    /// <summary><see langword="true"/> when <paramref name="selector"/> is a valid bare CSS
    /// <c>&lt;custom-ident&gt;</c> usable as a named page. Delegates to the shared
    /// <see cref="CssCustomIdent.IsValidPageName"/> so the <c>@page &lt;name&gt;</c> selector + the
    /// used-name walk stay in lock-step with <c>PageNameResolver</c> (the <c>page</c> property +
    /// <c>@supports</c>) — post-PR-#183 review P2, which centralized them so a valid DASHED ident
    /// (<c>--chapter</c>) is accepted by BOTH (was rejected by both). Anything with a <c>:</c> (a pseudo /
    /// compound) is screened off by <see cref="MatchSelector"/> before this runs.</summary>
    private static bool IsBarePageName(string selector) => CssCustomIdent.IsValidPageName(selector);

    /// <summary>The DISTINCT named pages declared by <c>@page &lt;name&gt;</c> rules applicable to
    /// <paramref name="media"/> (cycle 7) — for the structural/prefetch union, which must include a context
    /// per named page so a named margin box isn't missed (it never matches an anonymous representative
    /// context). A compound selector (<c>chapter:first</c>) contributes its leading name too.</summary>
    internal static IEnumerable<string> DeclaredPageNames(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media)
    {
        HashSet<string>? seen = null;
        foreach (var pr in WalkAllPageRules(sheets, media))
        {
            var at = pr.Rule;
            if (string.IsNullOrWhiteSpace(at.Prelude)) continue;
            foreach (var raw in at.Prelude.Split(','))
            {
                var sel = raw.Trim();
                // The leading ident before any pseudo (`chapter` or `chapter:first` → "chapter").
                var colon = sel.IndexOf(':');
                var name = (colon >= 0 ? sel[..colon] : sel).Trim();
                if (IsBarePageName(name) && (seen ??= new(StringComparer.Ordinal)).Add(name))
                    yield return name;
            }
        }
    }

    /// <summary>The USED value of the <c>page</c> property (CSS Page 3 §3.4) for
    /// <paramref name="element"/> — its nearest self-or-ancestor element whose <c>page</c> is a valid
    /// <c>&lt;custom-ident&gt;</c> (the property is read raw from the cascade; AngleSharp drops it, so
    /// <c>CssPreprocessor</c> recovers it). Returns the empty string when nothing in the chain names a page.
    /// Only a VALID custom-ident names a page (post-PR-#179 review P1): <c>auto</c>, the CSS-wide keywords
    /// (<c>inherit</c> / <c>initial</c> / <c>unset</c> / <c>revert</c>), and INVALID raw values (<c>-1</c>,
    /// <c>123</c>) all resolve to the parent's used value — so the walk CONTINUES past them. (For the
    /// non-inherited <c>page</c> property: <c>initial</c> / <c>unset</c> = <c>auto</c> = parent's used value;
    /// <c>inherit</c> = the parent's value — both mean "no name at this element", which the continue models;
    /// an invalid value is treated as the initial <c>auto</c>, CSS Syntax error recovery.) The property isn't
    /// a first-class registered property yet — a documented follow-up.</summary>
    public static string ResolveUsedPageName(IElement? element, ResolvedCascadeResult cascade)
    {
        ArgumentNullException.ThrowIfNull(cascade);
        for (IElement? el = element; el is not null; el = el.ParentElement)
        {
            var raw = cascade.TryGetStylesFor(el)?.GetWinner("page")?.ResolvedValue?.Trim();
            if (!string.IsNullOrEmpty(raw) && IsBarePageName(raw)) return raw;   // a valid name stops the walk
        }
        return string.Empty;
    }

    /// <summary>Recursive walk (cycle 6) yielding EVERY applicable <c>@page</c> rule in source order WITH
    /// its paper-size conditioning, with the SAME sheet-media / disabled filtering + <c>@media</c>
    /// recursion as <see cref="Walk"/>. The callers
    /// (<see cref="EnumeratePageRules(IEnumerable{CssStylesheet}, CssMediaContext, PageSelectorContext)"/>
    /// + <see cref="EnumeratePageRulesWithMediaInfo(IEnumerable{CssStylesheet}, CssMediaContext, PageSelectorContext)"/>)
    /// bin by <see cref="MatchTier"/>. Conditioning is now tracked (per-page-geometry cycle — the
    /// context-aware size resolver honors the §3.3 paper-size ignore; the margin / margin-box consumers
    /// ignore the flag).</summary>
    private static IEnumerable<PageRule> WalkAllPageRules(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(media);
        foreach (var sheet in sheets)
        {
            if (sheet.IsDisabled) continue;                 // disabled sheets don't contribute
            if (!media.Matches(sheet.MediaQuery)) continue; // sheet media must match (e.g. print)
            var conditioned = IsPaperSizeConditioned(sheet.MediaQuery);
            foreach (var at in FromAllPageRules(sheet.Rules, media, conditioned)) yield return at;
        }
    }

    private static IEnumerable<PageRule> FromAllPageRules(
        ImmutableArray<CssRule> rules, CssMediaContext media, bool conditioned)
    {
        foreach (var rule in rules)
        {
            if (rule is not CssAtRule at) continue;
            if (string.Equals(at.Name, "media", StringComparison.OrdinalIgnoreCase))
            {
                if (media.Matches(at.Prelude)) // recurse only when the @media condition matches
                {
                    // Conditioning is sticky down the tree (mirrors FromRules): a paper-size @media wrap
                    // keeps nested rules conditioned even if their own @media isn't.
                    var nested = conditioned || IsPaperSizeConditioned(at.Prelude);
                    foreach (var r in FromAllPageRules(at.ChildRules, media, nested)) yield return r;
                }
            }
            else if (string.Equals(at.Name, "page", StringComparison.OrdinalIgnoreCase))
            {
                yield return new PageRule(at, conditioned);
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
