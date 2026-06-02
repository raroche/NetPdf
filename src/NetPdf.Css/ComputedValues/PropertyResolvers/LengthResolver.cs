// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Resolves CSS dimension values per CSS Values L4 §6 (lengths) and §7 (percentages)
/// for the dimension family of <see cref="PropertyType"/>:
/// <see cref="PropertyType.Length"/>, <see cref="PropertyType.LengthPercentage"/>,
/// <see cref="PropertyType.LengthPercentageAuto"/>, <see cref="PropertyType.Percentage"/>,
/// <see cref="PropertyType.TextSpacing"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolved</b>: absolute lengths (<c>px</c>, <c>in</c>, <c>cm</c>, <c>mm</c>,
/// <c>pt</c>, <c>pc</c>, <c>q</c>) into pixels per the CSS Values L4 §6.1
/// conversion table; percentages into a fixed-point payload; the unitless zero
/// (<c>0</c> alone is a valid length per §6.2). Type-specific keywords reduce to
/// per-property keyword ids: <c>auto</c> for <see cref="PropertyType.LengthPercentageAuto"/>,
/// <c>normal</c> for <see cref="PropertyType.TextSpacing"/>.
/// </para>
/// <para>
/// <b>Deferred</b> (returns <see cref="ResolverResult.Deferred"/> with the original
/// text — no diagnostic): font-relative units (<c>em</c>, <c>rem</c>, <c>ch</c>,
/// <c>ex</c>, <c>lh</c>, <c>rlh</c>, <c>cap</c>, <c>ic</c>), viewport-relative units
/// (<c>vw</c>/<c>vh</c>/<c>svw</c>/<c>lvw</c>/<c>dvw</c>/<c>vmin</c>/<c>vmax</c>/etc.),
/// container-relative units (<c>cqw</c>/<c>cqh</c>/<c>cqi</c>/<c>cqb</c>/<c>cqmin</c>/<c>cqmax</c>).
/// These need a per-element font / viewport / container context that the cascade
/// stage doesn't have — Phase 3 layout finalizes them.
/// </para>
/// <para>
/// <b>Invalid</b> (returns <see cref="ResolverResult.Invalid"/> + emits
/// <see cref="CssDiagnosticCodes.CssPropertyValueInvalid001"/>): unrecognized units,
/// missing units (e.g., <c>width: 16</c> — non-zero requires a unit per §6.2),
/// garbled numerics, percentages on properties whose type doesn't admit them
/// (e.g., <c>letter-spacing: 50%</c>), <c>auto</c> on properties that don't admit
/// it, <c>letter-spacing: 50%</c> (CSS Text 3 §10.1 — letter-spacing accepts only
/// <c>normal | &lt;length&gt;</c>; word-spacing accepts <c>&lt;length-percentage&gt;</c>),
/// negative values for spec-disallowed properties per
/// <see cref="NonNegativeProperties"/>, NaN/±Infinity from upstream calc reductions.
/// </para>
/// </remarks>
internal static class LengthResolver
{
    /// <summary>The single keyword id used by the dimension family for the property's
    /// admitted keyword (<c>auto</c>, <c>normal</c>, or <c>none</c>). v1 uses one id
    /// per property since only one keyword applies.</summary>
    public const int KeywordIdAuto = 0;
    /// <inheritdoc cref="KeywordIdAuto"/>
    public const int KeywordIdNormal = 0;
    /// <inheritdoc cref="KeywordIdAuto"/>
    public const int KeywordIdNone = 0;
    /// <summary>Per Phase 3 Task 15 L8 — additional keyword admitted by
    /// <see cref="PropertyType.FlexBasis"/> per CSS Flexbox L1 §7.2:
    /// <c>content</c> sizes the item to its content size (an intrinsic
    /// sizing keyword distinct from <c>auto</c> which delegates to the
    /// item's <c>width</c>/<c>height</c>). L8 admits the keyword + the
    /// reader maps it to the Content variant of
    /// <c>ResolvedFlexBasis</c>; the §9.2.3.A "main-size keyword"
    /// resolution to the item's intrinsic content size is deferred to
    /// the broader intrinsic-sizing work (currently the layouter
    /// approximates <c>content</c> as <c>auto</c> = delegate to declared
    /// width).</summary>
    public const int KeywordIdContent = 1;

