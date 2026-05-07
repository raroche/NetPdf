// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Frozen;

namespace NetPdf.Css.ComputedValues;

/// <summary>
/// Per Task 16 review Rec 2 — single source of truth for the set of "modern
/// color function" names that:
/// <list type="bullet">
///   <item><see cref="NetPdf.Css.Parser.Preprocessing.CssPreprocessor"/>
///     preserves verbatim during recovery (because AngleSharp.Css 1.0.0-beta.144
///     mishandles them — silently corrupts <c>oklch()</c> / <c>oklab()</c> /
///     <c>lab()</c> / <c>lch()</c> / <c>color()</c> to bogus rgba, drops
///     <c>color-mix()</c> / <c>light-dark()</c> entirely).</item>
///   <item><see cref="PropertyResolvers.ColorResolver"/> diagnoses with
///     <see cref="Diagnostics.CssDiagnosticCodes.CssModernColorFunctionUnsupported001"/>
///     (Info severity) when encountered in a property value.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Cycle-1 deferral: full sRGB conversion of these spaces. CSS Color L4 §6
/// (oklch/oklab/lab/lch) + §10 (color()) define the conversion algorithms;
/// CSS Color L5 §3 (color-mix) + §6 (light-dark) layer on top. Cycle 2 will
/// implement the conversion so authored values render as their nearest sRGB
/// equivalent — for now they fall back to the cascade's invalid-at-computed-
/// value-time rule (initial / inherited).
/// </para>
/// <para>
/// References:
/// <list type="bullet">
///   <item><a href="https://drafts.csswg.org/css-color-4/">CSS Color L4 — oklch/oklab/lab/lch/color()</a></item>
///   <item><a href="https://drafts.csswg.org/css-color-5/">CSS Color L5 — color-mix() / light-dark()</a></item>
/// </list>
/// </para>
/// </remarks>
internal static class ModernColorFunctions
{
    /// <summary>The seven modern color function names recognized by the
    /// preprocessor + the color resolver. Lookup is ASCII case-insensitive
    /// per CSS Syntax §4.</summary>
    public static FrozenSet<string> Names { get; } = new[]
    {
        "oklch",
        "oklab",
        "lab",
        "lch",
        "color",      // color(<colorspace> ...)
        "color-mix",
        "light-dark",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Convenience: <see langword="true"/> when
    /// <paramref name="functionName"/> is one of the modern color function
    /// names (case-insensitive).</summary>
    public static bool Contains(string functionName) => Names.Contains(functionName);

    /// <summary>Convenience for <see cref="System.ReadOnlySpan{T}"/> callers —
    /// the preprocessor reads identifiers as spans during the recovery walk
    /// + we don't want to allocate just for the lookup.</summary>
    public static bool Contains(ReadOnlySpan<char> functionName)
    {
        // FrozenSet doesn't expose a span lookup, so allocate when needed.
        // Recovery is a pre-render one-time pass; the allocation cost is
        // dominated by the surrounding I/O.
        return Names.Contains(functionName.ToString());
    }
}
