// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Properties;

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
/// Leading style/variant/weight/stretch tokens appear in any order, are all optional, and each
/// category appears AT MOST ONCE (a duplicate — e.g. two weights — is malformed); <c>&lt;font-size&gt;</c>
/// and <c>&lt;font-family&gt;</c> are required. Per §6.6 the shorthand resets the longhands it
/// doesn't mention, so style/weight default to <c>normal</c>.
/// </para>
/// <para>
/// <b>Atomic (review P1).</b> Expansion is all-or-nothing: every generated longhand is validated
/// through the SAME resolvers the margin-box style path uses (<see cref="PropertyResolverDispatch"/>),
/// so what's accepted here is exactly what resolves downstream. If ANY part is invalid (e.g.
/// <c>font: italic 12bananas serif</c> — a bogus size unit), the whole shorthand is rejected and
/// NOTHING is emitted — no partial <c>font-style</c>/<c>font-family</c> can survive a later
/// per-longhand reject.
/// </para>
/// <para>
/// Only used for <c>@page</c> margin-box bodies (<c>CssParserAdapter.ParseRawDeclarations</c>),
/// which AngleSharp.Css never sees — regular style rules get the shorthand expanded by AngleSharp.
/// CSS comments are stripped first (they are whitespace per CSS Syntax 3, so <c>italic/*c*/12pt</c>
/// is two tokens). <c>font-variant</c> / <c>font-stretch</c> / <c>line-height</c> are parsed
/// (consumed) but not surfaced, since the margin-box style path doesn't read them; a <c>/</c> still
/// REQUIRES a following <c>&lt;line-height&gt;</c> token (else the shorthand is malformed). A
/// whole-value CSS-wide keyword (<c>font: inherit</c> / <c>initial</c> / …) maps every emitted
/// longhand to that keyword. System font keywords (<c>caption</c> / <c>icon</c> / …) and malformed
/// input return <see langword="false"/> (the caller keeps the raw <c>font</c> declaration as a
/// marker so <c>MarginBoxStyle</c> can surface a diagnostic rather than silently dropping it).
/// </para>
/// <para>
/// <b>Deliberate approximations (review P4).</b> The CSS Fonts 4 <c>oblique &lt;angle&gt;</c> form
/// (<c>oblique 25deg</c>) and an explicit <c>font-width</c>/<c>&lt;font-stretch&gt;</c> percentage
/// are NOT surfaced by the margin-box subset; rather than silently mangling them, such a shorthand
/// is rejected ATOMICALLY (its <c>&lt;angle&gt;</c> reaches the size slot, fails validation, and the
/// whole declaration is dropped + diagnosed). Pinned by tests.
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

    // font-variant-css2 (`normal | small-caps`) — `normal` is handled generically below.
    private static readonly FrozenSet<string> VariantKeywords = new[]
    {
        "small-caps",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // font-stretch keywords — consumed before the size but not surfaced (the margin-box style path
    // doesn't use them; an explicit <font-stretch> percentage is a deliberate non-surface, see P4).
    private static readonly FrozenSet<string> StretchKeywords = new[]
    {
        "ultra-condensed", "extra-condensed", "condensed", "semi-condensed",
        "semi-expanded", "expanded", "extra-expanded", "ultra-expanded",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Expand a <c>font</c> shorthand value into the four longhands NetPdf's margin-box
    /// style path consumes. Returns <see langword="false"/> for a system-font keyword, missing
    /// size/family, a malformed value, or any part that fails resolver validation (atomic — nothing
    /// is emitted on failure).</summary>
    public static bool TryExpand(
        string rawValue, out string fontStyle, out string fontWeight, out string fontSize, out string fontFamily)
    {
        fontStyle = "normal";
        fontWeight = "normal";
        fontSize = string.Empty;
        fontFamily = string.Empty;
        if (string.IsNullOrWhiteSpace(rawValue)) return false;

        // CSS comments are whitespace (CSS Syntax 3 §4) — strip them before tokenizing so
        // `italic/*c*/12pt serif` splits into `italic` + `12pt` (quote-aware so a `/*` inside a
        // quoted family name survives).
        var value = StripComments(rawValue).Trim();
        if (value.Length == 0) return false;

        // `font: inherit | initial | unset | revert | revert-layer` applies to every font longhand.
        if (CssWideKeyword.Is(value))
        {
            fontStyle = fontWeight = fontSize = fontFamily = value;
            return true;
        }
        if (SystemFonts.Contains(value)) return false; // system fonts aren't modeled — drop.

        var tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var i = 0;

        // Leading style / variant / weight / stretch (any order, all optional, each category at
        // most once). Stop at the first token that isn't one of these — that's the font-size.
        bool sawStyle = false, sawWeight = false, sawVariant = false, sawStretch = false;
        for (; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (StyleKeywords.Contains(t)) { if (sawStyle) return false; sawStyle = true; fontStyle = t.ToLowerInvariant(); }
            else if (WeightKeywords.Contains(t) || IsBareWeightNumber(t)) { if (sawWeight) return false; sawWeight = true; fontWeight = t.ToLowerInvariant(); }
            else if (VariantKeywords.Contains(t)) { if (sawVariant) return false; sawVariant = true; /* consumed, not surfaced */ }
            else if (StretchKeywords.Contains(t)) { if (sawStretch) return false; sawStretch = true; /* consumed, not surfaced */ }
            else if (t.Equals("normal", StringComparison.OrdinalIgnoreCase)) { /* applies to any; skip */ }
            else break; // not a pre-size token → this is the font-size.
        }

        if (i >= tokens.Length) return false; // no font-size.

        // font-size, optionally followed by a `/ line-height` (attached to the size token, or one /
        // two separate tokens). A `/` REQUIRES a line-height token (CSS Fonts 4 §6.6) — its value
        // isn't surfaced, but its presence + shape are validated so a stray `/` can't be ignored.
        var sizeToken = tokens[i++];
        var slash = sizeToken.IndexOf('/');
        if (slash >= 0)
        {
            fontSize = sizeToken[..slash];
            var lhAttached = sizeToken[(slash + 1)..];
            string lineHeight;
            if (lhAttached.Length > 0)
            {
                lineHeight = lhAttached;                  // "12pt/1.4"
            }
            else
            {
                if (i >= tokens.Length) return false;     // "12pt/" with nothing after.
                lineHeight = tokens[i++];                 // "12pt/ 1.4" — the lh is the next token.
            }
            if (!IsLineHeight(lineHeight)) return false;
        }
        else
        {
            fontSize = sizeToken;
            if (i < tokens.Length && tokens[i].Length > 0 && tokens[i][0] == '/')
            {
                var slashTok = tokens[i++];
                string lineHeight;
                if (slashTok.Length == 1)
                {
                    if (i >= tokens.Length) return false; // "12pt /" with nothing after.
                    lineHeight = tokens[i++];             // "12pt / 1.4"
                }
                else
                {
                    lineHeight = slashTok[1..];           // "12pt /1.4" → "1.4"
                }
                if (!IsLineHeight(lineHeight)) return false;
            }
        }

        if (i >= tokens.Length) return false; // no font-family.

        fontFamily = string.Join(' ', tokens, i, tokens.Length - i);
        if (string.IsNullOrWhiteSpace(fontFamily)) return false;

        // Atomic validation (review P1): every emitted longhand must resolve, or none applies. Reuse
        // the production resolvers so this gate is exactly the downstream cascade's — a `Deferred`
        // result (e.g. `em` font-size, `bolder` weight) counts as valid; only `Invalid` rejects.
        return IsValidLonghand(PropertyId.FontStyle, fontStyle)
            && IsValidLonghand(PropertyId.FontWeight, fontWeight)
            && IsValidLonghand(PropertyId.FontSize, fontSize)
            && IsValidLonghand(PropertyId.FontFamily, fontFamily);
    }

    /// <summary>True when <paramref name="value"/> resolves (or validly defers) for
    /// <paramref name="id"/> through the production dispatch — i.e. it is not
    /// <see cref="ResolverResult.Invalid"/>. The diagnostics sink is null (this is a
    /// validation-only pass; the caller diagnoses a rejected shorthand as a whole).</summary>
    private static bool IsValidLonghand(PropertyId id, string value) =>
        !PropertyResolverDispatch.Resolve(id, value).IsInvalid;

    /// <summary>A bare integer 1–1000 is a <c>font-weight</c> (a numeric weight has no unit, unlike
    /// a <c>font-size</c> length). A bare <c>0</c> is NOT a weight (out of range) — it falls through
    /// to the font-size slot, where the unitless zero is a valid length.</summary>
    private static bool IsBareWeightNumber(string token)
    {
        if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var w)) return false;
        return w is >= 1 and <= 1000;
    }

    /// <summary>A plausible <c>&lt;line-height&gt;</c> token: <c>normal</c>, or a numeric value
    /// (<c>&lt;number&gt;</c> / <c>&lt;length-percentage&gt;</c> — begins with a digit, sign, or
    /// dot). line-height isn't surfaced by the margin-box style path, so the exact unit isn't
    /// validated; this only distinguishes a real line-height from a family token, so a <c>/</c> can
    /// be required to carry one (rejecting e.g. <c>12pt/ serif</c>).</summary>
    private static bool IsLineHeight(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (token.Equals("normal", StringComparison.OrdinalIgnoreCase)) return true;
        var c = token[0];
        return char.IsAsciiDigit(c) || c == '.' || c == '+' || c == '-';
    }

    /// <summary>Remove CSS comments (<c>/* … */</c>), replacing each with a single space so it can't
    /// fuse two tokens. Quote-aware: a <c>/*</c> inside a quoted family name (<c>"A/*B"</c>) is left
    /// intact. An unterminated comment is stripped to end of input.</summary>
    private static string StripComments(string value)
    {
        if (value.IndexOf("/*", StringComparison.Ordinal) < 0) return value; // fast path: no comment.

        var sb = new StringBuilder(value.Length);
        var n = value.Length;
        var i = 0;
        var quote = '\0';
        while (i < n)
        {
            var c = value[i];
            if (quote != '\0')
            {
                // Inside a quoted string: copy verbatim; a backslash escapes the next char so an
                // escaped quote doesn't end the string. Comments are not recognized here.
                sb.Append(c);
                if (c == '\\' && i + 1 < n) { sb.Append(value[i + 1]); i += 2; continue; }
                if (c == quote) quote = '\0';
                i++;
                continue;
            }
            if (c is '"' or '\'') { quote = c; sb.Append(c); i++; continue; }
            if (c == '/' && i + 1 < n && value[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(value[i] == '*' && value[i + 1] == '/')) i++;
                i = i + 1 < n ? i + 2 : n;   // consume the closing */ when present.
                sb.Append(' ');
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
