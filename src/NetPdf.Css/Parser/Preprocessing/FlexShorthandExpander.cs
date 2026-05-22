// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Per Phase 3 Task 15 L13 — expander for the <c>flex</c> shorthand property
/// per CSS Flexbox L1 §7.4. Decomposes one <c>flex: …</c> declaration into
/// the three longhand values <c>flex-grow</c> / <c>flex-shrink</c> /
/// <c>flex-basis</c>.
/// <para>
/// AngleSharp.Css 1.0.0-beta.144 only partially handles the <c>flex</c>
/// shorthand: simple <c>flex: &lt;number&gt;</c> sets <c>flex-grow</c> but the
/// keyword forms (<c>flex: none</c>, <c>flex: auto</c>), the length-basis
/// form (<c>flex: 100px</c>), the two-value forms, and the full three-value
/// form do NOT round-trip the longhands the cascade needs. The
/// <see cref="CssPreprocessor"/>'s recovery pass calls this expander to emit
/// the three synthesized longhand declarations the cascade then sees.
/// </para>
/// <para>
/// <b>Grammar per §7.4:</b>
/// </para>
/// <list type="bullet">
///   <item><c>none</c> → <c>flex-grow: 0; flex-shrink: 0; flex-basis: auto</c></item>
///   <item><c>auto</c> → <c>flex-grow: 1; flex-shrink: 1; flex-basis: auto</c></item>
///   <item><c>&lt;number&gt;</c> → <c>flex-grow: N; flex-shrink: 1; flex-basis: 0</c></item>
///   <item><c>&lt;number&gt; &lt;number&gt;</c> → <c>flex-grow: A; flex-shrink: B; flex-basis: 0</c></item>
///   <item><c>&lt;number&gt; &lt;basis&gt;</c> → <c>flex-grow: N; flex-shrink: 1; flex-basis: &lt;basis&gt;</c></item>
///   <item><c>&lt;basis&gt;</c> (= length / percentage / content) → <c>flex-grow: 1; flex-shrink: 1; flex-basis: &lt;basis&gt;</c></item>
///   <item><c>&lt;number&gt; &lt;number&gt; &lt;basis&gt;</c> → <c>flex-grow: A; flex-shrink: B; flex-basis: &lt;basis&gt;</c></item>
/// </list>
/// <para>
/// The expansion always succeeds for valid input. Invalid input
/// (= &gt; 3 tokens, two basis tokens, etc.) returns <see langword="false"/>
/// and the caller leaves the raw shorthand for AngleSharp.Css's partial
/// handling — better than silently producing wrong longhands.
/// </para>
/// </summary>
internal static class FlexShorthandExpander
{
    /// <summary>Attempt to expand a <c>flex</c> shorthand value into its three longhands.</summary>
    /// <param name="rawValue">The raw value text (already trimmed,
    /// <c>!important</c> already stripped).</param>
    /// <param name="grow">Emitted on success — the <c>flex-grow</c> value
    /// as it would appear in CSS source text (e.g., "1", "0", "2.5").</param>
    /// <param name="shrink">Emitted on success — the <c>flex-shrink</c>
    /// value.</param>
    /// <param name="basis">Emitted on success — the <c>flex-basis</c> value
    /// (e.g., "auto", "0", "100px", "50%").</param>
    /// <returns><see langword="true"/> when the value parses as a valid
    /// <c>flex</c> shorthand per §7.4; <see langword="false"/> otherwise.</returns>
    public static bool TryExpand(
        string rawValue,
        out string grow,
        out string shrink,
        out string basis)
    {
        grow = string.Empty;
        shrink = string.Empty;
        basis = string.Empty;

        if (string.IsNullOrWhiteSpace(rawValue)) return false;

        // Per Phase 3 Task 15 L17 — strip CSS block comments per CSS
        // Syntax §4 before tokenizing (mirrors the FlexFlowShorthandExpander
        // fix for the same gap).
        var stripped = CssShorthandHelpers.StripBlockComments(rawValue);
        if (string.IsNullOrWhiteSpace(stripped)) return false;
        var trimmed = stripped.Trim();

        // CSS-wide keywords (initial / inherit / unset / revert / etc.)
        // — pass through to each longhand verbatim so the cascade
        // applies CSS Cascade §7 semantics uniformly.
        if (IsCssWideKeyword(trimmed))
        {
            grow = trimmed;
            shrink = trimmed;
            basis = trimmed;
            return true;
        }

        // Tokenize by whitespace. The shorthand admits 1–3 tokens.
        var tokens = trimmed.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || tokens.Length > 3) return false;

        // Single-token forms first — the two keyword shortcuts + the
        // one-number / one-basis shortcuts per §7.4.
        if (tokens.Length == 1)
        {
            var tok = tokens[0];
            if (tok.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                grow = "0";
                shrink = "0";
                basis = "auto";
                return true;
            }
            if (tok.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                grow = "1";
                shrink = "1";
                basis = "auto";
                return true;
            }
            if (TryParseNumber(tok, out _))
            {
                // <number> → flex-grow: N; flex-shrink: 1; flex-basis: 0
                // per §7.4.1 — sets a flexible main size of 0
                // (NOT the declared width).
                grow = tok;
                shrink = "1";
                basis = "0";
                return true;
            }
            if (IsBasisToken(tok))
            {
                // <basis> only → grow: 1, shrink: 1.
                grow = "1";
                shrink = "1";
                basis = tok;
                return true;
            }
            return false;
        }

