// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Single entry point for converting one property's resolved value text (post-<c>var()</c>
/// substitution + post-<c>calc()</c> reduction) into a <see cref="ResolverResult"/>.
/// Dispatches to a per-<see cref="PropertyType"/> resolver. The cycle-1 surface covers
/// the four highest-impact families: length / color / number / keyword.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pipeline position.</b> Runs after <c>VarResolver</c> (Task 8) +
/// <c>CalcResolver</c> (Task 9). Output is a structured <see cref="ResolverResult"/>
/// — see its docs for the three-way Resolved / Deferred / Invalid contract.
/// </para>
/// <para>
/// <b>Cycle 2 unsupported PropertyTypes.</b> Property types not yet wired return
/// <see cref="ResolverResult.UnsupportedUnvalidated"/> (carrying the raw text for
/// re-resolution), NOT <see cref="ResolverResult.Deferred"/> and NOT
/// <see cref="ResolverResult.Invalid"/>. The 4-state distinction matters per the
/// Task 10 hardening review: <see cref="ResolutionState.Deferred"/> means "the
/// resolver validated the value against the property's grammar but cannot reduce
/// without context"; <see cref="ResolutionState.UnsupportedUnvalidated"/> means
/// "no resolver wired yet — raw text passed through unchecked, cycle-2 work
/// surface". Treating them identically would silently let typos through for
/// cycle-2 PropertyTypes. Cycle 2 wires: <c>FontFamilyList</c>, <c>FontWeight</c>,
/// <c>FontSize</c>, <c>LineHeight</c>, <c>LineWidth</c>, <c>FlexBasis</c>,
/// <c>VerticalAlign</c>, <c>Content</c>, <c>Url</c>, <c>String</c>, <c>Time</c>,
/// <c>Angle</c>, <c>Resolution</c>.
/// </para>
/// </remarks>
internal static class PropertyResolverDispatch
{
    /// <summary>Resolve <paramref name="resolvedValue"/> into a typed result for
    /// the property identified by <paramref name="propertyId"/>.</summary>
    /// <param name="propertyId">The property whose value is being resolved — drives
    /// the dispatch via its <see cref="PropertyMeta.Type"/>.</param>
    /// <param name="resolvedValue">The post-<c>var()</c>, post-<c>calc()</c> value
    /// text. Whitespace at the edges is tolerated.</param>
    /// <param name="diagnostics">Sink for parse failures. <see langword="null"/> is
    /// allowed — failures are still observable through the returned
    /// <see cref="ResolverResult.State"/>.</param>
    /// <param name="location">Source location attached to any emitted diagnostic.</param>
    public static ResolverResult Resolve(
        PropertyId propertyId,
        string resolvedValue,
        ICssDiagnosticsSink? diagnostics = null,
        CssSourceLocation location = default)
    {
        ArgumentNullException.ThrowIfNull(resolvedValue);

        var idx = (int)propertyId;
        if (idx < 0 || idx >= PropertyMetadata.Count)
        {
            // Unknown PropertyId — nothing to do. Treat as Invalid (the cascade
            // shouldn't have given us this in the first place).
            return ResolverResult.Invalid();
        }

        var meta = PropertyMetadata.Table[idx];
        var trimmed = resolvedValue.AsSpan().Trim().ToString();
        if (trimmed.Length == 0)
        {
            // Empty value text → Invalid (CSS treats empty declaration values as
            // parse errors that the cascade discards).
            return ResolverResult.Invalid();
        }

        return meta.Type switch
        {
            PropertyType.Color => ColorResolver.Resolve(
                trimmed, propertyId, meta.Name, diagnostics, location),

            // The dimension family — all seven share LengthResolver's parser, with
            // per-type acceptance rules (auto / none / normal / percentage) plus
            // per-PropertyId rules (letter-spacing rejects %, padding/width/etc.
            // reject negatives). Per Phase 3 Task 15 L8 the FlexBasis type
            // joined the family: it accepts `auto` (KeywordIdAuto = 0) /
            // `content` (KeywordIdContent = 1) / `<length-percentage>` per
            // CSS Flexbox L1 §7.2. Per Phase 3 Task 15 L12 the MaxSize type
            // joined the family (= the seventh member): it accepts `none`
            // (= no upper bound, KeywordIdNone = 0) / `<length-percentage>`
            // per CSS Sizing L3 §5.2 — used by `max-width` / `max-height`
            // for the §9.7 step-4 min/max clamping iteration in the
            // FlexLayouter.
            PropertyType.Length or
            PropertyType.LengthPercentage or
            PropertyType.LengthPercentageAuto or
            PropertyType.Percentage or
            PropertyType.TextSpacing or
            PropertyType.FlexBasis or
            PropertyType.MaxSize => LengthResolver.Resolve(
                trimmed, meta.Type, propertyId, meta.Name, diagnostics, location),

            PropertyType.Number => NumberResolver.ResolveNumber(
                trimmed, propertyId, meta.Name, diagnostics, location),
            PropertyType.Integer => NumberResolver.ResolveInteger(
                trimmed, propertyId, meta.Name, diagnostics, location),

            PropertyType.Keyword => KeywordResolver.Resolve(
                trimmed, propertyId, meta.Name, diagnostics, location),

            // Cycle-2 PropertyTypes — return UnsupportedUnvalidated (NOT Deferred)
            // because the dispatch hasn't validated the value text against the
            // property's grammar; a typo would silently pass through. Distinguishing
            // the two states lets cycle-2 audits find the work surface.
            _ => ResolverResult.UnsupportedUnvalidated(trimmed),
        };
    }
}
