// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Resolves CSS color values per CSS Color L4 §4 (sRGB color forms). The cycle-1
/// surface covers: <see cref="CssNamedColors">named colors</see>, hex
/// (<c>#rgb</c> / <c>#rgba</c> / <c>#rrggbb</c> / <c>#rrggbbaa</c>),
/// <c>rgb()</c> / <c>rgba()</c> in both modern (whitespace-separated, slash alpha) and
/// legacy (comma-separated) syntax, <c>hsl()</c> / <c>hsla()</c> in both syntaxes,
/// plus the CSS-wide values <c>transparent</c> and <c>currentcolor</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Output encoding.</b> Every successful parse produces a <see cref="ComputedSlot.FromColor"/>
/// with the color packed as <c>0xAARRGGBB</c>. <c>transparent</c> packs as <c>0x00000000</c>;
/// <c>currentcolor</c> packs as the sentinel <see cref="CurrentColorSentinel"/>
/// (<c>0x00000001</c> — distinct from transparent because the high alpha bits are zero
/// but bit 0 of RGB is set, which transparent never has).
/// </para>
/// <para>
/// <b>Deferred to a follow-up cycle.</b> Modern color spaces (<c>oklch()</c>,
/// <c>oklab()</c>, <c>lab()</c>, <c>lch()</c>, <c>color()</c>) and the
/// <c>color-mix()</c> functional require Task 3's pre-pass capture (AngleSharp.Css
/// silently corrupts these to wrong rgba). System colors (<c>canvas</c>,
/// <c>canvastext</c>, etc. per CSS Color 4 §10) are context-dependent and resolve
/// at compute time once a color scheme is selected; they're not in the cycle-1 scope.
/// </para>
/// </remarks>
internal static class ColorResolver
{
    /// <summary>Sentinel value packed into a <see cref="ComputedSlot.FromColor"/> slot
    /// when the source value was the keyword <c>currentcolor</c>. The downstream
    /// pipeline (paint stage) substitutes the cascaded <c>color</c> property when it
    /// sees this exact pattern. Distinct from <c>transparent</c> (<c>0x00000000</c>)
    /// because the low bit of red is set — transparent has all 32 bits zero.</summary>
    public const uint CurrentColorSentinel = 0x00000001u;

    public static ComputedSlot Resolve(
        string value,
        PropertyId propertyId,
        string propertyName,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location)
    {
        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            return ComputedSlot.FromColor(0x00000000u);
        if (value.Equals("currentcolor", StringComparison.OrdinalIgnoreCase))
            return ComputedSlot.FromColor(CurrentColorSentinel);

        if (value.Length > 0 && value[0] == '#')
        {
            if (TryParseHex(value, out var argb)) return ComputedSlot.FromColor(argb);
            EmitInvalid(diagnostics, propertyName, value, "malformed hex color (expected #rgb, #rgba, #rrggbb, or #rrggbbaa)", location);
            return ComputedSlot.Unset;
        }

        if (CssNamedColors.TryGet(value, out var named))
            return ComputedSlot.FromColor(named);

        // Functional forms.
        if (TryParseFunctionalColor(value, out var fnArgb, out var fnReason))
            return ComputedSlot.FromColor(fnArgb);
        if (fnReason is not null)
        {
            EmitInvalid(diagnostics, propertyName, value, fnReason, location);
            return ComputedSlot.Unset;
        }

        EmitInvalid(diagnostics, propertyName, value,
            "expected a named color, hex (#rgb/#rgba/#rrggbb/#rrggbbaa), rgb()/rgba(), hsl()/hsla(), transparent, or currentcolor",
            location);
        return ComputedSlot.Unset;
    }

