// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Resolves CSS <c>font-size</c> (CSS Fonts 4 §3.4:
/// <c>&lt;absolute-size&gt; | &lt;relative-size&gt; | &lt;length-percentage&gt;</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two-phase, because some forms need the parent font-size.</b> The dispatch
/// (context-free) resolves the forms that DON'T need a parent: the
/// <c>&lt;absolute-size&gt;</c> keywords (<c>xx-small</c>…<c>xxx-large</c>, with
/// <c>medium</c> = 16px — the initial value) and absolute <c>&lt;length&gt;</c>
/// units (px / pt / cm / …), straight to a <c>LengthPx</c> slot. The
/// parent-relative forms — <c>em</c> / <c>ex</c> / <c>ch</c> / <c>%</c> and the
/// <c>&lt;relative-size&gt;</c> keywords <c>larger</c> / <c>smaller</c> — are
/// returned <see cref="ResolverResult.Deferred(string)"/> (raw text preserved) and
/// re-resolved during the box-builder's top-down walk against the parent's
/// resolved font-size via <see cref="TryResolveRelativeToParent"/>.
/// </para>
/// <para>
/// <b>Out of scope this cycle:</b> <c>rem</c> / <c>rlh</c> (root-relative — needs
/// the root font-size threaded through the walk) and viewport units stay deferred
/// (a documented follow-up); for now they fall back to the 16px default reader.
/// </para>
/// </remarks>
internal static class FontSizeResolver
{
    /// <summary>CSS <c>medium</c> — the initial font-size, 16px. The absolute-size
    /// keyword table is scaled relative to this.</summary>
    public const double MediumPx = 16.0;

    /// <summary>The <c>&lt;relative-size&gt;</c> step factor (<c>larger</c> /
    /// <c>smaller</c>) — the conventional 1.2 ratio.</summary>
    public const double RelativeStep = 1.2;

    public static ResolverResult Resolve(
        string value,
        PropertyId propertyId,
        string propertyName,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location)
    {
        // <absolute-size> keywords → absolute px (context-free).
        if (TryAbsoluteSizePx(value, out var absPx))
            return ResolverResult.Resolved(ComputedSlot.FromLengthPx(absPx));

        // <relative-size> + parent-relative units need the parent font-size — defer.
        if (IsParentRelative(value))
            return ResolverResult.Deferred(value);

        // Everything else (absolute lengths px/pt/…, and rem/viewport which
        // LengthResolver itself defers) goes through the shared length parser.
        // PropertyType.Length + FontSize in NonNegativeProperties means a negative
        // value is rejected; percentages were handled above (deferred).
        return LengthResolver.Resolve(
            value, PropertyType.Length, propertyId, propertyName, diagnostics, location);
    }

    /// <summary>
    /// Re-resolve a deferred <c>font-size</c> value against the parent's resolved
    /// font-size (px). Handles <c>em</c> / <c>ex</c> / <c>ch</c> / <c>%</c> and the
    /// <c>larger</c> / <c>smaller</c> keywords. Returns <see langword="false"/> for
    /// forms that are NOT parent-relative (e.g. <c>rem</c> / viewport units) so the
    /// caller leaves them deferred.
    /// </summary>
    public static bool TryResolveRelativeToParent(string rawText, double parentFontSizePx, out double px)
    {
        var v = rawText.Trim();
        px = 0;

        if (v.Equals("larger", StringComparison.OrdinalIgnoreCase)) { px = parentFontSizePx * RelativeStep; return true; }
        if (v.Equals("smaller", StringComparison.OrdinalIgnoreCase)) { px = parentFontSizePx / RelativeStep; return true; }

        if (v.EndsWith("%", StringComparison.Ordinal))
        {
            if (TryParseNumber(v[..^1], out var pct)) { px = parentFontSizePx * (pct / 100.0); return true; }
            return false;
        }

        // Font-relative length units that scale by the (parent, for font-size)
        // font-size. ex/ch are approximated as fractions of the em per CSS Values
        // §6.1.2 when no font metrics are available (0.5em / 0.5em respectively).
        if (TryStripUnit(v, "em", out var em) && TryParseNumber(em, out var emN)) { px = parentFontSizePx * emN; return true; }
        if (TryStripUnit(v, "ex", out var ex) && TryParseNumber(ex, out var exN)) { px = parentFontSizePx * exN * 0.5; return true; }
        if (TryStripUnit(v, "ch", out var ch) && TryParseNumber(ch, out var chN)) { px = parentFontSizePx * chN * 0.5; return true; }

        return false; // rem / viewport / unknown — leave deferred.
    }

    /// <summary><see langword="true"/> when a font-size value needs the parent
    /// font-size to resolve (so the dispatch defers it).</summary>
    private static bool IsParentRelative(string value)
    {
        var v = value.Trim();
        if (v.Equals("larger", StringComparison.OrdinalIgnoreCase)
            || v.Equals("smaller", StringComparison.OrdinalIgnoreCase))
            return true;
        if (v.EndsWith("%", StringComparison.Ordinal)) return true;
        return EndsWithUnit(v, "em") || EndsWithUnit(v, "ex") || EndsWithUnit(v, "ch");
    }

    private static bool TryAbsoluteSizePx(string value, out double px)
    {
        // CSS Fonts 4 §3.4 absolute-size scale, relative to medium = 16px:
        // 3/5, 3/4, 8/9, 1, 6/5, 3/2, 2/1, 3/1.
        px = value.Trim().ToLowerInvariant() switch
        {
            "xx-small" => MediumPx * 3.0 / 5.0,
            "x-small" => MediumPx * 3.0 / 4.0,
            "small" => MediumPx * 8.0 / 9.0,
            "medium" => MediumPx,
            "large" => MediumPx * 6.0 / 5.0,
            "x-large" => MediumPx * 3.0 / 2.0,
            "xx-large" => MediumPx * 2.0,
            "xxx-large" => MediumPx * 3.0,
            _ => double.NaN,
        };
        return !double.IsNaN(px);
    }

    private static bool EndsWithUnit(string v, string unit) =>
        v.Length > unit.Length && v.EndsWith(unit, StringComparison.OrdinalIgnoreCase);

    private static bool TryStripUnit(string v, string unit, out string number)
    {
        if (EndsWithUnit(v, unit)) { number = v[..^unit.Length]; return true; }
        number = string.Empty;
        return false;
    }

    private static bool TryParseNumber(string s, out double n) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out n)
        && double.IsFinite(n);
}
