// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Resolves CSS <c>font-weight</c> (CSS Fonts 4 §2.2:
/// <c>normal | bold | bolder | lighter | &lt;number [1,1000]&gt;</c>) to an integer
/// <see cref="ComputedSlot"/>. <c>normal</c> = 400, <c>bold</c> = 700.
/// </summary>
/// <remarks>
/// <c>bolder</c> / <c>lighter</c> are PARENT-relative (CSS Fonts 4 §2.2.1), so the
/// dispatch returns them <see cref="ResolverResult.Deferred(string)"/> and the
/// box-builder walk re-resolves them against the parent weight via
/// <see cref="TryResolveRelativeToParent"/>.
/// </remarks>
internal static class FontWeightResolver
{
    public const int Normal = 400;
    public const int Bold = 700;
    private const int MinWeight = 1;
    private const int MaxWeight = 1000;

    public static ResolverResult Resolve(
        string value,
        PropertyId propertyId,
        string propertyName,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location)
    {
        if (value.Equals("normal", StringComparison.OrdinalIgnoreCase))
            return ResolverResult.Resolved(ComputedSlot.FromInteger(Normal));
        if (value.Equals("bold", StringComparison.OrdinalIgnoreCase))
            return ResolverResult.Resolved(ComputedSlot.FromInteger(Bold));
        if (value.Equals("bolder", StringComparison.OrdinalIgnoreCase)
            || value.Equals("lighter", StringComparison.OrdinalIgnoreCase))
            return ResolverResult.Deferred(value);

        if (double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.IsFinite(n) && n >= MinWeight && n <= MaxWeight)
        {
            // CSS Fonts 4 admits a fractional <number> (variable fonts); the
            // weight the shaper queries is an integer, so round to nearest.
            return ResolverResult.Resolved(ComputedSlot.FromInteger((int)Math.Round(n)));
        }

        diagnostics?.Emit(new CssDiagnostic(
            CssDiagnosticCodes.CssPropertyValueInvalid001,
            $"Could not parse '{propertyName}: {DiagnosticTextSanitizer.Sanitize(value)}' — expected " +
            "normal | bold | bolder | lighter | <number 1-1000>.",
            CssDiagnosticSeverity.Warning,
            location));
        return ResolverResult.Invalid();
    }

    /// <summary>
    /// Re-resolve a deferred <c>bolder</c> / <c>lighter</c> against the parent's
    /// computed weight, per the CSS Fonts 4 §2.2.1 range table. Returns
    /// <see langword="false"/> for any other value.
    /// </summary>
    public static bool TryResolveRelativeToParent(string rawText, int parentWeight, out int weight)
    {
        var v = rawText.Trim();
        if (v.Equals("bolder", StringComparison.OrdinalIgnoreCase))
        {
            weight = parentWeight < 350 ? 400 : parentWeight < 550 ? 700 : 900;
            return true;
        }
        if (v.Equals("lighter", StringComparison.OrdinalIgnoreCase))
        {
            weight = parentWeight < 100 ? parentWeight
                : parentWeight < 550 ? 100
                : parentWeight < 750 ? 400
                : 700;
            return true;
        }
        weight = Normal;
        return false;
    }
}
