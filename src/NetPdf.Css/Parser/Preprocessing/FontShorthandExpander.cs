// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Frozen;
using System.Globalization;
using NetPdf.Css.ComputedValues.PropertyResolvers;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Phase 3 Task 21 cycle 6 — expander for the <c>font</c> shorthand (CSS Fonts L4 §6.6) into the
/// longhands NetPdf consumes for page margin boxes: <c>font-style</c> / <c>font-weight</c> /
/// <c>font-size</c> / <c>font-family</c>.
/// </summary>
/// <remarks>
/// <para>
/// The grammar is <c>[ &lt;font-style&gt; || &lt;font-variant-css2&gt; || &lt;font-weight&gt; ||
/// &lt;font-stretch&gt; ]? &lt;font-size&gt; [ / &lt;line-height&gt; ]? &lt;font-family&gt;</c>.
/// Leading style/variant/weight/stretch tokens appear in any order and are all optional;
/// <c>&lt;font-size&gt;</c> and <c>&lt;font-family&gt;</c> are required. Per §6.6 the shorthand
/// resets the longhands it doesn't mention, so style/weight default to <c>normal</c>.
/// </para>
/// <para>
/// Only used for <c>@page</c> margin-box bodies (<c>CssParserAdapter.ParseRawDeclarations</c>),
/// which AngleSharp.Css never sees — regular style rules get the shorthand expanded by AngleSharp.
/// <c>font-variant</c> / <c>font-stretch</c> / <c>line-height</c> are parsed (consumed) but not
/// surfaced, since the margin-box style path doesn't read them. A whole-value CSS-wide keyword
/// (<c>font: inherit</c> / <c>initial</c> / …) maps every emitted longhand to that keyword. System
/// font keywords (<c>caption</c> / <c>icon</c> / …) and malformed input return
/// <see langword="false"/> (the caller drops the shorthand rather than guess).
/// </para>
/// </remarks>
internal static class FontShorthandExpander
{
    private static readonly FrozenSet<string> SystemFonts = new[]
    {
        "caption", "icon", "menu", "message-box", "small-caption", "status-bar",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> StyleKeywords = new[]
    {
        "italic", "oblique",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> WeightKeywords = new[]
    {
        "bold", "bolder", "lighter",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // font-variant-css2 (small-caps) + font-stretch keywords — consumed before the size but not
    // surfaced (the margin-box style path doesn't use them).
    private static readonly FrozenSet<string> VariantStretchKeywords = new[]
    {
        "small-caps",
        "ultra-condensed", "extra-condensed", "condensed", "semi-condensed",
        "semi-expanded", "expanded", "extra-expanded", "ultra-expanded",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> SizeKeywords = new[]
    {
        "xx-small", "x-small", "small", "medium", "large", "x-large", "xx-large", "xxx-large",
        "smaller", "larger",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Expand a <c>font</c> shorthand value into the four longhands NetPdf's margin-box
    /// style path consumes. Returns <see langword="false"/> for a system-font keyword, missing
    /// size/family, or otherwise malformed input.</summary>
    public static bool TryExpand(
        string rawValue, out string fontStyle, out string fontWeight, out string fontSize, out string fontFamily)
    {
        fontStyle = "normal";
        fontWeight = "normal";
        fontSize = string.Empty;
        fontFamily = string.Empty;
        if (string.IsNullOrWhiteSpace(rawValue)) return false;

        var value = rawValue.Trim();

        // `font: inherit | initial | unset | revert | revert-layer` applies to every font longhand.
        if (CssWideKeyword.Is(value))
        {
            fontStyle = fontWeight = fontSize = fontFamily = value;
            return true;
        }
        if (SystemFonts.Contains(value)) return false; // system fonts aren't modeled — drop.

        var tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var i = 0;

        // Leading style / variant / weight / stretch (any order, all optional). Stop at the size.
        for (; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (StyleKeywords.Contains(t)) fontStyle = t.ToLowerInvariant();
            else if (WeightKeywords.Contains(t) || IsBareWeightNumber(t)) fontWeight = t.ToLowerInvariant();
            else if (VariantStretchKeywords.Contains(t)) { /* consumed, not surfaced */ }
            else if (t.Equals("normal", StringComparison.OrdinalIgnoreCase)) { /* applies to any; skip */ }
            else break; // not a pre-size token → this is the font-size
        }

        if (i >= tokens.Length) return false; // no font-size

        // font-size, possibly carrying an attached "/line-height".
        var sizeToken = tokens[i++];
        var slash = sizeToken.IndexOf('/');
        if (slash >= 0)
        {
            fontSize = sizeToken[..slash];
        }
        else
        {
            fontSize = sizeToken;
            // A separate "/ line-height" or "/lh" token: consume it (+ a following bare lh token).
            if (i < tokens.Length && tokens[i][0] == '/')
            {
                var lhTok = tokens[i++];
                if (lhTok.Length == 1 && i < tokens.Length) i++; // a lone "/" → also eat the lh token
            }
        }

        if (!IsFontSize(fontSize)) return false;
        if (i >= tokens.Length) return false; // no font-family

        fontFamily = string.Join(' ', tokens, i, tokens.Length - i);
        return !string.IsNullOrWhiteSpace(fontFamily);
    }

    /// <summary>A bare integer 1–1000 is a <c>font-weight</c> (a numeric weight has no unit, unlike
    /// a <c>font-size</c> length).</summary>
    private static bool IsBareWeightNumber(string token)
    {
        if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var w)) return false;
        return w is >= 1 and <= 1000;
    }

    /// <summary>A <c>font-size</c> token: an absolute/relative-size keyword, or a numeric value
    /// carrying a unit/percentage (a length — starts with a digit or sign/dot). A bare number is
    /// NOT a size (it would be a weight, handled above).</summary>
    private static bool IsFontSize(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (SizeKeywords.Contains(token)) return true;
        var c = token[0];
        if (!(char.IsAsciiDigit(c) || c == '.' || c == '+' || c == '-')) return false;
        // Must carry a unit or '%' (not a bare number) — find a trailing non-numeric run.
        for (var j = 0; j < token.Length; j++)
        {
            var ch = token[j];
            if (!char.IsAsciiDigit(ch) && ch != '.' && ch != '+' && ch != '-')
                return true; // a unit or '%' follows the numeric prefix
        }
        return false; // bare number → not a font-size
    }
}