        if (tokens.Length == 2)
        {
            var firstIsNumber = TryParseNumber(tokens[0], out _);
            if (!firstIsNumber) return false;
            if (TryParseNumber(tokens[1], out _))
            {
                // <number> <number> → grow + shrink, basis 0.
                grow = tokens[0];
                shrink = tokens[1];
                basis = "0";
                return true;
            }
            if (IsBasisToken(tokens[1]))
            {
                // <number> <basis> → grow + basis, shrink 1.
                grow = tokens[0];
                shrink = "1";
                basis = tokens[1];
                return true;
            }
            return false;
        }

        // tokens.Length == 3
        if (!TryParseNumber(tokens[0], out _)) return false;
        if (!TryParseNumber(tokens[1], out _)) return false;
        if (!IsBasisToken(tokens[2])) return false;
        grow = tokens[0];
        shrink = tokens[1];
        basis = tokens[2];
        return true;
    }

    /// <summary>Per CSS Cascade §7 — the wide keywords every property accepts.</summary>
    private static bool IsCssWideKeyword(string value) =>
        value.Equals("initial", StringComparison.OrdinalIgnoreCase)
        || value.Equals("inherit", StringComparison.OrdinalIgnoreCase)
        || value.Equals("unset", StringComparison.OrdinalIgnoreCase)
        || value.Equals("revert", StringComparison.OrdinalIgnoreCase)
        || value.Equals("revert-layer", StringComparison.OrdinalIgnoreCase);

    /// <summary>Per CSS Values L4 — accepts a signed decimal number
    /// (no unit). Leading + is optional; scientific notation is not
    /// required by §7.4 but commonly tolerated by parsers — we accept
    /// it for robustness.</summary>
    private static bool TryParseNumber(string token, out double value) =>
        double.TryParse(token, NumberStyles.Float | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out value);

    /// <summary>Per CSS Flexbox L1 §7.2 — a valid <c>flex-basis</c>
    /// value: <c>auto</c>, <c>content</c>, a length (px / em / etc.),
    /// a percentage, or an intrinsic-sizing keyword
    /// (<c>min-content</c> / <c>max-content</c> / <c>fit-content</c>).
    /// The expander only needs to identify these — it doesn't
    /// validate the unit; the downstream LengthResolver (for
    /// <c>FlexBasis</c> PropertyType) does the strict grammar check.</summary>
    private static bool IsBasisToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (token.Equals("auto", StringComparison.OrdinalIgnoreCase)) return true;
        if (token.Equals("content", StringComparison.OrdinalIgnoreCase)) return true;
        if (token.Equals("min-content", StringComparison.OrdinalIgnoreCase)) return true;
        if (token.Equals("max-content", StringComparison.OrdinalIgnoreCase)) return true;
        if (token.Equals("fit-content", StringComparison.OrdinalIgnoreCase)) return true;

        // Bare 0 (= length zero per CSS Values §4.3.2 — the "0 is a
        // valid length without a unit" rule) is a basis token in the
        // two-and-three-value forms. Numbers in OTHER positions are
        // handled by the caller's TryParseNumber path; here we accept
        // 0 + any number followed by a unit / percent suffix.
        if (token.Equals("0", StringComparison.Ordinal)) return true;

        // Must end with `%` or have an alphabetic unit suffix to qualify
        // as a basis token (= length or percentage). Plain numbers are
        // NOT basis tokens (they're <number> per §7.4 grammar).
        if (token.EndsWith('%')) return true;

        // Length with a unit suffix (e.g., "100px", "1.5em", "12rem").
        // Split at the digit/letter boundary + check the suffix against
        // the known CSS Values L4 unit allowlist. Rejecting unknown
        // suffixes (e.g., "100xyz") at the expander layer is friendlier
        // than emitting "flex-basis: 100xyz" + having LengthResolver
        // reject it downstream (= the cascade default would silently
        // apply with no obvious source).
        var split = -1;
        for (var i = 0; i < token.Length; i++)
        {
            var c = token[i];
            if (char.IsDigit(c) || c == '.' || c == '-' || c == '+') continue;
            if (char.IsLetter(c)) { split = i; break; }
            return false; // unknown character
        }
        if (split <= 0) return false; // no digits OR no unit
        var numericPart = token.AsSpan(0, split);
        var unitPart = token.AsSpan(split);
        if (!double.TryParse(numericPart, NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out _))
        {
            return false;
        }
        return IsKnownCssLengthUnit(unitPart);
    }

    /// <summary>Per CSS Values L4 §6.2 + §6.3 — the closed set of
    /// length units. Used to reject malformed basis tokens
    /// (e.g., "100xyz") at expansion time. Case-insensitive.</summary>
    private static bool IsKnownCssLengthUnit(ReadOnlySpan<char> unit)
    {
        // Absolute lengths.
        if (unit.Equals("px", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("cm", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("mm", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("q", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("in", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("pt", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("pc", StringComparison.OrdinalIgnoreCase)) return true;
        // Relative / font-based.
        if (unit.Equals("em", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("rem", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("ex", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("rex", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("ch", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("rch", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("ic", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("ric", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("cap", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("rcap", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("lh", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("rlh", StringComparison.OrdinalIgnoreCase)) return true;
        // Viewport-relative.
        if (unit.Equals("vw", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("vh", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("vmin", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("vmax", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("vi", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("vb", StringComparison.OrdinalIgnoreCase)) return true;
        // svw / svh / svmin / svmax + lvw / dvw / etc. families.
        if (unit.Equals("svw", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("svh", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("svmin", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("svmax", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("svi", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("svb", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("lvw", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("lvh", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("lvmin", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("lvmax", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("lvi", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("lvb", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("dvw", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("dvh", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("dvmin", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("dvmax", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("dvi", StringComparison.OrdinalIgnoreCase)) return true;
        if (unit.Equals("dvb", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
