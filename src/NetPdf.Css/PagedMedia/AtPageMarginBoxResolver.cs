// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using NetPdf.Css.Cascade;
using NetPdf.Css.Parser;

namespace NetPdf.Css.PagedMedia;

/// <summary>
/// Resolves the page margin boxes declared inside <c>@page</c> rules — the 16
/// <c>@top-center</c> / <c>@bottom-right-corner</c> / … at-rules of CSS Paged Media L3 §6.4 —
/// down to (box name, raw <c>content</c> value) pairs. Phase 3 Task 21 cycle 3 — the
/// keystone for running headers/footers.
/// </summary>
/// <remarks>
/// <para>
/// The pre-pass (<c>CssPreprocessor</c>) recovers the margin boxes AngleSharp.Css drops and the
/// adapter re-parents them under the owning <c>@page</c> rule's <see cref="CssAtRule.ChildRules"/>
/// (each a <see cref="CssAtRule"/> whose <see cref="CssAtRule.Name"/> is the box name and whose
/// <see cref="CssAtRule.Declarations"/> are parsed). This resolver reads them. Applicability +
/// ordering reuse the shared <see cref="AtPageRules"/> enumerations (cascade-style
/// media / disabled filtering; bare <c>@page</c> then the matching selector rules in specificity order,
/// so a <c>:first</c> / <c>:left</c> / <c>:right</c> / <c>:blank</c> margin box overrides the bare one on
/// the page it applies to — cycle 6) — the paper-size conditioning that gates <c>size</c> does NOT apply
/// to margin boxes. The cascade winner per box name is chosen by
/// importance then source order (an <c>!important</c> <c>content</c> beats a normal one; among
/// equal importance the last wins), within a box body AND across <c>@page</c> rules. A box whose
/// winning <c>content</c> is the bare keyword <c>none</c> / <c>normal</c> (= "no generated
/// content") is omitted WITHOUT a diagnostic, as is a box that declares no <c>content</c> (cycle 3
/// paints text only).
/// </para>
/// <para>
/// <b>Scope.</b> Only the raw <c>content</c> value is returned; the orchestrator resolves it via
/// <c>CssContentList</c> + <c>MarginContentCollector</c> — literal strings, <c>attr()</c>,
/// <c>counter(page)</c>/<c>counter(pages)</c>, <c>string(name)</c> (via <c>string-set</c>), and
/// <c>element(name)</c> (via <c>position: running()</c>) are supported; per-box style (font / color /
/// alignment / background / border / padding / size) + the CSS Page 3 §5.3 three-box-per-edge sizing
/// shipped across Task 21 cycles 3–16 + Tasks 22–23. Still deferred: a non-page <c>counter()</c> /
/// <c>counters()</c>, <c>string-set: … content()</c>, <c>element()</c> full block rendering, and
/// cross-page "running" persistence (deferrals.md#layout-to-pdf-pipeline).
/// </para>
/// </remarks>
internal static class AtPageMarginBoxResolver
{
    /// <summary>A margin box resolved to its name, the raw value of its winning <c>content</c>
    /// declaration (importance/quoting intact — the orchestrator resolves it via
    /// <c>CssContentList</c>), and the box's declarations (all of them, in source order across
    /// <c>@page</c> occurrences) from which the orchestrator builds the box's
    /// <c>ComputedStyle</c> (font / color / <c>text-align</c> / <c>vertical-align</c>, Task 21
    /// cycle 4).</summary>
    internal readonly record struct ResolvedMarginBox(
        string Name, string ContentRawValue, ImmutableArray<CssDeclaration> Declarations);

    /// <summary>The 16 CSS Paged Media L3 §6.4 margin-box names, in canonical paint order
    /// (corners + edges, top → bottom). The resolver emits present boxes in this order so the
    /// output is deterministic regardless of source order (CLAUDE.md #4).</summary>
    internal static readonly ImmutableArray<string> CanonicalNames = ImmutableArray.Create(
        "top-left-corner", "top-left", "top-center", "top-right", "top-right-corner",
        "left-top", "left-middle", "left-bottom",
        "right-top", "right-middle", "right-bottom",
        "bottom-left-corner", "bottom-left", "bottom-center", "bottom-right", "bottom-right-corner");

