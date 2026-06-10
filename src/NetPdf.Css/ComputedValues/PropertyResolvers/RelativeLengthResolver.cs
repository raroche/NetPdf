// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Resolves a DEFERRED font-/viewport-relative <c>&lt;length&gt;</c> (a raw the cascade's
/// <c>LengthResolver</c> couldn't resolve context-free) to used px, given the contexts it scales by:
/// <list type="bullet">
///   <item><c>em</c> / <c>ex</c> / <c>ch</c> — the owning element's resolved font-size
///   (<c>ex</c>/<c>ch</c> approximated as <c>0.5em</c> per CSS Values 4 §6.1.2 when no font metrics
///   are available — the same approximation <c>FontSizeResolver</c> uses, so font-size and box
///   lengths scale identically).</item>
///   <item><c>rem</c> — the root element's resolved font-size (CSS Values 4 §6.1.3).</item>
///   <item><c>vw</c> / <c>vh</c> / <c>vmin</c> / <c>vmax</c> — 1% of the viewport width / height /
///   smaller / larger dimension (CSS Values 4 §6.2). For paged media the viewport is the PAGE box
///   (CSS Paged Media §5.1 maps the initial containing block to the page area; we use the page box —
///   a documented approximation consistent across the engine).</item>
/// </list>
/// First consumer: the <c>@page</c> margin-box explicit <c>width</c>/<c>height</c>
/// (relative-units cycle — <c>PageMarginBoxPainter</c>). <c>calc()</c> + anything else stays
/// unsupported here (<see cref="IsSupported"/> returns <see langword="false"/> → callers keep their
/// diagnose-and-drop path).
/// </summary>
internal static class RelativeLengthResolver
{
    /// <summary>The ex/ch → em approximation factor (CSS Values 4 §6.1.2 fallback; mirrors
    /// <c>FontSizeResolver</c>).</summary>
    private const double ExChEmFactor = 0.5;

    /// <summary>Whether <paramref name="rawText"/> is a relative length this resolver can resolve —
    /// a non-negative number with an <c>em</c>/<c>ex</c>/<c>ch</c>/<c>rem</c>/<c>vw</c>/<c>vh</c>/
    /// <c>vmin</c>/<c>vmax</c> unit. <c>calc()</c>, container units, negatives, and malformed values
    /// are NOT supported (the caller diagnoses + drops them). NOTE this check is SYNTACTIC:
    /// <see cref="TryResolve"/> can still fail IN CONTEXT when the product overflows to a non-finite
    /// value (e.g. <c>1e308em</c> × a 16px base) — a caller that keeps a value because it
    /// IsSupported must handle (and surface) that contextual failure rather than fall back silently
    /// (post-PR-#156 review P2; the margin-box painter diagnoses it).</summary>
    public static bool IsSupported(string rawText) =>
        TryClassify(rawText, out _, out var n) && n >= 0;

    /// <summary>Resolve <paramref name="rawText"/> to used px against the given contexts. Returns
    /// <see langword="false"/> for an unsupported/malformed/negative value or a non-finite result
    /// (the caller falls back). All context values are CSS px.</summary>
    /// <param name="rawText">The deferred raw, e.g. <c>"10em"</c> / <c>"50vw"</c>.</param>
    /// <param name="emBasePx">The owning element's resolved font-size (the <c>em</c> base).</param>
    /// <param name="rootEmPx">The root element's resolved font-size (the <c>rem</c> base).</param>
    /// <param name="viewportWidthPx">The viewport (page box) width — the <c>vw</c> base × 100.</param>
    /// <param name="viewportHeightPx">The viewport (page box) height — the <c>vh</c> base × 100.</param>
    /// <param name="px">The resolved used length (≥ 0) when supported.</param>
    public static bool TryResolve(
        string rawText, double emBasePx, double rootEmPx,
        double viewportWidthPx, double viewportHeightPx, out double px)
    {
        px = 0;
        if (!TryClassify(rawText, out var unit, out var n) || n < 0) return false;

        var value = unit switch
        {
            RelativeUnit.Em => n * emBasePx,
            RelativeUnit.Ex or RelativeUnit.Ch => n * emBasePx * ExChEmFactor,
            RelativeUnit.Rem => n * rootEmPx,
            RelativeUnit.Vw => n / 100.0 * viewportWidthPx,
            RelativeUnit.Vh => n / 100.0 * viewportHeightPx,
            RelativeUnit.Vmin => n / 100.0 * Math.Min(viewportWidthPx, viewportHeightPx),
            _ => n / 100.0 * Math.Max(viewportWidthPx, viewportHeightPx), // Vmax
        };
        if (!double.IsFinite(value) || value < 0) return false;
        px = value;
        return true;
    }

    private enum RelativeUnit { Em, Ex, Ch, Rem, Vw, Vh, Vmin, Vmax }

    /// <summary>Parse <c>&lt;number&gt;&lt;unit&gt;</c> with a supported relative unit. Longer unit
    /// names are tried first so <c>"1.5rem"</c> never mis-strips as <c>em</c>.</summary>
    private static bool TryClassify(string rawText, out RelativeUnit unit, out double number)
    {
        unit = RelativeUnit.Em;
        number = 0;
        if (string.IsNullOrWhiteSpace(rawText)) return false;
        var v = rawText.Trim();

        // Longest-suffix-first: rem before em; vmin/vmax before vw/vh (no shared suffix, but the
        // fixed order keeps the scan deterministic and trivially auditable).
        ReadOnlySpan<(string Suffix, RelativeUnit Unit)> units =
        [
            ("vmin", RelativeUnit.Vmin), ("vmax", RelativeUnit.Vmax),
            ("rem", RelativeUnit.Rem),
            ("em", RelativeUnit.Em), ("ex", RelativeUnit.Ex), ("ch", RelativeUnit.Ch),
            ("vw", RelativeUnit.Vw), ("vh", RelativeUnit.Vh),
        ];
        foreach (var (suffix, u) in units)
        {
            if (v.Length <= suffix.Length
                || !v.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var numberText = v[..^suffix.Length].Trim();
            if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                || !double.IsFinite(n))
                return false; // right unit, malformed number — not supported (no other unit can match).
            unit = u;
            number = n;
            return true;
        }
        return false;
    }
}
