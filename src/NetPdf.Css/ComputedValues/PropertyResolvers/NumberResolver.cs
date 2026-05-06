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
/// (32-bit signed integer, e.g., <c>z-index: 5</c>). Both reject NaN/±Infinity/scientific
/// notation that overflows the target storage; both reject anything with a unit suffix.
/// </summary>
internal static class NumberResolver
{
    public static ComputedSlot ResolveNumber(
        string value,
        PropertyId propertyId,
        string propertyName,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            || double.IsNaN(n) || double.IsInfinity(n))
        {
            Emit(diagnostics, propertyName, value,
                "expected a unitless number (no unit suffix; finite real)", location);
            return ComputedSlot.Unset;
        }
        return ComputedSlot.FromNumber(n);
    }

    public static ComputedSlot ResolveInteger(
        string value,
        PropertyId propertyId,
        string propertyName,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location)
    {
        // CSS <integer> production allows a leading `+` or `-` plus ASCII digits.
        // Reject decimal points and exponents — those land in <number>.
        if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
        {
            Emit(diagnostics, propertyName, value,
                "expected a signed 32-bit integer (no unit, no decimal, no exponent)", location);
            return ComputedSlot.Unset;
        }
        return ComputedSlot.FromInteger(n);
    }

    private static void Emit(
        ICssDiagnosticsSink? sink, string propertyName, string value, string reason,
        CssSourceLocation location)
    {
        sink?.Emit(new CssDiagnostic(
            CssDiagnosticCodes.CssPropertyValueInvalid001,
            $"Could not parse '{propertyName}: {value}' — {reason}.",
            CssDiagnosticSeverity.Warning,
            location));
    }
}
