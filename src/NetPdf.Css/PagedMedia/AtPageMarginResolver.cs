// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using NetPdf.Css.Cascade;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Parser;

namespace NetPdf.Css.PagedMedia;

/// <summary>
/// Resolves the page margins declared by bare <c>@page { margin… }</c> rules into absolute px,
/// per CSS Paged Media L3 §3. Phase 3 Task 21 cycle 1 — <b>margins only</b>.
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
/// Only the BARE <c>@page</c> rule (no <c>:first</c> / <c>:left</c> / <c>:right</c> / named
/// selector) is honored in this cycle; selector-scoped page rules are a later cycle. Reads the
/// <c>margin-top</c> / <c>-right</c> / <c>-bottom</c> / <c>-left</c> longhands AngleSharp.Css
/// expands the <c>margin</c> shorthand into. Each side resolves an ABSOLUTE length (CSS Values
/// L4 §6.1, via <see cref="LengthResolver.TryAbsoluteUnitToPx"/>) or a PERCENTAGE — per CSS Page
/// 3, left/right percentages are relative to the page-box WIDTH and top/bottom to its HEIGHT
/// (taken from the media context's viewport, which the pipeline sets to the page size). Other
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
    /// margins. Returns all-null when no contributing bare <c>@page</c> declares a resolvable
    /// margin.</summary>
    public static ResolvedPageMargins Resolve(IEnumerable<CssStylesheet> sheets, CssMediaContext media)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(media);

        Candidate top = default, right = default, bottom = default, left = default;
        foreach (var sheet in sheets)
        {
            if (sheet.IsDisabled) continue;                 // disabled sheets don't contribute
            if (!media.Matches(sheet.MediaQuery)) continue; // sheet media must match (e.g. print)
            CollectRules(sheet.Rules, media, ref top, ref right, ref bottom, ref left);
        }
        return new ResolvedPageMargins(
            top.Set ? top.Px : null, right.Set ? right.Px : null,
            bottom.Set ? bottom.Px : null, left.Set ? left.Px : null);
    }

    private static void CollectRules(
        ImmutableArray<CssRule> rules, CssMediaContext media,
        ref Candidate top, ref Candidate right, ref Candidate bottom, ref Candidate left)
    {
        foreach (var rule in rules)
        {
            if (rule is not CssAtRule at) continue;

            if (string.Equals(at.Name, "media", StringComparison.OrdinalIgnoreCase))
            {
                if (media.Matches(at.Prelude)) // recurse only when the @media condition matches
                    CollectRules(at.ChildRules, media, ref top, ref right, ref bottom, ref left);
                continue;
            }

            if (!string.Equals(at.Name, "page", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(at.Prelude)) continue; // bare @page only this cycle.

            foreach (var decl in at.Declarations)
            {
                // CSS Page 3: left/right % relative to page WIDTH, top/bottom % to page HEIGHT.
                switch (decl.Property.ToLowerInvariant())
                {
                    case "margin-top": Apply(ref top, decl, media.ViewportHeightPx); break;
                    case "margin-bottom": Apply(ref bottom, decl, media.ViewportHeightPx); break;
                    case "margin-left": Apply(ref left, decl, media.ViewportWidthPx); break;
                    case "margin-right": Apply(ref right, decl, media.ViewportWidthPx); break;
                }
            }
        }
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