    private static readonly FrozenSet<string> KnownNames =
        CanonicalNames.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Walk the applicable <c>@page</c> rules (bare + <c>:first</c>, the single-page view) and
    /// resolve each declared margin box's cascade-winning <c>content</c> (importance then source order).
    /// Returns the renderable boxes in canonical order — omitting boxes with no <c>content</c> and boxes
    /// whose winner is <c>none</c> / <c>normal</c> (suppression); empty when none render.</summary>
    public static ImmutableArray<ResolvedMarginBox> Resolve(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(media);
        return ResolveFrom(AtPageRules.EnumeratePageRules(sheets, media));
    }

    /// <summary>Multi-page driver cycle 6 — resolve the margin boxes applicable to a SPECIFIC page,
    /// honoring the page's <c>:first</c> / <c>:left</c> / <c>:right</c> / <c>:blank</c> selectors in
    /// cascade-specificity order (so a left page paints <c>@page :left</c>'s boxes, the first page
    /// <c>@page :first</c>'s, etc., over the bare <c>@page</c>'s). Otherwise identical to
    /// <see cref="Resolve(IEnumerable{CssStylesheet}, CssMediaContext)"/>.</summary>
    public static ImmutableArray<ResolvedMarginBox> Resolve(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media, AtPageRules.PageSelectorContext ctx)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(media);
        return ResolveFrom(AtPageRules.EnumeratePageRules(sheets, media, ctx));
    }

    /// <summary>Multi-page driver cycle 6 — the UNION of the margin boxes that render on SOME page, for
    /// STRUCTURAL queries spanning all pages: detecting whether the document has any margin boxes at all,
    /// and prefetching their background-image urls (a page-specific box's image must be cached before any
    /// page that selector applies to paints). Resolves each of <see cref="AtPageRules.RepresentativeContexts"/>
    /// — the distinct first/parity/blank selector match-sets — and concatenates the results, so the cascade
    /// is applied PER CONTEXT (post-PR-#178 review P1): a single combined cascade across all selectors would
    /// let a bare <c>content: none</c> suppress a <c>@page :left</c> box from the union and wrongly drop it.
    /// A box that renders in more than one context appears more than once (one per context) — fine for the
    /// two consumers (emptiness check + reading every context's background-image url); per-page PAINTING
    /// uses the context-aware <see cref="Resolve(IEnumerable{CssStylesheet}, CssMediaContext, AtPageRules.PageSelectorContext)"/>.</summary>
    public static ImmutableArray<ResolvedMarginBox> ResolveAll(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(media);
        ImmutableArray<ResolvedMarginBox>.Builder? all = null;
        foreach (var ctx in AtPageRules.RepresentativeContexts)
        {
            foreach (var box in ResolveFrom(AtPageRules.EnumeratePageRules(sheets, media, ctx)))
                (all ??= ImmutableArray.CreateBuilder<ResolvedMarginBox>()).Add(box);
        }
        return all is null ? ImmutableArray<ResolvedMarginBox>.Empty : all.ToImmutable();
    }

    /// <summary>The shared margin-box cascade over a sequence of applicable <c>@page</c> rules (the
    /// single-page, per-page, and all-rules views differ only in which rules they feed here). Per box
    /// name accumulate: the cascade-winning <c>content</c> (importance then source order — a later normal
    /// can't override an earlier <c>!important</c>, within a box body AND across <c>@page</c> rules,
    /// post-PR-#132 review P1) + ALL declarations in source order (the orchestrator builds the box's
    /// ComputedStyle from these — Task 21 cycle 4).</summary>
    private static ImmutableArray<ResolvedMarginBox> ResolveFrom(IEnumerable<CssAtRule> pageRules)
    {
        Dictionary<string, Acc>? accs = null;
        foreach (var at in pageRules)
        {
            foreach (var child in at.ChildRules)
            {
                if (child is not CssAtRule box) continue;
                var name = box.Name.ToLowerInvariant();
                if (!KnownNames.Contains(name)) continue;
                accs ??= new Dictionary<string, Acc>(StringComparer.Ordinal);
                if (!accs.TryGetValue(name, out var acc)) accs[name] = acc = new Acc();
                acc.Declarations.AddRange(box.Declarations);
                foreach (var decl in box.Declarations)
                {
                    if (!string.Equals(decl.Property, "content", StringComparison.OrdinalIgnoreCase)) continue;
                    var raw = decl.Value.RawText;
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    Apply(ref acc.Content, raw, decl.IsImportant);
                }
            }
        }

        if (accs is null) return ImmutableArray<ResolvedMarginBox>.Empty;

        var output = ImmutableArray.CreateBuilder<ResolvedMarginBox>(accs.Count);
        foreach (var name in CanonicalNames) // emit in canonical order for determinism
        {
            // A winning `none` / `normal` means "no box" (suppression), not unsupported content —
            // omit it WITHOUT a diagnostic (post-PR-#132 review P2).
            if (accs.TryGetValue(name, out var acc) && acc.Content.Set && !IsSuppression(acc.Content.RawValue))
                output.Add(new ResolvedMarginBox(name, acc.Content.RawValue, acc.Declarations.ToImmutable()));
        }
        return output.Count == 0 ? ImmutableArray<ResolvedMarginBox>.Empty : output.ToImmutable();
    }

    /// <summary>The applicable <c>@page</c> rules' (bare + <c>:first</c>) OWN declarations
    /// (<c>color</c> / <c>font-*</c> / …) in specificity-then-source order — the page-context style the
    /// margin boxes inherit from (CSS Page 3, Task 21 cycle 5). The margin boxes are in each rule's <see cref="CssAtRule.ChildRules"/>,
    /// not here. The orchestrator builds the page-context <c>ComputedStyle</c> from these (its
    /// whitelist ignores <c>margin</c> / <c>size</c> / non-inherited declarations).</summary>
    public static ImmutableArray<CssDeclaration> PageContextDeclarations(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(media);
        return PageContextDeclarationsFrom(AtPageRules.EnumeratePageRules(sheets, media));
    }

    /// <summary>Multi-page driver cycle 6 — the page-context declarations applicable to a SPECIFIC page
    /// (its <c>:first</c>/<c>:left</c>/<c>:right</c>/<c>:blank</c> rules' own <c>color</c>/<c>font-*</c>/…
    /// in specificity-then-source order), so a page's margin boxes inherit that page's context style.</summary>
    public static ImmutableArray<CssDeclaration> PageContextDeclarations(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media, AtPageRules.PageSelectorContext ctx)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(media);
        return PageContextDeclarationsFrom(AtPageRules.EnumeratePageRules(sheets, media, ctx));
    }

    private static ImmutableArray<CssDeclaration> PageContextDeclarationsFrom(IEnumerable<CssAtRule> pageRules)
    {
        ImmutableArray<CssDeclaration>.Builder? decls = null;
        foreach (var at in pageRules)
        {
            if (at.Declarations.IsDefaultOrEmpty) continue;
            decls ??= ImmutableArray.CreateBuilder<CssDeclaration>();
            decls.AddRange(at.Declarations);
        }
        return decls is null ? ImmutableArray<CssDeclaration>.Empty : decls.ToImmutable();
    }

    /// <summary>Record <paramref name="raw"/> as the per-box cascade winner if it wins per CSS
    /// Cascade §5 (importance) + §7.4 (source order): an <c>!important</c> beats a normal
    /// declaration regardless of order; among equal importance the later (this one, visited in
    /// source order) wins.</summary>
    private static void Apply(ref Candidate candidate, string raw, bool important)
    {
        if (candidate.Set && candidate.Important && !important) return;
        candidate = new Candidate { Set = true, RawValue = raw, Important = important };
    }

    /// <summary>True when <paramref name="raw"/> is the bare keyword <c>none</c> or <c>normal</c> —
    /// both compute to "no generated content" for a margin box, so the box is omitted (NOT a
    /// diagnostic). A quoted <c>"none"</c> keeps its quotes and renders as the literal text.</summary>
    private static bool IsSuppression(string raw)
    {
        var v = raw.Trim();
        return v.Equals("none", StringComparison.OrdinalIgnoreCase)
            || v.Equals("normal", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Per-box cascade candidate: the winning raw <c>content</c> value so far + whether it
    /// came from an <c>!important</c> declaration. <see cref="Set"/> distinguishes "no winner yet"
    /// from a real value.</summary>
    private struct Candidate
    {
        public bool Set;
        public string RawValue;
        public bool Important;
    }

    /// <summary>Per-box-name accumulator: the cascade-winning <c>content</c> + every declaration
    /// seen for the box (source order, across <c>@page</c> occurrences) for the style build. A
    /// class so the <see cref="Content"/> field can be passed by <c>ref</c> + the builder mutated
    /// in place via the dictionary.</summary>
    private sealed class Acc
    {
        public Candidate Content;
        public readonly ImmutableArray<CssDeclaration>.Builder Declarations =
            ImmutableArray.CreateBuilder<CssDeclaration>();
    }
}
