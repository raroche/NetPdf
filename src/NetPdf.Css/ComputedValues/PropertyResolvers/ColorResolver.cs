// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Resolves CSS color values per CSS Color L4 §4 (sRGB color forms) + §10 (system
/// colors). Cycle-1 surface: <see cref="CssNamedColors">named colors</see>,
/// <see cref="CssSystemColors">system colors</see>, hex (<c>#rgb</c> / <c>#rgba</c> /
/// <c>#rrggbb</c> / <c>#rrggbbaa</c>), <c>rgb()</c> / <c>rgba()</c> in modern AND
/// legacy forms (strictly distinguished — see remarks), <c>hsl()</c> / <c>hsla()</c>
/// in both forms, <c>transparent</c>, and <c>currentcolor</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Modern vs legacy syntax (CSS Color 4 §4.2).</b> The two are mutually exclusive:
/// </para>
/// <list type="bullet">
///   <item><b>Legacy</b>: <c>rgb(R, G, B)</c> or <c>rgb(R, G, B, A)</c> — comma-
///     separated. The slash separator is forbidden.</item>
///   <item><b>Modern</b>: <c>rgb(R G B)</c> or <c>rgb(R G B / A)</c> — whitespace-
///     separated. Commas forbidden.</item>
/// </list>
/// <para>
/// Mixed forms (commas AND a slash) are invalid per spec and rejected with
/// <see cref="CssDiagnosticCodes.CssPropertyValueInvalid001"/>. Legacy with 4 commas
/// is the alpha case; modern with the slash is the alpha case.
/// </para>
/// <para>
/// <b>Output encoding.</b> RGB-bearing values pack as
/// <see cref="ComputedSlot.FromColor"/> with <c>0xAARRGGBB</c>. <c>transparent</c>
/// packs as opaque-zero <c>0x00000000</c>. <c>currentcolor</c> uses the dedicated
/// <see cref="ComputedSlot.CurrentColor"/> tag — it has no payload bits at all, so
/// no user-authored color value (e.g. <c>rgba(0, 0, 1, 0)</c>) can collide.
/// </para>
/// <para>
/// <b>Deferred</b> with diagnostic: modern color spaces (<c>oklch()</c>,
/// <c>oklab()</c>, <c>lab()</c>, <c>lch()</c>, <c>color()</c>) and the
/// <c>color-mix()</c> functional. They need Task 3's pre-pass capture (AngleSharp.Css
/// silently corrupts these to wrong rgba). Resolver returns
/// <see cref="ResolverResult.Invalid"/> + diagnostic until that's wired.
/// </para>
/// </remarks>
internal static class ColorResolver
{
    public static ResolverResult Resolve(
        string value,
        PropertyId propertyId,
        string propertyName,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location)
    {
        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            return ResolverResult.Resolved(ComputedSlot.FromColor(0x00000000u));
        if (value.Equals("currentcolor", StringComparison.OrdinalIgnoreCase))
            return ResolverResult.Resolved(ComputedSlot.CurrentColor);

        if (value.Length > 0 && value[0] == '#')
        {
            if (TryParseHex(value, out var argb))
                return ResolverResult.Resolved(ComputedSlot.FromColor(argb));
            EmitInvalid(diagnostics, propertyName, value,
                "malformed hex color (expected #rgb, #rgba, #rrggbb, or #rrggbbaa)", location);
            return ResolverResult.Invalid();
        }

        if (CssNamedColors.TryGet(value, out var named))
            return ResolverResult.Resolved(ComputedSlot.FromColor(named));

        if (CssSystemColors.TryGet(value, out var system))
            return ResolverResult.Resolved(ComputedSlot.FromColor(system));

        // Functional forms.
        if (TryParseFunctionalColor(value, out var fnArgb, out var fnReason))
            return ResolverResult.Resolved(ComputedSlot.FromColor(fnArgb));
        if (fnReason is not null)
        {
            EmitInvalid(diagnostics, propertyName, value, fnReason, location);
            return ResolverResult.Invalid();
        }

        EmitInvalid(diagnostics, propertyName, value,
            "expected a named color, system color, hex (#rgb/#rgba/#rrggbb/#rrggbbaa), rgb()/rgba(), hsl()/hsla(), transparent, or currentcolor",
            location);
        return ResolverResult.Invalid();
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

    /// <summary>Functional color parser. Returns <see langword="true"/> + packed
    /// argb on success; <see langword="false"/> + null reason if the value isn't a
    /// functional color at all (caller falls through to the unknown-token diagnostic);
    /// <see langword="false"/> + non-null reason if it WAS a functional color but
    /// malformed (caller emits diagnostic).</summary>
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
        if (!TrySplitColorArgs(body, out var args, out var syntax, out reason)) return false;
        if (args.Count is not (3 or 4))
        {
            reason = $"rgb()/rgba() expects 3 or 4 components, got {args.Count}";
            return false;
        }

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
            if (syntax == ColorSyntax.LegacyComma && !syntax.HasFlag(ColorSyntax.AlphaWasComma))
            {
                // Should be impossible — TrySplitColorArgs only adds a 4th arg via comma in legacy.
            }
            if (!TryParseAlphaComponent(args[3], out var a))
            {
                reason = "rgb()/rgba() alpha must be a number 0..1 or percentage 0..100%";
                return false;
            }
            aByte = a;
        }

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

    /// <summary>Strictness flags for the syntax detected by <see cref="TrySplitColorArgs"/>.</summary>
    [Flags]
    private enum ColorSyntax
    {
        None = 0,
        LegacyComma = 1,
        ModernSpace = 2,
        AlphaWasSlash = 4,
        AlphaWasComma = 8,
    }

    /// <summary>Splits a color-function argument list per CSS Color 4 §4.2. Strictly
    /// enforces the legacy/modern syntax dichotomy: legacy uses commas only (no slash
    /// allowed); modern uses whitespace + optional slash (no comma allowed). Mixed
    /// forms emit the reason "mixed comma + slash syntax forbidden per CSS Color 4 §4.2".</summary>
    private static bool TrySplitColorArgs(
        string body,
        out System.Collections.Generic.List<string> args,
        out ColorSyntax syntax,
        out string? reason)
    {
        args = new System.Collections.Generic.List<string>(4);
        syntax = ColorSyntax.None;
        reason = null;

        var span = body.AsSpan().Trim();
        if (span.IsEmpty) return false;

        // First pass: detect commas vs slashes at the top level.
        var hasComma = false;
        var hasSlash = false;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] == ',') hasComma = true;
            else if (span[i] == '/') hasSlash = true;
        }

        if (hasComma && hasSlash)
        {
            reason = "mixed comma + slash syntax forbidden per CSS Color 4 §4.2 — use either legacy commas or modern whitespace + '/' alpha, not both";
            return false;
        }

        if (hasComma)
        {
            // Legacy comma-separated. The 4th comma marks alpha.
            syntax = ColorSyntax.LegacyComma | ColorSyntax.AlphaWasComma;
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
            syntax = ColorSyntax.ModernSpace;
            var i = 0;
            while (i < span.Length)
            {
                while (i < span.Length && IsWhitespace(span[i])) i++;
                if (i >= span.Length) break;
                if (span[i] == '/')
                {
                    syntax |= ColorSyntax.AlphaWasSlash;
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
        var i = 0;
        if (token[i] == '+' || token[i] == '-') i++;
        while (i < token.Length && (IsAsciiDigit(token[i]) || token[i] == '.' || token[i] == 'e' || token[i] == 'E' || token[i] == '+' || token[i] == '-'))
        {
            i++;
        }
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