    /// <summary>Hex parsing per CSS Color L4 §4.4. 3-digit form expands #rgb → #rrggbb;
    /// 4-digit form expands #rgba → #rrggbbaa. 6-digit form is opaque; 8-digit form
    /// carries an alpha channel.</summary>
    internal static bool TryParseHex(string value, out uint argb)
    {
        argb = 0;
        if (value.Length is not (4 or 5 or 7 or 9)) return false;
        if (value[0] != '#') return false;

        Span<byte> nibbles = stackalloc byte[8];
        for (var i = 1; i < value.Length; i++)
        {
            if (!TryHexDigit(value[i], out var nyb)) return false;
            nibbles[i - 1] = nyb;
        }
        switch (value.Length)
        {
            case 4: // #rgb → #rrggbb (alpha = 0xFF)
                argb = (0xFFu << 24)
                     | ((uint)((nibbles[0] << 4) | nibbles[0]) << 16)
                     | ((uint)((nibbles[1] << 4) | nibbles[1]) << 8)
                     |  (uint)((nibbles[2] << 4) | nibbles[2]);
                return true;
            case 5: // #rgba → #rrggbbaa
                argb = ((uint)((nibbles[3] << 4) | nibbles[3]) << 24)
                     | ((uint)((nibbles[0] << 4) | nibbles[0]) << 16)
                     | ((uint)((nibbles[1] << 4) | nibbles[1]) << 8)
                     |  (uint)((nibbles[2] << 4) | nibbles[2]);
                return true;
            case 7: // #rrggbb (alpha = 0xFF)
                argb = (0xFFu << 24)
                     | ((uint)((nibbles[0] << 4) | nibbles[1]) << 16)
                     | ((uint)((nibbles[2] << 4) | nibbles[3]) << 8)
                     |  (uint)((nibbles[4] << 4) | nibbles[5]);
                return true;
            case 9: // #rrggbbaa
                argb = ((uint)((nibbles[6] << 4) | nibbles[7]) << 24)
                     | ((uint)((nibbles[0] << 4) | nibbles[1]) << 16)
                     | ((uint)((nibbles[2] << 4) | nibbles[3]) << 8)
                     |  (uint)((nibbles[4] << 4) | nibbles[5]);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Functional color parser. Supports both modern syntax
    /// (<c>rgb(255 0 0 / 50%)</c>) and legacy syntax (<c>rgb(255, 0, 0, 0.5)</c>) per
    /// CSS Color L4 §4.2. Returns <see langword="true"/> + packed argb on success;
    /// <see langword="false"/> + null reason if the value isn't a functional color
    /// at all (caller falls through); <see langword="false"/> + non-null reason if it
    /// LOOKED like a functional color but was malformed (caller emits diagnostic).</summary>
    private static bool TryParseFunctionalColor(string value, out uint argb, out string? reason)
    {
        argb = 0;
        reason = null;
        var openIdx = value.IndexOf('(');
        var closeIdx = value.Length > 0 && value[^1] == ')' ? value.Length - 1 : -1;
        if (openIdx < 0 || closeIdx < 0 || openIdx >= closeIdx) return false;

        var fnName = value[..openIdx].TrimEnd().ToLowerInvariant();
        var body = value[(openIdx + 1)..closeIdx];

        switch (fnName)
        {
            case "rgb":
            case "rgba":
                return TryParseRgbFunction(body, out argb, out reason);
            case "hsl":
            case "hsla":
                return TryParseHslFunction(body, out argb, out reason);
            case "oklch": case "oklab": case "lab": case "lch": case "color": case "color-mix":
                reason = $"modern color function '{fnName}()' is deferred to a follow-up cycle";
                return false;
            default:
                return false;
        }
    }

    private static bool TryParseRgbFunction(string body, out uint argb, out string? reason)
    {
        argb = 0;
        reason = null;
        if (!TrySplitColorArgs(body, out var args, out var sawSlash, out reason)) return false;
        if (args.Count is not (3 or 4))
        {
            reason = $"rgb()/rgba() expects 3 or 4 components, got {args.Count}";
            return false;
        }

        // Components 0..2 are R/G/B as either <number> 0..255 or <percentage> 0..100%.
        // Mixing forms is permitted in modern syntax but rejected in legacy. Cycle 1
        // is permissive — both forms in any combination accepted.
        if (!TryParseRgbChannel(args[0], out var r) ||
            !TryParseRgbChannel(args[1], out var g) ||
            !TryParseRgbChannel(args[2], out var b))
        {
            reason = "rgb()/rgba() component must be a number 0..255 or percentage 0..100%";
            return false;
        }

        var aByte = (byte)0xFF;
        if (args.Count == 4)
        {
            if (!TryParseAlphaComponent(args[3], out var a))
            {
                reason = "rgb()/rgba() alpha must be a number 0..1 or percentage 0..100%";
                return false;
            }
            aByte = a;
        }
        // Modern syntax requires the slash separator before alpha; legacy requires comma.
        // The split function records whether a slash was present; our cycle-1 check is
        // softer — we accept both shapes uniformly (CSS Color 4 doesn't penalize either
        // when the rest of the syntax is well-formed).
        _ = sawSlash;

        argb = ((uint)aByte << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
        return true;
    }

    private static bool TryParseHslFunction(string body, out uint argb, out string? reason)
    {
        argb = 0;
        reason = null;
        if (!TrySplitColorArgs(body, out var args, out _, out reason)) return false;
        if (args.Count is not (3 or 4))
        {
            reason = $"hsl()/hsla() expects 3 or 4 components, got {args.Count}";
            return false;
        }

        if (!TryParseHueComponent(args[0], out var hueDeg) ||
            !TryParsePercentComponent(args[1], out var sat) ||
            !TryParsePercentComponent(args[2], out var light))
        {
            reason = "hsl()/hsla() expects <hue> <sat%> <light%> (with optional alpha)";
            return false;
        }

        var aByte = (byte)0xFF;
        if (args.Count == 4)
        {
            if (!TryParseAlphaComponent(args[3], out var a))
            {
                reason = "hsl()/hsla() alpha must be a number 0..1 or percentage 0..100%";
                return false;
            }
            aByte = a;
        }

        HslToRgb(hueDeg, sat, light, out var r, out var g, out var b);
        argb = ((uint)aByte << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
        return true;
    }

    /// <summary>Splits a color-function argument list. Tolerates both legacy comma-
    /// separated and modern whitespace-separated forms; the alpha separator may be
    /// either <c>,</c> or <c>/</c> (modern requires <c>/</c> but cycle 1 is lenient).</summary>
    private static bool TrySplitColorArgs(
        string body,
        out System.Collections.Generic.List<string> args,
        out bool sawSlash,
        out string? reason)
    {
        args = new System.Collections.Generic.List<string>(4);
        sawSlash = false;
        reason = null;

        var span = body.AsSpan().Trim();
        if (span.IsEmpty) return false;

        // Decide separator style by sniffing for a comma at top level.
        var hasComma = false;
        for (var i = 0; i < span.Length; i++)
            if (span[i] == ',') { hasComma = true; break; }

        if (hasComma)
        {
            // Legacy comma-separated. The 4th comma marks alpha; slashes are not allowed.
            var start = 0;
            for (var i = 0; i <= span.Length; i++)
            {
                if (i == span.Length || span[i] == ',')
                {
                    var token = span[start..i].Trim();
                    if (token.IsEmpty) { reason = "empty component in color function"; return false; }
                    args.Add(token.ToString());
                    start = i + 1;
                }
            }
        }
        else
        {
            // Modern whitespace-separated. A `/` introduces the alpha component.
            var i = 0;
            while (i < span.Length)
            {
                while (i < span.Length && IsWhitespace(span[i])) i++;
                if (i >= span.Length) break;
                if (span[i] == '/')
                {
                    sawSlash = true;
                    i++;
                    while (i < span.Length && IsWhitespace(span[i])) i++;
                    var alphaStart = i;
                    while (i < span.Length && !IsWhitespace(span[i])) i++;
                    if (alphaStart == i) { reason = "missing alpha after '/'"; return false; }
                    args.Add(span[alphaStart..i].ToString());
                    continue;
                }
                var start = i;
                while (i < span.Length && !IsWhitespace(span[i]) && span[i] != '/') i++;
                args.Add(span[start..i].ToString());
            }
        }
        return true;
    }

    private static bool TryParseRgbChannel(string token, out byte channel)
    {
        channel = 0;
        if (token.Length == 0) return false;
        if (token[^1] == '%')
        {
            if (!double.TryParse(token.AsSpan(0, token.Length - 1),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) return false;
            // pct * 255 / 100 (NOT pct * 2.55) — 2.55 is not exactly representable in
            // IEEE-754, so 50 * 2.55 = 127.49999... rounds to 127 instead of 128.
            channel = (byte)Math.Clamp((int)Math.Round(pct * 255.0 / 100.0, MidpointRounding.AwayFromZero), 0, 255);
            return true;
        }
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return false;
        channel = (byte)Math.Clamp((int)Math.Round(n, MidpointRounding.AwayFromZero), 0, 255);
        return true;
    }

    private static bool TryParseAlphaComponent(string token, out byte alpha)
    {
        alpha = 0;
        if (token.Length == 0) return false;
        if (token[^1] == '%')
        {
            if (!double.TryParse(token.AsSpan(0, token.Length - 1),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) return false;
            alpha = (byte)Math.Clamp((int)Math.Round(pct * 255.0 / 100.0, MidpointRounding.AwayFromZero), 0, 255);
            return true;
        }
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return false;
        alpha = (byte)Math.Clamp((int)Math.Round(n * 255, MidpointRounding.AwayFromZero), 0, 255);
        return true;
    }

    private static bool TryParseHueComponent(string token, out double degrees)
    {
        degrees = 0;
        if (token.Length == 0) return false;
        // Strip the unit (deg / rad / grad / turn). Bare number = degrees.
        var i = 0;
        if (token[i] == '+' || token[i] == '-') i++;
        while (i < token.Length && (IsAsciiDigit(token[i]) || token[i] == '.' || token[i] == 'e' || token[i] == 'E' || token[i] == '+' || token[i] == '-'))
        {
            // Loose acceptance — TryParse below is the real validator.
            i++;
        }
        // Don't double-consume sign — back off if `e` hit then sign was previously consumed.
        // (In practice double.TryParse handles malformed numerics by failing.)
        var numText = token.AsSpan(0, i);
        var unit = token.AsSpan(i).ToString().Trim().ToLowerInvariant();
        if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw)) return false;
        degrees = unit switch
        {
            "" or "deg" => raw,
            "rad" => raw * (180.0 / Math.PI),
            "grad" => raw * 0.9,
            "turn" => raw * 360.0,
            _ => double.NaN,
        };
        if (double.IsNaN(degrees)) return false;
        // Normalize into [0, 360).
        degrees %= 360.0;
        if (degrees < 0) degrees += 360.0;
        return true;
    }

    private static bool TryParsePercentComponent(string token, out double normalized)
    {
        normalized = 0;
        if (token.Length == 0 || token[^1] != '%') return false;
        if (!double.TryParse(token.AsSpan(0, token.Length - 1),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) return false;
        normalized = Math.Clamp(pct / 100.0, 0.0, 1.0);
        return true;
    }

    /// <summary>Standard HSL → RGB conversion per CSS Color L4 §4.3 / W3C HSL spec.
    /// Inputs: hue in degrees [0, 360), saturation + lightness normalized to [0, 1].
    /// Outputs: 0..255 byte channels.</summary>
    private static void HslToRgb(double hueDeg, double sat, double light, out byte r, out byte g, out byte b)
    {
        var c = (1 - Math.Abs(2 * light - 1)) * sat;
        var hh = hueDeg / 60.0;
        var x = c * (1 - Math.Abs(hh % 2 - 1));
        double r1 = 0, g1 = 0, b1 = 0;
        if      (hh >= 0 && hh < 1) { r1 = c; g1 = x; b1 = 0; }
        else if (hh < 2)            { r1 = x; g1 = c; b1 = 0; }
        else if (hh < 3)            { r1 = 0; g1 = c; b1 = x; }
        else if (hh < 4)            { r1 = 0; g1 = x; b1 = c; }
        else if (hh < 5)            { r1 = x; g1 = 0; b1 = c; }
        else                        { r1 = c; g1 = 0; b1 = x; }
        var m = light - c / 2.0;
        r = (byte)Math.Clamp((int)Math.Round((r1 + m) * 255), 0, 255);
        g = (byte)Math.Clamp((int)Math.Round((g1 + m) * 255), 0, 255);
        b = (byte)Math.Clamp((int)Math.Round((b1 + m) * 255), 0, 255);
    }

    private static bool TryHexDigit(char c, out byte value)
    {
        if (c >= '0' && c <= '9') { value = (byte)(c - '0'); return true; }
        if (c >= 'a' && c <= 'f') { value = (byte)(c - 'a' + 10); return true; }
        if (c >= 'A' && c <= 'F') { value = (byte)(c - 'A' + 10); return true; }
        value = 0;
        return false;
    }

    private static bool IsWhitespace(char c) => c is ' ' or '\t' or '\r' or '\n' or '\f';
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
