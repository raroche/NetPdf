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
/// <b>Reduces</b>: absolute lengths (<c>px</c>, <c>in</c>, <c>cm</c>, <c>mm</c>,
/// <c>pt</c>, <c>pc</c>, <c>q</c>) into pixels per the CSS Values L4 §6.1
/// conversion table; percentages into a fixed-point payload via
/// <see cref="ComputedSlot.FromPercentage"/>; the unitless zero (<c>0</c> alone is a
/// valid length per §6.2). Type-specific keywords reduce to per-property keyword ids:
/// <c>auto</c> for <see cref="PropertyType.LengthPercentageAuto"/>,
/// <c>normal</c> for <see cref="PropertyType.TextSpacing"/>.
/// </para>
/// <para>
/// <b>Defers</b>: font-relative units (<c>em</c>, <c>rem</c>, <c>ch</c>, <c>ex</c>,
/// <c>lh</c>, <c>rlh</c>, <c>cap</c>, <c>ic</c>), viewport-relative units
/// (<c>vw</c>/<c>vh</c>/<c>svw</c>/<c>lvw</c>/<c>dvw</c>/<c>vmin</c>/<c>vmax</c>/etc.),
/// and container-relative units (<c>cqw</c>/<c>cqh</c>/<c>cqi</c>/<c>cqb</c>/<c>cqmin</c>/<c>cqmax</c>).
/// These need a per-element font/viewport/container context that the cascade stage
/// doesn't have — Phase 3 layout finalizes them. Returns <see cref="ComputedSlot.Unset"/>
/// with no diagnostic; the post-cascade pipeline carries the original text alongside
/// the slot for later resolution.
/// </para>
/// <para>
/// <b>Rejects</b> (emits <see cref="CssDiagnosticCodes.CssPropertyValueInvalid001"/>):
/// unrecognized units, missing units (e.g., <c>width: 16</c> — non-zero requires a
/// unit per §6.2), garbled numerics, percentages on properties whose type doesn't
/// admit them, <c>auto</c> on properties that don't admit it.
/// </para>
/// <para>
/// <b>v1 keyword IDs.</b> The dimension family currently uses two keyword ids:
/// <c>auto = 0</c>, <c>normal = 0</c>, <c>none = 0</c>. Each property's spec admits
/// only one, so the namespace doesn't collide. Cycle 2 will introduce a per-property
/// keyword table for richer enums (e.g., <c>display</c>, <c>position</c>) — those
/// land with <see cref="KeywordResolver"/> proper.
/// </para>
/// </remarks>
internal static class LengthResolver
{
    /// <summary>The single keyword id used by the dimension family for the property's
    /// admitted keyword (<c>auto</c>, <c>normal</c>, or <c>none</c>). v1 uses one id
    /// per property since only one keyword applies; the property's
    /// <see cref="PropertyMeta.Type"/> tells the consumer which keyword the id means.</summary>
    public const int KeywordIdAuto = 0;
    /// <inheritdoc cref="KeywordIdAuto"/>
    public const int KeywordIdNormal = 0;
    /// <inheritdoc cref="KeywordIdAuto"/>
    public const int KeywordIdNone = 0;

    public static ComputedSlot Resolve(
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
            return keywordSlot;

        // Numeric path: decompose into (number, unit). The unit is what determines
        // whether we reduce, defer, or reject. Empty unit + non-zero number is
        // invalid per CSS Values L4 §6.2; empty unit with the literal `0` is fine.
        if (!TrySplitNumberAndUnit(value, out var number, out var unit))
        {
            EmitInvalid(diagnostics, propertyName, value, "expected a length, percentage, or admitted keyword", location);
            return ComputedSlot.Unset;
        }

        // Bare number — only legal when it's exactly zero (CSS Values L4 §6.2).
        if (unit.Length == 0)
        {
            if (number == 0.0) return ComputedSlot.FromLengthPx(0.0);
            EmitInvalid(diagnostics, propertyName, value, "non-zero numeric requires a unit", location);
            return ComputedSlot.Unset;
        }

        // Percentage handling: only LengthPercentage / LengthPercentageAuto / Percentage
        // / TextSpacing accept it. (Length proper rejects.)
        if (unit == "%")
        {
            if (type is PropertyType.Length)
            {
                EmitInvalid(diagnostics, propertyName, value, "percentage is not allowed for this property", location);
                return ComputedSlot.Unset;
            }
            if (number < ComputedSlot.MinFixedPercentage || number > ComputedSlot.MaxFixedPercentage)
            {
                EmitInvalid(diagnostics, propertyName, value,
                    $"percentage out of representable range [{ComputedSlot.MinFixedPercentage}, {ComputedSlot.MaxFixedPercentage}]",
                    location);
                return ComputedSlot.Unset;
            }
            return ComputedSlot.FromPercentage(number);
        }

        // Absolute lengths fold to px per CSS Values L4 §6.1.
        if (TryAbsoluteUnitToPx(unit, number, out var px))
            return ComputedSlot.FromLengthPx(px);

        // Font/viewport/container-relative units: defer for Phase 3.
        // Returning Unset with no diagnostic signals "valid but not yet resolvable" —
        // the post-cascade pipeline keeps the original text for finalization.
        if (IsDeferredUnit(unit))
            return ComputedSlot.Unset;

        EmitInvalid(diagnostics, propertyName, value, $"unknown unit '{unit}'", location);
        return ComputedSlot.Unset;
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
        // PropertyType.MaxSize ('max-width: none') would land here in cycle 2 — for now
        // it's not in the dispatch's switch, so we don't handle it.
        return false;
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

        // Optional exponent (CSS does accept scientific notation in <number>; we
        // require at least one digit after e/E with optional sign).
        if (i < span.Length && (span[i] == 'e' || span[i] == 'E'))
        {
            // But guard against the `2em` trap — only consume `e` as exponent when
            // followed by an optional sign + at least one digit. Otherwise treat
            // `e` as the start of the unit identifier.
            var lookahead = i + 1;
            var sawSign = false;
            if (lookahead < span.Length && (span[lookahead] == '+' || span[lookahead] == '-'))
            {
                sawSign = true;
                lookahead++;
            }
            var sawExpDigit = false;
            while (lookahead < span.Length && IsAsciiDigit(span[lookahead]))
            {
                sawExpDigit = true;
                lookahead++;
            }
            if (sawExpDigit) i = lookahead;
            else _ = sawSign; // discard — unit lexing will pick up `em`/`ex`/etc.
        }

        var numText = span[..i];
        if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return false;

        unit = span[i..].ToString();
        // Unit must be ASCII letters or `%`. Reject anything else (e.g., `16xpx`
        // would have nonsense after the digits — but `1px` lower-cases cleanly).
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

    private static bool TryAbsoluteUnitToPx(string unit, double number, out double px)
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
        sink?.Emit(new CssDiagnostic(
            CssDiagnosticCodes.CssPropertyValueInvalid001,
            $"Could not parse '{propertyName}: {value}' — {reason}.",
            CssDiagnosticSeverity.Warning,
            location));
    }
}
