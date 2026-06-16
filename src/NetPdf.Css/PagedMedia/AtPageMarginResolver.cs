// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using NetPdf.Css.Cascade;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Parser;

namespace NetPdf.Css.PagedMedia;

/// <summary>
/// Resolves the page margins declared by <c>@page { margin… }</c> rules into absolute px,
/// per CSS Paged Media L3 §3. Phase 3 Task 21 — <b>margins only</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Applicability mirrors the cascade.</b> A sheet contributes only when it is not
/// <see cref="CssStylesheet.IsDisabled"/> and its <see cref="CssStylesheet.MediaQuery"/> matches
/// the active <see cref="CssMediaContext"/> (PDF output is <c>print</c>), and nested
/// <c>@media</c> rules are recursed into only when their condition matches — so a
/// <c>media="screen"</c> sheet or an <c>@media screen</c> block never affects the printed page,
/// and an <c>@media print { @page { … } }</c> block does.
/// </para>
/// <para>
/// The bare <c>@page</c> rule + the <c>@page :first</c> rule (which overrides it on the first page
/// by cascade specificity, via <see cref="AtPageRules.EnumeratePageRules(System.Collections.Generic.IEnumerable{CssStylesheet}, CssMediaContext)"/>)
/// are honored; <c>:left</c> / <c>:right</c> / <c>:blank</c> / named selectors stay deferred for page
/// GEOMETRY (the margin resolver is single-page; per-page margin BOXES honor them — cycle 6).
/// Reads the <c>margin-top</c> / <c>-right</c> / <c>-bottom</c> / <c>-left</c> longhands AngleSharp.Css
/// expands the <c>margin</c> shorthand into. Each side resolves an ABSOLUTE length (CSS Values
/// L4 §6.1, via <see cref="LengthResolver.TryAbsoluteUnitToPx"/>) or a PERCENTAGE — per CSS Page
/// 3, left/right percentages are relative to the page-box WIDTH and top/bottom to its HEIGHT
/// (the pipeline passes the RESOLVED page box — after any <c>@page { size }</c> override — so
/// percentages track the final page size, not the configured one). Other
/// units (<c>calc()</c>, <c>em</c>, …) leave the side unspecified. The cascade winner per side
/// is chosen by importance then source order: an <c>!important</c> declaration beats a normal
/// one, and among equal importance the later declaration wins. A side with no winner stays
/// <see langword="null"/> so the caller keeps its own default for that side.
/// </para>
/// <para>
/// <c>@page { size }</c> is NOT handled here — AngleSharp.Css drops the <c>size</c> descriptor,
/// so it needs pre-pass recovery in a follow-up cycle (deferrals.md#layout-to-pdf-pipeline).
/// </para>
/// </remarks>
internal static class AtPageMarginResolver
{
    /// <summary>Resolved bare-<c>@page</c> margins in px; a <see langword="null"/> side means
    /// "not specified by <c>@page</c>" (the caller keeps its default).</summary>
    internal readonly record struct ResolvedPageMargins(
        double? TopPx, double? RightPx, double? BottomPx, double? LeftPx)
    {
        /// <summary>True when at least one side was specified by an <c>@page</c> rule.</summary>
        public bool HasAny => TopPx is not null || RightPx is not null
            || BottomPx is not null || LeftPx is not null;
    }

    /// <summary>Walk the document's stylesheets — filtered + recursed exactly like the cascade
    /// for the given <paramref name="media"/> context — and resolve the effective bare-<c>@page</c>
    /// margins. Percentage margins resolve against the media context's viewport (the configured
    /// page size). Returns all-null when no contributing bare <c>@page</c> declares a resolvable
    /// margin.</summary>
    public static ResolvedPageMargins Resolve(IEnumerable<CssStylesheet> sheets, CssMediaContext media)
    {
        ArgumentNullException.ThrowIfNull(media);
        return Resolve(sheets, media, media.ViewportWidthPx, media.ViewportHeightPx);
    }

