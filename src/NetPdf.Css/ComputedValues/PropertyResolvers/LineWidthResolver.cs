// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Resolves the <c>&lt;line-width&gt;</c> value type (CSS Backgrounds &amp; Borders 3
/// §4.2): <c>thin | medium | thick | &lt;length&gt;</c> — used by
/// <c>border-*-width</c> and <c>column-rule-width</c>. The keywords resolve to
/// their conventional pixel values (thin = 1px, medium = 3px, thick = 5px); a
/// <c>&lt;length&gt;</c> delegates to <see cref="LengthResolver"/> (which converts
/// units to px, rejects percentages for <see cref="PropertyType.Length"/>, and
/// rejects negatives because the LineWidth properties are registered in
/// <see cref="NonNegativeProperties"/>). The result is a <c>LengthPx</c>
/// <see cref="ComputedSlot"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Style gating is a used-value concern, applied downstream.</b> Per §4.3 the
/// used border width is 0 when the corresponding <c>border-*-style</c> is
/// <c>none</c> / <c>hidden</c>. That rule depends on a sibling property, so it is
/// NOT applied here (single-property resolution) — the layout reader
/// (<c>ComputedStyleLayoutExtensions.ReadLengthPxOrZero</c>) and the paint bridge
/// gate the width by style. Resolving the width to its nominal px keeps this
/// resolver pure + context-free.
/// </para>
/// </remarks>
internal static class LineWidthResolver
{
    /// <summary>CSS <c>thin</c> in px (CSS B&amp;B 3 §4.2 conventional value).</summary>
    public const double ThinPx = 1.0;

    /// <summary>CSS <c>medium</c> in px — the initial value of <c>border-*-width</c>.</summary>
    public const double MediumPx = 3.0;

    /// <summary>CSS <c>thick</c> in px.</summary>
    public const double ThickPx = 5.0;

    public static ResolverResult Resolve(
        string value,
        PropertyId propertyId,
        string propertyName,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location)
    {
        if (TryKeywordPx(value, out var px))
            return ResolverResult.Resolved(ComputedSlot.FromLengthPx(px));

        // <length> — reuse the shared length parser. PropertyType.Length rejects
        // percentages; NonNegativeProperties (which now includes the line-width
        // properties) makes LengthResolver reject negatives with
        // CSS-PROPERTY-VALUE-INVALID-001.
        return LengthResolver.Resolve(
            value, PropertyType.Length, propertyId, propertyName, diagnostics, location);
    }

    private static bool TryKeywordPx(string value, out double px)
    {
        if (value.Equals("medium", StringComparison.OrdinalIgnoreCase)) { px = MediumPx; return true; }
        if (value.Equals("thin", StringComparison.OrdinalIgnoreCase)) { px = ThinPx; return true; }
        if (value.Equals("thick", StringComparison.OrdinalIgnoreCase)) { px = ThickPx; return true; }
        px = 0;
        return false;
    }
}
