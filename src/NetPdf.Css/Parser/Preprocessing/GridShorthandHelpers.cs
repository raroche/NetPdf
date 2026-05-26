// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Per Phase 3 Task 17 cycle 0c — shared helpers for the grid shorthand
/// expanders (<see cref="GridLineShorthandExpander"/> +
/// <see cref="GridAreaShorthandExpander"/>). Mirrors the
/// <see cref="CssShorthandHelpers"/> pattern from the flex shorthand
/// family — pulls common predicates into one site so the per-shorthand
/// expanders stay focused on their per-shorthand grammar.
/// </summary>
internal static class GridShorthandHelpers
{
    /// <summary>Per CSS Cascade L4 §7.3 + L5 §7.4 — the CSS-wide
    /// keywords every property accepts. Mirrors the same set used by
    /// <c>FlexShorthandExpander.IsCssWideKeyword</c> +
    /// <c>GridLineResolver.IsCssWideKeyword</c>.</summary>
    public static bool IsCssWideKeyword(string value) =>
        value.Equals("initial", StringComparison.OrdinalIgnoreCase)
        || value.Equals("inherit", StringComparison.OrdinalIgnoreCase)
        || value.Equals("unset", StringComparison.OrdinalIgnoreCase)
        || value.Equals("revert", StringComparison.OrdinalIgnoreCase)
        || value.Equals("revert-layer", StringComparison.OrdinalIgnoreCase);

    /// <summary>Per CSS Grid L1 §8.4 omitted-pair rule — true when
    /// <paramref name="value"/> is a bare <c>&lt;custom-ident&gt;</c>
    /// (= one identifier token, NOT a CSS keyword like
    /// <c>auto</c>/<c>span</c>/<c>initial</c>/<c>inherit</c>, NOT a
    /// number, NOT a compound form like <c>foo 2</c> or
    /// <c>span foo</c>). The shorthand expanders use this to decide
    /// whether to duplicate the value to the omitted end longhand
    /// (= the §8.4 "if the first value is a custom-ident, the omitted
    /// second value is set to that custom-ident; otherwise auto" rule).</summary>
    public static bool IsBareCustomIdent(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var span = value.AsSpan().Trim();
        if (span.Length == 0) return false;

        // Reject if it contains internal whitespace (= compound form).
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] is ' ' or '\t' or '\n' or '\r' or '\f') return false;
        }

        // Reject reserved CSS keywords that can appear in <grid-line>
        // grammar but aren't <custom-ident>s per §8.3.
        if (span.Equals("auto", StringComparison.OrdinalIgnoreCase)) return false;
        if (span.Equals("span", StringComparison.OrdinalIgnoreCase)) return false;
        if (span.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
        if (IsCssWideKeywordSpan(span)) return false;

        // First char must be an ident-start (letter or underscore — for
        // CSS custom-idents at this layer, hyphen-led idents are also
        // valid but we conservatively require a letter-start to avoid
        // misclassifying signed numbers like `-1`).
        var first = span[0];
        if (!IsIdentStart(first) && first != '-') return false;
        if (first == '-' && span.Length > 1 && IsAsciiDigit(span[1]))
        {
            // Looks like a signed integer.
            return false;
        }

        // Remaining chars must be ident-continue.
        for (var i = 1; i < span.Length; i++)
        {
            if (!IsIdentContinue(span[i])) return false;
        }
        // Reject pure-digit tokens too (= the loop above doesn't catch
        // these because IsIdentContinue admits digits).
        var allDigits = true;
        for (var i = 0; i < span.Length; i++)
        {
            if (!IsAsciiDigit(span[i])) { allDigits = false; break; }
        }
        if (allDigits) return false;

        return true;
    }

    private static bool IsCssWideKeywordSpan(ReadOnlySpan<char> span) =>
        span.Equals("initial", StringComparison.OrdinalIgnoreCase)
        || span.Equals("inherit", StringComparison.OrdinalIgnoreCase)
        || span.Equals("unset", StringComparison.OrdinalIgnoreCase)
        || span.Equals("revert", StringComparison.OrdinalIgnoreCase)
        || span.Equals("revert-layer", StringComparison.OrdinalIgnoreCase);

    private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';
    private static bool IsIdentStart(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
    private static bool IsIdentContinue(char c)
        => IsIdentStart(c) || IsAsciiDigit(c) || c == '-';
}
