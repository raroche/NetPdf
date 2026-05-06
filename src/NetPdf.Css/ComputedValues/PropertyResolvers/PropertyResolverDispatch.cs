// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Single entry point for converting one property's resolved value text (post-<c>var()</c>
/// substitution + post-<c>calc()</c> reduction) into a typed <see cref="ComputedSlot"/>.
/// Dispatches to a per-<see cref="PropertyType"/> resolver. The cycle-1 surface covers the
/// four highest-impact families: length / color / number / keyword.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pipeline position.</b> Runs after <c>VarResolver</c> (Task 8) +
/// <c>CalcResolver</c> (Task 9). The input is the post-resolution value text from
/// <c>ResolvedDeclaration.ResolvedValue</c>; the output is the typed slot the
/// <c>BoxBuilder</c> (Task 12) writes into a per-element <see cref="ComputedStyle"/>.
/// </para>
/// <para>
/// <b>Failure shape.</b> When the value text cannot be parsed into the property's
/// type, the resolver emits <see cref="CssDiagnosticCodes.CssPropertyValueInvalid001"/>
/// (Warning) and returns <see cref="ComputedSlot.Unset"/>. The cascade-level "invalid
/// at computed value time" rule then applies — the property's initial value (or
/// inherited value for inherited properties) is used by downstream stages.
/// </para>
/// <para>
/// <b>Cycle 1 deferred surface.</b> Property types not yet wired return
/// <see cref="ComputedSlot.Unset"/> with no diagnostic — they're intentionally
/// out-of-scope, not invalid. Cycle 2 will add: <c>FontFamilyList</c>,
/// <c>FontWeight</c>, <c>FontSize</c>, <c>LineHeight</c>, <c>LineWidth</c>,
/// <c>FlexBasis</c>, <c>VerticalAlign</c>, <c>Content</c>, <c>Url</c>, <c>String</c>,
/// <c>Time</c>, <c>Angle</c>, <c>Resolution</c>.
/// </para>
/// </remarks>
internal static class PropertyResolverDispatch
{
    /// <summary>Resolve <paramref name="resolvedValue"/> into a typed slot for
    /// the property identified by <paramref name="propertyId"/>.</summary>
    /// <param name="propertyId">The property whose value is being resolved — drives
    /// the dispatch via its <see cref="PropertyMeta.Type"/>.</param>
    /// <param name="resolvedValue">The post-<c>var()</c>, post-<c>calc()</c> value
    /// text. Whitespace at the edges is tolerated.</param>
    /// <param name="diagnostics">Sink for parse failures. <see langword="null"/> is
    /// allowed — failures are still observable through the returned
    /// <see cref="ComputedSlot.Unset"/>.</param>
    /// <param name="location">Source location attached to any emitted diagnostic.</param>
    public static ComputedSlot Resolve(
        PropertyId propertyId,
        string resolvedValue,
        ICssDiagnosticsSink? diagnostics = null,
        CssSourceLocation location = default)
    {
        ArgumentNullException.ThrowIfNull(resolvedValue);

        var idx = (int)propertyId;
        if (idx < 0 || idx >= PropertyMetadata.Count) return ComputedSlot.Unset;

        var meta = PropertyMetadata.Table[idx];
        var trimmed = resolvedValue.AsSpan().Trim().ToString();
        if (trimmed.Length == 0) return ComputedSlot.Unset;

        return meta.Type switch
        {
            PropertyType.Color => ColorResolver.Resolve(
                trimmed, propertyId, meta.Name, diagnostics, location),

            // The dimension family — all five share LengthResolver's parser, with
            // per-type acceptance rules (auto / none / normal / percentage).
            PropertyType.Length => LengthResolver.Resolve(
                trimmed, meta.Type, propertyId, meta.Name, diagnostics, location),
            PropertyType.LengthPercentage => LengthResolver.Resolve(
                trimmed, meta.Type, propertyId, meta.Name, diagnostics, location),
            PropertyType.LengthPercentageAuto => LengthResolver.Resolve(
                trimmed, meta.Type, propertyId, meta.Name, diagnostics, location),
            PropertyType.Percentage => LengthResolver.Resolve(
                trimmed, meta.Type, propertyId, meta.Name, diagnostics, location),
            PropertyType.TextSpacing => LengthResolver.Resolve(
                trimmed, meta.Type, propertyId, meta.Name, diagnostics, location),

            PropertyType.Number => NumberResolver.ResolveNumber(
                trimmed, propertyId, meta.Name, diagnostics, location),
            PropertyType.Integer => NumberResolver.ResolveInteger(
                trimmed, propertyId, meta.Name, diagnostics, location),

            PropertyType.Keyword => KeywordResolver.Resolve(
                trimmed, propertyId, meta.Name, diagnostics, location),

            // Cycle 1 deliberately leaves the specialized union types unresolved.
            // Cycle 2 will wire each one. Returning Unset (no diagnostic) lets the
            // cascade fall through to initial / inherited as the spec requires for
            // missing computed values.
            _ => ComputedSlot.Unset,
        };
    }
}
