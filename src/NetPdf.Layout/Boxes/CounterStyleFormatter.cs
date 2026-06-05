// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;
using System.Text;

namespace NetPdf.Layout.Boxes;

/// <summary>
/// Formats an integer counter value as a CSS counter style's text (the numeral only — no list-marker
/// suffix / no <c>counter()</c> wrapper). Shared by list-item markers (<see cref="BoxBuilder"/>) and
/// page counters (<see cref="CssContentList"/> — <c>counter(page, &lt;style&gt;)</c>) so both render
/// roman / alphabetic / leading-zero numerals identically. Algorithms from CSS Lists L3 §7.1.4 + CSS
/// Counter Styles L3 §6 (clean-room, spec-only).
/// </summary>
internal static class CounterStyleFormatter
{
    /// <summary>The counter styles this formatter supports — the predefined numeric/alphabetic styles
    /// shared by list markers + page counters. Used to validate a <c>&lt;counter-style&gt;</c> token.</summary>
    public static bool IsSupportedStyle(ReadOnlySpan<char> style) => MapStyle(style) is not StyleKind.Unsupported;

    /// <summary>Format <paramref name="value"/> as <paramref name="style"/>'s numeral, or
    /// <see langword="null"/> for an unsupported style name (the caller decides the fallback /
    /// diagnostic). Out-of-range values for a bounded style (roman 1..3999) fall back to decimal per
    /// CSS Lists L3 §7.1.4.</summary>
    public static string? TryFormat(int value, ReadOnlySpan<char> style) => MapStyle(style) switch
    {
        StyleKind.Decimal => value.ToString(CultureInfo.InvariantCulture),
        StyleKind.DecimalLeadingZero => FormatDecimalLeadingZero(value),
        StyleKind.LowerRoman => ToRoman(value, upper: false),
        StyleKind.UpperRoman => ToRoman(value, upper: true),
        StyleKind.LowerAlpha => ToAlpha(value, upper: false),
        StyleKind.UpperAlpha => ToAlpha(value, upper: true),
        StyleKind.LowerGreek => ToGreek(value),
        _ => null,
    };

    private enum StyleKind
    {
        Unsupported, Decimal, DecimalLeadingZero,
        LowerRoman, UpperRoman, LowerAlpha, UpperAlpha, LowerGreek,
    }

    private static StyleKind MapStyle(ReadOnlySpan<char> style)
    {
        // Case-insensitive per CSS Syntax §4 (keyword idents). `lower-latin`/`upper-latin` are aliases
        // of `lower-alpha`/`upper-alpha` (Lists L3 §7.1.4).
        if (style.Equals("decimal", StringComparison.OrdinalIgnoreCase)) return StyleKind.Decimal;
        if (style.Equals("decimal-leading-zero", StringComparison.OrdinalIgnoreCase)) return StyleKind.DecimalLeadingZero;
        if (style.Equals("lower-roman", StringComparison.OrdinalIgnoreCase)) return StyleKind.LowerRoman;
        if (style.Equals("upper-roman", StringComparison.OrdinalIgnoreCase)) return StyleKind.UpperRoman;
        if (style.Equals("lower-alpha", StringComparison.OrdinalIgnoreCase)
            || style.Equals("lower-latin", StringComparison.OrdinalIgnoreCase)) return StyleKind.LowerAlpha;
        if (style.Equals("upper-alpha", StringComparison.OrdinalIgnoreCase)
            || style.Equals("upper-latin", StringComparison.OrdinalIgnoreCase)) return StyleKind.UpperAlpha;
        if (style.Equals("lower-greek", StringComparison.OrdinalIgnoreCase)) return StyleKind.LowerGreek;
        return StyleKind.Unsupported;
    }

    /// <summary>Format with a leading zero when the count is a single digit (CSS Lists L3 §7.1 —
    /// <c>decimal-leading-zero</c>).</summary>
    private static string FormatDecimalLeadingZero(int n) =>
        n < 10 && n >= 0
            ? "0" + n.ToString(CultureInfo.InvariantCulture)
            : n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Roman-numeral conversion 1..3999. Out-of-range values fall back to decimal per CSS
    /// Lists L3 §7.1.4.</summary>
    private static string ToRoman(int n, bool upper)
    {
        if (n < 1 || n > 3999)
            return n.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        foreach (var (sym, val) in RomanPairs)
        {
            while (n >= val) { sb.Append(sym); n -= val; }
        }
        var s = sb.ToString();
        return upper ? s : s.ToLowerInvariant();
    }

    private static readonly (string Symbol, int Value)[] RomanPairs =
    {
        ("M", 1000), ("CM", 900), ("D", 500), ("CD", 400),
        ("C", 100), ("XC", 90), ("L", 50), ("XL", 40),
        ("X", 10), ("IX", 9), ("V", 5), ("IV", 4), ("I", 1),
    };

    /// <summary>Bijective base-26 alphabetic conversion (1→a, 26→z, 27→aa, …) per CSS Lists L3
    /// §7.1.4. Values &lt; 1 fall back to decimal.</summary>
    private static string ToAlpha(int n, bool upper)
    {
        if (n < 1) return n.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        var baseChar = upper ? 'A' : 'a';
        while (n > 0)
        {
            n--;
            sb.Insert(0, (char)(baseChar + (n % 26)));
            n /= 26;
        }
        return sb.ToString();
    }

    /// <summary>Bijective base-24 Greek alphabetic conversion per CSS Counter Styles L3 §6 — symbols
    /// α..ω (omits final-sigma ς; lists use the medial σ). 1..24 → α..ω; 25..600 → αα..ωω; etc.</summary>
    private static string ToGreek(int n)
    {
        if (n < 1) return n.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        while (n > 0)
        {
            n--;
            sb.Insert(0, GreekLowerSymbols[n % GreekLowerSymbols.Length]);
            n /= GreekLowerSymbols.Length;
        }
        return sb.ToString();
    }

    private static readonly char[] GreekLowerSymbols =
    {
        'α', 'β', 'γ', 'δ', 'ε', 'ζ', 'η', 'θ',
        'ι', 'κ', 'λ', 'μ', 'ν', 'ξ', 'ο', 'π',
        'ρ', 'σ', 'τ', 'υ', 'φ', 'χ', 'ψ', 'ω',
    };
}