    public static ResolverResult Resolve(
        string value,
        PropertyType type,
        PropertyId propertyId,
        string propertyName,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location)
    {
        // Type-specific keywords first — cheaper than a numeric parse and the keyword
        // forms are far more common (e.g., the auto / normal / none defaults).
        if (TryMatchKeyword(value, type, out var keywordSlot))
            return ResolverResult.Resolved(keywordSlot);

        // Per Phase 3 Task 14 cycle 1 — per-property keyword admittance
        // for the multicol family. CSS Multi-column L1 admits:
        //   - column-width: <length> | auto
        //   - column-gap:   <length> | normal
        // Both are Length-typed (not LengthPercentageAuto / TextSpacing)
        // because they don't admit percentages OR the broader keyword
        // sets. The cycle-1 layouter reads column-gap via
        // <c>ComputedStyleLayoutExtensions.ReadColumnGap</c>, which
        // treats a non-LengthPx slot as the initial keyword (= 16 px
        // hard-coded). <c>column-width</c> is parsed + cascades but
        // is unused by cycle 1 — sub-cycle 2+ adds the
        // column-width-derived column count + a corresponding
        // <c>ReadColumnWidth</c> extension; the Keyword-slot encoding
        // here is forward-compat for that work.
        if (TryMatchMulticolKeyword(value, propertyId, out var multicolKeywordSlot))
            return ResolverResult.Resolved(multicolKeywordSlot);

        // Numeric path: decompose into (number, unit).
        if (!TrySplitNumberAndUnit(value, out var number, out var unit))
        {
            EmitInvalid(diagnostics, propertyName, value,
                "expected a length, percentage, or admitted keyword", location);
            return ResolverResult.Invalid();
        }

        // Pre-validate: NaN / Infinity from upstream (calc reductions, scientific
        // notation overflow) lands here. Reject on the diagnostic path BEFORE the
        // slot factory's defensive throw fires.
        if (!double.IsFinite(number))
        {
            EmitInvalid(diagnostics, propertyName, value,
                $"numeric value is not finite ({number})", location);
            return ResolverResult.Invalid();
        }

        // Bare number — only legal when it's exactly zero (CSS Values L4 §6.2).
        if (unit.Length == 0)
        {
            if (number == 0.0) return ResolverResult.Resolved(ComputedSlot.FromLengthPx(0.0));
            EmitInvalid(diagnostics, propertyName, value,
                "non-zero numeric requires a unit", location);
            return ResolverResult.Invalid();
        }

        // Percentage handling — the per-PropertyType + per-PropertyId acceptance grid.
        if (unit == "%")
        {
            if (!IsPercentageAllowed(type, propertyId, out var rejectionReason))
            {
                EmitInvalid(diagnostics, propertyName, value, rejectionReason, location);
                return ResolverResult.Invalid();
            }
            if (number < ComputedSlot.MinFixedPercentage || number > ComputedSlot.MaxFixedPercentage)
            {
                EmitInvalid(diagnostics, propertyName, value,
                    $"percentage out of representable range [{ComputedSlot.MinFixedPercentage}, {ComputedSlot.MaxFixedPercentage}]",
                    location);
                return ResolverResult.Invalid();
            }
            // Negative-value gate (e.g., padding rejects negative percentages).
            if (number < 0 && NonNegativeProperties.IsRequired(propertyId))
            {
                EmitInvalid(diagnostics, propertyName, value,
                    "negative value not allowed for this property", location);
                return ResolverResult.Invalid();
            }
            return ResolverResult.Resolved(ComputedSlot.FromPercentage(number));
        }

        // Absolute lengths fold to px per CSS Values L4 §6.1.
        if (TryAbsoluteUnitToPx(unit, number, out var px))
        {
            // Defensive: even with finite operands, an extreme conversion could overflow
            // float32. Catch it here on the diagnostic path.
            if (!double.IsFinite(px) || px > float.MaxValue || px < float.MinValue)
            {
                EmitInvalid(diagnostics, propertyName, value,
                    "length overflows representable px range", location);
                return ResolverResult.Invalid();
            }
            if (px < 0 && NonNegativeProperties.IsRequired(propertyId))
            {
                EmitInvalid(diagnostics, propertyName, value,
                    "negative length not allowed for this property", location);
                return ResolverResult.Invalid();
            }
            return ResolverResult.Resolved(ComputedSlot.FromLengthPx(px));
        }

        // Font/viewport/container-relative units: defer for Phase 3.
        // The post-cascade pipeline carries the original text via
        // ResolverResult.RawText so it can be re-resolved with full context.
        if (IsDeferredUnit(unit))
            return ResolverResult.Deferred(value);

        EmitInvalid(diagnostics, propertyName, value, $"unknown unit '{unit}'", location);
        return ResolverResult.Invalid();
    }

