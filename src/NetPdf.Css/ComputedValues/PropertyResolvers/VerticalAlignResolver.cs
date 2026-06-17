// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Resolves the <c>&lt;vertical-align&gt;</c> value type (CSS 2.2 §10.8.1) for inline-level boxes:
/// <c>baseline | sub | super | text-top | text-bottom | middle | top | bottom |
/// &lt;percentage&gt; | &lt;length&gt;</c>. A keyword resolves to a <c>Keyword</c> slot at the index
/// below; a <c>&lt;length&gt;</c> / <c>&lt;percentage&gt;</c> (which may be negative for this property)
/// delegates to <see cref="LengthResolver"/> as a <see cref="PropertyType.LengthPercentage"/> — a
/// percentage is relative to the line-height and stays a <c>Percentage</c> slot for the consumer.
/// </summary>
/// <remarks>
/// <para><b>Keyword indices are a shared contract</b> with the inline-atomic placement consumer
/// (<c>BlockLayouter.ComputeInlineAtomicLayout</c>, which reads the slot via
/// <c>ReadKeywordOrDefault(PropertyId.VerticalAlign, defaultIndex: 0)</c>). <c>baseline</c> is 0, so an
/// UNSET slot reads as the initial <c>baseline</c>. Keep the two in sync.</para>
/// <para>The box-affecting keywords <c>top</c> / <c>bottom</c> / <c>middle</c> / <c>text-top</c> /
/// <c>text-bottom</c> are consumed by the inline-atomic placement; <c>sub</c> / <c>super</c> and a
/// numeric <c>&lt;length&gt;</c> / <c>&lt;percentage&gt;</c> are VALIDATED here but the consumer
/// currently approximates them as <c>baseline</c> (a documented deferral) — they read back as the
/// default index because a non-keyword slot returns the default from <c>ReadKeywordOrDefault</c>.</para>
/// </remarks>
internal static class VerticalAlignResolver
{
    /// <summary>The initial value — also the fallback for an unset slot (<c>defaultIndex: 0</c>).</summary>
    public const int Baseline = 0;
    public const int Sub = 1;
    public const int Super = 2;
    public const int TextTop = 3;
    public const int TextBottom = 4;
    public const int Middle = 5;
    public const int Top = 6;
    public const int Bottom = 7;

    public static ResolverResult Resolve(
        string value,
        PropertyId propertyId,
        string propertyName,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location)
    {
        if (TryKeyword(value, out var index))
            return ResolverResult.Resolved(ComputedSlot.FromKeyword(index));

        // <length> | <percentage> — validated via the shared parser (units → px; a percentage stays a
        // Percentage slot; negatives are allowed, vertical-align is NOT a non-negative property). The
        // inline-atomic consumer approximates a numeric value as baseline for now (deferred).
        return LengthResolver.Resolve(
            value, PropertyType.LengthPercentage, propertyId, propertyName, diagnostics, location);
    }

    private static bool TryKeyword(string value, out int index)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "baseline": index = Baseline; return true;
            case "sub": index = Sub; return true;
            case "super": index = Super; return true;
            case "text-top": index = TextTop; return true;
            case "text-bottom": index = TextBottom; return true;
            case "middle": index = Middle; return true;
            case "top": index = Top; return true;
            case "bottom": index = Bottom; return true;
            default: index = Baseline; return false;
        }
    }
}
