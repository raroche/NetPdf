// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Parser;

namespace NetPdf.Css.PagedMedia;

/// <summary>
/// Resolves the page margins declared by bare <c>@page { margin… }</c> rules into absolute px,
/// per CSS Paged Media L3 §3. Phase 3 Task 21 cycle 1 — <b>margins only</b>.
/// </summary>
/// <remarks>
/// <para>
/// Only the BARE <c>@page</c> rule (no <c>:first</c> / <c>:left</c> / <c>:right</c> / named
/// selector) is honored in this cycle; selector-scoped page rules are a later cycle. Multiple
/// bare rules merge per side with the LAST declaration winning (source order — a simplification
/// of the full <c>@page</c> cascade). A side with no resolvable margin declaration stays
/// <see langword="null"/> so the caller keeps its own default for that side.
/// </para>
/// <para>
/// Reads the <c>margin-top</c> / <c>margin-right</c> / <c>margin-bottom</c> / <c>margin-left</c>
/// longhands AngleSharp.Css expands the <c>margin</c> shorthand into. Only ABSOLUTE lengths
/// resolve (CSS Values L4 §6.1, via <see cref="LengthResolver.TryAbsoluteUnitToPx"/>);
/// percentages / <c>calc()</c> / font- or viewport-relative units leave the side unspecified
/// (a tracked refinement). <c>@page { size }</c> is NOT handled here — AngleSharp.Css drops the
/// <c>size</c> descriptor, so it needs pre-pass recovery in a follow-up cycle.
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

    /// <summary>Walk the document's stylesheets and resolve the effective bare-<c>@page</c>
    /// margins. Returns all-null when no bare <c>@page</c> declares a resolvable margin.</summary>
    public static ResolvedPageMargins Resolve(IEnumerable<CssStylesheet> sheets)
    {
        ArgumentNullException.ThrowIfNull(sheets);

        double? top = null, right = null, bottom = null, left = null;
        foreach (var sheet in sheets)
        {
            foreach (var rule in sheet.Rules)
            {
                if (rule is not CssAtRule at) continue;
                if (!string.Equals(at.Name, "page", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(at.Prelude)) continue; // bare @page only this cycle.

                foreach (var decl in at.Declarations)
                {
                    if (!TryParseAbsoluteLengthPx(decl.Value.RawText, out var px)) continue;
                    switch (decl.Property.ToLowerInvariant())
                    {
                        case "margin-top": top = px; break;
                        case "margin-right": right = px; break;
                        case "margin-bottom": bottom = px; break;
                        case "margin-left": left = px; break;
                    }
                }
            }
        }
        return new ResolvedPageMargins(top, right, bottom, left);
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
}
