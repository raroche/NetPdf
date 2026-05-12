// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Resolves CSS unitless numbers per CSS Values L4 §5: <see cref="PropertyType.Number"/>
/// (any finite real, e.g., <c>flex-grow: 0.5</c>) and <see cref="PropertyType.Integer"/>
/// (32-bit signed integer, e.g., <c>z-index: 5</c>). Both reject NaN/±Infinity, both
/// reject anything with a unit suffix, both reject scientific notation that overflows
/// the target storage. Per-property non-negativity per
/// <see cref="NonNegativeProperties"/> (e.g., <c>flex-grow</c> + <c>flex-shrink</c>
/// must be ≥ 0 per CSS Flexbox 1 §7.1).
/// </summary>
internal static class NumberResolver
{
    public static ResolverResult ResolveNumber(
        string value,
        PropertyId propertyId,
        string propertyName,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            || !double.IsFinite(n))
        {
            Emit(diagnostics, propertyName, value,
                "expected a unitless number (no unit suffix; finite real)", location);
            return ResolverResult.Invalid();
        }
        if (n < 0 && NonNegativeProperties.IsRequired(propertyId))
        {
            Emit(diagnostics, propertyName, value,
                "negative value not allowed for this property", location);
            return ResolverResult.Invalid();
        }
        return ResolverResult.Resolved(ComputedSlot.FromNumber(n));
    }

    public static ResolverResult ResolveInteger(
        string value,
        PropertyId propertyId,
        string propertyName,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location)
    {
        // Per Phase 3 Task 14 cycle 1 — column-count admits <c>auto</c>
        // as a keyword (CSS Multi-column L1 §3.2 — "the number of
        // columns is determined by column-width"). v1 stores this as
        // a Keyword slot so the multicol-layouter dispatch's
        // <c>ReadColumnCount</c> extension can distinguish "auto"
        // from a positive integer + skip the multicol path. Other
        // Integer-typed properties don't currently admit a keyword;
        // when a future property does, expand this gate or refactor
        // into a per-property table.
        if (propertyId == PropertyId.ColumnCount
            && value.Equals("auto", System.StringComparison.OrdinalIgnoreCase))
        {
            return ResolverResult.Resolved(ComputedSlot.FromKeyword(0));
        }

        if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
        {
            Emit(diagnostics, propertyName, value,
                "expected a signed 32-bit integer (no unit, no decimal, no exponent)", location);
            return ResolverResult.Invalid();
        }
        if (n < 0 && NonNegativeProperties.IsRequired(propertyId))
        {
            Emit(diagnostics, propertyName, value,
                "negative value not allowed for this property", location);
            return ResolverResult.Invalid();
        }
        return ResolverResult.Resolved(ComputedSlot.FromInteger(n));
    }

    private static void Emit(
        ICssDiagnosticsSink? sink, string propertyName, string value, string reason,
        CssSourceLocation location)
    {
        // Per Phase A A-6 — sanitize untrusted value + reason before message
        // interpolation. Property names are generator-validated (frozen-set
        // lookup), so they're trusted. Reason is internal but is sanitized
        // defensively so a future change can't accidentally interpolate raw
        // input into it without picking up the protection.
        var safeValue = DiagnosticTextSanitizer.Sanitize(value);
        var safeReason = DiagnosticTextSanitizer.Sanitize(reason);
        sink?.Emit(new CssDiagnostic(
            CssDiagnosticCodes.CssPropertyValueInvalid001,
            $"Could not parse '{propertyName}: {safeValue}' — {safeReason}.",
            CssDiagnosticSeverity.Warning,
            location));
    }
}
