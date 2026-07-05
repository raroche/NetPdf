// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Resolves the CSS <c>line-height</c> value (CSS 2.2 §10.8.1 / CSS Inline 3):
/// <c>normal | &lt;number&gt; | &lt;length&gt; | &lt;percentage&gt;</c>.
/// <list type="bullet">
///   <item><c>normal</c> → a <c>Keyword</c> slot (id <see cref="Normal"/> = 0); the layout readers
///   treat any non-numeric slot as "use font-size × 1.2" (the UA default).</item>
///   <item>A unitless <c>&lt;number&gt;</c> (≥ 0) → a <c>Number</c> slot — the multiplier. The used
///   line-height is number × the element's OWN font-size; the COMPUTED value is the number itself, so a
///   child inherits the number and re-multiplies by ITS font-size (CSS Inline 3 §4.2).</item>
///   <item><c>&lt;length&gt;</c> / <c>&lt;percentage&gt;</c> → delegated to <see cref="LengthResolver"/>
///   as a <see cref="PropertyType.LengthPercentage"/>: an absolute length → <c>LengthPx</c>; a
///   font-/viewport-relative length (<c>em</c>/<c>rem</c>/<c>vw</c>…) → Deferred raw (the box-builder's
///   <c>DeferredLengthResolver</c> folds it against font-size); a percentage → a <c>Percentage</c> slot
///   (used = % × font-size).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>Pre-fix <c>line-height</c> was UNWIRED in <see cref="PropertyResolverDispatch"/> — every value
/// fell through to <c>UnsupportedUnvalidated</c>, so a declared <c>line-height: 24px</c> never reached
/// the computed style and layout/paint fell back to font-size × 1.2 regardless.</para>
/// <para><b>Non-negative.</b> <c>line-height</c> is a non-negative property
/// (<c>NonNegativeProperties</c>): a negative <c>&lt;number&gt;</c> is rejected here (Invalid), a
/// negative <c>&lt;length&gt;</c> by <see cref="LengthResolver"/>.</para>
/// </remarks>
internal static class LineHeightResolver
{
    /// <summary>The <c>normal</c> keyword id — also the fallback meaning of any non-numeric slot.</summary>
    public const int Normal = 0;

    public static ResolverResult Resolve(
        string value,
        PropertyId propertyId,
        string propertyName,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location)
    {
        var trimmed = value.Trim();

        // `normal` — the initial; a Keyword(0) slot the readers map to font-size × 1.2.
        if (trimmed.Equals("normal", StringComparison.OrdinalIgnoreCase))
            return ResolverResult.Resolved(ComputedSlot.FromKeyword(Normal));

        // Note: CSS-wide keywords (initial / inherit / unset / …) never reach here — they are
        // intercepted centrally in PropertyResolverDispatch before the per-type dispatch, so
        // `line-height: initial` resolves to `normal` (this property's initial) and
        // `line-height: inherit` falls through the cascade's inherited-value path.

        // A unitless <number> (the multiplier) — distinguished from a <length> by carrying no unit and
        // no `%`. A non-negative number → a Number slot; a negative one is invalid (line-height ≥ 0).
        if (IsUnitlessNumber(trimmed))
        {
            // double.IsFinite guards the overflow path — `line-height: 1e309` parses to +Infinity, which
            // is `>= 0` but would THROW from ComputedSlot.FromNumber; reject it as invalid CSS instead
            // (matching NumberResolver's finite check) so untrusted input never crashes the pipeline.
            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && number >= 0 && double.IsFinite(number))
            {
                return ResolverResult.Resolved(ComputedSlot.FromNumber(number));
            }
            var safe = DiagnosticTextSanitizer.Sanitize(value);
            diagnostics?.Emit(new CssDiagnostic(
                CssDiagnosticCodes.CssPropertyValueInvalid001,
                $"'{propertyName}: {safe}' — line-height must be a non-negative number.",
                CssDiagnosticSeverity.Warning,
                location));
            return ResolverResult.Invalid();
        }

        // <length> / <percentage> — the dimension grammar; line-height is non-negative so a negative
        // literal is rejected on the unit path (NonNegativeProperties). An absolute length → LengthPx,
        // em/rem/vw → Deferred raw (folded by DeferredLengthResolver), a percentage → a Percentage slot.
        return LengthResolver.Resolve(
            trimmed, PropertyType.LengthPercentage, propertyId, propertyName, diagnostics, location);
    }

    /// <summary>True when <paramref name="value"/> is a bare number (digits / sign / decimal point /
    /// exponent) with NO unit and NO <c>%</c> — so a <c>&lt;length&gt;</c> (<c>24px</c>) or a
    /// <c>&lt;percentage&gt;</c> (<c>150%</c>) falls through to <see cref="LengthResolver"/>.</summary>
    private static bool IsUnitlessNumber(string value)
    {
        if (value.Length == 0) return false;
        foreach (var c in value)
        {
            var numeric = (c >= '0' && c <= '9') || c == '.' || c == '+' || c == '-' || c == 'e' || c == 'E';
            if (!numeric) return false;
        }
        return true;
    }
}