    private static bool TryMatchKeyword(string value, PropertyType type, out ComputedSlot slot)
    {
        slot = default;
        if (type is PropertyType.LengthPercentageAuto && value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            slot = ComputedSlot.FromKeyword(KeywordIdAuto);
            return true;
        }
        if (type is PropertyType.TextSpacing && value.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            slot = ComputedSlot.FromKeyword(KeywordIdNormal);
            return true;
        }
        // Per Phase 3 Task 15 L8 — flex-basis grammar per CSS Flexbox L1
        // §7.2: `content | <'width'>` where <'width'> admits
        // `<length-percentage> | auto | min-content | max-content |
        // fit-content(...)`. L8 Hello World admits `auto` + `content` +
        // the generic length-percentage path; the three intrinsic-sizing
        // keywords (min-content / max-content / fit-content) are L9+
        // scope. The reader (ReadFlexBasis) decodes Keyword(0) → Auto
        // and Keyword(1) → Content; the FlexLayouter currently
        // approximates Content as Auto (= delegate to declared
        // width/height) until intrinsic sizing lands.
        if (type is PropertyType.FlexBasis)
        {
            if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                slot = ComputedSlot.FromKeyword(KeywordIdAuto);
                return true;
            }
            if (value.Equals("content", StringComparison.OrdinalIgnoreCase))
            {
                slot = ComputedSlot.FromKeyword(KeywordIdContent);
                return true;
            }
        }
        // Per Phase 3 Task 15 L12 — MaxSize accepts `none` (= no upper
        // bound) per CSS Sizing L3 §5.2 + the `<length-percentage>`
        // production. The reader (ReadFlexMinMax / ReadMaxSizeOrInfinity)
        // maps Keyword(KeywordIdNone = 0) → double.PositiveInfinity (=
        // no clamp); LengthPx/Percentage → the explicit cap value.
        if (type is PropertyType.MaxSize
            && value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            slot = ComputedSlot.FromKeyword(KeywordIdNone);
            return true;
        }
        return false;
    }

    /// <summary>Per Phase 3 Task 14 cycle 1 — per-property keyword
    /// admittance for the multicol Length properties. The CSS spec
    /// admits ONE keyword in addition to the generic length grammar:
    /// <c>auto</c> for <c>column-width</c> (CSS Multi-column L1 §3.1),
    /// <c>normal</c> for <c>column-gap</c> (CSS Multi-column L1 §6.1).
    /// Both are Length-typed (not LengthPercentageAuto / TextSpacing
    /// where the generic <see cref="TryMatchKeyword"/> already admits
    /// the keyword) so a per-PropertyId gate is the cleanest extension.
    /// The layouter reads <c>column-gap</c> via
    /// <c>ComputedStyleLayoutExtensions.ReadColumnGap</c> + treats a
    /// Keyword slot as the initial value. <c>column-width</c> is
    /// parsed but unused by cycle 1 (sub-cycle 2+ ships the
    /// column-width-derived count + the matching reader extension).</summary>
    private static bool TryMatchMulticolKeyword(
        string value, PropertyId propertyId, out ComputedSlot slot)
    {
        slot = default;
        if (propertyId == PropertyId.ColumnWidth
            && value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            slot = ComputedSlot.FromKeyword(KeywordIdAuto);
            return true;
        }
        if (propertyId == PropertyId.ColumnGap
            && value.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            slot = ComputedSlot.FromKeyword(KeywordIdNormal);
            return true;
        }
        return false;
    }

    /// <summary>Per-PropertyType + per-PropertyId percentage acceptance gate. CSS
    /// Text 3 §10.1 splits the TextSpacing family: <c>letter-spacing</c> accepts only
    /// <c>normal | &lt;length&gt;</c> (no percentage); <c>word-spacing</c> accepts
    /// <c>normal | &lt;length-percentage&gt;</c>. Length-only rejects all percentages.
    /// LengthPercentage / LengthPercentageAuto / Percentage / WordSpacing accept.</summary>
    private static bool IsPercentageAllowed(PropertyType type, PropertyId propertyId, out string reason)
    {
        reason = string.Empty;
        switch (type)
        {
            case PropertyType.Length:
                reason = "percentage is not allowed for this property (Length-only)";
                return false;
            case PropertyType.TextSpacing when propertyId == PropertyId.LetterSpacing:
                reason = "percentage is not allowed for letter-spacing per CSS Text 3 §10.1 (use a length)";
                return false;
            case PropertyType.LengthPercentage:
            case PropertyType.LengthPercentageAuto:
            case PropertyType.Percentage:
            case PropertyType.TextSpacing: // word-spacing falls through here
            // Per Phase 3 Task 15 L8 — flex-basis admits the
            // <length-percentage> production per CSS Flexbox L1 §7.2.
            case PropertyType.FlexBasis:
            // Per Phase 3 Task 15 L12 — MaxSize admits the
            // <length-percentage> production per CSS Sizing L3 §5.2.
            case PropertyType.MaxSize:
                return true;
            default:
                reason = "percentage is not allowed for this property";
                return false;
        }
    }

    /// <summary>Splits a CSS dimension into number + unit. Accepts optional sign,
    /// optional decimal, optional exponent. Unit may be empty (bare number).
    /// Returns false on garbled input.</summary>
    private static bool TrySplitNumberAndUnit(string value, out double number, out string unit)
    {
        number = 0;
        unit = string.Empty;
        if (value.Length == 0) return false;

        var span = value.AsSpan();
        var i = 0;

        // Sign.
        if (span[i] == '+' || span[i] == '-') i++;

        // Integer part.
        var digitsBeforeDot = 0;
        while (i < span.Length && IsAsciiDigit(span[i])) { i++; digitsBeforeDot++; }

        // Optional decimal.
        var digitsAfterDot = 0;
        if (i < span.Length && span[i] == '.')
        {
            i++;
            while (i < span.Length && IsAsciiDigit(span[i])) { i++; digitsAfterDot++; }
        }
        if (digitsBeforeDot == 0 && digitsAfterDot == 0) return false;

        // Optional exponent — `2em` trap guard: only consume `e` as exponent when
        // followed by an optional sign + at least one digit. Otherwise treat `e` as
        // the start of the unit identifier.
        if (i < span.Length && (span[i] == 'e' || span[i] == 'E'))
        {
            var lookahead = i + 1;
            if (lookahead < span.Length && (span[lookahead] == '+' || span[lookahead] == '-'))
                lookahead++;
            var sawExpDigit = false;
            while (lookahead < span.Length && IsAsciiDigit(span[lookahead]))
            {
                sawExpDigit = true;
                lookahead++;
            }
            if (sawExpDigit) i = lookahead;
        }

        var numText = span[..i];
        if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return false;

        unit = span[i..].ToString();
        if (unit.Length > 0 && unit != "%")
        {
            for (var k = 0; k < unit.Length; k++)
            {
                var c = unit[k];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) return false;
            }
            unit = unit.ToLowerInvariant();
        }
        return true;
    }

    /// <summary>Fold an absolute CSS length (unit + number) to px per CSS Values L4 §6.1.
    /// Returns <see langword="false"/> for non-absolute (font-/viewport-relative, %) units.
    /// <see langword="internal"/> so paged-media descriptor resolution (e.g. <c>@page</c>
    /// <c>margin</c>) can reuse the exact same conversion table.</summary>
    internal static bool TryAbsoluteUnitToPx(string unit, double number, out double px)
    {
        // CSS Values L4 §6.1: absolute lengths.
        switch (unit)
        {
            case "px": px = number; return true;
            case "in": px = number * 96.0; return true;
            case "cm": px = number * (96.0 / 2.54); return true;
            case "mm": px = number * (96.0 / 25.4); return true;
            case "q":  px = number * (96.0 / 25.4 / 4.0); return true; // 1Q = 1/40 cm
            case "pt": px = number * (96.0 / 72.0); return true;
            case "pc": px = number * 16.0; return true; // 1pc = 12pt = 16px
            default:   px = 0; return false;
        }
    }

    private static bool IsDeferredUnit(string unit) => unit switch
    {
        // Font-relative.
        "em" or "rem" or "ch" or "ex" or "lh" or "rlh" or "cap" or "ic" => true,
        // Viewport-relative + small/large/dynamic variants.
        "vw" or "vh" or "vmin" or "vmax" or "vi" or "vb" => true,
        "svw" or "svh" or "svmin" or "svmax" or "svi" or "svb" => true,
        "lvw" or "lvh" or "lvmin" or "lvmax" or "lvi" or "lvb" => true,
        "dvw" or "dvh" or "dvmin" or "dvmax" or "dvi" or "dvb" => true,
        // Container-relative.
        "cqw" or "cqh" or "cqi" or "cqb" or "cqmin" or "cqmax" => true,
        _ => false,
    };

    private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

    private static void EmitInvalid(
        ICssDiagnosticsSink? sink, string propertyName, string value, string reason,
        CssSourceLocation location)
    {
        // Per Phase A A-6 — sanitize untrusted value + reason before message
        // interpolation; see DiagnosticTextSanitizer for rationale.
        var safeValue = DiagnosticTextSanitizer.Sanitize(value);
        var safeReason = DiagnosticTextSanitizer.Sanitize(reason);
        sink?.Emit(new CssDiagnostic(
            CssDiagnosticCodes.CssPropertyValueInvalid001,
            $"Could not parse '{propertyName}: {safeValue}' — {safeReason}.",
            CssDiagnosticSeverity.Warning,
            location));
    }
}