    /// <summary>As <see cref="Resolve(IEnumerable{CssStylesheet}, CssMediaContext)"/>, but resolves
    /// percentage margins against an explicit page box (<paramref name="pageWidthPx"/> ×
    /// <paramref name="pageHeightPx"/>) instead of the media viewport. Media applicability still
    /// uses <paramref name="media"/>; the page box is purely the percentage reference. The
    /// pipeline passes the RESOLVED page size here so <c>@page { size: A5; margin: 10% }</c>
    /// computes percentages against A5 (the final page box), not the configured size — per CSS
    /// Page 3, page-margin percentages are relative to the page box.</summary>
    public static ResolvedPageMargins Resolve(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media,
        double pageWidthPx, double pageHeightPx)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(media);
        return ResolveFrom(AtPageRules.EnumeratePageRules(sheets, media), pageWidthPx, pageHeightPx);
    }

    /// <summary>The PER-PAGE margins for the page described by <paramref name="ctx"/> (per-page-geometry
    /// cycle): like <see cref="Resolve(IEnumerable{CssStylesheet}, CssMediaContext, double, double)"/> but
    /// the applicable rules + cascade order come from the context-aware enumeration, so a
    /// <c>@page :left</c> / <c>:right</c> (duplex) / <c>:blank</c> / named-page margin wins on the matching
    /// page (and <c>:first</c> applies to the first page ONLY). Percentages resolve against
    /// <paramref name="pageWidthPx"/> × <paramref name="pageHeightPx"/> (the page's RESOLVED size).</summary>
    public static ResolvedPageMargins Resolve(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media,
        double pageWidthPx, double pageHeightPx, AtPageRules.PageSelectorContext ctx)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(media);
        return ResolveFrom(AtPageRules.EnumeratePageRules(sheets, media, ctx), pageWidthPx, pageHeightPx);
    }

    /// <summary>The BARE-<c>@page</c>-ONLY margins — bare rules only, with NO <c>:first</c> / selectors
    /// (post-PR-#184 review F2). The multi-page LAYOUT baseline uses this so the body fragments against the
    /// bare page margins; the per-page PAINT then applies the selector margins per page. (The
    /// <see cref="Resolve(IEnumerable{CssStylesheet}, CssMediaContext, double, double)"/> overload includes
    /// <c>:first</c>, correct only for a single-page document.)</summary>
    public static ResolvedPageMargins ResolveBare(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media,
        double pageWidthPx, double pageHeightPx)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(media);
        return ResolveFrom(AtPageRules.EnumerateBarePageRules(sheets, media), pageWidthPx, pageHeightPx);
    }

    private static ResolvedPageMargins ResolveFrom(
        IEnumerable<CssAtRule> rules, double pageWidthPx, double pageHeightPx)
    {
        Candidate top = default, right = default, bottom = default, left = default;
        foreach (var at in rules)
        {
            foreach (var decl in at.Declarations)
            {
                // CSS Page 3: left/right % relative to page WIDTH, top/bottom % to page HEIGHT.
                switch (decl.Property.ToLowerInvariant())
                {
                    case "margin-top": Apply(ref top, decl, pageHeightPx); break;
                    case "margin-bottom": Apply(ref bottom, decl, pageHeightPx); break;
                    case "margin-left": Apply(ref left, decl, pageWidthPx); break;
                    case "margin-right": Apply(ref right, decl, pageWidthPx); break;
                }
            }
        }
        return new ResolvedPageMargins(
            top.Set ? top.Px : null, right.Set ? right.Px : null,
            bottom.Set ? bottom.Px : null, left.Set ? left.Px : null);
    }

    /// <summary>Resolve a declaration's value and, if it wins the per-side cascade (importance
    /// then source order — declarations are visited in source order), record it.</summary>
    private static void Apply(ref Candidate side, CssDeclaration decl, double referencePx)
    {
        if (!TryResolveMarginPx(decl.Value.RawText, referencePx, out var px)) return;
        var important = decl.IsImportant;
        // A normal declaration cannot override a winning !important one; otherwise the later
        // declaration (this one, visited in source order) wins.
        if (side.Set && side.Important && !important) return;
        side = new Candidate { Set = true, Px = px, Important = important };
    }

    /// <summary>Resolve a single <c>@page</c> margin value to px: an absolute length, or a
    /// percentage of <paramref name="referencePx"/> (the relevant page-box dimension). Returns
    /// <see langword="false"/> for unsupported units (<c>calc()</c>, font-relative), keywords,
    /// or malformed input.</summary>
    internal static bool TryResolveMarginPx(string text, double referencePx, out double px)
    {
        px = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();

        if (s.EndsWith('%'))
        {
            if (!double.TryParse(s.AsSpan(0, s.Length - 1).Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var pct) || !double.IsFinite(pct))
                return false;
            if (!double.IsFinite(referencePx)) return false;
            px = pct / 100.0 * referencePx;
            return double.IsFinite(px);
        }
        return TryParseAbsoluteLengthPx(s, out px);
    }

    /// <summary>Parse a single absolute CSS length token (e.g. <c>"2cm"</c>, <c>"0"</c>,
    /// <c>"96px"</c>, <c>"-1in"</c>) to px. Returns <see langword="false"/> for non-absolute
    /// units, percentages, <c>calc()</c>, keywords, or malformed input.</summary>
    internal static bool TryParseAbsoluteLengthPx(string text, out double px)
    {
        px = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();

        // Split the leading number (optional sign + digits + decimal point) from the unit. CSS
        // <length> tokens don't use exponent notation, so a simple scan suffices.
        var i = 0;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
        var numberStart = i;
        while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.')) i++;
        if (i == numberStart) return false; // no numeric part

        if (!double.TryParse(s.AsSpan(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number))
            return false;

        var unit = s[i..].Trim().ToLowerInvariant();
        if (unit.Length == 0)
        {
            // Unitless 0 is a valid length; any other unitless number is invalid for a <length>.
            if (number == 0) { px = 0; return true; }
            return false;
        }
        return LengthResolver.TryAbsoluteUnitToPx(unit, number, out px) && double.IsFinite(px);
    }

    /// <summary>Per-side cascade candidate: the winning px value so far + whether it came from an
    /// <c>!important</c> declaration. <see cref="Set"/> distinguishes "no winner" from a real 0px.</summary>
    private struct Candidate
    {
        public bool Set;
        public double Px;
        public bool Important;
    }
}
